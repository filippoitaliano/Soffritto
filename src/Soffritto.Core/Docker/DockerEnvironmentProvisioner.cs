using System.Net.NetworkInformation;
using Soffritto.Core.Configuration;
using Soffritto.Core.Docker.Seed;
using Soffritto.Core.Env;

namespace Soffritto.Core.Docker;

/// <summary>Cosa creare: il numero dell'ambiente Docker e il branch di ogni repository incluso.</summary>
/// <param name="Branches">Id del repository → branch del suo worktree; le chiavi sono i repository inclusi.</param>
/// <param name="PoolRepositories">Gli id dei repository a cui assegnare una porta del pool di tunnel.</param>
/// <param name="RestartHost">
/// Riavvia l'host del tunnel del pool con quell'indice se e' acceso (le console sono del Desktop):
/// vero se l'ha riavviato. Vedi <see cref="TunnelPool.AssignAsync"/>.
/// </param>
/// <param name="PoolOptional">
/// Con il pool pieno (<see cref="TunnelPool.PoolFull"/>) l'ambiente si crea lo stesso, senza
/// porte del pool; falso = la creazione si ferma con l'errore.
/// </param>
public sealed record DockerCreateRequest(
    int Number,
    IReadOnlyDictionary<string, string> Branches,
    IReadOnlyList<string>? PoolRepositories = null,
    Func<int, bool>? RestartHost = null,
    bool PoolOptional = false);

/// <summary>
/// Il ciclo di vita di un ambiente Docker: creazione (voce in config.json, worktree dai cloni
/// sorgente, env, database dal seed, container dal compose del backend), avvio e stop dei
/// container, «Ripara env» e distruzione senza perdere codice. Ogni azione sul
/// database o avvio dell'api passa dalle guardie di <see cref="DatabaseGuard"/>.
/// </summary>
public static class DockerEnvironmentProvisioner
{
    /// <summary>
    /// Crea l'ambiente Docker: controlli senza effetti, registrazione della voce (poi
    /// <paramref name="onRegistered"/>), worktree, file Docker, porte del pool, env, guardie,
    /// database dal seed con le migrazioni del branch, container. Non apre la console del Compose
    /// Watch: la apre chi chiama. Dopo la registrazione un errore lascia la voce, che si toglie con
    /// la distruzione. Errore o null.
    /// </summary>
    public static async Task<string?> CreateAsync(
        AppConfiguration c,
        DockerCreateRequest request,
        Action<EnvironmentDefinition> onRegistered,
        Action<string> log,
        CancellationToken ct = default)
    {
        var number = request.Number;

        // 1. Docker, numero e configurazione con la voce nuova in memoria.
        if (await DockerCli.ServerVersionAsync(ct).ConfigureAwait(false) is null)
        {
            return $"{DockerStatusReader.Unreachable}: ambiente {number} non creato";
        }

        if (number is < 1 or > AppConfiguration.MaxEnvironmentNumber)
        {
            return $"il numero {number} e' fuori intervallo: da 1 a {AppConfiguration.MaxEnvironmentNumber}";
        }

        if (c.Environment(number) is not null)
        {
            return $"l'ambiente {number} esiste gia'";
        }

        if (request.Branches.FirstOrDefault(p => c.Repository(p.Key) is null || string.IsNullOrWhiteSpace(p.Value)) is { Key: not null } wrong)
        {
            return c.Repository(wrong.Key) is null ? $"{wrong.Key} non e' nel catalogo" : $"manca il branch di {wrong.Key}";
        }

        var working = ConfigurationFiles.Parse(ConfigurationFiles.Serialize(c));
        var ids = working.Repositories.Select(r => r.Id).Where(id => request.Branches.Keys.Contains(id, StringComparer.OrdinalIgnoreCase)).ToList();
        var definition = new EnvironmentDefinition { Number = number, Kind = EnvironmentKind.Docker, Repositories = ids };
        working.Environments.Add(definition);
        if (working.Validate() is { Count: > 0 } invalid)
        {
            return $"configurazione non valida per l'ambiente {number}: {string.Join(" ", invalid)}";
        }

        var slot = DockerSlot.Of(working, definition);
        var entries = working.RepositoriesOf(definition);
        var backend = (BackendConfiguration)slot.Backend!.Repository;
        var seed = working.PrimarySeed()!.Seed!;

        // 2. Cloni sorgente, branch liberi, cartelle, seed, porte e progetto: tutto prima della voce.
        foreach (var entry in entries)
        {
            var bare = entry.Repository.SourceClone!;
            if (!SourceCloneRepository.IsBare(bare))
            {
                return $"il clone sorgente di {entry.Id} non c'e' ({bare}): si crea salvando il setup";
            }

            var worktrees = await SourceCloneRepository.WorktreeBranchesAsync(bare, ct).ConfigureAwait(false);
            if (worktrees is null)
            {
                return $"worktree di {bare} non leggibili";
            }

            var branch = BranchOf(request, entry);
            if (worktrees.TryGetValue(branch, out var held))
            {
                return $"il branch {branch} di {entry.Id} e' gia' {HolderOf(c, held)}";
            }

            if (Directory.Exists(entry.Path))
            {
                return $"la cartella {entry.Path} esiste gia': toglila o scegli un altro numero";
            }
        }

        var (bak, seedError) = await SeedArchive.PrepareAsync(seed.Path, log, ct).ConfigureAwait(false);
        if (bak is null)
        {
            return seedError;
        }

        if (BusyPorts(slot) is { Count: > 0 } busy)
        {
            return $"porte gia' in ascolto: {string.Join(", ", busy.Select(p => $"{p.Host} ({p.Service})"))}: ambiente {number} non creato";
        }

        if (await ProjectResourcesAsync(slot.Project, ct).ConfigureAwait(false) is { } resources)
        {
            return resources.Length == 0
                ? $"il progetto {slot.Project} ha gia' container o volumi: distruggilo prima di ricrearlo"
                : resources;
        }

        // 3. Registrazione: prima dei worktree, cosi' una creazione a meta' resta visibile e distruggibile.
        var registered = ConfigurationFiles.Update(
            file =>
            {
                if (file.Environment(number) is not null)
                {
                    return $"l'ambiente {number} c'e' gia' in config.json";
                }

                file.Environments.Add(new EnvironmentDefinition { Number = number, Kind = EnvironmentKind.Docker, Repositories = ids.ToList() });
                return null;
            },
            ConfigurationFileFallback.Refuse);
        if (registered is not null)
        {
            return $"ambiente {number} non registrato: {registered}";
        }

        log($"ambiente {number} registrato in config.json");
        onRegistered(definition);
        var leftover = $"; la voce dell'ambiente {number} resta e si toglie con Distruggi";

        // 4. Worktree.
        foreach (var entry in entries)
        {
            var add = await SourceCloneRepository.AddWorktreeAsync(entry.Repository.SourceClone!, entry.Path, BranchOf(request, entry), log: log, ct: ct).ConfigureAwait(false);
            if (!add.Success)
            {
                return $"worktree di {entry.Id} non creato: {add.GitError()}{leftover}";
            }
        }

        // 4b. File Docker che il branch non ha, copiati dal fallback ed esclusi da git.
        if (await ComposeRunner.EnsureSlotFilesAsync(working, slot, log, ct).ConfigureAwait(false) is { } filesError)
        {
            return filesError + leftover;
        }

        // 5. Porte del pool.
        foreach (var id in request.PoolRepositories ?? [])
        {
            if (entries.FirstOrDefault(e => SameId(e.Id, id)) is not { } entry)
            {
                return $"{id} non e' nell'ambiente {number}: nessuna porta del pool{leftover}";
            }

            var (assignment, poolError) = await TunnelPool.AssignAsync(working, entry, log, ct, request.RestartHost).ConfigureAwait(false);
            if (poolError == TunnelPool.PoolFull && request.PoolOptional)
            {
                log($"{poolError}: l'ambiente {number} parte senza porte del pool");
                break;
            }

            if (poolError is not null)
            {
                return poolError + leftover;
            }

            // Senza URL le variabili {poolUrl} restano non risolvibili (H-2) e l'env non le scrive.
            log(await TunnelPool.WaitUrlAsync(assignment!, ct).ConfigureAwait(false) is not null
                ? $"URL della porta {assignment!.Port} di {entry.Id} pubblicato"
                : $"URL della porta {assignment!.Port} di {entry.Id} non ancora pubblicato: spegni e riaccendi «Tunnel» nell'intestazione dell'ambiente");
        }

        // 6. Env: file del worktree (solo alla creazione e con Ripara env) ed env_file dei container.
        if (await WriteEnvAsync(working, slot, local: true, log, ct).ConfigureAwait(false) is { } envError)
        {
            return envError + leftover;
        }

        // 7. Il worktree del backend punta al database dello slot.
        var probe = AppConfiguration.ToProbeOptions(backend);
        if (DatabaseGuard.Check(slot.BackendWorktree!, probe, slot.DatabasePort, slot.DatabaseName) is { } worktreeGuard)
        {
            return worktreeGuard + leftover;
        }

        log("guardia del worktree del backend: database dello slot");

        // 8. slot.env, override, config --quiet e guardia sull'ambiente dell'api risolto da compose.
        if (await PrepareComposeAsync(working, slot, log, ct).ConfigureAwait(false) is { } composeError)
        {
            return composeError + leftover;
        }

        // 9. Database dal seed, poi le migrazioni del branch che il seed non ha.
        var db = await ComposeRunner.RunAsync(slot, $"up -d {slot.DatabaseService}", log: log, timeoutSeconds: 900, ct: ct).ConfigureAwait(false);
        if (!db.Success)
        {
            return $"database dello slot non avviato: {db.Tail(3)}{leftover}";
        }

        var provider = SeedProviders.For(seed.Kind);
        if (await provider.WaitReadyAsync(slot.HostConnectionString, ct: ct).ConfigureAwait(false) is { } readyError)
        {
            return readyError + leftover;
        }

        if (await ServiceContainerAsync(slot, slot.DatabaseService, ct).ConfigureAwait(false) is not { } dbContainer)
        {
            return $"container del servizio {slot.DatabaseService} non trovato{leftover}";
        }

        if (await provider.RestoreAsync(dbContainer, bak, slot.HostConnectionString, slot.DatabaseName, log, ct).ConfigureAwait(false) is { } restoreError)
        {
            return restoreError + leftover;
        }

        if (await ApplyMigrationsAsync(slot, backend, log, ct).ConfigureAwait(false) is { } migrationError)
        {
            return migrationError + leftover;
        }

        // 10. Container dell'api (e del web), con la build.
        var up = await ComposeRunner.RunAsync(slot, "up -d --build", slot.HasContainerizedFrontend, log, ct: ct).ConfigureAwait(false);
        if (!up.Success)
        {
            return $"docker compose up non riuscito: {up.Tail(4)}{leftover}";
        }

        log($"ambiente {number} pronto: api {slot.ApiHostUrl}{(slot.Frontend is { } web && slot.PortOf(web) is { } webPort ? $", web http://127.0.0.1:{webPort.Host}" : string.Empty)}, database 127.0.0.1,{slot.DatabasePort}");
        return null;
    }

    /// <summary>
    /// Cosa impedisce di distruggere l'ambiente senza perdere codice: file modificati o non
    /// tracciati, un'operazione git a meta', commit che non stanno su nessun remoto. Non modifica nulla.
    /// </summary>
    public static async Task<(bool Ok, IReadOnlyList<string> Problems)> CheckDestroyableAsync(AppConfiguration c, int number, CancellationToken ct = default)
    {
        if (c.Environment(number) is not { Kind: EnvironmentKind.Docker } environment)
        {
            return (false, [$"l'ambiente {number} non e' un ambiente Docker"]);
        }

        var problems = new List<string>();
        foreach (var entry in c.RepositoriesOf(environment).Where(r => Directory.Exists(r.Path)))
        {
            if (!GitDirectory.Exists(entry.Path))
            {
                if (IsEmptyFolder(entry.Path))
                {
                    continue;
                }

                problems.Add($"{entry.Id}: {entry.Path} non e' un worktree git, non lo tocco");
                continue;
            }

            var status = await ProcessRunner.RunAsync("git", "status --porcelain", entry.Path, 30, cancellationToken: ct).ConfigureAwait(false);
            if (!status.Success)
            {
                problems.Add($"{entry.Id}: stato git non leggibile ({status.GitError()})");
                continue;
            }

            var dirty = status.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).Where(l => l.Length > 3).Select(l => l[3..]).ToList();
            if (dirty.Count > 0)
            {
                problems.Add($"{entry.Id}: {dirty.Count} file modificati o non tracciati in {entry.Path} ({string.Join(", ", dirty.Take(5))}{(dirty.Count > 5 ? ", ..." : string.Empty)})");
            }

            if (GitStatusReader.ReadOperationInProgress(entry.Path) is { } operation)
            {
                problems.Add($"{entry.Id}: {operation} in {entry.Path}");
            }

            var (commits, error) = await SourceCloneRepository.UnpushedCommitsAsync(entry.Path, ct).ConfigureAwait(false);
            if (error is not null)
            {
                problems.Add($"{entry.Id}: commit non verificabili ({error})");
            }
            else if (commits.Count > 0)
            {
                problems.Add($"{entry.Id}: {commits.Count} commit non su nessun remoto ({string.Join("; ", commits.Take(3))})");
            }
        }

        return (problems.Count == 0, problems);
    }

    /// <summary>
    /// Distrugge l'ambiente se <see cref="CheckDestroyableAsync"/> non trova problemi: down -v del
    /// solo suo progetto, immagini del suo compose tolte, worktree tolti senza --force, cartella
    /// dello slot cancellata, porte del pool rilasciate, voce tolta per ultima (poi
    /// <paramref name="onUnregistered"/>). Restano branch e note. Errore o null.
    /// </summary>
    public static async Task<string?> DestroyAsync(AppConfiguration c, int number, Action<int> onUnregistered, Action<string> log, CancellationToken ct = default)
    {
        var (ok, problems) = await CheckDestroyableAsync(c, number, ct).ConfigureAwait(false);
        if (!ok)
        {
            return $"ambiente {number} non distrutto: {string.Join("; ", problems)}";
        }

        var environment = c.Environment(number)!;
        var slot = DockerSlot.Of(c, environment);
        if (await ComposeRunner.CheckLabelAsync(slot, ct).ConfigureAwait(false) is { } labelError)
        {
            return labelError;
        }

        var resources = await ProjectResourcesAsync(slot.Project, ct).ConfigureAwait(false);
        if (resources is { Length: > 0 })
        {
            return resources;
        }

        if (resources is null)
        {
            log($"il progetto {slot.Project} non ha container ne' volumi: down non serve");
        }
        else
        {
            if (await PrepareDownAsync(c, slot, log, ct).ConfigureAwait(false) is { } prepareError)
            {
                return $"down non preparato: {prepareError}";
            }

            var down = await ComposeRunner.RunAsync(slot, "down -v --remove-orphans", slot.HasContainerizedFrontend, log, 300, ct).ConfigureAwait(false);
            if (!down.Success)
            {
                return $"docker compose down non riuscito: {down.Tail(3)}";
            }
        }

        // Le immagini dell'api e del web servono solo a questo ambiente: un ambiente nuovo con lo
        // stesso numero le ricostruisce. Resterebbero a occupare il disco di Docker.
        var (images, imagesError) = await DockerMaintenance.ImagesOfProjectAsync(slot.Project, ct).ConfigureAwait(false);
        log(imagesError is null ? await DockerMaintenance.RemoveImagesAsync(images, ct).ConfigureAwait(false) : $"immagini non lette, restano: {imagesError}");

        foreach (var entry in slot.Repositories.Where(r => Directory.Exists(r.Path)))
        {
            // Resto di un Distruggi interrotto: git ha gia' tolto file e registrazione, non la cartella.
            if (!GitDirectory.Exists(entry.Path) && IsEmptyFolder(entry.Path))
            {
                try
                {
                    Directory.Delete(entry.Path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return $"cartella vuota del worktree {entry.Path} non cancellata: {ex.Message}; la voce dell'ambiente {number} resta";
                }

                await SourceCloneRepository.PruneWorktreesAsync(entry.Repository.SourceClone!, ct).ConfigureAwait(false);
                log($"cartella vuota del worktree {entry.Path} cancellata");
                continue;
            }

            var remove = await SourceCloneRepository.RemoveWorktreeAsync(entry.Repository.SourceClone!, entry.Path, log, ct).ConfigureAwait(false);
            if (!remove.Success)
            {
                return $"worktree {entry.Path} non tolto: {remove.GitError()}; la voce dell'ambiente {number} resta";
            }

            log($"worktree {entry.Path} tolto");
        }

        try
        {
            if (Directory.Exists(slot.ComposeFolder))
            {
                Directory.Delete(slot.ComposeFolder, recursive: true);
                log($"cartella dello slot {slot.ComposeFolder} cancellata");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"cartella dello slot {slot.ComposeFolder} non cancellata: {ex.Message}; la voce dell'ambiente {number} resta";
        }

        if (await TunnelPool.ReleaseAsync(c, number, log, ct).ConfigureAwait(false) is { } poolError)
        {
            log($"porte del pool non tutte rilasciate, restano nel pannello: {poolError}");
        }

        var unregistered = ConfigurationFiles.Update(
            file => file.Environments.RemoveAll(e => e.Number == number && e.Kind == EnvironmentKind.Docker) > 0 ? null : $"l'ambiente {number} non c'e' piu' in config.json",
            ConfigurationFileFallback.Refuse);
        if (unregistered is not null)
        {
            return $"voce dell'ambiente {number} non tolta: {unregistered}";
        }

        onUnregistered(number);
        log($"ambiente {number} distrutto; restano i branch nei cloni sorgente");
        return null;
    }

    /// <summary>
    /// Avvia l'ambiente: env_file dei container riscritti (il worktree no, G-11), file Docker,
    /// slot.env, override, guardia del container dell'api, poi up -d --build. Errore o null.
    /// </summary>
    public static async Task<string?> StartAsync(AppConfiguration c, int number, Action<string> log, CancellationToken ct = default)
    {
        var (slot, slotError) = await CommandSlotAsync(c, number, ct).ConfigureAwait(false);
        if (slot is null)
        {
            return slotError;
        }

        if (await WriteEnvAsync(c, slot, local: false, log, ct).ConfigureAwait(false) is { } envError)
        {
            return envError;
        }

        if (await ComposeRunner.EnsureSlotFilesAsync(c, slot, log, ct).ConfigureAwait(false) is { } filesError)
        {
            return filesError;
        }

        if (await PrepareComposeAsync(c, slot, log, ct).ConfigureAwait(false) is { } composeError)
        {
            return composeError;
        }

        var up = await ComposeRunner.RunAsync(slot, "up -d --build", slot.HasContainerizedFrontend, log, ct: ct).ConfigureAwait(false);
        return up.Success ? null : $"docker compose up non riuscito: {up.Tail(4)}";
    }

    /// <summary>compose stop dell'ambiente (timeout 120 s): l'esito e se tutti i container sono fermi.</summary>
    public static async Task<(string Outcome, bool AllStopped)> StopAsync(AppConfiguration c, int number, Action<string> log, CancellationToken ct = default)
    {
        var (slot, slotError) = await CommandSlotAsync(c, number, ct).ConfigureAwait(false);
        if (slot is null)
        {
            return (slotError!, false);
        }

        var stop = await ComposeRunner.RunAsync(slot, "stop", slot.HasContainerizedFrontend, log, 120, ct).ConfigureAwait(false);
        var status = await DockerStatusReader.ReadAsync(ct).ConfigureAwait(false);
        var containers = status.OfProject(slot.Project).ToList();
        var allStopped = stop.Success && status.IsReachable && containers.All(x => x.RunState == ContainerRunState.Stopped);
        return stop.Success
            ? ($"container fermati: {containers.Count(x => x.RunState == ContainerRunState.Stopped)} su {containers.Count}", allStopped)
            : ($"compose stop non riuscito: {stop.Tail(2)}", false);
    }

    /// <summary>
    /// Avvia un servizio: l'api con il suo env_file, la guardia del container e la build; il web
    /// con il suo env_file e la build; il database. Errore o null.
    /// </summary>
    public static async Task<string?> StartServiceAsync(AppConfiguration c, int number, string service, Action<string> log, CancellationToken ct = default)
    {
        var (slot, slotError) = await CommandSlotAsync(c, number, ct).ConfigureAwait(false);
        if (slot is null)
        {
            return slotError;
        }

        var isApi = string.Equals(service, slot.ApiService, StringComparison.OrdinalIgnoreCase);
        var isWeb = slot.HasContainerizedFrontend && string.Equals(service, slot.WebService, StringComparison.OrdinalIgnoreCase);
        var isDb = string.Equals(service, slot.DatabaseService, StringComparison.OrdinalIgnoreCase);
        if (!isApi && !isWeb && !isDb)
        {
            return $"il servizio {service} non e' dell'ambiente {number}";
        }

        if (!isDb && await WriteEnvAsync(c, slot, local: false, log, ct, only: isApi ? slot.Backend : slot.Frontend).ConfigureAwait(false) is { } envError)
        {
            return envError;
        }

        if (await ComposeRunner.EnsureSlotFilesAsync(c, slot, log, ct).ConfigureAwait(false) is { } filesError)
        {
            return filesError;
        }

        if (await PrepareComposeAsync(c, slot, log, ct, checkApi: isApi).ConfigureAwait(false) is { } composeError)
        {
            return composeError;
        }

        var up = await ComposeRunner.RunAsync(slot, isDb ? $"up -d {service}" : $"up -d --build {service}", isWeb, log, ct: ct).ConfigureAwait(false);
        return up.Success ? null : $"docker compose up {service} non riuscito: {up.Tail(4)}";
    }

    /// <summary>compose stop di un servizio, con i file gia' presenti dall'ultimo avvio. Errore o null.</summary>
    public static async Task<string?> StopServiceAsync(AppConfiguration c, int number, string service, Action<string> log, CancellationToken ct = default)
    {
        var (slot, slotError) = await CommandSlotAsync(c, number, ct).ConfigureAwait(false);
        if (slot is null)
        {
            return slotError;
        }

        var stop = await ComposeRunner.RunAsync(slot, $"stop {service}", slot.HasContainerizedFrontend, log, 120, ct).ConfigureAwait(false);
        return stop.Success ? null : $"compose stop {service} non riuscito: {stop.Tail(2)}";
    }

    /// <summary>La riga di comando dei log di un servizio dello slot, per una console.</summary>
    public static string LogsCommand(DockerSlot slot, string service)
        => $"{ComposePrefix(slot)}{(slot.HasContainerizedFrontend ? $" --profile {ComposeRunner.WebProfile}" : string.Empty)} logs -f --tail 200 {service}";

    /// <summary>La riga di comando del Compose Watch del web, per la console marcata (dopo up, senza --no-up la build ripartirebbe).</summary>
    public static string WatchCommand(DockerSlot slot)
        => $"{ComposePrefix(slot)} --profile {ComposeRunner.WebProfile} watch --no-up {slot.WebService}";

    /// <summary>
    /// «Ripara env»: riscrive i file di env dei worktree (un target che non esiste piu' riparte
    /// dalle costanti del modello) e gli env_file dei container, poi controlla che il worktree del
    /// backend punti al database dello slot. Errore o null.
    /// </summary>
    public static async Task<string?> RepairEnvAsync(AppConfiguration c, int number, Action<string> log, CancellationToken ct = default)
    {
        if (c.Environment(number) is not { Kind: EnvironmentKind.Docker } environment)
        {
            return $"l'ambiente {number} non e' un ambiente Docker";
        }

        var slot = DockerSlot.Of(c, environment);
        if (await WriteEnvAsync(c, slot, local: true, log, ct, targetIsNew: entry => entry.Repository.Env is not { Target.Length: > 0 } env || !File.Exists(Path.Combine(entry.Path, env.Target))).ConfigureAwait(false) is { } envError)
        {
            return envError;
        }

        var backend = (BackendConfiguration)slot.Backend!.Repository;
        return DatabaseGuard.Check(slot.BackendWorktree!, AppConfiguration.ToProbeOptions(backend), slot.DatabasePort, slot.DatabaseName);
    }

    /// <summary>Gli URL delle porte del pool assegnate all'ambiente, per {poolUrl}: id del repository → URL.</summary>
    public static async Task<IReadOnlyDictionary<string, string>> PoolUrlsAsync(int number, CancellationToken ct = default)
    {
        var urls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var assignment in TunnelPool.Read().Where(a => a.Environment == number))
        {
            if (await TunnelPool.UrlOfAsync(assignment, ct).ConfigureAwait(false) is { } url)
            {
                urls[assignment.Repository] = url.TrimEnd('/');
            }
        }

        return urls;
    }

    // Env di tutti i repository: con local il file del worktree (creazione, Ripara env), sempre
    // l'env_file dei container. Nel log solo nomi e conteggi.
    private static bool IsEmptyFolder(string path) => !Directory.EnumerateFileSystemEntries(path).Any();

    private static async Task<string?> WriteEnvAsync(
        AppConfiguration c,
        DockerSlot slot,
        bool local,
        Action<string> log,
        CancellationToken ct,
        Func<EnvironmentRepository, bool>? targetIsNew = null,
        EnvironmentRepository? only = null)
    {
        var poolUrls = await PoolUrlsAsync(slot.Number, ct).ConfigureAwait(false);
        foreach (var entry in slot.Repositories)
        {
            var containerized = SameId(entry.Id, slot.Backend?.Id) || SameId(entry.Id, slot.Frontend?.Id);
            if ((!local && !containerized) || (only is not null && !SameId(entry.Id, only.Id)))
            {
                continue;
            }

            var resolution = EnvResolver.Resolve(c, entry, slot, poolUrls, targetIsNew?.Invoke(entry) ?? true);
            foreach (var warning in resolution.Warnings)
            {
                log($"avviso: {warning}");
            }

            if (local)
            {
                foreach (var line in await EnvResolver.WriteLocalAsync(c, entry, resolution, ct).ConfigureAwait(false))
                {
                    log(line);
                }
            }

            if (containerized && EnvResolver.WriteContainer(slot, entry, resolution) is { } error)
            {
                return error;
            }
        }

        return null;
    }

    // slot.env, override, config --quiet, poi la guardia sull'ambiente dell'api come lo risolve
    // compose (env_file ed environment: del compose del prodotto). Senza checkApi (web e db) la
    // guardia non serve: l'api non parte.
    private static async Task<string?> PrepareComposeAsync(AppConfiguration c, DockerSlot slot, Action<string> log, CancellationToken ct, bool checkApi = true)
    {
        if (ComposeRunner.WriteSlotEnv(c, slot) is { } slotEnvError)
        {
            return slotEnvError;
        }

        if (await ComposeRunner.WriteOverrideAsync(slot, ct).ConfigureAwait(false) is { } overrideError)
        {
            return overrideError;
        }

        if (await ComposeRunner.ValidateAsync(slot, ct).ConfigureAwait(false) is { } validateError)
        {
            return validateError;
        }

        if (!checkApi)
        {
            return null;
        }

        if (await CheckApiContainerAsync(slot, ct).ConfigureAwait(false) is { } guard)
        {
            return guard;
        }

        log("guardia del container dell'api: database dello slot");
        return null;
    }

    // Lo slot di un ambiente Docker esistente, se il suo progetto (quando c'e') ha l'etichetta di
    // questa cartella dati: un progetto di un altro exe non si tocca.
    private static async Task<(DockerSlot? Slot, string? Error)> CommandSlotAsync(AppConfiguration c, int number, CancellationToken ct)
    {
        if (c.Environment(number) is not { Kind: EnvironmentKind.Docker } environment)
        {
            return (null, $"l'ambiente {number} non e' un ambiente Docker");
        }

        var slot = DockerSlot.Of(c, environment);
        return await ComposeRunner.CheckLabelAsync(slot, ct).ConfigureAwait(false) is { } labelError ? (null, labelError) : (slot, null);
    }

    private static string ComposePrefix(DockerSlot slot)
        => $"docker compose -p {slot.Project} -f \"{ComposeRunner.ComposePath(slot)}\" -f \"{slot.OverridePath}\" --env-file \"{slot.SlotEnvPath}\"";

    // down legge il compose: usa i file gia' nel worktree e copia dal fallback solo quelli che
    // mancano, cosi' un fallback rimasto senza file non blocca la distruzione. slot.env e override
    // si riscrivono; un env_file mancante (creazione fermata prima) diventa un file vuoto, che va
    // via con la cartella dello slot.
    private static async Task<string?> PrepareDownAsync(AppConfiguration c, DockerSlot slot, Action<string> log, CancellationToken ct)
    {
        foreach (var entry in new[] { slot.Backend, slot.Frontend }.OfType<EnvironmentRepository>())
        {
            var dockerfile = entry.Repository.DockerDockerfile;
            List<string> files = entry.Type == RepositoryType.Backend
                ? [entry.Repository.DockerCompose, dockerfile, $"{dockerfile}.dockerignore"]
                : [dockerfile, $"{dockerfile}.dockerignore"];
            var missing = files.Where(f => !File.Exists(Path.Combine(entry.Path, f))).ToList();
            if (missing.Count > 0 && await ComposeRunner.EnsureDockerFilesAsync(entry.Path, entry.Repository.Docker?.FilesFallback, missing, log, ct).ConfigureAwait(false) is { } error)
            {
                return error;
            }

            var envFile = EnvResolver.ContainerEnvPath(slot, entry);
            if (!File.Exists(envFile))
            {
                Directory.CreateDirectory(slot.ComposeFolder);
                File.WriteAllText(envFile, string.Empty);
            }
        }

        if (ComposeRunner.WriteSlotEnv(c, slot) is { } slotEnvError)
        {
            return slotEnvError;
        }

        return File.Exists(slot.OverridePath) ? null : await ComposeRunner.WriteOverrideAsync(slot, ct).ConfigureAwait(false);
    }

    private static async Task<string?> CheckApiContainerAsync(DockerSlot slot, CancellationToken ct)
    {
        var backend = (BackendConfiguration)slot.Backend!.Repository;
        var (environment, error) = await ComposeRunner.ResolvedEnvironmentAsync(slot, slot.ApiService!, ct).ConfigureAwait(false);
        return environment is null
            ? error
            : DatabaseGuard.CheckContainer(environment.ToDictionary(), backend.ConnectionStringName, slot.DatabaseService, slot.DatabaseName);
    }

    /// <summary>
    /// Applica al database dello slot le migrazioni del branch del worktree del backend, con la
    /// guardia del database prima di ogni context: tutti i context, o solo <paramref name="contextName"/>.
    /// Errore o null.
    /// </summary>
    public static async Task<string?> ApplyMigrationsAsync(DockerSlot slot, BackendConfiguration backend, Action<string> log, CancellationToken ct, string? contextName = null)
    {
        if (slot.BackendWorktree is not { } worktree)
        {
            return $"l'ambiente {slot.Number} non ha un backend";
        }

        var probe = AppConfiguration.ToProbeOptions(backend);
        if (contextName is not null && !probe.Contexts.Any(x => string.Equals(x.Name, contextName, StringComparison.OrdinalIgnoreCase)))
        {
            return $"il context {contextName} non c'e' (context: {string.Join(", ", probe.Contexts.Select(x => x.Name))})";
        }
        foreach (var startup in probe.Contexts.Select(x => x.RelativeStartupProjectPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // Su un worktree appena creato dotnet ef fallisce senza project.assets.json (NETSDK1004).
            var restore = await ProcessRunner.RunAsync("dotnet", $"restore \"{Path.Combine(worktree, startup)}\"", worktree, 600, cancellationToken: ct).ConfigureAwait(false);
            if (!restore.Success)
            {
                return $"dotnet restore di {startup}: {restore.BuildError(3)}";
            }
        }

        foreach (var context in probe.Contexts.Where(x => contextName is null || string.Equals(x.Name, contextName, StringComparison.OrdinalIgnoreCase)))
        {
            if (DatabaseGuard.Check(worktree, probe, slot.DatabasePort, slot.DatabaseName) is { } guard)
            {
                return guard;
            }

            log($"migrazioni di {context.Name} del branch");
            var update = await MigrationApplier.UpdateDatabaseAsync(worktree, context, null, log, ct).ConfigureAwait(false);
            if (!update.Success)
            {
                return $"migrazioni di {context.Name} non applicate: {await MigrationApplier.FailureReasonAsync(worktree, context, update, ct).ConfigureAwait(false)}";
            }
        }

        return null;
    }

    /// <summary>L'id del container di un servizio dello slot, acceso o no; null se non c'e'.</summary>
    private static async Task<string?> ServiceContainerAsync(DockerSlot slot, string service, CancellationToken ct)
    {
        var ps = await ComposeRunner.RunAsync(slot, $"ps -a -q {service}", timeoutSeconds: 30, ct: ct).ConfigureAwait(false);
        return ps.Success && ps.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) is { } id ? id : null;
    }

    // Null se il progetto non ha ne' container ne' volumi; stringa vuota se li ha; un errore se Docker non risponde.
    private static async Task<string?> ProjectResourcesAsync(string project, CancellationToken ct)
    {
        var filter = $"--filter \"label=com.docker.compose.project={project}\"";
        var containers = await DockerCli.RunAsync($"ps -a -q {filter}", timeoutSeconds: 15, ct: ct).ConfigureAwait(false);
        var volumes = await DockerCli.RunAsync($"volume ls -q {filter}", timeoutSeconds: 15, ct: ct).ConfigureAwait(false);
        if (!containers.Success || !volumes.Success)
        {
            return $"{DockerStatusReader.Unreachable}: {(containers.Success ? volumes : containers).Tail(1)}";
        }

        return containers.StandardOutput.Trim().Length > 0 || volumes.StandardOutput.Trim().Length > 0 ? string.Empty : null;
    }

    private static List<SlotPort> BusyPorts(DockerSlot slot)
    {
        var listening = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(e => e.Port).ToHashSet();
        return slot.Ports.Where(p => listening.Contains(p.Host)).ToList();
    }

    private static string BranchOf(DockerCreateRequest request, EnvironmentRepository entry)
        => request.Branches.First(p => SameId(p.Key, entry.Id)).Value.Trim();

    // Dove sta il branch occupato: l'ambiente Docker che ha quel worktree, o il solo percorso.
    private static string HolderOf(AppConfiguration c, string worktree)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(worktree));
        var number = c.DockerEnvironments
            .FirstOrDefault(e => c.RepositoriesOf(e).Any(r => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(r.Path)), full, StringComparison.OrdinalIgnoreCase)))?
            .Number;
        return number is null ? $"nel worktree {worktree}" : $"nell'ambiente {number} (worktree {worktree})";
    }

    private static bool SameId(string? a, string? b) => a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

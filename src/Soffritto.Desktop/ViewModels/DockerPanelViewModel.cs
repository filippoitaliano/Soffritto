using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using Soffritto.Core;
using Soffritto.Core.Configuration;
using Soffritto.Core.Docker;
using Soffritto.Core.Docker.Seed;
using Soffritto.Desktop.Infrastructure;
using Soffritto.Presentation;

namespace Soffritto.Desktop.ViewModels;

/// <summary>
/// Le azioni Docker globali: la creazione di un ambiente (il riquadro del «+» nella testata), il
/// seed con le migrazioni di dev che gli mancano, «Allinea seed», lo spazio di Docker e il log
/// (scheda «Docker» del setup), il pool dei tunnel (scheda «Tunnel» del setup).
/// </summary>
public sealed class DockerPanelViewModel : ObservableObject
{
    private const int MaxLogLines = 300;
    private const string BackendRequired = "serve il backend con il compose: api, web e database partono dal compose del repository backend";
    private static readonly TimeSpan MissingInterval = TimeSpan.FromMinutes(30);

    private readonly CancellationToken lifetime;
    private readonly List<string> logLines = [];
    private bool isWorking;
    private string seedText = "seed: non letto";
    private string missingText = string.Empty;
    private string missingBadge = string.Empty;
    private string poolText = string.Empty;

    private readonly Action<EnvironmentDefinition> addColumn;
    private readonly Action<int>? refreshColumn;
    private readonly IUserPrompt prompt = new DialogPrompt();
    private string createNumber = string.Empty;
    private bool assignPool = true;
    private string diskText = "spazio di Docker: non letto";
    private string poolNotice = string.Empty;

    /// <param name="addColumn">Aggiunge la colonna dell'ambiente appena registrato, sul thread della UI.</param>
    /// <param name="refreshColumn">Rilegge la colonna di un ambiente a creazione finita, sul thread della UI.</param>
    public DockerPanelViewModel(CancellationToken lifetime, Action<EnvironmentDefinition> addColumn, Action<int>? refreshColumn = null)
    {
        this.lifetime = lifetime;
        this.addColumn = addColumn;
        this.refreshColumn = refreshColumn;
        AlignCommand = new AsyncCommand(AlignAsync);
        RefreshCommand = new AsyncCommand(RefreshMaintenanceAsync);
        RefreshCreateCommand = new AsyncCommand(OpenCreateAsync);
        CreateCommand = new AsyncCommand(CreateAsync);
        CleanCommand = new AsyncCommand(CleanAsync);
        CompactCommand = new AsyncCommand(CompactAsync);
        AddTunnelCommand = new AsyncCommand(AddTunnelAsync);
        FillPoolCommand = new AsyncCommand(FillPoolAsync);
    }

    /// <summary>Crea un tunnel del pool in piu', con la label degli altri, e lo aggiunge a config.json.</summary>
    public ICommand AddTunnelCommand { get; }

    /// <summary>Crea tunnel del pool finche' l'account devtunnel ne concede.</summary>
    public ICommand FillPoolCommand { get; }

    /// <summary>L'esito dell'ultima azione sul pool, sotto i bottoni della scheda «Tunnel» del setup.</summary>
    public string PoolNotice { get => poolNotice; private set => SetProperty(ref poolNotice, value); }

    /// <summary>
    /// Chiamata sul thread della UI quando un tunnel entra o esce dal pool: il setup aperto
    /// riallinea il suo draft alla configurazione attiva.
    /// </summary>
    public Action? PoolTunnelsChanged { get; set; }

    private void PoolLog(string line)
    {
        AppendLog(line);
        UiThread.Run(() => PoolNotice = line);
    }

    // Un tunnel in piu': creato sull'account e scritto in config.json da TunnelPool.AddTunnelAsync,
    // poi nella configurazione in memoria. La Description del tunnel creato, o null con l'errore.
    private async Task<(string? Description, string? Error)> AddOneAsync()
    {
        var (tunnel, error) = await Task.Run(() => TunnelPool.AddTunnelAsync(ActiveConfiguration.Current, AppendLog, lifetime), lifetime).ConfigureAwait(false);
        if (tunnel is null)
        {
            return (null, error);
        }

        UiThread.Run(() =>
        {
            if (ActiveConfiguration.Current.TunnelPool.Tunnels.All(t => t.Description != tunnel.Description))
            {
                ActiveConfiguration.Current.TunnelPool.Tunnels.Add(tunnel);
            }

            PoolTunnelsChanged?.Invoke();
        });
        return (tunnel.Description, null);
    }

    private Task AddTunnelAsync() => PoolActionAsync(async () =>
    {
        var (description, error) = await AddOneAsync().ConfigureAwait(false);
        PoolLog(description is null ? $"tunnel del pool non creato: {error}" : $"tunnel {description} creato");
    });

    // Il rifiuto di devtunnel create chiude il riempimento: e' la quota dell'account, non un errore.
    private Task FillPoolAsync() => PoolActionAsync(async () =>
    {
        const int safetyCap = 100;
        var created = new List<string>();
        for (var i = 0; i < safetyCap; i++)
        {
            var (description, error) = await AddOneAsync().ConfigureAwait(false);
            if (description is null)
            {
                PoolLog(created.Count == 0
                    ? $"nessun tunnel creato: l'account non ne concede altri ({error})"
                    : $"creati {string.Join(", ", created)}; l'account non ne concede altri ({error})");
                return;
            }

            created.Add(description);
            PoolLog($"tunnel {description} creato, provo il prossimo…");
        }
    });

    private Task DeleteTunnelAsync(PoolTunnelState state) => PoolActionAsync(async () =>
    {
        var error = await Task.Run(async () =>
        {
            ConsoleServices.Stop(ConsoleServices.Pids(ProcessTable.Read([]), PoolHosts.Marker(state.Description), _ => false));
            return await TunnelPool.RemoveTunnelAsync(ActiveConfiguration.Current, state.Description, AppendLog, lifetime).ConfigureAwait(false);
        }, lifetime).ConfigureAwait(false);
        if (error is not null)
        {
            PoolLog($"tunnel {state.Description} non eliminato: {error}");
            return;
        }

        UiThread.Run(() =>
        {
            ActiveConfiguration.Current.TunnelPool.Tunnels.RemoveAll(t => t.Description == state.Description);
            PoolTunnelsChanged?.Invoke();
        });
        PoolLog($"tunnel {state.Description} eliminato");
    }, confirm: new Confirmation(
        "Elimina il tunnel del pool",
        "Il tunnel viene cancellato dall'account devtunnel e tolto dal pool. I suoi URL non tornano: un tunnel nuovo ne ha di diversi.",
        [new("Tunnel", [new(state.Description)])],
        "Elimina"));

    // Una sola azione alla volta sul pool, con l'eventuale conferma prima e la rilettura dopo.
    private async Task PoolActionAsync(Func<Task> action, Confirmation? confirm = null)
    {
        if (IsWorking)
        {
            return;
        }

        if (confirm is not null && !await prompt.ConfirmAsync(confirm, lifetime).ConfigureAwait(false))
        {
            return;
        }

        UiThread.Run(() => IsWorking = true);
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PoolLog($"azione sul pool non riuscita: {ex.Message}");
        }
        finally
        {
            UiThread.Run(() => IsWorking = false);
        }

        await RefreshPoolAsync().ConfigureAwait(false);
    }

    private async Task RenewPoolAsync()
    {
        try
        {
            await Task.Run(() => TunnelPool.RenewAsync(ActiveConfiguration.Current, AppendLog, lifetime), lifetime).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppendLog($"scadenza dei tunnel del pool non rinnovata: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Lo spazio di Docker: il file del disco virtuale e l'uso interno per tipo.</summary>
    public string DiskText { get => diskText; private set => SetProperty(ref diskText, value); }

    public ICommand CleanCommand { get; }

    public ICommand CompactCommand { get; }

    /// <summary>Il numero del nuovo ambiente, come testo della casella; proposto il primo libero sopra i locali.</summary>
    public string CreateNumber
    {
        get => createNumber;
        set
        {
            if (SetProperty(ref createNumber, value ?? string.Empty))
            {
                OnCreateChanged();
            }
        }
    }

    /// <summary>Una riga per repository del catalogo: casella e branch, abilitata solo con il clone sorgente.</summary>
    public ObservableCollection<CreateRepositoryRowViewModel> CreateRows { get; } = [];

    /// <summary>«Assegna porte del pool»: una porta per il backend e il frontend nei container.</summary>
    public bool AssignPool { get => assignPool; set => SetProperty(ref assignPool, value); }

    /// <summary>Perche' «Crea» e' spento; vuoto quando si puo' creare.</summary>
    public string CreateBlockReason => BlockReason() ?? string.Empty;

    public bool CanCreate => BlockReason() is null && IsIdle;

    public ICommand CreateCommand { get; }

    /// <summary>Vero durante «Allinea seed» e la creazione di un ambiente: i comandi del pannello restano fermi.</summary>
    public bool IsWorking
    {
        get => isWorking;
        set
        {
            if (SetProperty(ref isWorking, value))
            {
                OnPropertyChanged(nameof(IsIdle));
                OnPropertyChanged(nameof(CanCreate));
            }
        }
    }

    public bool IsIdle => !isWorking;

    public string SeedText { get => seedText; private set => SetProperty(ref seedText, value); }

    public string MissingText { get => missingText; private set => SetProperty(ref missingText, value); }

    /// <summary>Il numero di migrazioni di dev che mancano al seed, per il badge del bottone «Setup»; vuoto con zero.</summary>
    public string MissingBadge { get => missingBadge; private set => SetProperty(ref missingBadge, value); }

    public string Log => string.Join(Environment.NewLine, logLines);

    public string PoolText { get => poolText; private set => SetProperty(ref poolText, value); }

    public ObservableCollection<PoolTunnelRowViewModel> PoolTunnels { get; } = [];

    public ICommand AlignCommand { get; }

    /// <summary>Rilegge seed (con fetch) e spazio di Docker: il bottone della scheda «Docker» del setup.</summary>
    public ICommand RefreshCommand { get; }

    /// <summary>Rilegge numero proposto e branch del riquadro di creazione.</summary>
    public ICommand RefreshCreateCommand { get; }

    /// <summary>
    /// Chiamata sul thread della UI quando «Allinea seed» scrive un seed nuovo, con l'id del backend
    /// e il percorso: il setup aperto lo mostra nella sua bozza.
    /// </summary>
    public Action<string, string>? SeedPathChanged { get; set; }

    /// <summary>Il badge all'avvio e ogni 30 minuti, senza fetch: la fetch la fa l'apertura della scheda «Docker» del setup.</summary>
    public void Start()
    {
        _ = RefreshMissingPeriodicallyAsync();
        _ = RenewPoolAsync();
    }

    /// <summary>All'apertura del riquadro di creazione: numero proposto e branch dei cloni sorgente.</summary>
    public async Task OpenCreateAsync()
    {
        if (!IsWorking)
        {
            await RefreshCreateRowsAsync().ConfigureAwait(false);
        }
    }

    /// <summary>All'apertura della scheda «Docker» del setup: fetch del clone sorgente, migrazioni mancanti e spazio di Docker.</summary>
    public async Task RefreshMaintenanceAsync()
    {
        await RefreshSeedAsync(fetch: true).ConfigureAwait(false);
        await RefreshDiskAsync().ConfigureAwait(false);
    }

    // Dopo una creazione: branch e numero cambiano, e lo spazio di Docker anche.
    private async Task AfterCreateAsync()
    {
        await OpenCreateAsync().ConfigureAwait(false);
        await RefreshDiskAsync().ConfigureAwait(false);
    }

    private async Task RefreshDiskAsync()
    {
        try
        {
            var file = DockerDisk.FileSize is { } size ? $"disco di Docker: {DockerDisk.Gigabytes(size)} sul PC" : "disco di Docker Desktop non trovato";
            var (usage, error) = await DockerMaintenance.UsageAsync(lifetime).ConfigureAwait(false);
            var inside = error is not null
                ? $"uso interno non letto: {error}"
                : $"dentro: {string.Join(" · ", usage.Select(u => $"{TypeName(u.Type)} {u.Size} (recuperabili {u.Reclaimable})"))}";
            UiThread.Run(() => DiskText = $"{file}{Environment.NewLine}{inside}");
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static string TypeName(string type) => type switch
    {
        "Images" => "immagini",
        "Containers" => "container",
        "Local Volumes" => "volumi",
        "Build Cache" => "cache di build",
        _ => type,
    };

    private async Task CleanAsync()
    {
        if (IsWorking)
        {
            return;
        }

        UiThread.Run(() => IsWorking = true);
        AppendLog("Pulizia di Docker: avvio");
        try
        {
            var error = await Task.Run(() => DockerCleanup.RunAsync(AppendLog, lifetime), lifetime).ConfigureAwait(false);
            AppendLog(error is null ? "Pulizia di Docker: fatta" : $"Pulizia di Docker saltata: {error}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppendLog($"Pulizia di Docker non riuscita: {ex.Message}");
        }
        finally
        {
            UiThread.Run(() => IsWorking = false);
        }

        await RefreshDiskAsync().ConfigureAwait(false);
    }

    private async Task CompactAsync()
    {
        if (IsWorking)
        {
            return;
        }

        if (DockerCleanup.BusyReason() is { } busy)
        {
            AppendLog($"Compattazione rimandata: {busy}");
            return;
        }

        var others = await DockerDisk.OtherRunningDistributionsAsync(lifetime).ConfigureAwait(false);
        var running = await DockerCli.RunAsync("ps -q", timeoutSeconds: 15, ct: lifetime).ConfigureAwait(false);
        var containers = running.Success ? running.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length : 0;
        var confirmed = await prompt.ConfirmAsync(
            new Confirmation(
                "Compatta il disco di Docker",
                "Restituisce al PC lo spazio gia' liberato dentro Docker: prima conviene «Pulisci ora». Si fermano tutti i container accesi, anche quelli non degli ambienti, e si riaccendono con Avvia.",
                [
                    new("Passi", [
                        new("Chiude Docker Desktop", "1", $"{containers} container accesi"),
                        new("Ferma WSL (wsl --shutdown)", "2", others.Count > 0 ? $"si fermano anche {string.Join(", ", others)}" : string.Empty),
                        new("Compatta docker_data.vhdx", "3", "permessi di amministratore"),
                        new("Riaccende Docker Desktop", "4"),
                    ]),
                ],
                "Compatta"),
            lifetime).ConfigureAwait(false);
        if (!confirmed)
        {
            return;
        }

        UiThread.Run(() => IsWorking = true);
        AppendLog("Compattazione del disco di Docker: avvio");
        try
        {
            var error = await Task.Run(
                () => DockerDisk.CompactAsync(
                    AppendLog,
                    desktop => DetachedLauncher.Start($"\"{desktop}\"", Path.GetDirectoryName(desktop)!, newConsole: false),
                    lifetime),
                lifetime).ConfigureAwait(false);
            AppendLog(error is null ? "Compattazione: fatta; Docker Desktop si sta riaccendendo" : $"Compattazione non riuscita: {error}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppendLog($"Compattazione non riuscita: {ex.Message}");
        }
        finally
        {
            UiThread.Run(() => IsWorking = false);
        }

        await RefreshDiskAsync().ConfigureAwait(false);
    }

    private void OnCreateChanged()
    {
        OnPropertyChanged(nameof(CreateBlockReason));
        OnPropertyChanged(nameof(CanCreate));
    }

    private string? BlockReason()
    {
        if (!int.TryParse(createNumber, out var number) || number is < 1 or > AppConfiguration.MaxEnvironmentNumber)
        {
            return $"numero dell'ambiente da 1 a {AppConfiguration.MaxEnvironmentNumber}";
        }

        if (ActiveConfiguration.Current.Environment(number) is not null)
        {
            return $"l'ambiente {number} esiste gia'";
        }

        var included = CreateRows.Where(r => r.Include).ToList();
        if (!included.Any(r => r.Type == RepositoryType.Backend && r.HasCompose))
        {
            return BackendRequired;
        }

        return included.FirstOrDefault(r => r.BranchName.Length == 0) is { } empty
            ? $"manca il branch di {empty.Id}"
            : null;
    }

    // Le righe del modulo «Nuovo ambiente Docker»: i branch remoti del clone sorgente, con quelli
    // gia' portati da un worktree marcati, perche' git non porta un branch in due worktree.
    private async Task RefreshCreateRowsAsync()
    {
        try
        {
            var configuration = ActiveConfiguration.Current;
            var rows = new List<CreateRepositoryRowViewModel>();
            foreach (var repository in configuration.Repositories)
            {
                var sourceClone = repository.SourceClone is { Length: > 0 } clone && Directory.Exists(clone) ? clone : null;
                var branches = new List<string>();
                if (sourceClone is not null)
                {
                    var remote = await SourceCloneRepository.RemoteBranchesAsync(sourceClone, lifetime).ConfigureAwait(false) ?? [];
                    var held = await SourceCloneRepository.WorktreeBranchesAsync(sourceClone, lifetime).ConfigureAwait(false) ?? new Dictionary<string, string>();
                    branches = remote
                        .OrderBy(b => b == GitConventions.DevelopmentBranch ? 0 : 1)
                        .ThenBy(b => b, StringComparer.OrdinalIgnoreCase)
                        .Select(b => held.TryGetValue(b, out var path) ? $"{b} ({HolderOf(configuration, path)})" : b)
                        .ToList();
                }

                rows.Add(new CreateRepositoryRowViewModel(repository, sourceClone is not null, branches, remote: branches.Select(CreateRepositoryRowViewModel.NameOf).ToHashSet(StringComparer.Ordinal), OnCreateChanged));
            }

            var used = configuration.Environments.Select(e => e.Number).ToHashSet();
            var proposed = FirstFreeNumber(configuration)?.ToString() ?? string.Empty;

            UiThread.Run(() =>
            {
                CreateRows.Clear();
                foreach (var row in rows)
                {
                    CreateRows.Add(row);
                }

                if (createNumber.Length == 0 || (int.TryParse(createNumber, out var n) && used.Contains(n)))
                {
                    CreateNumber = proposed;
                }

                OnCreateChanged();
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppendLog($"branch dei cloni sorgente non letti: {ex.Message}");
        }
    }

    // Il primo numero libero sopra gli ambienti locali; null se non ce ne sono.
    private static int? FirstFreeNumber(AppConfiguration configuration)
    {
        var used = configuration.Environments.Select(e => e.Number).ToHashSet();
        var start = configuration.LocalEnvironments.Select(e => e.Number).DefaultIfEmpty(0).Max() + 1;
        var free = Enumerable.Range(start, Math.Max(0, AppConfiguration.MaxEnvironmentNumber - start + 1)).FirstOrDefault(n => !used.Contains(n));
        return free > 0 ? free : null;
    }

    private static string HolderOf(AppConfiguration configuration, string worktree)
    {
        foreach (var environment in configuration.Environments)
        {
            if (configuration.RepositoriesOf(environment).Any(r => string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(r.Path)),
                Path.TrimEndingDirectorySeparator(worktree),
                StringComparison.OrdinalIgnoreCase)))
            {
                return $"in ambiente {environment.Number}";
            }
        }

        return $"in {Path.GetFileName(worktree)}";
    }

    private async Task CreateAsync()
    {
        if (!CanCreate || !int.TryParse(createNumber, out var number))
        {
            return;
        }

        var included = CreateRows.Where(r => r.Include).ToList();
        var missing = included.Where(r => !r.BranchExists).Select(r => new ConfirmationRow(r.BranchName, r.Id)).ToList();
        if (missing.Count > 0 && !await prompt.ConfirmAsync(
                new Confirmation(
                    $"Crea l'ambiente {number} con branch nuovi",
                    $"Questi branch non esistono su origin: nascono da {GitConventions.RemoteDevelopmentBranch} quando si crea l'ambiente.",
                    [new("Branch nuovi", missing)],
                    "Crea l'ambiente"),
                lifetime).ConfigureAwait(true))
        {
            return;
        }

        var request = new DockerCreateRequest(
            number,
            included.ToDictionary(r => r.Id, r => r.BranchName, StringComparer.OrdinalIgnoreCase),
            AssignPool ? included.Where(r => r.Type is RepositoryType.Backend or RepositoryType.Frontend).Select(r => r.Id).ToList() : null,
            RestartPoolHost);

        IsWorking = true;
        AppendLog($"Nuovo ambiente Docker {number}: avvio, il dettaglio va in «Operazioni» nella sua card");
        var operations = OperationLogs.Reset(number);
        try
        {
            var error = await ProvisionAsync(request, operations.Append).ConfigureAwait(false);
            ReportCreation(number, operations, error);
        }
        finally
        {
            UiThread.Run(() => IsWorking = false);
        }

        await AfterCreateAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// «Apri in → Nuovo ambiente Docker» del pannello PR: crea un ambiente sul primo numero libero
    /// con i repository di <paramref name="targets"/> (per nome sul server) sui loro branch, piu'
    /// backend e frontend senza target su <see cref="GitConventions.DevelopmentBranch"/>, con le
    /// porte del pool di backend e frontend se il pool ha un tunnel libero. Prima fa la fetch dei cloni sorgente dei target: un branch che origin non
    /// ha rifiuta, invece di nascere dal branch di sviluppo. Restituisce l'esito da mostrare.
    /// </summary>
    public async Task<string> CreateForBranchesAsync(IReadOnlyList<(string Repository, string Branch)> targets)
    {
        const string subject = "Nuovo ambiente Docker";
        if (IsWorking)
        {
            return $"{subject}: c'e' gia' un'operazione Docker in corso (creazione, Allinea seed o pool dei tunnel)";
        }

        var configuration = ActiveConfiguration.Current;
        if (FirstFreeNumber(configuration) is not { } number)
        {
            return $"{subject}: nessun numero libero fino a {AppConfiguration.MaxEnvironmentNumber}";
        }

        var branches = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var requested = new List<RepositoryConfiguration>();
        foreach (var repository in configuration.Repositories)
        {
            var hasSourceClone = repository.SourceClone is { Length: > 0 } clone && Directory.Exists(clone);
            var branch = targets
                .Where(t => string.Equals(t.Repository, ActiveConfiguration.RepositoryNameOf(repository), StringComparison.OrdinalIgnoreCase))
                .Select(t => t.Branch)
                .FirstOrDefault();
            if (branch is not null)
            {
                if (!hasSourceClone)
                {
                    return $"{subject}: {repository.Id} non ha il clone sorgente, che si crea salvando il setup";
                }

                branches[repository.Id] = branch;
                requested.Add(repository);
            }
            else if (hasSourceClone && repository.Type is RepositoryType.Backend or RepositoryType.Frontend)
            {
                branches[repository.Id] = GitConventions.DevelopmentBranch;
            }
        }

        if (!configuration.Repositories.Any(r => branches.ContainsKey(r.Id) && r.Type == RepositoryType.Backend && r.DockerCompose.Length > 0))
        {
            return $"{subject}: {BackendRequired}";
        }

        var pool = configuration.Repositories
            .Where(r => branches.ContainsKey(r.Id) && r.Type is RepositoryType.Backend or RepositoryType.Frontend)
            .Select(r => r.Id)
            .ToList();

        IsWorking = true;
        AppendLog($"Nuovo ambiente Docker {number} dalle PR: avvio, il dettaglio va in «Operazioni» nella sua card");
        var operations = OperationLogs.Reset(number);
        string? error;
        try
        {
            error = await FetchBranchesAsync(requested, branches, operations.Append).ConfigureAwait(false)
                ?? await ProvisionAsync(new DockerCreateRequest(number, branches, pool, RestartPoolHost, PoolOptional: true), operations.Append).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error = ex.Message;
        }
        finally
        {
            UiThread.Run(() => IsWorking = false);
        }

        ReportCreation(number, operations, error);

        _ = AfterCreateAsync();
        return error is null
            ? $"Ambiente {number} (Docker) creato: {string.Join(", ", branches.Select(b => $"{b.Key} su {b.Value}"))}"
            : $"Ambiente {number} (Docker) non creato: {error}";
    }

    // L'esito della creazione: nel pannello una riga sola, nella card dell'ambiente anche l'errore.
    // Se la creazione si ferma prima della colonna, l'errore resta comunque nel pannello.
    private void ReportCreation(int number, LogSectionViewModel operations, string? error)
    {
        if (error is null)
        {
            AppendLog($"Ambiente {number} creato");
            return;
        }

        operations.Append($"ambiente {number} non creato: {error}");
        AppendLog($"Ambiente {number} non creato: {error}");
    }

    private async Task<string?> FetchBranchesAsync(IReadOnlyList<RepositoryConfiguration> repositories, IReadOnlyDictionary<string, string> branches, Action<string> log)
    {
        foreach (var repository in repositories)
        {
            var sourceClone = repository.SourceClone!;
            log($"fetch del clone sorgente di {repository.Id}");
            if (await SourceCloneRepository.FetchAsync(sourceClone, log, lifetime).ConfigureAwait(false) is { } fetchError)
            {
                return $"fetch del clone sorgente di {repository.Id} non riuscito: {fetchError}";
            }

            var remote = await SourceCloneRepository.RemoteBranchesAsync(sourceClone, lifetime).ConfigureAwait(false);
            if (remote is null)
            {
                return $"branch di {sourceClone} non leggibili";
            }

            if (!remote.Contains(branches[repository.Id], StringComparer.Ordinal))
            {
                return $"il branch {branches[repository.Id]} di {repository.Id} non esiste su origin";
            }
        }

        return null;
    }

    // La creazione vera e propria, dopo i controlli di chi chiama: errore o null.
    private async Task<string?> ProvisionAsync(DockerCreateRequest request, Action<string> log)
    {
        var number = request.Number;
        try
        {
            var error = await Task.Run(
                () => DockerEnvironmentProvisioner.CreateAsync(
                    ActiveConfiguration.Current,
                    request,
                    definition => UiThread.Run(() =>
                    {
                        // La voce e' gia' in config.json: la configurazione in memoria e la griglia la seguono
                        // subito, se la rilettura di config.json non l'ha gia' portata.
                        if (ActiveConfiguration.Current.Environment(definition.Number) is null)
                        {
                            ActiveConfiguration.Current.Environments.Add(definition);
                        }

                        addColumn(definition);
                    }),
                    log,
                    lifetime),
                lifetime).ConfigureAwait(false);

            if (error is not null)
            {
                return error;
            }

            if (ActiveConfiguration.Current.Environment(number) is { } environment
                && DockerSlot.Of(ActiveConfiguration.Current, environment) is { HasContainerizedFrontend: true } slot)
            {
                log(DockerWatchConsole.Start(ProcessTable.Read([]), slot));
            }

            if (refreshColumn is not null)
            {
                UiThread.Run(() => refreshColumn(number));
            }

            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ex.Message;
        }
    }

    // Con una porta nuova l'host acceso di quel tunnel non la serve finche' non riparte (P02).
    private static bool RestartPoolHost(int index) => PoolHosts.Ensure(ActiveConfiguration.Current, index);

    public void AppendLog(string line) => UiThread.Run(() =>
    {
        logLines.Add($"{DateTime.Now:HH:mm:ss} {line}");
        if (logLines.Count > MaxLogLines)
        {
            logLines.RemoveRange(0, logLines.Count - MaxLogLines);
        }

        OnPropertyChanged(nameof(Log));
    });

    private async Task RefreshMissingPeriodicallyAsync()
    {
        while (!lifetime.IsCancellationRequested)
        {
            await RefreshSeedAsync(fetch: false).ConfigureAwait(false);
            try
            {
                await Task.Delay(MissingInterval, lifetime).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task RefreshSeedAsync(bool fetch)
    {
        try
        {
            var configuration = ActiveConfiguration.Current;
            var seed = configuration.PrimarySeed()?.Seed;
            var seedLine = seed is null || seed.Path.Length == 0 ? "seed: non configurato nella card del backend" : $"seed: {seed.Path}";
            var (missing, error) = await SeedMigrations.MissingOnDevAsync(configuration, fetch, lifetime).ConfigureAwait(false);
            var total = missing?.Values.Sum(m => m.Count) ?? 0;
            var detail = missing is null
                ? error ?? "migrazioni mancanti non lette"
                : total == 0
                    ? $"0 migrazioni mancanti rispetto a {GitConventions.RemoteDevelopmentBranch}"
                    : $"{total} migrazioni mancanti rispetto a {GitConventions.RemoteDevelopmentBranch}: {string.Join(", ", missing.Where(m => m.Value.Count > 0).Select(m => $"{m.Key} {m.Value.Count}"))}";
            UiThread.Run(() =>
            {
                SeedText = seedLine;
                MissingText = detail;
                MissingBadge = total > 0 ? total.ToString() : string.Empty;
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            UiThread.Run(() => MissingText = $"migrazioni mancanti non lette: {ex.Message}");
        }
    }

    private async Task AlignAsync()
    {
        if (IsWorking)
        {
            return;
        }

        UiThread.Run(() => IsWorking = true);
        AppendLog("Allinea seed: avvio");
        try
        {
            var error = await Task.Run(
                () => SeedAligner.AlignAsync(ActiveConfiguration.Current, OnNewSeedPath, AppendLog, lifetime),
                lifetime).ConfigureAwait(false);
            AppendLog(error is null ? "Allinea seed: fatto" : $"Allinea seed non riuscito: {error}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppendLog($"Allinea seed non riuscito: {ex.Message}");
        }
        finally
        {
            UiThread.Run(() => IsWorking = false);
        }

        await RefreshSeedAsync(fetch: true).ConfigureAwait(false);
    }

    // L'aligner ha gia' scritto seed.path in config.json: la configurazione in memoria lo segue,
    // cosi' gli ambienti creati dopo ripristinano il seed nuovo senza riavvio.
    private void OnNewSeedPath(string path) => UiThread.Run(() =>
    {
        if (ActiveConfiguration.Current.PrimarySeed() is { Seed: { } seed } backend)
        {
            seed.Path = path;
            SeedPathChanged?.Invoke(backend.Id, path);
        }
    });

    /// <summary>Rilegge tunnel del pool, porte e assegnazioni (devtunnel show) e lo stato degli host.</summary>
    public async Task RefreshPoolAsync()
    {
        try
        {
            // devtunnel list e show impiegano qualche secondo: senza un elenco gia' letto la scheda lo dice.
            UiThread.Run(() =>
            {
                if (PoolTunnels.Count == 0)
                {
                    PoolText = "Lettura del pool da devtunnel…";
                }
            });

            var configuration = ActiveConfiguration.Current;
            if (configuration.TunnelPool.Tunnels.Count == 0)
            {
                UiThread.Run(() =>
                {
                    PoolTunnels.Clear();
                    PoolText = "Nessun tunnel nel pool: creali con i bottoni qui sotto.";
                });
                return;
            }

            var states = await TunnelPool.StatusAsync(configuration, lifetime).ConfigureAwait(false);
            var scan = await Task.Run(() => ProcessTable.Read([]), lifetime).ConfigureAwait(false);
            var rows = new List<PoolTunnelRowViewModel>();
            foreach (var state in states)
            {
                // Le porte di un ambiente che c'e' si rilasciano spegnendo il suo tunnel: qui restano
                // solo quelle di un ambiente che non c'e' piu'.
                var orphans = state.Assignments
                    .Where(a => configuration.Environment(a.Environment) is null)
                    .Select(a => new PoolAssignmentRowViewModel(a, new AsyncCommand(() => ReleaseAsync(a))))
                    .ToList();

                var host = HostOf(state);
                var hostState = state.TunnelId is null ? ServiceState.Stopped : host.Evaluate(scan, state.TunnelId, hostConnected: false, string.Empty).State;
                rows.Add(new PoolTunnelRowViewModel(
                    state,
                    state.Index < configuration.TunnelPool.Tunnels.Count,
                    hostState,
                    orphans,
                    new AsyncCommand(() => DeleteTunnelAsync(state))));
            }

            UiThread.Run(() =>
            {
                PoolTunnels.Clear();
                foreach (var row in rows)
                {
                    PoolTunnels.Add(row);
                }

                var owners = states.SelectMany(s => s.Assignments).Select(a => a.Environment).Distinct().Order().ToList();
                PoolText = owners.Count == 0
                    ? $"{rows.Count} tunnel, tutti liberi"
                    : $"{states.Count(s => s.Assignments.Count > 0)} tunnel su {rows.Count} in uso: {(owners.Count == 1 ? "ambiente" : "ambienti")} {string.Join(", ", owners)}";
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            UiThread.Run(() => PoolText = $"pool non letto: {ex.Message}");
        }
    }

    private static TunnelHostTools HostOf(PoolTunnelState state) => PoolHosts.Tools(state.Description);

    private async Task ReleaseAsync(PoolAssignment assignment)
    {
        if (IsWorking)
        {
            return;
        }

        var configuration = ActiveConfiguration.Current;
        AppendLog($"Rilascio della porta {assignment.Port} di {assignment.Repository} dell'ambiente {assignment.Environment}");
        try
        {
            var error = await TunnelPool.ReleaseAsync(configuration, assignment.Environment, AppendLog, lifetime, assignment.Repository).ConfigureAwait(false);
            if (error is not null)
            {
                AppendLog($"rilascio non riuscito: {error}");
            }
            else
            {
                if (PoolHosts.StopIfUnused(assignment.TunnelDescription) is { } stopped)
                {
                    AppendLog(stopped);
                }

                // Un ambiente che non c'e' piu' non ha env da riscrivere.
                if (configuration.Environment(assignment.Environment) is not null)
                {
                    await Soffritto.Core.Env.EnvPoolSync
                        .ApplyAsync(configuration, assignment.Environment, Soffritto.Core.Env.EnvSyncScope.PoolDependent, AppendLog, assignment.Repository, lifetime)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppendLog($"rilascio non riuscito: {ex.Message}");
        }

        await RefreshPoolAsync().ConfigureAwait(false);
    }
}

/// <summary>Un tunnel del pool nel pannello: di quale ambiente e', lo stato dell'host e le porte rimaste orfane.</summary>
/// <param name="inPool">Falso per le assegnazioni su una Description tolta dal pool.</param>
/// <param name="orphans">Le porte assegnate a un ambiente che non c'e' piu'.</param>
public sealed class PoolTunnelRowViewModel(
    PoolTunnelState state,
    bool inPool,
    ServiceState hostState,
    IReadOnlyList<PoolAssignmentRowViewModel> orphans,
    ICommand deleteCommand)
{
    private static string OwnerOf(PoolTunnelState state)
    {
        var owners = state.Assignments.Select(a => a.Environment).Distinct().Order().ToList();
        return owners.Count switch
        {
            0 => "libero",
            1 => $"ambiente {owners[0]}",
            _ => $"ambienti {string.Join(", ", owners)}",
        };
    }

    /// <summary>Solo un tunnel senza porte assegnate si elimina: prima si spegne il tunnel dell'ambiente.</summary>
    public bool CanDelete { get; } = state.Assignments.Count == 0;

    public string DeleteToolTip { get; } = state.Assignments.Count == 0
        ? $"Cancella il tunnel {state.Description} dall'account devtunnel e lo toglie dal pool"
        : $"{state.Description} e' in uso: spegni prima «Tunnel» nell'intestazione dell'ambiente";

    public ICommand DeleteCommand { get; } = deleteCommand;

    public string Title { get; } = $"{state.Description} · {OwnerOf(state)}{(inPool ? string.Empty : " · fuori dal pool")}";

    /// <summary>Solo cio' che non torna: errore di lettura o differenze tra devtunnel e tunnel-pool.json.</summary>
    public string Detail { get; } = state.Error ?? string.Join("; ", state.Differences);

    public string DetailVisibility => Detail.Length > 0 ? "Visible" : "Collapsed";

    public string HostColor { get; } = ConsoleServices.ColorOf(hostState);

    public string HostText { get; } = $"{state.TunnelId ?? "tunnel non trovato"} · {state.Ports.Count} porte su {TunnelPool.MaxPortsPerTunnel} · host: {ConsoleServices.Describe(hostState)}";

    public IReadOnlyList<PoolAssignmentRowViewModel> Assignments { get; } = orphans;
}

/// <summary>Una porta del pool rimasta a un ambiente che non c'e' piu': si rilascia da qui.</summary>
public sealed class PoolAssignmentRowViewModel(PoolAssignment assignment, ICommand releaseCommand)
{
    public string Text { get; } = $"porta {assignment.Port} · ambiente {assignment.Environment} (non c'e' piu') · {assignment.Repository}";

    public string Url { get; } = string.Empty;

    public string ReleaseToolTip { get; } = $"Rilascia la porta {assignment.Port} di {assignment.Repository} dell'ambiente {assignment.Environment}";

    public ICommand ReleaseCommand { get; } = releaseCommand;
}

/// <summary>Un repository nel modulo «Nuovo ambiente Docker»: incluso o no, e su quale branch.</summary>
public sealed class CreateRepositoryRowViewModel : ObservableObject
{
    private const string HeldSuffix = " (in ";

    private readonly IReadOnlySet<string> remote;
    private readonly Action changed;
    private bool include;
    private string branch;

    public CreateRepositoryRowViewModel(RepositoryConfiguration repository, bool hasSourceClone, IReadOnlyList<string> branches, IReadOnlySet<string> remote, Action changed)
    {
        Id = repository.Id;
        Type = repository.Type;
        IsEnabled = hasSourceClone;
        HasCompose = repository.Type == RepositoryType.Backend && repository.DockerCompose.Length > 0;
        Branches = branches;
        this.remote = remote;
        this.changed = changed;
        include = hasSourceClone && repository.Type is RepositoryType.Backend or RepositoryType.Frontend;
        branch = hasSourceClone ? GitConventions.DevelopmentBranch : string.Empty;
        ToolTip = hasSourceClone
            ? $"{Id}: worktree dal clone sorgente {repository.SourceClone}"
            : $"{Id}: senza clone sorgente nel setup non entra in un ambiente Docker";
    }

    public string Id { get; }

    public RepositoryType Type { get; }

    public bool IsEnabled { get; }

    public bool HasCompose { get; }

    public string ToolTip { get; }

    /// <summary>I branch remoti; quelli gia' in un worktree hanno il suffisso «(in ambiente M)».</summary>
    public IReadOnlyList<string> Branches { get; }

    public bool Include
    {
        get => include;
        set
        {
            if (SetProperty(ref include, value && IsEnabled))
            {
                changed();
            }
        }
    }

    /// <summary>Il testo della tendina, anche scritto a mano (un branch nuovo nasce da origin/dev).</summary>
    public string Branch
    {
        get => branch;
        set
        {
            if (SetProperty(ref branch, value ?? string.Empty))
            {
                changed();
            }
        }
    }

    public string BranchName => NameOf(branch);

    public bool BranchExists => remote.Contains(BranchName);

    public static string NameOf(string text)
    {
        var trimmed = text.Trim();
        var held = trimmed.IndexOf(HeldSuffix, StringComparison.Ordinal);
        return held > 0 ? trimmed[..held] : trimmed;
    }
}

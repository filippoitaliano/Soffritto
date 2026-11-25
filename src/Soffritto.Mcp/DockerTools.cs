using System.ComponentModel;
using ModelContextProtocol.Server;
using Soffritto.Core;
using Soffritto.Core.Configuration;
using Soffritto.Core.Docker;
using Soffritto.Core.Env;
using Soffritto.Presentation;

namespace Soffritto.Mcp;

/// <summary>
/// Gli strumenti di scrittura degli ambienti Docker: le stesse azioni dell'exe (il «+» della
/// testata e le colonne), sulle stesse API di Core. Le operazioni lunghe girano in background (<see cref="Operations"/>).
/// </summary>
[McpServerToolType]
public static class DockerTools
{
    private const string WaitDescription = "Secondi da aspettare la fine prima di rispondere (0-110): se l'operazione dura di piu' la risposta contiene l'id da seguire con stato_operazione.";

    [McpServerTool(Name = "stato_ambiente_docker", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Stato di un ambiente Docker N: per ogni repository il worktree (cartella su cui lavorare), il branch e lo stato git; per ogni servizio del compose (database, api, web) il container e se risponde; indirizzi locali e URL del pool; console del Compose Watch del web; migrazioni EF del database dello slot rispetto al branch del worktree; nota dell'ambiente; operazione in corso. Solo lettura; impiega qualche secondo.")]
    public static async Task<string> StatoAmbienteDocker(
        [Description("Numero dell'ambiente Docker.")] int numero,
        CancellationToken cancellationToken = default)
    {
        if (Resolve(numero, out var configuration, out var slot) is { } error)
        {
            return EnvironmentTools.Serialize(new { errore = error });
        }

        var docker = await DockerStatusReader.ReadAsync(cancellationToken).ConfigureAwait(false);
        var containers = docker.OfProject(slot.Project).ToList();
        var databaseReady = docker.IsReachable && await DockerReadinessProbe.DatabaseAsync(slot, cancellationToken).ConfigureAwait(false);

        var services = new List<object>();
        foreach (var (service, port, isDatabase) in Services(slot))
        {
            var container = containers.FirstOrDefault(c => string.Equals(c.Service, service, StringComparison.Ordinal));
            var answers = container?.RunState == ContainerRunState.Running
                && (isDatabase ? databaseReady : await DockerReadinessProbe.HttpAsync(port, cancellationToken).ConfigureAwait(false));
            services.Add(new
            {
                servizio = service,
                stato = container is null ? "assente" : answers ? "pronto" : container.RunState == ContainerRunState.Stopped ? "fermo" : "in avvio",
                container = container?.Status,
                indirizzo = isDatabase ? $"127.0.0.1,{port}" : $"http://localhost:{port}/",
            });
        }

        var repositories = new List<object>();
        foreach (var entry in slot.Repositories)
        {
            var git = Directory.Exists(entry.Path) ? await GitStatusReader.ReadAsync(entry.Path, false, cancellationToken).ConfigureAwait(false) : null;
            repositories.Add(new
            {
                id = entry.Id,
                tipo = AppConfiguration.TypeName(entry.Type),
                worktree = entry.Path,
                esiste = Directory.Exists(entry.Path),
                git = git is null ? null : EnvironmentTools.GitSection(git),
            });
        }

        object? database = null;
        if (slot.Backend is { Repository: BackendConfiguration backend } backendEntry && Directory.Exists(backendEntry.Path))
        {
            var options = AppConfiguration.ToProbeOptions(backend);
            var report = await StatusProbe.RunMigrationsAsync(
                backendEntry.Path,
                options,
                cancellationToken,
                databaseReady ? null : "database dello slot spento o in avvio").ConfigureAwait(false);
            database = EnvironmentTools.DatabaseSection(backendEntry.Path, options, report);
        }

        var poolUrls = await DockerEnvironmentProvisioner.PoolUrlsAsync(numero, cancellationToken).ConfigureAwait(false);
        return EnvironmentTools.Serialize(new
        {
            ambiente = numero,
            progettoCompose = slot.Project,
            cartellaSlot = slot.ComposeFolder,
            docker = docker.Error,
            servizi = services,
            watchDelWeb = slot.HasContainerizedFrontend
                ? ConsoleServices.Describe(DockerWatchConsole.StateOf(ProcessTable.Read([]), slot))
                : null,
            urlDelPool = poolUrls.Count > 0 ? poolUrls : null,
            repository = repositories,
            database,
            nota = ReadNote(numero),
            operazioneInCorso = Operations.RunningOn(numero)?.Snapshot(10),
        });
    }

    [McpServerTool(Name = "crea_ambiente_docker", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Crea un ambiente Docker: per ogni repository un worktree sul branch indicato (un branch che non esiste su origin nasce dal branch di sviluppo principale, senza upstream), env scritti, database dello slot ripristinato dal seed e migrato al branch, container di database, api e web accesi, Compose Watch del web aperto. Si lavora nei worktree: il codice salvato arriva al container del web dal watch, l'api si ricostruisce con avvia_servizio_docker. Dura qualche minuto: risponde con l'operazione da seguire.")]
    public static Task<string> CreaAmbienteDocker(
        [Description("Repository da includere e branch di ognuno, per id del catalogo (vedi elenca_ambienti), es. { \"backend\": \"feature/123-ordini\", \"frontend\": \"feature/123-ordini\" }. Serve il backend con il compose.")] Dictionary<string, string> branch,
        [Description("Numero dell'ambiente; vuoto = il numero libero piu' basso.")] int? numero = null,
        [Description("Accende il tunnel dell'ambiente: backend e frontend ricevono una porta di un tunnel del pool, per raggiungerli da fuori. Con il pool pieno la creazione si ferma con l'errore.")] bool portePool = false,
        [Description("Testo della nota dell'ambiente, visibile sulla colonna dell'exe (es. il lavoro a cui e' dedicato).")] string? nota = null,
        [Description(WaitDescription)] int attesaSecondi = 20,
        CancellationToken cancellationToken = default)
        => Run(async () =>
        {
            if (EnvironmentTools.LoadConfiguration(out var error) is not { } configuration)
            {
                return new { errore = error };
            }

            var number = numero ?? Enumerable.Range(1, AppConfiguration.MaxEnvironmentNumber).FirstOrDefault(n => configuration.Environment(n) is null);
            if (number == 0)
            {
                return new { errore = "nessun numero libero per un ambiente nuovo" };
            }

            var branches = branch.ToDictionary(p => p.Key, p => p.Value.Trim(), StringComparer.OrdinalIgnoreCase);
            var included = configuration.Repositories.Where(r => branches.ContainsKey(r.Id)).ToList();
            var pool = portePool
                ? included.Where(r => r.Type is RepositoryType.Backend or RepositoryType.Frontend).Select(r => r.Id).ToList()
                : null;
            var request = new DockerCreateRequest(number, branches, pool, index => PoolHosts.Ensure(configuration, index));

            return await Operations.RunAsync("crea", number, attesaSecondi, async (operation, ct) =>
            {
                var createError = await DockerEnvironmentProvisioner.CreateAsync(
                    configuration,
                    request,
                    _ =>
                    {
                        if (!string.IsNullOrWhiteSpace(nota))
                        {
                            WriteNote(number, nota);
                        }
                    },
                    operation.Log,
                    ct).ConfigureAwait(false);
                if (createError is not null)
                {
                    return (createError, null);
                }

                // CreateAsync registra la voce in config.json, non nella configurazione passata.
                if (EnvironmentTools.LoadConfiguration(out var reloadError) is not { } reloaded || reloaded.Environment(number) is not { } created)
                {
                    return ($"ambiente {number} creato, ma config.json non si rilegge: {reloadError}", null);
                }

                var slot = DockerSlot.Of(reloaded, created);
                if (slot.HasContainerizedFrontend)
                {
                    operation.Log(DockerWatchConsole.Start(ProcessTable.Read([]), slot));
                }

                return (null, Summary(slot));
            }, cancellationToken).ConfigureAwait(false);
        });

    [McpServerTool(Name = "avvia_ambiente_docker", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Accende i container dell'ambiente Docker (docker compose up -d --build, con gli env_file riscritti), apre il Compose Watch del web e, se l'ambiente ha il tunnel, riaccende il suo host caduto o chiuso. Ricostruisce le immagini se il codice e' cambiato.")]
    public static Task<string> AvviaAmbienteDocker(
        [Description("Numero dell'ambiente Docker.")] int numero,
        [Description(WaitDescription)] int attesaSecondi = 20,
        CancellationToken cancellationToken = default)
        => OnSlot(numero, "avvia", attesaSecondi, async (configuration, slot, operation, ct) =>
        {
            if (await DockerEnvironmentProvisioner.StartAsync(configuration, numero, operation.Log, ct).ConfigureAwait(false) is { } error)
            {
                return (error, null);
            }

            if (slot.HasContainerizedFrontend)
            {
                operation.Log(DockerWatchConsole.Start(ProcessTable.Read([]), slot));
            }

            EnvironmentTunnel.RestoreHosts(numero, operation.Log);

            return (null, Summary(slot));
        }, cancellationToken);

    [McpServerTool(Name = "ferma_ambiente_docker", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Chiude il Compose Watch del web e ferma i container dell'ambiente Docker (docker compose stop). Worktree, database e immagini restano.")]
    public static Task<string> FermaAmbienteDocker(
        [Description("Numero dell'ambiente Docker.")] int numero,
        [Description(WaitDescription)] int attesaSecondi = 30,
        CancellationToken cancellationToken = default)
        => OnSlot(numero, "ferma", attesaSecondi, async (configuration, slot, operation, ct) =>
        {
            if (slot.HasContainerizedFrontend)
            {
                operation.Log(DockerWatchConsole.Stop(ProcessTable.Read([]), slot));
            }

            var (outcome, allStopped) = await DockerEnvironmentProvisioner.StopAsync(configuration, numero, operation.Log, ct).ConfigureAwait(false);
            return (allStopped ? null : outcome, new { esito = outcome });
        }, cancellationToken);

    [McpServerTool(Name = "avvia_servizio_docker", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Accende o ricostruisce un servizio dell'ambiente Docker (up -d --build per api e web, up -d per il database), con il suo env_file riscritto. Dopo modifiche al codice del backend serve per vederle nell'api; per il web apre anche il Compose Watch.")]
    public static Task<string> AvviaServizioDocker(
        [Description("Numero dell'ambiente Docker.")] int numero,
        [Description("Servizio del compose: quello dell'api, del web o del database (i nomi sono in stato_ambiente_docker).")] string servizio,
        [Description(WaitDescription)] int attesaSecondi = 20,
        CancellationToken cancellationToken = default)
        => OnSlot(numero, $"avvia {servizio}", attesaSecondi, async (configuration, slot, operation, ct) =>
        {
            if (await DockerEnvironmentProvisioner.StartServiceAsync(configuration, numero, servizio, operation.Log, ct).ConfigureAwait(false) is { } error)
            {
                return (error, null);
            }

            if (string.Equals(servizio, slot.WebService, StringComparison.Ordinal))
            {
                operation.Log(DockerWatchConsole.Start(ProcessTable.Read([]), slot));
            }

            return (null, new { esito = $"{servizio} acceso" });
        }, cancellationToken);

    [McpServerTool(Name = "ferma_servizio_docker", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Ferma un servizio dell'ambiente Docker (docker compose stop); per il web chiude prima il Compose Watch.")]
    public static Task<string> FermaServizioDocker(
        [Description("Numero dell'ambiente Docker.")] int numero,
        [Description("Servizio del compose: quello dell'api, del web o del database.")] string servizio,
        [Description(WaitDescription)] int attesaSecondi = 30,
        CancellationToken cancellationToken = default)
        => OnSlot(numero, $"ferma {servizio}", attesaSecondi, async (configuration, slot, operation, ct) =>
        {
            if (string.Equals(servizio, slot.WebService, StringComparison.Ordinal))
            {
                operation.Log(DockerWatchConsole.Stop(ProcessTable.Read([]), slot));
            }

            var error = await DockerEnvironmentProvisioner.StopServiceAsync(configuration, numero, servizio, operation.Log, ct).ConfigureAwait(false);
            return (error, error is null ? new { esito = $"{servizio} fermato" } : null);
        }, cancellationToken);

    [McpServerTool(Name = "log_servizio_docker", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Le ultime righe del log di un servizio dell'ambiente Docker (docker compose logs --tail).")]
    public static async Task<string> LogServizioDocker(
        [Description("Numero dell'ambiente Docker.")] int numero,
        [Description("Servizio del compose: quello dell'api, del web o del database.")] string servizio,
        [Description("Quante righe dalla fine (1-2000).")] int righe = 200,
        CancellationToken cancellationToken = default)
    {
        if (Resolve(numero, out _, out var slot) is { } error)
        {
            return EnvironmentTools.Serialize(new { errore = error });
        }

        if (!Services(slot).Any(s => s.Service == servizio))
        {
            return EnvironmentTools.Serialize(new { errore = $"{servizio} non e' un servizio dell'ambiente {numero} ({string.Join(", ", Services(slot).Select(s => s.Service))})" });
        }

        var logs = await ComposeRunner.RunAsync(
            slot,
            $"logs --no-color --tail {Math.Clamp(righe, 1, 2000)} {servizio}",
            web: string.Equals(servizio, slot.WebService, StringComparison.Ordinal),
            timeoutSeconds: 60,
            ct: cancellationToken).ConfigureAwait(false);
        return EnvironmentTools.Serialize(logs.Success
            ? new { servizio, log = logs.StandardOutput + logs.StandardError }
            : (object)new { errore = logs.Tail(10) });
    }

    [McpServerTool(Name = "applica_migrazioni_docker", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Applica al database dell'ambiente Docker le migrazioni EF del branch del worktree del backend (dotnet restore, poi dotnet ef database update per context), con la guardia che il worktree punti al database dello slot. Da usare dopo aver aggiunto o cambiato una migrazione.")]
    public static Task<string> ApplicaMigrazioniDocker(
        [Description("Numero dell'ambiente Docker.")] int numero,
        [Description("Solo questo context; vuoto = tutti.")] string? context = null,
        [Description(WaitDescription)] int attesaSecondi = 60,
        CancellationToken cancellationToken = default)
        => OnSlot(numero, "migrazioni", attesaSecondi, async (configuration, slot, operation, ct) =>
        {
            if (slot.Backend?.Repository is not BackendConfiguration backend)
            {
                return ($"l'ambiente {numero} non ha un backend", null);
            }

            var error = await DockerEnvironmentProvisioner.ApplyMigrationsAsync(slot, backend, operation.Log, ct, context).ConfigureAwait(false);
            return (error, error is null ? new { esito = "migrazioni applicate" } : null);
        }, cancellationToken);

    [McpServerTool(Name = "ripara_env_docker", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Riscrive gli env dei worktree e gli env_file dei container dell'ambiente Docker dalla configurazione (come «Ripara env» dell'exe), poi controlla che il backend punti al database dello slot. I container leggono gli env_file al prossimo avvio.")]
    public static Task<string> RiparaEnvDocker(
        [Description("Numero dell'ambiente Docker.")] int numero,
        [Description(WaitDescription)] int attesaSecondi = 20,
        CancellationToken cancellationToken = default)
        => OnSlot(numero, "ripara env", attesaSecondi, async (configuration, slot, operation, ct) =>
        {
            var error = await DockerEnvironmentProvisioner.RepairEnvAsync(configuration, numero, operation.Log, ct).ConfigureAwait(false);
            return (error, error is null ? new { esito = "env riscritti", prossimoPasso = "avvia_servizio_docker o avvia_ambiente_docker perche' i container li leggano" } : null);
        }, cancellationToken);

    [McpServerTool(Name = "accendi_tunnel_docker", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Accende il tunnel dell'ambiente Docker: l'ambiente reclama un tunnel libero del pool (un tunnel e' di un solo ambiente), ogni suo repository che espone una porta riceve un URL pubblico con accesso anonimo, l'host del tunnel si accende e le variabili di env che dipendono dal pool si riscrivono. Con il pool pieno risponde con l'errore. I container leggono gli env al prossimo avvio del servizio.")]
    public static Task<string> AccendiTunnelDocker(
        [Description("Numero dell'ambiente Docker.")] int numero,
        [Description(WaitDescription)] int attesaSecondi = 90,
        CancellationToken cancellationToken = default)
        => OnSlot(numero, "tunnel", attesaSecondi, async (configuration, slot, operation, ct) =>
        {
            if (await EnvironmentTunnel.EnableAsync(configuration, numero, operation.Log, ct).ConfigureAwait(false) is { } error)
            {
                return (error, null);
            }

            var urls = await DockerEnvironmentProvisioner.PoolUrlsAsync(numero, ct).ConfigureAwait(false);
            return (null, new { tunnel = TunnelPool.Read().Where(a => a.Environment == numero).Select(a => a.TunnelDescription).Distinct(), url = urls, prossimoPasso = "avvia_servizio_docker perche' i container leggano gli env nuovi" });
        }, cancellationToken);

    [McpServerTool(Name = "spegni_tunnel_docker", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Spegne il tunnel dell'ambiente Docker: rilascia tutte le sue porte del pool (con l'accesso anonimo), chiude l'host, restituisce il tunnel al pool e riscrive le variabili di env che dipendevano dal pool. Riaccendendolo l'ambiente puo' ricevere un tunnel diverso, con URL diversi.")]
    public static Task<string> SpegniTunnelDocker(
        [Description("Numero dell'ambiente Docker.")] int numero,
        [Description(WaitDescription)] int attesaSecondi = 60,
        CancellationToken cancellationToken = default)
        => OnSlot(numero, "tunnel spento", attesaSecondi, async (configuration, slot, operation, ct) =>
        {
            var had = EnvironmentTunnel.IsOn(numero);
            var error = await EnvironmentTunnel.DisableAsync(configuration, numero, operation.Log, ct).ConfigureAwait(false);
            return error is not null ? (error, null) : (null, new { esito = had ? "tunnel spento, porte rilasciate" : $"l'ambiente {numero} non aveva il tunnel" });
        }, cancellationToken);

    [McpServerTool(Name = "nota_ambiente", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Scrive la nota di un ambiente (locale o Docker), visibile sulla sua colonna nell'exe: per esempio il lavoro a cui e' dedicato. Sostituisce la nota precedente; vuota la cancella.")]
    public static string NotaAmbiente(
        [Description("Numero dell'ambiente.")] int numero,
        [Description("Testo della nota.")] string testo)
    {
        if (EnvironmentTools.LoadConfiguration(out var error) is not { } configuration)
        {
            return EnvironmentTools.Serialize(new { errore = error });
        }

        if (configuration.Environment(numero) is null)
        {
            return EnvironmentTools.Serialize(new { errore = $"l'ambiente {numero} non e' configurato" });
        }

        WriteNote(numero, testo);
        return EnvironmentTools.Serialize(new { esito = "nota scritta", nota = testo });
    }

    [McpServerTool(Name = "stato_operazione", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Avanzamento ed esito di un'operazione avviata da uno strumento Docker (crea, avvia, migrazioni…) o da esegui_comando_extra:stato in corso / riuscita / fallita, ultime righe del log, errore ed esito. Le operazioni vivono nel processo del server MCP.")]
    public static async Task<string> StatoOperazione(
        [Description("Id dell'operazione, dalla risposta dello strumento che l'ha avviata.")] string operazione,
        [Description("Righe del log da riportare, dalla fine.")] int righe = 40,
        [Description("Secondi da aspettare la fine prima di rispondere (0-110).")] int attesaSecondi = 0,
        CancellationToken cancellationToken = default)
    {
        if (Operations.Find(operazione) is not { } operation)
        {
            return EnvironmentTools.Serialize(new { errore = $"operazione {operazione} sconosciuta: le operazioni vivono solo nel processo del server MCP che le ha avviate" });
        }

        if (operation.IsRunning && attesaSecondi > 0)
        {
            try
            {
                await Task.WhenAny(operation.Completion, Task.Delay(TimeSpan.FromSeconds(Math.Clamp(attesaSecondi, 0, 110)), cancellationToken)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        return EnvironmentTools.Serialize(operation.Snapshot(Math.Clamp(righe, 1, 400)));
    }

    private static async Task<string> Run(Func<Task<object>> body)
        => EnvironmentTools.Serialize(await body().ConfigureAwait(false));

    private static Task<string> OnSlot(
        int numero,
        string kind,
        int attesaSecondi,
        Func<AppConfiguration, DockerSlot, Operation, CancellationToken, Task<(string? Error, object? Result)>> work,
        CancellationToken cancellationToken)
        => Run(async () => Resolve(numero, out var configuration, out var slot) is { } error
            ? new { errore = error }
            : await Operations.RunAsync(kind, numero, attesaSecondi, (operation, ct) => work(configuration, slot, operation, ct), cancellationToken).ConfigureAwait(false));

    // La configurazione e lo slot di un ambiente Docker, o l'errore da restituire.
    private static string? Resolve(int numero, out AppConfiguration configuration, out DockerSlot slot)
    {
        configuration = null!;
        slot = null!;
        if (EnvironmentTools.LoadConfiguration(out var error) is not { } loaded)
        {
            return error;
        }

        if (loaded.Environment(numero) is not { } environment)
        {
            return $"l'ambiente {numero} non e' configurato";
        }

        if (environment.Kind != EnvironmentKind.Docker)
        {
            return $"l'ambiente {numero} e' locale: gli strumenti *_docker agiscono solo sugli ambienti Docker";
        }

        configuration = loaded;
        slot = DockerSlot.Of(loaded, environment);
        return null;
    }

    private static IEnumerable<(string Service, int Port, bool IsDatabase)> Services(DockerSlot slot)
    {
        yield return (slot.DatabaseService, slot.DatabasePort, true);
        if (slot.Backend is { } backend && slot.ApiService is { } api && slot.PortOf(backend) is { } apiPort)
        {
            yield return (api, apiPort.Host, false);
        }

        if (slot.Frontend is { } frontend && slot.WebService is { } web && slot.PortOf(frontend) is { } webPort)
        {
            yield return (web, webPort.Host, false);
        }
    }

    private static object Summary(DockerSlot slot) => new
    {
        ambiente = slot.Number,
        worktree = slot.Repositories.ToDictionary(r => r.Id, r => r.Path),
        servizi = Services(slot).Select(s => new { servizio = s.Service, indirizzo = s.IsDatabase ? $"127.0.0.1,{s.Port}" : $"http://localhost:{s.Port}/" }),
        prossimoPasso = "stato_ambiente_docker per lo stato dei container e delle migrazioni",
    };

    private static string NotePath(int numero) => Path.Combine(ConfigurationFiles.DataFolder, "Note", $"ambiente-{numero}.txt");

    private static string? ReadNote(int numero)
        => File.Exists(NotePath(numero)) && File.ReadAllText(NotePath(numero)) is { Length: > 0 } text ? text : null;

    // Lo stesso file di EnvironmentNote: l'exe lo osserva e aggiorna la colonna.
    private static void WriteNote(int numero, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(NotePath(numero))!);
        File.WriteAllText(NotePath(numero), text);
    }
}

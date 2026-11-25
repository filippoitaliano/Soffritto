using System.ComponentModel;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using Soffritto.Core;
using Soffritto.Core.Configuration;
using Soffritto.Presentation;

namespace Soffritto.Mcp;

[McpServerToolType]
public static class EnvironmentTools
{
    // Oltre questa soglia l'elenco delle mancanti dice poco a chi legge e costa contesto: basta il conteggio.
    private const int MaxPendingListed = 20;

    private const string ExternalHost = "da un host che non e' tra i processi di questa macchina";

    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    [McpServerTool(Name = "elenca_ambienti", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Gli ambienti configurati, locali (cloni) e Docker (worktree e container), con le cartelle dei repository predefiniti (backend, frontend, app) e la lista completa dei repository di ogni ambiente, e l'ambiente a cui appartiene una cartella. Un ambiente con databaseProtetto=true punta a un database protetto (per esempio usato anche da altri): si legge e si usa normalmente, ma non vi si applicano migrazioni ne' si altera lo schema; per un lavoro che crea o applica migrazioni scegli un altro ambiente (o crea un ambiente Docker). Veloce: non legge database, git, tunnel ne' container.")]
    public static string ElencaAmbienti(
        [Description("Cartella da riconoscere, di solito la cartella di lavoro della sessione; vuota = la cartella corrente del server.")] string? cartella = null)
    {
        if (LoadConfiguration(out var error) is not { } configuration)
        {
            return Serialize(new { errore = error });
        }

        var path = string.IsNullOrWhiteSpace(cartella) ? Environment.CurrentDirectory : cartella;
        var located = Locate(configuration, path);

        return Serialize(new
        {
            fileConfigurazione = ConfigurationFiles.ConfigPath,
            cartellaRiconosciuta = new
            {
                cartella = path,
                ambiente = located?.Number,
                repository = located is { } l ? AppConfiguration.TypeName(l.Repo.Type) : null,
                id = located?.Repo.Id,
                nota = located is null ? "La cartella non appartiene a nessun ambiente configurato." : null,
            },
            ambienti = configuration.Environments.Select(environment =>
            {
                var repositories = configuration.RepositoriesOf(environment);
                return new
                {
                    numero = environment.Number,
                    tipo = environment.Kind == EnvironmentKind.Docker ? "docker" : "locale",
                    databaseProtetto = environment.SchemaLockReason is not null ? true : (bool?)null,
                    vincoloDatabase = environment.SchemaLockReason,
                    backend = Folder(PrimaryIn(repositories, RepositoryType.Backend)),
                    frontend = Folder(PrimaryIn(repositories, RepositoryType.Frontend)),
                    app = Folder(PrimaryIn(repositories, RepositoryType.App)),
                    repository = repositories.Select(r => new
                    {
                        id = r.Id,
                        tipo = AppConfiguration.TypeName(r.Type),
                        predefinito = r.IsPrimary,
                        cartella = r.Path,
                        esiste = Directory.Exists(r.Path),
                        comandoExtra = ExtraCommand.Of(r.Repository),
                    }),
                };
            }),
        });
    }

    [McpServerTool(Name = "stato_ambiente", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Stato completo di un ambiente N: database a cui punta il backend predefinito (server, nome, file da cui viene la connection string), migrazioni EF per context (mancanti, fuori ordine, sconosciute), git e dev tunnel di tutti i repository dell'ambiente, servizi accesi (Kestrel, host dei tunnel, dev server, app, Metro) e un elenco di problemi. Con databaseProtetto=true le migrazioni mancanti si segnalano ma non si applicano: lo schema di un database protetto non si tocca. Le chiavi backend, frontend e app descrivono i repository predefiniti; la lista repository li contiene tutti. Solo lettura; impiega qualche secondo.")]
    public static async Task<string> StatoAmbiente(
        [Description("Numero dell'ambiente; se manca si ricava da 'cartella'.")] int? numero = null,
        [Description("Cartella di un repository dell'ambiente, usata quando 'numero' manca; vuota = la cartella corrente del server.")] string? cartella = null,
        [Description("Esegue git fetch prima di leggere ahead/behind (tocca solo i remote-tracking ref). Piu' lento.")] bool fetch = false,
        [Description("Legge i dev tunnel dei repository dell'ambiente (devtunnel list/show e probe delle porte). Senza, l'host di un tunnel si riconosce solo se avviato dalla CLI.")] bool tunnel = true,
        CancellationToken cancellationToken = default)
    {
        if (LoadConfiguration(out var error) is not { } configuration)
        {
            return Serialize(new { errore = error });
        }

        var number = numero ?? Locate(configuration, string.IsNullOrWhiteSpace(cartella) ? Environment.CurrentDirectory : cartella)?.Number;
        if (number is { } docker && configuration.Environment(docker) is { Kind: EnvironmentKind.Docker })
        {
            return Serialize(new { errore = $"L'ambiente {docker} e' un ambiente Docker: usa stato_ambiente_docker." });
        }

        if (number is not { } n || configuration.Environment(n) is not { Kind: EnvironmentKind.Local } environment)
        {
            return Serialize(new
            {
                errore = numero is null
                    ? "La cartella non appartiene a nessun ambiente configurato: passa 'numero'."
                    : $"L'ambiente {numero} non e' configurato (ambienti configurati: {string.Join(", ", configuration.Environments.Select(e => e.Number))}).",
            });
        }

        var repositories = configuration.RepositoriesOf(environment);
        var backendRepo = PrimaryIn(repositories, RepositoryType.Backend);
        var frontendRepo = PrimaryIn(repositories, RepositoryType.Frontend);
        var appRepo = PrimaryIn(repositories, RepositoryType.App);
        var backendSettings = backendRepo?.Repository as BackendConfiguration ?? configuration.Backend;
        var frontendSettings = frontendRepo?.Repository as FrontendConfiguration ?? new FrontendConfiguration();
        var options = AppConfiguration.ToProbeOptions(backendSettings);

        var migrationsTask = backendRepo is not null && Directory.Exists(backendRepo.Path)
            ? StatusProbe.RunMigrationsAsync(backendRepo.Path, options, cancellationToken)
            : null;
        // I tunnel si leggono uno dopo l'altro, come fa l'exe: non e' verificato che la CLI
        // devtunnel regga chiamate concorrenti sulla stessa login.
        var tunnelsTask = tunnel ? ReadTunnelsAsync(repositories, cancellationToken) : null;
        var gitTasks = repositories
            .Where(r => Directory.Exists(r.Path))
            .ToDictionary(r => r, r => GitStatusReader.ReadAsync(r.Path, fetch, cancellationToken));
        var apiProcesses = repositories.Select(r => r.Repository).OfType<BackendConfiguration>().Select(b => b.ApiProcess).ToList();
        var scanTask = Task.Run(() => ProcessTable.Read(apiProcesses), cancellationToken);

        var migrations = migrationsTask is null ? null : await migrationsTask.ConfigureAwait(false);
        var tunnels = tunnelsTask is null ? new Dictionary<EnvironmentRepository, DevTunnelStatus>() : await tunnelsTask.ConfigureAwait(false);
        var git = new Dictionary<EnvironmentRepository, GitStatus>();
        foreach (var (repository, task) in gitTasks)
        {
            git[repository] = await task.ConfigureAwait(false);
        }

        var scan = await scanTask.ConfigureAwait(false);

        var tunnelStatus = backendRepo is not null ? tunnels.GetValueOrDefault(backendRepo) : null;
        var frontendTunnelStatus = frontendRepo is not null ? tunnels.GetValueOrDefault(frontendRepo) : null;

        var services = repositories.ToDictionary(r => r, r => Services(r, scan, tunnels.GetValueOrDefault(r)));

        var envVariables = frontendRepo is not null && Directory.Exists(frontendRepo.Path)
            ? FrontendEnv.ReadVariables(frontendRepo.Path, frontendSettings)
            : new Dictionary<string, string>();
        var devServerPort = FrontendEnv.ReadDevServerPort(frontendRepo?.Path ?? string.Empty, frontendSettings);

        // Il frontend punta al backend dello stesso ambiente se un valore del suo env contiene
        // localhost:<porta https di Kestrel> oppure l'host dell'URL del pool del backend.
        var kestrelPort = backendRepo is not null && Directory.Exists(backendRepo.Path)
            ? DevTunnelProvisioner.KestrelPorts(backendRepo.Path, backendSettings).FirstOrDefault()?.Number
            : null;
        var backendTargets = new List<string>();
        if (kestrelPort is { } port)
        {
            backendTargets.Add($"localhost:{port}");
        }

        if (tunnelStatus is { Shared: true, Error: null })
        {
            backendTargets.AddRange(tunnelStatus.AllPorts
                .Select(p => Uri.TryCreate(p.PortUri, UriKind.Absolute, out var uri) ? uri.Host : null)
                .OfType<string>());
        }

        bool? frontendOnBackend = backendTargets.Count > 0 && envVariables.Count > 0
            ? backendTargets.Any(target => envVariables.Values.Any(v => v.Contains(target, StringComparison.OrdinalIgnoreCase)))
            : null;

        var problems = new List<string>();
        CollectProblems(problems, n, repositories, migrations, tunnelStatus, git, frontendOnBackend, kestrelPort, environment.SchemaLockReason);

        var backendServices = backendRepo is not null ? services[backendRepo] : null;
        var frontendServices = frontendRepo is not null ? services[frontendRepo] : null;
        var appServices = appRepo is not null ? services[appRepo] : null;

        return Serialize(new
        {
            ambiente = n,
            databaseProtetto = environment.SchemaLockReason is not null ? true : (bool?)null,
            vincoloDatabase = environment.SchemaLockReason,
            problemi = problems,
            backend = backendRepo is null
                ? Missing(RepositoryType.Backend, n)
                : new
                {
                    cartella = backendRepo.Path,
                    esiste = Directory.Exists(backendRepo.Path),
                    kestrel = new
                    {
                        stato = backendServices!.Kestrel,
                        pid = Pids(scan, p => ConsoleServices.Is(p, backendSettings.ApiProcess) && ConsoleServices.Under(p.ExecutablePath, backendRepo.Path)),
                        profilo = backendSettings.LaunchProfile,
                        indirizzi = KestrelUrls(backendRepo.Path, backendSettings),
                    },
                    database = migrations is null ? null : DatabaseSection(backendRepo.Path, options, migrations),
                    git = git.TryGetValue(backendRepo, out var backendGit) ? GitSection(backendGit) : null,
                },
            devTunnel = backendRepo is null ? null : TunnelSection(tunnelStatus, backendServices!.HostState, backendServices.HostToolTip),
            frontend = frontendRepo is null
                ? Missing(RepositoryType.Frontend, n)
                : new
                {
                    cartella = frontendRepo.Path,
                    esiste = Directory.Exists(frontendRepo.Path),
                    devServer = new
                    {
                        stato = frontendServices!.DevServer,
                        pid = Pids(scan, p => RepoProcesses.IsFrontend(p, frontendRepo.Path, frontendSettings)),
                        url = $"http://localhost:{devServerPort}/",
                    },
                    fileEnv = frontendSettings.EnvFile.Length > 0 ? frontendSettings.EnvFile : null,
                    variabiliEnv = envVariables,
                    puntaAlBackendDellAmbiente = frontendOnBackend,
                    devTunnel = TunnelSection(frontendTunnelStatus, frontendServices.HostState, frontendServices.HostToolTip),
                    git = git.TryGetValue(frontendRepo, out var frontendGit) ? GitSection(frontendGit) : null,
                },
            app = appRepo is null
                ? Missing(RepositoryType.App, n)
                : new
                {
                    cartella = appRepo.Path,
                    esiste = Directory.Exists(appRepo.Path),
                    stato = appServices!.App,
                    metro = appServices.Metro,
                    pid = Pids(scan, p => RepoProcesses.IsApp(p, appRepo.Path)),
                    git = git.TryGetValue(appRepo, out var appGit) ? GitSection(appGit) : null,
                },
            repository = repositories.Select(r => new
            {
                id = r.Id,
                tipo = AppConfiguration.TypeName(r.Type),
                predefinito = r.IsPrimary,
                cartella = r.Path,
                esiste = Directory.Exists(r.Path),
                git = git.TryGetValue(r, out var repositoryGit) ? GitSection(repositoryGit) : null,
                devTunnel = !r.HasTunnel ? null : TunnelSection(tunnels.GetValueOrDefault(r), services[r].HostState, services[r].HostToolTip),
                servizi = new
                {
                    kestrel = services[r].Kestrel,
                    devServer = services[r].DevServer,
                    app = services[r].App,
                    metro = services[r].Metro,
                    hostTunnel = !r.HasTunnel ? null : ConsoleServices.Describe(services[r].HostState),
                },
            }),
        });
    }

    /// <summary>Lo stato dei servizi di un repository, con gli stessi marcatori dell'exe (chiave "N" o "N-id").</summary>
    private static RepositoryServices Services(EnvironmentRepository repository, IReadOnlyList<ProcessRow> scan, DevTunnelStatus? tunnel)
    {
        var key = repository.Key;
        var path = repository.Path;
        var (hostState, hostToolTip) = PoolHostOf(repository, scan, tunnel);

        switch (repository.Repository)
        {
            case BackendConfiguration backend:
            {
                var view = new BackendTools(path, backend, key).Evaluate(scan, tunnel?.TunnelId, tunnel?.IsHostConnected == true, string.Empty);
                return new RepositoryServices(ConsoleServices.Describe(view.Kestrel), null, null, null, hostState, hostToolTip);
            }

            case FrontendConfiguration frontend:
            {
                var devServer = ConsoleServices.StateOf(scan, ConsoleServices.Marker("frontend", key), p => RepoProcesses.IsFrontend(p, path, frontend));
                return new RepositoryServices(null, ConsoleServices.Describe(devServer), null, null, hostState, hostToolTip);
            }

            case AppRepoConfiguration:
            {
                var app = ConsoleServices.StateOf(scan, ConsoleServices.Marker("app", key), p => RepoProcesses.IsApp(p, path));
                var metro = ConsoleServices.StateOf(scan, ConsoleServices.Marker("metro", key), p => RepoProcesses.IsMetro(p, path));
                return new RepositoryServices(null, null, ConsoleServices.Describe(app), ConsoleServices.Describe(metro), hostState, hostToolTip);
            }

            default:
                return new RepositoryServices(null, null, null, null, hostState, hostToolTip);
        }
    }

    // L'host di un repository e' quello del tunnel del pool su cui ha la porta (console
    // AMBIENTI tunnel-pool-<Description> nella cartella dati); senza porta non e' condiviso.
    private static (ServiceState State, string ToolTip) PoolHostOf(EnvironmentRepository repository, IReadOnlyList<ProcessRow> scan, DevTunnelStatus? tunnel)
    {
        if (Soffritto.Core.Docker.TunnelPool.AssignmentOf(repository.Number, repository.Id) is not { } assignment)
        {
            return (ServiceState.Stopped, "non condiviso");
        }

        var host = PoolHosts.Tools(assignment.TunnelDescription).Evaluate(scan, tunnel?.TunnelId ?? assignment.TunnelId, tunnel?.IsHostConnected == true, string.Empty);
        return (host.State, host.ToolTip);
    }

    private static async Task<Dictionary<EnvironmentRepository, DevTunnelStatus>> ReadTunnelsAsync(
        IReadOnlyList<EnvironmentRepository> repositories,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<EnvironmentRepository, DevTunnelStatus>();

        // Prima i predefiniti, cosi' le chiavi di sempre arrivano anche se un tunnel in piu' e' lento.
        foreach (var repository in repositories.OrderByDescending(r => r.IsPrimary))
        {
            if (repository.HasTunnel && Directory.Exists(repository.Path))
            {
                // Il tunnel del repository e' quello della sua porta del pool, con la sola porta assegnata.
                result[repository] = Soffritto.Core.Docker.TunnelPool.AssignmentOf(repository.Number, repository.Id) is { } assignment
                    ? await DevTunnelReader.ReadAsync(repository.Path, assignment.TunnelDescription, cancellationToken, assignment.Port).ConfigureAwait(false)
                    : DevTunnelStatus.NotShared("non condiviso: nessuna porta del pool (si assegna dall'exe Soffritto)");
            }
        }

        return result;
    }

    private static object TunnelSection(DevTunnelStatus? status, ServiceState host, string hostToolTip) => new
    {
        letto = status is not null,
        condiviso = status?.Shared,
        descrizione = status?.Description,
        id = status?.TunnelId,
        host = ConsoleServices.Describe(host),
        dettaglioHost = hostToolTip,
        connessioniHost = status?.HostConnections,
        porte = status?.AllPorts.Select(p => new { porta = p.PortNumber, uri = p.PortUri, raggiungibile = p.Reachable }),
        errore = status is { Shared: true } ? status.Error : null,
        nota = status is { Shared: false } ? status.Error : null,
        tunnelDisponibili = status is { Shared: true, Error: not null }
            ? status.AllAvailable.Select(t => new { id = t.TunnelId, descrizione = t.Description, hostConnesso = t.IsHostConnected })
            : null,
    };

    internal static object DatabaseSection(string backendPath, ProbeOptions options, StatusReport report)
    {
        var (_, sourceFile, _) = ConnectionStringLocator.Find(backendPath, options);

        return new
        {
            server = report.Server,
            database = report.Database,
            fileConnectionString = sourceFile,
            errore = report.ConfigurationError ?? report.DatabaseError,
            allineato = report.IsEnvironmentUpToDate,
            context = report.Contexts.Select(c => new
            {
                nome = c.ContextName,
                applicate = c.AppliedCount,
                totali = c.Entries.Count,
                ultimaApplicata = c.LastApplied,
                mancanti = c.Pending.Count,
                fuoriOrdine = c.OutOfOrder.Count,
                elencoMancanti = c.Pending.Count > 0
                    ? c.Pending.Take(MaxPendingListed).Select(e => new
                    {
                        id = e.MigrationId,
                        fuoriOrdine = e.State == MigrationState.PendingOutOfOrder ? true : (bool?)null,
                    })
                    : null,
                updateBanale = c.Pending.Count > 0 ? c.CanApplyPending : (bool?)null,
                idDiversiDalNomeFile = c.FileNameMismatches.Count > 0
                    ? c.FileNameMismatches.Select(e => new { id = e.MigrationId, file = e.MismatchedFileName })
                    : null,
            }),
            sconosciute = report.Unknown.Select(e => e.MigrationId),
        };
    }

    internal static object GitSection(GitStatus git) => new
    {
        branch = git.Branch,
        detached = git.IsDetached ? true : (bool?)null,
        upstream = git.HasUpstream,
        ahead = git.Ahead,
        behind = git.Behind,
        fileModificati = git.DirtyFiles,
        erroreFetch = git.FetchError,
        errore = git.Error,
    };

    private static void CollectProblems(
        List<string> problems,
        int n,
        IReadOnlyList<EnvironmentRepository> repositories,
        StatusReport? migrations,
        DevTunnelStatus? tunnel,
        Dictionary<EnvironmentRepository, GitStatus> git,
        bool? frontendOnBackend,
        int? kestrelPort,
        string? schemaLock)
    {
        foreach (var repository in repositories.Where(r => !Directory.Exists(r.Path)))
        {
            problems.Add($"Manca la cartella {OfRepo(repository)} dell'ambiente {n}: {repository.Path}.");
        }

        if (migrations is not null)
        {
            if ((migrations.ConfigurationError ?? migrations.DatabaseError) is { } dbError)
            {
                problems.Add($"Database del backend {n} non letto: {dbError}");
            }

            foreach (var context in migrations.Contexts.Where(c => !c.IsUpToDate))
            {
                problems.Add(context.OutOfOrder.Count > 0
                    ? $"{context.ContextName}: {context.Pending.Count} migrazioni mancanti, di cui {context.OutOfOrder.Count} fuori ordine (rientro da un branch parallelo)."
                    : $"{context.ContextName}: {context.Pending.Count} migrazioni mancanti su {migrations.Database}.");
                if (schemaLock is not null)
                {
                    problems.Add($"{context.ContextName}: le migrazioni mancanti non si applicano, {schemaLock}.");
                }
            }

            if (migrations.Unknown.Count > 0)
            {
                problems.Add($"{migrations.Unknown.Count} migrazioni applicate su {migrations.Database} non esistono nel codice del branch corrente.");
            }
        }

        foreach (var (repository, status) in git)
        {
            var name = $"git {OfRepo(repository)}";
            if (status.Error is not null)
            {
                problems.Add($"{name}: {status.Error}");
            }
            else if (status.Behind > 0)
            {
                problems.Add($"{name}: il branch {status.Branch} e' indietro di {status.Behind} commit rispetto all'upstream.");
            }

            if (status.FetchError is not null)
            {
                problems.Add($"{name}: fetch non riuscito ({status.FetchError}).");
            }
        }

        // Un backend non condiviso non e' un problema: la porta del pool si assegna quando serve.
        if (tunnel is { Shared: true, Error: not null })
        {
            problems.Add($"Dev tunnel {n}: {tunnel.Error}");
        }

        if (frontendOnBackend == false)
        {
            problems.Add($"Nessuna variabile dell'env del frontend {n} punta al backend {n} (localhost:{kestrelPort?.ToString() ?? "<porta>"} o il suo URL del pool).");
        }
    }

    // Come li risolve ASP.NET: gli endpoint Kestrel del primo appsettings che li dichiara vincono
    // sull'applicationUrl del profilo di launchSettings.
    private static IReadOnlyList<string> KestrelUrls(string backendPath, BackendConfiguration settings)
    {
        foreach (var candidate in settings.AppSettings)
        {
            using var appSettings = ReadJson(Path.Combine(backendPath, candidate));
            if (appSettings is not null
                && appSettings.RootElement.TryGetProperty("Kestrel", out var kestrel)
                && kestrel.TryGetProperty("Endpoints", out var endpoints)
                && endpoints.ValueKind == JsonValueKind.Object)
            {
                var urls = endpoints.EnumerateObject()
                    .Select(e => e.Value.ValueKind == JsonValueKind.Object && e.Value.TryGetProperty("Url", out var url) ? url.GetString() : null)
                    .OfType<string>()
                    .ToList();
                if (urls.Count > 0)
                {
                    return urls;
                }
            }
        }

        using var launchSettings = ReadJson(Path.Combine(backendPath, settings.ApiProject, "Properties", "launchSettings.json"));
        if (launchSettings is not null
            && launchSettings.RootElement.TryGetProperty("profiles", out var profiles)
            && profiles.TryGetProperty(settings.LaunchProfile, out var profile)
            && profile.TryGetProperty("applicationUrl", out var applicationUrl)
            && applicationUrl.ValueKind == JsonValueKind.String)
        {
            return applicationUrl.GetString()!.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        return [];
    }

    private static JsonDocument? ReadJson(string file)
    {
        try
        {
            return File.Exists(file)
                ? JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    // I predefiniti si chiamano come sempre ("del backend"); gli altri con il loro id.
    private static string OfRepo(EnvironmentRepository repository)
        => !repository.IsPrimary ? $"del repository {repository.Id}"
            : repository.Type == RepositoryType.App ? "dell'app"
            : $"del {AppConfiguration.TypeName(repository.Type)}";

    private static IReadOnlyList<int> Pids(IReadOnlyList<ProcessRow> scan, Func<ProcessRow, bool> isAnchor)
        => scan.Where(isAnchor).Select(p => p.ProcessId).ToList();

    private static EnvironmentRepository? PrimaryIn(IReadOnlyList<EnvironmentRepository> repositories, RepositoryType type)
        => repositories.FirstOrDefault(r => r.IsPrimary && r.Type == type);

    private static object Folder(EnvironmentRepository? repository)
        => repository is null
            ? new { cartella = (string?)null, esiste = false }
            : new { cartella = (string?)repository.Path, esiste = Directory.Exists(repository.Path) };

    private static object Missing(RepositoryType type, int number) => new
    {
        cartella = (string?)null,
        esiste = false,
        nota = $"L'ambiente {number} non include un repository di tipo {AppConfiguration.TypeName(type)}.",
    };

    private static (int Number, EnvironmentRepository Repo)? Locate(AppConfiguration configuration, string path)
    {
        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        foreach (var environment in configuration.Environments)
        {
            foreach (var repository in configuration.RepositoriesOf(environment))
            {
                var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repository.Path));
                if (full.Equals(folder, StringComparison.OrdinalIgnoreCase)
                    || full.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    return (environment.Number, repository);
                }
            }
        }

        return null;
    }

    internal static AppConfiguration? LoadConfiguration(out string? error)
    {
        // Una cartella dati sbagliata non ricade su quella di lavoro: si risponde con l'errore.
        if (ConfigurationFiles.DataFolderProblem is { } problem)
        {
            error = problem;
            return null;
        }

        var configuration = ConfigurationFiles.TryLoad(out error);
        if (configuration is null)
        {
            error ??= $"Configurazione assente ({ConfigurationFiles.ConfigPath}): compilala dal setup dell'exe Soffritto.";
            return null;
        }

        if (configuration.Validate() is { Count: > 0 } errors)
        {
            error = $"Configurazione non valida ({ConfigurationFiles.ConfigPath}): {string.Join(" ", errors)}";
            return null;
        }

        return configuration;
    }

    internal static string Serialize(object value) => JsonSerializer.Serialize(value, Json);

    /// <summary>Stato dei servizi di un repository; null per quelli che il suo tipo non ha.</summary>
    private sealed record RepositoryServices(
        string? Kestrel,
        string? DevServer,
        string? App,
        string? Metro,
        ServiceState HostState,
        string HostToolTip);
}

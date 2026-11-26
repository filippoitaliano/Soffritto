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
    // Beyond this threshold the list of pending migrations tells the reader little and costs context: the count is enough.
    private const int MaxPendingListed = 20;

    private const string ExternalHost = "from a host that is not among this machine's processes";

    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    [McpServerTool(Name = "list_environments", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The configured environments, local (clones) and Docker (worktrees and containers), with the folders of the primary repositories (backend, frontend, app), the full list of repositories of each environment, and the environment a folder belongs to. An environment with protectedDatabase=true points to a protected database (for example one shared with others): it can be read and used normally, but no migrations are applied to it and its schema is never altered; for work that creates or applies migrations pick another environment (or create a Docker environment). Fast: it does not read databases, git, tunnels or containers.")]
    public static string ListEnvironments(
        [Description("Folder to recognize, usually the session's working folder; empty = the server's current folder.")] string? folder = null)
    {
        if (LoadConfiguration(out var error) is not { } configuration)
        {
            return Serialize(new { error });
        }

        var path = string.IsNullOrWhiteSpace(folder) ? Environment.CurrentDirectory : folder;
        var located = Locate(configuration, path);

        return Serialize(new
        {
            configFile = ConfigurationFiles.ConfigPath,
            recognizedFolder = new
            {
                folder = path,
                environment = located?.Number,
                repository = located is { } l ? AppConfiguration.TypeName(l.Repo.Type) : null,
                id = located?.Repo.Id,
                note = located is null ? "The folder does not belong to any configured environment." : null,
            },
            environments = configuration.Environments.Select(environment =>
            {
                var repositories = configuration.RepositoriesOf(environment);
                return new
                {
                    number = environment.Number,
                    kind = environment.Kind == EnvironmentKind.Docker ? "docker" : "local",
                    protectedDatabase = environment.SchemaLockReason is not null ? true : (bool?)null,
                    protectedDatabaseReason = environment.SchemaLockReason,
                    backend = Folder(PrimaryIn(repositories, RepositoryType.Backend)),
                    frontend = Folder(PrimaryIn(repositories, RepositoryType.Frontend)),
                    app = Folder(PrimaryIn(repositories, RepositoryType.App)),
                    repository = repositories.Select(r => new
                    {
                        id = r.Id,
                        type = AppConfiguration.TypeName(r.Type),
                        primary = r.IsPrimary,
                        folder = r.Path,
                        exists = Directory.Exists(r.Path),
                        extraCommand = ExtraCommand.Of(r.Repository),
                    }),
                };
            }),
        });
    }

    [McpServerTool(Name = "environment_status", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Full status of a local environment N: the database the primary backend points to (server, name, file the connection string comes from), EF migrations per context (pending, out of order, unknown), git and dev tunnels of all the environment's repositories, running services (Kestrel, tunnel hosts, dev server, app, Metro) and a list of problems. With protectedDatabase=true pending migrations are reported but not applied: the schema of a protected database is never touched. The backend, frontend and app keys describe the primary repositories; the repository list contains all of them. Read-only; takes a few seconds.")]
    public static async Task<string> EnvironmentStatus(
        [Description("Environment number; if missing, it is derived from 'folder'.")] int? number = null,
        [Description("Folder of a repository in the environment, used when 'number' is missing; empty = the server's current folder.")] string? folder = null,
        [Description("Runs git fetch before reading ahead/behind (touches only remote-tracking refs). Slower.")] bool fetch = false,
        [Description("Reads the dev tunnels of the environment's repositories (devtunnel list/show and port probes). Without it, a tunnel host is recognized only if it was started from the CLI.")] bool tunnel = true,
        CancellationToken cancellationToken = default)
    {
        if (LoadConfiguration(out var error) is not { } configuration)
        {
            return Serialize(new { error });
        }

        var resolved = number ?? Locate(configuration, string.IsNullOrWhiteSpace(folder) ? Environment.CurrentDirectory : folder)?.Number;
        if (resolved is { } docker && configuration.Environment(docker) is { Kind: EnvironmentKind.Docker })
        {
            return Serialize(new { error = $"Environment {docker} is a Docker environment: use docker_environment_status." });
        }

        if (resolved is not { } n || configuration.Environment(n) is not { Kind: EnvironmentKind.Local } environment)
        {
            return Serialize(new
            {
                error = number is null
                    ? "The folder does not belong to any configured environment: pass 'number'."
                    : $"Environment {number} is not configured (configured environments: {string.Join(", ", configuration.Environments.Select(e => e.Number))}).",
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
        // Tunnels are read one after the other, as the app does: it is not verified that the
        // devtunnel CLI handles concurrent calls on the same login.
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

        // The frontend points to the backend of the same environment if a value in its env contains
        // localhost:<Kestrel https port> or the host of the backend's pool URL.
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
            environment = n,
            protectedDatabase = environment.SchemaLockReason is not null ? true : (bool?)null,
            protectedDatabaseReason = environment.SchemaLockReason,
            problems,
            backend = backendRepo is null
                ? Missing(RepositoryType.Backend, n)
                : new
                {
                    folder = backendRepo.Path,
                    exists = Directory.Exists(backendRepo.Path),
                    kestrel = new
                    {
                        status = backendServices!.Kestrel,
                        pid = Pids(scan, p => ConsoleServices.Is(p, backendSettings.ApiProcess) && ConsoleServices.Under(p.ExecutablePath, backendRepo.Path)),
                        profile = backendSettings.LaunchProfile,
                        addresses = KestrelUrls(backendRepo.Path, backendSettings),
                    },
                    database = migrations is null ? null : DatabaseSection(backendRepo.Path, options, migrations),
                    git = git.TryGetValue(backendRepo, out var backendGit) ? GitSection(backendGit) : null,
                },
            devTunnel = backendRepo is null ? null : TunnelSection(tunnelStatus, backendServices!.HostState, backendServices.HostToolTip),
            frontend = frontendRepo is null
                ? Missing(RepositoryType.Frontend, n)
                : new
                {
                    folder = frontendRepo.Path,
                    exists = Directory.Exists(frontendRepo.Path),
                    devServer = new
                    {
                        status = frontendServices!.DevServer,
                        pid = Pids(scan, p => RepoProcesses.IsFrontend(p, frontendRepo.Path, frontendSettings)),
                        url = $"http://localhost:{devServerPort}/",
                    },
                    envFile = frontendSettings.EnvFile.Length > 0 ? frontendSettings.EnvFile : null,
                    envVariables,
                    pointsToEnvironmentBackend = frontendOnBackend,
                    devTunnel = TunnelSection(frontendTunnelStatus, frontendServices.HostState, frontendServices.HostToolTip),
                    git = git.TryGetValue(frontendRepo, out var frontendGit) ? GitSection(frontendGit) : null,
                },
            app = appRepo is null
                ? Missing(RepositoryType.App, n)
                : new
                {
                    folder = appRepo.Path,
                    exists = Directory.Exists(appRepo.Path),
                    status = appServices!.App,
                    metro = appServices.Metro,
                    pid = Pids(scan, p => RepoProcesses.IsApp(p, appRepo.Path)),
                    git = git.TryGetValue(appRepo, out var appGit) ? GitSection(appGit) : null,
                },
            repository = repositories.Select(r => new
            {
                id = r.Id,
                type = AppConfiguration.TypeName(r.Type),
                primary = r.IsPrimary,
                folder = r.Path,
                exists = Directory.Exists(r.Path),
                git = git.TryGetValue(r, out var repositoryGit) ? GitSection(repositoryGit) : null,
                devTunnel = !r.HasTunnel ? null : TunnelSection(tunnels.GetValueOrDefault(r), services[r].HostState, services[r].HostToolTip),
                services = new
                {
                    kestrel = services[r].Kestrel,
                    devServer = services[r].DevServer,
                    app = services[r].App,
                    metro = services[r].Metro,
                    tunnelHost = !r.HasTunnel ? null : ConsoleServices.Describe(services[r].HostState),
                },
            }),
        });
    }

    /// <summary>The status of a repository's services, with the same markers as the app (key "N" or "N-id").</summary>
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

    // A repository's host is the host of the pool tunnel it has a port on (console
    // AMBIENTI tunnel-pool-<Description> in the data folder); without a port it is not shared.
    private static (ServiceState State, string ToolTip) PoolHostOf(EnvironmentRepository repository, IReadOnlyList<ProcessRow> scan, DevTunnelStatus? tunnel)
    {
        if (Soffritto.Core.Docker.TunnelPool.AssignmentOf(repository.Number, repository.Id) is not { } assignment)
        {
            return (ServiceState.Stopped, "not shared");
        }

        var host = PoolHosts.Tools(assignment.TunnelDescription).Evaluate(scan, tunnel?.TunnelId ?? assignment.TunnelId, tunnel?.IsHostConnected == true, string.Empty);
        return (host.State, host.ToolTip);
    }

    private static async Task<Dictionary<EnvironmentRepository, DevTunnelStatus>> ReadTunnelsAsync(
        IReadOnlyList<EnvironmentRepository> repositories,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<EnvironmentRepository, DevTunnelStatus>();

        // Primary repositories first, so the usual keys arrive even if an additional tunnel is slow.
        foreach (var repository in repositories.OrderByDescending(r => r.IsPrimary))
        {
            if (repository.HasTunnel && Directory.Exists(repository.Path))
            {
                // The repository's tunnel is the one of its pool port, with only the assigned port.
                result[repository] = Soffritto.Core.Docker.TunnelPool.AssignmentOf(repository.Number, repository.Id) is { } assignment
                    ? await DevTunnelReader.ReadAsync(repository.Path, assignment.TunnelDescription, cancellationToken, assignment.Port).ConfigureAwait(false)
                    : DevTunnelStatus.NotShared("not shared: no pool port (assign one from the app)");
            }
        }

        return result;
    }

    private static object TunnelSection(DevTunnelStatus? status, ServiceState host, string hostToolTip) => new
    {
        read = status is not null,
        shared = status?.Shared,
        description = status?.Description,
        id = status?.TunnelId,
        host = ConsoleServices.Describe(host),
        hostDetail = hostToolTip,
        hostConnections = status?.HostConnections,
        ports = status?.AllPorts.Select(p => new { port = p.PortNumber, uri = p.PortUri, reachable = p.Reachable }),
        error = status is { Shared: true } ? status.Error : null,
        note = status is { Shared: false } ? status.Error : null,
        availableTunnels = status is { Shared: true, Error: not null }
            ? status.AllAvailable.Select(t => new { id = t.TunnelId, description = t.Description, hostConnected = t.IsHostConnected })
            : null,
    };

    internal static object DatabaseSection(string backendPath, ProbeOptions options, StatusReport report)
    {
        var (_, sourceFile, _) = ConnectionStringLocator.Find(backendPath, options);

        return new
        {
            server = report.Server,
            database = report.Database,
            connectionStringFile = sourceFile,
            error = report.ConfigurationError ?? report.DatabaseError,
            upToDate = report.IsEnvironmentUpToDate,
            context = report.Contexts.Select(c => new
            {
                name = c.ContextName,
                applied = c.AppliedCount,
                total = c.Entries.Count,
                lastApplied = c.LastApplied,
                pending = c.Pending.Count,
                outOfOrder = c.OutOfOrder.Count,
                pendingList = c.Pending.Count > 0
                    ? c.Pending.Take(MaxPendingListed).Select(e => new
                    {
                        id = e.MigrationId,
                        outOfOrder = e.State == MigrationState.PendingOutOfOrder ? true : (bool?)null,
                    })
                    : null,
                trivialUpdate = c.Pending.Count > 0 ? c.CanApplyPending : (bool?)null,
                idsNotMatchingFileName = c.FileNameMismatches.Count > 0
                    ? c.FileNameMismatches.Select(e => new { id = e.MigrationId, file = e.MismatchedFileName })
                    : null,
            }),
            unknown = report.Unknown.Select(e => e.MigrationId),
        };
    }

    internal static object GitSection(GitStatus git) => new
    {
        branch = git.Branch,
        detached = git.IsDetached ? true : (bool?)null,
        upstream = git.HasUpstream,
        ahead = git.Ahead,
        behind = git.Behind,
        modifiedFiles = git.DirtyFiles,
        fetchError = git.FetchError,
        error = git.Error,
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
            problems.Add($"The folder of {OfRepo(repository)} of environment {n} is missing: {repository.Path}.");
        }

        if (migrations is not null)
        {
            if ((migrations.ConfigurationError ?? migrations.DatabaseError) is { } dbError)
            {
                problems.Add($"Database of backend {n} not read: {dbError}");
            }

            foreach (var context in migrations.Contexts.Where(c => !c.IsUpToDate))
            {
                problems.Add(context.OutOfOrder.Count > 0
                    ? $"{context.ContextName}: {context.Pending.Count} pending migrations, {context.OutOfOrder.Count} of them out of order (merged in from a parallel branch)."
                    : $"{context.ContextName}: {context.Pending.Count} pending migrations on {migrations.Database}.");
                if (schemaLock is not null)
                {
                    problems.Add($"{context.ContextName}: the pending migrations are not applied, {schemaLock}.");
                }
            }

            if (migrations.Unknown.Count > 0)
            {
                problems.Add($"{migrations.Unknown.Count} migrations applied on {migrations.Database} do not exist in the code of the current branch.");
            }
        }

        foreach (var (repository, status) in git)
        {
            var name = $"git of {OfRepo(repository)}";
            if (status.Error is not null)
            {
                problems.Add($"{name}: {status.Error}");
            }
            else if (status.Behind > 0)
            {
                problems.Add($"{name}: branch {status.Branch} is {status.Behind} commits behind its upstream.");
            }

            if (status.FetchError is not null)
            {
                problems.Add($"{name}: fetch failed ({status.FetchError}).");
            }
        }

        // An unshared backend is not a problem: the pool port is assigned when needed.
        if (tunnel is { Shared: true, Error: not null })
        {
            problems.Add($"Dev tunnel {n}: {tunnel.Error}");
        }

        if (frontendOnBackend == false)
        {
            problems.Add($"No variable in the env of frontend {n} points to backend {n} (localhost:{kestrelPort?.ToString() ?? "<port>"} or its pool URL).");
        }
    }

    // As ASP.NET resolves them: the Kestrel endpoints of the first appsettings that declares them win
    // over the applicationUrl of the launchSettings profile.
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

    // Primary repositories are named as usual ("the backend"); the others by their id.
    private static string OfRepo(EnvironmentRepository repository)
        => !repository.IsPrimary ? $"repository {repository.Id}"
            : repository.Type == RepositoryType.App ? "the app"
            : $"the {AppConfiguration.TypeName(repository.Type)}";

    private static IReadOnlyList<int> Pids(IReadOnlyList<ProcessRow> scan, Func<ProcessRow, bool> isAnchor)
        => scan.Where(isAnchor).Select(p => p.ProcessId).ToList();

    private static EnvironmentRepository? PrimaryIn(IReadOnlyList<EnvironmentRepository> repositories, RepositoryType type)
        => repositories.FirstOrDefault(r => r.IsPrimary && r.Type == type);

    private static object Folder(EnvironmentRepository? repository)
        => repository is null
            ? new { folder = (string?)null, exists = false }
            : new { folder = (string?)repository.Path, exists = Directory.Exists(repository.Path) };

    private static object Missing(RepositoryType type, int number) => new
    {
        folder = (string?)null,
        exists = false,
        note = $"Environment {number} does not include a repository of type {AppConfiguration.TypeName(type)}.",
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
        // A wrong data folder does not fall back to the working folder: the response carries the error.
        if (ConfigurationFiles.DataFolderProblem is { } problem)
        {
            error = problem;
            return null;
        }

        var configuration = ConfigurationFiles.TryLoad(out error);
        if (configuration is null)
        {
            error ??= $"Configuration missing ({ConfigurationFiles.ConfigPath}): fill it in from the app's setup.";
            return null;
        }

        if (configuration.Validate() is { Count: > 0 } errors)
        {
            error = $"Invalid configuration ({ConfigurationFiles.ConfigPath}): {string.Join(" ", errors)}";
            return null;
        }

        return configuration;
    }

    internal static string Serialize(object value) => JsonSerializer.Serialize(value, Json);

    /// <summary>Status of a repository's services; null for those its type does not have.</summary>
    private sealed record RepositoryServices(
        string? Kestrel,
        string? DevServer,
        string? App,
        string? Metro,
        ServiceState HostState,
        string HostToolTip);
}

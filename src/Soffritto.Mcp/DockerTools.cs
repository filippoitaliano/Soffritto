using System.ComponentModel;
using ModelContextProtocol.Server;
using Soffritto.Core;
using Soffritto.Core.Configuration;
using Soffritto.Core.Docker;
using Soffritto.Core.Env;
using Soffritto.Presentation;

namespace Soffritto.Mcp;

/// <summary>
/// The write tools for Docker environments: the same actions as the app (the “+” in the
/// header and the columns), on the same Core APIs. Long operations run in the background (<see cref="Operations"/>).
/// </summary>
[McpServerToolType]
public static class DockerTools
{
    private const string WaitDescription = "Seconds to wait for completion before responding (0-110): if the operation takes longer, the response contains the id to follow with operation_status.";

    [McpServerTool(Name = "docker_environment_status", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Status of Docker environment N: for each repository the worktree (the folder to work in), the branch and the git status; for each compose service (database, api, web) the container and whether it responds; local addresses and pool URLs; the web's Compose Watch console; EF migrations of the slot database against the worktree's branch; the environment note; the running operation. Read-only; takes a few seconds.")]
    public static async Task<string> DockerEnvironmentStatus(
        [Description("Docker environment number.")] int number,
        [Description("Reads from Azure DevOps the work items whose PR branches the environment sits on (section 'work', like the environment tag of the app's PR panel); at most 10 seconds, in parallel with the other reads.")] bool work = true,
        CancellationToken cancellationToken = default)
    {
        if (Resolve(number, out var configuration, out var slot) is { } error)
        {
            return EnvironmentTools.Serialize(new { error });
        }

        var workTask = work ? WorkTools.ReadAsync(configuration, cancellationToken) : null;
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
                service,
                status = container is null ? "missing" : answers ? "ready" : container.RunState == ContainerRunState.Stopped ? "stopped" : "starting",
                container = container?.Status,
                address = isDatabase ? $"127.0.0.1,{port}" : $"http://localhost:{port}/",
            });
        }

        var repositories = new List<object>();
        var workRepositories = new List<WorkRepository>();
        foreach (var entry in slot.Repositories)
        {
            var git = Directory.Exists(entry.Path) ? await GitStatusReader.ReadAsync(entry.Path, false, cancellationToken).ConfigureAwait(false) : null;
            workRepositories.Add(new WorkRepository(EnvironmentWork.RepositoryNameOf(entry.Repository), git is not null, git?.Branch));
            repositories.Add(new
            {
                id = entry.Id,
                type = AppConfiguration.TypeName(entry.Type),
                worktree = entry.Path,
                exists = Directory.Exists(entry.Path),
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
                databaseReady ? null : "slot database stopped or starting").ConfigureAwait(false);
            database = EnvironmentTools.DatabaseSection(backendEntry.Path, options, report);
        }

        var poolUrls = await DockerEnvironmentProvisioner.PoolUrlsAsync(number, cancellationToken).ConfigureAwait(false);
        return EnvironmentTools.Serialize(new
        {
            environment = number,
            composeProject = slot.Project,
            slotFolder = slot.ComposeFolder,
            docker = docker.Error,
            services,
            webWatch = slot.HasContainerizedFrontend
                ? ConsoleServices.Describe(DockerWatchConsole.StateOf(ProcessTable.Read([]), slot))
                : null,
            poolUrls = poolUrls.Count > 0 ? poolUrls : null,
            repository = repositories,
            database,
            note = ReadNote(number),
            runningOperation = Operations.RunningOn(number)?.Snapshot(10),
            work = workTask is null ? null : WorkTools.Section(await workTask.ConfigureAwait(false), workRepositories),
        });
    }

    [McpServerTool(Name = "create_docker_environment", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Creates a Docker environment: for each repository a worktree on the given branch (a branch that does not exist on origin is created from the main development branch, without upstream), env files written, slot database restored from the seed and migrated to the branch, database, api and web containers started, the web's Compose Watch opened. Work happens in the worktrees: saved code reaches the web container through the watch, the api is rebuilt with start_docker_service. Takes a few minutes: responds with the operation to follow.")]
    public static Task<string> CreateDockerEnvironment(
        [Description("Repositories to include and the branch of each, by catalog id (see list_environments), e.g. { \"backend\": \"feature/123-orders\", \"frontend\": \"feature/123-orders\" }. The backend with the compose file is required.")] Dictionary<string, string> branch,
        [Description("Environment number; empty = the lowest free number.")] int? number = null,
        [Description("Turns on the environment's tunnel: backend and frontend get a port on a pool tunnel, so they can be reached from outside. If the pool is full, creation stops with the error.")] bool enableTunnel = false,
        [Description("Text of the environment note, shown on the environment's column in the app (e.g. the work it is dedicated to).")] string? note = null,
        [Description(WaitDescription)] int waitSeconds = 20,
        CancellationToken cancellationToken = default)
        => Run(async () =>
        {
            if (EnvironmentTools.LoadConfiguration(out var error) is not { } configuration)
            {
                return new { error };
            }

            var environmentNumber = number ?? Enumerable.Range(1, AppConfiguration.MaxEnvironmentNumber).FirstOrDefault(n => configuration.Environment(n) is null);
            if (environmentNumber == 0)
            {
                return new { error = "no free number for a new environment" };
            }

            var branches = branch.ToDictionary(p => p.Key, p => p.Value.Trim(), StringComparer.OrdinalIgnoreCase);
            var included = configuration.Repositories.Where(r => branches.ContainsKey(r.Id)).ToList();
            var pool = enableTunnel
                ? included.Where(r => r.Type is RepositoryType.Backend or RepositoryType.Frontend).Select(r => r.Id).ToList()
                : null;
            return await Operations.StartAsync(
                OperationCatalog.DockerCreate,
                "create",
                environmentNumber,
                new CreateArgs(environmentNumber, branches, pool, note),
                waitSeconds,
                cancellationToken).ConfigureAwait(false);
        });

    [McpServerTool(Name = "start_docker_environment", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Starts the Docker environment's containers (docker compose up -d --build, with the env_file files rewritten), opens the web's Compose Watch and, if the environment has its tunnel on, restarts the tunnel host if it dropped or was closed. Rebuilds the images if the code has changed.")]
    public static Task<string> StartDockerEnvironment(
        [Description("Docker environment number.")] int number,
        [Description(WaitDescription)] int waitSeconds = 20,
        CancellationToken cancellationToken = default)
        => OnSlot(number, OperationCatalog.DockerStart, "start", new EnvironmentArgs(number), waitSeconds, cancellationToken);

    [McpServerTool(Name = "stop_docker_environment", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Closes the web's Compose Watch and stops the Docker environment's containers (docker compose stop). Worktrees, database and images are kept.")]
    public static Task<string> StopDockerEnvironment(
        [Description("Docker environment number.")] int number,
        [Description(WaitDescription)] int waitSeconds = 30,
        CancellationToken cancellationToken = default)
        => OnSlot(number, OperationCatalog.DockerStop, "stop", new EnvironmentArgs(number), waitSeconds, cancellationToken);

    [McpServerTool(Name = "start_docker_service", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Starts or rebuilds a service of the Docker environment (up -d --build for api and web, up -d for the database), with its env_file rewritten. After changes to the backend code, use it to see them in the api; for the web it also opens the Compose Watch.")]
    public static Task<string> StartDockerService(
        [Description("Docker environment number.")] int number,
        [Description("Compose service: the api, web or database one (the names are in docker_environment_status).")] string service,
        [Description(WaitDescription)] int waitSeconds = 20,
        CancellationToken cancellationToken = default)
        => OnSlot(number, OperationCatalog.DockerStartService, $"start {service}", new ServiceArgs(number, service), waitSeconds, cancellationToken);

    [McpServerTool(Name = "stop_docker_service", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Stops a service of the Docker environment (docker compose stop); for the web it closes the Compose Watch first.")]
    public static Task<string> StopDockerService(
        [Description("Docker environment number.")] int number,
        [Description("Compose service: the api, web or database one.")] string service,
        [Description(WaitDescription)] int waitSeconds = 30,
        CancellationToken cancellationToken = default)
        => OnSlot(number, OperationCatalog.DockerStopService, $"stop {service}", new ServiceArgs(number, service), waitSeconds, cancellationToken);

    [McpServerTool(Name = "docker_service_logs", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("The last lines of the log of a service of the Docker environment (docker compose logs --tail).")]
    public static async Task<string> DockerServiceLogs(
        [Description("Docker environment number.")] int number,
        [Description("Compose service: the api, web or database one.")] string service,
        [Description("How many lines from the end (1-2000).")] int lines = 200,
        CancellationToken cancellationToken = default)
    {
        if (Resolve(number, out _, out var slot) is { } error)
        {
            return EnvironmentTools.Serialize(new { error });
        }

        if (!Services(slot).Any(s => s.Service == service))
        {
            return EnvironmentTools.Serialize(new { error = $"{service} is not a service of environment {number} ({string.Join(", ", Services(slot).Select(s => s.Service))})" });
        }

        var logs = await ComposeRunner.RunAsync(
            slot,
            $"logs --no-color --tail {Math.Clamp(lines, 1, 2000)} {service}",
            web: string.Equals(service, slot.WebService, StringComparison.Ordinal),
            timeoutSeconds: 60,
            ct: cancellationToken).ConfigureAwait(false);
        return EnvironmentTools.Serialize(logs.Success
            ? new { service, log = logs.StandardOutput + logs.StandardError }
            : (object)new { error = logs.Tail(10) });
    }

    [McpServerTool(Name = "apply_docker_migrations", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Applies the EF migrations of the backend worktree's branch to the Docker environment's database (dotnet restore, then dotnet ef database update per context), guarding that the worktree points to the slot database. Use it after adding or changing a migration.")]
    public static Task<string> ApplyDockerMigrations(
        [Description("Docker environment number.")] int number,
        [Description("Only this context; empty = all.")] string? context = null,
        [Description(WaitDescription)] int waitSeconds = 60,
        CancellationToken cancellationToken = default)
        => OnSlot(number, OperationCatalog.DockerMigrations, "migrations", new DockerMigrationsArgs(number, context), waitSeconds, cancellationToken);

    [McpServerTool(Name = "repair_docker_env", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Rewrites the worktrees' env files and the containers' env_file files of the Docker environment from the configuration (like \"Repair env\" in the app), then checks that the backend points to the slot database. The containers read the env_file files on their next start.")]
    public static Task<string> RepairDockerEnv(
        [Description("Docker environment number.")] int number,
        [Description(WaitDescription)] int waitSeconds = 20,
        CancellationToken cancellationToken = default)
        => OnSlot(number, OperationCatalog.DockerRepair, "repair env", new EnvironmentArgs(number), waitSeconds, cancellationToken);

    [McpServerTool(Name = "set_environment_note", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Writes the note of an environment (local or Docker), shown on its column in the app: for example the work it is dedicated to. Replaces the previous note; an empty text clears it.")]
    public static string SetEnvironmentNote(
        [Description("Environment number.")] int number,
        [Description("Text of the note.")] string text)
    {
        if (EnvironmentTools.LoadConfiguration(out var error) is not { } configuration)
        {
            return EnvironmentTools.Serialize(new { error });
        }

        if (configuration.Environment(number) is null)
        {
            return EnvironmentTools.Serialize(new { error = $"environment {number} is not configured" });
        }

        WriteNote(number, text);
        return EnvironmentTools.Serialize(new { result = "note written", note = text });
    }

    [McpServerTool(Name = "operation_status", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Progress and result of an operation started by a tool (create, start, migrations, pull, checkout, extra command…): status running / succeeded / failed, last lines of the log, error and result. Operations run in a worker process of their own, recorded in the data folder: they survive the MCP server and the client session, and any session can follow them.")]
    public static async Task<string> OperationStatus(
        [Description("Operation id, from the response of the tool that started it.")] string operation,
        [Description("Log lines to return, from the end.")] int lines = 40,
        [Description("Seconds to wait for completion before responding (0-110).")] int waitSeconds = 0,
        CancellationToken cancellationToken = default)
    {
        if (Operations.Find(operation) is not { } found)
        {
            return EnvironmentTools.Serialize(new { error = $"unknown operation {operation}: operations are kept for a week" });
        }

        await Operations.WaitAsync(found, waitSeconds, cancellationToken).ConfigureAwait(false);
        return EnvironmentTools.Serialize(found.Snapshot(Math.Clamp(lines, 1, 400)));
    }

    private static async Task<string> Run(Func<Task<object>> body)
        => EnvironmentTools.Serialize(await body().ConfigureAwait(false));

    private static Task<string> OnSlot(int number, string kind, string label, object args, int waitSeconds, CancellationToken cancellationToken)
        => Run(async () => Resolve(number, out _, out _) is { } error
            ? new { error }
            : await Operations.StartAsync(kind, label, number, args, waitSeconds, cancellationToken).ConfigureAwait(false));

    // The configuration and slot of a Docker environment, or the error to return.
    private static string? Resolve(int number, out AppConfiguration configuration, out DockerSlot slot)
    {
        configuration = null!;
        slot = null!;
        if (EnvironmentTools.LoadConfiguration(out var error) is not { } loaded)
        {
            return error;
        }

        if (loaded.Environment(number) is not { } environment)
        {
            return $"environment {number} is not configured";
        }

        if (environment.Kind != EnvironmentKind.Docker)
        {
            return $"environment {number} is local: the *_docker tools act only on Docker environments";
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

    internal static object Summary(DockerSlot slot) => new
    {
        environment = slot.Number,
        worktree = slot.Repositories.ToDictionary(r => r.Id, r => r.Path),
        services = Services(slot).Select(s => new { service = s.Service, address = s.IsDatabase ? $"127.0.0.1,{s.Port}" : $"http://localhost:{s.Port}/" }),
        nextStep = "docker_environment_status for the status of containers and migrations",
    };

    private static string NotePath(int number) => EnvironmentNote.PathOf(number.ToString());

    private static string? ReadNote(int number)
        => File.Exists(NotePath(number)) && File.ReadAllText(NotePath(number)) is { Length: > 0 } text ? text : null;

    // The same file as EnvironmentNote: the app watches it and updates the column.
    internal static void WriteNote(int number, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(NotePath(number))!);
        File.WriteAllText(NotePath(number), text);
    }
}

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
        CancellationToken cancellationToken = default)
    {
        if (Resolve(number, out var configuration, out var slot) is { } error)
        {
            return EnvironmentTools.Serialize(new { error });
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
                service,
                status = container is null ? "missing" : answers ? "ready" : container.RunState == ContainerRunState.Stopped ? "stopped" : "starting",
                container = container?.Status,
                address = isDatabase ? $"127.0.0.1,{port}" : $"http://localhost:{port}/",
            });
        }

        var repositories = new List<object>();
        foreach (var entry in slot.Repositories)
        {
            var git = Directory.Exists(entry.Path) ? await GitStatusReader.ReadAsync(entry.Path, false, cancellationToken).ConfigureAwait(false) : null;
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
            var request = new DockerCreateRequest(environmentNumber, branches, pool, index => PoolHosts.Ensure(configuration, index));

            return await Operations.RunAsync("create", environmentNumber, waitSeconds, async (operation, ct) =>
            {
                var createError = await DockerEnvironmentProvisioner.CreateAsync(
                    configuration,
                    request,
                    _ =>
                    {
                        if (!string.IsNullOrWhiteSpace(note))
                        {
                            WriteNote(environmentNumber, note);
                        }
                    },
                    operation.Log,
                    ct).ConfigureAwait(false);
                if (createError is not null)
                {
                    return (createError, null);
                }

                // CreateAsync records the entry in config.json, not in the configuration passed in.
                if (EnvironmentTools.LoadConfiguration(out var reloadError) is not { } reloaded || reloaded.Environment(environmentNumber) is not { } created)
                {
                    return ($"environment {environmentNumber} created, but config.json cannot be reloaded: {reloadError}", null);
                }

                var slot = DockerSlot.Of(reloaded, created);
                if (slot.HasContainerizedFrontend)
                {
                    operation.Log(DockerWatchConsole.Start(ProcessTable.Read([]), slot));
                }

                return (null, Summary(slot));
            }, cancellationToken).ConfigureAwait(false);
        });

    [McpServerTool(Name = "start_docker_environment", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Starts the Docker environment's containers (docker compose up -d --build, with the env_file files rewritten), opens the web's Compose Watch and, if the environment has its tunnel on, restarts the tunnel host if it dropped or was closed. Rebuilds the images if the code has changed.")]
    public static Task<string> StartDockerEnvironment(
        [Description("Docker environment number.")] int number,
        [Description(WaitDescription)] int waitSeconds = 20,
        CancellationToken cancellationToken = default)
        => OnSlot(number, "start", waitSeconds, async (configuration, slot, operation, ct) =>
        {
            if (await DockerEnvironmentProvisioner.StartAsync(configuration, number, operation.Log, ct).ConfigureAwait(false) is { } error)
            {
                return (error, null);
            }

            if (slot.HasContainerizedFrontend)
            {
                operation.Log(DockerWatchConsole.Start(ProcessTable.Read([]), slot));
            }

            EnvironmentTunnel.RestoreHosts(number, operation.Log);

            return (null, Summary(slot));
        }, cancellationToken);

    [McpServerTool(Name = "stop_docker_environment", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Closes the web's Compose Watch and stops the Docker environment's containers (docker compose stop). Worktrees, database and images are kept.")]
    public static Task<string> StopDockerEnvironment(
        [Description("Docker environment number.")] int number,
        [Description(WaitDescription)] int waitSeconds = 30,
        CancellationToken cancellationToken = default)
        => OnSlot(number, "stop", waitSeconds, async (configuration, slot, operation, ct) =>
        {
            if (slot.HasContainerizedFrontend)
            {
                operation.Log(DockerWatchConsole.Stop(ProcessTable.Read([]), slot));
            }

            var (outcome, allStopped) = await DockerEnvironmentProvisioner.StopAsync(configuration, number, operation.Log, ct).ConfigureAwait(false);
            return (allStopped ? null : outcome, new { result = outcome });
        }, cancellationToken);

    [McpServerTool(Name = "start_docker_service", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Starts or rebuilds a service of the Docker environment (up -d --build for api and web, up -d for the database), with its env_file rewritten. After changes to the backend code, use it to see them in the api; for the web it also opens the Compose Watch.")]
    public static Task<string> StartDockerService(
        [Description("Docker environment number.")] int number,
        [Description("Compose service: the api, web or database one (the names are in docker_environment_status).")] string service,
        [Description(WaitDescription)] int waitSeconds = 20,
        CancellationToken cancellationToken = default)
        => OnSlot(number, $"start {service}", waitSeconds, async (configuration, slot, operation, ct) =>
        {
            if (await DockerEnvironmentProvisioner.StartServiceAsync(configuration, number, service, operation.Log, ct).ConfigureAwait(false) is { } error)
            {
                return (error, null);
            }

            if (string.Equals(service, slot.WebService, StringComparison.Ordinal))
            {
                operation.Log(DockerWatchConsole.Start(ProcessTable.Read([]), slot));
            }

            return (null, new { result = $"{service} started" });
        }, cancellationToken);

    [McpServerTool(Name = "stop_docker_service", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Stops a service of the Docker environment (docker compose stop); for the web it closes the Compose Watch first.")]
    public static Task<string> StopDockerService(
        [Description("Docker environment number.")] int number,
        [Description("Compose service: the api, web or database one.")] string service,
        [Description(WaitDescription)] int waitSeconds = 30,
        CancellationToken cancellationToken = default)
        => OnSlot(number, $"stop {service}", waitSeconds, async (configuration, slot, operation, ct) =>
        {
            if (string.Equals(service, slot.WebService, StringComparison.Ordinal))
            {
                operation.Log(DockerWatchConsole.Stop(ProcessTable.Read([]), slot));
            }

            var error = await DockerEnvironmentProvisioner.StopServiceAsync(configuration, number, service, operation.Log, ct).ConfigureAwait(false);
            return (error, error is null ? new { result = $"{service} stopped" } : null);
        }, cancellationToken);

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
        => OnSlot(number, "migrations", waitSeconds, async (configuration, slot, operation, ct) =>
        {
            if (slot.Backend?.Repository is not BackendConfiguration backend)
            {
                return ($"environment {number} has no backend", null);
            }

            var error = await DockerEnvironmentProvisioner.ApplyMigrationsAsync(slot, backend, operation.Log, ct, context).ConfigureAwait(false);
            return (error, error is null ? new { result = "migrations applied" } : null);
        }, cancellationToken);

    [McpServerTool(Name = "repair_docker_env", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Rewrites the worktrees' env files and the containers' env_file files of the Docker environment from the configuration (like \"Repair env\" in the app), then checks that the backend points to the slot database. The containers read the env_file files on their next start.")]
    public static Task<string> RepairDockerEnv(
        [Description("Docker environment number.")] int number,
        [Description(WaitDescription)] int waitSeconds = 20,
        CancellationToken cancellationToken = default)
        => OnSlot(number, "repair env", waitSeconds, async (configuration, slot, operation, ct) =>
        {
            var error = await DockerEnvironmentProvisioner.RepairEnvAsync(configuration, number, operation.Log, ct).ConfigureAwait(false);
            return (error, error is null ? new { result = "env files rewritten", nextStep = "start_docker_service or start_docker_environment so the containers read them" } : null);
        }, cancellationToken);

    [McpServerTool(Name = "enable_docker_tunnel", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Turns on the Docker environment's tunnel: the environment claims a free tunnel from the pool (a tunnel belongs to a single environment), each of its repositories that exposes a port gets a public URL with anonymous access, the tunnel host starts and the env variables that depend on the pool are rewritten. If the pool is full, responds with the error. The containers read the env files on the service's next start.")]
    public static Task<string> EnableDockerTunnel(
        [Description("Docker environment number.")] int number,
        [Description(WaitDescription)] int waitSeconds = 90,
        CancellationToken cancellationToken = default)
        => OnSlot(number, "tunnel", waitSeconds, async (configuration, slot, operation, ct) =>
        {
            if (await EnvironmentTunnel.EnableAsync(configuration, number, operation.Log, ct).ConfigureAwait(false) is { } error)
            {
                return (error, null);
            }

            var urls = await DockerEnvironmentProvisioner.PoolUrlsAsync(number, ct).ConfigureAwait(false);
            return (null, new { tunnel = TunnelPool.Read().Where(a => a.Environment == number).Select(a => a.TunnelDescription).Distinct(), url = urls, nextStep = "start_docker_service so the containers read the new env files" });
        }, cancellationToken);

    [McpServerTool(Name = "disable_docker_tunnel", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Turns off the Docker environment's tunnel: releases all its pool ports (and their anonymous access), closes the host, returns the tunnel to the pool and rewrites the env variables that depended on the pool. When turned on again, the environment may get a different tunnel, with different URLs.")]
    public static Task<string> DisableDockerTunnel(
        [Description("Docker environment number.")] int number,
        [Description(WaitDescription)] int waitSeconds = 60,
        CancellationToken cancellationToken = default)
        => OnSlot(number, "tunnel off", waitSeconds, async (configuration, slot, operation, ct) =>
        {
            var had = EnvironmentTunnel.IsOn(number);
            var error = await EnvironmentTunnel.DisableAsync(configuration, number, operation.Log, ct).ConfigureAwait(false);
            return error is not null ? (error, null) : (null, new { result = had ? "tunnel turned off, ports released" : $"environment {number} did not have its tunnel on" });
        }, cancellationToken);

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
    [Description("Progress and result of an operation started by a Docker tool (create, start, migrations…) or by run_extra_command: status running / succeeded / failed, last lines of the log, error and result. Operations live in the MCP server process.")]
    public static async Task<string> OperationStatus(
        [Description("Operation id, from the response of the tool that started it.")] string operation,
        [Description("Log lines to return, from the end.")] int lines = 40,
        [Description("Seconds to wait for completion before responding (0-110).")] int waitSeconds = 0,
        CancellationToken cancellationToken = default)
    {
        if (Operations.Find(operation) is not { } found)
        {
            return EnvironmentTools.Serialize(new { error = $"unknown operation {operation}: operations live only in the process of the MCP server that started them" });
        }

        if (found.IsRunning && waitSeconds > 0)
        {
            try
            {
                await Task.WhenAny(found.Completion, Task.Delay(TimeSpan.FromSeconds(Math.Clamp(waitSeconds, 0, 110)), cancellationToken)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        return EnvironmentTools.Serialize(found.Snapshot(Math.Clamp(lines, 1, 400)));
    }

    private static async Task<string> Run(Func<Task<object>> body)
        => EnvironmentTools.Serialize(await body().ConfigureAwait(false));

    private static Task<string> OnSlot(
        int number,
        string kind,
        int waitSeconds,
        Func<AppConfiguration, DockerSlot, Operation, CancellationToken, Task<(string? Error, object? Result)>> work,
        CancellationToken cancellationToken)
        => Run(async () => Resolve(number, out var configuration, out var slot) is { } error
            ? new { error }
            : await Operations.RunAsync(kind, number, waitSeconds, (operation, ct) => work(configuration, slot, operation, ct), cancellationToken).ConfigureAwait(false));

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

    private static object Summary(DockerSlot slot) => new
    {
        environment = slot.Number,
        worktree = slot.Repositories.ToDictionary(r => r.Id, r => r.Path),
        services = Services(slot).Select(s => new { service = s.Service, address = s.IsDatabase ? $"127.0.0.1,{s.Port}" : $"http://localhost:{s.Port}/" }),
        nextStep = "docker_environment_status for the status of containers and migrations",
    };

    private static string NotePath(int number) => Path.Combine(ConfigurationFiles.DataFolder, "Note", $"ambiente-{number}.txt");

    private static string? ReadNote(int number)
        => File.Exists(NotePath(number)) && File.ReadAllText(NotePath(number)) is { Length: > 0 } text ? text : null;

    // The same file as EnvironmentNote: the app watches it and updates the column.
    private static void WriteNote(int number, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(NotePath(number))!);
        File.WriteAllText(NotePath(number), text);
    }
}

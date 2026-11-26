using System.Net.NetworkInformation;
using Soffritto.Core.Configuration;
using Soffritto.Core.Docker.Seed;
using Soffritto.Core.Env;

namespace Soffritto.Core.Docker;

/// <summary>What to create: the Docker environment number and the branch of each included repository.</summary>
/// <param name="Branches">Repository id → branch of its worktree; the keys are the included repositories.</param>
/// <param name="PoolRepositories">The ids of the repositories to assign a tunnel pool port to.</param>
/// <param name="RestartHost">
/// Restarts the host of the pool tunnel with that index if it is running (the consoles belong to the Desktop):
/// true if it restarted it. See <see cref="TunnelPool.AssignAsync"/>.
/// </param>
/// <param name="PoolOptional">
/// With the pool full (<see cref="TunnelPool.PoolFull"/>) the environment is created anyway, without
/// pool ports; false = creation stops with the error.
/// </param>
public sealed record DockerCreateRequest(
    int Number,
    IReadOnlyDictionary<string, string> Branches,
    IReadOnlyList<string>? PoolRepositories = null,
    Func<int, bool>? RestartHost = null,
    bool PoolOptional = false);

/// <summary>
/// The lifecycle of a Docker environment: creation (entry in config.json, worktrees from the source
/// clones, env, database from the seed, containers from the backend's compose file), starting and stopping the
/// containers, "Repair env" and destruction without losing code. Every action on the
/// database or start of the api goes through the guards of <see cref="DatabaseGuard"/>.
/// </summary>
public static class DockerEnvironmentProvisioner
{
    /// <summary>
    /// Creates the Docker environment: side-effect-free checks, registration of the entry (then
    /// <paramref name="onRegistered"/>), worktrees, Docker files, pool ports, env, guards,
    /// database from the seed with the branch's migrations, containers. It does not open the Compose
    /// Watch console: the caller does. After registration an error leaves the entry, which is removed by
    /// destroying the environment. Error or null.
    /// </summary>
    public static async Task<string?> CreateAsync(
        AppConfiguration c,
        DockerCreateRequest request,
        Action<EnvironmentDefinition> onRegistered,
        Action<string> log,
        CancellationToken ct = default)
    {
        var number = request.Number;

        // 1. Docker, number and configuration with the new entry in memory.
        if (await DockerCli.ServerVersionAsync(ct).ConfigureAwait(false) is null)
        {
            return $"{DockerStatusReader.Unreachable}: environment {number} not created";
        }

        if (number is < 1 or > AppConfiguration.MaxEnvironmentNumber)
        {
            return $"number {number} is out of range: from 1 to {AppConfiguration.MaxEnvironmentNumber}";
        }

        if (c.Environment(number) is not null)
        {
            return $"environment {number} already exists";
        }

        if (request.Branches.FirstOrDefault(p => c.Repository(p.Key) is null || string.IsNullOrWhiteSpace(p.Value)) is { Key: not null } wrong)
        {
            return c.Repository(wrong.Key) is null ? $"{wrong.Key} is not in the catalog" : $"the branch of {wrong.Key} is missing";
        }

        var working = ConfigurationFiles.Parse(ConfigurationFiles.Serialize(c));
        var ids = working.Repositories.Select(r => r.Id).Where(id => request.Branches.Keys.Contains(id, StringComparer.OrdinalIgnoreCase)).ToList();
        var definition = new EnvironmentDefinition { Number = number, Kind = EnvironmentKind.Docker, Repositories = ids };
        working.Environments.Add(definition);
        if (working.Validate() is { Count: > 0 } invalid)
        {
            return $"invalid configuration for environment {number}: {string.Join(" ", invalid)}";
        }

        var slot = DockerSlot.Of(working, definition);
        var entries = working.RepositoriesOf(definition);
        var backend = (BackendConfiguration)slot.Backend!.Repository;
        var seed = working.PrimarySeed()!.Seed!;

        // 2. Source clones, free branches, folders, seed, ports and project: all before the entry.
        foreach (var entry in entries)
        {
            var bare = entry.Repository.SourceClone!;
            if (!SourceCloneRepository.IsBare(bare))
            {
                return $"the source clone of {entry.Id} does not exist ({bare}): saving the setup creates it";
            }

            var worktrees = await SourceCloneRepository.WorktreeBranchesAsync(bare, ct).ConfigureAwait(false);
            if (worktrees is null)
            {
                return $"cannot read the worktrees of {bare}";
            }

            var branch = BranchOf(request, entry);
            if (worktrees.TryGetValue(branch, out var held))
            {
                return $"branch {branch} of {entry.Id} is already {HolderOf(c, held)}";
            }

            if (Directory.Exists(entry.Path))
            {
                return $"folder {entry.Path} already exists: remove it or choose another number";
            }
        }

        var (bak, seedError) = await SeedArchive.PrepareAsync(seed.Path, log, ct).ConfigureAwait(false);
        if (bak is null)
        {
            return seedError;
        }

        if (BusyPorts(slot) is { Count: > 0 } busy)
        {
            return $"ports already listening: {string.Join(", ", busy.Select(p => $"{p.Host} ({p.Service})"))}: environment {number} not created";
        }

        if (await ProjectResourcesAsync(slot.Project, ct).ConfigureAwait(false) is { } resources)
        {
            return resources.Length == 0
                ? $"project {slot.Project} already has containers or volumes: destroy it before recreating it"
                : resources;
        }

        // 3. Registration: before the worktrees, so a half-finished creation stays visible and destroyable.
        var registered = ConfigurationFiles.Update(
            file =>
            {
                if (file.Environment(number) is not null)
                {
                    return $"environment {number} is already in config.json";
                }

                file.Environments.Add(new EnvironmentDefinition { Number = number, Kind = EnvironmentKind.Docker, Repositories = ids.ToList() });
                return null;
            },
            ConfigurationFileFallback.Refuse);
        if (registered is not null)
        {
            return $"environment {number} not registered: {registered}";
        }

        log($"environment {number} registered in config.json");
        onRegistered(definition);
        var leftover = $"; the entry of environment {number} stays and is removed with Destroy environment";

        // 4. Worktrees.
        foreach (var entry in entries)
        {
            var add = await SourceCloneRepository.AddWorktreeAsync(entry.Repository.SourceClone!, entry.Path, BranchOf(request, entry), log: log, ct: ct).ConfigureAwait(false);
            if (!add.Success)
            {
                return $"worktree of {entry.Id} not created: {add.GitError()}{leftover}";
            }
        }

        // 4b. Docker files the branch lacks, copied from the fallback and excluded from git.
        if (await ComposeRunner.EnsureSlotFilesAsync(working, slot, log, ct).ConfigureAwait(false) is { } filesError)
        {
            return filesError + leftover;
        }

        // 5. Pool ports.
        foreach (var id in request.PoolRepositories ?? [])
        {
            if (entries.FirstOrDefault(e => SameId(e.Id, id)) is not { } entry)
            {
                return $"{id} is not in environment {number}: no pool port{leftover}";
            }

            var (assignment, poolError) = await TunnelPool.AssignAsync(working, entry, log, ct, request.RestartHost).ConfigureAwait(false);
            if (poolError == TunnelPool.PoolFull && request.PoolOptional)
            {
                log($"{poolError}: environment {number} starts without pool ports");
                break;
            }

            if (poolError is not null)
            {
                return poolError + leftover;
            }

            // Without a URL the {poolUrl} variables stay unresolvable (H-2) and the env does not write them.
            log(await TunnelPool.WaitUrlAsync(assignment!, ct).ConfigureAwait(false) is not null
                ? $"URL of port {assignment!.Port} of {entry.Id} published"
                : $"URL of port {assignment!.Port} of {entry.Id} not published yet: turn \"Tunnel\" off and on again in the environment header");
        }

        // 6. Env: the worktree file (only at creation and with Repair env) and the containers' env_file.
        if (await WriteEnvAsync(working, slot, local: true, log, ct).ConfigureAwait(false) is { } envError)
        {
            return envError + leftover;
        }

        // 7. The backend worktree points to the slot database.
        var probe = AppConfiguration.ToProbeOptions(backend);
        if (DatabaseGuard.Check(slot.BackendWorktree!, probe, slot.DatabasePort, slot.DatabaseName) is { } worktreeGuard)
        {
            return worktreeGuard + leftover;
        }

        log("backend worktree guard: slot database");

        // 8. slot.env, override, config --quiet and the guard on the api environment as resolved by compose.
        if (await PrepareComposeAsync(working, slot, log, ct).ConfigureAwait(false) is { } composeError)
        {
            return composeError + leftover;
        }

        // 9. Database from the seed, then the branch migrations the seed lacks.
        var db = await ComposeRunner.RunAsync(slot, $"up -d {slot.DatabaseService}", log: log, timeoutSeconds: 900, ct: ct).ConfigureAwait(false);
        if (!db.Success)
        {
            return $"slot database not started: {db.Tail(3)}{leftover}";
        }

        var provider = SeedProviders.For(seed.Kind);
        if (await provider.WaitReadyAsync(slot.HostConnectionString, ct: ct).ConfigureAwait(false) is { } readyError)
        {
            return readyError + leftover;
        }

        if (await ServiceContainerAsync(slot, slot.DatabaseService, ct).ConfigureAwait(false) is not { } dbContainer)
        {
            return $"container of service {slot.DatabaseService} not found{leftover}";
        }

        if (await provider.RestoreAsync(dbContainer, bak, slot.HostConnectionString, slot.DatabaseName, log, ct).ConfigureAwait(false) is { } restoreError)
        {
            return restoreError + leftover;
        }

        if (await ApplyMigrationsAsync(slot, backend, log, ct).ConfigureAwait(false) is { } migrationError)
        {
            return migrationError + leftover;
        }

        // 10. Api (and web) containers, with the build.
        var up = await ComposeRunner.RunAsync(slot, "up -d --build", slot.HasContainerizedFrontend, log, ct: ct).ConfigureAwait(false);
        if (!up.Success)
        {
            return $"docker compose up failed: {up.Tail(4)}{leftover}";
        }

        log($"environment {number} ready: api {slot.ApiHostUrl}{(slot.Frontend is { } web && slot.PortOf(web) is { } webPort ? $", web http://127.0.0.1:{webPort.Host}" : string.Empty)}, database 127.0.0.1,{slot.DatabasePort}");
        return null;
    }

    /// <summary>
    /// What prevents destroying the environment without losing code: modified or untracked
    /// files, an unfinished git operation, commits that are on no remote. Changes nothing.
    /// </summary>
    public static async Task<(bool Ok, IReadOnlyList<string> Problems)> CheckDestroyableAsync(AppConfiguration c, int number, CancellationToken ct = default)
    {
        if (c.Environment(number) is not { Kind: EnvironmentKind.Docker } environment)
        {
            return (false, [$"environment {number} is not a Docker environment"]);
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

                problems.Add($"{entry.Id}: {entry.Path} is not a git worktree, leaving it alone");
                continue;
            }

            var status = await ProcessRunner.RunAsync("git", "status --porcelain", entry.Path, 30, cancellationToken: ct).ConfigureAwait(false);
            if (!status.Success)
            {
                problems.Add($"{entry.Id}: cannot read git status ({status.GitError()})");
                continue;
            }

            var dirty = status.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).Where(l => l.Length > 3).Select(l => l[3..]).ToList();
            if (dirty.Count > 0)
            {
                problems.Add($"{entry.Id}: {dirty.Count} modified or untracked files in {entry.Path} ({string.Join(", ", dirty.Take(5))}{(dirty.Count > 5 ? ", ..." : string.Empty)})");
            }

            if (GitStatusReader.ReadOperationInProgress(entry.Path) is { } operation)
            {
                problems.Add($"{entry.Id}: {operation} in {entry.Path}");
            }

            var (commits, error) = await SourceCloneRepository.UnpushedCommitsAsync(entry.Path, ct).ConfigureAwait(false);
            if (error is not null)
            {
                problems.Add($"{entry.Id}: cannot check commits ({error})");
            }
            else if (commits.Count > 0)
            {
                problems.Add($"{entry.Id}: {commits.Count} commits not on any remote ({string.Join("; ", commits.Take(3))})");
            }
        }

        return (problems.Count == 0, problems);
    }

    /// <summary>
    /// Destroys the environment if <see cref="CheckDestroyableAsync"/> finds no problems: down -v of
    /// its own project only, its compose images removed, worktrees removed without --force, slot
    /// folder deleted, pool ports released, entry removed last (then
    /// <paramref name="onUnregistered"/>). The branches stay; the app deletes the note. Error or null.
    /// </summary>
    public static async Task<string?> DestroyAsync(AppConfiguration c, int number, Action<int> onUnregistered, Action<string> log, CancellationToken ct = default)
    {
        var (ok, problems) = await CheckDestroyableAsync(c, number, ct).ConfigureAwait(false);
        if (!ok)
        {
            return $"environment {number} not destroyed: {string.Join("; ", problems)}";
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
            log($"project {slot.Project} has no containers or volumes: no down needed");
        }
        else
        {
            if (await PrepareDownAsync(c, slot, log, ct).ConfigureAwait(false) is { } prepareError)
            {
                return $"down not prepared: {prepareError}";
            }

            var down = await ComposeRunner.RunAsync(slot, "down -v --remove-orphans", slot.HasContainerizedFrontend, log, 300, ct).ConfigureAwait(false);
            if (!down.Success)
            {
                return $"docker compose down failed: {down.Tail(3)}";
            }
        }

        // The api and web images serve only this environment: a new environment with the
        // same number rebuilds them. Otherwise they would keep taking up Docker's disk.
        var (images, imagesError) = await DockerMaintenance.ImagesOfProjectAsync(slot.Project, ct).ConfigureAwait(false);
        log(imagesError is null ? await DockerMaintenance.RemoveImagesAsync(images, ct).ConfigureAwait(false) : $"images not read, they stay: {imagesError}");

        foreach (var entry in slot.Repositories.Where(r => Directory.Exists(r.Path)))
        {
            // Leftover of an interrupted Destroy environment: git already removed files and registration, not the folder.
            if (!GitDirectory.Exists(entry.Path) && IsEmptyFolder(entry.Path))
            {
                try
                {
                    Directory.Delete(entry.Path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return $"empty worktree folder {entry.Path} not deleted: {ex.Message}; the entry of environment {number} stays";
                }

                await SourceCloneRepository.PruneWorktreesAsync(entry.Repository.SourceClone!, ct).ConfigureAwait(false);
                log($"empty worktree folder {entry.Path} deleted");
                continue;
            }

            var remove = await SourceCloneRepository.RemoveWorktreeAsync(entry.Repository.SourceClone!, entry.Path, log, ct).ConfigureAwait(false);
            if (!remove.Success)
            {
                return $"worktree {entry.Path} not removed: {remove.GitError()}; the entry of environment {number} stays";
            }

            log($"worktree {entry.Path} removed");
        }

        try
        {
            if (Directory.Exists(slot.ComposeFolder))
            {
                Directory.Delete(slot.ComposeFolder, recursive: true);
                log($"slot folder {slot.ComposeFolder} deleted");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"slot folder {slot.ComposeFolder} not deleted: {ex.Message}; the entry of environment {number} stays";
        }

        if (await TunnelPool.ReleaseAsync(c, number, log, ct).ConfigureAwait(false) is { } poolError)
        {
            log($"not all pool ports were released, they stay in the panel: {poolError}");
        }

        var unregistered = ConfigurationFiles.Update(
            file => file.Environments.RemoveAll(e => e.Number == number && e.Kind == EnvironmentKind.Docker) > 0 ? null : $"environment {number} is no longer in config.json",
            ConfigurationFileFallback.Refuse);
        if (unregistered is not null)
        {
            return $"entry of environment {number} not removed: {unregistered}";
        }

        onUnregistered(number);
        log($"environment {number} destroyed; the branches stay in the source clones");
        return null;
    }

    /// <summary>
    /// Starts the environment: containers' env_files rewritten (not the worktree, G-11), Docker files,
    /// slot.env, override, api container guard, then up -d --build. Error or null.
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
        return up.Success ? null : $"docker compose up failed: {up.Tail(4)}";
    }

    /// <summary>compose stop of the environment (timeout 120 s): the outcome and whether all containers are stopped.</summary>
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
            ? ($"containers stopped: {containers.Count(x => x.RunState == ContainerRunState.Stopped)} of {containers.Count}", allStopped)
            : ($"compose stop failed: {stop.Tail(2)}", false);
    }

    /// <summary>
    /// Starts a service: the api with its env_file, the container guard and the build; the web
    /// with its env_file and the build; the database. Error or null.
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
            return $"service {service} does not belong to environment {number}";
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
        return up.Success ? null : $"docker compose up {service} failed: {up.Tail(4)}";
    }

    /// <summary>compose stop of a service, with the files already in place since the last start. Error or null.</summary>
    public static async Task<string?> StopServiceAsync(AppConfiguration c, int number, string service, Action<string> log, CancellationToken ct = default)
    {
        var (slot, slotError) = await CommandSlotAsync(c, number, ct).ConfigureAwait(false);
        if (slot is null)
        {
            return slotError;
        }

        var stop = await ComposeRunner.RunAsync(slot, $"stop {service}", slot.HasContainerizedFrontend, log, 120, ct).ConfigureAwait(false);
        return stop.Success ? null : $"compose stop {service} failed: {stop.Tail(2)}";
    }

    /// <summary>The command line for the logs of one of the slot's services, for a console.</summary>
    public static string LogsCommand(DockerSlot slot, string service)
        => $"{ComposePrefix(slot)}{(slot.HasContainerizedFrontend ? $" --profile {ComposeRunner.WebProfile}" : string.Empty)} logs -f --tail 200 {service}";

    /// <summary>The command line of the web's Compose Watch, for the marked console (after up; without --no-up the build would start again).</summary>
    public static string WatchCommand(DockerSlot slot)
        => $"{ComposePrefix(slot)} --profile {ComposeRunner.WebProfile} watch --no-up {slot.WebService}";

    /// <summary>
    /// "Repair env": rewrites the worktrees' env files (a target that no longer exists starts again
    /// from the template constants) and the containers' env_files, then checks that the backend
    /// worktree points to the slot database. Error or null.
    /// </summary>
    public static async Task<string?> RepairEnvAsync(AppConfiguration c, int number, Action<string> log, CancellationToken ct = default)
    {
        if (c.Environment(number) is not { Kind: EnvironmentKind.Docker } environment)
        {
            return $"environment {number} is not a Docker environment";
        }

        var slot = DockerSlot.Of(c, environment);
        if (await WriteEnvAsync(c, slot, local: true, log, ct, targetIsNew: entry => entry.Repository.Env is not { Target.Length: > 0 } env || !File.Exists(Path.Combine(entry.Path, env.Target))).ConfigureAwait(false) is { } envError)
        {
            return envError;
        }

        var backend = (BackendConfiguration)slot.Backend!.Repository;
        return DatabaseGuard.Check(slot.BackendWorktree!, AppConfiguration.ToProbeOptions(backend), slot.DatabasePort, slot.DatabaseName);
    }

    /// <summary>The URLs of the pool ports assigned to the environment, for {poolUrl}: repository id → URL.</summary>
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

    // Env of all repositories: with local the worktree file (creation, Repair env), always
    // the containers' env_file. Only names and counts go to the log.
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
                log($"warning: {warning}");
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

    // slot.env, override, config --quiet, then the guard on the api environment as compose resolves
    // it (env_file and the product compose file's environment:). Without checkApi (web and db) the
    // guard is not needed: the api does not start.
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

        log("api container guard: slot database");
        return null;
    }

    // The slot of an existing Docker environment, if its project (when there is one) carries this
    // data folder's label: another executable's project is left alone.
    private static async Task<(DockerSlot? Slot, string? Error)> CommandSlotAsync(AppConfiguration c, int number, CancellationToken ct)
    {
        if (c.Environment(number) is not { Kind: EnvironmentKind.Docker } environment)
        {
            return (null, $"environment {number} is not a Docker environment");
        }

        var slot = DockerSlot.Of(c, environment);
        return await ComposeRunner.CheckLabelAsync(slot, ct).ConfigureAwait(false) is { } labelError ? (null, labelError) : (slot, null);
    }

    private static string ComposePrefix(DockerSlot slot)
        => $"docker compose -p {slot.Project} -f \"{ComposeRunner.ComposePath(slot)}\" -f \"{slot.OverridePath}\" --env-file \"{slot.SlotEnvPath}\"";

    // down reads the compose file: it uses the files already in the worktree and copies from the fallback only
    // the missing ones, so a fallback left without files does not block destruction. slot.env and override
    // are rewritten; a missing env_file (creation stopped early) becomes an empty file, which goes
    // away with the slot folder.
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
    /// Applies the migrations of the backend worktree's branch to the slot database, with the
    /// database guard before each context: all contexts, or only <paramref name="contextName"/>.
    /// Error or null.
    /// </summary>
    public static async Task<string?> ApplyMigrationsAsync(DockerSlot slot, BackendConfiguration backend, Action<string> log, CancellationToken ct, string? contextName = null)
    {
        if (slot.BackendWorktree is not { } worktree)
        {
            return $"environment {slot.Number} has no backend";
        }

        var probe = AppConfiguration.ToProbeOptions(backend);
        if (contextName is not null && !probe.Contexts.Any(x => string.Equals(x.Name, contextName, StringComparison.OrdinalIgnoreCase)))
        {
            return $"context {contextName} does not exist (contexts: {string.Join(", ", probe.Contexts.Select(x => x.Name))})";
        }
        foreach (var startup in probe.Contexts.Select(x => x.RelativeStartupProjectPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // On a freshly created worktree dotnet ef fails without project.assets.json (NETSDK1004).
            var restore = await ProcessRunner.RunAsync("dotnet", $"restore \"{Path.Combine(worktree, startup)}\"", worktree, 600, cancellationToken: ct).ConfigureAwait(false);
            if (!restore.Success)
            {
                return $"dotnet restore of {startup}: {restore.BuildError(3)}";
            }
        }

        foreach (var context in probe.Contexts.Where(x => contextName is null || string.Equals(x.Name, contextName, StringComparison.OrdinalIgnoreCase)))
        {
            if (DatabaseGuard.Check(worktree, probe, slot.DatabasePort, slot.DatabaseName) is { } guard)
            {
                return guard;
            }

            log($"branch migrations of {context.Name}");
            var update = await MigrationApplier.UpdateDatabaseAsync(worktree, context, null, log, ct).ConfigureAwait(false);
            if (!update.Success)
            {
                return $"{context.Name} migrations not applied: {await MigrationApplier.FailureReasonAsync(worktree, context, update, ct).ConfigureAwait(false)}";
            }
        }

        return null;
    }

    /// <summary>The container id of one of the slot's services, running or not; null if there is none.</summary>
    private static async Task<string?> ServiceContainerAsync(DockerSlot slot, string service, CancellationToken ct)
    {
        var ps = await ComposeRunner.RunAsync(slot, $"ps -a -q {service}", timeoutSeconds: 30, ct: ct).ConfigureAwait(false);
        return ps.Success && ps.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) is { } id ? id : null;
    }

    // Null if the project has neither containers nor volumes; empty string if it has them; an error if Docker does not respond.
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

    // Where the taken branch is: the Docker environment that has that worktree, or just the path.
    private static string HolderOf(AppConfiguration c, string worktree)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(worktree));
        var number = c.DockerEnvironments
            .FirstOrDefault(e => c.RepositoriesOf(e).Any(r => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(r.Path)), full, StringComparison.OrdinalIgnoreCase)))?
            .Number;
        return number is null ? $"in worktree {worktree}" : $"in environment {number} (worktree {worktree})";
    }

    private static bool SameId(string? a, string? b) => a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

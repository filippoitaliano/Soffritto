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
/// The global Docker actions: creating an environment (the “+” panel in the header), the seed
/// and the dev migrations it lacks, “Align seed”, Docker disk space and the log (the setup's
/// “Docker” tab), the tunnel pool (the setup's “Tunnels” tab).
/// </summary>
public sealed class DockerPanelViewModel : ObservableObject
{
    private const int MaxLogLines = 300;
    private const string BackendRequired = "a backend with a compose file is required: api, web and database start from the backend repository's compose";
    private static readonly TimeSpan MissingInterval = TimeSpan.FromMinutes(30);

    private readonly CancellationToken lifetime;
    private readonly List<string> logLines = [];
    private bool isWorking;
    private string seedText = "seed: not read";
    private string missingText = string.Empty;
    private string missingBadge = string.Empty;
    private string poolText = string.Empty;

    private readonly Action<EnvironmentDefinition> addColumn;
    private readonly Action<int>? refreshColumn;
    private readonly IUserPrompt prompt = new DialogPrompt();
    private string createNumber = string.Empty;
    private bool assignPool = true;
    private string diskText = "Docker disk space: not read";
    private string poolNotice = string.Empty;

    /// <param name="addColumn">Adds the column of the environment just registered, on the UI thread.</param>
    /// <param name="refreshColumn">Reads an environment's column again once creation is done, on the UI thread.</param>
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

    /// <summary>Creates one more pool tunnel, with the same label as the others, and adds it to config.json.</summary>
    public ICommand AddTunnelCommand { get; }

    /// <summary>Creates pool tunnels as long as the devtunnel account allows.</summary>
    public ICommand FillPoolCommand { get; }

    /// <summary>The outcome of the last pool action, below the buttons of the setup's “Tunnels” tab.</summary>
    public string PoolNotice { get => poolNotice; private set => SetProperty(ref poolNotice, value); }

    /// <summary>
    /// Called on the UI thread when a tunnel joins or leaves the pool: an open setup
    /// realigns its draft with the active configuration.
    /// </summary>
    public Action? PoolTunnelsChanged { get; set; }

    private void PoolLog(string line)
    {
        AppendLog(line);
        UiThread.Run(() => PoolNotice = line);
    }

    // One more tunnel: created on the account and written to config.json by TunnelPool.AddTunnelAsync,
    // then into the in-memory configuration. Returns the created tunnel's Description, or null with the error.
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
        PoolLog(description is null ? $"pool tunnel not created: {error}" : $"tunnel {description} created");
    });

    // A refusal from devtunnel create ends the filling: it is the account quota, not an error.
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
                    ? $"no tunnel created: the account allows no more ({error})"
                    : $"created {string.Join(", ", created)}; the account allows no more ({error})");
                return;
            }

            created.Add(description);
            PoolLog($"tunnel {description} created, trying the next one…");
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
            PoolLog($"tunnel {state.Description} not deleted: {error}");
            return;
        }

        UiThread.Run(() =>
        {
            ActiveConfiguration.Current.TunnelPool.Tunnels.RemoveAll(t => t.Description == state.Description);
            PoolTunnelsChanged?.Invoke();
        });
        PoolLog($"tunnel {state.Description} deleted");
    }, confirm: new Confirmation(
        "Delete the pool tunnel",
        "The tunnel is deleted from the devtunnel account and removed from the pool. Its URLs do not come back: a new tunnel gets different ones.",
        [new("Tunnel", [new(state.Description)])],
        "Delete"));

    // One pool action at a time, with the optional confirmation before and a re-read after.
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
            PoolLog($"pool action failed: {ex.Message}");
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
            AppendLog($"pool tunnel expiration not renewed: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Docker disk space: the virtual disk file and the internal usage by type.</summary>
    public string DiskText { get => diskText; private set => SetProperty(ref diskText, value); }

    public ICommand CleanCommand { get; }

    public ICommand CompactCommand { get; }

    /// <summary>The new environment's number, as the text box content; the first free one above the local environments is suggested.</summary>
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

    /// <summary>One row per catalog repository: checkbox and branch, enabled only with a source clone.</summary>
    public ObservableCollection<CreateRepositoryRowViewModel> CreateRows { get; } = [];

    /// <summary>“Assign pool ports”: one port for the backend and the frontend in the containers.</summary>
    public bool AssignPool { get => assignPool; set => SetProperty(ref assignPool, value); }

    /// <summary>Why “Create” is disabled; empty when creation is possible.</summary>
    public string CreateBlockReason => BlockReason() ?? string.Empty;

    public bool CanCreate => BlockReason() is null && IsIdle;

    public ICommand CreateCommand { get; }

    /// <summary>True during “Align seed” and environment creation: the panel's commands stay disabled.</summary>
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

    /// <summary>The number of dev migrations the seed lacks, for the “Setup” button badge; empty when zero.</summary>
    public string MissingBadge { get => missingBadge; private set => SetProperty(ref missingBadge, value); }

    public string Log => string.Join(Environment.NewLine, logLines);

    public string PoolText { get => poolText; private set => SetProperty(ref poolText, value); }

    public ObservableCollection<PoolTunnelRowViewModel> PoolTunnels { get; } = [];

    public ICommand AlignCommand { get; }

    /// <summary>Reads the seed (with fetch) and Docker disk space again: the button of the setup's “Docker” tab.</summary>
    public ICommand RefreshCommand { get; }

    /// <summary>Reads the suggested number and branches of the creation panel again.</summary>
    public ICommand RefreshCreateCommand { get; }

    /// <summary>
    /// Called on the UI thread when “Align seed” writes a new seed, with the backend id
    /// and the path: an open setup shows it in its draft.
    /// </summary>
    public Action<string, string>? SeedPathChanged { get; set; }

    /// <summary>The badge at startup and every 30 minutes, without fetch: fetching happens when the setup's “Docker” tab opens.</summary>
    public void Start()
    {
        _ = RefreshMissingPeriodicallyAsync();
        _ = RenewPoolAsync();
    }

    /// <summary>When the creation panel opens: suggested number and source clone branches.</summary>
    public async Task OpenCreateAsync()
    {
        if (!IsWorking)
        {
            await RefreshCreateRowsAsync().ConfigureAwait(false);
        }
    }

    /// <summary>When the setup's “Docker” tab opens: source clone fetch, missing migrations and Docker disk space.</summary>
    public async Task RefreshMaintenanceAsync()
    {
        await RefreshSeedAsync(fetch: true).ConfigureAwait(false);
        await RefreshDiskAsync().ConfigureAwait(false);
    }

    // After a creation: branches and number change, and so does Docker disk space.
    private async Task AfterCreateAsync()
    {
        await OpenCreateAsync().ConfigureAwait(false);
        await RefreshDiskAsync().ConfigureAwait(false);
    }

    private async Task RefreshDiskAsync()
    {
        try
        {
            var file = DockerDisk.FileSize is { } size ? $"Docker disk: {DockerDisk.Gigabytes(size)} on the PC" : "Docker Desktop disk not found";
            var (usage, error) = await DockerMaintenance.UsageAsync(lifetime).ConfigureAwait(false);
            var inside = error is not null
                ? $"internal usage not read: {error}"
                : $"inside: {string.Join(" · ", usage.Select(u => $"{TypeName(u.Type)} {u.Size} ({u.Reclaimable} reclaimable)"))}";
            UiThread.Run(() => DiskText = $"{file}{Environment.NewLine}{inside}");
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static string TypeName(string type) => type switch
    {
        "Images" => "images",
        "Containers" => "containers",
        "Local Volumes" => "volumes",
        "Build Cache" => "build cache",
        _ => type,
    };

    private async Task CleanAsync()
    {
        if (IsWorking)
        {
            return;
        }

        UiThread.Run(() => IsWorking = true);
        AppendLog("Docker cleanup: starting");
        try
        {
            var error = await Task.Run(() => DockerCleanup.RunAsync(AppendLog, lifetime), lifetime).ConfigureAwait(false);
            AppendLog(error is null ? "Docker cleanup: done" : $"Docker cleanup skipped: {error}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppendLog($"Docker cleanup failed: {ex.Message}");
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
            AppendLog($"Compaction postponed: {busy}");
            return;
        }

        var others = await DockerDisk.OtherRunningDistributionsAsync(lifetime).ConfigureAwait(false);
        var running = await DockerCli.RunAsync("ps -q", timeoutSeconds: 15, ct: lifetime).ConfigureAwait(false);
        var containers = running.Success ? running.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length : 0;
        var confirmed = await prompt.ConfirmAsync(
            new Confirmation(
                "Compact the Docker disk",
                "Gives back to the PC the space already freed inside Docker: run “Clean up now” first. Every running container stops, including those outside the environments, and comes back with Start.",
                [
                    new("Steps", [
                        new("Closes Docker Desktop", "1", $"{containers} running containers"),
                        new("Stops WSL (wsl --shutdown)", "2", others.Count > 0 ? $"also stops {string.Join(", ", others)}" : string.Empty),
                        new("Compacts docker_data.vhdx", "3", "administrator rights"),
                        new("Restarts Docker Desktop", "4"),
                    ]),
                ],
                "Compact"),
            lifetime).ConfigureAwait(false);
        if (!confirmed)
        {
            return;
        }

        UiThread.Run(() => IsWorking = true);
        AppendLog("Docker disk compaction: starting");
        try
        {
            var error = await Task.Run(
                () => DockerDisk.CompactAsync(
                    AppendLog,
                    desktop => DetachedLauncher.Start($"\"{desktop}\"", Path.GetDirectoryName(desktop)!, newConsole: false),
                    lifetime),
                lifetime).ConfigureAwait(false);
            AppendLog(error is null ? "Compaction: done; Docker Desktop is restarting" : $"Compaction failed: {error}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppendLog($"Compaction failed: {ex.Message}");
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
            return $"environment number from 1 to {AppConfiguration.MaxEnvironmentNumber}";
        }

        if (ActiveConfiguration.Current.Environment(number) is not null)
        {
            return $"environment {number} already exists";
        }

        var included = CreateRows.Where(r => r.Include).ToList();
        if (!included.Any(r => r.Type == RepositoryType.Backend && r.HasCompose))
        {
            return BackendRequired;
        }

        return included.FirstOrDefault(r => r.BranchName.Length == 0) is { } empty
            ? $"branch missing for {empty.Id}"
            : null;
    }

    // The rows of the “New Docker environment” form: the source clone's remote branches, with those
    // already checked out in a worktree marked, because git does not check out a branch in two worktrees.
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
            AppendLog($"source clone branches not read: {ex.Message}");
        }
    }

    // The first free number above the local environments; null if there is none.
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
                return $"in environment {environment.Number}";
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
                    $"Create environment {number} with new branches",
                    $"These branches do not exist on origin: they are created from {GitConventions.RemoteDevelopmentBranch} when the environment is created.",
                    [new("New branches", missing)],
                    "Create environment"),
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
        AppendLog($"New Docker environment {number}: starting, details go to “Operations” in its card");
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
    /// “Open in → New Docker environment” from the PR panel: creates an environment on the first free number
    /// with the <paramref name="targets"/> repositories (by name on the server) on their branches, plus
    /// backend and frontend without a target on <see cref="GitConventions.DevelopmentBranch"/>, with
    /// backend and frontend pool ports if the pool has a free tunnel. It first fetches the targets' source clones: a branch that origin does not
    /// have is rejected instead of being created from the development branch. Returns the outcome to show.
    /// </summary>
    public async Task<string> CreateForBranchesAsync(IReadOnlyList<(string Repository, string Branch)> targets)
    {
        const string subject = "New Docker environment";
        if (IsWorking)
        {
            return $"{subject}: a Docker operation is already in progress (creation, Align seed or tunnel pool)";
        }

        var configuration = ActiveConfiguration.Current;
        if (FirstFreeNumber(configuration) is not { } number)
        {
            return $"{subject}: no free number up to {AppConfiguration.MaxEnvironmentNumber}";
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
                    return $"{subject}: {repository.Id} has no source clone; saving the setup creates it";
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
        AppendLog($"New Docker environment {number} from PRs: starting, details go to “Operations” in its card");
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
            ? $"Environment {number} (Docker) created: {string.Join(", ", branches.Select(b => $"{b.Key} on {b.Value}"))}"
            : $"Environment {number} (Docker) not created: {error}";
    }

    // The creation outcome: a single line in the panel, the error in the environment card too.
    // If creation stops before the column exists, the error still stays in the panel.
    private void ReportCreation(int number, LogSectionViewModel operations, string? error)
    {
        if (error is null)
        {
            AppendLog($"Environment {number} created");
            return;
        }

        operations.Append($"environment {number} not created: {error}");
        AppendLog($"Environment {number} not created: {error}");
    }

    private async Task<string?> FetchBranchesAsync(IReadOnlyList<RepositoryConfiguration> repositories, IReadOnlyDictionary<string, string> branches, Action<string> log)
    {
        foreach (var repository in repositories)
        {
            var sourceClone = repository.SourceClone!;
            log($"fetching the source clone of {repository.Id}");
            if (await SourceCloneRepository.FetchAsync(sourceClone, log, lifetime).ConfigureAwait(false) is { } fetchError)
            {
                return $"fetch of the source clone of {repository.Id} failed: {fetchError}";
            }

            var remote = await SourceCloneRepository.RemoteBranchesAsync(sourceClone, lifetime).ConfigureAwait(false);
            if (remote is null)
            {
                return $"branches of {sourceClone} cannot be read";
            }

            if (!remote.Contains(branches[repository.Id], StringComparer.Ordinal))
            {
                return $"branch {branches[repository.Id]} of {repository.Id} does not exist on origin";
            }
        }

        return null;
    }

    // The actual creation, after the caller's checks: error or null.
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
                        // The entry is already in config.json: the in-memory configuration and the grid follow it
                        // right away, unless re-reading config.json has already brought it in.
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

    // With a new port, that tunnel's running host does not serve it until it restarts (P02).
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
            var seedLine = seed is null || seed.Path.Length == 0 ? "seed: not configured in the backend card" : $"seed: {seed.Path}";
            var (missing, error) = await SeedMigrations.MissingOnDevAsync(configuration, fetch, lifetime).ConfigureAwait(false);
            var total = missing?.Values.Sum(m => m.Count) ?? 0;
            var detail = missing is null
                ? error ?? "missing migrations not read"
                : total == 0
                    ? $"0 migrations missing compared to {GitConventions.RemoteDevelopmentBranch}"
                    : $"{total} migrations missing compared to {GitConventions.RemoteDevelopmentBranch}: {string.Join(", ", missing.Where(m => m.Value.Count > 0).Select(m => $"{m.Key} {m.Value.Count}"))}";
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
            UiThread.Run(() => MissingText = $"missing migrations not read: {ex.Message}");
        }
    }

    private async Task AlignAsync()
    {
        if (IsWorking)
        {
            return;
        }

        UiThread.Run(() => IsWorking = true);
        AppendLog("Align seed: starting");
        try
        {
            var error = await Task.Run(
                () => SeedAligner.AlignAsync(ActiveConfiguration.Current, OnNewSeedPath, AppendLog, lifetime),
                lifetime).ConfigureAwait(false);
            AppendLog(error is null ? "Align seed: done" : $"Align seed failed: {error}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppendLog($"Align seed failed: {ex.Message}");
        }
        finally
        {
            UiThread.Run(() => IsWorking = false);
        }

        await RefreshSeedAsync(fetch: true).ConfigureAwait(false);
    }

    // The aligner has already written seed.path to config.json: the in-memory configuration follows it,
    // so environments created afterwards restore the new seed without a restart.
    private void OnNewSeedPath(string path) => UiThread.Run(() =>
    {
        if (ActiveConfiguration.Current.PrimarySeed() is { Seed: { } seed } backend)
        {
            seed.Path = path;
            SeedPathChanged?.Invoke(backend.Id, path);
        }
    });

    /// <summary>Reads the pool tunnels, ports and assignments (devtunnel show) and the host status again.</summary>
    public async Task RefreshPoolAsync()
    {
        try
        {
            // devtunnel list and show take a few seconds: without a list already read, the tab says so.
            UiThread.Run(() =>
            {
                if (PoolTunnels.Count == 0)
                {
                    PoolText = "Reading the pool from devtunnel…";
                }
            });

            var configuration = ActiveConfiguration.Current;
            if (configuration.TunnelPool.Tunnels.Count == 0)
            {
                UiThread.Run(() =>
                {
                    PoolTunnels.Clear();
                    PoolText = "No tunnels in the pool: create them with the buttons below.";
                });
                return;
            }

            var states = await TunnelPool.StatusAsync(configuration, lifetime).ConfigureAwait(false);
            var scan = await Task.Run(() => ProcessTable.Read([]), lifetime).ConfigureAwait(false);
            var rows = new List<PoolTunnelRowViewModel>();
            foreach (var state in states)
            {
                // The ports of an existing environment are released by turning off its tunnel: only those
                // of an environment that no longer exists remain here.
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
                    ? $"{rows.Count} tunnels, all free"
                    : $"{states.Count(s => s.Assignments.Count > 0)} of {rows.Count} tunnels in use: {(owners.Count == 1 ? "environment" : "environments")} {string.Join(", ", owners)}";
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            UiThread.Run(() => PoolText = $"pool not read: {ex.Message}");
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
        AppendLog($"Releasing port {assignment.Port} of {assignment.Repository} in environment {assignment.Environment}");
        try
        {
            var error = await TunnelPool.ReleaseAsync(configuration, assignment.Environment, AppendLog, lifetime, assignment.Repository).ConfigureAwait(false);
            if (error is not null)
            {
                AppendLog($"release failed: {error}");
            }
            else
            {
                if (PoolHosts.StopIfUnused(assignment.TunnelDescription) is { } stopped)
                {
                    AppendLog(stopped);
                }

                // An environment that no longer exists has no env files to rewrite.
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
            AppendLog($"release failed: {ex.Message}");
        }

        await RefreshPoolAsync().ConfigureAwait(false);
    }
}

/// <summary>A pool tunnel in the panel: which environment it belongs to, the host status and any orphaned ports.</summary>
/// <param name="inPool">False for assignments on a Description removed from the pool.</param>
/// <param name="orphans">The ports assigned to an environment that no longer exists.</param>
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
            0 => "free",
            1 => $"environment {owners[0]}",
            _ => $"environments {string.Join(", ", owners)}",
        };
    }

    /// <summary>Only a tunnel with no assigned ports can be deleted: turn off the environment's tunnel first.</summary>
    public bool CanDelete { get; } = state.Assignments.Count == 0;

    public string DeleteToolTip { get; } = state.Assignments.Count == 0
        ? $"Deletes tunnel {state.Description} from the devtunnel account and removes it from the pool"
        : $"{state.Description} is in use: first turn off “Tunnel” in the environment header";

    public ICommand DeleteCommand { get; } = deleteCommand;

    public string Title { get; } = $"{state.Description} · {OwnerOf(state)}{(inPool ? string.Empty : " · outside the pool")}";

    /// <summary>Only what does not add up: a read error or differences between devtunnel and tunnel-pool.json.</summary>
    public string Detail { get; } = state.Error ?? string.Join("; ", state.Differences);

    public string DetailVisibility => Detail.Length > 0 ? "Visible" : "Collapsed";

    public string HostColor { get; } = ConsoleServices.ColorOf(hostState);

    public string HostText { get; } = $"{state.TunnelId ?? "tunnel not found"} · {state.Ports.Count} of {TunnelPool.MaxPortsPerTunnel} ports · host: {ConsoleServices.Describe(hostState)}";

    public IReadOnlyList<PoolAssignmentRowViewModel> Assignments { get; } = orphans;
}

/// <summary>A pool port still held by an environment that no longer exists: release it from here.</summary>
public sealed class PoolAssignmentRowViewModel(PoolAssignment assignment, ICommand releaseCommand)
{
    public string Text { get; } = $"port {assignment.Port} · environment {assignment.Environment} (no longer exists) · {assignment.Repository}";

    public string Url { get; } = string.Empty;

    public string ReleaseToolTip { get; } = $"Releases port {assignment.Port} of {assignment.Repository} in environment {assignment.Environment}";

    public ICommand ReleaseCommand { get; } = releaseCommand;
}

/// <summary>A repository in the “New Docker environment” form: included or not, and on which branch.</summary>
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
            ? $"{Id}: worktree from the source clone {repository.SourceClone}"
            : $"{Id}: without a source clone in the setup it cannot join a Docker environment";
    }

    public string Id { get; }

    public RepositoryType Type { get; }

    public bool IsEnabled { get; }

    public bool HasCompose { get; }

    public string ToolTip { get; }

    /// <summary>The remote branches; those already in a worktree carry the suffix “(in environment M)”.</summary>
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

    /// <summary>The drop-down text, possibly typed by hand (a new branch is created from origin/dev).</summary>
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

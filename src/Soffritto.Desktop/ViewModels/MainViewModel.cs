using System.Collections.ObjectModel;
using System.Windows.Input;
using Soffritto.Desktop.Infrastructure;
using Soffritto.Presentation;

namespace Soffritto.Desktop.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    // Reads outlive the command that requested them: they stop only with the window.
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<int, EnvironmentRuntime> runtimes = [];
    private readonly IUserPrompt prompt = new DialogPrompt();
    private readonly IClipboard clipboard = new ClipboardText();
    private readonly PlaywrightPreviewController playwrightPreview;

    // Wakes the process scan right after a start or a stop, without waiting for the next round.
    private readonly SemaphoreSlim scanRequested = new(0);
    private bool isLoading;
    private int activeOperations;

    public MainViewModel(IReadOnlyList<EnvironmentLayout> layouts)
    {
        WindowTitle = Soffritto.Core.Configuration.ConfigurationFiles.IsDataFolderOverridden
            ? $"{App.ProductName} · data: {Soffritto.Core.Configuration.ConfigurationFiles.DataFolder}"
            : App.ProductName;

        // Observable: Docker environments are added and removed without a restart.
        Environments = new ObservableCollection<EnvironmentViewModel>(layouts
            .Select(layout => new EnvironmentViewModel(layout, BeginOperation, EndOperation)));

        foreach (var environment in Environments)
        {
            Attach(environment);
        }

        Docker = new DockerPanelViewModel(
            lifetime.Token,
            definition => AddEnvironment(EnvironmentDiscovery.LayoutOf(ActiveConfiguration.Current, definition)),
            number =>
            {
                if (runtimes.TryGetValue(number, out var runtime))
                {
                    runtime.Controller.Refresh(full: true);
                }
            });
        playwrightPreview = new PlaywrightPreviewController(Environments.SelectMany(e => e.Previews).ToList());
        PullRequests = new PullRequestsViewModel(Environments, CheckoutAsync, createDocker: Docker.CreateForBranchesAsync);
        Notifications = new PullRequestNotifications(
            System.IO.Path.Combine(Soffritto.Core.Configuration.ConfigurationFiles.DataFolder, "notifiche.json"));
        PullRequests.AlertsRead += Notifications.Update;
        var settings = AppSettings.Current;
        Meeting = new StoryPointsMeetingViewModel(
            AzureDevOpsAccess.ReadStoriesAsync,
            AzureDevOpsAccess.SaveStoryPointsAsync,
            Browser.OpenCommand,
            AzureDevOpsAccess.WorkItemUrl,
            settings.MeetingInitials,
            !ThemeManager.IsDark,
            (initials, _) => settings.MeetingInitials = initials);

        RefreshAllCommand = new AsyncCommand(() =>
        {
            RefreshAll();
            return Task.CompletedTask;
        });

        foreach (var environment in Environments)
        {
            foreach (var card in environment.Repos)
            {
                card.RefreshExistence();
            }
        }
    }

    /// <summary>Banner in the header: empty on real environments, only the Ctrl+D demo sets it.</summary>
    public string DemoBanner => string.Empty;

    /// <summary>The header's “+”, which the demo hides.</summary>
    public string NewDockerVisibility => "Visible";

    public string WindowTitle { get; }

    /// <summary>With no environments (first launch, configuration to fix) the columns make way for the invitation to the setup.</summary>
    public string EmptyVisibility => Environments.Count == 0 ? "Visible" : "Collapsed";

    /// <summary>The app version (the csproj Version) for the footer: "v1.0.0".</summary>
    public string AppVersion { get; } =
        $"v{System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "?"}";

    private string updateNotice = string.Empty;

    /// <summary>Next to the version in the footer: a downloaded update that installs on close.</summary>
    public string UpdateNotice { get => updateNotice; set => SetProperty(ref updateNotice, value); }

    public ObservableCollection<EnvironmentViewModel> Environments { get; }

    public PullRequestsViewModel PullRequests { get; }

    /// <summary>The global Docker actions: environment creation (the “+”), seed, disk space, log and tunnel pool (setup).</summary>
    public DockerPanelViewModel Docker { get; }

    public PullRequestNotifications Notifications { get; }

    public StoryPointsMeetingViewModel Meeting { get; }

    /// <summary>
    /// Fixed width of a column (DIP), between the old limits of 480 and 720: cards always have
    /// the same shape, and with many columns a horizontal scrollbar appears.
    /// </summary>
    public const double ColumnWidth = 550;

    /// <summary>Minimum grid width: one <see cref="ColumnWidth"/> per open column; hidden ones do not count.</summary>
    public double ColumnsMinWidth => MinWidthOf(Environments);

    public static double MinWidthOf(IEnumerable<EnvironmentViewModel> environments)
        => environments.Count(e => !e.IsCollapsed) * ColumnWidth;

    /// <summary>True while at least one read or action is running in an environment: it animates the top bar.</summary>
    public bool IsLoading { get => isLoading; private set => SetProperty(ref isLoading, value); }

    public ICommand RefreshAllCommand { get; }

    public void Start()
    {
        RefreshAll();
        _ = ScanProcessesAsync(lifetime.Token);
        _ = RefreshPeriodicallyAsync(lifetime.Token);
        _ = playwrightPreview.RunAsync(lifetime.Token);
        _ = SystemLoad.Current.RunAsync(lifetime.Token);
        Docker.Start();
        WatchConfiguration();
    }

    public void Shutdown()
    {
        // Before stopping everything: with an action running in the app, the cleanup is skipped.
        if (AppSettings.Current.DockerCleanupOnExit && !AppSettings.Current.IsDemo && activeOperations == 0 && !Docker.IsWorking)
        {
            try
            {
                DockerCleanup.StartDetached(TimeSpan.FromSeconds(8));
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or System.IO.IOException or UnauthorizedAccessException)
            {
            }
        }

        lifetime.Cancel();
        configurationWatcher?.Dispose();

        foreach (var environment in Environments)
        {
            environment.CloseNote();
        }
    }

    /// <summary>Moves environment <paramref name="number"/> to the given branches: see <see cref="EnvironmentController.CheckoutAsync"/>.</summary>
    public Task<string> CheckoutAsync(int number, IReadOnlyList<(string Repository, string Branch)> targets)
        => runtimes.TryGetValue(number, out var runtime)
            ? runtime.Controller.CheckoutAsync(targets)
            : Task.FromResult($"Environment {number} no longer exists");

    /// <summary>
    /// A new column (a freshly created Docker environment), without a restart: controller, tools,
    /// PR panel and previews see it right away. Must be called on the UI thread.
    /// </summary>
    public void AddEnvironment(EnvironmentLayout layout)
    {
        if (runtimes.ContainsKey(layout.Number))
        {
            return;
        }

        var environment = new EnvironmentViewModel(layout, BeginOperation, EndOperation);
        Attach(environment);

        var index = 0;
        while (index < Environments.Count && Environments[index].Number < environment.Number)
        {
            index++;
        }

        Environments.Insert(index, environment);
        PullRequests.Track(environment);
        playwrightPreview.Replace(Environments.SelectMany(e => e.Previews).ToList());
        OnPropertyChanged(nameof(ColumnsMinWidth));
        OnPropertyChanged(nameof(EmptyVisibility));

        foreach (var card in environment.Repos)
        {
            card.RefreshExistence();
        }

        runtimes[environment.Number].Controller.Refresh(full: true);
        RequestScan();
    }

    /// <summary>
    /// Removes the column of a (destroyed) environment: its reads stop with its token.
    /// Must be called on the UI thread.
    /// </summary>
    public void RemoveEnvironment(int number)
    {
        if (!runtimes.Remove(number, out var runtime))
        {
            return;
        }

        runtime.Lifetime.Cancel();
        var environment = Environments.First(e => e.Number == number);
        environment.CloseNote();
        PullRequests.Untrack(environment);
        Environments.Remove(environment);
        playwrightPreview.Replace(Environments.SelectMany(e => e.Previews).ToList());
        OnPropertyChanged(nameof(ColumnsMinWidth));
        OnPropertyChanged(nameof(EmptyVisibility));
    }

    private System.IO.FileSystemWatcher? configurationWatcher;
    private CancellationTokenSource? configurationDebounce;

    // Docker environments are also created and destroyed from outside (MCP server, CLI): config.json
    // is watched and the Docker columns follow the file. Local environments change only through the
    // setup, which restarts the app.
    private void WatchConfiguration()
    {
        if (Soffritto.Core.Configuration.ConfigurationFiles.DataFolderProblem is not null)
        {
            return;
        }

        var path = Soffritto.Core.Configuration.ConfigurationFiles.ConfigPath;
        configurationWatcher = new System.IO.FileSystemWatcher(System.IO.Path.GetDirectoryName(path)!, System.IO.Path.GetFileName(path))
        {
            NotifyFilter = System.IO.NotifyFilters.LastWrite | System.IO.NotifyFilters.FileName | System.IO.NotifyFilters.Size,
        };
        configurationWatcher.Changed += (_, _) => ConfigurationChanged();
        configurationWatcher.Created += (_, _) => ConfigurationChanged();
        configurationWatcher.Renamed += (_, _) => ConfigurationChanged();
        configurationWatcher.EnableRaisingEvents = true;
    }

    // The write is tmp + move: read it again once, shortly after the last event.
    private void ConfigurationChanged()
    {
        var debounce = new CancellationTokenSource();
        Interlocked.Exchange(ref configurationDebounce, debounce)?.Cancel();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(400, debounce.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (Soffritto.Core.Configuration.ConfigurationFiles.TryLoad(out _) is { } file && !lifetime.IsCancellationRequested)
            {
                UiThread.Run(() => FollowDockerEnvironments(file));
            }
        });
    }

    private void FollowDockerEnvironments(Soffritto.Core.Configuration.AppConfiguration file)
    {
        var current = ActiveConfiguration.Current;
        var docker = Soffritto.Core.Configuration.EnvironmentKind.Docker;
        var removed = current.Environments
            .Where(e => e.Kind == docker && file.Environment(e.Number) is null)
            .Select(e => e.Number)
            .ToList();
        foreach (var number in removed)
        {
            current.Environments.RemoveAll(e => e.Number == number);
            RemoveEnvironment(number);
        }

        foreach (var added in file.Environments.Where(e => e.Kind == docker && current.Environment(e.Number) is null).ToList())
        {
            current.Environments.Add(added);
            AddEnvironment(EnvironmentDiscovery.LayoutOf(current, added));
        }
    }

    // Controller and tools of a column, with a token of its own linked to the window's:
    // a removed column stops its reads without touching the others.
    private void Attach(EnvironmentViewModel environment)
    {
        var columnLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var controller = new EnvironmentController(environment, prompt, clipboard, columnLifetime.Token);
        var devTools = new DevToolsController(environment, RequestScan, controller.RefreshTunnel, columnLifetime.Token);
        controller.StopServices = devTools.StopEverythingAsync;
        controller.DatabaseOffline = environment.Docker is not null
            ? () => devTools.DatabaseOffline
            : () => DockerOwningDatabaseOf(environment)?.Tools.DatabaseOffline;
        devTools.RefreshColumn = () =>
        {
            controller.Refresh(full: false);
            foreach (var follower in FollowersOf(environment))
            {
                follower.Controller.Refresh(full: false);
            }
        };
        environment.CollapsedChanged += () => OnPropertyChanged(nameof(ColumnsMinWidth));
        runtimes[environment.Number] = new EnvironmentRuntime(controller, devTools, columnLifetime);

        if (environment.Docker is { } docker)
        {
            docker.DestroyCommand = new AsyncCommand(() => DestroyDockerAsync(environment.Number));
            docker.RepairEnvCommand = new AsyncCommand(() => RepairEnvAsync(environment, controller, columnLifetime.Token));
        }
    }

    // A local environment whose backend points to the database in a Docker slot's container
    // (127.0.0.1,<slot db port>) follows that environment's phase: a database that is off or
    // starting counts as "not read", and it is read again when the database comes back.
    private EnvironmentRuntime? DockerOwningDatabaseOf(EnvironmentViewModel local)
    {
        if (LocalDatabasePort(local) is not { } port)
        {
            return null;
        }

        var owner = Environments.FirstOrDefault(e => e.Docker?.Slot?.DatabasePort == port);
        return owner is not null && runtimes.TryGetValue(owner.Number, out var runtime) ? runtime : null;
    }

    private List<EnvironmentRuntime> FollowersOf(EnvironmentViewModel docker)
    {
        if (docker.Docker?.Slot?.DatabasePort is not { } port)
        {
            return [];
        }

        return Environments
            .Where(e => e.Docker is null && LocalDatabasePort(e) == port && runtimes.ContainsKey(e.Number))
            .Select(e => runtimes[e.Number])
            .ToList();
    }

    private static int? LocalDatabasePort(EnvironmentViewModel local)
        => local.Repos.FirstOrDefault(r => r.Type == Soffritto.Core.Configuration.RepositoryType.Backend && r.IsPrimary)?.Location is { Settings: Soffritto.Core.Configuration.BackendConfiguration backend } location
            ? Soffritto.Core.Docker.DatabaseGuard.LocalPortOf(location.Path, Soffritto.Core.Configuration.AppConfiguration.ToProbeOptions(backend))
            : null;

    /// <summary>
    /// “Destroy” for a Docker environment: a check for no changes (code that would be lost),
    /// confirmation with the summary, the Compose Watch console and programs open on its folders
    /// closed, then the destruction. The column
    /// disappears when the entry leaves config.json; the details go to the column's “Operations”, the outcome of the destruction to the log of the setup's “Docker” tab.
    /// </summary>
    public async Task DestroyDockerAsync(int number)
    {
        if (Environments.FirstOrDefault(e => e.Number == number) is not { Docker: { } docker } environment)
        {
            return;
        }

        if (!environment.IsNotBusy)
        {
            environment.ActivityLine = $"Environment {number} not destroyed: an action is in progress";
            return;
        }

        if (docker.Slot is not { } slot)
        {
            environment.ActivityLine = $"Environment {number} not destroyed: {docker.SlotError ?? "slot cannot be computed"}";
            return;
        }

        void Log(string line) => environment.Report($"destroy: {line}");

        environment.BeginOperation();
        try
        {
            environment.Report("destroy: checking that no worktree has code to lose…");
            var configuration = ActiveConfiguration.Current;
            var (ok, problems) = await Task.Run(
                () => Soffritto.Core.Docker.DockerEnvironmentProvisioner.CheckDestroyableAsync(configuration, number, lifetime.Token),
                lifetime.Token).ConfigureAwait(false);
            if (!ok)
            {
                environment.Report($"Environment {number} not destroyed: {string.Join("; ", problems)}");
                return;
            }

            // A program closed by hand can stay alive without windows (an editor extension keeping
            // it up): instead of blocking, the confirmation lists them and Destroy terminates them.
            var holders = await Task.Run(() => DockerFolderHolders.Find(ProcessTable.ReadAll(), slot), lifetime.Token).ConfigureAwait(false);
            var removed = new List<ConfirmationRow>
            {
                new($"project {slot.Project}", "compose", "containers and database volume"),
                new("api and web images", "images"),
            };
            removed.AddRange(slot.Repositories.Select(r => new ConfirmationRow(r.Path, "worktree", "without --force")));
            removed.Add(new(slot.ComposeFolder, "slot", "with the env_files"));
            removed.Add(new("environment ports released", "pool"));
            removed.Add(new("entry removed from config.json, column closed", "config"));

            var confirmed = await prompt.ConfirmAsync(
                new Confirmation(
                    $"Destroy environment {number}",
                    $"Docker environment {number} is removed entirely; the database is regenerated from the seed with a new environment. The branches in the source clones and the environment note are kept.",
                    [
                        new("Programs to close, without saving", holders.Select(h => new ConfirmationRow($"{h.Name} on {h.Folder}", h.ProcessId.ToString(), "with children")).ToList()),
                        new("What gets removed", removed),
                    ],
                    "Destroy"),
                lifetime.Token).ConfigureAwait(false);
            if (!confirmed)
            {
                environment.Report($"Environment {number} not destroyed: cancelled");
                return;
            }

            // Before down: with the watch running, a rebuild rule would restart the web halfway through, and
            // an open watch or log console would block the deletion of the slot folder.
            Log(await Task.Run(() => DockerWatchConsole.StopAll(ProcessTable.Read([]), slot), lifetime.Token).ConfigureAwait(false));
            if (holders.Count > 0)
            {
                Log(await Task.Run(() => DockerFolderHolders.Stop(ProcessTable.ReadAll(), slot, holders), lifetime.Token).ConfigureAwait(false));
            }

            var error = await Task.Run(
                () => Soffritto.Core.Docker.DockerEnvironmentProvisioner.DestroyAsync(
                    configuration,
                    number,
                    removed => UiThread.Run(() =>
                    {
                        ActiveConfiguration.Current.Environments.RemoveAll(e => e.Number == removed && e.Kind == Soffritto.Core.Configuration.EnvironmentKind.Docker);
                        RemoveEnvironment(removed);
                    }),
                    Log,
                    lifetime.Token),
                lifetime.Token).ConfigureAwait(false);
            if (error is not null)
            {
                environment.Report($"Environment {number} not destroyed: {error}");
            }
            else
            {
                // The column is gone: the outcome stays in the setup's Docker log, the operations log is discarded.
                Docker.AppendLog($"Environment {number} destroyed; the branches in the source clones are kept");
                UiThread.Run(() => OperationLogs.Remove(number));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            environment.Report($"Environment {number} not destroyed: {ex.Message}");
        }
        finally
        {
            environment.EndOperation();
        }
    }

    // "Repair env": rewrites the env files of the worktrees and containers, then reads the column again.
    // An error stays in the activity line: the reread would cover it with "ready".
    private static async Task RepairEnvAsync(EnvironmentViewModel environment, EnvironmentController controller, CancellationToken cancellationToken)
    {
        if (!environment.IsNotBusy)
        {
            environment.ActivityLine = "Repair env not started: an action is in progress";
            return;
        }

        environment.BeginOperation();
        try
        {
            environment.Report("repair env: rewriting the env files of the worktrees and containers…");
            var configuration = ActiveConfiguration.Current;
            var error = await Task.Run(
                () => Soffritto.Core.Docker.DockerEnvironmentProvisioner.RepairEnvAsync(
                    configuration,
                    environment.Number,
                    line => environment.Report($"repair env: {line}"),
                    cancellationToken),
                cancellationToken).ConfigureAwait(false);
            if (error is not null)
            {
                environment.Report($"Repair env failed: {error}");
                return;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            environment.Report($"Repair env failed: {ex.Message}");
            return;
        }
        finally
        {
            environment.EndOperation();
        }

        controller.Refresh(full: true);
    }

    private sealed record EnvironmentRuntime(EnvironmentController Controller, DevToolsController Tools, CancellationTokenSource Lifetime);

    /// <summary>The write actions in progress, one line per busy environment.</summary>
    public bool HasWriteInProgress(out string description)
    {
        var busy = Environments
            .Where(e => !e.IsNotBusy)
            .Select(e => $"Environment {e.Number}: {e.ActivityLine}")
            .ToList();

        description = string.Join("; ", busy);
        return busy.Count > 0;
    }

    // Environments with an action in progress are not read again: the session bails out on its own
    // while the gate is busy, and the action rereads when it finishes. PRs are read again even with
    // the panel closed: notifications must notice what is new.
    private void RefreshAll()
    {
        foreach (var runtime in runtimes.Values)
        {
            runtime.Controller.Refresh(full: true);
        }

        _ = PullRequests.RefreshAsync();
    }

    // Only the app rereads on its own: the VSIX stays on manual refresh.
    private async Task RefreshPeriodicallyAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            UiThread.Run(RefreshAll);
        }
    }

    private void RequestScan() => scanRequested.Release();

    // The state of Kestrel, dev server and app also changes outside the app (a console closed by
    // hand, a debug session started from Visual Studio): the toolbar catches up every few seconds.
    // It is a local read of the processes; it does not touch git, the database or the network.
    private async Task ScanProcessesAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var apiProcesses = ActiveConfiguration.Current.Repositories.OfType<Soffritto.Core.Configuration.BackendConfiguration>().Select(b => b.ApiProcess).ToList();
                var scan = await Task.Run(() => ProcessTable.Read(apiProcesses), cancellationToken).ConfigureAwait(false);

                // The containers of Docker environments: a docker ps read filtered on the data folder
                // label, only if there is at least one Docker environment.
                var docker = ActiveConfiguration.Current.DockerEnvironments.Any()
                    ? await Soffritto.Core.Docker.DockerStatusReader.ReadAsync(cancellationToken).ConfigureAwait(false)
                    : null;

                // Readiness of db, api and web of the running containers: login and short HTTP requests,
                // in parallel for all columns, before recomputing the cards on the UI thread.
                if (docker is { IsReachable: true })
                {
                    var tools = new List<DevToolsController>();
                    UiThread.Run(() => tools.AddRange(runtimes.Values.Select(r => r.Tools)));
                    await Task.WhenAll(tools.Select(t => t.ProbeAsync(docker, cancellationToken))).ConfigureAwait(false);
                }

                UiThread.Run(() =>
                {
                    foreach (var runtime in runtimes.Values)
                    {
                        runtime.Tools.Apply(scan, docker);
                    }
                });
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // WMI unavailable for one round: the bar stays as it was and the next round tries again.
            }

            try
            {
                await scanRequested.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void BeginOperation()
    {
        if (Interlocked.Increment(ref activeOperations) == 1)
        {
            IsLoading = true;
        }
    }

    private void EndOperation()
    {
        if (Interlocked.Decrement(ref activeOperations) == 0)
        {
            IsLoading = false;
        }
    }
}

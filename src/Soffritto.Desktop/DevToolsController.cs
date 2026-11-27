using Soffritto.Core;
using Soffritto.Core.Configuration;
using Soffritto.Desktop.Infrastructure;
using Soffritto.Desktop.ViewModels;
using Soffritto.Presentation;

namespace Soffritto.Desktop;

/// <summary>
/// The tools of a column's cards, by repository type: the backend opens Visual Studio and starts
/// Kestrel (through <see cref="BackendTools"/>, shared with the VSIX), the frontend runs the dev
/// server, the app runs the app and Metro, a generic repository gets VS Code only; every card with a
/// dev tunnel is shared through a pool port, served by its tunnel's host. Everything starts in consoles detached from the exe, and the state is derived from the processes (see
/// <see cref="ConsoleServices"/>). Markers use the card key: the environment number for the
/// primary repository, as always, "N-id" for the others.
/// </summary>
public sealed class DevToolsController
{
    // Segoe Fluent Icons glyph of the globe with a cursor: the dev server on localhost.
    private const string LocalhostGlyph = "";

    private readonly EnvironmentViewModel vm;
    private readonly Action requestScan;
    private readonly Action<RepoCardViewModel> refreshTunnel;
    private readonly List<CardTools> cards = [];
    private IReadOnlyList<ProcessRow> lastScan = [];
    private Soffritto.Core.Docker.DockerStatus? lastDocker;
    private int stackRunning;
    private readonly DockerReadiness? readiness;
    private volatile int composeAction;
    private volatile string? databaseOffline;
    private DockerReadiness.Phase lastPhase = DockerReadiness.Phase.Off;
    private readonly CancellationToken lifetime;

    /// <param name="refreshTunnel">Reads again only a card's dev tunnel (see <see cref="EnvironmentController.RefreshTunnel"/>).</param>
    /// <param name="lifetime">The column's lifetime: stops reading the container logs.</param>
    public DevToolsController(EnvironmentViewModel vm, Action requestScan, Action<RepoCardViewModel> refreshTunnel, CancellationToken lifetime = default)
    {
        this.vm = vm;
        this.requestScan = requestScan;
        this.refreshTunnel = refreshTunnel;
        this.lifetime = lifetime;
        if (vm.Docker?.Slot is { } dockerSlot)
        {
            readiness = new DockerReadiness(dockerSlot);
            databaseOffline = DockerReadiness.NotRead.DatabaseOffline;
        }

        foreach (var card in vm.Repos)
        {
            cards.Add(Wire(card));
        }

        var stack = vm.Tools.Stack;
        stack.StartCommand = new AsyncCommand(() => RunStack("starting the environment…", () => Execute("started", StackStarts(lastScan)).Line, DockerReadiness.ComposeAction.Starting));
        stack.StopCommand = new AsyncCommand(() => RunStack("stopping the environment…", () => Execute("stopped", StackStops(lastScan)).Line, DockerReadiness.ComposeAction.Stopping));
        vm.TunnelCommand = new AsyncCommand(() => Run(ToggleTunnel));
    }

    // The header's “Tunnel” switch: turning it on claims a pool tunnel and gives a port to every
    // repository that exposes one, turning it off releases them all (EnvironmentTunnel).
    private void ToggleTunnel()
    {
        var c = ActiveConfiguration.Current;
        var on = EnvironmentTunnel.IsOn(vm.Number);
        var lines = new List<string>();
        void Log(string line)
        {
            lines.Add(line);
            vm.OperationLog?.Append(line);
            UiThread.Run(() => vm.ActivityLine = string.Join(" · ", lines));
        }

        UiThread.Run(() => vm.TunnelBusy = true);
        try
        {
            Report(on ? "tunnel: releasing the pool ports…" : "tunnel: claiming a pool tunnel…");
            var error = (on
                ? EnvironmentTunnel.DisableAsync(c, vm.Number, Log)
                : EnvironmentTunnel.EnableAsync(c, vm.Number, Log)).GetAwaiter().GetResult();
            Log(error is not null
                ? $"tunnel not turned {(on ? "off" : "on")}: {error}"
                : on ? "tunnel off" : "tunnel on");
        }
        finally
        {
            var now = EnvironmentTunnel.IsOn(vm.Number);
            // A full re-read of the environment, like its Refresh button: ports and URLs
            // in the cards, env files rewritten, host status.
            UiThread.Run(() =>
            {
                vm.TunnelBusy = false;
                vm.SetTunnelOn(now);
                vm.RefreshCommand?.Execute(null);
            });
            requestScan();
        }
    }

    /// <summary>Reads the column's cards again (local git and migrations): set by whoever creates the controller.</summary>
    public Action? RefreshColumn { get; set; }

    /// <summary>
    /// Why the Docker environment's migrations cannot be read (database off, starting, down);
    /// null if the database answers or the environment is local. Read by sessions off the UI thread.
    /// </summary>
    public string? DatabaseOffline => databaseOffline;

    /// <summary>Probes db, api and web of the running containers: off the UI thread, before <see cref="Apply"/>.</summary>
    public Task ProbeAsync(Soffritto.Core.Docker.DockerStatus? docker, CancellationToken ct)
        => readiness is null || docker is null ? Task.CompletedTask : readiness.ProbeAsync(docker, ct);

    private CardTools Wire(RepoCardViewModel card)
    {
        var location = card.Location;
        var key = location.Key;
        var tools = new CardTools(card);
        var tool = card.Tool!;

        // The card's tunnel section shows only the repository's pool port: the tunnel is turned
        // on and off for the whole environment, from the header (ToggleTunnel).
        tools.IsPoolTunnel = card.Tunnel is not null;

        // Container cards go through compose before the switch on type: they have no
        // local services, and the backend branch would start Kestrel on the machine.
        if (vm.Docker?.Slot is { } slot && ServiceOf(slot, location) is { } service)
        {
            WireContainer(card, tools, slot, service);
            return tools;
        }

        var local = LocalServices.Of(location.Entry);
        tools.Local = local;
        switch (location.Settings)
        {
            case BackendConfiguration backend:
                tool.OpenCommand = new AsyncCommand(() => Run(() => OpenVisualStudio(location, backend)));
                tool.StartCommand = new AsyncCommand(() => Run(() => Report(local.Start())));
                tool.StopCommand = new AsyncCommand(() => Run(() => Report(local.Stop(lastScan))));
                break;

            case FrontendConfiguration frontend:
                tool.OpenCommand = new AsyncCommand(() => Run(() => OpenVisualStudioCode(location)));
                tool.StartCommand = new AsyncCommand(() => Run(() => Report(local.Start())));
                tool.StopCommand = new AsyncCommand(() => Run(() => Report(local.Stop(lastScan))));

                if (ExtraCommand.Of(frontend) is { } extra)
                {
                    var word = ExtraCommand.Name(extra);
                    tool.ExtraText = LaneTags.Capitalize(word);
                    tool.ExtraToolTip = $"{extra} in a console";
                    tool.ExtraCommand = new AsyncCommand(() => Run(() =>
                        StartConsole(location.Path, ConsoleServices.Marker(word, key), ExtraCommand.CommandLine(extra, EnvironmentKind.Local), $"{extra} started")));
                }

                if (card.Preview is { } preview)
                {
                    // The dev server on localhost, bypassing the tunnel: the globe stays reserved for dev tunnels.
                    tool.SideGlyph = LocalhostGlyph;
                    tool.SideCommand = new AsyncCommand(() => Run(() => OpenInBrowser(preview.FrontendUrl)));
                    ShowPreviewUrl(tool, preview);

                    // The address is read on click, on the UI thread: it depends on the view chosen at that moment.
                    preview.OpenExternalCommand = new AsyncCommand(() =>
                    {
                        var url = preview.ExternalUrl;
                        return Run(() => OpenInBrowser(url));
                    });
                }

                break;

            case AppRepoConfiguration:
                tool.OpenCommand = new AsyncCommand(() => Run(() => OpenVisualStudioCode(location)));
                tool.StartCommand = new AsyncCommand(() => Run(() => Report(local.Start())));
                tool.StopCommand = new AsyncCommand(() => Run(() => Report(local.Stop(lastScan))));

                if (local.HasBundler)
                {
                    tool.SideText = "Metro";
                    tool.SideCommand = new AsyncCommand(() => Run(() => Report(local.StartBundler())));
                }

                break;

            default:
                tool.OpenCommand = new AsyncCommand(() => Run(() => OpenVisualStudioCode(location)));
                break;
        }

        return tools;
    }

    // The card's compose service: the api for the primary backend, the web for the primary
    // frontend; null for cards that stay local (app, generic, other repositories).
    private static string? ServiceOf(Soffritto.Core.Docker.DockerSlot slot, RepoLocation location)
    {
        if (slot.Backend is { } backend && string.Equals(backend.Id, location.Id, StringComparison.OrdinalIgnoreCase))
        {
            return slot.ApiService;
        }

        return slot.Frontend is { } frontend && string.Equals(frontend.Id, location.Id, StringComparison.OrdinalIgnoreCase)
            ? slot.WebService
            : null;
    }

    private void WireContainer(RepoCardViewModel card, CardTools tools, Soffritto.Core.Docker.DockerSlot slot, string service)
    {
        var location = card.Location;
        var tool = card.Tool!;
        tools.IsContainer = true;
        tools.Service = service;
        tools.IsWeb = location.Type == RepositoryType.Frontend;

        tool.OpenCommand = location.Settings is BackendConfiguration backend
            ? new AsyncCommand(() => Run(() => OpenVisualStudio(location, backend)))
            : new AsyncCommand(() => Run(() => OpenVisualStudioCode(location)));

        // The web brings the Compose Watch console along: it opens only after a successful up and
        // closes before the stop, with processes read on the spot rather than from the last scan.
        tool.StartCommand = new AsyncCommand(() => RunStack($"starting {service}…", () => ContainerStart(service), DockerReadiness.ComposeAction.Starting, card));
        tool.StopCommand = new AsyncCommand(() => RunStack($"stopping {service}…", () => ContainerStop(service), DockerReadiness.ComposeAction.Stopping, card));

        card.ContainerLog = new LogSectionViewModel($"{service} logs")
        {
            ConsoleToolTip = $"docker compose logs -f of the {service} service in a console",
            ConsoleCommand = new AsyncCommand(() => Run(() => StartConsole(
                slot.ComposeFolder,
                ConsoleServices.Marker($"log-{service}", DockerWatchConsole.Key(slot)),
                Soffritto.Core.Docker.DockerEnvironmentProvisioner.LogsCommand(slot, service),
                $"{service} logs in a console"))),
        };
        var containerLog = card.ContainerLog;
        var following = new int[1];
        containerLog.Expander.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Expander.IsExpanded)
                && containerLog.Expander.IsExpanded
                && Interlocked.CompareExchange(ref following[0], 1, 0) == 0)
            {
                _ = FollowContainerLogAsync(containerLog, slot, service)
                    .ContinueWith(_ => Interlocked.Exchange(ref following[0], 0), TaskScheduler.Default);
            }
        };

        // Dependencies installed in the worktree on first use also serve VS Code (imports, ESLint).
        if (location.Settings is FrontendConfiguration frontend && ExtraCommand.Of(frontend) is { } extra)
        {
            var word = ExtraCommand.Name(extra);
            tool.ExtraText = LaneTags.Capitalize(word);
            tool.ExtraToolTip = $"{extra} in a console; without node_modules in the worktree, npm ci first";
            tool.ExtraCommand = new AsyncCommand(() => Run(() => StartConsole(
                location.Path,
                ConsoleServices.Marker(word, location.Key),
                ExtraCommand.CommandLine(extra, EnvironmentKind.Docker),
                $"{extra} started")));
        }

        if (card.Preview is { } preview)
        {
            tool.SideGlyph = LocalhostGlyph;
            tool.SideCommand = new AsyncCommand(() => Run(() => OpenInBrowser(preview.FrontendUrl)));
            ShowPreviewUrl(tool, preview);
            preview.OpenExternalCommand = new AsyncCommand(() =>
            {
                var url = preview.ExternalUrl;
                return Run(() => OpenInBrowser(url));
            });
        }
    }

    private const int ContainerLogLines = 200;

    // While the section stays open it reads the service's last lines again every few seconds: a
    // container recreated by compose up is followed automatically, and nothing runs once the section is closed.
    private async Task FollowContainerLogAsync(LogSectionViewModel log, Soffritto.Core.Docker.DockerSlot slot, string service)
    {
        log.Replace(log.Text, "reading…");
        try
        {
            while (log.Expander.IsExpanded && !lifetime.IsCancellationRequested)
            {
                var outcome = await Soffritto.Core.Docker.ComposeRunner.RunAsync(
                    slot,
                    $"logs --no-color --no-log-prefix --tail {ContainerLogLines} {service}",
                    slot.HasContainerizedFrontend,
                    timeoutSeconds: 20,
                    ct: lifetime).ConfigureAwait(false);
                if (outcome.Success)
                {
                    var text = outcome.StandardOutput.TrimEnd();
                    log.Replace(
                        text.Length > 0 ? text : "no lines: the container does not exist or has not written anything yet",
                        $"last {ContainerLogLines} lines, {DateTime.Now:HH:mm:ss}");
                }
                else
                {
                    log.Replace(log.Text, $"logs not read: {outcome.Tail(1)}");
                }

                await Task.Delay(TimeSpan.FromSeconds(3), lifetime).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static void ShowPreviewUrl(ToolGroupViewModel tool, FrontendPreviewViewModel preview)
    {
        tool.SetSide(true, $"Opens {preview.FrontendUrl} in the browser");
        preview.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FrontendPreviewViewModel.FrontendUrl))
            {
                tool.SetSide(true, $"Opens {preview.FrontendUrl} in the browser");
            }
        };
    }

    private string ContainerStart(string service)
    {
        var slot = vm.Docker!.Slot!;
        var error = Soffritto.Core.Docker.DockerEnvironmentProvisioner
            .StartServiceAsync(ActiveConfiguration.Current, vm.Number, service, line => Report($"{service}: {line}"))
            .GetAwaiter().GetResult();
        if (error is not null)
        {
            return error;
        }

        return slot.HasContainerizedFrontend && service == slot.WebService
            ? $"{service} started; {DockerWatchConsole.Start(ProcessTable.Read([]), slot)}"
            : $"{service} started";
    }

    private string ContainerStop(string service)
    {
        var slot = vm.Docker!.Slot!;
        var watch = slot.HasContainerizedFrontend && service == slot.WebService
            ? $"{DockerWatchConsole.Stop(ProcessTable.Read([]), slot)}; "
            : string.Empty;
        var error = Soffritto.Core.Docker.DockerEnvironmentProvisioner
            .StopServiceAsync(ActiveConfiguration.Current, vm.Number, service, line => Report($"{service}: {line}"))
            .GetAwaiter().GetResult();
        return error ?? $"{watch}{service} stopped";
    }

    private Soffritto.Core.Docker.PoolAssignment? AssignmentOf(CardTools tools, IReadOnlyList<Soffritto.Core.Docker.PoolAssignment> assignments)
        => assignments.FirstOrDefault(a => a.Environment == vm.Number && string.Equals(a.Repository, tools.Card.Id, StringComparison.OrdinalIgnoreCase));

    // Docker environment phase in the environment card. When the database state changes the
    // column is read again, so migrations go from “starting” to read.
    private void ApplyPhase(Soffritto.Core.Docker.DockerStatus? docker)
    {
        if (readiness is null || vm.Docker is not { } dockerVm)
        {
            return;
        }

        var snapshot = readiness.Evaluate(docker, (DockerReadiness.ComposeAction)composeAction, DateTimeOffset.Now);
        dockerVm.ApplyPhase(snapshot);

        if (snapshot.DatabaseOffline != databaseOffline)
        {
            databaseOffline = snapshot.DatabaseOffline;
            RefreshColumn?.Invoke();
        }

        // Pool ports probed during startup say “no answer”: they are read again once services are ready.
        if (snapshot.Phase == DockerReadiness.Phase.Ready && lastPhase != DockerReadiness.Phase.Ready)
        {
            foreach (var card in vm.Repos.Where(card => card.Tunnel is not null))
            {
                refreshTunnel(card);
            }
        }

        lastPhase = snapshot.Phase;
    }

    /// <summary>Recomputes state and buttons from the process snapshot. Must be called on the UI thread.</summary>
    /// <param name="docker">The containers from the same scan; null if no environment is Docker.</param>
    public void Apply(IReadOnlyList<ProcessRow> scan, Soffritto.Core.Docker.DockerStatus? docker = null)
    {
        lastScan = scan;
        lastDocker = docker;
        var slot = vm.Docker?.Slot;
        var assignments = Soffritto.Core.Docker.TunnelPool.Read();

        if (vm.Docker is { } dockerVm && docker is not null)
        {
            dockerVm.ApplyDocker(docker.Error);
        }

        ApplyPhase(docker);
        vm.SetTunnelOn(EnvironmentTunnel.IsOn(vm.Number, assignments));

        foreach (var tools in cards)
        {
            var card = tools.Card;
            var tool = card.Tool!;

            if (tools.IsPoolTunnel && card.Tunnel is { } pool)
            {
                ApplyPoolHost(tools, pool, scan, assignments);
            }

            if (tools.IsContainer)
            {
                ApplyContainer(tools, scan, docker, slot!);
                continue;
            }

            var local = tools.Local!;
            switch (card.Location.Settings)
            {
                case BackendConfiguration backend:
                {
                    var view = EvaluateBackend(tools, scan);
                    tool.SetState(view.Kestrel, "Kestrel");
                    tool.SetActions(
                        canStart: view.Kestrel == ServiceState.Stopped,
                        canStop: view.Kestrel != ServiceState.Stopped,
                        startToolTip: $"Starts Kestrel (dotnet run --launch-profile {backend.LaunchProfile}) in a console");
                    break;
                }

                case FrontendConfiguration frontend:
                {
                    var state = local.State(scan);
                    tool.SetState(state, "dev server");
                    tool.SetActions(
                        canStart: state == ServiceState.Stopped,
                        canStop: state != ServiceState.Stopped,
                        startToolTip: $"{frontend.StartCommand} in a console");
                    break;
                }

                case AppRepoConfiguration app:
                {
                    var state = local.State(scan);
                    tool.SetState(state, "app (react-native / Metro)");
                    tool.SetActions(
                        canStart: state == ServiceState.Stopped,
                        canStop: state != ServiceState.Stopped,
                        startToolTip: $"{app.StartCommand} in a console");

                    var metro = local.MetroState(scan);
                    tool.SetSide(
                        metro == ServiceState.Stopped,
                        metro == ServiceState.Stopped
                            ? $"{app.BundlerCommand} in a console: starts the bundler"
                            : "The bundler is already running: the app's stop button stops it");
                    break;
                }
            }
        }

        // Play while something is left to start, stop only once the whole stack is running: in a mixed
        // state (for example Kestrel from Visual Studio and the frontend off) the button completes, it does not stop.
        if (Volatile.Read(ref stackRunning) == 1)
        {
            vm.Tools.Stack.SetActions(canStart: false, canStop: false, startToolTip: "A start or stop is already in progress");
            return;
        }

        var missing = StackStarts(scan);
        var stoppable = StackStops(scan);
        vm.Tools.Stack.SetActions(
            canStart: missing.Count > 0,
            canStop: missing.Count == 0 && stoppable.Count > 0,
            startToolTip: missing.Count > 0
                ? $"Starts {string.Join(", ", missing.Select(s => s.Name))} (not the app)"
                : "Nothing to start",
            stopToolTip: $"Stops {string.Join(", ", stoppable.Select(s => s.Name))}");
    }

    // The environment stack, card by card in column order: Kestrel and dev server. Start only
    // what is off; stop what is running. Starting skips the app, which is heavy and often not
    // needed; stopping also stops it and the bundler.
    // In a Docker environment the containers are a single step (“container”, compose up of the whole
    // slot) followed by the web watch, which starts only if the up succeeded; the environment's
    // tunnel host joins only the start, to bring back a host that went down.
    private List<(string Name, Action Act)> StackStarts(IReadOnlyList<ProcessRow> scan)
    {
        var starts = new List<(string Name, Action Act)>();

        if (vm.Docker?.Slot is { } slot && lastDocker is { IsReachable: true } docker)
        {
            var containersUp = cards.Where(t => t.IsContainer).Select(t => t.Service)
                .Append(slot.DatabaseService)
                .All(service => ContainerOf(docker, slot, service)?.RunState == Soffritto.Core.Docker.ContainerRunState.Running);
            var containerStarted = containersUp;
            if (!containersUp)
            {
                starts.Add(("container", () =>
                {
                    var error = Soffritto.Core.Docker.DockerEnvironmentProvisioner
                        .StartAsync(ActiveConfiguration.Current, vm.Number, line => Report($"container: {line}"))
                        .GetAwaiter().GetResult();
                    containerStarted = error is null;
                    if (error is not null)
                    {
                        throw new InvalidOperationException(error);
                    }
                }));
            }

            if (slot.HasContainerizedFrontend && (!containersUp || DockerWatchConsole.StateOf(scan, slot) != ServiceState.Running))
            {
                starts.Add(("watch", () =>
                {
                    if (!containerStarted)
                    {
                        throw new InvalidOperationException("containers not started");
                    }

                    DockerWatchConsole.Start(ProcessTable.Read([]), slot);
                }));
            }
        }

        // The environment's tunnel host, if the environment has the tunnel and the host went down or was closed.
        foreach (var tunnel in Soffritto.Core.Docker.TunnelPool.Read().Where(a => a.Environment == vm.Number).GroupBy(a => a.TunnelDescription))
        {
            var host = PoolHosts.Tools(tunnel.Key);
            var tunnelId = tunnel.First().TunnelId;
            if (host.Evaluate(scan, tunnelId, hostConnected: false, string.Empty).State == ServiceState.Stopped)
            {
                starts.Add(($"tunnel host {tunnel.Key}", () => host.Start(ProcessTable.Read([]), tunnelId, hostConnected: false)));
            }
        }

        starts.AddRange(LocalServices.StackStarts(LocalCards(), scan));

        return starts;
    }

    // The watch closes before the containers, with processes read when the step starts: a
    // rebuild rule must not restart the web halfway through the stop.
    private List<(string Name, Action Act)> StackStops(IReadOnlyList<ProcessRow> scan)
    {
        var stops = new List<(string Name, Action Act)>();

        if (vm.Docker?.Slot is { } slot)
        {
            if (slot.HasContainerizedFrontend && DockerWatchConsole.StateOf(scan, slot) != ServiceState.Stopped)
            {
                stops.Add(("watch", () => DockerWatchConsole.Stop(ProcessTable.Read([]), slot)));
            }

            // A Docker that does not answer says nothing about the containers: the stop is attempted, and its
            // outcome decides whether the checkout can go ahead.
            if (lastDocker is not { IsReachable: true } docker
                || docker.OfProject(slot.Project).Any(c => c.RunState != Soffritto.Core.Docker.ContainerRunState.Stopped))
            {
                stops.Add(("container", () =>
                {
                    var (outcome, allStopped) = Soffritto.Core.Docker.DockerEnvironmentProvisioner
                        .StopAsync(ActiveConfiguration.Current, vm.Number, line => Report($"container: {line}"))
                        .GetAwaiter().GetResult();
                    if (!allStopped)
                    {
                        throw new InvalidOperationException(outcome);
                    }
                }));
            }
        }

        stops.AddRange(LocalServices.StackStops(LocalCards(), scan));

        return stops;
    }

    /// <summary>
    /// Stops everything the environment has running, app and bundler included, from the last process
    /// snapshot: in a Docker environment also watch and containers, in that order. Returns the
    /// outcome and whether everything stopped; in local environments it is always true, as before.
    /// Used by reset and PR checkout before git.
    /// </summary>
    public Task<(string Outcome, bool AllStopped)> StopEverythingAsync() => Task.Run(() =>
    {
        UiThread.Run(() => readiness?.MarkStopped(null));
        composeAction = (int)DockerReadiness.ComposeAction.Stopping;
        try
        {
            var (line, failed) = Execute("stopped", StackStops(lastScan));
            return (line, vm.Docker is null || failed == 0);
        }
        finally
        {
            composeAction = (int)DockerReadiness.ComposeAction.None;
            requestScan();
        }
    });

    private static BackendToolsView EvaluateBackend(CardTools tools, IReadOnlyList<ProcessRow> scan)
    {
        var tunnel = tools.Card.Tunnel?.State;
        return tools.Local!.Backend!.Evaluate(scan, tunnel?.Id, tunnel?.HostConnected == true, tunnel?.ReadStamp ?? string.Empty);
    }

    private IEnumerable<LocalServices> LocalCards() => cards.Where(t => t.Local is not null).Select(t => t.Local!);

    // A failing service does not stop the others: the activity line reports both outcomes.
    private static (string Line, int Failed) Execute(string verb, List<(string Name, Action Act)> steps)
        => LocalServices.Execute(verb, steps);

    private static Soffritto.Core.Docker.DockerContainer? ContainerOf(Soffritto.Core.Docker.DockerStatus docker, Soffritto.Core.Docker.DockerSlot slot, string? service)
        => docker.OfProject(slot.Project).FirstOrDefault(c => string.Equals(c.Service, service, StringComparison.OrdinalIgnoreCase));

    private static ServiceState StateOf(Soffritto.Core.Docker.DockerContainer? container) => container?.RunState switch
    {
        Soffritto.Core.Docker.ContainerRunState.Running => ServiceState.Running,
        Soffritto.Core.Docker.ContainerRunState.Starting => ServiceState.WindowOnly,
        _ => ServiceState.Stopped,
    };

    // A container card's state comes from docker ps; a running web without its watch is
    // orange, and Start (which rebuilds the image and reopens the console) is the suggested action.
    private void ApplyContainer(CardTools tools, IReadOnlyList<ProcessRow> scan, Soffritto.Core.Docker.DockerStatus? docker, Soffritto.Core.Docker.DockerSlot slot)
    {
        var tool = tools.Card.Tool!;
        if (docker is null || !docker.IsReachable)
        {
            var reason = docker?.Error ?? "container status not read yet";
            tool.SetDescribedState(ServiceState.Stopped, reason);
            tool.SetActions(canStart: false, canStop: false, startToolTip: reason);
            return;
        }

        var container = ContainerOf(docker, slot, tools.Service);
        var state = StateOf(container);
        var name = $"container {tools.Service}";
        if (tools.IsWeb && state == ServiceState.Running && DockerWatchConsole.StateOf(scan, slot) != ServiceState.Running)
        {
            const string watchOff = "container running, watch off: worktree changes do not reach the container; Start reopens it";
            tool.SetDescribedState(ServiceState.WindowOnly, watchOff);
            tool.SetActions(canStart: true, canStop: false, startToolTip: watchOff);
            return;
        }

        tool.SetDescribedState(state, $"{name}: {container?.Status ?? "missing"}");
        tool.SetActions(
            canStart: state == ServiceState.Stopped,
            canStop: state != ServiceState.Stopped,
            startToolTip: tools.IsWeb ? $"docker compose up --build {tools.Service} and the Compose Watch console" : $"docker compose up --build {tools.Service}",
            stopToolTip: tools.IsWeb ? $"Closes the watch and stops the {tools.Service} container" : $"Stops the {tools.Service} container");
    }

    // The card's tunnel section exists only if the repository has a pool port: it shows the
    // port with its URL, and the dot says whether the environment's tunnel host is running.
    private void ApplyPoolHost(CardTools tools, TunnelSection section, IReadOnlyList<ProcessRow> scan, IReadOnlyList<Soffritto.Core.Docker.PoolAssignment> assignments)
    {
        var assignment = AssignmentOf(tools, assignments);
        tools.Card.TunnelShared = assignment is not null;
        if (assignment is null)
        {
            section.Host.SetDescribedState(ServiceState.Stopped, "no tunnel: turn it on with “Tunnel” in the environment header");
            return;
        }

        var description = assignment.TunnelDescription;
        var view = PoolHosts.Tools(description).Evaluate(scan, assignment.TunnelId, hostConnected: false, section.State.ReadStamp);
        if (view.State == ServiceState.Running)
        {
            section.Host.SetDescribedState(ServiceState.Running, $"tunnel host {description} running: port {assignment.Port} is reachable if the service is running");
        }
        else
        {
            section.Host.SetDescribedState(ServiceState.WindowOnly, $"tunnel host {description} off: the URL does not answer; starting the environment brings it back");
        }
    }

    private void OpenVisualStudio(RepoLocation location, BackendConfiguration backend)
    {
        var solution = System.IO.Path.Combine(location.Path, backend.Solution);
        var devenv = DevTools.VisualStudio() ?? throw new InvalidOperationException("Visual Studio not found (vswhere).");

        Launch($"\"{devenv}\" \"{solution}\"", location.Path);
        Report($"Visual Studio opened on {location.FolderName}");
    }

    private void OpenVisualStudioCode(RepoLocation location)
    {
        var code = DevTools.VisualStudioCode() ?? throw new InvalidOperationException("VS Code not found.");

        Launch($"\"{code}\" \"{location.Path}\"", location.Path);
        Report($"VS Code opened on {location.FolderName}");
    }

    // The default browser starts through rundll32 rather than ShellExecute: that way it goes through
    // Launch, outside the job, and does not close together with the exe.
    private void OpenInBrowser(Uri url)
    {
        Launch($"rundll32.exe url.dll,FileProtocolHandler {url.AbsoluteUri}", System.IO.Path.GetTempPath());
        Report($"{url.AbsoluteUri} opened in the browser");
    }

    private void StartConsole(string workingDirectory, string marker, string command, string notice)
        => Report(ConsoleServices.StartConsole(workingDirectory, marker, command)
            ? notice
            : $"{notice} inside the exe's job: it will close together with the app");

    private void Launch(string commandLine, string workingDirectory)
    {
        if (!DetachedLauncher.Start(commandLine, workingDirectory, newConsole: false))
        {
            Report("started inside the exe's job: it will close together with the app");
        }
    }

    private Task Run(Action action) => Task.Run(() =>
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Report(ex.Message);
        }
        finally
        {
            requestScan();
        }
    });

    // Stack and container actions can take minutes (compose up -d --build): without this guard every
    // extra click launched another compose in parallel on the same slot. While it runs, the environment
    // dot pulses and play stays disabled; clicks on the other compose buttons are ignored.
    // The card being acted on shows moving stripes; the action tells DockerReadiness whether services are
    // starting (waiting, not failed) or being stopped on purpose (off, not failed).
    private Task RunStack(string pending, Func<string> action, DockerReadiness.ComposeAction kind, RepoCardViewModel? card = null, string? service = null)
    {
        if (Interlocked.Exchange(ref stackRunning, 1) == 1)
        {
            return Task.CompletedTask;
        }

        service ??= card is null ? null : cards.First(t => ReferenceEquals(t.Card, card)).Service;
        UiThread.Run(() =>
        {
            vm.BeginOperation();
            vm.ActivityLine = pending;
            vm.Tools.Stack.SetActions(canStart: false, canStop: false, startToolTip: pending);
            if (card is not null)
            {
                card.IsWorking = true;
            }

            if (kind == DockerReadiness.ComposeAction.Starting)
            {
                readiness?.MarkStarted(service);
            }
            else
            {
                readiness?.MarkStopped(service);
            }

            composeAction = (int)kind;
        });

        return Run(() =>
        {
            try
            {
                Report(action());
            }
            finally
            {
                composeAction = (int)DockerReadiness.ComposeAction.None;
                Interlocked.Exchange(ref stackRunning, 0);
                UiThread.Run(() =>
                {
                    vm.EndOperation();
                    if (card is not null)
                    {
                        card.IsWorking = false;
                    }
                });
            }
        });
    }

    private void Report(string line) => vm.Report(line);

    /// <summary>A card's tools and state: markers, process recognition, tunnel.</summary>
    private sealed class CardTools(RepoCardViewModel card)
    {
        public RepoCardViewModel Card { get; } = card;

        /// <summary>The services of a card that runs on this machine; null for container cards.</summary>
        public LocalServices? Local { get; set; }

        /// <summary>Primary backend or frontend of a Docker environment: runs in a compose container.</summary>
        public bool IsContainer { get; set; }

        /// <summary>The container card's compose service (api, web).</summary>
        public string Service { get; set; } = string.Empty;

        public bool IsWeb { get; set; }

        /// <summary>The card has the tunnel section, for the repository's pool port.</summary>
        public bool IsPoolTunnel { get; set; }
    }
}

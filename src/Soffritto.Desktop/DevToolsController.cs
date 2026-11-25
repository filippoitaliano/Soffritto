using Soffritto.Core;
using Soffritto.Core.Configuration;
using Soffritto.Desktop.Infrastructure;
using Soffritto.Desktop.ViewModels;
using Soffritto.Presentation;

namespace Soffritto.Desktop;

/// <summary>
/// Gli strumenti delle card di una colonna, secondo il tipo del repository: il backend apre Visual
/// Studio e avvia Kestrel (via <see cref="BackendTools"/> condiviso con la VSIX), il frontend il
/// dev server, l'app l'app e Metro, il generico solo VS Code; ogni card con un dev tunnel si
/// condivide con una porta del pool, servita dall'host del suo tunnel. Tutto parte in console staccate dall'exe, e lo stato si ricava dai processi (vedi
/// <see cref="ConsoleServices"/>). I marcatori usano la chiave della card: il numero
/// dell'ambiente per il predefinito, come sempre, "N-id" per gli altri.
/// </summary>
public sealed class DevToolsController
{
    // Glifo Segoe Fluent Icons del globo con il cursore: il dev server su localhost.
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

    /// <param name="refreshTunnel">Rilegge il solo dev tunnel di una card (vedi <see cref="EnvironmentController.RefreshTunnel"/>).</param>
    /// <param name="lifetime">La vita della colonna: ferma la lettura dei log dei container.</param>
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
        stack.StartCommand = new AsyncCommand(() => RunStack("avvio dell'ambiente…", () => Execute("avviati", StackStarts(lastScan)).Line, DockerReadiness.ComposeAction.Starting));
        stack.StopCommand = new AsyncCommand(() => RunStack("arresto dell'ambiente…", () => Execute("fermati", StackStops(lastScan)).Line, DockerReadiness.ComposeAction.Stopping));
        vm.TunnelCommand = new AsyncCommand(() => Run(ToggleTunnel));
    }

    // L'interruttore «Tunnel» dell'intestazione: acceso reclama un tunnel del pool e da' una porta
    // a ogni repository che ne espone una, spento le rilascia tutte (EnvironmentTunnel).
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
            Report(on ? "tunnel: rilascio le porte del pool…" : "tunnel: reclamo un tunnel del pool…");
            var error = (on
                ? EnvironmentTunnel.DisableAsync(c, vm.Number, Log)
                : EnvironmentTunnel.EnableAsync(c, vm.Number, Log)).GetAwaiter().GetResult();
            Log(error is not null
                ? $"tunnel non {(on ? "spento" : "acceso")}: {error}"
                : on ? "tunnel spento" : "tunnel acceso");
        }
        finally
        {
            var now = EnvironmentTunnel.IsOn(vm.Number);
            // La rilettura completa dell'ambiente, come il suo pulsante Aggiorna: porte e URL
            // nelle card, env riscritti, stato dell'host.
            UiThread.Run(() =>
            {
                vm.TunnelBusy = false;
                vm.SetTunnelOn(now);
                vm.RefreshCommand?.Execute(null);
            });
            requestScan();
        }
    }

    /// <summary>Rilegge le card della colonna (git locale e migrazioni): la imposta chi crea il controller.</summary>
    public Action? RefreshColumn { get; set; }

    /// <summary>
    /// Perche' le migrazioni dell'ambiente Docker non si leggono (database spento, in avvio, fermo);
    /// null se il database risponde o se l'ambiente e' locale. Letta dalle sessioni fuori dal thread della UI.
    /// </summary>
    public string? DatabaseOffline => databaseOffline;

    /// <summary>Sonda db, api e web dei container accesi: fuori dal thread della UI, prima di <see cref="Apply"/>.</summary>
    public Task ProbeAsync(Soffritto.Core.Docker.DockerStatus? docker, CancellationToken ct)
        => readiness is null || docker is null ? Task.CompletedTask : readiness.ProbeAsync(docker, ct);

    private CardTools Wire(RepoCardViewModel card)
    {
        var location = card.Location;
        var key = location.Key;
        var tools = new CardTools(card);
        var tool = card.Tool!;

        // La sezione del tunnel della card mostra solo la porta del pool del repository: il tunnel
        // si accende e si spegne per tutto l'ambiente, dall'intestazione (ToggleTunnel).
        tools.IsPoolTunnel = card.Tunnel is not null;

        // Le card nei container passano da compose prima dello switch per tipo: non hanno
        // BackendTools, e il ramo del backend li userebbe (tools.Backend!).
        if (vm.Docker?.Slot is { } slot && ServiceOf(slot, location) is { } service)
        {
            WireContainer(card, tools, slot, service);
            return tools;
        }

        switch (location.Settings)
        {
            case BackendConfiguration backend:
                tools.Backend = new BackendTools(location.Path, backend, key);
                tool.OpenCommand = new AsyncCommand(() => Run(() => OpenVisualStudio(location, backend)));
                tool.StartCommand = new AsyncCommand(() => Run(() => Report(tools.Backend.StartKestrel())));
                tool.StopCommand = new AsyncCommand(() => Run(() => Report(tools.Backend.StopKestrel(lastScan))));
                break;

            case FrontendConfiguration frontend:
                tools.ServiceMarker = ConsoleServices.Marker("frontend", key);
                tools.IsService = p => RepoProcesses.IsFrontend(p, location.Path, frontend);
                tool.OpenCommand = new AsyncCommand(() => Run(() => OpenVisualStudioCode(location)));
                tool.StartCommand = new AsyncCommand(() => Run(() => StartConsole(location.Path, tools.ServiceMarker, frontend.StartCommand, $"{frontend.StartCommand} avviato")));
                tool.StopCommand = new AsyncCommand(() => Run(() => Stop(tools.ServiceMarker, tools.IsService, "dev server del frontend fermato")));

                if (ExtraCommand.Of(frontend) is { } extra)
                {
                    var word = ExtraCommand.Name(extra);
                    tool.ExtraText = LaneTags.Capitalize(word);
                    tool.ExtraToolTip = $"{extra} in una console";
                    tool.ExtraCommand = new AsyncCommand(() => Run(() =>
                        StartConsole(location.Path, ConsoleServices.Marker(word, key), ExtraCommand.CommandLine(extra, EnvironmentKind.Local), $"{extra} avviato")));
                }

                if (card.Preview is { } preview)
                {
                    // Il dev server su localhost, senza passare dal tunnel: il globo resta ai dev tunnel.
                    tool.SideGlyph = LocalhostGlyph;
                    tool.SideCommand = new AsyncCommand(() => Run(() => OpenInBrowser(preview.FrontendUrl)));
                    ShowPreviewUrl(tool, preview);

                    // L'indirizzo si legge al clic, sul thread della UI: dipende dalla vista scelta in quel momento.
                    preview.OpenExternalCommand = new AsyncCommand(() =>
                    {
                        var url = preview.ExternalUrl;
                        return Run(() => OpenInBrowser(url));
                    });
                }

                break;

            case AppRepoConfiguration app:
                tools.ServiceMarker = ConsoleServices.Marker("app", key);
                tools.MetroMarker = ConsoleServices.Marker("metro", key);
                tools.IsService = p => RepoProcesses.IsApp(p, location.Path);
                tools.IsMetro = p => RepoProcesses.IsMetro(p, location.Path);
                tool.OpenCommand = new AsyncCommand(() => Run(() => OpenVisualStudioCode(location)));
                tool.StartCommand = new AsyncCommand(() => Run(() => StartConsole(location.Path, tools.ServiceMarker, app.StartCommand, $"{app.StartCommand} avviato")));
                tool.StopCommand = new AsyncCommand(() => Run(() =>
                {
                    ConsoleServices.Stop(ConsoleServices.Pids(lastScan, tools.MetroMarker, tools.IsMetro));
                    Stop(tools.ServiceMarker, tools.IsService, "app fermata");
                }));

                if (app.BundlerCommand.Length > 0)
                {
                    tool.SideText = "Metro";
                    tool.SideCommand = new AsyncCommand(() => Run(() => StartConsole(location.Path, tools.MetroMarker, app.BundlerCommand, $"{app.BundlerCommand} (bundler) avviato")));
                }

                break;

            default:
                tool.OpenCommand = new AsyncCommand(() => Run(() => OpenVisualStudioCode(location)));
                break;
        }

        return tools;
    }

    // Il servizio compose della card: l'api per il backend predefinito, il web per il frontend
    // predefinito; null per le card che restano locali (app, generici, altri repository).
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

        // Il web porta con se' la console del Compose Watch: si apre solo dopo un up riuscito e si
        // chiude prima dello stop, con i processi letti al momento e non dall'ultima scansione.
        tool.StartCommand = new AsyncCommand(() => RunStack($"avvio di {service}…", () => ContainerStart(service), DockerReadiness.ComposeAction.Starting, card));
        tool.StopCommand = new AsyncCommand(() => RunStack($"arresto di {service}…", () => ContainerStop(service), DockerReadiness.ComposeAction.Stopping, card));

        card.ContainerLog = new LogSectionViewModel($"Log di {service}")
        {
            ConsoleToolTip = $"docker compose logs -f del servizio {service} in una console",
            ConsoleCommand = new AsyncCommand(() => Run(() => StartConsole(
                slot.ComposeFolder,
                ConsoleServices.Marker($"log-{service}", DockerWatchConsole.Key(slot)),
                Soffritto.Core.Docker.DockerEnvironmentProvisioner.LogsCommand(slot, service),
                $"log di {service} in una console"))),
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

        // Le dipendenze installate nel worktree al primo uso servono anche a VS Code (import, ESLint).
        if (location.Settings is FrontendConfiguration frontend && ExtraCommand.Of(frontend) is { } extra)
        {
            var word = ExtraCommand.Name(extra);
            tool.ExtraText = LaneTags.Capitalize(word);
            tool.ExtraToolTip = $"{extra} in una console; senza node_modules nel worktree prima npm ci";
            tool.ExtraCommand = new AsyncCommand(() => Run(() => StartConsole(
                location.Path,
                ConsoleServices.Marker(word, location.Key),
                ExtraCommand.CommandLine(extra, EnvironmentKind.Docker),
                $"{extra} avviato")));
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

    // Finche' la sezione resta aperta rilegge le ultime righe del servizio ogni pochi secondi: un
    // container ricreato da compose up si segue da solo, e a sezione chiusa non gira niente.
    private async Task FollowContainerLogAsync(LogSectionViewModel log, Soffritto.Core.Docker.DockerSlot slot, string service)
    {
        log.Replace(log.Text, "lettura…");
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
                        text.Length > 0 ? text : "nessuna riga: il container non c'e' o non ha ancora scritto",
                        $"ultime {ContainerLogLines} righe, {DateTime.Now:HH:mm:ss}");
                }
                else
                {
                    log.Replace(log.Text, $"log non letto: {outcome.Tail(1)}");
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
        tool.SetSide(true, $"Apre {preview.FrontendUrl} nel browser");
        preview.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FrontendPreviewViewModel.FrontendUrl))
            {
                tool.SetSide(true, $"Apre {preview.FrontendUrl} nel browser");
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
            ? $"{service} avviato; {DockerWatchConsole.Start(ProcessTable.Read([]), slot)}"
            : $"{service} avviato";
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
        return error ?? $"{watch}{service} fermato";
    }

    private Soffritto.Core.Docker.PoolAssignment? AssignmentOf(CardTools tools, IReadOnlyList<Soffritto.Core.Docker.PoolAssignment> assignments)
        => assignments.FirstOrDefault(a => a.Environment == vm.Number && string.Equals(a.Repository, tools.Card.Id, StringComparison.OrdinalIgnoreCase));

    // Fase dell'ambiente Docker nella card dell'ambiente. Quando cambia lo stato del database la
    // colonna si rilegge, cosi' le migrazioni passano da «in avvio» a lette.
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

        // Le porte del pool sondate durante l'avvio dicono «nessuna risposta»: a servizi pronti si rileggono.
        if (snapshot.Phase == DockerReadiness.Phase.Ready && lastPhase != DockerReadiness.Phase.Ready)
        {
            foreach (var card in vm.Repos.Where(card => card.Tunnel is not null))
            {
                refreshTunnel(card);
            }
        }

        lastPhase = snapshot.Phase;
    }

    /// <summary>Ricalcola stato e pulsanti dalla fotografia dei processi. Va chiamato sul thread della UI.</summary>
    /// <param name="docker">I container dalla stessa scansione; null se nessun ambiente e' Docker.</param>
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

            switch (card.Location.Settings)
            {
                case BackendConfiguration backend:
                {
                    var view = EvaluateBackend(tools, scan);
                    tool.SetState(view.Kestrel, "Kestrel");
                    tool.SetActions(
                        canStart: view.Kestrel == ServiceState.Stopped,
                        canStop: view.Kestrel != ServiceState.Stopped,
                        startToolTip: $"Avvia Kestrel (dotnet run --launch-profile {backend.LaunchProfile}) in una console");
                    break;
                }

                case FrontendConfiguration frontend:
                {
                    var state = ConsoleServices.StateOf(scan, tools.ServiceMarker, tools.IsService);
                    tool.SetState(state, "dev server");
                    tool.SetActions(
                        canStart: state == ServiceState.Stopped,
                        canStop: state != ServiceState.Stopped,
                        startToolTip: $"{frontend.StartCommand} in una console");
                    break;
                }

                case AppRepoConfiguration app:
                {
                    var state = ConsoleServices.StateOf(scan, tools.ServiceMarker, tools.IsService);
                    tool.SetState(state, "app (react-native / Metro)");
                    tool.SetActions(
                        canStart: state == ServiceState.Stopped,
                        canStop: state != ServiceState.Stopped,
                        startToolTip: $"{app.StartCommand} in una console");

                    var metro = ConsoleServices.StateOf(scan, tools.MetroMarker, tools.IsMetro);
                    tool.SetSide(
                        metro == ServiceState.Stopped,
                        metro == ServiceState.Stopped
                            ? $"{app.BundlerCommand} in una console: avvia il bundler"
                            : "Il bundler e' gia' acceso: lo ferma il bottone di stop dell'app");
                    break;
                }
            }
        }

        // Play finche' manca qualcosa da avviare, stop solo a pila tutta accesa: in uno stato misto
        // (per esempio Kestrel da Visual Studio e frontend spento) il pulsante completa, non spegne.
        if (Volatile.Read(ref stackRunning) == 1)
        {
            vm.Tools.Stack.SetActions(canStart: false, canStop: false, startToolTip: "Un avvio o un arresto e' gia' in corso");
            return;
        }

        var missing = StackStarts(scan);
        var stoppable = StackStops(scan);
        vm.Tools.Stack.SetActions(
            canStart: missing.Count > 0,
            canStop: missing.Count == 0 && stoppable.Count > 0,
            startToolTip: missing.Count > 0
                ? $"Avvia {string.Join(", ", missing.Select(s => s.Name))} (l'app no)"
                : "Niente da avviare",
            stopToolTip: $"Ferma {string.Join(", ", stoppable.Select(s => s.Name))}");
    }

    // La pila dell'ambiente, card per card in ordine di colonna: Kestrel e dev server. Avvia solo
    // cio' che e' spento; ferma cio' che e' acceso. L'avvio salta l'app, pesante e spesso non
    // richiesta; lo stop ferma anche lei e il bundler.
    // In un ambiente Docker i container sono un passo solo («container», compose up dell'intero
    // slot) seguito dal watch del web, che parte solo se l'up e' riuscito; l'host del tunnel
    // dell'ambiente entra solo nell'avvio, per riaccendere un host caduto.
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
                        throw new InvalidOperationException("container non avviati");
                    }

                    DockerWatchConsole.Start(ProcessTable.Read([]), slot);
                }));
            }
        }

        // L'host del tunnel dell'ambiente, se l'ambiente ha il tunnel e l'host e' caduto o chiuso.
        foreach (var tunnel in Soffritto.Core.Docker.TunnelPool.Read().Where(a => a.Environment == vm.Number).GroupBy(a => a.TunnelDescription))
        {
            var host = PoolHosts.Tools(tunnel.Key);
            var tunnelId = tunnel.First().TunnelId;
            if (host.Evaluate(scan, tunnelId, hostConnected: false, string.Empty).State == ServiceState.Stopped)
            {
                starts.Add(($"host del tunnel {tunnel.Key}", () => host.Start(ProcessTable.Read([]), tunnelId, hostConnected: false)));
            }
        }

        foreach (var tools in cards)
        {
            var card = tools.Card;
            if (tools.IsContainer)
            {
                continue;
            }

            if (tools.Backend is { } backend)
            {
                var view = EvaluateBackend(tools, scan);
                if (view.Kestrel == ServiceState.Stopped)
                {
                    starts.Add((NameOf(card, "Kestrel"), () => backend.StartKestrel()));
                }

                continue;
            }

            if (card.Type == RepositoryType.Frontend
                && ConsoleServices.StateOf(scan, tools.ServiceMarker, tools.IsService) == ServiceState.Stopped)
            {
                var command = ((FrontendConfiguration)card.Location.Settings).StartCommand;
                starts.Add((NameOf(card, "dev server"), () => ConsoleServices.StartConsole(card.Location.Path, tools.ServiceMarker, command)));
            }
        }

        return starts;
    }

    // Il watch si chiude prima dei container, con i processi letti quando il passo parte: una
    // regola rebuild non deve riaccendere il web a meta' stop.
    private List<(string Name, Action Act)> StackStops(IReadOnlyList<ProcessRow> scan)
    {
        var stops = new List<(string Name, Action Act)>();

        if (vm.Docker?.Slot is { } slot)
        {
            if (slot.HasContainerizedFrontend && DockerWatchConsole.StateOf(scan, slot) != ServiceState.Stopped)
            {
                stops.Add(("watch", () => DockerWatchConsole.Stop(ProcessTable.Read([]), slot)));
            }

            // Docker che non risponde non dice niente dei container: lo stop si tenta, e il suo
            // esito decide se il checkout puo' procedere.
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

        foreach (var tools in cards)
        {
            var card = tools.Card;
            if (tools.IsContainer)
            {
                continue;
            }

            if (tools.Backend is { } backend)
            {
                var view = EvaluateBackend(tools, scan);
                if (view.Kestrel != ServiceState.Stopped)
                {
                    stops.Add((NameOf(card, "Kestrel"), () => backend.StopKestrel(scan)));
                }

                continue;
            }

            if (tools.ServiceMarker.Length > 0
                && ConsoleServices.StateOf(scan, tools.ServiceMarker, tools.IsService) != ServiceState.Stopped)
            {
                var name = card.Type == RepositoryType.Frontend ? "dev server" : "app";
                stops.Add((NameOf(card, name), () => ConsoleServices.Stop(ConsoleServices.Pids(scan, tools.ServiceMarker, tools.IsService))));
            }

            if (tools.MetroMarker.Length > 0
                && ConsoleServices.StateOf(scan, tools.MetroMarker, tools.IsMetro) != ServiceState.Stopped)
            {
                stops.Add((NameOf(card, "Metro"), () => ConsoleServices.Stop(ConsoleServices.Pids(scan, tools.MetroMarker, tools.IsMetro))));
            }
        }

        return stops;
    }

    /// <summary>
    /// Ferma tutto cio' che l'ambiente ha acceso, app e bundler compresi, dall'ultima fotografia
    /// dei processi: in un ambiente Docker anche watch e container, in quest'ordine. Restituisce
    /// l'esito e se si e' fermato tutto; negli ambienti locali vale sempre vero, come prima.
    /// Lo usano il reset e il checkout delle PR prima di git.
    /// </summary>
    public Task<(string Outcome, bool AllStopped)> StopEverythingAsync() => Task.Run(() =>
    {
        UiThread.Run(() => readiness?.MarkStopped(null));
        composeAction = (int)DockerReadiness.ComposeAction.Stopping;
        try
        {
            var (line, failed) = Execute("fermati", StackStops(lastScan));
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
        return tools.Backend!.Evaluate(scan, tunnel?.Id, tunnel?.HostConnected == true, tunnel?.ReadStamp ?? string.Empty);
    }

    // I predefiniti si chiamano come sempre; per gli altri l'id dice di quale repository si tratta.
    private static string NameOf(RepoCardViewModel card, string service)
        => card.IsPrimary ? service : $"{service} ({card.Id})";

    // Un servizio che fallisce non ferma gli altri: la riga di attivita' riporta entrambi gli esiti.
    private static (string Line, int Failed) Execute(string verb, List<(string Name, Action Act)> steps)
    {
        if (steps.Count == 0)
        {
            return ($"niente da fare: nessun servizio da {(verb == "avviati" ? "avviare" : "fermare")}", 0);
        }

        var done = new List<string>();
        var failed = new List<string>();
        foreach (var (name, act) in steps)
        {
            try
            {
                act();
                done.Add(name);
            }
            catch (Exception ex)
            {
                failed.Add($"{name} ({ex.Message})");
            }
        }

        var line = done.Count > 0 ? $"{verb}: {string.Join(", ", done)}" : string.Empty;
        return (failed.Count > 0 ? $"{line}{(line.Length > 0 ? "; " : string.Empty)}non riusciti: {string.Join(", ", failed)}" : line, failed.Count);
    }

    private static Soffritto.Core.Docker.DockerContainer? ContainerOf(Soffritto.Core.Docker.DockerStatus docker, Soffritto.Core.Docker.DockerSlot slot, string? service)
        => docker.OfProject(slot.Project).FirstOrDefault(c => string.Equals(c.Service, service, StringComparison.OrdinalIgnoreCase));

    private static ServiceState StateOf(Soffritto.Core.Docker.DockerContainer? container) => container?.RunState switch
    {
        Soffritto.Core.Docker.ContainerRunState.Running => ServiceState.Running,
        Soffritto.Core.Docker.ContainerRunState.Starting => ServiceState.WindowOnly,
        _ => ServiceState.Stopped,
    };

    // Lo stato di una card container viene da docker ps; il web acceso senza il suo watch e'
    // arancio, e Avvia (che ricostruisce l'immagine e riapre la console) e' l'azione proposta.
    private void ApplyContainer(CardTools tools, IReadOnlyList<ProcessRow> scan, Soffritto.Core.Docker.DockerStatus? docker, Soffritto.Core.Docker.DockerSlot slot)
    {
        var tool = tools.Card.Tool!;
        if (docker is null || !docker.IsReachable)
        {
            var reason = docker?.Error ?? "stato dei container non ancora letto";
            tool.SetDescribedState(ServiceState.Stopped, reason);
            tool.SetActions(canStart: false, canStop: false, startToolTip: reason);
            return;
        }

        var container = ContainerOf(docker, slot, tools.Service);
        var state = StateOf(container);
        var name = $"container {tools.Service}";
        if (tools.IsWeb && state == ServiceState.Running && DockerWatchConsole.StateOf(scan, slot) != ServiceState.Running)
        {
            const string watchOff = "container acceso, watch spento: le modifiche del worktree non arrivano al container; Avvia lo riapre";
            tool.SetDescribedState(ServiceState.WindowOnly, watchOff);
            tool.SetActions(canStart: true, canStop: false, startToolTip: watchOff);
            return;
        }

        tool.SetDescribedState(state, $"{name}: {container?.Status ?? "assente"}");
        tool.SetActions(
            canStart: state == ServiceState.Stopped,
            canStop: state != ServiceState.Stopped,
            startToolTip: tools.IsWeb ? $"docker compose up --build {tools.Service} e la console del Compose Watch" : $"docker compose up --build {tools.Service}",
            stopToolTip: tools.IsWeb ? $"Chiude il watch e ferma il container {tools.Service}" : $"Ferma il container {tools.Service}");
    }

    // La sezione del tunnel della card c'e' solo se il repository ha una porta del pool: mostra
    // la porta con il suo URL, e il pallino dice se l'host del tunnel dell'ambiente e' acceso.
    private void ApplyPoolHost(CardTools tools, TunnelSection section, IReadOnlyList<ProcessRow> scan, IReadOnlyList<Soffritto.Core.Docker.PoolAssignment> assignments)
    {
        var assignment = AssignmentOf(tools, assignments);
        tools.Card.TunnelShared = assignment is not null;
        if (assignment is null)
        {
            section.Host.SetDescribedState(ServiceState.Stopped, "senza tunnel: si accende con «Tunnel» nell'intestazione dell'ambiente");
            return;
        }

        var description = assignment.TunnelDescription;
        var view = PoolHosts.Tools(description).Evaluate(scan, assignment.TunnelId, hostConnected: false, section.State.ReadStamp);
        if (view.State == ServiceState.Running)
        {
            section.Host.SetDescribedState(ServiceState.Running, $"host del tunnel {description} acceso: la porta {assignment.Port} e' raggiungibile se il servizio e' acceso");
        }
        else
        {
            section.Host.SetDescribedState(ServiceState.WindowOnly, $"host del tunnel {description} spento: l'URL non risponde; lo riaccende l'avvio dell'ambiente");
        }
    }

    private void OpenVisualStudio(RepoLocation location, BackendConfiguration backend)
    {
        var solution = System.IO.Path.Combine(location.Path, backend.Solution);
        var devenv = DevTools.VisualStudio() ?? throw new InvalidOperationException("Visual Studio non trovato (vswhere).");

        Launch($"\"{devenv}\" \"{solution}\"", location.Path);
        Report($"Visual Studio aperto su {location.FolderName}");
    }

    private void OpenVisualStudioCode(RepoLocation location)
    {
        var code = DevTools.VisualStudioCode() ?? throw new InvalidOperationException("VS Code non trovato.");

        Launch($"\"{code}\" \"{location.Path}\"", location.Path);
        Report($"VS Code aperto su {location.FolderName}");
    }

    // Il browser predefinito parte da rundll32 e non da ShellExecute: cosi' passa da Launch,
    // fuori dal job, e non si chiude insieme all'exe.
    private void OpenInBrowser(Uri url)
    {
        Launch($"rundll32.exe url.dll,FileProtocolHandler {url.AbsoluteUri}", System.IO.Path.GetTempPath());
        Report($"{url.AbsoluteUri} aperto nel browser");
    }

    private void StartConsole(string workingDirectory, string marker, string command, string notice)
        => Report(ConsoleServices.StartConsole(workingDirectory, marker, command)
            ? notice
            : $"{notice} dentro il job dell'exe: si chiudera' insieme all'app");

    private void Launch(string commandLine, string workingDirectory)
    {
        if (!DetachedLauncher.Start(commandLine, workingDirectory, newConsole: false))
        {
            Report("avviato dentro il job dell'exe: si chiudera' insieme all'app");
        }
    }

    private void Stop(string marker, Func<ProcessRow, bool> isAnchor, string notice)
    {
        ConsoleServices.Stop(ConsoleServices.Pids(lastScan, marker, isAnchor));
        Report(notice);
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

    // Pila e container possono durare minuti (compose up -d --build): senza questa guardia ogni clic
    // in piu' lanciava un altro compose in parallelo sullo stesso slot. Finche' gira, il pallino
    // dell'ambiente pulsa e il play resta spento; i clic sugli altri bottoni compose sono ignorati.
    // La card su cui si agisce scorre a righe; l'azione dice a DockerReadiness se i servizi stanno
    // partendo (in attesa, non in errore) o vengono fermati apposta (spenti, non in errore).
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

    /// <summary>Gli strumenti e lo stato di una card: marcatori, riconoscimento dei processi, tunnel.</summary>
    private sealed class CardTools(RepoCardViewModel card)
    {
        public RepoCardViewModel Card { get; } = card;

        public BackendTools? Backend { get; set; }

        public string ServiceMarker { get; set; } = string.Empty;

        public Func<ProcessRow, bool> IsService { get; set; } = _ => false;

        public string MetroMarker { get; set; } = string.Empty;

        public Func<ProcessRow, bool> IsMetro { get; set; } = _ => false;

        /// <summary>Backend o frontend predefinito di un ambiente Docker: gira in un container del compose.</summary>
        public bool IsContainer { get; set; }

        /// <summary>Il servizio compose della card container (api, web).</summary>
        public string Service { get; set; } = string.Empty;

        public bool IsWeb { get; set; }

        /// <summary>La card ha la sezione del tunnel, per la porta del pool del repository.</summary>
        public bool IsPoolTunnel { get; set; }
    }
}

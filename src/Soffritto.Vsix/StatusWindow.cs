using System.Diagnostics;
using Soffritto.Core;
using Soffritto.Core.Configuration;
using Soffritto.Core.Docker;
using Soffritto.Presentation;
using Soffritto.Vsix.UI;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.ToolWindows;
using Microsoft.VisualStudio.Extensibility.UI;
using Microsoft.VisualStudio.RpcContracts.RemoteUI;

namespace Soffritto.Vsix;

[VisualStudioContribution]
internal sealed class StatusWindow : ToolWindow
{
    private readonly StatusViewModel viewModel = new();
    private AppConfiguration configuration = new();

    // Le letture sopravvivono al comando che le ha chieste: si fermano solo con la finestra.
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationToken lifetimeToken;

    // Lo splash resta almeno questo tempo: le letture partono comunque dietro, cosi' quando
    // il pannello compare le sezioni sono gia' popolate invece di riempirsi alla spicciolata.
    private static readonly TimeSpan MinimumSplash = TimeSpan.FromSeconds(3);

    private StatusSink sink = null!;
    private RepoSession session = null!;

    // Kestrel e host del tunnel del clone: lo stato si rilegge dai processi ogni pochi secondi
    // (lettura locale, non tocca git, database o rete), e subito dopo un avvio o uno stop.
    private readonly SemaphoreSlim scanRequested = new(0);
    private BackendTools? tools;
    private string? toolsRoot;
    private IReadOnlyList<ProcessRow> lastScan = [];
    private StatusControl? content;

    // La nota segue il clone letto per ultimo, come gli strumenti di Kestrel.
    private readonly object noteGate = new();
    private EnvironmentNote? note;
    private bool loadedOnce;
    private bool refreshedOnce;
    private bool revealed;
    private DateTimeOffset splashShownAt;

    public StatusWindow()
    {
        this.Title = "Soffritto";
        lifetimeToken = lifetime.Token;
    }

    public override ToolWindowConfiguration ToolWindowConfiguration => new()
    {
        Placement = ToolWindowPlacement.DocumentWell,
    };

    public override Task InitializeAsync(CancellationToken cancellationToken)
    {
        // La configurazione e' quella dell'exe Soffritto: si rilegge a ogni Aggiorna, cosi'
        // ambienti, database protetti e pool cambiati nel setup dell'exe arrivano anche qui.
        ReloadConfiguration();

        sink = new StatusSink(
            viewModel,
            onRefreshStarted: () =>
            {
                refreshedOnce = true;
                _ = RevealAfterMinimumSplashAsync();
            },
            onRootChanged: FollowNote,
            applyCommandFactory: contextName => new AsyncCommand(
                (_, _, token) => session.ApplyMigrationsAsync(contextName, token)),
            copyCommandFactory: (contextName, migrationId, row) => new AsyncCommand(
                (_, _, token) => CopyUpdateCommandAsync(contextName, migrationId, row, token)),
            runCommandFactory: (contextName, migrationId) => new AsyncCommand(
                (_, _, token) => session.UpdateToMigrationAsync(contextName, migrationId, token)),
            copyPortFactory: row => new AsyncCommand(
                (_, _, token) => CopyFeedback.ShowAsync(session.CopyText(row.Url), () => row.Kind, k => row.Kind = k, row.State, token)),
            openPortFactory: row => new AsyncCommand(
                (_, _, _) => Task.Run(() => viewModel.ActivityLine = OpenInBrowser(row.Url))));

        // Una sola sessione per finestra, e il suo gate e' l'unico per le azioni di scrittura.
        session = new RepoSession(
            token => SolutionRootResolver.ResolveAsync(this.Extensibility, token),
            new RepoSessionOptions(
                configuration.ToProbeOptions(),
                ReadsMigrations: true,
                ReadsTunnel: true,
                TunnelGate: null,
                EnvironmentLabel: null,
                RootUnavailableHeadline: "nessuna soluzione aperta",
                RootUnavailableMessage: "Apri la soluzione del backend per leggere lo stato.",
                // Le regole seguono l'ambiente della soluzione aperta, locale o Docker, e la
                // configurazione riletta: porta del pool, guardia dello slot, database spento,
                // database protetto. Fuori dagli ambienti della configurazione non valgono.
                PoolAssignmentOf: root => EnvironmentOf(root) is { } match ? TunnelPool.AssignmentOf(match.Environment.Number, match.Backend.Id) : null,
                DatabaseGuard: SlotGuard,
                DatabaseOffline: () => databaseOffline,
                SchemaLock: root => EnvironmentOf(root)?.Environment.SchemaLockReason),
            sink,
            new ShellPrompt(this.Extensibility),
            new Win32Clipboard(),
            new WriteGate(),
            lifetimeToken);

        viewModel.RefreshCommand = new AsyncCommand((_, _, token) =>
        {
            ReloadConfiguration();
            ShowEnvironment(sink.Root);
            return RefreshAllAsync(sink.Root, token);
        });

        viewModel.PullCommand = new AsyncCommand(
            (_, _, token) => session.PullAsync(token));

        viewModel.FetchCommand = new AsyncCommand(
            (_, _, token) => session.FetchAsync(token));

        viewModel.PruneCommand = new AsyncCommand(
            (_, _, token) => session.PruneBranchesAsync(token));

        viewModel.BranchSelected = branch => _ = session.SwitchBranchAsync(branch, lifetimeToken);

        viewModel.NoteEdited = text =>
        {
            lock (noteGate)
            {
                note?.Edit(text);
            }
        };

        viewModel.KestrelToggleCommand = new AsyncCommand((_, _, token) =>
            DockerEnvironment() is { } docker
                ? ToggleContainerAsync(docker.Number, docker.Slot, token)
                : RunToolAsync(t => viewModel.KestrelCanStop ? t.StopKestrel(lastScan) : t.StartKestrel()));

        viewModel.TunnelSwitchCommand = new AsyncCommand((_, _, token) => ToggleTunnelAsync(token));

        sink.ApplyTunnel(ViewBuilder.Tunnel(null, DateTimeOffset.Now));

        return Task.CompletedTask;
    }

    public override Task<IRemoteUserControl> GetContentAsync(CancellationToken cancellationToken)
    {
        content ??= new StatusControl(viewModel);

        if (!loadedOnce)
        {
            loadedOnce = true;
            splashShownAt = DateTimeOffset.Now;
            _ = Task.Run(() => InitialRefreshAsync(lifetimeToken), CancellationToken.None);
            _ = Task.Run(() => ScanProcessesAsync(lifetimeToken), CancellationToken.None);
        }

        return Task.FromResult<IRemoteUserControl>(content);
    }

    // All'avvio dell'IDE la finestra viene ripristinata prima che la soluzione sia caricata, e
    // il Remote UI non offre un evento "soluzione pronta" comodo da intercettare: si aspetta un
    // secondo e, finche' la soluzione non c'e', si riprova ogni due secondi per un minuto. Poi
    // parte lo stesso refresh completo del pulsante Aggiorna, fetch e tunnel compresi.
    private async Task InitialRefreshAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.Now.AddMinutes(1);
        var delay = TimeSpan.FromSeconds(1);

        viewModel.BeginOperation();

        try
        {
            while (true)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

                if (refreshedOnce)
                {
                    // L'utente ha gia' premuto Aggiorna nel frattempo.
                    return;
                }

                if (await SolutionRootResolver.ResolveAsync(this.Extensibility, cancellationToken).ConfigureAwait(false) is { } root)
                {
                    await RefreshAllAsync(root, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (DateTimeOffset.Now >= deadline)
                {
                    session.ReportFailure("nessuna soluzione aperta", "Apri la soluzione del backend e premi Aggiorna.");
                    return;
                }

                // L'attesa della soluzione esiste solo nella VSIX: il testo resta qui.
                sink.SetActivity("in attesa che la soluzione sia caricata…");
                delay = TimeSpan.FromSeconds(2);
            }
        }
        catch (OperationCanceledException)
        {
            // Finestra chiusa durante l'attesa.
        }
        finally
        {
            viewModel.EndOperation();
        }
    }

    private async Task RevealAfterMinimumSplashAsync()
    {
        if (revealed)
        {
            return;
        }

        revealed = true;

        try
        {
            var remaining = MinimumSplash - (DateTimeOffset.Now - splashShownAt);
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining, lifetimeToken).ConfigureAwait(false);
            }

            viewModel.SplashVisibility = "Collapsed";
            viewModel.MainVisibility = "Visible";
        }
        catch (OperationCanceledException)
        {
            // Finestra chiusa durante lo splash.
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lifetime.Cancel();
            lifetime.Dispose();

            lock (noteGate)
            {
                note?.Dispose();
                note = null;
            }
        }

        base.Dispose(disposing);
    }

    private void ReloadConfiguration()
        => configuration = ConfigurationFiles.TryLoad(out _) ?? configuration;

    private (EnvironmentDefinition Environment, EnvironmentRepository Backend)? EnvironmentOf(string? root)
        => root is null ? null : configuration.BackendEnvironmentOf(root);

    /// <summary>L'ambiente Docker della soluzione aperta col suo slot; null per un locale o fuori configurazione.</summary>
    private (int Number, DockerSlot Slot)? DockerEnvironment()
    {
        if (EnvironmentOf(sink.Root) is not { Environment.Kind: EnvironmentKind.Docker } match)
        {
            return null;
        }

        try
        {
            return (match.Environment.Number, DockerSlot.Of(configuration, match.Environment));
        }
        catch
        {
            return null;
        }
    }

    // Come nell'exe: nel worktree di un ambiente Docker letture delle migrazioni, update, rollback
    // e copia del comando passano solo se il worktree punta al SQL Server del suo slot.
    private string? SlotGuard(string root)
    {
        if (EnvironmentOf(root) is not { Environment.Kind: EnvironmentKind.Docker } match)
        {
            return null;
        }

        try
        {
            var slot = DockerSlot.Of(configuration, match.Environment);
            var probe = match.Backend.Repository is BackendConfiguration backend
                ? AppConfiguration.ToProbeOptions(backend)
                : configuration.ToProbeOptions();
            return DatabaseGuard.Check(root, probe, slot.DatabasePort, slot.DatabaseName);
        }
        catch (Exception ex)
        {
            return $"slot non calcolabile: {ex.Message}";
        }
    }

    // Il database dello slot spento o in avvio: le migrazioni non tentano la connessione. RepoSession
    // chiede il motivo in modo sincrono, quindi si legge l'ultimo docker ps: lo aggiornano il giro
    // dei processi ogni 3 secondi e, prima di ogni refresh completo, UpdateDatabaseOfflineAsync.
    private volatile string? databaseOffline;

    private async Task UpdateDatabaseOfflineAsync(string? root, CancellationToken cancellationToken)
    {
        if (EnvironmentOf(root) is not { Environment.Kind: EnvironmentKind.Docker } match)
        {
            databaseOffline = null;
            return;
        }

        try
        {
            var slot = DockerSlot.Of(configuration, match.Environment);
            databaseOffline = OfflineReason(await DockerStatusReader.ReadAsync(cancellationToken).ConfigureAwait(false), slot);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            databaseOffline = $"stato del database non letto: {ex.Message}";
        }
    }

    private static string? OfflineReason(DockerStatus status, DockerSlot slot) => !status.IsReachable
        ? status.Error
        : status.StateOf(slot.Project, slot.DatabaseService) switch
        {
            ContainerRunState.Running => null,
            ContainerRunState.Starting => "Database in avvio",
            _ => "Database spento: avvia i container dall'exe",
        };

    private async Task RefreshAllAsync(string? root, CancellationToken cancellationToken)
    {
        await UpdateDatabaseOfflineAsync(root, cancellationToken).ConfigureAwait(false);
        await session.RefreshAsync(full: true, cancellationToken).ConfigureAwait(false);
    }

    // Tag dell'intestazione e interruttore Tunnel: dall'ambiente della soluzione aperta.
    private void ShowEnvironment(string? root)
    {
        var match = EnvironmentOf(root);
        viewModel.ShowEnvironment(
            known: match is not null,
            isDocker: match?.Environment.Kind == EnvironmentKind.Docker,
            schemaLock: match?.Environment.SchemaLockReason);
        if (match is { } found)
        {
            viewModel.SetTunnelOn(EnvironmentTunnel.IsOn(found.Environment.Number));
        }
    }

    private async Task ToggleContainerAsync(int number, DockerSlot slot, CancellationToken cancellationToken)
    {
        var service = slot.ApiService;
        if (service is null)
        {
            viewModel.ActivityLine = $"l'ambiente {number} non ha un'api nei container";
            return;
        }

        var stopping = viewModel.KestrelCanStop;
        viewModel.BeginOperation();
        try
        {
            viewModel.ActivityLine = stopping ? $"arresto di {service}…" : $"avvio di {service}…";
            void Log(string line) => viewModel.ActivityLine = StatusText.Progress(line);
            var error = stopping
                ? await DockerEnvironmentProvisioner.StopServiceAsync(configuration, number, service, Log, cancellationToken).ConfigureAwait(false)
                : await DockerEnvironmentProvisioner.StartServiceAsync(configuration, number, service, Log, cancellationToken).ConfigureAwait(false);
            viewModel.ActivityLine = error ?? (stopping ? $"{service} fermato" : $"{service} avviato");
        }
        catch (Exception ex)
        {
            viewModel.ActivityLine = ex.Message;
        }
        finally
        {
            viewModel.EndOperation();
            scanRequested.Release();
        }
    }

    // L'interruttore «Tunnel» dell'ambiente, come nell'exe: acceso reclama un tunnel del pool e da'
    // una porta a ogni repository che ne espone una, spento le rilascia (EnvironmentTunnel).
    private async Task ToggleTunnelAsync(CancellationToken cancellationToken)
    {
        if (EnvironmentOf(sink.Root) is not { } match)
        {
            return;
        }

        var number = match.Environment.Number;
        var on = EnvironmentTunnel.IsOn(number);
        viewModel.TunnelSwitchEnabled = false;
        viewModel.BeginOperation();
        try
        {
            viewModel.ActivityLine = on ? "tunnel: rilascio le porte del pool…" : "tunnel: reclamo un tunnel del pool…";
            void Log(string line) => viewModel.ActivityLine = StatusText.Progress(line);
            var error = on
                ? await EnvironmentTunnel.DisableAsync(configuration, number, Log, cancellationToken).ConfigureAwait(false)
                : await EnvironmentTunnel.EnableAsync(configuration, number, Log, cancellationToken).ConfigureAwait(false);
            viewModel.ActivityLine = error is not null
                ? $"tunnel non {(on ? "spento" : "acceso")}: {error}"
                : on ? "tunnel spento" : "tunnel acceso";
        }
        catch (Exception ex)
        {
            viewModel.ActivityLine = ex.Message;
        }
        finally
        {
            viewModel.SetTunnelOn(EnvironmentTunnel.IsOn(number));
            viewModel.TunnelSwitchEnabled = true;
            viewModel.EndOperation();
        }

        // Come il pulsante Aggiorna: porte e URL nella card del tunnel, stato dell'host.
        await RefreshAllAsync(sink.Root, cancellationToken).ConfigureAwait(false);
    }

    private static string OpenInBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
            return $"aperto nel browser: {url}";
        }
        catch (Exception ex)
        {
            return $"browser non aperto: {ex.Message}";
        }
    }

    private void FollowNote(string root)
    {
        ShowEnvironment(root);
        var key = BackendTools.EnvironmentKey(root, configuration);
        EnvironmentNote? previous;
        EnvironmentNote current;

        lock (noteGate)
        {
            previous = note;
            EnvironmentNote? created = null;
            created = new EnvironmentNote(key, text =>
            {
                // Una nota sostituita da un'altra soluzione aperta non scrive piu' nel pannello.
                if (ReferenceEquals(note, created))
                {
                    viewModel.ShowNote(key, text);
                }
            });
            note = current = created;
        }

        previous?.Dispose();
        viewModel.ShowNote(key, current.Text);
    }

    private async Task ScanProcessesAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var current = ToolsForRoot();
                if (current is not null)
                {
                    // Nel worktree Docker bottone e pallino sono quelli del container dell'api, e
                    // lo stesso docker ps dice se il database dello slot risponde: docker ps
                    // prima, cosi' lo stato di Kestrel non lampeggia nel mezzo.
                    var docker = DockerEnvironment();
                    var status = docker is not null ? await DockerStatusReader.ReadAsync(cancellationToken).ConfigureAwait(false) : null;
                    databaseOffline = docker is { } environment && status is not null ? OfflineReason(status, environment.Slot) : null;

                    lastScan = ProcessTable.Read(configuration.Backend.ApiProcess);
                    viewModel.ApplyTools(current.Evaluate(lastScan, sink.TunnelId, sink.TunnelHostConnected, sink.TunnelReadStamp));
                    if (docker is { Slot.ApiService: { } service } d && status is not null)
                    {
                        viewModel.ApplyContainer(service, status.IsReachable ? status.StateOf(d.Slot.Project, service) : null, status.Error);
                    }
                }
            }
            catch
            {
                // WMI non disponibile per un giro: lo stato resta com'era e si riprova al successivo.
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

    // Gli strumenti seguono il clone letto per ultimo: con un'altra soluzione aperta cambiano.
    private BackendTools? ToolsForRoot()
    {
        var root = sink.Root;
        if (root is null)
        {
            return null;
        }

        if (tools is null || !string.Equals(toolsRoot, root, StringComparison.OrdinalIgnoreCase))
        {
            tools = new BackendTools(root, configuration.Backend, BackendTools.EnvironmentKey(root, configuration));
            toolsRoot = root;
        }

        return tools;
    }

    private Task RunToolAsync(Func<BackendTools, string> action) => Task.Run(() =>
    {
        try
        {
            var current = ToolsForRoot() ?? throw new InvalidOperationException("Nessun clone letto: aggiorna il pannello.");
            viewModel.ActivityLine = action(current);
        }
        catch (Exception ex)
        {
            viewModel.ActivityLine = ex.Message;
        }
        finally
        {
            scanRequested.Release();
        }
    });

    private async Task CopyUpdateCommandAsync(
        string contextName,
        string migrationId,
        RecentMigrationRow row,
        CancellationToken cancellationToken)
    {
        var copied = await session
            .CopyUpdateCommandAsync(contextName, migrationId, cancellationToken)
            .ConfigureAwait(false);

        if (copied is bool result)
        {
            await CopyFeedback.ShowAsync(result, () => row.Kind, k => row.Kind = k, row.State, cancellationToken).ConfigureAwait(false);
        }
    }
}

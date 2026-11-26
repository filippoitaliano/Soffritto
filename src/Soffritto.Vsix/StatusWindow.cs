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

    // Reads outlive the command that requested them: they stop only with the window.
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationToken lifetimeToken;

    // The splash stays for at least this long: the reads start behind it anyway, so when
    // the panel appears the sections are already populated instead of filling in piecemeal.
    private static readonly TimeSpan MinimumSplash = TimeSpan.FromSeconds(3);

    private StatusSink sink = null!;
    private RepoSession session = null!;

    // Kestrel and tunnel host of the clone: the state is reread from the processes every few seconds
    // (a local read, it does not touch git, database or network), and right after a start or stop.
    private readonly SemaphoreSlim scanRequested = new(0);
    private BackendTools? tools;
    private string? toolsRoot;
    private IReadOnlyList<ProcessRow> lastScan = [];
    private StatusControl? content;

    // The note follows the last clone read, like the Kestrel tools.
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
        // The configuration is the Soffritto app's: it is reread on every Refresh, so
        // environments, protected databases and pool changed in the app's setup reach this window too.
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

        // A single session per window, and its gate is the only one for write actions.
        session = new RepoSession(
            token => SolutionRootResolver.ResolveAsync(this.Extensibility, token),
            new RepoSessionOptions(
                configuration.ToProbeOptions(),
                ReadsMigrations: true,
                ReadsTunnel: true,
                TunnelGate: null,
                EnvironmentLabel: null,
                RootUnavailableHeadline: "no solution open",
                RootUnavailableMessage: "Open the backend solution to read its status.",
                // The rules follow the environment of the open solution, local or Docker, and the
                // reloaded configuration: pool port, slot guard, stopped database,
                // protected database. Outside the configured environments they do not apply.
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

    // When the IDE starts, the window is restored before the solution is loaded, and
    // Remote UI offers no convenient "solution ready" event to hook: wait one
    // second and, while there is no solution, retry every two seconds for a minute. Then
    // the same full refresh as the Refresh button runs, fetch and tunnel included.
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
                    // The user has already pressed Refresh in the meantime.
                    return;
                }

                if (await SolutionRootResolver.ResolveAsync(this.Extensibility, cancellationToken).ConfigureAwait(false) is { } root)
                {
                    await RefreshAllAsync(root, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (DateTimeOffset.Now >= deadline)
                {
                    session.ReportFailure("no solution open", "Open the backend solution and press Refresh.");
                    return;
                }

                // Waiting for the solution exists only in the VSIX: the text lives here.
                sink.SetActivity("waiting for the solution to load…");
                delay = TimeSpan.FromSeconds(2);
            }
        }
        catch (OperationCanceledException)
        {
            // Window closed while waiting.
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
            // Window closed during the splash.
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

    /// <summary>The Docker environment of the open solution with its slot; null for a local one or outside the configuration.</summary>
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

    // As in the app: in the worktree of a Docker environment, migration reads, update, rollback
    // and command copy go through only if the worktree points to its slot's SQL Server.
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
            return $"slot cannot be computed: {ex.Message}";
        }
    }

    // The slot database is stopped or starting: migrations do not attempt the connection. RepoSession
    // asks for the reason synchronously, so the last docker ps is read: it is updated by the process
    // loop every 3 seconds and, before every full refresh, by UpdateDatabaseOfflineAsync.
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
            databaseOffline = $"database state not read: {ex.Message}";
        }
    }

    private static string? OfflineReason(DockerStatus status, DockerSlot slot) => !status.IsReachable
        ? status.Error
        : status.StateOf(slot.Project, slot.DatabaseService) switch
        {
            ContainerRunState.Running => null,
            ContainerRunState.Starting => "Database starting",
            _ => "Database stopped: start the containers from the app",
        };

    private async Task RefreshAllAsync(string? root, CancellationToken cancellationToken)
    {
        await UpdateDatabaseOfflineAsync(root, cancellationToken).ConfigureAwait(false);
        await session.RefreshAsync(full: true, cancellationToken).ConfigureAwait(false);
    }

    // Header tag and Tunnel switch: from the environment of the open solution.
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
            viewModel.ActivityLine = $"environment {number} has no api among its containers";
            return;
        }

        var stopping = viewModel.KestrelCanStop;
        viewModel.BeginOperation();
        try
        {
            viewModel.ActivityLine = stopping ? $"stopping {service}…" : $"starting {service}…";
            void Log(string line) => viewModel.ActivityLine = StatusText.Progress(line);
            var error = stopping
                ? await DockerEnvironmentProvisioner.StopServiceAsync(configuration, number, service, Log, cancellationToken).ConfigureAwait(false)
                : await DockerEnvironmentProvisioner.StartServiceAsync(configuration, number, service, Log, cancellationToken).ConfigureAwait(false);
            viewModel.ActivityLine = error ?? (stopping ? $"{service} stopped" : $"{service} started");
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

    // The environment's “Tunnel” switch, as in the app: on claims a pool tunnel and gives
    // a port to every repository that exposes one, off releases them (EnvironmentTunnel).
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
            viewModel.ActivityLine = on ? "tunnel: releasing the pool ports…" : "tunnel: claiming a pool tunnel…";
            void Log(string line) => viewModel.ActivityLine = StatusText.Progress(line);
            var error = on
                ? await EnvironmentTunnel.DisableAsync(configuration, number, Log, cancellationToken).ConfigureAwait(false)
                : await EnvironmentTunnel.EnableAsync(configuration, number, Log, cancellationToken).ConfigureAwait(false);
            viewModel.ActivityLine = error is not null
                ? $"tunnel not turned {(on ? "off" : "on")}: {error}"
                : on ? "tunnel turned off" : "tunnel turned on";
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

        // Like the Refresh button: ports and URLs in the tunnel card, host state.
        await RefreshAllAsync(sink.Root, cancellationToken).ConfigureAwait(false);
    }

    private static string OpenInBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
            return $"opened in the browser: {url}";
        }
        catch (Exception ex)
        {
            return $"browser not opened: {ex.Message}";
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
                // A note replaced by another opened solution no longer writes to the panel.
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
                    // In the Docker worktree the button and the dot belong to the api container, and
                    // the same docker ps says whether the slot database responds: docker ps
                    // first, so the Kestrel state does not flicker in between.
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
                // WMI unavailable for one round: the state stays as it was and the next round retries.
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

    // The tools follow the last clone read: they change when another solution is opened.
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
            var current = ToolsForRoot() ?? throw new InvalidOperationException("No clone read: refresh the panel.");
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

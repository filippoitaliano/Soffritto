using Soffritto.Desktop.Infrastructure;
using Soffritto.Desktop.ViewModels;
using Soffritto.Presentation;

namespace Soffritto.Desktop;

/// <summary>
/// Shows a session on an app card. The session calls from thread-pool threads: every write
/// to the ViewModels goes through the UI thread.
/// </summary>
public sealed class WpfRepoSink(
    EnvironmentViewModel environment,
    RepoCardViewModel card,
    BackendViewModel? backend,
    TunnelViewModel? tunnel,
    CancellationToken lifetime) : IStatusSink
{
    /// <summary>The session being shown: the rows derive their copy and database update commands from it.</summary>
    public RepoSession? Session { get; set; }

    // Busy comes from the repository's WriteGate (see EnvironmentController), which also carries it
    // to the backend sections: there is nothing to write here.
    public void SetBusy(bool busy)
    {
    }

    public void BeginRefresh()
    {
    }

    public void SetRepoLoading(RepoLoadingView view) => UiThread.Run(() =>
    {
        card.RepoLine = view.RepoLine;
        card.RepoToolTip = view.RepoToolTip;
        card.RepoStamp = view.Stamp;
    });

    public void ApplyGit(GitView view) => UiThread.Run(() =>
    {
        card.RepoLine = view.RepoLine;
        card.RepoDetail = view.RepoDetail;
        card.UpstreamWarning = view.UpstreamWarning;
        card.RepoToolTip = view.RepoToolTip;
        card.RepoStamp = view.Stamp;
        card.GitActionsVisibility = Visibility(view.GitActionsVisible);
        card.PullToolTip = view.PullToolTip;
        card.BranchPicker.SetBranches(view.CurrentBranch, view.Branches);
        environment.RefreshAllOnDev();
    });

    public void SetMigrationsLoading() => UiThread.Run(() =>
    {
        foreach (var row in backend?.Contexts ?? [])
        {
            row.Stamp = StatusText.Reading;
        }
    });

    public void ApplyMigrations(MigrationsView view) => UiThread.Run(() =>
    {
        if (backend is null)
        {
            return;
        }

        backend.Contexts.Clear();
        backend.Unknown.Clear();

        foreach (var context in view.Contexts)
        {
            var row = new ContextRowViewModel(context.Name, context.IsUpToDate, context.StatusColor)
            {
                Note = context.Note,
                Stamp = context.Stamp,
                Action = backend.MigrationsAction,
                IsNotBusy = backend.IsNotBusy,
                MissingCount = context.Rows.Count(r => r.State == MigrationLabels.Missing),
                OutOfOrderCount = context.Rows.Count(r => r.State == MigrationLabels.OutOfOrder),
            };

            var recentRows = new List<RecentMigrationRowViewModel>(context.Rows.Count);
            foreach (var migration in context.Rows)
            {
                recentRows.Add(new RecentMigrationRowViewModel(migration.Position, migration.Name, migration.State)
                {
                    RunToolTip = migration.RunToolTip,
                    RunGlyph = migration.RunGlyph,
                    CanRun = migration.CanRun,
                    RunCommand = UpdateToCommand(context.Name, migration.MigrationId),
                });
            }

            // Migrations not read (database off or unreachable): the context status stays, without a grid.
            row.SetRecent(context.Rows.All(r => r.State == MigrationLabels.NotRead) ? [] : recentRows);
            backend.Contexts.Add(row);
        }

        foreach (var migrationId in view.Unknown)
        {
            backend.Unknown.Add(new MigrationRowViewModel(migrationId, MigrationLabels.Unknown));
        }

        backend.UnknownHeader = view.UnknownHeader;
        backend.UnknownVisibility = Visibility(view.UnknownVisible);

        card.Server = view.Server;
        card.Database = view.Database;
    });

    public void FailMigrations() => UiThread.Run(() =>
    {
        foreach (var row in backend?.Contexts ?? [])
        {
            row.Stamp = StatusText.ReadFailed;
        }
    });

    public void SetMigrationsAction(string? action) => UiThread.Run(() =>
    {
        if (backend is not null)
        {
            backend.MigrationsAction = action;
        }
    });

    public void SetTunnelQueued() => UiThread.Run(() =>
    {
        if (tunnel is not null)
        {
            tunnel.Stamp = StatusText.Queued;
        }
    });

    public void SetTunnelLoading() => UiThread.Run(() =>
    {
        if (tunnel is not null)
        {
            tunnel.Stamp = StatusText.Reading;
        }
    });

    public void ApplyTunnel(TunnelView view) => UiThread.Run(() =>
    {
        if (tunnel is null)
        {
            return;
        }

        tunnel.Ports.Clear();
        tunnel.List.Clear();

        tunnel.Line = view.Line;
        tunnel.Detail = view.Detail;
        tunnel.DetailVisibility = Visibility(view.DetailVisible);
        tunnel.Stamp = view.Stamp;
        tunnel.Id = view.TunnelId;
        tunnel.HostConnected = view.HostConnected;
        tunnel.ReadStamp = view.Stamp;

        foreach (var port in view.Ports)
        {
            var row = new TunnelPortRowViewModel(port.PortNumber, port.Url, port.Reachable)
            {
                CopyToolTip = port.CopyToolTip,
                OpenToolTip = port.CanCopy
                    ? $"Opens {port.Url} in the default browser"
                    : port.CopyToolTip,
            };

            if (port.CanCopy)
            {
                row.CopyCommand = CopyPortCommand(row);
                row.OpenCommand = OpenPortCommand(row);
            }

            tunnel.Ports.Add(row);
        }

        foreach (var summary in view.Available)
        {
            tunnel.List.Add(new TunnelSummaryRowViewModel(summary.Title, summary.Kind, summary.StatusColor));
        }

        tunnel.PortsVisibility = Visibility(tunnel.Ports.Count > 0);
        tunnel.ListVisibility = Visibility(tunnel.List.Count > 0);
    });

    public void SetActivity(string line) => UiThread.Run(() => environment.ActivityLine = line);

    public void SetErrorLine(string errorLine) => UiThread.Run(() =>
    {
        card.ErrorLine = errorLine;
        card.ErrorVisibility = Visibility(errorLine.Length > 0);
    });

    public void BeginOperation() => UiThread.Run(environment.BeginOperation);

    public void EndOperation() => UiThread.Run(environment.EndOperation);

    public void RestoreBranchSelection()
        => UiThread.Run(() => card.BranchPicker.SelectedBranch = card.BranchPicker.CurrentBranch);

    private AsyncCommand UpdateToCommand(string contextName, string migrationId)
        => new(() => Session?.UpdateToMigrationAsync(contextName, migrationId, lifetime) ?? Task.CompletedTask);

    private AsyncCommand CopyPortCommand(TunnelPortRowViewModel row)
        => new(() => Session is null
            ? Task.CompletedTask
            : CopyFeedback.ShowAsync(Session.CopyText(row.Url), () => row.Kind, k => UiThread.Run(() => row.Kind = k), row.State, lifetime));

    private AsyncCommand OpenPortCommand(TunnelPortRowViewModel row)
        => new(() => Task.Run(() =>
        {
            var notice = Browser.Open(new Uri(row.Url));
            UiThread.Run(() => environment.ActivityLine = notice);
        }));

    private static string Visibility(bool visible) => visible ? "Visible" : "Collapsed";
}

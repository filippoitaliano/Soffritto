using Soffritto.Presentation;
using Soffritto.Vsix.UI;
using Microsoft.VisualStudio.Extensibility.UI;

namespace Soffritto.Vsix;

// Each section is applied to the ViewModel as soon as its read finishes: migrations do not
// wait for the git fetch or the dev tunnel resolution.
internal sealed class StatusSink : IStatusSink
{
    private readonly StatusViewModel vm;
    private readonly Action onRefreshStarted;
    private readonly Action<string> onRootChanged;
    private readonly Func<string, AsyncCommand> applyCommandFactory;
    private readonly Func<string, string, RecentMigrationRow, AsyncCommand> copyCommandFactory;
    private readonly Func<string, string, AsyncCommand> runCommandFactory;
    private readonly Func<TunnelPortRow, AsyncCommand> copyPortFactory;
    private readonly Func<TunnelPortRow, AsyncCommand> openPortFactory;
    private string? migrationsAction;

    /// <param name="onRootChanged">The clone being read has changed (first read or another solution opened): the environment note follows it.</param>
    /// <param name="applyCommandFactory">
    /// Factory of the "apply migrations" command for a context. The sink does not know how the action
    /// runs: the view only says WHETHER the button is allowed.
    /// </param>
    /// <param name="copyCommandFactory">
    /// Factory of the "copy the update command" command for (context, migration, row). The row
    /// lets the executor write the copy feedback into it.
    /// </param>
    /// <param name="runCommandFactory">
    /// Factory of the "run the update up to this migration" command for (context, migration).
    /// </param>
    /// <param name="copyPortFactory">
    /// Factory of the "copy the URL" command for a port row: each port has its own icon,
    /// because the tunnel may expose more ports than the clone's, and the choice is yours.
    /// </param>
    /// <param name="openPortFactory">Factory of the "open in browser" command for a port row.</param>
    public StatusSink(
        StatusViewModel vm,
        Action onRefreshStarted,
        Action<string> onRootChanged,
        Func<string, AsyncCommand> applyCommandFactory,
        Func<string, string, RecentMigrationRow, AsyncCommand> copyCommandFactory,
        Func<string, string, AsyncCommand> runCommandFactory,
        Func<TunnelPortRow, AsyncCommand> copyPortFactory,
        Func<TunnelPortRow, AsyncCommand> openPortFactory)
    {
        this.openPortFactory = openPortFactory;
        this.vm = vm;
        this.onRefreshStarted = onRefreshStarted;
        this.onRootChanged = onRootChanged;
        this.applyCommandFactory = applyCommandFactory;
        this.copyCommandFactory = copyCommandFactory;
        this.runCommandFactory = runCommandFactory;
        this.copyPortFactory = copyPortFactory;
    }

    /// <summary>Clone root from the last read: used by the Kestrel tools.</summary>
    public string? Root { get; private set; }

    /// <summary>Tunnel id from the last successful read; null if not read or not found.</summary>
    public string? TunnelId { get; private set; }

    /// <summary>Whether the host was connected at the last tunnel read (CLI or Visual Studio).</summary>
    public bool TunnelHostConnected { get; private set; }

    public string TunnelReadStamp { get; private set; } = string.Empty;

    public void BeginRefresh() => onRefreshStarted();

    public void SetRepoLoading(RepoLoadingView view)
    {
        if (!string.Equals(Root, view.RepoToolTip, StringComparison.OrdinalIgnoreCase))
        {
            Root = view.RepoToolTip;
            onRootChanged(Root);
        }

        vm.Clone = view.CloneName;
        vm.RepoLine = view.RepoLine;
        vm.RepoToolTip = view.RepoToolTip;
        vm.RepoStamp = view.Stamp;
    }

    public void ApplyGit(GitView view)
    {
        vm.Branch = view.Branch;
        vm.RepoLine = view.RepoLine;
        vm.RepoDetail = view.RepoDetail;
        vm.UpstreamWarning = view.UpstreamWarning;
        vm.UpstreamWarningVisibility = Visibility(view.UpstreamWarning.Length > 0);
        vm.RepoToolTip = view.RepoToolTip;
        vm.RepoStamp = view.Stamp;
        vm.GitActionsVisibility = Visibility(view.GitActionsVisible);
        vm.PullToolTip = view.PullToolTip;
        vm.SetBranches(view.CurrentBranch, view.Branches);
    }

    public void SetMigrationsLoading()
    {
        foreach (var row in vm.Contexts)
        {
            row.Stamp = StatusText.Reading;
        }
    }

    public void ApplyMigrations(MigrationsView view)
    {
        vm.Contexts.Clear();
        vm.Unknown.Clear();

        foreach (var context in view.Contexts)
        {
            var row = new ContextRow(context.Name, context.Counters, context.IsUpToDate, context.StatusColor);

            var recentRows = new List<RecentMigrationRow>(context.Rows.Count);
            foreach (var migration in context.Rows)
            {
                var recent = new RecentMigrationRow(migration.DisplayName, migration.State, migration.Position, migration.Name)
                {
                    CopyToolTip = migration.CopyToolTip,
                    RunToolTip = migration.RunToolTip,
                    RunGlyph = migration.RunGlyph,
                    CanRun = migration.CanRun,
                    RunCommand = runCommandFactory(context.Name, migration.MigrationId),
                };

                recent.CopyCommand = copyCommandFactory(context.Name, migration.MigrationId, recent);
                recentRows.Add(recent);
            }

            row.SetRecent(recentRows);

            row.Note = context.Note;
            row.NoteVisibility = Visibility(context.Note.Length > 0);

            if (context.CanApply)
            {
                row.ApplyCommand = applyCommandFactory(context.Name);
                row.ApplyVisibility = "Visible";
                row.ApplyToolTip = context.ApplyToolTip;
            }

            row.Stamp = context.Stamp;
            row.Action = migrationsAction;
            row.IsNotBusy = vm.IsNotBusy;
            vm.Contexts.Add(row);
        }

        foreach (var migrationId in view.Unknown)
        {
            vm.Unknown.Add(new MigrationRow(migrationId, MigrationLabels.Unknown));
        }

        vm.UnknownHeader = view.UnknownHeader;
        vm.UnknownVisibility = Visibility(view.UnknownVisible);

        vm.Server = view.Server;
        vm.Database = view.Database;
    }

    public void FailMigrations()
    {
        foreach (var row in vm.Contexts)
        {
            row.Stamp = StatusText.ReadFailed;
        }
    }

    public void SetMigrationsAction(string? action)
    {
        migrationsAction = action;
        foreach (var row in vm.Contexts)
        {
            row.Action = action;
        }
    }

    public void SetTunnelQueued() => vm.TunnelStamp = StatusText.Queued;

    public void SetTunnelLoading() => vm.TunnelStamp = StatusText.Reading;

    public void ApplyTunnel(TunnelView view)
    {
        vm.TunnelPorts.Clear();
        vm.TunnelList.Clear();

        vm.TunnelLine = view.Line;
        vm.TunnelDetail = view.Detail;
        vm.TunnelDetailVisibility = Visibility(view.DetailVisible);
        vm.TunnelStamp = view.Stamp;
        TunnelId = view.TunnelId;
        TunnelHostConnected = view.HostConnected;
        TunnelReadStamp = view.Stamp;

        foreach (var port in view.Ports)
        {
            var row = new TunnelPortRow(port.Endpoint, port.Url, port.State, port.PortNumber)
            {
                CopyToolTip = port.CopyToolTip,
                OpenToolTip = port.CanCopy ? $"Opens {port.Url} in the default browser" : port.CopyToolTip,
            };

            if (port.CanCopy)
            {
                row.CopyCommand = copyPortFactory(row);
                row.OpenCommand = openPortFactory(row);
            }

            vm.TunnelPorts.Add(row);
        }

        foreach (var summary in view.Available)
        {
            vm.TunnelList.Add(new TunnelSummaryRow(summary.Title, summary.Kind, summary.StatusColor));
        }

        vm.TunnelPortsVisibility = Visibility(vm.TunnelPorts.Count > 0);
        vm.TunnelListVisibility = Visibility(vm.TunnelList.Count > 0);
    }

    public void SetActivity(string line) => vm.ActivityLine = line;

    public void SetErrorLine(string errorLine)
    {
        vm.ErrorLine = errorLine;
        vm.ErrorVisibility = Visibility(errorLine.Length > 0);
    }

    public void SetBusy(bool busy)
    {
        vm.IsNotBusy = !busy;
        foreach (var row in vm.Contexts)
        {
            row.IsNotBusy = !busy;
        }
    }

    public void BeginOperation() => vm.BeginOperation();

    public void EndOperation() => vm.EndOperation();

    public void RestoreBranchSelection() => vm.SelectedBranch = vm.CurrentBranch;

    private static string Visibility(bool visible) => visible ? "Visible" : "Collapsed";
}

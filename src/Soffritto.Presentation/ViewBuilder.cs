using Soffritto.Core;

namespace Soffritto.Presentation;

/// <summary>Turns the Core reports into the panel views, without knowing which host shows them.</summary>
public static class ViewBuilder
{
    // During the reread everything stays as it was and only the timestamp says what is happening:
    // the panel must not jump up and down on every refresh.
    public static RepoLoadingView RepoLoading(string root, bool fetch) => new(
        StatusText.WorkingFolder(root),
        StatusText.WorkingFolder(root),
        root,
        fetch ? StatusText.Fetching : StatusText.Reading);

    public static GitView Git(GitStatus git, string root, DateTimeOffset now)
    {
        var folder = StatusText.WorkingFolder(root);
        var problems = new List<string>();

        if (git.Error is not null)
        {
            problems.Add($"git: {git.Error}");
        }

        if (git.FetchError is not null)
        {
            problems.Add($"git fetch: {git.FetchError}");
        }

        return new GitView(
            CloneName: folder,
            Branch: StatusText.BuildBranch(git),
            RepoLine: git.Error is not null ? $"{folder} · git unavailable" : folder,
            RepoDetail: git.Error ?? StatusText.BuildRepoDetail(git),
            UpstreamWarning: StatusText.BuildUpstreamWarning(git),
            RepoToolTip: root,
            Stamp: StatusText.Stamp(now),
            GitActionsVisible: git.Error is null,
            PullToolTip: StatusText.PullToolTip(git),
            CurrentBranch: git.Error is null ? git.Branch : null,
            Branches: git.AllBranches,
            Problems: string.Join(Environment.NewLine, problems));
    }

    /// <param name="schemaLock">The reason the schema must not be touched (protected database): no update buttons, and the note says so.</param>
    public static MigrationsView Migrations(StatusReport report, DateTimeOffset now, string? schemaLock = null)
    {
        var contexts = new List<ContextView>();

        // With the database unreachable the history is empty: every migration would show up as
        // “missing” and the button would offer to apply them all. “not read” is shown instead.
        // A database known to be stopped or starting is not an error: the reason goes in the note. A
        // database reached without the history table really does have everything to apply.
        var unread = (report.DatabaseError is not null && !report.DatabaseReached) || report.DatabaseOffline is not null;

        foreach (var context in report.Contexts)
        {
            // Entries is in ascending ID order: the last ones are the most recent, and the newest
            // goes on top because it is the one you come back to most often. The number before
            // the name is the absolute position in the context (1 = the first migration): it makes
            // the descending order readable and matches the "applied/total" counter.
            // All rows are built: the host shows one page at a time.
            var total = context.Entries.Count;
            var rows = context.Entries
                .Reverse()
                .Select((entry, offset) =>
                {
                    var plan = context.PlanUpdateTo(entry.MigrationId)!;
                    return new MigrationRowView(
                        entry.MigrationId,
                        $"{total - offset,3}  {entry.DisplayName}",
                        unread ? MigrationLabels.NotRead : StatusText.StateLabel(entry.State),
                        schemaLock is null ? StatusText.CopyUpdateToolTip(entry.MigrationId) : StatusText.ProtectedDatabaseToolTip,
                        schemaLock is null ? StatusText.RunUpdateToolTip(plan) : StatusText.ProtectedDatabaseToolTip,
                        StatusText.RunUpdateGlyph(plan),
                        CanRun: schemaLock is null && !unread && !plan.IsNoOp,
                        Position: total - offset,
                        Name: entry.DisplayName);
                })
                .ToList();

            var notes = new List<string>();

            if (schemaLock is not null)
            {
                notes.Add(StatusText.ProtectedDatabaseNote);
            }

            if (unread)
            {
                notes.Add(report.DatabaseOffline is { } offline
                    ? $"{offline}: migrations not read."
                    : "Database unreachable: migrations not read.");
            }

            var mismatches = context.FileNameMismatches;
            if (mismatches.Count > 0)
            {
                notes.Add($"{mismatches.Count} migration(s) with a file name different from the [Migration] attribute.");
            }

            if (context.OutOfOrder.Count > 0)
            {
                notes.Add("Automatic update not offered: there are out-of-order migrations.");
            }

            contexts.Add(new ContextView(
                Name: context.ContextName,
                Counters: unread ? $"?/{context.Entries.Count}" : $"{context.AppliedCount}/{context.Entries.Count}",
                IsUpToDate: !unread && context.IsUpToDate,
                StatusColor: unread ? StatusBadge.OffColor : context.IsUpToDate ? StatusBadge.OkColor : StatusBadge.WarningColor,
                Note: string.Join(" ", notes),
                CanApply: schemaLock is null && !unread && context.CanApplyPending,
                ApplyToolTip: schemaLock is null && !unread && context.CanApplyPending
                    ? StatusText.ApplyToolTip(context.Pending.Count, report.Database)
                    : string.Empty,
                Rows: rows,
                Stamp: StatusText.Stamp(now)));
        }

        var problems = new List<string>();

        if (report.ConfigurationError is not null)
        {
            problems.Add(report.ConfigurationError);
        }

        if (report.DatabaseError is not null)
        {
            problems.Add(report.DatabaseError);
        }

        return new MigrationsView(
            Server: report.Server ?? StatusText.Unresolved,
            Database: report.Database ?? StatusText.Unresolved,
            DatabaseLine: (report.Server is null
                ? "database unresolved"
                : $"{report.Server} · {report.Database}") + (schemaLock is null ? string.Empty : $" · {StatusText.ProtectedDatabaseTag}"),
            Contexts: contexts,
            Unknown: report.Unknown.Select(e => e.MigrationId).ToList(),
            UnknownHeader: $"Applied but unknown to the code ({report.Unknown.Count})",
            UnknownVisible: report.Unknown.Count > 0,
            Problems: string.Join(Environment.NewLine, problems));
    }

    public static TunnelView Tunnel(DevTunnelStatus? tunnel, DateTimeOffset now)
    {
        if (tunnel is null)
        {
            return new TunnelView(
                "dev tunnel not read",
                "Refresh to query the devtunnel CLI.",
                DetailVisible: true,
                Ports: [],
                Available: [],
                Stamp: StatusText.NotRead);
        }

        if (!tunnel.Shared)
        {
            return new TunnelView(
                "not shared",
                tunnel.Error ?? string.Empty,
                DetailVisible: true,
                Ports: [],
                Available: [],
                Stamp: StatusText.Stamp(now));
        }

        if (tunnel.Error is not null)
        {
            // No match with the clone: instead of the detail, show what is there,
            // so you can see at a glance which tunnel is on and what it is called.
            var available = tunnel.AllAvailable
                .Select(summary => new TunnelSummaryView(
                    summary.Description.Length > 0
                        ? $"{summary.TunnelId} · {summary.Description}"
                        : $"{summary.TunnelId} · (no description)",
                    $"{(summary.IsHostConnected ? "host connected" : "host not connected")} · {summary.PortCount} ports",
                    summary.IsHostConnected ? StatusBadge.OkColor : StatusBadge.ErrorColor))
                .ToList();

            return new TunnelView(
                tunnel.TunnelId ?? "dev tunnel unavailable",
                tunnel.AllAvailable.Count > 0 ? $"{tunnel.Error} Account tunnels:" : tunnel.Error,
                DetailVisible: true,
                Ports: [],
                Available: available,
                Stamp: StatusText.Stamp(now));
        }

        var ports = tunnel.AllPorts
            .Select(port =>
            {
                var kind = port.Reachable switch
                {
                    true => "reachable",
                    false => "no response",
                    null => string.Empty,
                };

                var url = port.PortUri.TrimEnd('/');
                return new TunnelPortView(
                    $"{port.PortNumber} · {port.PortUri}",
                    url,
                    kind,
                    StatusText.CopyUrlToolTip(url),
                    CanCopy: url.Length > 0,
                    PortNumber: port.PortNumber,
                    Reachable: port.Reachable);
            })
            .ToList();

        // The dot says whether the host is connected, the ports can be counted at a glance: no detail.
        return new TunnelView(
            tunnel.TunnelId ?? string.Empty,
            string.Empty,
            DetailVisible: false,
            Ports: ports,
            Available: [],
            Stamp: StatusText.Stamp(now),
            TunnelId: tunnel.TunnelId,
            HostConnected: tunnel.IsHostConnected);
    }
}

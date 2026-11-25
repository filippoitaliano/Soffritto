using Soffritto.Core;

namespace Soffritto.Presentation;

/// <summary>Trasforma i report di Core nelle viste del pannello, senza sapere quale host le mostra.</summary>
public static class ViewBuilder
{
    // Durante la rilettura tutto resta com'era e solo il timestamp dice cosa si sta facendo:
    // il pannello non deve scattare su e giu' a ogni refresh.
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
            RepoLine: git.Error is not null ? $"{folder} · git non disponibile" : folder,
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

    /// <param name="schemaLock">Il motivo per cui lo schema non si tocca (database protetto): niente pulsanti di update, la nota lo dice.</param>
    public static MigrationsView Migrations(StatusReport report, DateTimeOffset now, string? schemaLock = null)
    {
        var contexts = new List<ContextView>();

        // Con il database irraggiungibile la history e' vuota: ogni migrazione risulterebbe
        // «mancante» e il pulsante proporrebbe di applicarle tutte. Si mostra «non letta».
        // Un database noto come spento o in avvio non e' un errore: il motivo va nella nota. Un
        // database raggiunto senza la tabella della history ha davvero tutto da applicare.
        var unread = (report.DatabaseError is not null && !report.DatabaseReached) || report.DatabaseOffline is not null;

        foreach (var context in report.Contexts)
        {
            // Entries e' in ordine crescente di ID: le ultime sono le piu' recenti, e in cima
            // va la piu' nuova perche' e' quella a cui si torna piu' spesso. Il numero davanti
            // al nome e' la posizione assoluta nel context (1 = la prima migrazione): rende
            // leggibile l'ordine decrescente e combacia con il contatore "applicate/totali".
            // Le righe si costruiscono tutte: l'host ne mostra una pagina alla volta.
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
                    ? $"{offline}: migrazioni non lette."
                    : "Database non raggiungibile: migrazioni non lette.");
            }

            var mismatches = context.FileNameMismatches;
            if (mismatches.Count > 0)
            {
                notes.Add($"{mismatches.Count} migrazione/i con nome file diverso dall'attributo [Migration].");
            }

            if (context.OutOfOrder.Count > 0)
            {
                notes.Add("Aggiornamento automatico non proposto: ci sono migrazioni fuori ordine.");
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
                ? "database non risolto"
                : $"{report.Server} · {report.Database}") + (schemaLock is null ? string.Empty : $" · {StatusText.ProtectedDatabaseTag}"),
            Contexts: contexts,
            Unknown: report.Unknown.Select(e => e.MigrationId).ToList(),
            UnknownHeader: $"Applicate ma sconosciute al codice ({report.Unknown.Count})",
            UnknownVisible: report.Unknown.Count > 0,
            Problems: string.Join(Environment.NewLine, problems));
    }

    public static TunnelView Tunnel(DevTunnelStatus? tunnel, DateTimeOffset now)
    {
        if (tunnel is null)
        {
            return new TunnelView(
                "dev tunnel non letto",
                "Aggiorna per interrogare la CLI devtunnel.",
                DetailVisible: true,
                Ports: [],
                Available: [],
                Stamp: StatusText.NotRead);
        }

        if (!tunnel.Shared)
        {
            return new TunnelView(
                "non condiviso",
                tunnel.Error ?? string.Empty,
                DetailVisible: true,
                Ports: [],
                Available: [],
                Stamp: StatusText.Stamp(now));
        }

        if (tunnel.Error is not null)
        {
            // Nessuna corrispondenza col clone: al posto del dettaglio si mostra cosa c'e',
            // cosi' si vede a colpo d'occhio quale tunnel e' acceso e come si chiama.
            var available = tunnel.AllAvailable
                .Select(summary => new TunnelSummaryView(
                    summary.Description.Length > 0
                        ? $"{summary.TunnelId} · {summary.Description}"
                        : $"{summary.TunnelId} · (senza descrizione)",
                    $"{(summary.IsHostConnected ? "host connesso" : "host non connesso")} · {summary.PortCount} porte",
                    summary.IsHostConnected ? StatusBadge.OkColor : StatusBadge.ErrorColor))
                .ToList();

            return new TunnelView(
                tunnel.TunnelId ?? "dev tunnel non disponibile",
                tunnel.AllAvailable.Count > 0 ? $"{tunnel.Error} Tunnel dell'account:" : tunnel.Error,
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
                    true => "raggiungibile",
                    false => "nessuna risposta",
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

        // Host connesso o no lo dice il pallino, le porte si contano a vista: nessun dettaglio.
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

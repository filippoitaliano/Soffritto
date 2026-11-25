namespace Soffritto.Presentation;

// Viste indipendenti dall'host: ciascun sink le mappa sui propri ViewModel. Visibilita' come
// bool e colori come stringhe #RRGGBB, che entrambi gli host sanno convertire.

public sealed record RepoLoadingView(string CloneName, string RepoLine, string RepoToolTip, string Stamp);

public sealed record GitView(
    string CloneName,
    string Branch,
    string RepoLine,
    string RepoDetail,
    string UpstreamWarning,
    string RepoToolTip,
    string Stamp,
    bool GitActionsVisible,
    string PullToolTip,
    string? CurrentBranch,
    IReadOnlyList<string> Branches,
    string Problems);

/// <param name="RunGlyph">Glifo Segoe Fluent Icons del lancio: avanti, rollback o niente da fare.</param>
/// <param name="CanRun">Falso se il database e' gia' fermo a questa migrazione.</param>
public sealed record MigrationRowView(
    string MigrationId,
    string DisplayName,
    string State,
    string CopyToolTip,
    string RunToolTip,
    string RunGlyph,
    bool CanRun,
    int Position = 0,
    string Name = "");

public sealed record ContextView(
    string Name,
    string Counters,
    bool IsUpToDate,
    string StatusColor,
    string Note,
    bool CanApply,
    string ApplyToolTip,
    IReadOnlyList<MigrationRowView> Rows,
    string Stamp);

public sealed record MigrationsView(
    string Server,
    string Database,
    string DatabaseLine,
    IReadOnlyList<ContextView> Contexts,
    IReadOnlyList<string> Unknown,
    string UnknownHeader,
    bool UnknownVisible,
    string Problems);

public sealed record TunnelPortView(
    string Endpoint,
    string Url,
    string State,
    string CopyToolTip,
    bool CanCopy,
    int PortNumber = 0,
    bool? Reachable = null);

public sealed record TunnelSummaryView(string Title, string Kind, string StatusColor);

public sealed record TunnelView(
    string Line,
    string Detail,
    bool DetailVisible,
    IReadOnlyList<TunnelPortView> Ports,
    IReadOnlyList<TunnelSummaryView> Available,
    string Stamp,
    string? TunnelId = null,
    bool HostConnected = false);

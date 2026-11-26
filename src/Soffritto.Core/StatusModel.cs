namespace Soffritto.Core;

public enum MigrationState
{
    Applied,

    Pending,

    /// <summary>
    /// Not applied, but with a timestamp earlier than the last applied one: typical of a merge
    /// from a parallel branch. EF would apply it anyway, but the resulting schema
    /// may differ from the one produced by applying them in order.
    /// </summary>
    PendingOutOfOrder,

    /// <summary>
    /// Present in __EFMigrationsHistory but missing from the files of every known context.
    /// </summary>
    Unknown,
}

public sealed record MigrationEntry(
    string MigrationId,
    MigrationState State,
    string? ProductVersion = null,
    string? MismatchedFileName = null)
{
    public string DisplayName => MigrationId.Length > 15 && MigrationId[14] == '_'
        ? MigrationId[15..]
        : MigrationId;

    public string? Timestamp => MigrationId.Length >= 14 && MigrationId[..14].All(char.IsDigit)
        ? MigrationId[..14]
        : null;
}

public sealed record ContextStatus(
    string ContextName,
    string MigrationsDirectory,
    IReadOnlyList<MigrationEntry> Entries)
{
    public int AppliedCount => Entries.Count(e => e.State == MigrationState.Applied);

    public IReadOnlyList<MigrationEntry> Pending => Entries
        .Where(e => e.State is MigrationState.Pending or MigrationState.PendingOutOfOrder)
        .ToList();

    public IReadOnlyList<MigrationEntry> OutOfOrder => Entries
        .Where(e => e.State == MigrationState.PendingOutOfOrder)
        .ToList();

    public string? LastApplied => Entries
        .Where(e => e.State == MigrationState.Applied)
        .Select(e => e.MigrationId)
        .LastOrDefault();

    public bool IsUpToDate => Pending.Count == 0;

    public IReadOnlyList<MigrationEntry> FileNameMismatches => Entries
        .Where(e => e.MismatchedFileName is not null)
        .ToList();

    /// <summary>
    /// The "forward with no surprises" case: there are migrations to apply and ALL of them come after
    /// the last applied one. With even one out of order the automatic update is no longer
    /// trivial (the gap comes from a parallel branch and the resulting schema may diverge),
    /// so the decision goes back to you and the button does not appear.
    /// Unknown migrations do NOT block: they are inert rows in the database and do not stop
    /// EF from applying the pending ones correctly.
    /// </summary>
    public bool CanApplyPending => Pending.Count > 0 && OutOfOrder.Count == 0;

    /// <summary>
    /// What <c>dotnet ef database update</c> does when stopping at <paramref name="targetMigrationId"/>:
    /// like EF, it reverts the applied ones after the target (most recent first) and applies the
    /// unapplied ones up to and including the target, out-of-order ones included. Null if the target is not
    /// among the context's migrations.
    /// </summary>
    public UpdatePlan? PlanUpdateTo(string targetMigrationId)
    {
        var target = Entries.ToList().FindIndex(e => e.MigrationId == targetMigrationId);
        if (target < 0)
        {
            return null;
        }

        var toRevert = Entries
            .Skip(target + 1)
            .Where(e => e.State == MigrationState.Applied)
            .Reverse()
            .ToList();

        var toApply = Entries
            .Take(target + 1)
            .Where(e => e.State is MigrationState.Pending or MigrationState.PendingOutOfOrder)
            .ToList();

        return new UpdatePlan(Entries[target], toApply, toRevert);
    }
}

/// <param name="ToRevert">Most recent first: the order in which EF runs the Down methods.</param>
public sealed record UpdatePlan(
    MigrationEntry Target,
    IReadOnlyList<MigrationEntry> ToApply,
    IReadOnlyList<MigrationEntry> ToRevert)
{
    public bool IsRollback => ToRevert.Count > 0;

    public bool IsNoOp => ToApply.Count == 0 && ToRevert.Count == 0;
}

public sealed record GitStatus(
    string? Branch = null,
    int Ahead = 0,
    int Behind = 0,
    int DirtyFiles = 0,
    bool HasUpstream = false,
    bool IsDetached = false,
    string? FetchError = null,
    string? Error = null,
    IReadOnlyList<string>? Branches = null)
{
    public bool HasPendingPull => Behind > 0;

    /// <summary>
    /// Selectable branch names: local ones plus those present only on the remote, without the
    /// remote prefix, sorted and without duplicates. Empty if git is not available.
    /// </summary>
    public IReadOnlyList<string> AllBranches => Branches ?? [];

    public bool IsClean => DirtyFiles == 0;

    /// <summary>
    /// A trivial pull: there are commits to fetch, none to push, a clean working tree and a
    /// configured upstream. With <c>Ahead > 0</c> the pull would be a merge or a rebase, not a
    /// fast-forward, so it is no longer the "standard conflict-free" operation and the button
    /// does not appear.
    /// </summary>
    public bool CanFastForward =>
        Error is null
        && HasUpstream
        && !IsDetached
        && Behind > 0
        && Ahead == 0
        && IsClean;
}

/// <summary>
/// Local branches whose upstream has disappeared from the remote, as git sees them after pruning
/// the remote-tracking refs.
/// </summary>
public sealed record PrunableBranches(IReadOnlyList<string> Branches, string? Error = null);

public sealed record DevTunnelPort(int PortNumber, string Protocol, string PortUri)
{
    /// <summary>Outcome of the HTTP probe; null if the probe was not run (host not connected).</summary>
    public bool? Reachable { get; init; }
}

/// <summary>A 'devtunnel list' row: just enough to recognize a tunnel and tell whether it is alive.</summary>
public sealed record DevTunnelSummary(string TunnelId, string Description, int HostConnections, int PortCount)
{
    public bool IsHostConnected => HostConnections > 0;
}

public sealed record DevTunnelStatus(
    string? TunnelId = null,
    string? Description = null,
    int HostConnections = 0,
    IReadOnlyList<DevTunnelPort>? Ports = null,
    string? Error = null,
    IReadOnlyList<DevTunnelSummary>? Available = null)
{
    public IReadOnlyList<DevTunnelPort> AllPorts => Ports ?? [];

    /// <summary>
    /// The account's tunnels when none matches the clone: set only together with
    /// <see cref="Error"/>, because once a match is found the tunnel's details are enough.
    /// </summary>
    public IReadOnlyList<DevTunnelSummary> AllAvailable => Available ?? [];

    public bool Shared { get; init; } = true;

    /// <summary>A repository without a pool port: no CLI read.</summary>
    public static DevTunnelStatus NotShared(string message) => new(Error: message) { Shared = false };

    /// <summary>
    /// This is the primary reachability signal, not an HTTP probe: the devtunnels gateway keeps
    /// a request to an unhosted tunnel hanging instead of rejecting it, so without a connected
    /// host the probe can only time out.
    /// </summary>
    public bool IsHostConnected => Error is null && HostConnections > 0;
}

public sealed record StatusReport(
    string SolutionRoot,
    string? Server,
    string? Database,
    IReadOnlyList<ContextStatus> Contexts,
    IReadOnlyList<MigrationEntry> Unknown,
    GitStatus Git,
    DateTimeOffset TimestampLocal,
    string? DatabaseError = null,
    string? ConfigurationError = null,
    DevTunnelStatus? DevTunnel = null,
    string? DatabaseOffline = null,
    bool DatabaseReached = false)
{
    public bool IsEnvironmentUpToDate =>
        DatabaseError is null
        && DatabaseOffline is null
        && ConfigurationError is null
        && Contexts.All(c => c.IsUpToDate)
        && Unknown.Count == 0;
}

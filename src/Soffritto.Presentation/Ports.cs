using Soffritto.Core;
using Soffritto.Core.Docker;

namespace Soffritto.Presentation;

/// <summary>
/// How the host displays the sections. Each section is applied as soon as its read finishes:
/// migrations do not wait for the git fetch or the dev tunnel resolution.
/// </summary>
public interface IStatusSink
{
    /// <summary>Start of a refresh, after the root has been resolved.</summary>
    void BeginRefresh();

    void SetRepoLoading(RepoLoadingView view);

    void ApplyGit(GitView view);

    /// <summary>The context rows stay readable during the reread: only the timestamp changes.</summary>
    void SetMigrationsLoading();

    void ApplyMigrations(MigrationsView view);

    void FailMigrations();

    /// <summary>
    /// Migration action in progress ("dotnet ef database update…"), shown in place of the
    /// contexts' timestamp; null when the action ends, and the timestamp goes back to that of the last read.
    /// </summary>
    void SetMigrationsAction(string? action);

    void SetTunnelQueued();

    /// <summary>The ports from the last read stay visible until the new read arrives.</summary>
    void SetTunnelLoading();

    void ApplyTunnel(TunnelView view);

    void SetActivity(string line);

    void SetErrorLine(string errorLine);

    void SetBusy(bool busy);

    void BeginOperation();

    void EndOperation();

    /// <summary>Puts the drop-down back on the current branch after a switch that did not happen.</summary>
    void RestoreBranchSelection();
}

public interface IUserPrompt
{
    Task<bool> ConfirmAsync(Confirmation confirmation, CancellationToken cancellationToken);
}

public interface IClipboard
{
    bool TrySetText(string text);
}

/// <param name="TunnelGate">
/// Serialises dev tunnel reads across several sessions (null: no serialisation).
/// </param>
/// <param name="EnvironmentLabel">Environment name in confirmations (null: not mentioned).</param>
/// <param name="RootUnavailableHeadline">
/// Error title when the root cannot be resolved during a refresh (null: silent exit).
/// </param>
/// <param name="PoolAssignmentOf">
/// From the root, the pool port assigned to the repository: the card reads that tunnel and only the
/// assigned port, and without an assignment shows “not shared” without querying the CLI
/// (no function: the read by the folder's numeric suffix).
/// </param>
/// <param name="DatabaseGuard">
/// From the root, the error that blocks reading migrations, database update, rollback and copying
/// the command, or null if the database is the right one (null: no guard, for local environments).
/// </param>
/// <param name="SchemaLock">
/// From the root, the reason why database update, rollback and copying the command are refused
/// (protected database), or null if the schema may be changed. Reading migrations stays
/// allowed (null: no lock).
/// </param>
public sealed record RepoSessionOptions(
    ProbeOptions Probe,
    bool ReadsMigrations,
    bool ReadsTunnel,
    SemaphoreSlim? TunnelGate,
    string? EnvironmentLabel,
    string? RootUnavailableHeadline,
    string? RootUnavailableMessage,
    Func<string, PoolAssignment?>? PoolAssignmentOf = null,
    Func<string, string?>? DatabaseGuard = null,
    Func<string?>? DatabaseOffline = null,
    Func<string, string?>? SchemaLock = null);

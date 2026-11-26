using Soffritto.Core;

namespace Soffritto.Presentation;

/// <summary>Panel texts shared between the VSIX and the app: a single source for labels, tooltips and confirmations.</summary>
public static class StatusText
{
    /// <summary>Microcopy shown when no action is in progress: the line stays, so the layout does not move.</summary>
    public const string IdleActivity = "ready";

    public const string Reading = "reading…";
    public const string Fetching = "git fetch…";
    public const string Queued = "queued…";
    public const string NotRead = "not read";
    public const string NotYetRead = "not read yet";
    public const string WaitingFirstRead = "waiting for the first read";
    public const string ReadFailed = "read failed";
    public const string Copied = "copied";
    public const string CopyFailed = "copy failed";
    public const string Unresolved = "unresolved";

    public static string Stamp(DateTimeOffset now) => $"updated {now:HH:mm:ss}";

    public static string WorkingFolder(string root)
        => Path.GetFileName(Path.TrimEndingDirectorySeparator(root));

    // The branch in the header carries the count of commits to pull: that is what decides whether
    // something is worth doing before starting work.
    public static string BuildBranch(GitStatus git)
    {
        if (git.Error is not null)
        {
            return "git unavailable";
        }

        var branch = git.Branch ?? "?";
        return git.Behind > 0 ? $"{branch} · {git.Behind} to pull" : branch;
    }

    // The line opens with the working folder name (see WorkingFolder): with several clones
    // active on the same machine it is the only thing that says WHICH clone the panel refers to.
    public static string BuildRepoDetail(GitStatus git)
    {
        var parts = new List<string>();

        if (git.Behind > 0)
        {
            parts.Add($"{git.Behind} to pull");
        }

        parts.Add(git.IsClean ? "working tree clean" : $"{git.DirtyFiles} modified files");

        if (!MissingUpstream(git) && git.Ahead > 0)
        {
            parts.Add($"{git.Ahead} commits to push");
        }

        return string.Join(" · ", parts);
    }

    // Kept out of the detail because the host shows it in red and the kitten reports it.
    public static string BuildUpstreamWarning(GitStatus git)
        => git.Error is null && MissingUpstream(git) ? "no upstream configured" : string.Empty;

    private static bool MissingUpstream(GitStatus git) => !git.HasUpstream && !git.IsDetached;

    public static string PullToolTip(GitStatus git) => git.Behind > 0
        ? $"git pull --ff-only — takes {git.Behind} commits from {git.Branch}"
        : "git pull --ff-only — nothing to take according to the last fetch";

    public static string StateLabel(MigrationState state) => state switch
    {
        MigrationState.Applied => MigrationLabels.Applied,
        MigrationState.PendingOutOfOrder => MigrationLabels.OutOfOrder,
        MigrationState.Unknown => MigrationLabels.Unknown,
        _ => MigrationLabels.Missing,
    };

    // Only the last output line: it shows the command is alive, it is not meant as a log.
    public static string Progress(string line) => line.Length > 90 ? line[..90] + "…" : line;

    public static string ApplyToolTip(int pending, string? database)
        => $"dotnet ef database update — applies {pending} migration(s) to {database}";

    public const string ProtectedDatabaseTag = "Protected DB";

    public const string ProtectedDatabaseNote = "Protected database: migrations are read but not applied, and the schema is not changed.";

    public const string ProtectedDatabaseToolTip = "Protected database (for example because others use it too): migrations are read, but no update, rollback or command copy, and the schema is not changed. For work involving migrations use another environment.";

    public static string CopyUpdateToolTip(string migrationId)
        => $"Copy the dotnet ef database update command up to {migrationId}";

    // Segoe Fluent Icons glyphs: Play to move forward, Undo for a rollback.
    private const string ForwardGlyph = "";
    private const string RollbackGlyph = "";

    public static string RunUpdateGlyph(UpdatePlan plan) => plan.IsRollback ? RollbackGlyph : ForwardGlyph;

    public static string RunUpdateToolTip(UpdatePlan plan)
    {
        if (plan.IsNoOp)
        {
            return $"The database is already at {plan.Target.DisplayName}";
        }

        var apply = plan.ToApply.Count > 0 ? $"applies {plan.ToApply.Count} migration(s)" : null;

        return plan.IsRollback
            ? $"Rollback to {plan.Target.DisplayName}: reverts {plan.ToRevert.Count} migration(s)"
                + (apply is null ? string.Empty : $" and {apply}")
                + " (asks for confirmation)"
            : $"dotnet ef database update up to {plan.Target.DisplayName}: {apply} (asks for confirmation)";
    }

    public static string CopyUrlToolTip(string url) => url.Length > 0 ? $"Copy {url}" : "URL not available";

    // The rollback runs the Down methods: tables and columns created by those migrations disappear along with
    // their data. The confirmation says so and lists them by name, because it is the last point at which
    // you can notice you clicked the wrong row.
    public static Confirmation UpdateToConfirmation(
        string contextName,
        UpdatePlan plan,
        string clone,
        string? environmentLabel,
        string server,
        string database)
        => new(
            plan.IsRollback ? $"Roll back {contextName}" : $"Update {contextName}",
            plan.IsRollback
                ? $"Rolling {contextName} back to {plan.Target.DisplayName} on {clone}{EnvironmentSuffix(environmentLabel)}. The rollback runs the Down methods: whatever the reverted migrations created is deleted along with its data."
                : $"Updating {contextName} up to {plan.Target.DisplayName} on {clone}{EnvironmentSuffix(environmentLabel)}.",
            [
                new($"To revert ({plan.ToRevert.Count})", plan.ToRevert.Select(e => new ConfirmationRow(e.DisplayName, Note: "Down")).ToList()),
                new($"To apply ({plan.ToApply.Count})", plan.ToApply.Select(e => new ConfirmationRow(e.DisplayName)).ToList()),
                Target(server, database),
            ],
            plan.IsRollback ? "Roll back" : "Update");

    // The confirmation shows clone, server and database: it is the only point at which you can notice
    // you are pointed at a different environment from the one you thought. The forced variables are
    // stated because they are what decides which database gets touched.
    public static Confirmation ApplyConfirmation(
        string contextName,
        string clone,
        string? environmentLabel,
        string server,
        string database)
        => new(
            $"Apply the migrations of {contextName}",
            $"Running dotnet ef database update for {contextName} on {clone}{EnvironmentSuffix(environmentLabel)}: check server and database before confirming.",
            [Target(server, database)],
            "Apply");

    public static Confirmation PruneConfirmation(
        int count,
        IEnumerable<string> branches,
        string clone,
        string? environmentLabel)
        => new(
            "Branch cleanup",
            $"Deleting the local branches of {clone}{EnvironmentSuffix(environmentLabel)} whose remote branch no longer exists.",
            [new($"Branches to delete ({count})", branches.Select(b => new ConfirmationRow(b)).ToList())],
            "Delete");

    private static ConfirmationList Target(string server, string database)
        => new("Target",
        [
            new(server, "server"),
            new(database, "database"),
            new("ASPNETCORE_ENVIRONMENT and DOTNET_ENVIRONMENT", "env", "forced to Development"),
        ]);

    private static string EnvironmentSuffix(string? environmentLabel)
        => environmentLabel is null ? string.Empty : $" ({environmentLabel})";
}

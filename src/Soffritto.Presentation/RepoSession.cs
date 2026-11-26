using Soffritto.Core;

namespace Soffritto.Presentation;

/// <summary>
/// Panel orchestration for a repository: per-section reads, refresh, write actions
/// with their guards and the refreshes that follow them. The host provides only how things are
/// shown (sink), how they are confirmed (prompt), how they are copied (clipboard) and where the root is.
/// </summary>
public sealed class RepoSession
{
    private readonly Func<CancellationToken, Task<string?>> resolveRoot;
    private readonly RepoSessionOptions options;
    private readonly IStatusSink sink;
    private readonly IUserPrompt prompt;
    private readonly IClipboard clipboard;
    private readonly WriteGate gate;

    // Reads outlive the command that requested them: they stop only with the host.
    private readonly CancellationToken lifetime;

    // One read per section, independent of each other: migrations do not wait for the git fetch
    // or the dev tunnel, and no read blocks the actions, whose only gate is the
    // WriteGate.
    private readonly SectionLoader migrationsLoader = new();
    private readonly SectionLoader gitLoader = new();
    private readonly SectionLoader tunnelLoader = new();

    private readonly ProblemBuffer problems = new();

    // Last server and database read: the database update confirmation shows them.
    private string server = StatusText.Unresolved;
    private string database = StatusText.Unresolved;

    // Guarded by the gate: only one action at a time writes it.
    private bool actingOnMigrations;

    public RepoSession(
        Func<CancellationToken, Task<string?>> resolveRoot,
        RepoSessionOptions options,
        IStatusSink sink,
        IUserPrompt prompt,
        IClipboard clipboard,
        WriteGate gate,
        CancellationToken lifetime)
    {
        this.resolveRoot = resolveRoot;
        this.options = options;
        this.sink = sink;
        this.prompt = prompt;
        this.clipboard = clipboard;
        this.gate = gate;
        this.lifetime = lifetime;
    }

    /// <param name="full">
    /// True for the full read (including git fetch and tunnel probe), false for the local
    /// one that is limited to migrations and the on-disk git state.
    /// </param>
    public async Task RefreshAsync(bool full, CancellationToken cancellationToken)
    {
        if (!gate.IsNotBusy)
        {
            return;
        }

        try
        {
            var root = await resolveRoot(cancellationToken).ConfigureAwait(false);
            if (root is null)
            {
                if (options.RootUnavailableHeadline is not null)
                {
                    Fail(options.RootUnavailableHeadline, options.RootUnavailableMessage ?? string.Empty);
                }

                return;
            }

            // Start of a refresh: the outcome of the last action is superseded by the new read.
            // Each section signals on its own that it is reading, so the activity line goes idle.
            sink.BeginRefresh();
            problems.Action = string.Empty;
            SetActivity(string.Empty);
            sink.SetErrorLine(problems.Compose());

            // The tunnel probe travels with the fetch: both hit the network, and the local
            // read (after a database update) skips them, leaving the tunnel section as it is
            // instead of resetting it to "not read".
            if (options.ReadsMigrations)
            {
                migrationsLoader.Request(() => LoadMigrationsAsync(root));
            }

            gitLoader.Request(() => LoadGitAsync(root, fetch: full));

            if (full && options.ReadsTunnel)
            {
                tunnelLoader.Request(() => LoadTunnelAsync(root));
            }
        }
        catch (OperationCanceledException)
        {
            // The host closed while the root was being resolved.
        }
        catch (Exception ex)
        {
            Fail(StatusText.ReadFailed, ex.Message);
        }
    }

    /// <summary>Error raised outside the session (e.g. the wait for the solution timing out in the VSIX).</summary>
    public void ReportFailure(string headline, string message) => Fail(headline, message);

    /// <summary>Rereads only the dev tunnel section, without git fetch or migrations.</summary>
    public async Task RefreshTunnelAsync(CancellationToken cancellationToken)
    {
        if (!options.ReadsTunnel)
        {
            return;
        }

        var root = await resolveRoot(cancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return;
        }

        tunnelLoader.Request(() => LoadTunnelAsync(root));
    }

    // The fetch is a read: it updates the remote-tracking refs and rereads only the git section.
    public async Task FetchAsync(CancellationToken cancellationToken)
    {
        if (!gate.IsNotBusy)
        {
            return;
        }

        var root = await resolveRoot(cancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return;
        }

        gitLoader.Request(() => LoadGitAsync(root, fetch: true));
    }

    public async Task PullAsync(CancellationToken cancellationToken)
    {
        if (!gate.IsNotBusy)
        {
            return;
        }

        var root = await resolveRoot(cancellationToken).ConfigureAwait(false);
        if (root is null || !BeginAction("git pull --ff-only…"))
        {
            return;
        }

        try
        {
            var outcome = await GitStatusReader
                .FastForwardAsync(root, Progress, cancellationToken)
                .ConfigureAwait(false);

            if (!outcome.Success)
            {
                Fail("pull failed", outcome.Tail());
                return;
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Fail("pull failed", ex.Message);
            return;
        }
        finally
        {
            EndAction();
        }

        // After a pull the migrations on disk may have changed: rereading is mandatory,
        // and everything is reread like the Refresh button does.
        await RefreshAsync(full: true, cancellationToken).ConfigureAwait(false);
    }

    // No confirmation: the choice in the drop-down is already the intent, and git switch refuses
    // by itself if local changes would conflict. If something prevents the switch the
    // drop-down goes back to the current branch.
    public async Task SwitchBranchAsync(string branch, CancellationToken cancellationToken)
    {
        if (!gate.IsNotBusy)
        {
            sink.RestoreBranchSelection();
            return;
        }

        var root = await resolveRoot(cancellationToken).ConfigureAwait(false);
        if (root is null || !BeginAction($"git switch {branch}…"))
        {
            sink.RestoreBranchSelection();
            return;
        }

        try
        {
            var outcome = await GitStatusReader
                .SwitchAsync(root, branch, Progress, cancellationToken)
                .ConfigureAwait(false);

            if (!outcome.Success)
            {
                Fail("branch switch failed", outcome.Tail());
                sink.RestoreBranchSelection();
                return;
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Fail("branch switch failed", ex.Message);
            sink.RestoreBranchSelection();
            return;
        }
        finally
        {
            EndAction();
        }

        // Different branch: different migrations on disk and a different upstream to compare, hence
        // a full refresh like the Refresh button.
        await RefreshAsync(full: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Switch (if needed) and fast-forward pull (if the branch is behind) within the same action,
    /// then a single full reread. With <see cref="SwitchBranchAsync"/> followed by
    /// <see cref="PullAsync"/> the reread after the switch ran a git fetch in the background
    /// while the pull was starting, and the two fetches could fight over the ref locks. The caller
    /// has already done the fetch. Returns what happened, or the problem.
    /// </summary>
    public async Task<(bool Pulled, string? Problem)> SwitchAndPullAsync(string branch, CancellationToken cancellationToken)
    {
        var root = await resolveRoot(cancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return (false, "folder not available");
        }

        if (!BeginAction($"git switch {branch}…"))
        {
            return (false, "an action is already in progress");
        }

        var pulled = false;
        string? problem = null;

        try
        {
            var status = await GitStatusReader.ReadAsync(root, fetch: false, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(status.Branch, branch, StringComparison.Ordinal))
            {
                var switched = await GitStatusReader.SwitchAsync(root, branch, Progress, cancellationToken).ConfigureAwait(false);
                if (!switched.Success)
                {
                    problem = "switch failed";
                    Fail("branch switch failed", switched.Tail());
                    sink.RestoreBranchSelection();
                    return (false, problem);
                }

                status = await GitStatusReader.ReadAsync(root, fetch: false, cancellationToken).ConfigureAwait(false);
            }

            if (status.Behind > 0)
            {
                SetActivity("git pull --ff-only…");
                var outcome = await GitStatusReader.FastForwardAsync(root, Progress, cancellationToken).ConfigureAwait(false);
                if (!outcome.Success)
                {
                    problem = "pull failed";
                    Fail(problem, outcome.Tail());
                    return (false, problem);
                }

                pulled = true;
            }
        }
        catch (OperationCanceledException)
        {
            return (false, "cancelled");
        }
        catch (Exception ex)
        {
            Fail("branch switch failed", ex.Message);
            return (false, ex.Message);
        }
        finally
        {
            EndAction();
        }

        await RefreshAsync(full: true, cancellationToken).ConfigureAwait(false);
        return (pulled, null);
    }

    // Deleting branches is irreversible as far as local refs are concerned, so the confirmation
    // comes once the candidates are known and lists them by name: it is the only point at which you notice
    // that among them is a branch you thought was still alive on the remote.
    public async Task PruneBranchesAsync(CancellationToken cancellationToken)
    {
        if (!gate.IsNotBusy)
        {
            return;
        }

        var root = await resolveRoot(cancellationToken).ConfigureAwait(false);
        if (root is null || !BeginAction("git fetch --prune…"))
        {
            return;
        }

        PrunableBranches candidates;

        try
        {
            candidates = await GitStatusReader
                .ReadPrunableBranchesAsync(root, Progress, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Fail("branch cleanup failed", ex.Message);
            return;
        }
        finally
        {
            EndAction();
        }

        if (candidates.Error is not null)
        {
            Fail("branch cleanup failed", candidates.Error);
            return;
        }

        if (candidates.Branches.Count == 0)
        {
            // --prune may still have removed some remote-tracking refs: the drop-down must be reread.
            // The message comes afterwards, because the start of the refresh clears the activity line.
            await RefreshAsync(full: false, cancellationToken).ConfigureAwait(false);
            SetActivity("no local branches to delete");
            return;
        }

        var confirmed = await prompt.ConfirmAsync(
            StatusText.PruneConfirmation(
                candidates.Branches.Count,
                candidates.Branches,
                StatusText.WorkingFolder(root),
                options.EnvironmentLabel),
            cancellationToken).ConfigureAwait(false);

        if (!confirmed || !BeginAction($"git branch -D — {candidates.Branches.Count} branch…"))
        {
            return;
        }

        try
        {
            var outcome = await GitStatusReader
                .DeleteBranchesAsync(root, candidates.Branches, Progress, cancellationToken)
                .ConfigureAwait(false);

            if (!outcome.Success)
            {
                Fail("branch cleanup failed", outcome.Tail());
                return;
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Fail("branch cleanup failed", ex.Message);
            return;
        }
        finally
        {
            EndAction();
        }

        // --prune has already done the fetch: rereading the local state and the drop-down is enough.
        await RefreshAsync(full: false, cancellationToken).ConfigureAwait(false);
        SetActivity($"{candidates.Branches.Count} branches deleted");
    }

    public async Task ApplyMigrationsAsync(string contextName, CancellationToken cancellationToken)
    {
        if (!gate.IsNotBusy)
        {
            return;
        }

        var definition = options.Probe.Contexts.FirstOrDefault(c => c.Name == contextName);
        if (definition is null)
        {
            return;
        }

        var root = await resolveRoot(cancellationToken).ConfigureAwait(false);
        if (root is null || GuardRefuses(root) || SchemaLockRefuses(root))
        {
            return;
        }

        var confirmed = await prompt.ConfirmAsync(
            StatusText.ApplyConfirmation(
                contextName,
                StatusText.WorkingFolder(root),
                options.EnvironmentLabel,
                server,
                database),
            cancellationToken).ConfigureAwait(false);

        if (!confirmed || GuardRefuses(root) || !BeginAction("dotnet ef database update…", onMigrations: true))
        {
            return;
        }

        try
        {
            var outcome = await MigrationApplier
                .UpdateDatabaseAsync(root, definition, targetMigrationId: null, Progress, cancellationToken)
                .ConfigureAwait(false);

            if (!outcome.Success)
            {
                Fail("update failed", await MigrationApplier.FailureReasonAsync(root, definition, outcome, cancellationToken).ConfigureAwait(false));
                return;
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Fail("update failed", ex.Message);
            return;
        }
        finally
        {
            EndAction();
        }

        // Only the database changed: neither git nor the tunnel has any reason to be reread.
        await RefreshAsync(full: false, cancellationToken).ConfigureAwait(false);
    }

    // The plan is recomputed from a fresh read, not from the rows shown: between the last refresh
    // and the click someone else may have migrated, and a rollback computed on a stale state
    // would revert migrations other than those listed in the confirmation.
    public async Task UpdateToMigrationAsync(
        string contextName,
        string migrationId,
        CancellationToken cancellationToken)
    {
        if (!gate.IsNotBusy)
        {
            return;
        }

        var definition = options.Probe.Contexts.FirstOrDefault(c => c.Name == contextName);
        if (definition is null)
        {
            return;
        }

        var root = await resolveRoot(cancellationToken).ConfigureAwait(false);
        if (root is null || GuardRefuses(root) || SchemaLockRefuses(root) || !BeginAction("reading migrations…", onMigrations: true))
        {
            return;
        }

        StatusReport report;

        try
        {
            report = await StatusProbe
                .RunMigrationsAsync(root, options.Probe, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Fail("reading migrations failed", ex.Message);
            return;
        }
        finally
        {
            EndAction();
        }

        if (report.DatabaseError is not null || report.ConfigurationError is not null)
        {
            Fail("reading migrations failed", report.DatabaseError ?? report.ConfigurationError!);
            return;
        }

        var plan = report.Contexts
            .FirstOrDefault(c => c.ContextName == contextName)?
            .PlanUpdateTo(migrationId);

        if (plan is null)
        {
            Fail("migration not found", $"{migrationId} is no longer among the migrations of {contextName}: refresh the panel.");
            return;
        }

        if (plan.IsNoOp)
        {
            SetActivity($"database already at {plan.Target.DisplayName}");
            return;
        }

        var confirmed = await prompt.ConfirmAsync(
            StatusText.UpdateToConfirmation(
                contextName,
                plan,
                StatusText.WorkingFolder(root),
                options.EnvironmentLabel,
                report.Server ?? StatusText.Unresolved,
                report.Database ?? StatusText.Unresolved),
            cancellationToken).ConfigureAwait(false);

        if (!confirmed || GuardRefuses(root) || !BeginAction($"dotnet ef database update {plan.Target.DisplayName}…", onMigrations: true))
        {
            return;
        }

        try
        {
            var outcome = await MigrationApplier
                .UpdateDatabaseAsync(root, definition, migrationId, Progress, cancellationToken)
                .ConfigureAwait(false);

            if (!outcome.Success)
            {
                Fail(plan.IsRollback ? "rollback failed" : "update failed", await MigrationApplier.FailureReasonAsync(root, definition, outcome, cancellationToken).ConfigureAwait(false));
                return;
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Fail(plan.IsRollback ? "rollback failed" : "update failed", ex.Message);
            return;
        }
        finally
        {
            EndAction();
        }

        await RefreshAsync(full: false, cancellationToken).ConfigureAwait(false);
    }

    // Copying touches nothing: no confirmation, unlike the button that actually runs
    // the command. The copied line carries the Development environment with it. The database guard
    // of Docker environments applies here too: the pasted line would really migrate.
    /// <summary>
    /// Copies to the clipboard the update command up to <paramref name="migrationId"/>. Null if
    /// there was nothing to copy (unknown context or unresolved root): no feedback.
    /// </summary>
    public async Task<bool?> CopyUpdateCommandAsync(
        string contextName,
        string migrationId,
        CancellationToken cancellationToken)
    {
        var definition = options.Probe.Contexts.FirstOrDefault(c => c.Name == contextName);
        if (definition is null)
        {
            return null;
        }

        var root = await resolveRoot(cancellationToken).ConfigureAwait(false);
        if (root is null || GuardRefuses(root) || SchemaLockRefuses(root))
        {
            return null;
        }

        return clipboard.TrySetText(MigrationApplier.BuildUpdateCommandLine(root, definition, migrationId));
    }

    // In Docker environments a database command runs only if the worktree connection
    // points to the slot's SQL Server: otherwise it would migrate someone else's database.
    private bool GuardRefuses(string root)
    {
        if (options.DatabaseGuard?.Invoke(root) is not { } error)
        {
            return false;
        }

        Fail("database outside the slot", error);
        return true;
    }

    // A protected database is read but not migrated: the lock applies to every schema write,
    // copying the command included, because the pasted line would really migrate.
    private bool SchemaLockRefuses(string root)
    {
        if (options.SchemaLock?.Invoke(root) is not { } reason)
        {
            return false;
        }

        Fail("protected database", reason);
        return true;
    }

    public bool CopyText(string text) => clipboard.TrySetText(text);

    private bool BeginAction(string message, bool onMigrations = false)
    {
        if (!gate.TryEnter())
        {
            return false;
        }

        actingOnMigrations = onMigrations;
        sink.SetBusy(true);
        sink.BeginOperation();
        SetActivity(message);

        if (onMigrations)
        {
            sink.SetMigrationsAction(message);
        }

        return true;
    }

    private void EndAction()
    {
        if (actingOnMigrations)
        {
            actingOnMigrations = false;
            sink.SetMigrationsAction(null);
        }

        gate.Exit();
        sink.SetBusy(false);
        sink.EndOperation();
    }

    private void Fail(string headline, string message)
    {
        SetActivity(headline);
        problems.Action = message;
        sink.SetErrorLine(problems.Compose());
    }

    private void SetActivity(string line)
        => sink.SetActivity(line.Length > 0 ? line : StatusText.IdleActivity);

    private void Progress(string line) => SetActivity(StatusText.Progress(line));

    private async Task LoadMigrationsAsync(string root)
    {
        sink.SetMigrationsLoading();
        sink.BeginOperation();

        try
        {
            if (options.DatabaseGuard?.Invoke(root) is { } refused)
            {
                SetActivity("database outside the slot");
                problems.Migrations = refused;
                sink.FailMigrations();
                sink.SetErrorLine(problems.Compose());
                return;
            }

            var report = await StatusProbe
                .RunMigrationsAsync(root, options.Probe, lifetime, options.DatabaseOffline?.Invoke())
                .ConfigureAwait(false);

            var view = ViewBuilder.Migrations(report, DateTimeOffset.Now, options.SchemaLock?.Invoke(root));
            server = view.Server;
            database = view.Database;
            problems.Migrations = view.Problems;
            sink.ApplyMigrations(view);
            sink.SetErrorLine(problems.Compose());
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            // The host closed during the read.
        }
        catch (Exception ex)
        {
            SetActivity("reading migrations failed");
            problems.Migrations = ex.Message;
            sink.FailMigrations();
            sink.SetErrorLine(problems.Compose());
        }
        finally
        {
            sink.EndOperation();
        }
    }

    private async Task LoadGitAsync(string root, bool fetch)
    {
        sink.SetRepoLoading(ViewBuilder.RepoLoading(root, fetch));
        sink.BeginOperation();

        try
        {
            GitStatus git;

            try
            {
                git = await GitStatusReader.ReadAsync(root, fetch, lifetime).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                // The host closed during the read.
                return;
            }
            catch (Exception ex)
            {
                git = new GitStatus(Error: ex.Message);
            }

            var view = ViewBuilder.Git(git, root, DateTimeOffset.Now);
            problems.Git = view.Problems;
            sink.ApplyGit(view);
            sink.SetErrorLine(problems.Compose());
        }
        finally
        {
            sink.EndOperation();
        }
    }

    private async Task LoadTunnelAsync(string root)
    {
        var tunnelGate = options.TunnelGate;

        try
        {
            // With several sessions the devtunnel CLI is queried by one at a time: the section
            // says it is queued until its turn comes. The wait sits outside the try that
            // releases, so a cancellation during the wait does not release a gate never taken.
            if (tunnelGate is not null)
            {
                sink.SetTunnelQueued();
                await tunnelGate.WaitAsync(lifetime).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            return;
        }

        try
        {
            sink.BeginOperation();
            sink.SetTunnelLoading();

            DevTunnelStatus tunnel;

            try
            {
                tunnel = options.PoolAssignmentOf is null
                    ? await DevTunnelReader.ReadAsync(root, cancellationToken: lifetime).ConfigureAwait(false)
                    : options.PoolAssignmentOf(root) is { } assignment
                        ? await DevTunnelReader.ReadAsync(root, assignment.TunnelDescription, lifetime, assignment.Port).ConfigureAwait(false)
                        : DevTunnelStatus.NotShared("Turn on “Tunnel” in the environment header to get a URL to give your colleagues");
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                // The host closed during the read.
                return;
            }
            catch (Exception ex)
            {
                tunnel = new DevTunnelStatus(Error: ex.Message);
            }

            sink.ApplyTunnel(ViewBuilder.Tunnel(tunnel, DateTimeOffset.Now));
        }
        finally
        {
            sink.EndOperation();
            tunnelGate?.Release();
        }
    }
}

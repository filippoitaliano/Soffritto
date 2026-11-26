using Soffritto.Core;
using Soffritto.Core.Configuration;
using Soffritto.Desktop.Infrastructure;
using Soffritto.Desktop.ViewModels;
using Soffritto.Presentation;

namespace Soffritto.Desktop;

/// <summary>
/// Builds a session for each card of the environment: git for all of them, migrations for the
/// backends, the dev tunnel for the cards that have one. One write gate per repository: an action
/// locks only the buttons of the repository it works on, the others stay free.
/// </summary>
public sealed class EnvironmentController
{
    // The devtunnel CLI for the backend and frontend tunnels of all environments is queried one
    // read at a time: it is not verified that it handles concurrent calls on the same login.
    private static readonly SemaphoreSlim TunnelGate = new(1, 1);

    private readonly EnvironmentViewModel vm;
    private readonly CancellationToken lifetime;
    private readonly List<(RepoCardViewModel Card, RepoSession Session, WriteGate Gate)> repos = [];

    public EnvironmentController(
        EnvironmentViewModel vm,
        IUserPrompt prompt,
        IClipboard clipboard,
        CancellationToken lifetime)
    {
        this.vm = vm;
        this.lifetime = lifetime;

        foreach (var card in vm.Repos)
        {
            var isBackend = card.Type == RepositoryType.Backend;
            var tunnel = card.Tunnel?.State;
            var location = card.Location;
            var sink = new WpfRepoSink(vm, card, card.Migrations, tunnel, lifetime);
            var gate = new WriteGate();

            gate.Changed += (_, _) => UiThread.Run(() =>
            {
                card.IsNotBusy = gate.IsNotBusy;

                // Database update is a backend action: its button follows that gate.
                if (card.Migrations is { } migrations)
                {
                    migrations.IsNotBusy = gate.IsNotBusy;
                }

                // For the confirmation on close: a write is in progress in the environment.
                vm.IsNotBusy = repos.All(r => r.Gate.IsNotBusy);
            });

            var probe = card.Location.Settings is BackendConfiguration backend
                ? AppConfiguration.ToProbeOptions(backend)
                : ActiveConfiguration.Current.ToProbeOptions();
            var session = new RepoSession(
                _ => Task.FromResult(card.Location.Exists() ? card.Location.Path : null),
                new RepoSessionOptions(
                    probe,
                    ReadsMigrations: isBackend,
                    ReadsTunnel: tunnel is not null,
                    TunnelGate: tunnel is not null ? TunnelGate : null,
                    EnvironmentLabel: $"environment {vm.Number}",
                    RootUnavailableHeadline: null,
                    RootUnavailableMessage: null,
                    // Re-read on every tunnel read: ports are assigned and released while the app is open.
                    PoolAssignmentOf: _ => Soffritto.Core.Docker.TunnelPool.AssignmentOf(location.Entry.Number, location.Id),
                    DatabaseGuard: vm.Docker is { } docker ? GuardOf(docker, probe) : null,
                    // The Docker slot's database (its own, or for a local environment that of a slot the
                    // connection points to): while it is stopped or starting, no connection is attempted.
                    DatabaseOffline: isBackend && card.IsPrimary ? () => DatabaseOffline?.Invoke() : null,
                    SchemaLock: isBackend && vm.SchemaLock is { } schemaLock ? _ => schemaLock : null),
                sink,
                prompt,
                clipboard,
                gate,
                lifetime);

            sink.Session = session;

            card.FetchCommand = new AsyncCommand(() => session.FetchAsync(lifetime));
            card.PullCommand = new AsyncCommand(() => session.PullAsync(lifetime));
            card.PruneCommand = new AsyncCommand(() => session.PruneBranchesAsync(lifetime));
            card.BranchPicker.BranchSelected = branch => _ = session.SwitchBranchAsync(branch, lifetime);

            if (tunnel is not null)
            {
                sink.ApplyTunnel(ViewBuilder.Tunnel(null, DateTimeOffset.Now));
            }

            repos.Add((card, session, gate));
        }

        vm.RefreshCommand = new AsyncCommand(() =>
        {
            Refresh(full: true);
            return Task.CompletedTask;
        });

        vm.ResetCommand = new AsyncCommand(async () =>
        {
            vm.Report("reset: stopping the services, checking the repositories and fetching…");
            var outcome = await CheckoutAsync([], alwaysStop: true).ConfigureAwait(false);
            vm.Report(outcome.Text);
        });
    }

    private int aligning;

    // Realigns the env files without asking: only the per-slot variables whose value is {apiHostUrl}
    // (the api on Kestrel) or that depend on {poolUrl}. It writes only if something
    // changes; the outcome goes to the activity line after the re-read, which would clear it,
    // because it says to restart the dev server or Metro.
    private async Task AlignThenRefreshAsync()
    {
        var outcome = await AlignEnvAsync().ConfigureAwait(false);
        try
        {
            await RefreshAsync(full: true).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
        }

        if (outcome is not null)
        {
            vm.Report(outcome);
        }
    }

    // The outcome to show, or null if the env files were already aligned.
    private async Task<string?> AlignEnvAsync()
    {
        if (Interlocked.Exchange(ref aligning, 1) == 1)
        {
            return null;
        }

        try
        {
            var configuration = ActiveConfiguration.Current;
            var plan = await Task.Run(
                () => Soffritto.Core.Env.EnvPoolSync.PlanAsync(configuration, vm.Number, Soffritto.Core.Env.EnvSyncScope.All, ct: lifetime),
                lifetime).ConfigureAwait(false);
            if (!plan.Any(c => c.Outcome == Soffritto.Core.Env.EnvPoolSync.Written))
            {
                return null;
            }

            var lines = new List<string>();
            await Task.Run(
                () => Soffritto.Core.Env.EnvPoolSync.ApplyAsync(configuration, vm.Number, Soffritto.Core.Env.EnvSyncScope.All, lines.Add, ct: lifetime),
                lifetime).ConfigureAwait(false);
            return $"env files aligned: {string.Join(" · ", lines)}";
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            return $"env file alignment failed: {ex.Message}";
        }
        finally
        {
            Interlocked.Exchange(ref aligning, 0);
        }
    }

    /// <summary>
    /// Stops all the environment's services: the outcome and whether everything stopped (see
    /// <see cref="DevToolsController.StopEverythingAsync"/>).
    /// </summary>
    public Func<Task<(string Outcome, bool AllStopped)>>? StopServices { get; set; }

    /// <summary>Why the slot's database cannot be read right now (see <see cref="DevToolsController.DatabaseOffline"/>); for a local environment, that of the Docker slot it points to, if any.</summary>
    public Func<string?>? DatabaseOffline { get; set; }

    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(120);

    // In the sessions of a Docker environment every migrations read, database update, rollback
    // and command copy goes through here first: the worktree must point to the slot's SQL Server.
    private static Func<string, string?> GuardOf(DockerEnvironmentViewModel docker, ProbeOptions probe)
        => docker.Slot is { } slot
            ? root => Soffritto.Core.Docker.DatabaseGuard.Check(root, probe, slot.DatabasePort, slot.DatabaseName)
            : _ => docker.SlotError ?? "slot cannot be computed";

    /// <summary>Re-reads only the dev tunnel of one of the environment's repositories, if its folder exists.</summary>
    public void RefreshTunnel(RepoCardViewModel target)
    {
        var (card, session, _) = repos.First(r => ReferenceEquals(r.Card, target));
        if (card.Location.Exists())
        {
            _ = session.RefreshTunnelAsync(lifetime);
        }
    }

    /// <summary>
    /// Stops the environment and moves each repository onto the branch given in <paramref name="targets"/>
    /// or, if there is none, onto <see cref="GitConventions.DevelopmentBranch"/>: the reset gives none, the PR panel
    /// gives the branches of a work item's PRs. All repositories are checked (fetch included):
    /// if even one has changes, a git operation halfway through, an action in progress, or the
    /// branch does not exist, nothing changes. Only once the checks pass are all services stopped
    /// (app included), then git switch and, if the local branch is behind, a fast-forward pull.
    /// With <paramref name="alwaysStop"/> (the Reset) the services stop before the checks, even if
    /// the repositories then stay where they are. Returns the outcome to show the user. Targets
    /// are keyed by repository name on the server; generic repositories follow the same rules, so
    /// a dirty one or one without the branch blocks the environment like the others.
    /// </summary>
    public async Task<ActionOutcome> CheckoutAsync(IReadOnlyList<(string Repository, string Branch)> targets, bool alwaysStop = false)
    {
        var problems = new List<string>();
        var stopped = "nothing stopped";
        if (alwaysStop)
        {
            var (outcome, stopProblem) = await StopForCheckoutAsync().ConfigureAwait(false);
            stopped = outcome;
            if (stopProblem is not null)
            {
                problems.Add(stopProblem);
            }
        }

        var plan = await PlanCheckoutAsync(targets, problems).ConfigureAwait(false);

        // “Open in” stops services only when it can switch every branch: a refusal leaves the
        // environment running as it was.
        if (!alwaysStop && problems.Count == 0)
        {
            var (outcome, stopProblem) = await StopForCheckoutAsync().ConfigureAwait(false);
            stopped = outcome;
            if (stopProblem is not null)
            {
                problems.Add(stopProblem);
            }
        }

        if (problems.Count > 0)
        {
            return ActionOutcome.Failed($"Environment {vm.Number}: {stopped}. Repositories left untouched: {string.Join("; ", problems)}");
        }

        var done = new List<string>();
        var partial = false;
        foreach (var (card, session, branch, _) in plan)
        {
            var (pulled, problem) = await session.SwitchAndPullAsync(branch, lifetime).ConfigureAwait(false);
            partial |= problem is not null;
            done.Add(problem is not null
                ? $"{card.Clone}: {problem}, see the card"
                : $"{card.Clone} on {branch}{(pulled ? " (updated with pull)" : string.Empty)}");
        }

        var text = $"Environment {vm.Number}: {stopped}; {string.Join(", ", done)}";
        return partial ? ActionOutcome.Partial(text) : ActionOutcome.Done(text);
    }

    // Stopping a Docker environment's containers can fail or never finish: in that case no
    // repository switches branch under a service that is still running.
    private async Task<(string Outcome, string? Problem)> StopForCheckoutAsync()
    {
        if (StopServices is not { } stop)
        {
            return ("services not stopped", null);
        }

        var stopping = Task.Run(stop);
        if (await Task.WhenAny(stopping, Task.Delay(StopTimeout, lifetime)).ConfigureAwait(false) != stopping)
        {
            return ("services not stopped in time", "services not stopped in time");
        }

        var (outcome, allStopped) = await stopping.ConfigureAwait(false);
        return allStopped ? (outcome, null) : ("services not stopped", $"services not stopped: {outcome}");
    }

    private async Task<List<(RepoCardViewModel Card, RepoSession Session, string Branch, GitStatus Status)>> PlanCheckoutAsync(
        IReadOnlyList<(string Repository, string Branch)> targets,
        List<string> problems)
    {
        var plan = new List<(RepoCardViewModel Card, RepoSession Session, string Branch, GitStatus Status)>();
        var worktreeBranches = new Dictionary<string, IReadOnlyDictionary<string, string>?>(StringComparer.OrdinalIgnoreCase);

        foreach (var (card, session, gate) in repos)
        {
            var requested = targets
                .Where(t => string.Equals(t.Repository, card.RepositoryName, StringComparison.OrdinalIgnoreCase))
                .Select(t => t.Branch)
                .FirstOrDefault();
            var branch = requested ?? GitConventions.DevelopmentBranch;

            // A missing clone does not block the reset; it blocks only if a branch is requested for it.
            if (!card.Location.Exists())
            {
                if (requested is not null)
                {
                    problems.Add($"{card.Clone}: folder not found");
                }

                continue;
            }

            if (!gate.IsNotBusy)
            {
                problems.Add($"{card.Clone}: an action is already in progress");
                continue;
            }

            var status = await GitStatusReader.ReadAsync(card.Path, fetch: true, lifetime).ConfigureAwait(false);
            var problem = status.Error
                ?? (status.FetchError is { } fetchError ? $"fetch failed ({fetchError})" : null)
                ?? GitStatusReader.ReadOperationInProgress(card.Path)
                ?? (status.IsDetached ? "detached HEAD" : null)
                ?? (status.DirtyFiles > 0 ? $"{status.DirtyFiles} modified or untracked files" : null)
                ?? (!status.AllBranches.Contains(branch, StringComparer.OrdinalIgnoreCase) ? $"branch {branch} does not exist" : null)
                ?? (card.Location.IsDocker ? await HeldElsewhereAsync(card, branch, worktreeBranches).ConfigureAwait(false) : null);

            if (problem is not null)
            {
                problems.Add($"{card.Clone}: {problem}");
                continue;
            }

            plan.Add((card, session, branch, status));
        }

        return plan;
    }

    // Git checks out a branch in only one worktree of the same source clone: if another worktree
    // holds it, git switch would fail halfway through the checkout. One read per source clone.
    private async Task<string?> HeldElsewhereAsync(
        RepoCardViewModel card,
        string branch,
        Dictionary<string, IReadOnlyDictionary<string, string>?> cache)
    {
        if (card.Location.Settings.SourceClone is not { Length: > 0 } sourceClone)
        {
            return null;
        }

        if (!cache.TryGetValue(sourceClone, out var held))
        {
            held = await Soffritto.Core.Docker.SourceCloneRepository.WorktreeBranchesAsync(sourceClone, lifetime).ConfigureAwait(false);
            cache[sourceClone] = held;
        }

        if (held is null)
        {
            return $"worktrees of source clone {sourceClone} could not be read";
        }

        return held.TryGetValue(branch, out var path) && !SamePath(path, card.Path)
            ? $"branch {branch} is already in worktree {path}"
            : null;
    }

    private static bool SamePath(string a, string b)
        => string.Equals(
            System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(a)),
            System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(b)),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Re-reads the existing cards. A missing folder starts no reads: with the backend missing
    /// neither the migrations nor its tunnel are read, with the frontend missing the frontend
    /// tunnel is not read. A repository with an action in
    /// progress skips the re-read (the session bails out by itself) and is re-read when the action ends.
    /// </summary>
    public void Refresh(bool full)
    {
        // A local environment's env files are realigned only once, at the first full re-read after
        // the app starts: a value changed by hand afterwards (another Kestrel, IIS Express, a
        // colleague's tunnel) stays until the next start.
        if (full && !vm.IsDocker && Interlocked.Exchange(ref alignedAtStartup, 1) == 0)
        {
            _ = AlignThenRefreshAsync();
            return;
        }

        _ = RefreshAsync(full);
    }

    private int alignedAtStartup;

    private Task RefreshAsync(bool full)
    {
        var existing = new List<RepoSession>();

        UiThread.Run(() =>
        {
            foreach (var (card, session, _) in repos)
            {
                if (card.RefreshExistence())
                {
                    existing.Add(session);
                }
            }

            // No session will start: the activity line must not stay stuck on "starting…".
            if (existing.Count == 0)
            {
                vm.ActivityLine = StatusText.IdleActivity;
            }
        });

        var reads = existing.Select(session => session.RefreshAsync(full, lifetime)).ToList();

        if (full && vm.Docker is { Slot: not null } docker)
        {
            reads.Add(RefreshDockerAsync(docker));
        }

        return Task.WhenAll(reads);
    }

    // The warnings of the slot's env files (such as a {poolUrl:<id>} variable without a pool port) and
    // the assigned pool ports: reads of files and of tunnel-pool.json, no writes.
    private async Task RefreshDockerAsync(DockerEnvironmentViewModel docker)
    {
        try
        {
            var configuration = ActiveConfiguration.Current;
            var slot = docker.Slot!;
            var poolUrls = await Soffritto.Core.Docker.DockerEnvironmentProvisioner.PoolUrlsAsync(vm.Number, lifetime).ConfigureAwait(false);
            var warnings = await Task.Run(
                () => slot.Repositories
                    .SelectMany(entry => Soffritto.Core.Env.EnvResolver.Resolve(configuration, entry, slot, poolUrls, targetIsNew: false).Warnings)
                    .ToList(),
                lifetime).ConfigureAwait(false);

            UiThread.Run(() => docker.EnvWarningText = string.Join(Environment.NewLine, warnings));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            UiThread.Run(() => docker.EnvWarningText = $"env file warnings not read: {ex.Message}");
        }
    }
}

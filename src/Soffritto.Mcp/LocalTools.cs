using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using ModelContextProtocol.Server;
using Soffritto.Core;
using Soffritto.Core.Configuration;
using Soffritto.Presentation;

namespace Soffritto.Mcp;

/// <summary>
/// The actions of the app on a local environment: services in detached consoles, pull, branch
/// switch, branch cleanup, database update and the environment's checkout. Git and database
/// actions run as operations in a worker process; the irreversible ones (branch cleanup,
/// database update) go in two steps: the first call returns the plan and a confirmation code,
/// the second, with the code, runs that plan only if the state read again still matches it.
/// </summary>
[McpServerToolType]
public static class LocalTools
{
    private const string WaitDescription = "Seconds to wait for completion before responding (0-110): if the operation takes longer, the response contains the id to follow with operation_status.";
    private const string ConfirmDescription = "Confirmation code returned by the previous call without it, after the person has seen the plan and agreed; empty = only compute the plan, touching nothing.";

    [McpServerTool(Name = "start_local_service", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Starts the service of a repository of a local environment in a console detached from the server, like Start on its card in the app: Kestrel for a backend, the dev server for a frontend, the app for an app (with bundler = true the app's bundler, like the Metro button). The console survives the server and the client session; environment_status shows its state.")]
    public static string StartLocalService(
        [Description("Local environment number.")] int number,
        [Description("Catalog id of the repository (see list_environments).")] string repository,
        [Description("For an app: starts the bundler instead of the app.")] bool bundler = false)
    {
        if (LocalWork.ResolveRepository(number, repository, out _, out var entry) is { } error)
        {
            return EnvironmentTools.Serialize(new { error });
        }

        var services = LocalServices.Of(entry);
        var scan = ProcessTable.Read(LocalServices.ApiProcesses([entry]));
        try
        {
            if (bundler)
            {
                return EnvironmentTools.Serialize(services.MetroState(scan) != ServiceState.Stopped
                    ? new { result = "the bundler is already running" }
                    : new { result = services.StartBundler() });
            }

            return EnvironmentTools.Serialize(services.State(scan) != ServiceState.Stopped
                ? new { result = $"{services.Name} is already running ({ConsoleServices.Describe(services.State(scan))})" }
                : new { result = services.Start(), nextStep = "environment_status to see when the service is running" });
        }
        catch (InvalidOperationException ex)
        {
            return EnvironmentTools.Serialize(new { error = ex.Message });
        }
    }

    [McpServerTool(Name = "stop_local_service", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Stops the service of a repository of a local environment, like Stop on its card in the app: closes its console and process tree, or the recognised processes if they were started some other way (for example Kestrel from Visual Studio). For an app it also stops the bundler; with bundler = true only the bundler.")]
    public static string StopLocalService(
        [Description("Local environment number.")] int number,
        [Description("Catalog id of the repository (see list_environments).")] string repository,
        [Description("For an app: stops only the bundler.")] bool bundler = false)
    {
        if (LocalWork.ResolveRepository(number, repository, out _, out var entry) is { } error)
        {
            return EnvironmentTools.Serialize(new { error });
        }

        var services = LocalServices.Of(entry);
        if (!services.HasService)
        {
            return EnvironmentTools.Serialize(new { error = $"{entry.Id} is a generic repository: it has no service" });
        }

        var scan = ProcessTable.Read(LocalServices.ApiProcesses([entry]));
        return EnvironmentTools.Serialize(new { result = bundler ? services.StopBundler(scan) : services.Stop(scan) });
    }

    [McpServerTool(Name = "start_local_environment", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Starts a local environment like the play button on its column in the app: Kestrel and the dev servers that are off, repository by repository (not the app), and the host of the environment's tunnel if it has its tunnel on and the host went down. Everything runs in consoles detached from the server.")]
    public static string StartLocalEnvironment([Description("Local environment number.")] int number)
    {
        if (LocalWork.ResolveEnvironment(number, out var configuration, out var environment) is { } error)
        {
            return EnvironmentTools.Serialize(new { error });
        }

        var repositories = configuration.RepositoriesOf(environment).Where(r => Directory.Exists(r.Path)).ToList();
        var scan = ProcessTable.Read(LocalServices.ApiProcesses(repositories));
        var (line, failed) = LocalServices.Execute("started", LocalServices.StackStarts(repositories.Select(LocalServices.Of), scan));
        var hosts = new List<string>();
        EnvironmentTunnel.RestoreHosts(number, hosts.Add);
        return EnvironmentTools.Serialize(new
        {
            result = line,
            tunnelHost = hosts.Count > 0 ? hosts : null,
            error = failed > 0 ? line : null,
            nextStep = "environment_status to see when the services are running",
        });
    }

    [McpServerTool(Name = "stop_local_environment", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Stops a local environment like the stop button on its column in the app: Kestrel, dev servers, app and bundler. The tunnel hosts stay on: they are turned off with disable_tunnel.")]
    public static string StopLocalEnvironment([Description("Local environment number.")] int number)
    {
        if (LocalWork.ResolveEnvironment(number, out var configuration, out var environment) is { } error)
        {
            return EnvironmentTools.Serialize(new { error });
        }

        var repositories = configuration.RepositoriesOf(environment).ToList();
        var scan = ProcessTable.Read(LocalServices.ApiProcesses(repositories));
        var (line, failed) = LocalServices.Execute("stopped", LocalServices.StackStops(repositories.Select(LocalServices.Of), scan));
        return EnvironmentTools.Serialize(new { result = line, error = failed > 0 ? line : null });
    }

    [McpServerTool(Name = "pull_repository", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("git pull --ff-only in a repository of a local environment, like the pull icon on its card in the app. Git refuses by itself, without merges or conflicts, if a fast-forward is not possible (local commits, no upstream, detached HEAD) or if local changes would be overwritten.")]
    public static Task<string> PullRepository(
        [Description("Local environment number.")] int number,
        [Description("Catalog id of the repository (see list_environments).")] string repository,
        [Description(WaitDescription)] int waitSeconds = 60,
        CancellationToken cancellationToken = default)
        => LocalWork.ResolveRepository(number, repository, out _, out var entry) is { } error
            ? Task.FromResult(EnvironmentTools.Serialize(new { error }))
            : Start(OperationCatalog.LocalPull, $"pull {entry.Id}", number, new RepositoryArgs(number, entry.Id), waitSeconds, cancellationToken);

    [McpServerTool(Name = "switch_branch", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("git switch in a repository of a local environment, like the branch drop-down on its card in the app: the branch must exist locally or on the remote (a remote-only branch gets a local branch that tracks it). Git refuses by itself if local changes would conflict with the target branch.")]
    public static Task<string> SwitchBranch(
        [Description("Local environment number.")] int number,
        [Description("Catalog id of the repository (see list_environments).")] string repository,
        [Description("Branch name, without the remote prefix.")] string branch,
        [Description(WaitDescription)] int waitSeconds = 60,
        CancellationToken cancellationToken = default)
        => LocalWork.ResolveRepository(number, repository, out _, out var entry) is { } error
            ? Task.FromResult(EnvironmentTools.Serialize(new { error }))
            : Start(OperationCatalog.LocalSwitch, $"switch {entry.Id}", number, new SwitchArgs(number, entry.Id, branch.Trim()), waitSeconds, cancellationToken);

    [McpServerTool(Name = "prune_branches", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Deletes the local branches whose remote branch no longer exists, like the cleanup icon on a card in the app: git fetch --prune, then git branch -D of the branches with a gone upstream (never the current one, never those without upstream). Irreversible for the local refs: a gone branch may hold commits never pushed. In two steps: without 'confirm' it runs the fetch and returns the branches it would delete and a code; show them to the person and call again with confirm = code only if they agree. With the code it deletes only if the candidates are still the same.")]
    public static async Task<string> PruneBranches(
        [Description("Local environment number.")] int number,
        [Description("Catalog id of the repository (see list_environments).")] string repository,
        [Description(ConfirmDescription)] string? confirm = null,
        [Description(WaitDescription)] int waitSeconds = 60,
        CancellationToken cancellationToken = default)
    {
        if (LocalWork.ResolveRepository(number, repository, out _, out var entry) is { } error)
        {
            return EnvironmentTools.Serialize(new { error });
        }

        if (!string.IsNullOrWhiteSpace(confirm))
        {
            return await Start(OperationCatalog.LocalPrune, $"prune {entry.Id}", number, new PruneArgs(number, entry.Id, confirm.Trim()), waitSeconds, cancellationToken).ConfigureAwait(false);
        }

        var plan = await LocalWork.PlanPruneAsync(entry, cancellationToken).ConfigureAwait(false);
        return EnvironmentTools.Serialize(plan.Error is not null ? new { error = plan.Error } : plan.View!);
    }

    [McpServerTool(Name = "apply_local_migrations", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Applies the pending EF migrations to the database the primary backend of a local environment points to, like the icon next to a context in the app: dotnet ef database update per context, with ASPNETCORE_ENVIRONMENT and DOTNET_ENVIRONMENT forced to Development. Only when every pending migration comes after the last applied one: with one out of order the decision goes back to the person (update_local_database_to). Refused on an environment with protectedDatabase=true. In two steps: without 'confirm' it returns clone, server, database and the migrations it would apply, with a code; show them to the person and call again with confirm = code only if they agree. With the code it runs only if the state read again is the same.")]
    public static async Task<string> ApplyLocalMigrations(
        [Description("Local environment number.")] int number,
        [Description("Only this context; empty = every context with pending migrations.")] string? context = null,
        [Description(ConfirmDescription)] string? confirm = null,
        [Description(WaitDescription)] int waitSeconds = 90,
        CancellationToken cancellationToken = default)
    {
        if (LocalWork.ResolveEnvironment(number, out _, out _) is { } error)
        {
            return EnvironmentTools.Serialize(new { error });
        }

        if (!string.IsNullOrWhiteSpace(confirm))
        {
            return await Start(OperationCatalog.LocalMigrations, "migrations", number, new LocalMigrationsArgs(number, context, confirm.Trim()), waitSeconds, cancellationToken).ConfigureAwait(false);
        }

        var plan = await LocalWork.PlanMigrationsAsync(number, context, cancellationToken).ConfigureAwait(false);
        return EnvironmentTools.Serialize(plan.Error is not null ? new { error = plan.Error } : plan.View!);
    }

    [McpServerTool(Name = "update_local_database_to", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Brings a context of the local environment's database to a given migration, like choosing a migration in the app: like EF, it reverts the applied migrations after the target (running their Down) and applies the unapplied ones up to the target, out-of-order ones included. A rollback can lose data. Refused on an environment with protectedDatabase=true. In two steps: without 'confirm' it returns server, database and the migrations it would revert and apply, with a code; show them to the person and call again with confirm = code only if they agree.")]
    public static async Task<string> UpdateLocalDatabaseTo(
        [Description("Local environment number.")] int number,
        [Description("Context name (environment_status lists the contexts).")] string context,
        [Description("Target migration id, e.g. 20260101120000_AddOrders.")] string migration,
        [Description(ConfirmDescription)] string? confirm = null,
        [Description(WaitDescription)] int waitSeconds = 90,
        CancellationToken cancellationToken = default)
    {
        if (LocalWork.ResolveEnvironment(number, out _, out _) is { } error)
        {
            return EnvironmentTools.Serialize(new { error });
        }

        if (!string.IsNullOrWhiteSpace(confirm))
        {
            return await Start(OperationCatalog.LocalUpdateTo, $"update {context}", number, new UpdateToArgs(number, context, migration.Trim(), confirm.Trim()), waitSeconds, cancellationToken).ConfigureAwait(false);
        }

        var plan = await LocalWork.PlanUpdateToAsync(number, context, migration.Trim(), cancellationToken).ConfigureAwait(false);
        return EnvironmentTools.Serialize(plan.Error is not null ? new { error = plan.Error } : plan.View!);
    }

    [McpServerTool(Name = "checkout_local_environment", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Moves every repository of a local environment onto a branch, like Reset (every repository on the main development branch) and \"Open in\" in the app. First it checks all of them, fetch included: if even one has local changes, a git operation halfway through, a detached HEAD, an action in progress or lacks the branch, nothing changes and the services keep running. Only then it stops the environment's services (app and bundler included), runs git switch and, if the branch is behind, git pull --ff-only.")]
    public static Task<string> CheckoutLocalEnvironment(
        [Description("Local environment number.")] int number,
        [Description("Branch per catalog id, e.g. { \"backend\": \"feature/123-orders\" }; repositories not listed go to the main development branch. Empty = Reset.")] Dictionary<string, string>? branch = null,
        [Description(WaitDescription)] int waitSeconds = 90,
        CancellationToken cancellationToken = default)
        => LocalWork.ResolveEnvironment(number, out _, out _) is { } error
            ? Task.FromResult(EnvironmentTools.Serialize(new { error }))
            : Start(OperationCatalog.LocalCheckout, branch is { Count: > 0 } ? "checkout" : "reset", number, new CheckoutArgs(number, branch), waitSeconds, cancellationToken);

    private static async Task<string> Start(string kind, string label, int number, object args, int waitSeconds, CancellationToken ct)
        => EnvironmentTools.Serialize(await Operations.StartAsync(kind, label, number, args, waitSeconds, ct).ConfigureAwait(false));
}

/// <summary>A plan to confirm: the error, or what to show the person with its code; <see cref="Done"/> if there is nothing to do.</summary>
internal sealed record LocalPlan(string? Error, object? View = null, string Token = "", bool Done = false, IReadOnlyList<string>? Branches = null);

/// <summary>The local environment work run by the worker process, and the plans of the two-step tools.</summary>
internal static class LocalWork
{
    private const string NextStep = "show this plan to the person; only if they agree, call the same tool again with the same arguments and confirm = this code";

    public static string? ResolveEnvironment(int number, out AppConfiguration configuration, out EnvironmentDefinition environment)
    {
        configuration = null!;
        environment = null!;
        if (EnvironmentTools.LoadConfiguration(out var error) is not { } loaded)
        {
            return error;
        }

        if (loaded.Environment(number) is not { } found)
        {
            return $"environment {number} is not configured";
        }

        if (found.Kind != EnvironmentKind.Local)
        {
            return $"environment {number} is a Docker environment: use the *_docker tools";
        }

        configuration = loaded;
        environment = found;
        return null;
    }

    public static string? ResolveRepository(int number, string? repository, out AppConfiguration configuration, out EnvironmentRepository entry)
    {
        entry = null!;
        if (ResolveEnvironment(number, out configuration, out var environment) is { } error)
        {
            return error;
        }

        var repositories = configuration.RepositoriesOf(environment);
        if (repositories.FirstOrDefault(r => string.Equals(r.Id, repository?.Trim(), StringComparison.OrdinalIgnoreCase)) is not { } found)
        {
            return $"'{repository}' is not a repository of environment {number} (repositories: {string.Join(", ", repositories.Select(r => r.Id))})";
        }

        if (!Directory.Exists(found.Path))
        {
            return $"folder {found.Path} does not exist";
        }

        entry = found;
        return null;
    }

    public static async Task<(string? Error, object? Result)> PullAsync(AppConfiguration c, RepositoryArgs args, Action<string> log, CancellationToken ct)
    {
        if (ResolveRepository(args.Number, args.Repository, out _, out var entry) is { } error)
        {
            return (error, null);
        }

        using var held = RepositoryLock.TryAcquire(entry.Path);
        if (held is null)
        {
            return (RepositoryLock.BusyMessage(entry.Path), null);
        }

        log($"git pull --ff-only in {entry.Path}");
        var outcome = await GitStatusReader.FastForwardAsync(entry.Path, log, ct).ConfigureAwait(false);
        return outcome.Success
            ? (null, new { result = "pull done", branch = (await GitStatusReader.ReadAsync(entry.Path, false, ct).ConfigureAwait(false)).Branch })
            : ($"pull refused: {outcome.Tail()}", null);
    }

    public static async Task<(string? Error, object? Result)> SwitchAsync(AppConfiguration c, SwitchArgs args, Action<string> log, CancellationToken ct)
    {
        if (ResolveRepository(args.Number, args.Repository, out _, out var entry) is { } error)
        {
            return (error, null);
        }

        using var held = RepositoryLock.TryAcquire(entry.Path);
        if (held is null)
        {
            return (RepositoryLock.BusyMessage(entry.Path), null);
        }

        // Like the drop-down: only a name among the local and remote branches.
        var status = await GitStatusReader.ReadAsync(entry.Path, false, ct).ConfigureAwait(false);
        if (status.Error is not null)
        {
            return (status.Error, null);
        }

        if (!status.AllBranches.Contains(args.Branch, StringComparer.Ordinal))
        {
            return ($"branch {args.Branch} does not exist in {entry.Id}, neither locally nor on the remote (a fetch, for example pull_repository or environment_status with fetch = true, updates the remote branches)", null);
        }

        if (string.Equals(status.Branch, args.Branch, StringComparison.Ordinal))
        {
            return (null, new { result = $"{entry.Id} is already on {args.Branch}" });
        }

        log($"git switch {args.Branch} in {entry.Path}");
        var outcome = await GitStatusReader.SwitchAsync(entry.Path, args.Branch, log, ct).ConfigureAwait(false);
        return outcome.Success
            ? (null, new { result = $"{entry.Id} on {args.Branch}", nextStep = "after a branch switch the migrations on disk may have changed: environment_status" })
            : ($"switch refused: {outcome.Tail()}", null);
    }

    public static async Task<LocalPlan> PlanPruneAsync(EnvironmentRepository entry, CancellationToken ct)
    {
        var candidates = await GitStatusReader.ReadPrunableBranchesAsync(entry.Path, cancellationToken: ct).ConfigureAwait(false);
        if (candidates.Error is not null)
        {
            return new LocalPlan(candidates.Error);
        }

        if (candidates.Branches.Count == 0)
        {
            return new LocalPlan(null, new { result = "no local branches to delete", clone = entry.Path }, Done: true);
        }

        var token = Token([entry.Path, .. candidates.Branches.Order(StringComparer.Ordinal)]);
        return new LocalPlan(
            null,
            new
            {
                environment = entry.Number,
                clone = entry.Path,
                delete = candidates.Branches,
                warning = "git branch -D is irreversible for the local refs: a branch can hold commits never pushed, recoverable only from the reflog",
                confirm = token,
                nextStep = NextStep,
            },
            token,
            Branches: candidates.Branches);
    }

    public static async Task<(string? Error, object? Result)> PruneAsync(AppConfiguration c, PruneArgs args, Action<string> log, CancellationToken ct)
    {
        if (ResolveRepository(args.Number, args.Repository, out _, out var entry) is { } error)
        {
            return (error, null);
        }

        using var held = RepositoryLock.TryAcquire(entry.Path);
        if (held is null)
        {
            return (RepositoryLock.BusyMessage(entry.Path), null);
        }

        log($"git fetch --prune in {entry.Path}");
        var plan = await PlanPruneAsync(entry, ct).ConfigureAwait(false);
        if (plan.Error is not null || plan.Done)
        {
            return (plan.Error, plan.View);
        }

        if (plan.Token != args.Confirm)
        {
            return (Changed(plan), null);
        }

        var branches = plan.Branches!;
        log($"git branch -D {string.Join(" ", branches)}");
        var outcome = await GitStatusReader.DeleteBranchesAsync(entry.Path, branches, log, ct).ConfigureAwait(false);
        return outcome.Success
            ? (null, new { result = $"{branches.Count} branches deleted", deleted = branches })
            : ($"branch cleanup failed: {outcome.Tail()}", null);
    }

    public static async Task<LocalPlan> PlanMigrationsAsync(int number, string? context, CancellationToken ct)
    {
        if (await ReadMigrationsAsync(number, ct).ConfigureAwait(false) is not { } read)
        {
            return new LocalPlan($"environment {number}: migrations not readable");
        }

        if (read.Error is not null)
        {
            return new LocalPlan(read.Error);
        }

        var (backend, report) = (read.Backend!, read.Report!);
        if (!string.IsNullOrWhiteSpace(context) && report.Contexts.All(x => x.ContextName != context))
        {
            return new LocalPlan($"context {context} not found (contexts: {string.Join(", ", report.Contexts.Select(x => x.ContextName))})");
        }

        var pending = report.Contexts
            .Where(x => string.IsNullOrWhiteSpace(context) || x.ContextName == context)
            .Where(x => x.Pending.Count > 0)
            .ToList();
        if (pending.FirstOrDefault(x => !x.CanApplyPending) is { } blocked)
        {
            return new LocalPlan(
                $"{blocked.ContextName}: {blocked.OutOfOrder.Count} pending migrations are out of order ({string.Join(", ", blocked.OutOfOrder.Select(m => m.MigrationId))}), typical of a merge from a parallel branch: "
                + "applied in sequence the schema may differ, so the decision goes back to the person; update_local_database_to brings the context to a chosen migration");
        }

        if (pending.Count == 0)
        {
            return new LocalPlan(null, new { result = "no pending migrations", server = report.Server, database = report.Database }, Done: true);
        }

        var token = Token([backend.Path, report.Server ?? string.Empty, report.Database ?? string.Empty, .. pending.SelectMany(x => x.Pending.Select(m => $"{x.ContextName}:{m.MigrationId}"))]);
        return new LocalPlan(
            null,
            new
            {
                environment = number,
                clone = backend.Path,
                server = report.Server,
                database = report.Database,
                apply = pending.Select(x => new { context = x.ContextName, migrations = x.Pending.Select(m => m.MigrationId) }),
                note = "ASPNETCORE_ENVIRONMENT and DOTNET_ENVIRONMENT are forced to Development: it is what decides which database is touched",
                confirm = token,
                nextStep = NextStep,
            },
            token);
    }

    public static async Task<(string? Error, object? Result)> ApplyMigrationsAsync(AppConfiguration c, LocalMigrationsArgs args, Action<string> log, CancellationToken ct)
    {
        var read = await ReadMigrationsAsync(args.Number, ct).ConfigureAwait(false);
        if (read is not { Error: null, Backend: { } backend, Options: { } options })
        {
            return (read?.Error ?? $"environment {args.Number}: migrations not readable", null);
        }

        using var held = RepositoryLock.TryAcquire(backend.Path);
        if (held is null)
        {
            return (RepositoryLock.BusyMessage(backend.Path), null);
        }

        var plan = await PlanMigrationsAsync(args.Number, args.Context, ct).ConfigureAwait(false);
        if (plan.Error is not null || plan.Done)
        {
            return (plan.Error, plan.View);
        }

        if (plan.Token != args.Confirm)
        {
            return (Changed(plan), null);
        }

        var report = (await ReadMigrationsAsync(args.Number, ct).ConfigureAwait(false))!.Report!;
        var applied = new List<string>();
        foreach (var status in report.Contexts.Where(x => (string.IsNullOrWhiteSpace(args.Context) || x.ContextName == args.Context) && x.Pending.Count > 0))
        {
            var definition = options.Contexts.First(d => d.Name == status.ContextName);
            log($"dotnet ef database update --context {definition.Name}");
            var outcome = await MigrationApplier.UpdateDatabaseAsync(backend.Path, definition, null, log, ct).ConfigureAwait(false);
            if (!outcome.Success)
            {
                var reason = await MigrationApplier.FailureReasonAsync(backend.Path, definition, outcome, ct).ConfigureAwait(false);
                return ($"update of {definition.Name} failed: {reason}{(applied.Count > 0 ? $" (already updated: {string.Join(", ", applied)})" : string.Empty)}", null);
            }

            applied.Add(definition.Name);
        }

        return (null, new { result = $"migrations applied: {string.Join(", ", applied)}", server = report.Server, database = report.Database });
    }

    public static async Task<LocalPlan> PlanUpdateToAsync(int number, string context, string migration, CancellationToken ct)
    {
        if (await ReadMigrationsAsync(number, ct).ConfigureAwait(false) is not { } read)
        {
            return new LocalPlan($"environment {number}: migrations not readable");
        }

        if (read.Error is not null)
        {
            return new LocalPlan(read.Error);
        }

        var (backend, report) = (read.Backend!, read.Report!);
        if (report.Contexts.FirstOrDefault(x => x.ContextName == context) is not { } status)
        {
            return new LocalPlan($"context {context} not found (contexts: {string.Join(", ", report.Contexts.Select(x => x.ContextName))})");
        }

        if (status.PlanUpdateTo(migration) is not { } plan)
        {
            return new LocalPlan($"{migration} is not among the migrations of {context}");
        }

        if (plan.IsNoOp)
        {
            return new LocalPlan(null, new { result = $"database already at {plan.Target.MigrationId}" }, Done: true);
        }

        var token = Token([backend.Path, report.Server ?? string.Empty, report.Database ?? string.Empty, context, migration, .. plan.ToRevert.Select(m => "-" + m.MigrationId), .. plan.ToApply.Select(m => "+" + m.MigrationId)]);
        return new LocalPlan(
            null,
            new
            {
                environment = number,
                clone = backend.Path,
                server = report.Server,
                database = report.Database,
                context,
                target = plan.Target.MigrationId,
                revert = plan.ToRevert.Select(m => m.MigrationId),
                apply = plan.ToApply.Select(m => m.MigrationId),
                warning = plan.IsRollback ? "a rollback runs the Down methods of the reverted migrations and can lose data" : null,
                confirm = token,
                nextStep = NextStep,
            },
            token);
    }

    public static async Task<(string? Error, object? Result)> UpdateToAsync(AppConfiguration c, UpdateToArgs args, Action<string> log, CancellationToken ct)
    {
        var read = await ReadMigrationsAsync(args.Number, ct).ConfigureAwait(false);
        if (read is not { Error: null, Backend: { } backend, Options: { } options })
        {
            return (read?.Error ?? $"environment {args.Number}: migrations not readable", null);
        }

        using var held = RepositoryLock.TryAcquire(backend.Path);
        if (held is null)
        {
            return (RepositoryLock.BusyMessage(backend.Path), null);
        }

        var plan = await PlanUpdateToAsync(args.Number, args.Context, args.Migration, ct).ConfigureAwait(false);
        if (plan.Error is not null || plan.Done)
        {
            return (plan.Error, plan.View);
        }

        if (plan.Token != args.Confirm)
        {
            return (Changed(plan), null);
        }

        var definition = options.Contexts.First(d => d.Name == args.Context);
        log($"dotnet ef database update {args.Migration} --context {definition.Name}");
        var outcome = await MigrationApplier.UpdateDatabaseAsync(backend.Path, definition, args.Migration, log, ct).ConfigureAwait(false);
        return outcome.Success
            ? (null, new { result = $"{definition.Name} at {args.Migration}" })
            : ($"update failed: {await MigrationApplier.FailureReasonAsync(backend.Path, definition, outcome, ct).ConfigureAwait(false)}", null);
    }

    public static async Task<(string? Error, object? Result)> CheckoutAsync(AppConfiguration c, CheckoutArgs args, Action<string> log, CancellationToken ct)
    {
        if (ResolveEnvironment(args.Number, out var configuration, out var environment) is { } error)
        {
            return (error, null);
        }

        var targets = args.Branches ?? [];
        var repositories = configuration.RepositoriesOf(environment);
        var unknown = targets.Keys.Where(id => repositories.All(r => !string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase))).ToList();
        if (unknown.Count > 0)
        {
            return ($"not repositories of environment {args.Number}: {string.Join(", ", unknown)} (repositories: {string.Join(", ", repositories.Select(r => r.Id))})", null);
        }

        var problems = new List<string>();
        var plan = new List<(EnvironmentRepository Entry, string Branch)>();
        var locks = new List<CrossProcessLock>();
        try
        {
            foreach (var entry in repositories)
            {
                var requested = targets.FirstOrDefault(t => string.Equals(t.Key, entry.Id, StringComparison.OrdinalIgnoreCase)).Value?.Trim();
                var branch = requested ?? GitConventions.DevelopmentBranch;

                // A missing clone does not block the reset; it blocks only if a branch is requested for it.
                if (!Directory.Exists(entry.Path))
                {
                    if (requested is not null)
                    {
                        problems.Add($"{entry.Id}: folder not found");
                    }

                    continue;
                }

                if (RepositoryLock.TryAcquire(entry.Path) is not { } held)
                {
                    problems.Add($"{entry.Id}: an action is already in progress");
                    continue;
                }

                locks.Add(held);
                log($"{entry.Id}: fetch and checks for {branch}");
                var status = await GitStatusReader.ReadAsync(entry.Path, fetch: true, ct).ConfigureAwait(false);
                if (CheckoutGuard.Problem(entry.Path, branch, status) is { } problem)
                {
                    problems.Add($"{entry.Id}: {problem}");
                    continue;
                }

                plan.Add((entry, branch));
            }

            if (problems.Count > 0)
            {
                return ($"environment {args.Number}: nothing changed, services left as they were. {string.Join("; ", problems)}", null);
            }

            var scan = ProcessTable.Read(LocalServices.ApiProcesses(repositories));
            var (stopped, _) = LocalServices.Execute("stopped", LocalServices.StackStops(repositories.Select(LocalServices.Of), scan));
            log(stopped);

            var done = new List<string>();
            var partial = new List<string>();
            foreach (var (entry, branch) in plan)
            {
                var status = await GitStatusReader.ReadAsync(entry.Path, fetch: false, ct).ConfigureAwait(false);
                if (!string.Equals(status.Branch, branch, StringComparison.Ordinal))
                {
                    log($"{entry.Id}: git switch {branch}");
                    var switched = await GitStatusReader.SwitchAsync(entry.Path, branch, log, ct).ConfigureAwait(false);
                    if (!switched.Success)
                    {
                        partial.Add($"{entry.Id}: switch failed ({switched.Tail(2)})");
                        continue;
                    }

                    status = await GitStatusReader.ReadAsync(entry.Path, fetch: false, ct).ConfigureAwait(false);
                }

                var pulled = false;
                if (status.Behind > 0)
                {
                    log($"{entry.Id}: git pull --ff-only");
                    var outcome = await GitStatusReader.FastForwardAsync(entry.Path, log, ct).ConfigureAwait(false);
                    if (!outcome.Success)
                    {
                        partial.Add($"{entry.Id}: on {branch}, pull failed ({outcome.Tail(2)})");
                        continue;
                    }

                    pulled = true;
                }

                done.Add($"{entry.Id} on {branch}{(pulled ? " (updated with pull)" : string.Empty)}");
            }

            var text = $"environment {args.Number}: {stopped}; {string.Join(", ", done.Concat(partial))}";
            return partial.Count > 0
                ? (text, null)
                : (null, new { result = text, nextStep = "the migrations on disk may have changed: environment_status, then apply_local_migrations if needed; start_local_environment to start the services again" });
        }
        finally
        {
            foreach (var held in locks)
            {
                held.Dispose();
            }
        }
    }

    private sealed record MigrationsRead(string? Error, EnvironmentRepository? Backend = null, ProbeOptions? Options = null, StatusReport? Report = null);

    // The primary backend's migrations, with the refusals that come before any plan: protected
    // database, no backend, database or configuration not readable.
    private static async Task<MigrationsRead?> ReadMigrationsAsync(int number, CancellationToken ct)
    {
        if (ResolveEnvironment(number, out var configuration, out var environment) is { } error)
        {
            return new MigrationsRead(error);
        }

        if (environment.SchemaLockReason is { } locked)
        {
            return new MigrationsRead(locked);
        }

        if (configuration.RepositoriesOf(environment).FirstOrDefault(r => r.IsPrimary && r.Type == RepositoryType.Backend) is not { Repository: BackendConfiguration settings } backend)
        {
            return new MigrationsRead($"environment {number} has no backend");
        }

        if (!Directory.Exists(backend.Path))
        {
            return new MigrationsRead($"folder {backend.Path} does not exist");
        }

        var options = AppConfiguration.ToProbeOptions(settings);
        var report = await StatusProbe.RunMigrationsAsync(backend.Path, options, ct).ConfigureAwait(false);
        return (report.ConfigurationError ?? report.DatabaseError) is { } problem
            ? new MigrationsRead(problem)
            : new MigrationsRead(null, backend, options, report);
    }

    private static string Changed(LocalPlan plan)
        => $"the confirmation code does not match the current plan (the state changed since it was shown, or the code is wrong): nothing done. Current plan, to show the person again (code {plan.Token}): "
            + System.Text.Json.JsonSerializer.Serialize(plan.View, Operations.Json);

    private static string Token(IEnumerable<string> parts)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", parts))))[..12].ToLowerInvariant();
}

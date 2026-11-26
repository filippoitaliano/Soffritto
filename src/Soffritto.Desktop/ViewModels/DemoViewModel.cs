using System.Windows.Input;
using System.Windows.Threading;
using Soffritto.Core;
using Soffritto.Core.Configuration;
using Soffritto.Desktop.Infrastructure;
using Soffritto.Presentation;

namespace Soffritto.Desktop.ViewModels;

/// <summary>
/// The Ctrl+D demo: the team behind a smartphone card game (server, web backoffice and app) with
/// three local environments and one Docker environment, PRs and stories to estimate. The
/// environments have the same shape as <see cref="MainViewModel"/>, so the window shows them with
/// the same templates. Repositories, URLs and names are made up and do not come from the
/// configuration; no command touches git, databases, Docker, tunnels, processes, note files or the
/// DevOps service: they only answer in the activity line, and the demo works even on a machine that
/// can reach nothing.
/// </summary>
public sealed class DemoViewModel : ObservableObject
{
    // A root that does not exist: no card finds a real clone.
    private const string DemoRoot = @"X:\Demo";
    private const string Me = "demo-me";
    private const string Server = "deck-server";
    private const string Backoffice = "deck-backoffice";
    private const string AppRepo = "deck-app";

    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private readonly List<(EnvironmentViewModel Environment, string[] Script)> scripts = [];
    private readonly Dictionary<RepoCardViewModel, List<string>> branches = [];

    // The answer to a click stays visible for a few ticks before the script resumes.
    private readonly Dictionary<EnvironmentViewModel, int> heldUntil = [];
    private bool isLoading;
    private int tick;

    public DemoViewModel(string appVersion)
    {
        AppVersion = appVersion;

        Environments =
        [
            Tournament(),
            DoubleDraw(),
            Review(),
            Release(),
        ];

        foreach (var environment in Environments)
        {
            environment.RefreshAllOnDev();
        }

        PullRequests = new PullRequestsViewModel(
            Environments,
            CheckoutAsync,
            ReadPullRequestsAsync,
            _ => new AsyncCommand(() => Task.CompletedTask),
            typeOf: TypeOf);
        Notifications = new PullRequestNotifications(filePath: null, notifyFirstRead: true);
        PullRequests.AlertsRead += Notifications.Update;

        Meeting = new StoryPointsMeetingViewModel(
            ReadStoriesAsync,
            async (_, _) =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(300));
                return null;
            },
            _ => new AsyncCommand(() => Task.CompletedTask),
            _ => null,
            ["GR", "MT", "SB", "ME"],
            isLight: false,
            (_, _) => { });

        RefreshAllCommand = new AsyncCommand(async () =>
        {
            foreach (var environment in Environments)
            {
                SetActivity(environment, "demo · refreshed without reading anything");
                heldUntil[environment] = tick + 2;
            }

            await PullRequests.RefreshAsync();
        });
        timer.Tick += (_, _) => Advance();
    }

    public string DemoBanner => "DEMO · made-up data · actions do nothing · Ctrl+D to exit";

    public string WindowTitle => $"{App.ProductName} · DEMO";

    // The demo always has its environments: without this property the binding would fail and the
    // setup prompt would stay visible over the fake columns.
    public string EmptyVisibility => "Collapsed";

    // The “+” works on real source clones: the demo does not have it.
    public string NewDockerVisibility => "Collapsed";

    public string AppVersion { get; }

    public IReadOnlyList<EnvironmentViewModel> Environments { get; }

    public PullRequestsViewModel PullRequests { get; }

    public PullRequestNotifications Notifications { get; }

    public StoryPointsMeetingViewModel Meeting { get; }

    public double ColumnsMinWidth => MainViewModel.MinWidthOf(Environments);

    public bool IsLoading { get => isLoading; private set => SetProperty(ref isLoading, value); }

    public ICommand RefreshAllCommand { get; }

    public void Start()
    {
        tick = 0;
        Advance();
        timer.Start();
        _ = PullRequests.RefreshAsync();
    }

    public void Stop()
    {
        timer.Stop();
        Meeting.Stop();
    }

    private static RepositoryType? TypeOf(string repository) => repository switch
    {
        Server => RepositoryType.Backend,
        Backoffice => RepositoryType.Frontend,
        AppRepo => RepositoryType.App,
        _ => null,
    };

    private void Advance()
    {
        foreach (var (environment, script) in scripts)
        {
            if (!heldUntil.TryGetValue(environment, out var until) || tick >= until)
            {
                SetActivity(environment, script[tick % script.Length]);
            }
        }

        // The backoffice dev server in the bug environment restarts on every save.
        Fe(Environments[1]).Tool!.SetState(tick % 4 < 2 ? ServiceState.WindowOnly : ServiceState.Running, "dev server");

        tick++;
    }

    // As in real environments: a pulsing light-blue dot during an action (the line ends with an
    // ellipsis), green on "ready", grey on the outcome of a finished action. The top bar follows.
    private void SetActivity(EnvironmentViewModel env, string line)
    {
        env.ActivityLine = line;

        var running = line.EndsWith('…');
        if (running != env.IsLoading)
        {
            if (running)
            {
                env.BeginOperation();
            }
            else
            {
                env.EndOperation();
            }
        }

        IsLoading = Environments.Any(e => e.IsLoading);
    }

    // All good: the tournament story is tested from the phone, through the tunnel.
    private EnvironmentViewModel Tournament()
    {
        var env = Create(
            1,
            EnvironmentKind.Local,
            "US 312 Weekend tournament: testing from the app on the phone, tunnel on.",
            StatusText.IdleActivity,
            "git fetch…",
            StatusText.IdleActivity,
            "migrations up to date",
            StatusText.IdleActivity);

        Git(Be(env), "feature/312-weekend-tournament", "2 files modified", "dev", "main");
        Git(Fe(env), "dev", "working tree clean", "main");
        Git(Ap(env), "feature/312-weekend-tournament", "working tree clean · 1 commit to push", "dev", "main");
        Db(Be(env), "deck_dev1", "localhost");

        Running(Be(env).Tool!, "Kestrel");
        Running(Fe(env).Tool!, "dev server");
        Running(Ap(env).Tool!, "app (Expo / Metro)");
        Tunnel(env, "quiet-river-4k2p", connected: true, (Be(env), 5001, true), (Fe(env), 5173, true));

        Context(env, "GameDbContext", (MigrationLabels.Applied, 46), (MigrationLabels.Missing, 0));
        Context(env, "IdentityDbContext", (MigrationLabels.Applied, 9), (MigrationLabels.Missing, 0));
        return env;
    }

    // The bug being worked on: two new migrations to apply and code to push.
    private EnvironmentViewModel DoubleDraw()
    {
        var env = Create(
            2,
            EnvironmentKind.Local,
            "Bug 327: after reconnecting, the drawn card shows up twice in the hand.",
            "database update 1/2: LegendaryLimit…",
            "database update 2/2: DrawHistory…",
            StatusText.IdleActivity,
            "dotnet build…",
            StatusText.IdleActivity);

        Git(Be(env), "bugfix/327-double-draw", "4 files modified · 2 commits to push", "dev", "main");
        Git(Fe(env), "dev", "3 to pull · working tree clean", "main");
        Git(Ap(env), "bugfix/327-double-draw", "1 file modified", "dev");
        Db(Be(env), "deck_dev2", "localhost");

        Running(Be(env).Tool!, "Kestrel");
        Running(Fe(env).Tool!, "dev server");
        Stopped(Ap(env).Tool!, "app (Expo / Metro)");

        Context(env, "GameDbContext", (MigrationLabels.Applied, 44), (MigrationLabels.Missing, 2));
        Context(env, "IdentityDbContext", (MigrationLabels.Applied, 9), (MigrationLabels.Missing, 0));
        return env;
    }

    // Reviewing PRs before the release: a branch behind, a migration out of order and a tunnel
    // that cannot be found.
    private EnvironmentViewModel Review()
    {
        var env = Create(
            3,
            EnvironmentKind.Local,
            "Review PRs 418 and 421 before 1.9: the database is shared, no manual updates.",
            "git fetch…",
            "dev tunnel not available",
            StatusText.IdleActivity);

        Git(Be(env), "feature/305-legendary-cards", "23 to pull · working tree clean", "dev", "main");
        Be(env).UpstreamWarning = "no upstream configured";
        Git(Fe(env), "feature/330-card-editor", "working tree clean", "dev", "main");
        Git(Ap(env), "dev", "working tree clean", "main");
        Db(Be(env), "deck_review", "sql-dev-shared");

        Fe(env).ErrorLine = "npm ERR! ERESOLVE: vite@6 requires a newer version of node";
        Fe(env).ErrorVisibility = "Visible";

        Running(Be(env).Tool!, "Kestrel");
        Stopped(Fe(env).Tool!, "dev server");
        Stopped(Ap(env).Tool!, "app (Expo / Metro)");

        env.SetTunnelOn(true);
        Be(env).TunnelShared = true;
        var tunnel = Be(env).Tunnel!.State;
        tunnel.Line = "dev tunnel not available";
        tunnel.Detail = "No tunnel matches deck-server-3. Tunnels on the account:";
        tunnel.DetailVisibility = "Visible";
        tunnel.List.Add(new TunnelSummaryRowViewModel("quiet-river-4k2p · environment 1", "host connected · 2 ports", StatusBadge.OkColor));
        tunnel.List.Add(new TunnelSummaryRowViewModel("bold-hill-7m3x · (no description)", "host not connected · 0 ports", StatusBadge.ErrorColor));
        tunnel.ListVisibility = "Visible";
        tunnel.Stamp = Stamp();
        Be(env).Tunnel!.Host.SetState(ServiceState.Stopped, "tunnel host");
        Be(env).Tunnel!.Host.SetActions(canStart: false, canStop: false, "No tunnel to host");

        Be(env).Migrations!.Unknown.Add(new MigrationRowViewModel("20261118091500_ShopSaleTest", MigrationLabels.Unknown));
        Be(env).Migrations!.UnknownHeader = "1 migration on the database that the code does not know";
        Be(env).Migrations!.UnknownVisibility = "Visible";

        Context(env, "GameDbContext", (MigrationLabels.Applied, 41), (MigrationLabels.OutOfOrder, 1), (MigrationLabels.Missing, 2));
        Context(env, "IdentityDbContext", (MigrationLabels.Applied, 9), (MigrationLabels.Missing, 0));
        return env;
    }

    // Release acceptance testing in a throwaway Docker environment: database from the seed and containers.
    private EnvironmentViewModel Release()
    {
        var env = Create(
            4,
            EnvironmentKind.Docker,
            "Acceptance testing release/1.9 on a database from the seed: thrown away after the release.",
            StatusText.IdleActivity,
            "docker ps…",
            StatusText.IdleActivity);

        Git(Be(env), "release/1.9", "working tree clean", "dev", "main");
        Git(Fe(env), "release/1.9", "working tree clean", "dev", "main");
        Git(Ap(env), "release/1.9", "working tree clean", "dev", "main");
        Db(Be(env), "deck", "db");

        Running(Be(env).Tool!, "api");
        Running(Fe(env).Tool!, "web");
        Stopped(Ap(env).Tool!, "app (Expo / Metro)");
        Tunnel(env, "brave-dune-9q1s", connected: true, (Be(env), 24401, true), (Fe(env), 24402, true));

        env.Docker!.ApplyPhase(new DockerReadiness.Snapshot(
            DockerReadiness.Phase.Ready,
            [
                new DockerReadiness.Service("db", DockerReadiness.ServiceState.Ready, string.Empty),
                new DockerReadiness.Service("api", DockerReadiness.ServiceState.Ready, string.Empty),
                new DockerReadiness.Service("web", DockerReadiness.ServiceState.Ready, string.Empty),
            ],
            "ready",
            null));
        env.Docker.DestroyCommand = Say(env, "demo · “Destroy environment” does not touch containers, worktrees or ports");
        env.Docker.RepairEnvCommand = Say(env, "demo · env files not rewritten");
        env.OperationLog!.Append("worktrees created from release/1.9 for deck-server, deck-backoffice and deck-app");
        env.OperationLog.Append("database restored from the seed and migrated: 46 migrations");
        env.OperationLog.Append("db, api and web containers started");

        Context(env, "GameDbContext", (MigrationLabels.Applied, 46), (MigrationLabels.Missing, 0));
        Context(env, "IdentityDbContext", (MigrationLabels.Applied, 9), (MigrationLabels.Missing, 0));
        return env;
    }

    // The note stays in memory: it can be edited, but the next demo starts again from here.
    private EnvironmentViewModel Create(int number, EnvironmentKind kind, string note, params string[] script)
    {
        var definition = new EnvironmentDefinition { Number = number, Kind = kind, Repositories = ["backend", "frontend", "app"] };
        var env = new EnvironmentViewModel(
            new EnvironmentLayout(
                number,
                [
                    DemoLocation(definition, new BackendConfiguration { Solution = "StarDeck.sln", DevTunnel = new DevTunnelConfiguration() }, Server),
                    DemoLocation(definition, new FrontendConfiguration { DevTunnel = new DevTunnelConfiguration(), ExtraCommand = "npm run storybook" }, Backoffice),
                    DemoLocation(definition, new AppRepoConfiguration(), AppRepo),
                ],
                kind),
            () => { },
            () => { },
            demoNote: note);

        env.RefreshCommand = Say(env, $"demo · environment {number} refreshed without reading anything");
        env.ResetCommand = new AsyncCommand(async () =>
        {
            await CheckoutAsync(number, []);
            SetActivity(env, "demo · simulated reset: everything on dev, nothing actually stopped");
        });
        env.TunnelCommand = new AsyncCommand(() =>
        {
            env.SetTunnelOn(!env.TunnelOn);
            SetActivity(env, env.TunnelOn ? "demo · tunnel claimed for pretend" : "demo · tunnel ports released for pretend");
            heldUntil[env] = tick + 2;
            return Task.CompletedTask;
        });

        foreach (var card in env.Repos)
        {
            card.Preview = null;
            card.FetchCommand = Say(env, "demo · git fetch not run");
            card.PullCommand = Say(env, "demo · git pull not run");
            card.PruneCommand = Say(env, "demo · no branches deleted");
            card.RepoStamp = Stamp();
            card.Tool!.OpenCommand = Say(env, "demo · the editor stays closed");
            card.Tool.StartCommand = Say(env, "demo · simulated start, no process launched");
            card.Tool.StopCommand = Say(env, "demo · simulated stop, no process stopped");
        }

        Fe(env).Tool!.ExtraText = "Storybook";
        Fe(env).Tool!.ExtraToolTip = "npm run storybook in a console (does not run in the demo)";
        Fe(env).Tool!.ExtraCommand = Say(env, "demo · storybook not started");
        Be(env).Tunnel!.Host.StartCommand = Say(env, "demo · tunnel host not started");
        Be(env).Tunnel!.Host.StopCommand = Say(env, "demo · tunnel host not stopped");
        Fe(env).Tunnel!.Host.StartCommand = Say(env, "demo · tunnel host not started");
        Fe(env).Tunnel!.Host.StopCommand = Say(env, "demo · tunnel host not stopped");
        env.Tools.Stack.StartCommand = Say(env, "demo · simulated environment start");
        env.Tools.Stack.StopCommand = Say(env, "demo · simulated environment stop");
        env.Tools.Stack.SetActions(canStart: true, canStop: false, "Start backend, frontend and tunnel (nothing runs in the demo)");

        scripts.Add((env, script));
        return env;
    }

    private static RepoLocation DemoLocation(EnvironmentDefinition definition, RepositoryConfiguration settings, string repository)
    {
        settings.Id = AppConfiguration.TypeName(settings.Type);
        settings.Folder = $"{repository}-{AppConfiguration.NumberPlaceholder}";
        settings.CloneUrl = $"https://git.example/star-deck/{repository}.git";
        var path = $@"{DemoRoot}\{repository}-{definition.Number}";
        return new RepoLocation(new EnvironmentRepository(settings, definition, path, IsPrimary: true));
    }

    private static RepoCardViewModel Be(EnvironmentViewModel env) => env.Card(RepositoryType.Backend)!;

    private static RepoCardViewModel Fe(EnvironmentViewModel env) => env.Card(RepositoryType.Frontend)!;

    private static RepoCardViewModel Ap(EnvironmentViewModel env) => env.Card(RepositoryType.App)!;

    private void Git(RepoCardViewModel card, string branch, string detail, params string[] others)
    {
        var names = new List<string> { branch };
        names.AddRange(others.Where(o => o != branch));
        branches[card] = names;

        card.RepoDetail = detail;
        card.PullToolTip = "Pull does not run in the demo";
        card.BranchPicker.SetBranches(branch, names);

        // The choice "succeeds" right away: the chosen branch becomes the current one, without git switch.
        card.BranchPicker.BranchSelected = chosen => card.BranchPicker.SetBranches(chosen, names);
    }

    // “Open in” for the fake PRs: the branches change in the cards, git knows nothing about it.
    private async Task<ActionOutcome> CheckoutAsync(int number, IReadOnlyList<(string Repository, string Branch)> targets)
    {
        var env = Environments.First(e => e.Number == number);
        SetActivity(env, "Open in: fetching and switching branches…");
        heldUntil[env] = int.MaxValue;
        await Task.Delay(TimeSpan.FromSeconds(1.2));

        foreach (var card in env.Repos.Where(c => branches.ContainsKey(c)))
        {
            var branch = targets
                .Where(t => string.Equals(t.Repository, card.RepositoryName, StringComparison.OrdinalIgnoreCase))
                .Select(t => t.Branch)
                .FirstOrDefault() ?? GitConventions.DevelopmentBranch;
            var names = branches[card];
            if (!names.Contains(branch))
            {
                names.Add(branch);
            }

            card.BranchPicker.SetBranches(branch, names);
            card.RepoDetail = "working tree clean";
        }

        env.RefreshAllOnDev();
        env.OperationLog?.Append($"demo · Open in: {targets.Count} branches");
        SetActivity(env, "demo · PR branches in place, git was not touched");
        heldUntil[env] = tick + 2;
        return ActionOutcome.Done($"Environment {number}: {targets.Count} branches switched only in the demo");
    }

    // Season 2 planning: three stories of one feature, one of another and a bug.
    private static async Task<StoriesToEstimate> ReadStoriesAsync()
    {
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        return new StoriesToEstimate(
        [
            new StoryToEstimate(341, "Season pass with 30 reward tiers", "Players level up by playing ranked matches." + Environment.NewLine + "Each tier unlocks a reward: coins, card backs or a season card.", 340, "Season 2: the Dragon Deck"),
            new StoryToEstimate(342, "Daily quests", "Three quests a day (win 2 matches, play 10 fire cards, ...)." + Environment.NewLine + "They reset at midnight in the player's time zone.", 340, "Season 2: the Dragon Deck"),
            new StoryToEstimate(343, "Push notification at season end", "Three days before the end, a notification lists the tiers still to unlock.", 340, "Season 2: the Dragon Deck"),
            new StoryToEstimate(351, "Private match with invite code", "Whoever creates the match gets a 6-character code to send to a friend.", 350, "Friends mode"),
            new StoryToEstimate(360, "Turn timer keeps running after the opponent quits", "1. Start a ranked match." + Environment.NewLine + "2. The opponent closes the app during their turn." + Environment.NewLine + "3. The timer keeps running and the win only comes after 90 seconds.", null, string.Empty, IsBug: true),
        ]);
    }

    // My PRs in various states (draft with changes requested and comments to answer, published and
    // waiting, approved) and colleagues' PRs to review, one without a work item.
    private static async Task<MyPullRequests> ReadPullRequestsAsync()
    {
        await Task.Delay(TimeSpan.FromMilliseconds(700));

        var tournament = new PullRequestWorkItem(312, "User Story", "Weekend tournament");
        var doubleDraw = new PullRequestWorkItem(327, "Bug", "Double draw after reconnecting");
        var legendary = new PullRequestWorkItem(305, "User Story", "Legendary cards");
        var editor = new PullRequestWorkItem(330, "User Story", "Card editor in the backoffice");

        var giulia = new PullRequestVote("demo-giulia", "Giulia Rinaldi", -5, IsRequired: true, IsGroup: false);
        var marco = new PullRequestVote("demo-marco", "Marco Tosi", 10, IsRequired: false, IsGroup: false);
        var sara = new PullRequestVote("demo-sara", "Sara Bellini", 0, IsRequired: true, IsGroup: false);
        var me = new PullRequestVote(Me, "Me", 0, IsRequired: true, IsGroup: false);

        var created = new List<PullRequestSummary>
        {
            Pr(412, "Weekend tournament: leaderboard and prizes", Server, "Me", "feature/312-weekend-tournament", 3, [giulia, marco], tournament, draft: true) with
            {
                ActiveThreads = [new PullRequestThread("demo-giulia", [Me, "demo-giulia"]), new PullRequestThread("demo-giulia", ["demo-giulia"])],
            },
            Pr(415, "Weekend tournament: sign-up from the app", AppRepo, "Me", "feature/312-weekend-tournament", 2, [marco], tournament),
            Pr(419, "Double draw: idempotency on reconnect", Server, "Me", "bugfix/327-double-draw", 1, [sara], doubleDraw),
        };

        var toReview = new List<PullRequestSummary>
        {
            Pr(418, "Legendary cards: limit of one per deck", Server, "Giulia Rinaldi", "feature/305-legendary-cards", 4, [me, marco], legendary),
            Pr(421, "Card editor: rarity preview", Backoffice, "Marco Tosi", "feature/330-card-editor", 2, [me, sara], editor),
            Pr(423, "Shop: updated terms text", AppRepo, "Sara Bellini", "fix/shop-terms-text", 1, [me with { Vote = 10 }], null),
        };

        return new MyPullRequests(created, toReview, Me);
    }

    private static PullRequestSummary Pr(int id, string title, string repository, string author, string branch, int days, PullRequestVote[] reviewers, PullRequestWorkItem? workItem, bool draft = false)
        => new(id, title, repository, repository, author, branch, draft, DateTime.UtcNow.AddDays(-days).AddHours(-1), new Uri($"https://git.example/star-deck/pullrequest/{id}"), reviewers)
        {
            WorkItem = workItem,
        };

    private static void Db(RepoCardViewModel card, string database, string server)
    {
        card.Database = database;
        card.Server = server;
    }

    private static void Running(ToolGroupViewModel tool, string name)
    {
        tool.SetState(ServiceState.Running, name);
        tool.SetActions(canStart: false, canStop: true, string.Empty);
    }

    private static void Stopped(ToolGroupViewModel tool, string name)
    {
        tool.SetState(ServiceState.Stopped, name);
        tool.SetActions(canStart: true, canStop: false, "Start (does not run in the demo)");
    }

    // One pool tunnel for the environment: one port for each repository that exposes one.
    private static void Tunnel(EnvironmentViewModel env, string tunnelId, bool connected, params (RepoCardViewModel Card, int Port, bool? Reachable)[] ports)
    {
        env.SetTunnelOn(true);
        foreach (var group in ports.GroupBy(p => p.Card))
        {
            group.Key.TunnelShared = true;
            Tunnel(group.Key.Tunnel!.State, group.Key.Tunnel!.Host, tunnelId, connected, group.Select(p => (p.Port, p.Reachable)).ToArray());
        }
    }

    private static void Tunnel(TunnelViewModel tunnel, ToolGroupViewModel host, string tunnelId, bool connected, (int Port, bool? Reachable)[] ports)
    {
        tunnel.Line = tunnelId;
        tunnel.DetailVisibility = "Collapsed";
        tunnel.Stamp = Stamp();

        foreach (var (port, reachable) in ports)
        {
            var url = $"https://{tunnelId}-{port}.euw.devtunnels.ms";
            var row = new TunnelPortRowViewModel(port, url, reachable) { CopyToolTip = "Nothing is copied in the demo", OpenToolTip = "Nothing opens in the demo" };
            row.CopyCommand = new AsyncCommand(async () =>
            {
                row.Kind = StatusText.Copied;
                await Task.Delay(1500);
                row.Kind = row.State;
            });
            row.OpenCommand = new AsyncCommand(() => Task.CompletedTask);
            tunnel.Ports.Add(row);
        }

        tunnel.PortsVisibility = "Visible";

        host.SetState(connected ? ServiceState.Running : ServiceState.Stopped, "tunnel host");
        host.SetActions(canStart: !connected, canStop: connected, "Start the tunnel host (does not run in the demo)");
    }

    // Migrations are listed newest first, numbered by their position in the context as
    // ViewBuilder does; the groups go from oldest to newest.
    private void Context(EnvironmentViewModel env, string name, params (string State, int Count)[] groups)
    {
        var names = new[]
        {
            "AddTournamentsTable", "TournamentLeaderboard", "TournamentPrizes", "DeckIndex",
            "CardRarity", "LegendaryLimit", "DrawHistory", "TurnDrawIndex",
        };

        var states = groups.SelectMany(g => Enumerable.Repeat(g.State, g.Count)).ToList();
        var total = states.Count;
        var applied = states.Count(s => s != MigrationLabels.Missing);
        var pending = total - applied;
        var outOfOrder = states.Count(s => s == MigrationLabels.OutOfOrder);

        var row = new ContextRowViewModel(
            name,
            isUpToDate: pending == 0 && outOfOrder == 0,
            pending == 0 && outOfOrder == 0 ? StatusBadge.OkColor : StatusBadge.WarningColor)
        {
            MissingCount = pending,
            OutOfOrderCount = outOfOrder,
            Note = outOfOrder > 0 ? "A migration from another branch is applied before the missing ones." : string.Empty,
            Stamp = Stamp(),
        };

        var rows = new List<RecentMigrationRowViewModel>(total);
        for (var position = total; position >= 1; position--)
        {
            var state = states[position - 1];
            rows.Add(new RecentMigrationRowViewModel(position, names[position % names.Length], state)
            {
                RunGlyph = state == MigrationLabels.Missing ? "\uE768" : "\uE7A7",
                RunToolTip = "Update does not run in the demo",
                CanRun = position != applied,
                RunCommand = Say(env, $"demo · update up to migration {position} not run"),
            });
        }

        row.SetRecent(rows);
        Be(env).Migrations!.Contexts.Add(row);
    }

    private AsyncCommand Say(EnvironmentViewModel env, string line) => new(() =>
    {
        env.OperationLog?.Append(line);
        SetActivity(env, line);
        heldUntil[env] = tick + 2;
        return Task.CompletedTask;
    });

    private static string Stamp() => StatusText.Stamp(DateTimeOffset.Now);
}

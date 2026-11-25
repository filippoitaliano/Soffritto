using System.Windows.Input;
using System.Windows.Threading;
using Soffritto.Core;
using Soffritto.Core.Configuration;
using Soffritto.Desktop.Infrastructure;
using Soffritto.Presentation;

namespace Soffritto.Desktop.ViewModels;

/// <summary>
/// La demo di Ctrl+D: il team di un gioco di carte per smartphone (server, backoffice web e app)
/// con tre ambienti locali e uno Docker, PR e storie da stimare. Gli ambienti hanno la stessa
/// forma di <see cref="MainViewModel"/>, cosi' la finestra li mostra con gli stessi template.
/// Repository, URL e nomi sono inventati e non vengono dalla configurazione; nessun comando tocca
/// git, database, Docker, tunnel, processi, file delle note o il servizio DevOps: rispondono solo
/// nella riga di attivita', e la demo funziona anche su una macchina che non raggiunge niente.
/// </summary>
public sealed class DemoViewModel : ObservableObject
{
    // Una radice che non esiste: nessuna card trova un clone vero.
    private const string DemoRoot = @"X:\Demo";
    private const string Me = "demo-io";
    private const string Server = "mazzo-server";
    private const string Backoffice = "mazzo-backoffice";
    private const string AppRepo = "mazzo-app";

    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private readonly List<(EnvironmentViewModel Environment, string[] Script)> scripts = [];
    private readonly Dictionary<RepoCardViewModel, List<string>> branches = [];

    // La risposta a un clic resta in vista per qualche giro prima che il copione riprenda.
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
            ["GR", "MT", "SB", "IO"],
            isLight: false,
            (_, _) => { });

        RefreshAllCommand = new AsyncCommand(async () =>
        {
            foreach (var environment in Environments)
            {
                SetActivity(environment, "demo · riletto senza leggere niente");
                heldUntil[environment] = tick + 2;
            }

            await PullRequests.RefreshAsync();
        });
        timer.Tick += (_, _) => Advance();
    }

    public string DemoBanner => "DEMO · dati inventati · le azioni non eseguono niente · Ctrl+D per uscire";

    public string WindowTitle => $"{App.ProductName} · DEMO";

    // La demo ha sempre i suoi ambienti: senza questa proprieta' il binding fallirebbe e
    // l'invito al setup resterebbe visibile sopra le colonne finte.
    public string EmptyVisibility => "Collapsed";

    // Il «+» lavora sui cloni sorgente veri: in demo non c'e'.
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

        // Il dev server del backoffice nell'ambiente del bug si riavvia a ogni salvataggio.
        Fe(Environments[1]).Tool!.SetState(tick % 4 < 2 ? ServiceState.WindowOnly : ServiceState.Running, "dev server");

        tick++;
    }

    // Come negli ambienti veri: pallino azzurro pulsante durante un'azione (la riga finisce coi
    // puntini), verde su "pronto", grigio sull'esito di un'azione finita. La barra in alto segue.
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

    // Tutto in ordine: la storia del torneo si prova dal telefono, attraverso il tunnel.
    private EnvironmentViewModel Tournament()
    {
        var env = Create(
            1,
            EnvironmentKind.Local,
            "US 312 Torneo del weekend: si prova dall'app sul telefono, tunnel acceso.",
            StatusText.IdleActivity,
            "git fetch…",
            StatusText.IdleActivity,
            "migrazioni allineate",
            StatusText.IdleActivity);

        Git(Be(env), "feature/312-torneo-weekend", "2 file modificati", "dev", "main");
        Git(Fe(env), "dev", "working tree pulito", "main");
        Git(Ap(env), "feature/312-torneo-weekend", "working tree pulito · 1 commit da pushare", "dev", "main");
        Db(Be(env), "mazzo_dev1", "localhost");

        Running(Be(env).Tool!, "Kestrel");
        Running(Fe(env).Tool!, "dev server");
        Running(Ap(env).Tool!, "app (Expo / Metro)");
        Tunnel(env, "quiet-river-4k2p", connected: true, (Be(env), 5001, true), (Fe(env), 5173, true));

        Context(env, "GameDbContext", (MigrationLabels.Applied, 46), (MigrationLabels.Missing, 0));
        Context(env, "IdentityDbContext", (MigrationLabels.Applied, 9), (MigrationLabels.Missing, 0));
        return env;
    }

    // Il bug in lavorazione: due migrazioni nuove da applicare e codice da pushare.
    private EnvironmentViewModel DoubleDraw()
    {
        var env = Create(
            2,
            EnvironmentKind.Local,
            "Bug 327: dopo la riconnessione la carta pescata compare due volte nella mano.",
            "database update 1/2: LimiteLeggendarie…",
            "database update 2/2: StoricoPescate…",
            StatusText.IdleActivity,
            "dotnet build…",
            StatusText.IdleActivity);

        Git(Be(env), "bugfix/327-pescata-doppia", "4 file modificati · 2 commit da pushare", "dev", "main");
        Git(Fe(env), "dev", "3 da pullare · working tree pulito", "main");
        Git(Ap(env), "bugfix/327-pescata-doppia", "1 file modificato", "dev");
        Db(Be(env), "mazzo_dev2", "localhost");

        Running(Be(env).Tool!, "Kestrel");
        Running(Fe(env).Tool!, "dev server");
        Stopped(Ap(env).Tool!, "app (Expo / Metro)");

        Context(env, "GameDbContext", (MigrationLabels.Applied, 44), (MigrationLabels.Missing, 2));
        Context(env, "IdentityDbContext", (MigrationLabels.Applied, 9), (MigrationLabels.Missing, 0));
        return env;
    }

    // La review delle PR prima del rilascio: branch indietro, una migrazione fuori ordine e il
    // tunnel che non si trova.
    private EnvironmentViewModel Review()
    {
        var env = Create(
            3,
            EnvironmentKind.Local,
            "Review PR 418 e 421 prima della 1.9: il database e' condiviso, niente update a mano.",
            "git fetch…",
            "dev tunnel non disponibile",
            StatusText.IdleActivity);

        Git(Be(env), "feature/305-carte-leggendarie", "23 da pullare · working tree pulito", "dev", "main");
        Be(env).UpstreamWarning = "nessun upstream configurato";
        Git(Fe(env), "feature/330-editor-carte", "working tree pulito", "dev", "main");
        Git(Ap(env), "dev", "working tree pulito", "main");
        Db(Be(env), "mazzo_review", "sql-dev-condiviso");

        Fe(env).ErrorLine = "npm ERR! ERESOLVE: vite@6 richiede una versione di node piu' recente";
        Fe(env).ErrorVisibility = "Visible";

        Running(Be(env).Tool!, "Kestrel");
        Stopped(Fe(env).Tool!, "dev server");
        Stopped(Ap(env).Tool!, "app (Expo / Metro)");

        env.SetTunnelOn(true);
        Be(env).TunnelShared = true;
        var tunnel = Be(env).Tunnel!.State;
        tunnel.Line = "dev tunnel non disponibile";
        tunnel.Detail = "Nessun tunnel corrisponde a mazzo-server-3. Tunnel dell'account:";
        tunnel.DetailVisibility = "Visible";
        tunnel.List.Add(new TunnelSummaryRowViewModel("quiet-river-4k2p · ambiente 1", "host connesso · 2 porte", StatusBadge.OkColor));
        tunnel.List.Add(new TunnelSummaryRowViewModel("bold-hill-7m3x · (senza descrizione)", "host non connesso · 0 porte", StatusBadge.ErrorColor));
        tunnel.ListVisibility = "Visible";
        tunnel.Stamp = Stamp();
        Be(env).Tunnel!.Host.SetState(ServiceState.Stopped, "host del tunnel");
        Be(env).Tunnel!.Host.SetActions(canStart: false, canStop: false, "Nessun tunnel da ospitare");

        Be(env).Migrations!.Unknown.Add(new MigrationRowViewModel("20261118091500_ProvaSaldiShop", MigrationLabels.Unknown));
        Be(env).Migrations!.UnknownHeader = "1 migrazione sul database che il codice non conosce";
        Be(env).Migrations!.UnknownVisibility = "Visible";

        Context(env, "GameDbContext", (MigrationLabels.Applied, 41), (MigrationLabels.OutOfOrder, 1), (MigrationLabels.Missing, 2));
        Context(env, "IdentityDbContext", (MigrationLabels.Applied, 9), (MigrationLabels.Missing, 0));
        return env;
    }

    // Il collaudo della release in un ambiente Docker usa e getta: database dal seed e container.
    private EnvironmentViewModel Release()
    {
        var env = Create(
            4,
            EnvironmentKind.Docker,
            "Collaudo release/1.9 su database dal seed: si butta via dopo il rilascio.",
            StatusText.IdleActivity,
            "docker ps…",
            StatusText.IdleActivity);

        Git(Be(env), "release/1.9", "working tree pulito", "dev", "main");
        Git(Fe(env), "release/1.9", "working tree pulito", "dev", "main");
        Git(Ap(env), "release/1.9", "working tree pulito", "dev", "main");
        Db(Be(env), "mazzo", "db");

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
            "pronto",
            null));
        env.Docker.DestroyCommand = Say(env, "demo · «Distruggi ambiente» non tocca container, worktree e porte");
        env.Docker.RepairEnvCommand = Say(env, "demo · env non riscritti");
        env.OperationLog!.Append("worktree creati da release/1.9 per mazzo-server, mazzo-backoffice e mazzo-app");
        env.OperationLog.Append("database ripristinato dal seed e migrato: 46 migrazioni");
        env.OperationLog.Append("container db, api e web accesi");

        Context(env, "GameDbContext", (MigrationLabels.Applied, 46), (MigrationLabels.Missing, 0));
        Context(env, "IdentityDbContext", (MigrationLabels.Applied, 9), (MigrationLabels.Missing, 0));
        return env;
    }

    // La nota resta in memoria: si puo' scrivere, ma alla demo successiva riparte da qui.
    private EnvironmentViewModel Create(int number, EnvironmentKind kind, string note, params string[] script)
    {
        var definition = new EnvironmentDefinition { Number = number, Kind = kind, Repositories = ["backend", "frontend", "app"] };
        var env = new EnvironmentViewModel(
            new EnvironmentLayout(
                number,
                [
                    DemoLocation(definition, new BackendConfiguration { Solution = "MazzoStellare.sln", DevTunnel = new DevTunnelConfiguration() }, Server),
                    DemoLocation(definition, new FrontendConfiguration { DevTunnel = new DevTunnelConfiguration(), ExtraCommand = "npm run storybook" }, Backoffice),
                    DemoLocation(definition, new AppRepoConfiguration(), AppRepo),
                ],
                kind),
            () => { },
            () => { },
            demoNote: note);

        env.RefreshCommand = Say(env, $"demo · ambiente {number} riletto senza leggere niente");
        env.ResetCommand = new AsyncCommand(async () =>
        {
            await CheckoutAsync(number, []);
            SetActivity(env, "demo · reset simulato: tutto su dev, niente fermato davvero");
        });
        env.TunnelCommand = new AsyncCommand(() =>
        {
            env.SetTunnelOn(!env.TunnelOn);
            SetActivity(env, env.TunnelOn ? "demo · tunnel reclamato per finta" : "demo · porte del tunnel rilasciate per finta");
            heldUntil[env] = tick + 2;
            return Task.CompletedTask;
        });

        foreach (var card in env.Repos)
        {
            card.Preview = null;
            card.FetchCommand = Say(env, "demo · git fetch non eseguito");
            card.PullCommand = Say(env, "demo · git pull non eseguito");
            card.PruneCommand = Say(env, "demo · nessun branch cancellato");
            card.RepoStamp = Stamp();
            card.Tool!.OpenCommand = Say(env, "demo · l'editor resta chiuso");
            card.Tool.StartCommand = Say(env, "demo · avvio simulato, nessun processo partito");
            card.Tool.StopCommand = Say(env, "demo · arresto simulato, nessun processo fermato");
        }

        Fe(env).Tool!.ExtraText = "Storybook";
        Fe(env).Tool!.ExtraToolTip = "npm run storybook in una console (in demo non parte)";
        Fe(env).Tool!.ExtraCommand = Say(env, "demo · storybook non avviato");
        Be(env).Tunnel!.Host.StartCommand = Say(env, "demo · host del tunnel non avviato");
        Be(env).Tunnel!.Host.StopCommand = Say(env, "demo · host del tunnel non fermato");
        Fe(env).Tunnel!.Host.StartCommand = Say(env, "demo · host del tunnel non avviato");
        Fe(env).Tunnel!.Host.StopCommand = Say(env, "demo · host del tunnel non fermato");
        env.Tools.Stack.StartCommand = Say(env, "demo · avvio dell'ambiente simulato");
        env.Tools.Stack.StopCommand = Say(env, "demo · arresto dell'ambiente simulato");
        env.Tools.Stack.SetActions(canStart: true, canStop: false, "Avvia backend, frontend e tunnel (in demo non parte niente)");

        scripts.Add((env, script));
        return env;
    }

    private static RepoLocation DemoLocation(EnvironmentDefinition definition, RepositoryConfiguration settings, string repository)
    {
        settings.Id = AppConfiguration.TypeName(settings.Type);
        settings.Folder = $"{repository}-{AppConfiguration.NumberPlaceholder}";
        settings.CloneUrl = $"https://git.example/mazzo-stellare/{repository}.git";
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
        card.PullToolTip = "In demo il pull non parte";
        card.BranchPicker.SetBranches(branch, names);

        // La scelta "riesce" subito: il branch scelto diventa il corrente, senza git switch.
        card.BranchPicker.BranchSelected = chosen => card.BranchPicker.SetBranches(chosen, names);
    }

    // «Apri in» delle PR finte: i branch cambiano nelle card, git non ne sa niente.
    private async Task<string> CheckoutAsync(int number, IReadOnlyList<(string Repository, string Branch)> targets)
    {
        var env = Environments.First(e => e.Number == number);
        SetActivity(env, "Apri in: fetch e switch dei branch…");
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
            card.RepoDetail = "working tree pulito";
        }

        env.RefreshAllOnDev();
        env.OperationLog?.Append($"demo · Apri in: {targets.Count} branch");
        SetActivity(env, "demo · branch delle PR in posizione, git non e' stato toccato");
        heldUntil[env] = tick + 2;
        return $"Ambiente {number}: {targets.Count} branch cambiati solo nella demo";
    }

    // La pianificazione della stagione 2: tre storie di una funzionalita', una di un'altra e un bug.
    private static async Task<StoriesToEstimate> ReadStoriesAsync()
    {
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        return new StoriesToEstimate(
        [
            new StoryToEstimate(341, "Pass stagionale con 30 livelli di ricompense", "Il giocatore sale di livello giocando partite classificate." + Environment.NewLine + "Ogni livello sblocca una ricompensa: monete, dorsi o una carta della stagione.", 340, "Stagione 2: il Mazzo del Drago"),
            new StoryToEstimate(342, "Missioni giornaliere", "Tre missioni al giorno (vinci 2 partite, gioca 10 carte di fuoco, ...)." + Environment.NewLine + "Si rinnovano a mezzanotte nel fuso del giocatore.", 340, "Stagione 2: il Mazzo del Drago"),
            new StoryToEstimate(343, "Notifica push a fine stagione", "Tre giorni prima della fine arriva una notifica con i livelli ancora da sbloccare.", 340, "Stagione 2: il Mazzo del Drago"),
            new StoryToEstimate(351, "Partita privata con codice d'invito", "Chi crea la partita riceve un codice di 6 caratteri da mandare all'amico.", 350, "Modalita' amici"),
            new StoryToEstimate(360, "Il timer del turno continua dopo l'abbandono dell'avversario", "1. Avviare una partita classificata." + Environment.NewLine + "2. L'avversario chiude l'app durante il suo turno." + Environment.NewLine + "3. Il timer continua e la vittoria arriva solo dopo 90 secondi.", null, string.Empty, IsBug: true),
        ]);
    }

    // Le mie PR in vari stati (bozza con modifiche chieste e commenti a cui rispondere, pubblicata in
    // attesa, approvata) e quelle dei colleghi da revisionare, una senza work item.
    private static async Task<MyPullRequests> ReadPullRequestsAsync()
    {
        await Task.Delay(TimeSpan.FromMilliseconds(700));

        var tournament = new PullRequestWorkItem(312, "User Story", "Torneo del weekend");
        var doubleDraw = new PullRequestWorkItem(327, "Bug", "Pescata doppia dopo la riconnessione");
        var legendary = new PullRequestWorkItem(305, "User Story", "Carte leggendarie");
        var editor = new PullRequestWorkItem(330, "User Story", "Editor delle carte nel backoffice");

        var giulia = new PullRequestVote("demo-giulia", "Giulia Rinaldi", -5, IsRequired: true, IsGroup: false);
        var marco = new PullRequestVote("demo-marco", "Marco Tosi", 10, IsRequired: false, IsGroup: false);
        var sara = new PullRequestVote("demo-sara", "Sara Bellini", 0, IsRequired: true, IsGroup: false);
        var me = new PullRequestVote(Me, "Io", 0, IsRequired: true, IsGroup: false);

        var created = new List<PullRequestSummary>
        {
            Pr(412, "Torneo del weekend: classifica e premi", Server, "Io", "feature/312-torneo-weekend", 3, [giulia, marco], tournament, draft: true) with
            {
                ActiveThreads = [new PullRequestThread("demo-giulia", [Me, "demo-giulia"]), new PullRequestThread("demo-giulia", ["demo-giulia"])],
            },
            Pr(415, "Torneo del weekend: iscrizione dall'app", AppRepo, "Io", "feature/312-torneo-weekend", 2, [marco], tournament),
            Pr(419, "Pescata doppia: idempotenza sulla riconnessione", Server, "Io", "bugfix/327-pescata-doppia", 1, [sara], doubleDraw),
        };

        var toReview = new List<PullRequestSummary>
        {
            Pr(418, "Carte leggendarie: limite di una per mazzo", Server, "Giulia Rinaldi", "feature/305-carte-leggendarie", 4, [me, marco], legendary),
            Pr(421, "Editor delle carte: anteprima della rarita'", Backoffice, "Marco Tosi", "feature/330-editor-carte", 2, [me, sara], editor),
            Pr(423, "Shop: testo dei termini aggiornato", AppRepo, "Sara Bellini", "fix/testo-termini-shop", 1, [me with { Vote = 10 }], null),
        };

        return new MyPullRequests(created, toReview, Me);
    }

    private static PullRequestSummary Pr(int id, string title, string repository, string author, string branch, int days, PullRequestVote[] reviewers, PullRequestWorkItem? workItem, bool draft = false)
        => new(id, title, repository, repository, author, branch, draft, DateTime.UtcNow.AddDays(-days).AddHours(-1), new Uri($"https://git.example/mazzo-stellare/pullrequest/{id}"), reviewers)
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
        tool.SetActions(canStart: true, canStop: false, "Avvia (in demo non parte)");
    }

    // Un tunnel del pool per l'ambiente: una porta per ogni repository che ne espone una.
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
            var row = new TunnelPortRowViewModel(port, url, reachable) { CopyToolTip = "In demo non si copia niente", OpenToolTip = "In demo non si apre niente" };
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

        host.SetState(connected ? ServiceState.Running : ServiceState.Stopped, "host del tunnel");
        host.SetActions(canStart: !connected, canStop: connected, "Avvia l'host del tunnel (in demo non parte)");
    }

    // Le migrazioni si elencano dalla piu' nuova, numerate con la posizione nel context come
    // fa ViewBuilder; i gruppi vanno dal piu' vecchio al piu' nuovo.
    private void Context(EnvironmentViewModel env, string name, params (string State, int Count)[] groups)
    {
        var names = new[]
        {
            "AggiungeTabellaTornei", "ClassificaTorneo", "PremiTorneo", "IndiceSuMazzo",
            "RaritaCarte", "LimiteLeggendarie", "StoricoPescate", "IndiceSuPescataTurno",
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
            Note = outOfOrder > 0 ? "Una migrazione di un altro branch e' applicata prima di quelle che mancano." : string.Empty,
            Stamp = Stamp(),
        };

        var rows = new List<RecentMigrationRowViewModel>(total);
        for (var position = total; position >= 1; position--)
        {
            var state = states[position - 1];
            rows.Add(new RecentMigrationRowViewModel(position, names[position % names.Length], state)
            {
                RunGlyph = state == MigrationLabels.Missing ? "\uE768" : "\uE7A7",
                RunToolTip = "In demo l'update non parte",
                CanRun = position != applied,
                RunCommand = Say(env, $"demo · update fino alla migrazione {position} non eseguito"),
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

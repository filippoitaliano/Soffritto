using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows.Input;
using Soffritto.Core;
using Soffritto.Core.Configuration;
using Soffritto.Desktop.Infrastructure;
using Soffritto.Presentation;

namespace Soffritto.Desktop.ViewModels;

/// <summary>
/// Una PR nel pannello. Cio' che l'utente deve fare (<see cref="TodoText"/>) e' l'unica parte
/// in evidenza; lo stato dei voti resta un'informazione di contorno.
/// </summary>
public sealed partial class PullRequestRow
{
    public PullRequestRow(PullRequestSummary pr, bool showAuthor, IReadOnlyList<string> todos, IReadOnlyList<PullRequestAlert> alerts, bool isApproved, string statusText, string statusDot, string statusToolTip, Func<Uri, ICommand> open, Func<string, RepositoryType?> typeOf)
    {
        Id = pr.Id;
        Alerts = alerts;
        IsApproved = isApproved;
        Created = pr.Created;
        (RepoTag, RepoOrder, RepoForeground, RepoBackground, RepoBorder) = RepoStyle(typeOf(pr.Repository), pr.Repository);
        RepoToolTip = pr.Repository;

        // Il numero del work item sta gia' nell'intestazione del gruppo.
        Title = WorkItemPrefix().Replace(pr.Title, string.Empty);
        if (Title.Length == 0)
        {
            Title = pr.Title;
        }

        var meta = new List<string> { $"!{pr.Id}" };
        if (showAuthor)
        {
            meta.Add(pr.Author);
        }

        meta.Add(Age(pr.Created));
        Meta = string.Join(" · ", meta);
        DraftVisibility = pr.IsDraft ? "Visible" : "Collapsed";

        Todo = todos.Count;
        TodoText = string.Join(" · ", todos);
        StatusText = statusText;
        StatusDot = statusDot;
        StatusToolTip = statusToolTip;
        ToolTip = $"{pr.Title}{Environment.NewLine}{pr.WebUrl}";
        OpenCommand = open(pr.WebUrl);
    }

    public int Id { get; }

    public DateTime Created { get; }

    public IReadOnlyList<PullRequestAlert> Alerts { get; }

    /// <summary>Sulle proprie PR: approvata da tutti i reviewer. Su quelle degli altri: approvata dall'utente.</summary>
    public bool IsApproved { get; }

    public string RepoTag { get; }

    public int RepoOrder { get; }

    public string RepoForeground { get; }

    public string RepoBackground { get; }

    public string RepoBorder { get; }

    public string RepoToolTip { get; }

    public string Title { get; }

    public string Meta { get; }

    public string DraftVisibility { get; }

    public int Todo { get; }

    public string TodoText { get; }

    public string TodoVisibility => Todo > 0 ? "Visible" : "Collapsed";

    public string StatusText { get; }

    public string StatusDot { get; }

    public string StatusToolTip { get; }

    public string ToolTip { get; }

    public ICommand OpenCommand { get; }

    // Lo schema del Tag dell'app (fondo al 10%, bordo al 30%) con una tinta per strato, senza
    // rosso ne' arancio: non devono sembrare avvisi.
    private static (string Tag, int Order, string Foreground, string Background, string Border) RepoStyle(RepositoryType? type, string repository) => type switch
    {
        RepositoryType.Backend => ("BE", 0, "#3F43A8", "#1A5B5FC7", "#4D5B5FC7"),
        RepositoryType.Frontend => ("FE", 1, "#0B6470", "#1A0F7B8A", "#4D0F7B8A"),
        RepositoryType.App => ("APP", 2, "#6E3F94", "#1A8E5BB5", "#4D8E5BB5"),
        _ => (repository.ToUpperInvariant(), 3, "#404040", "#1A808080", "#4D808080"),
    };

    private static string Age(DateTime created)
    {
        var age = DateTime.UtcNow - created.ToUniversalTime();
        return age.TotalHours < 1 ? "meno di un'ora fa"
            : age.TotalHours < 2 ? "un'ora fa"
            : age.TotalDays < 1 ? $"{(int)age.TotalHours} ore fa"
            : age.TotalDays < 2 ? "ieri"
            : $"{(int)age.TotalDays} giorni fa";
    }

    [GeneratedRegex(@"^\s*#\d+\s*[-:]?\s*")]
    private static partial Regex WorkItemPrefix();
}

/// <summary>
/// Una cosa da fare su una PR: diventa notifica (vedi <see cref="PullRequestNotifications"/>) se
/// la sua <see cref="Key"/> non c'era alla lettura precedente o se <see cref="Count"/> e' cresciuto.
/// </summary>
public sealed record PullRequestAlert(string Key, int Count, string Text, string Title, Uri Url, int PullRequestId);

/// <summary>Una voce del menu «Apri in»: porta l'ambiente sui branch delle PR del gruppo.</summary>
public sealed record CheckoutTarget(string Label, ICommand Command, string? ToolTip = null);

/// <summary>
/// Le PR di una sezione che appartengono allo stesso work item, una per repository. «Apri in»
/// porta un ambiente sui loro branch sorgente, ognuno nelle card di quel repository.
/// </summary>
public sealed class PullRequestGroup : ObservableObject
{
    private readonly IReadOnlyList<EnvironmentViewModel> environments;
    private readonly List<(string Repository, string Branch)> targets;
    private readonly Func<int, IReadOnlyList<(string Repository, string Branch)>, Task<string>> checkout;
    private readonly Func<IReadOnlyList<(string Repository, string Branch)>, Task<string>>? createDocker;
    private string actionLine = string.Empty;
    private string matchText = string.Empty;
    private int? lastCheckout;
    private bool isNotBusy = true;
    private bool isHighlighted;

    public PullRequestGroup(
        PullRequestWorkItem? workItem,
        IReadOnlyList<PullRequestSummary> prs,
        Func<PullRequestSummary, PullRequestRow> toRow,
        IReadOnlyList<EnvironmentViewModel> environments,
        Func<int, IReadOnlyList<(string Repository, string Branch)>, Task<string>> checkout,
        Func<Uri, ICommand> open,
        Func<string, RepositoryType?> typeOf,
        Func<IReadOnlyList<(string Repository, string Branch)>, Task<string>>? createDocker = null)
    {
        this.environments = environments;
        Rows = prs.Select(toRow)
            .OrderByDescending(r => r.Todo)
            .ThenBy(r => r.RepoOrder)
            .ThenByDescending(r => r.Created)
            .ToList();

        Todo = Rows.Sum(r => r.Todo);
        Latest = Rows.Max(r => r.Created);

        // Un work item senza niente da fare parte chiuso: resta l'intestazione.
        IsAllApproved = Todo == 0 && Rows.All(r => r.IsApproved);
        Expander = new Expander(isExpanded: Todo > 0);
        HasWorkItem = workItem is not null;
        WorkItemId = workItem is null ? string.Empty : $"#{workItem.Id}";
        Title = workItem?.Title ?? "Senza work item";
        TitleToolTip = workItem is null ? "PR non collegate a nessun work item" : $"{workItem.Type} {workItem.Id}: {workItem.Title}";
        OpenWorkItemCommand = workItem is not null && AzureDevOpsAccess.WorkItemUrl(workItem.Id) is { } workItemUrl
            ? open(workItemUrl)
            : null;

        var branches = prs
            .Where(pr => typeOf(pr.Repository) is not null && pr.SourceBranch.Length > 0)
            .Select(pr => (pr.Repository, pr.SourceBranch))
            .Distinct()
            .ToList();

        // Due PR dello stesso repository su branch diversi: quale mettere nel clone lo decide l'utente.
        var ambiguous = branches
            .GroupBy(b => b.Repository, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        targets = branches.Select(b => (b.Repository, b.SourceBranch)).ToList();

        CheckoutToolTip = ambiguous.Count > 0
            ? $"Non disponibile: piu' PR di {string.Join(", ", ambiguous)} su branch diversi"
            : targets.Count == 0
                ? "Non disponibile: nessuna PR dei repository configurati"
                : "Spegne un ambiente (app compresa) e lo porta sui branch di queste PR, gli altri repository su dev, solo se sono tutti puliti:"
                    + Environment.NewLine + string.Join(Environment.NewLine, branches.Select(b => $"{b.Repository}: {b.SourceBranch}"));
        CanCheckout = ambiguous.Count == 0 && targets.Count > 0;

        this.checkout = checkout;
        this.createDocker = createDocker;

        RefreshMatch();
    }

    /// <summary>Gli ambienti della griglia al momento del clic: quelli Docker entrano ed escono mentre l'exe gira.</summary>
    public IReadOnlyList<CheckoutTarget> Targets => environments
        .Select(env => env.Number)
        .Select(number => new CheckoutTarget(
            $"Ambiente {number}",
            Run($"Ambiente {number}", number, "controllo i repository e fetch, poi spengo i servizi…", () => checkout(number, targets))))
        .ToList();

    /// <summary>
    /// La voce che crea un ambiente Docker nuovo sui branch delle PR del gruppo; null se la
    /// configurazione non ha un backend con compose e clone sorgente, o nella demo.
    /// </summary>
    public CheckoutTarget? NewDockerTarget => createDocker is { } create
        && ActiveConfiguration.Current.Repositories.Any(r => r.Type == RepositoryType.Backend && r.DockerCompose.Length > 0 && r.SourceClone is { Length: > 0 })
            ? new CheckoutTarget(
                "Nuovo ambiente Docker",
                Run("Nuovo ambiente Docker", null, "fetch dei cloni sorgente, poi creazione: il dettaglio va in «Operazioni» nella card del nuovo ambiente…", () => create(targets)),
                $"Crea un ambiente Docker sul primo numero libero, con i worktree sui branch di queste PR; backend e frontend senza PR su {GitConventions.DevelopmentBranch}")
            : null;

    private ICommand Run(string subject, int? number, string start, Func<Task<string>> action) => new AsyncCommand(async () =>
    {
        if (!IsNotBusy)
        {
            return;
        }

        IsNotBusy = false;
        lastCheckout = number;
        ActionLine = $"{subject}: {start}";
        try
        {
            ActionLine = await action();
        }
        catch (Exception ex)
        {
            ActionLine = $"{subject}: {ex.Message}";
        }
        finally
        {
            IsNotBusy = true;
            RefreshMatch();
        }
    });

    /// <summary>
    /// Ricalcola il tag degli ambienti che stanno esattamente sui branch di queste PR. Quando
    /// ci arriva l'ambiente dell'ultimo «Apri in», il tag prende il posto della sua riga d'esito.
    /// </summary>
    public void RefreshMatch()
    {
        var matching = CanCheckout
            ? environments.Where(env => env.IsOnBranches(targets)).Select(env => env.Number).ToList()
            : [];

        MatchText = matching.Count switch
        {
            0 => string.Empty,
            1 => $"Ambiente {matching[0]}",
            _ => $"Ambienti {string.Join(", ", matching)}",
        };

        if (isNotBusy && lastCheckout is { } number && matching.Contains(number))
        {
            ActionLine = string.Empty;
            lastCheckout = null;
        }
    }

    /// <summary>Gli ambienti gia' sui branch delle PR del gruppo, vuoto se nessuno.</summary>
    public string MatchText
    {
        get => matchText;
        private set
        {
            if (SetProperty(ref matchText, value))
            {
                OnPropertyChanged(nameof(MatchVisibility));
            }
        }
    }

    public string MatchVisibility => matchText.Length > 0 ? "Visible" : "Collapsed";

    public string MatchToolTip => "Su questi branch, gli altri repository su dev:"
        + Environment.NewLine + string.Join(Environment.NewLine, targets.Select(t => $"{t.Repository}: {t.Branch}"));

    public Expander Expander { get; }

    public bool IsAllApproved { get; }

    public string AllApprovedVisibility => IsAllApproved ? "Visible" : "Collapsed";

    public bool CanCheckout { get; }

    public string CheckoutToolTip { get; }

    public bool IsNotBusy
    {
        get => isNotBusy;
        private set
        {
            if (SetProperty(ref isNotBusy, value))
            {
                OnPropertyChanged(nameof(CheckoutEnabled));
            }
        }
    }

    /// <summary>Spento durante un «Apri in» e quando i branch non si possono assegnare: il tooltip dice perche'.</summary>
    public bool CheckoutEnabled => CanCheckout && isNotBusy;

    /// <summary>Esito dell'ultimo «Apri in», sotto l'intestazione del gruppo.</summary>
    public string ActionLine
    {
        get => actionLine;
        private set
        {
            if (SetProperty(ref actionLine, value))
            {
                OnPropertyChanged(nameof(ActionVisibility));
            }
        }
    }

    public string ActionVisibility => actionLine.Length > 0 ? "Visible" : "Collapsed";

    /// <summary>Bordo verde acceso per qualche secondo: la card a cui porta il clic su una notifica.</summary>
    public bool IsHighlighted { get => isHighlighted; set => SetProperty(ref isHighlighted, value); }

    public IReadOnlyList<PullRequestRow> Rows { get; }

    public int Todo { get; }

    public DateTime Latest { get; }

    public bool HasWorkItem { get; }

    public string WorkItemId { get; }

    public string WorkItemVisibility => HasWorkItem ? "Visible" : "Collapsed";

    public string Title { get; }

    public string TitleToolTip { get; }

    public string TodoText => Todo == 1 ? "1 da fare" : $"{Todo} da fare";

    public string TodoVisibility => Todo > 0 ? "Visible" : "Collapsed";

    public ICommand? OpenWorkItemCommand { get; }
}

/// <summary>
/// Il pannello delle pull request attive che riguardano l'utente: le sue e quelle degli altri in
/// cui e' reviewer. In ogni sezione le PR sono raggruppate per work item, e in cima vanno i gruppi
/// e le PR con piu' cose da fare. Si legge all'apertura e col refresh.
/// </summary>
public sealed class PullRequestsViewModel : ObservableObject
{
    private const string Approved = StatusBadge.OkColor;
    private const string Changes = StatusBadge.WarningColor;
    private const string Neutral = StatusBadge.OffColor;

    private bool isLoading;
    private string statusLine = string.Empty;
    private string errorLine = string.Empty;
    private string createdSummary = string.Empty;
    private string toReviewSummary = string.Empty;

    // La stessa collezione della griglia: gli ambienti Docker entrano ed escono mentre l'exe gira.
    private readonly ObservableCollection<EnvironmentViewModel> environments;
    private readonly Func<int, IReadOnlyList<(string Repository, string Branch)>, Task<string>> checkout;
    private readonly Func<Task<MyPullRequests>> read;
    private readonly Func<Uri, ICommand> open;
    private readonly Func<IReadOnlyList<(string Repository, string Branch)>, Task<string>>? createDocker;
    private readonly Func<string, RepositoryType?> typeOf;

    /// <param name="read">Da dove arrivano le PR: Azure DevOps se omesso, dati inventati nella demo.</param>
    /// <param name="open">Cosa fa il clic su PR e work item: il browser se omesso.</param>
    /// <param name="createDocker">Crea un ambiente Docker nuovo sui branch indicati e restituisce l'esito; senza, «Apri in» non lo propone.</param>
    /// <param name="typeOf">Lo strato di un repository dal suo nome sul server: la configurazione attiva se omesso, i repository inventati nella demo.</param>
    public PullRequestsViewModel(
        IReadOnlyList<EnvironmentViewModel> environments,
        Func<int, IReadOnlyList<(string Repository, string Branch)>, Task<string>> checkout,
        Func<Task<MyPullRequests>>? read = null,
        Func<Uri, ICommand>? open = null,
        Func<IReadOnlyList<(string Repository, string Branch)>, Task<string>>? createDocker = null,
        Func<string, RepositoryType?>? typeOf = null)
    {
        this.createDocker = createDocker;
        this.typeOf = typeOf ?? ActiveConfiguration.TypeOfRepository;
        this.environments = environments as ObservableCollection<EnvironmentViewModel> ?? new ObservableCollection<EnvironmentViewModel>(environments);
        this.checkout = checkout;
        this.read = read ?? AzureDevOpsAccess.ReadAsync;
        this.open = open ?? Browser.OpenCommand;
        RefreshCommand = new AsyncCommand(RefreshAsync);

        foreach (var environment in environments)
        {
            environment.BranchesChanged += RefreshMatches;
        }
    }

    /// <summary>Un ambiente appena entrato nella griglia: i gruppi ricalcolano su quali ambienti stanno le loro PR.</summary>
    public void Track(EnvironmentViewModel environment)
    {
        environment.BranchesChanged += RefreshMatches;
        RefreshMatches();
    }

    /// <summary>Un ambiente uscito dalla griglia: non si seguono piu' i suoi branch.</summary>
    public void Untrack(EnvironmentViewModel environment)
    {
        environment.BranchesChanged -= RefreshMatches;
        RefreshMatches();
    }

    private void RefreshMatches()
    {
        foreach (var group in Created.Concat(ToReview))
        {
            group.RefreshMatch();
        }
    }

    public ObservableCollection<PullRequestGroup> Created { get; } = [];

    public ObservableCollection<PullRequestGroup> ToReview { get; } = [];

    public ICommand RefreshCommand { get; }

    /// <summary>
    /// Tutte le cose da fare dell'ultima lettura riuscita: chi le riceve decide cosa e' nuovo.
    /// Una lettura fallita non lo solleva, perche' le sue liste vuote sembrerebbero tutto risolto.
    /// </summary>
    public event Action<IReadOnlyList<PullRequestAlert>>? AlertsRead;

    public bool IsLoading { get => isLoading; private set => SetProperty(ref isLoading, value); }

    public string StatusLine { get => statusLine; private set => SetProperty(ref statusLine, value); }

    public string CreatedSummary { get => createdSummary; private set => SetProperty(ref createdSummary, value); }

    public string ToReviewSummary { get => toReviewSummary; private set => SetProperty(ref toReviewSummary, value); }

    public string ErrorLine
    {
        get => errorLine;
        private set
        {
            if (SetProperty(ref errorLine, value))
            {
                OnPropertyChanged(nameof(ErrorVisibility));
                OnPropertyChanged(nameof(StatusColor));
            }
        }
    }

    public string ErrorVisibility => errorLine.Length > 0 ? "Visible" : "Collapsed";

    /// <summary>Pallino accanto al titolo a riposo: verde se l'ultima lettura e' riuscita, rosso se e' fallita.</summary>
    public string StatusColor => errorLine.Length > 0 ? StatusBadge.ErrorColor : StatusBadge.OkColor;

    public string CreatedEmptyVisibility => !isLoading && Created.Count == 0 ? "Visible" : "Collapsed";

    public string ToReviewEmptyVisibility => !isLoading && ToReview.Count == 0 ? "Visible" : "Collapsed";

    public async Task RefreshAsync()
    {
        if (IsLoading)
        {
            return;
        }

        IsLoading = true;
        StatusLine = "lettura da Azure DevOps…";

        var result = await read();

        Fill(Created, result.Created, pr => MineRow(pr, result.MyId));
        Fill(ToReview, result.ToReview, pr => ReviewRow(pr, result.MyId));

        if (result.Error is null)
        {
            AlertsRead?.Invoke(Created.Concat(ToReview).SelectMany(g => g.Rows).SelectMany(r => r.Alerts).ToList());
        }

        CreatedSummary = Summary(result.Created.Count, Created.Sum(g => g.Todo));
        ToReviewSummary = Summary(result.ToReview.Count, ToReview.Sum(g => g.Todo));
        ErrorLine = result.Error ?? string.Empty;
        StatusLine = result.Error is null ? $"aggiornato alle {DateTime.Now:HH:mm}" : "lettura fallita";
        IsLoading = false;
        OnPropertyChanged(nameof(CreatedEmptyVisibility));
        OnPropertyChanged(nameof(ToReviewEmptyVisibility));
    }

    /// <summary>
    /// Apre e illumina per qualche secondo la card del work item che contiene la PR. Null se la
    /// PR non e' nelle liste (chiusa, o lavoro gia' risolto e PR sparita dall'ultima lettura).
    /// </summary>
    public PullRequestGroup? Highlight(int pullRequestId)
    {
        var group = Created.Concat(ToReview).FirstOrDefault(g => g.Rows.Any(r => r.Id == pullRequestId));
        if (group is null)
        {
            return null;
        }

        foreach (var other in Created.Concat(ToReview))
        {
            other.IsHighlighted = false;
        }

        group.Expander.IsExpanded = true;
        group.IsHighlighted = true;
        _ = DimLaterAsync(group);
        return group;
    }

    private static async Task DimLaterAsync(PullRequestGroup group)
    {
        await Task.Delay(TimeSpan.FromSeconds(3));
        group.IsHighlighted = false;
    }

    // Prima i gruppi con piu' cose da fare, poi i piu' recenti; "Senza work item" sempre in fondo.
    private void Fill(ObservableCollection<PullRequestGroup> target, IReadOnlyList<PullRequestSummary> prs, Func<PullRequestSummary, PullRequestRow> toRow)
    {
        target.Clear();

        var groups = prs
            .GroupBy(pr => pr.WorkItem?.Id ?? 0)
            .Select(g => new PullRequestGroup(g.First().WorkItem, g.ToList(), toRow, environments, checkout, open, typeOf, createDocker))
            .OrderBy(g => !g.HasWorkItem)
            .ThenByDescending(g => g.Todo)
            .ThenByDescending(g => g.Latest);

        foreach (var group in groups)
        {
            target.Add(group);
        }
    }

    private static string Summary(int count, int todo)
        => todo == 0 ? $"{count} aperte, niente da fare" : $"{todo} da fare · {count} aperte";

    // Sulle proprie PR il lavoro c'e' solo quando sono in bozza: e' il reviewer a rimetterle in
    // bozza quando restituisce la PR all'autore, specchio delle PR degli altri, dove la bozza non
    // chiede niente. Finche' la PR e' pubblicata i voti e i commenti restano nel dettaglio ma non
    // sono da fare. In bozza c'e' da fare quando un reviewer chiede modifiche o un commento
    // aspetta risposta (l'ultima parola nella discussione non e' dell'utente). Le discussioni
    // aperte dall'utente stesso contano sempre: sono promemoria da risolvere, come i commenti altrui.
    private PullRequestRow MineRow(PullRequestSummary pr, string? myId)
    {
        var people = pr.Reviewers.Where(r => !r.IsGroup).ToList();
        var detail = people.Count == 0
            ? "Nessun reviewer"
            : string.Join(Environment.NewLine, people.Select(r => $"{r.Name}: {VoteName(r.Vote)}{(r.IsRequired ? " (obbligatorio)" : string.Empty)}"));

        var todos = new List<string>();
        var alerts = new List<PullRequestAlert>();
        if (pr.IsDraft)
        {
            foreach (var reviewer in people.Where(r => r.Vote < 0))
            {
                var todo = reviewer.Vote == -10 ? $"rivedi: rifiutata da {reviewer.Name}" : $"modifiche chieste da {reviewer.Name}";
                todos.Add(todo);
                alerts.Add(new PullRequestAlert($"vote|{pr.Id}|{reviewer.Id}|{reviewer.Vote}", 1, $"La tua PR !{pr.Id}: {todo}.", pr.Title, pr.WebUrl, pr.Id));
            }

            var replies = pr.ActiveThreads.Count(t => !IsMe(t.LastAuthorId, myId) || IsMe(t.AuthorIds[0], myId));
            if (replies > 0)
            {
                var todo = replies == 1 ? "rispondi a 1 commento" : $"rispondi a {replies} commenti";
                todos.Add(todo);
                alerts.Add(new PullRequestAlert($"replies|{pr.Id}", replies, $"La tua PR !{pr.Id}: {todo}.", pr.Title, pr.WebUrl, pr.Id));
            }
        }

        var approved = people.Count(r => r.Vote > 0);
        var (status, dot) = people.Count == 0 ? ("senza reviewer", Neutral)
            : people.Any(r => r.Vote < 0) ? ("modifiche richieste", Changes)
            : approved == people.Count ? ("approvata", Approved)
            : ($"{approved}/{people.Count} approvazioni", Neutral);

        return new PullRequestRow(pr, showAuthor: false, todos, alerts, isApproved: dot == Approved, status, dot, detail, open, typeOf);
    }

    // Sulle PR degli altri c'e' da fare il voto e le risposte ai propri commenti. Una bozza non
    // chiede niente: quando l'autore la ripubblica il lavoro ricompare e diventa notifica.
    private PullRequestRow ReviewRow(PullRequestSummary pr, string? myId)
    {
        var vote = pr.Reviewers.FirstOrDefault(r => IsMe(r.Id, myId))?.Vote ?? 0;
        var detail = string.Join(Environment.NewLine, pr.Reviewers.Where(r => !r.IsGroup).Select(r => $"{r.Name}: {VoteName(r.Vote)}"));

        var todos = new List<string>();
        var alerts = new List<PullRequestAlert>();
        if (vote == 0 && !pr.IsDraft)
        {
            todos.Add("da revisionare e votare");
            alerts.Add(new PullRequestAlert($"review|{pr.Id}", 1, $"PR !{pr.Id} di {pr.Author}: da revisionare e votare.", pr.Title, pr.WebUrl, pr.Id));
        }

        var replies = pr.IsDraft ? 0 : pr.ActiveThreads.Count(t => t.AuthorIds.Any(a => IsMe(a, myId)) && !IsMe(t.LastAuthorId, myId));
        if (replies > 0)
        {
            var todo = replies == 1 ? "1 risposta ai tuoi commenti" : $"{replies} risposte ai tuoi commenti";
            todos.Add(todo);
            alerts.Add(new PullRequestAlert($"replies|{pr.Id}", replies, $"PR !{pr.Id} di {pr.Author}: {todo}.", pr.Title, pr.WebUrl, pr.Id));
        }

        var status = vote == 0 ? "non hai votato" : $"hai votato: {VoteName(vote)}";
        var dot = vote > 0 ? Approved : vote < 0 ? Changes : Neutral;
        return new PullRequestRow(pr, showAuthor: true, todos, alerts, isApproved: vote > 0, status, dot, detail, open, typeOf);
    }

    private static bool IsMe(string id, string? myId) => string.Equals(id, myId, StringComparison.OrdinalIgnoreCase);

    private static string VoteName(int vote) => vote switch
    {
        10 => "approvata",
        5 => "approvata con suggerimenti",
        -5 => "in attesa dell'autore",
        -10 => "rifiutata",
        _ => "nessun voto",
    };
}

internal static class Browser
{
    // Il browser predefinito parte da rundll32, fuori dal job dell'exe (vedi DevToolsController).
    public static ICommand OpenCommand(Uri url) => new AsyncCommand(() => Task.Run(() => Open(url)));

    /// <summary>Apre l'URL nel browser predefinito, fuori dal job dell'exe. Restituisce la riga di attivita'.</summary>
    public static string Open(Uri url)
    {
        DetachedLauncher.Start($"rundll32.exe url.dll,FileProtocolHandler {url.AbsoluteUri}", System.IO.Path.GetTempPath(), newConsole: false);
        return $"{url.AbsoluteUri} aperto nel browser predefinito";
    }

    public static void OpenFolder(string folder)
    {
        System.IO.Directory.CreateDirectory(folder);
        DetachedLauncher.Start($"explorer.exe \"{folder}\"", folder, newConsole: false);
    }
}

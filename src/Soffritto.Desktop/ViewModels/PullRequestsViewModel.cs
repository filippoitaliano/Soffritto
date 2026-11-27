using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows.Input;
using Soffritto.Core;
using Soffritto.Core.Configuration;
using Soffritto.Desktop.Infrastructure;
using Soffritto.Presentation;

namespace Soffritto.Desktop.ViewModels;

/// <summary>
/// A PR in the panel. What the user has to do (<see cref="TodoText"/>) is the only highlighted
/// part; the vote status stays secondary information.
/// </summary>
public sealed partial class PullRequestRow
{
    public PullRequestRow(PullRequestSummary pr, bool showAuthor, IReadOnlyList<string> todos, IReadOnlyList<PullRequestAlert> alerts, string statusText, string statusDot, string statusToolTip, Func<Uri, ICommand> open, Func<string, RepositoryType?> typeOf)
    {
        Id = pr.Id;
        Alerts = alerts;
        Created = pr.Created;
        (RepoTag, RepoOrder, RepoLayer, RepoBackground, RepoBorder) = RepoStyle(typeOf(pr.Repository), pr.Repository);
        RepoToolTip = pr.Repository;

        // The work item number is already in the group header.
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

    /// <summary>On the user's own PRs: approved by all reviewers. On other people's: approved by the user.</summary>
    public string RepoTag { get; }

    public int RepoOrder { get; }

    /// <summary>The layer (backend, frontend, app, other): the tag's text takes the theme's colour for it.</summary>
    public string RepoLayer { get; }

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

    // The app's Tag scheme (10% background, 30% border) with one tint per layer, without red or
    // orange: they must not look like warnings. The translucent tints work on both themes; the
    // text colour comes from the theme (Theme.Layer*Text), chosen in the panel by RepoLayer.
    private static (string Tag, int Order, string Layer, string Background, string Border) RepoStyle(RepositoryType? type, string repository) => type switch
    {
        RepositoryType.Backend => ("BE", 0, "Backend", "#1A5B5FC7", "#4D5B5FC7"),
        RepositoryType.Frontend => ("FE", 1, "Frontend", "#1A0F7B8A", "#4D0F7B8A"),
        RepositoryType.App => ("APP", 2, "App", "#1A8E5BB5", "#4D8E5BB5"),
        _ => (repository.ToUpperInvariant(), 3, "Other", "#1A808080", "#4D808080"),
    };

    private static string Age(DateTime created)
    {
        var age = DateTime.UtcNow - created.ToUniversalTime();
        return age.TotalHours < 1 ? "less than an hour ago"
            : age.TotalHours < 2 ? "an hour ago"
            : age.TotalDays < 1 ? $"{(int)age.TotalHours} hours ago"
            : age.TotalDays < 2 ? "yesterday"
            : $"{(int)age.TotalDays} days ago";
    }

    [GeneratedRegex(@"^\s*#\d+\s*[-:]?\s*")]
    private static partial Regex WorkItemPrefix();
}

/// <summary>
/// Something to do on a PR: it becomes a notification (see <see cref="PullRequestNotifications"/>) if
/// its <see cref="Key"/> was not there at the previous read or if <see cref="Count"/> has grown.
/// </summary>
public sealed record PullRequestAlert(string Key, int Count, string Text, string Title, Uri Url, int PullRequestId);

/// <summary>A work item (or a PR without one) on the column of an environment that sits on its branches; the click opens it.</summary>
public sealed record MatchedWork(string Text, string ToolTip, ICommand? Open);

/// <summary>An item of the “Open in” menu: moves the environment onto the branches of the group's PRs.</summary>
public sealed record CheckoutTarget(string Label, ICommand Command, string? ToolTip = null);

/// <summary>
/// The PRs of a section that belong to the same work item, one per repository. “Open in” moves an
/// environment onto their source branches, each one in that repository's cards.
/// </summary>
public sealed class PullRequestGroup : ObservableObject
{
    private readonly IReadOnlyList<EnvironmentViewModel> environments;
    private readonly List<(string Repository, string Branch)> targets;
    private readonly Func<int, IReadOnlyList<(string Repository, string Branch)>, Task<ActionOutcome>> checkout;
    private readonly Func<IReadOnlyList<(string Repository, string Branch)>, Task<ActionOutcome>>? createDocker;
    private string actionLine = string.Empty;
    private FlashKind actionKind = FlashKind.Info;
    private string matchText = string.Empty;
    private int? lastCheckout;
    private bool isNotBusy = true;
    private bool isHighlighted;

    public PullRequestGroup(
        PullRequestWorkItem? workItem,
        IReadOnlyList<PullRequestSummary> prs,
        Func<PullRequestSummary, PullRequestRow> toRow,
        IReadOnlyList<EnvironmentViewModel> environments,
        Func<int, IReadOnlyList<(string Repository, string Branch)>, Task<ActionOutcome>> checkout,
        Func<Uri, ICommand> open,
        Func<string, RepositoryType?> typeOf,
        Func<IReadOnlyList<(string Repository, string Branch)>, Task<ActionOutcome>>? createDocker = null)
    {
        this.environments = environments;
        Rows = prs.Select(toRow)
            .OrderByDescending(r => r.Todo)
            .ThenBy(r => r.RepoOrder)
            .ThenByDescending(r => r.Created)
            .ToList();

        Todo = Rows.Sum(r => r.Todo);
        Latest = Rows.Max(r => r.Created);

        // A work item with nothing to do starts collapsed: only the header shows.
        Expander = new Expander(isExpanded: Todo > 0);
        HasWorkItem = workItem is not null;
        WorkItemId = workItem is null ? string.Empty : $"#{workItem.Id}";
        Title = workItem?.Title ?? "No work item";
        TitleToolTip = workItem is null ? "PRs not linked to any work item" : $"{workItem.Type} {workItem.Id}: {workItem.Title}";
        OpenWorkItemCommand = workItem is not null && AzureDevOpsAccess.WorkItemUrl(workItem.Id) is { } workItemUrl
            ? open(workItemUrl)
            : null;

        var work = EnvironmentWork.Group(workItem, prs, typeOf);
        var branches = work.Targets;
        var ambiguous = work.Ambiguous;
        targets = branches.ToList();

        // The mirror on the environment column: the work item, or the PR when there is none.
        var first = prs.OrderByDescending(pr => pr.Created).First();
        Mirror = workItem is not null
            ? new MatchedWork($"#{workItem.Id} {workItem.Title}", $"{workItem.Type} {workItem.Id}: {workItem.Title}", OpenWorkItemCommand)
            : new MatchedWork($"PR {first.Id} {first.Title}", $"PR {first.Id} ({first.Repository}): {first.Title}", open(first.WebUrl));

        CheckoutToolTip = ambiguous.Count > 0
            ? $"Not available: several PRs of {string.Join(", ", ambiguous)} on different branches"
            : targets.Count == 0
                ? "Not available: no PRs from the configured repositories"
                : "Stops an environment (app included) and moves it onto the branches of these PRs, the other repositories onto dev, only if they are all clean:"
                    + Environment.NewLine + string.Join(Environment.NewLine, branches.Select(b => $"{b.Repository}: {b.Branch}"));
        CanCheckout = ambiguous.Count == 0 && targets.Count > 0;

        this.checkout = checkout;
        this.createDocker = createDocker;

        RefreshMatch();
    }

    /// <summary>The grid's environments at the time of the click: Docker ones come and go while the app is running.</summary>
    public IReadOnlyList<CheckoutTarget> Targets => environments
        .Select(env => env.Number)
        .Select(number => new CheckoutTarget(
            $"Environment {number}",
            Run($"Environment {number}", number, "checking the repositories and fetching, then stopping the services…", () => checkout(number, targets))))
        .ToList();

    /// <summary>
    /// The item that creates a new Docker environment on the branches of the group's PRs; null if
    /// the configuration has no backend with a compose file and source clone, or in the demo.
    /// </summary>
    public CheckoutTarget? NewDockerTarget => createDocker is { } create
        && ActiveConfiguration.Current.Repositories.Any(r => r.Type == RepositoryType.Backend && r.DockerCompose.Length > 0 && r.SourceClone is { Length: > 0 })
            ? new CheckoutTarget(
                "New Docker environment",
                Run("New Docker environment", null, "fetching the source clones, then creating: details go to “Operations” in the new environment's card…", () => create(targets)),
                $"Creates a Docker environment on the first free number, with worktrees on the branches of these PRs; backend and frontend without PRs on {GitConventions.DevelopmentBranch}")
            : null;

    private ICommand Run(string subject, int? number, string start, Func<Task<ActionOutcome>> action) => new AsyncCommand(async () =>
    {
        if (!IsNotBusy)
        {
            return;
        }

        IsNotBusy = false;
        lastCheckout = number;
        ShowAction($"{subject}: {start}", FlashKind.Progress);
        try
        {
            var outcome = await action();
            ShowAction(outcome.Text, outcome.Kind);
        }
        catch (Exception ex)
        {
            ShowAction($"{subject}: {ex.Message}", FlashKind.Error);
        }
        finally
        {
            IsNotBusy = true;
            RefreshMatch();
        }
    });

    /// <summary>
    /// Recomputes the tag of the environments that sit exactly on the branches of these PRs. When
    /// the environment of the last “Open in” gets there, the tag replaces its outcome line.
    /// </summary>
    public void RefreshMatch()
    {
        var matching = CanCheckout
            ? environments.Where(env => env.IsOnBranches(targets)).Select(env => env.Number).ToList()
            : [];

        MatchingEnvironments = matching;
        MatchText = matching.Count switch
        {
            0 => string.Empty,
            _ => $"Env {string.Join(", ", matching)}",
        };

        if (isNotBusy && lastCheckout is { } number && matching.Contains(number))
        {
            ShowAction(string.Empty, FlashKind.Info);
            lastCheckout = null;
        }
    }

    /// <summary>The numbers of the environments on the branches of the group's PRs.</summary>
    public IReadOnlyList<int> MatchingEnvironments { get; private set; } = [];

    /// <summary>How the group shows on the column of an environment that sits on its branches.</summary>
    public MatchedWork Mirror { get; }

    /// <summary>The environments already on the branches of the group's PRs, empty if none.</summary>
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

    public string MatchToolTip => "On these branches, the other repositories on dev:"
        + Environment.NewLine + string.Join(Environment.NewLine, targets.Select(t => $"{t.Repository}: {t.Branch}"));

    public Expander Expander { get; }

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
                OnPropertyChanged(nameof(IsBusy));
            }
        }
    }

    /// <summary>An “Open in” is running: the card shows the busy stripes of the repository cards.</summary>
    public bool IsBusy => !isNotBusy;

    /// <summary>Disabled during an “Open in” and when the branches cannot be assigned: the tooltip says why.</summary>
    public bool CheckoutEnabled => CanCheckout && isNotBusy;

    /// <summary>Outcome of the last “Open in”, under the group header.</summary>
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

    /// <summary>How the FlashCard shows <see cref="ActionLine"/>: running, done, partial or failed.</summary>
    public FlashKind ActionKind { get => actionKind; private set => SetProperty(ref actionKind, value); }

    private void ShowAction(string text, FlashKind kind)
    {
        ActionKind = kind;
        ActionLine = text;
    }

    /// <summary>Green border lit for a few seconds: the card a click on a notification leads to.</summary>
    public bool IsHighlighted { get => isHighlighted; set => SetProperty(ref isHighlighted, value); }

    public IReadOnlyList<PullRequestRow> Rows { get; }

    public int Todo { get; }

    public DateTime Latest { get; }

    public bool HasWorkItem { get; }

    public string WorkItemId { get; }

    public string WorkItemVisibility => HasWorkItem ? "Visible" : "Collapsed";

    public string Title { get; }

    public string TitleToolTip { get; }

    public ICommand? OpenWorkItemCommand { get; }
}

/// <summary>
/// The panel of active pull requests that concern the user: their own and other people's where
/// they are a reviewer. In each section the PRs are grouped by work item, with the groups and PRs
/// that have the most to do on top. It is read on opening and on refresh.
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

    // The same collection as the grid: Docker environments come and go while the app is running.
    private readonly ObservableCollection<EnvironmentViewModel> environments;
    private readonly Func<int, IReadOnlyList<(string Repository, string Branch)>, Task<ActionOutcome>> checkout;
    private readonly Func<Task<MyPullRequests>> read;
    private readonly Func<Uri, ICommand> open;
    private readonly Func<IReadOnlyList<(string Repository, string Branch)>, Task<ActionOutcome>>? createDocker;
    private readonly Func<string, RepositoryType?> typeOf;

    /// <param name="read">Where the PRs come from: Azure DevOps if omitted, made-up data in the demo.</param>
    /// <param name="open">What a click on a PR or work item does: the browser if omitted.</param>
    /// <param name="createDocker">Creates a new Docker environment on the given branches and returns the outcome; without it, “Open in” does not offer it.</param>
    /// <param name="typeOf">A repository's layer from its name on the server: the active configuration if omitted, the made-up repositories in the demo.</param>
    public PullRequestsViewModel(
        IReadOnlyList<EnvironmentViewModel> environments,
        Func<int, IReadOnlyList<(string Repository, string Branch)>, Task<ActionOutcome>> checkout,
        Func<Task<MyPullRequests>>? read = null,
        Func<Uri, ICommand>? open = null,
        Func<IReadOnlyList<(string Repository, string Branch)>, Task<ActionOutcome>>? createDocker = null,
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

    /// <summary>An environment that just joined the grid: the groups recompute which environments their PRs are on.</summary>
    public void Track(EnvironmentViewModel environment)
    {
        environment.BranchesChanged += RefreshMatches;
        RefreshMatches();
    }

    /// <summary>An environment that left the grid: its branches are no longer tracked.</summary>
    public void Untrack(EnvironmentViewModel environment)
    {
        environment.BranchesChanged -= RefreshMatches;
        RefreshMatches();
    }

    private void RefreshMatches()
    {
        var groups = Created.Concat(ToReview).ToList();
        foreach (var group in groups)
        {
            group.RefreshMatch();
        }

        // The mirror of the tags on the environments' columns; a work item in both sections counts once.
        foreach (var environment in environments)
        {
            environment.SetMatchedWork(groups
                .Where(g => g.MatchingEnvironments.Contains(environment.Number))
                .Select(g => g.Mirror)
                .DistinctBy(m => m.Text)
                .ToList());
        }
    }

    public ObservableCollection<PullRequestGroup> Created { get; } = [];

    public ObservableCollection<PullRequestGroup> ToReview { get; } = [];

    public ICommand RefreshCommand { get; }

    /// <summary>
    /// All the things to do from the last successful read: the receiver decides what is new.
    /// A failed read does not raise it, because its empty lists would look like everything was resolved.
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

    /// <summary>Dot next to the title when idle: green if the last read succeeded, red if it failed.</summary>
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
        StatusLine = "reading from Azure DevOps…";

        var result = await read();

        Fill(Created, result.Created, pr => MineRow(pr, result.MyId));
        Fill(ToReview, result.ToReview, pr => ReviewRow(pr, result.MyId));

        if (result.Error is null)
        {
            AlertsRead?.Invoke(Created.Concat(ToReview).SelectMany(g => g.Rows).SelectMany(r => r.Alerts).ToList());
        }

        RefreshMatches();
        CreatedSummary = Summary(result.Created.Count, Created.Sum(g => g.Todo));
        ToReviewSummary = Summary(result.ToReview.Count, ToReview.Sum(g => g.Todo));
        ErrorLine = result.Error ?? string.Empty;
        StatusLine = result.Error is null ? $"updated at {DateTime.Now:HH:mm}" : "read failed";
        IsLoading = false;
        OnPropertyChanged(nameof(CreatedEmptyVisibility));
        OnPropertyChanged(nameof(ToReviewEmptyVisibility));
    }

    /// <summary>
    /// Expands and highlights for a few seconds the card of the work item containing the PR. Null if
    /// the PR is not in the lists (closed, or work already resolved and the PR gone since the last read).
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

    // Groups with the most to do first, then the most recent; "No work item" always at the bottom.
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
        => todo == 0 ? $"{count} open, nothing to do" : $"{todo} to do · {count} open";

    // On the user's own PRs there is work only while they are drafts: the reviewer puts them back
    // into draft when handing the PR back to the author, mirroring other people's PRs, where a
    // draft asks for nothing. While the PR is published, votes and comments stay in the details but
    // are not to-dos. In draft there is something to do when a reviewer asks for changes or a
    // comment awaits a reply (the last word in the thread is not the user's). Threads opened by the
    // user themselves always count: they are reminders to resolve, like other people's comments.
    private PullRequestRow MineRow(PullRequestSummary pr, string? myId)
    {
        var people = pr.Reviewers.Where(r => !r.IsGroup).ToList();
        var detail = people.Count == 0
            ? "No reviewers"
            : string.Join(Environment.NewLine, people.Select(r => $"{r.Name}: {VoteName(r.Vote)}{(r.IsRequired ? " (required)" : string.Empty)}"));

        var todos = new List<string>();
        var alerts = new List<PullRequestAlert>();
        if (pr.IsDraft)
        {
            foreach (var reviewer in people.Where(r => r.Vote < 0))
            {
                var todo = reviewer.Vote == -10 ? $"rework: rejected by {reviewer.Name}" : $"changes requested by {reviewer.Name}";
                todos.Add(todo);
                alerts.Add(new PullRequestAlert($"vote|{pr.Id}|{reviewer.Id}|{reviewer.Vote}", 1, $"Your PR !{pr.Id}: {todo}.", pr.Title, pr.WebUrl, pr.Id));
            }

            var replies = pr.ActiveThreads.Count(t => !IsMe(t.LastAuthorId, myId) || IsMe(t.AuthorIds[0], myId));
            if (replies > 0)
            {
                var todo = replies == 1 ? "reply to 1 comment" : $"reply to {replies} comments";
                todos.Add(todo);
                alerts.Add(new PullRequestAlert($"replies|{pr.Id}", replies, $"Your PR !{pr.Id}: {todo}.", pr.Title, pr.WebUrl, pr.Id));
            }
        }

        var approved = people.Count(r => r.Vote > 0);
        var (status, dot) = people.Count == 0 ? ("no reviewers", Neutral)
            : people.Any(r => r.Vote < 0) ? ("changes requested", Changes)
            : approved == people.Count ? ("approved", Approved)
            : ($"{approved}/{people.Count} approvals", Neutral);

        return new PullRequestRow(pr, showAuthor: false, todos, alerts, status, dot, detail, open, typeOf);
    }

    // On other people's PRs the to-dos are the vote and the replies to the user's own comments. A
    // draft asks for nothing: when the author publishes it again, the work reappears and becomes a notification.
    private PullRequestRow ReviewRow(PullRequestSummary pr, string? myId)
    {
        var vote = pr.Reviewers.FirstOrDefault(r => IsMe(r.Id, myId))?.Vote ?? 0;
        var detail = string.Join(Environment.NewLine, pr.Reviewers.Where(r => !r.IsGroup).Select(r => $"{r.Name}: {VoteName(r.Vote)}"));

        var todos = new List<string>();
        var alerts = new List<PullRequestAlert>();
        if (vote == 0 && !pr.IsDraft)
        {
            todos.Add("to review and vote");
            alerts.Add(new PullRequestAlert($"review|{pr.Id}", 1, $"PR !{pr.Id} by {pr.Author}: to review and vote.", pr.Title, pr.WebUrl, pr.Id));
        }

        var replies = pr.IsDraft ? 0 : pr.ActiveThreads.Count(t => t.AuthorIds.Any(a => IsMe(a, myId)) && !IsMe(t.LastAuthorId, myId));
        if (replies > 0)
        {
            var todo = replies == 1 ? "1 reply to your comments" : $"{replies} replies to your comments";
            todos.Add(todo);
            alerts.Add(new PullRequestAlert($"replies|{pr.Id}", replies, $"PR !{pr.Id} by {pr.Author}: {todo}.", pr.Title, pr.WebUrl, pr.Id));
        }

        var status = vote == 0 ? "you have not voted" : $"you voted: {VoteName(vote)}";
        var dot = vote > 0 ? Approved : vote < 0 ? Changes : Neutral;
        return new PullRequestRow(pr, showAuthor: true, todos, alerts, status, dot, detail, open, typeOf);
    }

    private static bool IsMe(string id, string? myId) => string.Equals(id, myId, StringComparison.OrdinalIgnoreCase);

    private static string VoteName(int vote) => vote switch
    {
        10 => "approved",
        5 => "approved with suggestions",
        -5 => "waiting for author",
        -10 => "rejected",
        _ => "no vote",
    };
}

internal static class Browser
{
    // The default browser starts from rundll32, outside the exe's job (see DevToolsController).
    public static ICommand OpenCommand(Uri url) => new AsyncCommand(() => Task.Run(() => Open(url)));

    /// <summary>Opens the URL in the default browser, outside the exe's job. Returns the activity line.</summary>
    public static string Open(Uri url)
    {
        DetachedLauncher.Start($"rundll32.exe url.dll,FileProtocolHandler {url.AbsoluteUri}", System.IO.Path.GetTempPath(), newConsole: false);
        return $"{url.AbsoluteUri} opened in the default browser";
    }

    public static void OpenFolder(string folder)
    {
        System.IO.Directory.CreateDirectory(folder);
        DetachedLauncher.Start($"explorer.exe \"{folder}\"", folder, newConsole: false);
    }
}

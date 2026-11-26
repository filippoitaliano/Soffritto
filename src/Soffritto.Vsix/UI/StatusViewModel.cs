using System.Runtime.Serialization;
using Soffritto.Presentation;
using Microsoft.VisualStudio.Extensibility.UI;

namespace Soffritto.Vsix.UI;

// The *Visibility properties are strings ("Visible"/"Collapsed") instead of bools: the Remote UI
// XAML dialect exposes no converters, and the Visibility type converter accepts the string.
// Same technique for the *Color properties, read by Border.Background through the Brush converter.

// Status dots (palette in Presentation's StatusBadge) and icons are drawn in XAML,
// dots as Ellipses and icons as Segoe Fluent Icons glyphs, instead of using <vs:Image>: that
// control delegates to CrispImage, which, when it inherits CrispImage.HighContrast=true from the tree
// hosting the Remote UI, serves the monochrome variant of the moniker. The glyphs follow the theme's
// text color, as in the Desktop app.

/// <summary>
/// The pill on the right of a list row, like the Note of the app's Lane: capitalised
/// first letter, red for what is broken and grey for the rest, in tints that work on light and dark.
/// </summary>
internal static class LaneNote
{
    public const string Error = "#E5484D";
    public const string Neutral = "#8A8F98";

    public static string Of(string kind, string hidden)
        => kind.Length == 0 || kind == hidden ? string.Empty : char.ToUpperInvariant(kind[0]) + kind[1..];

    public static string ColorOf(string kind, bool error) => error ? Error : Neutral;
}

[DataContract]
internal sealed class MigrationRow(string migrationId, string kind) : NotifyPropertyChangedObject
{
    [DataMember]
    public string MigrationId { get; } = migrationId;

    /// <summary>Short label next to the ID.</summary>
    [DataMember]
    public string Kind { get; } = kind;
}

[DataContract]
internal sealed class RecentMigrationRow(string displayName, string state, int position = 0, string name = "") : NotifyPropertyChangedObject
{
    /// <summary>Position in the context, in the row's grey badge as in the app; empty if unknown.</summary>
    [DataMember]
    public string Position { get; } = position > 0 ? position.ToString() : string.Empty;

    [DataMember]
    public string BadgeVisibility => Position.Length > 0 ? "Visible" : "Collapsed";

    /// <summary>Name without timestamp; without a position, the name to show in full.</summary>
    [DataMember]
    public string Name { get; } = name.Length > 0 ? name : displayName;

    /// <summary>The row's pill: only what is not “applied” (missing, out of order, copy feedback).</summary>
    [DataMember]
    public string Note => LaneNote.Of(kind, hidden: MigrationLabels.Applied);

    [DataMember]
    public string NoteColor => LaneNote.ColorOf(kind, error: kind == MigrationLabels.Missing);

    [DataMember]
    public string NoteVisibility => Note.Length > 0 ? "Visible" : "Collapsed";

    [DataMember]
    public string RunToolTip { get; set; } = string.Empty;

    /// <summary>Segoe Fluent Icons glyph of the run button: Play forward, Undo for a rollback.</summary>
    [DataMember]
    public string RunGlyph { get; set; } = string.Empty;

    /// <summary>False if the database is already at this migration.</summary>
    [DataMember]
    public bool CanRun { get; set; }

    /// <summary>False while a write action is in progress: copying stays available, running does not.</summary>
    public bool IsNotBusy
    {
        get => isNotBusy;
        set
        {
            if (SetProperty(ref isNotBusy, value))
            {
                RaiseNotifyPropertyChangedEvent(nameof(RunEnabled));
            }
        }
    }

    [DataMember]
    public bool RunEnabled => CanRun && isNotBusy;

    [DataMember]
    public AsyncCommand? RunCommand { get; set; }

    private bool isNotBusy = true;
    private string kind = state;
    private string kindColor = MigrationColors.For(state);

    /// <summary>Migration name without the timestamp: what you read at a glance.</summary>
    [DataMember]
    public string DisplayName { get; } = displayName;

    /// <summary>Migration state, or the transient copy feedback.</summary>
    [DataMember]
    public string Kind
    {
        get => kind;
        set
        {
            if (SetProperty(ref kind, value))
            {
                KindColor = MigrationColors.For(value);
                RaiseNotifyPropertyChangedEvent(nameof(Note));
                RaiseNotifyPropertyChangedEvent(nameof(NoteColor));
                RaiseNotifyPropertyChangedEvent(nameof(NoteVisibility));
            }
        }
    }

    [DataMember]
    public string KindColor { get => kindColor; private set => SetProperty(ref kindColor, value); }

    /// <summary>Label to restore when the copy feedback expires.</summary>
    internal string State { get; } = state;

    [DataMember]
    public string CopyToolTip { get; set; } = string.Empty;

    [DataMember]
    public AsyncCommand? CopyCommand { get; set; }
}

[DataContract]
internal sealed class TunnelPortRow(string endpoint, string url, string state, int portNumber = 0) : NotifyPropertyChangedObject
{
    private string kind = state;

    [DataMember]
    public string PortNumber { get; } = portNumber > 0 ? portNumber.ToString() : string.Empty;

    [DataMember]
    public string BadgeVisibility => PortNumber.Length > 0 ? "Visible" : "Collapsed";

    /// <summary>The host without scheme, as in the app: public tunnel URLs are https.</summary>
    [DataMember]
    public string Host { get; } = url.Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase);

    /// <summary>The pill: “no response” and the copy feedback; not “reachable”.</summary>
    [DataMember]
    public string Note => LaneNote.Of(kind, hidden: "reachable");

    [DataMember]
    public string NoteColor => LaneNote.ColorOf(kind, error: kind == StatusText.CopyFailed);

    [DataMember]
    public string NoteVisibility => Note.Length > 0 ? "Visible" : "Collapsed";

    /// <summary>Port and URL as they read in the row: "5000 · https://…".</summary>
    [DataMember]
    public string Endpoint { get; } = endpoint;

    /// <summary>
    /// Outcome of the HTTP probe ("reachable", "no response", empty if not run) or the
    /// transient copy feedback.
    /// </summary>
    [DataMember]
    public string Kind
    {
        get => kind;
        set
        {
            if (SetProperty(ref kind, value))
            {
                RaiseNotifyPropertyChangedEvent(nameof(Note));
                RaiseNotifyPropertyChangedEvent(nameof(NoteColor));
                RaiseNotifyPropertyChangedEvent(nameof(NoteVisibility));
            }
        }
    }

    /// <summary>Full URL the icon puts on the clipboard, without the trailing slash.</summary>
    internal string Url { get; } = url;

    /// <summary>Label to restore when the copy feedback expires.</summary>
    internal string State { get; } = state;

    [DataMember]
    public string CopyToolTip { get; set; } = string.Empty;

    [DataMember]
    public AsyncCommand? CopyCommand { get; set; }

    [DataMember]
    public string OpenToolTip { get; set; } = string.Empty;

    [DataMember]
    public AsyncCommand? OpenCommand { get; set; }
}

[DataContract]
internal sealed class TunnelSummaryRow(string title, string kind, string statusColor) : NotifyPropertyChangedObject
{
    /// <summary>Tunnel ID and its description: "jolly-river-lx35cgb.euw · 2".</summary>
    [DataMember]
    public string Title { get; } = title;

    /// <summary>Short state: "host connected · 2 ports".</summary>
    [DataMember]
    public string Kind { get; } = kind;

    [DataMember]
    public string StatusColor { get; } = statusColor;
}

/// <summary>
/// Expanding and collapsing a card: the header stays, the body hides. Same
/// behavior as the Desktop app (ViewModels/Expander.cs), on Remote UI types.
/// </summary>
[DataContract]
internal sealed class ExpanderState : NotifyPropertyChangedObject
{
    // Segoe Fluent Icons glyphs: arrow down (expanded) and right (collapsed).
    private const string ExpandedGlyph = "";
    private const string CollapsedGlyph = "";

    private bool isExpanded;

    public ExpanderState(bool isExpanded)
    {
        this.isExpanded = isExpanded;

        ToggleCommand = new AsyncCommand((_, _, _) =>
        {
            IsExpanded = !IsExpanded;
            return Task.CompletedTask;
        });
    }

    [DataMember]
    public AsyncCommand ToggleCommand { get; }

    public bool IsExpanded
    {
        get => isExpanded;
        set
        {
            if (SetProperty(ref isExpanded, value))
            {
                RaiseNotifyPropertyChangedEvent(nameof(BodyVisibility));
                RaiseNotifyPropertyChangedEvent(nameof(Glyph));
            }
        }
    }

    [DataMember]
    public string BodyVisibility => isExpanded ? "Visible" : "Collapsed";

    [DataMember]
    public string Glyph => isExpanded ? ExpandedGlyph : CollapsedGlyph;
}

[DataContract]
internal sealed class ContextRow : NotifyPropertyChangedObject
{
    private string stamp = string.Empty;
    private string? action;
    private bool isNotBusy = true;
    private string pageLabel = string.Empty;
    private bool canGoPrevious;
    private bool canGoNext;
    private IReadOnlyList<RecentMigrationRow> allRecent = [];
    private int page;

    public ContextRow(string name, string counters, bool isUpToDate, string statusColor)
    {
        Name = name;
        Counters = counters;
        StatusColor = statusColor;

        // An up-to-date context has nothing to say beyond its counter: it starts collapsed. The rows
        // are recreated on every read, so the state realigns on every refresh.
        Expander = new ExpanderState(isExpanded: !isUpToDate);

        // Paging is a local operation on rows already built: no reread,
        // so the commands live here instead of coming from the window like the real actions.
        PreviousPageCommand = new AsyncCommand((_, _, _) =>
        {
            ShowPage(page - 1);
            return Task.CompletedTask;
        });

        NextPageCommand = new AsyncCommand((_, _, _) =>
        {
            ShowPage(page + 1);
            return Task.CompletedTask;
        });
    }

    [DataMember]
    public string Name { get; }

    /// <summary>Notes, latest migrations and pager: visible only when the card is expanded.</summary>
    [DataMember]
    public ExpanderState Expander { get; }

    /// <summary>
    /// Time of the last read ("updated 10:32:15"), "reading…" while rereading, or the migration
    /// action in progress.
    /// </summary>
    [DataMember]
    public string Stamp { get => action ?? stamp; set => SetProperty(ref stamp, value); }

    /// <summary>Migration action in progress ("dotnet ef database update…"); null when the action ends.</summary>
    internal string? Action
    {
        get => action;
        set
        {
            if (!EqualityComparer<string?>.Default.Equals(action, value))
            {
                action = value;
                RaiseNotifyPropertyChangedEvent(nameof(Stamp));
            }
        }
    }

    /// <summary>False while a write action is in progress: disables all update buttons.</summary>
    [DataMember]
    public bool IsNotBusy
    {
        get => isNotBusy;
        set
        {
            if (!SetProperty(ref isNotBusy, value))
            {
                return;
            }

            foreach (var row in allRecent)
            {
                row.IsNotBusy = value;
            }
        }
    }

    [DataMember]
    public string Counters { get; }

    /// <summary>Dot after the name: green up to date, orange with migrations to sort out.</summary>
    [DataMember]
    public string StatusColor { get; }

    /// <summary>The current page of the context's migrations, newest first.</summary>
    [DataMember]
    public ObservableList<RecentMigrationRow> Recent { get; } = [];

    [DataMember]
    public string RecentVisibility { get; set; } = "Collapsed";

    /// <summary>The pager appears only when the migrations do not fit in one page.</summary>
    [DataMember]
    public string PagerVisibility { get; set; } = "Collapsed";

    /// <summary>Position of the page in the total: "1-8 of 47".</summary>
    [DataMember]
    public string PageLabel { get => pageLabel; set => SetProperty(ref pageLabel, value); }

    [DataMember]
    public bool CanGoPrevious { get => canGoPrevious; set => SetProperty(ref canGoPrevious, value); }

    [DataMember]
    public bool CanGoNext { get => canGoNext; set => SetProperty(ref canGoNext, value); }

    [DataMember]
    public AsyncCommand PreviousPageCommand { get; }

    [DataMember]
    public AsyncCommand NextPageCommand { get; }

    [DataMember]
    public string Note { get; set; } = string.Empty;

    [DataMember]
    public string NoteVisibility { get; set; } = "Collapsed";

    /// <summary>Visible only when all pending migrations come after the last applied one.</summary>
    [DataMember]
    public string ApplyVisibility { get; set; } = "Collapsed";

    [DataMember]
    public string ApplyToolTip { get; set; } = string.Empty;

    [DataMember]
    public AsyncCommand? ApplyCommand { get; set; }

    /// <summary>
    /// All the context's migrations, already sorted newest first: <see cref="Recent"/>
    /// shows one page at a time and starts again from the first.
    /// </summary>
    internal void SetRecent(IReadOnlyList<RecentMigrationRow> ordered)
    {
        allRecent = ordered;
        foreach (var row in ordered)
        {
            row.IsNotBusy = isNotBusy;
        }

        RecentVisibility = ordered.Count > 0 ? "Visible" : "Collapsed";
        PagerVisibility = ordered.Count > Pager.PageSize ? "Visible" : "Collapsed";
        ShowPage(0);
    }

    private void ShowPage(int index)
    {
        var window = Pager.Compute(allRecent.Count, index);
        page = window.Page;

        Recent.Clear();
        foreach (var row in allRecent.Skip(window.Start).Take(window.Count))
        {
            Recent.Add(row);
        }

        PageLabel = window.Label;
        CanGoPrevious = window.CanGoPrevious;
        CanGoNext = window.CanGoNext;
    }
}

[DataContract]
internal sealed class StatusViewModel : NotifyPropertyChangedObject
{
    private bool isNotBusy = true;
    private string activityLine = "starting…";
    private string splashVisibility = "Visible";
    private string mainVisibility = "Collapsed";
    private string clone = "\u2014";
    private string branch = "\u2014";
    private string server = "\u2014";
    private string database = "\u2014";
    private string repoLine = string.Empty;
    private string repoDetail = "waiting for the first read";
    private string upstreamWarning = string.Empty;
    private string upstreamWarningVisibility = "Collapsed";
    private string repoToolTip = string.Empty;
    private string repoStamp = "not read yet";
    private string tunnelStamp = "not read";
    private string tunnelLine = string.Empty;
    private string tunnelDetail = string.Empty;
    private string tunnelDetailVisibility = "Visible";
    private string tunnelPortsVisibility = "Collapsed";
    private string tunnelListVisibility = "Collapsed";
    private string errorLine = string.Empty;
    private string errorVisibility = "Collapsed";
    private string unknownVisibility = "Collapsed";
    private string unknownHeader = string.Empty;
    private string gitActionsVisibility = "Visible";
    private string pullToolTip = string.Empty;
    private string? selectedBranch;
    private string branchFilter = string.Empty;
    private string branchText = string.Empty;
    private bool branchDropDownOpen;
    private bool isLoading;
    private int activeOperations;
    private readonly object branchesGate = new();
    private List<string> allBranches = [];

    /// <summary>
    /// False only while a write action (pull, database update) is in progress. Section reads
    /// do not touch it: a slow tunnel read or fetch must block neither
    /// the actions nor a new refresh.
    /// </summary>
    [DataMember]
    public bool IsNotBusy { get => isNotBusy; set => SetProperty(ref isNotBusy, value); }

    // Until the first real read starts (solution resolved), the splash with the logo stands in for
    // the empty sections: a header full of dashes and "not read yet" sections would look like
    // a broken panel. The two visibilities are complementary.
    [DataMember]
    public string SplashVisibility { get => splashVisibility; set => SetProperty(ref splashVisibility, value); }

    [DataMember]
    public string MainVisibility { get => mainVisibility; set => SetProperty(ref mainVisibility, value); }

    /// <summary>
    /// What an action is doing (progress of pull, switch or database update), the title of
    /// its failure, or <see cref="StatusText.IdleActivity"/>. Always one line: status lines that
    /// appear and disappear would make the whole panel jump.
    /// </summary>
    [DataMember]
    public string ActivityLine
    {
        get => activityLine;
        set
        {
            if (SetProperty(ref activityLine, value))
            {
                RaiseNotifyPropertyChangedEvent(nameof(EnvironmentStatusColor));
            }
        }
    }

    /// <summary>Dot next to the environment name, as in the app: green when ready, grey otherwise.</summary>
    [DataMember]
    public string EnvironmentStatusColor => activityLine == StatusText.IdleActivity ? StatusBadge.OkColor : StatusBadge.OffColor;

    private string environmentTitle = string.Empty;
    private string note = string.Empty;
    private string notePlaceholder = string.Empty;
    private string noteVisibility = "Collapsed";

    /// <summary>"ENVIRONMENT 1", like the app's column header; empty until the clone is known.</summary>
    [DataMember]
    public string EnvironmentTitle { get => environmentTitle; private set => SetProperty(ref environmentTitle, value); }

    /// <summary>
    /// The environment's free-text note, the same one the app shows. The setter is called by the text box
    /// (TwoWay binding); what comes from the file goes through <see cref="ShowNote"/>.
    /// </summary>
    [DataMember]
    public string Note
    {
        get => note;
        set
        {
            if (SetProperty(ref note, value ?? string.Empty))
            {
                RaiseNotifyPropertyChangedEvent(nameof(NotePlaceholderVisibility));
                NoteEdited?.Invoke(note);
            }
        }
    }

    /// <summary>"add a note to environment 1…": says which environment the note belongs to.</summary>
    [DataMember]
    public string NotePlaceholder { get => notePlaceholder; private set => SetProperty(ref notePlaceholder, value); }

    [DataMember]
    public string NotePlaceholderVisibility => note.Length == 0 ? "Visible" : "Collapsed";

    /// <summary>The text box appears once the clone, and therefore the environment, is known.</summary>
    [DataMember]
    public string NoteVisibility { get => noteVisibility; private set => SetProperty(ref noteVisibility, value); }

    internal Action<string>? NoteEdited { get; set; }

    private string protectedDatabaseVisibility = "Collapsed";
    private string protectedDatabaseToolTip = string.Empty;
    private string dockerVisibility = "Collapsed";
    private string serverVisibility = "Visible";
    private string tunnelSwitchVisibility = "Collapsed";
    private bool tunnelOn;
    private bool tunnelSwitchEnabled = true;

    /// <summary>Red “Protected DB” tag in the header, as in the app: migrations are read but not applied.</summary>
    [DataMember]
    public string ProtectedDatabaseVisibility { get => protectedDatabaseVisibility; private set => SetProperty(ref protectedDatabaseVisibility, value); }

    [DataMember]
    public string ProtectedDatabaseTag => StatusText.ProtectedDatabaseTag;

    [DataMember]
    public string ProtectedDatabaseToolTip { get => protectedDatabaseToolTip; private set => SetProperty(ref protectedDatabaseToolTip, value); }

    /// <summary>Purple “Docker” tag: the open solution is the worktree of a Docker environment.</summary>
    [DataMember]
    public string DockerVisibility { get => dockerVisibility; private set => SetProperty(ref dockerVisibility, value); }

    /// <summary>In a Docker environment the server is always the slot's container: the tag says nothing.</summary>
    [DataMember]
    public string ServerVisibility { get => serverVisibility; private set => SetProperty(ref serverVisibility, value); }

    /// <summary>The Tunnel switch appears when the open solution is the backend of a configured environment.</summary>
    [DataMember]
    public string TunnelSwitchVisibility { get => tunnelSwitchVisibility; private set => SetProperty(ref tunnelSwitchVisibility, value); }

    /// <summary>The environment has pool ports: the state read from the assignments, not the click.</summary>
    [DataMember]
    public bool TunnelOn { get => tunnelOn; set => SetProperty(ref tunnelOn, value); }

    /// <summary>False while the tunnel is turning on or off: the switch stays put.</summary>
    [DataMember]
    public bool TunnelSwitchEnabled { get => tunnelSwitchEnabled; set => SetProperty(ref tunnelSwitchEnabled, value); }

    [DataMember]
    public string TunnelSwitchToolTip => tunnelOn
        ? "The environment has a pool tunnel: the URLs are in the tunnel card. Turn off to release the ports"
        : "Claims a free pool tunnel, gives a port to every repository that exposes one and turns on the host";

    [DataMember]
    public AsyncCommand TunnelSwitchCommand { get; set; } = null!;

    /// <summary>The environment of the open solution according to the configuration: header tags and switch.</summary>
    internal void ShowEnvironment(bool known, bool isDocker, string? schemaLock)
    {
        TunnelSwitchVisibility = known ? "Visible" : "Collapsed";
        DockerVisibility = isDocker ? "Visible" : "Collapsed";
        ServerVisibility = isDocker ? "Collapsed" : "Visible";
        ProtectedDatabaseVisibility = schemaLock is null ? "Collapsed" : "Visible";
        ProtectedDatabaseToolTip = schemaLock is null ? string.Empty : StatusText.ProtectedDatabaseToolTip;
    }

    internal void SetTunnelOn(bool value)
    {
        TunnelOn = value;
        RaiseNotifyPropertyChangedEvent(nameof(TunnelOn));
        RaiseNotifyPropertyChangedEvent(nameof(TunnelSwitchToolTip));
    }

    internal void ShowNote(string environment, string text)
    {
        EnvironmentTitle = $"ENVIRONMENT {environment}";
        NotePlaceholder = $"add a note to environment {environment}…";
        NoteVisibility = "Visible";

        if (SetProperty(ref note, text, nameof(Note)))
        {
            RaiseNotifyPropertyChangedEvent(nameof(NotePlaceholderVisibility));
        }
    }

    // Header at a glance. The default is an em dash (U+2014), written as an
    // escape so as not to depend on the file encoding.
    /// <summary>Name of the clone's working folder (e.g. backend-1).</summary>
    [DataMember]
    public string Clone { get => clone; set => SetProperty(ref clone, value); }

    [DataMember]
    public string Branch { get => branch; set => SetProperty(ref branch, value); }

    [DataMember]
    public string Server { get => server; set => SetProperty(ref server, value); }

    [DataMember]
    public string Database { get => database; set => SetProperty(ref database, value); }

    [DataMember]
    public string RepoLine { get => repoLine; set => SetProperty(ref repoLine, value); }

    [DataMember]
    public string RepoDetail { get => repoDetail; set => SetProperty(ref repoDetail, value); }

    [DataMember]
    public string UpstreamWarning { get => upstreamWarning; set => SetProperty(ref upstreamWarning, value); }

    [DataMember]
    public string UpstreamWarningVisibility { get => upstreamWarningVisibility; set => SetProperty(ref upstreamWarningVisibility, value); }

    [DataMember]
    public string RepoToolTip { get => repoToolTip; set => SetProperty(ref repoToolTip, value); }

    /// <summary>"updated HH:mm:ss", or what is being read: never empty, so the layout does not move.</summary>
    [DataMember]
    public string RepoStamp { get => repoStamp; set => SetProperty(ref repoStamp, value); }

    [DataMember]
    public string TunnelLine { get => tunnelLine; set => SetProperty(ref tunnelLine, value); }

    [DataMember]
    public string TunnelDetail { get => tunnelDetail; set => SetProperty(ref tunnelDetail, value); }

    /// <summary>The detail is needed only when the tunnel is not read or is in error: once the tunnel is resolved, badge and ports speak for it.</summary>
    [DataMember]
    public string TunnelDetailVisibility { get => tunnelDetailVisibility; set => SetProperty(ref tunnelDetailVisibility, value); }

    [DataMember]
    public string TunnelPortsVisibility { get => tunnelPortsVisibility; set => SetProperty(ref tunnelPortsVisibility, value); }

    [DataMember]
    public ObservableList<TunnelPortRow> TunnelPorts { get; } = [];

    /// <summary>The account's tunnels, shown only when none matches the clone.</summary>
    [DataMember]
    public ObservableList<TunnelSummaryRow> TunnelList { get; } = [];

    [DataMember]
    public string TunnelListVisibility { get => tunnelListVisibility; set => SetProperty(ref tunnelListVisibility, value); }

    /// <summary>"updated HH:mm:ss", "reading…" or "not read": never empty, so the layout does not move.</summary>
    [DataMember]
    public string TunnelStamp { get => tunnelStamp; set => SetProperty(ref tunnelStamp, value); }

    [DataMember]
    public string ErrorLine { get => errorLine; set => SetProperty(ref errorLine, value); }

    [DataMember]
    public string ErrorVisibility { get => errorVisibility; set => SetProperty(ref errorVisibility, value); }

    [DataMember]
    public string UnknownHeader { get => unknownHeader; set => SetProperty(ref unknownHeader, value); }

    [DataMember]
    public string UnknownVisibility { get => unknownVisibility; set => SetProperty(ref unknownVisibility, value); }

    /// <summary>Fetch, pull and branch drop-down: visible from the start, hidden only if git is unavailable.</summary>
    [DataMember]
    public string GitActionsVisibility { get => gitActionsVisibility; set => SetProperty(ref gitActionsVisibility, value); }

    [DataMember]
    public string PullToolTip { get => pullToolTip; set => SetProperty(ref pullToolTip, value); }

    /// <summary>The drop-down items: the current branch first, then those matching the filter, in order.</summary>
    [DataMember]
    public ObservableList<string> Branches { get; } = [];

    /// <summary>
    /// Text in the editable ComboBox's box. While typing it filters the items and opens the
    /// menu; when WPF rewrites it with the chosen item (or the current one) it is not a search.
    /// </summary>
    [DataMember]
    public string BranchText
    {
        get => branchText;
        set
        {
            var text = value ?? string.Empty;
            if (!SetProperty(ref branchText, text))
            {
                return;
            }

            if (text == selectedBranch || text == CurrentBranch)
            {
                ClearBranchFilter();
                return;
            }

            branchFilter = text;

            // If the selection was lost (text cleared by the control), with an empty filter
            // go back to the current branch; while typing the selection must not be touched, otherwise
            // WPF would rewrite the box with the name of the selected branch.
            SyncBranches(reselect: text.Length == 0 && selectedBranch is null);

            if (text.Length > 0)
            {
                BranchDropDownOpen = true;
            }
        }
    }

    /// <summary>
    /// Opened by the ViewModel as soon as you type, closed by the user or by a choice. If it closes
    /// (Esc, click outside, Tab) while the box still holds search text, the search
    /// is abandoned: the box goes back to showing the current branch.
    /// </summary>
    [DataMember]
    public bool BranchDropDownOpen
    {
        get => branchDropDownOpen;
        set
        {
            if (!SetProperty(ref branchDropDownOpen, value))
            {
                return;
            }

            if (!value && CurrentBranch is not null && branchText != CurrentBranch && branchText != selectedBranch)
            {
                RestoreBranchText();
            }
        }
    }

    /// <summary>True while at least one read, action or the initial wait is in progress: animates the bar at the bottom.</summary>
    [DataMember]
    public bool IsLoading { get => isLoading; private set => SetProperty(ref isLoading, value); }

    internal void BeginOperation()
    {
        if (Interlocked.Increment(ref activeOperations) == 1)
        {
            IsLoading = true;
        }
    }

    internal void EndOperation()
    {
        if (Interlocked.Decrement(ref activeOperations) == 0)
        {
            IsLoading = false;
        }
    }

    private void RestoreBranchText()
    {
        branchFilter = string.Empty;
        SyncBranches(reselect: true);
        SetProperty(ref branchText, CurrentBranch ?? string.Empty, nameof(BranchText));
    }

    /// <summary>Current branch according to the last read: a different selection is a switch request.</summary>
    internal string? CurrentBranch { get; private set; }

    /// <summary>
    /// New git read: sets the current branch and the full list, then realigns the drop-down
    /// on the current one. A null <paramref name="current"/> means git is unavailable: empty drop-down.
    /// </summary>
    internal void SetBranches(string? current, IEnumerable<string> names)
    {
        lock (branchesGate)
        {
            CurrentBranch = current;
            allBranches = names.ToList();
        }

        SyncBranches(reselect: true);
    }

    /// <summary>Called by the <see cref="SelectedBranch"/> setter when the user picks a branch other than the current one.</summary>
    internal Action<string>? BranchSelected { get; set; }

    // TwoWay binding from the ComboBox: the setter is called both by the user's choices and by
    // the presenter's reassignments (and with null if the selected item disappears). Only a choice
    // different from the current branch is an intent to switch. A choice closes the
    // search anyway: the text echo WPF sends right after finds the filter already empty.
    [DataMember]
    public string? SelectedBranch
    {
        get => selectedBranch;
        set
        {
            if (!SetProperty(ref selectedBranch, value))
            {
                return;
            }

            if (value is null)
            {
                return;
            }

            ClearBranchFilter();
            SetProperty(ref branchText, value, nameof(BranchText));
            BranchDropDownOpen = false;

            if (value != CurrentBranch)
            {
                BranchSelected?.Invoke(value);
            }
        }
    }

    private void ClearBranchFilter()
    {
        if (branchFilter.Length == 0)
        {
            return;
        }

        branchFilter = string.Empty;
        SyncBranches(reselect: false);
    }

    private void SyncBranches(bool reselect)
    {
        lock (branchesGate)
        {
            var current = CurrentBranch;

            BranchList.Sync(Branches, BranchList.Desired(current, allBranches, branchFilter));

            if (current is null)
            {
                SelectedBranch = null;
            }
            else if (reselect)
            {
                SelectedBranch = current;
            }
        }
    }

    [DataMember]
    public ObservableList<ContextRow> Contexts { get; } = [];

    [DataMember]
    public ObservableList<MigrationRow> Unknown { get; } = [];

    [DataMember]
    public AsyncCommand RefreshCommand { get; set; } = null!;

    [DataMember]
    public AsyncCommand PullCommand { get; set; } = null!;

    [DataMember]
    public AsyncCommand FetchCommand { get; set; } = null!;

    [DataMember]
    public AsyncCommand PruneCommand { get; set; } = null!;

    // Cards stay as the user leaves them, even across refreshes: they start expanded.
    [DataMember]
    public ExpanderState RepoExpander { get; } = new(isExpanded: true);

    [DataMember]
    public ExpanderState TunnelExpander { get; } = new(isExpanded: true);

    [DataMember]
    public ExpanderState UnknownExpander { get; } = new(isExpanded: true);

    /// <summary>Extension version for the footer: "v1.0.3".</summary>
    [DataMember]
    public string AppVersion { get; } =
        $"v{typeof(StatusViewModel).Assembly.GetName().Version?.ToString(3) ?? "?"}";

    // Kestrel (or the api container in a Docker environment) has a dot and start/stop in the repository
    // card; the tunnel host has only the dot, in the tunnel card. Start and stop
    // are the same button: the glyph says what it will do.
    private const string PlayGlyph = "";
    private const string StopGlyph = "";

    private string kestrelColor = ConsoleServices.ColorOf(ServiceState.Stopped);
    private string kestrelToolTip = string.Empty;
    private string kestrelToggleGlyph = PlayGlyph;
    private bool kestrelToggleEnabled;
    private string kestrelToggleToolTip = string.Empty;
    private string tunnelHostColor = ConsoleServices.ColorOf(ServiceState.Stopped);
    private string tunnelHostToolTip = string.Empty;

    [DataMember]
    public string KestrelColor { get => kestrelColor; set => SetProperty(ref kestrelColor, value); }

    [DataMember]
    public string KestrelToolTip { get => kestrelToolTip; set => SetProperty(ref kestrelToolTip, value); }

    [DataMember]
    public string KestrelToggleGlyph { get => kestrelToggleGlyph; set => SetProperty(ref kestrelToggleGlyph, value); }

    [DataMember]
    public bool KestrelToggleEnabled { get => kestrelToggleEnabled; set => SetProperty(ref kestrelToggleEnabled, value); }

    [DataMember]
    public string KestrelToggleToolTip { get => kestrelToggleToolTip; set => SetProperty(ref kestrelToggleToolTip, value); }

    [DataMember]
    public string TunnelHostColor { get => tunnelHostColor; set => SetProperty(ref tunnelHostColor, value); }

    [DataMember]
    public string TunnelHostToolTip { get => tunnelHostToolTip; set => SetProperty(ref tunnelHostToolTip, value); }

    [DataMember]
    public AsyncCommand KestrelToggleCommand { get; set; } = null!;

    private bool kestrelCanStop;

    /// <summary>True if the Kestrel button stops instead of starting: it is then red, as in the app.</summary>
    [DataMember]
    public bool KestrelCanStop { get => kestrelCanStop; private set => SetProperty(ref kestrelCanStop, value); }

    /// <summary>Applies the state of Kestrel and of the tunnel host computed by BackendTools (local environment).</summary>
    internal void ApplyTools(BackendToolsView view)
    {
        KestrelColor = ConsoleServices.ColorOf(view.Kestrel);
        KestrelToolTip = $"Kestrel: {ConsoleServices.Describe(view.Kestrel)}";
        KestrelCanStop = view.Kestrel != ServiceState.Stopped;
        KestrelToggleGlyph = KestrelCanStop ? StopGlyph : PlayGlyph;
        KestrelToggleEnabled = true;
        KestrelToggleToolTip = KestrelCanStop
            ? "Stop Kestrel started from the console (closes the console and its child processes)"
            : "Start Kestrel (dotnet run) in a console";

        // The pool tunnel host is turned on and off with the environment's Tunnel switch.
        TunnelHostColor = ConsoleServices.ColorOf(view.Tunnel);
        TunnelHostToolTip = view.TunnelToolTip;
    }

    /// <summary>
    /// In the worktree of a Docker environment the Kestrel button controls the api container:
    /// <paramref name="state"/> is null if Docker does not respond (<paramref name="error"/>).
    /// </summary>
    internal void ApplyContainer(string service, Soffritto.Core.Docker.ContainerRunState? state, string? error)
    {
        KestrelColor = state switch
        {
            Soffritto.Core.Docker.ContainerRunState.Running => StatusBadge.OkColor,
            Soffritto.Core.Docker.ContainerRunState.Starting => StatusBadge.WarningColor,
            null => StatusBadge.ErrorColor,
            _ => StatusBadge.OffColor,
        };
        KestrelToolTip = state switch
        {
            Soffritto.Core.Docker.ContainerRunState.Running => $"container {service}: running",
            Soffritto.Core.Docker.ContainerRunState.Starting => $"container {service}: starting",
            null => $"container {service}: {error}",
            _ => $"container {service}: stopped",
        };
        KestrelCanStop = state is Soffritto.Core.Docker.ContainerRunState.Running or Soffritto.Core.Docker.ContainerRunState.Starting;
        KestrelToggleGlyph = KestrelCanStop ? StopGlyph : PlayGlyph;
        KestrelToggleEnabled = state is not null;
        KestrelToggleToolTip = KestrelCanStop
            ? $"Stop the {service} container (docker compose stop)"
            : $"Start the {service} container (docker compose up --build)";
    }
}

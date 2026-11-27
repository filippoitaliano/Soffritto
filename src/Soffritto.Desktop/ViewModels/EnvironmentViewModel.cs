using System.Windows.Input;
using Soffritto.Core.Configuration;
using Soffritto.Desktop.Infrastructure;
using Soffritto.Presentation;

namespace Soffritto.Desktop.ViewModels;

/// <summary>A column of the window: environment N with the cards of its repositories.</summary>
public sealed class EnvironmentViewModel : ObservableObject
{
    private bool isCollapsed;
    private readonly Action beginOperation;
    private readonly Action endOperation;
    private readonly EnvironmentNote? note;
    private string noteText;
    private string activityLine = "starting…";
    private bool isNotBusy = true;
    private bool isLoading;
    private int activeOperations;
    private readonly RepoCardViewModel? backendCard;

    /// <param name="demoNote">Demo only: the note starts from this text and stays in memory, with no file.</param>
    public EnvironmentViewModel(EnvironmentLayout layout, Action beginOperation, Action endOperation, string? demoNote = null)
    {
        this.beginOperation = beginOperation;
        this.endOperation = endOperation;

        Number = layout.Number;
        SchemaLock = layout.SchemaLock;
        Title = $"ENV {layout.Number}";
        Repos = layout.Repos.Select(location => CreateCard(layout.Number, location)).ToList();
        Previews = Repos.Where(card => card.Preview is not null).Select(card => card.Preview!).ToList();
        backendCard = Repos.FirstOrDefault(card => card.Type == RepositoryType.Backend && card.IsPrimary);
        if (backendCard is not null)
        {
            backendCard.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(RepoCardViewModel.Server) or nameof(RepoCardViewModel.Database))
                {
                    OnPropertyChanged(nameof(DatabaseTag));
                    OnPropertyChanged(nameof(ServerTag));
                    OnPropertyChanged(nameof(DatabaseTagToolTip));
                    OnPropertyChanged(nameof(ServerTagToolTip));
                }
            };
        }
        isCollapsed = demoNote is null && AppSettings.Current.CollapsedEnvironments.Contains(Number);
        ToggleCollapseCommand = new RelayCommand(_ => IsCollapsed = !IsCollapsed);
        // The demo does not look up a slot in the real configuration: its number may match a real environment.
        Docker = layout.Kind != EnvironmentKind.Docker ? null
            : demoNote is null ? CreateDocker(layout.Number)
            : new DockerEnvironmentViewModel(null, null);
        if (Docker is not null)
        {
            OperationLog = demoNote is null ? OperationLogs.For(Number) : new LogSectionViewModel("Operations");
            Docker.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(DockerEnvironmentViewModel.IsWarmingUp))
                {
                    OnPropertyChanged(nameof(IsHeaderBusy));
                }

                OnPropertyChanged(nameof(StatusLine));
                OnPropertyChanged(nameof(StatusColor));
            };
        }

        if (demoNote is not null)
        {
            noteText = demoNote;
            return;
        }

        note = new EnvironmentNote(layout.Number.ToString(), text => UiThread.Run(() => ApplyExternalNote(text)));
        noteText = note.Text;
    }

    public int Number { get; }

    public string Title { get; }

    /// <summary>The Docker part of the column; null for a local environment.</summary>
    public DockerEnvironmentViewModel? Docker { get; }

    public bool IsDocker => Docker is not null;

    public string KindBadgeVisibility => IsDocker ? "Visible" : "Collapsed";

    /// <summary>Why the database schema must not be touched (protected database); null if it can be.</summary>
    public string? SchemaLock { get; }

    public string ProtectedDatabaseVisibility => SchemaLock is null ? "Collapsed" : "Visible";

    public string ProtectedDatabaseTag => StatusText.ProtectedDatabaseTag;

    public string ProtectedDatabaseToolTip => SchemaLock is null ? string.Empty : StatusText.ProtectedDatabaseToolTip;

    /// <summary>The database of the environment's default backend, read from appsettings.</summary>
    public string DatabaseTag => backendCard?.Database ?? string.Empty;

    public string DatabaseTagVisibility => backendCard is null ? "Collapsed" : "Visible";

    public string ServerTag => backendCard?.Server ?? string.Empty;

    /// <summary>In a Docker environment the server is always the slot's container: the tag says nothing.</summary>
    public string ServerTagVisibility => backendCard is null || IsDocker ? "Collapsed" : "Visible";

    public string DatabaseTagToolTip => $"Database of the backend, from its appsettings: {DatabaseTag}";

    public string ServerTagToolTip => $"Server of the backend's database, from its appsettings: {ServerTag}";

    /// <summary>Operations log of the Docker environment (create, start, stop, repair env, destroy); null for a local one.</summary>
    public LogSectionViewModel? OperationLog { get; }

    /// <summary>
    /// Activity line of an action: shows it in the header and, in Docker environments, appends
    /// it to the operations log. Callable from any thread.
    /// </summary>
    public void Report(string line)
    {
        OperationLog?.Append(line);
        UiThread.Run(() => ActivityLine = line);
    }

    // A slot that cannot be computed does not remove the column: the database guard will refuse every action.
    private static DockerEnvironmentViewModel CreateDocker(int number)
    {
        var configuration = ActiveConfiguration.Current;
        try
        {
            return configuration.Environment(number) is { } environment
                ? new DockerEnvironmentViewModel(Soffritto.Core.Docker.DockerSlot.Of(configuration, environment), null)
                : new DockerEnvironmentViewModel(null, $"slot cannot be computed: environment {number} is not in the configuration");
        }
        catch (Exception ex)
        {
            return new DockerEnvironmentViewModel(null, $"slot cannot be computed: {ex.Message}");
        }
    }

    /// <summary>The environment's free-form note, the same one the VSIX shows: it saves as you type.</summary>
    public string Note
    {
        get => noteText;
        set
        {
            if (SetProperty(ref noteText, value ?? string.Empty))
            {
                note?.Edit(noteText);
                OnPropertyChanged(nameof(NotePlaceholderVisibility));
            }
        }
    }

    public string NotePlaceholderVisibility => noteText.Length == 0 ? "Visible" : "Collapsed";



    /// <summary>Saves the pending note and stops watching the file.</summary>
    public void CloseNote() => note?.Dispose();

    private void ApplyExternalNote(string text)
    {
        if (SetProperty(ref noteText, text, nameof(Note)))
        {
            OnPropertyChanged(nameof(NotePlaceholderVisibility));
        }
    }

    /// <summary>The cards of the environment's repositories, in catalog order.</summary>
    public IReadOnlyList<RepoCardViewModel> Repos { get; }

    /// <summary>The default card of that type (the first repository of the type); null if the environment has none.</summary>
    public RepoCardViewModel? Card(RepositoryType type) => Repos.FirstOrDefault(card => card.Type == type && card.IsPrimary);

    /// <summary>The Claude browser previews, one per frontend card.</summary>
    public IReadOnlyList<FrontendPreviewViewModel> Previews { get; }

    /// <summary>The column is hidden (it reopens from the chip in the header): remembered across launches.</summary>
    public bool IsCollapsed
    {
        get => isCollapsed;
        set
        {
            if (SetProperty(ref isCollapsed, value))
            {
                // The demo uses the same numbers as real environments: its columns are not remembered.
                if (note is not null)
                {
                    AppSettings.Current.SetCollapsed(Number, value);
                }

                CollapsedChanged?.Invoke();
            }
        }
    }

    public ICommand ToggleCollapseCommand { get; }

    /// <summary>After every opening or closing of the column: the grid's minimum width changes.</summary>
    public event Action? CollapsedChanged;

    // Each card carries the tools and sections of its type; a generic one opens VS Code and starts nothing.
    private static RepoCardViewModel CreateCard(int number, RepoLocation location)
    {
        var card = new RepoCardViewModel(location);
        card.Tool = location.Settings is BackendConfiguration backend
            ? new ToolGroupViewModel("VS", $"Open {backend.Solution} in Visual Studio")
            : new ToolGroupViewModel("VSC", "Open the folder in VS Code") { HasToggle = location.Type != RepositoryType.Generic };

        if (location.Settings.DevTunnel is not null)
        {
            card.Tunnel = new TunnelSection(new TunnelViewModel(), new ToolGroupViewModel(string.Empty, string.Empty));
        }

        if (location.Type == RepositoryType.Backend)
        {
            card.Migrations = new BackendViewModel();
        }

        if (location.Type == RepositoryType.Frontend)
        {
            card.Preview = new FrontendPreviewViewModel(
                location.IsPrimary ? $"Environment {number}" : $"Environment {number} · {location.Id}",
                location);
        }

        return card;
    }

    /// <summary>After every branch read of a repository of the environment: the PR panel recomputes which work items sit on it.</summary>
    public event Action? BranchesChanged;

    /// <summary>
    /// After every branch read of a repository: the branches of the chip's card and the
    /// <see cref="BranchesChanged"/> event, from which the PR panel's environment tags and their
    /// mirror on this column are recomputed. Missing clones are left out.
    /// </summary>
    public void RefreshBranches()
    {
        var present = Repos.Where(repo => repo.ContentVisibility == "Visible").ToList();
        ChipBranches = present
            .Select(repo => new ChipBranch(repo.IsPrimary ? repo.RepositoryName : repo.Clone, repo.BranchPicker.CurrentBranch ?? "—"))
            .ToList();
        OnPropertyChanged(nameof(ChipBranches));
        BranchesChanged?.Invoke();
    }

    /// <summary>The branch of every present repository, for the header chip's card.</summary>
    public IReadOnlyList<ChipBranch> ChipBranches { get; private set; } = [];

    /// <summary>
    /// The environment is exactly where “Open in” would take it with these branches: every listed
    /// repository has its clone on its branch, and the other present clones, generic ones included,
    /// are on <see cref="Soffritto.Core.GitConventions.DevelopmentBranch"/>.
    /// </summary>
    public bool IsOnBranches(IReadOnlyList<(string Repository, string Branch)> targets)
        => EnvironmentWork.IsOnBranches(
            Repos.Select(repo => new WorkRepository(repo.RepositoryName, repo.ContentVisibility == "Visible", repo.BranchPicker.CurrentBranch)),
            targets);

    /// <summary>
    /// The work items (or PRs without one) of the PR panel whose branches the environment sits on:
    /// the mirror of the environment tag on the panel's cards. Empty if none, or before the first read.
    /// </summary>
    public IReadOnlyList<MatchedWork> MatchedWork { get; private set; } = [];

    public string MatchedWorkVisibility => MatchedWork.Count > 0 ? "Visible" : "Collapsed";

    /// <summary>Called by the PR panel after every match recomputation.</summary>
    public void SetMatchedWork(IReadOnlyList<MatchedWork> work)
    {
        if (work.Select(w => w.Text).SequenceEqual(MatchedWork.Select(w => w.Text)))
        {
            return;
        }

        MatchedWork = work;
        OnPropertyChanged(nameof(MatchedWork));
        OnPropertyChanged(nameof(MatchedWorkVisibility));
    }

    public ToolBarViewModel Tools { get; } = new();


    /// <summary>
    /// Progress of an action, the title of its failure, or "ready". One line per environment:
    /// write actions on the three repos go through the same gate.
    /// </summary>
    public string ActivityLine
    {
        get => activityLine;
        set
        {
            if (SetProperty(ref activityLine, value))
            {
                OnPropertyChanged(nameof(StatusColor));
                OnPropertyChanged(nameof(StatusLine));
            }
        }
    }

    /// <summary>
    /// The header status line: <see cref="ActivityLine"/>; in a Docker environment that is idle,
    /// starting up or has a failed container, the containers' phase.
    /// </summary>
    public string StatusLine => Docker is { PhaseText.Length: > 0 } docker
        && (docker.IsWarmingUp || docker.HasFailure || activityLine == StatusText.IdleActivity)
            ? docker.PhaseText
            : activityLine;

    /// <summary>
    /// Header dot when idle: red with a failed container, green if the last action ended well
    /// ("ready") and the Docker environment is not off, grey otherwise.
    /// </summary>
    public string StatusColor => Docker?.HasFailure == true
        ? StatusBadge.ErrorColor
        : activityLine == StatusText.IdleActivity && Docker?.IsOff != true ? StatusBadge.OkColor : StatusBadge.OffColor;

    public bool IsNotBusy { get => isNotBusy; set => SetProperty(ref isNotBusy, value); }

    public bool IsLoading
    {
        get => isLoading;
        private set
        {
            if (SetProperty(ref isLoading, value))
            {
                OnPropertyChanged(nameof(IsHeaderBusy));
            }
        }
    }

    /// <summary>An environment action in progress or containers starting up: the environment card scrolls in stripes.</summary>
    public bool IsHeaderBusy => isLoading || Docker?.IsWarmingUp == true;

    public ICommand? RefreshCommand { get; set; }

    /// <summary>Stops everything and, if the repositories are clean, brings them back to dev: see <see cref="EnvironmentController.CheckoutAsync"/>.</summary>
    public ICommand? ResetCommand { get; set; }

    private bool tunnelOn;
    private bool tunnelBusy;

    /// <summary>The environment has the tunnel: at least one pool port assigned to one of its repositories.</summary>
    public bool TunnelOn => tunnelOn;

    /// <summary>
    /// The state read from the assignments. It always notifies, so the checkbox goes back to how it
    /// was when the click changed nothing (pool full, error); during a change it stays put.
    /// </summary>
    public void SetTunnelOn(bool value)
    {
        if (tunnelBusy)
        {
            return;
        }

        tunnelOn = value;
        OnPropertyChanged(nameof(TunnelOn));
        OnPropertyChanged(nameof(TunnelToolTip));
    }

    /// <summary>True while the tunnel is turning on or off: the switch stays put.</summary>
    public bool TunnelBusy
    {
        get => tunnelBusy;
        set
        {
            if (SetProperty(ref tunnelBusy, value))
            {
                OnPropertyChanged(nameof(TunnelEnabled));
            }
        }
    }

    public bool TunnelEnabled => !tunnelBusy;

    public string TunnelToolTip => tunnelOn
        ? "The environment has a pool tunnel: the URLs are in the cards. Uncheck to release the ports and give the tunnel back"
        : "Claims a free pool tunnel, gives a port to every repository that exposes one (anonymous access) and turns on the host";

    /// <summary>Turns the environment's tunnel on or off: see <see cref="Soffritto.Presentation.EnvironmentTunnel"/>.</summary>
    public ICommand? TunnelCommand { get; set; }

    public void BeginOperation()
    {
        if (Interlocked.Increment(ref activeOperations) == 1)
        {
            IsLoading = true;
        }

        beginOperation();
    }

    public void EndOperation()
    {
        if (Interlocked.Decrement(ref activeOperations) == 0)
        {
            IsLoading = false;
        }

        endOperation();
    }
}

/// <summary>A repository in the environment chip's card: name and current branch.</summary>
public sealed record ChipBranch(string Repository, string Branch);

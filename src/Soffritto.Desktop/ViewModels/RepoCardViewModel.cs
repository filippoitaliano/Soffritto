using System.Windows.Input;
using Soffritto.Core.Configuration;
using Soffritto.Desktop.Infrastructure;
using Soffritto.Presentation;

namespace Soffritto.Desktop.ViewModels;

/// <summary>Tunnel details read from the devtunnel CLI, and the tunnel host with its start/stop.</summary>
public sealed record TunnelSection(TunnelViewModel State, ToolGroupViewModel Host);

/// <summary>
/// The card of a repository inside an environment column: git section, errors and, for backend
/// and frontend, the dev tunnel; for the backend also migrations and contexts, for the frontend
/// the Claude browser preview.
/// </summary>
public sealed class RepoCardViewModel : ObservableObject
{
    // The em dash (U+2014) marks a value not read yet.
    private string server = "—";
    private string database = "—";
    private string repoLine;
    private string repoDetail = StatusText.WaitingFirstRead;
    private string upstreamWarning = string.Empty;
    private string repoToolTip;
    private string repoStamp = StatusText.NotYetRead;
    private string gitActionsVisibility = "Visible";
    private string pullToolTip = string.Empty;
    private string missingVisibility = "Collapsed";
    private string contentVisibility = "Visible";
    private string errorLine = string.Empty;
    private string errorVisibility = "Collapsed";
    private bool isNotBusy = true;
    private bool isWorking;
    private ICommand? fetchCommand;
    private ICommand? pullCommand;
    private ICommand? pruneCommand;

    public RepoCardViewModel(RepoLocation location)
    {
        Location = location;
        repoLine = location.FolderName;
        repoToolTip = location.Path;
    }

    public RepoLocation Location { get; }

    public RepositoryType Type => Location.Type;

    public string Id => Location.Id;

    /// <summary>The first of its type in the environment: it keeps the historical console and tunnel names.</summary>
    public bool IsPrimary => Location.IsPrimary;

    /// <summary>The id next to the folder name, only for non-default repositories.</summary>
    public string IdTagVisibility => IsPrimary ? "Collapsed" : "Visible";

    /// <summary>The card's tag group is shown when its only tag, the id, is.</summary>
    public string TagsVisibility => IdTagVisibility;

    /// <summary>The repository name on the server: used by “Open in” and by Reset.</summary>
    public string RepositoryName => ActiveConfiguration.RepositoryNameOf(Location.Settings);

    public string Clone => Location.FolderName;

    public string Path => Location.Path;

    public BranchPickerViewModel BranchPicker { get; } = new();

    /// <summary>The repository's dropdown and details; the chosen state survives refreshes.</summary>
    public Expander Expander { get; } = new(isExpanded: true);

    /// <summary>Server and database read from appsettings: the environment card shows them as tags.</summary>
    public string Server { get => server; set => SetProperty(ref server, value); }

    public string Database { get => database; set => SetProperty(ref database, value); }

    /// <summary>Open in editor, start and stop: next to the repository name.</summary>
    public ToolGroupViewModel? Tool { get; set; }

    /// <summary>Dev tunnel section inside the card body: on the backend and frontend cards.</summary>
    public TunnelSection? Tunnel { get; set; }

    private bool tunnelShared;

    /// <summary>The repository has a pool port: only then does the card show the tunnel section.</summary>
    public bool TunnelShared
    {
        get => tunnelShared;
        set
        {
            if (SetProperty(ref tunnelShared, value))
            {
                OnPropertyChanged(nameof(TunnelVisibility));
            }
        }
    }

    public string TunnelVisibility => Tunnel is null || !tunnelShared ? "Collapsed" : "Visible";

    /// <summary>Claude browser preview section inside the card body: only on the frontend card.</summary>
    public FrontendPreviewViewModel? Preview { get; set; }

    public string PreviewVisibility => Preview is null ? "Collapsed" : "Visible";

    /// <summary>Unknown migrations and contexts sections at the bottom of the card body: only on the backend card.</summary>
    public BackendViewModel? Migrations { get; set; }

    public string MigrationsVisibility => Migrations is null ? "Collapsed" : "Visible";

    /// <summary>Last lines of the card's container: only on the backend and frontend cards of a Docker environment.</summary>
    public LogSectionViewModel? ContainerLog { get; set; }

    public string ContainerLogVisibility => ContainerLog is null ? "Collapsed" : "Visible";

    public string RepoLine { get => repoLine; set => SetProperty(ref repoLine, value); }

    public string RepoDetail { get => repoDetail; set => SetProperty(ref repoDetail, value); }

    /// <summary>The "no upstream configured" warning or empty: in red on the detail line, also read by the cat.</summary>
    public string UpstreamWarning
    {
        get => upstreamWarning;
        set
        {
            if (SetProperty(ref upstreamWarning, value))
            {
                OnPropertyChanged(nameof(UpstreamWarningVisibility));
            }
        }
    }

    public string UpstreamWarningVisibility => upstreamWarning.Length > 0 ? "Visible" : "Collapsed";

    public string RepoToolTip { get => repoToolTip; set => SetProperty(ref repoToolTip, value); }

    /// <summary>"updated HH:mm:ss", or what is being read: never empty, so the layout does not shift.</summary>
    public string RepoStamp
    {
        get => repoStamp;
        set
        {
            if (SetProperty(ref repoStamp, value))
            {
                OnPropertyChanged(nameof(IsLoading));
                OnPropertyChanged(nameof(IsBusyVisual));
            }
        }
    }

    /// <summary>In-progress read labels end with an ellipsis ("reading…", "git fetch…"): it makes the dot pulse.</summary>
    public bool IsLoading => repoStamp.EndsWith('…');

    /// <summary>A long card action in progress (container, pool port): see <see cref="IsBusyVisual"/>.</summary>
    public bool IsWorking
    {
        get => isWorking;
        set
        {
            if (SetProperty(ref isWorking, value))
            {
                OnPropertyChanged(nameof(IsBusyVisual));
            }
        }
    }

    /// <summary>Read or action in progress: the card background scrolls in stripes (<see cref="CardBusy"/>).</summary>
    public bool IsBusyVisual => IsLoading || isWorking;

    /// <summary>Fetch, pull, prune and dropdown: visible from the start, hidden only if git is not available.</summary>
    public string GitActionsVisibility { get => gitActionsVisibility; set => SetProperty(ref gitActionsVisibility, value); }

    public string PullToolTip { get => pullToolTip; set => SetProperty(ref pullToolTip, value); }

    public string MissingVisibility { get => missingVisibility; private set => SetProperty(ref missingVisibility, value); }

    public string ContentVisibility { get => contentVisibility; private set => SetProperty(ref contentVisibility, value); }

    public string ErrorLine { get => errorLine; set => SetProperty(ref errorLine, value); }

    public string ErrorVisibility { get => errorVisibility; set => SetProperty(ref errorVisibility, value); }

    /// <summary>False while a write action is running on a repo of the same environment.</summary>
    public bool IsNotBusy { get => isNotBusy; set => SetProperty(ref isNotBusy, value); }

    public ICommand? FetchCommand { get => fetchCommand; set => SetProperty(ref fetchCommand, value); }

    public ICommand? PullCommand { get => pullCommand; set => SetProperty(ref pullCommand, value); }

    public ICommand? PruneCommand { get => pruneCommand; set => SetProperty(ref pruneCommand, value); }

    /// <summary>Checks the folder again: a missing card starts no reads and says so in place of its content.</summary>
    public bool RefreshExistence()
    {
        var exists = Location.Exists();
        Preview?.Reload(Location);

        MissingVisibility = exists ? "Collapsed" : "Visible";
        ContentVisibility = exists ? "Visible" : "Collapsed";

        if (!exists)
        {
            RepoLine = $"{Clone} · folder not found";
            RepoDetail = Path;
            UpstreamWarning = string.Empty;
        }

        return exists;
    }
}

using System.Windows.Input;
using Soffritto.Core.Configuration;
using Soffritto.Desktop.Infrastructure;
using Soffritto.Presentation;

namespace Soffritto.Desktop.ViewModels;

/// <summary>Dettaglio del tunnel letto dalla CLI devtunnel e host del tunnel con il suo avvia/ferma.</summary>
public sealed record TunnelSection(TunnelViewModel State, ToolGroupViewModel Host);

/// <summary>
/// La card di un repository dentro la colonna di un ambiente: sezione git, errori e, per backend
/// e frontend, il dev tunnel; per il backend anche migrazioni e context, per il frontend
/// l'anteprima del browser di Claude.
/// </summary>
public sealed class RepoCardViewModel : ObservableObject
{
    // Il trattino lungo (U+2014) segna il valore non ancora letto.
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

    /// <summary>Il primo del suo tipo nell'ambiente: tiene i nomi storici di console e tunnel.</summary>
    public bool IsPrimary => Location.IsPrimary;

    /// <summary>L'id accanto al nome della cartella, solo per i repository non predefiniti.</summary>
    public string IdTagVisibility => IsPrimary ? "Collapsed" : "Visible";

    /// <summary>Il gruppo dei tag della card c'e' se c'e' il suo unico tag, l'id.</summary>
    public string TagsVisibility => IdTagVisibility;

    /// <summary>Il nome del repository sul server: lo usano «Apri in» e il Reset.</summary>
    public string RepositoryName => ActiveConfiguration.RepositoryNameOf(Location.Settings);

    public string Clone => Location.FolderName;

    public string Path => Location.Path;

    public BranchPickerViewModel BranchPicker { get; } = new();

    /// <summary>Tendina e dettaglio del repository; lo stato resta quello scelto anche ai refresh.</summary>
    public Expander Expander { get; } = new(isExpanded: true);

    /// <summary>Server e database letti da appsettings: la card dell'ambiente li mostra come tag.</summary>
    public string Server { get => server; set => SetProperty(ref server, value); }

    public string Database { get => database; set => SetProperty(ref database, value); }

    /// <summary>Apri nell'editor, avvia e ferma: accanto al nome del repository.</summary>
    public ToolGroupViewModel? Tool { get; set; }

    /// <summary>Sezione del dev tunnel dentro il corpo della card: sulle card del backend e del frontend.</summary>
    public TunnelSection? Tunnel { get; set; }

    private bool tunnelShared;

    /// <summary>Il repository ha una porta del pool: solo allora la card mostra la sezione del tunnel.</summary>
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

    /// <summary>Sezione di anteprima del browser di Claude dentro il corpo della card: solo sulla card del frontend.</summary>
    public FrontendPreviewViewModel? Preview { get; set; }

    public string PreviewVisibility => Preview is null ? "Collapsed" : "Visible";

    /// <summary>Sezioni delle migrazioni sconosciute e dei context in fondo al corpo della card: solo sulla card del backend.</summary>
    public BackendViewModel? Migrations { get; set; }

    public string MigrationsVisibility => Migrations is null ? "Collapsed" : "Visible";

    /// <summary>Ultime righe del container della card: solo sulle card di backend e frontend di un ambiente Docker.</summary>
    public LogSectionViewModel? ContainerLog { get; set; }

    public string ContainerLogVisibility => ContainerLog is null ? "Collapsed" : "Visible";

    public string RepoLine { get => repoLine; set => SetProperty(ref repoLine, value); }

    public string RepoDetail { get => repoDetail; set => SetProperty(ref repoDetail, value); }

    /// <summary>"nessun upstream configurato" o vuoto: in rosso sulla riga del dettaglio, letta anche dal gattino.</summary>
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

    /// <summary>"aggiornato HH:mm:ss", oppure cosa si sta leggendo: mai vuoto, per non muovere il layout.</summary>
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

    /// <summary>Le scritte di lettura in corso finiscono coi puntini ("lettura…", "git fetch…"): fa pulsare il pallino.</summary>
    public bool IsLoading => repoStamp.EndsWith('…');

    /// <summary>Un'azione lunga della card in corso (container, porta del pool): vedi <see cref="IsBusyVisual"/>.</summary>
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

    /// <summary>Lettura o azione in corso: lo sfondo della card scorre a righe (<see cref="CardBusy"/>).</summary>
    public bool IsBusyVisual => IsLoading || isWorking;

    /// <summary>Fetch, pull, pulizia e tendina: visibili dall'inizio, nascosti solo se git non e' disponibile.</summary>
    public string GitActionsVisibility { get => gitActionsVisibility; set => SetProperty(ref gitActionsVisibility, value); }

    public string PullToolTip { get => pullToolTip; set => SetProperty(ref pullToolTip, value); }

    public string MissingVisibility { get => missingVisibility; private set => SetProperty(ref missingVisibility, value); }

    public string ContentVisibility { get => contentVisibility; private set => SetProperty(ref contentVisibility, value); }

    public string ErrorLine { get => errorLine; set => SetProperty(ref errorLine, value); }

    public string ErrorVisibility { get => errorVisibility; set => SetProperty(ref errorVisibility, value); }

    /// <summary>False mentre un'azione di scrittura e' in corso su un repo dello stesso ambiente.</summary>
    public bool IsNotBusy { get => isNotBusy; set => SetProperty(ref isNotBusy, value); }

    public ICommand? FetchCommand { get => fetchCommand; set => SetProperty(ref fetchCommand, value); }

    public ICommand? PullCommand { get => pullCommand; set => SetProperty(ref pullCommand, value); }

    public ICommand? PruneCommand { get => pruneCommand; set => SetProperty(ref pruneCommand, value); }

    /// <summary>Ricontrolla la cartella: una card mancante non avvia letture e lo dice al posto del contenuto.</summary>
    public bool RefreshExistence()
    {
        var exists = Location.Exists();
        Preview?.Reload(Location);

        MissingVisibility = exists ? "Collapsed" : "Visible";
        ContentVisibility = exists ? "Visible" : "Collapsed";

        if (!exists)
        {
            RepoLine = $"{Clone} · cartella non trovata";
            RepoDetail = Path;
            UpstreamWarning = string.Empty;
        }

        return exists;
    }
}

using System.Windows.Input;
using Soffritto.Core.Configuration;
using Soffritto.Desktop.Infrastructure;
using Soffritto.Presentation;

namespace Soffritto.Desktop.ViewModels;

/// <summary>Una colonna della finestra: l'ambiente N con le card dei suoi repository.</summary>
public sealed class EnvironmentViewModel : ObservableObject
{
    private bool isCollapsed;
    private readonly Action beginOperation;
    private readonly Action endOperation;
    private readonly EnvironmentNote? note;
    private string noteText;
    private string activityLine = "avvio…";
    private bool isNotBusy = true;
    private bool isLoading;
    private bool allOnDev;
    private int activeOperations;
    private readonly RepoCardViewModel? backendCard;

    /// <param name="demoNote">Solo per la demo: la nota parte da questo testo e resta in memoria, senza file.</param>
    public EnvironmentViewModel(EnvironmentLayout layout, Action beginOperation, Action endOperation, string? demoNote = null)
    {
        this.beginOperation = beginOperation;
        this.endOperation = endOperation;

        Number = layout.Number;
        SchemaLock = layout.SchemaLock;
        Title = $"AMBIENTE {layout.Number}";
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
                    OnPropertyChanged(nameof(IsDatabaseHighlighted));
                    OnPropertyChanged(nameof(IsServerHighlighted));
                    OnPropertyChanged(nameof(DatabaseTagToolTip));
                    OnPropertyChanged(nameof(ServerTagToolTip));
                }
            };
        }
        isCollapsed = demoNote is null && AppSettings.Current.CollapsedEnvironments.Contains(Number);
        ToggleCollapseCommand = new RelayCommand(_ => IsCollapsed = !IsCollapsed);
        // La demo non cerca uno slot nella configurazione vera: il suo numero puo' coincidere con un ambiente vero.
        Docker = layout.Kind != EnvironmentKind.Docker ? null
            : demoNote is null ? CreateDocker(layout.Number)
            : new DockerEnvironmentViewModel(null, null);
        if (Docker is not null)
        {
            OperationLog = demoNote is null ? OperationLogs.For(Number) : new LogSectionViewModel("Operazioni");
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

    /// <summary>La parte Docker della colonna; null per un ambiente locale.</summary>
    public DockerEnvironmentViewModel? Docker { get; }

    public bool IsDocker => Docker is not null;

    public string KindBadgeVisibility => IsDocker ? "Visible" : "Collapsed";

    /// <summary>Il motivo per cui lo schema del database non si tocca (database protetto); null se si puo'.</summary>
    public string? SchemaLock { get; }

    public string ProtectedDatabaseVisibility => SchemaLock is null ? "Collapsed" : "Visible";

    public string ProtectedDatabaseTag => StatusText.ProtectedDatabaseTag;

    public string ProtectedDatabaseToolTip => SchemaLock is null ? string.Empty : StatusText.ProtectedDatabaseToolTip;

    /// <summary>Il database del backend predefinito dell'ambiente, letto dagli appsettings.</summary>
    public string DatabaseTag => backendCard?.Database ?? string.Empty;

    public string DatabaseTagVisibility => backendCard is null ? "Collapsed" : "Visible";

    public string ServerTag => backendCard?.Server ?? string.Empty;

    /// <summary>In un ambiente Docker il server e' sempre il container dello slot: il tag non dice niente.</summary>
    public string ServerTagVisibility => backendCard is null || IsDocker ? "Collapsed" : "Visible";

    /// <summary>Ogni tag e' evidenziato per conto suo, se il suo nome e' tra quelli che la configurazione del backend chiede di evidenziare.</summary>
    public bool IsDatabaseHighlighted => IsHighlighted(DatabaseTag);

    public bool IsServerHighlighted => IsHighlighted(ServerTag);

    public string DatabaseTagToolTip => ToolTipOf("Database", DatabaseTag);

    public string ServerTagToolTip => ToolTipOf("Server", ServerTag);

    private bool IsHighlighted(string tag) => backendCard?.Location.Settings is BackendConfiguration backend
        && backend.HighlightedTags.Any(name => string.Equals(name.Trim(), tag, StringComparison.OrdinalIgnoreCase));

    private string ToolTipOf(string kind, string tag) => IsHighlighted(tag)
        ? $"{kind}: {tag} · evidenziato nel setup: da trattare con cautela"
        : $"{kind}: {tag}";

    /// <summary>Log delle operazioni dell'ambiente Docker (creazione, avvio, stop, ripara env, distruggi); null per un locale.</summary>
    public LogSectionViewModel? OperationLog { get; }

    /// <summary>
    /// Riga di attivita' di un'azione: la mostra nell'intestazione e, negli ambienti Docker, la
    /// aggiunge al log delle operazioni. Da qualunque thread.
    /// </summary>
    public void Report(string line)
    {
        OperationLog?.Append(line);
        UiThread.Run(() => ActivityLine = line);
    }

    // Uno slot non calcolabile non toglie la colonna: la guardia del database rifiutera' ogni azione.
    private static DockerEnvironmentViewModel CreateDocker(int number)
    {
        var configuration = ActiveConfiguration.Current;
        try
        {
            return configuration.Environment(number) is { } environment
                ? new DockerEnvironmentViewModel(Soffritto.Core.Docker.DockerSlot.Of(configuration, environment), null)
                : new DockerEnvironmentViewModel(null, $"slot non calcolabile: l'ambiente {number} non e' in configurazione");
        }
        catch (Exception ex)
        {
            return new DockerEnvironmentViewModel(null, $"slot non calcolabile: {ex.Message}");
        }
    }

    /// <summary>Nota libera dell'ambiente, la stessa che mostra la VSIX: si salva mentre si scrive.</summary>
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


    public string SunsetVisibility => AllOnDev ? "Visible" : "Collapsed";

    /// <summary>Salva la nota ancora in attesa e smette di seguire il file.</summary>
    public void CloseNote() => note?.Dispose();

    private void ApplyExternalNote(string text)
    {
        if (SetProperty(ref noteText, text, nameof(Note)))
        {
            OnPropertyChanged(nameof(NotePlaceholderVisibility));
        }
    }

    /// <summary>Le card dei repository dell'ambiente, nell'ordine del catalogo.</summary>
    public IReadOnlyList<RepoCardViewModel> Repos { get; }

    /// <summary>La card predefinita di quel tipo (il primo repository del tipo); null se l'ambiente non ne ha.</summary>
    public RepoCardViewModel? Card(RepositoryType type) => Repos.FirstOrDefault(card => card.Type == type && card.IsPrimary);

    /// <summary>Le anteprime del browser di Claude, una per card del frontend.</summary>
    public IReadOnlyList<FrontendPreviewViewModel> Previews { get; }

    /// <summary>La colonna e' nascosta (si riapre dal chip nella testata): si ricorda tra un avvio e l'altro.</summary>
    public bool IsCollapsed
    {
        get => isCollapsed;
        set
        {
            if (SetProperty(ref isCollapsed, value))
            {
                // La demo ha gli stessi numeri degli ambienti veri: le sue colonne non si ricordano.
                if (note is not null)
                {
                    AppSettings.Current.SetCollapsed(Number, value);
                }

                OnPropertyChanged(nameof(ColumnVisibility));
                CollapsedChanged?.Invoke();
            }
        }
    }

    public string ColumnVisibility => isCollapsed ? "Collapsed" : "Visible";


    public ICommand ToggleCollapseCommand { get; }

    /// <summary>Dopo ogni apertura o chiusura della colonna: la larghezza minima della griglia cambia.</summary>
    public event Action? CollapsedChanged;

    // Ogni card porta gli strumenti e le sezioni del suo tipo; il generico apre VS Code e non avvia niente.
    private static RepoCardViewModel CreateCard(int number, RepoLocation location)
    {
        var card = new RepoCardViewModel(location);
        card.Tool = location.Settings is BackendConfiguration backend
            ? new ToolGroupViewModel("VS", $"Apre {backend.Solution} in Visual Studio")
            : new ToolGroupViewModel("VSC", "Apre la cartella in VS Code") { HasToggle = location.Type != RepositoryType.Generic };

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
                location.IsPrimary ? $"Ambiente {number}" : $"Ambiente {number} · {location.Id}",
                location);
        }

        return card;
    }

    /// <summary>Tutti i repository presenti nell'ambiente sono su dev: lo sfondo delle card della colonna scende al 70% di opacita'.</summary>
    public bool AllOnDev
    {
        get => allOnDev;
        private set
        {
            if (SetProperty(ref allOnDev, value))
            {
                OnPropertyChanged(nameof(SunsetVisibility));
            }
        }
    }

    /// <summary>Dopo ogni lettura dei branch di un repository dell'ambiente: il pannello PR ricalcola quali work item ci stanno sopra.</summary>
    public event Action? BranchesChanged;

    // I cloni mancanti non contano: un ambiente senza app resta "su dev" se backend e frontend lo sono.
    public void RefreshAllOnDev()
    {
        var present = Repos.Where(repo => repo.ContentVisibility == "Visible").ToList();
        AllOnDev = present.Count > 0 && present.All(repo => repo.BranchPicker.CurrentBranch == Soffritto.Core.GitConventions.DevelopmentBranch);
        ChipBranches = present
            .Select(repo => new ChipBranch(repo.IsPrimary ? repo.RepositoryName : repo.Clone, repo.BranchPicker.CurrentBranch ?? "—"))
            .ToList();
        OnPropertyChanged(nameof(ChipBranches));
        BranchesChanged?.Invoke();
    }

    /// <summary>Il branch di ogni repository presente, per la scheda del chip della testata.</summary>
    public IReadOnlyList<ChipBranch> ChipBranches { get; private set; } = [];

    /// <summary>
    /// L'ambiente e' esattamente dove lo porterebbe «Apri in» con questi branch: ogni repository
    /// indicato ha il clone sul suo branch, gli altri cloni presenti, generici compresi, sono su
    /// <see cref="Soffritto.Core.GitConventions.DevelopmentBranch"/>.
    /// </summary>
    public bool IsOnBranches(IReadOnlyList<(string Repository, string Branch)> targets)
    {
        if (targets.Count == 0)
        {
            return false;
        }

        foreach (var repo in Repos)
        {
            var requested = targets
                .Where(t => string.Equals(t.Repository, repo.RepositoryName, StringComparison.OrdinalIgnoreCase))
                .Select(t => t.Branch)
                .FirstOrDefault();
            if (repo.ContentVisibility != "Visible")
            {
                if (requested is not null)
                {
                    return false;
                }

                continue;
            }

            if (!string.Equals(repo.BranchPicker.CurrentBranch, requested ?? Soffritto.Core.GitConventions.DevelopmentBranch, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    public ToolBarViewModel Tools { get; } = new();


    /// <summary>
    /// Progresso di un'azione, titolo del suo fallimento, o "pronto". Una riga per ambiente:
    /// le azioni di scrittura dei tre repo passano dallo stesso gate.
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
    /// La riga di stato dell'intestazione: <see cref="ActivityLine"/>; in un ambiente Docker, a
    /// riposo, in avvio o con un container in errore, la fase dei container.
    /// </summary>
    public string StatusLine => Docker is { PhaseText.Length: > 0 } docker
        && (docker.IsWarmingUp || docker.HasFailure || activityLine == StatusText.IdleActivity)
            ? docker.PhaseText
            : activityLine;

    /// <summary>
    /// Pallino dell'intestazione a riposo: rosso con un container in errore, verde se l'ultima
    /// azione e' finita bene ("pronto") e l'ambiente Docker non e' spento, grigio altrimenti.
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

    /// <summary>Un'azione d'ambiente in corso o i container in avvio: la card dell'ambiente scorre a righe.</summary>
    public bool IsHeaderBusy => isLoading || Docker?.IsWarmingUp == true;

    public ICommand? RefreshCommand { get; set; }

    /// <summary>Spegne tutto e, se i repository sono puliti, li riporta su dev: vedi <see cref="EnvironmentController.CheckoutAsync"/>.</summary>
    public ICommand? ResetCommand { get; set; }

    private bool tunnelOn;
    private bool tunnelBusy;

    /// <summary>L'ambiente ha il tunnel: almeno una porta del pool assegnata a un suo repository.</summary>
    public bool TunnelOn => tunnelOn;

    /// <summary>
    /// Lo stato letto dalle assegnazioni. Notifica sempre, cosi' la casella torna com'era quando
    /// il clic non ha cambiato niente (pool pieno, errore); durante un cambio resta ferma.
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

    /// <summary>Vero mentre il tunnel si accende o si spegne: l'interruttore resta fermo.</summary>
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
        ? "L'ambiente ha un tunnel del pool: gli URL sono nelle card. Togli la spunta per rilasciare le porte e restituire il tunnel"
        : "Reclama un tunnel libero del pool, da' una porta a ogni repository che ne espone una (accesso anonimo) e accende l'host";

    /// <summary>Accende o spegne il tunnel dell'ambiente: vedi <see cref="Soffritto.Presentation.EnvironmentTunnel"/>.</summary>
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

/// <summary>Un repository nella scheda del chip dell'ambiente: nome e branch corrente.</summary>
public sealed record ChipBranch(string Repository, string Branch);

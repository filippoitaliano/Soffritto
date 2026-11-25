using System.Runtime.Serialization;
using Soffritto.Presentation;
using Microsoft.VisualStudio.Extensibility.UI;

namespace Soffritto.Vsix.UI;

// Le proprieta' *Visibility sono stringhe ("Visible"/"Collapsed") invece di bool: il dialetto
// XAML del Remote UI non espone converter, e il type converter di Visibility accetta la stringa.
// Stessa tecnica per le proprieta' *Color, lette da Border.Background via il converter di Brush.

// Pallini di stato (tavolozza in StatusBadge di Presentation) e icone sono disegnati in XAML,
// pallini come Ellipse e icone come glifi Segoe Fluent Icons, invece di usare <vs:Image>: quel
// controllo delega a CrispImage, che quando eredita CrispImage.HighContrast=true dall'albero
// che ospita il Remote UI serve la variante monocromatica del moniker. I glifi seguono il colore
// del testo del tema, come nell'exe Desktop.

/// <summary>
/// La pastiglia a destra di una riga delle liste, come la Note della Lane dell'exe: iniziale
/// maiuscola, rossa per cio' che e' rotto e grigia per il resto, in tinte che reggono chiaro e scuro.
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

    /// <summary>Etichetta breve a fianco dell'ID.</summary>
    [DataMember]
    public string Kind { get; } = kind;
}

[DataContract]
internal sealed class RecentMigrationRow(string displayName, string state, int position = 0, string name = "") : NotifyPropertyChangedObject
{
    /// <summary>Posizione nel context, nel badge grigio della riga come nell'exe; vuota se non nota.</summary>
    [DataMember]
    public string Position { get; } = position > 0 ? position.ToString() : string.Empty;

    [DataMember]
    public string BadgeVisibility => Position.Length > 0 ? "Visible" : "Collapsed";

    /// <summary>Nome senza timestamp; senza posizione, il nome da mostrare per intero.</summary>
    [DataMember]
    public string Name { get; } = name.Length > 0 ? name : displayName;

    /// <summary>La pastiglia della riga: solo cio' che non e' «applicata» (mancante, fuori ordine, feedback della copia).</summary>
    [DataMember]
    public string Note => LaneNote.Of(kind, hidden: MigrationLabels.Applied);

    [DataMember]
    public string NoteColor => LaneNote.ColorOf(kind, error: kind == MigrationLabels.Missing);

    [DataMember]
    public string NoteVisibility => Note.Length > 0 ? "Visible" : "Collapsed";

    [DataMember]
    public string RunToolTip { get; set; } = string.Empty;

    /// <summary>Glifo Segoe Fluent Icons del lancio: Play in avanti, Undo per un rollback.</summary>
    [DataMember]
    public string RunGlyph { get; set; } = string.Empty;

    /// <summary>Falso se il database e' gia' fermo a questa migrazione.</summary>
    [DataMember]
    public bool CanRun { get; set; }

    /// <summary>False mentre un'azione di scrittura e' in corso: la copia resta libera, il lancio no.</summary>
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

    /// <summary>Nome della migrazione senza il timestamp: quello che si legge a colpo d'occhio.</summary>
    [DataMember]
    public string DisplayName { get; } = displayName;

    /// <summary>Stato della migrazione, o il feedback transitorio della copia.</summary>
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

    /// <summary>Etichetta da ripristinare quando il feedback della copia scade.</summary>
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

    /// <summary>L'host senza schema, come nell'exe: gli URL pubblici dei tunnel sono https.</summary>
    [DataMember]
    public string Host { get; } = url.Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase);

    /// <summary>La pastiglia: «nessuna risposta» e il feedback della copia; «raggiungibile» no.</summary>
    [DataMember]
    public string Note => LaneNote.Of(kind, hidden: "raggiungibile");

    [DataMember]
    public string NoteColor => LaneNote.ColorOf(kind, error: kind == StatusText.CopyFailed);

    [DataMember]
    public string NoteVisibility => Note.Length > 0 ? "Visible" : "Collapsed";

    /// <summary>Porta e URL come si leggono nella riga: "5000 · https://…".</summary>
    [DataMember]
    public string Endpoint { get; } = endpoint;

    /// <summary>
    /// Esito del probe HTTP ("raggiungibile", "nessuna risposta", vuota se non eseguito) o il
    /// feedback transitorio della copia.
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

    /// <summary>URL completo che l'icona mette negli appunti, senza slash finale.</summary>
    internal string Url { get; } = url;

    /// <summary>Etichetta da ripristinare quando il feedback della copia scade.</summary>
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
    /// <summary>ID del tunnel e sua descrizione: "jolly-river-lx35cgb.euw · 2".</summary>
    [DataMember]
    public string Title { get; } = title;

    /// <summary>Stato sintetico: "host connesso · 2 porte".</summary>
    [DataMember]
    public string Kind { get; } = kind;

    [DataMember]
    public string StatusColor { get; } = statusColor;
}

/// <summary>
/// Apertura e chiusura di una card: l'intestazione resta, il corpo si nasconde. Stesso
/// comportamento dell'exe Desktop (ViewModels/Expander.cs), sui tipi del Remote UI.
/// </summary>
[DataContract]
internal sealed class ExpanderState : NotifyPropertyChangedObject
{
    // Glifi Segoe Fluent Icons: freccia giu' (aperta) e a destra (chiusa).
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

        // Un context allineato non ha niente da dire oltre al contatore: parte chiuso. Le righe
        // si ricreano a ogni lettura, quindi lo stato si riallinea a ogni refresh.
        Expander = new ExpanderState(isExpanded: !isUpToDate);

        // Sfogliare e' un'operazione locale sulle righe gia' costruite: nessuna rilettura,
        // quindi i comandi restano qui invece di arrivare dalla finestra come le azioni vere.
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

    /// <summary>Note, ultime migrazioni e pager: visibili solo a card aperta.</summary>
    [DataMember]
    public ExpanderState Expander { get; }

    /// <summary>
    /// Ora dell'ultima lettura ("aggiornato 10:32:15"), "lettura…" mentre si rilegge, o l'azione
    /// in corso sulle migrazioni.
    /// </summary>
    [DataMember]
    public string Stamp { get => action ?? stamp; set => SetProperty(ref stamp, value); }

    /// <summary>Azione in corso sulle migrazioni ("dotnet ef database update…"); null a fine azione.</summary>
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

    /// <summary>False mentre un'azione di scrittura e' in corso: blocca tutti i bottoni di update.</summary>
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

    /// <summary>Pallino dopo il nome: verde allineato, arancio con migrazioni da sistemare.</summary>
    [DataMember]
    public string StatusColor { get; }

    /// <summary>La pagina corrente delle migrazioni del context, dalla piu' nuova.</summary>
    [DataMember]
    public ObservableList<RecentMigrationRow> Recent { get; } = [];

    [DataMember]
    public string RecentVisibility { get; set; } = "Collapsed";

    /// <summary>Il pager compare solo quando le migrazioni non stanno in una pagina.</summary>
    [DataMember]
    public string PagerVisibility { get; set; } = "Collapsed";

    /// <summary>Posizione della pagina nel totale: "1-8 di 47".</summary>
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

    /// <summary>Visibile solo quando le pendenti sono tutte successive all'ultima applicata.</summary>
    [DataMember]
    public string ApplyVisibility { get; set; } = "Collapsed";

    [DataMember]
    public string ApplyToolTip { get; set; } = string.Empty;

    [DataMember]
    public AsyncCommand? ApplyCommand { get; set; }

    /// <summary>
    /// Tutte le migrazioni del context, gia' ordinate dalla piu' recente: <see cref="Recent"/>
    /// ne mostra una pagina alla volta e riparte dalla prima.
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
    private string activityLine = "avvio…";
    private string splashVisibility = "Visible";
    private string mainVisibility = "Collapsed";
    private string clone = "\u2014";
    private string branch = "\u2014";
    private string server = "\u2014";
    private string database = "\u2014";
    private string repoLine = string.Empty;
    private string repoDetail = "in attesa della prima lettura";
    private string upstreamWarning = string.Empty;
    private string upstreamWarningVisibility = "Collapsed";
    private string repoToolTip = string.Empty;
    private string repoStamp = "non ancora letto";
    private string tunnelStamp = "non letto";
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
    /// False solo mentre un'azione di scrittura (pull, database update) e' in corso. Le letture
    /// delle sezioni non lo toccano: una lettura lenta del tunnel o del fetch non deve impedire
    /// ne' le azioni ne' un nuovo refresh.
    /// </summary>
    [DataMember]
    public bool IsNotBusy { get => isNotBusy; set => SetProperty(ref isNotBusy, value); }

    // Finche' la prima lettura vera non parte (soluzione risolta), al posto delle sezioni vuote
    // c'e' lo splash col logo: intestazione a trattini e sezioni "non ancora lette" sembrerebbero
    // un pannello rotto. Le due visibilita' sono complementari.
    [DataMember]
    public string SplashVisibility { get => splashVisibility; set => SetProperty(ref splashVisibility, value); }

    [DataMember]
    public string MainVisibility { get => mainVisibility; set => SetProperty(ref mainVisibility, value); }

    /// <summary>
    /// Cosa sta facendo un'azione (progresso di pull, switch o database update), il titolo del
    /// suo fallimento, o <see cref="StatusText.IdleActivity"/>. Sempre una riga: le righe di stato che
    /// compaiono e scompaiono farebbero scattare tutto il pannello.
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

    /// <summary>Pallino accanto al nome dell'ambiente, come nell'exe: verde se pronto, grigio altrimenti.</summary>
    [DataMember]
    public string EnvironmentStatusColor => activityLine == StatusText.IdleActivity ? StatusBadge.OkColor : StatusBadge.OffColor;

    private string environmentTitle = string.Empty;
    private string note = string.Empty;
    private string notePlaceholder = string.Empty;
    private string noteVisibility = "Collapsed";

    /// <summary>"AMBIENTE 1", come la testata di colonna dell'exe; vuoto finche' non si conosce il clone.</summary>
    [DataMember]
    public string EnvironmentTitle { get => environmentTitle; private set => SetProperty(ref environmentTitle, value); }

    /// <summary>
    /// Nota libera dell'ambiente, la stessa che mostra l'exe. Il setter arriva dalla casella
    /// (binding TwoWay); quello che arriva dal file passa da <see cref="ShowNote"/>.
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

    /// <summary>"aggiungi una nota all'ambiente 1…": dice a quale ambiente va la nota.</summary>
    [DataMember]
    public string NotePlaceholder { get => notePlaceholder; private set => SetProperty(ref notePlaceholder, value); }

    [DataMember]
    public string NotePlaceholderVisibility => note.Length == 0 ? "Visible" : "Collapsed";

    /// <summary>La casella compare quando si conosce il clone, quindi l'ambiente.</summary>
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

    /// <summary>Tag rosso «DB protetto» dell'intestazione, come nell'exe: le migrazioni si leggono ma non si applicano.</summary>
    [DataMember]
    public string ProtectedDatabaseVisibility { get => protectedDatabaseVisibility; private set => SetProperty(ref protectedDatabaseVisibility, value); }

    [DataMember]
    public string ProtectedDatabaseTag => StatusText.ProtectedDatabaseTag;

    [DataMember]
    public string ProtectedDatabaseToolTip { get => protectedDatabaseToolTip; private set => SetProperty(ref protectedDatabaseToolTip, value); }

    /// <summary>Tag viola «Docker»: la soluzione aperta e' il worktree di un ambiente Docker.</summary>
    [DataMember]
    public string DockerVisibility { get => dockerVisibility; private set => SetProperty(ref dockerVisibility, value); }

    /// <summary>In un ambiente Docker il server e' sempre il container dello slot: il tag non dice niente.</summary>
    [DataMember]
    public string ServerVisibility { get => serverVisibility; private set => SetProperty(ref serverVisibility, value); }

    /// <summary>L'interruttore Tunnel compare quando la soluzione aperta e' il backend di un ambiente della configurazione.</summary>
    [DataMember]
    public string TunnelSwitchVisibility { get => tunnelSwitchVisibility; private set => SetProperty(ref tunnelSwitchVisibility, value); }

    /// <summary>L'ambiente ha porte del pool: lo stato letto dalle assegnazioni, non il clic.</summary>
    [DataMember]
    public bool TunnelOn { get => tunnelOn; set => SetProperty(ref tunnelOn, value); }

    /// <summary>Falso mentre il tunnel si accende o si spegne: l'interruttore resta fermo.</summary>
    [DataMember]
    public bool TunnelSwitchEnabled { get => tunnelSwitchEnabled; set => SetProperty(ref tunnelSwitchEnabled, value); }

    [DataMember]
    public string TunnelSwitchToolTip => tunnelOn
        ? "L'ambiente ha un tunnel del pool: gli URL sono nella card del tunnel. Spegni per rilasciare le porte"
        : "Reclama un tunnel libero del pool, da' una porta a ogni repository che ne espone una e accende l'host";

    [DataMember]
    public AsyncCommand TunnelSwitchCommand { get; set; } = null!;

    /// <summary>L'ambiente della soluzione aperta secondo la configurazione: tag dell'intestazione e interruttore.</summary>
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
        EnvironmentTitle = $"AMBIENTE {environment}";
        NotePlaceholder = $"aggiungi una nota all'ambiente {environment}…";
        NoteVisibility = "Visible";

        if (SetProperty(ref note, text, nameof(Note)))
        {
            RaiseNotifyPropertyChangedEvent(nameof(NotePlaceholderVisibility));
        }
    }

    // Intestazione a colpo d'occhio. Il default e' un trattino lungo (U+2014), scritto come
    // escape per non dipendere dall'encoding del file.
    /// <summary>Nome della cartella di lavoro del clone (es. backend-1).</summary>
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

    /// <summary>"aggiornato HH:mm:ss", oppure cosa si sta leggendo: mai vuoto, per non muovere il layout.</summary>
    [DataMember]
    public string RepoStamp { get => repoStamp; set => SetProperty(ref repoStamp, value); }

    [DataMember]
    public string TunnelLine { get => tunnelLine; set => SetProperty(ref tunnelLine, value); }

    [DataMember]
    public string TunnelDetail { get => tunnelDetail; set => SetProperty(ref tunnelDetail, value); }

    /// <summary>Il dettaglio serve solo quando il tunnel non e' letto o e' in errore: a tunnel risolto parlano badge e porte.</summary>
    [DataMember]
    public string TunnelDetailVisibility { get => tunnelDetailVisibility; set => SetProperty(ref tunnelDetailVisibility, value); }

    [DataMember]
    public string TunnelPortsVisibility { get => tunnelPortsVisibility; set => SetProperty(ref tunnelPortsVisibility, value); }

    [DataMember]
    public ObservableList<TunnelPortRow> TunnelPorts { get; } = [];

    /// <summary>I tunnel dell'account, mostrati solo quando nessuno corrisponde al clone.</summary>
    [DataMember]
    public ObservableList<TunnelSummaryRow> TunnelList { get; } = [];

    [DataMember]
    public string TunnelListVisibility { get => tunnelListVisibility; set => SetProperty(ref tunnelListVisibility, value); }

    /// <summary>"aggiornato HH:mm:ss", "lettura…" o "non letto": mai vuoto, per non muovere il layout.</summary>
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

    /// <summary>Fetch, pull e tendina dei branch: visibili dall'inizio, nascosti solo se git non e' disponibile.</summary>
    [DataMember]
    public string GitActionsVisibility { get => gitActionsVisibility; set => SetProperty(ref gitActionsVisibility, value); }

    [DataMember]
    public string PullToolTip { get => pullToolTip; set => SetProperty(ref pullToolTip, value); }

    /// <summary>Le voci della tendina: il branch corrente in testa, poi quelli che passano il filtro, in ordine.</summary>
    [DataMember]
    public ObservableList<string> Branches { get; } = [];

    /// <summary>
    /// Testo nella casella della ComboBox editabile. Mentre si scrive filtra le voci e apre il
    /// menu; quando WPF lo riscrive con la voce scelta (o col corrente) non e' una ricerca.
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

            // Se la selezione e' andata persa (testo svuotato dal controllo), col filtro vuoto
            // si torna sul corrente; mentre si scrive la selezione non va toccata, altrimenti
            // WPF riscriverebbe la casella con il nome del branch selezionato.
            SyncBranches(reselect: text.Length == 0 && selectedBranch is null);

            if (text.Length > 0)
            {
                BranchDropDownOpen = true;
            }
        }
    }

    /// <summary>
    /// Aperto dal ViewModel appena si scrive, chiuso dall'utente o da una scelta. Se si chiude
    /// (Esc, click fuori, Tab) mentre nella casella c'e' ancora un testo di ricerca, la ricerca
    /// e' abbandonata: la casella torna a mostrare il branch corrente.
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

    /// <summary>Vero mentre almeno una lettura, un'azione o l'attesa iniziale e' in corso: anima la barra in fondo.</summary>
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

    /// <summary>Branch corrente secondo l'ultima lettura: una selezione diversa da questo e' una richiesta di switch.</summary>
    internal string? CurrentBranch { get; private set; }

    /// <summary>
    /// Nuova lettura di git: fissa il corrente e l'elenco completo, poi riallinea la tendina
    /// sul corrente. <paramref name="current"/> null significa git non disponibile: tendina vuota.
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

    /// <summary>Chiamato dal setter di <see cref="SelectedBranch"/> quando l'utente sceglie un branch diverso dal corrente.</summary>
    internal Action<string>? BranchSelected { get; set; }

    // Binding TwoWay dalla ComboBox: il setter arriva sia dalle scelte dell'utente sia dalle
    // riassegnazioni del presenter (e con null se la voce selezionata sparisce). Solo una scelta
    // diversa dal branch corrente e' un'intenzione di cambiare. Una scelta chiude comunque la
    // ricerca: l'eco del testo che WPF manda subito dopo trova gia' il filtro vuoto.
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

    // Le card restano come le lascia l'utente anche ai refresh: partono aperte.
    [DataMember]
    public ExpanderState RepoExpander { get; } = new(isExpanded: true);

    [DataMember]
    public ExpanderState TunnelExpander { get; } = new(isExpanded: true);

    [DataMember]
    public ExpanderState UnknownExpander { get; } = new(isExpanded: true);

    /// <summary>Versione dell'estensione per il footer: "v1.0.3".</summary>
    [DataMember]
    public string AppVersion { get; } =
        $"v{typeof(StatusViewModel).Assembly.GetName().Version?.ToString(3) ?? "?"}";

    // Kestrel (o il container dell'api in un ambiente Docker) ha pallino e avvia/ferma nella card
    // del repository; l'host del tunnel ha solo il pallino, nella card del tunnel. Avvia e ferma
    // sono lo stesso bottone: il glifo dice cosa fara'.
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

    /// <summary>Vero se il bottone di Kestrel ferma invece di avviare: allora e' rosso, come nell'exe.</summary>
    [DataMember]
    public bool KestrelCanStop { get => kestrelCanStop; private set => SetProperty(ref kestrelCanStop, value); }

    /// <summary>Applica lo stato di Kestrel e dell'host del tunnel calcolato da BackendTools (ambiente locale).</summary>
    internal void ApplyTools(BackendToolsView view)
    {
        KestrelColor = ConsoleServices.ColorOf(view.Kestrel);
        KestrelToolTip = $"Kestrel: {ConsoleServices.Describe(view.Kestrel)}";
        KestrelCanStop = view.Kestrel != ServiceState.Stopped;
        KestrelToggleGlyph = KestrelCanStop ? StopGlyph : PlayGlyph;
        KestrelToggleEnabled = true;
        KestrelToggleToolTip = KestrelCanStop
            ? "Ferma Kestrel avviato dalla console (chiude la console e i processi figli)"
            : "Avvia Kestrel (dotnet run) in una console";

        // L'host del tunnel del pool si accende e si spegne con l'interruttore Tunnel dell'ambiente.
        TunnelHostColor = ConsoleServices.ColorOf(view.Tunnel);
        TunnelHostToolTip = view.TunnelToolTip;
    }

    /// <summary>
    /// Nel worktree di un ambiente Docker il bottone di Kestrel governa il container dell'api:
    /// <paramref name="state"/> null se Docker non risponde (<paramref name="error"/>).
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
            Soffritto.Core.Docker.ContainerRunState.Running => $"container {service}: acceso",
            Soffritto.Core.Docker.ContainerRunState.Starting => $"container {service}: in avvio",
            null => $"container {service}: {error}",
            _ => $"container {service}: spento",
        };
        KestrelCanStop = state is Soffritto.Core.Docker.ContainerRunState.Running or Soffritto.Core.Docker.ContainerRunState.Starting;
        KestrelToggleGlyph = KestrelCanStop ? StopGlyph : PlayGlyph;
        KestrelToggleEnabled = state is not null;
        KestrelToggleToolTip = KestrelCanStop
            ? $"Ferma il container {service} (docker compose stop)"
            : $"Avvia il container {service} (docker compose up --build)";
    }
}

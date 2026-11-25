using System.Collections.ObjectModel;
using System.Windows.Input;
using Soffritto.Desktop.Infrastructure;
using Soffritto.Presentation;

namespace Soffritto.Desktop.ViewModels;

public sealed class MigrationRowViewModel(string migrationId, string kind)
{
    public string MigrationId { get; } = migrationId;

    public string Kind { get; } = kind;
}

public sealed class RecentMigrationRowViewModel(int position, string name, string state) : ObservableObject
{
    private bool isNotBusy = true;

    /// <summary>Posizione nel context (1 = la prima migrazione), nel badge della lane.</summary>
    public string Position { get; } = position.ToString();

    /// <summary>Nome della migrazione senza il timestamp.</summary>
    public string Name { get; } = name;

    /// <summary>Il tag della lane: nessuno per l'applicata, rosso per la mancante, grigio per le altre anomalie.</summary>
    public string Note { get; } = state == MigrationLabels.Applied ? string.Empty : LaneTags.Capitalize(state);

    public string NoteColor { get; } = state == MigrationLabels.Missing ? LaneTags.Error : LaneTags.Neutral;

    public string RunToolTip { get; init; } = string.Empty;

    public string RunGlyph { get; init; } = string.Empty;

    public bool CanRun { get; init; }

    /// <summary>False mentre un'azione di scrittura e' in corso sul backend.</summary>
    public bool IsNotBusy
    {
        get => isNotBusy;
        set
        {
            if (SetProperty(ref isNotBusy, value))
            {
                OnPropertyChanged(nameof(RunEnabled));
            }
        }
    }

    public bool RunEnabled => CanRun && isNotBusy;

    /// <summary>Lancia dotnet ef database update fino a questa migrazione, anche all'indietro.</summary>
    public ICommand? RunCommand { get; init; }
}

public sealed class ContextRowViewModel : ObservableObject
{
    private string stamp = string.Empty;
    private string? action;
    private bool isNotBusy = true;
    private string pageLabel = string.Empty;
    private bool canGoPrevious;
    private bool canGoNext;
    private IReadOnlyList<RecentMigrationRowViewModel> allRecent = [];
    private int page;

    private readonly bool isUpToDate;

    public ContextRowViewModel(string name, bool isUpToDate, string statusColor)
    {
        Name = name;
        StatusColor = statusColor;
        this.isUpToDate = isUpToDate;

        // Un context allineato non ha niente da dire: parte chiuso. Le righe
        // si ricreano a ogni lettura, quindi lo stato si riallinea a ogni refresh.
        Expander = new Expander(isExpanded: !isUpToDate);

        // Sfogliare e' un'operazione locale sulle righe gia' costruite: nessuna rilettura.
        PreviousPageCommand = new AsyncCommand(() =>
        {
            ShowPage(page - 1);
            return Task.CompletedTask;
        });

        NextPageCommand = new AsyncCommand(() =>
        {
            ShowPage(page + 1);
            return Task.CompletedTask;
        });
    }

    public string Name { get; }

    public string StatusColor { get; }

    public int MissingCount { get; init; }

    public int OutOfOrderCount { get; init; }

    /// <summary>Lo stato del context in parole, per il tooltip del pallino.</summary>
    public string StatusToolTip
    {
        get
        {
            if (isUpToDate)
            {
                return "Migrazioni allineate";
            }

            var parts = new List<string>();
            if (MissingCount > 0)
            {
                parts.Add(MissingCount == 1 ? "1 migrazione mancante" : $"{MissingCount} migrazioni mancanti");
            }

            if (OutOfOrderCount > 0)
            {
                parts.Add(OutOfOrderCount == 1 ? "1 migrazione fuori ordine" : $"{OutOfOrderCount} migrazioni fuori ordine");
            }

            return parts.Count > 0 ? string.Join(" · ", parts) : Note.Length > 0 ? Note : "Migrazioni da controllare";
        }
    }

    /// <summary>Note, ultime migrazioni e pager: visibili solo a card aperta.</summary>
    public Expander Expander { get; }

    /// <summary>Ora dell'ultima lettura, "lettura…" mentre si rilegge, o l'azione in corso sulle migrazioni.</summary>
    public string Stamp
    {
        get => action ?? stamp;
        set
        {
            if (SetProperty(ref stamp, value))
            {
                OnPropertyChanged(nameof(IsLoading));
            }
        }
    }

    public bool IsLoading => Stamp.EndsWith('…');

    /// <summary>Azione in corso sulle migrazioni del backend; null a fine azione.</summary>
    public string? Action
    {
        get => action;
        set
        {
            if (SetProperty(ref action, value))
            {
                OnPropertyChanged(nameof(Stamp));
                OnPropertyChanged(nameof(IsLoading));
            }
        }
    }

    /// <summary>False mentre un'azione di scrittura e' in corso sul backend: blocca tutti i bottoni di update.</summary>
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

    /// <summary>La pagina corrente delle migrazioni del context, dalla piu' nuova.</summary>
    public ObservableCollection<RecentMigrationRowViewModel> Recent { get; } = [];

    public string RecentVisibility { get; private set; } = "Collapsed";

    public string PagerVisibility { get; private set; } = "Collapsed";

    public string PageLabel { get => pageLabel; private set => SetProperty(ref pageLabel, value); }

    public bool CanGoPrevious { get => canGoPrevious; private set => SetProperty(ref canGoPrevious, value); }

    public bool CanGoNext { get => canGoNext; private set => SetProperty(ref canGoNext, value); }

    public ICommand PreviousPageCommand { get; }

    public ICommand NextPageCommand { get; }

    public string Note { get; init; } = string.Empty;

    public string NoteVisibility => Note.Length > 0 ? "Visible" : "Collapsed";

    /// <summary>Tutte le migrazioni del context, dalla piu' recente: se ne mostra una pagina alla volta.</summary>
    public void SetRecent(IReadOnlyList<RecentMigrationRowViewModel> ordered)
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

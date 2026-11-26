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

    /// <summary>Position in the context (1 = the first migration), shown in the lane badge.</summary>
    public string Position { get; } = position.ToString();

    /// <summary>Migration name without the timestamp.</summary>
    public string Name { get; } = name;

    /// <summary>The lane tag: none for an applied migration, red for a missing one, grey for any other anomaly.</summary>
    public string Note { get; } = state == MigrationLabels.Applied ? string.Empty : LaneTags.Capitalize(state);

    public string NoteColor { get; } = state == MigrationLabels.Missing ? LaneTags.Error : LaneTags.Neutral;

    public string RunToolTip { get; init; } = string.Empty;

    public string RunGlyph { get; init; } = string.Empty;

    public bool CanRun { get; init; }

    /// <summary>False while a write action is running on the backend.</summary>
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

    /// <summary>Runs dotnet ef database update up to this migration, backwards too.</summary>
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

        // An up-to-date context has nothing to say, so it starts collapsed. Rows are
        // rebuilt on every read, so the state realigns on every refresh.
        Expander = new Expander(isExpanded: !isUpToDate);

        // Paging is local to the rows already built: nothing is read again.
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

    /// <summary>The context status in words, for the status dot tooltip.</summary>
    public string StatusToolTip
    {
        get
        {
            if (isUpToDate)
            {
                return "Migrations up to date";
            }

            var parts = new List<string>();
            if (MissingCount > 0)
            {
                parts.Add(MissingCount == 1 ? "1 missing migration" : $"{MissingCount} missing migrations");
            }

            if (OutOfOrderCount > 0)
            {
                parts.Add(OutOfOrderCount == 1 ? "1 migration out of order" : $"{OutOfOrderCount} migrations out of order");
            }

            return parts.Count > 0 ? string.Join(" · ", parts) : Note.Length > 0 ? Note : "Migrations need checking";
        }
    }

    /// <summary>Note, latest migrations and pager: visible only while the card is expanded.</summary>
    public Expander Expander { get; }

    /// <summary>Time of the last read, "reading…" while reading again, or the migration action in progress.</summary>
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

    /// <summary>Migration action in progress on the backend; null once it ends.</summary>
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

    /// <summary>False while a write action is running on the backend: disables every update button.</summary>
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

    /// <summary>The current page of the context's migrations, newest first.</summary>
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

    /// <summary>All the context's migrations, newest first: one page is shown at a time.</summary>
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

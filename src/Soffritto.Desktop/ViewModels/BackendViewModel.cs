using System.Collections.ObjectModel;
using Soffritto.Desktop.Infrastructure;
using Soffritto.Presentation;

namespace Soffritto.Desktop.ViewModels;

/// <summary>
/// The backend sections at the bottom of its card: unknown migrations and contexts. The backend's
/// dev tunnel lives in <see cref="RepoCardViewModel.Tunnel"/>.
/// </summary>
public sealed class BackendViewModel : ObservableObject
{
    public BackendViewModel()
    {
        Contexts.CollectionChanged += (_, _) => OnPropertyChanged(nameof(DatabaseVisibility));
    }

    private string unknownHeader = string.Empty;
    private string unknownVisibility = "Collapsed";
    private bool isNotBusy = true;
    private string? migrationsAction;

    public Expander UnknownExpander { get; } = new(isExpanded: true);

    public ObservableCollection<ContextRowViewModel> Contexts { get; } = [];

    public ObservableCollection<MigrationRowViewModel> Unknown { get; } = [];

    public string UnknownHeader { get => unknownHeader; set => SetProperty(ref unknownHeader, value); }

    public string UnknownVisibility
    {
        get => unknownVisibility;
        set
        {
            if (SetProperty(ref unknownVisibility, value))
            {
                OnPropertyChanged(nameof(DatabaseVisibility));
            }
        }
    }

    /// <summary>The Database card is shown when it has at least one section: a context or the unknown migrations.</summary>
    public string DatabaseVisibility => Contexts.Count > 0 || unknownVisibility == "Visible" ? "Visible" : "Collapsed";

    /// <summary>Follows the backend's gate; contexts, including those recreated by a read, receive it from here.</summary>
    public bool IsNotBusy
    {
        get => isNotBusy;
        set
        {
            if (!SetProperty(ref isNotBusy, value))
            {
                return;
            }

            foreach (var row in Contexts)
            {
                row.IsNotBusy = value;
            }
        }
    }

    /// <summary>Action running on the migrations ("dotnet ef database update…") or null.</summary>
    public string? MigrationsAction
    {
        get => migrationsAction;
        set
        {
            if (!SetProperty(ref migrationsAction, value))
            {
                return;
            }

            foreach (var row in Contexts)
            {
                row.Action = value;
            }
        }
    }
}

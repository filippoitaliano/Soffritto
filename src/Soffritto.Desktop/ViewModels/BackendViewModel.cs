using System.Collections.ObjectModel;
using Soffritto.Desktop.Infrastructure;
using Soffritto.Presentation;

namespace Soffritto.Desktop.ViewModels;

/// <summary>
/// Le sezioni del backend in fondo alla sua card: migrazioni sconosciute e context. Il dev tunnel
/// del backend sta in <see cref="RepoCardViewModel.Tunnel"/>.
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

    /// <summary>La card Database c'e' quando ha almeno una sezione: un context o le migrazioni sconosciute.</summary>
    public string DatabaseVisibility => Contexts.Count > 0 || unknownVisibility == "Visible" ? "Visible" : "Collapsed";

    /// <summary>Segue il gate del backend; i context, anche quelli ricreati da una lettura, lo ricevono da qui.</summary>
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

    /// <summary>Azione in corso sulle migrazioni ("dotnet ef database update…") o null.</summary>
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

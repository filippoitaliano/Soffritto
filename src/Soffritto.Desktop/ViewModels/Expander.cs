using System.Windows.Input;
using Soffritto.Desktop.Infrastructure;

namespace Soffritto.Desktop.ViewModels;

/// <summary>Apertura e chiusura di una card: l'intestazione resta, il corpo si nasconde.</summary>
public sealed class Expander : ObservableObject
{
    // Glifi Segoe Fluent Icons: freccia giu' (aperta) e a destra (chiusa).
    private const string ExpandedGlyph = "";
    private const string CollapsedGlyph = "";

    private bool isExpanded;

    public Expander(bool isExpanded)
    {
        this.isExpanded = isExpanded;

        ToggleCommand = new AsyncCommand(() =>
        {
            IsExpanded = !IsExpanded;
            return Task.CompletedTask;
        });
    }

    public ICommand ToggleCommand { get; }

    public bool IsExpanded
    {
        get => isExpanded;
        set
        {
            if (SetProperty(ref isExpanded, value))
            {
                OnPropertyChanged(nameof(BodyVisibility));
                OnPropertyChanged(nameof(Glyph));
            }
        }
    }

    public string BodyVisibility => isExpanded ? "Visible" : "Collapsed";

    public string Glyph => isExpanded ? ExpandedGlyph : CollapsedGlyph;
}

using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Media;
using Soffritto.Desktop.Infrastructure;

namespace Soffritto.Desktop.ViewModels;

/// <summary>Un'anteprima del pannello «Sfondi» del setup: un clic mette o toglie la scena dalla rotazione.</summary>
public sealed class SceneryChoiceViewModel : ObservableObject
{
    // Griglia dell'anteprima in pixel del paesaggio: come una finestra di 1200x700 DIP, abbastanza
    // larga perche' la centrale e il lungomare mostrino palco, tavoli e strada.
    private const int PreviewCols = 300;
    private const int PreviewRows = 175;

    private readonly AppSettings settings;
    private ImageSource? preview;

    private SceneryChoiceViewModel(AppSettings settings, int index)
    {
        this.settings = settings;
        Index = index;
        Key = PixelScenery.Scenes[index].Key;
        Label = PixelScenery.Scenes[index].Label;
        ToggleCommand = new RelayCommand(_ => settings.SetSceneryIncluded(Key, !IsSelected));
        settings.PropertyChanged += OnSettingsChanged;
    }

    public static IReadOnlyList<SceneryChoiceViewModel> All { get; } =
        [.. Enumerable.Range(0, PixelScenery.Scenes.Count).Select(i => new SceneryChoiceViewModel(AppSettings.Current, i))];

    public int Index { get; }

    public string Key { get; }

    public string Label { get; }

    // Disegnata alla prima richiesta del binding, cioe' quando la scheda Preferenze si apre.
    public ImageSource Preview => preview ??= PixelScenery.RenderPreview(Index, PreviewCols, PreviewRows);

    public bool IsSelected => settings.Sceneries.Contains(Key);

    public bool IsOnlySelected => IsSelected && settings.Sceneries.Count == 1;

    public string ToolTip => IsOnlySelected
        ? "È l'unico sfondo in rotazione: per non avere sfondi spegni l'interruttore «Sfondo»"
        : IsSelected ? "In rotazione · clic per toglierlo" : "Fuori rotazione · clic per aggiungerlo";

    public ICommand ToggleCommand { get; }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.Sceneries))
        {
            OnPropertyChanged(nameof(IsSelected));
            OnPropertyChanged(nameof(IsOnlySelected));
            OnPropertyChanged(nameof(ToolTip));
        }
    }
}

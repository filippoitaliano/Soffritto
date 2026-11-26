using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Media;
using Soffritto.Desktop.Infrastructure;

namespace Soffritto.Desktop.ViewModels;

/// <summary>A preview in the setup's “Backgrounds” panel: a click adds the scene to the rotation or removes it.</summary>
public sealed class SceneryChoiceViewModel : ObservableObject
{
    // Preview grid in landscape pixels: like a 1200x700 DIP window, wide enough for the power
    // station and the seafront to show stage, tables and street.
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

    // Drawn on the binding's first request, that is when the Preferences tab opens.
    public ImageSource Preview => preview ??= PixelScenery.RenderPreview(Index, PreviewCols, PreviewRows);

    public bool IsSelected => settings.Sceneries.Contains(Key);

    public bool IsOnlySelected => IsSelected && settings.Sceneries.Count == 1;

    public string ToolTip => IsOnlySelected
        ? "It's the only background in rotation: to have no background, turn off the “Background” switch"
        : IsSelected ? "In rotation · click to remove" : "Not in rotation · click to add";

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

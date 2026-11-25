using System.Windows;
using System.Windows.Controls;

namespace Soffritto.Desktop;

/// <summary>
/// Elenco delle card (porte, migrazioni, tunnel; stile in App.xaml): un solo riquadro con il bordo
/// delle lane, le righe dentro separate da un filo sottile. Con un elemento solo e' identico a una
/// lane. Gli elementi sono di solito <see cref="Lane"/>, la riga senza bordo.
/// </summary>
/// <remarks>
/// Il filo sta in cima a ogni riga tranne la prima: la riga lo sa da AlternationIndex, che WPF
/// tiene aggiornato quando le voci cambiano (pagine delle migrazioni, porte che compaiono).
/// </remarks>
public sealed class LaneGrid : ItemsControl
{
    public LaneGrid()
    {
        AlternationCount = int.MaxValue;
    }

    protected override DependencyObject GetContainerForItemOverride() => new LaneGridRow();

    protected override bool IsItemItsOwnContainerOverride(object item) => item is LaneGridRow;
}

/// <summary>Contenitore di una riga di <see cref="LaneGrid"/>: il filo di separazione e la riga.</summary>
public sealed class LaneGridRow : ContentControl
{
}

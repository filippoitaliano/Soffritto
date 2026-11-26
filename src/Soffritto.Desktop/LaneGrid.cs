using System.Windows;
using System.Windows.Controls;

namespace Soffritto.Desktop;

/// <summary>
/// List inside the cards (ports, migrations, tunnels; style in App.xaml): a single box with the lane
/// border, the rows inside separated by a thin line. With a single item it looks just like a
/// lane. The items are usually <see cref="Lane"/>, the borderless row.
/// </summary>
/// <remarks>
/// The line sits at the top of every row except the first: the row knows from AlternationIndex, which
/// WPF keeps up to date when the items change (migration pages, ports appearing).
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

/// <summary>Container of a <see cref="LaneGrid"/> row: the separator line and the row.</summary>
public sealed class LaneGridRow : ContentControl
{
}

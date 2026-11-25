using System.Windows;
using System.Windows.Controls;
using Soffritto.Desktop.ViewModels;

namespace Soffritto.Desktop;

/// <summary>
/// Le colonne degli ambienti affiancate, da sinistra, larghe <see cref="MainViewModel.ColumnWidth"/>
/// ciascuna; una colonna nascosta non occupa spazio. Lo spazio che avanza resta vuoto a destra.
/// </summary>
public sealed class ColumnsPanel : Panel
{
    public static readonly DependencyProperty IsCollapsedProperty = DependencyProperty.RegisterAttached(
        "IsCollapsed",
        typeof(bool),
        typeof(ColumnsPanel),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsParentMeasure | FrameworkPropertyMetadataOptions.AffectsParentArrange));

    public static bool GetIsCollapsed(DependencyObject element) => (bool)element.GetValue(IsCollapsedProperty);

    public static void SetIsCollapsed(DependencyObject element, bool value) => element.SetValue(IsCollapsedProperty, value);

    protected override Size MeasureOverride(Size availableSize)
    {
        var height = 0.0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(WidthOf(child), availableSize.Height));
            height = Math.Max(height, child.DesiredSize.Height);
        }

        return new Size(InternalChildren.Cast<UIElement>().Sum(WidthOf), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var x = 0.0;
        foreach (UIElement child in InternalChildren)
        {
            var width = WidthOf(child);
            child.Arrange(new Rect(x, 0, width, finalSize.Height));
            x += width;
        }

        return finalSize;
    }

    private static double WidthOf(UIElement child) => GetIsCollapsed(child) ? 0 : MainViewModel.ColumnWidth;
}

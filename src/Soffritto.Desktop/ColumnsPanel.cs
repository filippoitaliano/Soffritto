using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using Soffritto.Desktop.ViewModels;

namespace Soffritto.Desktop;

/// <summary>
/// The environment columns side by side, from the left, each <see cref="MainViewModel.ColumnWidth"/>
/// wide; a hidden column takes no space. Leftover space stays empty on the right. Hiding or
/// showing a column animates its slot: the column fades while its space closes or opens, and the
/// columns on its right slide over. The panel owns the columns' Visibility: a hidden column is
/// collapsed only once its slot has closed.
/// </summary>
public sealed class ColumnsPanel : Panel
{
    public static readonly DependencyProperty IsCollapsedProperty = DependencyProperty.RegisterAttached(
        "IsCollapsed",
        typeof(bool),
        typeof(ColumnsPanel),
        new FrameworkPropertyMetadata(false, OnIsCollapsedChanged));

    // How open the column's slot is, from 0 (hidden) to 1.
    private static readonly DependencyProperty OpennessProperty = DependencyProperty.RegisterAttached(
        "Openness",
        typeof(double),
        typeof(ColumnsPanel),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsParentMeasure | FrameworkPropertyMetadataOptions.AffectsParentArrange));

    private static readonly Duration SlideDuration = TimeSpan.FromMilliseconds(200);

    public static bool GetIsCollapsed(DependencyObject element) => (bool)element.GetValue(IsCollapsedProperty);

    public static void SetIsCollapsed(DependencyObject element, bool value) => element.SetValue(IsCollapsedProperty, value);

    protected override Size MeasureOverride(Size availableSize)
    {
        var height = 0.0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(MainViewModel.ColumnWidth, availableSize.Height));
            height = Math.Max(height, child.DesiredSize.Height);
        }

        return new Size(InternalChildren.Cast<UIElement>().Sum(SlotWidth), height);
    }

    // Each column is laid out at its full width, so its content does not reflow while the slot
    // animates; the columns on its right start where its slot ends and are drawn above it.
    protected override Size ArrangeOverride(Size finalSize)
    {
        var x = 0.0;
        foreach (UIElement child in InternalChildren)
        {
            child.Arrange(new Rect(x, 0, MainViewModel.ColumnWidth, finalSize.Height));
            x += SlotWidth(child);
        }

        return finalSize;
    }

    private static double SlotWidth(UIElement child) =>
        child.Visibility == Visibility.Collapsed ? 0 : (double)child.GetValue(OpennessProperty) * MainViewModel.ColumnWidth;

    private static void OnIsCollapsedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement column)
        {
            return;
        }

        var collapsed = (bool)e.NewValue;

        // Columns created hidden (at startup) take their state without animation.
        if (!column.IsLoaded)
        {
            column.BeginAnimation(OpennessProperty, null);
            column.BeginAnimation(OpacityProperty, null);
            column.SetValue(OpennessProperty, collapsed ? 0.0 : 1.0);
            column.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var wasHidden = column.Visibility == Visibility.Collapsed;
        if (!collapsed)
        {
            column.Visibility = Visibility.Visible;
        }

        var slide = new DoubleAnimation(collapsed ? 0 : 1, SlideDuration) { EasingFunction = ease };
        if (collapsed)
        {
            slide.Completed += (_, _) =>
            {
                if (GetIsCollapsed(column))
                {
                    column.Visibility = Visibility.Collapsed;
                }
            };
        }

        column.BeginAnimation(OpennessProperty, slide);
        var fade = new DoubleAnimation(collapsed ? 0 : 1, SlideDuration) { EasingFunction = ease };
        if (wasHidden)
        {
            fade.From = 0;
        }

        column.BeginAnimation(OpacityProperty, fade);
    }
}

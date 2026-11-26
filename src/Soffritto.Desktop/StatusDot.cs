using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Soffritto.Desktop;

/// <summary>
/// The status dot of the cards (style in App.xaml): the resource colour at rest, pulsing light blue
/// while it is being read. The status in words is in its tooltip.
/// </summary>
public sealed class StatusDot : Control
{
    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(
        nameof(Color), typeof(Brush), typeof(StatusDot), new PropertyMetadata(Brushes.Transparent));

    public static readonly DependencyProperty IsLoadingProperty = DependencyProperty.Register(
        nameof(IsLoading), typeof(bool), typeof(StatusDot), new PropertyMetadata(false));

    public static readonly DependencyProperty DotSizeProperty = DependencyProperty.Register(
        nameof(DotSize), typeof(double), typeof(StatusDot), new PropertyMetadata(10.0));

    public Brush Color { get => (Brush)GetValue(ColorProperty); set => SetValue(ColorProperty, value); }

    public bool IsLoading { get => (bool)GetValue(IsLoadingProperty); set => SetValue(IsLoadingProperty, value); }

    /// <summary>Diameter of the circle; the control stays as large as the mouse-sensitive area.</summary>
    public double DotSize { get => (double)GetValue(DotSizeProperty); set => SetValue(DotSizeProperty, value); }
}

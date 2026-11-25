using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Soffritto.Desktop;

/// <summary>
/// Il pallino di stato delle card (stile in App.xaml): il colore della risorsa a riposo, azzurro
/// pulsante mentre la si legge. Lo stato a parole sta nel suo tooltip.
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

    /// <summary>Diametro del cerchio; il controllo resta grande quanto l'area sensibile al mouse.</summary>
    public double DotSize { get => (double)GetValue(DotSizeProperty); set => SetValue(DotSizeProperty, value); }
}

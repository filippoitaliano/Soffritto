using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Soffritto.Desktop;

/// <summary>
/// La scrollbar a scomparsa (stile ScrollBar di App.xaml): il pollice si vede mentre si scorre,
/// per <see cref="Linger"/> dopo l'ultimo scorrimento, e finche' il mouse e' sopra la barra.
/// L'hover che blindtales.app non ha (in Chrome lo stile della barra non segue l'hover) qui
/// serve: in orizzontale la rotella non scorre, la barra si trascina e va trovata.
/// </summary>
public static class ScrollBarFade
{
    private static readonly TimeSpan Linger = TimeSpan.FromMilliseconds(900);
    private static readonly Duration FadeOut = TimeSpan.FromMilliseconds(200);

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(ScrollBarFade), new PropertyMetadata(false, OnIsEnabledChanged));

    private static readonly DependencyProperty TimerProperty = DependencyProperty.RegisterAttached(
        "Timer", typeof(DispatcherTimer), typeof(ScrollBarFade));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollBar bar || e.NewValue is not true)
        {
            return;
        }

        var timer = new DispatcherTimer { Interval = Linger };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Update(bar);
        };
        bar.SetValue(TimerProperty, timer);

        bar.ValueChanged += (_, _) =>
        {
            timer.Stop();
            timer.Start();
            Update(bar);
        };
        bar.MouseEnter += (_, _) => Update(bar);
        bar.MouseLeave += (_, _) => Update(bar);
    }

    // Compare subito, sparisce sfumando.
    private static void Update(ScrollBar bar)
    {
        if (bar.Track is not { } track)
        {
            return;
        }

        var visible = bar.IsMouseOver || ((DispatcherTimer)bar.GetValue(TimerProperty)).IsEnabled;
        track.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(visible ? 1 : 0, visible ? TimeSpan.Zero : FadeOut));
    }
}

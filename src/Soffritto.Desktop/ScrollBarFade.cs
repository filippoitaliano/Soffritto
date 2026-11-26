using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Soffritto.Desktop;

/// <summary>
/// The auto-hiding scrollbar (ScrollBar style in App.xaml): the thumb shows while scrolling,
/// for <see cref="Linger"/> after the last scroll, and while the mouse is over the bar.
/// The hover that blindtales.app lacks (in Chrome the bar style does not follow hover) is needed
/// here: horizontally the wheel does not scroll, so the bar is dragged and has to be found.
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

    // Appears immediately, fades out.
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

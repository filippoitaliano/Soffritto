using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Soffritto.Desktop;

// Busy card: the background turns into 45-degree stripes, slanted to the right, in two close
// shades that scroll, like the buttons of garden.filippoitaliano.com/atmosfere while a track is
// playing (20 px stripes, 200 px in 3.6 s), on the card cream and softer. The brush is new each time it turns on:
// the Section style's brush is frozen and cannot be animated. When it turns off, the style comes back.
public static class CardBusy
{
    public static readonly DependencyProperty IsBusyProperty = DependencyProperty.RegisterAttached(
        "IsBusy",
        typeof(bool),
        typeof(CardBusy),
        new PropertyMetadata(false, OnIsBusyChanged));

    // The gradient runs diagonally: a 40 px period along the diagonal gives 20 px stripes, and
    // horizontally the pattern repeats every 40·√2 px, which is the scroll of one cycle.
    private const double Period = 40;
    private static readonly double Step = Period / Math.Sqrt(2);
    private static readonly double Loop = Period * Math.Sqrt(2);

    public static bool GetIsBusy(DependencyObject element) => (bool)element.GetValue(IsBusyProperty);

    public static void SetIsBusy(DependencyObject element, bool value) => element.SetValue(IsBusyProperty, value);

    private static void OnIsBusyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Border border)
        {
            return;
        }

        if ((bool)e.NewValue)
        {
            // The shades come from the card theme (Theme.BusyLight/BusyWarm), day or night.
            border.Background = Stripes(
                border.TryFindResource("Theme.BusyLight") as Color? ?? Colors.White,
                border.TryFindResource("Theme.BusyWarm") as Color? ?? Colors.White);
        }
        else
        {
            border.ClearValue(Border.BackgroundProperty);
        }
    }

    private static LinearGradientBrush Stripes(Color light, Color warm)
    {
        var shift = new TranslateTransform();
        var brush = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Point(0, 0),
            EndPoint = new Point(Step, Step),
            SpreadMethod = GradientSpreadMethod.Repeat,
            Transform = shift,
        };
        brush.GradientStops.Add(new GradientStop(light, 0));
        brush.GradientStops.Add(new GradientStop(light, 0.5));
        brush.GradientStops.Add(new GradientStop(warm, 0.5));
        brush.GradientStops.Add(new GradientStop(warm, 1));
        shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, Loop, TimeSpan.FromSeconds(1.02)) { RepeatBehavior = RepeatBehavior.Forever });
        return brush;
    }
}

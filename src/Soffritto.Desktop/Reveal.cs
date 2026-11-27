using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace Soffritto.Desktop;

/// <summary>
/// Shows or hides its content with a short animation instead of a jump: the height opens from 0
/// (or closes to 0) while the content fades, so what sits below slides instead of jumping. Use
/// <see cref="Shown"/> in place of the content's Visibility binding. The first value is applied as
/// it is; once closed the element is collapsed, so hidden content takes no focus and no layout.
/// </summary>
public sealed class Reveal : Decorator
{
    public static readonly DependencyProperty ShownProperty = DependencyProperty.Register(
        nameof(Shown), typeof(Visibility), typeof(Reveal),
        new FrameworkPropertyMetadata(Visibility.Visible, OnShownChanged));

    private static readonly DependencyProperty OpennessProperty = DependencyProperty.Register(
        "Openness", typeof(double), typeof(Reveal),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    private static readonly Duration OpenDuration = TimeSpan.FromMilliseconds(180);
    private static readonly Duration CloseDuration = TimeSpan.FromMilliseconds(150);

    public Visibility Shown
    {
        get => (Visibility)GetValue(ShownProperty);
        set => SetValue(ShownProperty, value);
    }

    private double Openness => (double)GetValue(OpennessProperty);

    protected override Size MeasureOverride(Size constraint)
    {
        if (Child is null)
        {
            return default;
        }

        Child.Measure(new Size(constraint.Width, double.PositiveInfinity));
        var desired = Child.DesiredSize;
        return new Size(desired.Width, Math.Min(constraint.Height, desired.Height * Openness));
    }

    // The content keeps its full height and is clipped while the height animates, so it does not
    // squash or reflow.
    protected override Size ArrangeOverride(Size arrangeSize)
    {
        Child?.Arrange(new Rect(0, 0, arrangeSize.Width, Math.Max(arrangeSize.Height, Child.DesiredSize.Height)));
        ClipToBounds = Openness < 1;
        return arrangeSize;
    }

    private static void OnShownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var reveal = (Reveal)d;
        var shown = (Visibility)e.NewValue == Visibility.Visible;

        if (!reveal.IsLoaded)
        {
            reveal.BeginAnimation(OpennessProperty, null);
            reveal.BeginAnimation(OpacityProperty, null);
            reveal.SetValue(OpennessProperty, shown ? 1.0 : 0.0);
            reveal.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        var duration = shown ? OpenDuration : CloseDuration;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var wasHidden = reveal.Visibility == Visibility.Collapsed;
        if (shown)
        {
            reveal.Visibility = Visibility.Visible;
        }

        var height = new DoubleAnimation(shown ? 1 : 0, duration) { EasingFunction = ease };
        if (!shown)
        {
            height.Completed += (_, _) =>
            {
                if (reveal.Shown != Visibility.Visible)
                {
                    reveal.Visibility = Visibility.Collapsed;
                }
            };
        }

        var fade = new DoubleAnimation(shown ? 1 : 0, duration) { EasingFunction = ease };
        if (wasHidden)
        {
            fade.From = 0;
        }

        reveal.BeginAnimation(OpennessProperty, height);
        reveal.BeginAnimation(OpacityProperty, fade);
    }
}

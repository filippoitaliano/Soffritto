using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace Soffritto.Desktop;

/// <summary>
/// Takes the width its content asks for, but animated: when the content grows or shrinks (a
/// status icon appearing or disappearing in a chip) the width eases to the new value instead of
/// jumping. The content stays on the left at its full width and is clipped while the width catches up.
/// </summary>
public sealed class SmoothWidth : Decorator
{
    private static readonly DependencyProperty AnimatedWidthProperty = DependencyProperty.Register(
        nameof(AnimatedWidth), typeof(double), typeof(SmoothWidth),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsMeasure));

    private double target = double.NaN;

    public SmoothWidth()
    {
        ClipToBounds = true;
    }

    private double AnimatedWidth
    {
        get => (double)GetValue(AnimatedWidthProperty);
        set => SetValue(AnimatedWidthProperty, value);
    }

    protected override Size MeasureOverride(Size constraint)
    {
        if (Child is null)
        {
            return default;
        }

        Child.Measure(new Size(double.PositiveInfinity, constraint.Height));
        var desired = Child.DesiredSize;

        // The first width is taken as it is: only later changes are animated.
        if (double.IsNaN(target))
        {
            target = desired.Width;
            AnimatedWidth = desired.Width;
        }
        else if (Math.Abs(desired.Width - target) > 0.5)
        {
            target = desired.Width;
            BeginAnimation(AnimatedWidthProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
        }

        return new Size(Math.Min(AnimatedWidth, constraint.Width), desired.Height);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        Child?.Arrange(new Rect(0, 0, Math.Max(target, arrangeSize.Width), arrangeSize.Height));
        return arrangeSize;
    }
}

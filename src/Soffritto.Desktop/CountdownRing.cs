using System.Windows;
using System.Windows.Media;

namespace Soffritto.Desktop;

/// <summary>
/// The timer ring of the story points meeting: the filled part is <see cref="Ratio"/>, clockwise
/// from twelve o'clock. The content (the time) sits on top of it, in XAML.
/// </summary>
public sealed class CountdownRing : FrameworkElement
{
    public static readonly DependencyProperty RatioProperty = DependencyProperty.Register(
        nameof(Ratio), typeof(double), typeof(CountdownRing), new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(CountdownRing), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
        nameof(Track), typeof(Brush), typeof(CountdownRing), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(CountdownRing), new FrameworkPropertyMetadata(18.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Ratio { get => (double)GetValue(RatioProperty); set => SetValue(RatioProperty, value); }

    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    public Brush Track { get => (Brush)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }

    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        var radius = (size - Thickness) / 2;
        if (radius <= 0)
        {
            return;
        }

        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        drawingContext.DrawEllipse(null, new Pen(Track, Thickness), center, radius, radius);

        var ratio = Math.Clamp(Ratio, 0, 1);
        var pen = new Pen(Stroke, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (ratio >= 0.9999)
        {
            drawingContext.DrawEllipse(null, pen, center, radius, radius);
            return;
        }

        if (ratio <= 0)
        {
            return;
        }

        var angle = ratio * 2 * Math.PI;
        var start = new Point(center.X, center.Y - radius);
        var end = new Point(center.X + radius * Math.Sin(angle), center.Y - radius * Math.Cos(angle));

        var arc = new StreamGeometry();
        using (var context = arc.Open())
        {
            context.BeginFigure(start, isFilled: false, isClosed: false);
            context.ArcTo(end, new Size(radius, radius), 0, isLargeArc: ratio > 0.5, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: false);
        }

        arc.Freeze();
        drawingContext.DrawGeometry(null, pen, arc);
    }
}

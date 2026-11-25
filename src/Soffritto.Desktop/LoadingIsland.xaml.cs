using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Soffritto.Desktop;

public partial class LoadingIsland : UserControl
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(LoadingIsland), new PropertyMetadata("letture in corso"));

    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(LoadingIsland), new PropertyMetadata(false, (d, _) => ((LoadingIsland)d).OnIsActiveChanged()));

    public static readonly DependencyProperty FromBottomProperty = DependencyProperty.Register(
        nameof(FromBottom), typeof(bool), typeof(LoadingIsland), new PropertyMetadata(false));

    public static readonly DependencyProperty DodgesMouseProperty = DependencyProperty.Register(
        nameof(DodgesMouse), typeof(bool), typeof(LoadingIsland), new PropertyMetadata(false));

    private const double EntryOffset = 36;

    // Quanto il mouse puo' avvicinarsi all'isola prima che si sposti.
    private const double DodgeMargin = 14;

    private Window? window;
    private bool onRight;
    private bool moving;

    public LoadingIsland()
    {
        InitializeComponent();
        SetBinding(IsActiveProperty, new Binding("IsLoading"));
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += (_, _) => KeepSide();
    }

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    /// <summary>Legata a IsLoading del DataContext: accende e spegne l'isola.</summary>
    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    /// <summary>L'isola sta in basso: entra salendo e se ne va scendendo.</summary>
    public bool FromBottom
    {
        get => (bool)GetValue(FromBottomProperty);
        set => SetValue(FromBottomProperty, value);
    }

    /// <summary>
    /// Col mouse sopra l'isola scivola sul lato opposto del contenitore e torna indietro allo
    /// stesso modo: va allineata a sinistra, e il lato destro lo calcola dalla larghezza del padre.
    /// </summary>
    public bool DodgesMouse
    {
        get => (bool)GetValue(DodgesMouseProperty);
        set => SetValue(DodgesMouseProperty, value);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Parent is FrameworkElement parent)
        {
            parent.SizeChanged += (_, _) => KeepSide();
        }

        window = Window.GetWindow(this);
        if (window is not null)
        {
            window.PreviewMouseMove += OnWindowMouseMove;
        }

        if (IsActive)
        {
            OnIsActiveChanged();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (window is not null)
        {
            window.PreviewMouseMove -= OnWindowMouseMove;
            window = null;
        }
    }

    private void OnIsActiveChanged()
    {
        if (!IsLoaded)
        {
            return;
        }

        if (IsActive)
        {
            Show();
        }
        else
        {
            Hide();
        }
    }

    // Si allarga come una goccia: la larghezza arriva con un piccolo rimbalzo elastico,
    // l'altezza e la posizione con un'uscita morbida.
    private void Show()
    {
        Shell.Visibility = Visibility.Visible;
        StartLoops();

        var offset = FromBottom ? EntryOffset : -EntryOffset;
        Animate(Shell, OpacityProperty, 1, 0.3, new SineEase { EasingMode = EasingMode.EaseOut });
        Animate(Shift, TranslateTransform.YProperty, 0, 0.55, new CubicEase { EasingMode = EasingMode.EaseOut }, from: offset);
        Animate(Scale, ScaleTransform.ScaleXProperty, 1, 0.8, new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 1, Springiness = 5 }, from: 0.35);
        Animate(Scale, ScaleTransform.ScaleYProperty, 1, 0.5, new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 }, from: 0.55);
    }

    private void Hide()
    {
        var offset = FromBottom ? EntryOffset * 0.8 : -EntryOffset * 0.8;
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        Animate(Scale, ScaleTransform.ScaleXProperty, 0.4, 0.38, ease);
        Animate(Scale, ScaleTransform.ScaleYProperty, 0.6, 0.38, ease);
        Animate(Shift, TranslateTransform.YProperty, offset, 0.38, ease);

        var fade = new DoubleAnimation(0, Seconds(0.34)) { EasingFunction = ease };
        fade.Completed += (_, _) =>
        {
            if (!IsActive)
            {
                Shell.Visibility = Visibility.Collapsed;
                StopLoops();
            }
        };
        Shell.BeginAnimation(OpacityProperty, fade);
    }

    // I puntini salgono e scendono a onda, l'alone respira, il riflesso attraversa la pillola:
    // tutto su curve sinusoidali, cosi' niente scatta. Girano solo mentre l'isola si vede.
    private void StartLoops()
    {
        var dots = new[] { Dot1, Dot2, Dot3 };
        for (var i = 0; i < dots.Length; i++)
        {
            var begin = TimeSpan.FromSeconds(i * 0.16);
            var wave = new SineEase { EasingMode = EasingMode.EaseInOut };
            dots[i].RenderTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, -3.5, Seconds(0.45))
            {
                BeginTime = begin, AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = wave,
            });
            dots[i].BeginAnimation(OpacityProperty, new DoubleAnimation(0.5, 1, Seconds(0.45))
            {
                BeginTime = begin, AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = wave,
            });
        }

        Halo.BeginAnimation(OpacityProperty, new DoubleAnimation(0.15, 0.4, Seconds(1.6))
        {
            AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        });
        Sheen.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-1, 1, Seconds(2.4))
        {
            RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        });
    }

    private void StopLoops()
    {
        foreach (var dot in new[] { Dot1, Dot2, Dot3 })
        {
            dot.RenderTransform.BeginAnimation(TranslateTransform.YProperty, null);
            dot.BeginAnimation(OpacityProperty, null);
        }

        Halo.BeginAnimation(OpacityProperty, null);
        Sheen.BeginAnimation(TranslateTransform.XProperty, null);
    }

    private void OnWindowMouseMove(object sender, MouseEventArgs e)
    {
        if (!DodgesMouse || moving || !IsActive || Shell.Visibility != Visibility.Visible)
        {
            return;
        }

        var position = e.GetPosition(Shell);
        var zone = new Rect(-DodgeMargin, -DodgeMargin, Shell.ActualWidth + 2 * DodgeMargin, Shell.ActualHeight + 2 * DodgeMargin);
        if (zone.Contains(position))
        {
            SlideTo(!onRight);
        }
    }

    // Scivola con partenza e arrivo dolci; a meta' corsa si allunga e si appiattisce un poco,
    // come una goccia trascinata.
    private void SlideTo(bool right)
    {
        onRight = right;
        moving = true;

        var slide = new DoubleAnimation(SideOffset(right), Seconds(0.8)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } };
        slide.Completed += (_, _) => moving = false;
        Shift.BeginAnimation(TranslateTransform.XProperty, slide);

        Scale.BeginAnimation(ScaleTransform.ScaleXProperty, Stretch(1.1));
        Scale.BeginAnimation(ScaleTransform.ScaleYProperty, Stretch(0.9));
    }

    private static DoubleAnimationUsingKeyFrames Stretch(double peak) => new()
    {
        Duration = Seconds(0.8),
        KeyFrames =
        {
            new EasingDoubleKeyFrame(peak, KeyTime.FromPercent(0.45), new SineEase { EasingMode = EasingMode.EaseInOut }),
            new EasingDoubleKeyFrame(1, KeyTime.FromPercent(1), new SineEase { EasingMode = EasingMode.EaseInOut }),
        },
    };

    // A destra la distanza dal bordo resta quella che il margine da' a sinistra.
    private double SideOffset(bool right)
    {
        if (!right || Parent is not FrameworkElement parent)
        {
            return 0;
        }

        return Math.Max(0, parent.ActualWidth - ActualWidth - 2 * Margin.Left);
    }

    // Finestra ridimensionata o etichetta cambiata con l'isola a destra: si riallinea al bordo.
    private void KeepSide()
    {
        if (!onRight || moving)
        {
            return;
        }

        Shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(SideOffset(true), Seconds(0.2)));
    }

    private static void Animate(IAnimatable target, DependencyProperty property, double to, double seconds, IEasingFunction ease, double? from = null)
    {
        var animation = new DoubleAnimation(to, Seconds(seconds)) { EasingFunction = ease };
        if (from is { } start)
        {
            animation.From = start;
        }

        target.BeginAnimation(property, animation);
    }

    private static Duration Seconds(double seconds) => new(TimeSpan.FromSeconds(seconds));
}

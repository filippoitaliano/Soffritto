using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Soffritto.Desktop;

// Card al lavoro: lo sfondo diventa a righe a 45 gradi, inclinate verso destra, di due tinte
// vicine che scorrono, come i bottoni di garden.filippoitaliano.com/atmosfere con una traccia in
// riproduzione (righe da 20 px, 200 px in 3,6 s), sul crema delle card e piu' tenue. Il pennello e' nuovo a ogni accensione:
// quello dello stile Section e' congelato e non si anima. Allo spegnimento torna lo stile.
public static class CardBusy
{
    public static readonly DependencyProperty IsBusyProperty = DependencyProperty.RegisterAttached(
        "IsBusy",
        typeof(bool),
        typeof(CardBusy),
        new PropertyMetadata(false, OnIsBusyChanged));

    // Il gradiente corre in diagonale: 40 px di periodo lungo la diagonale danno righe da 20 px, e
    // in orizzontale il disegno si ripete ogni 40·√2 px, che e' lo scorrimento di un giro.
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
            // Le tinte vengono dal tema delle card (Theme.BusyLight/BusyWarm), giorno o notte.
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

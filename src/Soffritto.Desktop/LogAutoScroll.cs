using System.Windows;
using System.Windows.Controls;

namespace Soffritto.Desktop;

/// <summary>
/// Un TextBox di log che resta sull'ultima riga quando il testo cambia, se chi legge era gia' in
/// fondo: scorrendo in su per leggere, la posizione resta dov'e'.
/// </summary>
public static class LogAutoScroll
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(LogAutoScroll),
        new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box)
        {
            return;
        }

        box.TextChanged -= OnTextChanged;
        box.IsVisibleChanged -= OnVisibleChanged;
        if ((bool)e.NewValue)
        {
            box.TextChanged += OnTextChanged;
            box.IsVisibleChanged += OnVisibleChanged;
        }
    }

    private static void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        // La posizione si legge prima che il testo nuovo vada in layout; lo scorrimento dopo.
        var box = (TextBox)sender;
        var atEnd = box.VerticalOffset + box.ViewportHeight >= box.ExtentHeight - 24;
        if (atEnd || box.ExtentHeight <= box.ViewportHeight)
        {
            box.Dispatcher.BeginInvoke(box.ScrollToEnd, System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    // Alla prima apertura della sezione il log parte dall'ultima riga.
    private static void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            var box = (TextBox)sender;
            box.Dispatcher.BeginInvoke(box.ScrollToEnd, System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }
}

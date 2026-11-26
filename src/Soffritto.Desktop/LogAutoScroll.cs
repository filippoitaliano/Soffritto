using System.Windows;
using System.Windows.Controls;

namespace Soffritto.Desktop;

/// <summary>
/// A log TextBox that stays on the last line when the text changes, if the reader was already at
/// the bottom: after scrolling up to read, the position stays where it is.
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
        // The position is read before the new text goes through layout; the scroll happens after.
        var box = (TextBox)sender;
        var atEnd = box.VerticalOffset + box.ViewportHeight >= box.ExtentHeight - 24;
        if (atEnd || box.ExtentHeight <= box.ViewportHeight)
        {
            box.Dispatcher.BeginInvoke(box.ScrollToEnd, System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    // When the section is first opened, the log starts from the last line.
    private static void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            var box = (TextBox)sender;
            box.Dispatcher.BeginInvoke(box.ScrollToEnd, System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }
}

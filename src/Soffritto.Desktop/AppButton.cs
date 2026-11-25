using System.Windows;

namespace Soffritto.Desktop;

/// <summary>
/// L'etichetta dei bottoni della riga delle app nella testata (stile AppButton): il Content del
/// bottone e' il glifo, il colore della pillola sta nel Tag.
/// </summary>
public static class AppButton
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.RegisterAttached(
        "Label", typeof(string), typeof(AppButton), new PropertyMetadata(string.Empty));

    public static string GetLabel(DependencyObject element) => (string)element.GetValue(LabelProperty);

    public static void SetLabel(DependencyObject element, string value) => element.SetValue(LabelProperty, value);

    /// <summary>Il numero nel pallino rosso sull'icona; vuoto, niente pallino.</summary>
    public static readonly DependencyProperty BadgeProperty = DependencyProperty.RegisterAttached(
        "Badge", typeof(string), typeof(AppButton), new PropertyMetadata(string.Empty));

    public static string GetBadge(DependencyObject element) => (string)element.GetValue(BadgeProperty);

    public static void SetBadge(DependencyObject element, string value) => element.SetValue(BadgeProperty, value);
}

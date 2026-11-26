using System.Windows;

namespace Soffritto.Desktop;

/// <summary>
/// The label of the buttons in the header's app row (AppButton style): the button's Content is
/// the glyph, and the pill colour lives in the Tag.
/// </summary>
public static class AppButton
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.RegisterAttached(
        "Label", typeof(string), typeof(AppButton), new PropertyMetadata(string.Empty));

    public static string GetLabel(DependencyObject element) => (string)element.GetValue(LabelProperty);

    public static void SetLabel(DependencyObject element, string value) => element.SetValue(LabelProperty, value);

    /// <summary>The number in the blue badge on the icon; empty means no badge.</summary>
    public static readonly DependencyProperty BadgeProperty = DependencyProperty.RegisterAttached(
        "Badge", typeof(string), typeof(AppButton), new PropertyMetadata(string.Empty));

    public static string GetBadge(DependencyObject element) => (string)element.GetValue(BadgeProperty);

    public static void SetBadge(DependencyObject element, string value) => element.SetValue(BadgeProperty, value);
}

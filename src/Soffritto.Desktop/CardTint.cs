using System.Windows;

namespace Soffritto.Desktop;

// Property inherited down the visual tree: the column sets it once and the Section style
// reads it on every card, whatever the card's DataContext (repo, backend, preview).
public static class CardTint
{
    public static readonly DependencyProperty AllOnDevProperty = DependencyProperty.RegisterAttached(
        "AllOnDev",
        typeof(bool),
        typeof(CardTint),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetAllOnDev(DependencyObject element) => (bool)element.GetValue(AllOnDevProperty);

    public static void SetAllOnDev(DependencyObject element, bool value) => element.SetValue(AllOnDevProperty, value);
}

using System.Windows;

namespace Soffritto.Desktop;

// Proprieta' ereditata lungo l'albero visuale: la colonna la imposta una volta e lo stile
// Section la legge su ogni card, qualunque sia il DataContext della card (repo, backend, anteprima).
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

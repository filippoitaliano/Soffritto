using System.Windows;
using System.Windows.Input;
using Soffritto.Presentation;

namespace Soffritto.Desktop;

public partial class ConfirmDialog : Window
{
    private ConfirmDialog(Confirmation confirmation, string cancelText)
    {
        InitializeComponent();
        Title = confirmation.Title;
        TitleText.Text = confirmation.Title;
        DescriptionText.Text = confirmation.Description;
        ListsControl.ItemsSource = confirmation.Lists.Where(l => l.Rows.Count > 0).ToList();
        ConfirmButton.Content = confirmation.ConfirmText;
        CancelButton.Content = cancelText;
        Loaded += (_, _) => CancelButton.Focus();
    }

    /// <summary>
    /// Chiede conferma con una finestra modale sopra <paramref name="owner"/>. Vero solo con il
    /// clic sul bottone di conferma: Esc, Invio e la chiusura valgono come annulla.
    /// </summary>
    public static bool Ask(Window? owner, Confirmation confirmation, string cancelText = "Annulla")
    {
        var dialog = new ConfirmDialog(confirmation, cancelText);

        // Una finestra mai mostrata non puo' fare da proprietaria; nascosta o ridotta a icona non da' un centro.
        if (owner is { IsLoaded: true })
        {
            dialog.Owner = owner;
        }

        dialog.WindowStartupLocation = dialog.Owner is { IsVisible: true } visible && visible.WindowState != WindowState.Minimized
            ? WindowStartupLocation.CenterOwner
            : WindowStartupLocation.CenterScreen;

        return dialog.ShowDialog() == true;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();
}

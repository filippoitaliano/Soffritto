using System.Windows;
using System.Windows.Controls;

namespace Soffritto.Desktop;

public partial class NotificationsPanel : UserControl
{
    public NotificationsPanel()
    {
        InitializeComponent();
    }

    /// <summary>La X del pannello: chi lo ospita decide come nasconderlo.</summary>
    public event Action? CloseRequested;

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
}

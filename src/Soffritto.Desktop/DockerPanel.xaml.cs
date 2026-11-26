using System.Windows;
using System.Windows.Controls;

namespace Soffritto.Desktop;

public partial class DockerPanel : UserControl
{
    public DockerPanel()
    {
        InitializeComponent();
    }

    /// <summary>The panel's X: the host decides how to hide it.</summary>
    public event Action? CloseRequested;

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
}

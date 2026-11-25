using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Soffritto.Desktop.ViewModels;

namespace Soffritto.Desktop;

public partial class PullRequestsPanel : UserControl
{
    public PullRequestsPanel()
    {
        InitializeComponent();
    }

    /// <summary>La X del pannello: chi lo ospita decide come nasconderlo.</summary>
    public event Action? CloseRequested;

    /// <summary>«Cambia lato»: chi lo ospita decide dove spostarlo.</summary>
    public event Action? SwitchSideRequested;

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    private void SwitchSide_Click(object sender, RoutedEventArgs e) => SwitchSideRequested?.Invoke();

    /// <summary>Scorre il pannello fino alla card del gruppo, se c'e'. Va chiamata a layout fatto.</summary>
    public void Reveal(PullRequestGroup group)
    {
        var card = CreatedList.ItemContainerGenerator.ContainerFromItem(group) as FrameworkElement
            ?? ToReviewList.ItemContainerGenerator.ContainerFromItem(group) as FrameworkElement;
        card?.BringIntoView();
    }

    // Il menu degli ambienti si costruisce al clic: il ContextMenu non sta nell'albero visuale e
    // non erediterebbe il DataContext del gruppo.
    private void OpenIn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: PullRequestGroup group } button)
        {
            return;
        }

        var itemStyle = (Style)FindResource("CardMenuItem");
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom, Style = (Style)FindResource("CardMenu") };
        foreach (var target in group.Targets)
        {
            menu.Items.Add(new MenuItem { Header = target.Label, Command = target.Command, Style = itemStyle });
        }

        if (group.NewDockerTarget is { } docker)
        {
            if (menu.Items.Count > 0)
            {
                menu.Items.Add(new Separator { Style = (Style)FindResource("CardMenuSeparator") });
            }

            menu.Items.Add(new MenuItem { Header = docker.Label, Command = docker.Command, ToolTip = docker.ToolTip, Style = itemStyle });
        }

        menu.IsOpen = true;
    }
}

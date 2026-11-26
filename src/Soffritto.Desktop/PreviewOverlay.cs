using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Soffritto.Desktop.ViewModels;

namespace Soffritto.Desktop;

/// <summary>
/// An environment's preview enlarged to fill the app window, above the columns and the header:
/// it stays inside the window, it is not a system full screen. It shows the same Claude browser
/// as the card.
/// </summary>
public sealed class PreviewOverlay : Grid
{
    private readonly ContentControl host = new();
    private readonly TextBlock title = new()
    {
        Foreground = Brushes.White,
        FontWeight = FontWeights.Bold,
        FontSize = 14,
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private FrontendPreviewViewModel? preview;

    public PreviewOverlay()
    {
        Visibility = Visibility.Collapsed;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var exit = new Button
        {
            Content = "",
            Foreground = Brushes.White,
            ToolTip = "Back to the columns (Esc)",
        };
        exit.SetResourceReference(StyleProperty, "IconButton");
        exit.Click += (_, _) => Close();

        var bar = new DockPanel { Margin = new Thickness(12, 6, 12, 6), LastChildFill = true };
        DockPanel.SetDock(exit, Dock.Right);
        bar.Children.Add(exit);
        bar.Children.Add(title);
        Children.Add(bar);

        var frame = new Border { Background = Brushes.Black, Child = host };
        SetRow(frame, 1);
        Children.Add(frame);
    }

    /// <summary>The background of the top bar: the same brush as the title bar and footer.</summary>
    public Brush HeaderBackground { get => Background; set => Background = value; }

    public bool IsOpen => preview is not null;

    public void Open(FrontendPreviewViewModel target)
    {
        Close();

        preview = target;
        title.Text = $"{target.EnvironmentTitle} · Claude · {target.Header}";

        host.DataContext = target;
        host.Content = new PlaywrightLiveView();
        target.PropertyChanged += OnPreviewChanged;

        Visibility = Visibility.Visible;
        (host.Content as UIElement)?.Focus();
    }

    public void Close()
    {
        if (preview is not { } closing)
        {
            return;
        }

        preview = null;
        closing.PropertyChanged -= OnPreviewChanged;
        Visibility = Visibility.Collapsed;
        host.Content = null;
        host.DataContext = null;
    }

    // The Claude browser closed or left the frontend: there is nothing left to show.
    private void OnPreviewChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FrontendPreviewViewModel.ClaudeViewVisibility)
            && preview?.ClaudeViewVisibility != "Visible")
        {
            Close();
        }
    }
}

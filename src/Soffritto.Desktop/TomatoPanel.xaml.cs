using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Soffritto.Core.Configuration;
using Soffritto.Presentation;

namespace Soffritto.Desktop;

/// <summary>
/// The garden's tomato timer in a WebView2. The page is created on first open and then stays
/// alive: closing the panel hides it without unloading it, so the timer keeps running.
/// </summary>
/// <remarks>
/// It is the WebView2CompositionControl and not the classic WebView2: that one is a native window,
/// which ignores the panel's opacity, scale and clipping and would draw on top of everything.
/// </remarks>
public partial class TomatoPanel : UserControl
{
    private static readonly Uri Address = new("https://garden.filippoitaliano.com/tomato-timer/");

    // A profile next to the exe would end up in the publish folder: it goes in the user profile,
    // where the page also finds the durations chosen last time.
    private static readonly string ProfileFolder = Path.Combine(ConfigurationFiles.DataFolder, "WebView2");

    private WebView2CompositionControl? view;

    public TomatoPanel()
    {
        InitializeComponent();
        PageHost.SizeChanged += (_, _) =>
            PageHost.Clip = new RectangleGeometry(new Rect(PageHost.RenderSize), 16, 16);
    }

    /// <summary>The panel's X: the host decides how to hide it.</summary>
    public event Action? CloseRequested;

    /// <summary>Creates the page on first open; later opens find it as it was.</summary>
    public async void EnsurePage()
    {
        if (view is not null)
        {
            return;
        }

        var created = new WebView2CompositionControl
        {
            CreationProperties = new CoreWebView2CreationProperties { UserDataFolder = ProfileFolder },
        };
        view = created;
        PageHost.Children.Add(created);

        try
        {
            await created.EnsureCoreWebView2Async();
        }
        catch (Exception exception)
        {
            PageHost.Children.Remove(created);
            view = null;
            Message.Text = $"The tomato timer won't open: {exception.Message}";
            return;
        }

        // Links that would open a new window go to the default browser.
        created.CoreWebView2.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            OpenExternally(e.Uri);
        };
        created.Source = Address;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    private void OpenInBrowser_Click(object sender, RoutedEventArgs e) => OpenExternally(Address.ToString());

    // Like DevToolsController.OpenInBrowser: rundll32 starts outside the exe's job, so the
    // browser does not close together with the app.
    private static void OpenExternally(string url) =>
        DetachedLauncher.Start($"rundll32.exe url.dll,FileProtocolHandler {url}", Path.GetTempPath(), newConsole: false);
}

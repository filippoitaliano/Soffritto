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
/// Il tomato timer del giardino in una WebView2. La pagina nasce alla prima apertura e poi resta
/// viva: il pannello chiuso la nasconde senza scaricarla, cosi' il timer continua a correre.
/// </summary>
/// <remarks>
/// E' la WebView2CompositionControl e non la WebView2 classica: quella e' una finestra nativa,
/// che non segue opacita', scala e ritaglio del pannello e si disegnerebbe sopra tutto.
/// </remarks>
public partial class TomatoPanel : UserControl
{
    private static readonly Uri Address = new("https://garden.filippoitaliano.com/tomato-timer/");

    // Il profilo accanto all'exe finirebbe nella cartella di publish: va nel profilo utente, dove
    // la pagina ritrova anche le durate scelte l'ultima volta.
    private static readonly string ProfileFolder = Path.Combine(ConfigurationFiles.DataFolder, "WebView2");

    private WebView2CompositionControl? view;

    public TomatoPanel()
    {
        InitializeComponent();
        PageHost.SizeChanged += (_, _) =>
            PageHost.Clip = new RectangleGeometry(new Rect(PageHost.RenderSize), 16, 16);
    }

    /// <summary>La X del pannello: chi lo ospita decide come nasconderlo.</summary>
    public event Action? CloseRequested;

    /// <summary>Crea la pagina alla prima apertura; le successive la ritrovano com'era.</summary>
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
            Message.Text = $"Il tomato timer non si apre: {exception.Message}";
            return;
        }

        // I link che aprirebbero una finestra nuova vanno nel browser predefinito.
        created.CoreWebView2.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            OpenExternally(e.Uri);
        };
        created.Source = Address;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    private void OpenInBrowser_Click(object sender, RoutedEventArgs e) => OpenExternally(Address.ToString());

    // Come DevToolsController.OpenInBrowser: rundll32 parte fuori dal job dell'exe, cosi' il
    // browser non si chiude insieme all'app.
    private static void OpenExternally(string url) =>
        DetachedLauncher.Start($"rundll32.exe url.dll,FileProtocolHandler {url}", Path.GetTempPath(), newConsole: false);
}

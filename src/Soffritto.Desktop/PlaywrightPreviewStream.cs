using System.Globalization;
using System.IO;
using System.Net.WebSockets;
using System.Text.Json;
using System.Windows.Media.Imaging;
using Soffritto.Desktop.Infrastructure;
using Soffritto.Desktop.ViewModels;

namespace Soffritto.Desktop;

/// <summary>
/// Una connessione alla dashboard di playwright-cli per un ambiente: si aggancia alla pagina
/// aperta sul dev server del frontend di quell'ambiente, ne riceve i fotogrammi e le inoltra
/// mouse e tastiera. Ogni connessione della dashboard segue una pagina sola, per questo ce n'e'
/// una per ambiente.
/// </summary>
/// <remarks>
/// Il protocollo e' quello interno della dashboard (eventi "sessions", "tabs" e "frame", metodi
/// selectTab, mousedown, keydown, ...): non e' un'API pubblica e un aggiornamento della CLI puo'
/// cambiarlo.
/// </remarks>
/// <param name="localAppData">La LOCALAPPDATA del registro letto dalla dashboard, per lanciare playwright-cli sulle sue sessioni.</param>
public sealed class PlaywrightPreviewStream(FrontendPreviewViewModel vm, string localAppData) : IPreviewInput
{
    private static readonly TimeSpan FirstFrameWait = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CliTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan RestartPause = TimeSpan.FromMilliseconds(300);

    private readonly ClientWebSocket socket = new();
    private readonly SemaphoreSlim sendGate = new(1, 1);
    private readonly Dictionary<string, string> sessionOfBrowser = [];
    private readonly HashSet<string> alignedPages = [];
    private CancellationToken lifetime;
    private volatile string? attachedPage;
    private volatile bool frameReceived;
    private int nextId;

    public async Task RunAsync(Uri socketUri, CancellationToken cancellationToken)
    {
        lifetime = cancellationToken;
        await socket.ConnectAsync(socketUri, cancellationToken).ConfigureAwait(false);

        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();

        try
        {
            while (socket.State == WebSocketState.Open)
            {
                message.SetLength(0);
                WebSocketReceiveResult received;
                do
                {
                    received = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (received.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }

                    message.Write(buffer, 0, received.Count);
                }
                while (!received.EndOfMessage);

                await HandleAsync(new ReadOnlyMemory<byte>(message.GetBuffer(), 0, (int)message.Length)).ConfigureAwait(false);
            }
        }
        finally
        {
            UiThread.Run(vm.DetachSession);
            socket.Dispose();
        }
    }

    public void Send(string method, object parameters)
    {
        if (attachedPage is not null)
        {
            _ = SendAsync(method, parameters);
        }
    }

    private async Task HandleAsync(ReadOnlyMemory<byte> json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        // Le risposte ai metodi hanno "id" e nessun "method": non servono.
        if (!root.TryGetProperty("method", out var method) || !root.TryGetProperty("params", out var parameters))
        {
            return;
        }

        switch (method.GetString())
        {
            case "sessions":
                OnSessions(parameters.GetProperty("sessions"));
                break;

            case "tabs":
                await OnTabsAsync(parameters.GetProperty("tabs")).ConfigureAwait(false);
                break;

            case "frame" when attachedPage is not null:
                frameReceived = true;
                OnFrame(parameters);
                break;
        }
    }

    // Le tab portano solo il guid del browser; il nome della sessione (-s) sta in "sessions".
    private void OnSessions(JsonElement sessions)
    {
        lock (sessionOfBrowser)
        {
            sessionOfBrowser.Clear();
            foreach (var session in sessions.EnumerateArray())
            {
                if (session.TryGetProperty("title", out var title) && title.GetString() is { Length: > 0 } name
                    && session.TryGetProperty("browser", out var browser) && browser.TryGetProperty("guid", out var guid)
                    && guid.GetString() is { Length: > 0 } id)
                {
                    sessionOfBrowser[id] = name;
                }
            }
        }
    }

    private async Task OnTabsAsync(JsonElement tabs)
    {
        JsonElement? match = null;
        var elsewhere = new List<string>();
        foreach (var tab in tabs.EnumerateArray())
        {
            if (!IsFrontendPage(tab))
            {
                if (HostOf(tab) is { } host && !elsewhere.Contains(host))
                {
                    elsewhere.Add(host);
                }

                continue;
            }

            // A parita' si resta sulla pagina gia' agganciata.
            if (match is null || tab.GetProperty("selected").GetBoolean())
            {
                match = tab;
            }
        }

        if (match is not { } chosen)
        {
            // Il caso tipico: il browser e' aperto sul devtunnel o sulla porta di un altro ambiente.
            var reason = elsewhere.Count == 0
                ? "I browser di playwright-cli non hanno pagine aperte."
                : $"Nessuna pagina su localhost:{vm.Port}. Pagine aperte su: {string.Join(", ", elsewhere.Take(3))}{(elsewhere.Count > 3 ? ", ..." : "")}.";

            var wasAttached = attachedPage is not null;
            attachedPage = null;
            UiThread.Run(() =>
            {
                if (wasAttached)
                {
                    vm.DetachSession();
                }

                vm.ShowUnavailableReason(reason);
            });
            return;
        }

        var page = chosen.GetProperty("page").GetString();
        var browser = chosen.GetProperty("browser").GetString();
        var title = chosen.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
        var url = chosen.GetProperty("url").GetString() ?? "";
        UiThread.Run(() => vm.AttachSession(this, title, url));

        if (page == attachedPage)
        {
            return;
        }

        attachedPage = page;
        frameReceived = false;
        _ = AlignViewportIfSilentAsync(page, browser);

        if (!chosen.GetProperty("selected").GetBoolean())
        {
            await SendAsync("selectTab", new
            {
                browser,
                context = chosen.GetProperty("context").GetString(),
                page,
            }).ConfigureAwait(false);
        }
    }

    private bool IsFrontendPage(JsonElement tab)
        => tab.TryGetProperty("url", out var url)
            && Uri.TryCreate(url.GetString(), UriKind.Absolute, out var uri)
            && uri.IsLoopback
            && uri.Port == vm.Port;

    private static string? HostOf(JsonElement tab)
        => tab.TryGetProperty("url", out var url) && Uri.TryCreate(url.GetString(), UriKind.Absolute, out var uri)
            ? uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}"
            : null;

    /// <summary>
    /// Con lo zoom di Windows diverso dal 100% e il browser visibile, la pagina segue la finestra
    /// e Chrome ne riporta le misure in px frazionari (1038.4 x 835.2): playwright-core le vuole
    /// intere e scarta ogni fotogramma prima che arrivi alla dashboard. Se dopo l'aggancio non ne
    /// arriva nessuno, la pagina si fissa alla misura piu' vicina, per difetto, che allo zoom del
    /// browser fa pixel interi: da li' la pagina non segue piu' la finestra.
    /// </summary>
    private async Task AlignViewportIfSilentAsync(string? page, string? browser)
    {
        try
        {
            await Task.Delay(FirstFrameWait, lifetime).ConfigureAwait(false);
            if (frameReceived || attachedPage != page || page is null || browser is null)
            {
                return;
            }

            string? session;
            lock (sessionOfBrowser)
            {
                if (!alignedPages.Add(page))
                {
                    return;
                }

                session = sessionOfBrowser.GetValueOrDefault(browser);
            }

            if (session is null)
            {
                return;
            }

            // Senza spazi ne' virgolette: passa intatta da cmd.
            var measure = await PlaywrightCli.RunAsync(localAppData,
                [$"-s={session}", "--raw", "eval", "[innerWidth,innerHeight,devicePixelRatio].join()"],
                CliTimeout, lifetime).ConfigureAwait(false);

            if (measure is not { ExitCode: 0 } || ParseMeasure(measure.Output) is not { } size
                || size.Scale == Math.Floor(size.Scale) || frameReceived || attachedPage != page)
            {
                return;
            }

            var (width, height, scale) = size;

            var resized = await PlaywrightCli.RunAsync(localAppData,
                [$"-s={session}", "resize", Aligned(width, scale).ToString(CultureInfo.InvariantCulture), Aligned(height, scale).ToString(CultureInfo.InvariantCulture)],
                CliTimeout, lifetime).ConfigureAwait(false);

            if (resized is not { ExitCode: 0 } || attachedPage != page)
            {
                return;
            }

            // Il fotogramma scartato fa saltare anche l'ack a Chrome, che smette di mandarne:
            // lo screencast va fermato e riavviato. La pausa evita che l'avvio insegua l'arresto.
            await SendAsync("setVisible", new { visible = false }).ConfigureAwait(false);
            await Task.Delay(RestartPause, lifetime).ConfigureAwait(false);
            await SendAsync("setVisible", new { visible = true }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static (int Width, int Height, double Scale)? ParseMeasure(string output)
    {
        var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        var parts = line?.Trim('"').Split(',');
        return parts is [var w, var h, var s]
            && int.TryParse(w, CultureInfo.InvariantCulture, out var width)
            && int.TryParse(h, CultureInfo.InvariantCulture, out var height)
            && double.TryParse(s, CultureInfo.InvariantCulture, out var scale)
            && width > 0 && height > 0 && scale > 0
                ? (width, height, scale)
                : null;
    }

    // A 1.25 sono i multipli di 4, a 1.5 i pari: si scende di pochi px al massimo.
    private static int Aligned(int size, double scale)
    {
        for (var candidate = size; candidate > 0 && candidate > size - 100; candidate--)
        {
            var device = candidate * scale;
            if (Math.Abs(device - Math.Round(device)) < 0.001)
            {
                return candidate;
            }
        }

        return size;
    }

    private void OnFrame(JsonElement parameters)
    {
        var data = Convert.FromBase64String(parameters.GetProperty("data").GetString() ?? "");
        var width = parameters.GetProperty("viewportWidth").GetDouble();
        var height = parameters.GetProperty("viewportHeight").GetDouble();

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(data);
        image.EndInit();
        image.Freeze();

        UiThread.Run(() => vm.SetFrame(image, width, height));
    }

    // ClientWebSocket non ammette due invii contemporanei: l'input del mouse arriva a raffica.
    private async Task SendAsync(string method, object parameters)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            id = Interlocked.Increment(ref nextId),
            method,
            @params = parameters,
        });

        try
        {
            await sendGate.WaitAsync(lifetime).ConfigureAwait(false);
            try
            {
                await socket.SendAsync(payload, WebSocketMessageType.Text, true, lifetime).ConfigureAwait(false);
            }
            finally
            {
                sendGate.Release();
            }
        }
        catch
        {
            // Connessione in chiusura: l'input perso non ha conseguenze, la lettura se ne accorge.
        }
    }
}

using System.Globalization;
using System.IO;
using System.Net.WebSockets;
using System.Text.Json;
using System.Windows.Media.Imaging;
using Soffritto.Desktop.Infrastructure;
using Soffritto.Desktop.ViewModels;

namespace Soffritto.Desktop;

/// <summary>
/// A connection to the playwright-cli dashboard for one environment: it attaches to the page open
/// on that environment's frontend dev server, receives its frames and forwards mouse and keyboard
/// to it. Each dashboard connection follows a single page, which is why there is one per environment.
/// </summary>
/// <remarks>
/// The protocol is the dashboard's internal one ("sessions", "tabs" and "frame" events, selectTab,
/// mousedown, keydown, ... methods): it is not a public API and a CLI update may change it.
/// </remarks>
/// <param name="localAppData">The LOCALAPPDATA of the registry the dashboard reads, used to run playwright-cli against its sessions.</param>
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

        // Method responses have an "id" and no "method": we don't need them.
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

    // Tabs carry only the browser guid; the session name (-s) is in "sessions".
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

            // On a tie we stay on the page already attached.
            if (match is null || tab.GetProperty("selected").GetBoolean())
            {
                match = tab;
            }
        }

        if (match is not { } chosen)
        {
            // The typical case: the browser is open on the dev tunnel or on another environment's port.
            var reason = elsewhere.Count == 0
                ? "The playwright-cli browsers have no open pages."
                : $"No page on localhost:{vm.Port}. Pages open on: {string.Join(", ", elsewhere.Take(3))}{(elsewhere.Count > 3 ? ", ..." : "")}.";

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
    /// With Windows scaling other than 100% and a visible browser, the page follows the window and
    /// Chrome reports its size in fractional px (1038.4 x 835.2): playwright-core wants integers and
    /// drops every frame before it reaches the dashboard. If no frame arrives after attaching, the
    /// page is pinned to the nearest smaller size that gives whole pixels at the browser zoom: from
    /// then on the page no longer follows the window.
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

            // No spaces or quotes: it gets through cmd intact.
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

            // The dropped frame also skips the ack to Chrome, which stops sending frames: the
            // screencast has to be stopped and restarted. The pause keeps the start from racing the stop.
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

    // At 1.25 that means multiples of 4, at 1.5 even numbers: we go down a few px at most.
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

    // ClientWebSocket does not allow two concurrent sends: mouse input arrives in bursts.
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
            // Connection closing: the lost input does no harm, and the reader will notice.
        }
    }
}

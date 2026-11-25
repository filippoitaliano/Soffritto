using System.IO;
using System.Net.Sockets;
using System.Text.Json;

namespace Soffritto.Desktop;

/// <summary>
/// Un browser di playwright-cli acceso: nome della sessione (-s), cartella da cui e' stato aperto,
/// la LOCALAPPDATA del registro in cui sta e quando ci e' stato scritto.
/// </summary>
public sealed record PlaywrightSession(string Name, string? WorkspaceDir, string LocalAppData, DateTime RegisteredAt);

/// <summary>
/// I browser che playwright-cli tiene accesi, letti dallo stesso registro che usa la sua
/// dashboard: un file JSON per browser in %LOCALAPPDATA%\ms-playwright\b. Un file resta anche se
/// il daemon muore male, quindi conta solo il browser il cui endpoint risponde ancora.
/// </summary>
/// <remarks>
/// Un'app MSIX (l'app desktop di Claude) virtualizza AppData\Local per i processi che lancia: il
/// registro dei browser aperti dalle sue sessioni sta in Packages\&lt;famiglia&gt;\LocalCache\Local e
/// fuori dal pacchetto non si vede. Le pipe degli endpoint invece sono globali.
/// </remarks>
public static class PlaywrightSessions
{
    private static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static IReadOnlyList<PlaywrightSession> ReadLive()
    {
        // Elencare le pipe non apre connessioni verso il browser: per la pipe basta esserci.
        HashSet<string>? pipes = null;
        var sessions = new List<PlaywrightSession>();

        foreach (var localAppData in LocalAppDataRoots())
        {
            var registry = Path.Combine(localAppData, "ms-playwright", "b");
            if (!Directory.Exists(registry))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(registry))
            {
                if (TryRead(file) is not { } descriptor)
                {
                    continue;
                }

                // Come la dashboard: i browser interni di playwright non sono sessioni di qualcuno.
                if (descriptor.Title.StartsWith("--playwright-internal", StringComparison.Ordinal))
                {
                    continue;
                }

                var live = descriptor.Endpoint.StartsWith(@"\\.\pipe\", StringComparison.OrdinalIgnoreCase)
                    ? (pipes ??= ReadPipes()).Contains(descriptor.Endpoint)
                    : CanConnect(descriptor.Endpoint);

                if (live)
                {
                    sessions.Add(new PlaywrightSession(descriptor.Title, descriptor.WorkspaceDir, localAppData, File.GetLastWriteTimeUtc(file)));
                }
            }
        }

        return sessions.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<string> LocalAppDataRoots()
    {
        yield return LocalAppData;

        string[] packages;
        try
        {
            packages = Directory.GetDirectories(Path.Combine(LocalAppData, "Packages"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var package in packages)
        {
            var virtualized = Path.Combine(package, "LocalCache", "Local");
            if (Directory.Exists(Path.Combine(virtualized, "ms-playwright", "b")))
            {
                yield return virtualized;
            }
        }
    }

    private static Descriptor? TryRead(string file)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            var root = document.RootElement;

            if (!root.TryGetProperty("title", out var title)
                || !root.TryGetProperty("endpoint", out var endpoint)
                || endpoint.GetString() is not { Length: > 0 } endpointText)
            {
                return null;
            }

            var workspace = root.TryGetProperty("workspaceDir", out var ws) ? ws.GetString() : null;
            return new Descriptor(title.GetString() ?? "", endpointText, workspace);
        }
        catch
        {
            // File a meta' scrittura o rimosso tra l'elenco e la lettura: si rilegge al giro dopo.
            return null;
        }
    }

    private static HashSet<string> ReadPipes()
    {
        try
        {
            return new HashSet<string>(Directory.GetFiles(@"\\.\pipe\"), StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return [];
        }
    }

    // Endpoint ws:// (browser attaccati via CDP o server remoti): stesso controllo della dashboard,
    // una connessione TCP aperta e subito chiusa.
    private static bool CanConnect(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Port <= 0)
        {
            return false;
        }

        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync(uri.Host, uri.Port).Wait(TimeSpan.FromMilliseconds(500)) && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private sealed record Descriptor(string Title, string Endpoint, string? WorkspaceDir);
}

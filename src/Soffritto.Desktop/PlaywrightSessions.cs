using System.IO;
using System.Net.Sockets;
using System.Text.Json;

namespace Soffritto.Desktop;

/// <summary>
/// A running playwright-cli browser: session name (-s), the folder it was opened from, the
/// LOCALAPPDATA of the registry it lives in and when it was written there.
/// </summary>
public sealed record PlaywrightSession(string Name, string? WorkspaceDir, string LocalAppData, DateTime RegisteredAt);

/// <summary>
/// The browsers playwright-cli keeps running, read from the same registry its dashboard uses: one
/// JSON file per browser in %LOCALAPPDATA%\ms-playwright\b. A file survives a daemon that dies
/// badly, so only browsers whose endpoint still answers count.
/// </summary>
/// <remarks>
/// An MSIX app (the Claude desktop app) virtualizes AppData\Local for the processes it launches: the
/// registry of the browsers opened by its sessions lives in Packages\&lt;family&gt;\LocalCache\Local
/// and is invisible outside the package. Endpoint pipes, on the other hand, are global.
/// </remarks>
public static class PlaywrightSessions
{
    private static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static IReadOnlyList<PlaywrightSession> ReadLive()
    {
        // Listing pipes opens no connection to the browser: for a pipe, existing is enough.
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

                // Like the dashboard: playwright's internal browsers are nobody's sessions.
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
            // File half-written or removed between listing and reading: it is read again next round.
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

    // ws:// endpoints (browsers attached via CDP or remote servers): same check as the dashboard,
    // a TCP connection opened and closed right away.
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

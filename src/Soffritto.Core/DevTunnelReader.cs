using System.Text.Json;
using System.Text.RegularExpressions;

namespace Soffritto.Core;

public static class DevTunnelReader
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>
    /// Status of the dev tunnel associated with the current clone. The association is by convention, on the
    /// tunnel's Description: <paramref name="tunnelDescription"/> if the caller knows it (the environment
    /// number for the backend, "N-fe" for the frontend, see <see cref="DevTunnelProvisioner"/>),
    /// otherwise the numeric suffix of the working folder (backend-1 -> "1"). This is not a
    /// shortcut: VS persists the real association only in its privateregistry, which cannot be read
    /// reliably. With <paramref name="onlyPort"/> (the assigned pool port) the ports
    /// read and the probe are limited to that one.
    /// </summary>
    public static async Task<DevTunnelStatus> ReadAsync(
        string solutionRoot,
        string? tunnelDescription = null,
        CancellationToken cancellationToken = default,
        int? onlyPort = null)
    {
        var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(solutionRoot));
        var cloneNumber = tunnelDescription ?? Regex.Match(folderName, @"(\d+)$").Groups[1].Value;

        var exe = ResolveExecutable();
        if (exe is null)
        {
            return new DevTunnelStatus(Error: "devtunnel.exe not found (neither in PATH nor among the WinGet packages).");
        }

        var list = await DevTunnelAsync(exe, "list --json", solutionRoot, cancellationToken).ConfigureAwait(false);
        if (!list.Success)
        {
            return new DevTunnelStatus(Error: $"devtunnel list: {list.Tail(2)}");
        }

        string? tunnelId = null;
        var available = new List<DevTunnelSummary>();

        using (var doc = ParseJson(list.StandardOutput))
        {
            if (doc is null || !doc.RootElement.TryGetProperty("tunnels", out var tunnels))
            {
                return new DevTunnelStatus(Error: "Unrecognized output from 'devtunnel list --json'.");
            }

            foreach (var tunnel in tunnels.EnumerateArray())
            {
                var description = GetString(tunnel, "description");

                available.Add(new DevTunnelSummary(
                    GetString(tunnel, "tunnelId"),
                    description,
                    GetInt(tunnel, "hostConnections"),
                    GetInt(tunnel, "portCount")));

                if (cloneNumber.Length > 0 && description == cloneNumber)
                {
                    tunnelId = GetString(tunnel, "tunnelId");
                }
            }
        }

        // Without a match the section does not stay silent: the list of the account's tunnels,
        // with their summary status, is the most useful thing to show instead of the details.
        if (cloneNumber.Length == 0)
        {
            return new DevTunnelStatus(
                Error: $"Folder '{folderName}' has no numeric suffix: cannot tell which tunnel matches it.",
                Available: available);
        }

        if (tunnelId is null)
        {
            return new DevTunnelStatus(
                Description: cloneNumber,
                Error: $"No tunnel with description '{cloneNumber}'.",
                Available: available);
        }

        var show = await DevTunnelAsync(exe, $"show {tunnelId} --json", solutionRoot, cancellationToken).ConfigureAwait(false);
        if (!show.Success)
        {
            return new DevTunnelStatus(TunnelId: tunnelId, Description: cloneNumber, Error: $"devtunnel show: {show.Tail(2)}");
        }

        int hostConnections;
        var ports = new List<DevTunnelPort>();

        using (var doc = ParseJson(show.StandardOutput))
        {
            if (doc is null || !doc.RootElement.TryGetProperty("tunnel", out var tunnel))
            {
                return new DevTunnelStatus(TunnelId: tunnelId, Description: cloneNumber, Error: "Unrecognized output from 'devtunnel show --json'.");
            }

            hostConnections = GetInt(tunnel, "hostConnections");

            if (tunnel.TryGetProperty("ports", out var portArray) && portArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var port in portArray.EnumerateArray())
                {
                    var number = GetInt(port, "portNumber");
                    if (onlyPort is null || number == onlyPort)
                    {
                        ports.Add(new DevTunnelPort(number, GetString(port, "protocol"), GetString(port, "portUri")));
                    }
                }
            }
        }

        if (hostConnections > 0)
        {
            var probed = await Task.WhenAll(ports.Select(p => ProbeAsync(p, cancellationToken))).ConfigureAwait(false);
            ports = [.. probed];
        }

        return new DevTunnelStatus(
            TunnelId: tunnelId,
            Description: cloneNumber,
            HostConnections: hostConnections,
            Ports: ports);
    }

    private static async Task<DevTunnelPort> ProbeAsync(DevTunnelPort port, CancellationToken cancellationToken)
    {
        if (port.PortUri.Length == 0)
        {
            return port;
        }

        // A short local timeout: even with the host connected, a port without a listener can stay silent.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, port.PortUri);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);

            // A 401 or a 404 is a response that made it to the app. 502, 503 and 504 instead come from
            // the devtunnels gateway when the host is connected but nothing is listening on the local
            // port (e.g. the dev server is stopped).
            var status = (int)response.StatusCode;
            return port with { Reachable = status is not (502 or 503 or 504) };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return port with { Reachable = false };
        }
    }

    /// <summary>The devtunnel CLI: first in PATH, then among the user's WinGet packages. Null if missing.</summary>
    public static string? ResolveExecutable()
    {
        var fromPath = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir.Trim(), "devtunnel.exe"))
            .FirstOrDefault(File.Exists);

        if (fromPath is not null)
        {
            return fromPath;
        }

        var packages = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WinGet", "Packages");

        if (!Directory.Exists(packages))
        {
            return null;
        }

        return Directory.EnumerateDirectories(packages, "Microsoft.devtunnel_*")
            .Select(dir => Path.Combine(dir, "devtunnel.exe"))
            .FirstOrDefault(File.Exists);
    }

    internal static Task<ProcessOutcome> DevTunnelAsync(
        string executable,
        string arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        return ProcessRunner.RunAsync(executable, arguments, workingDirectory, timeoutSeconds: 30, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// The CLI may print banners or version notices before the JSON: parsing starts at the first brace.
    /// </summary>
    internal static JsonDocument? ParseJson(string output)
    {
        var start = output.IndexOf('{');
        if (start < 0)
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(output[start..]);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int GetInt(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : 0;

    internal static string GetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}

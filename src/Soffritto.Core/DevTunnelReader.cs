using System.Text.Json;
using System.Text.RegularExpressions;

namespace Soffritto.Core;

public static class DevTunnelReader
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>
    /// Stato del dev tunnel associato al clone corrente. L'associazione e' per convenzione, sulla
    /// Description del tunnel: <paramref name="tunnelDescription"/> se il chiamante la conosce (il numero
    /// dell'ambiente per il backend, "N-fe" per il frontend, vedi <see cref="DevTunnelProvisioner"/>),
    /// altrimenti il suffisso numerico della cartella di lavoro (backend-1 -> "1"). Non e' una
    /// scorciatoia: VS persiste l'associazione reale solo nel privateregistry, che non e' leggibile
    /// in modo affidabile. Con <paramref name="onlyPort"/> (la porta del pool assegnata) le porte
    /// lette e il probe si limitano a quella.
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
            return new DevTunnelStatus(Error: "devtunnel.exe non trovato (ne' nel PATH ne' tra i pacchetti WinGet).");
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
                return new DevTunnelStatus(Error: "Output di 'devtunnel list --json' non riconosciuto.");
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

        // Senza corrispondenza la sezione non resta muta: l'elenco dei tunnel dell'account,
        // con il loro stato sintetico, e' la cosa piu' utile da mostrare al posto del dettaglio.
        if (cloneNumber.Length == 0)
        {
            return new DevTunnelStatus(
                Error: $"La cartella '{folderName}' non ha un suffisso numerico: impossibile dedurre quale tunnel le corrisponde.",
                Available: available);
        }

        if (tunnelId is null)
        {
            return new DevTunnelStatus(
                Description: cloneNumber,
                Error: $"Nessun tunnel con descrizione '{cloneNumber}'.",
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
                return new DevTunnelStatus(TunnelId: tunnelId, Description: cloneNumber, Error: "Output di 'devtunnel show --json' non riconosciuto.");
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

        // Timeout corto e locale: anche a host connesso una porta senza listener puo' restare muta.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, port.PortUri);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);

            // Un 401 o un 404 e' una risposta arrivata fino all'app. 502, 503 e 504 li da' invece
            // il gateway devtunnels quando l'host e' connesso ma sulla porta locale non ascolta
            // nessuno (es. dev server spento).
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

    /// <summary>La CLI devtunnel: prima nel PATH, poi tra i pacchetti WinGet dell'utente. Null se assente.</summary>
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
    /// La CLI puo' anteporre banner o avvisi di versione al JSON: si parte dalla prima graffa.
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

using System.Text.Json;
using Soffritto.Core.Configuration;

namespace Soffritto.Core;

public sealed record KestrelPort(int Number, string Protocol);

/// <summary>
/// I tunnel del pool dell'account: elenco, creazione dei tunnel del pool che mancano (persistenti,
/// "Owner only", senza porte) e porte che un repository espone quando gli si assegna una porta.
/// </summary>
public static class DevTunnelProvisioner
{
    /// <summary>
    /// Le porte da esporre per un repository: quelle scritte a mano se ci sono, altrimenti gli
    /// endpoint https di Kestrel (backend) o la porta del dev server (frontend); vuoto per gli altri.
    /// </summary>
    public static IReadOnlyList<KestrelPort> PortsFor(RepositoryConfiguration repository, string path)
    {
        if (repository.DevTunnel?.Ports is { Count: > 0 } ports)
        {
            return ports.Select(p => new KestrelPort(p.Number, p.Protocol)).ToList();
        }

        return repository switch
        {
            BackendConfiguration backend => KestrelPorts(path, backend),
            FrontendConfiguration frontend => [FrontendPort(FrontendEnv.ReadDevServerPort(path, frontend))],
            _ => [],
        };
    }

    /// <summary>
    /// Le porte da esporre per un repository di un ambiente: in un ambiente Docker la porta host
    /// che lo slot pubblica per il suo container (http), vuoto se non e' in un container; negli
    /// ambienti locali come <see cref="PortsFor(RepositoryConfiguration, string)"/>.
    /// </summary>
    public static IReadOnlyList<KestrelPort> PortsFor(EnvironmentRepository entry, AppConfiguration configuration)
    {
        if (entry.Environment.Kind != EnvironmentKind.Docker)
        {
            return PortsFor(entry.Repository, entry.Path);
        }

        return Docker.DockerSlot.Of(configuration, entry.Environment).PortOf(entry) is { } port
            ? [new KestrelPort(port.Host, "http")]
            : [];
    }

    /// <summary>La porta del dev server del frontend: http, come la serve webpack su localhost.</summary>
    public static KestrelPort FrontendPort(int devServerPort) => new(devServerPort, "http");

    /// <summary>Le Description dei tunnel dell'account; null con l'errore se la CLI manca o non risponde.</summary>
    public static async Task<(IReadOnlySet<string>? Descriptions, string? Error)> ListDescriptionsAsync(CancellationToken cancellationToken = default)
    {
        var exe = DevTunnelReader.ResolveExecutable();
        if (exe is null)
        {
            return (null, "devtunnel.exe non trovato (ne' nel PATH ne' tra i pacchetti WinGet).");
        }

        var list = await DevTunnelReader.DevTunnelAsync(exe, "list --json", Path.GetTempPath(), cancellationToken).ConfigureAwait(false);
        if (!list.Success)
        {
            return (null, $"devtunnel list: {list.Tail(2)}");
        }

        using var doc = DevTunnelReader.ParseJson(list.StandardOutput);
        if (doc is null || !doc.RootElement.TryGetProperty("tunnels", out var tunnels))
        {
            return (null, "Output di 'devtunnel list --json' non riconosciuto.");
        }

        return (tunnels.EnumerateArray().Select(t => DevTunnelReader.GetString(t, "description")).ToHashSet(), null);
    }

    /// <summary>Id e Description dei tunnel dell'account; null con l'errore se la CLI manca o non risponde.</summary>
    public static async Task<(IReadOnlyList<(string Id, string Description)>? Tunnels, string? Error)> ListTunnelsAsync(CancellationToken cancellationToken = default)
    {
        var exe = DevTunnelReader.ResolveExecutable();
        if (exe is null)
        {
            return (null, "devtunnel.exe non trovato (ne' nel PATH ne' tra i pacchetti WinGet).");
        }

        var list = await DevTunnelReader.DevTunnelAsync(exe, "list --json", Path.GetTempPath(), cancellationToken).ConfigureAwait(false);
        if (!list.Success)
        {
            return (null, $"devtunnel list: {list.Tail(2)}");
        }

        using var doc = DevTunnelReader.ParseJson(list.StandardOutput);
        if (doc is null || !doc.RootElement.TryGetProperty("tunnels", out var tunnels) || tunnels.ValueKind != JsonValueKind.Array)
        {
            return (null, "Output di 'devtunnel list --json' non riconosciuto.");
        }

        return (tunnels.EnumerateArray()
            .Select(t => (DevTunnelReader.GetString(t, "tunnelId"), DevTunnelReader.GetString(t, "description")))
            .ToList(), null);
    }

    /// <summary>Le Description del pool che mancano tra quelle dell'account, nell'ordine del pool.</summary>
    public static IReadOnlyList<string> MissingPoolDescriptions(AppConfiguration configuration, IReadOnlyCollection<string> descriptions)
        => configuration.TunnelPool.Tunnels
            .Select(t => t.Description)
            .Where(d => d.Length > 0 && !descriptions.Contains(d))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Crea un tunnel del pool: persistente, "Owner only" (l'accesso anonimo si da' per porta
    /// all'assegnazione) e senza porte. Restituisce l'id del tunnel o l'errore.
    /// </summary>
    public static async Task<(string? TunnelId, string? Error)> CreatePoolTunnelAsync(string description, string label, CancellationToken cancellationToken = default)
    {
        var exe = DevTunnelReader.ResolveExecutable();
        if (exe is null)
        {
            return (null, "devtunnel.exe non trovato.");
        }

        var create = await DevTunnelReader.DevTunnelAsync(
            exe,
            $"create --description {description} --labels {label} --expiration {MaxExpiration} --json",
            Path.GetTempPath(),
            cancellationToken).ConfigureAwait(false);

        if (!create.Success)
        {
            return (null, $"devtunnel create: {create.Tail(2)}");
        }

        using var doc = DevTunnelReader.ParseJson(create.StandardOutput);
        var tunnelId = doc is not null && doc.RootElement.TryGetProperty("tunnel", out var tunnel)
            ? DevTunnelReader.GetString(tunnel, "tunnelId")
            : string.Empty;

        return tunnelId.Length == 0
            ? (null, "Tunnel creato, ma l'output di 'devtunnel create --json' non ne riporta l'id: controlla con devtunnel list.")
            : (tunnelId, null);
    }

    /// <summary>La scadenza massima che il servizio accetta: oltre i 30 giorni rifiuta («Value must be between 1 hour to 30 days»).</summary>
    public const string MaxExpiration = "30d";

    /// <summary>Riporta la scadenza del tunnel a <see cref="MaxExpiration"/> da adesso. Errore o null.</summary>
    public static async Task<string?> RenewAsync(string tunnelId, CancellationToken cancellationToken = default)
    {
        if (DevTunnelReader.ResolveExecutable() is not { } exe)
        {
            return "devtunnel.exe non trovato.";
        }

        var update = await DevTunnelReader.DevTunnelAsync(exe, $"update {tunnelId} --expiration {MaxExpiration}", Path.GetTempPath(), cancellationToken).ConfigureAwait(false);
        return update.Success ? null : $"devtunnel update: {update.Tail(2)}";
    }

    /// <summary>Cancella il tunnel dall'account, con le sue porte e i suoi URL. Errore o null.</summary>
    public static async Task<string?> DeleteTunnelAsync(string tunnelId, CancellationToken cancellationToken = default)
    {
        if (DevTunnelReader.ResolveExecutable() is not { } exe)
        {
            return "devtunnel.exe non trovato.";
        }

        var delete = await DevTunnelReader.DevTunnelAsync(exe, $"delete {tunnelId} --force", Path.GetTempPath(), cancellationToken).ConfigureAwait(false);
        return delete.Success ? null : $"devtunnel delete: {delete.Tail(2)}";
    }

    /// <summary>
    /// Gli endpoint https di Kestrel (Kestrel:Endpoints:*:Url) dal primo appsettings configurato che
    /// li dichiara: gli stessi file da cui si legge la connection string.
    /// </summary>
    /// <remarks>
    /// Gli endpoint http restano fuori: frontend e app puntano sempre alla porta https del tunnel.
    /// </remarks>
    public static IReadOnlyList<KestrelPort> KestrelPorts(string backendPath, BackendConfiguration settings)
    {
        foreach (var candidate in settings.AppSettings)
        {
            var full = Path.Combine(backendPath, candidate);
            if (!File.Exists(full))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(
                    File.ReadAllText(full),
                    new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

                if (!doc.RootElement.TryGetProperty("Kestrel", out var kestrel)
                    || !kestrel.TryGetProperty("Endpoints", out var endpoints)
                    || endpoints.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var ports = endpoints.EnumerateObject()
                    .Select(e => DevTunnelReader.GetString(e.Value, "Url"))
                    .Select(url => Uri.TryCreate(url.Replace("*", "localhost").Replace("+", "localhost"), UriKind.Absolute, out var uri) ? uri : null)
                    .Where(uri => uri is not null && uri.Scheme == Uri.UriSchemeHttps)
                    .Select(uri => new KestrelPort(uri!.Port, uri.Scheme))
                    .Distinct()
                    .OrderBy(p => p.Number)
                    .ToList();

                if (ports.Count > 0)
                {
                    return ports;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
            }
        }

        return [];
    }
}

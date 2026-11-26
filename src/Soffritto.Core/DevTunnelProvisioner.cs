using System.Text.Json;
using Soffritto.Core.Configuration;

namespace Soffritto.Core;

public sealed record KestrelPort(int Number, string Protocol);

/// <summary>
/// The account's pool tunnels: listing, creating the missing pool tunnels (persistent,
/// "Owner only", without ports) and the ports a repository exposes when it is assigned a pool port.
/// </summary>
public static class DevTunnelProvisioner
{
    /// <summary>
    /// The ports to expose for a repository: the hand-written ones if any, otherwise the
    /// Kestrel https endpoints (backend) or the dev server port (frontend); empty for the others.
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
    /// The ports to expose for a repository of an environment: in a Docker environment the host port
    /// the slot publishes for its container (http), empty if it is not in a container; in
    /// local environments as <see cref="PortsFor(RepositoryConfiguration, string)"/>.
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

    /// <summary>The frontend's dev server port: http, as webpack serves it on localhost.</summary>
    public static KestrelPort FrontendPort(int devServerPort) => new(devServerPort, "http");

    /// <summary>The Descriptions of the account's tunnels; null with the error if the CLI is missing or does not respond.</summary>
    public static async Task<(IReadOnlySet<string>? Descriptions, string? Error)> ListDescriptionsAsync(CancellationToken cancellationToken = default)
    {
        var exe = DevTunnelReader.ResolveExecutable();
        if (exe is null)
        {
            return (null, "devtunnel.exe not found (neither in PATH nor among the WinGet packages).");
        }

        var list = await DevTunnelReader.DevTunnelAsync(exe, "list --json", Path.GetTempPath(), cancellationToken).ConfigureAwait(false);
        if (!list.Success)
        {
            return (null, $"devtunnel list: {list.Tail(2)}");
        }

        using var doc = DevTunnelReader.ParseJson(list.StandardOutput);
        if (doc is null || !doc.RootElement.TryGetProperty("tunnels", out var tunnels))
        {
            return (null, "Unrecognized output from 'devtunnel list --json'.");
        }

        return (tunnels.EnumerateArray().Select(t => DevTunnelReader.GetString(t, "description")).ToHashSet(), null);
    }

    /// <summary>Id and Description of the account's tunnels; null with the error if the CLI is missing or does not respond.</summary>
    public static async Task<(IReadOnlyList<(string Id, string Description)>? Tunnels, string? Error)> ListTunnelsAsync(CancellationToken cancellationToken = default)
    {
        var exe = DevTunnelReader.ResolveExecutable();
        if (exe is null)
        {
            return (null, "devtunnel.exe not found (neither in PATH nor among the WinGet packages).");
        }

        var list = await DevTunnelReader.DevTunnelAsync(exe, "list --json", Path.GetTempPath(), cancellationToken).ConfigureAwait(false);
        if (!list.Success)
        {
            return (null, $"devtunnel list: {list.Tail(2)}");
        }

        using var doc = DevTunnelReader.ParseJson(list.StandardOutput);
        if (doc is null || !doc.RootElement.TryGetProperty("tunnels", out var tunnels) || tunnels.ValueKind != JsonValueKind.Array)
        {
            return (null, "Unrecognized output from 'devtunnel list --json'.");
        }

        return (tunnels.EnumerateArray()
            .Select(t => (DevTunnelReader.GetString(t, "tunnelId"), DevTunnelReader.GetString(t, "description")))
            .ToList(), null);
    }

    /// <summary>The pool Descriptions missing from the account's ones, in pool order.</summary>
    public static IReadOnlyList<string> MissingPoolDescriptions(AppConfiguration configuration, IReadOnlyCollection<string> descriptions)
        => configuration.TunnelPool.Tunnels
            .Select(t => t.Description)
            .Where(d => d.Length > 0 && !descriptions.Contains(d))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Creates a pool tunnel: persistent, "Owner only" (anonymous access is granted per port
    /// on assignment) and without ports. Returns the tunnel id or the error.
    /// </summary>
    public static async Task<(string? TunnelId, string? Error)> CreatePoolTunnelAsync(string description, string label, CancellationToken cancellationToken = default)
    {
        var exe = DevTunnelReader.ResolveExecutable();
        if (exe is null)
        {
            return (null, "devtunnel.exe not found.");
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
            ? (null, "Tunnel created, but the output of 'devtunnel create --json' does not report its id: check with devtunnel list.")
            : (tunnelId, null);
    }

    /// <summary>The maximum expiration the service accepts: beyond 30 days it refuses ("Value must be between 1 hour to 30 days").</summary>
    public const string MaxExpiration = "30d";

    /// <summary>Resets the tunnel's expiration to <see cref="MaxExpiration"/> from now. Error or null.</summary>
    public static async Task<string?> RenewAsync(string tunnelId, CancellationToken cancellationToken = default)
    {
        if (DevTunnelReader.ResolveExecutable() is not { } exe)
        {
            return "devtunnel.exe not found.";
        }

        var update = await DevTunnelReader.DevTunnelAsync(exe, $"update {tunnelId} --expiration {MaxExpiration}", Path.GetTempPath(), cancellationToken).ConfigureAwait(false);
        return update.Success ? null : $"devtunnel update: {update.Tail(2)}";
    }

    /// <summary>Deletes the tunnel from the account, with its ports and URLs. Error or null.</summary>
    public static async Task<string?> DeleteTunnelAsync(string tunnelId, CancellationToken cancellationToken = default)
    {
        if (DevTunnelReader.ResolveExecutable() is not { } exe)
        {
            return "devtunnel.exe not found.";
        }

        var delete = await DevTunnelReader.DevTunnelAsync(exe, $"delete {tunnelId} --force", Path.GetTempPath(), cancellationToken).ConfigureAwait(false);
        return delete.Success ? null : $"devtunnel delete: {delete.Tail(2)}";
    }

    /// <summary>
    /// The Kestrel https endpoints (Kestrel:Endpoints:*:Url) from the first configured appsettings that
    /// declares them: the same files the connection string is read from.
    /// </summary>
    /// <remarks>
    /// http endpoints are left out: frontend and app always point to the tunnel's https port.
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

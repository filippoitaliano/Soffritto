using Soffritto.Core;
using Soffritto.Core.Configuration;
using Soffritto.Core.Docker;
using Soffritto.Core.Env;

namespace Soffritto.Presentation;

/// <summary>
/// The tunnel of an environment: when on, the environment has a pool tunnel with a port for each of its
/// repositories that exposes one, and the host in a console; when off, it has no ports and the tunnel is
/// free again. This is the only command for the tunnel: both the app and the MCP server go through here.
/// </summary>
public static class EnvironmentTunnel
{
    /// <summary>True if the environment has at least one pool port.</summary>
    public static bool IsOn(int number, IReadOnlyList<PoolAssignment>? assignments = null)
        => (assignments ?? TunnelPool.Read()).Any(a => a.Environment == number);

    /// <summary>
    /// Assigns a pool port to every repository of the environment that exposes one (the first one
    /// claims a free tunnel), turns on the host or restarts it if it has new ports, and rewrites the
    /// env variables that depend on the pool. Error or null.
    /// </summary>
    public static async Task<string?> EnableAsync(AppConfiguration c, int number, Action<string> log, CancellationToken ct = default)
    {
        if (c.Environment(number) is not { } environment)
        {
            return $"environment {number} does not exist";
        }

        if (c.TunnelPool.Tunnels.Count == 0)
        {
            return "empty pool: create the tunnels from the “Tunnels” tab of the setup";
        }

        var entries = c.RepositoriesOf(environment).Where(e => DevTunnelProvisioner.PortsFor(e, c).Count > 0).ToList();
        if (entries.Count == 0)
        {
            return $"no repository of environment {number} exposes a port";
        }

        var before = TunnelPool.Read();
        var assigned = new List<PoolAssignment>();
        foreach (var entry in entries)
        {
            var (assignment, error) = await TunnelPool.AssignAsync(c, entry, log, ct).ConfigureAwait(false);
            if (assignment is not null)
            {
                assigned.Add(assignment);
            }
            else if (assigned.Count == 0)
            {
                return error;
            }
            else
            {
                log($"{entry.Id} without a pool port: {error}");
            }
        }

        // A running host does not serve ports added after it started: with new ports it restarts.
        foreach (var tunnel in assigned.GroupBy(a => a.TunnelDescription, StringComparer.Ordinal))
        {
            if (tunnel.Any(a => !before.Contains(a)))
            {
                PoolHosts.Ensure(c, c.TunnelPool.Tunnels.FindIndex(t => t.Description == tunnel.Key));
                log($"host of tunnel {tunnel.Key} turned on");
            }
            else if (PoolHosts.StartIfStopped(tunnel.Key, tunnel.First().TunnelId) is { } started)
            {
                log(started);
            }
        }

        var urls = new Dictionary<string, string>(await DockerEnvironmentProvisioner.PoolUrlsAsync(number, ct).ConfigureAwait(false), StringComparer.OrdinalIgnoreCase);
        foreach (var assignment in assigned)
        {
            if (await TunnelPool.WaitUrlAsync(assignment, ct).ConfigureAwait(false) is { } url)
            {
                urls[assignment.Repository] = url.TrimEnd('/');
            }
            else
            {
                log($"URL of port {assignment.Port} of {assignment.Repository} not published yet: turn the tunnel off and on again");
            }
        }

        await EnvPoolSync.ApplyAsync(c, number, EnvSyncScope.PoolDependent, log, null, ct, urls).ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// Turns the crashed or closed host of the tunnels the environment already has back on: ports and env stay
    /// as they are. A host that does not start is reported in the log without stopping the caller.
    /// </summary>
    public static void RestoreHosts(int number, Action<string> log)
    {
        foreach (var tunnel in TunnelPool.Read().Where(a => a.Environment == number).GroupBy(a => a.TunnelDescription, StringComparer.Ordinal))
        {
            try
            {
                if (PoolHosts.StartIfStopped(tunnel.Key, tunnel.First().TunnelId) is { } started)
                {
                    log(started);
                }
            }
            catch (InvalidOperationException ex)
            {
                log($"host of tunnel {tunnel.Key} not turned on: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Releases all the environment's pool ports, closes the host of the tunnels left without
    /// ports and rewrites the env variables that depended on the pool. Error or null.
    /// </summary>
    public static async Task<string?> DisableAsync(AppConfiguration c, int number, Action<string> log, CancellationToken ct = default)
    {
        var mine = TunnelPool.Read().Where(a => a.Environment == number).ToList();
        if (mine.Count == 0)
        {
            return null;
        }

        var error = await TunnelPool.ReleaseAsync(c, number, log, ct).ConfigureAwait(false);
        foreach (var description in mine.Select(a => a.TunnelDescription).Distinct(StringComparer.Ordinal))
        {
            if (PoolHosts.StopIfUnused(description) is { } stopped)
            {
                log(stopped);
            }
        }

        if (c.Environment(number) is not null)
        {
            await EnvPoolSync.ApplyAsync(c, number, EnvSyncScope.PoolDependent, log, null, ct).ConfigureAwait(false);
        }

        return error;
    }
}

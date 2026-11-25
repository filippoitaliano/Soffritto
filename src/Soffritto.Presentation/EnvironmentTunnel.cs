using Soffritto.Core;
using Soffritto.Core.Configuration;
using Soffritto.Core.Docker;
using Soffritto.Core.Env;

namespace Soffritto.Presentation;

/// <summary>
/// Il tunnel di un ambiente: acceso, l'ambiente ha un tunnel del pool con una porta per ogni suo
/// repository che ne espone una e l'host in una console; spento, non ha porte e il tunnel e' di
/// nuovo libero. E' l'unico comando sul tunnel: exe e server MCP passano da qui.
/// </summary>
public static class EnvironmentTunnel
{
    /// <summary>Vero se l'ambiente ha almeno una porta del pool.</summary>
    public static bool IsOn(int number, IReadOnlyList<PoolAssignment>? assignments = null)
        => (assignments ?? TunnelPool.Read()).Any(a => a.Environment == number);

    /// <summary>
    /// Assegna una porta del pool a ogni repository dell'ambiente che ne espone una (il primo
    /// reclama un tunnel libero), accende l'host o lo riavvia se ha porte nuove, e riscrive le
    /// variabili di env che dipendono dal pool. Errore o null.
    /// </summary>
    public static async Task<string?> EnableAsync(AppConfiguration c, int number, Action<string> log, CancellationToken ct = default)
    {
        if (c.Environment(number) is not { } environment)
        {
            return $"l'ambiente {number} non c'e'";
        }

        if (c.TunnelPool.Tunnels.Count == 0)
        {
            return "pool vuoto: crea i tunnel dalla scheda «Tunnel» del setup";
        }

        var entries = c.RepositoriesOf(environment).Where(e => DevTunnelProvisioner.PortsFor(e, c).Count > 0).ToList();
        if (entries.Count == 0)
        {
            return $"nessun repository dell'ambiente {number} espone una porta";
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
                log($"{entry.Id} senza porta del pool: {error}");
            }
        }

        // Un host acceso non ospita le porte aggiunte dopo il suo avvio: con porte nuove riparte.
        foreach (var tunnel in assigned.GroupBy(a => a.TunnelDescription, StringComparer.Ordinal))
        {
            if (tunnel.Any(a => !before.Contains(a)))
            {
                PoolHosts.Ensure(c, c.TunnelPool.Tunnels.FindIndex(t => t.Description == tunnel.Key));
                log($"host del tunnel {tunnel.Key} acceso");
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
                log($"URL della porta {assignment.Port} di {assignment.Repository} non ancora pubblicato: spegni e riaccendi il tunnel");
            }
        }

        await EnvPoolSync.ApplyAsync(c, number, EnvSyncScope.PoolDependent, log, null, ct, urls).ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// Riaccende l'host caduto o chiuso dei tunnel che l'ambiente ha gia': porte ed env restano
    /// come sono. Un host che non parte si riporta nel log senza fermare chi chiama.
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
                log($"host del tunnel {tunnel.Key} non acceso: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Rilascia tutte le porte del pool dell'ambiente, chiude l'host dei tunnel rimasti senza
    /// porte e riscrive le variabili di env che dipendevano dal pool. Errore o null.
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

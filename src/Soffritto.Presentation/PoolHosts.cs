using Soffritto.Core;
using Soffritto.Core.Configuration;
using Soffritto.Core.Docker;

namespace Soffritto.Presentation;

/// <summary>Gli host dei tunnel del pool: una console per tunnel, nella cartella dati.</summary>
public static class PoolHosts
{
    private const string ExternalHost = "da un host che non e' tra i processi di questa macchina";

    /// <summary>Il marcatore della console dell'host del tunnel con quella Description: "AMBIENTI tunnel-pool-pool-1".</summary>
    public static string Marker(string description)
    {
        var (service, key) = TunnelPool.HostKey(description);
        return ConsoleServices.Marker(service, key);
    }

    /// <summary>Stato, avvio e stop dell'host del tunnel con quella Description, con la console nella cartella dati.</summary>
    public static TunnelHostTools Tools(string description) => new(ConfigurationFiles.DataFolder, Marker(description), ExternalHost);

    /// <summary>
    /// Accende l'host del tunnel <paramref name="index"/> del pool, o lo riavvia se la sua console
    /// e' accesa (un host acceso non ospita le porte aggiunte dopo il suo avvio): vero se l'ha riavviato.
    /// </summary>
    public static bool Ensure(AppConfiguration c, int index)
    {
        if (index < 0 || index >= c.TunnelPool.Tunnels.Count)
        {
            return false;
        }

        var description = c.TunnelPool.Tunnels[index].Description;
        var tunnelId = TunnelPool.Read().FirstOrDefault(a => a.TunnelDescription == description)?.TunnelId;
        var exe = DevTunnelReader.ResolveExecutable();
        if (tunnelId is null || exe is null)
        {
            return false;
        }

        var marker = Marker(description);
        var consoles = ConsoleServices.Pids(ProcessTable.Read([]), marker, _ => false).ToList();
        ConsoleServices.Stop(consoles);
        ConsoleServices.StartConsole(ConfigurationFiles.DataFolder, marker, $"\"{exe}\" host {tunnelId}");
        return consoles.Count > 0;
    }

    /// <summary>Accende l'host del tunnel con quella Description se e' spento: l'esito, o null se c'era gia'.</summary>
    public static string? StartIfStopped(string description, string tunnelId)
    {
        var tools = Tools(description);
        var scan = ProcessTable.Read([]);
        return tools.Evaluate(scan, tunnelId, hostConnected: false, string.Empty).State == ServiceState.Stopped
            ? $"{tools.Start(scan, tunnelId, hostConnected: false)} ({description})"
            : null;
    }

    /// <summary>Chiude l'host del tunnel se nessun ambiente ha piu' porte su quel tunnel: l'esito, o null se resta acceso.</summary>
    public static string? StopIfUnused(string description)
    {
        if (TunnelPool.Read().Any(a => a.TunnelDescription == description))
        {
            return null;
        }

        ConsoleServices.Stop(ConsoleServices.Pids(ProcessTable.Read([]), Marker(description), _ => false));
        return $"host del tunnel {description} fermato: nessuna porta assegnata";
    }
}

using Soffritto.Core;
using Soffritto.Core.Configuration;
using Soffritto.Core.Docker;

namespace Soffritto.Presentation;

/// <summary>The hosts of the pool tunnels: one console per tunnel, in the data folder.</summary>
public static class PoolHosts
{
    private const string ExternalHost = "from a host that is not among this machine's processes";

    /// <summary>The console marker of the host of the tunnel with that Description: "AMBIENTI tunnel-pool-pool-1".</summary>
    public static string Marker(string description)
    {
        var (service, key) = TunnelPool.HostKey(description);
        return ConsoleServices.Marker(service, key);
    }

    /// <summary>State, start and stop of the host of the tunnel with that Description, with the console in the data folder.</summary>
    public static TunnelHostTools Tools(string description) => new(ConfigurationFiles.DataFolder, Marker(description), ExternalHost);

    /// <summary>
    /// Turns on the host of pool tunnel <paramref name="index"/>, or restarts it if its console
    /// is running (a running host does not serve ports added after it started): true if it restarted it.
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

    /// <summary>Turns on the host of the tunnel with that Description if it is off: the outcome, or null if it was already there.</summary>
    public static string? StartIfStopped(string description, string tunnelId)
    {
        var tools = Tools(description);
        var scan = ProcessTable.Read([]);
        return tools.Evaluate(scan, tunnelId, hostConnected: false, string.Empty).State == ServiceState.Stopped
            ? $"{tools.Start(scan, tunnelId, hostConnected: false)} ({description})"
            : null;
    }

    /// <summary>Closes the tunnel host if no environment has ports on that tunnel any more: the outcome, or null if it stays on.</summary>
    public static string? StopIfUnused(string description)
    {
        if (TunnelPool.Read().Any(a => a.TunnelDescription == description))
        {
            return null;
        }

        ConsoleServices.Stop(ConsoleServices.Pids(ProcessTable.Read([]), Marker(description), _ => false));
        return $"host of tunnel {description} stopped: no ports assigned";
    }
}

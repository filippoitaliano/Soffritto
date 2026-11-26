using Soffritto.Core;

namespace Soffritto.Presentation;

/// <param name="ToolTip">Where the host state comes from: the CLI seen among the processes, or the last tunnel read.</param>
public sealed record TunnelHostView(
    ServiceState State,
    string ToolTip,
    bool CanStart,
    bool CanStop,
    string StartToolTip);

/// <summary>
/// The host of a dev tunnel (devtunnel host) in a detached console: state, start, stop. It serves the
/// backend tunnel (via <see cref="BackendTools"/>) and the frontend one.
/// </summary>
/// <param name="workingDirectory">The clone folder the console starts from.</param>
/// <param name="marker">The console marker (see <see cref="ConsoleServices.Marker"/>).</param>
/// <param name="externalHost">
/// Who else can host the tunnel outside this machine's CLI, for the tooltip of the
/// state read from the tunnel (e.g. "for example from Visual Studio").
/// </param>
public sealed class TunnelHostTools(string workingDirectory, string marker, string externalHost)
{
    /// <param name="tunnelId">Tunnel id from the last read; null if not read or not found.</param>
    /// <param name="hostConnected">Whether the host was connected at the last read, whoever hosts it.</param>
    /// <param name="readStamp">When that read was made, for the tooltip.</param>
    public TunnelHostView Evaluate(IReadOnlyList<ProcessRow> scan, string? tunnelId, bool hostConnected, string readStamp)
    {
        var cli = ConsoleServices.StateOf(scan, marker, p => IsTunnelHost(p, tunnelId));
        var state = State(cli, tunnelId, hostConnected, readStamp, out var toolTip);

        return new TunnelHostView(
            state,
            toolTip,
            CanStart: tunnelId is not null && state == ServiceState.Stopped,
            // Stop only closes a host started from the CLI: one hosted elsewhere is not ours to close.
            CanStop: cli != ServiceState.Stopped,
            StartToolTip: tunnelId is null
                ? "The dev tunnel has not been read yet: refresh."
                : state != ServiceState.Stopped
                    ? $"The tunnel host is already there ({externalHost}): a second one is not opened"
                    : $"Start the host of dev tunnel {tunnelId} (devtunnel host) in a console");
    }

    /// <summary>Starts the tunnel host; throws if the tunnel has not been read or a host is already there.</summary>
    public string Start(IReadOnlyList<ProcessRow> scan, string? tunnelId, bool hostConnected)
    {
        var cli = ConsoleServices.StateOf(scan, marker, p => IsTunnelHost(p, tunnelId));
        if (State(cli, tunnelId, hostConnected, string.Empty, out _) != ServiceState.Stopped)
        {
            throw new InvalidOperationException("The tunnel host is already there.");
        }

        var id = tunnelId ?? throw new InvalidOperationException("Dev tunnel not read.");
        var exe = DevTunnelReader.ResolveExecutable() ?? throw new InvalidOperationException("devtunnel.exe not found.");

        return ConsoleServices.StartConsole(workingDirectory, marker, $"\"{exe}\" host {id}")
            ? "tunnel host started"
            : "tunnel host started inside the host's job: it will close along with the host";
    }

    // Only closes a host started from the CLI: one hosted elsewhere is not ours to close.
    public string Stop(IReadOnlyList<ProcessRow> scan, string? tunnelId)
    {
        ConsoleServices.Stop(ConsoleServices.Pids(scan, marker, p => IsTunnelHost(p, tunnelId)));
        return "tunnel host stopped";
    }

    // Visual Studio hosts its tunnels inside its own process: only the CLI is recognised here.
    private static bool IsTunnelHost(ProcessRow p, string? tunnelId)
    {
        var id = tunnelId?.Split('.')[0];
        return ConsoleServices.Is(p, "devtunnel.exe")
            && id is not null
            && p.CommandLine.Contains(" host", StringComparison.OrdinalIgnoreCase)
            && p.CommandLine.Contains(id, StringComparison.OrdinalIgnoreCase);
    }

    // Two sources: the devtunnel CLI is visible among the processes, in real time; a host running elsewhere
    // (Visual Studio, another machine) is known only from the last tunnel read
    // (hostConnections), which holds until the next refresh.
    private ServiceState State(
        ServiceState cli,
        string? tunnelId,
        bool hostConnected,
        string readStamp,
        out string toolTip)
    {
        var name = $"Host of tunnel {tunnelId ?? "(not read)"}";

        if (cli == ServiceState.Running)
        {
            toolTip = $"{name}: running (devtunnel host)";
            return ServiceState.Running;
        }

        if (hostConnected)
        {
            toolTip = $"{name}: connected according to the tunnel read ({readStamp}), "
                + $"{externalHost}. Refresh to update.";
            return ServiceState.Running;
        }

        toolTip = cli == ServiceState.WindowOnly
            ? $"{name}: console open, host not running (yet)"
            : $"{name}: stopped";

        return cli;
    }
}

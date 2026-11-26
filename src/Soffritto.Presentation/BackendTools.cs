using System.Text.RegularExpressions;
using Soffritto.Core;
using Soffritto.Core.Configuration;

namespace Soffritto.Presentation;

/// <param name="TunnelToolTip">Where the host state comes from: the CLI seen among the processes, or the last tunnel read.</param>
public sealed record BackendToolsView(
    ServiceState Kestrel,
    ServiceState Tunnel,
    string TunnelToolTip,
    bool TunnelCanStart,
    bool TunnelCanStop,
    string TunnelStartToolTip);

/// <summary>
/// Kestrel and the dev tunnel host of a backend: state, start in detached consoles, stop.
/// Used by both the Desktop app and the VSIX, which see the same consoles thanks to the markers.
/// </summary>
public sealed partial class BackendTools
{
    private readonly string backendPath;
    private readonly BackendConfiguration settings;
    private readonly string kestrelMarker;
    private readonly TunnelHostTools tunnelHost;

    /// <param name="key">The environment number (see <see cref="EnvironmentKey"/>).</param>
    public BackendTools(string backendPath, BackendConfiguration settings, string key)
    {
        this.backendPath = backendPath;
        this.settings = settings;

        // The VSIX and the app mark consoles the same way, so each recognises the other's.
        kestrelMarker = ConsoleServices.Marker("kestrel", key);
        tunnelHost = new TunnelHostTools(backendPath, ConsoleServices.Marker("tunnel", key), "for example from Visual Studio");
    }

    /// <summary>
    /// The number of the environment the backend belongs to according to the configuration; otherwise
    /// the folder suffix (backend-1 -> "1"), as DevTunnelReader does, or the folder name if it
    /// does not end with a number.
    /// </summary>
    public static string EnvironmentKey(string backendPath, AppConfiguration? configuration)
    {
        if (configuration?.BackendEnvironmentOf(backendPath) is { } environment)
        {
            return environment.Environment.Number.ToString();
        }

        var folder = StatusText.WorkingFolder(backendPath);
        var match = NumberSuffix().Match(folder);
        return match.Success ? match.Value : folder;
    }

    /// <param name="tunnelId">Tunnel id from the last read; null if not read or not found.</param>
    /// <param name="hostConnected">Whether the host was connected at the last read (CLI or Visual Studio).</param>
    /// <param name="readStamp">When that read was made, for the tooltip.</param>
    public BackendToolsView Evaluate(IReadOnlyList<ProcessRow> scan, string? tunnelId, bool hostConnected, string readStamp)
    {
        var kestrel = ConsoleServices.StateOf(scan, kestrelMarker, IsKestrel);
        var tunnel = tunnelHost.Evaluate(scan, tunnelId, hostConnected, readStamp);

        return new BackendToolsView(
            kestrel,
            tunnel.State,
            tunnel.ToolTip,
            tunnel.CanStart,
            tunnel.CanStop,
            tunnel.StartToolTip);
    }

    /// <summary>Starts Kestrel only, without touching the tunnel host.</summary>
    public string StartKestrel()
        => LaunchKestrel()
            ? "Kestrel started"
            : "Kestrel started inside the host's job: it will close along with the host";

    public string StopKestrel(IReadOnlyList<ProcessRow> scan)
    {
        ConsoleServices.Stop(ConsoleServices.Pids(scan, kestrelMarker, IsKestrel));
        return "Kestrel stopped";
    }

    private bool LaunchKestrel()
        => ConsoleServices.StartConsole(
            Path.Combine(backendPath, settings.ApiProject),
            kestrelMarker,
            $"dotnet run --launch-profile {settings.LaunchProfile} --no-launch-browser");

    private bool IsKestrel(ProcessRow p)
        => settings.ApiProcess.Length > 0
            && ConsoleServices.Is(p, settings.ApiProcess)
            && ConsoleServices.Under(p.ExecutablePath, backendPath);

    [GeneratedRegex(@"\d+$")]
    private static partial Regex NumberSuffix();
}

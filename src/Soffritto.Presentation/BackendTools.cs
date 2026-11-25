using System.Text.RegularExpressions;
using Soffritto.Core;
using Soffritto.Core.Configuration;

namespace Soffritto.Presentation;

/// <param name="TunnelToolTip">Da dove viene lo stato dell'host: CLI vista tra i processi o ultima lettura del tunnel.</param>
public sealed record BackendToolsView(
    ServiceState Kestrel,
    ServiceState Tunnel,
    string TunnelToolTip,
    bool TunnelCanStart,
    bool TunnelCanStop,
    string TunnelStartToolTip);

/// <summary>
/// Kestrel e l'host del dev tunnel di un backend: stato, avvio in console staccate, stop.
/// Lo usano sia l'exe Desktop sia la VSIX, che vedono le stesse console grazie ai marcatori.
/// </summary>
public sealed partial class BackendTools
{
    private readonly string backendPath;
    private readonly BackendConfiguration settings;
    private readonly string kestrelMarker;
    private readonly TunnelHostTools tunnelHost;

    /// <param name="key">Il numero dell'ambiente (vedi <see cref="EnvironmentKey"/>).</param>
    public BackendTools(string backendPath, BackendConfiguration settings, string key)
    {
        this.backendPath = backendPath;
        this.settings = settings;

        // VSIX ed exe marcano le console allo stesso modo e si riconoscono.
        kestrelMarker = ConsoleServices.Marker("kestrel", key);
        tunnelHost = new TunnelHostTools(backendPath, ConsoleServices.Marker("tunnel", key), "per esempio da Visual Studio");
    }

    /// <summary>
    /// Il numero dell'ambiente a cui appartiene il backend secondo la configurazione; altrimenti
    /// il suffisso della cartella (backend-1 -> "1"), come fa DevTunnelReader, o il nome della
    /// cartella se non finisce con un numero.
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

    /// <param name="tunnelId">Id del tunnel dall'ultima lettura; null se non letto o non trovato.</param>
    /// <param name="hostConnected">Host connesso secondo l'ultima lettura (CLI o Visual Studio).</param>
    /// <param name="readStamp">Quando e' stata fatta quella lettura, per il tooltip.</param>
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

    /// <summary>Avvia solo Kestrel, senza toccare l'host del tunnel.</summary>
    public string StartKestrel()
        => LaunchKestrel()
            ? "Kestrel avviato"
            : "Kestrel avviato dentro il job dell'host: si chiudera' insieme a lui";

    public string StopKestrel(IReadOnlyList<ProcessRow> scan)
    {
        ConsoleServices.Stop(ConsoleServices.Pids(scan, kestrelMarker, IsKestrel));
        return "Kestrel fermato";
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

using Soffritto.Core;

namespace Soffritto.Presentation;

/// <param name="ToolTip">Da dove viene lo stato dell'host: CLI vista tra i processi o ultima lettura del tunnel.</param>
public sealed record TunnelHostView(
    ServiceState State,
    string ToolTip,
    bool CanStart,
    bool CanStop,
    string StartToolTip);

/// <summary>
/// L'host di un dev tunnel (devtunnel host) in una console staccata: stato, avvio, stop. Serve il
/// tunnel del backend (via <see cref="BackendTools"/>) e quello del frontend.
/// </summary>
/// <param name="workingDirectory">La cartella del clone da cui parte la console.</param>
/// <param name="marker">Il marcatore della console (vedi <see cref="ConsoleServices.Marker"/>).</param>
/// <param name="externalHost">
/// Chi altro puo' ospitare il tunnel fuori dalla CLI di questa macchina, per il tooltip dello
/// stato letto dal tunnel (es. "per esempio da Visual Studio").
/// </param>
public sealed class TunnelHostTools(string workingDirectory, string marker, string externalHost)
{
    /// <param name="tunnelId">Id del tunnel dall'ultima lettura; null se non letto o non trovato.</param>
    /// <param name="hostConnected">Host connesso secondo l'ultima lettura, chiunque lo ospiti.</param>
    /// <param name="readStamp">Quando e' stata fatta quella lettura, per il tooltip.</param>
    public TunnelHostView Evaluate(IReadOnlyList<ProcessRow> scan, string? tunnelId, bool hostConnected, string readStamp)
    {
        var cli = ConsoleServices.StateOf(scan, marker, p => IsTunnelHost(p, tunnelId));
        var state = State(cli, tunnelId, hostConnected, readStamp, out var toolTip);

        return new TunnelHostView(
            state,
            toolTip,
            CanStart: tunnelId is not null && state == ServiceState.Stopped,
            // Ferma chiude solo un host avviato dalla CLI: quello ospitato altrove non e' suo da chiudere.
            CanStop: cli != ServiceState.Stopped,
            StartToolTip: tunnelId is null
                ? "Il dev tunnel non e' ancora stato letto: aggiorna."
                : state != ServiceState.Stopped
                    ? $"L'host del tunnel c'e' gia' ({externalHost}): non se ne apre un secondo"
                    : $"Avvia l'host del dev tunnel {tunnelId} (devtunnel host) in una console");
    }

    /// <summary>Avvia l'host del tunnel; solleva se il tunnel non e' letto o un host c'e' gia'.</summary>
    public string Start(IReadOnlyList<ProcessRow> scan, string? tunnelId, bool hostConnected)
    {
        var cli = ConsoleServices.StateOf(scan, marker, p => IsTunnelHost(p, tunnelId));
        if (State(cli, tunnelId, hostConnected, string.Empty, out _) != ServiceState.Stopped)
        {
            throw new InvalidOperationException("L'host del tunnel c'e' gia'.");
        }

        var id = tunnelId ?? throw new InvalidOperationException("Dev tunnel non letto.");
        var exe = DevTunnelReader.ResolveExecutable() ?? throw new InvalidOperationException("devtunnel.exe non trovato.");

        return ConsoleServices.StartConsole(workingDirectory, marker, $"\"{exe}\" host {id}")
            ? "host del tunnel avviato"
            : "host del tunnel avviato dentro il job dell'host: si chiudera' insieme a lui";
    }

    // Chiude solo un host avviato dalla CLI: quello ospitato altrove non e' suo da chiudere.
    public string Stop(IReadOnlyList<ProcessRow> scan, string? tunnelId)
    {
        ConsoleServices.Stop(ConsoleServices.Pids(scan, marker, p => IsTunnelHost(p, tunnelId)));
        return "host del tunnel fermato";
    }

    // Visual Studio ospita i suoi tunnel dentro il proprio processo: qui si riconosce solo la CLI.
    private static bool IsTunnelHost(ProcessRow p, string? tunnelId)
    {
        var id = tunnelId?.Split('.')[0];
        return ConsoleServices.Is(p, "devtunnel.exe")
            && id is not null
            && p.CommandLine.Contains(" host", StringComparison.OrdinalIgnoreCase)
            && p.CommandLine.Contains(id, StringComparison.OrdinalIgnoreCase);
    }

    // Due fonti: la CLI devtunnel si vede tra i processi, in tempo reale; un host ospitato altrove
    // (Visual Studio, un'altra macchina) si sa solo dall'ultima lettura del tunnel
    // (hostConnections), che vale fino al refresh successivo.
    private ServiceState State(
        ServiceState cli,
        string? tunnelId,
        bool hostConnected,
        string readStamp,
        out string toolTip)
    {
        var name = $"Host del tunnel {tunnelId ?? "(non letto)"}";

        if (cli == ServiceState.Running)
        {
            toolTip = $"{name}: acceso (devtunnel host)";
            return ServiceState.Running;
        }

        if (hostConnected)
        {
            toolTip = $"{name}: connesso secondo la lettura del tunnel ({readStamp}), "
                + $"{externalHost}. Si aggiorna con Aggiorna.";
            return ServiceState.Running;
        }

        toolTip = cli == ServiceState.WindowOnly
            ? $"{name}: console aperta, host non (ancora) attivo"
            : $"{name}: spento";

        return cli;
    }
}

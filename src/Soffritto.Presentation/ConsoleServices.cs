using System.Diagnostics;

namespace Soffritto.Presentation;

public enum ServiceState
{
    Stopped,

    /// <summary>La console aperta dall'host c'e', il processo vero non ancora (build, install) o non piu'.</summary>
    WindowOnly,

    Running,
}

/// <summary>
/// Servizi di sviluppo in console staccate (Kestrel, host del tunnel, dev server, app): lo stato
/// si ricava dai processi della macchina, non da cio' che l'host ricorda, cosi' vale anche per
/// cio' che e' partito prima o da Visual Studio. Ogni console aperta porta nel titolo un
/// marcatore (es. "AMBIENTI kestrel-1") che la riconosce anche dopo un riavvio dell'host.
/// </summary>
public static class ConsoleServices
{
    /// <summary>Il marcatore nel titolo della console di un servizio, es. "AMBIENTI kestrel-1".</summary>
    public static string Marker(string service, string key) => $"AMBIENTI {service}-{key}";

    /// <summary>Running se c'e' un processo riconosciuto, WindowOnly se c'e' solo la console marcata.</summary>
    public static ServiceState StateOf(IReadOnlyList<ProcessRow> scan, string marker, Func<ProcessRow, bool> isAnchor)
    {
        if (scan.Any(isAnchor))
        {
            return ServiceState.Running;
        }

        return scan.Any(p => IsConsole(p, marker)) ? ServiceState.WindowOnly : ServiceState.Stopped;
    }

    // Prima le console marcate (il loro albero comprende tutto), poi i processi riconosciuti
    // partiti in altro modo.
    public static IEnumerable<int> Pids(IReadOnlyList<ProcessRow> scan, string marker, Func<ProcessRow, bool> isAnchor)
        => scan.Where(p => IsConsole(p, marker)).Concat(scan.Where(isAnchor)).Select(p => p.ProcessId).Distinct();

    /// <summary>
    /// Apre una console staccata che esegue <paramref name="command"/>. False se e' partita
    /// dentro il job dell'host e quindi si chiudera' con lui.
    /// </summary>
    // /k lascia la console aperta quando il comando finisce: un errore di build o di avvio resta
    // leggibile invece di sparire con la finestra.
    // La console nasce da un cmd intermedio che esce subito: resta senza un genitore vivo, e chi
    // chiude l'host con tutto il suo albero di processi (un client MCP che riavvia il server) non
    // la raggiunge. Il breakaway dal job da solo non basta contro una chiusura per albero.
    public static bool StartConsole(string workingDirectory, string marker, string command)
        => DetachedLauncher.Start(
            $"cmd.exe /d /s /c \"start \"\" cmd.exe /k \"title {marker} && {command}\"\"",
            workingDirectory,
            newConsole: false,
            hidden: true);

    /// <summary>Chiude ogni processo indicato con tutto il suo albero.</summary>
    public static void Stop(IEnumerable<int> pids)
    {
        foreach (var pid in pids.ToList())
        {
            using var taskkill = Process.Start(new ProcessStartInfo("taskkill.exe", $"/PID {pid} /T /F")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            taskkill?.WaitForExit(10_000);
        }
    }

    /// <summary>Colore del pallino di stato: verde acceso, arancio solo console aperta, grigio spento.</summary>
    public static string ColorOf(ServiceState state) => state switch
    {
        ServiceState.Running => StatusBadge.OkColor,
        ServiceState.WindowOnly => StatusBadge.WarningColor,
        _ => StatusBadge.OffColor,
    };

    public static string Describe(ServiceState state) => state switch
    {
        ServiceState.Running => "acceso",
        ServiceState.WindowOnly => "console aperta, processo non (ancora) attivo",
        _ => "spento",
    };

    public static bool Is(ProcessRow p, string name) => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase);

    public static bool Under(string text, string folder)
        => text.Contains(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    private static bool IsConsole(ProcessRow p, string marker)
        => Is(p, "cmd.exe") && p.CommandLine.Contains(marker + " ", StringComparison.OrdinalIgnoreCase);
}

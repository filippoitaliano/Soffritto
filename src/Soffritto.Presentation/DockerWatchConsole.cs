using Soffritto.Core.Docker;

namespace Soffritto.Presentation;

/// <summary>La console del Compose Watch del web di un ambiente Docker.</summary>
public static class DockerWatchConsole
{
    public const string Service = "watch-web";

    /// <summary>Il progetto compose senza "soffritto-": "11", o "&lt;4 cifre del tag&gt;-11" con la cartella dati sovrascritta.</summary>
    public static string Key(DockerSlot slot) => DockerNames.KeyOf(slot.Project);

    public static string Marker(DockerSlot slot) => ConsoleServices.Marker(Service, Key(slot));

    public static ServiceState StateOf(IReadOnlyList<ProcessRow> scan, DockerSlot slot)
        => ConsoleServices.StateOf(scan, Marker(slot), p => IsWatch(p, slot));

    /// <summary>
    /// Apre la console del watch. Con il watch acceso non fa nulla; con la sola console rimasta
    /// (watch uscito) la chiude e ne apre una nuova. Chi chiama passa una lettura fresca dei processi.
    /// </summary>
    public static string Start(IReadOnlyList<ProcessRow> scan, DockerSlot slot)
    {
        switch (StateOf(scan, slot))
        {
            case ServiceState.Running:
                return "watch gia' acceso";
            case ServiceState.WindowOnly:
                ConsoleServices.Stop(ConsoleServices.Pids(scan, Marker(slot), p => IsWatch(p, slot)));
                break;
        }

        ConsoleServices.StartConsole(slot.ComposeFolder, Marker(slot), DockerEnvironmentProvisioner.WatchCommand(slot));
        return "watch del web avviato";
    }

    /// <summary>Chiude la console del watch e il suo processo; chi chiama passa una lettura fresca dei processi.</summary>
    public static string Stop(IReadOnlyList<ProcessRow> scan, DockerSlot slot)
    {
        ConsoleServices.Stop(ConsoleServices.Pids(scan, Marker(slot), p => IsWatch(p, slot)));
        return "watch del web fermato";
    }

    /// <summary>
    /// Prima di distruggere l'ambiente chiude ogni sua console (watch del web, log dei servizi) e il
    /// processo del watch: una console con /k resta aperta con la cartella dello slot come cartella
    /// di lavoro e ne impedisce la cancellazione. Chi chiama passa una lettura fresca dei processi.
    /// </summary>
    public static string StopAll(IReadOnlyList<ProcessRow> scan, DockerSlot slot)
    {
        var consoles = scan.Where(p => IsSlotConsole(p, slot)).Select(p => p.ProcessId).ToList();
        ConsoleServices.Stop(consoles.Concat(scan.Where(p => IsWatch(p, slot)).Select(p => p.ProcessId)).Distinct());
        return consoles.Count == 0 ? "nessuna console dell'ambiente aperta" : $"console dell'ambiente chiuse: {consoles.Count}";
    }

    /// <summary>Vero per le console e il watch che <see cref="StopAll"/> chiude da solo.</summary>
    public static bool IsClosedByStopAll(ProcessRow p, DockerSlot slot) => IsSlotConsole(p, slot) || IsWatch(p, slot);

    // Le console del watch e dei log eseguono docker compose -p <progetto> sotto un titolo AMBIENTI.
    private static bool IsSlotConsole(ProcessRow p, DockerSlot slot)
        => ConsoleServices.Is(p, "cmd.exe")
            && p.CommandLine.Contains("title AMBIENTI ", StringComparison.Ordinal)
            && p.CommandLine.Contains($"-p {slot.Project} ", StringComparison.Ordinal);

    // docker.exe lancia il plugin docker-compose.exe con gli stessi argomenti: e' lui il processo del watch.
    private static bool IsWatch(ProcessRow p, DockerSlot slot)
        => ConsoleServices.Is(p, "docker-compose.exe")
            && p.CommandLine.Contains(" watch", StringComparison.OrdinalIgnoreCase)
            && p.CommandLine.Contains($"-p {slot.Project} ", StringComparison.Ordinal);
}

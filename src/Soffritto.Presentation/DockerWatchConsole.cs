using Soffritto.Core.Docker;

namespace Soffritto.Presentation;

/// <summary>The Compose Watch console for the web service of a Docker environment.</summary>
public static class DockerWatchConsole
{
    public const string Service = "watch-web";

    /// <summary>The compose project without "soffritto-": "11", or "&lt;4 tag digits&gt;-11" when the data folder is overridden.</summary>
    public static string Key(DockerSlot slot) => DockerNames.KeyOf(slot.Project);

    public static string Marker(DockerSlot slot) => ConsoleServices.Marker(Service, Key(slot));

    public static ServiceState StateOf(IReadOnlyList<ProcessRow> scan, DockerSlot slot)
        => ConsoleServices.StateOf(scan, Marker(slot), p => IsWatch(p, slot));

    /// <summary>
    /// Opens the watch console. With the watch running it does nothing; with only the console left
    /// (watch exited) it closes it and opens a new one. The caller passes a fresh process scan.
    /// </summary>
    public static string Start(IReadOnlyList<ProcessRow> scan, DockerSlot slot)
    {
        switch (StateOf(scan, slot))
        {
            case ServiceState.Running:
                return "watch already running";
            case ServiceState.WindowOnly:
                ConsoleServices.Stop(ConsoleServices.Pids(scan, Marker(slot), p => IsWatch(p, slot)));
                break;
        }

        ConsoleServices.StartConsole(slot.ComposeFolder, Marker(slot), DockerEnvironmentProvisioner.WatchCommand(slot));
        return "web watch started";
    }

    /// <summary>Closes the watch console and its process; the caller passes a fresh process scan.</summary>
    public static string Stop(IReadOnlyList<ProcessRow> scan, DockerSlot slot)
    {
        ConsoleServices.Stop(ConsoleServices.Pids(scan, Marker(slot), p => IsWatch(p, slot)));
        return "web watch stopped";
    }

    /// <summary>
    /// Before the environment is destroyed, closes all its consoles (web watch, service logs) and the
    /// watch process: a /k console stays open with the slot folder as its working directory
    /// and prevents it from being deleted. The caller passes a fresh process scan.
    /// </summary>
    public static string StopAll(IReadOnlyList<ProcessRow> scan, DockerSlot slot)
    {
        var consoles = scan.Where(p => IsSlotConsole(p, slot)).Select(p => p.ProcessId).ToList();
        ConsoleServices.Stop(consoles.Concat(scan.Where(p => IsWatch(p, slot)).Select(p => p.ProcessId)).Distinct());
        return consoles.Count == 0 ? "no environment consoles open" : $"environment consoles closed: {consoles.Count}";
    }

    /// <summary>True for the consoles and the watch that <see cref="StopAll"/> closes by itself.</summary>
    public static bool IsClosedByStopAll(ProcessRow p, DockerSlot slot) => IsSlotConsole(p, slot) || IsWatch(p, slot);

    // The watch and log consoles run docker compose -p <project> under an AMBIENTI title.
    private static bool IsSlotConsole(ProcessRow p, DockerSlot slot)
        => ConsoleServices.Is(p, "cmd.exe")
            && p.CommandLine.Contains("title AMBIENTI ", StringComparison.Ordinal)
            && p.CommandLine.Contains($"-p {slot.Project} ", StringComparison.Ordinal);

    // docker.exe launches the docker-compose.exe plugin with the same arguments: that is the watch process.
    private static bool IsWatch(ProcessRow p, DockerSlot slot)
        => ConsoleServices.Is(p, "docker-compose.exe")
            && p.CommandLine.Contains(" watch", StringComparison.OrdinalIgnoreCase)
            && p.CommandLine.Contains($"-p {slot.Project} ", StringComparison.Ordinal);
}

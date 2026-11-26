using System.Diagnostics;

namespace Soffritto.Presentation;

public enum ServiceState
{
    Stopped,

    /// <summary>The console opened by the host is there, the actual process is not yet (build, install) or no longer.</summary>
    WindowOnly,

    Running,
}

/// <summary>
/// Development services in detached consoles (Kestrel, tunnel host, dev server, app): the state
/// is derived from the machine's processes, not from what the host remembers, so it also holds for
/// whatever was started earlier or from Visual Studio. Every opened console carries a marker in its
/// title (e.g. "AMBIENTI kestrel-1") that identifies it even after the host restarts.
/// </summary>
public static class ConsoleServices
{
    /// <summary>The marker in the console title of a service, e.g. "AMBIENTI kestrel-1".</summary>
    public static string Marker(string service, string key) => $"AMBIENTI {service}-{key}";

    /// <summary>Running if a recognised process exists, WindowOnly if only the marked console exists.</summary>
    public static ServiceState StateOf(IReadOnlyList<ProcessRow> scan, string marker, Func<ProcessRow, bool> isAnchor)
    {
        if (scan.Any(isAnchor))
        {
            return ServiceState.Running;
        }

        return scan.Any(p => IsConsole(p, marker)) ? ServiceState.WindowOnly : ServiceState.Stopped;
    }

    // Marked consoles first (their tree covers everything), then recognised processes
    // started some other way.
    public static IEnumerable<int> Pids(IReadOnlyList<ProcessRow> scan, string marker, Func<ProcessRow, bool> isAnchor)
        => scan.Where(p => IsConsole(p, marker)).Concat(scan.Where(isAnchor)).Select(p => p.ProcessId).Distinct();

    /// <summary>
    /// Opens a detached console that runs <paramref name="command"/>. False if it started
    /// inside the host's job and will therefore close along with it.
    /// </summary>
    // /k keeps the console open when the command ends: a build or startup error stays
    // readable instead of vanishing with the window.
    // The console is spawned by an intermediate cmd that exits immediately: it is left without a live
    // parent, so whoever kills the host along with its whole process tree (an MCP client restarting
    // the server) does not reach it. Breaking away from the job alone is not enough against a tree kill.
    public static bool StartConsole(string workingDirectory, string marker, string command)
        => DetachedLauncher.Start(
            $"cmd.exe /d /s /c \"start \"\" cmd.exe /k \"title {marker} && {command}\"\"",
            workingDirectory,
            newConsole: false,
            hidden: true);

    /// <summary>Kills every given process along with its whole tree.</summary>
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

    /// <summary>Color of the status dot: green running, orange console only, grey stopped.</summary>
    public static string ColorOf(ServiceState state) => state switch
    {
        ServiceState.Running => StatusBadge.OkColor,
        ServiceState.WindowOnly => StatusBadge.WarningColor,
        _ => StatusBadge.OffColor,
    };

    public static string Describe(ServiceState state) => state switch
    {
        ServiceState.Running => "running",
        ServiceState.WindowOnly => "console open, process not running (yet)",
        _ => "stopped",
    };

    public static bool Is(ProcessRow p, string name) => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase);

    public static bool Under(string text, string folder)
        => text.Contains(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    private static bool IsConsole(ProcessRow p, string marker)
        => Is(p, "cmd.exe") && p.CommandLine.Contains(marker + " ", StringComparison.OrdinalIgnoreCase);
}

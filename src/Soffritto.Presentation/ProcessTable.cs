using System.Management;
using System.Runtime.Versioning;

namespace Soffritto.Presentation;

public sealed record ProcessRow(int ProcessId, string Name, string CommandLine, string ExecutablePath);

/// <summary>Snapshot of only the processes the tools can recognise (console, node, devtunnel, Kestrel).</summary>
public static class ProcessTable
{
    /// <param name="apiProcess">The backend API process (e.g. MyApp.Web.Api.exe); empty if not configured.</param>
    public static IReadOnlyList<ProcessRow> Read(string apiProcess) => Read([apiProcess]);

    /// <param name="apiProcesses">The API processes of all backends; empty ones are ignored.</param>
    public static IReadOnlyList<ProcessRow> Read(IEnumerable<string> apiProcesses)
        => OperatingSystem.IsWindows() ? ReadWindows(apiProcesses) : [];

    /// <summary>All the machine's processes, for finding who is holding a folder open.</summary>
    public static IReadOnlyList<ProcessRow> ReadAll()
        => OperatingSystem.IsWindows() ? Query("SELECT ProcessId, Name, CommandLine, ExecutablePath FROM Win32_Process") : [];

    [SupportedOSPlatform("windows")]
    private static List<ProcessRow> ReadWindows(IEnumerable<string> apiProcesses)
    {
        var names = new List<string> { "cmd.exe", "node.exe", "devtunnel.exe", "docker-compose.exe" };
        // The name ends up inside the WQL query: a single quote would break it.
        names.AddRange(apiProcesses
            .Where(p => p.Length > 0 && !p.Contains('\''))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(p => !names.Contains(p, StringComparer.OrdinalIgnoreCase)));

        return Query("SELECT ProcessId, Name, CommandLine, ExecutablePath FROM Win32_Process WHERE "
            + string.Join(" OR ", names.Select(n => $"Name = '{n}'")));
    }

    [SupportedOSPlatform("windows")]
    private static List<ProcessRow> Query(string query)
    {
        using var searcher = new ManagementObjectSearcher(query);
        using var results = searcher.Get();

        var rows = new List<ProcessRow>();
        foreach (var p in results.Cast<ManagementObject>())
        {
            rows.Add(new ProcessRow(
                Convert.ToInt32(p["ProcessId"]),
                p["Name"] as string ?? string.Empty,
                p["CommandLine"] as string ?? string.Empty,
                p["ExecutablePath"] as string ?? string.Empty));
        }

        return rows;
    }
}

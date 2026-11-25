using System.Management;
using System.Runtime.Versioning;

namespace Soffritto.Presentation;

public sealed record ProcessRow(int ProcessId, string Name, string CommandLine, string ExecutablePath);

/// <summary>Fotografia dei soli processi che gli strumenti sanno riconoscere (console, node, devtunnel, Kestrel).</summary>
public static class ProcessTable
{
    /// <param name="apiProcess">Il processo dell'API del backend (es. MyApp.Web.Api.exe); vuoto se non configurato.</param>
    public static IReadOnlyList<ProcessRow> Read(string apiProcess) => Read([apiProcess]);

    /// <param name="apiProcesses">I processi delle API di tutti i backend; i vuoti si ignorano.</param>
    public static IReadOnlyList<ProcessRow> Read(IEnumerable<string> apiProcesses)
        => OperatingSystem.IsWindows() ? ReadWindows(apiProcesses) : [];

    /// <summary>Tutti i processi della macchina, per chi cerca chi tiene aperta una cartella.</summary>
    public static IReadOnlyList<ProcessRow> ReadAll()
        => OperatingSystem.IsWindows() ? Query("SELECT ProcessId, Name, CommandLine, ExecutablePath FROM Win32_Process") : [];

    [SupportedOSPlatform("windows")]
    private static List<ProcessRow> ReadWindows(IEnumerable<string> apiProcesses)
    {
        var names = new List<string> { "cmd.exe", "node.exe", "devtunnel.exe", "docker-compose.exe" };
        // Il nome finisce dentro la query WQL: un apice la romperebbe.
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

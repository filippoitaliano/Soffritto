using Soffritto.Core.Docker;

namespace Soffritto.Presentation;

/// <summary>A program that names an environment folder in its command line.</summary>
public sealed record FolderHolder(int ProcessId, string Name, string Folder)
{
    public override string ToString() => $"{Name} (pid {ProcessId}) on {Folder}";
}

/// <summary>
/// The programs open on a folder of a Docker environment (worktree or slot folder), read
/// from the command line. An editor open on a worktree makes “git worktree remove” fail halfway:
/// git deletes the files and the registration but not the folder, and the environment is left with neither a worktree nor its entry removed.
/// </summary>
public static class DockerFolderHolders
{
    /// <summary>The programs on the slot folders; excluding this process and the consoles that Destroy closes by itself.</summary>
    public static IReadOnlyList<FolderHolder> Find(IReadOnlyList<ProcessRow> scan, DockerSlot slot)
    {
        var folders = slot.Repositories.Select(r => r.Path).Append(slot.ComposeFolder)
            .Where(f => f.Length > 0)
            .Select(f => Path.TrimEndingDirectorySeparator(Path.GetFullPath(f)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return scan
            .Where(p => p.ProcessId != Environment.ProcessId && !DockerWatchConsole.IsClosedByStopAll(p, slot))
            .Select(p => (Process: p, Folder: folders.FirstOrDefault(f => Mentions(p.CommandLine, f))))
            .Where(h => h.Folder is not null)
            .Select(h => new FolderHolder(h.Process.ProcessId, h.Process.Name, h.Folder!))
            .ToList();
    }

    /// <summary>
    /// Kills, with their children, the confirmed programs that are still on the slot folders: a pid
    /// that exited in the meantime and was reused by another program is left alone. The caller passes a fresh process scan.
    /// </summary>
    public static string Stop(IReadOnlyList<ProcessRow> scan, DockerSlot slot, IReadOnlyList<FolderHolder> confirmed)
    {
        var still = Find(scan, slot).Where(h => confirmed.Any(c => c.ProcessId == h.ProcessId && c.Name == h.Name)).ToList();
        ConsoleServices.Stop(still.Select(h => h.ProcessId));
        return still.Count == 0
            ? "no programs to close on the environment folders"
            : $"programs closed on the environment folders: {string.Join("; ", still)}";
    }

    // The folder must end there: frontend-5 is not frontend-50.
    private static bool Mentions(string commandLine, string folder)
    {
        var line = commandLine.Replace('/', '\\');
        for (var at = line.IndexOf(folder, StringComparison.OrdinalIgnoreCase); at >= 0; at = line.IndexOf(folder, at + 1, StringComparison.OrdinalIgnoreCase))
        {
            var end = at + folder.Length;
            if (end == line.Length || line[end] is '\\' or '"' or '\'' or ' ')
            {
                return true;
            }
        }

        return false;
    }
}

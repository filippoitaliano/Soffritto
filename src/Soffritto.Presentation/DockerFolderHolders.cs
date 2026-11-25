using Soffritto.Core.Docker;

namespace Soffritto.Presentation;

/// <summary>Un programma che nomina una cartella dell'ambiente nella riga di comando.</summary>
public sealed record FolderHolder(int ProcessId, string Name, string Folder)
{
    public override string ToString() => $"{Name} (pid {ProcessId}) su {Folder}";
}

/// <summary>
/// I programmi aperti su una cartella di un ambiente Docker (worktree o cartella dello slot), letti
/// dalla riga di comando. Un editor aperto su un worktree fa fallire «git worktree remove» a meta':
/// git cancella file e registrazione ma non la cartella, e l'ambiente resta senza worktree ne' voce tolta.
/// </summary>
public static class DockerFolderHolders
{
    /// <summary>I programmi sulle cartelle dello slot; esclusi questo processo e le console che Distruggi chiude da solo.</summary>
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
    /// Termina, con i figli, i programmi confermati che sono ancora sulle cartelle dello slot: un pid
    /// uscito nel frattempo e riusato da un altro programma non si tocca. Chi chiama passa una lettura fresca dei processi.
    /// </summary>
    public static string Stop(IReadOnlyList<ProcessRow> scan, DockerSlot slot, IReadOnlyList<FolderHolder> confirmed)
    {
        var still = Find(scan, slot).Where(h => confirmed.Any(c => c.ProcessId == h.ProcessId && c.Name == h.Name)).ToList();
        ConsoleServices.Stop(still.Select(h => h.ProcessId));
        return still.Count == 0
            ? "nessun programma da chiudere sulle cartelle dell'ambiente"
            : $"programmi chiusi sulle cartelle dell'ambiente: {string.Join("; ", still)}";
    }

    // La cartella deve finire li': frontend-5 non e' frontend-50.
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

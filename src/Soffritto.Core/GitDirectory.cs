using System.Collections.Concurrent;

namespace Soffritto.Core;

/// <summary>
/// Dove sta la cartella git di un working tree: ".git" e' una cartella in un clone normale e un
/// file "gitdir: ..." in un worktree. Da qui anche il lock che serializza il git di rete dei
/// worktree che condividono lo stesso clone sorgente.
/// </summary>
public static class GitDirectory
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> locks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Vero se la cartella ha un ".git", cartella o file.</summary>
    public static bool Exists(string root)
    {
        var git = Path.Combine(root, ".git");
        return Directory.Exists(git) || File.Exists(git);
    }

    /// <summary>Vero se ".git" e' un file: la cartella e' un worktree di un altro clone.</summary>
    public static bool IsWorktree(string root) => File.Exists(Path.Combine(root, ".git"));

    /// <summary>La cartella git del working tree (".git", o quella indicata dal file "gitdir:"); null se non c'e'.</summary>
    public static string? Resolve(string root)
    {
        var git = Path.Combine(root, ".git");
        if (Directory.Exists(git))
        {
            return git;
        }

        try
        {
            if (File.Exists(git)
                && File.ReadLines(git).FirstOrDefault() is { } line
                && line.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase))
            {
                var target = line["gitdir:".Length..].Trim();
                return Normalize(Path.IsPathRooted(target) ? target : Path.Combine(root, target));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    /// <summary>La cartella comune di un worktree (il clone sorgente, da "commondir"); null per un clone normale.</summary>
    public static string? Common(string root)
    {
        if (Resolve(root) is not { } gitDirectory)
        {
            return null;
        }

        try
        {
            var file = Path.Combine(gitDirectory, "commondir");
            if (File.Exists(file) && File.ReadLines(file).FirstOrDefault()?.Trim() is { Length: > 0 } common)
            {
                return Normalize(Path.IsPathRooted(common) ? common : Path.Combine(gitDirectory, common));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    /// <summary>
    /// Il lock del clone a cui appartiene la cartella: lo stesso per un clone bare e per tutti i
    /// suoi worktree, cosi' fetch, pull e comandi worktree sullo stesso clone non si sovrappongono.
    /// </summary>
    public static SemaphoreSlim LockOf(string root)
    {
        var key = Common(root) ?? Resolve(root) ?? Normalize(root);
        return locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}

using System.Collections.Concurrent;

namespace Soffritto.Core;

/// <summary>
/// Where a working tree's git folder is: ".git" is a folder in a normal clone and a
/// "gitdir: ..." file in a worktree. This also provides the lock that serializes network git
/// operations of worktrees sharing the same source clone.
/// </summary>
public static class GitDirectory
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> locks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True if the folder has a ".git", folder or file.</summary>
    public static bool Exists(string root)
    {
        var git = Path.Combine(root, ".git");
        return Directory.Exists(git) || File.Exists(git);
    }

    /// <summary>True if ".git" is a file: the folder is a worktree of another clone.</summary>
    public static bool IsWorktree(string root) => File.Exists(Path.Combine(root, ".git"));

    /// <summary>The working tree's git folder (".git", or the one pointed to by the "gitdir:" file); null if there is none.</summary>
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

    /// <summary>A worktree's common folder (the source clone, from "commondir"); null for a normal clone.</summary>
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
    /// The lock of the clone the folder belongs to: the same for a bare clone and all of its
    /// worktrees, so fetch, pull and worktree commands on the same clone never overlap.
    /// </summary>
    public static SemaphoreSlim LockOf(string root)
    {
        var key = Common(root) ?? Resolve(root) ?? Normalize(root);
        return locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}

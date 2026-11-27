using System.Security.Cryptography;
using System.Text;
using Soffritto.Core.Configuration;

namespace Soffritto.Core;

/// <summary>
/// The cross-process lock of a repository's write actions (pull, branch switch, branch cleanup,
/// database update): the app and the MCP server take it, so they never act on the same clone
/// at the same time. One lock file per folder, in the data folder.
/// </summary>
public static class RepositoryLock
{
    public static string PathOf(string repositoryRoot)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot)).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..16];
        return Path.Combine(ConfigurationFiles.DataFolder, "locks", $"repository-{hash}.lock");
    }

    /// <summary>The lock if it is free; null if another process (app or MCP server) is acting on the repository.</summary>
    public static CrossProcessLock? TryAcquire(string repositoryRoot) => CrossProcessLock.TryAcquire(PathOf(repositoryRoot));

    public static string BusyMessage(string repositoryRoot)
        => $"another process (the app or an MCP session) is running an action on {Path.GetFileName(Path.TrimEndingDirectorySeparator(repositoryRoot))}: wait for it to finish";
}

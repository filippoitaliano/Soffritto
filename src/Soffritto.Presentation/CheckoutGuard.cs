using Soffritto.Core;

namespace Soffritto.Presentation;

/// <summary>
/// The checks before moving a repository onto another branch for an environment's reset or
/// checkout (app and MCP server): git readable, fetch succeeded, no git operation halfway
/// through, not detached, no local changes, the branch exists.
/// </summary>
public static class CheckoutGuard
{
    /// <summary>The first reason the repository cannot switch to <paramref name="branch"/>; null if it can.</summary>
    public static string? Problem(string repositoryRoot, string branch, GitStatus status)
        => status.Error
            ?? (status.FetchError is { } fetchError ? $"fetch failed ({fetchError})" : null)
            ?? GitStatusReader.ReadOperationInProgress(repositoryRoot)
            ?? (status.IsDetached ? "detached HEAD" : null)
            ?? (status.DirtyFiles > 0 ? $"{status.DirtyFiles} modified or untracked files" : null)
            ?? (!status.AllBranches.Contains(branch, StringComparer.OrdinalIgnoreCase) ? $"branch {branch} does not exist" : null);
}

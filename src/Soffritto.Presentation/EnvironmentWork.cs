using Soffritto.Core;
using Soffritto.Core.Configuration;

namespace Soffritto.Presentation;

/// <summary>
/// The PRs of a work item, one per repository, with the branches “Open in” would move an
/// environment onto. <see cref="CanMatch"/> is false when two PRs of the same repository are on
/// different branches, or when none belongs to a configured repository.
/// </summary>
public sealed record WorkGroup(
    PullRequestWorkItem? WorkItem,
    IReadOnlyList<PullRequestSummary> PullRequests,
    IReadOnlyList<(string Repository, string Branch)> Targets,
    IReadOnlyList<string> Ambiguous)
{
    public bool CanMatch => Ambiguous.Count == 0 && Targets.Count > 0;
}

/// <summary>A repository of an environment as the match sees it: its server name and its current branch, if the clone is there.</summary>
public sealed record WorkRepository(string RepositoryName, bool Present, string? Branch);

/// <summary>
/// Which work an environment is on: the rule of the environment tag on the PR panel's cards,
/// shared by the app (the tag and its mirror on the environment column) and the MCP server.
/// </summary>
public static class EnvironmentWork
{
    /// <summary>The repository's name on the server: <c>repositoryName</c> if set, otherwise its id.</summary>
    public static string RepositoryNameOf(RepositoryConfiguration repository)
        => repository.RepositoryName is { Length: > 0 } name ? name : repository.Id;

    /// <summary>The type of the first catalog entry with that server name; null if none.</summary>
    public static RepositoryType? TypeOf(AppConfiguration configuration, string repository)
        => configuration.Repositories
            .Where(r => string.Equals(RepositoryNameOf(r), repository, StringComparison.OrdinalIgnoreCase))
            .Select(r => (RepositoryType?)r.Type)
            .FirstOrDefault();

    /// <summary>The targets of a work item's PRs: the source branches of the PRs of configured repositories.</summary>
    public static WorkGroup Group(PullRequestWorkItem? workItem, IReadOnlyList<PullRequestSummary> prs, Func<string, RepositoryType?> typeOf)
    {
        var branches = prs
            .Where(pr => typeOf(pr.Repository) is not null && pr.SourceBranch.Length > 0)
            .Select(pr => (pr.Repository, pr.SourceBranch))
            .Distinct()
            .ToList();

        // Two PRs of the same repository on different branches: the user decides which one goes in the clone.
        var ambiguous = branches
            .GroupBy(b => b.Repository, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        return new WorkGroup(workItem, prs, branches, ambiguous);
    }

    /// <summary>The PRs grouped by work item (those without one in a single group), in reading order.</summary>
    public static IReadOnlyList<WorkGroup> Groups(IEnumerable<PullRequestSummary> prs, Func<string, RepositoryType?> typeOf)
        => prs
            .GroupBy(pr => pr.WorkItem?.Id ?? 0)
            .Select(g => Group(g.First().WorkItem, g.ToList(), typeOf))
            .ToList();

    /// <summary>
    /// The environment is exactly where “Open in” would take it with these branches: every listed
    /// repository has its clone on its branch, and the other present clones, generic ones included,
    /// are on <see cref="GitConventions.DevelopmentBranch"/>.
    /// </summary>
    public static bool IsOnBranches(IEnumerable<WorkRepository> repositories, IReadOnlyList<(string Repository, string Branch)> targets)
    {
        if (targets.Count == 0)
        {
            return false;
        }

        foreach (var repository in repositories)
        {
            var requested = targets
                .Where(t => string.Equals(t.Repository, repository.RepositoryName, StringComparison.OrdinalIgnoreCase))
                .Select(t => t.Branch)
                .FirstOrDefault();
            if (!repository.Present)
            {
                if (requested is not null)
                {
                    return false;
                }

                continue;
            }

            if (!string.Equals(repository.Branch, requested ?? GitConventions.DevelopmentBranch, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}

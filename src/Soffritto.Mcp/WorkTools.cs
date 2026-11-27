using System.ComponentModel;
using ModelContextProtocol.Server;
using Soffritto.Core;
using Soffritto.Core.AzureDevOps;
using Soffritto.Core.Configuration;
using Soffritto.Presentation;

namespace Soffritto.Mcp;

/// <summary>
/// Which work item an environment is on: the environment tag of the PR panel's cards in the app,
/// with the same PRs (the active ones the user opened or reviews) and the same rule
/// (<see cref="EnvironmentWork"/>).
/// </summary>
[McpServerToolType]
public static class WorkTools
{
    // A slow or unreachable Azure DevOps must not hold back the status of an environment.
    private static readonly TimeSpan ReadLimit = TimeSpan.FromSeconds(10);

    internal const string Rule = "an environment is on a work item when every repository with one of its PRs is on the PR's source branch and the other repositories are on the main development branch (the state \"Open in\" leaves); only the active PRs the user opened or reviews are considered, mine and to review separately, as in the app's PR panel";

    [McpServerTool(Name = "environments_work", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("For every environment, local and Docker, the work items (or PRs without one) whose branches it sits on: the environment tag of the cards in the app's PR panel. It reads the active PRs the user opened or reviews from Azure DevOps once, and the current branch of every clone or worktree. Takes a few seconds.")]
    public static async Task<string> EnvironmentsWork(CancellationToken cancellationToken = default)
    {
        if (EnvironmentTools.LoadConfiguration(out var error) is not { } configuration)
        {
            return EnvironmentTools.Serialize(new { error });
        }

        var groups = await ReadAsync(configuration, cancellationToken).ConfigureAwait(false);
        if (groups.Error is not null)
        {
            return EnvironmentTools.Serialize(new { error = groups.Error });
        }

        var environments = new List<object>();
        foreach (var environment in configuration.Environments)
        {
            var repositories = new List<WorkRepository>();
            foreach (var entry in configuration.RepositoriesOf(environment))
            {
                var present = Directory.Exists(entry.Path);
                var branch = present ? (await GitStatusReader.ReadAsync(entry.Path, false, cancellationToken).ConfigureAwait(false)).Branch : null;
                repositories.Add(new WorkRepository(EnvironmentWork.RepositoryNameOf(entry.Repository), present, branch));
            }

            environments.Add(new
            {
                environment = environment.Number,
                kind = environment.Kind == EnvironmentKind.Docker ? "docker" : "local",
                work = Matching(groups, repositories),
            });
        }

        return EnvironmentTools.Serialize(new { rule = Rule, environments });
    }

    /// <param name="WorkItemBase">The prefix of a work item's URL: collection and project, as the app opens it.</param>
    internal sealed record WorkRead(IReadOnlyList<WorkGroup>? Groups, string? Error, string WorkItemBase = "");

    /// <summary>The PR groups of the panel, mine and to review separately; or why they cannot be read.</summary>
    internal static async Task<WorkRead> ReadAsync(AppConfiguration configuration, CancellationToken cancellationToken)
    {
        using var connection = AzureDevOpsSecrets.Connect(configuration.AzureDevOps, out var problem);
        if (connection is null)
        {
            return new WorkRead(null, problem);
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(ReadLimit);
        try
        {
            var prs = await new PullRequestReader(connection).ReadAsync(limit.Token).ConfigureAwait(false);
            if (prs.Error is not null)
            {
                return new WorkRead(null, $"PRs not read from Azure DevOps: {prs.Error}");
            }

            RepositoryType? TypeOf(string repository) => EnvironmentWork.TypeOf(configuration, repository);
            return new WorkRead(
                [.. EnvironmentWork.Groups(prs.Created, TypeOf), .. EnvironmentWork.Groups(prs.ToReview, TypeOf)],
                null,
                $"{connection.CollectionUrl}/{connection.Project}/_workitems/edit/");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new WorkRead(null, $"Azure DevOps did not answer within {ReadLimit.TotalSeconds:0} s");
        }
    }

    /// <summary>The work items the environment sits on, in the shape of the status tools; a work item in both sections counts once.</summary>
    internal static IReadOnlyList<object> Matching(WorkRead read, IReadOnlyList<WorkRepository> repositories)
        => read.Groups!
            .Where(g => g.CanMatch && EnvironmentWork.IsOnBranches(repositories, g.Targets))
            .DistinctBy(g => g.WorkItem?.Id ?? -g.PullRequests[0].Id)
            .Select(g => (object)new
            {
                workItem = g.WorkItem is { } w ? new { id = w.Id, type = w.Type, title = w.Title, url = $"{read.WorkItemBase}{w.Id}" } : null,
                pullRequests = g.PullRequests.Select(pr => new { id = pr.Id, title = pr.Title, repository = pr.Repository, branch = pr.SourceBranch, url = pr.WebUrl }),
            })
            .ToList();

    /// <summary>The section of environment_status and docker_environment_status: the work items, or why they are not known.</summary>
    internal static object Section(WorkRead read, IReadOnlyList<WorkRepository> repositories)
        => read.Error is not null
            ? new { error = read.Error }
            : new { items = Matching(read, repositories), rule = Rule };
}

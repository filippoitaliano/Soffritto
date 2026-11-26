using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.ProjectSystem.Query;

namespace Soffritto.Vsix;

internal static class SolutionRootResolver
{
    public static async Task<string?> ResolveAsync(
        VisualStudioExtensibility extensibility,
        CancellationToken cancellationToken)
    {
        try
        {
            var solutions = await extensibility.Workspaces()
                .QuerySolutionAsync(query => query.With(solution => solution.Path), cancellationToken)
                .ConfigureAwait(false);

            var path = solutions?.FirstOrDefault()?.Path;

            if (!string.IsNullOrWhiteSpace(path))
            {
                return Path.GetDirectoryName(path);
            }
        }
        catch (Exception)
        {
            // No solution open, or the query is not available in this context.
        }

        return null;
    }
}

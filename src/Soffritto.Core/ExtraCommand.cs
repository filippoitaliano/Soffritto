using Soffritto.Core.Configuration;

namespace Soffritto.Core;

/// <summary>
/// A repository's extra command (<see cref="FrontendConfiguration.ExtraCommand"/>): the same one
/// for the app's button and for the MCP tool.
/// </summary>
public static class ExtraCommand
{
    /// <summary>The repository's configured command; null if it has none.</summary>
    public static string? Of(RepositoryConfiguration repository)
        => repository is FrontendConfiguration { ExtraCommand: var extra } && extra.Trim() is { Length: > 0 } trimmed ? trimmed : null;

    /// <summary>The last word of the command, which names the button (npm run genapi -> genapi).</summary>
    public static string Name(string command) => command.Split(' ', StringSplitOptions.RemoveEmptyEntries)[^1];

    /// <summary>
    /// The line to pass to cmd.exe. In a Docker worktree the dependencies live in the container: if the
    /// worktree has no node_modules, they are installed first.
    /// </summary>
    public static string CommandLine(string command, EnvironmentKind kind)
        => kind == EnvironmentKind.Docker ? $"if exist node_modules\\ ({command}) else (npm ci && {command})" : command;
}

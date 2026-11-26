using System.ComponentModel;
using ModelContextProtocol.Server;
using Soffritto.Core;
using Soffritto.Core.Configuration;

namespace Soffritto.Mcp;

/// <summary>
/// A repository's extra command (the accessory button on its card in the app), on local and
/// Docker environments: it is the only tool that writes into the clones of a local environment.
/// </summary>
[McpServerToolType]
public static class ExtraCommandTools
{
    private const int TimeoutSeconds = 900;

    [McpServerTool(Name = "run_extra_command", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Runs the extra command of a repository in the environment (the extraCommand field of the configuration, the same as the accessory button on the repository's card in the app; list_environments shows it as extraCommand) in the repository folder: the clone for a local environment, the worktree for a Docker environment (where, if node_modules is missing, it installs the dependencies first). It works on local environments too and may rewrite files in the repository. Responds with the result and the output; if it takes longer than the wait, with the operation to follow with operation_status.")]
    public static async Task<string> RunExtraCommand(
        [Description("Environment number, local or Docker.")] int number,
        [Description("Catalog id of the repository (see list_environments); empty = the only repository in the environment that has an extra command.")] string? repository = null,
        [Description("Seconds to wait for completion before responding (0-110): if the command takes longer, the response contains the id to follow with operation_status.")] int waitSeconds = 90,
        CancellationToken cancellationToken = default)
    {
        if (EnvironmentTools.LoadConfiguration(out var error) is not { } configuration)
        {
            return EnvironmentTools.Serialize(new { error });
        }

        if (configuration.Environment(number) is not { } environment)
        {
            return EnvironmentTools.Serialize(new { error = $"environment {number} is not configured" });
        }

        var candidates = configuration.RepositoriesOf(environment)
            .Where(r => ExtraCommand.Of(r.Repository) is not null)
            .ToList();
        var target = string.IsNullOrWhiteSpace(repository)
            ? candidates.Count == 1 ? candidates[0] : null
            : candidates.FirstOrDefault(r => string.Equals(r.Id, repository, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            var available = candidates.Count == 0 ? "none" : string.Join(", ", candidates.Select(r => r.Id));
            return EnvironmentTools.Serialize(new
            {
                error = string.IsNullOrWhiteSpace(repository)
                    ? $"specify 'repository': repositories of environment {number} with an extra command: {available}"
                    : $"{repository} is not a repository of environment {number} with an extra command (with an extra command: {available})",
            });
        }

        if (!Directory.Exists(target.Path))
        {
            return EnvironmentTools.Serialize(new { error = $"folder {target.Path} does not exist" });
        }

        var command = ExtraCommand.Of(target.Repository)!;
        // The server inherits NoDefaultCurrentDirectoryInExePath from Claude Code: with that variable cmd
        // does not look for commands in the current folder, so scripts that call a local .bat
        // (e.g. "cd envs && dev") fail, while they work from the app's button.
        var commandLine = $"set \"NoDefaultCurrentDirectoryInExePath=\" && {ExtraCommand.CommandLine(command, environment.Kind)}";
        var snapshot = await Operations.RunAsync(ExtraCommand.Name(command), number, waitSeconds, async (operation, ct) =>
        {
            operation.Log($"{commandLine} in {target.Path}");
            var outcome = await ProcessRunner.RunAsync(
                "cmd.exe",
                $"/d /s /c \"{commandLine}\"",
                target.Path,
                TimeoutSeconds,
                onOutputLine: operation.Log,
                cancellationToken: ct).ConfigureAwait(false);
            return outcome.Success
                ? (null, new { result = $"{command} succeeded", folder = target.Path })
                : ($"{command} failed (exit {outcome.ExitCode}): {outcome.Tail(10)}", null);
        }, cancellationToken).ConfigureAwait(false);
        return EnvironmentTools.Serialize(snapshot);
    }
}

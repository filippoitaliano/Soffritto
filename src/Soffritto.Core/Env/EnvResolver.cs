using System.Text.RegularExpressions;
using Soffritto.Core.Configuration;
using Soffritto.Core.Docker;

namespace Soffritto.Core.Env;

/// <param name="Local">The variables for the worktree's env file.</param>
/// <param name="Container">The variables for the container's env_file, with "A__B" names.</param>
/// <param name="Warnings">Warnings for the user: names only, never values.</param>
public sealed record EnvResolution(EnvSet Local, EnvSet Container, IReadOnlyList<string> Warnings);

/// <summary>
/// A repository's variables in a slot: constants from the template environment (plus the manual ones),
/// secrets from their source, per-slot values that win. The worktree gets them through the adapter, the
/// container from &lt;slot folder&gt;\&lt;id&gt;.env.
/// </summary>
public static partial class EnvResolver
{
    /// <param name="poolUrls">Repository id → URL of its pool port, for {poolUrl}.</param>
    /// <param name="targetIsNew">
    /// True when the worktree's env file does not exist yet: a variable whose per-slot value
    /// cannot be resolved then keeps the template constant, instead of disappearing from the set.
    /// </param>
    public static EnvResolution Resolve(
        AppConfiguration c,
        EnvironmentRepository entry,
        DockerSlot slot,
        IReadOnlyDictionary<string, string>? poolUrls,
        bool targetIsNew)
    {
        var warnings = new List<string>();
        if (entry.Repository.Env is not { } env)
        {
            return new EnvResolution(new EnvSet(), new EnvSet(), warnings);
        }

        var template = TemplatePathOf(c, entry.Repository);
        if (template is null)
        {
            warnings.Add($"{entry.Id}: the template environment does not include the repository, so no constants or secrets.");
        }

        var constants = new EnvSet();
        if (template is not null && env.Adapter != EnvAdapterKind.Manual)
        {
            var source = Path.Combine(template, env.ConstantsSource.Length > 0 ? env.ConstantsSource : env.Target);
            if (File.Exists(source))
            {
                constants.Merge(EnvAdapters.For(env.Adapter).Read(source));
            }
            else
            {
                warnings.Add($"{entry.Id}: constants not found, {source} does not exist.");
            }
        }

        constants.Merge(new EnvSet(env.Manual.Select(m => new EnvVariable(m.Name, m.Value, EnvClass.Constant, "setup"))));

        var secrets = new EnvSet();
        if (SecretsPathOf(c, entry.Repository) is { } secretsPath)
        {
            if (File.Exists(secretsPath))
            {
                secrets = EnvAdapters.For(env.Secrets!.Adapter).Read(secretsPath).As(EnvClass.SharedSecret, secretsPath);
            }
            else
            {
                warnings.Add($"{entry.Id}: secrets not found, {secretsPath} does not exist.");
            }
        }

        var containerConstants = new EnvSet(constants.Where(v => !env.ContainerExclude.Any(p => p.Length > 0 && v.Name.StartsWith(p, StringComparison.OrdinalIgnoreCase))));
        var local = new EnvSet(constants).Merge(secrets);
        var container = new EnvSet(containerConstants).Merge(secrets);

        foreach (var variable in env.Slot.Where(v => v.Name.Length > 0))
        {
            var (localValue, localMissing) = Substitute(variable.Local, entry, slot, poolUrls);
            if (localValue is not null)
            {
                local.Set(new EnvVariable(variable.Name, localValue, EnvClass.Slot, "slot"));
            }
            else
            {
                // Without a per-slot value the worktree's line stays as it is (even if hand-written):
                // the template constant would overwrite it with another environment's value.
                if (!targetIsNew)
                {
                    local.Remove(variable.Name);
                }

                warnings.Add(localMissing!.StartsWith("{poolUrl", StringComparison.Ordinal)
                    ? PoolHint(variable.Name, entry.Id, PoolOwnerOf(localMissing, entry.Id), slot.Number)
                    : $"{variable.Name} of {entry.Id}: unresolvable per-slot value ({localMissing})");
            }

            if (variable.Container.Length == 0 || localValue is null)
            {
                container.Remove(variable.Name);
                continue;
            }

            var (containerValue, containerMissing) = Substitute(variable.Container, entry, slot, poolUrls);
            if (containerValue is not null)
            {
                container.Set(new EnvVariable(variable.Name, containerValue, EnvClass.Slot, "slot"));
            }
            else
            {
                container.Remove(variable.Name);
                warnings.Add($"{variable.Name} of {entry.Id} is not passed to the container: unresolvable per-slot value ({containerMissing})");
            }
        }

        var containerNames = new EnvSet(container.Select(v => v with { Name = v.Name.Replace(":", "__", StringComparison.Ordinal) }));
        return new EnvResolution(local, containerNames, warnings);
    }

    /// <summary>The warning for a {poolUrl} variable without a pool port: says to assign one to <paramref name="owner"/> and where.</summary>
    public static string PoolHint(string variable, string repository, string owner, int number)
        => $"{variable} of {repository} not set: {owner} of environment {number} has no pool port (turn on \"Tunnel\" in the header of environment {number})";

    // {poolUrl:<id>} → id; {poolUrl} → the repository itself.
    private static string PoolOwnerOf(string placeholder, string repository)
        => placeholder.StartsWith("{" + EnvConfiguration.PoolUrlPrefix, StringComparison.Ordinal)
            ? placeholder[(EnvConfiguration.PoolUrlPrefix.Length + 1)..^1]
            : repository;

    /// <summary>The repository's folder in the template environment; null if it is not there.</summary>
    public static string? TemplatePathOf(AppConfiguration c, RepositoryConfiguration repository)
        => c.TemplateNumber() is { } number && c.Environment(number) is { } template
            ? c.RepositoriesOf(template).FirstOrDefault(r => string.Equals(r.Id, repository.Id, StringComparison.OrdinalIgnoreCase))?.Path
            : null;

    /// <summary>The secrets file: absolute, or relative to the repository in the template environment; null without secrets.</summary>
    public static string? SecretsPathOf(AppConfiguration c, RepositoryConfiguration repository)
        => repository.Env?.Secrets is { Source.Length: > 0 } secrets
            ? Path.IsPathFullyQualified(secrets.Source)
                ? secrets.Source
                : TemplatePathOf(c, repository) is { } template ? Path.Combine(template, secrets.Source) : null
            : null;

    public static string ContainerEnvPath(DockerSlot slot, EnvironmentRepository entry) => Path.Combine(slot.ComposeFolder, $"{entry.Id}.env");

    /// <summary>
    /// Writes the worktree's env file with the repository's adapter and copies the local files
    /// from the template environment. Secrets go only into a target ignored by git, or stay
    /// in their own .bat invoked with @CALL. Returns the log lines (names and counts).
    /// </summary>
    public static async Task<IReadOnlyList<string>> WriteLocalAsync(AppConfiguration c, EnvironmentRepository entry, EnvResolution resolution, CancellationToken ct = default)
    {
        var log = new List<string>();
        if (entry.Repository.Env is { Adapter: not EnvAdapterKind.Manual } env && env.Target.Length > 0)
        {
            var target = Path.Combine(entry.Path, env.Target);
            var set = new EnvSet(resolution.Local);
            var options = new EnvWriteOptions();
            var secretCount = set.Count(v => v.Class == EnvClass.SharedSecret);
            if (secretCount > 0)
            {
                if (env.Adapter == EnvAdapterKind.Bat && env.Secrets?.Adapter == EnvAdapterKind.Bat && SecretsPathOf(c, entry.Repository) is { } call)
                {
                    set.RemoveWhere(v => v.Class == EnvClass.SharedSecret);
                    options = new EnvWriteOptions(SecretsCall: call);
                    log.Add($"{entry.Id}: {secretCount} secrets invoked with @CALL from {call}");
                }
                else if (await SourceCloneRepository.IsIgnoredAsync(entry.Path, env.Target, ct).ConfigureAwait(false) != true)
                {
                    set.RemoveWhere(v => v.Class == EnvClass.SharedSecret);
                    log.Add($"secrets not written: {target} is not ignored by git");
                }
            }

            EnvAdapters.For(env.Adapter).Write(target, set, options);
            log.Add($"{entry.Id}: {target} written ({set.Count} variables, {set.Count(v => v.Class == EnvClass.Slot)} per slot, {set.Count(v => v.Class == EnvClass.SharedSecret)} secrets)");
        }

        var template = TemplatePathOf(c, entry.Repository);
        foreach (var file in entry.Repository.Docker?.LocalFiles ?? [])
        {
            var source = template is null ? null : Path.Combine(template, file);
            if (source is null || !File.Exists(source))
            {
                log.Add($"{entry.Id}: local file {file} not found in the template environment");
                continue;
            }

            var destination = Path.Combine(entry.Path, file);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: true);
            log.Add($"{entry.Id}: local file {file} copied from the template environment");
        }

        return log;
    }

    /// <summary>
    /// Writes &lt;slot folder&gt;\&lt;id&gt;.env for compose (values in single quotes,
    /// literal). Error or null; the error names the variable, never the value.
    /// </summary>
    public static string? WriteContainer(DockerSlot slot, EnvironmentRepository entry, EnvResolution resolution)
    {
        var lines = new List<string>();
        foreach (var variable in resolution.Container)
        {
            if (variable.Value.Contains('\'') || variable.Value.Contains('\n') || variable.Value.Contains('\r'))
            {
                return $"{variable.Name} of {entry.Id}: the value contains a single quote or a line break, which compose's env_file cannot express";
            }

            lines.Add($"{variable.Name}='{variable.Value}'");
        }

        var path = ContainerEnvPath(slot, entry);
        Directory.CreateDirectory(slot.ComposeFolder);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, string.Join("\n", lines) + (lines.Count > 0 ? "\n" : string.Empty));
        File.Move(temporary, path, overwrite: true);
        return null;
    }

    // (value, null) if every placeholder resolves; (null, first missing placeholder) otherwise.
    private static (string? Value, string? Missing) Substitute(string text, EnvironmentRepository entry, DockerSlot slot, IReadOnlyDictionary<string, string>? poolUrls)
    {
        string? missing = null;
        var result = Placeholder().Replace(text, match =>
        {
            var value = ValueOf(match.Value, entry, slot, poolUrls);
            if (value is null)
            {
                missing ??= match.Value;
                return match.Value;
            }

            return value;
        });

        return missing is null ? (result, null) : (null, missing);
    }

    private static string? ValueOf(string placeholder, EnvironmentRepository entry, DockerSlot slot, IReadOnlyDictionary<string, string>? poolUrls)
    {
        var port = slot.PortOf(entry);
        return placeholder switch
        {
            "{n}" => slot.Number.ToString(),
            "{hostPort}" => port?.Host.ToString(),
            "{containerPort}" => port?.Container.ToString(),
            "{apiHostUrl}" => slot.ApiHostUrl,
            "{apiContainerUrl}" => slot.ApiContainerUrl,
            "{hostConnection}" => slot.HostConnectionString,
            "{containerConnection}" => slot.ContainerConnectionString,
            "{poolUrl}" => poolUrls?.GetValueOrDefault(entry.Id),
            _ when placeholder.StartsWith("{" + EnvConfiguration.PoolUrlPrefix, StringComparison.Ordinal)
                => poolUrls?.GetValueOrDefault(placeholder[(EnvConfiguration.PoolUrlPrefix.Length + 1)..^1]),
            _ => null,
        };
    }

    [GeneratedRegex(@"\{[^{}\s]+\}")]
    private static partial Regex Placeholder();
}

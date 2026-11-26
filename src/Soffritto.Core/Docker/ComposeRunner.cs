using System.Text;
using System.Text.Json;
using Soffritto.Core.Configuration;
using Soffritto.Core.Env;

namespace Soffritto.Core.Docker;

/// <summary>
/// The contract between Soffritto and the backend repository's compose file: Docker files in the
/// worktree, slot.env, label override and the slot's docker compose commands.
/// </summary>
public static class ComposeRunner
{
    public const string WebProfile = "web";

    /// <summary>The compose file used with -f: always the one at the root of the backend worktree.</summary>
    public static string ComposePath(DockerSlot slot)
        => Path.Combine(slot.BackendWorktree ?? throw new InvalidOperationException("The slot has no backend."),slot.Backend!.Repository.DockerCompose);

    /// <summary>
    /// The Docker files at the worktree root: a versioned (not ignored) file is used; a missing
    /// or ignored one (an earlier copy) is copied from the fallback and excluded from git in the
    /// clone's common folder. One log line per file, without contents. Error or null.
    /// </summary>
    public static async Task<string?> EnsureDockerFilesAsync(
        string worktree,
        string? fallbackFolder,
        IReadOnlyList<string> files,
        Action<string> log,
        CancellationToken ct = default)
    {
        var isGit = GitDirectory.Exists(worktree);
        if (!isGit)
        {
            log($"{worktree} is not a git worktree: files copied without exclusion");
        }

        foreach (var file in files)
        {
            var target = Path.Combine(worktree, file);
            var present = File.Exists(target);
            if (isGit && present)
            {
                var ignored = await SourceCloneRepository.IsIgnoredAsync(worktree, file, ct).ConfigureAwait(false);
                if (ignored is null)
                {
                    return $"{file}: git check-ignore does not respond in {worktree}";
                }

                if (ignored == false)
                {
                    log($"{file}: using the worktree's own");
                    continue;
                }
            }

            var source = string.IsNullOrWhiteSpace(fallbackFolder) ? null : Path.Combine(fallbackFolder, file);
            if (source is null || !File.Exists(source))
            {
                return present
                    ? $"{file} in the worktree is an ignored copy and {DisplayFallback(fallbackFolder)} does not have it"
                    : $"{file} found neither in the worktree nor in {DisplayFallback(fallbackFolder)}";
            }

            if (isGit && await SourceCloneRepository.ExcludeAsync(worktree, ["/" + file.Replace('\\', '/')], ct).ConfigureAwait(false) is { } excludeError)
            {
                return excludeError;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
                File.Copy(source, target, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return $"{file}: copy from {fallbackFolder} failed ({ex.Message})";
            }

            log($"{file} copied from {fallbackFolder}");
        }

        return null;
    }

    /// <summary>The Docker files of the slot's worktrees: the backend's compose file and Dockerfile, the containerized frontend's Dockerfile.</summary>
    public static async Task<string?> EnsureSlotFilesAsync(AppConfiguration c, DockerSlot slot, Action<string> log, CancellationToken ct = default)
    {
        foreach (var entry in new[] { slot.Backend, slot.Frontend })
        {
            if (entry is null)
            {
                continue;
            }

            var repository = entry.Repository;
            var dockerfile = repository.DockerDockerfile;
            List<string> files = repository.Type == RepositoryType.Backend
                ? [repository.DockerCompose, dockerfile, $"{dockerfile}.dockerignore"]
                : [dockerfile, $"{dockerfile}.dockerignore"];
            if (await EnsureDockerFilesAsync(entry.Path, repository.Docker?.FilesFallback, files, log, ct).ConfigureAwait(false) is { } error)
            {
                return error;
            }
        }

        return null;
    }

    /// <summary>
    /// slot.env with the contract's variables: contexts, Dockerfiles relative to the context, ports,
    /// container env_files and database. No shared secrets; the SQL password is not one.
    /// </summary>
    public static string? WriteSlotEnv(AppConfiguration c, DockerSlot slot)
    {
        if (slot.Backend is not { } backend || slot.PortOf(backend) is not { } apiPort)
        {
            return "the slot has no backend with a published port";
        }

        var sql = c.PrimarySeed()?.Seed?.SqlServer ?? new SqlServerSeedConfiguration();
        var web = slot.Frontend;
        var webPort = web is null ? null : slot.PortOf(web);

        // Compose interpolates the web variables even outside the profile: without a frontend
        // they point to the backend, and the web service stays stopped because the profile is not activated.
        var values = new List<(string Name, string Value)>
        {
            ("API_CONTEXT", Slash(backend.Path)),
            ("API_DOCKERFILE", Slash(backend.Repository.DockerDockerfile)),
            ("API_HOST_PORT", apiPort.Host.ToString()),
            ("API_ENV_FILE", Slash(EnvResolver.ContainerEnvPath(slot, backend))),
            ("WEB_CONTEXT", Slash((web ?? backend).Path)),
            ("WEB_DOCKERFILE", Slash((web ?? backend).Repository.DockerDockerfile)),
            ("WEB_HOST_PORT", (webPort ?? apiPort).Host.ToString()),
            ("WEB_ENV_FILE", Slash(EnvResolver.ContainerEnvPath(slot, web ?? backend))),
            ("DB_HOST_PORT", slot.DatabasePort.ToString()),
            ("DB_IMAGE", sql.Image),
            ("DB_PASSWORD", sql.Password),
            ("DB_MEMORY_LIMIT_MB", sql.MemoryLimitMb.ToString()),
        };

        var text = new StringBuilder();
        foreach (var (name, value) in values)
        {
            if (value.Contains('\'') || value.Contains('\n'))
            {
                return $"{name}: the value contains a single quote or a line break";
            }

            text.Append(name).Append('=').Append(NeedsQuotes(value) ? $"'{value}'" : value).Append('\n');
        }

        try
        {
            Directory.CreateDirectory(slot.ComposeFolder);
            var temporary = slot.SlotEnvPath + ".tmp";
            File.WriteAllText(temporary, text.ToString());
            File.Move(temporary, slot.SlotEnvPath, overwrite: true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"slot.env not written: {ex.Message}";
        }
    }

    /// <summary>
    /// soffritto.override.yaml with the data folder's label on every compose service
    /// (from config --services): Soffritto adds it, even on an old fallback compose file.
    /// </summary>
    public static async Task<string?> WriteOverrideAsync(DockerSlot slot, CancellationToken ct = default)
    {
        var services = await RunRawAsync(slot, $"{Profile(slot)}config --services", includeOverride: false, timeoutSeconds: 60, ct: ct).ConfigureAwait(false);
        if (!services.Success)
        {
            return $"docker compose config --services: {Filter(slot, services.Tail(3))}";
        }

        var names = services.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        var text = new StringBuilder("# Generated by Soffritto: data folder label on every service\nservices:\n");
        foreach (var name in names)
        {
            text.Append($"  {name}:\n    labels:\n      {DockerNames.LabelKey}: \"{DockerNames.DataFolderTag}\"\n");
        }

        try
        {
            Directory.CreateDirectory(slot.ComposeFolder);
            File.WriteAllText(slot.OverridePath, text.ToString());
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"override not written: {ex.Message}";
        }
    }

    /// <summary>
    /// docker compose -p &lt;project&gt; -f &lt;compose&gt; -f &lt;override&gt; --env-file &lt;slot.env&gt;
    /// [--profile web] &lt;subcommand&gt;, from the slot folder. The lines go through a filter
    /// that strips the values of the slot's env_files (a malformed env_file prints them).
    /// </summary>
    public static async Task<ProcessOutcome> RunAsync(
        DockerSlot slot,
        string subcommand,
        bool web = false,
        Action<string>? log = null,
        int timeoutSeconds = 1800,
        CancellationToken ct = default)
    {
        var filter = SecretFilter(slot);
        var outcome = await RunRawAsync(
            slot,
            $"{(web ? $"--profile {WebProfile} " : string.Empty)}{subcommand}",
            includeOverride: true,
            timeoutSeconds,
            log is null ? null : line => log(filter(line)),
            ct).ConfigureAwait(false);
        return outcome with { StandardOutput = filter(outcome.StandardOutput), StandardError = filter(outcome.StandardError) };
    }

    /// <summary>config --quiet with both files: a missing variable gives a clear error. Error or null.</summary>
    public static async Task<string?> ValidateAsync(DockerSlot slot, CancellationToken ct = default)
    {
        var config = await RunAsync(slot, "config --quiet", slot.HasContainerizedFrontend, timeoutSeconds: 60, ct: ct).ConfigureAwait(false);
        return config.Success ? null : $"docker compose config: {config.Tail(3)}";
    }

    /// <summary>
    /// The service's environment as compose resolves it (env_file and the compose file's environment:).
    /// The output contains secrets: it never goes through any log.
    /// </summary>
    public static async Task<(EnvSet? Environment, string? Error)> ResolvedEnvironmentAsync(DockerSlot slot, string service, CancellationToken ct = default)
    {
        var config = await RunRawAsync(slot, $"{Profile(slot)}config --format json", includeOverride: true, timeoutSeconds: 60, ct: ct).ConfigureAwait(false);
        if (!config.Success)
        {
            return (null, $"docker compose config: {Filter(slot, config.Tail(3))}");
        }

        try
        {
            using var json = JsonDocument.Parse(config.StandardOutput);
            if (!json.RootElement.TryGetProperty("services", out var services) || !services.TryGetProperty(service, out var definition))
            {
                return (null, $"the compose file has no service {service}");
            }

            var set = new EnvSet();
            if (definition.TryGetProperty("environment", out var environment) && environment.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in environment.EnumerateObject())
                {
                    var value = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() ?? string.Empty : string.Empty;
                    set.Set(new EnvVariable(property.Name, value, EnvClass.Constant, "compose"));
                }
            }

            return (set, null);
        }
        catch (JsonException)
        {
            return (null, "docker compose config --format json: unreadable output");
        }
    }

    /// <summary>
    /// An existing project must carry this data folder's label on its first
    /// container: otherwise it belongs to another executable (or was created by hand) and is left alone. Error or null.
    /// </summary>
    public static async Task<string?> CheckLabelAsync(DockerSlot slot, CancellationToken ct = default)
    {
        var ps = await DockerCli.RunAsync($"ps -a --filter \"label=com.docker.compose.project={slot.Project}\" --format \"{{{{.ID}}}}\"", timeoutSeconds: 15, ct: ct).ConfigureAwait(false);
        if (!ps.Success)
        {
            return $"{DockerStatusReader.Unreachable}: {ps.Tail(1)}";
        }

        var first = ps.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).FirstOrDefault();
        if (first is null)
        {
            return null;
        }

        var label = await DockerCli.ContainerLabelAsync(first, DockerNames.LabelKey, ct).ConfigureAwait(false);
        return label == DockerNames.DataFolderTag
            ? null
            : $"project {slot.Project} exists but does not belong to this data folder (label {DockerNames.LabelKey} {(string.IsNullOrEmpty(label) ? "missing" : label)}): leaving it alone";
    }

    private static Task<ProcessOutcome> RunRawAsync(
        DockerSlot slot,
        string arguments,
        bool includeOverride,
        int timeoutSeconds,
        Action<string>? onOutputLine = null,
        CancellationToken ct = default)
    {
        var files = $"-f \"{ComposePath(slot)}\"" + (includeOverride ? $" -f \"{slot.OverridePath}\"" : string.Empty);
        return DockerCli.RunAsync(
            $"compose -p {slot.Project} {files} --env-file \"{slot.SlotEnvPath}\" {arguments}",
            slot.ComposeFolder,
            timeoutSeconds,
            null,
            onOutputLine,
            ct);
    }

    private static string Profile(DockerSlot slot) => slot.HasContainerizedFrontend ? $"--profile {WebProfile} " : string.Empty;

    private static string Filter(DockerSlot slot, string text) => SecretFilter(slot)(text);

    // The values of the slot's env_files (constants and secrets) must never end up in a log: a
    // malformed env_file makes compose print the whole line.
    private static Func<string, string> SecretFilter(DockerSlot slot)
    {
        var values = new List<string>();
        try
        {
            if (Directory.Exists(slot.ComposeFolder))
            {
                foreach (var file in Directory.EnumerateFiles(slot.ComposeFolder, "*.env").Where(f => !string.Equals(Path.GetFileName(f), DockerSlot.SlotEnvFile, StringComparison.OrdinalIgnoreCase)))
                {
                    foreach (var line in File.ReadLines(file))
                    {
                        var equals = line.IndexOf('=');
                        if (equals > 0)
                        {
                            var value = line[(equals + 1)..].Trim().Trim('\'');
                            if (value.Length >= 6)
                            {
                                values.Add(value);
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        var ordered = values.Distinct(StringComparer.Ordinal).OrderByDescending(v => v.Length).ToList();
        return text => ordered.Aggregate(text, (current, value) => current.Replace(value, "***", StringComparison.Ordinal));
    }

    private static string Slash(string path) => path.Replace('\\', '/');

    private static bool NeedsQuotes(string value) => value.Any(ch => char.IsWhiteSpace(ch) || ch is '#' or '$' or '"' or '`');

    private static string DisplayFallback(string? folder) => string.IsNullOrWhiteSpace(folder) ? "(empty fallback)" : folder;
}

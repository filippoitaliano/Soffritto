using System.Text;
using System.Text.Json;
using Soffritto.Core.Configuration;
using Soffritto.Core.Env;

namespace Soffritto.Core.Docker;

/// <summary>
/// Il contratto tra Soffritto e il compose del repository backend: file Docker nel
/// worktree, slot.env, override delle etichette e comandi docker compose dello slot.
/// </summary>
public static class ComposeRunner
{
    public const string WebProfile = "web";

    /// <summary>Il compose usato da -f: sempre quello nella radice del worktree del backend.</summary>
    public static string ComposePath(DockerSlot slot)
        => Path.Combine(slot.BackendWorktree ?? throw new InvalidOperationException("Lo slot non ha un backend."), slot.Backend!.Repository.DockerCompose);

    /// <summary>
    /// I file Docker nella radice del worktree: un file versionato (non ignorato) si usa; uno
    /// assente o ignorato (una copia precedente) si copia dal fallback, escluso da git nella
    /// cartella comune del clone. Una riga di log per file, senza contenuti. Errore o null.
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
            log($"{worktree} non e' un worktree git: file copiati senza esclusione");
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
                    return $"{file}: git check-ignore non risponde in {worktree}";
                }

                if (ignored == false)
                {
                    log($"{file}: usato quello del worktree");
                    continue;
                }
            }

            var source = string.IsNullOrWhiteSpace(fallbackFolder) ? null : Path.Combine(fallbackFolder, file);
            if (source is null || !File.Exists(source))
            {
                return present
                    ? $"{file} nel worktree e' una copia ignorata e {DisplayFallback(fallbackFolder)} non lo ha"
                    : $"{file} non trovato ne' nel worktree ne' in {DisplayFallback(fallbackFolder)}";
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
                return $"{file}: copia da {fallbackFolder} non riuscita ({ex.Message})";
            }

            log($"{file} copiato da {fallbackFolder}");
        }

        return null;
    }

    /// <summary>I file Docker dei worktree dello slot: compose e Dockerfile del backend, Dockerfile del frontend nel container.</summary>
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
    /// slot.env con le variabili del contratto: contesti, Dockerfile relativi al contesto, porte,
    /// env_file dei container e database. Niente segreti condivisi; la password SQL non lo e'.
    /// </summary>
    public static string? WriteSlotEnv(AppConfiguration c, DockerSlot slot)
    {
        if (slot.Backend is not { } backend || slot.PortOf(backend) is not { } apiPort)
        {
            return "lo slot non ha un backend con una porta pubblicata";
        }

        var sql = c.PrimarySeed()?.Seed?.SqlServer ?? new SqlServerSeedConfiguration();
        var web = slot.Frontend;
        var webPort = web is null ? null : slot.PortOf(web);

        // Il compose interpola anche le variabili del web fuori dal profilo: senza frontend
        // puntano al backend, e il servizio web resta spento perche' il profilo non si attiva.
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
                return $"{name}: il valore contiene un apice o un a capo";
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
            return $"slot.env non scritto: {ex.Message}";
        }
    }

    /// <summary>
    /// soffritto.override.yaml con l'etichetta della cartella dati su ogni servizio del compose
    /// (da config --services): la mette Soffritto, anche su un compose di fallback vecchio.
    /// </summary>
    public static async Task<string?> WriteOverrideAsync(DockerSlot slot, CancellationToken ct = default)
    {
        var services = await RunRawAsync(slot, $"{Profile(slot)}config --services", includeOverride: false, timeoutSeconds: 60, ct: ct).ConfigureAwait(false);
        if (!services.Success)
        {
            return $"docker compose config --services: {Filter(slot, services.Tail(3))}";
        }

        var names = services.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        var text = new StringBuilder("# Generato da Soffritto: etichetta della cartella dati su ogni servizio\nservices:\n");
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
            return $"override non scritto: {ex.Message}";
        }
    }

    /// <summary>
    /// docker compose -p &lt;progetto&gt; -f &lt;compose&gt; -f &lt;override&gt; --env-file &lt;slot.env&gt;
    /// [--profile web] &lt;sottocomando&gt;, dalla cartella dello slot. Le righe passano da un filtro
    /// che toglie i valori degli env_file dello slot (un env_file malformato li stampa).
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

    /// <summary>config --quiet con i due file: una variabile mancante da' un errore chiaro. Errore o null.</summary>
    public static async Task<string?> ValidateAsync(DockerSlot slot, CancellationToken ct = default)
    {
        var config = await RunAsync(slot, "config --quiet", slot.HasContainerizedFrontend, timeoutSeconds: 60, ct: ct).ConfigureAwait(false);
        return config.Success ? null : $"docker compose config: {config.Tail(3)}";
    }

    /// <summary>
    /// L'ambiente del servizio come lo risolve compose (env_file ed environment: del compose).
    /// L'uscita contiene i segreti: non passa da nessun log.
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
                return (null, $"il compose non ha il servizio {service}");
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
            return (null, "docker compose config --format json: uscita non leggibile");
        }
    }

    /// <summary>
    /// Un progetto gia' esistente deve avere l'etichetta di questa cartella dati sul primo
    /// container: altrimenti e' di un altro exe (o creato a mano) e non si tocca. Errore o null.
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
            : $"il progetto {slot.Project} esiste ma non e' di questa cartella dati (etichetta {DockerNames.LabelKey} {(string.IsNullOrEmpty(label) ? "assente" : label)}): non lo tocco";
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

    // I valori degli env_file dello slot (costanti e segreti) non devono finire in un log: un
    // env_file malformato fa stampare a compose la riga intera.
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

    private static string DisplayFallback(string? folder) => string.IsNullOrWhiteSpace(folder) ? "(fallback vuoto)" : folder;
}

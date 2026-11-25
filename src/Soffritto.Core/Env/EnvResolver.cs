using System.Text.RegularExpressions;
using Soffritto.Core.Configuration;
using Soffritto.Core.Docker;

namespace Soffritto.Core.Env;

/// <param name="Local">Le variabili per il file di env del worktree.</param>
/// <param name="Container">Le variabili per l'env_file del container, con i nomi "A__B".</param>
/// <param name="Warnings">Avvisi per l'utente: solo nomi, mai valori.</param>
public sealed record EnvResolution(EnvSet Local, EnvSet Container, IReadOnlyList<string> Warnings);

/// <summary>
/// Le variabili di un repository in uno slot: costanti dall'ambiente modello (piu' le manuali),
/// segreti dalla loro fonte, valori per slot che vincono. Il worktree le riceve dall'adapter, il
/// container da &lt;cartella dello slot&gt;\&lt;id&gt;.env.
/// </summary>
public static partial class EnvResolver
{
    /// <param name="poolUrls">Id del repository → URL della sua porta del pool, per {poolUrl}.</param>
    /// <param name="targetIsNew">
    /// Vero quando il file di env del worktree non esiste ancora: una variabile con il valore per
    /// slot non risolvibile tiene allora la costante del modello, invece di sparire dal set.
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
            warnings.Add($"{entry.Id}: l'ambiente modello non include il repository, niente costanti ne' segreti.");
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
                warnings.Add($"{entry.Id}: costanti non trovate, {source} non esiste.");
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
                warnings.Add($"{entry.Id}: segreti non trovati, {secretsPath} non esiste.");
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
                // Senza valore per slot la riga del worktree resta com'e' (anche se scritta a mano):
                // la costante del modello la riscriverebbe con il valore di un altro ambiente.
                if (!targetIsNew)
                {
                    local.Remove(variable.Name);
                }

                warnings.Add(localMissing!.StartsWith("{poolUrl", StringComparison.Ordinal)
                    ? PoolHint(variable.Name, entry.Id, PoolOwnerOf(localMissing, entry.Id), slot.Number)
                    : $"{variable.Name} di {entry.Id}: valore per slot non risolvibile ({localMissing})");
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
                warnings.Add($"{variable.Name} di {entry.Id} non va al container: valore per slot non risolvibile ({containerMissing})");
            }
        }

        var containerNames = new EnvSet(container.Select(v => v with { Name = v.Name.Replace(":", "__", StringComparison.Ordinal) }));
        return new EnvResolution(local, containerNames, warnings);
    }

    /// <summary>L'avviso di una variabile {poolUrl} senza porta del pool: dice di assegnarla a <paramref name="owner"/> e dove.</summary>
    public static string PoolHint(string variable, string repository, string owner, int number)
        => $"{variable} di {repository} non impostato: {owner} dell'ambiente {number} non ha una porta del pool (accendi «Tunnel» nell'intestazione dell'ambiente {number})";

    // {poolUrl:<id>} → id; {poolUrl} → il repository stesso.
    private static string PoolOwnerOf(string placeholder, string repository)
        => placeholder.StartsWith("{" + EnvConfiguration.PoolUrlPrefix, StringComparison.Ordinal)
            ? placeholder[(EnvConfiguration.PoolUrlPrefix.Length + 1)..^1]
            : repository;

    /// <summary>La cartella del repository nell'ambiente modello; null se non c'e'.</summary>
    public static string? TemplatePathOf(AppConfiguration c, RepositoryConfiguration repository)
        => c.TemplateNumber() is { } number && c.Environment(number) is { } template
            ? c.RepositoriesOf(template).FirstOrDefault(r => string.Equals(r.Id, repository.Id, StringComparison.OrdinalIgnoreCase))?.Path
            : null;

    /// <summary>Il file dei segreti: assoluto, o relativo al repository nell'ambiente modello; null senza segreti.</summary>
    public static string? SecretsPathOf(AppConfiguration c, RepositoryConfiguration repository)
        => repository.Env?.Secrets is { Source.Length: > 0 } secrets
            ? Path.IsPathFullyQualified(secrets.Source)
                ? secrets.Source
                : TemplatePathOf(c, repository) is { } template ? Path.Combine(template, secrets.Source) : null
            : null;

    public static string ContainerEnvPath(DockerSlot slot, EnvironmentRepository entry) => Path.Combine(slot.ComposeFolder, $"{entry.Id}.env");

    /// <summary>
    /// Scrive il file di env del worktree con l'adapter del repository e copia i file locali
    /// dall'ambiente modello. I segreti vanno solo in un target ignorato da git, oppure restano
    /// nel loro .bat richiamato con @CALL. Restituisce le righe di log (nomi e conteggi).
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
                    log.Add($"{entry.Id}: {secretCount} segreti richiamati con @CALL da {call}");
                }
                else if (await SourceCloneRepository.IsIgnoredAsync(entry.Path, env.Target, ct).ConfigureAwait(false) != true)
                {
                    set.RemoveWhere(v => v.Class == EnvClass.SharedSecret);
                    log.Add($"segreti non scritti: {target} non e' ignorato da git");
                }
            }

            EnvAdapters.For(env.Adapter).Write(target, set, options);
            log.Add($"{entry.Id}: {target} scritto ({set.Count} variabili, {set.Count(v => v.Class == EnvClass.Slot)} per slot, {set.Count(v => v.Class == EnvClass.SharedSecret)} segreti)");
        }

        var template = TemplatePathOf(c, entry.Repository);
        foreach (var file in entry.Repository.Docker?.LocalFiles ?? [])
        {
            var source = template is null ? null : Path.Combine(template, file);
            if (source is null || !File.Exists(source))
            {
                log.Add($"{entry.Id}: file locale {file} non trovato nell'ambiente modello");
                continue;
            }

            var destination = Path.Combine(entry.Path, file);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: true);
            log.Add($"{entry.Id}: file locale {file} copiato dall'ambiente modello");
        }

        return log;
    }

    /// <summary>
    /// Scrive &lt;cartella dello slot&gt;\&lt;id&gt;.env per compose (valori tra apici singoli,
    /// letterali). Errore o null; l'errore nomina la variabile, mai il valore.
    /// </summary>
    public static string? WriteContainer(DockerSlot slot, EnvironmentRepository entry, EnvResolution resolution)
    {
        var lines = new List<string>();
        foreach (var variable in resolution.Container)
        {
            if (variable.Value.Contains('\'') || variable.Value.Contains('\n') || variable.Value.Contains('\r'))
            {
                return $"{variable.Name} di {entry.Id}: il valore contiene un apice o un a capo, che l'env_file di compose non sa scrivere";
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

    // (valore, null) se tutti i segnaposto si risolvono; (null, primo segnaposto mancante) altrimenti.
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

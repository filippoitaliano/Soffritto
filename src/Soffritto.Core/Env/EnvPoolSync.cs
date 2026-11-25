using Soffritto.Core.Configuration;
using Soffritto.Core.Docker;

namespace Soffritto.Core.Env;

/// <summary>Quali variabili sincronizzare: quelle del pool, oppure anche l'URL dell'api locale («Allinea env»).</summary>
public enum EnvSyncScope
{
    /// <summary>Le variabili con {poolUrl} o {poolUrl:&lt;id&gt;}: dopo un'assegnazione o un rilascio.</summary>
    PoolDependent,

    /// <summary>In piu', negli ambienti locali, le variabili che valgono esattamente {apiHostUrl}.</summary>
    All,
}

/// <summary>Una riga di env della sincronizzazione; vecchio e nuovo valore solo per i valori gestiti (URL).</summary>
/// <param name="Outcome">Uno tra scritto, invariato, a mano, non risolvibile, senza target.</param>
public sealed record EnvSyncChange(string Repository, string File, string Name, string? OldValue, string? NewValue, string Outcome)
{
    /// <summary>Il motivo per cui la riga non si scrive (avviso del pool, valore a mano, file assente); null altrimenti.</summary>
    public string? Detail { get; init; }
}

/// <summary>
/// Riscrive negli env di un ambiente, locale o Docker, solo le variabili che dipendono dal pool di
/// tunnel (e con «Allinea env» l'URL dell'api locale), e solo se il valore attuale e' gestito:
/// assente, vuoto, o un URL verso *.devtunnels.ms, localhost o 127.0.0.1. Le altre righe del file
/// restano come sono; un file senza righe da scrivere non si apre in scrittura.
/// </summary>
public static class EnvPoolSync
{
    public const string Written = "scritto";

    public const string Unchanged = "invariato";

    public const string ByHand = "a mano";

    public const string Unresolvable = "non risolvibile";

    public const string NoTarget = "senza target";

    // Connessioni e porte non dipendono mai dal pool: anche in un valore misto la riga non si tocca.
    private static readonly string[] Excluded = ["{hostConnection}", "{containerConnection}", "{hostPort}", "{containerPort}"];

    /// <summary>Le righe che la sincronizzazione scriverebbe o lascerebbe, senza scrivere niente.</summary>
    /// <param name="repository">Con <see cref="EnvSyncScope.PoolDependent"/>: solo le variabili che dipendono dalla porta di quel repository.</param>
    /// <param name="poolUrls">Id del repository → URL della porta del pool; null = letti dalle assegnazioni.</param>
    public static async Task<IReadOnlyList<EnvSyncChange>> PlanAsync(
        AppConfiguration c,
        int number,
        EnvSyncScope scope,
        string? repository = null,
        CancellationToken ct = default,
        IReadOnlyDictionary<string, string>? poolUrls = null)
        => (await PlanItemsAsync(c, number, scope, repository, ct, poolUrls).ConfigureAwait(false)).Select(i => i.Change).ToList();

    /// <summary>
    /// <see cref="PlanAsync"/>, poi scrive con l'adapter del repository le sole righe "scritto";
    /// nel log una riga per variabile e i riavvii che servono perche' il valore nuovo si legga.
    /// </summary>
    public static async Task<IReadOnlyList<EnvSyncChange>> ApplyAsync(
        AppConfiguration c,
        int number,
        EnvSyncScope scope,
        Action<string> log,
        string? repository = null,
        CancellationToken ct = default,
        IReadOnlyDictionary<string, string>? poolUrls = null)
    {
        var items = await PlanItemsAsync(c, number, scope, repository, ct, poolUrls).ConfigureAwait(false);
        foreach (var file in items.GroupBy(i => i.Change.File, StringComparer.OrdinalIgnoreCase))
        {
            var written = file.Where(i => i.Change.Outcome == Written).ToList();
            if (written.Count > 0)
            {
                var set = new EnvSet(written.Select(i => new EnvVariable(i.Change.Name, i.Change.NewValue!, EnvClass.Slot, "pool")));
                EnvAdapters.For(written[0].Adapter).Write(file.Key, set, new EnvWriteOptions());
            }
        }

        foreach (var item in items)
        {
            var change = item.Change;
            log(change.Outcome == Written
                ? $"{change.Repository}: {change.Name} in {change.File} scritto: {change.NewValue}"
                : change.Detail ?? $"{change.Repository}: {change.Name} in {change.File} {change.Outcome}");
        }

        foreach (var name in items.Where(i => i.Type == RepositoryType.App && i.Change.Outcome == Written).Select(i => i.Change.Name).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            log($"riavvia Metro perche' l'app legga {name}");
        }

        // Il dev server legge l'env solo all'avvio: ogni variabile riscritta chiede un riavvio, e
        // il comando accessorio del frontend, se c'e', legge lo stesso file.
        foreach (var frontend in items.Where(i => i.Type == RepositoryType.Frontend && i.Change.Outcome == Written).GroupBy(i => i.Change.Repository, StringComparer.OrdinalIgnoreCase))
        {
            var names = string.Join(", ", frontend.Select(i => i.Change.Name).Distinct(StringComparer.OrdinalIgnoreCase));
            var extra = c.Repository(frontend.Key) is FrontendConfiguration { ExtraCommand.Length: > 0 } configured
                ? $" (e {configured.ExtraCommand} legge i valori nuovi)"
                : string.Empty;
            log($"riavvia il dev server di {frontend.Key} perche' legga {names}{extra}");
        }

        foreach (var item in items.Where(i => i.ToContainer && i.Change.Outcome != Unresolvable))
        {
            log($"{item.Change.Name} di {item.Change.Repository} arriva al container al prossimo Avvia");
        }

        return items.Select(i => i.Change).ToList();
    }

    /// <summary>Vero per un valore che l'exe puo' riscrivere: assente, vuoto, o un URL verso *.devtunnels.ms, localhost o 127.0.0.1.</summary>
    internal static bool IsManaged(string? value)
        => string.IsNullOrWhiteSpace(value)
            || (Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
                && (uri.Host.EndsWith(".devtunnels.ms", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
                    || uri.Host == "127.0.0.1"));

    private sealed record Item(EnvSyncChange Change, EnvAdapterKind Adapter, RepositoryType Type, bool ToContainer);

    private static async Task<IReadOnlyList<Item>> PlanItemsAsync(
        AppConfiguration c,
        int number,
        EnvSyncScope scope,
        string? repository,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? poolUrls)
    {
        if (c.Environment(number) is not { } environment)
        {
            return [];
        }

        poolUrls ??= await DockerEnvironmentProvisioner.PoolUrlsAsync(number, ct).ConfigureAwait(false);
        var entries = c.RepositoriesOf(environment);
        var isDocker = environment.Kind == EnvironmentKind.Docker;
        var slot = isDocker ? DockerSlot.Of(c, environment) : null;
        var apiHostUrl = isDocker ? null : LocalApiUrl(entries);
        var items = new List<Item>();
        foreach (var entry in entries)
        {
            var env = entry.Repository.Env;
            if (env is null || env.Adapter == EnvAdapterKind.Manual || env.Target.Length == 0)
            {
                continue;
            }

            var chosen = env.Slot.Where(v => v.Name.Length > 0 && InScope(v, entry, scope, repository, isDocker)).ToList();
            if (chosen.Count == 0)
            {
                continue;
            }

            var target = Path.Combine(entry.Path, env.Target);
            if (!File.Exists(target))
            {
                foreach (var variable in chosen)
                {
                    items.Add(new Item(
                        new EnvSyncChange(entry.Id, target, variable.Name, null, null, NoTarget) { Detail = $"{entry.Id}: {target} non c'e', niente scritto" },
                        env.Adapter,
                        entry.Type,
                        ToContainer(variable, isDocker)));
                }

                continue;
            }

            var current = EnvAdapters.For(env.Adapter).Read(target);
            var resolved = slot is null ? null : EnvResolver.Resolve(c, entry, slot, poolUrls, targetIsNew: false).Local;
            foreach (var variable in chosen)
            {
                var (value, missing) = resolved is not null
                    ? (resolved[variable.Name]?.Value, resolved[variable.Name] is null ? FirstPlaceholder(variable.Local) : null)
                    : SubstituteLocal(variable.Local, entry, number, poolUrls, apiHostUrl);
                var old = current[variable.Name]?.Value;
                var change = value is null
                    ? new EnvSyncChange(entry.Id, target, variable.Name, null, null, Unresolvable) { Detail = UnresolvableText(variable, entry, missing, number) }
                    : !IsManaged(old)
                        ? new EnvSyncChange(entry.Id, target, variable.Name, null, null, ByHand) { Detail = $"{variable.Name} di {entry.Id} ha un valore scritto a mano ({HostOf(old!)}): non toccato" }
                        : new EnvSyncChange(entry.Id, target, variable.Name, old, value, old == value ? Unchanged : Written);
                items.Add(new Item(change, env.Adapter, entry.Type, ToContainer(variable, isDocker)));
            }
        }

        return items;
    }

    private static bool InScope(EnvSlotVariable variable, EnvironmentRepository entry, EnvSyncScope scope, string? repository, bool isDocker)
    {
        if (Excluded.Any(p => variable.Local.Contains(p, StringComparison.Ordinal)))
        {
            return false;
        }

        var dependsOnPool = repository is null
            ? variable.Local.Contains("{poolUrl", StringComparison.Ordinal)
            : variable.Local.Contains($"{{{EnvConfiguration.PoolUrlPrefix}{repository}}}", StringComparison.OrdinalIgnoreCase)
                || (variable.Local.Contains("{poolUrl}", StringComparison.Ordinal) && string.Equals(entry.Id, repository, StringComparison.OrdinalIgnoreCase));
        return dependsOnPool || (scope == EnvSyncScope.All && !isDocker && variable.Local == "{apiHostUrl}");
    }

    private static bool ToContainer(EnvSlotVariable variable, bool isDocker)
        => isDocker && variable.Container.Contains("{poolUrl", StringComparison.Ordinal);

    // L'api del backend predefinito dell'ambiente locale, come la raggiungono sulla stessa macchina
    // il proxy del dev server e la barra del frontend: la prima porta https di Kestrel.
    private static string? LocalApiUrl(IReadOnlyList<EnvironmentRepository> entries)
        => entries.FirstOrDefault(e => e.Type == RepositoryType.Backend && e.IsPrimary) is { Repository: BackendConfiguration backend } entry
            && DevTunnelProvisioner.KestrelPorts(entry.Path, backend).FirstOrDefault() is { } port
                ? $"https://localhost:{port.Number}"
                : null;

    // Negli ambienti locali si sostituiscono solo {poolUrl}, {poolUrl:<id>}, {apiHostUrl} e {n}.
    private static (string? Value, string? Missing) SubstituteLocal(string text, EnvironmentRepository entry, int number, IReadOnlyDictionary<string, string> poolUrls, string? apiHostUrl)
    {
        string? missing = null;
        var value = System.Text.RegularExpressions.Regex.Replace(text, @"\{[^{}\s]+\}", match =>
        {
            var placeholder = match.Value;
            var resolved = placeholder switch
            {
                "{n}" => number.ToString(),
                "{apiHostUrl}" => apiHostUrl,
                "{poolUrl}" => poolUrls.GetValueOrDefault(entry.Id),
                _ when placeholder.StartsWith("{" + EnvConfiguration.PoolUrlPrefix, StringComparison.Ordinal)
                    => poolUrls.GetValueOrDefault(placeholder[(EnvConfiguration.PoolUrlPrefix.Length + 1)..^1]),
                _ => null,
            };
            missing ??= resolved is null ? placeholder : null;
            return resolved ?? placeholder;
        });
        return missing is null ? (value, null) : (null, missing);
    }

    private static string? FirstPlaceholder(string text)
        => EnvConfiguration.PlaceholdersIn(text).FirstOrDefault(p => p.StartsWith("{poolUrl", StringComparison.Ordinal)) ?? EnvConfiguration.PlaceholdersIn(text).FirstOrDefault();

    private static string UnresolvableText(EnvSlotVariable variable, EnvironmentRepository entry, string? missing, int number)
        => missing is not null && missing.StartsWith("{poolUrl", StringComparison.Ordinal)
            ? EnvResolver.PoolHint(
                variable.Name,
                entry.Id,
                missing.StartsWith("{" + EnvConfiguration.PoolUrlPrefix, StringComparison.Ordinal) ? missing[(EnvConfiguration.PoolUrlPrefix.Length + 1)..^1] : entry.Id,
                number)
            : $"{variable.Name} di {entry.Id}: valore non risolvibile ({missing ?? variable.Local})";

    private static string HostOf(string value)
        => Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? uri.Host : "valore non URL";
}

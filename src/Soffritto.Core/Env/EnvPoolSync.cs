using Soffritto.Core.Configuration;
using Soffritto.Core.Docker;

namespace Soffritto.Core.Env;

/// <summary>Which variables to sync: the pool ones, or also the local api URL ("Align env").</summary>
public enum EnvSyncScope
{
    /// <summary>The variables with {poolUrl} or {poolUrl:&lt;id&gt;}: after an assignment or a release.</summary>
    PoolDependent,

    /// <summary>In addition, in local environments, the variables whose value is exactly {apiHostUrl}.</summary>
    All,
}

/// <summary>An env line of the sync; old and new value only for managed values (URLs).</summary>
/// <param name="Outcome">One of written, unchanged, manual, unresolvable, no target.</param>
public sealed record EnvSyncChange(string Repository, string File, string Name, string? OldValue, string? NewValue, string Outcome)
{
    /// <summary>Why the line is not written (pool warning, manual value, missing file); null otherwise.</summary>
    public string? Detail { get; init; }
}

/// <summary>
/// Rewrites in an environment's env files, local or Docker, only the variables that depend on the tunnel
/// pool (and with "Align env" the local api URL), and only if the current value is managed:
/// missing, empty, or a URL to *.devtunnels.ms, localhost or 127.0.0.1. The file's other lines
/// stay as they are; a file with no lines to write is not opened for writing.
/// </summary>
public static class EnvPoolSync
{
    public const string Written = "written";

    public const string Unchanged = "unchanged";

    public const string ByHand = "manual";

    public const string Unresolvable = "unresolvable";

    public const string NoTarget = "no target";

    // Connections and ports never depend on the pool: even in a mixed value the line is left alone.
    private static readonly string[] Excluded = ["{hostConnection}", "{containerConnection}", "{hostPort}", "{containerPort}"];

    /// <summary>The lines the sync would write or leave, without writing anything.</summary>
    /// <param name="repository">With <see cref="EnvSyncScope.PoolDependent"/>: only the variables that depend on that repository's port.</param>
    /// <param name="poolUrls">Repository id → pool port URL; null = read from the assignments.</param>
    public static async Task<IReadOnlyList<EnvSyncChange>> PlanAsync(
        AppConfiguration c,
        int number,
        EnvSyncScope scope,
        string? repository = null,
        CancellationToken ct = default,
        IReadOnlyDictionary<string, string>? poolUrls = null)
        => (await PlanItemsAsync(c, number, scope, repository, ct, poolUrls).ConfigureAwait(false)).Select(i => i.Change).ToList();

    /// <summary>
    /// <see cref="PlanAsync"/>, then writes only the "written" lines with the repository's adapter;
    /// the log gets one line per variable and the restarts needed for the new value to be read.
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
                ? $"{change.Repository}: {change.Name} in {change.File} written: {change.NewValue}"
                : change.Detail ?? $"{change.Repository}: {change.Name} in {change.File} {change.Outcome}");
        }

        foreach (var name in items.Where(i => i.Type == RepositoryType.App && i.Change.Outcome == Written).Select(i => i.Change.Name).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            log($"restart Metro so the app reads {name}");
        }

        // The dev server reads the env only at startup: every rewritten variable calls for a restart, and
        // the frontend's extra command, if any, reads the same file.
        foreach (var frontend in items.Where(i => i.Type == RepositoryType.Frontend && i.Change.Outcome == Written).GroupBy(i => i.Change.Repository, StringComparer.OrdinalIgnoreCase))
        {
            var names = string.Join(", ", frontend.Select(i => i.Change.Name).Distinct(StringComparer.OrdinalIgnoreCase));
            var extra = c.Repository(frontend.Key) is FrontendConfiguration { ExtraCommand.Length: > 0 } configured
                ? $" (and {configured.ExtraCommand} reads the new values)"
                : string.Empty;
            log($"restart the {frontend.Key} dev server so it reads {names}{extra}");
        }

        foreach (var item in items.Where(i => i.ToContainer && i.Change.Outcome != Unresolvable))
        {
            log($"{item.Change.Name} of {item.Change.Repository} reaches the container on the next Start");
        }

        return items.Select(i => i.Change).ToList();
    }

    /// <summary>True for a value the app may rewrite: missing, empty, or a URL to *.devtunnels.ms, localhost or 127.0.0.1.</summary>
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
                        new EnvSyncChange(entry.Id, target, variable.Name, null, null, NoTarget) { Detail = $"{entry.Id}: {target} does not exist, nothing written" },
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
                        ? new EnvSyncChange(entry.Id, target, variable.Name, null, null, ByHand) { Detail = $"{variable.Name} of {entry.Id} has a hand-written value ({HostOf(old!)}): left alone" }
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

    // The api of the local environment's primary backend, as the dev server proxy and the frontend's
    // address bar reach it on the same machine: the first Kestrel https port.
    private static string? LocalApiUrl(IReadOnlyList<EnvironmentRepository> entries)
        => entries.FirstOrDefault(e => e.Type == RepositoryType.Backend && e.IsPrimary) is { Repository: BackendConfiguration backend } entry
            && DevTunnelProvisioner.KestrelPorts(entry.Path, backend).FirstOrDefault() is { } port
                ? $"https://localhost:{port.Number}"
                : null;

    // In local environments only {poolUrl}, {poolUrl:<id>}, {apiHostUrl} and {n} are substituted.
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
            : $"{variable.Name} of {entry.Id}: unresolvable value ({missing ?? variable.Local})";

    private static string HostOf(string value)
        => Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? uri.Host : "not a URL";
}

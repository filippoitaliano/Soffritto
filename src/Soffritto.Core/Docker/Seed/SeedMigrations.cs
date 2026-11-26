using System.Text.RegularExpressions;
using Soffritto.Core.Configuration;

namespace Soffritto.Core.Docker.Seed;

/// <summary>The dev migrations the configured seed is missing, per context.</summary>
public static partial class SeedMigrations
{
    public const string DevReference = GitConventions.RemoteDevelopmentReference;

    [GeneratedRegex(@"^\d{14}_.+\.cs$", RegexOptions.CultureInvariant)]
    private static partial Regex MigrationFile();

    /// <summary>
    /// For each context, the origin/dev migrations whose id is greater (ordinal order) than the last
    /// one the seed applied, based on the info written by "Verify seed".
    /// </summary>
    public static async Task<(IReadOnlyDictionary<string, IReadOnlyList<string>>? Missing, string? Error)> MissingOnDevAsync(
        AppConfiguration c,
        bool fetch,
        CancellationToken ct = default)
    {
        if (c.PrimarySeed() is not { Seed: { } seed } backend)
        {
            return (null, "no backend has a seed configured");
        }

        if (SeedInfo.Read(seed.Path) is not { } info)
        {
            return (null, "seed not verified");
        }

        if (fetch && backend.SourceClone is { Length: > 0 } bare && await SourceCloneRepository.FetchAsync(bare, ct: ct).ConfigureAwait(false) is { } fetchError)
        {
            return (null, fetchError);
        }

        var (dev, error) = await DevMigrationsAsync(backend, ct).ConfigureAwait(false);
        if (dev is null)
        {
            return (null, error);
        }

        var missing = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var (context, ids) in dev)
        {
            missing[context] = After(ids, info.LastMigrations.GetValueOrDefault(context));
        }

        return (missing, null);
    }

    /// <summary>The ids of the origin/dev migrations per context, from the file names in the source clone (no checkout).</summary>
    public static async Task<(IReadOnlyDictionary<string, IReadOnlyList<string>>? Migrations, string? Error)> DevMigrationsAsync(BackendConfiguration backend, CancellationToken ct = default)
    {
        if (backend.SourceClone is not { Length: > 0 } bare)
        {
            return (null, $"{backend.Id} has no source clone: the dev migrations cannot be read");
        }

        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var context in backend.Contexts)
        {
            var (names, error) = await SourceCloneRepository.ListFilesAsync(bare, DevReference, context.Migrations, ct).ConfigureAwait(false);
            if (error is not null)
            {
                return (null, $"{context.Name} migrations on origin/dev: {error}");
            }

            result[context.Name] = names
                .Where(n => MigrationFile().IsMatch(n) && !n.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase))
                .Select(n => n[..^3])
                .Order(StringComparer.Ordinal)
                .ToList();
        }

        return (result, null);
    }

    /// <summary>The ids after <paramref name="last"/> in ordinal order; all of them if the seed has none.</summary>
    public static IReadOnlyList<string> After(IEnumerable<string> ids, string? last)
        => ids.Where(id => last is null || string.CompareOrdinal(id, last) > 0).Order(StringComparer.Ordinal).ToList();
}

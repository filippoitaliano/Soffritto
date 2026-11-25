using System.Text.RegularExpressions;
using Soffritto.Core.Configuration;

namespace Soffritto.Core.Docker.Seed;

/// <summary>Le migrazioni di dev che mancano al seed configurato, per context.</summary>
public static partial class SeedMigrations
{
    public const string DevReference = GitConventions.RemoteDevelopmentReference;

    [GeneratedRegex(@"^\d{14}_.+\.cs$", RegexOptions.CultureInvariant)]
    private static partial Regex MigrationFile();

    /// <summary>
    /// Per ogni context, le migrazioni di origin/dev con id maggiore (ordine ordinale) dell'ultima
    /// che il seed ha applicato, dalle informazioni scritte da «Verifica seed».
    /// </summary>
    public static async Task<(IReadOnlyDictionary<string, IReadOnlyList<string>>? Missing, string? Error)> MissingOnDevAsync(
        AppConfiguration c,
        bool fetch,
        CancellationToken ct = default)
    {
        if (c.PrimarySeed() is not { Seed: { } seed } backend)
        {
            return (null, "nessun backend ha un seed configurato");
        }

        if (SeedInfo.Read(seed.Path) is not { } info)
        {
            return (null, "seed non verificato");
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

    /// <summary>Gli id delle migrazioni di origin/dev per context, dai nomi dei file del clone sorgente (senza checkout).</summary>
    public static async Task<(IReadOnlyDictionary<string, IReadOnlyList<string>>? Migrations, string? Error)> DevMigrationsAsync(BackendConfiguration backend, CancellationToken ct = default)
    {
        if (backend.SourceClone is not { Length: > 0 } bare)
        {
            return (null, $"{backend.Id} non ha un clone sorgente: le migrazioni di dev non si leggono");
        }

        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var context in backend.Contexts)
        {
            var (names, error) = await SourceCloneRepository.ListFilesAsync(bare, DevReference, context.Migrations, ct).ConfigureAwait(false);
            if (error is not null)
            {
                return (null, $"migrazioni di {context.Name} su origin/dev: {error}");
            }

            result[context.Name] = names
                .Where(n => MigrationFile().IsMatch(n) && !n.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase))
                .Select(n => n[..^3])
                .Order(StringComparer.Ordinal)
                .ToList();
        }

        return (result, null);
    }

    /// <summary>Gli id dopo <paramref name="last"/> in ordine ordinale; tutti se il seed non ne ha nessuno.</summary>
    public static IReadOnlyList<string> After(IEnumerable<string> ids, string? last)
        => ids.Where(id => last is null || string.CompareOrdinal(id, last) > 0).Order(StringComparer.Ordinal).ToList();
}

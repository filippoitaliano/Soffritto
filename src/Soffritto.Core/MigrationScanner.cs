using System.Text.RegularExpressions;

namespace Soffritto.Core;

public sealed record ScannedMigration(string MigrationId, string FileName)
{
    /// <summary>
    /// Il file e' stato rinominato senza aggiornare l'attributo [Migration] o viceversa.
    /// Non rompe EF, che usa l'attributo, ma rende il repo fuorviante a colpo d'occhio.
    /// </summary>
    public bool FileNameMismatch => !string.Equals(MigrationId, FileName, StringComparison.Ordinal);
}

public static partial class MigrationScanner
{
    private const int AttributeSearchLineLimit = 80;

    [GeneratedRegex(@"^\d{14}_.+$", RegexOptions.CultureInvariant)]
    private static partial Regex MigrationIdPattern();

    [GeneratedRegex(@"\[\s*Migration\s*\(\s*""([^""]+)""\s*\)\s*\]", RegexOptions.CultureInvariant)]
    private static partial Regex MigrationAttributePattern();

    /// <summary>
    /// Elenca le migrazioni di una cartella senza caricare assembly ne' EF Core.
    /// L'ID autoritativo e' quello dichiarato in [Migration("...")] nel file .Designer.cs:
    /// il nome del file puo' divergere se la migrazione e' stata rinominata a meta'.
    /// </summary>
    public static IReadOnlyList<ScannedMigration> Scan(string migrationsDirectory)
    {
        if (!Directory.Exists(migrationsDirectory))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(migrationsDirectory, "*.cs", SearchOption.TopDirectoryOnly)
            .Select(path => (Path: path, Name: Path.GetFileNameWithoutExtension(path)))
            .Where(f => !f.Name.EndsWith(".Designer", StringComparison.OrdinalIgnoreCase))
            .Where(f => MigrationIdPattern().IsMatch(f.Name))
            .Select(f => new ScannedMigration(ReadDeclaredId(f.Path, f.Name), f.Name))
            .OrderBy(m => m.MigrationId, StringComparer.Ordinal)
            .ToList();
    }

    private static string ReadDeclaredId(string migrationPath, string fallbackId)
    {
        var designerPath = Path.ChangeExtension(migrationPath, null) + ".Designer.cs";

        // L'attributo puo' stare sulla migrazione stessa quando il Designer manca.
        foreach (var candidate in new[] { designerPath, migrationPath })
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            try
            {
                var line = 0;
                foreach (var text in File.ReadLines(candidate))
                {
                    if (++line > AttributeSearchLineLimit)
                    {
                        break;
                    }

                    var match = MigrationAttributePattern().Match(text);
                    if (match.Success)
                    {
                        return match.Groups[1].Value;
                    }
                }
            }
            catch (IOException)
            {
                // File bloccato o illeggibile: il nome file resta un fallback ragionevole.
            }
        }

        return fallbackId;
    }
}

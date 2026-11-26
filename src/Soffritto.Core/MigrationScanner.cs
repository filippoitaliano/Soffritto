using System.Text.RegularExpressions;

namespace Soffritto.Core;

public sealed record ScannedMigration(string MigrationId, string FileName)
{
    /// <summary>
    /// The file was renamed without updating the [Migration] attribute, or vice versa.
    /// It does not break EF, which uses the attribute, but it makes the repo misleading at a glance.
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
    /// Lists the migrations in a folder without loading assemblies or EF Core.
    /// The authoritative ID is the one declared in [Migration("...")] in the .Designer.cs file:
    /// the file name can differ if the migration was only partially renamed.
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

        // The attribute can sit on the migration itself when the Designer file is missing.
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
                // File locked or unreadable: the file name remains a reasonable fallback.
            }
        }

        return fallbackId;
    }
}

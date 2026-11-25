using System.Text.Json;
using Soffritto.Core.Env;

namespace Soffritto.Core;

public sealed record ContextDefinition(
    string Name,
    string RelativeMigrationsPath,
    string RelativeProjectPath,
    string RelativeStartupProjectPath);

public sealed record ProbeOptions(
    IReadOnlyList<ContextDefinition> Contexts,
    IReadOnlyList<string> AppSettingsCandidates,
    string ConnectionStringName = "DefaultConnection");

public static class ConnectionStringLocator
{
    /// <summary>
    /// Legge la connection string dal primo appsettings disponibile. I file del repo sono
    /// JSONC (varianti alternative lasciate commentate con //), quindi il parser deve
    /// saltare i commenti invece di fallire.
    /// </summary>
    public static (string? ConnectionString, string? SourceFile, string? Error) Find(string solutionRoot, ProbeOptions options)
    {
        var probed = new List<string>();

        foreach (var candidate in options.AppSettingsCandidates)
        {
            var full = Path.Combine(solutionRoot, candidate);
            if (!File.Exists(full))
            {
                probed.Add(candidate);
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(full), JsonSettingsFiles.Options);

                if (doc.RootElement.TryGetProperty("ConnectionStrings", out var section)
                    && section.TryGetProperty(options.ConnectionStringName, out var value)
                    && value.ValueKind == JsonValueKind.String)
                {
                    var cs = value.GetString();
                    if (!string.IsNullOrWhiteSpace(cs))
                    {
                        return (cs, candidate, null);
                    }
                }

                probed.Add($"{candidate} (nessuna {options.ConnectionStringName})");
            }
            catch (JsonException ex)
            {
                return (null, candidate, $"JSON non valido in {candidate}: {ex.Message}");
            }
        }

        return (null, null, $"Connection string non trovata. Percorsi provati: {string.Join(", ", probed)}");
    }
}

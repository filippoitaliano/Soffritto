using System.Security.Cryptography;
using System.Text;
using Soffritto.Core.Configuration;

namespace Soffritto.Core.Docker;

/// <summary>
/// I nomi delle risorse Docker di una cartella dati: progetti, container del seed ed etichetta.
/// Due exe con cartelle dati diverse (la Debug isolata e la Release) non si toccano le risorse.
/// </summary>
public static class DockerNames
{
    public const string LabelKey = "soffritto.dati";

    private const string Prefix = "soffritto-";

    /// <summary>Le prime 8 cifre esadecimali dello SHA256 del percorso normalizzato della cartella dati.</summary>
    public static string DataFolderTag => TagOf(ConfigurationFiles.DataFolder);

    /// <summary>"soffritto-", o "soffritto-&lt;4 cifre del tag&gt;-" con la cartella dati sovrascritta.</summary>
    public static string ProjectPrefix => ConfigurationFiles.IsDataFolderOverridden ? $"{Prefix}{DataFolderTag[..4]}-" : Prefix;

    /// <summary>L'etichetta che l'override generato mette su ogni servizio, e su cui filtra docker ps.</summary>
    public static string Label => $"{LabelKey}={DataFolderTag}";

    public static string Project(int number) => $"{ProjectPrefix}{number}";

    public static string SeedContainer => $"{ProjectPrefix}seed";

    /// <summary>Il progetto senza "soffritto-": la chiave delle console di un ambiente Docker.</summary>
    public static string KeyOf(string project) => project.StartsWith(Prefix, StringComparison.Ordinal) ? project[Prefix.Length..] : project;

    public static string TagOf(string dataFolder)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataFolder)).ToUpperInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash)[..8].ToLowerInvariant();
    }
}

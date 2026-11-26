using System.Security.Cryptography;
using System.Text;
using Soffritto.Core.Configuration;

namespace Soffritto.Core.Docker;

/// <summary>
/// The names of a data folder's Docker resources: projects, seed container and label.
/// Two executables with different data folders (the isolated Debug one and the Release one) never touch each other's resources.
/// </summary>
public static class DockerNames
{
    public const string LabelKey = "soffritto.dati";

    private const string Prefix = "soffritto-";

    /// <summary>The first 8 hex digits of the SHA256 of the data folder's normalized path.</summary>
    public static string DataFolderTag => TagOf(ConfigurationFiles.DataFolder);

    /// <summary>"soffritto-", or "soffritto-&lt;4 digits of the tag&gt;-" when the data folder is overridden.</summary>
    public static string ProjectPrefix => ConfigurationFiles.IsDataFolderOverridden ? $"{Prefix}{DataFolderTag[..4]}-" : Prefix;

    /// <summary>The label the generated override puts on every service, and that docker ps filters on.</summary>
    public static string Label => $"{LabelKey}={DataFolderTag}";

    public static string Project(int number) => $"{ProjectPrefix}{number}";

    public static string SeedContainer => $"{ProjectPrefix}seed";

    /// <summary>The project without "soffritto-": the key of a Docker environment's consoles.</summary>
    public static string KeyOf(string project) => project.StartsWith(Prefix, StringComparison.Ordinal) ? project[Prefix.Length..] : project;

    public static string TagOf(string dataFolder)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataFolder)).ToUpperInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash)[..8].ToLowerInvariant();
    }
}

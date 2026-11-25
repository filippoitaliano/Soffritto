using System.Security.Cryptography;
using System.Text.Json;
using Soffritto.Core.Configuration;

namespace Soffritto.Core.Docker.Seed;

/// <summary>
/// Cosa contiene un file di seed, scritto da «Verifica seed» e da «Allinea seed» in
/// &lt;cartella dati&gt;\docker\seed-info\&lt;sha256 del file&gt;.json.
/// </summary>
public sealed record SeedInfo(
    string File,
    string Sha256,
    DateTimeOffset VerifiedAt,
    string Source,
    string? DevCommit,
    Dictionary<string, string> LastMigrations,
    List<string> UnknownMigrations)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string Folder => Path.Combine(ConfigurationFiles.DataFolder, "docker", "seed-info");

    public static string PathOf(string sha256) => System.IO.Path.Combine(Folder, $"{sha256}.json");

    public static async Task<string> HashAsync(string seedPath, CancellationToken ct = default)
    {
        await using var stream = System.IO.File.OpenRead(seedPath);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
    }

    /// <summary>Le informazioni del file di seed (per SHA256 del contenuto); null se il file o le informazioni mancano.</summary>
    public static SeedInfo? Read(string seedPath)
    {
        try
        {
            if (!System.IO.File.Exists(seedPath))
            {
                return null;
            }

            string sha;
            using (var stream = System.IO.File.OpenRead(seedPath))
            {
                sha = Convert.ToHexString(SHA256.HashData(stream));
            }

            var path = PathOf(sha);
            return System.IO.File.Exists(path) ? JsonSerializer.Deserialize<SeedInfo>(System.IO.File.ReadAllText(path), Json) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static void Write(SeedInfo info)
    {
        Directory.CreateDirectory(Folder);
        var path = PathOf(info.Sha256);
        var temporary = path + ".tmp";
        System.IO.File.WriteAllText(temporary, JsonSerializer.Serialize(info, Json));
        System.IO.File.Move(temporary, path, overwrite: true);
    }
}

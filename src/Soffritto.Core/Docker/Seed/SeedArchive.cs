using System.Security.Cryptography;
using Soffritto.Core.Configuration;

namespace Soffritto.Core.Docker.Seed;

/// <summary>Il file del seed pronto per il ripristino: un .bak cosi' com'e', un .7z estratto una volta in cache.</summary>
public static class SeedArchive
{
    public static string CacheFolder => Path.Combine(ConfigurationFiles.DataFolder, "docker", "seed-cache");

    /// <summary>
    /// .bak: il percorso stesso. .7z: il solo file contenuto, estratto con tar.exe in
    /// seed-cache\&lt;sha256 dell'archivio&gt;\ la prima volta e poi letto dalla cache.
    /// </summary>
    public static async Task<(string? File, string? Error)> PrepareAsync(string path, Action<string>? log = null, CancellationToken ct = default)
    {
        if (!File.Exists(path))
        {
            return (null, $"seed non trovato: {path}");
        }

        var extension = Path.GetExtension(path);
        if (extension.Equals(".bak", StringComparison.OrdinalIgnoreCase))
        {
            return (path, null);
        }

        if (!extension.Equals(".7z", StringComparison.OrdinalIgnoreCase))
        {
            return (null, $"seed con estensione {extension} non gestita: servono .bak o .7z");
        }

        string sha;
        await using (var stream = File.OpenRead(path))
        {
            sha = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
        }

        var folder = Path.Combine(CacheFolder, sha);
        if (Directory.Exists(folder) && Directory.GetFiles(folder, "*", SearchOption.AllDirectories) is [var cached])
        {
            log?.Invoke($"seed dalla cache: {cached}");
            return (cached, null);
        }

        // Estrazione in una cartella temporanea, spostata solo a estrazione riuscita: una cache
        // a meta' non viene mai presa per buona.
        var temporary = folder + ".tmp";
        try
        {
            if (Directory.Exists(temporary))
            {
                Directory.Delete(temporary, recursive: true);
            }

            Directory.CreateDirectory(temporary);
            log?.Invoke($"estrazione di {path}");
            var tar = await ProcessRunner.RunAsync(
                Path.Combine(Environment.SystemDirectory, "tar.exe"),
                $"-xf \"{path}\" -C \"{temporary}\"",
                temporary,
                1800,
                cancellationToken: ct).ConfigureAwait(false);
            if (!tar.Success)
            {
                return (null, $"estrazione del seed non riuscita: {tar.Tail(3)}");
            }

            var files = Directory.GetFiles(temporary, "*", SearchOption.AllDirectories);
            if (files.Length != 1)
            {
                return (null, $"il seed {path} contiene {files.Length} file: ne serve uno solo (il backup)");
            }

            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }

            Directory.Move(temporary, folder);
            var extracted = Path.Combine(folder, Path.GetRelativePath(temporary, files[0]));
            log?.Invoke($"seed estratto in {extracted} ({new FileInfo(extracted).Length} byte)");
            return (extracted, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, $"cache del seed non scritta: {ex.Message}");
        }
        finally
        {
            try
            {
                if (Directory.Exists(temporary))
                {
                    Directory.Delete(temporary, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}

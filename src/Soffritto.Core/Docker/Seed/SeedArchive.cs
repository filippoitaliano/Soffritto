using System.Security.Cryptography;
using Soffritto.Core.Configuration;

namespace Soffritto.Core.Docker.Seed;

/// <summary>The seed file ready for restore: a .bak as is, a .7z extracted once into a cache.</summary>
public static class SeedArchive
{
    public static string CacheFolder => Path.Combine(ConfigurationFiles.DataFolder, "docker", "seed-cache");

    /// <summary>
    /// .bak: the path itself. .7z: the single file inside, extracted with tar.exe into
    /// seed-cache\&lt;sha256 of the archive&gt;\ the first time and then read from the cache.
    /// </summary>
    public static async Task<(string? File, string? Error)> PrepareAsync(string path, Action<string>? log = null, CancellationToken ct = default)
    {
        if (!File.Exists(path))
        {
            return (null, $"seed not found: {path}");
        }

        var extension = Path.GetExtension(path);
        if (extension.Equals(".bak", StringComparison.OrdinalIgnoreCase))
        {
            return (path, null);
        }

        if (!extension.Equals(".7z", StringComparison.OrdinalIgnoreCase))
        {
            return (null, $"seed extension {extension} is not supported: use .bak or .7z");
        }

        string sha;
        await using (var stream = File.OpenRead(path))
        {
            sha = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
        }

        var folder = Path.Combine(CacheFolder, sha);
        if (Directory.Exists(folder) && Directory.GetFiles(folder, "*", SearchOption.AllDirectories) is [var cached])
        {
            log?.Invoke($"seed from cache: {cached}");
            return (cached, null);
        }

        // Extract into a temporary folder that is moved only once extraction succeeds: a half-written
        // cache is never taken as valid.
        var temporary = folder + ".tmp";
        try
        {
            if (Directory.Exists(temporary))
            {
                Directory.Delete(temporary, recursive: true);
            }

            Directory.CreateDirectory(temporary);
            log?.Invoke($"extracting {path}");
            var tar = await ProcessRunner.RunAsync(
                Path.Combine(Environment.SystemDirectory, "tar.exe"),
                $"-xf \"{path}\" -C \"{temporary}\"",
                temporary,
                1800,
                cancellationToken: ct).ConfigureAwait(false);
            if (!tar.Success)
            {
                return (null, $"seed extraction failed: {tar.Tail(3)}");
            }

            var files = Directory.GetFiles(temporary, "*", SearchOption.AllDirectories);
            if (files.Length != 1)
            {
                return (null, $"seed {path} contains {files.Length} files: it must contain exactly one (the backup)");
            }

            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }

            Directory.Move(temporary, folder);
            var extracted = Path.Combine(folder, Path.GetRelativePath(temporary, files[0]));
            log?.Invoke($"seed extracted to {extracted} ({new FileInfo(extracted).Length} bytes)");
            return (extracted, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, $"could not write the seed cache: {ex.Message}");
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

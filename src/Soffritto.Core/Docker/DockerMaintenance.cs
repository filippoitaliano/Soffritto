using System.Text.RegularExpressions;
using Soffritto.Core.Configuration;

namespace Soffritto.Core.Docker;

/// <summary>
/// The space environments take up in Docker: the images built by each environment's compose
/// file and the build cache. A destroyed environment's images are no longer needed; the cache
/// speeds up builds but grows without limit. Docker reuses freed space, so keeping internal usage
/// low stops its virtual disk from growing.
/// </summary>
public static class DockerMaintenance
{
    /// <summary>The build cache the cleanup keeps: above it, Docker removes the least recently used entries.</summary>
    public const string CacheLimit = "10GB";

    /// <summary>The images built by the project's compose file: "&lt;project&gt;-&lt;service&gt;".</summary>
    public static async Task<(IReadOnlyList<string> Images, string? Error)> ImagesOfProjectAsync(string project, CancellationToken ct = default)
    {
        var (all, error) = await RepositoriesAsync(ct).ConfigureAwait(false);
        return (all.Where(name => name.StartsWith(project + "-", StringComparison.Ordinal)).ToList(), error);
    }

    /// <summary>
    /// Images of this data folder's environments whose number is no longer a Docker environment
    /// in the configuration: leftovers of environments destroyed by versions that left them behind, or
    /// of half-finished creations.
    /// </summary>
    public static async Task<(IReadOnlyList<string> Images, string? Error)> OrphanImagesAsync(AppConfiguration c, CancellationToken ct = default)
    {
        var (all, error) = await RepositoriesAsync(ct).ConfigureAwait(false);
        var pattern = new Regex($"^{Regex.Escape(DockerNames.ProjectPrefix)}(\\d+)-.+$", RegexOptions.CultureInvariant);
        var orphans = all
            .Select(name => (Name: name, Match: pattern.Match(name)))
            .Where(x => x.Match.Success
                && int.TryParse(x.Match.Groups[1].Value, out var number)
                && c.Environment(number) is not { Kind: EnvironmentKind.Docker })
            .Select(x => x.Name)
            .ToList();
        return (orphans, error);
    }

    /// <summary>Removes the given images; the outcome in one line, even when some remain.</summary>
    public static async Task<string> RemoveImagesAsync(IReadOnlyList<string> images, CancellationToken ct = default)
    {
        if (images.Count == 0)
        {
            return "no images to remove";
        }

        var remove = await DockerCli.RunAsync($"image rm {string.Join(' ', images)}", timeoutSeconds: 300, ct: ct).ConfigureAwait(false);
        return remove.Success
            ? $"images removed: {string.Join(", ", images)}"
            : $"not all images were removed ({string.Join(", ", images)}): {remove.Tail(2)}";
    }

    /// <summary>Trims the build cache to <see cref="CacheLimit"/>, removing the least recently used entries.</summary>
    public static async Task<string> TrimBuildCacheAsync(CancellationToken ct = default)
    {
        var prune = await DockerCli.RunAsync($"builder prune -f --max-used-space {CacheLimit}", timeoutSeconds: 600, ct: ct).ConfigureAwait(false);
        return prune.Success
            ? $"build cache trimmed to {CacheLimit} ({LastLine(prune.StandardOutput)})"
            : $"build cache not trimmed: {prune.Tail(2)}";
    }

    /// <summary>
    /// The command line of the cleanup launched in a detached process (when the app closes):
    /// orphan images, then the build cache, with the output in <paramref name="logPath"/>.
    /// </summary>
    public static string CleanupCommandLine(IReadOnlyList<string> orphanImages, string logPath)
    {
        var steps = new List<string>();
        if (orphanImages.Count > 0)
        {
            steps.Add($"docker image rm {string.Join(' ', orphanImages)}");
        }

        steps.Add($"docker builder prune -f --max-used-space {CacheLimit}");
        return $"cmd.exe /d /c \"(echo %DATE% %TIME% & {string.Join(" & ", steps)}) > \"{logPath}\" 2>&1\"";
    }

    /// <summary>Docker usage by type (images, containers, volumes, cache): size and how much can be reclaimed.</summary>
    public static async Task<(IReadOnlyList<(string Type, string Size, string Reclaimable)> Usage, string? Error)> UsageAsync(CancellationToken ct = default)
    {
        var df = await DockerCli.RunAsync("system df --format \"{{.Type}}|{{.Size}}|{{.Reclaimable}}\"", timeoutSeconds: 60, ct: ct).ConfigureAwait(false);
        if (!df.Success)
        {
            return ([], df.Tail(2));
        }

        var rows = df.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim().Split('|'))
            .Where(parts => parts.Length == 3)
            .Select(parts => (parts[0], parts[1], parts[2]))
            .ToList();
        return (rows, null);
    }

    private static async Task<(IReadOnlyList<string> Repositories, string? Error)> RepositoriesAsync(CancellationToken ct)
    {
        var list = await DockerCli.RunAsync("image ls --format \"{{.Repository}}\"", timeoutSeconds: 60, ct: ct).ConfigureAwait(false);
        if (!list.Success)
        {
            return ([], list.Tail(2));
        }

        return (list.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(name => name.Length > 0 && name != "<none>")
            .Distinct(StringComparer.Ordinal)
            .ToList(), null);
    }

    private static string LastLine(string text)
        => text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "nothing to remove";
}

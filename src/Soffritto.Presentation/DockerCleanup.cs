using Soffritto.Core.Configuration;
using Soffritto.Core.Docker;

namespace Soffritto.Presentation;

/// <summary>
/// The light Docker cleanup: images of environments that no longer exist and build cache
/// beyond <see cref="DockerMaintenance.CacheLimit"/>. It does not stop Docker, does not touch containers and
/// does not ask for permissions; it is skipped while a compose command is working (build, up, down, stop),
/// launched by the app or by another process such as the MCP server.
/// </summary>
public static class DockerCleanup
{
    public static string LogPath => Path.Combine(ConfigurationFiles.DataFolder, "docker", "pulizia.log");

    /// <summary>Why the cleanup must not start now; null if it can.</summary>
    public static string? BusyReason()
    {
        var compose = ProcessTable.Read([]).FirstOrDefault(p => ConsoleServices.Is(p, "docker-compose.exe")
            && !p.CommandLine.Contains(" watch", StringComparison.OrdinalIgnoreCase)
            && !p.CommandLine.Contains(" logs", StringComparison.OrdinalIgnoreCase));
        return compose is null ? null : $"docker compose is busy (PID {compose.ProcessId})";
    }

    /// <summary>
    /// When the app closes: reads the orphan images and launches the cleanup in a detached,
    /// windowless process that outlives the app; the outcome goes to <see cref="LogPath"/>.
    /// Returns what happened, or why it did not start.
    /// </summary>
    public static string StartDetached(TimeSpan timeout)
    {
        if (BusyReason() is { } busy)
        {
            return $"Docker cleanup skipped: {busy}";
        }

        using var cancel = new CancellationTokenSource(timeout);
        try
        {
            if (DockerCli.ServerVersionAsync(cancel.Token).GetAwaiter().GetResult() is null)
            {
                return "Docker cleanup skipped: Docker is not responding";
            }

            if (ConfigurationFiles.TryLoad(out var error) is not { } configuration)
            {
                return $"Docker cleanup skipped: config.json could not be read ({error})";
            }

            var (orphans, orphansError) = DockerMaintenance.OrphanImagesAsync(configuration, cancel.Token).GetAwaiter().GetResult();
            if (orphansError is not null)
            {
                return $"Docker cleanup skipped: images could not be read ({orphansError})";
            }

            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            DetachedLauncher.Start(DockerMaintenance.CleanupCommandLine(orphans, LogPath), ConfigurationFiles.DataFolder, newConsole: false, hidden: true);
            return $"Docker cleanup started: {orphans.Count} orphan images, build cache down to {DockerMaintenance.CacheLimit}";
        }
        catch (OperationCanceledException)
        {
            return "Docker cleanup skipped: Docker did not respond in time";
        }
    }

    /// <summary>The same cleanup inside the process, with the outcome in the log: for “Clean up now” in the “Docker” tab of the setup.</summary>
    public static async Task<string?> RunAsync(Action<string> log, CancellationToken ct = default)
    {
        if (BusyReason() is { } busy)
        {
            return busy;
        }

        if (ConfigurationFiles.TryLoad(out var error) is not { } configuration)
        {
            return $"config.json could not be read: {error}";
        }

        var (orphans, orphansError) = await DockerMaintenance.OrphanImagesAsync(configuration, ct).ConfigureAwait(false);
        if (orphansError is not null)
        {
            return $"images could not be read: {orphansError}";
        }

        log(await DockerMaintenance.RemoveImagesAsync(orphans, ct).ConfigureAwait(false));
        log(await DockerMaintenance.TrimBuildCacheAsync(ct).ConfigureAwait(false));
        return null;
    }
}

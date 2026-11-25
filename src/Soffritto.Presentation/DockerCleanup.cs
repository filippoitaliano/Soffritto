using Soffritto.Core.Configuration;
using Soffritto.Core.Docker;

namespace Soffritto.Presentation;

/// <summary>
/// La pulizia leggera di Docker: immagini degli ambienti che non esistono piu' e cache di build
/// oltre <see cref="DockerMaintenance.CacheLimit"/>. Non ferma Docker, non tocca i container e
/// non chiede permessi; salta quando un comando compose sta lavorando (build, up, down, stop),
/// lanciato dall'exe o da un altro processo come il server MCP.
/// </summary>
public static class DockerCleanup
{
    public static string LogPath => Path.Combine(ConfigurationFiles.DataFolder, "docker", "pulizia.log");

    /// <summary>Perche' la pulizia ora non deve partire; null se puo'.</summary>
    public static string? BusyReason()
    {
        var compose = ProcessTable.Read([]).FirstOrDefault(p => ConsoleServices.Is(p, "docker-compose.exe")
            && !p.CommandLine.Contains(" watch", StringComparison.OrdinalIgnoreCase)
            && !p.CommandLine.Contains(" logs", StringComparison.OrdinalIgnoreCase));
        return compose is null ? null : $"docker compose al lavoro (PID {compose.ProcessId})";
    }

    /// <summary>
    /// Alla chiusura dell'exe: legge le immagini orfane e lancia la pulizia in un processo
    /// staccato e senza finestra, che sopravvive all'exe; l'esito va in <see cref="LogPath"/>.
    /// Restituisce cosa e' successo, o perche' non e' partita.
    /// </summary>
    public static string StartDetached(TimeSpan timeout)
    {
        if (BusyReason() is { } busy)
        {
            return $"pulizia di Docker saltata: {busy}";
        }

        using var cancel = new CancellationTokenSource(timeout);
        try
        {
            if (DockerCli.ServerVersionAsync(cancel.Token).GetAwaiter().GetResult() is null)
            {
                return "pulizia di Docker saltata: Docker non risponde";
            }

            if (ConfigurationFiles.TryLoad(out var error) is not { } configuration)
            {
                return $"pulizia di Docker saltata: config.json non letta ({error})";
            }

            var (orphans, orphansError) = DockerMaintenance.OrphanImagesAsync(configuration, cancel.Token).GetAwaiter().GetResult();
            if (orphansError is not null)
            {
                return $"pulizia di Docker saltata: immagini non lette ({orphansError})";
            }

            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            DetachedLauncher.Start(DockerMaintenance.CleanupCommandLine(orphans, LogPath), ConfigurationFiles.DataFolder, newConsole: false, hidden: true);
            return $"pulizia di Docker avviata: {orphans.Count} immagini orfane, cache di build a {DockerMaintenance.CacheLimit}";
        }
        catch (OperationCanceledException)
        {
            return "pulizia di Docker saltata: Docker non ha risposto in tempo";
        }
    }

    /// <summary>La stessa pulizia dentro il processo, con l'esito nel log: per «Pulisci ora» nella scheda «Docker» del setup.</summary>
    public static async Task<string?> RunAsync(Action<string> log, CancellationToken ct = default)
    {
        if (BusyReason() is { } busy)
        {
            return busy;
        }

        if (ConfigurationFiles.TryLoad(out var error) is not { } configuration)
        {
            return $"config.json non letta: {error}";
        }

        var (orphans, orphansError) = await DockerMaintenance.OrphanImagesAsync(configuration, ct).ConfigureAwait(false);
        if (orphansError is not null)
        {
            return $"immagini non lette: {orphansError}";
        }

        log(await DockerMaintenance.RemoveImagesAsync(orphans, ct).ConfigureAwait(false));
        log(await DockerMaintenance.TrimBuildCacheAsync(ct).ConfigureAwait(false));
        return null;
    }
}

using System.ComponentModel;
using System.Diagnostics;

namespace Soffritto.Core.Docker;

/// <summary>
/// Il disco virtuale di Docker Desktop su WSL 2 (docker_data.vhdx): cresce con immagini e cache e
/// non si restringe da solo quando le si toglie. Compattarlo vuole Docker Desktop e WSL fermi e i
/// permessi di amministratore (Optimize-VHD, o diskpart dove Hyper-V manca).
/// </summary>
public static class DockerDisk
{
    private const int UserCancelled = 1223;

    /// <summary>Il file del disco nella posizione predefinita di Docker Desktop; null se non c'e'.</summary>
    public static string? VirtualDiskPath
    {
        get
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Docker", "wsl", "disk", "docker_data.vhdx");
            return File.Exists(path) ? path : null;
        }
    }

    /// <summary>La dimensione del file sul disco del PC, in byte; null se il file non c'e'.</summary>
    public static long? FileSize => VirtualDiskPath is { } path ? new FileInfo(path).Length : null;

    /// <summary>Le distribuzioni WSL accese oltre a quelle di Docker Desktop: wsl --shutdown fermerebbe anche loro.</summary>
    public static async Task<IReadOnlyList<string>> OtherRunningDistributionsAsync(CancellationToken ct = default)
    {
        var list = await ProcessRunner.RunAsync(
            "wsl.exe",
            "--list --running --quiet",
            Path.GetTempPath(),
            30,
            new Dictionary<string, string> { ["WSL_UTF8"] = "1" },
            cancellationToken: ct).ConfigureAwait(false);
        return list.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Replace("\0", string.Empty).Trim())
            .Where(name => name.Length > 0 && !name.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// Ferma Docker Desktop e WSL, compatta il disco con i permessi di amministratore (chiede
    /// l'UAC) e riaccende Docker Desktop. <paramref name="restartDocker"/> riceve il percorso di
    /// Docker Desktop.exe e lo riapre fuori dal processo chiamante. Errore o null.
    /// </summary>
    public static async Task<string?> CompactAsync(Action<string> log, Action<string> restartDocker, CancellationToken ct = default)
    {
        if (VirtualDiskPath is not { } disk)
        {
            return "disco di Docker Desktop non trovato (docker_data.vhdx)";
        }

        var before = new FileInfo(disk).Length;
        var desktop = DesktopExecutable();
        log($"disco di Docker: {Gigabytes(before)}");

        log("chiudo Docker Desktop");
        var cli = Path.Combine(Path.GetDirectoryName(desktop)!, "DockerCli.exe");
        if (File.Exists(cli))
        {
            await ProcessRunner.RunAsync(cli, "-Shutdown", Path.GetTempPath(), 60, cancellationToken: ct).ConfigureAwait(false);
        }

        await StopProcessesAsync(["Docker Desktop", "com.docker.backend", "com.docker.build"], ct).ConfigureAwait(false);

        log("fermo WSL (wsl --shutdown)");
        var shutdown = await ProcessRunner.RunAsync("wsl.exe", "--shutdown", Path.GetTempPath(), 120, cancellationToken: ct).ConfigureAwait(false);
        if (!shutdown.Success)
        {
            restartDocker(desktop);
            return $"wsl --shutdown non riuscito: {shutdown.Tail(2)}";
        }

        log("compatto il disco: conferma la richiesta di amministratore");
        var error = await CompactElevatedAsync(disk, ct).ConfigureAwait(false);

        var after = new FileInfo(disk).Length;
        log(error is null
            ? $"disco di Docker: da {Gigabytes(before)} a {Gigabytes(after)}, recuperati {Gigabytes(before - after)}"
            : $"compattazione non riuscita: {error}");

        log("riaccendo Docker Desktop");
        restartDocker(desktop);
        return error;
    }

    public static string Gigabytes(long bytes) => $"{bytes / 1024d / 1024 / 1024:0.0} GB";

    // Optimize-VHD c'e' con il modulo Hyper-V; senza, diskpart fa la stessa compattazione.
    private static async Task<string?> CompactElevatedAsync(string disk, CancellationToken ct)
    {
        var folder = Path.Combine(Path.GetTempPath(), "Soffritto");
        Directory.CreateDirectory(folder);
        var result = Path.Combine(folder, "compatta-docker.txt");
        var diskpart = Path.Combine(folder, "compatta-docker.diskpart");
        File.Delete(result);
        File.WriteAllText(diskpart, $"select vdisk file=\"{disk}\"\r\nattach vdisk readonly\r\ncompact vdisk\r\ndetach vdisk\r\n");

        var script =
            "try { "
            + "if (Get-Command Optimize-VHD -ErrorAction SilentlyContinue) "
            + $"{{ Optimize-VHD -Path '{disk}' -Mode Full -ErrorAction Stop }} "
            + $"else {{ $o = diskpart /s '{diskpart}'; if ($LASTEXITCODE -ne 0) {{ throw ($o -join ' ') }} }}; "
            + $"'ok' | Out-File -Encoding utf8 '{result}' "
            + $"}} catch {{ $_.Exception.Message | Out-File -Encoding utf8 '{result}' }}";

        try
        {
            using var process = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -Command \"{script.Replace("\"", "\\\"")}\"")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (process is null)
            {
                return "powershell da amministratore non avviato";
            }

            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == UserCancelled)
        {
            return "richiesta di amministratore annullata";
        }

        var outcome = File.Exists(result) ? File.ReadAllText(result).Trim() : "nessun esito dalla compattazione";
        return outcome == "ok" ? null : outcome;
    }

    private static string DesktopExecutable()
    {
        foreach (var process in Process.GetProcessesByName("Docker Desktop"))
        {
            try
            {
                if (process.MainModule?.FileName is { } path)
                {
                    return path;
                }
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
            }
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Docker", "Docker", "Docker Desktop.exe");
    }

    private static async Task StopProcessesAsync(IReadOnlyList<string> names, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 30 && names.Any(n => Process.GetProcessesByName(n).Length > 0); attempt++)
        {
            await Task.Delay(1000, ct).ConfigureAwait(false);
        }

        foreach (var process in names.SelectMany(Process.GetProcessesByName))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
            }
        }
    }
}

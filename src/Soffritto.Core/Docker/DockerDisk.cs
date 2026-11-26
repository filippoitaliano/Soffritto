using System.ComponentModel;
using System.Diagnostics;

namespace Soffritto.Core.Docker;

/// <summary>
/// Docker Desktop's virtual disk on WSL 2 (docker_data.vhdx): it grows with images and cache and
/// does not shrink on its own when they are removed. Compacting it requires Docker Desktop and WSL to be stopped and
/// administrator rights (Optimize-VHD, or diskpart where Hyper-V is missing).
/// </summary>
public static class DockerDisk
{
    private const int UserCancelled = 1223;

    /// <summary>The disk file in Docker Desktop's default location; null if it is not there.</summary>
    public static string? VirtualDiskPath
    {
        get
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Docker", "wsl", "disk", "docker_data.vhdx");
            return File.Exists(path) ? path : null;
        }
    }

    /// <summary>The file's size on the PC's disk, in bytes; null if the file is not there.</summary>
    public static long? FileSize => VirtualDiskPath is { } path ? new FileInfo(path).Length : null;

    /// <summary>The running WSL distributions other than Docker Desktop's: wsl --shutdown would stop them too.</summary>
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
    /// Stops Docker Desktop and WSL, compacts the disk with administrator rights (prompts
    /// UAC) and starts Docker Desktop again. <paramref name="restartDocker"/> receives the path of
    /// Docker Desktop.exe and reopens it outside the calling process. Error or null.
    /// </summary>
    public static async Task<string?> CompactAsync(Action<string> log, Action<string> restartDocker, CancellationToken ct = default)
    {
        if (VirtualDiskPath is not { } disk)
        {
            return "Docker Desktop disk not found (docker_data.vhdx)";
        }

        var before = new FileInfo(disk).Length;
        var desktop = DesktopExecutable();
        log($"Docker disk: {Gigabytes(before)}");

        log("closing Docker Desktop");
        var cli = Path.Combine(Path.GetDirectoryName(desktop)!, "DockerCli.exe");
        if (File.Exists(cli))
        {
            await ProcessRunner.RunAsync(cli, "-Shutdown", Path.GetTempPath(), 60, cancellationToken: ct).ConfigureAwait(false);
        }

        await StopProcessesAsync(["Docker Desktop", "com.docker.backend", "com.docker.build"], ct).ConfigureAwait(false);

        log("stopping WSL (wsl --shutdown)");
        var shutdown = await ProcessRunner.RunAsync("wsl.exe", "--shutdown", Path.GetTempPath(), 120, cancellationToken: ct).ConfigureAwait(false);
        if (!shutdown.Success)
        {
            restartDocker(desktop);
            return $"wsl --shutdown failed: {shutdown.Tail(2)}";
        }

        log("compacting the disk: confirm the administrator prompt");
        var error = await CompactElevatedAsync(disk, ct).ConfigureAwait(false);

        var after = new FileInfo(disk).Length;
        log(error is null
            ? $"Docker disk: from {Gigabytes(before)} to {Gigabytes(after)}, {Gigabytes(before - after)} reclaimed"
            : $"compaction failed: {error}");

        log("starting Docker Desktop again");
        restartDocker(desktop);
        return error;
    }

    public static string Gigabytes(long bytes) => $"{bytes / 1024d / 1024 / 1024:0.0} GB";

    // Optimize-VHD comes with the Hyper-V module; without it, diskpart does the same compaction.
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
                return "administrator PowerShell did not start";
            }

            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == UserCancelled)
        {
            return "administrator prompt cancelled";
        }

        var outcome = File.Exists(result) ? File.ReadAllText(result).Trim() : "no result from the compaction";
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

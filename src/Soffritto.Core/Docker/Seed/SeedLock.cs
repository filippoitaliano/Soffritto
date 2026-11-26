using System.Diagnostics;
using Soffritto.Core.Configuration;

namespace Soffritto.Core.Docker.Seed;

/// <summary>
/// Only one "Verify seed" or "Align seed" at a time per data folder: they use the same
/// temporary container and the same port. An in-process flag, and seed.lock with the PID across processes.
/// </summary>
public static class SeedLock
{
    private static readonly object Gate = new();

    private static bool held;

    public static string LockPath => Path.Combine(ConfigurationFiles.DataFolder, "docker", "seed.lock");

    /// <summary>The lock, to release with Dispose; or the error with the PID of whoever holds it.</summary>
    public static (IDisposable? Lock, string? Error) Acquire()
    {
        lock (Gate)
        {
            var self = Environment.ProcessId;
            if (held)
            {
                return (null, $"a seed verification or alignment is already running (PID {self})");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(LockPath)!);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    using (var stream = new FileStream(LockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    using (var writer = new StreamWriter(stream))
                    {
                        writer.Write(self);
                    }

                    held = true;
                    return (new Releaser(), null);
                }
                catch (IOException) when (File.Exists(LockPath))
                {
                    if (OwnerOf(LockPath) is { } owner && IsAlive(owner))
                    {
                        return (null, $"a seed verification or alignment is already running (PID {owner})");
                    }

                    // Dead PID or unreadable file: the lock was left behind by an interrupted run.
                    TryDelete(LockPath);
                }
            }

            return (null, $"cannot create {LockPath}");
        }
    }

    private static int? OwnerOf(string path)
    {
        try
        {
            return int.TryParse(File.ReadAllText(path).Trim(), out var pid) ? pid : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed class Releaser : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            lock (Gate)
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                TryDelete(LockPath);
                held = false;
            }
        }
    }
}

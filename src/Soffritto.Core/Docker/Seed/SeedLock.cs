using System.Diagnostics;
using Soffritto.Core.Configuration;

namespace Soffritto.Core.Docker.Seed;

/// <summary>
/// Una sola «Verifica seed» o «Allinea seed» alla volta per cartella dati: usano lo stesso
/// container temporaneo e la stessa porta. Flag nel processo e seed.lock con il PID tra processi.
/// </summary>
public static class SeedLock
{
    private static readonly object Gate = new();

    private static bool held;

    public static string LockPath => Path.Combine(ConfigurationFiles.DataFolder, "docker", "seed.lock");

    /// <summary>Il lock, da rilasciare con Dispose; oppure l'errore con il PID di chi lo tiene.</summary>
    public static (IDisposable? Lock, string? Error) Acquire()
    {
        lock (Gate)
        {
            var self = Environment.ProcessId;
            if (held)
            {
                return (null, $"c'e' gia' una verifica o un allineamento in corso (PID {self})");
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
                        return (null, $"c'e' gia' una verifica o un allineamento in corso (PID {owner})");
                    }

                    // PID morto o file illeggibile: il lock e' rimasto da un'esecuzione interrotta.
                    TryDelete(LockPath);
                }
            }

            return (null, $"{LockPath} non si crea");
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

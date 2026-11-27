namespace Soffritto.Core;

/// <summary>
/// A cross-process lock on a lock file in the data folder: the app, VSIX, CLI and MCP server
/// take it before writing the same state file. The lock is the file opened without
/// sharing; it is released by <see cref="Dispose"/> or when the process ends.
/// </summary>
public sealed class CrossProcessLock : IDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(50);

    private readonly FileStream stream;

    private CrossProcessLock(FileStream stream) => this.stream = stream;

    /// <summary>Waits for the lock up to <paramref name="timeout"/>; then <see cref="TimeoutException"/>.</summary>
    public static CrossProcessLock Acquire(string lockPath, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (TryOpen(lockPath) is { } stream)
            {
                return new CrossProcessLock(stream);
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException($"{lockPath} has been held by another process for more than {timeout.TotalSeconds:0} s");
            }

            Thread.Sleep(RetryDelay);
        }
    }

    /// <summary>Takes the lock only if it is free right now; null if another process holds it.</summary>
    public static CrossProcessLock? TryAcquire(string lockPath)
        => TryOpen(lockPath) is { } stream ? new CrossProcessLock(stream) : null;

    /// <summary>Like <see cref="Acquire"/>, without blocking the thread while waiting.</summary>
    public static async Task<CrossProcessLock> AcquireAsync(string lockPath, TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (TryOpen(lockPath) is { } stream)
            {
                return new CrossProcessLock(stream);
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException($"{lockPath} has been held by another process for more than {timeout.TotalSeconds:0} s");
            }

            await Task.Delay(RetryDelay, ct).ConfigureAwait(false);
        }
    }

    public void Dispose() => stream.Dispose();

    private static FileStream? TryOpen(string lockPath)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
            return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

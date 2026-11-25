namespace Soffritto.Core;

/// <summary>
/// Un lock fra processi su un file di lock della cartella dati: exe, VSIX, CLI e server MCP lo
/// prendono prima di scrivere lo stesso file di stato. Il lock e' il file aperto senza
/// condivisione; si rilascia con <see cref="Dispose"/> o con la fine del processo.
/// </summary>
public sealed class CrossProcessLock : IDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(50);

    private readonly FileStream stream;

    private CrossProcessLock(FileStream stream) => this.stream = stream;

    /// <summary>Attende il lock fino a <paramref name="timeout"/>; poi <see cref="TimeoutException"/>.</summary>
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
                throw new TimeoutException($"{lockPath} e' tenuto da un altro processo da piu' di {timeout.TotalSeconds:0} s");
            }

            Thread.Sleep(RetryDelay);
        }
    }

    /// <summary>Come <see cref="Acquire"/>, senza bloccare il thread durante l'attesa.</summary>
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
                throw new TimeoutException($"{lockPath} e' tenuto da un altro processo da piu' di {timeout.TotalSeconds:0} s");
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

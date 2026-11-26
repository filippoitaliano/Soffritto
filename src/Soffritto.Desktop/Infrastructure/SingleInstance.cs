using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Soffritto.Desktop.Infrastructure;

/// <summary>
/// A single exe per root folder: the X hides the window, and whoever reopens from Start wants it
/// back, not a second copy that rereads the same repositories and fights over the playwright-cli
/// dashboard. The name includes the exe path, so a Debug build coexists with the published one.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const int AnyProcess = -1;

    private readonly Mutex mutex;
    private readonly EventWaitHandle showRequested;
    private RegisteredWaitHandle? listener;

    private SingleInstance(Mutex mutex, EventWaitHandle showRequested)
    {
        this.mutex = mutex;
        this.showRequested = showRequested;
    }

    /// <summary>Null if the copy was already open: in that case it has been asked to show itself.</summary>
    public static SingleInstance? Acquire(string root)
    {
        var name = NameOf(root);
        var mutex = new Mutex(false, name, out var isFirst);
        if (isFirst)
        {
            return new SingleInstance(mutex, new EventWaitHandle(false, EventResetMode.AutoReset, name + ".Show"));
        }

        mutex.Dispose();
        try
        {
            using var show = EventWaitHandle.OpenExisting(name + ".Show");

            // The window belongs to the other process: without this permission Windows only makes it flash.
            AllowSetForegroundWindow(AnyProcess);
            show.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // The other copy is starting or closing right now.
        }

        return null;
    }

    /// <summary>Called from a thread-pool thread at every later launch.</summary>
    public void OnShowRequested(Action action)
        => listener = ThreadPool.RegisterWaitForSingleObject(showRequested, (_, _) => action(), null, Timeout.Infinite, executeOnlyOnce: false);

    public void Dispose()
    {
        listener?.Unregister(null);
        showRequested.Dispose();
        mutex.Dispose();
    }

    private static string NameOf(string root)
    {
        var key = $"{Environment.ProcessPath}|{Path.GetFullPath(root)}".ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
        return $@"Local\Soffritto.Desktop.{hash}";
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}

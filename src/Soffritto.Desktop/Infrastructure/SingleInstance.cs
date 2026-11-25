using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Soffritto.Desktop.Infrastructure;

/// <summary>
/// Un solo exe per cartella radice: la X nasconde la finestra, e chi riapre da Start vuole
/// ritrovarla, non una seconda copia che rilegge gli stessi repository e si contende la dashboard
/// di playwright-cli. Il nome include il percorso dell'exe, cosi' una build Debug convive con
/// quella pubblicata.
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

    /// <summary>Null se la copia era gia' aperta: in quel caso le e' stato chiesto di mostrarsi.</summary>
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

            // La finestra e' dell'altro processo: senza questo permesso Windows la fa solo lampeggiare.
            AllowSetForegroundWindow(AnyProcess);
            show.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // L'altra copia si sta avviando o chiudendo proprio ora.
        }

        return null;
    }

    /// <summary>Chiamata da un thread del pool a ogni avvio successivo.</summary>
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

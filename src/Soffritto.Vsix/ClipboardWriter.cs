using System.Runtime.InteropServices;
using Soffritto.Presentation;

namespace Soffritto.Vsix;

/// <summary>
/// Clipboard via Win32 puro: l'host out-of-process dell'estensione non carica WPF ne'
/// WinForms, quindi System.Windows.Clipboard non e' disponibile.
/// </summary>
internal static class ClipboardWriter
{
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    public static bool TrySetText(string text)
    {
        // La clipboard e' una risorsa globale contesa: un altro processo puo' tenerla
        // aperta per qualche millisecondo, quindi si ritenta invece di fallire subito.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (TryOnce(text))
            {
                return true;
            }

            Thread.Sleep(50);
        }

        return false;
    }

    private static bool TryOnce(string text)
    {
        if (!OpenClipboard(IntPtr.Zero))
        {
            return false;
        }

        try
        {
            if (!EmptyClipboard())
            {
                return false;
            }

            var handle = GlobalAlloc(GMEM_MOVEABLE, (nuint)((text.Length + 1) * sizeof(char)));
            if (handle == IntPtr.Zero)
            {
                return false;
            }

            var target = GlobalLock(handle);
            if (target == IntPtr.Zero)
            {
                GlobalFree(handle);
                return false;
            }

            try
            {
                Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
                Marshal.WriteInt16(target, text.Length * sizeof(char), 0);
            }
            finally
            {
                GlobalUnlock(handle);
            }

            if (SetClipboardData(CF_UNICODETEXT, handle) == IntPtr.Zero)
            {
                GlobalFree(handle);
                return false;
            }

            // A SetClipboardData riuscita la memoria appartiene al sistema: niente free.
            return true;
        }
        finally
        {
            CloseClipboard();
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll")]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalAlloc(uint uFlags, nuint dwBytes);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr hMem);
}

internal sealed class Win32Clipboard : IClipboard
{
    public bool TrySetText(string text) => ClipboardWriter.TrySetText(text);
}

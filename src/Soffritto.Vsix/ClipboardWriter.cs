using System.Runtime.InteropServices;
using Soffritto.Presentation;

namespace Soffritto.Vsix;

/// <summary>
/// Clipboard through plain Win32: the extension's out-of-process host loads neither WPF nor
/// WinForms, so System.Windows.Clipboard is not available.
/// </summary>
internal static class ClipboardWriter
{
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    public static bool TrySetText(string text)
    {
        // The clipboard is a contended global resource: another process may keep it
        // open for a few milliseconds, so retry instead of failing right away.
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

            // Once SetClipboardData succeeds the memory belongs to the system: no free.
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

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Soffritto.Desktop.Infrastructure;

/// <summary>
/// Windows title bar in the footer colour, with a white title and the app's colour icon.
/// Caption colours via DWM: Windows 11 or later; on earlier versions the call has no effect
/// and the system bar stays.
/// </summary>
internal static class TitleBar
{
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;
    private const int White = 0x00FFFFFF;

    /// <summary>Call once the window is created (SourceInitialized), when the handle exists.</summary>
    public static void Apply(Window window, Color caption)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var text = White;
        DwmSetWindowAttribute(hwnd, DwmwaTextColor, ref text, sizeof(int));
        SetCaption(window, caption);
    }

    public static void SetCaption(Window window, Color caption)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        // COLORREF is 0x00BBGGRR.
        var colorRef = caption.R | (caption.G << 8) | (caption.B << 16);
        DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref colorRef, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

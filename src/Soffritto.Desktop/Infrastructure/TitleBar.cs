using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Soffritto.Desktop.Infrastructure;

/// <summary>
/// Barra del titolo di Windows nel colore del footer, con titolo bianco e l'icona a colori dell'exe.
/// Colori della caption via DWM: Windows 11 o successivo; su versioni precedenti la chiamata non
/// ha effetto e resta la barra di sistema.
/// </summary>
internal static class TitleBar
{
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;
    private const int White = 0x00FFFFFF;

    /// <summary>Da chiamare a finestra creata (SourceInitialized), quando esiste l'handle.</summary>
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

        // COLORREF e' 0x00BBGGRR.
        var colorRef = caption.R | (caption.G << 8) | (caption.B << 16);
        DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref colorRef, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

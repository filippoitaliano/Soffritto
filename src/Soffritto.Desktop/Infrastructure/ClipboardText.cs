using System.Runtime.InteropServices;
using System.Windows;
using Soffritto.Presentation;

namespace Soffritto.Desktop.Infrastructure;

public sealed class ClipboardText : IClipboard
{
    public bool TrySetText(string text)
    {
        var copied = false;

        UiThread.Run(() =>
        {
            // The clipboard is a contended global resource: another process may keep it
            // open for a few milliseconds, so retry instead of failing right away.
            for (var attempt = 0; attempt < 5 && !copied; attempt++)
            {
                try
                {
                    Clipboard.SetText(text);
                    copied = true;
                }
                catch (ExternalException)
                {
                    Thread.Sleep(50);
                }
            }
        });

        return copied;
    }
}

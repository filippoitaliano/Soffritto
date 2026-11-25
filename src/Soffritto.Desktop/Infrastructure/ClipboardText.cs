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
            // La clipboard e' una risorsa globale contesa: un altro processo puo' tenerla
            // aperta per qualche millisecondo, quindi si ritenta invece di fallire subito.
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

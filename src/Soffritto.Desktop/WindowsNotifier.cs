using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Soffritto.Desktop.ViewModels;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Soffritto.Desktop;

/// <summary>
/// Il consumer Windows delle notifiche PR: fumetto dell'icona di sistema per ogni notifica nuova,
/// lampeggio della barra delle applicazioni finche' la finestra non torna in primo piano, e un
/// pallino rosso sull'icona di sistema finche' resta qualcosa da leggere. Ha la sua
/// <see cref="PullRequestNotifications.Listen"/>, indipendente da quella del gattino.
/// L'icona di sistema e' anche la via per riaprire la finestra nascosta dalla X e, dal suo menu,
/// per chiudere davvero l'exe.
/// </summary>
public sealed class WindowsNotifier : IDisposable
{
    // Il testo del fumetto di Shell_NotifyIcon si ferma a 255 caratteri.
    private const int MaxTextLength = 255;

    private readonly Window window;
    private readonly string title;
    private readonly PullRequestNotifications notifications;
    private readonly Func<IReadOnlyList<string>> listen;
    private readonly Forms.NotifyIcon icon;
    private readonly Drawing.Icon? plainIcon;
    private readonly Drawing.Icon? badgeIcon;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };

    public WindowsNotifier(Window window, string title, PullRequestNotifications notifications)
    {
        this.window = window;
        this.title = title;
        this.notifications = notifications;
        listen = notifications.Listen();

        // In Debug l'exe ha gia' l'icona di lava e roccia (app-debug.ico, vedi il csproj).
        plainIcon = Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        badgeIcon = plainIcon is null ? null : WithBadge(plainIcon);

        // Windows mostra la notifica solo se l'icona di sistema e' visibile.
        icon = new Forms.NotifyIcon { Visible = true };
        icon.ContextMenuStrip = new Forms.ContextMenuStrip();
        icon.ContextMenuStrip.Items.Add("Esci", null, (_, _) => ExitRequested?.Invoke());
        icon.BalloonTipClicked += (_, _) => NotificationOpened?.Invoke();
        icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
            {
                BringToFront();
            }
        };

        ShowCount();
        notifications.PropertyChanged += OnNotificationsChanged;
        timer.Tick += (_, _) => Notify();
    }

    public event Action? ExitRequested;

    /// <summary>Clic sul fumetto: chi ospita la finestra la porta davanti col pannello delle notifiche.</summary>
    public event Action? NotificationOpened;

    /// <summary>
    /// Vero durante la demo: niente fumetti sopra i dati finti. Le notifiche restano da dire e
    /// arrivano tutte insieme al ritorno agli ambienti veri.
    /// </summary>
    public bool IsPaused { get; set; }

    public void Start()
    {
        Notify();
        timer.Start();
    }

    public void Dispose()
    {
        timer.Stop();
        notifications.PropertyChanged -= OnNotificationsChanged;

        // Senza Visible = false l'icona resta nell'area di notifica finche' non ci passa sopra il mouse.
        icon.Visible = false;
        icon.ContextMenuStrip?.Dispose();
        icon.Dispose();
        plainIcon?.Dispose();
        badgeIcon?.Dispose();
    }

    public void BringToFront()
    {
        window.Show();
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Maximized;
        }

        window.Activate();
    }

    private void OnNotificationsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PullRequestNotifications.UnreadCount))
        {
            ShowCount();
        }
    }

    // NotifyIcon.Text lancia oltre i 127 caratteri, e il titolo porta il percorso della cartella
    // dati quando viene da SOFFRITTO_DATA: si accorcia il titolo, il conteggio resta intero.
    private void ShowCount()
    {
        const int MaxLength = 127;

        var count = notifications.UnreadCount;
        icon.Icon = count > 0 && badgeIcon is not null ? badgeIcon : plainIcon;

        var suffix = count switch
        {
            0 => string.Empty,
            1 => " · 1 notifica",
            _ => $" · {count} notifiche",
        };
        var room = MaxLength - suffix.Length;
        icon.Text = (title.Length > room ? title[..(room - 1)] + "…" : title) + suffix;
    }

    // Le notifiche nuove dello stesso giro vanno in un fumetto solo: al ritorno dalla demo o
    // dopo un'assenza lunga possono arrivarne parecchie insieme.
    private void Notify()
    {
        if (IsPaused)
        {
            return;
        }

        var fresh = listen();
        if (fresh.Count == 0)
        {
            return;
        }

        icon.ShowBalloonTip(0, title, Join(fresh), Forms.ToolTipIcon.None);
        Flash();
    }

    // Lampeggia finche' la finestra non torna in primo piano; se e' gia' davanti, Windows non
    // fa niente. A finestra nascosta il bottone non c'e': resta il pallino sull'icona di sistema.
    private void Flash()
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || !window.IsVisible)
        {
            return;
        }

        var info = new FlashInfo
        {
            Size = (uint)Marshal.SizeOf<FlashInfo>(),
            Window = hwnd,
            Flags = FlashTray | FlashUntilForeground,
        };
        FlashWindowEx(ref info);
    }

    private static string Join(IReadOnlyList<string> messages)
    {
        var text = messages[0];
        for (var i = 1; i < messages.Count; i++)
        {
            var more = $"\n… e altre {messages.Count - i}";
            var next = $"{text}\n{messages[i]}";
            if (next.Length + (i < messages.Count - 1 ? more.Length : 0) > MaxTextLength)
            {
                return text + more;
            }

            text = next;
        }

        return text.Length > MaxTextLength ? text[..(MaxTextLength - 1)] + "…" : text;
    }

    // Il pallino in alto a destra, col bordo bianco perche' si stacchi anche sulla barra scura:
    // l'area di notifica rimpicciolisce l'icona a 16 px, quindi deve occuparne quasi meta'.
    private static Drawing.Icon WithBadge(Drawing.Icon source)
    {
        using var bitmap = new Drawing.Bitmap(source.ToBitmap());
        using (var graphics = Drawing.Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var size = bitmap.Width * 0.5f;
            var x = bitmap.Width - size - 1;
            using var border = new Drawing.SolidBrush(Drawing.Color.White);
            using var fill = new Drawing.SolidBrush(Drawing.Color.FromArgb(0xC4, 0x2B, 0x1C));
            graphics.FillEllipse(border, x - 1.5f, 0, size + 3, size + 3);
            graphics.FillEllipse(fill, x, 1.5f, size, size);
        }

        var handle = bitmap.GetHicon();
        try
        {
            return (Drawing.Icon)Drawing.Icon.FromHandle(handle).Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private const uint FlashTray = 0x2;
    private const uint FlashUntilForeground = 0xC;

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public IntPtr Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FlashInfo info);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}

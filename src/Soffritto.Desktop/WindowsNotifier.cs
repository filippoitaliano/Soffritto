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
/// The Windows consumer of PR notifications: a tray icon balloon for every new notification, a
/// flashing taskbar button until the window comes back to the foreground, and a red dot on the tray
/// icon while something is still unread. It has its own <see cref="PullRequestNotifications.Listen"/>,
/// independent of the kitten's. The tray icon is also the way to reopen the window hidden by the X
/// and, from its menu, to really quit the app.
/// </summary>
public sealed class WindowsNotifier : IDisposable
{
    // The Shell_NotifyIcon balloon text stops at 255 characters.
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

        // In Debug the exe already has the lava-and-rock icon (app-debug.ico, see the csproj).
        plainIcon = Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        badgeIcon = plainIcon is null ? null : WithBadge(plainIcon);

        // Windows shows the notification only if the tray icon is visible.
        icon = new Forms.NotifyIcon { Visible = true };
        icon.ContextMenuStrip = new Forms.ContextMenuStrip();
        icon.ContextMenuStrip.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke());
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

    /// <summary>Balloon clicked: the window host brings the window to the front with the notifications panel.</summary>
    public event Action? NotificationOpened;

    /// <summary>
    /// True during the demo: no balloons over the fake data. The notifications stay pending and
    /// arrive all together on returning to the real environments.
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

        // Without Visible = false the icon lingers in the notification area until the mouse passes over it.
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

    // NotifyIcon.Text throws beyond 127 characters, and the title carries the data folder path when
    // it comes from SOFFRITTO_DATA: the title is shortened, the count stays whole.
    private void ShowCount()
    {
        const int MaxLength = 127;

        var count = notifications.UnreadCount;
        icon.Icon = count > 0 && badgeIcon is not null ? badgeIcon : plainIcon;

        var suffix = count switch
        {
            0 => string.Empty,
            1 => " · 1 notification",
            _ => $" · {count} notifications",
        };
        var room = MaxLength - suffix.Length;
        icon.Text = (title.Length > room ? title[..(room - 1)] + "…" : title) + suffix;
    }

    // New notifications from the same round go into a single balloon: on returning from the demo
    // or after a long absence, quite a few can arrive at once.
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

    // Flashes until the window comes back to the foreground; if it is already in front, Windows does
    // nothing. With the window hidden there is no taskbar button: the dot on the tray icon remains.
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
            var more = $"\n… and {messages.Count - i} more";
            var next = $"{text}\n{messages[i]}";
            if (next.Length + (i < messages.Count - 1 ? more.Length : 0) > MaxTextLength)
            {
                return text + more;
            }

            text = next;
        }

        return text.Length > MaxTextLength ? text[..(MaxTextLength - 1)] + "…" : text;
    }

    // The dot in the top right, with a white border so it stands out on a dark taskbar too: the
    // notification area shrinks the icon to 16 px, so the dot must take up almost half of it.
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

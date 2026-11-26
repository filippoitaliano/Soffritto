using System.Runtime.InteropServices;

namespace Soffritto.Desktop.Infrastructure;

/// <summary>
/// Global keyboard hook that recognises Win+Shift+S (the Windows Snipping Tool) even with the
/// window in the background. It only observes: the key goes on to Windows.
/// </summary>
/// <remarks>
/// A low-level hook is called back on the thread that installs it, which must pump messages: it has
/// to be created on the UI thread. If the hook does not return within Windows' timeout, Windows drops it,
/// so the callback raises the event and returns right away.
/// </remarks>
internal sealed class SnipShortcutHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int VkS = 0x53;
    private const int VkShift = 0x10;
    private const int VkLWin = 0x5B;
    private const int VkRWin = 0x5C;

    // Reference kept alive: the delegate passed to SetWindowsHookEx must not be collected.
    private readonly HookProc callback;
    private IntPtr hook;

    public SnipShortcutHook()
    {
        callback = OnKey;
        hook = SetWindowsHookExW(WhKeyboardLl, callback, GetModuleHandleW(null), 0);
    }

    public event Action? Pressed;

    public void Dispose()
    {
        if (hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(hook);
            hook = IntPtr.Zero;
        }
    }

    private IntPtr OnKey(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0
            && (message == WmKeyDown || message == WmSysKeyDown)
            && Marshal.ReadInt32(data) == VkS
            && IsDown(VkShift)
            && (IsDown(VkLWin) || IsDown(VkRWin)))
        {
            Pressed?.Invoke();
        }

        return CallNextHookEx(hook, code, message, data);
    }

    private static bool IsDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);
}

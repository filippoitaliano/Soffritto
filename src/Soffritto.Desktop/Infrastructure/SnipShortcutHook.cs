using System.Runtime.InteropServices;

namespace Soffritto.Desktop.Infrastructure;

/// <summary>
/// Hook di tastiera globale che riconosce Win+Shift+S (lo strumento di cattura di Windows) anche
/// con la finestra in secondo piano. Si limita a osservare: il tasto prosegue verso Windows.
/// </summary>
/// <remarks>
/// Un hook low-level viene richiamato sul thread che lo installa, che deve pompare messaggi: va
/// creato sul thread della UI. Se Windows non lo chiama entro il suo timeout lo scarta, quindi la
/// callback solleva l'evento e torna subito.
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

    // Riferimento tenuto in vita: il delegato passato a SetWindowsHookEx non deve essere raccolto.
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

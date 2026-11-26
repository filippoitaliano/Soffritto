using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Soffritto.Presentation;

/// <summary>
/// Starts processes that must outlive whoever launches them: outside the job the host runs in
/// (the kill-on-close job of the Desktop app, or those of the Visual Studio extension host);
/// otherwise closing it would also close Visual Studio (with unsaved work) or the consoles
/// of Kestrel, dev server and app.
/// </summary>
public static class DetachedLauncher
{
    private const uint CreateNewConsole = 0x00000010;
    private const uint CreateNoWindow = 0x08000000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateBreakawayFromJob = 0x01000000;
    private const int ErrorAccessDenied = 5;

    // Variables the Desktop app sets for its own children (see App.OnStartup) or that a developer
    // shell may have left behind: detached processes must start with the normal environment.
    private static readonly string[] StrippedVariables =
    [
        "MSBUILDDISABLENODEREUSE",
        "DOTNET_CLI_USE_MSBUILD_SERVER",
        "UseSharedCompilation",
        "NoDefaultCurrentDirectoryInExePath",
    ];

    /// <summary>
    /// Starts <paramref name="commandLine"/> in the given folder. Returns false if the
    /// process started but inside the job (the host's job does not allow breakaway): in that
    /// case it will close along with the host. With <paramref name="hidden"/> a console program
    /// runs without a window.
    /// </summary>
    public static bool Start(string commandLine, string workingDirectory, bool newConsole, bool hidden = false)
    {
        var flags = CreateUnicodeEnvironment | (newConsole ? CreateNewConsole : 0) | (hidden && !newConsole ? CreateNoWindow : 0);

        if (TryCreate(commandLine, workingDirectory, flags | CreateBreakawayFromJob, out var error))
        {
            return true;
        }

        if (error != ErrorAccessDenied)
        {
            throw new Win32Exception(error);
        }

        if (!TryCreate(commandLine, workingDirectory, flags, out error))
        {
            throw new Win32Exception(error);
        }

        return false;
    }

    private static bool TryCreate(string commandLine, string workingDirectory, uint flags, out int error)
    {
        var startup = new STARTUPINFOW { cb = Marshal.SizeOf<STARTUPINFOW>() };
        var command = new StringBuilder(commandLine);

        if (CreateProcessW(null, command, IntPtr.Zero, IntPtr.Zero, false, flags, EnvironmentBlock(), workingDirectory, ref startup, out var info))
        {
            CloseHandle(info.hProcess);
            CloseHandle(info.hThread);
            error = 0;
            return true;
        }

        error = Marshal.GetLastWin32Error();
        return false;
    }

    private static string EnvironmentBlock()
    {
        var variables = Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .Select(e => ((string)e.Key, (string?)e.Value ?? string.Empty))
            .Where(e => !StrippedVariables.Contains(e.Item1, StringComparer.OrdinalIgnoreCase))
            .OrderBy(e => e.Item1, StringComparer.OrdinalIgnoreCase);

        var block = new StringBuilder();
        foreach (var (name, value) in variables)
        {
            block.Append(name).Append('=').Append(value).Append('\0');
        }

        return block.Append('\0').ToString();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFOW
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(
        string? applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        bool inheritHandles,
        uint creationFlags,
        string environment,
        string currentDirectory,
        ref STARTUPINFOW startupInfo,
        out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}

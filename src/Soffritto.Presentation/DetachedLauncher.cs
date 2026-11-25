using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Soffritto.Presentation;

/// <summary>
/// Avvia processi che devono sopravvivere a chi li lancia: fuori dal job in cui gira l'host
/// (il job kill-on-close dell'exe Desktop, o quelli dell'host delle estensioni di Visual Studio),
/// altrimenti chiuderlo chiuderebbe anche Visual Studio (con il lavoro non salvato) o le console
/// di Kestrel, dev server e app.
/// </summary>
public static class DetachedLauncher
{
    private const uint CreateNewConsole = 0x00000010;
    private const uint CreateNoWindow = 0x08000000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateBreakawayFromJob = 0x01000000;
    private const int ErrorAccessDenied = 5;

    // Variabili che l'exe Desktop imposta per i propri figli (vedi App.OnStartup) o che una shell
    // di sviluppo puo' aver lasciato: i processi staccati devono partire con l'ambiente normale.
    private static readonly string[] StrippedVariables =
    [
        "MSBUILDDISABLENODEREUSE",
        "DOTNET_CLI_USE_MSBUILD_SERVER",
        "UseSharedCompilation",
        "NoDefaultCurrentDirectoryInExePath",
    ];

    /// <summary>
    /// Avvia <paramref name="commandLine"/> nella cartella indicata. Restituisce false se il
    /// processo e' partito ma dentro il job (il job dell'host non consente il breakaway): in quel
    /// caso si chiudera' insieme all'host. Con <paramref name="hidden"/> un programma da console
    /// gira senza finestra.
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

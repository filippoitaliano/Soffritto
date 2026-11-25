using System.Reflection;
using System.Text;
using Soffritto.Presentation;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace Soffritto.Desktop.Infrastructure;

/// <summary>
/// L'installazione fatta con il Setup.exe di Velopack: prima apertura dopo il setup e
/// aggiornamenti dalla release. Una build lanciata da bin\ o da publish\ non e' installata e qui
/// non fa niente.
/// </summary>
public static class Installation
{
    private static VelopackAsset? pending;
    private static bool applyOnExit = true;

    /// <summary>
    /// Update.exe deve sopravvivere all'exe che lo lancia: il job kill-on-close dell'exe
    /// (ChildProcessJob) lo chiuderebbe proprio mentre aspetta quell'uscita per installare.
    /// </summary>
    public static IVelopackLocator Locator { get; } = new WindowsVelopackLocator(new DetachedProcess(), null);

    /// <summary>Prima apertura dopo il Setup.exe: il setup parte dalla scheda Componenti.</summary>
    public static bool IsFirstRun { get; set; }

    public static bool IsInstalled => Locator.CurrentlyInstalledVersion is not null;

    /// <summary>La release da cui arrivano gli aggiornamenti: la fissa release.ps1 al momento del pacchetto.</summary>
    public static string? UpdateRepository { get; } = typeof(Installation).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "UpdateRepository")?.Value is { Length: > 0 } url ? url : null;

    /// <summary>La versione scaricata e non ancora installata, null se non c'e'.</summary>
    public static string? PendingVersion => pending?.Version.ToString();

    /// <summary>Cerca e scarica l'ultima release. Restituisce il testo da mostrare.</summary>
    public static async Task<string> CheckForUpdatesAsync()
    {
        if (!IsInstalled)
        {
            return "Questa build non viene dal Setup.exe: si aggiorna ricompilandola.";
        }

        if (UpdateRepository is null)
        {
            return "Il pacchetto non indica una release da cui aggiornarsi.";
        }

        var manager = new UpdateManager(new GithubSource(UpdateRepository, accessToken: null, prerelease: false), locator: Locator);
        var update = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
        if (update is null)
        {
            return pending is null
                ? $"v{Locator.CurrentlyInstalledVersion}: è la versione più recente."
                : PendingText();
        }

        await manager.DownloadUpdatesAsync(update).ConfigureAwait(false);
        pending = update.TargetFullRelease;
        return PendingText();
    }

    private static string PendingText() => $"La v{pending!.Version} è scaricata: si installa alla chiusura dell'exe.";

    /// <summary>
    /// Il riavvio dopo un Salva del setup apre subito l'exe nuovo: installare in quel momento
    /// sostituirebbe i file sotto i piedi della copia appena partita.
    /// </summary>
    public static void SkipApplyOnExit() => applyOnExit = false;

    public static void ApplyOnExit()
    {
        if (!applyOnExit || pending is null || !IsInstalled)
        {
            return;
        }

        try
        {
            new UpdateManager(new GithubSource(UpdateRepository!, accessToken: null, prerelease: false), locator: Locator)
                .WaitExitThenApplyUpdates(pending, silent: true, restart: false);
        }
        catch
        {
            // L'aggiornamento resta scaricato: Velopack lo riprende al prossimo avvio.
        }
    }

    private sealed class DetachedProcess : IProcessImpl
    {
        public string GetCurrentProcessPath() => Environment.ProcessPath!;

        public uint GetCurrentProcessId() => (uint)Environment.ProcessId;

        public void StartProcess(string exePath, IEnumerable<string> args, string workDir, bool showWindow)
        {
            var command = string.Join(' ', new[] { exePath }.Concat(args).Select(Quote));
            DetachedLauncher.Start(command, workDir, newConsole: false, hidden: !showWindow);
        }

        public void Exit(int exitCode) => Environment.Exit(exitCode);

        // Le regole di CommandLineToArgvW: le barre prima di una virgoletta si raddoppiano.
        private static string Quote(string arg)
        {
            if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '"']) < 0)
            {
                return arg;
            }

            var quoted = new StringBuilder("\"");
            var backslashes = 0;
            foreach (var c in arg)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }

                quoted.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes).Append(c);
                backslashes = 0;
            }

            return quoted.Append('\\', backslashes * 2).Append('"').ToString();
        }
    }
}

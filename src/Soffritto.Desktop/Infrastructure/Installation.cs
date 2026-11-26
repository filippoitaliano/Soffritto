using System.Reflection;
using System.Text;
using Soffritto.Presentation;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace Soffritto.Desktop.Infrastructure;

/// <summary>
/// The installation made with the Velopack Setup.exe: first launch after setup and updates
/// from the release. A build launched from bin\ or publish\ is not installed, and here it
/// does nothing.
/// </summary>
public static class Installation
{
    private static VelopackAsset? pending;
    private static bool applyOnExit = true;

    /// <summary>
    /// Update.exe must outlive the exe that launches it: the exe's kill-on-close job
    /// (ChildProcessJob) would close it right while it waits for that exit to install.
    /// </summary>
    public static IVelopackLocator Locator { get; } = new WindowsVelopackLocator(new DetachedProcess(), null);

    /// <summary>First launch after the Setup.exe: the setup opens on the Components tab.</summary>
    public static bool IsFirstRun { get; set; }

    public static bool IsInstalled => Locator.CurrentlyInstalledVersion is not null;

    /// <summary>The release that updates come from: release.ps1 sets it when packaging.</summary>
    public static string? UpdateRepository { get; } = typeof(Installation).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "UpdateRepository")?.Value is { Length: > 0 } url ? url : null;

    /// <summary>The version downloaded and not yet installed, null if there is none.</summary>
    public static string? PendingVersion => pending?.Version.ToString();

    /// <summary>Checks for and downloads the latest release. Returns the text to show.</summary>
    public static async Task<string> CheckForUpdatesAsync()
    {
        if (!IsInstalled)
        {
            return "This build does not come from the Setup.exe: update it by rebuilding.";
        }

        if (UpdateRepository is null)
        {
            return "The package does not name a release to update from.";
        }

        var manager = new UpdateManager(new GithubSource(UpdateRepository, accessToken: null, prerelease: false), locator: Locator);
        var update = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
        if (update is null)
        {
            return pending is null
                ? $"v{Locator.CurrentlyInstalledVersion}: this is the latest version."
                : PendingText();
        }

        await manager.DownloadUpdatesAsync(update).ConfigureAwait(false);
        pending = update.TargetFullRelease;
        return PendingText();
    }

    private static string PendingText() => $"v{pending!.Version} is downloaded: it installs when the app closes.";

    /// <summary>
    /// The restart after a setup Save opens the new exe right away: installing at that moment
    /// would replace the files under the feet of the copy that just started.
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
            // The update stays downloaded: Velopack picks it up at the next start.
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

        // The CommandLineToArgvW rules: backslashes before a quote are doubled.
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

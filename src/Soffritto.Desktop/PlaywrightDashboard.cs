using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Soffritto.Desktop;

/// <summary>Startup result: the dashboard address, or the pid of one already open elsewhere, or the error.</summary>
public sealed record DashboardStart(Uri? Url, int? OtherPid, string? Error);

/// <summary>
/// The playwright-cli dashboard (<c>playwright-cli show --port=0</c>): a local server that shows
/// the pages of all the CLI's browsers live. It runs as a child of the exe, inside its job: it dies
/// with the exe even if the exe crashes.
/// </summary>
public sealed partial class PlaywrightDashboard
{
    private Process? process;

    public bool IsRunning => process is { HasExited: false };

    /// <param name="localAppData">The LOCALAPPDATA of the browser registry to show: the dashboard reads that registry and no other.</param>
    /// <param name="onExited">Called if the dashboard closes on its own, not after <see cref="Stop"/>.</param>
    public async Task<DashboardStart> StartAsync(string localAppData, Action onExited, CancellationToken cancellationToken)
    {
        var path = PlaywrightCli.CurrentPath();

        // Without this check the error comes from cmd, in the OEM code page instead of UTF-8.
        if (!PlaywrightCli.IsInstalled(path))
        {
            return new DashboardStart(null, null, "playwright-cli not found: it is not installed or not on the PATH");
        }

        // stdin stays open on purpose: the dashboard exits on its own when its stdin is closed,
        // and that is how Stop shuts it down without leaving orphans.
        var startInfo = new ProcessStartInfo("cmd.exe", "/d /c playwright-cli show --port=0")
        {
            WorkingDirectory = Path.GetTempPath(),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.Environment["LOCALAPPDATA"] = localAppData;
        startInfo.Environment["PATH"] = path;

        var announced = new TaskCompletionSource<DashboardStart>(TaskCreationOptions.RunContinuationsAsynchronously);
        var errors = new StringBuilder();
        var started = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        started.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            if (ListeningLine().Match(e.Data) is { Success: true } listening
                && Uri.TryCreate(listening.Groups[1].Value, UriKind.Absolute, out var url))
            {
                announced.TrySetResult(new DashboardStart(url, null, null));
            }
            else if (RunningLine().Match(e.Data) is { Success: true } running)
            {
                announced.TrySetResult(new DashboardStart(null, int.Parse(running.Groups[1].Value), null));
            }
        };
        started.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                lock (errors)
                {
                    errors.AppendLine(e.Data.Trim());
                }
            }
        };
        started.Exited += (_, _) =>
        {
            string detail;
            lock (errors)
            {
                detail = errors.ToString().Trim();
            }

            announced.TrySetResult(new DashboardStart(null, null,
                detail.Length > 0 ? detail : "playwright-cli show exited without opening the dashboard"));

            if (ReferenceEquals(process, started))
            {
                process = null;
                onExited();
            }
        };

        try
        {
            started.Start();
        }
        catch (Exception ex)
        {
            started.Dispose();
            return new DashboardStart(null, null, $"playwright-cli could not be started: {ex.Message}");
        }

        process = started;
        started.BeginOutputReadLine();
        started.BeginErrorReadLine();

        var winner = await Task.WhenAny(announced.Task, Task.Delay(TimeSpan.FromSeconds(30), cancellationToken)).ConfigureAwait(false);
        var outcome = winner == announced.Task
            ? announced.Task.Result
            : new DashboardStart(null, null, "playwright-cli show did not announce the dashboard within 30 seconds");

        if (outcome.Url is null)
        {
            Stop();
        }

        return outcome;
    }

    /// <summary>Closes the dashboard: blocks for up to a few seconds, so call it off the UI thread.</summary>
    public void Stop()
    {
        var running = process;
        process = null;

        if (running is null)
        {
            return;
        }

        try
        {
            running.StandardInput.Close();

            if (!running.WaitForExit(3000))
            {
                running.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Already exited between the check and the close.
        }

        running.Dispose();
    }

    [GeneratedRegex(@"Listening on (\S+)")]
    private static partial Regex ListeningLine();

    [GeneratedRegex(@"Dashboard is running pid=(\d+)")]
    private static partial Regex RunningLine();
}

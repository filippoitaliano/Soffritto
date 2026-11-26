using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using Soffritto.Desktop.Infrastructure;
using Soffritto.Desktop.ViewModels;

namespace Soffritto.Desktop;

/// <summary>
/// Keeps the frontend card previews in step with the playwright-cli browsers: starts the dashboard
/// when one appears, opens a connection per environment and shuts everything down when none is left.
/// </summary>
public sealed class PlaywrightPreviewController(IReadOnlyList<FrontendPreviewViewModel> initialPreviews)
{
    // Replaced when a Docker column comes or goes; each round takes a copy at the start.
    private volatile IReadOnlyList<FrontendPreviewViewModel> previews = initialPreviews;
    private readonly PlaywrightDashboard dashboard = new();
    private readonly SemaphoreSlim wake = new(0);
    private CancellationTokenSource? streams;

    // There is one playwright-cli dashboard per machine: if one is already running (a "show" started
    // by hand or by Claude) ours exits immediately, and we retry only once that one closes.
    private Process? otherDashboard;

    // After a failed start (CLI not installed, node error) we do not relaunch on every round.
    private DateTime retryAfter = DateTime.MinValue;

    // The dashboard connections share the same session reader, and closing one tears it down for
    // all of them: if one drops we start over with a fresh dashboard and fresh connections.
    private volatile bool restartRequested;

    // True while the cards show a reason from the controller: with zero sessions it is cleared once.
    private bool reasonShown;

    private string? dashboardLocalAppData;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // An error in one round must not kill the loop: without the loop the previews stay
                // frozen until the exe restarts. That is also why the cleanup below must not throw.
                try
                {
                    await TickAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    retryAfter = DateTime.UtcNow.AddMinutes(1);
                    StopAll();
                    ShowReason($"Error checking the playwright-cli browsers: {OneLine(ex.Message)}. Retrying at {RetryTime()}.", isProblem: true);
                }

                await wake.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            StopAll();
        }
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        var sessions = await Task.Run(PlaywrightSessions.ReadLive, cancellationToken).ConfigureAwait(false);

        if ((sessions.Count == 0 && (streams is not null || dashboard.IsRunning)) || restartRequested)
        {
            restartRequested = false;
            StopAll();
        }

        if (sessions.Count == 0)
        {
            if (reasonShown)
            {
                ShowReason(null);
            }

            return;
        }

        // The dashboard reads a single registry: with browsers in several registries (Claude desktop
        // and a regular terminal) the one with the most recently opened browser wins.
        var localAppData = sessions.MaxBy(s => s.RegisteredAt)!.LocalAppData;
        if (dashboard.IsRunning && !string.Equals(dashboardLocalAppData, localAppData, StringComparison.OrdinalIgnoreCase))
        {
            StopAll();
        }

        if (!dashboard.IsRunning && !OtherDashboardAlive() && DateTime.UtcNow >= retryAfter)
        {
            await StartAsync(localAppData, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task StartAsync(string localAppData, CancellationToken cancellationToken)
    {
        ShowReason("Starting the playwright-cli dashboard...");
        dashboardLocalAppData = localAppData;
        var start = await dashboard.StartAsync(localAppData, RequestRestart, cancellationToken).ConfigureAwait(false);

        if (start.OtherPid is { } pid)
        {
            otherDashboard = OpenOtherDashboard(pid);
            ShowReason($"The playwright-cli dashboard is already open in another process (pid {pid}), usually a \"playwright-cli show\" or a second app instance: the preview starts when it closes.", isProblem: true);
            return;
        }

        if (start.Url is not { } url)
        {
            retryAfter = DateTime.UtcNow.AddMinutes(1);
            ShowReason($"The playwright-cli dashboard did not start: {OneLine(start.Error)}. Retrying at {RetryTime()}.", isProblem: true);
            return;
        }

        if (await ResolveSocketAsync(url, cancellationToken).ConfigureAwait(false) is not { } socketUri)
        {
            retryAfter = DateTime.UtcNow.AddMinutes(1);
            dashboard.Stop();
            ShowReason($"The playwright-cli dashboard at {url} did not provide the connection address. Retrying at {RetryTime()}.", isProblem: true);
            return;
        }

        ShowReason("Connecting to the playwright-cli dashboard...");
        streams = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var current = previews;
        foreach (var preview in current.Where(p => p.Visibility == "Visible"))
        {
            _ = RunStreamAsync(preview, socketUri, localAppData, streams.Token);
        }
    }

    private async Task RunStreamAsync(FrontendPreviewViewModel preview, Uri socketUri, string localAppData, CancellationToken cancellationToken)
    {
        try
        {
            await new PlaywrightPreviewStream(preview, localAppData).RunAsync(socketUri, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Connection refused or dropped: below we decide whether to restart.
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            RequestRestart();
        }
    }

    private void RequestRestart()
    {
        restartRequested = true;
        wake.Release();
    }

    /// <summary>
    /// The previews after a column comes or goes. With connections already open we start over,
    /// because each preview gets its connection only at startup.
    /// </summary>
    public void Replace(IReadOnlyList<FrontendPreviewViewModel> list)
    {
        previews = list;
        if (streams is not null)
        {
            RequestRestart();
            return;
        }

        wake.Release();
    }

    // Also called from the loop's catch: an exception here would silently kill it.
    private void StopAll()
    {
        var running = streams;
        streams = null;

        try
        {
            running?.Cancel();
        }
        catch (AggregateException)
        {
            // A stream cancellation callback threw: the streams close anyway.
        }

        running?.Dispose();
        dashboard.Stop();

        OnPreviews(preview => preview.DetachSession());
    }

    private void ShowReason(string? reason, bool isProblem = false)
    {
        reasonShown = reason is not null;
        OnPreviews(preview => preview.ShowUnavailableReason(reason, isProblem));
    }

    private void OnPreviews(Action<FrontendPreviewViewModel> action)
    {
        try
        {
            var current = previews;
            UiThread.Run(() =>
            {
                foreach (var preview in current)
                {
                    action(preview);
                }
            });
        }
        catch (Exception)
        {
            // The cards miss this round's update: the loop must stay alive.
        }
    }

    private string RetryTime() => retryAfter.ToLocalTime().ToString("HH:mm:ss");

    private static string OneLine(string? text)
    {
        var line = string.Join(' ', (text ?? "").Split((char[])['\r', '\n', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries)).TrimEnd('.');
        return line.Length switch
        {
            0 => "unknown reason",
            > 200 => line[..200] + "...",
            _ => line,
        };
    }

    // The dashboard root redirects to /index.html?ws=<guid>, and the websocket lives at /<guid>.
    private static async Task<Uri?> ResolveSocketAsync(Uri dashboardUrl, CancellationToken cancellationToken)
    {
        try
        {
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };
            using var response = await http.GetAsync(dashboardUrl, cancellationToken).ConfigureAwait(false);
            var location = response.Headers.Location?.OriginalString ?? "";
            var guid = location.Split("ws=", 2) is [_, var value] ? value.Split('&')[0] : null;

            return string.IsNullOrEmpty(guid)
                ? null
                : new UriBuilder(dashboardUrl) { Scheme = "ws", Path = "/" + guid, Query = "" }.Uri;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient timeout: a failed start, not the exe shutting down.
            return null;
        }
    }

    // A pid alone is not enough: once the dashboard closes, Windows reassigns it and another
    // process would keep it "alive" forever. While someone holds the handle open the pid is not
    // reused, so the handle is opened right away and kept until the process exits.
    private static Process? OpenOtherDashboard(int pid)
    {
        Process? other = null;
        try
        {
            other = Process.GetProcessById(pid);
            _ = other.SafeHandle;
            return other;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            // Already exited, or access denied: the start is retried next round.
            other?.Dispose();
            return null;
        }
    }

    private bool OtherDashboardAlive()
    {
        if (otherDashboard is not { } other)
        {
            return false;
        }

        try
        {
            if (!other.HasExited)
            {
                return true;
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }

        other.Dispose();
        otherDashboard = null;
        return false;
    }
}

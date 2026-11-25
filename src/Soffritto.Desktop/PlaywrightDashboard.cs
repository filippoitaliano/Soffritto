using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Soffritto.Desktop;

/// <summary>Esito dell'avvio: l'indirizzo della dashboard, oppure il pid di quella gia' aperta altrove, oppure l'errore.</summary>
public sealed record DashboardStart(Uri? Url, int? OtherPid, string? Error);

/// <summary>
/// La dashboard di playwright-cli (<c>playwright-cli show --port=0</c>): un server locale che
/// mostra in diretta le pagine di tutti i browser della CLI. Gira come figlio dell'exe, dentro il
/// suo job: muore con l'exe anche se l'exe muore male.
/// </summary>
public sealed partial class PlaywrightDashboard
{
    private Process? process;

    public bool IsRunning => process is { HasExited: false };

    /// <param name="localAppData">La LOCALAPPDATA del registro dei browser da mostrare: la dashboard legge quel registro e nessun altro.</param>
    /// <param name="onExited">Chiamato se la dashboard si chiude da sola, non dopo <see cref="Stop"/>.</param>
    public async Task<DashboardStart> StartAsync(string localAppData, Action onExited, CancellationToken cancellationToken)
    {
        var path = PlaywrightCli.CurrentPath();

        // Senza questo controllo l'errore arriva da cmd, nella codepage OEM e non in UTF-8.
        if (!PlaywrightCli.IsInstalled(path))
        {
            return new DashboardStart(null, null, "playwright-cli non trovato, non e' installato o non e' nel PATH");
        }

        // Lo stdin resta aperto apposta: la dashboard si chiude da sola quando le si chiude lo
        // stdin, ed e' cosi' che Stop la ferma senza lasciare orfani.
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
                detail.Length > 0 ? detail : "playwright-cli show si e' chiuso senza aprire la dashboard"));

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
            return new DashboardStart(null, null, $"playwright-cli non avviabile: {ex.Message}");
        }

        process = started;
        started.BeginOutputReadLine();
        started.BeginErrorReadLine();

        var winner = await Task.WhenAny(announced.Task, Task.Delay(TimeSpan.FromSeconds(30), cancellationToken)).ConfigureAwait(false);
        var outcome = winner == announced.Task
            ? announced.Task.Result
            : new DashboardStart(null, null, "playwright-cli show non ha annunciato la dashboard entro 30 secondi");

        if (outcome.Url is null)
        {
            Stop();
        }

        return outcome;
    }

    /// <summary>Chiude la dashboard: bloccante fino a qualche secondo, da chiamare fuori dal thread della UI.</summary>
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
            // Gia' uscita tra il controllo e la chiusura.
        }

        running.Dispose();
    }

    [GeneratedRegex(@"Listening on (\S+)")]
    private static partial Regex ListeningLine();

    [GeneratedRegex(@"Dashboard is running pid=(\d+)")]
    private static partial Regex RunningLine();
}

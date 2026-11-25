using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using Soffritto.Desktop.Infrastructure;
using Soffritto.Desktop.ViewModels;

namespace Soffritto.Desktop;

/// <summary>
/// Tiene le anteprime delle card del frontend allineate ai browser di playwright-cli: accende la
/// dashboard quando ne compare uno, apre una connessione per ogni ambiente e spegne tutto quando
/// non ne resta nessuno.
/// </summary>
public sealed class PlaywrightPreviewController(IReadOnlyList<FrontendPreviewViewModel> initialPreviews)
{
    // Sostituita quando una colonna Docker entra o esce; ogni giro ne prende una copia all'inizio.
    private volatile IReadOnlyList<FrontendPreviewViewModel> previews = initialPreviews;
    private readonly PlaywrightDashboard dashboard = new();
    private readonly SemaphoreSlim wake = new(0);
    private CancellationTokenSource? streams;

    // La dashboard di playwright-cli e' una per macchina: se ne gira gia' una (un "show" lanciato
    // a mano o da Claude) la nostra esce subito, e si riprova solo quando quella si chiude.
    private Process? otherDashboard;

    // Dopo un avvio fallito (CLI non installata, errore di node) non si rilancia a ogni giro.
    private DateTime retryAfter = DateTime.MinValue;

    // Le connessioni della dashboard condividono lo stesso lettore delle sessioni e chiuderne una
    // lo smonta per tutte: se una cade si riparte da capo con dashboard e connessioni nuove.
    private volatile bool restartRequested;

    // Vero finche' le card mostrano un motivo del controller: con zero sessioni va tolto una volta.
    private bool reasonShown;

    private string? dashboardLocalAppData;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // Un errore in un giro non deve spegnere il ciclo: senza ciclo le anteprime restano
                // ferme finche' non si riavvia l'exe. Per questo anche la pulizia qui sotto non
                // puo' lanciare.
                try
                {
                    await TickAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    retryAfter = DateTime.UtcNow.AddMinutes(1);
                    StopAll();
                    ShowReason($"Errore nel controllo dei browser di playwright-cli: {OneLine(ex.Message)}. Nuovo tentativo alle {RetryTime()}.", isProblem: true);
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

        // La dashboard legge un registro solo: con browser in piu' registri (Claude desktop e un
        // terminale normale) vince quello del browser aperto per ultimo.
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
        ShowReason("Avvio della dashboard di playwright-cli...");
        dashboardLocalAppData = localAppData;
        var start = await dashboard.StartAsync(localAppData, RequestRestart, cancellationToken).ConfigureAwait(false);

        if (start.OtherPid is { } pid)
        {
            otherDashboard = OpenOtherDashboard(pid);
            ShowReason($"La dashboard di playwright-cli e' gia' aperta da un altro processo (pid {pid}), di solito un «playwright-cli show» o un secondo exe: l'anteprima parte quando si chiude.", isProblem: true);
            return;
        }

        if (start.Url is not { } url)
        {
            retryAfter = DateTime.UtcNow.AddMinutes(1);
            ShowReason($"La dashboard di playwright-cli non si e' avviata: {OneLine(start.Error)}. Nuovo tentativo alle {RetryTime()}.", isProblem: true);
            return;
        }

        if (await ResolveSocketAsync(url, cancellationToken).ConfigureAwait(false) is not { } socketUri)
        {
            retryAfter = DateTime.UtcNow.AddMinutes(1);
            dashboard.Stop();
            ShowReason($"La dashboard di playwright-cli su {url} non ha dato l'indirizzo del collegamento. Nuovo tentativo alle {RetryTime()}.", isProblem: true);
            return;
        }

        ShowReason("Collegamento alla dashboard di playwright-cli...");
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
            // Connessione rifiutata o caduta: sotto si decide se ripartire.
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
    /// Le anteprime dopo l'arrivo o l'uscita di una colonna. Con le connessioni gia' aperte si
    /// riparte da capo, perche' ogni anteprima ha la sua connessione aperta solo all'avvio.
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

    // Chiamata anche dal catch del ciclo: un'eccezione qui lo ucciderebbe in silenzio.
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
            // Una callback di cancellazione degli stream ha lanciato: gli stream si chiudono comunque.
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
            // Le card non si aggiornano a questo giro: il ciclo deve restare vivo.
        }
    }

    private string RetryTime() => retryAfter.ToLocalTime().ToString("HH:mm:ss");

    private static string OneLine(string? text)
    {
        var line = string.Join(' ', (text ?? "").Split((char[])['\r', '\n', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries)).TrimEnd('.');
        return line.Length switch
        {
            0 => "motivo sconosciuto",
            > 200 => line[..200] + "...",
            _ => line,
        };
    }

    // La radice della dashboard rimanda a /index.html?ws=<guid>, e il websocket vive su /<guid>.
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
            // Timeout di HttpClient: e' un avvio fallito, non la chiusura dell'exe.
            return null;
        }
    }

    // Un pid da solo non basta: chiusa la dashboard, Windows lo riassegna e un altro processo
    // la terrebbe "viva" per sempre. Finche' qualcuno tiene aperto l'handle il pid non si riusa,
    // quindi l'handle va aperto subito e tenuto fino alla chiusura.
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
            // Gia' uscita, o accesso negato: si ritenta l'avvio al giro dopo.
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

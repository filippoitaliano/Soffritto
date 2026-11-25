using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Soffritto.Desktop.Infrastructure;
using Soffritto.Presentation;

namespace Soffritto.Desktop.ViewModels;

/// <summary>Riceve l'input della vista dal vivo e lo inoltra al browser di playwright.</summary>
public interface IPreviewInput
{
    void Send(string method, object parameters);
}

/// <summary>
/// La sezione di anteprima nella card del frontend di un ambiente: mostra dal vivo il browser di
/// playwright-cli aperto sulla porta del dev server.
/// </summary>
public sealed class FrontendPreviewViewModel : ObservableObject
{
    private ImageSource? frame;
    private string pageTitle = string.Empty;
    private string pageUrl = string.Empty;
    private bool hasSession;
    private bool autoExpanded;
    private string? unavailableReason;
    private bool reasonIsProblem;
    private DispatcherTimer? collapseTimer;

    public FrontendPreviewViewModel(string environmentTitle, RepoLocation frontend)
    {
        EnvironmentTitle = environmentTitle;
        Port = FrontendDevServer.ReadPort(frontend);
        FrontendUrl = FrontendDevServer.UrlOf(Port);
        Visibility = frontend.Exists() ? "Visible" : "Collapsed";
    }

    public string EnvironmentTitle { get; }

    public int Port { get; private set; }

    public Uri FrontendUrl { get; private set; }

    public string Visibility { get; private set; }

    /// <summary>
    /// Rilegge porta e presenza del clone: in un ambiente Docker creato a caldo la colonna nasce
    /// prima che il worktree e il suo file di env esistano.
    /// </summary>
    public void Reload(RepoLocation frontend)
    {
        var port = FrontendDevServer.ReadPort(frontend);
        var visibility = frontend.Exists() ? "Visible" : "Collapsed";
        if (visibility != Visibility)
        {
            Visibility = visibility;
            OnPropertyChanged(nameof(Visibility));
        }

        if (port == Port)
        {
            return;
        }

        Port = port;
        FrontendUrl = FrontendDevServer.UrlOf(port);
        OnPropertyChanged(nameof(Port));
        OnPropertyChanged(nameof(FrontendUrl));
        OnPropertyChanged(nameof(ExternalUrl));
        OnPropertyChanged(nameof(ClaudePlaceholder));
        OnPropertyChanged(nameof(Header));
        OnPropertyChanged(nameof(HeaderToolTip));
    }

    public Expander Expander { get; } = new(isExpanded: false);

    public ICommand? OpenExternalCommand { get; set; }

    /// <summary>Da aprire nel browser esterno: la pagina del browser di Claude, o il dev server se non c'e'.</summary>
    public Uri ExternalUrl => hasSession && Uri.TryCreate(pageUrl, UriKind.Absolute, out var page) ? page : FrontendUrl;

    public string ClaudeViewVisibility => hasSession ? "Visible" : "Collapsed";

    public string ClaudePlaceholderVisibility => hasSession ? "Collapsed" : "Visible";

    public string ClaudePlaceholder => unavailableReason ?? $"Nessun browser di playwright-cli aperto su localhost:{Port}";

    /// <summary>Verde quando un browser di playwright e' sul frontend di questo ambiente, arancio quando l'anteprima e' bloccata.</summary>
    public string SessionColor => hasSession ? StatusBadge.OkColor : reasonIsProblem ? StatusBadge.WarningColor : StatusBadge.OffColor;

    public string Header => hasSession && pageTitle.Length > 0
        ? pageTitle
        : $"localhost:{Port}";

    // A card chiusa il tooltip e' l'unico posto in cui il motivo si legge.
    public string HeaderToolTip => hasSession
        ? pageUrl
        : unavailableReason is { } reason ? $"{FrontendUrl}{Environment.NewLine}{reason}" : FrontendUrl.ToString();

    public ImageSource? Frame { get => frame; private set => SetProperty(ref frame, value); }

    /// <summary>Dimensioni della pagina in CSS px: servono a riportare i click sulle coordinate del browser.</summary>
    public double ViewportWidth { get; private set; }

    public double ViewportHeight { get; private set; }

    /// <summary>
    /// Proporzione del riquadro dell'anteprima: quella del fotogramma quando e' piu' largo del 16:9,
    /// altrimenti 16:9, che fa da altezza massima e lascia il bordo stringersi in larghezza.
    /// </summary>
    public double SizerWidth => FrameIsWide ? ViewportWidth : 16;

    public double SizerHeight => FrameIsWide ? ViewportHeight : 9;

    private bool FrameIsWide => frame is not null && ViewportHeight > 0 && ViewportWidth / ViewportHeight > 16.0 / 9;

    public IPreviewInput? Input { get; private set; }

    public void AttachSession(IPreviewInput input, string title, string url)
    {
        Input = input;
        pageTitle = title.Length > 0 ? title : url;
        pageUrl = url;

        if (!hasSession && !autoExpanded)
        {
            // Alla prima sessione la card si apre da sola; dopo decide chi la usa.
            autoExpanded = true;
            Expander.IsExpanded = true;
        }

        SetSession(true);
        OnPropertyChanged(nameof(Header));
        OnPropertyChanged(nameof(HeaderToolTip));
    }

    public void SetFrame(ImageSource image, double viewportWidth, double viewportHeight)
    {
        var sizeChanged = viewportWidth != ViewportWidth || viewportHeight != ViewportHeight || frame is null;
        ViewportWidth = viewportWidth;
        ViewportHeight = viewportHeight;
        Frame = image;
        if (sizeChanged)
        {
            OnPropertyChanged(nameof(SizerWidth));
            OnPropertyChanged(nameof(SizerHeight));
        }
    }

    /// <summary>
    /// Perche' l'anteprima «Claude» non c'e'; null torna al testo di quando non c'e' nessun browser.
    /// Un problema (dashboard bloccata, avvio fallito) colora il pallino; un'informazione no.
    /// </summary>
    public void ShowUnavailableReason(string? reason, bool isProblem = false)
    {
        isProblem &= reason is not null;
        if (unavailableReason == reason && reasonIsProblem == isProblem)
        {
            return;
        }

        unavailableReason = reason;
        reasonIsProblem = isProblem;
        OnPropertyChanged(nameof(ClaudePlaceholder));
        OnPropertyChanged(nameof(SessionColor));
        OnPropertyChanged(nameof(HeaderToolTip));
    }

    public void DetachSession()
    {
        Input = null;
        Frame = null;
        OnPropertyChanged(nameof(SizerWidth));
        OnPropertyChanged(nameof(SizerHeight));
        pageTitle = string.Empty;
        pageUrl = string.Empty;
        SetSession(false);
        OnPropertyChanged(nameof(Header));
        OnPropertyChanged(nameof(HeaderToolTip));
    }

    private void SetSession(bool value)
    {
        if (hasSession == value)
        {
            return;
        }

        hasSession = value;
        if (value)
        {
            collapseTimer?.Stop();
        }
        else
        {
            // Creato qui e non nel costruttore: SetSession gira sempre sul thread della UI, il
            // costruttore non e' detto.
            collapseTimer ??= CreateCollapseTimer();
            collapseTimer.Start();
        }

        OnPropertyChanged(nameof(ClaudeViewVisibility));
        OnPropertyChanged(nameof(ClaudePlaceholderVisibility));
        OnPropertyChanged(nameof(SessionColor));
    }

    // Un minuto senza sessione chiude la sezione e la prossima sessione la riapre da sola.
    private DispatcherTimer CreateCollapseTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!hasSession)
            {
                Expander.IsExpanded = false;
                autoExpanded = false;
            }
        };
        return timer;
    }
}

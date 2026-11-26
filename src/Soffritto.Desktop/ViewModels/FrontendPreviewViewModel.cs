using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Soffritto.Desktop.Infrastructure;
using Soffritto.Presentation;

namespace Soffritto.Desktop.ViewModels;

/// <summary>Receives input from the live view and forwards it to the playwright browser.</summary>
public interface IPreviewInput
{
    void Send(string method, object parameters);
}

/// <summary>
/// The preview section in an environment's frontend card: a live view of the playwright-cli
/// browser open on the dev server port.
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
    /// Re-reads the port and whether the clone exists: in a Docker environment created on the fly
    /// the column appears before the worktree and its env file exist.
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

    /// <summary>What to open in the external browser: the page in Claude's browser, or the dev server if there is none.</summary>
    public Uri ExternalUrl => hasSession && Uri.TryCreate(pageUrl, UriKind.Absolute, out var page) ? page : FrontendUrl;

    public string ClaudeViewVisibility => hasSession ? "Visible" : "Collapsed";

    public string ClaudePlaceholderVisibility => hasSession ? "Collapsed" : "Visible";

    public string ClaudePlaceholder => unavailableReason ?? $"No playwright-cli browser open on localhost:{Port}";

    /// <summary>Green when a playwright browser is on this environment's frontend, orange when the preview is blocked.</summary>
    public string SessionColor => hasSession ? StatusBadge.OkColor : reasonIsProblem ? StatusBadge.WarningColor : StatusBadge.OffColor;

    public string Header => hasSession && pageTitle.Length > 0
        ? pageTitle
        : $"localhost:{Port}";

    // With the card collapsed, the tooltip is the only place where the reason can be read.
    public string HeaderToolTip => hasSession
        ? pageUrl
        : unavailableReason is { } reason ? $"{FrontendUrl}{Environment.NewLine}{reason}" : FrontendUrl.ToString();

    public ImageSource? Frame { get => frame; private set => SetProperty(ref frame, value); }

    /// <summary>Page size in CSS px: used to map clicks onto the browser's coordinates.</summary>
    public double ViewportWidth { get; private set; }

    public double ViewportHeight { get; private set; }

    /// <summary>
    /// Aspect ratio of the preview box: the frame's when it is wider than 16:9, otherwise 16:9,
    /// which acts as a maximum height and lets the border shrink in width.
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
            // On the first session the card opens by itself; after that the user decides.
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
    /// Why the “Claude” preview is missing; null goes back to the text shown when there is no browser.
    /// A problem (dashboard blocked, launch failed) colours the dot; a plain notice does not.
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
            // Created here rather than in the constructor: SetSession always runs on the UI thread,
            // the constructor might not.
            collapseTimer ??= CreateCollapseTimer();
            collapseTimer.Start();
        }

        OnPropertyChanged(nameof(ClaudeViewVisibility));
        OnPropertyChanged(nameof(ClaudePlaceholderVisibility));
        OnPropertyChanged(nameof(SessionColor));
    }

    // One minute without a session collapses the section, and the next session reopens it by itself.
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

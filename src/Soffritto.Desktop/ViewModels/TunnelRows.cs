using System.Windows.Input;
using Soffritto.Desktop.Infrastructure;
using Soffritto.Presentation;

namespace Soffritto.Desktop.ViewModels;

/// <summary>
/// A tunnel port: number, public host and URL copy. Whether the port responds is shown by the
/// host's dot; the row speaks up only when the probe got no response.
/// </summary>
public sealed class TunnelPortRowViewModel : ObservableObject
{
    private string kind;

    public TunnelPortRowViewModel(int portNumber, string url, bool? reachable)
    {
        PortNumber = portNumber.ToString();
        Url = url;
        Host = url.Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase);
        State = reachable == false ? "no response" : string.Empty;
        kind = State;
    }

    public string PortNumber { get; }

    /// <summary>Full URL that the icon copies to the clipboard, without a trailing slash.</summary>
    public string Url { get; }

    /// <summary>The URL without the scheme: tunnel ports are always https.</summary>
    public string Host { get; }

    /// <summary>"no response" or empty: the label to restore when the copy feedback expires.</summary>
    public string State { get; }

    /// <summary>The probe warning or the transient copy feedback.</summary>
    public string Kind
    {
        get => kind;
        set
        {
            if (SetProperty(ref kind, value))
            {
                OnPropertyChanged(nameof(Note));
                OnPropertyChanged(nameof(NoteColor));
            }
        }
    }

    /// <summary>The lane tag: only what is wrong (no response, copy failed), not a successful copy.</summary>
    public string Note => kind == StatusText.Copied ? string.Empty : LaneTags.Capitalize(kind);

    /// <summary>Grey for “no response” (the service behind the port may simply be off), red for a failed copy.</summary>
    public string NoteColor => kind == State ? LaneTags.Neutral : LaneTags.Error;

    public string CopyToolTip { get; init; } = string.Empty;

    public ICommand? CopyCommand { get; set; }

    public string OpenToolTip { get; init; } = string.Empty;

    public ICommand? OpenCommand { get; set; }
}

public sealed class TunnelSummaryRowViewModel(string title, string kind, string statusColor)
{
    public string Title { get; } = title;

    public string Kind { get; } = kind;

    public string StatusColor { get; } = statusColor;
}

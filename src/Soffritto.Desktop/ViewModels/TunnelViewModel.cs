using System.Collections.ObjectModel;
using Soffritto.Desktop.Infrastructure;
using Soffritto.Presentation;

namespace Soffritto.Desktop.ViewModels;

/// <summary>The section of a dev tunnel inside a card: the backend's or the frontend's.</summary>
public sealed class TunnelViewModel : ObservableObject
{
    private string line = string.Empty;
    private string detail = string.Empty;
    private string detailVisibility = "Visible";
    private string portsVisibility = "Collapsed";
    private string listVisibility = "Collapsed";
    private string stamp = StatusText.NotRead;

    /// <summary>Tunnel id according to the last successful read; null if not read or not found.</summary>
    public string? Id { get; set; }

    /// <summary>
    /// Host connected according to the last tunnel read, whoever hosts it (the CLI or Visual
    /// Studio, which hosts it in its own process). Valid until the next refresh.
    /// </summary>
    public bool HostConnected { get; set; }

    /// <summary>Time of the last tunnel read, to tell since when <see cref="HostConnected"/> holds.</summary>
    public string ReadStamp { get; set; } = string.Empty;

    public Expander Expander { get; } = new(isExpanded: true);

    public string Line { get => line; set => SetProperty(ref line, value); }

    public string Detail { get => detail; set => SetProperty(ref detail, value); }

    public string DetailVisibility { get => detailVisibility; set => SetProperty(ref detailVisibility, value); }

    public ObservableCollection<TunnelPortRowViewModel> Ports { get; } = [];

    public string PortsVisibility { get => portsVisibility; set => SetProperty(ref portsVisibility, value); }

    /// <summary>The account's tunnels, shown only when none matches the clone.</summary>
    public ObservableCollection<TunnelSummaryRowViewModel> List { get; } = [];

    public string ListVisibility { get => listVisibility; set => SetProperty(ref listVisibility, value); }

    /// <summary>"updated HH:mm:ss", "queued…", "reading…" or "not read".</summary>
    public string Stamp
    {
        get => stamp;
        set
        {
            if (SetProperty(ref stamp, value))
            {
                OnPropertyChanged(nameof(IsLoading));
            }
        }
    }

    public bool IsLoading => stamp.EndsWith('…');
}

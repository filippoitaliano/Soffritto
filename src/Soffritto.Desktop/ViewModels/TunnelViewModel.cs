using System.Collections.ObjectModel;
using Soffritto.Desktop.Infrastructure;
using Soffritto.Presentation;

namespace Soffritto.Desktop.ViewModels;

/// <summary>La sezione di un dev tunnel dentro una card: quello del backend o quello del frontend.</summary>
public sealed class TunnelViewModel : ObservableObject
{
    private string line = string.Empty;
    private string detail = string.Empty;
    private string detailVisibility = "Visible";
    private string portsVisibility = "Collapsed";
    private string listVisibility = "Collapsed";
    private string stamp = StatusText.NotRead;

    /// <summary>Id del tunnel secondo l'ultima lettura riuscita; null se non letto o non trovato.</summary>
    public string? Id { get; set; }

    /// <summary>
    /// Host connesso secondo l'ultima lettura del tunnel, chiunque lo ospiti (la CLI o Visual
    /// Studio, che lo ospita nel proprio processo). Vale fino al refresh successivo.
    /// </summary>
    public bool HostConnected { get; set; }

    /// <summary>Ora dell'ultima lettura del tunnel, per dire da quando vale <see cref="HostConnected"/>.</summary>
    public string ReadStamp { get; set; } = string.Empty;

    public Expander Expander { get; } = new(isExpanded: true);

    public string Line { get => line; set => SetProperty(ref line, value); }

    public string Detail { get => detail; set => SetProperty(ref detail, value); }

    public string DetailVisibility { get => detailVisibility; set => SetProperty(ref detailVisibility, value); }

    public ObservableCollection<TunnelPortRowViewModel> Ports { get; } = [];

    public string PortsVisibility { get => portsVisibility; set => SetProperty(ref portsVisibility, value); }

    /// <summary>I tunnel dell'account, mostrati solo quando nessuno corrisponde al clone.</summary>
    public ObservableCollection<TunnelSummaryRowViewModel> List { get; } = [];

    public string ListVisibility { get => listVisibility; set => SetProperty(ref listVisibility, value); }

    /// <summary>"aggiornato HH:mm:ss", "in coda…", "lettura…" o "non letto".</summary>
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

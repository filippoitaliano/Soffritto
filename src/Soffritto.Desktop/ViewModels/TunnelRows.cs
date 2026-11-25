using System.Windows.Input;
using Soffritto.Desktop.Infrastructure;
using Soffritto.Presentation;

namespace Soffritto.Desktop.ViewModels;

/// <summary>
/// Una porta del tunnel: numero, host pubblico e copia dell'URL. Che la porta risponda lo dice il
/// pallino dell'host; la riga parla solo quando il probe non ha avuto risposta.
/// </summary>
public sealed class TunnelPortRowViewModel : ObservableObject
{
    private string kind;

    public TunnelPortRowViewModel(int portNumber, string url, bool? reachable)
    {
        PortNumber = portNumber.ToString();
        Url = url;
        Host = url.Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase);
        State = reachable == false ? "nessuna risposta" : string.Empty;
        kind = State;
    }

    public string PortNumber { get; }

    /// <summary>URL completo che l'icona mette negli appunti, senza slash finale.</summary>
    public string Url { get; }

    /// <summary>L'URL senza schema: le porte del tunnel sono sempre https.</summary>
    public string Host { get; }

    /// <summary>"nessuna risposta" o vuoto: l'etichetta da ripristinare quando il feedback della copia scade.</summary>
    public string State { get; }

    /// <summary>L'avviso del probe o il feedback transitorio della copia.</summary>
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

    /// <summary>Il tag della lane: solo cio' che non va (nessuna risposta, copia fallita), la copia riuscita no.</summary>
    public string Note => kind == StatusText.Copied ? string.Empty : LaneTags.Capitalize(kind);

    /// <summary>Grigio per «nessuna risposta» (il servizio dietro la porta puo' essere solo spento), rosso per la copia fallita.</summary>
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

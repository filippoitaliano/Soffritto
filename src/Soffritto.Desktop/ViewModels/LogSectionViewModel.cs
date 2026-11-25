using Soffritto.Desktop.Infrastructure;

namespace Soffritto.Desktop.ViewModels;

/// <summary>
/// Una sezione «Log» chiusa di default dentro una card: le operazioni di un ambiente Docker
/// (<see cref="Append"/>) o le ultime righe del container di una card (<see cref="Replace"/>).
/// </summary>
public sealed class LogSectionViewModel : ObservableObject
{
    private const int MaxLines = 300;

    private readonly List<string> lines = [];
    private string text = string.Empty;
    private string state = string.Empty;

    public LogSectionViewModel(string header)
    {
        Header = header;
    }

    public string Header { get; }

    public Expander Expander { get; } = new(isExpanded: false);

    public string Text { get => text; private set => SetProperty(ref text, value); }

    /// <summary>Nota accanto al titolo: quante righe, o perche' il log non si legge.</summary>
    public string State { get => state; set => SetProperty(ref state, value); }

    /// <summary>Lo stesso log seguito in una console: il bottone dell'intestazione c'e' solo se e' impostato.</summary>
    public System.Windows.Input.ICommand? ConsoleCommand { get; init; }

    public string ConsoleToolTip { get; init; } = string.Empty;

    public string ConsoleVisibility => ConsoleCommand is null ? "Collapsed" : "Visible";

    /// <summary>Una riga con l'ora in fondo al log. Da qualunque thread.</summary>
    public void Append(string line) => UiThread.Run(() =>
    {
        lines.Add($"{DateTime.Now:HH:mm:ss} {line}");
        if (lines.Count > MaxLines)
        {
            lines.RemoveRange(0, lines.Count - MaxLines);
        }

        Text = string.Join(Environment.NewLine, lines);
        State = $"{lines.Count} righe";
    });

    /// <summary>Tutto il log in una volta, com'e' letto dalla sorgente. Da qualunque thread.</summary>
    public void Replace(string content, string note) => UiThread.Run(() =>
    {
        lines.Clear();
        Text = content;
        State = note;
    });
}

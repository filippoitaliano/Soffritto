using Soffritto.Desktop.Infrastructure;

namespace Soffritto.Desktop.ViewModels;

/// <summary>
/// A “Log” section inside a card, closed by default: the operations of a Docker environment
/// (<see cref="Append"/>) or the last lines of a card's container (<see cref="Replace"/>).
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

    /// <summary>Note next to the title: how many lines, or why the log cannot be read.</summary>
    public string State { get => state; set => SetProperty(ref state, value); }

    /// <summary>The same log followed in a console: the header button is there only when this is set.</summary>
    public System.Windows.Input.ICommand? ConsoleCommand { get; init; }

    public string ConsoleToolTip { get; init; } = string.Empty;

    public string ConsoleVisibility => ConsoleCommand is null ? "Collapsed" : "Visible";

    /// <summary>A timestamped line at the end of the log. From any thread.</summary>
    public void Append(string line) => UiThread.Run(() =>
    {
        lines.Add($"{DateTime.Now:HH:mm:ss} {line}");
        if (lines.Count > MaxLines)
        {
            lines.RemoveRange(0, lines.Count - MaxLines);
        }

        Text = string.Join(Environment.NewLine, lines);
        State = $"{lines.Count} lines";
    });

    /// <summary>The whole log at once, as read from the source. From any thread.</summary>
    public void Replace(string content, string note) => UiThread.Run(() =>
    {
        lines.Clear();
        Text = content;
        State = note;
    });
}

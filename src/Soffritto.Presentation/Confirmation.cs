namespace Soffritto.Presentation;

/// <summary>
/// A confirmation request: title, a short paragraph saying what happens, the lists with the
/// details (what gets touched, and where) and the text of the confirm button.
/// </summary>
public sealed record Confirmation(
    string Title,
    string Description,
    IReadOnlyList<ConfirmationList> Lists,
    string ConfirmText = "Confirm")
{
    /// <summary>The same confirmation as plain text, for prompts that cannot draw lists.</summary>
    public string ToPlainText()
    {
        var lines = new List<string> { Title, string.Empty, Description };
        foreach (var list in Lists.Where(l => l.Rows.Count > 0))
        {
            lines.Add(string.Empty);
            lines.Add($"{list.Heading}:");
            lines.AddRange(list.Rows.Select(r => "  - " + string.Join(" ", new[] { r.Badge, r.Text, r.Note.Length > 0 ? $"({r.Note})" : string.Empty }.Where(s => s.Length > 0))));
        }

        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>A list in the confirmation, with its heading; an empty list is not shown.</summary>
public sealed record ConfirmationList(string Heading, IReadOnlyList<ConfirmationRow> Rows);

/// <summary>A row: grey badge on the left (short label), text, note on the right; badge and note are optional.</summary>
public sealed record ConfirmationRow(string Text, string Badge = "", string Note = "");

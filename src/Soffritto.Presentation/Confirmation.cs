namespace Soffritto.Presentation;

/// <summary>
/// Una richiesta di conferma: titolo, un paragrafo breve che dice cosa succede, gli elenchi con i
/// dettagli (cosa viene toccato, dove) e il testo del bottone di conferma.
/// </summary>
public sealed record Confirmation(
    string Title,
    string Description,
    IReadOnlyList<ConfirmationList> Lists,
    string ConfirmText = "Conferma")
{
    /// <summary>La stessa conferma in testo semplice, per i prompt che non disegnano elenchi.</summary>
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

/// <summary>Un elenco della conferma, con la sua intestazione; vuoto non si mostra.</summary>
public sealed record ConfirmationList(string Heading, IReadOnlyList<ConfirmationRow> Rows);

/// <summary>Una riga: badge grigio a sinistra (etichetta breve), testo, nota a destra; badge e nota facoltativi.</summary>
public sealed record ConfirmationRow(string Text, string Badge = "", string Note = "");

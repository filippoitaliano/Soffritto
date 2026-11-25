namespace Soffritto.Presentation;

/// <summary>Algoritmo delle voci della tendina dei branch, indipendente dal tipo di lista dell'host.</summary>
public static class BranchList
{
    /// <summary>
    /// Il corrente sta sempre in testa, anche se non passa il filtro; poi i nomi che contengono
    /// il filtro senza distinguere le maiuscole. Corrente null (git non disponibile): nessuna voce.
    /// </summary>
    public static IReadOnlyList<string> Desired(string? current, IReadOnlyList<string> all, string filter)
    {
        var desired = new List<string>();

        if (current is not null)
        {
            desired.Add(current);
            desired.AddRange(all.Where(name =>
                name != current && name.Contains(filter, StringComparison.OrdinalIgnoreCase)));
        }

        return desired;
    }

    // La lista viene riallineata voce per voce invece di svuotata e riempita: con la ComboBox
    // editabile, togliere la voce selezionata azzera anche il testo che l'utente sta scrivendo.
    public static void Sync(IList<string> target, IReadOnlyList<string> desired)
    {
        for (var i = 0; i < desired.Count; i++)
        {
            while (i < target.Count && target[i] != desired[i])
            {
                if (desired.Contains(target[i]))
                {
                    target.Insert(i, desired[i]);
                }
                else
                {
                    target.RemoveAt(i);
                }
            }

            if (i >= target.Count)
            {
                target.Add(desired[i]);
            }
        }

        while (target.Count > desired.Count)
        {
            target.RemoveAt(target.Count - 1);
        }
    }
}

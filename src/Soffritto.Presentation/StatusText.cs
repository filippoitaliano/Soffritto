using Soffritto.Core;

namespace Soffritto.Presentation;

/// <summary>Testi del pannello condivisi tra VSIX ed exe: una sola fonte per etichette, tooltip e conferme.</summary>
public static class StatusText
{
    /// <summary>Microtesto mostrato quando nessuna azione e' in corso: la riga resta, il layout non si muove.</summary>
    public const string IdleActivity = "pronto";

    public const string Reading = "lettura…";
    public const string Fetching = "git fetch…";
    public const string Queued = "in coda…";
    public const string NotRead = "non letto";
    public const string NotYetRead = "non ancora letto";
    public const string WaitingFirstRead = "in attesa della prima lettura";
    public const string ReadFailed = "lettura fallita";
    public const string Copied = "copiato";
    public const string CopyFailed = "copia non riuscita";
    public const string Unresolved = "non risolto";

    public static string Stamp(DateTimeOffset now) => $"aggiornato {now:HH:mm:ss}";

    public static string WorkingFolder(string root)
        => Path.GetFileName(Path.TrimEndingDirectorySeparator(root));

    // Il branch in intestazione porta con se' il conteggio da pullare: e' il dato che decide se
    // vale la pena fare qualcosa prima di lavorare.
    public static string BuildBranch(GitStatus git)
    {
        if (git.Error is not null)
        {
            return "git non disponibile";
        }

        var branch = git.Branch ?? "?";
        return git.Behind > 0 ? $"{branch} · {git.Behind} da pullare" : branch;
    }

    // La riga si apre col nome della cartella di lavoro (vedi WorkingFolder): con piu' cloni
    // attivi sulla stessa macchina e' l'unico dato che dice A QUALE clone si riferisce il pannello.
    public static string BuildRepoDetail(GitStatus git)
    {
        var parts = new List<string>();

        if (git.Behind > 0)
        {
            parts.Add($"{git.Behind} da pullare");
        }

        parts.Add(git.IsClean ? "working tree pulito" : $"{git.DirtyFiles} file modificati");

        if (!MissingUpstream(git) && git.Ahead > 0)
        {
            parts.Add($"{git.Ahead} commit da pushare");
        }

        return string.Join(" · ", parts);
    }

    // Fuori dal dettaglio perche' l'host la mostra in rosso e il gattino la riporta.
    public static string BuildUpstreamWarning(GitStatus git)
        => git.Error is null && MissingUpstream(git) ? "nessun upstream configurato" : string.Empty;

    private static bool MissingUpstream(GitStatus git) => !git.HasUpstream && !git.IsDetached;

    public static string PullToolTip(GitStatus git) => git.Behind > 0
        ? $"git pull --ff-only — prende {git.Behind} commit da {git.Branch}"
        : "git pull --ff-only — niente da prendere secondo l'ultimo fetch";

    public static string StateLabel(MigrationState state) => state switch
    {
        MigrationState.Applied => MigrationLabels.Applied,
        MigrationState.PendingOutOfOrder => MigrationLabels.OutOfOrder,
        MigrationState.Unknown => MigrationLabels.Unknown,
        _ => MigrationLabels.Missing,
    };

    // Solo l'ultima riga di output: serve a mostrare che il comando e' vivo, non a fare da log.
    public static string Progress(string line) => line.Length > 90 ? line[..90] + "…" : line;

    public static string ApplyToolTip(int pending, string? database)
        => $"dotnet ef database update — applica {pending} migrazione/i su {database}";

    public const string ProtectedDatabaseTag = "DB protetto";

    public const string ProtectedDatabaseNote = "Database protetto: le migrazioni si leggono ma non si applicano, e lo schema non si altera.";

    public const string ProtectedDatabaseToolTip = "Database protetto (per esempio perché lo usano anche altri): le migrazioni si leggono ma niente update, rollback né copia del comando, e lo schema non si altera. Per un lavoro con migrazioni usa un altro ambiente.";

    public static string CopyUpdateToolTip(string migrationId)
        => $"Copia il comando dotnet ef database update fino a {migrationId}";

    // Glifi Segoe Fluent Icons: Play per andare avanti, Undo per un rollback.
    private const string ForwardGlyph = "";
    private const string RollbackGlyph = "";

    public static string RunUpdateGlyph(UpdatePlan plan) => plan.IsRollback ? RollbackGlyph : ForwardGlyph;

    public static string RunUpdateToolTip(UpdatePlan plan)
    {
        if (plan.IsNoOp)
        {
            return $"Il database e' gia' fermo a {plan.Target.DisplayName}";
        }

        var apply = plan.ToApply.Count > 0 ? $"applica {plan.ToApply.Count} migrazione/i" : null;

        return plan.IsRollback
            ? $"Rollback a {plan.Target.DisplayName}: annulla {plan.ToRevert.Count} migrazione/i"
                + (apply is null ? string.Empty : $" e {apply}")
                + " (chiede conferma)"
            : $"dotnet ef database update fino a {plan.Target.DisplayName}: {apply} (chiede conferma)";
    }

    public static string CopyUrlToolTip(string url) => url.Length > 0 ? $"Copia {url}" : "URL non disponibile";

    // Il rollback esegue i Down: tabelle e colonne create da quelle migrazioni spariscono con i
    // loro dati. La conferma lo dice e le elenca per nome, perche' e' l'ultimo punto in cui ci si
    // accorge di aver cliccato la riga sbagliata.
    public static Confirmation UpdateToConfirmation(
        string contextName,
        UpdatePlan plan,
        string clone,
        string? environmentLabel,
        string server,
        string database)
        => new(
            plan.IsRollback ? $"Rollback di {contextName}" : $"Aggiorna {contextName}",
            plan.IsRollback
                ? $"Riporto {contextName} a {plan.Target.DisplayName} su {clone}{EnvironmentSuffix(environmentLabel)}. Il rollback esegue i Down: quello che le migrazioni annullate hanno creato viene eliminato con i suoi dati."
                : $"Aggiorno {contextName} fino a {plan.Target.DisplayName} su {clone}{EnvironmentSuffix(environmentLabel)}.",
            [
                new($"Da annullare ({plan.ToRevert.Count})", plan.ToRevert.Select(e => new ConfirmationRow(e.DisplayName, Note: "Down")).ToList()),
                new($"Da applicare ({plan.ToApply.Count})", plan.ToApply.Select(e => new ConfirmationRow(e.DisplayName)).ToList()),
                Target(server, database),
            ],
            plan.IsRollback ? "Rollback" : "Aggiorna");

    // La conferma riporta clone, server e database: e' l'unico punto in cui puoi accorgerti di
    // essere puntato su un ambiente diverso da quello che credevi. Le variabili forzate sono
    // dichiarate perche' sono loro a decidere quale database viene toccato.
    public static Confirmation ApplyConfirmation(
        string contextName,
        string clone,
        string? environmentLabel,
        string server,
        string database)
        => new(
            $"Applica le migrazioni di {contextName}",
            $"Eseguo dotnet ef database update di {contextName} su {clone}{EnvironmentSuffix(environmentLabel)}: controlla server e database prima di confermare.",
            [Target(server, database)],
            "Applica");

    public static Confirmation PruneConfirmation(
        int count,
        IEnumerable<string> branches,
        string clone,
        string? environmentLabel)
        => new(
            "Pulizia branch",
            $"Elimino i branch locali di {clone}{EnvironmentSuffix(environmentLabel)} che non hanno piu' il branch remoto.",
            [new($"Branch da eliminare ({count})", branches.Select(b => new ConfirmationRow(b)).ToList())],
            "Elimina");

    private static ConfirmationList Target(string server, string database)
        => new("Destinazione",
        [
            new(server, "server"),
            new(database, "database"),
            new("ASPNETCORE_ENVIRONMENT e DOTNET_ENVIRONMENT", "env", "forzate a Development"),
        ]);

    private static string EnvironmentSuffix(string? environmentLabel)
        => environmentLabel is null ? string.Empty : $" ({environmentLabel})";
}

namespace Soffritto.Core;

public static class MigrationApplier
{
    /// <summary>
    /// Esegue <c>dotnet ef database update</c> sul context indicato: fino a
    /// <paramref name="targetMigrationId"/> (anche all'indietro), o tutte le pendenti se null.
    /// </summary>
    /// <remarks>
    /// ASPNETCORE_ENVIRONMENT viene forzato a Development perche' e' cio' che determina QUALE
    /// database viene toccato: senza, EF leggerebbe appsettings.json invece di
    /// appsettings.Development.json e migrerebbe un database diverso da quello che il pannello
    /// mostra. I profili di launchSettings.json oggi impostano Development, ma dipendere dal loro
    /// ordine per un'operazione distruttiva non e' accettabile.
    ///
    /// Il build non viene saltato (nessun --no-build): applicare migrazioni compilate da sorgenti
    /// non aggiornati e' peggio che attendere qualche decina di secondi.
    ///
    /// Con <paramref name="connection"/> il database e' quello indicato (--connection, e la
    /// variabile ConnectionStrings__&lt;nome&gt; del processo se <paramref name="connectionStringName"/>
    /// c'e'), non quello dei file del clone: serve al seed, il cui container non e' nei file.
    /// </remarks>
    public static Task<ProcessOutcome> UpdateDatabaseAsync(
        string solutionRoot,
        ContextDefinition context,
        string? targetMigrationId = null,
        Action<string>? onOutputLine = null,
        CancellationToken cancellationToken = default,
        string? connection = null,
        string? connectionStringName = null)
    {
        var environment = new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["DOTNET_ENVIRONMENT"] = "Development",
        };

        if (connection is not null && !string.IsNullOrWhiteSpace(connectionStringName))
        {
            environment[$"ConnectionStrings__{connectionStringName}"] = connection;
        }

        return ProcessRunner.RunAsync(
            "dotnet",
            BuildArguments(solutionRoot, context, targetMigrationId, connection),
            solutionRoot,
            timeoutSeconds: 600,
            environment: environment,
            onOutputLine: onOutputLine,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Il motivo leggibile di un <see cref="UpdateDatabaseAsync"/> fallito: le righe di errore di
    /// <c>dotnet build</c> sullo startup project quando EF si e' fermato in compilazione,
    /// altrimenti le righe di errore dell'output di EF.
    /// </summary>
    /// <remarks>
    /// Quando il build fallisce, <c>dotnet ef</c> stampa solo «Build failed. Use dotnet build to see
    /// the errors.»: in fondo all'output restano gli avvisi di NuGet. Il secondo build trova gli
    /// stessi errori del primo, perche' i sorgenti non sono cambiati.
    /// </remarks>
    public static async Task<string> FailureReasonAsync(
        string solutionRoot,
        ContextDefinition context,
        ProcessOutcome update,
        CancellationToken cancellationToken = default)
    {
        if (!(update.StandardOutput + update.StandardError).Contains("Build failed", StringComparison.OrdinalIgnoreCase))
        {
            return update.BuildError(4);
        }

        var build = await ProcessRunner.RunAsync(
            "dotnet",
            $"build {Quote(Path.Combine(solutionRoot, context.RelativeStartupProjectPath))} -nologo -clp:ErrorsOnly",
            solutionRoot,
            timeoutSeconds: 600,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return build.Success
            ? $"{update.BuildError(4)} (un secondo dotnet build riesce: riprova)"
            : $"build non riuscito: {build.BuildError(4)}";
    }

    /// <summary>
    /// Lo stesso comando del bottone, come riga pronta da incollare in un terminale:
    /// <paramref name="targetMigrationId"/> ferma l'aggiornamento a quella migrazione (o ci torna
    /// indietro), null applica tutte le pendenti.
    /// </summary>
    /// <remarks>
    /// Le due variabili d'ambiente viaggiano con la riga per la stessa ragione per cui il bottone
    /// le forza: senza, EF legge appsettings.json e migra un database diverso da quello mostrato
    /// nel pannello. La sintassi del prefisso e' PowerShell, il terminale predefinito di Visual
    /// Studio; i percorsi sono assoluti, quindi la riga funziona da qualunque cartella.
    /// </remarks>
    public static string BuildUpdateCommandLine(
        string solutionRoot,
        ContextDefinition context,
        string? targetMigrationId = null,
        string? connection = null)
    {
        return "$env:ASPNETCORE_ENVIRONMENT='Development'; $env:DOTNET_ENVIRONMENT='Development'; "
            + $"dotnet {BuildArguments(solutionRoot, context, targetMigrationId, connection)}";
    }

    private static string BuildArguments(string solutionRoot, ContextDefinition context, string? targetMigrationId, string? connection)
    {
        // La riga non passa da una shell che sappia fare l'escape: una virgoletta la spezzerebbe.
        if (connection is not null && connection.Contains('"'))
        {
            throw new ArgumentException("La connection string non puo' contenere virgolette doppie.", nameof(connection));
        }

        var parts = new List<string> { "ef", "database", "update" };

        // EF vuole il target subito dopo "update", prima delle opzioni.
        if (!string.IsNullOrWhiteSpace(targetMigrationId))
        {
            parts.Add(targetMigrationId);
        }

        parts.AddRange(
        [
            "--project", Quote(Path.Combine(solutionRoot, context.RelativeProjectPath)),
            "--startup-project", Quote(Path.Combine(solutionRoot, context.RelativeStartupProjectPath)),
            "--context", context.Name,
        ]);

        if (connection is not null)
        {
            parts.AddRange(["--connection", Quote(connection)]);
        }

        return string.Join(' ', parts);
    }

    private static string Quote(string path) => $"\"{path}\"";
}

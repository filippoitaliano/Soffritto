using Soffritto.Core.Docker;

namespace Soffritto.Core;

public static class StatusProbe
{
    /// <param name="tunnel">La porta del pool del repository: il tunnel si legge da li', con la sola porta assegnata.</param>
    public static async Task<StatusReport> RunAsync(
        string solutionRoot,
        ProbeOptions options,
        bool fetchGit,
        bool probeDevTunnel = false,
        CancellationToken cancellationToken = default,
        PoolAssignment? tunnel = null)
    {
        var gitTask = GitStatusReader.ReadAsync(solutionRoot, fetchGit, cancellationToken);

        var tunnelTask = !probeDevTunnel
            ? null
            : tunnel is null
                ? Task.FromResult(DevTunnelStatus.NotShared("non condiviso"))
                : DevTunnelReader.ReadAsync(solutionRoot, tunnel.TunnelDescription, cancellationToken, tunnel.Port);

        var report = await RunMigrationsAsync(solutionRoot, options, cancellationToken).ConfigureAwait(false);

        return report with
        {
            Git = await gitTask.ConfigureAwait(false),
            DevTunnel = tunnelTask is null ? null : await tunnelTask.ConfigureAwait(false),
        };
    }

    /// <summary>
    /// Solo migrazioni e database, senza git ne' dev tunnel (Git resta al suo default):
    /// permette a chi orchestra di applicare ogni sezione appena la sua lettura finisce.
    /// </summary>
    /// <param name="databaseOffline">
    /// Il database e' gia' noto come spento o in avvio (container di un ambiente Docker): niente
    /// connessione, le migrazioni del codice restano non lette e il motivo va in
    /// <see cref="StatusReport.DatabaseOffline"/>, non tra gli errori.
    /// </param>
    public static async Task<StatusReport> RunMigrationsAsync(
        string solutionRoot,
        ProbeOptions options,
        CancellationToken cancellationToken = default,
        string? databaseOffline = null)
    {
        var knownByContext = options.Contexts.ToDictionary(
            ctx => ctx.Name,
            ctx => MigrationScanner.Scan(Path.Combine(solutionRoot, ctx.RelativeMigrationsPath)));

        var (connectionString, _, configError) = ConnectionStringLocator.Find(solutionRoot, options);

        HistoryReadResult history = connectionString is null
            ? new HistoryReadResult([], null, null, null)
            : databaseOffline is not null
                ? OfflineHistory(connectionString)
                : await MigrationsHistoryReader.ReadAsync(connectionString, cancellationToken: cancellationToken).ConfigureAwait(false);

        var appliedById = history.Applied.ToDictionary(a => a.MigrationId, StringComparer.Ordinal);

        var contexts = new List<ContextStatus>();
        foreach (var definition in options.Contexts)
        {
            var known = knownByContext[definition.Name];

            // Frontiera oltre la quale una migrazione non applicata e' semplicemente "nuova":
            // sotto di essa e' un buco lasciato da un branch parallelo.
            var lastAppliedForContext = known
                .Where(m => appliedById.ContainsKey(m.MigrationId))
                .Select(m => m.MigrationId)
                .LastOrDefault();

            var entries = known
                .Select(m =>
                {
                    var mismatch = m.FileNameMismatch ? m.FileName : null;

                    if (appliedById.TryGetValue(m.MigrationId, out var applied))
                    {
                        return new MigrationEntry(m.MigrationId, MigrationState.Applied, applied.ProductVersion, mismatch);
                    }

                    var outOfOrder = lastAppliedForContext is not null
                        && string.CompareOrdinal(m.MigrationId, lastAppliedForContext) < 0;

                    return new MigrationEntry(
                        m.MigrationId,
                        outOfOrder ? MigrationState.PendingOutOfOrder : MigrationState.Pending,
                        MismatchedFileName: mismatch);
                })
                .ToList();

            contexts.Add(new ContextStatus(
                definition.Name,
                Path.Combine(solutionRoot, definition.RelativeMigrationsPath),
                entries));
        }

        // I due context condividono la stessa __EFMigrationsHistory: una riga e' davvero
        // sconosciuta solo se non appartiene a NESSUN set noto, altrimenti le migrazioni
        // di Identity risulterebbero sconosciute all'altro context e viceversa.
        var allKnown = knownByContext.Values
            .SelectMany(migrations => migrations)
            .Select(m => m.MigrationId)
            .ToHashSet(StringComparer.Ordinal);

        var unknown = history.Applied
            .Where(a => !allKnown.Contains(a.MigrationId))
            .Select(a => new MigrationEntry(a.MigrationId, MigrationState.Unknown, a.ProductVersion))
            .OrderBy(e => e.MigrationId, StringComparer.Ordinal)
            .ToList();

        return new StatusReport(
            SolutionRoot: solutionRoot,
            Server: history.Server,
            Database: history.Database,
            Contexts: contexts,
            Unknown: unknown,
            Git: new GitStatus(),
            TimestampLocal: DateTimeOffset.Now,
            DatabaseError: history.Error,
            ConfigurationError: configError,
            DatabaseOffline: connectionString is null ? null : databaseOffline,
            DatabaseReached: history.Connected);
    }

    // Server e database dalla connection string, senza connettersi: la card li mostra comunque.
    private static HistoryReadResult OfflineHistory(string connectionString)
    {
        try
        {
            var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString);
            return new HistoryReadResult([], builder.DataSource, builder.InitialCatalog, null);
        }
        catch (Exception)
        {
            return new HistoryReadResult([], null, null, null);
        }
    }
}

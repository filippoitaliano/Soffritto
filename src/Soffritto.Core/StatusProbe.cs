using Soffritto.Core.Docker;

namespace Soffritto.Core;

public static class StatusProbe
{
    /// <param name="tunnel">The repository's pool port: the tunnel is read from there, with only the assigned port.</param>
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
                ? Task.FromResult(DevTunnelStatus.NotShared("not shared"))
                : DevTunnelReader.ReadAsync(solutionRoot, tunnel.TunnelDescription, cancellationToken, tunnel.Port);

        var report = await RunMigrationsAsync(solutionRoot, options, cancellationToken).ConfigureAwait(false);

        return report with
        {
            Git = await gitTask.ConfigureAwait(false),
            DevTunnel = tunnelTask is null ? null : await tunnelTask.ConfigureAwait(false),
        };
    }

    /// <summary>
    /// Only migrations and database, without git or dev tunnel (Git keeps its default):
    /// lets the orchestrator apply each section as soon as its read finishes.
    /// </summary>
    /// <param name="databaseOffline">
    /// The database is already known to be stopped or starting (a Docker environment's container): no
    /// connection, the code's migrations stay unread and the reason goes into
    /// <see cref="StatusReport.DatabaseOffline"/>, not among the errors.
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

            // The boundary past which an unapplied migration is simply "new":
            // below it, it is a gap left by a parallel branch.
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

        // The two contexts share the same __EFMigrationsHistory: a row is truly
        // unknown only if it belongs to NONE of the known sets, otherwise the Identity
        // migrations would look unknown to the other context and vice versa.
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

    // Server and database from the connection string, without connecting: the card shows them anyway.
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

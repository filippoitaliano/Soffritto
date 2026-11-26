using Microsoft.Data.SqlClient;

namespace Soffritto.Core;

public sealed record AppliedMigration(string MigrationId, string? ProductVersion);

/// <param name="Connected">The database responded: when the history table is missing there is an error, but the empty history is real.</param>
public sealed record HistoryReadResult(
    IReadOnlyList<AppliedMigration> Applied,
    string? Server,
    string? Database,
    string? Error,
    bool Connected = false);

public static class MigrationsHistoryReader
{
    private const string HistoryTable = "__EFMigrationsHistory";

    public static async Task<HistoryReadResult> ReadAsync(
        string connectionString,
        int connectTimeoutSeconds = 10,
        CancellationToken cancellationToken = default)
    {
        SqlConnectionStringBuilder builder;
        try
        {
            builder = new SqlConnectionStringBuilder(connectionString)
            {
                ConnectTimeout = connectTimeoutSeconds,
                ApplicationName = "Soffritto",
            };
        }
        catch (Exception ex)
        {
            return new HistoryReadResult([], null, null, $"Invalid connection string: {ex.Message}");
        }

        var server = builder.DataSource;
        var database = builder.InitialCatalog;

        try
        {
            await using var connection = new SqlConnection(builder.ConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var existsCommand = connection.CreateCommand();
            existsCommand.CommandText = "SELECT OBJECT_ID(@table, 'U')";
            existsCommand.CommandTimeout = connectTimeoutSeconds;
            existsCommand.Parameters.AddWithValue("@table", HistoryTable);

            var objectId = await existsCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (objectId is null or DBNull)
            {
                return new HistoryReadResult(
                    [],
                    server,
                    database,
                    $"Table {HistoryTable} is missing: no migration has ever been applied to this database.",
                    Connected: true);
            }

            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT [MigrationId], [ProductVersion] FROM [{HistoryTable}] ORDER BY [MigrationId]";
            command.CommandTimeout = connectTimeoutSeconds;

            var applied = new List<AppliedMigration>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                applied.Add(new AppliedMigration(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1)));
            }

            return new HistoryReadResult(applied, server, database, null, Connected: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new HistoryReadResult([], server, database, ex.Message);
        }
    }
}

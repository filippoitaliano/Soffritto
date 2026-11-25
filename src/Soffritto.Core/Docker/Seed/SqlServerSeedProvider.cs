using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Soffritto.Core.Configuration;

namespace Soffritto.Core.Docker.Seed;

/// <summary>
/// Il seed su SQL Server in un container: RESTORE e BACKUP dall'host con SqlClient e docker cp,
/// compattazione dei file prima del BACKUP, container temporaneo del seed.
/// </summary>
public sealed class SqlServerSeedProvider : ISeedProvider
{
    private const int CommandTimeoutSeconds = 3600;

    private const string SeedInContainer = "/tmp/seed.bak";

    private const string BackupInContainer = "/tmp/out.bak";

    private const string DataFolderInContainer = "/var/opt/mssql/data";

    public SeedKind Kind => SeedKind.SqlServer;

    public async Task<string?> StartTemporaryAsync(AppConfiguration c, Action<string>? log = null, CancellationToken ct = default)
    {
        var sql = c.PrimarySeed()?.Seed?.SqlServer;
        if (sql is null)
        {
            return "nessun backend ha un seed configurato";
        }

        if (sql.Password.Contains('"'))
        {
            return "la password SQL del seed non puo' contenere virgolette doppie";
        }

        var name = DockerNames.SeedContainer;
        if (await RemoveIfOursAsync(name, log, ct).ConfigureAwait(false) is { } error)
        {
            return error;
        }

        var port = c.Docker.DatabasePortBase;
        var run = await DockerCli.RunAsync(
            $"run --rm -d --name {name} --label {DockerNames.Label} -p 127.0.0.1:{port}:{DockerSlot.SqlServerPort} "
            + $"-e ACCEPT_EULA=Y -e MSSQL_PID=Developer -e \"MSSQL_SA_PASSWORD={sql.Password}\" -e MSSQL_MEMORY_LIMIT_MB={sql.MemoryLimitMb} {sql.Image}",
            timeoutSeconds: 900,
            ct: ct).ConfigureAwait(false);
        if (!run.Success)
        {
            return $"container temporaneo del seed non avviato: {run.Tail(2)}";
        }

        log?.Invoke($"container temporaneo {name} avviato su 127.0.0.1:{port}");
        return null;
    }

    public Task<string?> StopTemporaryAsync(Action<string>? log = null, CancellationToken ct = default)
        => RemoveIfOursAsync(DockerNames.SeedContainer, log, ct);

    public async Task<string?> WaitReadyAsync(string hostConnectionString, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var builder = new SqlConnectionStringBuilder(hostConnectionString) { InitialCatalog = "master", ConnectTimeout = 5, Pooling = false };
        var limit = timeout ?? TimeSpan.FromSeconds(120);
        var started = DateTime.UtcNow;
        var last = "nessuna risposta";
        while (true)
        {
            try
            {
                await using var connection = new SqlConnection(builder.ConnectionString);
                await connection.OpenAsync(ct).ConfigureAwait(false);
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT 1";
                command.CommandTimeout = 5;
                if (await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is 1)
                {
                    return null;
                }
            }
            catch (SqlException ex)
            {
                last = ex.Message;
            }

            if (DateTime.UtcNow - started > limit)
            {
                return $"SQL Server non pronto in {limit.TotalSeconds:0} s: {last}";
            }

            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }
    }

    public async Task<string?> RestoreAsync(string container, string bakFile, string hostConnectionString, string database, Action<string>? log = null, CancellationToken ct = default)
    {
        if (await GuardAsync(container, hostConnectionString, database, ct).ConfigureAwait(false) is { } guard)
        {
            return guard;
        }

        var copy = await DockerCli.CopyToAsync(container, bakFile, SeedInContainer, ct).ConfigureAwait(false);
        if (!copy.Success)
        {
            return $"docker cp del seed non riuscito: {copy.Tail(2)}";
        }

        try
        {
            var master = Master(hostConnectionString);
            var files = await QueryAsync(master, $"RESTORE FILELISTONLY FROM DISK = N'{SeedInContainer}'", ct).ConfigureAwait(false);
            var total = files.Rows.Cast<DataRow>().Sum(r => Convert.ToInt64(r["Size"], CultureInfo.InvariantCulture));
            log?.Invoke($"il seed ripristina file per {(total / 1e9).ToString("0.0", CultureInfo.InvariantCulture)} GB ({total} byte)");

            var moves = files.Rows.Cast<DataRow>().Select(r =>
            {
                var fileId = Convert.ToInt64(r["FileId"], CultureInfo.InvariantCulture);
                var suffix = (string)r["Type"] == "L" ? "_log.ldf" : fileId == 1 ? ".mdf" : $"_{fileId}.ndf";
                return $"MOVE N'{Literal((string)r["LogicalName"])}' TO N'{DataFolderInContainer}/{Literal(database)}{suffix}'";
            });
            await ExecuteAsync(master, $"RESTORE DATABASE {Name(database)} FROM DISK = N'{SeedInContainer}' WITH {string.Join(", ", moves)}, REPLACE, RECOVERY", ct).ConfigureAwait(false);
            log?.Invoke($"database {database} ripristinato");
            return null;
        }
        catch (SqlException ex)
        {
            return $"RESTORE non riuscito: {ex.Message}";
        }
        finally
        {
            await DockerCli.ExecAsync(container, $"rm -f {SeedInContainer}", user: "0", ct: CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Compatta i file del database (recovery SIMPLE, CHECKPOINT, SHRINKFILE per file_id con target
    /// esplicito) e poi fa il BACKUP compresso in <paramref name="targetFile"/>, che non deve esistere.
    /// Modifica il database da cui legge: si chiama solo sul container temporaneo del seed. Errore o null.
    /// </summary>
    public async Task<string?> BackupAsync(string container, string hostConnectionString, string database, string targetFile, Action<string>? log = null, CancellationToken ct = default)
    {
        if (!string.Equals(container, DockerNames.SeedContainer, StringComparison.Ordinal))
        {
            return $"il backup compatta il database: si fa solo sul container temporaneo del seed ({DockerNames.SeedContainer}), non su {container}";
        }

        if (await GuardAsync(container, hostConnectionString, database, ct).ConfigureAwait(false) is { } guard)
        {
            return guard;
        }

        if (File.Exists(targetFile))
        {
            return $"{targetFile} esiste gia': il backup non lo sovrascrive";
        }

        var temporary = targetFile + ".tmp";
        var done = false;
        try
        {
            var onDatabase = new SqlConnectionStringBuilder(hostConnectionString) { InitialCatalog = database, Pooling = false }.ConnectionString;
            await ExecuteAsync(onDatabase, $"ALTER DATABASE {Name(database)} SET RECOVERY SIMPLE", ct).ConfigureAwait(false);
            await ExecuteAsync(onDatabase, "CHECKPOINT", ct).ConfigureAwait(false);

            // Per file_id: i nomi logici cambiano da un seed all'altro e possono contenere un punto.
            // Senza target esplicito lo shrink si ferma alla dimensione di creazione del file.
            var before = await QueryAsync(onDatabase, "SELECT file_id, type, name, size, CAST(FILEPROPERTY(name, 'SpaceUsed') AS bigint) AS used FROM sys.database_files", ct).ConfigureAwait(false);
            foreach (DataRow row in before.Rows)
            {
                var fileId = Convert.ToInt32(row["file_id"], CultureInfo.InvariantCulture);
                var type = Convert.ToInt32(row["type"], CultureInfo.InvariantCulture);
                var used = row["used"] is DBNull ? 0L : Convert.ToInt64(row["used"], CultureInfo.InvariantCulture);
                int? target = type switch
                {
                    0 => (int)Math.Ceiling(used / 128.0) + 64,
                    1 => 64,
                    _ => null,
                };
                if (target is { } megabytes)
                {
                    await ExecuteAsync(onDatabase, $"DBCC SHRINKFILE({fileId}, {megabytes})", ct).ConfigureAwait(false);
                }
            }

            var after = await QueryAsync(onDatabase, "SELECT file_id, size FROM sys.database_files", ct).ConfigureAwait(false);
            foreach (DataRow row in before.Rows)
            {
                var fileId = Convert.ToInt32(row["file_id"], CultureInfo.InvariantCulture);
                var sizeAfter = after.Rows.Cast<DataRow>().FirstOrDefault(r => Convert.ToInt32(r["file_id"], CultureInfo.InvariantCulture) == fileId)?["size"];
                log?.Invoke($"file {fileId} ({row["name"]}): {Megabytes(row["size"])} MB prima, {(sizeAfter is null ? "?" : Megabytes(sizeAfter))} MB dopo la compattazione");
            }

            await ExecuteAsync(Master(hostConnectionString), $"BACKUP DATABASE {Name(database)} TO DISK = N'{BackupInContainer}' WITH COPY_ONLY, INIT, COMPRESSION", ct).ConfigureAwait(false);
            var copy = await DockerCli.CopyFromAsync(container, BackupInContainer, temporary, ct).ConfigureAwait(false);
            if (!copy.Success)
            {
                return $"docker cp del backup non riuscito: {copy.Tail(2)}";
            }

            File.Move(temporary, targetFile, overwrite: false);
            done = true;
            log?.Invoke($"backup scritto in {targetFile} ({new FileInfo(targetFile).Length} byte)");
            return null;
        }
        catch (SqlException ex)
        {
            return $"compattazione o BACKUP non riusciti: {ex.Message}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"backup non scritto in {targetFile}: {ex.Message}";
        }
        finally
        {
            await DockerCli.ExecAsync(container, $"rm -f {BackupInContainer}", user: "0", ct: CancellationToken.None).ConfigureAwait(false);
            if (!done)
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    public Task<HistoryReadResult> ReadAppliedMigrationsAsync(string hostConnectionString, CancellationToken ct = default)
        => MigrationsHistoryReader.ReadAsync(new SqlConnectionStringBuilder(hostConnectionString) { Pooling = false }.ConnectionString, cancellationToken: ct);

    // La connessione deve puntare alla porta che il container pubblica sull'host e al database
    // atteso: cosi' RESTORE e BACKUP non partono verso un altro server.
    private static async Task<string?> GuardAsync(string container, string hostConnectionString, string database, CancellationToken ct)
    {
        var port = await DockerCli.RunAsync($"port {container} {DockerSlot.SqlServerPort}/tcp", timeoutSeconds: 15, ct: ct).ConfigureAwait(false);
        var published = port.Success
            ? port.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => l.StartsWith("127.0.0.1:", StringComparison.Ordinal))
                .Select(l => int.TryParse(l["127.0.0.1:".Length..], out var p) ? p : (int?)null)
                .FirstOrDefault(p => p is not null)
            : null;
        if (published is not { } hostPort)
        {
            return $"il container {container} non pubblica SQL Server su 127.0.0.1: operazione fermata";
        }

        return DatabaseGuard.CheckConnectionString(hostConnectionString, hostPort, database);
    }

    private static async Task<string?> RemoveIfOursAsync(string name, Action<string>? log, CancellationToken ct)
    {
        if (await DockerCli.ContainerIdAsync(name, ct).ConfigureAwait(false) is null)
        {
            return null;
        }

        var label = await DockerCli.ContainerLabelAsync(name, DockerNames.LabelKey, ct).ConfigureAwait(false);
        if (label != DockerNames.DataFolderTag)
        {
            return $"il container {name} esiste ma non e' di questa cartella dati (etichetta {DockerNames.LabelKey} {(string.IsNullOrEmpty(label) ? "assente" : label)}): non lo tocco";
        }

        var remove = await DockerCli.RunAsync($"rm -f {name}", timeoutSeconds: 60, ct: ct).ConfigureAwait(false);
        if (!remove.Success)
        {
            return $"container {name} non rimosso: {remove.Tail(2)}";
        }

        log?.Invoke($"container temporaneo {name} rimosso");
        return null;
    }

    // Pooling = false in tutte le connessioni del provider: il container temporaneo si ricrea
    // sulla stessa porta, e una connessione rimasta nel pool verso quello rimosso fallirebbe.
    private static string Master(string connectionString)
        => new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master", Pooling = false }.ConnectionString;

    private static async Task<DataTable> QueryAsync(string connectionString, string sql, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = CommandTimeoutSeconds;
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var table = new DataTable();
        table.Load(reader);
        return table;
    }

    private static async Task ExecuteAsync(string connectionString, string sql, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = CommandTimeoutSeconds;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static string Name(string identifier) => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";

    private static string Literal(string text) => text.Replace("'", "''", StringComparison.Ordinal);

    private static string Megabytes(object pages) => (Convert.ToInt64(pages, CultureInfo.InvariantCulture) / 128.0).ToString("0.0", CultureInfo.InvariantCulture);
}

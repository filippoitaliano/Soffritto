using Microsoft.Data.SqlClient;

namespace Soffritto.Core.Docker;

/// <summary>
/// The guards on a Docker environment's database: before reading migrations, updating,
/// restoring or starting the api, the connection must point to its slot's SQL Server.
/// Messages state file, server and database, never the password.
/// </summary>
public static class DatabaseGuard
{
    private static readonly string[] LocalHosts = ["127.0.0.1", "localhost"];

    /// <summary>
    /// The connection the worktree uses (the first appsettings with the connection string, like
    /// <see cref="ConnectionStringLocator"/>) points to 127.0.0.1,&lt;port&gt; and to the expected database. Error or null.
    /// </summary>
    public static string? Check(string root, ProbeOptions probe, int port, string database)
    {
        var (connection, file, error) = ConnectionStringLocator.Find(root, probe);
        if (connection is null)
        {
            return $"cannot verify the slot database in {root}: {error}";
        }

        return Compare(connection, $"{file} in {root}", HostMatches(port), $"127.0.0.1,{port}", database);
    }

    /// <summary>
    /// The port of the SQL Server on this machine (127.0.0.1,&lt;port&gt; or localhost,&lt;port&gt;) that
    /// the clone's connection points to; null if it points elsewhere or cannot be read. This way a local
    /// environment can use the database in a Docker slot's container.
    /// </summary>
    public static int? LocalPortOf(string root, ProbeOptions probe)
    {
        if (ConnectionStringLocator.Find(root, probe).ConnectionString is not { } connection)
        {
            return null;
        }

        try
        {
            var server = Normalize(new SqlConnectionStringBuilder(connection).DataSource);
            var comma = server.LastIndexOf(',');
            return comma > 0
                && LocalHosts.Contains(server[..comma], StringComparer.OrdinalIgnoreCase)
                && int.TryParse(server[(comma + 1)..], out var port)
                    ? port
                    : null;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or KeyNotFoundException)
        {
            return null;
        }
    }

    /// <summary>A connection from the host (e.g. to the temporary seed container) points to 127.0.0.1,&lt;port&gt; and to the expected database.</summary>
    public static string? CheckConnectionString(string connectionString, int port, string database)
        => Compare(connectionString, "the connection", HostMatches(port), $"127.0.0.1,{port}", database);

    /// <summary>
    /// The api container's environment (name → value, as compose resolves it) has
    /// ConnectionStrings__&lt;name&gt; pointing to the slot's database service and to the expected database.
    /// </summary>
    public static string? CheckContainer(IReadOnlyDictionary<string, string> container, string connectionStringName, string dbService, string database)
    {
        var name = $"ConnectionStrings__{connectionStringName}";
        var value = container.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
        if (string.IsNullOrWhiteSpace(value))
        {
            return $"the api container has no {name}: without it, it would use the connection from the image's files instead of the slot database";
        }

        return Compare(
            value,
            $"the container's {name}",
            server => string.Equals(server, dbService, StringComparison.OrdinalIgnoreCase) || string.Equals(server, $"{dbService},1433", StringComparison.OrdinalIgnoreCase),
            $"{dbService},1433",
            database);
    }

    private static Func<string, bool> HostMatches(int port)
        => server => LocalHosts.Any(h => string.Equals(server, $"{h},{port}", StringComparison.OrdinalIgnoreCase));

    private static string? Compare(string connection, string where, Func<string, bool> serverMatches, string expectedServer, string database)
    {
        SqlConnectionStringBuilder builder;
        try
        {
            builder = new SqlConnectionStringBuilder(connection);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or KeyNotFoundException)
        {
            return $"{where}: unreadable connection string";
        }

        var server = Normalize(builder.DataSource);
        var catalog = builder.InitialCatalog;
        if (serverMatches(server) && string.Equals(catalog, database, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return $"{where} points to {DisplayOrMissing(builder.DataSource)} / {DisplayOrMissing(catalog)}, not to the slot database {expectedServer} / {database}: operation stopped";
    }

    private static string Normalize(string dataSource)
    {
        var server = dataSource.Replace(" ", string.Empty);
        return server.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase) ? server[4..] : server;
    }

    private static string DisplayOrMissing(string value) => value.Length > 0 ? value : "(empty)";
}

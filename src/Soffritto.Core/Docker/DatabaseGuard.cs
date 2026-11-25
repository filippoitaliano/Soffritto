using Microsoft.Data.SqlClient;

namespace Soffritto.Core.Docker;

/// <summary>
/// Le guardie sul database di un ambiente Docker: prima di leggere migrazioni, aggiornare,
/// ripristinare o avviare l'api, la connessione deve puntare al SQL Server del suo slot.
/// I messaggi dicono file, server e database, mai la password.
/// </summary>
public static class DatabaseGuard
{
    private static readonly string[] LocalHosts = ["127.0.0.1", "localhost"];

    /// <summary>
    /// La connessione che il worktree usa (il primo appsettings con la connection string, come
    /// <see cref="ConnectionStringLocator"/>) punta a 127.0.0.1,&lt;port&gt; e al database atteso. Errore o null.
    /// </summary>
    public static string? Check(string root, ProbeOptions probe, int port, string database)
    {
        var (connection, file, error) = ConnectionStringLocator.Find(root, probe);
        if (connection is null)
        {
            return $"database dello slot non verificabile in {root}: {error}";
        }

        return Compare(connection, $"{file} di {root}", HostMatches(port), $"127.0.0.1,{port}", database);
    }

    /// <summary>
    /// La porta del SQL Server sulla macchina (127.0.0.1,&lt;porta&gt; o localhost,&lt;porta&gt;) a cui
    /// punta la connessione del clone; null se punta altrove o non si legge. Un ambiente locale
    /// puo' usare cosi' il database nel container di uno slot Docker.
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

    /// <summary>Una connessione dall'host (es. al container temporaneo del seed) punta a 127.0.0.1,&lt;port&gt; e al database atteso.</summary>
    public static string? CheckConnectionString(string connectionString, int port, string database)
        => Compare(connectionString, "la connessione", HostMatches(port), $"127.0.0.1,{port}", database);

    /// <summary>
    /// L'ambiente del container dell'api (nome → valore, come lo risolve compose) ha
    /// ConnectionStrings__&lt;nome&gt; sul servizio del database dello slot e sul database atteso.
    /// </summary>
    public static string? CheckContainer(IReadOnlyDictionary<string, string> container, string connectionStringName, string dbService, string database)
    {
        var name = $"ConnectionStrings__{connectionStringName}";
        var value = container.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
        if (string.IsNullOrWhiteSpace(value))
        {
            return $"il container dell'api non ha {name}: senza, userebbe la connessione dei file dell'immagine e non il database dello slot";
        }

        return Compare(
            value,
            $"{name} del container",
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
            return $"{where}: connection string non leggibile";
        }

        var server = Normalize(builder.DataSource);
        var catalog = builder.InitialCatalog;
        if (serverMatches(server) && string.Equals(catalog, database, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return $"{where} punta a {DisplayOrMissing(builder.DataSource)} / {DisplayOrMissing(catalog)}, non al database dello slot {expectedServer} / {database}: operazione fermata";
    }

    private static string Normalize(string dataSource)
    {
        var server = dataSource.Replace(" ", string.Empty);
        return server.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase) ? server[4..] : server;
    }

    private static string DisplayOrMissing(string value) => value.Length > 0 ? value : "(vuoto)";
}

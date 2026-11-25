using Soffritto.Core.Configuration;

namespace Soffritto.Core.Docker.Seed;

/// <summary>
/// Le operazioni di un sistema di persistenza sul seed: container temporaneo, ripristino,
/// migrazioni applicate e backup. Oggi c'e' solo SQL Server (<see cref="SqlServerSeedProvider"/>).
/// </summary>
public interface ISeedProvider
{
    SeedKind Kind { get; }

    /// <summary>Avvia il container temporaneo del seed della cartella dati, sulla porta databasePortBase. Errore o null.</summary>
    Task<string?> StartTemporaryAsync(AppConfiguration c, Action<string>? log = null, CancellationToken ct = default);

    /// <summary>Ferma e rimuove il container temporaneo del seed, se e' di questa cartella dati. Errore o null.</summary>
    Task<string?> StopTemporaryAsync(Action<string>? log = null, CancellationToken ct = default);

    /// <summary>Attende che il server risponda, fino a <paramref name="timeout"/>. Errore (con l'ultimo messaggio) o null.</summary>
    Task<string?> WaitReadyAsync(string hostConnectionString, TimeSpan? timeout = null, CancellationToken ct = default);

    /// <summary>Ripristina il backup nel database del container; nel log quanto spazio occupano i file. Errore o null.</summary>
    Task<string?> RestoreAsync(string container, string bakFile, string hostConnectionString, string database, Action<string>? log = null, CancellationToken ct = default);

    /// <summary>Compatta il database e lo salva in <paramref name="targetFile"/>: solo sul container temporaneo del seed. Errore o null.</summary>
    Task<string?> BackupAsync(string container, string hostConnectionString, string database, string targetFile, Action<string>? log = null, CancellationToken ct = default);

    /// <summary>Le migrazioni registrate nel database della connessione.</summary>
    Task<HistoryReadResult> ReadAppliedMigrationsAsync(string hostConnectionString, CancellationToken ct = default);
}

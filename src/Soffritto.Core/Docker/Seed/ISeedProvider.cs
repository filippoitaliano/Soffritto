using Soffritto.Core.Configuration;

namespace Soffritto.Core.Docker.Seed;

/// <summary>
/// A persistence system's operations on the seed: temporary container, restore,
/// applied migrations and backup. Today there is only SQL Server (<see cref="SqlServerSeedProvider"/>).
/// </summary>
public interface ISeedProvider
{
    SeedKind Kind { get; }

    /// <summary>Starts the data folder's temporary seed container on port databasePortBase. Error or null.</summary>
    Task<string?> StartTemporaryAsync(AppConfiguration c, Action<string>? log = null, CancellationToken ct = default);

    /// <summary>Stops and removes the temporary seed container, if it belongs to this data folder. Error or null.</summary>
    Task<string?> StopTemporaryAsync(Action<string>? log = null, CancellationToken ct = default);

    /// <summary>Waits for the server to respond, up to <paramref name="timeout"/>. Error (with the last message) or null.</summary>
    Task<string?> WaitReadyAsync(string hostConnectionString, TimeSpan? timeout = null, CancellationToken ct = default);

    /// <summary>Restores the backup into the container's database and logs how much space the files take. Error or null.</summary>
    Task<string?> RestoreAsync(string container, string bakFile, string hostConnectionString, string database, Action<string>? log = null, CancellationToken ct = default);

    /// <summary>Shrinks the database and saves it to <paramref name="targetFile"/>: only on the temporary seed container. Error or null.</summary>
    Task<string?> BackupAsync(string container, string hostConnectionString, string database, string targetFile, Action<string>? log = null, CancellationToken ct = default);

    /// <summary>The migrations recorded in the connection's database.</summary>
    Task<HistoryReadResult> ReadAppliedMigrationsAsync(string hostConnectionString, CancellationToken ct = default);
}

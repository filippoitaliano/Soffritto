namespace Soffritto.Core.Configuration;

/// <summary>The seed's persistence system: decides which provider restores and saves it.</summary>
public enum SeedKind
{
    SqlServer,
}

/// <summary>A repository's database seed: the backup Docker environments start from.</summary>
public sealed class SeedConfiguration
{
    public SeedKind Kind { get; set; } = SeedKind.SqlServer;

    /// <summary>The seed file (.bak, or .7z with a single .bak).</summary>
    public string Path { get; set; } = string.Empty;

    public SqlServerSeedConfiguration SqlServer { get; set; } = new();

    public static readonly IReadOnlyList<string> Extensions = [".bak", ".7z"];
}

/// <summary>The containers' SQL Server: the slots' one and the temporary seed one.</summary>
public sealed class SqlServerSeedConfiguration
{
    public const string DefaultImage = "mcr.microsoft.com/mssql/server:2022-latest";

    /// <summary>The suggested password: it only protects containers bound to 127.0.0.1 holding development data.</summary>
    public const string ProposedPassword = "Soffritto_Sql1";

    public string Image { get; set; } = DefaultImage;

    public string DatabaseName { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public int MemoryLimitMb { get; set; } = 2048;

    /// <summary>The database service in the backend's compose file.</summary>
    public string Service { get; set; } = "db";
}

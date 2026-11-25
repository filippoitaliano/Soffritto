namespace Soffritto.Core.Configuration;

/// <summary>Il sistema di persistenza del seed: decide quale provider lo ripristina e lo salva.</summary>
public enum SeedKind
{
    SqlServer,
}

/// <summary>Il seed del database di un repository: il backup da cui partono gli ambienti Docker.</summary>
public sealed class SeedConfiguration
{
    public SeedKind Kind { get; set; } = SeedKind.SqlServer;

    /// <summary>Il file del seed (.bak o .7z con un solo .bak).</summary>
    public string Path { get; set; } = string.Empty;

    public SqlServerSeedConfiguration SqlServer { get; set; } = new();

    public static readonly IReadOnlyList<string> Extensions = [".bak", ".7z"];
}

/// <summary>Il SQL Server dei container: quello degli slot e quello temporaneo del seed.</summary>
public sealed class SqlServerSeedConfiguration
{
    public const string DefaultImage = "mcr.microsoft.com/mssql/server:2022-latest";

    /// <summary>La password proposta: protegge solo container legati a 127.0.0.1 con dati di sviluppo.</summary>
    public const string ProposedPassword = "Soffritto_Sql1";

    public string Image { get; set; } = DefaultImage;

    public string DatabaseName { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public int MemoryLimitMb { get; set; } = 2048;

    /// <summary>Il servizio del database nel compose del backend.</summary>
    public string Service { get; set; } = "db";
}

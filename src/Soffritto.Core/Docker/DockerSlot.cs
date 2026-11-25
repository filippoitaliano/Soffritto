using Soffritto.Core.Configuration;

namespace Soffritto.Core.Docker;

/// <summary>
/// Tutto quello che si calcola da un ambiente Docker N: progetto compose, cartella dello slot,
/// porte, connessioni al database del suo SQL Server, repository nei container e worktree.
/// </summary>
public sealed class DockerSlot
{
    public const int SqlServerPort = 1433;

    public const string SlotEnvFile = "slot.env";

    public const string OverrideFile = "soffritto.override.yaml";

    private DockerSlot()
    {
    }

    /// <summary>N; 0 per il container temporaneo del seed.</summary>
    public int Number { get; private init; }

    public bool IsSeed { get; private init; }

    public string Project { get; private init; } = string.Empty;

    /// <summary>La cartella di slot.env, override ed env_file: anche la cartella di lavoro dei comandi compose.</summary>
    public string ComposeFolder { get; private init; } = string.Empty;

    public string SlotEnvPath => Path.Combine(ComposeFolder, SlotEnvFile);

    public string OverridePath => Path.Combine(ComposeFolder, OverrideFile);

    public int DatabasePort { get; private init; }

    public string DatabaseService { get; private init; } = "db";

    public string DatabaseName { get; private init; } = string.Empty;

    public string HostConnectionString { get; private init; } = string.Empty;

    /// <summary>La connessione vista dai container: il servizio del database sulla rete del progetto.</summary>
    public string ContainerConnectionString { get; private init; } = string.Empty;

    /// <summary>Il backend predefinito, nel container dell'api; null se l'ambiente non lo include.</summary>
    public EnvironmentRepository? Backend { get; private init; }

    /// <summary>Il frontend predefinito, nel container del web; null se l'ambiente non lo include.</summary>
    public EnvironmentRepository? Frontend { get; private init; }

    /// <summary>Le porte host dello slot (database e servizi pubblicati).</summary>
    public IReadOnlyList<SlotPort> Ports { get; private init; } = [];

    /// <summary>I repository dell'ambiente con le cartelle dei loro worktree.</summary>
    public IReadOnlyList<EnvironmentRepository> Repositories { get; private init; } = [];

    public string? BackendWorktree => Backend?.Path;

    public string? FrontendWorktree => Frontend?.Path;

    public bool HasContainerizedFrontend => Frontend is not null;

    public string? ApiService => Backend?.Repository.DockerService;

    public string? WebService => Frontend?.Repository.DockerService;

    /// <summary>La prima porta pubblicata del repository: host e container; null se non e' in un container.</summary>
    public (int Host, int Container)? PortOf(EnvironmentRepository repository)
    {
        if (!SameId(repository, Backend) && !SameId(repository, Frontend))
        {
            return null;
        }

        return repository.Repository.DockerPublishedPorts.Select(p => PortMapping.TryParse(p, out var m) ? m : (PortMapping?)null).FirstOrDefault(m => m is not null) is { } mapping
            ? (mapping.HostPort(Number), mapping.Container)
            : null;
    }

    /// <summary>L'URL dell'api dall'host (http://127.0.0.1:&lt;porta&gt;): 127.0.0.1 perche' le porte dello slot sono legate li'.</summary>
    public string? ApiHostUrl => Backend is { } backend && PortOf(backend) is { } port ? $"http://127.0.0.1:{port.Host}" : null;

    /// <summary>L'URL dell'api dagli altri container: il servizio sulla rete del progetto.</summary>
    public string? ApiContainerUrl => Backend is { } backend && PortOf(backend) is { } port ? $"http://{backend.Repository.DockerService}:{port.Container}" : null;

    /// <summary>Lo slot di un ambiente Docker; <paramref name="composeFolder"/> sostituisce &lt;cartella dati&gt;\docker\ambiente-N (prove).</summary>
    public static DockerSlot Of(AppConfiguration c, EnvironmentDefinition environment, string? composeFolder = null)
    {
        var sql = c.PrimarySeed()?.Seed?.SqlServer ?? new SqlServerSeedConfiguration();
        var port = c.Docker.DatabasePortBase + environment.Number;
        var containerized = c.ContainerizedOf(environment);
        return new DockerSlot
        {
            Number = environment.Number,
            Project = DockerNames.Project(environment.Number),
            ComposeFolder = composeFolder ?? Path.Combine(ConfigurationFiles.DataFolder, "docker", $"ambiente-{environment.Number}"),
            DatabasePort = port,
            DatabaseService = sql.Service,
            DatabaseName = sql.DatabaseName,
            HostConnectionString = Connection($"127.0.0.1,{port}", sql),
            ContainerConnectionString = Connection($"{sql.Service},{SqlServerPort}", sql),
            Backend = containerized.FirstOrDefault(r => r.Type == RepositoryType.Backend),
            Frontend = containerized.FirstOrDefault(r => r.Type == RepositoryType.Frontend),
            Ports = c.PortsOf(environment),
            Repositories = c.RepositoriesOf(environment),
        };
    }

    /// <summary>
    /// Il container temporaneo del seed: porta databasePortBase, worktree del backend con {n} =
    /// "seed" (es. backend-seed), nessun container dell'api.
    /// </summary>
    public static DockerSlot Seed(AppConfiguration c)
    {
        var sql = c.PrimarySeed()?.Seed?.SqlServer ?? new SqlServerSeedConfiguration();
        var port = c.Docker.DatabasePortBase;
        var backend = c.PrimarySeed() ?? c.Backend;
        var environment = new EnvironmentDefinition { Number = 0, Kind = EnvironmentKind.Docker, Repositories = [backend.Id], Root = c.DockerWorktreeRoot };
        var worktree = new EnvironmentRepository(backend, environment, Path.Combine(c.DockerWorktreeRoot, AppConfiguration.Resolve(backend.Folder, "seed")), IsPrimary: true);
        return new DockerSlot
        {
            Number = 0,
            IsSeed = true,
            Project = DockerNames.SeedContainer,
            ComposeFolder = Path.Combine(ConfigurationFiles.DataFolder, "docker", "seed"),
            DatabasePort = port,
            DatabaseService = sql.Service,
            DatabaseName = sql.DatabaseName,
            HostConnectionString = Connection($"127.0.0.1,{port}", sql),
            ContainerConnectionString = Connection($"{sql.Service},{SqlServerPort}", sql),
            Backend = worktree,
            Ports = [new SlotPort(sql.Service, port, SqlServerPort)],
            Repositories = [worktree],
        };
    }

    private static bool SameId(EnvironmentRepository repository, EnvironmentRepository? other)
        => other is not null && string.Equals(repository.Id, other.Id, StringComparison.OrdinalIgnoreCase);

    private static string Connection(string server, SqlServerSeedConfiguration sql)
        => $"Server={server};Database={sql.DatabaseName};User ID=sa;Password={sql.Password};TrustServerCertificate=True";
}

using Soffritto.Core.Configuration;

namespace Soffritto.Core.Docker;

/// <summary>
/// Everything derived from a Docker environment N: compose project, slot folder,
/// ports, connections to its SQL Server database, repositories in containers and worktrees.
/// </summary>
public sealed class DockerSlot
{
    public const int SqlServerPort = 1433;

    public const string SlotEnvFile = "slot.env";

    public const string OverrideFile = "soffritto.override.yaml";

    private DockerSlot()
    {
    }

    /// <summary>N; 0 for the temporary seed container.</summary>
    public int Number { get; private init; }

    public bool IsSeed { get; private init; }

    public string Project { get; private init; } = string.Empty;

    /// <summary>The folder of slot.env, override and env_file: also the working folder of compose commands.</summary>
    public string ComposeFolder { get; private init; } = string.Empty;

    public string SlotEnvPath => Path.Combine(ComposeFolder, SlotEnvFile);

    public string OverridePath => Path.Combine(ComposeFolder, OverrideFile);

    public int DatabasePort { get; private init; }

    public string DatabaseService { get; private init; } = "db";

    public string DatabaseName { get; private init; } = string.Empty;

    public string HostConnectionString { get; private init; } = string.Empty;

    /// <summary>The connection as seen from containers: the database service on the project's network.</summary>
    public string ContainerConnectionString { get; private init; } = string.Empty;

    /// <summary>The primary backend, in the api container; null if the environment does not include it.</summary>
    public EnvironmentRepository? Backend { get; private init; }

    /// <summary>The primary frontend, in the web container; null if the environment does not include it.</summary>
    public EnvironmentRepository? Frontend { get; private init; }

    /// <summary>The slot's host ports (database and published services).</summary>
    public IReadOnlyList<SlotPort> Ports { get; private init; } = [];

    /// <summary>The environment's repositories with their worktree folders.</summary>
    public IReadOnlyList<EnvironmentRepository> Repositories { get; private init; } = [];

    public string? BackendWorktree => Backend?.Path;

    public string? FrontendWorktree => Frontend?.Path;

    public bool HasContainerizedFrontend => Frontend is not null;

    public string? ApiService => Backend?.Repository.DockerService;

    public string? WebService => Frontend?.Repository.DockerService;

    /// <summary>The repository's first published port: host and container; null if it is not in a container.</summary>
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

    /// <summary>The api URL from the host (http://127.0.0.1:&lt;port&gt;): 127.0.0.1 because the slot's ports are bound there.</summary>
    public string? ApiHostUrl => Backend is { } backend && PortOf(backend) is { } port ? $"http://127.0.0.1:{port.Host}" : null;

    /// <summary>The api URL from the other containers: the service on the project's network.</summary>
    public string? ApiContainerUrl => Backend is { } backend && PortOf(backend) is { } port ? $"http://{backend.Repository.DockerService}:{port.Container}" : null;

    /// <summary>A Docker environment's slot; <paramref name="composeFolder"/> replaces &lt;data folder&gt;\docker\ambiente-N (tests).</summary>
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
    /// The temporary seed container: port databasePortBase, backend worktree with {n} =
    /// "seed" (e.g. backend-seed), no api container.
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

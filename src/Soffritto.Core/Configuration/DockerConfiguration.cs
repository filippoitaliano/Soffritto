namespace Soffritto.Core.Configuration;

/// <summary>Settings shared by Docker environments: where worktrees are created, database ports, the template environment.</summary>
public sealed class DockerConfiguration
{
    /// <summary>The folder for worktrees and source clones; empty = &lt;root&gt;\ambienti-docker.</summary>
    public string WorktreeRoot { get; set; } = string.Empty;

    /// <summary>Base of the databases' host ports: slot N uses base + N, the seed container uses the base itself.</summary>
    public int DatabasePortBase { get; set; } = DockerDefaults.DatabasePortBase;

    /// <summary>The local environment constants, secrets and local files are read from; null = the first local one.</summary>
    public int? TemplateEnvironment { get; set; }
}

/// <summary>A repository's service in a Docker environment; empty fields take the type's defaults (<see cref="DockerDefaults"/>).</summary>
public sealed class DockerServiceConfiguration
{
    /// <summary>Ports as "base:container port": slot N publishes base + N.</summary>
    public List<string> PublishedPorts { get; set; } = [];

    /// <summary>The slot's compose file, relative to the repository root (backend only).</summary>
    public string Compose { get; set; } = string.Empty;

    /// <summary>The Dockerfile, relative to the repository root: it is also the path relative to the build context.</summary>
    public string Dockerfile { get; set; } = string.Empty;

    /// <summary>Absolute folder of a checkout with the Docker files, copied into the worktree when the branch lacks them.</summary>
    public string FilesFallback { get; set; } = string.Empty;

    /// <summary>The service name in the compose file.</summary>
    public string Service { get; set; } = string.Empty;

    /// <summary>Files copied from the template environment to the worktree, relative to the repository root.</summary>
    public List<string> LocalFiles { get; set; } = [];
}

/// <summary>A published port: slot N exposes it on the host at <see cref="Base"/> + N.</summary>
public readonly record struct PortMapping(int Base, int Container)
{
    public int HostPort(int number) => Base + number;

    public override string ToString() => $"{Base}:{Container}";

    public static bool TryParse(string? text, out PortMapping mapping)
    {
        mapping = default;
        var parts = (text ?? string.Empty).Split(':');
        if (parts.Length != 2
            || !int.TryParse(parts[0].Trim(), out var hostBase)
            || !int.TryParse(parts[1].Trim(), out var container)
            || hostBase is < 1 or > 65535 - AppConfiguration.MaxEnvironmentNumber
            || container is < 1 or > 65535)
        {
            return false;
        }

        mapping = new PortMapping(hostBase, container);
        return true;
    }
}

/// <summary>A host port of a Docker environment, with the compose service that publishes it.</summary>
public sealed record SlotPort(string Service, int Host, int Container);

/// <summary>The suggested values for Docker environments: used by empty configuration fields and by the setup's "Suggested values".</summary>
public static class DockerDefaults
{
    public const int DatabasePortBase = 14000;

    public const string WorktreeFolder = "ambienti-docker";

    public static string WorktreeRoot(string root) => Path.Combine(root, WorktreeFolder);

    public static string Compose(RepositoryType type) => type == RepositoryType.Backend ? "compose.yaml" : string.Empty;

    public static string Dockerfile(RepositoryType type) => type is RepositoryType.Backend or RepositoryType.Frontend ? "Dockerfile" : string.Empty;

    public static string Service(RepositoryType type) => type switch
    {
        RepositoryType.Backend => "api",
        RepositoryType.Frontend => "web",
        _ => string.Empty,
    };

    public static IReadOnlyList<string> PublishedPorts(RepositoryType type) => type switch
    {
        RepositoryType.Backend => ["15000:8080"],
        RepositoryType.Frontend => ["18000:8082"],
        _ => [],
    };
}

using System.Text.Json.Serialization;

namespace Soffritto.Core.Configuration;

/// <summary>The type of a catalog repository: decides fields, card and tools.</summary>
public enum RepositoryType
{
    Backend,
    Frontend,
    App,
    Generic,
}

public enum AzureAuthentication
{
    /// <summary>The Windows user running the app, with no secrets to keep.</summary>
    Windows,

    /// <summary>Personal access token, like the Azure DevOps MCP server.</summary>
    Pat,
}

/// <summary>
/// The configuration of the whole product: the repository catalog, the environments that
/// include them, where the clones live and Azure DevOps. The app, VSIX, CLI and MCP read the same file
/// (see <see cref="ConfigurationFiles"/>).
/// </summary>
public sealed partial class AppConfiguration
{
    public const int CurrentSchemaVersion = 2;

    /// <summary>Placeholder for the environment number in clone folder names and in tunnel Descriptions.</summary>
    public const string NumberPlaceholder = "{n}";

    public const int MaxEnvironmentNumber = 99;

    [JsonPropertyOrder(-2)]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>The folder that holds the clones of all environments.</summary>
    [JsonPropertyOrder(-1)]
    public string Root { get; set; } = string.Empty;

    /// <summary>The repository catalog, in order: it is the order of the cards in the columns.</summary>
    [JsonPropertyOrder(1)]
    public List<RepositoryConfiguration> Repositories { get; set; } = [];

    [JsonPropertyOrder(2)]
    public List<EnvironmentDefinition> Environments { get; set; } = [];

    [JsonPropertyOrder(3)]
    public AzureDevOpsConfiguration AzureDevOps { get; set; } = new();

    [JsonPropertyOrder(4)]
    public DockerConfiguration Docker { get; set; } = new();

    [JsonPropertyOrder(5)]
    public TunnelPoolConfiguration TunnelPool { get; set; } = new();

    /// <summary>The Docker environments, by increasing number.</summary>
    [JsonIgnore]
    public IEnumerable<EnvironmentDefinition> DockerEnvironments
        => Environments.Where(e => e.Kind == EnvironmentKind.Docker).OrderBy(e => e.Number);

    /// <summary>The folder for the worktrees and source clones of Docker environments.</summary>
    [JsonIgnore]
    public string DockerWorktreeRoot
        => Docker.WorktreeRoot is { Length: > 0 } folder ? folder : DockerDefaults.WorktreeRoot(Root);

    /// <summary>The primary backend, if it has a seed configured.</summary>
    public BackendConfiguration? PrimarySeed()
        => Primary(RepositoryType.Backend) is BackendConfiguration { Seed: not null } backend ? backend : null;

    /// <summary>The template environment: the chosen one, or the first local one; null without local environments.</summary>
    public int? TemplateNumber()
        => Docker.TemplateEnvironment ?? LocalEnvironments.Select(e => (int?)e.Number).FirstOrDefault();

    /// <summary>The repositories of a Docker environment that run in a container: the primary backend and frontend.</summary>
    public IReadOnlyList<EnvironmentRepository> ContainerizedOf(EnvironmentDefinition environment)
        => environment.Kind == EnvironmentKind.Docker
            ? RepositoriesOf(environment).Where(r => r.IsPrimary && r.Type is RepositoryType.Backend or RepositoryType.Frontend).ToList()
            : [];

    /// <summary>The host ports of a Docker environment: the database and those published by the containerized repositories.</summary>
    public IReadOnlyList<SlotPort> PortsOf(EnvironmentDefinition environment)
    {
        var ports = new List<SlotPort>();
        if (environment.Kind != EnvironmentKind.Docker)
        {
            return ports;
        }

        var database = PrimarySeed()?.Seed?.SqlServer.Service ?? "db";
        ports.Add(new SlotPort(database, Docker.DatabasePortBase + environment.Number, 1433));
        foreach (var repository in ContainerizedOf(environment))
        {
            foreach (var text in repository.Repository.DockerPublishedPorts)
            {
                if (PortMapping.TryParse(text, out var mapping))
                {
                    ports.Add(new SlotPort(repository.Repository.DockerService, mapping.HostPort(environment.Number), mapping.Container));
                }
            }
        }

        return ports;
    }

    /// <summary>The first backend in the catalog: the one VSIX and CLI work on.</summary>
    [JsonIgnore]
    public BackendConfiguration Backend => Primary(RepositoryType.Backend) as BackendConfiguration ?? new BackendConfiguration();

    /// <summary>The local environments, i.e. the ones the grid shows, by increasing number.</summary>
    [JsonIgnore]
    public IEnumerable<EnvironmentDefinition> LocalEnvironments
        => Environments.Where(e => e.Kind == EnvironmentKind.Local).OrderBy(e => e.Number);

    /// <summary>The numbers of the local environments, increasing.</summary>
    [JsonIgnore]
    public IEnumerable<int> Numbers => LocalEnvironments.Select(e => e.Number);

    public EnvironmentDefinition? Environment(int number) => Environments.FirstOrDefault(e => e.Number == number);

    public RepositoryConfiguration? Repository(string id)
        => Repositories.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The first catalog repository of that type; generic repositories have no primary.</summary>
    public RepositoryConfiguration? Primary(RepositoryType type)
        => type == RepositoryType.Generic ? null : Repositories.FirstOrDefault(r => r.Type == type);

    /// <summary>
    /// The repositories included in the environment, in catalog order. The first of each type (generic
    /// ones excluded) is the primary: it keeps the historical console and tunnel names.
    /// </summary>
    public IReadOnlyList<EnvironmentRepository> RepositoriesOf(EnvironmentDefinition environment)
    {
        var seen = new HashSet<RepositoryType>();
        var result = new List<EnvironmentRepository>();
        foreach (var repository in Repositories)
        {
            if (!environment.Repositories.Contains(repository.Id, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var isPrimary = repository.Type != RepositoryType.Generic && seen.Add(repository.Type);
            result.Add(new EnvironmentRepository(repository, environment, FolderOf(repository, environment), isPrimary));
        }

        return result;
    }

    /// <summary>The primary repository of that type in environment N; null if the environment includes none.</summary>
    public EnvironmentRepository? PrimaryOf(RepositoryType type, int number)
        => Environment(number) is { } environment
            ? RepositoriesOf(environment).FirstOrDefault(r => r.IsPrimary && r.Type == type)
            : null;

    public string FolderOf(RepositoryConfiguration repository, EnvironmentDefinition environment)
        => Path.Combine(
            environment.Root ?? (environment.Kind == EnvironmentKind.Docker ? DockerWorktreeRoot : Root),
            Resolve(repository.Folder, environment.Number));

    /// <summary>Replaces {n} with the environment number.</summary>
    public static string Resolve(string template, int number) => Resolve(template, number.ToString());

    /// <summary>Replaces {n} with any value (e.g. "seed" for the seed worktree).</summary>
    public static string Resolve(string template, string value)
        => template.Replace(NumberPlaceholder, value, StringComparison.OrdinalIgnoreCase);

    /// <summary>The local environment whose primary backend is in that folder, if any.</summary>
    public int? EnvironmentOfBackend(string backendPath)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(backendPath));
        foreach (var number in Numbers)
        {
            if (PrimaryOf(RepositoryType.Backend, number) is { } backend
                && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(backend.Path)), full, StringComparison.OrdinalIgnoreCase))
            {
                return number;
            }
        }

        return null;
    }

    /// <summary>
    /// The environment, local or Docker, whose primary backend is in that folder, with that
    /// backend; null if none. Unlike <see cref="EnvironmentOfBackend"/> it also covers
    /// the worktrees of Docker environments.
    /// </summary>
    public (EnvironmentDefinition Environment, EnvironmentRepository Backend)? BackendEnvironmentOf(string backendPath)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(backendPath));
        foreach (var environment in Environments.OrderBy(e => e.Number))
        {
            if (PrimaryOf(RepositoryType.Backend, environment.Number) is { } backend
                && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(backend.Path)), full, StringComparison.OrdinalIgnoreCase))
            {
                return (environment, backend);
            }
        }

        return null;
    }

    /// <summary>From a primary backend's folder, why schema writes are refused (null: allowed).</summary>
    public string? SchemaLockOfBackend(string backendPath)
        => EnvironmentOfBackend(backendPath) is { } number ? Environment(number)?.SchemaLockReason : null;

    public ProbeOptions ToProbeOptions() => ToProbeOptions(Backend);

    public static ProbeOptions ToProbeOptions(BackendConfiguration backend) => new(
        backend.Contexts
            .Select(c => new ContextDefinition(c.Name, c.Migrations, c.Project, c.StartupProject))
            .ToList(),
        backend.AppSettings.ToList(),
        backend.ConnectionStringName);

    /// <summary>The first-run configuration: empty backend, frontend and app, one environment.</summary>
    public static AppConfiguration CreateDefault() => new()
    {
        Repositories =
        [
            new BackendConfiguration
            {
                Id = "backend",
                DevTunnel = new DevTunnelConfiguration(),
            },
            new FrontendConfiguration
            {
                Id = "frontend",
                DevTunnel = new DevTunnelConfiguration(),
            },
            new AppRepoConfiguration { Id = "app" },
        ],
        Environments = [new EnvironmentDefinition { Number = 1, Repositories = ["backend", "frontend", "app"] }],
    };

    /// <summary>The errors that prevent using the configuration, worded for the user.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(Root))
        {
            errors.Add("The clones folder is missing.");
        }
        else if (!Path.IsPathFullyQualified(Root))
        {
            errors.Add("The clones folder must be a full path (e.g. C:\\Dev).");
        }

        ValidateRepositories(errors);
        ValidateEnvironments(errors);
        ValidateTunnels(errors);
        ValidateDocker(errors);

        if (AzureDevOps.IsConfigured)
        {
            if (!Uri.TryCreate(AzureDevOps.CollectionUrl, UriKind.Absolute, out _))
            {
                errors.Add("The Azure DevOps collection URL is not a valid address.");
            }

            if (string.IsNullOrWhiteSpace(AzureDevOps.Project))
            {
                errors.Add("The Azure DevOps project is missing.");
            }
        }

        return errors;
    }

    /// <summary>
    /// Configurations that are allowed but worth knowing about, worded for the user: they do not prevent saving.
    /// </summary>
    public IReadOnlyList<string> Warnings()
    {
        var warnings = new List<string>();

        foreach (var environment in Environments.OrderBy(e => e.Number))
        {
            var included = RepositoriesOf(environment);

            foreach (var group in included.Where(r => r.Type != RepositoryType.Generic).GroupBy(r => r.Type).Where(g => g.Count() > 1))
            {
                var primary = group.First(r => r.IsPrimary);
                var other = group.First(r => !r.IsPrimary);
                warnings.Add($"Environment {environment.Number}: several repositories of type {TypeName(group.Key)} ({string.Join(", ", group.Select(r => r.Id))}): the primary is {primary.Id}, the others use derived names ({ServiceOf(group.Key)}-{other.Key}...).");
            }

            if (environment.Kind == EnvironmentKind.Docker
                && included.FirstOrDefault(r => r.IsPrimary && r.Type == RepositoryType.Backend) is { } dockerBackend
                && !(dockerBackend.Repository.Env?.ContainerExclude ?? []).Any(p => p.Length > 0 && "Kestrel:".StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            {
                warnings.Add($"Environment {environment.Number}: no containerExclude prefix of {dockerBackend.Id} covers Kestrel:, so the template environment's Kestrel endpoints reach the container and override the Dockerfile's port 8080.");
            }

            foreach (var primary in included.Where(r => r.IsPrimary))
            {
                if (Primary(primary.Type) is { } first && !ReferenceEquals(first, primary.Repository)
                    && !included.Any(r => ReferenceEquals(r.Repository, first)))
                {
                    warnings.Add($"Environment {environment.Number} uses {primary.Id} as its {TypeName(primary.Type)}, not {first.Id} (the first in the catalog): VSIX and CLI on this environment use the settings of {first.Id}.");
                }
            }
        }

        if (Repositories.Count(r => r.Type == RepositoryType.Backend) > 1)
        {
            warnings.Add("With more than one backend, open the VSIX only on an environment's primary backend: on another backend its consoles would take the primary's kestrel-N marker.");
        }

        return warnings;
    }

    public static string TypeName(RepositoryType type) => type switch
    {
        RepositoryType.Backend => "backend",
        RepositoryType.Frontend => "frontend",
        RepositoryType.App => "app",
        _ => "generic",
    };

    private static string ServiceOf(RepositoryType type) => type switch
    {
        RepositoryType.Backend => "kestrel",
        RepositoryType.Frontend => "frontend",
        _ => "app",
    };

    private void ValidateRepositories(List<string> errors)
    {
        if (Repositories.Count == 0)
        {
            errors.Add("The catalog has no repositories: add at least one.");
        }

        foreach (var repository in Repositories)
        {
            var name = repository.Id.Length > 0 ? repository.Id : "(no id)";
            if (!IdPattern().IsMatch(repository.Id))
            {
                errors.Add($"The id \"{repository.Id}\" is not valid: lowercase letters, digits and hyphens, starting with a letter, at most 24 characters.");
            }

            var folder = repository.Folder;
            if (string.IsNullOrWhiteSpace(folder))
            {
                errors.Add($"The folder of repository {name} is missing.");
            }
            else if (folder.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                errors.Add($"The folder of repository {name} contains invalid characters.");
            }
            else if (EnvironmentsIncluding(repository) > 1 && !folder.Contains(NumberPlaceholder, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"The folder of repository {name} must contain {NumberPlaceholder}: it is in more than one environment, and the folders would be the same.");
            }

            if (repository is BackendConfiguration backend)
            {
                foreach (var context in backend.Contexts)
                {
                    if (string.IsNullOrWhiteSpace(context.Name) || string.IsNullOrWhiteSpace(context.Migrations)
                        || string.IsNullOrWhiteSpace(context.Project) || string.IsNullOrWhiteSpace(context.StartupProject))
                    {
                        errors.Add($"Context \"{context.Name}\" of {name} has empty fields: name, migrations, project and startup project are all required.");
                    }
                }
            }
        }

        foreach (var duplicate in Repositories.Where(r => r.Id.Length > 0).GroupBy(r => r.Id, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            errors.Add($"The id \"{duplicate.Key}\" appears more than once in the catalog.");
        }
    }

    private void ValidateEnvironments(List<string> errors)
    {
        if (Environments.Count == 0)
        {
            errors.Add("At least one environment is required.");
        }

        foreach (var environment in Environments)
        {
            if (environment.Number is < 1 or > MaxEnvironmentNumber)
            {
                errors.Add($"Environment {environment.Number} is out of range: numbers go from 1 to {MaxEnvironmentNumber}.");
            }

            if (environment.Repositories.Count == 0)
            {
                errors.Add($"Environment {environment.Number} does not include any repository.");
            }

            foreach (var id in environment.Repositories.Where(id => Repository(id) is null))
            {
                errors.Add($"Environment {environment.Number} includes \"{id}\", which is not in the catalog.");
            }

            if (environment.Root is { } root && !Path.IsPathFullyQualified(root))
            {
                errors.Add($"The folder of environment {environment.Number} must be a full path.");
            }

            if (environment.ProtectedDatabase && environment.Kind == EnvironmentKind.Docker)
            {
                errors.Add($"Environment {environment.Number} is a Docker environment: it always has its own database and cannot be protected.");
            }
        }

        foreach (var duplicate in Environments.GroupBy(e => e.Number).Where(g => g.Count() > 1))
        {
            errors.Add($"Environment number {duplicate.Key} appears more than once.");
        }

        if (Path.IsPathFullyQualified(Root))
        {
            var folders = Environments
                .Where(e => e.Root is null || Path.IsPathFullyQualified(e.Root))
                .SelectMany(e => RepositoriesOf(e))
                .Where(r => r.Repository.Folder.Length > 0 && r.Repository.Folder.IndexOfAny(Path.GetInvalidPathChars()) < 0)
                .GroupBy(r => Path.GetFullPath(r.Path), StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();
            if (folders.Count > 0)
            {
                errors.Add($"Two repositories would end up in the same folder: {string.Join(", ", folders)}.");
            }
        }
    }

    private void ValidateTunnels(List<string> errors)
    {
        foreach (var repository in Repositories)
        {
            if (repository.DevTunnel is not { } tunnel)
            {
                continue;
            }

            var name = repository.Id;
            foreach (var port in tunnel.Ports ?? [])
            {
                if (port.Number is < 1 or > 65535 || port.Protocol is not ("http" or "https"))
                {
                    errors.Add($"The dev tunnel of {name} has an invalid port ({port.Number}/{port.Protocol}): number between 1 and 65535, protocol http or https.");
                }
            }

            if (repository.Type == RepositoryType.Generic && (tunnel.Ports is null || tunnel.Ports.Count == 0))
            {
                errors.Add($"The dev tunnel of generic repository {name} has no ports: for generic repositories they must be written by hand.");
            }
        }
    }

    private void ValidateDocker(List<string> errors)
    {
        if (Docker.WorktreeRoot.Length > 0 && !Path.IsPathFullyQualified(Docker.WorktreeRoot))
        {
            errors.Add("The Docker worktree folder must be a full path.");
        }

        if (Docker.DatabasePortBase is < 1 or > 65535 - MaxEnvironmentNumber)
        {
            errors.Add($"The database port base ({Docker.DatabasePortBase}) is out of range: between 1 and {65535 - MaxEnvironmentNumber}.");
        }

        if (Docker.TemplateEnvironment is { } template && Environment(template) is not { Kind: EnvironmentKind.Local })
        {
            errors.Add($"Template environment {template} is not a local environment.");
        }

        foreach (var repository in Repositories)
        {
            ValidateDockerService(repository, errors);
            ValidateEnv(repository, errors);
            ValidateSeed(repository, errors);
        }

        ValidatePortRanges(errors);

        for (var i = 0; i < TunnelPool.Tunnels.Count; i++)
        {
            var tunnel = TunnelPool.Tunnels[i];
            if (string.IsNullOrWhiteSpace(tunnel.Description) || tunnel.Description.Any(char.IsWhiteSpace))
            {
                errors.Add($"Pool tunnel {i + 1} has an empty Description or one with spaces.");
            }

            if (string.IsNullOrWhiteSpace(tunnel.Label) || tunnel.Label.Any(char.IsWhiteSpace) || tunnel.Label.Contains(','))
            {
                errors.Add($"Pool tunnel {i + 1} has an empty Label or one with spaces or commas.");
            }
        }

        foreach (var duplicate in TunnelPool.Tunnels.Where(t => t.Description.Length > 0).GroupBy(t => t.Description, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            errors.Add($"Description {duplicate.Key} appears more than once in the tunnel pool.");
        }

        foreach (var environment in DockerEnvironments)
        {
            ValidateDockerEnvironment(environment, errors);
        }
    }

    private void ValidateDockerEnvironment(EnvironmentDefinition environment, List<string> errors)
    {
        var number = environment.Number;
        var included = RepositoriesOf(environment);
        if (included.FirstOrDefault(r => r.IsPrimary && r.Type == RepositoryType.Backend) is not { Repository: BackendConfiguration backend })
        {
            errors.Add($"Docker environment {number} does not include a backend: the slot's compose file lives in the backend.");
        }
        else
        {
            if (backend.DockerCompose.Length == 0 || backend.DockerDockerfile.Length == 0)
            {
                errors.Add($"Docker environment {number}: backend {backend.Id} has no compose file or Dockerfile.");
            }

            var connection = $"ConnectionStrings:{backend.ConnectionStringName}";
            if (backend.Env?.Slot.FirstOrDefault(v => string.Equals(v.Name, connection, StringComparison.OrdinalIgnoreCase)) is not { } variable
                || string.IsNullOrWhiteSpace(variable.Local) || string.IsNullOrWhiteSpace(variable.Container))
            {
                errors.Add($"Docker environment {number}: the per-slot variable {connection} of backend {backend.Id} is missing or empty (both local and container values are required: without them the slot database is not guaranteed).");
            }

            if (PrimarySeed() is null)
            {
                errors.Add($"Docker environment {number}: backend {backend.Id} has no database seed.");
            }
        }

        foreach (var frontend in included.Where(r => r.IsPrimary && r.Type == RepositoryType.Frontend && r.Repository.DockerDockerfile.Length == 0))
        {
            errors.Add($"Docker environment {number}: frontend {frontend.Id} has no Dockerfile.");
        }

        foreach (var repository in included)
        {
            if (!repository.Repository.Folder.Contains(NumberPlaceholder, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"Docker environment {number}: the folder of {repository.Id} must contain {NumberPlaceholder}.");
            }

            if (string.IsNullOrWhiteSpace(repository.Repository.SourceClone))
            {
                errors.Add($"Docker environment {number}: {repository.Id} has no source clone.");
            }
        }

        if (TemplateNumber() is null)
        {
            errors.Add("Docker environments read constants and secrets from a local template environment: there is none.");
        }
    }

    private static void ValidateDockerService(RepositoryConfiguration repository, List<string> errors)
    {
        var name = repository.Id;
        if (repository.SourceClone is { Length: > 0 } source && !Path.IsPathFullyQualified(source))
        {
            errors.Add($"The source clone of {name} must be a full path.");
        }

        if (repository.Docker is not { } docker)
        {
            return;
        }

        foreach (var (field, value) in new[] { ("compose", docker.Compose), ("Dockerfile", docker.Dockerfile) })
        {
            if (value.Length > 0 && !IsRelativeInside(value))
            {
                errors.Add($"The {field} of {name} must be a path relative to the repository root, without '..' or a drive ({value}).");
            }
        }

        if (docker.FilesFallback.Length > 0 && !Path.IsPathFullyQualified(docker.FilesFallback))
        {
            errors.Add($"The fallback Docker files folder of {name} must be a full path.");
        }

        foreach (var port in docker.PublishedPorts.Where(p => !PortMapping.TryParse(p, out _)))
        {
            errors.Add($"The published port \"{port}\" of {name} is not valid: use base:container port (e.g. 15000:8080).");
        }

        foreach (var file in docker.LocalFiles)
        {
            if (!IsRelativeInside(file))
            {
                errors.Add($"The local file \"{file}\" of {name} must be a path relative to the repository root.");
            }
            else if (repository.Env is { } env
                && (SamePath(file, env.Target) || (env.Secrets is { } secrets && SamePath(Path.GetFileName(file), Path.GetFileName(secrets.Source)))))
            {
                errors.Add($"The local file \"{file}\" of {name} is written by the env adapter (target or secrets source): remove it from the local files.");
            }
        }
    }

    private void ValidateEnv(RepositoryConfiguration repository, List<string> errors)
    {
        if (repository.Env is not { } env)
        {
            return;
        }

        var name = repository.Id;
        if (env.Adapter != EnvAdapterKind.Manual)
        {
            if (string.IsNullOrWhiteSpace(env.Target))
            {
                errors.Add($"The env of {name} has an adapter but no target file.");
            }
            else if (!IsRelativeInside(env.Target))
            {
                errors.Add($"The env target of {name} must be a path relative to the repository root.");
            }
        }

        if (env.ConstantsSource.Length > 0 && !IsRelativeInside(env.ConstantsSource))
        {
            errors.Add($"The constants source of {name} must be a path relative to the repository root.");
        }

        if (env.Secrets is { } secrets)
        {
            if (secrets.Adapter == EnvAdapterKind.Manual)
            {
                errors.Add($"The secrets source of {name} cannot be manual: secrets are not written in setup.");
            }

            if (string.IsNullOrWhiteSpace(secrets.Source))
            {
                errors.Add($"The secrets source of {name} has no file.");
            }
        }

        foreach (var variable in env.Manual.Where(m => string.IsNullOrWhiteSpace(m.Name)))
        {
            errors.Add($"A manual variable of {name} has no name.");
        }

        foreach (var variable in env.Slot)
        {
            if (string.IsNullOrWhiteSpace(variable.Name))
            {
                errors.Add($"A per-slot value of {name} has no name.");
                continue;
            }

            foreach (var placeholder in EnvConfiguration.PlaceholdersIn(variable.Local).Concat(EnvConfiguration.PlaceholdersIn(variable.Container)))
            {
                if (EnvConfiguration.Placeholders.Contains(placeholder, StringComparer.Ordinal))
                {
                    continue;
                }

                var inner = placeholder[1..^1];
                if (inner.StartsWith(EnvConfiguration.PoolUrlPrefix, StringComparison.Ordinal))
                {
                    var id = inner[EnvConfiguration.PoolUrlPrefix.Length..];
                    if (Repository(id) is null)
                    {
                        errors.Add($"The per-slot value {variable.Name} of {name} uses {placeholder}, but {id} is not in the catalog.");
                    }

                    continue;
                }

                errors.Add($"The per-slot value {variable.Name} of {name} uses the unknown placeholder {placeholder}.");
            }

            if (env.Manual.Any(m => string.Equals(m.Name, variable.Name, StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add($"Variable {variable.Name} of {name} is both manual and per slot: keep it in only one list.");
            }
        }
    }

    private static void ValidateSeed(RepositoryConfiguration repository, List<string> errors)
    {
        if (repository.Seed is not { } seed)
        {
            return;
        }

        var name = repository.Id;
        if (repository.Type != RepositoryType.Backend)
        {
            errors.Add($"The database seed belongs only to the backend, not to {name}.");
        }

        if (seed.Path.Length > 0
            && (!Path.IsPathFullyQualified(seed.Path) || !SeedConfiguration.Extensions.Contains(Path.GetExtension(seed.Path), StringComparer.OrdinalIgnoreCase)))
        {
            errors.Add($"The seed of {name} must be a full path to a {string.Join(" or ", SeedConfiguration.Extensions)} file ({seed.Path}).");
        }

        var sql = seed.SqlServer;
        if (string.IsNullOrWhiteSpace(sql.Image) || string.IsNullOrWhiteSpace(sql.DatabaseName) || string.IsNullOrWhiteSpace(sql.Password) || string.IsNullOrWhiteSpace(sql.Service))
        {
            errors.Add($"The seed of {name}: SQL Server image, database name, password and service are all required.");
        }

        if (sql.DatabaseName.IndexOfAny([']', '[', '\'', ';']) >= 0)
        {
            errors.Add($"The database name of the seed of {name} contains characters that are not allowed.");
        }

        if (sql.MemoryLimitMb < 512)
        {
            errors.Add($"The SQL Server memory of the seed of {name} must be at least 512 MB.");
        }
    }

    private void ValidatePortRanges(List<string> errors)
    {
        var ranges = new List<(string Owner, int Base)> { ("database", Docker.DatabasePortBase) };
        foreach (var repository in Repositories.Where(r => r.Docker is not null || DockerEnvironments.Any(e => ContainerizedOf(e).Any(c => ReferenceEquals(c.Repository, r)))))
        {
            foreach (var text in repository.DockerPublishedPorts)
            {
                if (PortMapping.TryParse(text, out var mapping))
                {
                    ranges.Add(($"{repository.Id} {text}", mapping.Base));
                }
            }
        }

        for (var i = 0; i < ranges.Count; i++)
        {
            for (var j = i + 1; j < ranges.Count; j++)
            {
                if (Math.Abs(ranges[i].Base - ranges[j].Base) <= MaxEnvironmentNumber)
                {
                    errors.Add($"The ports of {ranges[i].Owner} and {ranges[j].Owner} overlap: each base covers base+1..base+{MaxEnvironmentNumber}.");
                }
            }
        }
    }

    private static bool IsRelativeInside(string path)
        => !Path.IsPathRooted(path)
            && path.IndexOfAny(Path.GetInvalidPathChars()) < 0
            && !path.Split('/', '\\').Contains("..");

    private static bool SamePath(string a, string b)
    {
        static string Normalize(string path)
        {
            var normalized = path.Replace('/', '\\');
            return normalized.StartsWith(".\\", StringComparison.Ordinal) ? normalized[2..] : normalized;
        }

        return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
    }

    private int EnvironmentsIncluding(RepositoryConfiguration repository)
        => Environments.Count(e => e.Repositories.Contains(repository.Id, StringComparer.OrdinalIgnoreCase));

    [System.Text.RegularExpressions.GeneratedRegex(@"^(?=.{1,24}$)[a-z][a-z0-9]*(-[a-z0-9]+)*$")]
    private static partial System.Text.RegularExpressions.Regex IdPattern();
}

/// <summary>
/// A catalog entry: in the file it is a flat object with "type" and the fields of its type
/// (see <see cref="RepositoryConfigurationConverter"/>).
/// </summary>
public abstract class RepositoryConfiguration
{
    /// <summary>The type: written to the file; when reading, the converter uses it to choose the class.</summary>
    [JsonPropertyOrder(-10)]
    public abstract RepositoryType Type { get; }

    /// <summary>Stable identifier in the catalog: used by environments and by derived console and tunnel names.</summary>
    [JsonPropertyOrder(-9)]
    public string Id { get; set; } = string.Empty;

    /// <summary>URL to clone missing folders from; empty = no cloning.</summary>
    [JsonPropertyOrder(-8)]
    public string CloneUrl { get; set; } = string.Empty;

    /// <summary>Name of the folder under the root, with {n} in place of the number.</summary>
    [JsonPropertyOrder(-7)]
    public string Folder { get; set; } = string.Empty;

    /// <summary>The repository's dev tunnel; null = no tunnel.</summary>
    [JsonPropertyOrder(-6)]
    public DevTunnelConfiguration? DevTunnel { get; set; }

    /// <summary>Bare source clone the Docker environments' worktrees are created from.</summary>
    [JsonPropertyOrder(-5)]
    public string? SourceClone { get; set; }

    /// <summary>The repository's service in Docker environments; null = the type's defaults.</summary>
    [JsonPropertyOrder(50)]
    public DockerServiceConfiguration? Docker { get; set; }

    /// <summary>The repository's variables in Docker environments; null = no managed env.</summary>
    [JsonPropertyOrder(51)]
    public EnvConfiguration? Env { get; set; }

    /// <summary>The database seed (backend only); null = no seed.</summary>
    [JsonPropertyOrder(52)]
    public SeedConfiguration? Seed { get; set; }

    /// <summary>The slot's compose file, relative to the root: the written value or the type's default.</summary>
    [JsonIgnore]
    public string DockerCompose => Docker?.Compose is { Length: > 0 } compose ? compose : DockerDefaults.Compose(Type);

    /// <summary>The Dockerfile, relative to the root and to the context: the written value or the type's default.</summary>
    [JsonIgnore]
    public string DockerDockerfile => Docker?.Dockerfile is { Length: > 0 } dockerfile ? dockerfile : DockerDefaults.Dockerfile(Type);

    [JsonIgnore]
    public string DockerService => Docker?.Service is { Length: > 0 } service ? service : DockerDefaults.Service(Type);

    [JsonIgnore]
    public IReadOnlyList<string> DockerPublishedPorts => Docker?.PublishedPorts is { Count: > 0 } ports ? ports : DockerDefaults.PublishedPorts(Type);

    /// <summary>The repository name on the server (last segment of the URL), to recognize its pull requests.</summary>
    [JsonIgnore]
    public string RepositoryName
    {
        get
        {
            var url = CloneUrl.Trim().TrimEnd('/');
            var name = url[(url.LastIndexOfAny(['/', '\\', ':']) + 1)..];
            return name.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        }
    }
}

public sealed class BackendConfiguration : RepositoryConfiguration
{
    [JsonPropertyOrder(-10)]
    public override RepositoryType Type => RepositoryType.Backend;

    /// <summary>The solution the VS button opens, relative to the clone.</summary>
    public string Solution { get; set; } = string.Empty;

    /// <summary>Folder of the project Kestrel starts from (dotnet run), relative to the clone.</summary>
    public string ApiProject { get; set; } = string.Empty;

    /// <summary>Name of the running API process (e.g. MyApp.Web.Api.exe): recognizes Kestrel even when started from Visual Studio.</summary>
    public string ApiProcess { get; set; } = string.Empty;

    public string LaunchProfile { get; set; } = "Kestrel";

    public string ConnectionStringName { get; set; } = "DefaultConnection";

    /// <summary>The appsettings to read the connection string from, in order, relative to the clone.</summary>
    public List<string> AppSettings { get; set; } = [];

    public List<ContextConfiguration> Contexts { get; set; } = [];

}

public sealed class ContextConfiguration
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Migrations folder, relative to the clone.</summary>
    public string Migrations { get; set; } = string.Empty;

    /// <summary>The project that contains the migrations (--project), relative to the clone.</summary>
    public string Project { get; set; } = string.Empty;

    /// <summary>The project with the DbContext configuration (--startup-project), relative to the clone.</summary>
    public string StartupProject { get; set; } = string.Empty;
}

public sealed class FrontendConfiguration : RepositoryConfiguration
{
    [JsonPropertyOrder(-10)]
    public override RepositoryType Type => RepositoryType.Frontend;

    public string StartCommand { get; set; } = "npm run start";

    /// <summary>Command of the accessory button (e.g. regenerating the APIs); empty = no button.</summary>
    public string ExtraCommand { get; set; } = string.Empty;

    /// <summary>Env file to read the dev server port from, relative to the clone.</summary>
    public string EnvFile { get; set; } = string.Empty;

    public string DevServerPortVariable { get; set; } = string.Empty;

    public int DevServerDefaultPort { get; set; } = 8080;

    /// <summary>Text in the command line of the dev server's node process (e.g. webpack): tells the dev server apart from the clone's other node processes.</summary>
    public string DevServerProcessHint { get; set; } = string.Empty;
}

public sealed class AppRepoConfiguration : RepositoryConfiguration
{
    [JsonPropertyOrder(-10)]
    public override RepositoryType Type => RepositoryType.App;

    public string StartCommand { get; set; } = string.Empty;

    /// <summary>Bundler command (e.g. Metro); empty = no button.</summary>
    public string BundlerCommand { get; set; } = string.Empty;
}

/// <summary>A repository with no fields or actions of its own: git card, VS Code and optional tunnel.</summary>
public sealed class GenericRepoConfiguration : RepositoryConfiguration
{
    [JsonPropertyOrder(-10)]
    public override RepositoryType Type => RepositoryType.Generic;
}

/// <summary>
/// The repository can be shared through a tunnel pool port. The description, label and pool keys
/// of the previous model, if still in the file, are ignored and disappear on Save.
/// </summary>
public sealed class DevTunnelConfiguration
{
    /// <summary>The ports to expose; null = the type's ones (Kestrel for the backend, dev server for the frontend).</summary>
    public List<TunnelPortConfiguration>? Ports { get; set; }
}

public sealed class TunnelPortConfiguration
{
    public int Number { get; set; }

    public string Protocol { get; set; } = "http";
}

public sealed class AzureDevOpsConfiguration
{
    /// <summary>URL of the collection or organization, e.g. https://server/tfs/Collection or https://dev.azure.com/org.</summary>
    public string CollectionUrl { get; set; } = string.Empty;

    public string Project { get; set; } = string.Empty;

    public AzureAuthentication Authentication { get; set; } = AzureAuthentication.Windows;

    /// <summary>The PAT in clear text: accepted only in the imported file, never written by the program.</summary>
    public string? Pat { get; set; }

    /// <summary>The PAT encrypted with DPAPI for the current Windows user: readable only by them, on this machine.</summary>
    public string? PatProtected { get; set; }

    [JsonIgnore]
    public bool IsConfigured => !string.IsNullOrWhiteSpace(CollectionUrl);
}

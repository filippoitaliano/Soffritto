using Soffritto.Core.Configuration;
using Soffritto.Presentation;

namespace Soffritto.Desktop;

/// <summary>A catalog repository inside an environment, as the grid sees it.</summary>
public sealed record RepoLocation(EnvironmentRepository Entry)
{
    public RepositoryType Type => Entry.Type;

    public string Path => Entry.Path;

    public string Id => Entry.Id;

    public bool IsPrimary => Entry.IsPrimary;

    /// <summary>The console marker key: the number for the default repository, "N-id" for the others.</summary>
    public string Key => Entry.Key;

    public RepositoryConfiguration Settings => Entry.Repository;

    public string FolderName => StatusText.WorkingFolder(Path);

    /// <summary>The repository is in a Docker environment: its folder is a worktree of the source clone.</summary>
    public bool IsDocker => Entry.Environment.Kind == EnvironmentKind.Docker;

    public bool Exists() => System.IO.Directory.Exists(Path);
}

/// <summary>An environment N: the clones of the repositories it includes, in catalog order.</summary>
/// <param name="SchemaLock">Why the database schema must not be touched (protected database); null if it can be.</param>
public sealed record EnvironmentLayout(int Number, IReadOnlyList<RepoLocation> Repos, EnvironmentKind Kind = EnvironmentKind.Local, string? SchemaLock = null);

/// <summary>
/// The configuration the app runs with. It changes on restart, except for the list of Docker environments
/// and the seed paths, which the Docker actions (create, destroy, Align seed) update while the app runs.
/// </summary>
public static class ActiveConfiguration
{
    public static AppConfiguration Current { get; set; } = new();

    /// <summary>The repository name on the server; without a clone URL, the entry id.</summary>
    public static string RepositoryNameOf(RepositoryConfiguration repository)
        => EnvironmentWork.RepositoryNameOf(repository);

    /// <summary>The server name of the default repository of that type; without one, the type name.</summary>
    public static string RepositoryNameOf(RepositoryType type)
        => Current.Primary(type) is { } repository ? RepositoryNameOf(repository) : AppConfiguration.TypeName(type);

    /// <summary>The type of the first catalog entry with that server name; null if none.</summary>
    public static RepositoryType? TypeOfRepository(string repository)
        => EnvironmentWork.TypeOf(Current, repository);
}

public static class EnvironmentDiscovery
{
    /// <summary>
    /// The configuration's environments, local and Docker, in file order, even with missing folders:
    /// the column is there and says what is missing.
    /// </summary>
    public static IReadOnlyList<EnvironmentLayout> Discover(AppConfiguration configuration) => configuration.Environments
        .Select(environment => LayoutOf(configuration, environment))
        .ToList();

    /// <summary>An environment's column: the repositories it includes, with their folders or worktrees.</summary>
    public static EnvironmentLayout LayoutOf(AppConfiguration configuration, EnvironmentDefinition environment) => new(
        environment.Number,
        configuration.RepositoriesOf(environment).Select(entry => new RepoLocation(entry)).ToList(),
        environment.Kind,
        environment.SchemaLockReason);
}

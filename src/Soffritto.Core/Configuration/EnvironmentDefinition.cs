using System.Text.Json.Serialization;

namespace Soffritto.Core.Configuration;

public enum EnvironmentKind
{
    /// <summary>Clones in local folders, services started in consoles: the original kind of environment.</summary>
    Local,

    /// <summary>Worktrees from the source clones; api, web and database in the containers of the backend's compose file.</summary>
    Docker,
}

/// <summary>An environment N: which catalog repositories it includes, what kind it is and where its clones live.</summary>
public sealed class EnvironmentDefinition
{
    public int Number { get; set; }

    public EnvironmentKind Kind { get; set; } = EnvironmentKind.Local;

    /// <summary>The ids of the included repositories; the order in the column stays the catalog's.</summary>
    public List<string> Repositories { get; set; } = [];

    /// <summary>The folder of this environment's clones; null = the common root, or the worktree folder for a Docker environment.</summary>
    public string? Root { get; set; }

    /// <summary>
    /// Local environments only: the backend's database is protected (also used by
    /// others), so schema and migrations are left alone. Reading, starting and everything else work as usual.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ProtectedDatabase { get; set; }

    /// <summary>Why schema writes are refused, or null if the environment allows them.</summary>
    [JsonIgnore]
    public string? SchemaLockReason
        => ProtectedDatabase && Kind == EnvironmentKind.Local ? ProtectedDatabaseReason(Number) : null;

    public static string ProtectedDatabaseReason(int number)
        => $"environment {number} has a protected database: no migrations are applied and the schema is not altered; for work involving migrations use another environment";
}

/// <summary>A catalog repository inside an environment, with its folder and the names derived from it.</summary>
/// <param name="IsPrimary">The first of its type in the environment (generic ones excluded): it keeps the historical names.</param>
public sealed record EnvironmentRepository(
    RepositoryConfiguration Repository,
    EnvironmentDefinition Environment,
    string Path,
    bool IsPrimary)
{
    public int Number => Environment.Number;

    public RepositoryType Type => Repository.Type;

    public string Id => Repository.Id;

    /// <summary>
    /// The key of the console markers (AMBIENTI kestrel-Key): the number for the primary one, as
    /// before, "N-id" for the others. An id starts with a letter, so it is never mistaken for
    /// a historical key.
    /// </summary>
    public string Key => IsPrimary ? Number.ToString() : $"{Number}-{Repository.Id}";

    /// <summary>The repository can be shared through a tunnel pool port.</summary>
    public bool HasTunnel => Repository.DevTunnel is not null;
}

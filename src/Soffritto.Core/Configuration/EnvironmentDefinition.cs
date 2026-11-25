using System.Text.Json.Serialization;

namespace Soffritto.Core.Configuration;

public enum EnvironmentKind
{
    /// <summary>Cloni in cartelle locali, servizi avviati in console: gli ambienti di oggi.</summary>
    Local,

    /// <summary>Worktree dai cloni sorgente; api, web e database nei container del compose del backend.</summary>
    Docker,
}

/// <summary>Un ambiente N: quali repository del catalogo include, di che tipo e' e dove stanno i suoi cloni.</summary>
public sealed class EnvironmentDefinition
{
    public int Number { get; set; }

    public EnvironmentKind Kind { get; set; } = EnvironmentKind.Local;

    /// <summary>Gli id dei repository inclusi; l'ordine nella colonna resta quello del catalogo.</summary>
    public List<string> Repositories { get; set; } = [];

    /// <summary>La cartella dei cloni di questo ambiente; null = la radice comune, o la cartella dei worktree per un ambiente Docker.</summary>
    public string? Root { get; set; }

    /// <summary>
    /// Solo per un ambiente locale: il database del backend e' protetto (usato anche da
    /// altri), quindi schema e migrazioni non si toccano. Lettura, avvio e tutto il resto restano come sempre.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ProtectedDatabase { get; set; }

    /// <summary>Il motivo per cui le scritture di schema sono rifiutate, o null se l'ambiente le permette.</summary>
    [JsonIgnore]
    public string? SchemaLockReason
        => ProtectedDatabase && Kind == EnvironmentKind.Local ? ProtectedDatabaseReason(Number) : null;

    public static string ProtectedDatabaseReason(int number)
        => $"l'ambiente {number} ha il database protetto: non si applicano migrazioni e non si altera lo schema; per un lavoro con migrazioni usa un altro ambiente";
}

/// <summary>Un repository del catalogo dentro un ambiente, con la sua cartella e i nomi che ne derivano.</summary>
/// <param name="IsPrimary">Il primo del suo tipo nell'ambiente (generici esclusi): tiene i nomi storici.</param>
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
    /// La chiave dei marcatori console (AMBIENTI kestrel-Key): il numero per il predefinito, come
    /// oggi, "N-id" per gli altri. Un id comincia con una lettera, quindi non si confonde mai con
    /// una chiave storica.
    /// </summary>
    public string Key => IsPrimary ? Number.ToString() : $"{Number}-{Repository.Id}";

    /// <summary>Il repository si puo' condividere con una porta del pool di tunnel.</summary>
    public bool HasTunnel => Repository.DevTunnel is not null;
}

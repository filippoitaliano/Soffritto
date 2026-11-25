using Soffritto.Core.Configuration;
using Soffritto.Presentation;

namespace Soffritto.Desktop;

/// <summary>Un repository del catalogo dentro un ambiente, come lo vede la griglia.</summary>
public sealed record RepoLocation(EnvironmentRepository Entry)
{
    public RepositoryType Type => Entry.Type;

    public string Path => Entry.Path;

    public string Id => Entry.Id;

    public bool IsPrimary => Entry.IsPrimary;

    /// <summary>La chiave dei marcatori console: il numero per il predefinito, "N-id" per gli altri.</summary>
    public string Key => Entry.Key;

    public RepositoryConfiguration Settings => Entry.Repository;

    public string FolderName => StatusText.WorkingFolder(Path);

    /// <summary>Il repository sta in un ambiente Docker: la sua cartella e' un worktree del clone sorgente.</summary>
    public bool IsDocker => Entry.Environment.Kind == EnvironmentKind.Docker;

    public bool Exists() => System.IO.Directory.Exists(Path);
}

/// <summary>Un ambiente N: i cloni dei repository che include, in ordine di catalogo.</summary>
/// <param name="SchemaLock">Il motivo per cui lo schema del database non si tocca (database protetto); null se si puo'.</param>
public sealed record EnvironmentLayout(int Number, IReadOnlyList<RepoLocation> Repos, EnvironmentKind Kind = EnvironmentKind.Local, string? SchemaLock = null);

/// <summary>
/// La configurazione con cui gira l'exe. Cambia riavviando, tranne la lista degli ambienti Docker
/// e i percorsi dei seed, che le azioni Docker (creazione, distruzione, Allinea seed) aggiornano mentre l'exe gira.
/// </summary>
public static class ActiveConfiguration
{
    public static AppConfiguration Current { get; set; } = new();

    /// <summary>Il nome del repository sul server; senza URL di clone, l'id della voce.</summary>
    public static string RepositoryNameOf(RepositoryConfiguration repository)
        => repository.RepositoryName is { Length: > 0 } name ? name : repository.Id;

    /// <summary>Il nome sul server del predefinito di quel tipo; senza, il nome del tipo.</summary>
    public static string RepositoryNameOf(RepositoryType type)
        => Current.Primary(type) is { } repository ? RepositoryNameOf(repository) : AppConfiguration.TypeName(type);

    /// <summary>Il tipo della prima voce del catalogo con quel nome sul server; null se nessuna.</summary>
    public static RepositoryType? TypeOfRepository(string repository)
        => Current.Repositories
            .Where(r => string.Equals(RepositoryNameOf(r), repository, StringComparison.OrdinalIgnoreCase))
            .Select(r => (RepositoryType?)r.Type)
            .FirstOrDefault();
}

public static class EnvironmentDiscovery
{
    /// <summary>
    /// Gli ambienti della configurazione, locali e Docker, nell'ordine del file, anche con cartelle
    /// mancanti: la colonna c'e' e dice cosa manca.
    /// </summary>
    public static IReadOnlyList<EnvironmentLayout> Discover(AppConfiguration configuration) => configuration.Environments
        .Select(environment => LayoutOf(configuration, environment))
        .ToList();

    /// <summary>La colonna di un ambiente: i repository che include, con le loro cartelle o worktree.</summary>
    public static EnvironmentLayout LayoutOf(AppConfiguration configuration, EnvironmentDefinition environment) => new(
        environment.Number,
        configuration.RepositoriesOf(environment).Select(entry => new RepoLocation(entry)).ToList(),
        environment.Kind,
        environment.SchemaLockReason);
}

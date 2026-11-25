namespace Soffritto.Core.Configuration;

/// <summary>Le impostazioni comuni degli ambienti Docker: dove nascono i worktree, le porte dei database, l'ambiente modello.</summary>
public sealed class DockerConfiguration
{
    /// <summary>La cartella dei worktree e dei cloni sorgente; vuota = &lt;root&gt;\ambienti-docker.</summary>
    public string WorktreeRoot { get; set; } = string.Empty;

    /// <summary>Base delle porte host dei database: lo slot N usa base + N, il container del seed la base.</summary>
    public int DatabasePortBase { get; set; } = DockerDefaults.DatabasePortBase;

    /// <summary>L'ambiente locale da cui si leggono costanti, segreti e file locali; null = il primo locale.</summary>
    public int? TemplateEnvironment { get; set; }
}

/// <summary>Un servizio di un repository in un ambiente Docker; i campi vuoti valgono i default del tipo (<see cref="DockerDefaults"/>).</summary>
public sealed class DockerServiceConfiguration
{
    /// <summary>Porte "base:porta del container": lo slot N pubblica base + N.</summary>
    public List<string> PublishedPorts { get; set; } = [];

    /// <summary>Il compose dello slot, relativo alla radice del repository (solo backend).</summary>
    public string Compose { get; set; } = string.Empty;

    /// <summary>Il Dockerfile, relativo alla radice del repository: e' anche il percorso relativo al contesto.</summary>
    public string Dockerfile { get; set; } = string.Empty;

    /// <summary>Cartella assoluta di un checkout con i file Docker, copiati nel worktree quando il branch non li ha.</summary>
    public string FilesFallback { get; set; } = string.Empty;

    /// <summary>Il nome del servizio nel compose.</summary>
    public string Service { get; set; } = string.Empty;

    /// <summary>File copiati dall'ambiente modello al worktree, relativi alla radice del repository.</summary>
    public List<string> LocalFiles { get; set; } = [];
}

/// <summary>Una porta pubblicata: lo slot N la espone sull'host a <see cref="Base"/> + N.</summary>
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

/// <summary>Una porta host di un ambiente Docker, con il servizio del compose che la pubblica.</summary>
public sealed record SlotPort(string Service, int Host, int Container);

/// <summary>I valori proposti per gli ambienti Docker: li usano i campi vuoti della configurazione e i "Valori proposti" del setup.</summary>
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

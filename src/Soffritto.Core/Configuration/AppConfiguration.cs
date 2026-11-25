using System.Text.Json.Serialization;

namespace Soffritto.Core.Configuration;

/// <summary>Il tipo di un repository del catalogo: decide campi, card e strumenti.</summary>
public enum RepositoryType
{
    Backend,
    Frontend,
    App,
    Generic,
}

public enum AzureAuthentication
{
    /// <summary>L'utente Windows che esegue l'exe, senza segreti da custodire.</summary>
    Windows,

    /// <summary>Personal access token, come il server MCP Azure DevOps.</summary>
    Pat,
}

/// <summary>
/// La configurazione dell'intero prodotto: il catalogo dei repository, gli ambienti che li
/// includono, dove stanno i cloni e Azure DevOps. Exe, VSIX, CLI e MCP leggono lo stesso file
/// (vedi <see cref="ConfigurationFiles"/>).
/// </summary>
public sealed partial class AppConfiguration
{
    public const int CurrentSchemaVersion = 2;

    /// <summary>Segnaposto del numero dell'ambiente nel nome delle cartelle dei cloni e nelle Description dei tunnel.</summary>
    public const string NumberPlaceholder = "{n}";

    public const int MaxEnvironmentNumber = 99;

    [JsonPropertyOrder(-2)]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>La cartella che contiene i cloni di tutti gli ambienti.</summary>
    [JsonPropertyOrder(-1)]
    public string Root { get; set; } = string.Empty;

    /// <summary>Il catalogo dei repository, in ordine: l'ordine e' quello delle card nelle colonne.</summary>
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

    /// <summary>Gli ambienti Docker, per numero crescente.</summary>
    [JsonIgnore]
    public IEnumerable<EnvironmentDefinition> DockerEnvironments
        => Environments.Where(e => e.Kind == EnvironmentKind.Docker).OrderBy(e => e.Number);

    /// <summary>La cartella dei worktree e dei cloni sorgente degli ambienti Docker.</summary>
    [JsonIgnore]
    public string DockerWorktreeRoot
        => Docker.WorktreeRoot is { Length: > 0 } folder ? folder : DockerDefaults.WorktreeRoot(Root);

    /// <summary>Il backend predefinito, se ha un seed configurato.</summary>
    public BackendConfiguration? PrimarySeed()
        => Primary(RepositoryType.Backend) is BackendConfiguration { Seed: not null } backend ? backend : null;

    /// <summary>L'ambiente modello: quello scelto, o il primo locale; null senza ambienti locali.</summary>
    public int? TemplateNumber()
        => Docker.TemplateEnvironment ?? LocalEnvironments.Select(e => (int?)e.Number).FirstOrDefault();

    /// <summary>I repository di un ambiente Docker che girano in un container: il backend e il frontend predefiniti.</summary>
    public IReadOnlyList<EnvironmentRepository> ContainerizedOf(EnvironmentDefinition environment)
        => environment.Kind == EnvironmentKind.Docker
            ? RepositoriesOf(environment).Where(r => r.IsPrimary && r.Type is RepositoryType.Backend or RepositoryType.Frontend).ToList()
            : [];

    /// <summary>Le porte host di un ambiente Docker: il database e quelle pubblicate dai repository nei container.</summary>
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

    /// <summary>Il primo backend del catalogo: quello su cui lavorano VSIX e CLI.</summary>
    [JsonIgnore]
    public BackendConfiguration Backend => Primary(RepositoryType.Backend) as BackendConfiguration ?? new BackendConfiguration();

    /// <summary>Gli ambienti locali, cioe' quelli che la griglia mostra, per numero crescente.</summary>
    [JsonIgnore]
    public IEnumerable<EnvironmentDefinition> LocalEnvironments
        => Environments.Where(e => e.Kind == EnvironmentKind.Local).OrderBy(e => e.Number);

    /// <summary>I numeri degli ambienti locali, crescenti.</summary>
    [JsonIgnore]
    public IEnumerable<int> Numbers => LocalEnvironments.Select(e => e.Number);

    public EnvironmentDefinition? Environment(int number) => Environments.FirstOrDefault(e => e.Number == number);

    public RepositoryConfiguration? Repository(string id)
        => Repositories.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Il primo repository del catalogo di quel tipo; i generici non hanno un predefinito.</summary>
    public RepositoryConfiguration? Primary(RepositoryType type)
        => type == RepositoryType.Generic ? null : Repositories.FirstOrDefault(r => r.Type == type);

    /// <summary>
    /// I repository inclusi nell'ambiente, in ordine di catalogo. Il primo di ogni tipo (generici
    /// esclusi) e' il predefinito: tiene i nomi storici di console e tunnel.
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

    /// <summary>Il repository predefinito di quel tipo nell'ambiente N; null se l'ambiente non ne include uno.</summary>
    public EnvironmentRepository? PrimaryOf(RepositoryType type, int number)
        => Environment(number) is { } environment
            ? RepositoriesOf(environment).FirstOrDefault(r => r.IsPrimary && r.Type == type)
            : null;

    public string FolderOf(RepositoryConfiguration repository, EnvironmentDefinition environment)
        => Path.Combine(
            environment.Root ?? (environment.Kind == EnvironmentKind.Docker ? DockerWorktreeRoot : Root),
            Resolve(repository.Folder, environment.Number));

    /// <summary>Sostituisce {n} con il numero dell'ambiente.</summary>
    public static string Resolve(string template, int number) => Resolve(template, number.ToString());

    /// <summary>Sostituisce {n} con un valore qualunque (es. "seed" per il worktree del seed).</summary>
    public static string Resolve(string template, string value)
        => template.Replace(NumberPlaceholder, value, StringComparison.OrdinalIgnoreCase);

    /// <summary>L'ambiente locale il cui backend predefinito sta in quella cartella, se c'e'.</summary>
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
    /// L'ambiente, locale o Docker, il cui backend predefinito sta in quella cartella, con quel
    /// backend; null se nessuno. A differenza di <see cref="EnvironmentOfBackend"/> copre anche
    /// i worktree degli ambienti Docker.
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

    /// <summary>Dalla cartella di un backend predefinito, il motivo per cui le scritture di schema sono rifiutate (null: permesse).</summary>
    public string? SchemaLockOfBackend(string backendPath)
        => EnvironmentOfBackend(backendPath) is { } number ? Environment(number)?.SchemaLockReason : null;

    public ProbeOptions ToProbeOptions() => ToProbeOptions(Backend);

    public static ProbeOptions ToProbeOptions(BackendConfiguration backend) => new(
        backend.Contexts
            .Select(c => new ContextDefinition(c.Name, c.Migrations, c.Project, c.StartupProject))
            .ToList(),
        backend.AppSettings.ToList(),
        backend.ConnectionStringName);

    /// <summary>La configurazione del primo avvio: backend, frontend e app vuoti, un ambiente.</summary>
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

    /// <summary>Gli errori che impediscono di usare la configurazione, in parole per l'utente.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(Root))
        {
            errors.Add("Manca la cartella dei cloni.");
        }
        else if (!Path.IsPathFullyQualified(Root))
        {
            errors.Add("La cartella dei cloni deve essere un percorso completo (es. C:\\Dev).");
        }

        ValidateRepositories(errors);
        ValidateEnvironments(errors);
        ValidateTunnels(errors);
        ValidateDocker(errors);

        if (AzureDevOps.IsConfigured)
        {
            if (!Uri.TryCreate(AzureDevOps.CollectionUrl, UriKind.Absolute, out _))
            {
                errors.Add("L'URL della collection Azure DevOps non e' un indirizzo valido.");
            }

            if (string.IsNullOrWhiteSpace(AzureDevOps.Project))
            {
                errors.Add("Manca il progetto Azure DevOps.");
            }
        }

        return errors;
    }

    /// <summary>
    /// Le configurazioni lecite ma da conoscere, in parole per l'utente: non impediscono di salvare.
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
                warnings.Add($"Ambiente {environment.Number}: due repository di tipo {TypeName(group.Key)} ({string.Join(", ", group.Select(r => r.Id))}): il predefinito e' {primary.Id}, gli altri usano nomi derivati ({ServiceOf(group.Key)}-{other.Key}...).");
            }

            if (environment.Kind == EnvironmentKind.Docker
                && included.FirstOrDefault(r => r.IsPrimary && r.Type == RepositoryType.Backend) is { } dockerBackend
                && !(dockerBackend.Repository.Env?.ContainerExclude ?? []).Any(p => p.Length > 0 && "Kestrel:".StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            {
                warnings.Add($"Ambiente {environment.Number}: nessun prefisso di containerExclude di {dockerBackend.Id} copre Kestrel:, quindi gli endpoint Kestrel dell'ambiente modello arrivano al container e vincono sulla porta 8080 del Dockerfile.");
            }

            foreach (var primary in included.Where(r => r.IsPrimary))
            {
                if (Primary(primary.Type) is { } first && !ReferenceEquals(first, primary.Repository)
                    && !included.Any(r => ReferenceEquals(r.Repository, first)))
                {
                    warnings.Add($"Ambiente {environment.Number} usa come {TypeName(primary.Type)} {primary.Id}, non {first.Id} (il primo del catalogo): VSIX e CLI su questo ambiente usano le impostazioni di {first.Id}.");
                }
            }
        }

        if (Repositories.Count(r => r.Type == RepositoryType.Backend) > 1)
        {
            warnings.Add("Con piu' backend la VSIX va aperta solo sul backend predefinito di un ambiente: su un altro backend le sue console prenderebbero il marcatore kestrel-N del predefinito.");
        }

        return warnings;
    }

    public static string TypeName(RepositoryType type) => type switch
    {
        RepositoryType.Backend => "backend",
        RepositoryType.Frontend => "frontend",
        RepositoryType.App => "app",
        _ => "generico",
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
            errors.Add("Il catalogo non ha repository: aggiungine almeno uno.");
        }

        foreach (var repository in Repositories)
        {
            var name = repository.Id.Length > 0 ? repository.Id : "(senza id)";
            if (!IdPattern().IsMatch(repository.Id))
            {
                errors.Add($"L'id \"{repository.Id}\" non e' valido: lettere minuscole, cifre e trattini, inizia con una lettera, al massimo 24 caratteri.");
            }

            var folder = repository.Folder;
            if (string.IsNullOrWhiteSpace(folder))
            {
                errors.Add($"Manca la cartella del repository {name}.");
            }
            else if (folder.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                errors.Add($"La cartella del repository {name} contiene caratteri non validi.");
            }
            else if (EnvironmentsIncluding(repository) > 1 && !folder.Contains(NumberPlaceholder, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"La cartella del repository {name} deve contenere {NumberPlaceholder}: e' in piu' ambienti, e le cartelle sarebbero la stessa.");
            }

            if (repository is BackendConfiguration backend)
            {
                foreach (var context in backend.Contexts)
                {
                    if (string.IsNullOrWhiteSpace(context.Name) || string.IsNullOrWhiteSpace(context.Migrations)
                        || string.IsNullOrWhiteSpace(context.Project) || string.IsNullOrWhiteSpace(context.StartupProject))
                    {
                        errors.Add($"Il context \"{context.Name}\" di {name} ha campi vuoti: nome, migrazioni, progetto e progetto di avvio servono tutti.");
                    }
                }
            }
        }

        foreach (var duplicate in Repositories.Where(r => r.Id.Length > 0).GroupBy(r => r.Id, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            errors.Add($"L'id \"{duplicate.Key}\" compare piu' volte nel catalogo.");
        }
    }

    private void ValidateEnvironments(List<string> errors)
    {
        if (Environments.Count == 0)
        {
            errors.Add("Manca almeno un ambiente.");
        }

        foreach (var environment in Environments)
        {
            if (environment.Number is < 1 or > MaxEnvironmentNumber)
            {
                errors.Add($"L'ambiente {environment.Number} e' fuori intervallo: i numeri vanno da 1 a {MaxEnvironmentNumber}.");
            }

            if (environment.Repositories.Count == 0)
            {
                errors.Add($"L'ambiente {environment.Number} non include nessun repository.");
            }

            foreach (var id in environment.Repositories.Where(id => Repository(id) is null))
            {
                errors.Add($"L'ambiente {environment.Number} include \"{id}\", che non e' nel catalogo.");
            }

            if (environment.Root is { } root && !Path.IsPathFullyQualified(root))
            {
                errors.Add($"La cartella dell'ambiente {environment.Number} deve essere un percorso completo.");
            }

            if (environment.ProtectedDatabase && environment.Kind == EnvironmentKind.Docker)
            {
                errors.Add($"L'ambiente {environment.Number} e' Docker: ha sempre il suo database, non puo' essere protetto.");
            }
        }

        foreach (var duplicate in Environments.GroupBy(e => e.Number).Where(g => g.Count() > 1))
        {
            errors.Add($"Il numero di ambiente {duplicate.Key} compare piu' volte.");
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
                errors.Add($"Due repository finirebbero nella stessa cartella: {string.Join(", ", folders)}.");
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
                    errors.Add($"Il dev tunnel di {name} ha una porta non valida ({port.Number}/{port.Protocol}): numero tra 1 e 65535, protocollo http o https.");
                }
            }

            if (repository.Type == RepositoryType.Generic && (tunnel.Ports is null || tunnel.Ports.Count == 0))
            {
                errors.Add($"Il dev tunnel del repository generico {name} non ha porte: per i generici vanno scritte a mano.");
            }
        }
    }

    private void ValidateDocker(List<string> errors)
    {
        if (Docker.WorktreeRoot.Length > 0 && !Path.IsPathFullyQualified(Docker.WorktreeRoot))
        {
            errors.Add("La cartella dei worktree Docker deve essere un percorso completo.");
        }

        if (Docker.DatabasePortBase is < 1 or > 65535 - MaxEnvironmentNumber)
        {
            errors.Add($"La base delle porte dei database ({Docker.DatabasePortBase}) e' fuori intervallo: tra 1 e {65535 - MaxEnvironmentNumber}.");
        }

        if (Docker.TemplateEnvironment is { } template && Environment(template) is not { Kind: EnvironmentKind.Local })
        {
            errors.Add($"L'ambiente modello {template} non e' un ambiente locale.");
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
                errors.Add($"Il tunnel {i + 1} del pool ha una Description vuota o con spazi.");
            }

            if (string.IsNullOrWhiteSpace(tunnel.Label) || tunnel.Label.Any(char.IsWhiteSpace) || tunnel.Label.Contains(','))
            {
                errors.Add($"Il tunnel {i + 1} del pool ha una Label vuota, con spazi o con virgole.");
            }
        }

        foreach (var duplicate in TunnelPool.Tunnels.Where(t => t.Description.Length > 0).GroupBy(t => t.Description, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            errors.Add($"La Description {duplicate.Key} compare piu' volte nel pool di tunnel.");
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
            errors.Add($"L'ambiente Docker {number} non include un backend: il compose dello slot sta nel backend.");
        }
        else
        {
            if (backend.DockerCompose.Length == 0 || backend.DockerDockerfile.Length == 0)
            {
                errors.Add($"L'ambiente Docker {number}: il backend {backend.Id} non ha compose o Dockerfile.");
            }

            var connection = $"ConnectionStrings:{backend.ConnectionStringName}";
            if (backend.Env?.Slot.FirstOrDefault(v => string.Equals(v.Name, connection, StringComparison.OrdinalIgnoreCase)) is not { } variable
                || string.IsNullOrWhiteSpace(variable.Local) || string.IsNullOrWhiteSpace(variable.Container))
            {
                errors.Add($"L'ambiente Docker {number}: la variabile per slot {connection} del backend {backend.Id} manca o ha un valore vuoto (servono locale e container: senza, il database dello slot non e' garantito).");
            }

            if (PrimarySeed() is null)
            {
                errors.Add($"L'ambiente Docker {number}: il backend {backend.Id} non ha un seed del database.");
            }
        }

        foreach (var frontend in included.Where(r => r.IsPrimary && r.Type == RepositoryType.Frontend && r.Repository.DockerDockerfile.Length == 0))
        {
            errors.Add($"L'ambiente Docker {number}: il frontend {frontend.Id} non ha un Dockerfile.");
        }

        foreach (var repository in included)
        {
            if (!repository.Repository.Folder.Contains(NumberPlaceholder, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"L'ambiente Docker {number}: la cartella di {repository.Id} deve contenere {NumberPlaceholder}.");
            }

            if (string.IsNullOrWhiteSpace(repository.Repository.SourceClone))
            {
                errors.Add($"L'ambiente Docker {number}: {repository.Id} non ha un clone sorgente.");
            }
        }

        if (TemplateNumber() is null)
        {
            errors.Add("Gli ambienti Docker leggono costanti e segreti da un ambiente modello locale: non ce n'e' nessuno.");
        }
    }

    private static void ValidateDockerService(RepositoryConfiguration repository, List<string> errors)
    {
        var name = repository.Id;
        if (repository.SourceClone is { Length: > 0 } source && !Path.IsPathFullyQualified(source))
        {
            errors.Add($"Il clone sorgente di {name} deve essere un percorso completo.");
        }

        if (repository.Docker is not { } docker)
        {
            return;
        }

        foreach (var (field, value) in new[] { ("compose", docker.Compose), ("Dockerfile", docker.Dockerfile) })
        {
            if (value.Length > 0 && !IsRelativeInside(value))
            {
                errors.Add($"Il {field} di {name} deve essere un percorso relativo alla radice del repository, senza '..' ne' disco ({value}).");
            }
        }

        if (docker.FilesFallback.Length > 0 && !Path.IsPathFullyQualified(docker.FilesFallback))
        {
            errors.Add($"La cartella dei file Docker di riserva di {name} deve essere un percorso completo.");
        }

        foreach (var port in docker.PublishedPorts.Where(p => !PortMapping.TryParse(p, out _)))
        {
            errors.Add($"La porta pubblicata \"{port}\" di {name} non e' valida: serve base:porta del container (es. 15000:8080).");
        }

        foreach (var file in docker.LocalFiles)
        {
            if (!IsRelativeInside(file))
            {
                errors.Add($"Il file locale \"{file}\" di {name} deve essere un percorso relativo alla radice del repository.");
            }
            else if (repository.Env is { } env
                && (SamePath(file, env.Target) || (env.Secrets is { } secrets && SamePath(Path.GetFileName(file), Path.GetFileName(secrets.Source)))))
            {
                errors.Add($"Il file locale \"{file}\" di {name} lo scrive l'adapter degli env (target o fonte dei segreti): toglilo dai file locali.");
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
                errors.Add($"Gli env di {name} hanno un adapter ma nessun file target.");
            }
            else if (!IsRelativeInside(env.Target))
            {
                errors.Add($"Il target degli env di {name} deve essere un percorso relativo alla radice del repository.");
            }
        }

        if (env.ConstantsSource.Length > 0 && !IsRelativeInside(env.ConstantsSource))
        {
            errors.Add($"La fonte delle costanti di {name} deve essere un percorso relativo alla radice del repository.");
        }

        if (env.Secrets is { } secrets)
        {
            if (secrets.Adapter == EnvAdapterKind.Manual)
            {
                errors.Add($"La fonte dei segreti di {name} non puo' essere manuale: i segreti non si scrivono nel setup.");
            }

            if (string.IsNullOrWhiteSpace(secrets.Source))
            {
                errors.Add($"La fonte dei segreti di {name} non ha un file.");
            }
        }

        foreach (var variable in env.Manual.Where(m => string.IsNullOrWhiteSpace(m.Name)))
        {
            errors.Add($"Una variabile manuale di {name} non ha nome.");
        }

        foreach (var variable in env.Slot)
        {
            if (string.IsNullOrWhiteSpace(variable.Name))
            {
                errors.Add($"Un valore per slot di {name} non ha nome.");
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
                        errors.Add($"Il valore per slot {variable.Name} di {name} usa {placeholder}, ma {id} non e' nel catalogo.");
                    }

                    continue;
                }

                errors.Add($"Il valore per slot {variable.Name} di {name} usa il segnaposto sconosciuto {placeholder}.");
            }

            if (env.Manual.Any(m => string.Equals(m.Name, variable.Name, StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add($"La variabile {variable.Name} di {name} e' sia manuale sia per slot: tienila in una sola lista.");
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
            errors.Add($"Il seed del database sta solo sul backend, non su {name}.");
        }

        if (seed.Path.Length > 0
            && (!Path.IsPathFullyQualified(seed.Path) || !SeedConfiguration.Extensions.Contains(Path.GetExtension(seed.Path), StringComparer.OrdinalIgnoreCase)))
        {
            errors.Add($"Il seed di {name} deve essere un percorso completo a un file {string.Join(" o ", SeedConfiguration.Extensions)} ({seed.Path}).");
        }

        var sql = seed.SqlServer;
        if (string.IsNullOrWhiteSpace(sql.Image) || string.IsNullOrWhiteSpace(sql.DatabaseName) || string.IsNullOrWhiteSpace(sql.Password) || string.IsNullOrWhiteSpace(sql.Service))
        {
            errors.Add($"Il seed di {name}: immagine, nome del database, password e servizio di SQL Server servono tutti.");
        }

        if (sql.DatabaseName.IndexOfAny([']', '[', '\'', ';']) >= 0)
        {
            errors.Add($"Il nome del database del seed di {name} contiene caratteri non ammessi.");
        }

        if (sql.MemoryLimitMb < 512)
        {
            errors.Add($"La memoria di SQL Server del seed di {name} deve essere almeno 512 MB.");
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
                    errors.Add($"Le porte di {ranges[i].Owner} e di {ranges[j].Owner} si sovrappongono: ogni base copre base+1..base+{MaxEnvironmentNumber}.");
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
/// Una voce del catalogo: nel file e' un oggetto piatto con "type" e i campi del suo tipo
/// (vedi <see cref="RepositoryConfigurationConverter"/>).
/// </summary>
public abstract class RepositoryConfiguration
{
    /// <summary>Il tipo: scritto nel file, in lettura lo usa il convertitore per scegliere la classe.</summary>
    [JsonPropertyOrder(-10)]
    public abstract RepositoryType Type { get; }

    /// <summary>Identificativo stabile nel catalogo: lo usano gli ambienti e i nomi derivati di console e tunnel.</summary>
    [JsonPropertyOrder(-9)]
    public string Id { get; set; } = string.Empty;

    /// <summary>URL da cui clonare le cartelle mancanti; vuoto = non si clona.</summary>
    [JsonPropertyOrder(-8)]
    public string CloneUrl { get; set; } = string.Empty;

    /// <summary>Nome della cartella sotto la radice, con {n} al posto del numero.</summary>
    [JsonPropertyOrder(-7)]
    public string Folder { get; set; } = string.Empty;

    /// <summary>Il dev tunnel del repository; null = nessun tunnel.</summary>
    [JsonPropertyOrder(-6)]
    public DevTunnelConfiguration? DevTunnel { get; set; }

    /// <summary>Clone sorgente bare da cui nascono i worktree degli ambienti Docker.</summary>
    [JsonPropertyOrder(-5)]
    public string? SourceClone { get; set; }

    /// <summary>Il servizio del repository negli ambienti Docker; null = i default del tipo.</summary>
    [JsonPropertyOrder(50)]
    public DockerServiceConfiguration? Docker { get; set; }

    /// <summary>Le variabili del repository negli ambienti Docker; null = nessun env gestito.</summary>
    [JsonPropertyOrder(51)]
    public EnvConfiguration? Env { get; set; }

    /// <summary>Il seed del database (solo backend); null = nessun seed.</summary>
    [JsonPropertyOrder(52)]
    public SeedConfiguration? Seed { get; set; }

    /// <summary>Il compose dello slot, relativo alla radice: il valore scritto o il default del tipo.</summary>
    [JsonIgnore]
    public string DockerCompose => Docker?.Compose is { Length: > 0 } compose ? compose : DockerDefaults.Compose(Type);

    /// <summary>Il Dockerfile, relativo alla radice e al contesto: il valore scritto o il default del tipo.</summary>
    [JsonIgnore]
    public string DockerDockerfile => Docker?.Dockerfile is { Length: > 0 } dockerfile ? dockerfile : DockerDefaults.Dockerfile(Type);

    [JsonIgnore]
    public string DockerService => Docker?.Service is { Length: > 0 } service ? service : DockerDefaults.Service(Type);

    [JsonIgnore]
    public IReadOnlyList<string> DockerPublishedPorts => Docker?.PublishedPorts is { Count: > 0 } ports ? ports : DockerDefaults.PublishedPorts(Type);

    /// <summary>Il nome del repository sul server (ultimo segmento dell'URL), per riconoscerne le pull request.</summary>
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

    /// <summary>La soluzione che apre il bottone VS, relativa al clone.</summary>
    public string Solution { get; set; } = string.Empty;

    /// <summary>Cartella del progetto da cui parte Kestrel (dotnet run), relativa al clone.</summary>
    public string ApiProject { get; set; } = string.Empty;

    /// <summary>Nome del processo dell'API in esecuzione (es. MyApp.Web.Api.exe): riconosce Kestrel anche partito da Visual Studio.</summary>
    public string ApiProcess { get; set; } = string.Empty;

    public string LaunchProfile { get; set; } = "Kestrel";

    public string ConnectionStringName { get; set; } = "DefaultConnection";

    /// <summary>Gli appsettings da cui leggere la connection string, in ordine, relativi al clone.</summary>
    public List<string> AppSettings { get; set; } = [];

    public List<ContextConfiguration> Contexts { get; set; } = [];

    /// <summary>
    /// I nomi di database e di server il cui tag, sulla card dell'ambiente, e' evidenziato: quelli
    /// da trattare con cautela. Ogni tag si confronta per conto suo, con il nome letto dalla
    /// connection string.
    /// </summary>
    public List<string> HighlightedTags { get; set; } = [];
}

public sealed class ContextConfiguration
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Cartella delle migrazioni, relativa al clone.</summary>
    public string Migrations { get; set; } = string.Empty;

    /// <summary>Il progetto che contiene le migrazioni (--project), relativo al clone.</summary>
    public string Project { get; set; } = string.Empty;

    /// <summary>Il progetto con la configurazione del DbContext (--startup-project), relativo al clone.</summary>
    public string StartupProject { get; set; } = string.Empty;
}

public sealed class FrontendConfiguration : RepositoryConfiguration
{
    [JsonPropertyOrder(-10)]
    public override RepositoryType Type => RepositoryType.Frontend;

    public string StartCommand { get; set; } = "npm run start";

    /// <summary>Comando del bottone accessorio (es. rigenerare le API); vuoto = nessun bottone.</summary>
    public string ExtraCommand { get; set; } = string.Empty;

    /// <summary>File di env da cui leggere la porta del dev server, relativo al clone.</summary>
    public string EnvFile { get; set; } = string.Empty;

    public string DevServerPortVariable { get; set; } = string.Empty;

    public int DevServerDefaultPort { get; set; } = 8080;

    /// <summary>Testo nella riga di comando del node del dev server (es. webpack): distingue il dev server dagli altri node del clone.</summary>
    public string DevServerProcessHint { get; set; } = string.Empty;
}

public sealed class AppRepoConfiguration : RepositoryConfiguration
{
    [JsonPropertyOrder(-10)]
    public override RepositoryType Type => RepositoryType.App;

    public string StartCommand { get; set; } = string.Empty;

    /// <summary>Comando del bundler (es. Metro); vuoto = nessun bottone.</summary>
    public string BundlerCommand { get; set; } = string.Empty;
}

/// <summary>Un repository senza campi ne' azioni propri: card git, VS Code e tunnel opzionale.</summary>
public sealed class GenericRepoConfiguration : RepositoryConfiguration
{
    [JsonPropertyOrder(-10)]
    public override RepositoryType Type => RepositoryType.Generic;
}

/// <summary>
/// Il repository si puo' condividere con una porta del pool di tunnel. Le chiavi description,
/// label e pool del modello precedente, se ancora nel file, si ignorano e spariscono al Salva.
/// </summary>
public sealed class DevTunnelConfiguration
{
    /// <summary>Le porte da esporre; null = quelle del tipo (Kestrel per il backend, dev server per il frontend).</summary>
    public List<TunnelPortConfiguration>? Ports { get; set; }
}

public sealed class TunnelPortConfiguration
{
    public int Number { get; set; }

    public string Protocol { get; set; } = "http";
}

public sealed class AzureDevOpsConfiguration
{
    /// <summary>URL della collection o dell'organizzazione, es. https://server/tfs/Collection o https://dev.azure.com/org.</summary>
    public string CollectionUrl { get; set; } = string.Empty;

    public string Project { get; set; } = string.Empty;

    public AzureAuthentication Authentication { get; set; } = AzureAuthentication.Windows;

    /// <summary>Il PAT in chiaro: accettato solo nel file importato, mai scritto dal programma.</summary>
    public string? Pat { get; set; }

    /// <summary>Il PAT cifrato con DPAPI per l'utente Windows corrente: leggibile solo da lui, su questa macchina.</summary>
    public string? PatProtected { get; set; }

    [JsonIgnore]
    public bool IsConfigured => !string.IsNullOrWhiteSpace(CollectionUrl);
}

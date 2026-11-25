using System.Text.RegularExpressions;

namespace Soffritto.Core.Configuration;

/// <summary>Il formato del file di env di un repository: decide come si legge e come si scrive.</summary>
public enum EnvAdapterKind
{
    /// <summary>Nessun file: solo le variabili scritte nel setup.</summary>
    Manual,

    /// <summary>@set / set di un .bat.</summary>
    Bat,

    /// <summary>NOME=valore di un .env.</summary>
    Dotenv,

    /// <summary>Le chiavi "A:B" di un appsettings*.json.</summary>
    AppSettings,
}

/// <summary>
/// Le variabili di un repository negli ambienti Docker: costanti dall'ambiente modello, segreti
/// dalla loro fonte, valori per slot. Il worktree le riceve dall'adapter, il container da un env_file.
/// </summary>
public sealed partial class EnvConfiguration
{
    public EnvAdapterKind Adapter { get; set; } = EnvAdapterKind.Manual;

    /// <summary>Il file di env nel repository, relativo alla radice.</summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>Il file delle costanti nell'ambiente modello, relativo alla radice; vuoto = il target.</summary>
    public string ConstantsSource { get; set; } = string.Empty;

    /// <summary>Prefissi di nome esclusi dalle costanti del container.</summary>
    public List<string> ContainerExclude { get; set; } = [];

    public List<EnvManualVariable> Manual { get; set; } = [];

    /// <summary>La fonte dei segreti condivisi; null = nessun segreto.</summary>
    public EnvSecretsConfiguration? Secrets { get; set; }

    public List<EnvSlotVariable> Slot { get; set; } = [];

    /// <summary>I segnaposto ammessi nei valori per slot, oltre a {poolUrl:&lt;id&gt;}.</summary>
    public static readonly IReadOnlyList<string> Placeholders =
    [
        "{n}", "{hostPort}", "{containerPort}", "{apiHostUrl}", "{apiContainerUrl}",
        "{hostConnection}", "{containerConnection}", "{poolUrl}",
    ];

    public const string PoolUrlPrefix = "poolUrl:";

    /// <summary>I segnaposto di un valore, con le graffe.</summary>
    public static IEnumerable<string> PlaceholdersIn(string? value)
        => PlaceholderPattern().Matches(value ?? string.Empty).Select(m => m.Value);

    /// <summary>
    /// I valori proposti per tipo; null per i generici, che non hanno un adapter. Le variabili con
    /// nomi di un progetto (URL del backend per il frontend, URL del tunnel per l'app) non si
    /// propongono: si scrivono nel setup.
    /// </summary>
    public static EnvConfiguration? Proposed(RepositoryConfiguration repository) => repository switch
    {
        BackendConfiguration backend => new EnvConfiguration
        {
            Adapter = EnvAdapterKind.AppSettings,
            Target = backend.AppSettings.FirstOrDefault() ?? string.Empty,
            ContainerExclude = ["Kestrel:"],
            Slot = [new EnvSlotVariable { Name = $"ConnectionStrings:{backend.ConnectionStringName}", Local = "{hostConnection}", Container = "{containerConnection}" }],
        },
        FrontendConfiguration frontend => new EnvConfiguration
        {
            Adapter = EnvAdapterKind.Bat,
            Target = frontend.EnvFile,
            Secrets = new EnvSecretsConfiguration
            {
                Adapter = EnvAdapterKind.Bat,
                Source = Path.Combine(Path.GetDirectoryName(frontend.EnvFile) ?? string.Empty, "secrets.bat"),
            },
            Slot = frontend.DevServerPortVariable.Length > 0
                ? [new EnvSlotVariable { Name = frontend.DevServerPortVariable, Local = "{hostPort}", Container = "{containerPort}" }]
                : [],
        },
        AppRepoConfiguration => new EnvConfiguration
        {
            Adapter = EnvAdapterKind.Dotenv,
            Target = ".env",
        },
        _ => null,
    };

    [GeneratedRegex(@"\{[^{}\s]+\}")]
    private static partial Regex PlaceholderPattern();
}

public sealed class EnvManualVariable
{
    public string Name { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;
}

/// <summary>Dove stanno i segreti condivisi del repository, letti con il loro adapter.</summary>
public sealed class EnvSecretsConfiguration
{
    public EnvAdapterKind Adapter { get; set; } = EnvAdapterKind.Bat;

    /// <summary>Il file dei segreti: relativo alla radice del repository nell'ambiente modello, o assoluto.</summary>
    public string Source { get; set; } = string.Empty;
}

/// <summary>Una variabile con un valore per slot; un valore container vuoto non va al container.</summary>
public sealed class EnvSlotVariable
{
    public string Name { get; set; } = string.Empty;

    public string Local { get; set; } = string.Empty;

    public string Container { get; set; } = string.Empty;
}

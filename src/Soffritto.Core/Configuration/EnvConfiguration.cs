using System.Text.RegularExpressions;

namespace Soffritto.Core.Configuration;

/// <summary>The format of a repository's env file: decides how it is read and written.</summary>
public enum EnvAdapterKind
{
    /// <summary>No file: only the variables written in setup.</summary>
    Manual,

    /// <summary>@set / set lines of a .bat.</summary>
    Bat,

    /// <summary>NAME=value lines of a .env.</summary>
    Dotenv,

    /// <summary>The "A:B" keys of an appsettings*.json.</summary>
    AppSettings,
}

/// <summary>
/// A repository's variables in Docker environments: constants from the template environment, secrets
/// from their source, per-slot values. The worktree gets them through the adapter, the container through an env_file.
/// </summary>
public sealed partial class EnvConfiguration
{
    public EnvAdapterKind Adapter { get; set; } = EnvAdapterKind.Manual;

    /// <summary>The env file in the repository, relative to the root.</summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>The constants file in the template environment, relative to the root; empty = the target.</summary>
    public string ConstantsSource { get; set; } = string.Empty;

    /// <summary>Name prefixes excluded from the container's constants.</summary>
    public List<string> ContainerExclude { get; set; } = [];

    public List<EnvManualVariable> Manual { get; set; } = [];

    /// <summary>The source of the shared secrets; null = no secrets.</summary>
    public EnvSecretsConfiguration? Secrets { get; set; }

    public List<EnvSlotVariable> Slot { get; set; } = [];

    /// <summary>The placeholders allowed in per-slot values, besides {poolUrl:&lt;id&gt;}.</summary>
    public static readonly IReadOnlyList<string> Placeholders =
    [
        "{n}", "{hostPort}", "{containerPort}", "{apiHostUrl}", "{apiContainerUrl}",
        "{hostConnection}", "{containerConnection}", "{poolUrl}",
    ];

    public const string PoolUrlPrefix = "poolUrl:";

    /// <summary>The placeholders in a value, braces included.</summary>
    public static IEnumerable<string> PlaceholdersIn(string? value)
        => PlaceholderPattern().Matches(value ?? string.Empty).Select(m => m.Value);

    /// <summary>
    /// The suggested values per type; null for generic repositories, which have no adapter. Variables with
    /// project-specific names (backend URL for the frontend, tunnel URL for the app) are not
    /// suggested: they are written in setup.
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

/// <summary>Where the repository's shared secrets live, read with their adapter.</summary>
public sealed class EnvSecretsConfiguration
{
    public EnvAdapterKind Adapter { get; set; } = EnvAdapterKind.Bat;

    /// <summary>The secrets file: relative to the repository root in the template environment, or absolute.</summary>
    public string Source { get; set; } = string.Empty;
}

/// <summary>A variable with a per-slot value; an empty container value is not passed to the container.</summary>
public sealed class EnvSlotVariable
{
    public string Name { get; set; } = string.Empty;

    public string Local { get; set; } = string.Empty;

    public string Container { get; set; } = string.Empty;
}

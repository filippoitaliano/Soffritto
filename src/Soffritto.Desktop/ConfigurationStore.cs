using System.IO;
using System.Security.Cryptography;
using System.Text;
using Soffritto.Core;
using Soffritto.Core.AzureDevOps;
using Soffritto.Core.Configuration;

namespace Soffritto.Desktop;

/// <summary>
/// Saving and provisioning of the configuration. The working copy lives in %LOCALAPPDATA%;
/// provisioning is a soffritto.json next to the exe (or the --config file): at startup, if its
/// content changed since the last import, it replaces the working copy. The program never
/// writes it, so changes made in the setup stay until the file changes.
/// </summary>
public static class ConfigurationStore
{
    public const string ProvisioningFileName = "soffritto.json";

    public static string ProvisioningFile { get; } = Path.Combine(AppContext.BaseDirectory, ProvisioningFileName);

    // The hash of the last imported file: the same file is not reimported at every startup.
    private static readonly string ImportedHashPath = Path.Combine(ConfigurationFiles.DataFolder, "ultimo-import.txt");

    /// <summary>
    /// The configuration to start with: imports <paramref name="importPath"/> (or the provisioning
    /// file next to the exe) if it changed, then reads the working copy. Null on the first start
    /// without provisioning.
    /// </summary>
    /// <param name="notice">What happened at startup, to show in the setup panel; null if there is nothing to say.</param>
    public static AppConfiguration? LoadAtStartup(string? importPath, out string? notice)
    {
        notice = null;
        var source = importPath ?? ProvisioningFile;

        try
        {
            if (File.Exists(source))
            {
                var text = File.ReadAllText(source);
                var hash = Hash(text);
                if (hash != ReadImportedHash())
                {
                    var imported = ConfigurationFiles.Parse(text);
                    var lostDocker = DockerEnvironmentsLost(imported);

                    // An import replaces the working copy: this copy keeps it.
                    if (File.Exists(ConfigurationFiles.ConfigPath))
                    {
                        File.Copy(ConfigurationFiles.ConfigPath, Path.Combine(ConfigurationFiles.DataFolder, "config.prima-import.json"), overwrite: true);
                    }

                    Save(imported);
                    File.WriteAllText(ImportedHashPath, hash);
                    notice = $"Configuration imported from {source}. The previous configuration is in config.prima-import.json.{lostDocker}";
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            notice = $"{source} was not imported: {ex.Message}";
        }

        var configuration = ConfigurationFiles.TryLoad(out var error);
        if (error is not null)
        {
            notice = notice is null ? error : $"{notice} {error}";
        }

        return configuration;
    }

    // The import replaces everything, Docker entries included: a Docker environment in the working copy
    // that the imported file lacks loses its entry, but containers and worktrees stay on disk. Say so.
    private static string DockerEnvironmentsLost(AppConfiguration imported)
    {
        if (ConfigurationFiles.TryLoad(out _) is not { } current)
        {
            return string.Empty;
        }

        var lost = current.DockerEnvironments
            .Where(e => imported.Environment(e.Number) is not { Kind: EnvironmentKind.Docker })
            .Select(e =>
            {
                try
                {
                    var project = Soffritto.Core.Docker.DockerSlot.Of(current, e).Project;
                    var worktrees = string.Join(", ", current.RepositoriesOf(e).Select(r => r.Path));
                    return $"{e.Number} (project {project}, worktrees in {worktrees}). Containers and worktrees remain: recreate the entry or destroy them with docker compose -p {project} down";
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
                {
                    return $"{e.Number} (project and worktrees cannot be determined: {ex.Message})";
                }
            })
            .ToList();

        return lost.Count == 0 ? string.Empty : $" Docker environments no longer in the configuration: {string.Join("; ", lost)}.";
    }

    /// <summary>Writes the working copy. A plain-text PAT is encrypted before it touches the disk.</summary>
    public static void Save(AppConfiguration configuration)
    {
        ProtectPat(configuration.AzureDevOps);
        ConfigurationFiles.Write(configuration);
    }

    /// <summary>Encrypts the plain-text PAT with the Windows user (into PatProtected) and clears it: call before every write.</summary>
    public static void ProtectPat(AzureDevOpsConfiguration azure)
    {
        if (!string.IsNullOrEmpty(azure.Pat))
        {
            azure.PatProtected = Protect(azure.Pat);
        }

        azure.Pat = null;
    }

    /// <summary>The plain-text PAT for Azure DevOps calls; null if missing or it cannot be decrypted (another user or another machine).</summary>
    public static string? PatOf(AzureDevOpsConfiguration azure) => AzureDevOpsSecrets.PatOf(azure);

    private static string Protect(string secret) => AzureDevOpsSecrets.Protect(secret);

    private static string? ReadImportedHash()
    {
        try
        {
            return File.Exists(ImportedHashPath) ? File.ReadAllText(ImportedHashPath).Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

/// <summary>
/// Azure DevOps as the active configuration describes it: the panel's PRs, the stories of the
/// story points meeting and the work item links.
/// </summary>
public static class AzureDevOpsAccess
{
    private static readonly Lazy<AzureDevOpsConnection?> Connection = new(() =>
    {
        var azure = ActiveConfiguration.Current.AzureDevOps;
        if (!azure.IsConfigured)
        {
            return null;
        }

        var pat = azure.Authentication == AzureAuthentication.Pat ? ConfigurationStore.PatOf(azure) : null;
        return new AzureDevOpsConnection(azure.CollectionUrl, azure.Project, pat);
    });

    private static readonly Lazy<PullRequestReader?> Reader = new(() => Connection.Value is { } c ? new PullRequestReader(c) : null);

    private static readonly Lazy<StoryPointsBoard?> Board = new(() => Connection.Value is { } c ? new StoryPointsBoard(c) : null);

    public static Task<MyPullRequests> ReadAsync()
        => Problem() is { } problem
            ? Task.FromResult(new MyPullRequests([], [], Error: problem))
            : Reader.Value!.ReadAsync();

    public static Task<StoriesToEstimate> ReadStoriesAsync()
        => Problem() is { } problem
            ? Task.FromResult(new StoriesToEstimate([], problem))
            : Board.Value!.ReadAsync();

    public static Task<string?> SaveStoryPointsAsync(int storyId, int points)
        => Problem() is { } problem
            ? Task.FromResult<string?>(problem)
            : Board.Value!.SaveAsync(storyId, points);

    private static string? Problem()
    {
        var azure = ActiveConfiguration.Current.AzureDevOps;
        if (!azure.IsConfigured)
        {
            return "Azure DevOps is not configured: fill in the DevOps tab in the setup.";
        }

        return azure.Authentication == AzureAuthentication.Pat && ConfigurationStore.PatOf(azure) is null
            ? "The Azure DevOps PAT is missing or cannot be decrypted on this machine: enter it in the setup."
            : null;
    }

    public static Uri? WorkItemUrl(int id)
        => Reader.Value is { } reader ? new Uri($"{reader.CollectionUrl}/{reader.Project}/_workitems/edit/{id}") : null;
}

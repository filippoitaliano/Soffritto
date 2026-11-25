using System.IO;
using System.Security.Cryptography;
using System.Text;
using Soffritto.Core;
using Soffritto.Core.AzureDevOps;
using Soffritto.Core.Configuration;

namespace Soffritto.Desktop;

/// <summary>
/// Salvataggio e provisioning della configurazione. La copia di lavoro sta in %LOCALAPPDATA%;
/// il provisioning e' un soffritto.json accanto all'exe (o il file di --config): all'avvio,
/// se il suo contenuto e' cambiato dall'ultimo import, sostituisce la copia di lavoro. Il
/// programma non lo scrive mai, quindi le modifiche dal setup restano finche' il file non cambia.
/// </summary>
public static class ConfigurationStore
{
    public const string ProvisioningFileName = "soffritto.json";

    public static string ProvisioningFile { get; } = Path.Combine(AppContext.BaseDirectory, ProvisioningFileName);

    // L'impronta dell'ultimo file importato: lo stesso file non si reimporta a ogni avvio.
    private static readonly string ImportedHashPath = Path.Combine(ConfigurationFiles.DataFolder, "ultimo-import.txt");

    /// <summary>
    /// La configurazione con cui partire: importa <paramref name="importPath"/> (o il file di
    /// provisioning accanto all'exe) se e' cambiato, poi legge la copia di lavoro. Null al primo
    /// avvio senza provisioning.
    /// </summary>
    /// <param name="notice">Cosa e' successo all'avvio, da mostrare nel pannello di setup; null se niente da dire.</param>
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

                    // Un import sostituisce la copia di lavoro: questa copia la conserva.
                    if (File.Exists(ConfigurationFiles.ConfigPath))
                    {
                        File.Copy(ConfigurationFiles.ConfigPath, Path.Combine(ConfigurationFiles.DataFolder, "config.prima-import.json"), overwrite: true);
                    }

                    Save(imported);
                    File.WriteAllText(ImportedHashPath, hash);
                    notice = $"Configurazione importata da {source}. La configurazione precedente e' in config.prima-import.json.{lostDocker}";
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            notice = $"{source} non e' stato importato: {ex.Message}";
        }

        var configuration = ConfigurationFiles.TryLoad(out var error);
        if (error is not null)
        {
            notice = notice is null ? error : $"{notice} {error}";
        }

        return configuration;
    }

    // L'import sostituisce tutto, voci Docker comprese: un ambiente Docker della copia di lavoro che
    // il file importato non ha perde la voce, ma container e worktree restano sul disco. Lo si dice.
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
                    return $"{e.Number} (progetto {project}, worktree in {worktrees}). Container e worktree restano: ricrea la voce o distruggili con docker compose -p {project} down";
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
                {
                    return $"{e.Number} (progetto e worktree non calcolabili: {ex.Message})";
                }
            })
            .ToList();

        return lost.Count == 0 ? string.Empty : $" Ambienti Docker non piu' in configurazione: {string.Join("; ", lost)}.";
    }

    /// <summary>Scrive la copia di lavoro. Un PAT in chiaro viene cifrato prima di toccare il disco.</summary>
    public static void Save(AppConfiguration configuration)
    {
        ProtectPat(configuration.AzureDevOps);
        ConfigurationFiles.Write(configuration);
    }

    /// <summary>Cifra il PAT in chiaro con l'utente Windows (in PatProtected) e lo toglie: da chiamare prima di ogni scrittura.</summary>
    public static void ProtectPat(AzureDevOpsConfiguration azure)
    {
        if (!string.IsNullOrEmpty(azure.Pat))
        {
            azure.PatProtected = Protect(azure.Pat);
        }

        azure.Pat = null;
    }

    /// <summary>Il PAT in chiaro per le chiamate ad Azure DevOps; null se manca o non si decifra (altro utente o altra macchina).</summary>
    public static string? PatOf(AzureDevOpsConfiguration azure)
    {
        if (!string.IsNullOrEmpty(azure.Pat))
        {
            return azure.Pat;
        }

        try
        {
            return string.IsNullOrEmpty(azure.PatProtected)
                ? null
                : Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(azure.PatProtected), null, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }

    private static string Protect(string secret)
        => Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), null, DataProtectionScope.CurrentUser));

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
/// Azure DevOps come lo descrive la configurazione attiva: le PR del pannello, le storie della
/// riunione dei punti storia e i link ai work item.
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
            return "Azure DevOps non e' configurato: compila la scheda DevOps nel setup.";
        }

        return azure.Authentication == AzureAuthentication.Pat && ConfigurationStore.PatOf(azure) is null
            ? "Il PAT di Azure DevOps manca o non si decifra su questa macchina: inseriscilo nel setup."
            : null;
    }

    public static Uri? WorkItemUrl(int id)
        => Reader.Value is { } reader ? new Uri($"{reader.CollectionUrl}/{reader.Project}/_workitems/edit/{id}") : null;
}

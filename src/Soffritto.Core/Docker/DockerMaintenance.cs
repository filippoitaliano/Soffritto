using System.Text.RegularExpressions;
using Soffritto.Core.Configuration;

namespace Soffritto.Core.Docker;

/// <summary>
/// Lo spazio occupato in Docker dagli ambienti: le immagini costruite dal compose di ogni
/// ambiente e la cache di build. Le immagini di un ambiente distrutto non servono piu'; la cache
/// accelera le build ma cresce senza limite. Docker riusa lo spazio liberato, quindi tenere basso
/// l'uso interno impedisce al suo disco virtuale di crescere.
/// </summary>
public static class DockerMaintenance
{
    /// <summary>La cache di build che la pulizia lascia: sopra, Docker toglie le voci usate meno di recente.</summary>
    public const string CacheLimit = "10GB";

    /// <summary>Le immagini costruite dal compose del progetto: "&lt;progetto&gt;-&lt;servizio&gt;".</summary>
    public static async Task<(IReadOnlyList<string> Images, string? Error)> ImagesOfProjectAsync(string project, CancellationToken ct = default)
    {
        var (all, error) = await RepositoriesAsync(ct).ConfigureAwait(false);
        return (all.Where(name => name.StartsWith(project + "-", StringComparison.Ordinal)).ToList(), error);
    }

    /// <summary>
    /// Le immagini degli ambienti di questa cartella dati il cui numero non e' piu' un ambiente
    /// Docker della configurazione: resti di ambienti distrutti con versioni che le lasciavano, o
    /// di creazioni a meta'.
    /// </summary>
    public static async Task<(IReadOnlyList<string> Images, string? Error)> OrphanImagesAsync(AppConfiguration c, CancellationToken ct = default)
    {
        var (all, error) = await RepositoriesAsync(ct).ConfigureAwait(false);
        var pattern = new Regex($"^{Regex.Escape(DockerNames.ProjectPrefix)}(\\d+)-.+$", RegexOptions.CultureInvariant);
        var orphans = all
            .Select(name => (Name: name, Match: pattern.Match(name)))
            .Where(x => x.Match.Success
                && int.TryParse(x.Match.Groups[1].Value, out var number)
                && c.Environment(number) is not { Kind: EnvironmentKind.Docker })
            .Select(x => x.Name)
            .ToList();
        return (orphans, error);
    }

    /// <summary>Toglie le immagini indicate; l'esito in una riga, anche quando qualcuna resta.</summary>
    public static async Task<string> RemoveImagesAsync(IReadOnlyList<string> images, CancellationToken ct = default)
    {
        if (images.Count == 0)
        {
            return "nessuna immagine da togliere";
        }

        var remove = await DockerCli.RunAsync($"image rm {string.Join(' ', images)}", timeoutSeconds: 300, ct: ct).ConfigureAwait(false);
        return remove.Success
            ? $"immagini tolte: {string.Join(", ", images)}"
            : $"immagini non tutte tolte ({string.Join(", ", images)}): {remove.Tail(2)}";
    }

    /// <summary>Riduce la cache di build a <see cref="CacheLimit"/>, togliendo le voci usate meno di recente.</summary>
    public static async Task<string> TrimBuildCacheAsync(CancellationToken ct = default)
    {
        var prune = await DockerCli.RunAsync($"builder prune -f --max-used-space {CacheLimit}", timeoutSeconds: 600, ct: ct).ConfigureAwait(false);
        return prune.Success
            ? $"cache di build ridotta a {CacheLimit} ({LastLine(prune.StandardOutput)})"
            : $"cache di build non ridotta: {prune.Tail(2)}";
    }

    /// <summary>
    /// La riga di comando della pulizia lanciata in un processo staccato (alla chiusura dell'exe):
    /// immagini orfane, poi la cache di build, con l'uscita in <paramref name="logPath"/>.
    /// </summary>
    public static string CleanupCommandLine(IReadOnlyList<string> orphanImages, string logPath)
    {
        var steps = new List<string>();
        if (orphanImages.Count > 0)
        {
            steps.Add($"docker image rm {string.Join(' ', orphanImages)}");
        }

        steps.Add($"docker builder prune -f --max-used-space {CacheLimit}");
        return $"cmd.exe /d /c \"(echo %DATE% %TIME% & {string.Join(" & ", steps)}) > \"{logPath}\" 2>&1\"";
    }

    /// <summary>L'uso di Docker per tipo (immagini, container, volumi, cache): dimensione e quanto se ne recupera.</summary>
    public static async Task<(IReadOnlyList<(string Type, string Size, string Reclaimable)> Usage, string? Error)> UsageAsync(CancellationToken ct = default)
    {
        var df = await DockerCli.RunAsync("system df --format \"{{.Type}}|{{.Size}}|{{.Reclaimable}}\"", timeoutSeconds: 60, ct: ct).ConfigureAwait(false);
        if (!df.Success)
        {
            return ([], df.Tail(2));
        }

        var rows = df.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim().Split('|'))
            .Where(parts => parts.Length == 3)
            .Select(parts => (parts[0], parts[1], parts[2]))
            .ToList();
        return (rows, null);
    }

    private static async Task<(IReadOnlyList<string> Repositories, string? Error)> RepositoriesAsync(CancellationToken ct)
    {
        var list = await DockerCli.RunAsync("image ls --format \"{{.Repository}}\"", timeoutSeconds: 60, ct: ct).ConfigureAwait(false);
        if (!list.Success)
        {
            return ([], list.Tail(2));
        }

        return (list.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(name => name.Length > 0 && name != "<none>")
            .Distinct(StringComparer.Ordinal)
            .ToList(), null);
    }

    private static string LastLine(string text)
        => text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "niente da togliere";
}

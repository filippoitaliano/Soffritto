using System.Text.Json;
using System.Text.RegularExpressions;

namespace Soffritto.Core.Docker;

/// <summary>Lo stato di un container per i pallini: il Desktop lo porta su ServiceState.</summary>
public enum ContainerRunState
{
    /// <summary>exited, dead, paused, removing, created e mai partito, o container assente.</summary>
    Stopped,

    /// <summary>created da poco, restarting, o running con l'healthcheck ancora in partenza: arancio.</summary>
    Starting,

    Running,
}

public sealed record DockerContainer(string Id, string Name, string Project, string Service, string State, string Status, ContainerRunState RunState);

/// <summary>I container della cartella dati, o l'errore se Docker non risponde.</summary>
public sealed record DockerStatus(IReadOnlyList<DockerContainer> Containers, string? Error = null)
{
    public bool IsReachable => Error is null;

    public IEnumerable<DockerContainer> OfProject(string project)
        => Containers.Where(c => string.Equals(c.Project, project, StringComparison.Ordinal));

    /// <summary>Lo stato del servizio del progetto; Stopped se il container non c'e'.</summary>
    public ContainerRunState StateOf(string project, string service)
        => OfProject(project).FirstOrDefault(c => string.Equals(c.Service, service, StringComparison.Ordinal))?.RunState ?? ContainerRunState.Stopped;
}

/// <summary>Legge i container della cartella dati (etichetta di <see cref="DockerNames"/>) con docker ps.</summary>
public static class DockerStatusReader
{
    public const string Unreachable = "Docker non raggiungibile";

    public static async Task<DockerStatus> ReadAsync(CancellationToken ct = default)
    {
        var ps = await DockerCli.RunAsync(
            $"ps -a --filter \"label={DockerNames.Label}\" --format \"{{{{json .}}}}\"",
            timeoutSeconds: 10,
            ct: ct).ConfigureAwait(false);

        if (!ps.Success)
        {
            var reason = ps.Tail(2);
            return new DockerStatus([], reason.Length > 0 ? $"{Unreachable}: {reason}" : Unreachable);
        }

        var now = DateTimeOffset.Now;
        var containers = new List<DockerContainer>();
        foreach (var line in ps.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                var labels = Text(root, "Labels");
                var state = Text(root, "State");
                var status = Text(root, "Status");
                containers.Add(new DockerContainer(
                    Text(root, "ID"),
                    Text(root, "Names"),
                    LabelValue(labels, "com.docker.compose.project"),
                    LabelValue(labels, "com.docker.compose.service"),
                    state,
                    status,
                    RunStateOf(state, status, Text(root, "CreatedAt"), now)));
            }
            catch (JsonException)
            {
                // Una riga non json (avviso del client) non e' un container.
            }
        }

        return new DockerStatus(containers);
    }

    // compose up lascia un container in «created» per pochi secondi, o finche' aspetta una
    // dipendenza. Oltre questo tempo il container non parte piu' da solo: e' spento.
    private static readonly TimeSpan CreatedLimit = TimeSpan.FromMinutes(2);

    /// <param name="createdAt">Il campo CreatedAt di docker ps, es. "2026-11-18 10:05:30 +0100 CET".</param>
    public static ContainerRunState RunStateOf(string state, string status, string createdAt, DateTimeOffset now) => state switch
    {
        "running" when status.Contains("(health: starting)", StringComparison.Ordinal) => ContainerRunState.Starting,
        "running" => ContainerRunState.Running,
        "created" when CreatedOf(createdAt) is { } created && now - created > CreatedLimit => ContainerRunState.Stopped,
        "restarting" or "created" => ContainerRunState.Starting,
        _ => ContainerRunState.Stopped,
    };

    // Data, ora e offset; il nome del fuso in coda non si legge.
    private static DateTimeOffset? CreatedOf(string createdAt)
    {
        var parts = createdAt.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3
            && DateTimeOffset.TryParseExact(
                $"{parts[0]} {parts[1]} {parts[2]}",
                "yyyy-MM-dd HH:mm:ss zzz",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out var created)
            ? created
            : null;
    }

    private static string Text(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    // Le etichette arrivano come "k=v,k=v", e alcuni valori di compose contengono virgole
    // (config_files): si cerca la chiave esatta a inizio stringa o dopo una virgola.
    private static string LabelValue(string labels, string key)
    {
        var match = Regex.Match(labels, $@"(?:^|,){Regex.Escape(key)}=([^,]*)");
        return match.Success ? match.Groups[1].Value : string.Empty;
    }
}

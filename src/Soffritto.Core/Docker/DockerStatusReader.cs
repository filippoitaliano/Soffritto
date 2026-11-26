using System.Text.Json;
using System.Text.RegularExpressions;

namespace Soffritto.Core.Docker;

/// <summary>A container's state for the status dots: the Desktop maps it to ServiceState.</summary>
public enum ContainerRunState
{
    /// <summary>exited, dead, paused, removing, created and never started, or no container.</summary>
    Stopped,

    /// <summary>recently created, restarting, or running with the healthcheck still starting: orange.</summary>
    Starting,

    Running,
}

public sealed record DockerContainer(string Id, string Name, string Project, string Service, string State, string Status, ContainerRunState RunState);

/// <summary>The data folder's containers, or the error if Docker does not respond.</summary>
public sealed record DockerStatus(IReadOnlyList<DockerContainer> Containers, string? Error = null)
{
    public bool IsReachable => Error is null;

    public IEnumerable<DockerContainer> OfProject(string project)
        => Containers.Where(c => string.Equals(c.Project, project, StringComparison.Ordinal));

    /// <summary>The state of the project's service; Stopped if there is no container.</summary>
    public ContainerRunState StateOf(string project, string service)
        => OfProject(project).FirstOrDefault(c => string.Equals(c.Service, service, StringComparison.Ordinal))?.RunState ?? ContainerRunState.Stopped;
}

/// <summary>Reads the data folder's containers (label from <see cref="DockerNames"/>) with docker ps.</summary>
public static class DockerStatusReader
{
    public const string Unreachable = "Docker unreachable";

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
                // A non-JSON line (a client warning) is not a container.
            }
        }

        return new DockerStatus(containers);
    }

    // compose up leaves a container "created" for a few seconds, or while it waits for a
    // dependency. Past this time the container will not start on its own anymore: it is stopped.
    private static readonly TimeSpan CreatedLimit = TimeSpan.FromMinutes(2);

    /// <param name="createdAt">The CreatedAt field of docker ps, e.g. "2026-11-18 10:05:30 +0100 CET".</param>
    public static ContainerRunState RunStateOf(string state, string status, string createdAt, DateTimeOffset now) => state switch
    {
        "running" when status.Contains("(health: starting)", StringComparison.Ordinal) => ContainerRunState.Starting,
        "running" => ContainerRunState.Running,
        "created" when CreatedOf(createdAt) is { } created && now - created > CreatedLimit => ContainerRunState.Stopped,
        "restarting" or "created" => ContainerRunState.Starting,
        _ => ContainerRunState.Stopped,
    };

    // Date, time and offset; the trailing time zone name is ignored.
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

    // Labels arrive as "k=v,k=v", and some compose values contain commas
    // (config_files): look for the exact key at the start of the string or after a comma.
    private static string LabelValue(string labels, string key)
    {
        var match = Regex.Match(labels, $@"(?:^|,){Regex.Escape(key)}=([^,]*)");
        return match.Success ? match.Groups[1].Value : string.Empty;
    }
}

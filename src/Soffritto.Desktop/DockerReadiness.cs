using Soffritto.Core.Docker;

namespace Soffritto.Desktop;

/// <summary>
/// The phase of a Docker environment (off, starting, ready) and the state of db, api and web, from the
/// docker ps containers and the <see cref="DockerReadinessProbe"/> probes. Probes run off the UI
/// thread (<see cref="ProbeAsync"/>), evaluation runs on the UI thread (<see cref="Evaluate"/>).
/// </summary>
internal sealed class DockerReadiness(DockerSlot slot)
{
    // Past this time a running container that does not answer is no longer “starting”: it is an error.
    private static readonly TimeSpan NoAnswerLimit = TimeSpan.FromMinutes(5);

    private readonly object gate = new();
    private readonly Dictionary<string, (string ContainerId, bool Ready)> probed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string ContainerId, DateTimeOffset Since)> runningSince = new(StringComparer.OrdinalIgnoreCase);

    // Services stopped on purpose (stop from the card or the stack): being off is not an error for them. On
    // the first read whatever is already off counts as stopped on purpose, since the intent is unknown.
    private readonly HashSet<string> stoppedOnPurpose = new(StringComparer.OrdinalIgnoreCase);
    private bool evaluated;

    public enum Phase
    {
        Off,
        Warmup,
        Ready,
        Failed,
    }

    public enum ServiceState
    {
        Off,
        Starting,
        Ready,
        Failed,
    }

    /// <summary>The compose action running on the environment, from the app's buttons.</summary>
    public enum ComposeAction
    {
        None,
        Starting,
        Stopping,
    }

    public sealed record Service(string Name, ServiceState State, string Detail);

    /// <param name="DatabaseOffline">Why migrations cannot be read (database off or starting); null if the database answers or has a real error to show.</param>
    public sealed record Snapshot(
        Phase Phase,
        IReadOnlyList<Service> Services,
        string Text,
        string? DatabaseOffline)
    {
        public bool IsWarmingUp => Phase == Phase.Warmup;
    }

    /// <summary>Before the first docker ps read: no database connection.</summary>
    public static Snapshot NotRead { get; } = new(Phase.Off, [], string.Empty, "Container status not read yet");

    private IEnumerable<(string Name, bool IsDatabase, int Port)> Services()
    {
        yield return (slot.DatabaseService, true, slot.DatabasePort);

        if (slot.Backend is { } backend && slot.ApiService is { } api && slot.PortOf(backend) is { } apiPort)
        {
            yield return (api, false, apiPort.Host);
        }

        if (slot.Frontend is { } frontend && slot.WebService is { } web && slot.PortOf(frontend) is { } webPort)
        {
            yield return (web, false, webPort.Host);
        }
    }

    /// <summary>A stop requested from the app: a null <paramref name="service"/> means every service. On the UI thread.</summary>
    public void MarkStopped(string? service)
    {
        foreach (var name in service is null ? Services().Select(s => s.Name) : [service])
        {
            stoppedOnPurpose.Add(name);
        }
    }

    /// <summary>A start requested from the app: a null <paramref name="service"/> means every service. On the UI thread.</summary>
    public void MarkStarted(string? service)
    {
        if (service is null)
        {
            stoppedOnPurpose.Clear();
            return;
        }

        stoppedOnPurpose.Remove(service);
    }

    /// <summary>
    /// Probes the running containers. A ready service stays ready as long as the same container keeps
    /// running: probing restarts only with a new container (compose up recreates it).
    /// </summary>
    public async Task ProbeAsync(DockerStatus docker, CancellationToken ct)
    {
        if (!docker.IsReachable)
        {
            return;
        }

        var probes = Services().Select(async service =>
        {
            var container = ContainerOf(docker, service.Name);
            if (container is not { RunState: ContainerRunState.Running })
            {
                lock (gate)
                {
                    probed.Remove(service.Name);
                }

                return;
            }

            lock (gate)
            {
                if (probed.TryGetValue(service.Name, out var known) && known.ContainerId == container.Id && known.Ready)
                {
                    return;
                }
            }

            var ready = service.IsDatabase
                ? await DockerReadinessProbe.DatabaseAsync(slot, ct).ConfigureAwait(false)
                : await DockerReadinessProbe.HttpAsync(service.Port, ct).ConfigureAwait(false);

            lock (gate)
            {
                probed[service.Name] = (container.Id, ready);
            }
        });

        await Task.WhenAll(probes).ConfigureAwait(false);
    }

    public Snapshot Evaluate(DockerStatus? docker, ComposeAction action, DateTimeOffset now)
    {
        if (docker is null)
        {
            return NotRead;
        }

        if (!docker.IsReachable)
        {
            return new Snapshot(Phase.Off, [], string.Empty, "Docker unreachable");
        }

        if (action == ComposeAction.Stopping)
        {
            return new Snapshot(Phase.Off, [], "stopping containers…", "Database stopping");
        }

        var services = Services().Select(service => Observe(docker, service.Name, now)).ToList();
        if (!evaluated)
        {
            evaluated = true;
            foreach (var off in services.Where(s => s.State == ServiceState.Off))
            {
                stoppedOnPurpose.Add(off.Name);
            }
        }

        // Environment starting or running: an off service is waiting while compose works, stays off
        // if stopped on purpose, otherwise it has gone down (container exited or never created).
        services = services
            .Select(s => s.State != ServiceState.Off
                ? s
                : action == ComposeAction.Starting && !stoppedOnPurpose.Contains(s.Name)
                    ? s with { State = ServiceState.Starting, Detail = "waiting for compose" }
                    : stoppedOnPurpose.Contains(s.Name)
                        ? s with { Detail = "stopped" }
                        : s with { State = ServiceState.Failed })
            .ToList();

        var up = services.Where(s => s.State != ServiceState.Off).ToList();
        if (up.Count == 0)
        {
            return new Snapshot(Phase.Off, services, "off: ▷ to start", "Database off");
        }

        var db = services[0];
        // A service that went down is not a start in progress: no animated stripes, text “error”.
        var phase = up.Any(s => s.State == ServiceState.Failed) || db.State == ServiceState.Failed
            ? Phase.Failed
            : up.All(s => s.State == ServiceState.Ready) ? Phase.Ready : Phase.Warmup;

        var offline = db.State switch
        {
            ServiceState.Ready => null,
            ServiceState.Off => "Database off",
            ServiceState.Failed => "Database down",
            _ => "Database starting",
        };

        var text = phase == Phase.Ready && up.Count == services.Count
            ? "ready"
            : $"{phase switch { Phase.Ready => "running", Phase.Failed => "error", _ => "starting" }}: {string.Join(" · ", services.Select(s => $"{s.Name} {Describe(s)}"))}";
        return new Snapshot(phase, services, text, offline);
    }

    private Service Observe(DockerStatus docker, string name, DateTimeOffset now)
    {
        var container = ContainerOf(docker, name);
        switch (container?.RunState)
        {
            case ContainerRunState.Running:
            {
                bool ready;
                DateTimeOffset since;
                lock (gate)
                {
                    ready = probed.TryGetValue(name, out var known) && known.ContainerId == container.Id && known.Ready;
                    if (!runningSince.TryGetValue(name, out var running) || running.ContainerId != container.Id)
                    {
                        running = (container.Id, now);
                        runningSince[name] = running;
                    }

                    since = running.Since;
                }

                if (ready)
                {
                    return new Service(name, ServiceState.Ready, "ready");
                }

                return now - since > NoAnswerLimit
                    ? new Service(name, ServiceState.Failed, $"running but not answering for {(int)NoAnswerLimit.TotalMinutes} minutes")
                    : new Service(name, ServiceState.Starting, "running, not answering yet");
            }

            case ContainerRunState.Starting:
                return new Service(name, ServiceState.Starting, container.Status);

            default:
                // A container created but never started has not gone down: it stays off until someone starts it.
                if (container is { State: "created" })
                {
                    stoppedOnPurpose.Add(name);
                }

                return new Service(name, ServiceState.Off, container is null ? "no container" : container.Status);
        }
    }

    private static string Describe(Service service) => service.State switch
    {
        ServiceState.Ready => "ready",
        ServiceState.Failed => $"failed ({service.Detail})",
        ServiceState.Off => "off",
        _ => "starting",
    };

    private DockerContainer? ContainerOf(DockerStatus docker, string service)
        => docker.OfProject(slot.Project).FirstOrDefault(c => string.Equals(c.Service, service, StringComparison.OrdinalIgnoreCase));
}

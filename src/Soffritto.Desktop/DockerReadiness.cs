using Soffritto.Core.Docker;

namespace Soffritto.Desktop;

/// <summary>
/// La fase di un ambiente Docker (spento, avvio, pronto) e lo stato di db, api e web, dai container
/// di docker ps e dalle sonde di <see cref="DockerReadinessProbe"/>. Le sonde girano fuori dal
/// thread della UI (<see cref="ProbeAsync"/>), la valutazione sul thread della UI (<see cref="Evaluate"/>).
/// </summary>
internal sealed class DockerReadiness(DockerSlot slot)
{
    // Oltre questo tempo un container acceso che non risponde non e' piu' «in avvio»: e' un errore.
    private static readonly TimeSpan NoAnswerLimit = TimeSpan.FromMinutes(5);

    private readonly object gate = new();
    private readonly Dictionary<string, (string ContainerId, bool Ready)> probed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string ContainerId, DateTimeOffset Since)> runningSince = new(StringComparer.OrdinalIgnoreCase);

    // Servizi fermati apposta (stop della card o della pila): spenti non sono un errore. Alla prima
    // lettura chi e' gia' spento conta come fermato apposta, perche' l'intenzione non si conosce.
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

    /// <summary>L'azione compose in corso sull'ambiente, dai pulsanti dell'exe.</summary>
    public enum ComposeAction
    {
        None,
        Starting,
        Stopping,
    }

    public sealed record Service(string Name, ServiceState State, string Detail);

    /// <param name="DatabaseOffline">Perche' le migrazioni non si leggono (database spento o in avvio); null se il database risponde o ha un errore vero da mostrare.</param>
    public sealed record Snapshot(
        Phase Phase,
        IReadOnlyList<Service> Services,
        string Text,
        string? DatabaseOffline)
    {
        public bool IsWarmingUp => Phase == Phase.Warmup;
    }

    /// <summary>Prima della prima lettura di docker ps: nessuna connessione al database.</summary>
    public static Snapshot NotRead { get; } = new(Phase.Off, [], string.Empty, "Stato dei container non ancora letto");

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

    /// <summary>Uno stop chiesto dall'exe: <paramref name="service"/> null vale per tutti i servizi. Sul thread della UI.</summary>
    public void MarkStopped(string? service)
    {
        foreach (var name in service is null ? Services().Select(s => s.Name) : [service])
        {
            stoppedOnPurpose.Add(name);
        }
    }

    /// <summary>Un avvio chiesto dall'exe: <paramref name="service"/> null vale per tutti i servizi. Sul thread della UI.</summary>
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
    /// Sonda i container accesi. Un servizio pronto resta pronto finche' resta acceso lo stesso
    /// container: la sonda riparte solo con un container nuovo (compose up lo ricrea).
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
            return new Snapshot(Phase.Off, [], string.Empty, "Docker non raggiungibile");
        }

        if (action == ComposeAction.Stopping)
        {
            return new Snapshot(Phase.Off, [], "arresto dei container…", "Database in arresto");
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

        // Ambiente in avvio o acceso: un servizio spento e' in attesa mentre compose lavora, resta
        // spento se fermato apposta, altrimenti e' caduto (container uscito o mai creato).
        services = services
            .Select(s => s.State != ServiceState.Off
                ? s
                : action == ComposeAction.Starting && !stoppedOnPurpose.Contains(s.Name)
                    ? s with { State = ServiceState.Starting, Detail = "in attesa di compose" }
                    : stoppedOnPurpose.Contains(s.Name)
                        ? s with { Detail = "fermato" }
                        : s with { State = ServiceState.Failed })
            .ToList();

        var up = services.Where(s => s.State != ServiceState.Off).ToList();
        if (up.Count == 0)
        {
            return new Snapshot(Phase.Off, services, "spento: ▷ per avviare", "Database spento");
        }

        var db = services[0];
        // Un servizio caduto non e' un avvio in corso: niente righe animate, testo «errore».
        var phase = up.Any(s => s.State == ServiceState.Failed) || db.State == ServiceState.Failed
            ? Phase.Failed
            : up.All(s => s.State == ServiceState.Ready) ? Phase.Ready : Phase.Warmup;

        var offline = db.State switch
        {
            ServiceState.Ready => null,
            ServiceState.Off => "Database spento",
            ServiceState.Failed => "Database fermo",
            _ => "Database in avvio",
        };

        var text = phase == Phase.Ready && up.Count == services.Count
            ? "pronto"
            : $"{phase switch { Phase.Ready => "acceso", Phase.Failed => "errore", _ => "avvio" }}: {string.Join(" · ", services.Select(s => $"{s.Name} {Describe(s)}"))}";
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
                    return new Service(name, ServiceState.Ready, "pronto");
                }

                return now - since > NoAnswerLimit
                    ? new Service(name, ServiceState.Failed, $"acceso ma non risponde da {(int)NoAnswerLimit.TotalMinutes} minuti")
                    : new Service(name, ServiceState.Starting, "acceso, non risponde ancora");
            }

            case ContainerRunState.Starting:
                return new Service(name, ServiceState.Starting, container.Status);

            default:
                // Un container creato e mai partito non e' caduto: resta spento finche' non lo si avvia.
                if (container is { State: "created" })
                {
                    stoppedOnPurpose.Add(name);
                }

                return new Service(name, ServiceState.Off, container is null ? "container assente" : container.Status);
        }
    }

    private static string Describe(Service service) => service.State switch
    {
        ServiceState.Ready => "pronto",
        ServiceState.Failed => $"in errore ({service.Detail})",
        ServiceState.Off => "spento",
        _ => "in avvio",
    };

    private DockerContainer? ContainerOf(DockerStatus docker, string service)
        => docker.OfProject(slot.Project).FirstOrDefault(c => string.Equals(c.Service, service, StringComparison.OrdinalIgnoreCase));
}

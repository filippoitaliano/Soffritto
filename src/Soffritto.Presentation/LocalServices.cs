using Soffritto.Core;
using Soffritto.Core.Configuration;

namespace Soffritto.Presentation;

/// <summary>
/// The services of a repository of a local environment, by type: Kestrel for the backend, the dev
/// server for the frontend, the app and its bundler (Metro) for the app; a generic repository has
/// none. Consoles are marked with the repository key, so the app, the VSIX and the MCP server
/// recognise each other's (see <see cref="ConsoleServices"/>).
/// </summary>
public sealed class LocalServices
{
    private LocalServices(EnvironmentRepository repository)
    {
        Repository = repository;
        var key = repository.Key;
        var path = repository.Path;
        switch (repository.Repository)
        {
            case BackendConfiguration backend:
                Backend = new BackendTools(path, backend, key);
                Name = "Kestrel";
                break;

            case FrontendConfiguration frontend:
                ServiceMarker = ConsoleServices.Marker("frontend", key);
                IsService = p => RepoProcesses.IsFrontend(p, path, frontend);
                StartCommand = frontend.StartCommand;
                Name = "dev server";
                break;

            case AppRepoConfiguration app:
                ServiceMarker = ConsoleServices.Marker("app", key);
                MetroMarker = ConsoleServices.Marker("metro", key);
                IsService = p => RepoProcesses.IsApp(p, path);
                IsMetro = p => RepoProcesses.IsMetro(p, path);
                StartCommand = app.StartCommand;
                BundlerCommand = app.BundlerCommand;
                Name = "app";
                break;
        }
    }

    public static LocalServices Of(EnvironmentRepository repository) => new(repository);

    public EnvironmentRepository Repository { get; }

    public BackendTools? Backend { get; }

    public string ServiceMarker { get; } = string.Empty;

    public Func<ProcessRow, bool> IsService { get; } = _ => false;

    public string MetroMarker { get; } = string.Empty;

    public Func<ProcessRow, bool> IsMetro { get; } = _ => false;

    public string StartCommand { get; } = string.Empty;

    public string BundlerCommand { get; } = string.Empty;

    /// <summary>The service's name (Kestrel, dev server, app); empty for a generic repository.</summary>
    public string Name { get; } = string.Empty;

    public bool HasService => Name.Length > 0;

    public bool HasBundler => BundlerCommand.Length > 0;

    /// <summary>Primary repositories keep their usual names; for the others the id says which repository it is.</summary>
    public string Label(string service) => Repository.IsPrimary ? service : $"{service} ({Repository.Id})";

    public ServiceState State(IReadOnlyList<ProcessRow> scan)
        => Backend is not null
            ? Backend.Evaluate(scan, null, false, string.Empty).Kestrel
            : HasService ? ConsoleServices.StateOf(scan, ServiceMarker, IsService) : ServiceState.Stopped;

    public ServiceState MetroState(IReadOnlyList<ProcessRow> scan)
        => MetroMarker.Length > 0 ? ConsoleServices.StateOf(scan, MetroMarker, IsMetro) : ServiceState.Stopped;

    /// <summary>Starts the service in a detached console; returns the line to show.</summary>
    public string Start()
    {
        if (Backend is not null)
        {
            return Backend.StartKestrel();
        }

        if (!HasService)
        {
            throw new InvalidOperationException($"{Repository.Id} is a generic repository: it has no service to start");
        }

        return Started(ConsoleServices.StartConsole(Repository.Path, ServiceMarker, StartCommand), $"{StartCommand} started");
    }

    public string StartBundler()
    {
        if (!HasBundler)
        {
            throw new InvalidOperationException($"{Repository.Id} has no bundler command");
        }

        return Started(ConsoleServices.StartConsole(Repository.Path, MetroMarker, BundlerCommand), $"{BundlerCommand} (bundler) started");
    }

    /// <summary>Stops the service (for the app also its bundler), from the given process snapshot.</summary>
    public string Stop(IReadOnlyList<ProcessRow> scan)
    {
        if (Backend is not null)
        {
            return Backend.StopKestrel(scan);
        }

        if (MetroMarker.Length > 0)
        {
            ConsoleServices.Stop(ConsoleServices.Pids(scan, MetroMarker, IsMetro));
        }

        ConsoleServices.Stop(ConsoleServices.Pids(scan, ServiceMarker, IsService));
        return $"{Name} stopped";
    }

    public string StopBundler(IReadOnlyList<ProcessRow> scan)
    {
        ConsoleServices.Stop(ConsoleServices.Pids(scan, MetroMarker, IsMetro));
        return "Metro stopped";
    }

    /// <summary>
    /// The start of a local environment, repository by repository in column order: Kestrel and the
    /// dev servers that are off. The app is skipped, heavy and often not needed.
    /// </summary>
    public static List<(string Name, Action Act)> StackStarts(IEnumerable<LocalServices> services, IReadOnlyList<ProcessRow> scan)
    {
        var starts = new List<(string Name, Action Act)>();
        foreach (var s in services)
        {
            if ((s.Backend is not null || s.Repository.Type == RepositoryType.Frontend) && s.State(scan) == ServiceState.Stopped)
            {
                starts.Add((s.Label(s.Name), () => s.Start()));
            }
        }

        return starts;
    }

    /// <summary>The stop of a local environment: everything running, app and bundler included.</summary>
    public static List<(string Name, Action Act)> StackStops(IEnumerable<LocalServices> services, IReadOnlyList<ProcessRow> scan)
    {
        var stops = new List<(string Name, Action Act)>();
        foreach (var s in services)
        {
            if (s.Backend is not null)
            {
                if (s.State(scan) != ServiceState.Stopped)
                {
                    stops.Add((s.Label(s.Name), () => s.Backend.StopKestrel(scan)));
                }

                continue;
            }

            if (s.HasService && s.State(scan) != ServiceState.Stopped)
            {
                stops.Add((s.Label(s.Name), () => ConsoleServices.Stop(ConsoleServices.Pids(scan, s.ServiceMarker, s.IsService))));
            }

            if (s.MetroState(scan) != ServiceState.Stopped)
            {
                stops.Add((s.Label("Metro"), () => ConsoleServices.Stop(ConsoleServices.Pids(scan, s.MetroMarker, s.IsMetro))));
            }
        }

        return stops;
    }

    /// <summary>Runs the steps; a failing one does not stop the others. Returns the line to show and how many failed.</summary>
    public static (string Line, int Failed) Execute(string verb, List<(string Name, Action Act)> steps)
    {
        if (steps.Count == 0)
        {
            return ($"nothing to do: no service to {(verb == "started" ? "start" : "stop")}", 0);
        }

        var done = new List<string>();
        var failed = new List<string>();
        foreach (var (name, act) in steps)
        {
            try
            {
                act();
                done.Add(name);
            }
            catch (Exception ex)
            {
                failed.Add($"{name} ({ex.Message})");
            }
        }

        var line = done.Count > 0 ? $"{verb}: {string.Join(", ", done)}" : string.Empty;
        return (failed.Count > 0 ? $"{line}{(line.Length > 0 ? "; " : string.Empty)}failed: {string.Join(", ", failed)}" : line, failed.Count);
    }

    /// <summary>The API process names to pass to <see cref="ProcessTable.Read"/> so Kestrel is recognised.</summary>
    public static IReadOnlyList<string> ApiProcesses(IEnumerable<EnvironmentRepository> repositories)
        => repositories.Select(r => r.Repository).OfType<BackendConfiguration>().Select(b => b.ApiProcess).ToList();

    private static string Started(bool detached, string notice)
        => detached ? notice : $"{notice} inside the host's job: it will close along with the host";
}

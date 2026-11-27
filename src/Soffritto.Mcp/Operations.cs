using System.Diagnostics;
using System.Text.Json;
using Soffritto.Core;
using Soffritto.Core.Configuration;
using Soffritto.Presentation;

namespace Soffritto.Mcp;

internal sealed record OperationRequest(string Id, string Kind, string Label, int Environment, JsonElement Args, DateTimeOffset Started);

internal sealed record OperationWorkerInfo(int Pid, DateTime ProcessStart);

internal sealed record OperationOutcome(string? Error, JsonElement? Result, DateTimeOffset Finished);

/// <summary>
/// A long operation on an environment (create, start, migrations, pull…), recorded in the data
/// folder (operations\&lt;id&gt;: request, log, outcome) and run by a worker process detached from the
/// MCP server: it survives the server and the client session, and any server reads its state.
/// </summary>
internal sealed class OperationView
{
    private const string RequestFile = "request.json";
    private const string WorkerFile = "worker.json";
    private const string LogFile = "log.txt";
    private const string OutcomeFile = "outcome.json";

    // Between the launch and the worker's first write: a worker that never shows up within this
    // time did not start.
    private static readonly TimeSpan StartGrace = TimeSpan.FromSeconds(60);

    private OperationView(string folder, OperationRequest request)
    {
        Folder = folder;
        Request = request;
    }

    public string Folder { get; }

    public OperationRequest Request { get; }

    public string Id => Request.Id;

    public string Kind => Request.Label;

    public int Environment => Request.Environment;

    public static string RequestPath(string folder) => Path.Combine(folder, RequestFile);

    public static string WorkerPath(string folder) => Path.Combine(folder, WorkerFile);

    public static string LogPath(string folder) => Path.Combine(folder, LogFile);

    public static string OutcomePath(string folder) => Path.Combine(folder, OutcomeFile);

    public static OperationView? Read(string folder)
        => Operations.ReadJson<OperationRequest>(RequestPath(folder)) is { } request ? new OperationView(folder, request) : null;

    public OperationOutcome? Outcome => Operations.ReadJson<OperationOutcome>(OutcomePath(Folder));

    /// <summary>Running, or the reason it ended without an outcome (null if it has one or is running).</summary>
    public (bool Running, string? Lost) State()
    {
        if (File.Exists(OutcomePath(Folder)))
        {
            return (false, null);
        }

        if (Operations.ReadJson<OperationWorkerInfo>(WorkerPath(Folder)) is not { } worker)
        {
            return DateTimeOffset.Now - Request.Started < StartGrace
                ? (true, null)
                : (false, "the worker process never started");
        }

        if (IsAlive(worker))
        {
            return (true, null);
        }

        // The worker may have written the outcome between the two reads.
        return File.Exists(OutcomePath(Folder))
            ? (false, null)
            : (false, "the worker process ended without an outcome (killed, or the machine restarted)");
    }

    public bool IsRunning => State().Running;

    public object Snapshot(int lines)
    {
        var (running, lost) = State();
        var outcome = running ? null : Outcome;
        var error = lost ?? outcome?.Error;
        var finished = outcome?.Finished ?? (running ? (DateTimeOffset?)null : File.GetLastWriteTime(LogPath(Folder)));
        return new
        {
            operation = Id,
            kind = Kind,
            environment = Environment,
            status = running ? "running" : error is null ? "succeeded" : "failed",
            started = Request.Started.ToString("HH:mm:ss"),
            durationSeconds = (int)((finished ?? DateTimeOffset.Now) - Request.Started).TotalSeconds,
            error,
            result = outcome?.Result,
            log = Tail(lines),
            nextStep = running ? $"call operation_status with operation = \"{Id}\" to follow it" : null,
        };
    }

    private List<string> Tail(int lines)
    {
        var path = LogPath(Folder);
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var all = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
            return all.Skip(Math.Max(0, all.Count - lines)).ToList();
        }
        catch (IOException)
        {
            return [];
        }
    }

    private static bool IsAlive(OperationWorkerInfo worker)
    {
        try
        {
            using var process = Process.GetProcessById(worker.Pid);
            return !process.HasExited && Math.Abs((process.StartTime - worker.ProcessStart).TotalSeconds) < 2;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}

/// <summary>The operations of every server and worker on this machine: at most one running per environment.</summary>
internal static class Operations
{
    public const int DefaultLogLines = 40;

    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string Root => Path.Combine(ConfigurationFiles.DataFolder, "operations");

    private static string LockPath => Path.Combine(ConfigurationFiles.DataFolder, "operations.lock");

    public static OperationView? Find(string id)
        => id.Length > 0 && id.All(char.IsAsciiLetterOrDigit) && Directory.Exists(Path.Combine(Root, id))
            ? OperationView.Read(Path.Combine(Root, id))
            : null;

    public static OperationView? RunningOn(int environment)
        => All().FirstOrDefault(o => o.Environment == environment && o.IsRunning);

    /// <summary>
    /// Records the operation, launches its worker and waits up to <paramref name="waitSeconds"/>:
    /// if it finishes in time the response carries the result, otherwise the operation to follow.
    /// </summary>
    public static async Task<object> StartAsync(string kind, string label, int environment, object args, int waitSeconds, CancellationToken requestToken)
    {
        string folder;
        using (await CrossProcessLock.AcquireAsync(LockPath, TimeSpan.FromSeconds(10), requestToken).ConfigureAwait(false))
        {
            if (RunningOn(environment) is { } busy)
            {
                return new
                {
                    error = $"operation {busy.Kind} ({busy.Id}) is already running on environment {environment}: wait for it to finish",
                    operation = busy.Snapshot(DefaultLogLines),
                };
            }

            var id = Guid.NewGuid().ToString("N")[..12];
            folder = Path.Combine(Root, id);
            Directory.CreateDirectory(folder);
            WriteJson(OperationView.RequestPath(folder), new OperationRequest(id, kind, label, environment, JsonSerializer.SerializeToElement(args, Json), DateTimeOffset.Now));
        }

        try
        {
            Launch(Path.GetFileName(folder));
        }
        catch (Exception ex)
        {
            WriteJson(OperationView.OutcomePath(folder), new OperationOutcome($"worker not started: {ex.Message}", null, DateTimeOffset.Now));
        }

        var operation = OperationView.Read(folder)!;
        await WaitAsync(operation, waitSeconds, requestToken).ConfigureAwait(false);
        return operation.Snapshot(DefaultLogLines);
    }

    /// <summary>Waits for the operation to finish, up to <paramref name="waitSeconds"/> (0-110).</summary>
    public static async Task WaitAsync(OperationView operation, int waitSeconds, CancellationToken ct)
    {
        var deadline = DateTimeOffset.Now + TimeSpan.FromSeconds(Math.Clamp(waitSeconds, 0, 110));
        try
        {
            while (operation.IsRunning && DateTimeOffset.Now < deadline)
            {
                await Task.Delay(300, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Removes the operations finished more than a week ago.</summary>
    public static void Prune()
    {
        try
        {
            foreach (var operation in All().Where(o => DateTimeOffset.Now - o.Request.Started > Retention && !o.IsRunning))
            {
                Directory.Delete(operation.Folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
        }
    }

    public static T? ReadJson<T>(string path)
        where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Written aside and renamed: a reader never sees half a file.
    public static void WriteJson<T>(string path, T value)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json));
        File.Move(temporary, path, overwrite: true);
    }

    private static IEnumerable<OperationView> All()
    {
        if (!Directory.Exists(Root))
        {
            yield break;
        }

        foreach (var folder in Directory.EnumerateDirectories(Root))
        {
            if (OperationView.Read(folder) is { } operation)
            {
                yield return operation;
            }
        }
    }

    // The worker is the server's own exe in worker mode, started by an intermediate cmd that exits
    // at once (like ConsoleServices.StartConsole): it is left without a live parent, so the client
    // that kills the server with its whole process tree does not reach it, and it starts outside
    // the client's job when Windows allows it.
    private static void Launch(string id)
    {
        var exe = System.Environment.ProcessPath ?? throw new InvalidOperationException("path of the server exe unknown");
        DetachedLauncher.Start(
            $"cmd.exe /d /s /c \"start \"\" /b \"{exe}\" {OperationWorker.Argument} {id}\"",
            AppContext.BaseDirectory,
            newConsole: false,
            hidden: true);
    }
}

/// <summary>The worker mode of the server exe: runs one recorded operation and writes its outcome.</summary>
internal static class OperationWorker
{
    public const string Argument = "--operation";

    public static async Task<int> RunAsync(string id)
    {
        if (ConfigurationFiles.DataFolderProblem is not null || Operations.Find(id) is not { } operation)
        {
            return 2;
        }

        var folder = operation.Folder;
        using (var self = Process.GetCurrentProcess())
        {
            Operations.WriteJson(OperationView.WorkerPath(folder), new OperationWorkerInfo(self.Id, self.StartTime));
        }

        var gate = new object();
        await using var stream = new FileStream(OperationView.LogPath(folder), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        await using var writer = new StreamWriter(stream) { AutoFlush = true };
        void Log(string line)
        {
            lock (gate)
            {
                foreach (var part in line.Split('\n'))
                {
                    writer.WriteLine($"{DateTimeOffset.Now:HH:mm:ss} {part.TrimEnd('\r')}");
                }
            }
        }

        string? error;
        object? result;
        try
        {
            (error, result) = await OperationCatalog.RunAsync(operation.Request, Log, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            (error, result) = ($"{ex.GetType().Name}: {ex.Message}", null);
        }

        Operations.WriteJson(
            OperationView.OutcomePath(folder),
            new OperationOutcome(error, result is null ? null : JsonSerializer.SerializeToElement(result, Operations.Json), DateTimeOffset.Now));
        return 0;
    }
}

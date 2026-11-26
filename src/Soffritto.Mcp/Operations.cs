using System.Collections.Concurrent;

namespace Soffritto.Mcp;

/// <summary>
/// A long operation on an environment (create, start, migrations, destroy…): it runs in the
/// background in the server process, outlives the call that started it and is read with
/// operation_status.
/// </summary>
internal sealed class Operation
{
    private const int MaxLogLines = 400;

    private readonly object gate = new();
    private readonly List<string> log = [];
    private string? error;
    private object? result;
    private DateTimeOffset? finished;

    public Operation(string kind, int environment)
    {
        Kind = kind;
        Environment = environment;
    }

    public string Id { get; } = Guid.NewGuid().ToString("N")[..12];

    public string Kind { get; }

    public int Environment { get; }

    public DateTimeOffset Started { get; } = DateTimeOffset.Now;

    public Task Completion { get; set; } = Task.CompletedTask;

    public bool IsRunning
    {
        get
        {
            lock (gate)
            {
                return finished is null;
            }
        }
    }

    public void Log(string line)
    {
        lock (gate)
        {
            log.Add($"{DateTimeOffset.Now:HH:mm:ss} {line}");
            if (log.Count > MaxLogLines)
            {
                log.RemoveRange(0, log.Count - MaxLogLines);
            }
        }
    }

    public void Finish(string? failure, object? outcome)
    {
        lock (gate)
        {
            error = failure;
            result = outcome;
            finished = DateTimeOffset.Now;
        }
    }

    public object Snapshot(int lines)
    {
        lock (gate)
        {
            return new
            {
                operation = Id,
                kind = Kind,
                environment = Environment,
                status = finished is null ? "running" : error is null ? "succeeded" : "failed",
                started = Started.ToString("HH:mm:ss"),
                durationSeconds = (int)((finished ?? DateTimeOffset.Now) - Started).TotalSeconds,
                error,
                result,
                log = log.Skip(Math.Max(0, log.Count - lines)).ToList(),
                nextStep = finished is null ? $"call operation_status with operation = \"{Id}\" to follow it" : null,
            };
        }
    }
}

/// <summary>The operations of this process: at most one running per environment.</summary>
internal static class Operations
{
    private const int DefaultLogLines = 40;

    private static readonly ConcurrentDictionary<string, Operation> All = new();

    // Operations do not depend on the call that starts them: they stop only with the process.
    private static readonly CancellationTokenSource Lifetime = new();

    public static Operation? Find(string id) => All.TryGetValue(id, out var operation) ? operation : null;

    public static Operation? RunningOn(int environment) => All.Values.FirstOrDefault(o => o.Environment == environment && o.IsRunning);

    /// <summary>
    /// Starts <paramref name="work"/> in the background and waits up to <paramref name="waitSeconds"/>:
    /// if it finishes in time the response carries the result, otherwise the operation to follow. The
    /// work returns the error (null on success) and a result to show.
    /// </summary>
    public static async Task<object> RunAsync(
        string kind,
        int environment,
        int waitSeconds,
        Func<Operation, CancellationToken, Task<(string? Error, object? Result)>> work,
        CancellationToken requestToken)
    {
        if (RunningOn(environment) is { } busy)
        {
            return new
            {
                error = $"operation {busy.Kind} ({busy.Id}) is already running on environment {environment}: wait for it to finish",
                operation = busy.Snapshot(DefaultLogLines),
            };
        }

        var operation = new Operation(kind, environment);
        All[operation.Id] = operation;
        operation.Completion = Task.Run(async () =>
        {
            try
            {
                var (error, result) = await work(operation, Lifetime.Token).ConfigureAwait(false);
                operation.Finish(error, result);
            }
            catch (Exception ex)
            {
                operation.Finish($"{ex.GetType().Name}: {ex.Message}", null);
            }
        });

        var wait = TimeSpan.FromSeconds(Math.Clamp(waitSeconds, 0, 110));
        try
        {
            await Task.WhenAny(operation.Completion, Task.Delay(wait, requestToken)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        return operation.Snapshot(DefaultLogLines);
    }
}

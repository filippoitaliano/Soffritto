using System.Collections.Concurrent;

namespace Soffritto.Mcp;

/// <summary>
/// Un'operazione lunga su un ambiente (creazione, avvio, migrazioni, distruzione…): gira in
/// background nel processo del server, sopravvive alla chiamata che l'ha avviata e si legge con
/// stato_operazione.
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
                operazione = Id,
                tipo = Kind,
                ambiente = Environment,
                stato = finished is null ? "in corso" : error is null ? "riuscita" : "fallita",
                avviata = Started.ToString("HH:mm:ss"),
                durataSecondi = (int)((finished ?? DateTimeOffset.Now) - Started).TotalSeconds,
                errore = error,
                esito = result,
                log = log.Skip(Math.Max(0, log.Count - lines)).ToList(),
                prossimoPasso = finished is null ? $"chiama stato_operazione con operazione = \"{Id}\" per seguirla" : null,
            };
        }
    }
}

/// <summary>Le operazioni di questo processo: una sola in corso per ambiente.</summary>
internal static class Operations
{
    private const int DefaultLogLines = 40;

    private static readonly ConcurrentDictionary<string, Operation> All = new();

    // Le operazioni non dipendono dalla chiamata che le avvia: si fermano solo con il processo.
    private static readonly CancellationTokenSource Lifetime = new();

    public static Operation? Find(string id) => All.TryGetValue(id, out var operation) ? operation : null;

    public static Operation? RunningOn(int environment) => All.Values.FirstOrDefault(o => o.Environment == environment && o.IsRunning);

    /// <summary>
    /// Avvia <paramref name="work"/> in background e aspetta fino a <paramref name="waitSeconds"/>:
    /// se finisce prima risponde con l'esito, altrimenti con l'operazione da seguire. Il lavoro
    /// restituisce l'errore (null se riuscito) e un esito da mostrare.
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
                errore = $"sull'ambiente {environment} e' gia' in corso l'operazione {busy.Kind} ({busy.Id}): aspetta che finisca",
                operazione = busy.Snapshot(DefaultLogLines),
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

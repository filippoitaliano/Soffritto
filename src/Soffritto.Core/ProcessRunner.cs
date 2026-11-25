using System.Diagnostics;
using System.Text;

namespace Soffritto.Core;

public sealed record ProcessOutcome(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut,
    string? StartError = null)
{
    public bool Success => StartError is null && !TimedOut && ExitCode == 0;

    /// <summary>Ultime righe non vuote di stderr e stdout, per un messaggio d'errore leggibile.</summary>
    public string Tail(int maxLines = 6)
    {
        var lines = (StandardError + "\n" + StandardOutput)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Trim().Length > 0)
            .ToList();

        if (StartError is not null)
        {
            return StartError;
        }

        if (TimedOut)
        {
            return "Timeout: il comando non e' terminato entro il limite.";
        }

        return lines.Count <= maxLines
            ? string.Join(Environment.NewLine, lines)
            : string.Join(Environment.NewLine, lines.Skip(lines.Count - maxLines));
    }

    /// <summary>
    /// Righe <c>error:</c>/<c>fatal:</c> di git, che non stanno per forza in fondo: il fetch con
    /// <c>--prune</c> stampa l'errore prima dell'elenco dei <c>[deleted]</c>. Senza righe del
    /// genere ripiega su <see cref="Tail"/>.
    /// </summary>
    public string GitError(int maxLines = 2)
    {
        if (StartError is not null || TimedOut)
        {
            return Tail(maxLines);
        }

        var errors = (StandardError + "\n" + StandardOutput)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("error:", StringComparison.OrdinalIgnoreCase)
                || l.StartsWith("fatal:", StringComparison.OrdinalIgnoreCase))
            .Take(maxLines)
            .ToList();

        return errors.Count > 0 ? string.Join(Environment.NewLine, errors) : Tail(maxLines);
    }

    /// <summary>
    /// Righe <c>error</c> di MSBuild e di dotnet (<c>file(riga,col): error CS1002: …</c>, <c>error NU1101: …</c>)
    /// e righe di eccezione (<c>Microsoft.Data.SqlClient.SqlException (0x80131904): …</c>), senza
    /// doppioni: in fondo all'output ci sono avvisi, riepilogo o stack, non l'errore. Senza righe
    /// del genere ripiega su <see cref="Tail"/>.
    /// </summary>
    public string BuildError(int maxLines = 4)
    {
        if (StartError is not null || TimedOut)
        {
            return Tail(maxLines);
        }

        var errors = (StandardError + "\n" + StandardOutput)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => BuildErrorLine.IsMatch(l))
            .Distinct(StringComparer.Ordinal)
            .Take(maxLines)
            .ToList();

        return errors.Count > 0 ? string.Join(Environment.NewLine, errors) : Tail(maxLines);
    }

    private static readonly System.Text.RegularExpressions.Regex BuildErrorLine =
        new(@"(^|:\s)error\s+[A-Z]+\d+\s*:|^[\w.]+Exception(\s\(0x[0-9A-F]+\))?:", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
}

public static class ProcessRunner
{
    public static async Task<ProcessOutcome> RunAsync(
        string fileName,
        string arguments,
        string workingDirectory,
        int timeoutSeconds,
        IReadOnlyDictionary<string, string>? environment = null,
        Action<string>? onOutputLine = null,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo(fileName, arguments)
        {
            WorkingDirectory = workingDirectory,
            // Uno stdin proprio, chiuso subito: ereditato da un host che lo sta leggendo (il server
            // MCP, che ci riceve JSON-RPC) blocca git all'avvio fino al timeout.
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (environment is not null)
        {
            foreach (var pair in environment)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        try
        {
            using var process = new Process { StartInfo = startInfo };

            process.OutputDataReceived += (_, e) => Capture(stdout, e.Data, onOutputLine);
            process.ErrorDataReceived += (_, e) => Capture(stderr, e.Data, onOutputLine);

            if (!process.Start())
            {
                return new ProcessOutcome(-1, string.Empty, string.Empty, false, $"Impossibile avviare {fileName}.");
            }

            process.StandardInput.Close();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Anche su cancellazione esterna il processo non deve restare orfano: senza kill
                // un devtunnel o un fetch continuerebbero in background a finestra chiusa.
                TryKill(process);

                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                return new ProcessOutcome(-1, stdout.ToString(), stderr.ToString(), true);
            }

            return new ProcessOutcome(process.ExitCode, stdout.ToString(), stderr.ToString(), false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ProcessOutcome(-1, stdout.ToString(), stderr.ToString(), false, ex.Message);
        }
    }

    private static void Capture(StringBuilder buffer, string? line, Action<string>? onOutputLine)
    {
        if (line is null)
        {
            return;
        }

        lock (buffer)
        {
            buffer.AppendLine(line);
        }

        if (line.Trim().Length > 0)
        {
            onOutputLine?.Invoke(line.Trim());
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Il processo puo' essere gia' terminato tra il timeout e la kill.
        }
    }
}

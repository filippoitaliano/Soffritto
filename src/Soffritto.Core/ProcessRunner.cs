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

    /// <summary>The last non-empty lines of stderr and stdout, for a readable error message.</summary>
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
            return "Timeout: the command did not finish within the time limit.";
        }

        return lines.Count <= maxLines
            ? string.Join(Environment.NewLine, lines)
            : string.Join(Environment.NewLine, lines.Skip(lines.Count - maxLines));
    }

    /// <summary>
    /// git's <c>error:</c>/<c>fatal:</c> lines, which are not necessarily at the end: fetch with
    /// <c>--prune</c> prints the error before the list of <c>[deleted]</c> refs. Without such
    /// lines it falls back to <see cref="Tail"/>.
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
    /// MSBuild and dotnet <c>error</c> lines (<c>file(line,col): error CS1002: …</c>, <c>error NU1101: …</c>)
    /// and exception lines (<c>Microsoft.Data.SqlClient.SqlException (0x80131904): …</c>), without
    /// duplicates: the end of the output holds warnings, a summary or a stack, not the error. Without such
    /// lines it falls back to <see cref="Tail"/>.
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
            // A stdin of its own, closed immediately: inherited from a host that is reading it (the MCP
            // server, which receives JSON-RPC on it) it blocks git at startup until the timeout.
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
                return new ProcessOutcome(-1, string.Empty, string.Empty, false, $"Could not start {fileName}.");
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
                // Even on external cancellation the process must not be orphaned: without the kill
                // a devtunnel or a fetch would keep running in the background after the window closes.
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
            // The process may already have exited between the timeout and the kill.
        }
    }
}

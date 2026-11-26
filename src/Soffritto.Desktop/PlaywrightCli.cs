using System.Diagnostics;
using System.IO;
using System.Text;

namespace Soffritto.Desktop;

/// <summary>Result of a playwright-cli command: exit code and standard output.</summary>
public sealed record PlaywrightCliResult(int ExitCode, string Output);

/// <summary>
/// Runs playwright-cli with the same environment as the dashboard: the PATH refreshed from the
/// registry and the LOCALAPPDATA of the browser registry, without which <c>-s=&lt;session&gt;</c>
/// cannot find the browsers opened by an MSIX app.
/// </summary>
public static class PlaywrightCli
{
    public static bool IsInstalled(string path) => IsOnPath("playwright-cli", path);

    /// <summary>Runs <c>playwright-cli</c> with the given arguments; null if it fails to start or does not finish in time.</summary>
    public static async Task<PlaywrightCliResult?> RunAsync(string localAppData, IEnumerable<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var path = CurrentPath();
        if (!IsInstalled(path))
        {
            return null;
        }

        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            WorkingDirectory = Path.GetTempPath(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("playwright-cli");
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["LOCALAPPDATA"] = localAppData;
        startInfo.Environment["PATH"] = path;

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception)
        {
            return null;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        try
        {
            var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var errors = process.StandardError.ReadToEndAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            await errors.ConfigureAwait(false);
            return new PlaywrightCliResult(process.ExitCode, await output.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Already exited between the timeout and the kill.
            }

            return null;
        }
    }

    // Explorer keeps the environment it started with: an exe launched from Start does not see PATH
    // entries added later (here C:\Program Files\nodejs, without which playwright-cli.cmd cannot
    // find node). The system and user entries read from the registry are appended to the inherited ones.
    public static string CurrentPath()
    {
        var entries = new List<string>();
        foreach (var source in new[]
        {
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
        })
        {
            foreach (var entry in (source ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!entries.Contains(entry, StringComparer.OrdinalIgnoreCase))
                {
                    entries.Add(entry);
                }
            }
        }

        return string.Join(';', entries);
    }

    private static bool IsOnPath(string command, string path)
    {
        var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries);

        foreach (var directory in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (extensions.Any(extension => File.Exists(Path.Combine(directory.Trim('"'), command + extension))))
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                // PATH entry with invalid characters: move on to the next one.
            }
        }

        return false;
    }
}

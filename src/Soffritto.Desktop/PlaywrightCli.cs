using System.Diagnostics;
using System.IO;
using System.Text;

namespace Soffritto.Desktop;

/// <summary>Esito di un comando di playwright-cli: codice di uscita e output standard.</summary>
public sealed record PlaywrightCliResult(int ExitCode, string Output);

/// <summary>
/// Lancia playwright-cli con lo stesso ambiente della dashboard: il PATH aggiornato dal registro e
/// la LOCALAPPDATA del registro dei browser, senza la quale <c>-s=&lt;sessione&gt;</c> non trova i
/// browser aperti da un'app MSIX.
/// </summary>
public static class PlaywrightCli
{
    public static bool IsInstalled(string path) => IsOnPath("playwright-cli", path);

    /// <summary>Esegue <c>playwright-cli</c> con gli argomenti dati; null se non parte o non finisce in tempo.</summary>
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
                // Gia' uscito tra il timeout e la chiusura.
            }

            return null;
        }
    }

    // Explorer tiene l'ambiente di quando e' partito: un exe aperto da Start non vede le voci del
    // PATH aggiunte dopo (qui C:\Program Files\nodejs, senza cui playwright-cli.cmd non trova node).
    // Si aggiungono quelle di sistema e utente lette dal registro, in coda a quelle ereditate.
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
                // Voce del PATH con caratteri non validi: si passa alla successiva.
            }
        }

        return false;
    }
}

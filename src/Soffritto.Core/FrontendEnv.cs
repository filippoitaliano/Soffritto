using System.Text.RegularExpressions;
using Soffritto.Core.Configuration;
using Soffritto.Core.Env;

namespace Soffritto.Core;

/// <summary>
/// Il file di env di un clone del frontend (righe "set NOME=valore" di un .bat o "NOME=valore" di
/// un .env): si legge e basta, non si modifica.
/// </summary>
public static class FrontendEnv
{
    /// <summary>La porta del dev server dalla variabile configurata; il default configurato se manca.</summary>
    public static int ReadDevServerPort(string frontendPath, FrontendConfiguration settings)
    {
        if (settings.EnvFile.Length > 0 && settings.DevServerPortVariable.Length > 0)
        {
            var line = new Regex(
                $@"^\s*(@?set\s+)?{Regex.Escape(settings.DevServerPortVariable)}\s*=\s*(\d+)\s*$",
                RegexOptions.IgnoreCase);

            try
            {
                foreach (var text in File.ReadLines(Path.Combine(frontendPath, settings.EnvFile)))
                {
                    if (line.Match(text) is { Success: true } match)
                    {
                        return int.Parse(match.Groups[2].Value);
                    }
                }
            }
            catch
            {
                // File di env assente o illeggibile: vale il default del dev server.
            }
        }

        return settings.DevServerDefaultPort;
    }

    /// <summary>Tutte le variabili del file di env, nell'ordine in cui compaiono; vuoto se il file non c'e'.</summary>
    public static IReadOnlyDictionary<string, string> ReadVariables(string frontendPath, FrontendConfiguration settings)
    {
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (settings.EnvFile.Length == 0)
        {
            return variables;
        }

        try
        {
            foreach (var (name, value) in BatEnvAdapter.ReadLines(Path.Combine(frontendPath, settings.EnvFile)))
            {
                variables[name] = value;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return variables;
    }
}

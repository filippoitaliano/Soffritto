using System.Text.RegularExpressions;
using Soffritto.Core.Configuration;
using Soffritto.Core.Env;

namespace Soffritto.Core;

/// <summary>
/// The env file of a frontend clone ("set NAME=value" lines of a .bat or "NAME=value" lines of
/// a .env): read only, never modified.
/// </summary>
public static class FrontendEnv
{
    /// <summary>The dev server port from the configured variable; the configured default if it is missing.</summary>
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
                // Env file missing or unreadable: the dev server default applies.
            }
        }

        return settings.DevServerDefaultPort;
    }

    /// <summary>All the variables of the env file, in the order they appear; empty if the file does not exist.</summary>
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

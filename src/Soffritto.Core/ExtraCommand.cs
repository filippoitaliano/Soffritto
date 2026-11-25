using Soffritto.Core.Configuration;

namespace Soffritto.Core;

/// <summary>
/// Il comando in piu' di un repository (<see cref="FrontendConfiguration.ExtraCommand"/>): lo stesso
/// per il bottone dell'exe e per lo strumento MCP.
/// </summary>
public static class ExtraCommand
{
    /// <summary>Il comando configurato del repository; null se non ne ha uno.</summary>
    public static string? Of(RepositoryConfiguration repository)
        => repository is FrontendConfiguration { ExtraCommand: var extra } && extra.Trim() is { Length: > 0 } trimmed ? trimmed : null;

    /// <summary>L'ultima parola del comando, che da' il nome al bottone (npm run genapi -> genapi).</summary>
    public static string Name(string command) => command.Split(' ', StringSplitOptions.RemoveEmptyEntries)[^1];

    /// <summary>
    /// La riga da passare a cmd.exe. In un worktree Docker le dipendenze stanno nel container: senza
    /// node_modules nel worktree prima si installano.
    /// </summary>
    public static string CommandLine(string command, EnvironmentKind kind)
        => kind == EnvironmentKind.Docker ? $"if exist node_modules\\ ({command}) else (npm ci && {command})" : command;
}

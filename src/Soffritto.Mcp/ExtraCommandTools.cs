using System.ComponentModel;
using ModelContextProtocol.Server;
using Soffritto.Core;
using Soffritto.Core.Configuration;

namespace Soffritto.Mcp;

/// <summary>
/// Il comando in piu' di un repository (il bottone accessorio della card nell'exe), su ambienti
/// locali e Docker: e' l'unico strumento che scrive nei cloni di un ambiente locale.
/// </summary>
[McpServerToolType]
public static class ExtraCommandTools
{
    private const int TimeoutSeconds = 900;

    [McpServerTool(Name = "esegui_comando_extra", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Esegue il comando in piu' di un repository dell'ambiente (il campo extraCommand della configurazione, lo stesso del bottone accessorio della card nell'exe; elenca_ambienti lo mostra come comandoExtra), nella cartella del repository: il clone per un ambiente locale, il worktree per un ambiente Docker (dove, senza node_modules, prima installa le dipendenze). Vale anche sugli ambienti locali e puo' riscrivere file del repository. Risponde con l'esito e l'output; se dura di piu' dell'attesa, con l'operazione da seguire con stato_operazione.")]
    public static async Task<string> EseguiComandoExtra(
        [Description("Numero dell'ambiente, locale o Docker.")] int numero,
        [Description("Id del repository nel catalogo (vedi elenca_ambienti); vuoto = l'unico repository dell'ambiente che ha un comando extra.")] string? repository = null,
        [Description("Secondi da aspettare la fine prima di rispondere (0-110): se il comando dura di piu' la risposta contiene l'id da seguire con stato_operazione.")] int attesaSecondi = 90,
        CancellationToken cancellationToken = default)
    {
        if (EnvironmentTools.LoadConfiguration(out var error) is not { } configuration)
        {
            return EnvironmentTools.Serialize(new { errore = error });
        }

        if (configuration.Environment(numero) is not { } environment)
        {
            return EnvironmentTools.Serialize(new { errore = $"l'ambiente {numero} non e' configurato" });
        }

        var candidates = configuration.RepositoriesOf(environment)
            .Where(r => ExtraCommand.Of(r.Repository) is not null)
            .ToList();
        var target = string.IsNullOrWhiteSpace(repository)
            ? candidates.Count == 1 ? candidates[0] : null
            : candidates.FirstOrDefault(r => string.Equals(r.Id, repository, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            var available = candidates.Count == 0 ? "nessuno" : string.Join(", ", candidates.Select(r => r.Id));
            return EnvironmentTools.Serialize(new
            {
                errore = string.IsNullOrWhiteSpace(repository)
                    ? $"indica 'repository': repository dell'ambiente {numero} con un comando extra: {available}"
                    : $"{repository} non e' un repository dell'ambiente {numero} con un comando extra (con comando extra: {available})",
            });
        }

        if (!Directory.Exists(target.Path))
        {
            return EnvironmentTools.Serialize(new { errore = $"la cartella {target.Path} non esiste" });
        }

        var command = ExtraCommand.Of(target.Repository)!;
        // Il server eredita da Claude Code NoDefaultCurrentDirectoryInExePath: con quella variabile cmd
        // non cerca i comandi nella cartella corrente, e gli script che chiamano un .bat locale
        // (es. "cd envs && dev") falliscono, mentre dal bottone dell'exe funzionano.
        var commandLine = $"set \"NoDefaultCurrentDirectoryInExePath=\" && {ExtraCommand.CommandLine(command, environment.Kind)}";
        var snapshot = await Operations.RunAsync(ExtraCommand.Name(command), numero, attesaSecondi, async (operation, ct) =>
        {
            operation.Log($"{commandLine} in {target.Path}");
            var outcome = await ProcessRunner.RunAsync(
                "cmd.exe",
                $"/d /s /c \"{commandLine}\"",
                target.Path,
                TimeoutSeconds,
                onOutputLine: operation.Log,
                cancellationToken: ct).ConfigureAwait(false);
            return outcome.Success
                ? (null, new { esito = $"{command} riuscito", cartella = target.Path })
                : ($"{command} fallito (exit {outcome.ExitCode}): {outcome.Tail(10)}", null);
        }, cancellationToken).ConfigureAwait(false);
        return EnvironmentTools.Serialize(snapshot);
    }
}

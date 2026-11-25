using Soffritto.Core;
using Soffritto.Core.Configuration;

var solutionRoot = args.FirstOrDefault(a => !a.StartsWith('-')) ?? Directory.GetCurrentDirectory();
var fetch = args.Contains("--fetch", StringComparer.OrdinalIgnoreCase);

if (!Directory.Exists(solutionRoot))
{
    Console.Error.WriteLine($"Cartella inesistente: {solutionRoot}");
    return 2;
}

var configuration = ConfigurationFiles.TryLoad(out var configurationError);
if (configuration is null)
{
    Console.Error.WriteLine(configurationError ?? $"Configurazione assente ({ConfigurationFiles.ConfigPath}): compilala dal setup dell'exe Soffritto.");
    return 2;
}

Console.WriteLine($"Soluzione : {solutionRoot}");
Console.WriteLine($"Git fetch : {(fetch ? "si" : "no (usa --fetch)")}");
Console.WriteLine();

var tunnel = configuration.EnvironmentOfBackend(solutionRoot) is { } n ? Soffritto.Core.Docker.TunnelPool.AssignmentOf(n, configuration.Backend.Id) : null;
var report = await StatusProbe.RunAsync(solutionRoot, configuration.ToProbeOptions(), fetch, probeDevTunnel: fetch, tunnel: tunnel);

Write("DATABASE", ConsoleColor.Cyan);
if (report.ConfigurationError is not null)
{
    Write($"  ! {report.ConfigurationError}", ConsoleColor.Red);
}
else
{
    Console.WriteLine($"  Server   : {report.Server}");
    Console.WriteLine($"  Database : {report.Database}");
}

if (report.DatabaseError is not null)
{
    Write($"  ! {report.DatabaseError}", ConsoleColor.Red);
}

Console.WriteLine();

foreach (var context in report.Contexts)
{
    var pending = context.Pending;
    Write($"{context.ContextName}", ConsoleColor.Cyan);
    Console.WriteLine($"  Applicate : {context.AppliedCount} / {context.Entries.Count}");
    Console.WriteLine($"  Ultima    : {context.LastApplied ?? "(nessuna)"}");

    if (pending.Count == 0)
    {
        Write("  Allineato", ConsoleColor.Green);
    }
    else
    {
        Write($"  MANCANTI  : {pending.Count}", ConsoleColor.Yellow);
        foreach (var entry in pending)
        {
            var marker = entry.State == MigrationState.PendingOutOfOrder ? " [FUORI ORDINE]" : string.Empty;
            Write($"    - {entry.MigrationId}{marker}", entry.State == MigrationState.PendingOutOfOrder ? ConsoleColor.Red : ConsoleColor.Yellow);
        }
    }

    foreach (var mismatch in context.FileNameMismatches)
    {
        Write($"  ~ ID [Migration] '{mismatch.MigrationId}' != nome file '{mismatch.MismatchedFileName}'", ConsoleColor.DarkYellow);
    }

    Write(
        $"  GUARDIA database update : {(context.CanApplyPending ? "BOTTONE VISIBILE" : "bottone nascosto")}",
        context.CanApplyPending ? ConsoleColor.Cyan : ConsoleColor.DarkGray);

    Console.WriteLine();
}

if (report.Unknown.Count > 0)
{
    Write($"SCONOSCIUTE APPLICATE : {report.Unknown.Count}", ConsoleColor.Red);
    foreach (var entry in report.Unknown)
    {
        Console.WriteLine($"    - {entry.MigrationId}  (EF {entry.ProductVersion})");
    }

    Console.WriteLine();
}

Write("GIT", ConsoleColor.Cyan);
if (report.Git.Error is not null)
{
    Write($"  ! {report.Git.Error}", ConsoleColor.Red);
}
else
{
    Console.WriteLine($"  Branch   : {report.Git.Branch}");
    Console.WriteLine($"  Upstream : {(report.Git.HasUpstream ? $"avanti {report.Git.Ahead}, indietro {report.Git.Behind}" : "nessuno")}");
    Console.WriteLine($"  Modifiche locali : {report.Git.DirtyFiles}");
    Console.WriteLine($"  Branch selezionabili : {report.Git.AllBranches.Count}");

    if (report.Git.HasPendingPull)
    {
        Write($"  DA PULLARE: {report.Git.Behind} commit", ConsoleColor.Yellow);
    }

    if (report.Git.FetchError is not null)
    {
        Write($"  ! fetch: {report.Git.FetchError}", ConsoleColor.Yellow);
    }

    // Il pulsante pull e' sempre visibile: --ff-only e' la guardia. Qui si dice solo se oggi
    // il fast-forward riuscirebbe.
    Write(
        $"  pull --ff-only          : {(report.Git.CanFastForward ? "fast-forward possibile" : "git rifiuterebbe (niente da prendere, commit locali, upstream o tree)")}",
        report.Git.CanFastForward ? ConsoleColor.Cyan : ConsoleColor.DarkGray);
}

if (report.DevTunnel is not null)
{
    Console.WriteLine();
    Write("DEV TUNNEL", ConsoleColor.Cyan);

    if (!report.DevTunnel.Shared)
    {
        Write("  Tunnel : non condiviso (si assegna dall'exe Soffritto)", ConsoleColor.DarkGray);
    }
    else if (report.DevTunnel.Error is not null)
    {
        Write($"  ! {report.DevTunnel.Error}", ConsoleColor.Yellow);

        foreach (var summary in report.DevTunnel.AllAvailable)
        {
            var description = summary.Description.Length > 0 ? summary.Description : "(senza descrizione)";
            Write(
                $"    {summary.TunnelId}  {description}  host {(summary.IsHostConnected ? "connesso" : "non connesso")}  {summary.PortCount} porte",
                summary.IsHostConnected ? ConsoleColor.Green : ConsoleColor.DarkGray);
        }
    }
    else
    {
        Console.WriteLine($"  Tunnel : {report.DevTunnel.TunnelId} (clone {report.DevTunnel.Description})");
        Write(
            $"  Host   : {(report.DevTunnel.IsHostConnected ? "connesso" : "non connesso")}",
            report.DevTunnel.IsHostConnected ? ConsoleColor.Green : ConsoleColor.Yellow);

        foreach (var port in report.DevTunnel.AllPorts)
        {
            var probe = port.Reachable switch
            {
                true => "  [raggiungibile]",
                false => "  [nessuna risposta]",
                null => string.Empty,
            };

            Console.WriteLine($"    {port.PortNumber,5}  {port.PortUri}{probe}");
        }
    }
}

Console.WriteLine();
Write(
    report.IsEnvironmentUpToDate ? "AMBIENTE AGGIORNATO" : "AMBIENTE NON ALLINEATO",
    report.IsEnvironmentUpToDate ? ConsoleColor.Green : ConsoleColor.Yellow);

return report.IsEnvironmentUpToDate ? 0 : 1;

static void Write(string text, ConsoleColor color)
{
    var previous = Console.ForegroundColor;
    Console.ForegroundColor = color;
    Console.WriteLine(text);
    Console.ForegroundColor = previous;
}

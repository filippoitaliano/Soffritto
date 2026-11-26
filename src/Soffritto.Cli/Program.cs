using Soffritto.Core;
using Soffritto.Core.Configuration;

var solutionRoot = args.FirstOrDefault(a => !a.StartsWith('-')) ?? Directory.GetCurrentDirectory();
var fetch = args.Contains("--fetch", StringComparer.OrdinalIgnoreCase);

if (!Directory.Exists(solutionRoot))
{
    Console.Error.WriteLine($"Folder does not exist: {solutionRoot}");
    return 2;
}

var configuration = ConfigurationFiles.TryLoad(out var configurationError);
if (configuration is null)
{
    Console.Error.WriteLine(configurationError ?? $"No configuration ({ConfigurationFiles.ConfigPath}): fill it in from the Soffritto app's setup.");
    return 2;
}

Console.WriteLine($"Solution  : {solutionRoot}");
Console.WriteLine($"Git fetch : {(fetch ? "yes" : "no (use --fetch)")}");
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
    Console.WriteLine($"  Applied   : {context.AppliedCount} / {context.Entries.Count}");
    Console.WriteLine($"  Last      : {context.LastApplied ?? "(none)"}");

    if (pending.Count == 0)
    {
        Write("  Up to date", ConsoleColor.Green);
    }
    else
    {
        Write($"  PENDING   : {pending.Count}", ConsoleColor.Yellow);
        foreach (var entry in pending)
        {
            var marker = entry.State == MigrationState.PendingOutOfOrder ? " [OUT OF ORDER]" : string.Empty;
            Write($"    - {entry.MigrationId}{marker}", entry.State == MigrationState.PendingOutOfOrder ? ConsoleColor.Red : ConsoleColor.Yellow);
        }
    }

    foreach (var mismatch in context.FileNameMismatches)
    {
        Write($"  ~ ID [Migration] '{mismatch.MigrationId}' != file name '{mismatch.MismatchedFileName}'", ConsoleColor.DarkYellow);
    }

    Write(
        $"  GUARD database update   : {(context.CanApplyPending ? "BUTTON VISIBLE" : "button hidden")}",
        context.CanApplyPending ? ConsoleColor.Cyan : ConsoleColor.DarkGray);

    Console.WriteLine();
}

if (report.Unknown.Count > 0)
{
    Write($"UNKNOWN APPLIED : {report.Unknown.Count}", ConsoleColor.Red);
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
    Console.WriteLine($"  Upstream : {(report.Git.HasUpstream ? $"ahead {report.Git.Ahead}, behind {report.Git.Behind}" : "none")}");
    Console.WriteLine($"  Local changes : {report.Git.DirtyFiles}");
    Console.WriteLine($"  Selectable branches : {report.Git.AllBranches.Count}");

    if (report.Git.HasPendingPull)
    {
        Write($"  TO PULL: {report.Git.Behind} commits", ConsoleColor.Yellow);
    }

    if (report.Git.FetchError is not null)
    {
        Write($"  ! fetch: {report.Git.FetchError}", ConsoleColor.Yellow);
    }

    // The pull button is always visible: --ff-only is the guard. Here we only say whether
    // the fast-forward would succeed right now.
    Write(
        $"  pull --ff-only          : {(report.Git.CanFastForward ? "fast-forward possible" : "git would refuse (nothing to pull, local commits, upstream or tree)")}",
        report.Git.CanFastForward ? ConsoleColor.Cyan : ConsoleColor.DarkGray);
}

if (report.DevTunnel is not null)
{
    Console.WriteLine();
    Write("DEV TUNNEL", ConsoleColor.Cyan);

    if (!report.DevTunnel.Shared)
    {
        Write("  Tunnel : not shared (assign it from the Soffritto app)", ConsoleColor.DarkGray);
    }
    else if (report.DevTunnel.Error is not null)
    {
        Write($"  ! {report.DevTunnel.Error}", ConsoleColor.Yellow);

        foreach (var summary in report.DevTunnel.AllAvailable)
        {
            var description = summary.Description.Length > 0 ? summary.Description : "(no description)";
            Write(
                $"    {summary.TunnelId}  {description}  host {(summary.IsHostConnected ? "connected" : "not connected")}  {summary.PortCount} ports",
                summary.IsHostConnected ? ConsoleColor.Green : ConsoleColor.DarkGray);
        }
    }
    else
    {
        Console.WriteLine($"  Tunnel : {report.DevTunnel.TunnelId} (clone {report.DevTunnel.Description})");
        Write(
            $"  Host   : {(report.DevTunnel.IsHostConnected ? "connected" : "not connected")}",
            report.DevTunnel.IsHostConnected ? ConsoleColor.Green : ConsoleColor.Yellow);

        foreach (var port in report.DevTunnel.AllPorts)
        {
            var probe = port.Reachable switch
            {
                true => "  [reachable]",
                false => "  [no response]",
                null => string.Empty,
            };

            Console.WriteLine($"    {port.PortNumber,5}  {port.PortUri}{probe}");
        }
    }
}

Console.WriteLine();
Write(
    report.IsEnvironmentUpToDate ? "ENVIRONMENT UP TO DATE" : "ENVIRONMENT OUT OF DATE",
    report.IsEnvironmentUpToDate ? ConsoleColor.Green : ConsoleColor.Yellow);

return report.IsEnvironmentUpToDate ? 0 : 1;

static void Write(string text, ConsoleColor color)
{
    var previous = Console.ForegroundColor;
    Console.ForegroundColor = color;
    Console.WriteLine(text);
    Console.ForegroundColor = previous;
}

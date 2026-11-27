using System.Text.Json;
using Soffritto.Core;
using Soffritto.Core.Configuration;
using Soffritto.Core.Docker;
using Soffritto.Presentation;

namespace Soffritto.Mcp;

internal sealed record EnvironmentArgs(int Number);

internal sealed record ServiceArgs(int Number, string Service);

internal sealed record CreateArgs(int Number, Dictionary<string, string> Branches, List<string>? Pool, string? Note);

internal sealed record DockerMigrationsArgs(int Number, string? Context);

internal sealed record RepositoryArgs(int Number, string Repository);

internal sealed record SwitchArgs(int Number, string Repository, string Branch);

internal sealed record PruneArgs(int Number, string Repository, string Confirm);

internal sealed record LocalMigrationsArgs(int Number, string? Context, string Confirm);

internal sealed record UpdateToArgs(int Number, string Context, string Migration, string Confirm);

internal sealed record CheckoutArgs(int Number, Dictionary<string, string>? Branches);

/// <summary>
/// What each kind of operation does, run by the worker process (<see cref="OperationWorker"/>)
/// from the arguments recorded by the tool that started it. The configuration is read again
/// here: the worker may start after the server has changed it.
/// </summary>
internal static class OperationCatalog
{
    public const string DockerCreate = "docker.create";
    public const string DockerStart = "docker.start";
    public const string DockerStop = "docker.stop";
    public const string DockerStartService = "docker.start-service";
    public const string DockerStopService = "docker.stop-service";
    public const string DockerMigrations = "docker.migrations";
    public const string DockerRepair = "docker.repair";
    public const string TunnelEnable = "tunnel.enable";
    public const string TunnelDisable = "tunnel.disable";
    public const string Extra = "extra";
    public const string LocalPull = "local.pull";
    public const string LocalSwitch = "local.switch";
    public const string LocalPrune = "local.prune";
    public const string LocalMigrations = "local.migrations";
    public const string LocalUpdateTo = "local.update-to";
    public const string LocalCheckout = "local.checkout";

    public static async Task<(string? Error, object? Result)> RunAsync(OperationRequest request, Action<string> log, CancellationToken ct)
    {
        if (EnvironmentTools.LoadConfiguration(out var error) is not { } c)
        {
            return (error, null);
        }

        T Args<T>() => request.Args.Deserialize<T>(Operations.Json) ?? throw new InvalidOperationException($"arguments of {request.Kind} not readable");

        return request.Kind switch
        {
            DockerCreate => await CreateAsync(c, Args<CreateArgs>(), log, ct).ConfigureAwait(false),
            DockerStart => await OnSlotAsync(c, Args<EnvironmentArgs>().Number, (slot, n) => StartDockerAsync(c, slot, n, log, ct)).ConfigureAwait(false),
            DockerStop => await OnSlotAsync(c, Args<EnvironmentArgs>().Number, (slot, n) => StopDockerAsync(c, slot, n, log, ct)).ConfigureAwait(false),
            DockerStartService => await StartServiceAsync(c, Args<ServiceArgs>(), log, ct).ConfigureAwait(false),
            DockerStopService => await StopServiceAsync(c, Args<ServiceArgs>(), log, ct).ConfigureAwait(false),
            DockerMigrations => await DockerMigrationsAsync(c, Args<DockerMigrationsArgs>(), log, ct).ConfigureAwait(false),
            DockerRepair => await RepairAsync(c, Args<EnvironmentArgs>().Number, log, ct).ConfigureAwait(false),
            TunnelEnable => await EnableTunnelAsync(c, Args<EnvironmentArgs>().Number, log, ct).ConfigureAwait(false),
            TunnelDisable => await DisableTunnelAsync(c, Args<EnvironmentArgs>().Number, log, ct).ConfigureAwait(false),
            Extra => await ExtraCommandTools.RunAsync(c, Args<RepositoryArgs>(), log, ct).ConfigureAwait(false),
            LocalPull => await LocalWork.PullAsync(c, Args<RepositoryArgs>(), log, ct).ConfigureAwait(false),
            LocalSwitch => await LocalWork.SwitchAsync(c, Args<SwitchArgs>(), log, ct).ConfigureAwait(false),
            LocalPrune => await LocalWork.PruneAsync(c, Args<PruneArgs>(), log, ct).ConfigureAwait(false),
            LocalMigrations => await LocalWork.ApplyMigrationsAsync(c, Args<LocalMigrationsArgs>(), log, ct).ConfigureAwait(false),
            LocalUpdateTo => await LocalWork.UpdateToAsync(c, Args<UpdateToArgs>(), log, ct).ConfigureAwait(false),
            LocalCheckout => await LocalWork.CheckoutAsync(c, Args<CheckoutArgs>(), log, ct).ConfigureAwait(false),
            _ => ($"unknown operation kind {request.Kind}", null),
        };
    }

    private static async Task<(string? Error, object? Result)> OnSlotAsync(AppConfiguration c, int number, Func<DockerSlot, int, Task<(string? Error, object? Result)>> work)
        => c.Environment(number) is { Kind: EnvironmentKind.Docker } environment
            ? await work(DockerSlot.Of(c, environment), number).ConfigureAwait(false)
            : ($"environment {number} is not a configured Docker environment", null);

    private static async Task<(string? Error, object? Result)> CreateAsync(AppConfiguration c, CreateArgs args, Action<string> log, CancellationToken ct)
    {
        var request = new DockerCreateRequest(args.Number, args.Branches, args.Pool, index => PoolHosts.Ensure(c, index));
        var createError = await DockerEnvironmentProvisioner.CreateAsync(
            c,
            request,
            _ =>
            {
                if (!string.IsNullOrWhiteSpace(args.Note))
                {
                    DockerTools.WriteNote(args.Number, args.Note);
                }
            },
            log,
            ct).ConfigureAwait(false);
        if (createError is not null)
        {
            return (createError, null);
        }

        // CreateAsync records the entry in config.json, not in the configuration passed in.
        if (EnvironmentTools.LoadConfiguration(out var reloadError) is not { } reloaded || reloaded.Environment(args.Number) is not { } created)
        {
            return ($"environment {args.Number} created, but config.json cannot be reloaded: {reloadError}", null);
        }

        var slot = DockerSlot.Of(reloaded, created);
        if (slot.HasContainerizedFrontend)
        {
            log(DockerWatchConsole.Start(ProcessTable.Read([]), slot));
        }

        return (null, DockerTools.Summary(slot));
    }

    private static async Task<(string? Error, object? Result)> StartDockerAsync(AppConfiguration c, DockerSlot slot, int number, Action<string> log, CancellationToken ct)
    {
        if (await DockerEnvironmentProvisioner.StartAsync(c, number, log, ct).ConfigureAwait(false) is { } error)
        {
            return (error, null);
        }

        if (slot.HasContainerizedFrontend)
        {
            log(DockerWatchConsole.Start(ProcessTable.Read([]), slot));
        }

        EnvironmentTunnel.RestoreHosts(number, log);
        return (null, DockerTools.Summary(slot));
    }

    private static async Task<(string? Error, object? Result)> StopDockerAsync(AppConfiguration c, DockerSlot slot, int number, Action<string> log, CancellationToken ct)
    {
        if (slot.HasContainerizedFrontend)
        {
            log(DockerWatchConsole.Stop(ProcessTable.Read([]), slot));
        }

        var (outcome, allStopped) = await DockerEnvironmentProvisioner.StopAsync(c, number, log, ct).ConfigureAwait(false);
        return (allStopped ? null : outcome, new { result = outcome });
    }

    private static Task<(string? Error, object? Result)> StartServiceAsync(AppConfiguration c, ServiceArgs args, Action<string> log, CancellationToken ct)
        => OnSlotAsync(c, args.Number, async (slot, number) =>
        {
            if (await DockerEnvironmentProvisioner.StartServiceAsync(c, number, args.Service, log, ct).ConfigureAwait(false) is { } error)
            {
                return (error, null);
            }

            if (string.Equals(args.Service, slot.WebService, StringComparison.Ordinal))
            {
                log(DockerWatchConsole.Start(ProcessTable.Read([]), slot));
            }

            return (null, new { result = $"{args.Service} started" });
        });

    private static Task<(string? Error, object? Result)> StopServiceAsync(AppConfiguration c, ServiceArgs args, Action<string> log, CancellationToken ct)
        => OnSlotAsync(c, args.Number, async (slot, number) =>
        {
            if (string.Equals(args.Service, slot.WebService, StringComparison.Ordinal))
            {
                log(DockerWatchConsole.Stop(ProcessTable.Read([]), slot));
            }

            var error = await DockerEnvironmentProvisioner.StopServiceAsync(c, number, args.Service, log, ct).ConfigureAwait(false);
            return (error, error is null ? new { result = $"{args.Service} stopped" } : null);
        });

    private static Task<(string? Error, object? Result)> DockerMigrationsAsync(AppConfiguration c, DockerMigrationsArgs args, Action<string> log, CancellationToken ct)
        => OnSlotAsync(c, args.Number, async (slot, number) =>
        {
            if (slot.Backend?.Repository is not BackendConfiguration backend)
            {
                return ($"environment {number} has no backend", null);
            }

            var error = await DockerEnvironmentProvisioner.ApplyMigrationsAsync(slot, backend, log, ct, args.Context).ConfigureAwait(false);
            return (error, error is null ? new { result = "migrations applied" } : null);
        });

    private static Task<(string? Error, object? Result)> RepairAsync(AppConfiguration c, int number, Action<string> log, CancellationToken ct)
        => OnSlotAsync(c, number, async (_, n) =>
        {
            var error = await DockerEnvironmentProvisioner.RepairEnvAsync(c, n, log, ct).ConfigureAwait(false);
            return (error, error is null ? new { result = "env files rewritten", nextStep = "start_docker_service or start_docker_environment so the containers read them" } : null);
        });

    private static async Task<(string? Error, object? Result)> EnableTunnelAsync(AppConfiguration c, int number, Action<string> log, CancellationToken ct)
    {
        if (c.Environment(number) is not { } environment)
        {
            return ($"environment {number} is not configured", null);
        }

        if (await EnvironmentTunnel.EnableAsync(c, number, log, ct).ConfigureAwait(false) is { } error)
        {
            return (error, null);
        }

        var urls = await DockerEnvironmentProvisioner.PoolUrlsAsync(number, ct).ConfigureAwait(false);
        return (null, new
        {
            tunnel = TunnelPool.Read().Where(a => a.Environment == number).Select(a => a.TunnelDescription).Distinct(),
            url = urls,
            nextStep = environment.Kind == EnvironmentKind.Docker
                ? "start_docker_service so the containers read the new env files"
                : "restart the services named in the log (stop_local_service, start_local_service) so they read the new env files",
        });
    }

    private static async Task<(string? Error, object? Result)> DisableTunnelAsync(AppConfiguration c, int number, Action<string> log, CancellationToken ct)
    {
        var had = EnvironmentTunnel.IsOn(number);
        var error = await EnvironmentTunnel.DisableAsync(c, number, log, ct).ConfigureAwait(false);
        return error is not null ? (error, null) : (null, new { result = had ? "tunnel turned off, ports released" : $"environment {number} did not have its tunnel on" });
    }
}

using System.ComponentModel;
using ModelContextProtocol.Server;
using Soffritto.Core.Configuration;
using Soffritto.Core.Docker;
using Soffritto.Presentation;

namespace Soffritto.Mcp;

/// <summary>
/// An environment's tunnel, local or Docker: the same command as the “Tunnel” switch in the
/// environment header of the app (<see cref="EnvironmentTunnel"/>).
/// </summary>
[McpServerToolType]
public static class TunnelTools
{
    private const string WaitDescription = "Seconds to wait for completion before responding (0-110): if the operation takes longer, the response contains the id to follow with operation_status.";

    [McpServerTool(Name = "enable_tunnel", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Turns on the tunnel of an environment, local or Docker, like the “Tunnel” switch in its header in the app: the environment claims a free tunnel from the pool (a tunnel belongs to a single environment), each of its repositories that exposes a port gets a public URL with anonymous access, the tunnel host starts and the env variables that depend on the pool are rewritten. If the pool is full, responds with the error. The services read the rewritten env files only when they start again: in a Docker environment restart them with start_docker_service, in a local one the operation log names the services to restart.")]
    public static Task<string> EnableTunnel(
        [Description("Environment number, local or Docker.")] int number,
        [Description(WaitDescription)] int waitSeconds = 90,
        CancellationToken cancellationToken = default)
        => OnEnvironment(number, "tunnel", waitSeconds, async (configuration, environment, operation, ct) =>
        {
            if (await EnvironmentTunnel.EnableAsync(configuration, number, operation.Log, ct).ConfigureAwait(false) is { } error)
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
                    : "restart the services named in the log so they read the new env files",
            });
        }, cancellationToken);

    [McpServerTool(Name = "disable_tunnel", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Turns off the tunnel of an environment, local or Docker, like the “Tunnel” switch in its header in the app: releases all its pool ports (and their anonymous access), closes the host of tunnels left without ports, returns the tunnel to the pool and rewrites the env variables that depended on the pool. When turned on again, the environment may get a different tunnel, with different URLs.")]
    public static Task<string> DisableTunnel(
        [Description("Environment number, local or Docker.")] int number,
        [Description(WaitDescription)] int waitSeconds = 60,
        CancellationToken cancellationToken = default)
        => OnEnvironment(number, "tunnel off", waitSeconds, async (configuration, environment, operation, ct) =>
        {
            var had = EnvironmentTunnel.IsOn(number);
            var error = await EnvironmentTunnel.DisableAsync(configuration, number, operation.Log, ct).ConfigureAwait(false);
            return error is not null ? (error, null) : (null, new { result = had ? "tunnel turned off, ports released" : $"environment {number} did not have its tunnel on" });
        }, cancellationToken);

    private static async Task<string> OnEnvironment(
        int number,
        string kind,
        int waitSeconds,
        Func<AppConfiguration, EnvironmentDefinition, Operation, CancellationToken, Task<(string? Error, object? Result)>> work,
        CancellationToken cancellationToken)
    {
        if (EnvironmentTools.LoadConfiguration(out var error) is not { } configuration)
        {
            return EnvironmentTools.Serialize(new { error });
        }

        if (configuration.Environment(number) is not { } environment)
        {
            return EnvironmentTools.Serialize(new { error = $"environment {number} is not configured" });
        }

        var snapshot = await Operations.RunAsync(kind, number, waitSeconds, (operation, ct) => work(configuration, environment, operation, ct), cancellationToken).ConfigureAwait(false);
        return EnvironmentTools.Serialize(snapshot);
    }
}

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
        => OnEnvironment(number, OperationCatalog.TunnelEnable, "tunnel", waitSeconds, cancellationToken);

    [McpServerTool(Name = "disable_tunnel", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Turns off the tunnel of an environment, local or Docker, like the “Tunnel” switch in its header in the app: releases all its pool ports (and their anonymous access), closes the host of tunnels left without ports, returns the tunnel to the pool and rewrites the env variables that depended on the pool. When turned on again, the environment may get a different tunnel, with different URLs.")]
    public static Task<string> DisableTunnel(
        [Description("Environment number, local or Docker.")] int number,
        [Description(WaitDescription)] int waitSeconds = 60,
        CancellationToken cancellationToken = default)
        => OnEnvironment(number, OperationCatalog.TunnelDisable, "tunnel off", waitSeconds, cancellationToken);

    private static async Task<string> OnEnvironment(int number, string kind, string label, int waitSeconds, CancellationToken cancellationToken)
    {
        if (EnvironmentTools.LoadConfiguration(out var error) is not { } configuration)
        {
            return EnvironmentTools.Serialize(new { error });
        }

        if (configuration.Environment(number) is null)
        {
            return EnvironmentTools.Serialize(new { error = $"environment {number} is not configured" });
        }

        var snapshot = await Operations.StartAsync(kind, label, number, new EnvironmentArgs(number), waitSeconds, cancellationToken).ConfigureAwait(false);
        return EnvironmentTools.Serialize(snapshot);
    }
}

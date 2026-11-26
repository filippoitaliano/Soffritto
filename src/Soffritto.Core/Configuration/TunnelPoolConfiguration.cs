namespace Soffritto.Core.Configuration;

/// <summary>The user-created dev tunnels whose ports Soffritto assigns to environments.</summary>
public sealed class TunnelPoolConfiguration
{
    public List<PoolTunnelConfiguration> Tunnels { get; set; } = [];
}

/// <summary>A pool tunnel, found by Description like the tunnels of local environments.</summary>
public sealed class PoolTunnelConfiguration
{
    public string Description { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;
}

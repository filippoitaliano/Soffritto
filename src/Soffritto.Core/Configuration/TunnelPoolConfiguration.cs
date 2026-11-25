namespace Soffritto.Core.Configuration;

/// <summary>I dev tunnel creati dall'utente le cui porte Soffritto assegna agli ambienti.</summary>
public sealed class TunnelPoolConfiguration
{
    public List<PoolTunnelConfiguration> Tunnels { get; set; } = [];
}

/// <summary>Un tunnel del pool, ritrovato per Description come quelli degli ambienti locali.</summary>
public sealed class PoolTunnelConfiguration
{
    public string Description { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;
}

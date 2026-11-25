using Soffritto.Core.Configuration;

namespace Soffritto.Core.Docker.Seed;

/// <summary>Il provider del seed per il tipo configurato in seed.kind.</summary>
public static class SeedProviders
{
    private static readonly SqlServerSeedProvider SqlServer = new();

    public static ISeedProvider For(SeedKind kind) => kind switch
    {
        SeedKind.SqlServer => SqlServer,
        _ => throw new NotSupportedException($"Seed di tipo {kind} non gestito."),
    };
}

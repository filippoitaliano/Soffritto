using Soffritto.Core.Configuration;

namespace Soffritto.Core.Docker.Seed;

/// <summary>The seed provider for the kind configured in seed.kind.</summary>
public static class SeedProviders
{
    private static readonly SqlServerSeedProvider SqlServer = new();

    public static ISeedProvider For(SeedKind kind) => kind switch
    {
        SeedKind.SqlServer => SqlServer,
        _ => throw new NotSupportedException($"Seed kind {kind} is not supported."),
    };
}

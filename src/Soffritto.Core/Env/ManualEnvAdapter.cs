using Soffritto.Core.Configuration;

namespace Soffritto.Core.Env;

/// <summary>No file: the variables are only the ones written in setup, and nothing is written to the worktree.</summary>
public sealed class ManualEnvAdapter : IEnvAdapter
{
    public EnvAdapterKind Kind => EnvAdapterKind.Manual;

    public EnvSet Read(string path) => new();

    public void Write(string path, EnvSet set, EnvWriteOptions options)
    {
    }
}

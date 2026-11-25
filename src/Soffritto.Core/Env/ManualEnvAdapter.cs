using Soffritto.Core.Configuration;

namespace Soffritto.Core.Env;

/// <summary>Nessun file: le variabili sono solo quelle scritte nel setup, e nel worktree non si scrive niente.</summary>
public sealed class ManualEnvAdapter : IEnvAdapter
{
    public EnvAdapterKind Kind => EnvAdapterKind.Manual;

    public EnvSet Read(string path) => new();

    public void Write(string path, EnvSet set, EnvWriteOptions options)
    {
    }
}

using Soffritto.Core.Configuration;

namespace Soffritto.Core.Env;

public static class EnvAdapters
{
    public static IEnvAdapter For(EnvAdapterKind kind) => kind switch
    {
        EnvAdapterKind.Bat => new BatEnvAdapter(),
        EnvAdapterKind.Dotenv => new DotenvEnvAdapter(),
        EnvAdapterKind.AppSettings => new AppSettingsEnvAdapter(),
        _ => new ManualEnvAdapter(),
    };
}

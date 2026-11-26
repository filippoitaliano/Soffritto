using Soffritto.Core;
using Soffritto.Core.Configuration;

namespace Soffritto.Desktop;

/// <summary>Port and address of the dev server of a frontend clone (vedi <see cref="FrontendEnv"/>).</summary>
public static class FrontendDevServer
{
    public static int ReadPort(RepoLocation frontend)
        => frontend.Settings is FrontendConfiguration settings ? ReadPort(frontend, settings) : new FrontendConfiguration().DevServerDefaultPort;

    public static int ReadPort(RepoLocation frontend, FrontendConfiguration settings)
        => FrontendEnv.ReadDevServerPort(frontend.Path, settings);

    public static Uri UrlOf(int port) => new($"http://localhost:{port}/");
}

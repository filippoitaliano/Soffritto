using Soffritto.Core.Configuration;

namespace Soffritto.Presentation;

/// <summary>How the frontend dev server, the app and its bundler are recognised in the process snapshot.</summary>
public static class RepoProcesses
{
    public static bool IsFrontend(ProcessRow p, string frontendPath, FrontendConfiguration settings)
        => ConsoleServices.Is(p, "node.exe")
            && ConsoleServices.Under(p.CommandLine, frontendPath)
            && p.CommandLine.Contains(settings.DevServerProcessHint, StringComparison.OrdinalIgnoreCase);

    public static bool IsApp(ProcessRow p, string appPath)
        => ConsoleServices.Is(p, "node.exe") && ConsoleServices.Under(p.CommandLine, appPath);

    // react-native start, launched by npm run start or started on its own by run-android. The node of
    // cli.js gets the argument in quotes, the cross-env one does not.
    public static bool IsMetro(ProcessRow p, string appPath)
        => IsApp(p, appPath)
            && (p.CommandLine.Contains(" start", StringComparison.OrdinalIgnoreCase)
                || p.CommandLine.Contains(" \"start\"", StringComparison.OrdinalIgnoreCase));
}

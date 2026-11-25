using Soffritto.Core.Configuration;

namespace Soffritto.Presentation;

/// <summary>Come si riconoscono, nella fotografia dei processi, il dev server del frontend, l'app e il suo bundler.</summary>
public static class RepoProcesses
{
    public static bool IsFrontend(ProcessRow p, string frontendPath, FrontendConfiguration settings)
        => ConsoleServices.Is(p, "node.exe")
            && ConsoleServices.Under(p.CommandLine, frontendPath)
            && p.CommandLine.Contains(settings.DevServerProcessHint, StringComparison.OrdinalIgnoreCase);

    public static bool IsApp(ProcessRow p, string appPath)
        => ConsoleServices.Is(p, "node.exe") && ConsoleServices.Under(p.CommandLine, appPath);

    // react-native start, lanciato da npm run start o partito da solo con run-android. Il node di
    // cli.js riceve l'argomento tra virgolette, quello di cross-env no.
    public static bool IsMetro(ProcessRow p, string appPath)
        => IsApp(p, appPath)
            && (p.CommandLine.Contains(" start", StringComparison.OrdinalIgnoreCase)
                || p.CommandLine.Contains(" \"start\"", StringComparison.OrdinalIgnoreCase));
}

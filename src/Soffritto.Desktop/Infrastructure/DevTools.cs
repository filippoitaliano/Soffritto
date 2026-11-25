using System.Diagnostics;

namespace Soffritto.Desktop.Infrastructure;

/// <summary>Dove stanno Visual Studio e VS Code su questa macchina.</summary>
public static class DevTools
{
    /// <summary>devenv.exe dell'installazione piu' recente, prerelease comprese (oggi VS 18 Insiders).</summary>
    public static string? VisualStudio()
    {
        var vswhere = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft Visual Studio", "Installer", "vswhere.exe");

        if (!System.IO.File.Exists(vswhere))
        {
            return null;
        }

        using var process = Process.Start(new ProcessStartInfo(vswhere, "-latest -prerelease -property productPath")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });

        var path = process?.StandardOutput.ReadToEnd().Trim();
        process?.WaitForExit();

        return string.IsNullOrEmpty(path) || !System.IO.File.Exists(path) ? null : path;
    }

    /// <summary>VS Code, preferendo l'Insiders installata sulla macchina; poi la stabile.</summary>
    public static string? VisualStudioCode()
    {
        var programs = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");

        string[] candidates =
        [
            System.IO.Path.Combine(programs, "Microsoft VS Code Insiders", "Code - Insiders.exe"),
            System.IO.Path.Combine(programs, "Microsoft VS Code", "Code.exe"),
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft VS Code", "Code.exe"),
        ];

        return candidates.FirstOrDefault(System.IO.File.Exists);
    }
}

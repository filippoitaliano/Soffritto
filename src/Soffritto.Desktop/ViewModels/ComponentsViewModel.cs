using System.IO;
using System.Text.Json.Nodes;
using System.Windows.Input;
using Soffritto.Core;
using Soffritto.Desktop.Infrastructure;
using Soffritto.Presentation;

namespace Soffritto.Desktop.ViewModels;

/// <summary>
/// The Components tab of the setup: the external programs the app uses, the MCP server and the
/// VSIX that ship with the installation, and updates. Nothing ends up in config.json: every
/// button acts right away, almost always in a console where you can watch what happens.
/// </summary>
public sealed class ComponentsViewModel : ObservableObject
{
    public const string McpServerName = "soffritto";

    private string mcpStatus = string.Empty;
    private string vsixStatus = string.Empty;
    private string updateStatus = string.Empty;
    private string wingetStatus = string.Empty;
    private bool canRegisterMcp;
    private bool canInstallVsix;
    private bool isCheckingUpdates;
    private string? claude;
    private string? visualStudio;

    public ComponentsViewModel()
    {
        Prerequisites =
        [
            new Prerequisite("Git", "Clone status, pull, branch switching, worktrees of Docker environments.", "Git.Git", "git.exe"),
            new Prerequisite("Dev tunnel", "Share environments outside this PC (Tunnels tab).", "Microsoft.devtunnel", "devtunnel.exe"),
            new Prerequisite("Docker Desktop", "Only for Docker environments.", "Docker.DockerDesktop", "docker.exe"),
        ];

        RefreshCommand = new RelayCommand(_ => Refresh());
        InstallCommand = new RelayCommand(item => Install(item as Prerequisite));
        DevTunnelLoginCommand = new RelayCommand(_ => DevTunnelLogin());
        RegisterMcpCommand = new RelayCommand(_ => RegisterMcp());
        CopyMcpPathCommand = new RelayCommand(_ => new ClipboardText().TrySetText(McpPath));
        InstallVsixCommand = new RelayCommand(_ => InstallVsix());
        CheckUpdatesCommand = new AsyncCommand(CheckUpdatesAsync);

        UpdateStatus = Installation.IsInstalled
            ? Installation.PendingVersion is { } version ? $"v{version} is downloaded: it installs when the app closes." : "Updates are checked when the app starts."
            : "This build does not come from Setup.exe: rebuild it to update.";
    }

    public IReadOnlyList<Prerequisite> Prerequisites { get; }

    public string McpPath { get; } = Path.Combine(AppContext.BaseDirectory, "Soffritto.Mcp.exe");

    public string VsixPath { get; } = Path.Combine(AppContext.BaseDirectory, "vsix", "Soffritto.Vsix.vsix");

    public string WingetStatus { get => wingetStatus; private set => SetProperty(ref wingetStatus, value); }

    public string McpStatus { get => mcpStatus; private set => SetProperty(ref mcpStatus, value); }

    public bool CanRegisterMcp { get => canRegisterMcp; private set => SetProperty(ref canRegisterMcp, value); }

    public string VsixStatus { get => vsixStatus; private set => SetProperty(ref vsixStatus, value); }

    public bool CanInstallVsix { get => canInstallVsix; private set => SetProperty(ref canInstallVsix, value); }

    public string UpdateStatus { get => updateStatus; private set => SetProperty(ref updateStatus, value); }

    public bool CanCheckUpdates => Installation.IsInstalled && !isCheckingUpdates;

    public ICommand RefreshCommand { get; }

    public ICommand InstallCommand { get; }

    public ICommand DevTunnelLoginCommand { get; }

    public ICommand RegisterMcpCommand { get; }

    public ICommand CopyMcpPathCommand { get; }

    public ICommand InstallVsixCommand { get; }

    public ICommand CheckUpdatesCommand { get; }

    /// <summary>Reads again what is on the machine: called when the tab opens and from “Check again”.</summary>
    public void Refresh()
    {
        var winget = ExecutableSearch.Find("winget.exe");
        WingetStatus = winget is null
            ? "winget is missing: install “App Installer” from the Microsoft Store, or get the programs from their websites."
            : string.Empty;

        foreach (var prerequisite in Prerequisites)
        {
            prerequisite.Refresh(canInstall: winget is not null);
        }

        claude = ExecutableSearch.Find("claude.exe") ?? ExecutableSearch.Find("claude.cmd");
        RefreshMcp();

        visualStudio = DevTools.VisualStudio();
        RefreshVsix();
    }

    private void RefreshMcp()
    {
        if (!File.Exists(McpPath))
        {
            McpStatus = "The MCP server ships with Setup.exe: this build does not include it.";
            CanRegisterMcp = false;
            return;
        }

        if (claude is null)
        {
            McpStatus = "Claude Code is not installed: for another MCP client, register the exe below by hand as a stdio server.";
            CanRegisterMcp = false;
            return;
        }

        CanRegisterMcp = true;
        McpStatus = RegisteredMcpCommand() switch
        {
            null => $"Claude Code has no “{McpServerName}” server.",
            var command when string.Equals(Path.GetFullPath(command), McpPath, StringComparison.OrdinalIgnoreCase)
                => $"Registered in Claude Code as “{McpServerName}”.",
            var command => $"In Claude Code, “{McpServerName}” launches a different exe ({command}): “Register” replaces it with this one.",
        };
    }

    // The server registered for the user (claude mcp add --scope user) lives in ~\.claude.json.
    // Reading the file avoids "claude mcp get", which starts the server in order to check it.
    private static string? RegisteredMcpCommand()
    {
        try
        {
            var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");
            var root = JsonNode.Parse(File.ReadAllText(file));
            return root?["mcpServers"]?[McpServerName]?["command"]?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    private void RefreshVsix()
    {
        if (!File.Exists(VsixPath))
        {
            VsixStatus = "The extension ships with Setup.exe: this build does not include it.";
            CanInstallVsix = false;
        }
        else if (visualStudio is null)
        {
            VsixStatus = "Visual Studio is not installed: the extension is only useful there.";
            CanInstallVsix = false;
        }
        else
        {
            VsixStatus = "Close Visual Studio before installing: the installer asks you to anyway. Requires Visual Studio 2026 18.12 or later.";
            CanInstallVsix = true;
        }
    }

    // winget in its own console: licenses, elevation prompts and the outcome stay in front of whoever is installing.
    private void Install(Prerequisite? prerequisite)
    {
        if (prerequisite is null)
        {
            return;
        }

        OpenConsole(prerequisite.InstallCommandText);
    }

    private void DevTunnelLogin()
    {
        var devtunnel = DevTunnelReader.ResolveExecutable() ?? "devtunnel";
        OpenConsole($"\"{devtunnel}\" user login");
    }

    // A server with the same name is removed first: "claude mcp add" does not overwrite.
    private void RegisterMcp()
    {
        OpenConsole($"claude mcp remove --scope user {McpServerName} & claude mcp add --scope user {McpServerName} -- \"{McpPath}\"");
    }

    // VSLauncher routes the .vsix to the right installer, like a double click: VSIXInstaller
    // from the command line rejects VisualStudio.Extensibility extensions (docs/GUIDE.md, “Installing the extension”).
    private void InstallVsix()
    {
        var launcher = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Common Files", "Microsoft Shared", "MSEnv", "VSLauncher.exe");

        if (!File.Exists(launcher))
        {
            VsixStatus = $"VSLauncher.exe not found: install by double-clicking {VsixPath}.";
            return;
        }

        DetachedLauncher.Start($"\"{launcher}\" \"{VsixPath}\"", AppContext.BaseDirectory, newConsole: false);
    }

    private async Task CheckUpdatesAsync()
    {
        isCheckingUpdates = true;
        OnPropertyChanged(nameof(CanCheckUpdates));
        UpdateStatus = "Looking for the latest release…";
        try
        {
            UpdateStatus = await Installation.CheckForUpdatesAsync();
        }
        catch (Exception ex)
        {
            UpdateStatus = $"Check failed: {ex.Message}";
        }
        finally
        {
            isCheckingUpdates = false;
            OnPropertyChanged(nameof(CanCheckUpdates));
        }
    }

    // cmd /k: when the command finishes the console stays open with the outcome. cmd itself strips
    // the outer quotes; the inner ones stay with the command.
    private static void OpenConsole(string command) =>
        DetachedLauncher.Start(
            $"cmd.exe /k \"{command}\"",
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            newConsole: true);
}

public sealed class Prerequisite(string name, string purpose, string wingetId, string executable) : ObservableObject
{
    private string status = string.Empty;
    private bool isMissing;
    private bool canInstall;

    public string Name { get; } = name;

    public string Purpose { get; } = purpose;

    public string InstallCommandText { get; } = $"winget install --id {wingetId} --exact";

    public string Status { get => status; private set => SetProperty(ref status, value); }

    public bool IsMissing { get => isMissing; private set => SetProperty(ref isMissing, value); }

    public bool CanInstall { get => canInstall; private set => SetProperty(ref canInstall, value); }

    public void Refresh(bool canInstall)
    {
        var found = executable == "devtunnel.exe" ? DevTunnelReader.ResolveExecutable() : ExecutableSearch.Find(executable);
        IsMissing = found is null;
        Status = found ?? "Not found.";
        CanInstall = IsMissing && canInstall;
    }
}

/// <summary>
/// Looks for an executable in the machine and user PATH, read fresh from the registry: a
/// program winget has just installed is not in the PATH the app received at startup.
/// </summary>
public static class ExecutableSearch
{
    public static string? Find(string executable) =>
        new[]
        {
            Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine),
            Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable("Path"),
        }
        .SelectMany(path => (path ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        .Select(dir => Environment.ExpandEnvironmentVariables(dir.Trim()))
        .Where(dir => dir.Length > 0 && dir.IndexOfAny(Path.GetInvalidPathChars()) < 0)
        .Select(dir => Path.Combine(dir, executable))
        .FirstOrDefault(File.Exists);
}

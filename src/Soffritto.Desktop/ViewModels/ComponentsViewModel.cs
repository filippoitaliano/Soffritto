using System.IO;
using System.Text.Json.Nodes;
using System.Windows.Input;
using Soffritto.Core;
using Soffritto.Desktop.Infrastructure;
using Soffritto.Presentation;

namespace Soffritto.Desktop.ViewModels;

/// <summary>
/// La scheda Componenti del setup: i programmi esterni che l'exe usa, il server MCP e la VSIX
/// che arrivano con l'installazione, gli aggiornamenti. Niente finisce in config.json: ogni
/// bottone agisce subito, quasi sempre in una console dove si vede cosa succede.
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
            new Prerequisite("Git", "Stato dei cloni, pull, cambio branch, worktree degli ambienti Docker.", "Git.Git", "git.exe"),
            new Prerequisite("Dev tunnel", "Condividere gli ambienti fuori dal PC (scheda Tunnel).", "Microsoft.devtunnel", "devtunnel.exe"),
            new Prerequisite("Docker Desktop", "Solo per gli ambienti Docker.", "Docker.DockerDesktop", "docker.exe"),
        ];

        RefreshCommand = new RelayCommand(_ => Refresh());
        InstallCommand = new RelayCommand(item => Install(item as Prerequisite));
        DevTunnelLoginCommand = new RelayCommand(_ => DevTunnelLogin());
        RegisterMcpCommand = new RelayCommand(_ => RegisterMcp());
        CopyMcpPathCommand = new RelayCommand(_ => new ClipboardText().TrySetText(McpPath));
        InstallVsixCommand = new RelayCommand(_ => InstallVsix());
        CheckUpdatesCommand = new AsyncCommand(CheckUpdatesAsync);

        UpdateStatus = Installation.IsInstalled
            ? Installation.PendingVersion is { } version ? $"La v{version} è scaricata: si installa alla chiusura dell'exe." : "Si cercano all'avvio dell'exe."
            : "Questa build non viene dal Setup.exe: si aggiorna ricompilandola.";
    }

    public IReadOnlyList<Prerequisite> Prerequisites { get; }

    public string McpPath { get; } = Path.Combine(AppContext.BaseDirectory, "mcp", "Soffritto.Mcp.exe");

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

    /// <summary>Rilegge cosa c'e' sulla macchina: si chiama all'apertura della scheda e da «Ricontrolla».</summary>
    public void Refresh()
    {
        var winget = ExecutableSearch.Find("winget.exe");
        WingetStatus = winget is null
            ? "winget non c'è: installa «App Installer» dal Microsoft Store, oppure i programmi dai loro siti."
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
            McpStatus = "Il server MCP arriva con il Setup.exe: in questa build non c'è.";
            CanRegisterMcp = false;
            return;
        }

        if (claude is null)
        {
            McpStatus = "Claude Code non è installato: per un altro client MCP registra a mano l'exe qui sotto come server stdio.";
            CanRegisterMcp = false;
            return;
        }

        CanRegisterMcp = true;
        McpStatus = RegisteredMcpCommand() switch
        {
            null => $"In Claude Code non c'è un server «{McpServerName}».",
            var command when string.Equals(Path.GetFullPath(command), McpPath, StringComparison.OrdinalIgnoreCase)
                => $"Registrato in Claude Code come «{McpServerName}».",
            var command => $"In Claude Code «{McpServerName}» lancia un altro exe ({command}): «Registra» lo sostituisce con questo.",
        };
    }

    // Il server registrato per l'utente (claude mcp add --scope user) sta in ~\.claude.json.
    // Leggere il file evita «claude mcp get», che per controllare il server lo avvia.
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
            VsixStatus = "L'estensione arriva con il Setup.exe: in questa build non c'è.";
            CanInstallVsix = false;
        }
        else if (visualStudio is null)
        {
            VsixStatus = "Visual Studio non è installato: l'estensione serve solo lì.";
            CanInstallVsix = false;
        }
        else
        {
            VsixStatus = "Chiudi Visual Studio prima di installare: l'installer lo chiede comunque. Serve Visual Studio 2026 18.12 o successivo.";
            CanInstallVsix = true;
        }
    }

    // winget in una console sua: licenze, richieste di elevazione ed esito restano davanti a chi installa.
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

    // Un server con lo stesso nome si toglie prima: «claude mcp add» non sovrascrive.
    private void RegisterMcp()
    {
        OpenConsole($"claude mcp remove --scope user {McpServerName} & claude mcp add --scope user {McpServerName} -- \"{McpPath}\"");
    }

    // VSLauncher instrada il .vsix verso l'installer giusto, come il doppio click: VSIXInstaller
    // da riga di comando rifiuta le estensioni VisualStudio.Extensibility (docs/GUIDA.md, «Installazione dell'estensione»).
    private void InstallVsix()
    {
        var launcher = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Common Files", "Microsoft Shared", "MSEnv", "VSLauncher.exe");

        if (!File.Exists(launcher))
        {
            VsixStatus = $"VSLauncher.exe non trovato: installa con un doppio click su {VsixPath}.";
            return;
        }

        DetachedLauncher.Start($"\"{launcher}\" \"{VsixPath}\"", AppContext.BaseDirectory, newConsole: false);
    }

    private async Task CheckUpdatesAsync()
    {
        isCheckingUpdates = true;
        OnPropertyChanged(nameof(CanCheckUpdates));
        UpdateStatus = "Cerco l'ultima release…";
        try
        {
            UpdateStatus = await Installation.CheckForUpdatesAsync();
        }
        catch (Exception ex)
        {
            UpdateStatus = $"Ricerca non riuscita: {ex.Message}";
        }
        finally
        {
            isCheckingUpdates = false;
            OnPropertyChanged(nameof(CanCheckUpdates));
        }
    }

    // cmd /k: a comando finito la console resta aperta con l'esito. Le virgolette esterne le toglie
    // cmd stesso, quelle interne restano al comando.
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
        Status = found ?? "Non trovato.";
        CanInstall = IsMissing && canInstall;
    }
}

/// <summary>
/// Cerca un eseguibile nel PATH della macchina e dell'utente letti adesso dal registro: un
/// programma appena installato da winget non e' nel PATH che l'exe ha ricevuto all'avvio.
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

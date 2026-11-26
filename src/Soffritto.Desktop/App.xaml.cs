using System.Windows;
using Soffritto.Core.Configuration;
using Soffritto.Desktop.Infrastructure;
using Soffritto.Desktop.ViewModels;
using Soffritto.Presentation;

namespace Soffritto.Desktop;

public partial class App : Application
{
    public const string ProductName = "Soffritto";

    private SingleInstance? instance;
    private string? importPath;

    protected override void OnStartup(StartupEventArgs e)
    {
        // dotnet ef database update builds the backend, and by default leaves MSBuild nodes, the
        // MSBuild server and the shared compiler (VBCSCompiler) alive for other builds on the
        // machine to reuse. Inside the exe's job they would be killed on exit: with these
        // variables, inherited by every child process, those processes are never started.
        Environment.SetEnvironmentVariable("MSBUILDDISABLENODEREUSE", "1");
        Environment.SetEnvironmentVariable("DOTNET_CLI_USE_MSBUILD_SERVER", "0");
        Environment.SetEnvironmentVariable("UseSharedCompilation", "false");

        // If the job cannot be attached we carry on: reads are still cancelled on exit.
        ChildProcessJob.TryAttachCurrentProcess();

        base.OnStartup(e);

        // Before any data access: a wrong data folder must not fall back to the working one.
        if (ConfigurationFiles.DataFolderProblem is { } problem)
        {
            MessageBox.Show(problem, ProductName, MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(2);
            return;
        }

        if (!TryParseConfig(e.Args, out importPath))
        {
            MessageBox.Show(
                "Usage: Soffritto.exe [--config <json file>]",
                ProductName,
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            Shutdown(2);
            return;
        }

        instance = SingleInstance.Acquire(importPath ?? ConfigurationFiles.ConfigPath);
        if (instance is null)
        {
            Shutdown(0);
            return;
        }

        var configuration = ConfigurationStore.LoadAtStartup(importPath, out var notice);
        var usable = configuration is not null && configuration.Validate().Count == 0;
        ActiveConfiguration.Current = configuration ?? AppConfiguration.CreateDefault();

        var viewModel = new MainViewModel(usable ? EnvironmentDiscovery.Discover(ActiveConfiguration.Current) : []);
        var window = new MainWindow(viewModel, setupNotice: notice, openSetup: !usable, firstRun: Installation.IsFirstRun);
        window.RestartRequested += () => Restart(window);
        instance.OnShowRequested(() => Dispatcher.InvokeAsync(window.BringToFront));
        window.Show();

        if (Installation.IsInstalled)
        {
            _ = CheckForUpdatesAsync(viewModel);
        }
    }

    private static async Task CheckForUpdatesAsync(MainViewModel viewModel)
    {
        try
        {
            await Installation.CheckForUpdatesAsync();
            if (Installation.PendingVersion is { } version)
            {
                UiThread.Run(() => viewModel.UpdateNotice = $" · v{version} on exit");
            }
        }
        catch
        {
            // Without a network or a release the app works anyway: it tries again at next startup.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Installation.ApplyOnExit();
        instance?.Dispose();
        base.OnExit(e);
    }

    // A new configuration is applied by reopening the exe: the new copy starts outside this one's
    // job, which would otherwise close it on exit. It restarts without --config: the file has
    // already been imported, and importing it again would wipe what was just saved from setup.
    private void Restart(MainWindow window)
    {
        if (!window.ExitForRestart())
        {
            return;
        }

        instance?.Dispose();
        instance = null;

        Installation.SkipApplyOnExit();
        DetachedLauncher.Start($"\"{Environment.ProcessPath}\"", AppContext.BaseDirectory, newConsole: false);
    }

    private static bool TryParseConfig(string[] args, out string? path)
    {
        path = null;

        if (args.Length == 0)
        {
            return true;
        }

        if (args.Length == 2 && args[0] == "--config" && !string.IsNullOrWhiteSpace(args[1]))
        {
            path = System.IO.Path.GetFullPath(args[1]);
            return true;
        }

        return false;
    }
}

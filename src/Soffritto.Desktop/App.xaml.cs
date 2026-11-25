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
        // dotnet ef database update compila il backend, e per default lascia vivi nodi MSBuild,
        // server MSBuild e compilatore condiviso (VBCSCompiler) che altre build della macchina
        // possono riusare. Dentro il job dell'exe verrebbero uccisi alla chiusura: con queste
        // variabili, ereditate da tutti i processi figli, quei processi non nascono affatto.
        Environment.SetEnvironmentVariable("MSBUILDDISABLENODEREUSE", "1");
        Environment.SetEnvironmentVariable("DOTNET_CLI_USE_MSBUILD_SERVER", "0");
        Environment.SetEnvironmentVariable("UseSharedCompilation", "false");

        // Se il job non si aggancia si prosegue: resta la cancellazione delle letture alla chiusura.
        ChildProcessJob.TryAttachCurrentProcess();

        base.OnStartup(e);

        // Prima di qualunque accesso ai dati: una cartella dati sbagliata non ricade su quella di lavoro.
        if (ConfigurationFiles.DataFolderProblem is { } problem)
        {
            MessageBox.Show(problem, ProductName, MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(2);
            return;
        }

        if (!TryParseConfig(e.Args, out importPath))
        {
            MessageBox.Show(
                "Uso: Soffritto.exe [--config <file json>]",
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
                UiThread.Run(() => viewModel.UpdateNotice = $" · v{version} alla chiusura");
            }
        }
        catch
        {
            // Senza rete o senza release l'exe funziona lo stesso: si riprova al prossimo avvio.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Installation.ApplyOnExit();
        instance?.Dispose();
        base.OnExit(e);
    }

    // Una configurazione nuova si applica riaprendo l'exe: la copia nuova parte fuori dal job
    // di questa, che altrimenti la chiuderebbe uscendo. Riparte senza --config: il file e' gia'
    // stato importato, e reimportarlo cancellerebbe cio' che si e' appena salvato dal setup.
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

using System.Collections.ObjectModel;
using System.Windows.Input;
using Soffritto.Desktop.Infrastructure;
using Soffritto.Presentation;

namespace Soffritto.Desktop.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    // Le letture sopravvivono al comando che le ha chieste: si fermano solo con la finestra.
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<int, EnvironmentRuntime> runtimes = [];
    private readonly IUserPrompt prompt = new DialogPrompt();
    private readonly IClipboard clipboard = new ClipboardText();
    private readonly PlaywrightPreviewController playwrightPreview;

    // Sveglia la scansione dei processi subito dopo un avvio o uno stop, senza aspettare il giro.
    private readonly SemaphoreSlim scanRequested = new(0);
    private bool isLoading;
    private int activeOperations;

    public MainViewModel(IReadOnlyList<EnvironmentLayout> layouts)
    {
        WindowTitle = Soffritto.Core.Configuration.ConfigurationFiles.IsDataFolderOverridden
            ? $"{App.ProductName} · dati: {Soffritto.Core.Configuration.ConfigurationFiles.DataFolder}"
            : App.ProductName;

        // Osservabile: gli ambienti Docker si aggiungono e si tolgono senza riavvio.
        Environments = new ObservableCollection<EnvironmentViewModel>(layouts
            .Select(layout => new EnvironmentViewModel(layout, BeginOperation, EndOperation)));

        foreach (var environment in Environments)
        {
            Attach(environment);
        }

        Docker = new DockerPanelViewModel(
            lifetime.Token,
            definition => AddEnvironment(EnvironmentDiscovery.LayoutOf(ActiveConfiguration.Current, definition)),
            number =>
            {
                if (runtimes.TryGetValue(number, out var runtime))
                {
                    runtime.Controller.Refresh(full: true);
                }
            });
        playwrightPreview = new PlaywrightPreviewController(Environments.SelectMany(e => e.Previews).ToList());
        PullRequests = new PullRequestsViewModel(Environments, CheckoutAsync, createDocker: Docker.CreateForBranchesAsync);
        Notifications = new PullRequestNotifications(
            System.IO.Path.Combine(Soffritto.Core.Configuration.ConfigurationFiles.DataFolder, "notifiche.json"));
        PullRequests.AlertsRead += Notifications.Update;
        var settings = AppSettings.Current;
        Meeting = new StoryPointsMeetingViewModel(
            AzureDevOpsAccess.ReadStoriesAsync,
            AzureDevOpsAccess.SaveStoryPointsAsync,
            Browser.OpenCommand,
            AzureDevOpsAccess.WorkItemUrl,
            settings.MeetingInitials,
            !ThemeManager.IsDark,
            (initials, _) => settings.MeetingInitials = initials);

        RefreshAllCommand = new AsyncCommand(() =>
        {
            RefreshAll();
            return Task.CompletedTask;
        });

        foreach (var environment in Environments)
        {
            foreach (var card in environment.Repos)
            {
                card.RefreshExistence();
            }
        }
    }

    /// <summary>Avviso nella testata: vuoto sugli ambienti veri, lo valorizza solo la demo di Ctrl+D.</summary>
    public string DemoBanner => string.Empty;

    /// <summary>Il «+» della testata, che la demo nasconde.</summary>
    public string NewDockerVisibility => "Visible";

    public string WindowTitle { get; }

    /// <summary>Senza ambienti (primo avvio, configurazione da sistemare) le colonne lasciano il posto all'invito al setup.</summary>
    public string EmptyVisibility => Environments.Count == 0 ? "Visible" : "Collapsed";

    /// <summary>Versione dell'exe (Version del csproj) per il footer: "v1.0.0".</summary>
    public string AppVersion { get; } =
        $"v{System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "?"}";

    private string updateNotice = string.Empty;

    /// <summary>Accanto alla versione nel footer: un aggiornamento scaricato che si installa alla chiusura.</summary>
    public string UpdateNotice { get => updateNotice; set => SetProperty(ref updateNotice, value); }

    public ObservableCollection<EnvironmentViewModel> Environments { get; }

    public PullRequestsViewModel PullRequests { get; }

    /// <summary>Le azioni Docker globali: creazione degli ambienti (il «+»), seed, spazio, log e pool dei tunnel (setup).</summary>
    public DockerPanelViewModel Docker { get; }

    public PullRequestNotifications Notifications { get; }

    public StoryPointsMeetingViewModel Meeting { get; }

    /// <summary>
    /// Larghezza fissa di una colonna (DIP), tra i vecchi limiti di 480 e 720: le card hanno
    /// sempre la stessa forma, con molte colonne compare lo scroll orizzontale.
    /// </summary>
    public const double ColumnWidth = 550;

    /// <summary>Larghezza minima della griglia: una <see cref="ColumnWidth"/> per colonna aperta; le nascoste non contano.</summary>
    public double ColumnsMinWidth => MinWidthOf(Environments);

    public static double MinWidthOf(IEnumerable<EnvironmentViewModel> environments)
        => environments.Count(e => !e.IsCollapsed) * ColumnWidth;

    /// <summary>Vero mentre almeno una lettura o un'azione e' in corso in un ambiente: anima la barra in alto.</summary>
    public bool IsLoading { get => isLoading; private set => SetProperty(ref isLoading, value); }

    public ICommand RefreshAllCommand { get; }

    public void Start()
    {
        RefreshAll();
        _ = ScanProcessesAsync(lifetime.Token);
        _ = RefreshPeriodicallyAsync(lifetime.Token);
        _ = playwrightPreview.RunAsync(lifetime.Token);
        _ = SystemLoad.Current.RunAsync(lifetime.Token);
        Docker.Start();
        WatchConfiguration();
    }

    public void Shutdown()
    {
        // Prima di fermare tutto: con un'azione in corso nell'exe la pulizia salta.
        if (AppSettings.Current.DockerCleanupOnExit && !AppSettings.Current.IsDemo && activeOperations == 0 && !Docker.IsWorking)
        {
            try
            {
                DockerCleanup.StartDetached(TimeSpan.FromSeconds(8));
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or System.IO.IOException or UnauthorizedAccessException)
            {
            }
        }

        lifetime.Cancel();
        configurationWatcher?.Dispose();

        foreach (var environment in Environments)
        {
            environment.CloseNote();
        }
    }

    /// <summary>Porta l'ambiente <paramref name="number"/> sui branch indicati: vedi <see cref="EnvironmentController.CheckoutAsync"/>.</summary>
    public Task<string> CheckoutAsync(int number, IReadOnlyList<(string Repository, string Branch)> targets)
        => runtimes.TryGetValue(number, out var runtime)
            ? runtime.Controller.CheckoutAsync(targets)
            : Task.FromResult($"Ambiente {number} non c'e' piu'");

    /// <summary>
    /// Una colonna nuova (un ambiente Docker appena creato), senza riavvio: controller, strumenti,
    /// pannello PR e anteprime la vedono subito. Va chiamato sul thread della UI.
    /// </summary>
    public void AddEnvironment(EnvironmentLayout layout)
    {
        if (runtimes.ContainsKey(layout.Number))
        {
            return;
        }

        var environment = new EnvironmentViewModel(layout, BeginOperation, EndOperation);
        Attach(environment);

        var index = 0;
        while (index < Environments.Count && Environments[index].Number < environment.Number)
        {
            index++;
        }

        Environments.Insert(index, environment);
        PullRequests.Track(environment);
        playwrightPreview.Replace(Environments.SelectMany(e => e.Previews).ToList());
        OnPropertyChanged(nameof(ColumnsMinWidth));
        OnPropertyChanged(nameof(EmptyVisibility));

        foreach (var card in environment.Repos)
        {
            card.RefreshExistence();
        }

        runtimes[environment.Number].Controller.Refresh(full: true);
        RequestScan();
    }

    /// <summary>
    /// Toglie la colonna di un ambiente (distrutto): le sue letture si fermano con il suo token.
    /// Va chiamato sul thread della UI.
    /// </summary>
    public void RemoveEnvironment(int number)
    {
        if (!runtimes.Remove(number, out var runtime))
        {
            return;
        }

        runtime.Lifetime.Cancel();
        var environment = Environments.First(e => e.Number == number);
        environment.CloseNote();
        PullRequests.Untrack(environment);
        Environments.Remove(environment);
        playwrightPreview.Replace(Environments.SelectMany(e => e.Previews).ToList());
        OnPropertyChanged(nameof(ColumnsMinWidth));
        OnPropertyChanged(nameof(EmptyVisibility));
    }

    private System.IO.FileSystemWatcher? configurationWatcher;
    private CancellationTokenSource? configurationDebounce;

    // Gli ambienti Docker si creano e si distruggono anche da fuori (server MCP, CLI): config.json
    // si osserva e le colonne Docker seguono il file. Gli ambienti locali cambiano solo col setup,
    // che riavvia l'exe.
    private void WatchConfiguration()
    {
        if (Soffritto.Core.Configuration.ConfigurationFiles.DataFolderProblem is not null)
        {
            return;
        }

        var path = Soffritto.Core.Configuration.ConfigurationFiles.ConfigPath;
        configurationWatcher = new System.IO.FileSystemWatcher(System.IO.Path.GetDirectoryName(path)!, System.IO.Path.GetFileName(path))
        {
            NotifyFilter = System.IO.NotifyFilters.LastWrite | System.IO.NotifyFilters.FileName | System.IO.NotifyFilters.Size,
        };
        configurationWatcher.Changed += (_, _) => ConfigurationChanged();
        configurationWatcher.Created += (_, _) => ConfigurationChanged();
        configurationWatcher.Renamed += (_, _) => ConfigurationChanged();
        configurationWatcher.EnableRaisingEvents = true;
    }

    // La scrittura e' tmp + move: si rilegge una volta, poco dopo l'ultimo evento.
    private void ConfigurationChanged()
    {
        var debounce = new CancellationTokenSource();
        Interlocked.Exchange(ref configurationDebounce, debounce)?.Cancel();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(400, debounce.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (Soffritto.Core.Configuration.ConfigurationFiles.TryLoad(out _) is { } file && !lifetime.IsCancellationRequested)
            {
                UiThread.Run(() => FollowDockerEnvironments(file));
            }
        });
    }

    private void FollowDockerEnvironments(Soffritto.Core.Configuration.AppConfiguration file)
    {
        var current = ActiveConfiguration.Current;
        var docker = Soffritto.Core.Configuration.EnvironmentKind.Docker;
        var removed = current.Environments
            .Where(e => e.Kind == docker && file.Environment(e.Number) is null)
            .Select(e => e.Number)
            .ToList();
        foreach (var number in removed)
        {
            current.Environments.RemoveAll(e => e.Number == number);
            RemoveEnvironment(number);
        }

        foreach (var added in file.Environments.Where(e => e.Kind == docker && current.Environment(e.Number) is null).ToList())
        {
            current.Environments.Add(added);
            AddEnvironment(EnvironmentDiscovery.LayoutOf(current, added));
        }
    }

    // Controller e strumenti di una colonna, con un token proprio legato a quello della finestra:
    // una colonna tolta ferma le sue letture senza toccare le altre.
    private void Attach(EnvironmentViewModel environment)
    {
        var columnLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var controller = new EnvironmentController(environment, prompt, clipboard, columnLifetime.Token);
        var devTools = new DevToolsController(environment, RequestScan, controller.RefreshTunnel, columnLifetime.Token);
        controller.StopServices = devTools.StopEverythingAsync;
        controller.DatabaseOffline = environment.Docker is not null
            ? () => devTools.DatabaseOffline
            : () => DockerOwningDatabaseOf(environment)?.Tools.DatabaseOffline;
        devTools.RefreshColumn = () =>
        {
            controller.Refresh(full: false);
            foreach (var follower in FollowersOf(environment))
            {
                follower.Controller.Refresh(full: false);
            }
        };
        environment.CollapsedChanged += () => OnPropertyChanged(nameof(ColumnsMinWidth));
        runtimes[environment.Number] = new EnvironmentRuntime(controller, devTools, columnLifetime);

        if (environment.Docker is { } docker)
        {
            docker.DestroyCommand = new AsyncCommand(() => DestroyDockerAsync(environment.Number));
            docker.RepairEnvCommand = new AsyncCommand(() => RepairEnvAsync(environment, controller, columnLifetime.Token));
        }
    }

    // Un ambiente locale il cui backend punta al database nel container di uno slot Docker
    // (127.0.0.1,<porta del db dello slot>) segue la fase di quell'ambiente: database spento o in
    // avvio come «non letta», e rilettura quando il database torna.
    private EnvironmentRuntime? DockerOwningDatabaseOf(EnvironmentViewModel local)
    {
        if (LocalDatabasePort(local) is not { } port)
        {
            return null;
        }

        var owner = Environments.FirstOrDefault(e => e.Docker?.Slot?.DatabasePort == port);
        return owner is not null && runtimes.TryGetValue(owner.Number, out var runtime) ? runtime : null;
    }

    private List<EnvironmentRuntime> FollowersOf(EnvironmentViewModel docker)
    {
        if (docker.Docker?.Slot?.DatabasePort is not { } port)
        {
            return [];
        }

        return Environments
            .Where(e => e.Docker is null && LocalDatabasePort(e) == port && runtimes.ContainsKey(e.Number))
            .Select(e => runtimes[e.Number])
            .ToList();
    }

    private static int? LocalDatabasePort(EnvironmentViewModel local)
        => local.Repos.FirstOrDefault(r => r.Type == Soffritto.Core.Configuration.RepositoryType.Backend && r.IsPrimary)?.Location is { Settings: Soffritto.Core.Configuration.BackendConfiguration backend } location
            ? Soffritto.Core.Docker.DatabaseGuard.LocalPortOf(location.Path, Soffritto.Core.Configuration.AppConfiguration.ToProbeOptions(backend))
            : null;

    /// <summary>
    /// «Distruggi» di un ambiente Docker: controllo senza modifiche (codice che si perderebbe),
    /// conferma con il riepilogo, console del Compose Watch e programmi aperti sulle sue cartelle
    /// chiusi, poi la distruzione. La colonna
    /// sparisce quando la voce lascia config.json; il dettaglio va in «Operazioni» della colonna, l'esito della distruzione nel log della scheda «Docker» del setup.
    /// </summary>
    public async Task DestroyDockerAsync(int number)
    {
        if (Environments.FirstOrDefault(e => e.Number == number) is not { Docker: { } docker } environment)
        {
            return;
        }

        if (!environment.IsNotBusy)
        {
            environment.ActivityLine = $"Ambiente {number} non distrutto: c'e' un'azione in corso";
            return;
        }

        if (docker.Slot is not { } slot)
        {
            environment.ActivityLine = $"Ambiente {number} non distrutto: {docker.SlotError ?? "slot non calcolabile"}";
            return;
        }

        void Log(string line) => environment.Report($"distruggi: {line}");

        environment.BeginOperation();
        try
        {
            environment.Report("distruggi: controllo che nessun worktree abbia codice da perdere…");
            var configuration = ActiveConfiguration.Current;
            var (ok, problems) = await Task.Run(
                () => Soffritto.Core.Docker.DockerEnvironmentProvisioner.CheckDestroyableAsync(configuration, number, lifetime.Token),
                lifetime.Token).ConfigureAwait(false);
            if (!ok)
            {
                environment.Report($"Ambiente {number} non distrutto: {string.Join("; ", problems)}");
                return;
            }

            // Un programma chiuso a mano puo' restare vivo senza finestre (un'estensione dell'editor che
            // lo tiene in piedi): invece di bloccare, la conferma li elenca e Distruggi li termina.
            var holders = await Task.Run(() => DockerFolderHolders.Find(ProcessTable.ReadAll(), slot), lifetime.Token).ConfigureAwait(false);
            var removed = new List<ConfirmationRow>
            {
                new($"progetto {slot.Project}", "compose", "container e volume del database"),
                new("immagini dell'api e del web", "immagini"),
            };
            removed.AddRange(slot.Repositories.Select(r => new ConfirmationRow(r.Path, "worktree", "senza --force")));
            removed.Add(new(slot.ComposeFolder, "slot", "con gli env_file"));
            removed.Add(new("porte dell'ambiente rilasciate", "pool"));
            removed.Add(new("voce tolta da config.json, colonna chiusa", "config"));

            var confirmed = await prompt.ConfirmAsync(
                new Confirmation(
                    $"Distruggi l'ambiente {number}",
                    $"L'ambiente Docker {number} viene tolto del tutto; il database si rigenera dal seed con un ambiente nuovo. Restano i branch nei cloni sorgente e la nota dell'ambiente.",
                    [
                        new("Programmi da chiudere, senza salvare", holders.Select(h => new ConfirmationRow($"{h.Name} su {h.Folder}", h.ProcessId.ToString(), "con i figli")).ToList()),
                        new("Cosa viene tolto", removed),
                    ],
                    "Distruggi"),
                lifetime.Token).ConfigureAwait(false);
            if (!confirmed)
            {
                environment.Report($"Ambiente {number} non distrutto: annullato");
                return;
            }

            // Prima di down: con il watch acceso una regola rebuild riaccenderebbe il web a meta', e
            // una console del watch o dei log aperta bloccherebbe la cancellazione della cartella dello slot.
            Log(await Task.Run(() => DockerWatchConsole.StopAll(ProcessTable.Read([]), slot), lifetime.Token).ConfigureAwait(false));
            if (holders.Count > 0)
            {
                Log(await Task.Run(() => DockerFolderHolders.Stop(ProcessTable.ReadAll(), slot, holders), lifetime.Token).ConfigureAwait(false));
            }

            var error = await Task.Run(
                () => Soffritto.Core.Docker.DockerEnvironmentProvisioner.DestroyAsync(
                    configuration,
                    number,
                    removed => UiThread.Run(() =>
                    {
                        ActiveConfiguration.Current.Environments.RemoveAll(e => e.Number == removed && e.Kind == Soffritto.Core.Configuration.EnvironmentKind.Docker);
                        RemoveEnvironment(removed);
                    }),
                    Log,
                    lifetime.Token),
                lifetime.Token).ConfigureAwait(false);
            if (error is not null)
            {
                environment.Report($"Ambiente {number} non distrutto: {error}");
            }
            else
            {
                // La colonna non c'e' piu': l'esito resta nel log Docker del setup, il log delle operazioni si scarta.
                Docker.AppendLog($"Ambiente {number} distrutto; restano i branch nei cloni sorgente");
                UiThread.Run(() => OperationLogs.Remove(number));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            environment.Report($"Ambiente {number} non distrutto: {ex.Message}");
        }
        finally
        {
            environment.EndOperation();
        }
    }

    // «Ripara env»: riscrive gli env dei worktree e dei container, poi rilegge la colonna. Un
    // errore resta nella riga di attivita': la rilettura lo coprirebbe con «pronto».
    private static async Task RepairEnvAsync(EnvironmentViewModel environment, EnvironmentController controller, CancellationToken cancellationToken)
    {
        if (!environment.IsNotBusy)
        {
            environment.ActivityLine = "Ripara env non avviato: c'e' un'azione in corso";
            return;
        }

        environment.BeginOperation();
        try
        {
            environment.Report("ripara env: riscrivo gli env dei worktree e dei container…");
            var configuration = ActiveConfiguration.Current;
            var error = await Task.Run(
                () => Soffritto.Core.Docker.DockerEnvironmentProvisioner.RepairEnvAsync(
                    configuration,
                    environment.Number,
                    line => environment.Report($"ripara env: {line}"),
                    cancellationToken),
                cancellationToken).ConfigureAwait(false);
            if (error is not null)
            {
                environment.Report($"Ripara env non riuscito: {error}");
                return;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            environment.Report($"Ripara env non riuscito: {ex.Message}");
            return;
        }
        finally
        {
            environment.EndOperation();
        }

        controller.Refresh(full: true);
    }

    private sealed record EnvironmentRuntime(EnvironmentController Controller, DevToolsController Tools, CancellationTokenSource Lifetime);

    /// <summary>Le azioni di scrittura in corso, una riga per ambiente occupato.</summary>
    public bool HasWriteInProgress(out string description)
    {
        var busy = Environments
            .Where(e => !e.IsNotBusy)
            .Select(e => $"Ambiente {e.Number}: {e.ActivityLine}")
            .ToList();

        description = string.Join("; ", busy);
        return busy.Count > 0;
    }

    // Gli ambienti con un'azione in corso non vengono riletti: la sessione esce da sola finche'
    // il gate e' occupato, e l'azione rilegge quando finisce. Le PR si rileggono anche a pannello
    // chiuso: le notifiche devono accorgersi delle novita'.
    private void RefreshAll()
    {
        foreach (var runtime in runtimes.Values)
        {
            runtime.Controller.Refresh(full: true);
        }

        _ = PullRequests.RefreshAsync();
    }

    // Solo l'exe rilegge da solo: la VSIX resta a refresh manuale.
    private async Task RefreshPeriodicallyAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            UiThread.Run(RefreshAll);
        }
    }

    private void RequestScan() => scanRequested.Release();

    // Lo stato di Kestrel, dev server e app cambia anche fuori dall'exe (una console chiusa a
    // mano, un debug avviato da Visual Studio): la barra strumenti si allinea ogni pochi secondi.
    // E' una lettura locale dei processi, non tocca git, database o rete.
    private async Task ScanProcessesAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var apiProcesses = ActiveConfiguration.Current.Repositories.OfType<Soffritto.Core.Configuration.BackendConfiguration>().Select(b => b.ApiProcess).ToList();
                var scan = await Task.Run(() => ProcessTable.Read(apiProcesses), cancellationToken).ConfigureAwait(false);

                // I container degli ambienti Docker: una lettura di docker ps filtrata sull'etichetta
                // della cartella dati, solo se c'e' almeno un ambiente Docker.
                var docker = ActiveConfiguration.Current.DockerEnvironments.Any()
                    ? await Soffritto.Core.Docker.DockerStatusReader.ReadAsync(cancellationToken).ConfigureAwait(false)
                    : null;

                // Prontezza di db, api e web dei container accesi: login e richieste HTTP brevi, in
                // parallelo per tutte le colonne, prima di ricalcolare le card sul thread della UI.
                if (docker is { IsReachable: true })
                {
                    var tools = new List<DevToolsController>();
                    UiThread.Run(() => tools.AddRange(runtimes.Values.Select(r => r.Tools)));
                    await Task.WhenAll(tools.Select(t => t.ProbeAsync(docker, cancellationToken))).ConfigureAwait(false);
                }

                UiThread.Run(() =>
                {
                    foreach (var runtime in runtimes.Values)
                    {
                        runtime.Tools.Apply(scan, docker);
                    }
                });
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // WMI non disponibile per un giro: la barra resta com'era e si riprova al successivo.
            }

            try
            {
                await scanRequested.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void BeginOperation()
    {
        if (Interlocked.Increment(ref activeOperations) == 1)
        {
            IsLoading = true;
        }
    }

    private void EndOperation()
    {
        if (Interlocked.Decrement(ref activeOperations) == 0)
        {
            IsLoading = false;
        }
    }
}

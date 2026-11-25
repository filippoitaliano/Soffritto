using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using Soffritto.Core;
using Soffritto.Core.AzureDevOps;
using Soffritto.Core.Configuration;
using Soffritto.Desktop.Infrastructure;

namespace Soffritto.Desktop.ViewModels;

/// <summary>Una voce della tendina del tipo di repository.</summary>
public sealed record TypeOption(RepositoryType Value, string Label);

/// <summary>
/// Il pannello di setup: lavora su una copia della configurazione attiva (catalogo dei
/// repository e ambienti locali) e la valida mentre si scrive. Gli ambienti Docker restano nella
/// copia ma non si mostrano: si creano e si distruggono dalla finestra principale. Salvare scrive la configurazione sotto il lock di
/// <see cref="ConfigurationFiles.Update"/> (le voci Docker e i seed.path scritti a exe aperto
/// restano), clona le cartelle e i cloni sorgente mancanti, crea i dev tunnel che mancano e chiede
/// il riavvio dell'exe, rimandato se c'e' un'operazione Docker in corso.
/// </summary>
public sealed partial class SetupViewModel : ObservableObject
{
    /// <summary>Il tooltip dei campi bloccati dagli ambienti Docker esistenti.</summary>
    public const string DockerLockToolTip = "Bloccato: esistono ambienti Docker che lo usano, e cambiarlo sposterebbe worktree, database o porte gia' in uso. Distruggi prima quegli ambienti con «Distruggi» nella loro colonna.";

    private readonly Func<string?> restart;

    // I seed.path di config.json all'apertura (e dopo ogni Salva): dicono se «Allinea seed» li
    // ha cambiati mentre il setup era aperto.
    private readonly Dictionary<string, string> openedSeedPaths = new(StringComparer.OrdinalIgnoreCase);
    private AppConfiguration draft;
    private string root = string.Empty;
    private string newPat = string.Empty;
    private string errorsText = string.Empty;
    private string warningsText = string.Empty;
    private string azureStatus = string.Empty;
    private string notice;
    private bool isWorking;

    /// <param name="restart">Chiede il riavvio dell'exe: null se partito, altrimenti il motivo per cui si rimanda.</param>
    public SetupViewModel(AppConfiguration current, string? notice, Func<string?> restart)
    {
        this.restart = restart;
        this.notice = notice ?? string.Empty;
        draft = Clone(current);
        RememberSeedPaths(ConfigurationFiles.TryLoad(out _));

        SaveCommand = new AsyncCommand(SaveAsync);
        TestAzureCommand = new AsyncCommand(TestAzureAsync);
        AddRepositoryCommand = new RelayCommand(_ => AddRepository());
        MoveUpCommand = new RelayCommand(item => Move(item, -1));
        MoveDownCommand = new RelayCommand(item => Move(item, +1));
        RemoveRepositoryCommand = new RelayCommand(item =>
        {
            if (item is RepositoryCardEditor editor && Repositories.Remove(editor))
            {
                CatalogChanged();
            }
        });
        AddEnvironmentCommand = new RelayCommand(_ => AddEnvironment());
        RemoveEnvironmentCommand = new RelayCommand(item =>
        {
            if (item is EnvironmentEditor editor && EnvironmentsList.Remove(editor))
            {
                Refresh();
            }
        });
        OpenExeFolderCommand = new RelayCommand(_ => Browser.OpenFolder(AppContext.BaseDirectory));

        LoadDraft();
    }

    /// <summary>Esistono ambienti Docker: i campi globali che li spostano (worktree, porte dei database) sono in sola lettura.</summary>
    public bool HasDockerEnvironments => draft.DockerEnvironments.Any();

    public bool IsDockerGlobalEditable => !HasDockerEnvironments;

    public string? DockerGlobalToolTip => HasDockerEnvironments ? DockerLockToolTip : null;

    /// <summary>Il modello degli ambienti Docker come testo: vuoto = il primo ambiente locale.</summary>
    public string TemplateEnvironmentText
    {
        get => draft.Docker.TemplateEnvironment?.ToString() ?? string.Empty;
        set
        {
            var text = value?.Trim() ?? string.Empty;
            draft.Docker.TemplateEnvironment = int.TryParse(text, out var number) ? number : text.Length == 0 ? null : 0;
            Revalidate();
        }
    }

    public string WorktreeRootHint => $"Vuota = {DockerDefaults.WorktreeRoot(root.Length > 0 ? root : "<cartella dei cloni>")}";

    private DockerPanelViewModel? pool;

    /// <summary>
    /// Le azioni Docker che valgono subito, fuori dal draft: il pool di tunnel della scheda
    /// «Tunnel», seed, spazio e log della scheda «Docker». Dopo ogni azione sul pool il draft
    /// riprende i tunnel della configurazione attiva, cosi' il Salva non rimette un tunnel
    /// eliminato e non toglie un tunnel aggiunto. Un seed nuovo di «Allinea seed» entra nella card
    /// del backend se il setup non ne ha cambiato il percorso (al Salva vince comunque, vedi Merge).
    /// </summary>
    public DockerPanelViewModel? Pool
    {
        get => pool;
        init
        {
            pool = value;
            if (value is not null)
            {
                value.PoolTunnelsChanged = () =>
                {
                    PoolTunnels.Clear();
                    foreach (var tunnel in ActiveConfiguration.Current.TunnelPool.Tunnels)
                    {
                        PoolTunnels.Add(new PoolTunnelEditor(new PoolTunnelConfiguration { Description = tunnel.Description, Label = tunnel.Label }, Revalidate));
                    }
                };
                value.SeedPathChanged = (repositoryId, path) =>
                {
                    var opened = openedSeedPaths.GetValueOrDefault(repositoryId, string.Empty);
                    if (Repositories.FirstOrDefault(r => string.Equals(r.Id, repositoryId, StringComparison.OrdinalIgnoreCase)) is { } editor
                        && string.Equals(editor.SeedPath, opened, StringComparison.OrdinalIgnoreCase))
                    {
                        editor.SeedPath = path;
                    }
                };
            }
        }
    }

    /// <summary>I tunnel del pool, ritrovati per Description e creati al Salva se l'account non li ha.</summary>
    public ObservableCollection<PoolTunnelEditor> PoolTunnels { get; } = [];


    private void RememberSeedPaths(AppConfiguration? file)
    {
        openedSeedPaths.Clear();
        foreach (var repository in file?.Repositories ?? [])
        {
            openedSeedPaths[repository.Id] = repository.Seed?.Path ?? string.Empty;
        }
    }

    public static IReadOnlyList<TypeOption> TypeOptions { get; } =
        Enum.GetValues<RepositoryType>().Select(t => new TypeOption(t, AppConfiguration.TypeName(t))).ToList();

    /// <summary>La configurazione su cui si lavora: i campi senza effetti sull'anteprima si legano direttamente qui.</summary>
    public AppConfiguration Draft
    {
        get => draft;
        private set => SetProperty(ref draft, value);
    }

    public string Root
    {
        get => root;
        set
        {
            if (SetProperty(ref root, value?.Trim() ?? string.Empty))
            {
                draft.Root = root;
                OnPropertyChanged(nameof(WorktreeRootHint));
                Refresh();
            }
        }
    }

    /// <summary>Il catalogo, una card per repository: l'ordine e' quello delle card nelle colonne.</summary>
    public ObservableCollection<RepositoryCardEditor> Repositories { get; } = [];

    public ObservableCollection<EnvironmentEditor> EnvironmentsList { get; } = [];

    public ObservableCollection<FolderPreviewRow> Preview { get; } = [];

    public bool UsesPat
    {
        get => draft.AzureDevOps.Authentication == AzureAuthentication.Pat;
        set
        {
            draft.AzureDevOps.Authentication = value ? AzureAuthentication.Pat : AzureAuthentication.Windows;
            OnPropertyChanged();
            OnPropertyChanged(nameof(UsesWindows));
            OnPropertyChanged(nameof(PatVisibility));
        }
    }

    public bool UsesWindows
    {
        get => !UsesPat;
        set => UsesPat = !value;
    }

    public string PatVisibility => UsesPat ? "Visible" : "Collapsed";

    /// <summary>Il PAT scritto nella casella: vuoto lascia quello gia' salvato.</summary>
    public string NewPat
    {
        get => newPat;
        set
        {
            if (SetProperty(ref newPat, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(PatHint));
            }
        }
    }

    public string PatHint => newPat.Length > 0
        ? $"Nuovo {AzureDevOpsConnection.DescribePat(newPat)}: al salvataggio viene cifrato con il tuo utente Windows."
        : !string.IsNullOrEmpty(draft.AzureDevOps.PatProtected)
            ? "PAT salvato e cifrato con il tuo utente Windows: lascia vuoto per tenerlo."
            : "Nessun PAT salvato.";

    public string AzureStatus { get => azureStatus; private set => SetProperty(ref azureStatus, value); }

    public string ErrorsText
    {
        get => errorsText;
        private set
        {
            if (SetProperty(ref errorsText, value))
            {
                OnPropertyChanged(nameof(ErrorsVisibility));
            }
        }
    }

    public string ErrorsVisibility => errorsText.Length > 0 ? "Visible" : "Collapsed";

    /// <summary>Configurazioni lecite ma da conoscere (doppioni di tipo, ambienti docker, VSIX): non bloccano il salvataggio.</summary>
    public string WarningsText
    {
        get => warningsText;
        private set
        {
            if (SetProperty(ref warningsText, value))
            {
                OnPropertyChanged(nameof(WarningsVisibility));
            }
        }
    }

    public string WarningsVisibility => warningsText.Length > 0 ? "Visible" : "Collapsed";

    /// <summary>Esito dell'avvio, dell'import o del salvataggio, e i passi della clonazione.</summary>
    public string Notice
    {
        get => notice;
        private set
        {
            if (SetProperty(ref notice, value))
            {
                OnPropertyChanged(nameof(NoticeVisibility));
            }
        }
    }

    public string NoticeVisibility => notice.Length > 0 ? "Visible" : "Collapsed";

    public bool IsWorking
    {
        get => isWorking;
        private set
        {
            if (SetProperty(ref isWorking, value))
            {
                OnPropertyChanged(nameof(IsIdle));
            }
        }
    }

    public bool IsIdle => !isWorking;

    public string SaveText => Preview.Any(r => r.WillClone) ? "Salva, clona e riavvia" : "Salva e riavvia";

    public ICommand SaveCommand { get; }

    public ICommand TestAzureCommand { get; }

    public ICommand AddRepositoryCommand { get; }

    public ICommand MoveUpCommand { get; }

    public ICommand MoveDownCommand { get; }

    public ICommand RemoveRepositoryCommand { get; }

    public ICommand AddEnvironmentCommand { get; }

    public ICommand RemoveEnvironmentCommand { get; }

    public ICommand OpenExeFolderCommand { get; }

    public ComponentsViewModel Components { get; } = new();

    /// <summary>Sostituisce i campi con un file json scelto dall'utente; si salva solo con Salva.</summary>
    public void Import(string path)
    {
        try
        {
            Draft = ConfigurationFiles.Parse(File.ReadAllText(path));
            LoadDraft();
            Notice = $"Caricato {path}: controlla i campi e premi Salva per applicarlo, o Annulla per lasciare la configurazione com'e'.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Notice = $"{path} non si legge: {ex.Message}";
        }
    }

    /// <summary>Da chiamare quando cambia un campo legato direttamente a <see cref="Draft"/> o a una voce del catalogo.</summary>
    public void Revalidate()
    {
        var configuration = Collect();
        ErrorsText = string.Join(Environment.NewLine, configuration.Validate().Select(e => $"• {e}"));
        WarningsText = string.Join(Environment.NewLine, configuration.Warnings().Select(w => $"• {w}"));
    }

    private void LoadDraft()
    {
        root = draft.Root;
        newPat = string.Empty;

        Repositories.Clear();
        foreach (var repository in draft.Repositories)
        {
            Repositories.Add(NewEditor(repository));
        }

        EnvironmentsList.Clear();
        foreach (var environment in draft.LocalEnvironments)
        {
            EnvironmentsList.Add(new EnvironmentEditor(environment.Root, environment.Number, Repositories, environment.Repositories, Refresh) { ProtectedDatabase = environment.ProtectedDatabase });
        }

        PoolTunnels.Clear();
        foreach (var tunnel in draft.TunnelPool.Tunnels)
        {
            PoolTunnels.Add(new PoolTunnelEditor(tunnel, Revalidate));
        }

        foreach (var name in new[]
        {
            nameof(Root), nameof(NewPat), nameof(PatHint), nameof(UsesPat), nameof(UsesWindows), nameof(PatVisibility),
            nameof(HasDockerEnvironments), nameof(IsDockerGlobalEditable), nameof(DockerGlobalToolTip), nameof(TemplateEnvironmentText), nameof(WorktreeRootHint),
        })
        {
            OnPropertyChanged(name);
        }

        Refresh();
    }

    // Un repository incluso in un ambiente Docker ha cartella, clone sorgente e servizio del
    // compose in sola lettura: cambiarli sposterebbe worktree e container gia' creati.
    private RepositoryCardEditor NewEditor(RepositoryConfiguration repository)
        => new(
            repository,
            Refresh,
            editor => editor.Type != RepositoryType.Generic && Repositories.FirstOrDefault(r => r.Type == editor.Type) == editor,
            draft.DockerEnvironments.Any(e => e.Repositories.Contains(repository.Id, StringComparer.OrdinalIgnoreCase)),
            () => draft.DockerWorktreeRoot,
            VerifySeedAsync,
            PreviewEnv);

    // L'anteprima risolve gli env del repository su uno slot Docker fittizio (il numero libero piu'
    // alto, senza porte del pool) con l'ambiente modello del draft. Mostra nomi, classi e origini:
    // i valori no, perche' le costanti del modello possono contenere connessioni con password.
    private string PreviewEnv(RepositoryCardEditor editor)
    {
        try
        {
            var configuration = Clone(Collect());
            if (configuration.Repository(editor.Id) is not { } repository)
            {
                return "Anteprima non calcolabile: id del repository non valido.";
            }

            if (repository.Env is null)
            {
                return "Nessun env: scegli un adapter o premi «Valori proposti».";
            }

            var number = Enumerable.Range(1, AppConfiguration.MaxEnvironmentNumber).Reverse().First(n => configuration.Environment(n) is null);
            var ids = new[] { configuration.Primary(RepositoryType.Backend)?.Id, configuration.Primary(RepositoryType.Frontend)?.Id, repository.Id }
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var environment = new EnvironmentDefinition { Number = number, Kind = EnvironmentKind.Docker, Repositories = ids };
            configuration.Environments.Add(environment);
            var slot = Soffritto.Core.Docker.DockerSlot.Of(configuration, environment);
            var entry = slot.Repositories.First(r => string.Equals(r.Id, repository.Id, StringComparison.OrdinalIgnoreCase));
            var resolution = Soffritto.Core.Env.EnvResolver.Resolve(configuration, entry, slot, new Dictionary<string, string>(), targetIsNew: false);

            var lines = new List<string> { $"Slot fittizio {number}, ambiente modello {configuration.TemplateNumber()?.ToString() ?? "nessuno"}, senza porte del pool." };
            lines.Add($"Worktree ({resolution.Local.Count}):");
            lines.AddRange(resolution.Local.Select(Line));
            lines.Add($"Container ({resolution.Container.Count}):");
            lines.AddRange(resolution.Container.Select(Line));
            lines.Add(resolution.Warnings.Count == 0 ? "Nessun avviso." : "Avvisi:");
            lines.AddRange(resolution.Warnings.Select(w => $"  ⚠ {w}"));
            return string.Join(Environment.NewLine, lines);
        }
        catch (Exception ex)
        {
            return $"Anteprima non calcolabile: {ex.Message}";
        }

        static string Line(Soffritto.Core.Env.EnvVariable variable) => variable.Class switch
        {
            Soffritto.Core.Env.EnvClass.SharedSecret => $"  {variable.Name} · (segreto da {variable.Origin})",
            Soffritto.Core.Env.EnvClass.Constant => $"  {variable.Name} · costante, da {variable.Origin}",
            _ => $"  {variable.Name} · per slot",
        };
    }

    // «Verifica seed» sul draft, anche non salvato: un SQL Server temporaneo ripristina il seed e
    // lo confronta con le migrazioni del codice. Con campi da sistemare non parte.
    private async Task VerifySeedAsync(RepositoryCardEditor editor)
    {
        var configuration = Collect();
        if (configuration.Validate() is { Count: > 0 } errors)
        {
            editor.SeedVerifyText = $"Verifica non avviata, il setup ha campi da sistemare: {string.Join(" ", errors)}";
            return;
        }

        if (editor.Current.Seed is not { Path.Length: > 0 } seed)
        {
            editor.SeedVerifyText = "Verifica non avviata: manca il percorso del seed.";
            return;
        }

        if (!ReferenceEquals(configuration.PrimarySeed(), editor.Current))
        {
            editor.SeedVerifyText = "Verifica non avviata: vale per il seed del backend predefinito.";
            return;
        }

        if (!File.Exists(seed.Path))
        {
            editor.SeedVerifyText = $"il file del seed non esiste: {seed.Path}";
            return;
        }

        editor.IsVerifyingSeed = true;
        editor.SeedVerifyText = "Verifica seed: avvio…";
        var copy = Clone(configuration);
        try
        {
            var result = await Task.Run(() => Soffritto.Core.Docker.Seed.SeedVerifier.VerifyAsync(
                copy,
                line => UiThread.Run(() => editor.SeedVerifyText = $"Verifica seed: {line}")));
            editor.SeedVerifyText = Describe(result);
        }
        catch (Exception ex)
        {
            editor.SeedVerifyText = $"Verifica seed non riuscita: {ex.Message}";
        }
        finally
        {
            editor.IsVerifyingSeed = false;
        }
    }

    private static string Describe(Soffritto.Core.Docker.Seed.SeedVerification result)
    {
        if (result.Error is { } error)
        {
            return $"Verifica seed non riuscita: {error}";
        }

        var unknown = result.Contexts.SelectMany(c => c.Unknown).Distinct(StringComparer.Ordinal).ToList();
        var contexts = result.Contexts.Select(c => $"{c.Context}: ultima {c.LastApplied ?? "nessuna"}, {c.MissingOnDev} migrazioni di dev mancanti");
        return $"{(result.Compatible ? "compatibile" : "non compatibile")}. {string.Join("; ", contexts)}."
            + (unknown.Count > 0 ? $" Migrazioni del seed che il codice non conosce: {string.Join(", ", unknown)}." : string.Empty);
    }

    // Il primo libero tra generico, generico2, ...: un id valido e unico, da rinominare a piacere.
    private void AddRepository()
    {
        var id = Enumerable.Range(1, 99)
            .Select(i => i == 1 ? "generico" : $"generico{i}")
            .First(candidate => !Repositories.Any(r => string.Equals(r.Id, candidate, StringComparison.OrdinalIgnoreCase)));

        Repositories.Add(NewEditor(new GenericRepoConfiguration { Id = id, Folder = $"{id}-{AppConfiguration.NumberPlaceholder}" }));
        CatalogChanged();
    }

    private void Move(object? item, int delta)
    {
        if (item is not RepositoryCardEditor editor)
        {
            return;
        }

        var index = Repositories.IndexOf(editor);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= Repositories.Count)
        {
            return;
        }

        Repositories.Move(index, target);
        CatalogChanged();
    }

    // Un ambiente nuovo prende il primo numero dopo l'ultimo locale che nessun ambiente Docker usa, e
    // le inclusioni dell'ambiente locale col numero piu' alto: di solito si aggiunge un ambiente uguale agli altri.
    private void AddEnvironment()
    {
        var docker = draft.DockerEnvironments.Select(e => e.Number).ToHashSet();
        var number = (EnvironmentsList.Select(e => (int?)e.Number).Max() ?? 0) + 1;
        while (docker.Contains(number))
        {
            number++;
        }

        var last = EnvironmentsList.OrderBy(e => e.Number).LastOrDefault();
        var included = last?.Inclusions.Where(i => i.IsIncluded).Select(i => i.Repository.Id).ToList() ?? Repositories.Select(r => r.Id).ToList();

        EnvironmentsList.Add(new EnvironmentEditor(null, number, Repositories, included, Refresh));
        Refresh();
    }

    private void CatalogChanged()
    {
        foreach (var environment in EnvironmentsList)
        {
            environment.Sync(Repositories);
        }

        Refresh();
    }

    private void Refresh()
    {
        var configuration = Collect();

        Preview.Clear();
        if (Path.IsPathFullyQualified(root))
        {
            foreach (var environment in configuration.LocalEnvironments)
            {
                if (environment.Root is { } environmentRoot && !Path.IsPathFullyQualified(environmentRoot))
                {
                    continue;
                }

                Preview.Add(new FolderPreviewRow(
                    environment.Number,
                    configuration.RepositoriesOf(environment)
                        .Where(r => r.Repository.Folder.Length > 0 && r.Repository.Folder.IndexOfAny(Path.GetInvalidPathChars()) < 0)
                        .Select(FolderPreview.Of)
                        .ToList()));
            }
        }

        OnPropertyChanged(nameof(SaveText));
        Revalidate();
    }

    // I campi con una vista propria (catalogo, ambienti locali, elenchi) tornano nel draft solo
    // qui; gli ambienti Docker del draft, che il setup non mostra, restano com'erano.
    private AppConfiguration Collect()
    {
        var docker = draft.DockerEnvironments.ToList();

        foreach (var editor in Repositories)
        {
            editor.Collect();
        }

        draft.Repositories = Repositories.Select(r => r.Current).ToList();
        draft.Environments = EnvironmentsList.Select(e => e.ToDefinition()).Concat(docker).ToList();
        draft.TunnelPool.Tunnels = PoolTunnels.Select(t => t.Tunnel).ToList();
        return draft;
    }

    private async Task TestAzureAsync()
    {
        var azure = draft.AzureDevOps;
        if (!azure.IsConfigured)
        {
            AzureStatus = "Scrivi prima l'URL della collection.";
            return;
        }

        var pat = UsesPat ? (newPat.Length > 0 ? newPat : ConfigurationStore.PatOf(azure)) : null;
        if (UsesPat && pat is null)
        {
            AzureStatus = "Manca il PAT.";
            return;
        }

        AzureStatus = "Provo la connessione…";
        try
        {
            using var connection = new AzureDevOpsConnection(azure.CollectionUrl, azure.Project, pat);
            var who = await connection.WhoAmIAsync();
            AzureStatus = $"Connesso come {who}.";
        }
        catch (Exception ex)
        {
            AzureStatus = ex is TaskCanceledException ? "Azure DevOps non ha risposto entro 30 secondi." : ex.Message;
        }
    }

    private async Task SaveAsync()
    {
        if (AppSettings.Current.IsDemo)
        {
            Notice = "In modalità demo la configurazione non si salva: esci dalla demo con Ctrl+D.";
            return;
        }

        var configuration = Collect();
        var errors = configuration.Validate();
        if (errors.Count > 0)
        {
            Revalidate();
            Notice = "Ci sono campi da sistemare prima di salvare.";
            return;
        }

        IsWorking = true;
        try
        {
            if (newPat.Length > 0)
            {
                configuration.AzureDevOps.Pat = newPat;
            }

            // Salva una copia con il PAT gia' cifrato: il draft resta com'e' se la scrittura viene
            // rifiutata e si riprova.
            var saved = Clone(configuration);
            saved.AzureDevOps.Pat = configuration.AzureDevOps.Pat;
            ConfigurationStore.ProtectPat(saved.AzureDevOps);
            configuration.AzureDevOps.Pat = null;

            var log = new List<string>();
            var fileReadable = ConfigurationFiles.TryLoad(out _) is not null;
            AppConfiguration? written = null;
            var updateError = await Task.Run(() => ConfigurationFiles.Update(
                file => (written = Merge(file, saved, fileReadable, log)) is null ? "configurazione non composta" : null,
                ConfigurationFileFallback.Replace,
                log.Add));
            if (updateError is not null)
            {
                log.Insert(0, $"Configurazione non salvata: {updateError}");
                Notice = string.Join(Environment.NewLine, log);
                return;
            }

            // Il setup resta aperto se il riavvio si rimanda: riparte da cio' che e' stato scritto,
            // voci Docker e seed.path del file compresi.
            RememberSeedPaths(written);
            Draft = Clone(written!);
            LoadDraft();
            configuration = draft;
            NewPat = string.Empty;
            OnPropertyChanged(nameof(PatHint));

            log.Insert(0, "Configurazione salvata.");
            Notice = string.Join(Environment.NewLine, log);

            var failed = false;
            foreach (var row in Preview.SelectMany(r => r.Folders).Where(f => f.WillClone).ToList())
            {
                log.Add($"git clone {row.CloneUrl} → {row.Path}…");
                Notice = string.Join(Environment.NewLine, log);

                var outcome = await Task.Run(() => ProcessRunner.RunAsync(
                    "git",
                    $"clone \"{row.CloneUrl}\" \"{row.Path}\"",
                    Path.GetDirectoryName(row.Path)!,
                    timeoutSeconds: 1800));

                log[^1] = outcome.Success
                    ? $"✓ {row.FolderName} clonato"
                    : $"✗ {row.FolderName}: {outcome.Tail(2)}";
                Notice = string.Join(Environment.NewLine, log);
                failed |= !outcome.Success;
            }

            failed |= await CreateMissingSourceClonesAsync(configuration, log);

            if (failed)
            {
                Refresh();
                log.Add("Qualche clone non e' riuscito: la configurazione e' salvata, risolvi e premi di nuovo Salva (clona solo cio' che manca).");
                Notice = string.Join(Environment.NewLine, log);
                return;
            }

            var (createdPoolTunnels, tunnelsFailed) = await CreateMissingPoolTunnelsAsync(configuration, log);
            if (createdPoolTunnels || tunnelsFailed)
            {
                Refresh();
                log.Add(tunnelsFailed
                    ? "Qualche tunnel non e' stato creato: la configurazione e' salvata, risolvi e premi di nuovo Salva (crea solo cio' che manca)."
                    : "Configurazione salvata. Premi di nuovo Salva per riavviare.");
                Notice = string.Join(Environment.NewLine, log);
                return;
            }

            log.Add("Riavvio con la configurazione nuova…");
            Notice = string.Join(Environment.NewLine, log);
            if (restart() is { } postponed)
            {
                log[^1] = $"Riavvio rimandato: {postponed}.";
                Notice = string.Join(Environment.NewLine, log);
            }
        }
        catch (Exception ex)
        {
            Notice = $"Salvataggio non riuscito: {ex.Message}";
        }
        finally
        {
            IsWorking = false;
        }
    }

    // Dentro il lock di config.json: il file riletto diventa il draft, tranne cio' che scrivono
    // gli altri a exe aperto. Le voci Docker restano quelle del file (creazione e distruzione); un
    // seed.path cambiato nel file da quando il setup e' aperto vince se il setup non l'ha toccato.
    // Con il file assente o illeggibile (fileReadable falso) si scrive il draft com'e'.
    private AppConfiguration Merge(AppConfiguration file, AppConfiguration saved, bool fileReadable, List<string> log)
    {
        var result = Clone(saved);
        if (fileReadable)
        {
            result.Environments = result.LocalEnvironments.Concat(file.DockerEnvironments).OrderBy(e => e.Number).ToList();

            foreach (var repository in result.Repositories)
            {
                var opened = openedSeedPaths.GetValueOrDefault(repository.Id, string.Empty);
                var inFile = file.Repository(repository.Id)?.Seed?.Path ?? string.Empty;
                var inDraft = repository.Seed?.Path ?? string.Empty;
                if (string.Equals(inFile, opened, StringComparison.OrdinalIgnoreCase) || string.Equals(inFile, inDraft, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (string.Equals(inDraft, opened, StringComparison.OrdinalIgnoreCase))
                {
                    (repository.Seed ??= new SeedConfiguration()).Path = inFile;
                    log.Add($"seed.path di {repository.Id} preso da config.json ({inFile}): l'ha scritto «Allinea seed» mentre il setup era aperto.");
                }
                else
                {
                    log.Add($"seed.path di {repository.Id}: vince il valore del setup ({inDraft}); config.json aveva {inFile}, scritto mentre il setup era aperto.");
                }
            }
        }

        file.SchemaVersion = result.SchemaVersion;
        file.Root = result.Root;
        file.Repositories = result.Repositories;
        file.Environments = result.Environments;
        file.AzureDevOps = result.AzureDevOps;
        file.Docker = result.Docker;
        file.TunnelPool = result.TunnelPool;
        return result;
    }

    // Un clone sorgente configurato e assente nasce bare con il fetch di origin: da li' partono i
    // worktree degli ambienti Docker. Uno che c'e' non si tocca.
    private async Task<bool> CreateMissingSourceClonesAsync(AppConfiguration configuration, List<string> log)
    {
        var failed = false;
        foreach (var repository in configuration.Repositories.Where(r => r.SourceClone is { Length: > 0 } source && !Directory.Exists(source)))
        {
            if (repository.CloneUrl.Length == 0)
            {
                log.Add($"✗ {repository.Id}: clone sorgente non creato, manca l'URL di clone");
                failed = true;
                continue;
            }

            log.Add($"clone sorgente di {repository.Id} in {repository.SourceClone} (git init --bare e fetch)…");
            Notice = string.Join(Environment.NewLine, log);

            var error = await Task.Run(() => Soffritto.Core.Docker.SourceCloneRepository.InitAsync(repository.SourceClone!, repository.CloneUrl));
            log[^1] = error is null ? $"✓ {repository.Id} clone sorgente creato" : $"✗ {repository.Id}: {error}";
            Notice = string.Join(Environment.NewLine, log);
            failed |= error is not null;
        }

        return failed;
    }

    // I tunnel del pool che l'account non ha nascono qui, «Owner only» e senza porte: le porte e
    // l'accesso anonimo per porta arrivano accendendo «Tunnel» nell'intestazione di un ambiente.
    private async Task<(bool Created, bool Failed)> CreateMissingPoolTunnelsAsync(AppConfiguration configuration, List<string> log)
    {
        if (configuration.TunnelPool.Tunnels.Count == 0)
        {
            return (false, false);
        }

        log.Add("Controllo i dev tunnel (devtunnel list)…");
        Notice = string.Join(Environment.NewLine, log);

        var (tunnels, error) = await Task.Run(() => DevTunnelProvisioner.ListTunnelsAsync());
        if (tunnels is null)
        {
            log[^1] = $"✗ dev tunnel non controllati: {error}";
            return (false, true);
        }

        log.RemoveAt(log.Count - 1);

        var created = false;
        var failed = false;
        var descriptions = tunnels.Select(t => t.Description).ToHashSet(StringComparer.Ordinal);
        foreach (var description in DevTunnelProvisioner.MissingPoolDescriptions(configuration, descriptions))
        {
            var label = configuration.TunnelPool.Tunnels.First(t => t.Description == description).Label;
            log.Add($"devtunnel create {description} (tunnel del pool, Owner only, senza porte)…");
            Notice = string.Join(Environment.NewLine, log);

            var (tunnelId, createError) = await Task.Run(() => DevTunnelProvisioner.CreatePoolTunnelAsync(description, label));
            log[^1] = createError is null
                ? $"✓ tunnel del pool {description} creato: {tunnelId}"
                : $"✗ {description}: {createError}";
            Notice = string.Join(Environment.NewLine, log);

            created |= tunnelId is not null;
            failed |= createError is not null;
        }

        Notice = string.Join(Environment.NewLine, log);
        return (created, failed);
    }

    private static AppConfiguration Clone(AppConfiguration configuration)
        => ConfigurationFiles.Parse(ConfigurationFiles.Serialize(configuration));
}

/// <summary>
/// Una card del catalogo nel setup. La voce cambia classe quando cambia il tipo: le istanze di
/// ogni tipo restano in memoria, cosi' tornando a un tipo i suoi campi ricompaiono.
/// </summary>
public sealed class RepositoryCardEditor : ObservableObject
{
    private readonly Action changed;
    private readonly Func<RepositoryCardEditor, bool> isPrimary;
    private readonly Dictionary<RepositoryType, RepositoryConfiguration> cache = [];
    private readonly Func<string> worktreeRoot;
    private string portsText;
    private string appSettingsText = string.Empty;

    /// <param name="isDockerLocked">Il repository e' in un ambiente Docker: cartella, clone sorgente, compose, servizio e porte in sola lettura.</param>
    /// <param name="worktreeRoot">La cartella dei worktree Docker del draft, per proporre il clone sorgente.</param>
    public RepositoryCardEditor(
        RepositoryConfiguration repository,
        Action changed,
        Func<RepositoryCardEditor, bool> isPrimary,
        bool isDockerLocked = false,
        Func<string>? worktreeRoot = null,
        Func<RepositoryCardEditor, Task>? verifySeed = null,
        Func<RepositoryCardEditor, string>? previewEnv = null)
    {
        this.changed = changed;
        this.isPrimary = isPrimary;
        this.worktreeRoot = worktreeRoot ?? (() => string.Empty);
        IsDockerLocked = isDockerLocked;
        Current = repository;
        cache[repository.Type] = repository;
        portsText = FormatPorts(repository.DevTunnel?.Ports);
        LoadBackendViews();
        LoadEnvViews();

        AddManualCommand = new RelayCommand(_ => EditEnv(_ => EnvManualRows.Add(new EnvManualVariable())));
        RemoveManualCommand = new RelayCommand(item =>
        {
            if (item is EnvManualVariable row && EnvManualRows.Remove(row))
            {
                changed();
            }
        });
        AddSlotCommand = new RelayCommand(_ => EditEnv(_ => EnvSlotRows.Add(new EnvSlotRowEditor(new EnvSlotVariable(), false, changed))));
        RemoveSlotCommand = new RelayCommand(item =>
        {
            if (item is EnvSlotRowEditor { IsLocked: false } row && EnvSlotRows.Remove(row))
            {
                changed();
            }
        });
        ProposeEnvCommand = new RelayCommand(_ => ProposeEnv());
        PreviewEnvCommand = new RelayCommand(_ => EnvPreviewText = previewEnv?.Invoke(this) ?? string.Empty);

        VerifySeedCommand = new AsyncCommand(() => verifySeed is null || isVerifyingSeed ? Task.CompletedTask : verifySeed(this));

        ProposeSourceCloneCommand = new RelayCommand(_ =>
        {
            if (!IsDockerLocked && Current.RepositoryName.Length > 0 && this.worktreeRoot() is { Length: > 0 } folder)
            {
                SourceClone = Path.Combine(folder, "sorgenti", $"{Current.RepositoryName}.git");
                OnPropertyChanged(nameof(SourceClone));
            }
        });

        AddContextCommand = new RelayCommand(_ =>
        {
            Contexts.Add(new ContextConfiguration());
            changed();
        });
        RemoveContextCommand = new RelayCommand(item =>
        {
            if (item is ContextConfiguration context && Contexts.Remove(context))
            {
                changed();
            }
        });
    }

    /// <summary>La voce del catalogo con il tipo scelto; i campi con una vista propria ci tornano con <see cref="Collect"/>.</summary>
    public RepositoryConfiguration Current { get; private set; }

    public RepositoryType Type
    {
        get => Current.Type;
        set
        {
            if (value == Current.Type)
            {
                return;
            }

            Collect();
            var previous = Current;
            var next = cache.TryGetValue(value, out var cached) ? cached : Create(value);
            next.Id = previous.Id;
            next.CloneUrl = previous.CloneUrl;
            next.Folder = previous.Folder;
            next.DevTunnel = previous.DevTunnel;
            next.SourceClone = previous.SourceClone;
            next.Docker = previous.Docker;
            cache[value] = next;
            Current = next;
            LoadBackendViews();
            LoadEnvViews();

            OnPropertyChanged(string.Empty);
            changed();
        }
    }

    public string Id
    {
        get => Current.Id;
        set
        {
            Current.Id = value?.Trim() ?? string.Empty;
            OnPropertyChanged();
            changed();
        }
    }

    public string CloneUrl
    {
        get => Current.CloneUrl;
        set
        {
            Current.CloneUrl = value?.Trim() ?? string.Empty;
            OnPropertyChanged();
            changed();
        }
    }

    public string Folder
    {
        get => Current.Folder;
        set
        {
            Current.Folder = value?.Trim() ?? string.Empty;
            OnPropertyChanged();
            changed();
        }
    }

    /// <summary>Il repository si puo' condividere con una porta del pool di tunnel.</summary>
    public bool TunnelEnabled
    {
        get => Current.DevTunnel is not null;
        set
        {
            if (value == TunnelEnabled)
            {
                return;
            }

            Current.DevTunnel = value ? new DevTunnelConfiguration { Ports = ParsePorts(portsText) } : null;

            OnPropertyChanged();
            OnPropertyChanged(nameof(TunnelVisibility));
            changed();
        }
    }

    public string TunnelVisibility => TunnelEnabled ? "Visible" : "Collapsed";

    /// <summary>Le porte manuali, "8443/https, 3000/http": vuoto = quelle del tipo.</summary>
    public string TunnelPortsText
    {
        get => portsText;
        set
        {
            portsText = value ?? string.Empty;
            if (Current.DevTunnel is { } tunnel)
            {
                tunnel.Ports = ParsePorts(portsText);
                changed();
            }
        }
    }

    /// <summary>Il repository e' in un ambiente Docker: i campi che spostano worktree e container sono in sola lettura.</summary>
    public bool IsDockerLocked { get; }

    public bool IsDockerEditable => !IsDockerLocked;

    public string? DockerLockToolTip => IsDockerLocked ? SetupViewModel.DockerLockToolTip : null;

    /// <summary>Il clone bare da cui nascono i worktree degli ambienti Docker; vuoto = il repository non entra negli ambienti Docker.</summary>
    public string SourceClone
    {
        get => Current.SourceClone ?? string.Empty;
        set
        {
            var text = value?.Trim() ?? string.Empty;
            Current.SourceClone = text.Length > 0 ? text : null;
            changed();
        }
    }

    public ICommand ProposeSourceCloneCommand { get; }

    public string ComposeVisibility => Type == RepositoryType.Backend ? "Visible" : "Collapsed";

    // I campi della sezione Docker mostrano il valore che vale (il default del tipo se il campo
    // e' vuoto) e scrivono solo quando si cambiano: un campo lasciato com'e' resta vuoto nel file.
    public string DockerCompose
    {
        get => Current.DockerCompose;
        set => EditDocker(d => d.Compose = value?.Trim() ?? string.Empty);
    }

    public string DockerDockerfile
    {
        get => Current.DockerDockerfile;
        set => EditDocker(d => d.Dockerfile = value?.Trim() ?? string.Empty);
    }

    public string DockerFilesFallback
    {
        get => Current.Docker?.FilesFallback ?? string.Empty;
        set
        {
            EditDocker(d => d.FilesFallback = value?.Trim() ?? string.Empty);
            OnPropertyChanged();
        }
    }

    public string DockerService
    {
        get => Current.DockerService;
        set => EditDocker(d => d.Service = value?.Trim() ?? string.Empty);
    }

    /// <summary>Le porte pubblicate "base:porta", separate da virgole.</summary>
    public string DockerPublishedPortsText
    {
        get => string.Join(", ", Current.DockerPublishedPorts);
        set => EditDocker(d => d.PublishedPorts = SplitList(value, [',', ';']));
    }

    /// <summary>I file copiati dall'ambiente modello al worktree, uno per riga.</summary>
    public string DockerLocalFilesText
    {
        get => string.Join(Environment.NewLine, Current.Docker?.LocalFiles ?? []);
        set => EditDocker(d => d.LocalFiles = SplitList(value, ['\r', '\n']));
    }

    /// <summary>I sistemi di persistenza del seed: oggi solo SQL Server.</summary>
    public static IReadOnlyList<SeedKindOption> SeedKindOptions { get; } = [new(SeedKind.SqlServer, "SQL Server")];

    // Senza seed i campi mostrano i valori di un seed nuovo; il primo campo cambiato lo crea.
    private SqlServerSeedConfiguration ShownSqlServer => Current.Seed?.SqlServer ?? new SqlServerSeedConfiguration();

    public SeedKind SeedKind
    {
        get => Current.Seed?.Kind ?? SeedKind.SqlServer;
        set => EditSeed(s => s.Kind = value);
    }

    public string SeedPath
    {
        get => Current.Seed?.Path ?? string.Empty;
        set
        {
            EditSeed(s => s.Path = value?.Trim() ?? string.Empty);
            OnPropertyChanged();
        }
    }

    public string SeedImage
    {
        get => ShownSqlServer.Image;
        set => EditSeed(s => s.SqlServer.Image = value?.Trim() ?? string.Empty);
    }

    public string SeedDatabaseName
    {
        get => ShownSqlServer.DatabaseName;
        set => EditSeed(s => s.SqlServer.DatabaseName = value?.Trim() ?? string.Empty);
    }

    /// <summary>La password del SQL Server dei container: non e' un segreto, protegge solo container su 127.0.0.1.</summary>
    public string SeedPassword
    {
        get => ShownSqlServer.Password;
        set => EditSeed(s => s.SqlServer.Password = value ?? string.Empty);
    }

    /// <summary>La memoria di SQL Server in MB, come testo: un valore che non si legge diventa 0 e la validazione lo segnala.</summary>
    public string SeedMemoryText
    {
        get => ShownSqlServer.MemoryLimitMb.ToString();
        set => EditSeed(s => s.SqlServer.MemoryLimitMb = int.TryParse(value?.Trim(), out var mb) ? mb : 0);
    }

    public string SeedService
    {
        get => ShownSqlServer.Service;
        set => EditSeed(s => s.SqlServer.Service = value?.Trim() ?? string.Empty);
    }

    public ICommand VerifySeedCommand { get; }

    private bool isVerifyingSeed;
    private string seedVerifyText = string.Empty;

    public bool IsVerifyingSeed
    {
        get => isVerifyingSeed;
        set
        {
            if (SetProperty(ref isVerifyingSeed, value))
            {
                OnPropertyChanged(nameof(CanVerifySeed));
            }
        }
    }

    public bool CanVerifySeed => !isVerifyingSeed;

    /// <summary>Esito di «Verifica seed», o il motivo per cui non e' partita.</summary>
    public string SeedVerifyText { get => seedVerifyText; set => SetProperty(ref seedVerifyText, value); }

    // Un seed nuovo parte dalla password proposta (G-7): il resto lo scrive l'utente.
    private void EditSeed(Action<SeedConfiguration> edit)
    {
        edit(Current.Seed ??= new SeedConfiguration { SqlServer = { Password = SqlServerSeedConfiguration.ProposedPassword } });
        changed();
    }

    private void EditDocker(Action<DockerServiceConfiguration> edit)
    {
        edit(Current.Docker ??= new DockerServiceConfiguration());
        changed();
    }

    private static List<string> SplitList(string? text, char[] separators)
        => (text ?? string.Empty).Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    public BackendConfiguration? Backend => Current as BackendConfiguration;

    public FrontendConfiguration? Frontend => Current as FrontendConfiguration;

    public AppRepoConfiguration? App => Current as AppRepoConfiguration;

    public string BackendVisibility => Backend is null ? "Collapsed" : "Visible";

    public string FrontendVisibility => Frontend is null ? "Collapsed" : "Visible";

    public string AppVisibility => App is null ? "Collapsed" : "Visible";

    /// <summary>Gli appsettings da cui leggere la connection string, uno per riga.</summary>
    public string AppSettingsText
    {
        get => appSettingsText;
        set => SetProperty(ref appSettingsText, value ?? string.Empty);
    }

    /// <summary>I nomi di database e di server da evidenziare sulla card dell'ambiente, separati da virgole.</summary>
    public string HighlightedTagsText
    {
        get => string.Join(", ", Backend?.HighlightedTags ?? []);
        set
        {
            if (Backend is { } backend)
            {
                backend.HighlightedTags = SplitList(value, [',', ';']);
                changed();
            }
        }
    }

    public ObservableCollection<ContextConfiguration> Contexts { get; } = [];

    public ICommand AddContextCommand { get; }

    public ICommand RemoveContextCommand { get; }

    public void Collect()
    {
        if (Current is BackendConfiguration backend)
        {
            backend.AppSettings = appSettingsText
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            backend.Contexts = Contexts.ToList();
        }

        if (Current.Env is { } env)
        {
            env.Manual = EnvManualRows.ToList();
            env.Slot = EnvSlotRows.Select(r => r.Variable).ToList();
        }
    }

    // Con ambienti Docker la variabile di connessione del backend resta com'e' (G-1, B30): il
    // container dell'api e la guardia del database dipendono da lei.
    private void LoadEnvViews()
    {
        EnvManualRows.Clear();
        EnvSlotRows.Clear();
        if (Current.Env is not { } env)
        {
            return;
        }

        foreach (var manual in env.Manual)
        {
            EnvManualRows.Add(manual);
        }

        foreach (var slot in env.Slot)
        {
            EnvSlotRows.Add(new EnvSlotRowEditor(slot, IsDockerLocked && IsConnectionVariable(slot.Name), changed));
        }
    }

    private bool IsConnectionVariable(string name)
        => Current is BackendConfiguration backend
            && string.Equals(name, $"ConnectionStrings:{backend.ConnectionStringName}", StringComparison.OrdinalIgnoreCase);

    // I valori proposti del tipo sostituiscono la sezione; una riga di connessione bloccata resta quella che c'e'.
    private void ProposeEnv()
    {
        Collect();
        var locked = EnvSlotRows.Where(r => r.IsLocked).Select(r => r.Variable).ToList();
        var proposed = EnvConfiguration.Proposed(Current);
        if (proposed is not null && locked.Count > 0)
        {
            proposed.Slot = proposed.Slot.Where(s => !locked.Any(l => string.Equals(l.Name, s.Name, StringComparison.OrdinalIgnoreCase))).Concat(locked).ToList();
        }

        Current.Env = proposed;
        LoadEnvViews();
        NotifyEnv();
        changed();
    }

    private void NotifyEnv()
    {
        foreach (var name in new[]
        {
            nameof(EnvAdapterKey), nameof(EnvEnabled), nameof(EnvTarget), nameof(EnvConstantsSource),
            nameof(EnvContainerExcludeText), nameof(SecretsAdapterKey), nameof(SecretsSource),
        })
        {
            OnPropertyChanged(name);
        }
    }

    /// <summary>Le voci della tendina dell'adapter: "none" = il repository non ha env.</summary>
    public static IReadOnlyList<EnvAdapterOption> EnvAdapterOptions { get; } =
    [
        new("none", "(nessuno)"),
        new(nameof(EnvAdapterKind.Manual), "manuale"),
        new(nameof(EnvAdapterKind.Bat), ".bat"),
        new(nameof(EnvAdapterKind.Dotenv), "dotenv"),
        new(nameof(EnvAdapterKind.AppSettings), "appsettings"),
    ];

    /// <summary>Le voci della tendina della fonte dei segreti: i segreti non si scrivono a mano.</summary>
    public static IReadOnlyList<EnvAdapterOption> SecretsAdapterOptions { get; } =
    [
        new("none", "(nessuna)"),
        new(nameof(EnvAdapterKind.Bat), ".bat"),
        new(nameof(EnvAdapterKind.Dotenv), "dotenv"),
        new(nameof(EnvAdapterKind.AppSettings), "appsettings"),
    ];

    public string EnvAdapterKey
    {
        get => Current.Env?.Adapter.ToString() ?? "none";
        set
        {
            if (value == EnvAdapterKey)
            {
                return;
            }

            if (value is null or "none")
            {
                Current.Env = null;
            }
            else
            {
                (Current.Env ??= new EnvConfiguration()).Adapter = Enum.Parse<EnvAdapterKind>(value);
            }

            LoadEnvViews();
            NotifyEnv();
            changed();
        }
    }

    public bool EnvEnabled => Current.Env is not null;

    public string EnvTarget
    {
        get => Current.Env?.Target ?? string.Empty;
        set => EditEnv(e => e.Target = value?.Trim() ?? string.Empty);
    }

    public string EnvConstantsSource
    {
        get => Current.Env?.ConstantsSource ?? string.Empty;
        set => EditEnv(e => e.ConstantsSource = value?.Trim() ?? string.Empty);
    }

    /// <summary>I prefissi esclusi dalle costanti del container, separati da virgole (es. Kestrel:).</summary>
    public string EnvContainerExcludeText
    {
        get => string.Join(", ", Current.Env?.ContainerExclude ?? []);
        set => EditEnv(e => e.ContainerExclude = SplitList(value, [',', ';']));
    }

    public string SecretsAdapterKey
    {
        get => Current.Env?.Secrets?.Adapter.ToString() ?? "none";
        set => EditEnv(e => e.Secrets = value is null or "none"
            ? null
            : new EnvSecretsConfiguration { Adapter = Enum.Parse<EnvAdapterKind>(value), Source = e.Secrets?.Source ?? string.Empty });
    }

    public string SecretsSource
    {
        get => Current.Env?.Secrets?.Source ?? string.Empty;
        set => EditEnv(e =>
        {
            if (e.Secrets is { } secrets)
            {
                secrets.Source = value?.Trim() ?? string.Empty;
            }
        });
    }

    public ObservableCollection<EnvManualVariable> EnvManualRows { get; } = [];

    public ObservableCollection<EnvSlotRowEditor> EnvSlotRows { get; } = [];

    public ICommand AddManualCommand { get; }

    public ICommand RemoveManualCommand { get; }

    public ICommand AddSlotCommand { get; }

    public ICommand RemoveSlotCommand { get; }

    public ICommand ProposeEnvCommand { get; }

    public ICommand PreviewEnvCommand { get; }

    private string envPreviewText = string.Empty;

    /// <summary>L'anteprima degli env su uno slot fittizio: nomi, classi e origini, mai valori.</summary>
    public string EnvPreviewText { get => envPreviewText; private set => SetProperty(ref envPreviewText, value); }

    // Le sezioni env esistono solo con un adapter scelto: senza, i campi restano spenti.
    private void EditEnv(Action<EnvConfiguration> edit)
    {
        if (Current.Env is not { } env)
        {
            return;
        }

        edit(env);
        changed();
    }

    private void LoadBackendViews()
    {
        Contexts.Clear();
        if (Current is BackendConfiguration backend)
        {
            appSettingsText = string.Join(Environment.NewLine, backend.AppSettings);
            foreach (var context in backend.Contexts)
            {
                Contexts.Add(context);
            }
        }
    }

    private static RepositoryConfiguration Create(RepositoryType type) => type switch
    {
        RepositoryType.Backend => new BackendConfiguration(),
        RepositoryType.Frontend => new FrontendConfiguration(),
        RepositoryType.App => new AppRepoConfiguration(),
        _ => new GenericRepoConfiguration(),
    };

    private static string FormatPorts(List<TunnelPortConfiguration>? ports)
        => ports is null ? string.Empty : string.Join(", ", ports.Select(p => $"{p.Number}/{p.Protocol}"));

    // Una porta che non si legge diventa la porta 0: la validazione la segnala con il resto.
    private static List<TunnelPortConfiguration>? ParsePorts(string text)
    {
        var ports = text
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(token =>
            {
                var parts = token.Split('/', 2);
                return new TunnelPortConfiguration
                {
                    Number = int.TryParse(parts[0], out var number) ? number : 0,
                    Protocol = parts.Length > 1 ? parts[1].Trim().ToLowerInvariant() : "http",
                };
            })
            .ToList();

        return ports.Count > 0 ? ports : null;
    }
}

/// <summary>Un ambiente locale nel setup: numero, repository inclusi, radice facoltativa e database protetto.</summary>
public sealed class EnvironmentEditor : ObservableObject
{
    private readonly Action changed;
    private int number;
    private string root;
    private bool protectedDatabase;

    public EnvironmentEditor(
        string? root,
        int number,
        IEnumerable<RepositoryCardEditor> catalog,
        IEnumerable<string> included,
        Action changed)
    {
        this.changed = changed;
        this.number = number;
        this.root = root ?? string.Empty;

        var ids = included.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var repository in catalog)
        {
            Inclusions.Add(new InclusionToggle(repository, ids.Contains(repository.Id), changed));
        }
    }

    public int Number
    {
        get => number;
        set
        {
            if (SetProperty(ref number, value))
            {
                changed();
            }
        }
    }

    /// <summary>La cartella dei cloni di questo ambiente; vuota = la radice comune.</summary>
    public string Root
    {
        get => root;
        set
        {
            if (SetProperty(ref root, value?.Trim() ?? string.Empty))
            {
                changed();
            }
        }
    }

    /// <summary>Il database del backend e' protetto: migrazioni e schema non si toccano.</summary>
    public bool ProtectedDatabase
    {
        get => protectedDatabase;
        set
        {
            if (SetProperty(ref protectedDatabase, value))
            {
                changed();
            }
        }
    }

    public ObservableCollection<InclusionToggle> Inclusions { get; } = [];

    /// <summary>Riallinea le caselle al catalogo dopo un'aggiunta, una rimozione o uno spostamento.</summary>
    public void Sync(IEnumerable<RepositoryCardEditor> catalog)
    {
        var previous = Inclusions.ToDictionary(i => i.Repository, i => i.IsIncluded);
        Inclusions.Clear();
        foreach (var repository in catalog)
        {
            Inclusions.Add(new InclusionToggle(repository, previous.TryGetValue(repository, out var included) && included, changed));
        }
    }

    public EnvironmentDefinition ToDefinition() => new()
    {
        Number = number,
        Kind = EnvironmentKind.Local,
        Root = root.Length > 0 ? root : null,
        Repositories = Inclusions.Where(i => i.IsIncluded).Select(i => i.Repository.Id).ToList(),
        ProtectedDatabase = protectedDatabase,
    };
}

/// <summary>Una voce delle tendine degli adapter degli env: la chiave e' il nome dell'adapter o "none".</summary>
public sealed record EnvAdapterOption(string Key, string Label);

/// <summary>Una variabile con valore per slot nel setup; quella di connessione del backend e' bloccata con ambienti Docker.</summary>
public sealed class EnvSlotRowEditor(EnvSlotVariable variable, bool isLocked, Action changed) : ObservableObject
{
    public EnvSlotVariable Variable { get; } = variable;

    public bool IsLocked { get; } = isLocked;

    public bool IsEditable => !IsLocked;

    public string? LockToolTip => IsLocked ? SetupViewModel.DockerLockToolTip : null;

    public string Name
    {
        get => Variable.Name;
        set
        {
            Variable.Name = value?.Trim() ?? string.Empty;
            changed();
        }
    }

    public string Local
    {
        get => Variable.Local;
        set
        {
            Variable.Local = value ?? string.Empty;
            changed();
        }
    }

    public string Container
    {
        get => Variable.Container;
        set
        {
            Variable.Container = value ?? string.Empty;
            changed();
        }
    }
}

/// <summary>Una voce della tendina del tipo di seed.</summary>
public sealed record SeedKindOption(SeedKind Value, string Label);

/// <summary>Un tunnel del pool nel setup: la Description con cui si ritrova e la label.</summary>
public sealed class PoolTunnelEditor(PoolTunnelConfiguration tunnel, Action changed) : ObservableObject
{
    /// <summary>La label proposta per un tunnel nuovo del pool.</summary>
    public const string ProposedLabel = Soffritto.Core.Docker.TunnelPool.DefaultLabel;

    public PoolTunnelConfiguration Tunnel { get; } = tunnel;

    public string Description
    {
        get => Tunnel.Description;
        set
        {
            Tunnel.Description = value ?? string.Empty;
            changed();
        }
    }

    public string Label
    {
        get => Tunnel.Label;
        set
        {
            Tunnel.Label = value ?? string.Empty;
            changed();
        }
    }
}

/// <summary>Un repository del catalogo, incluso o no in un ambiente.</summary>
public sealed class InclusionToggle(RepositoryCardEditor repository, bool isIncluded, Action changed) : ObservableObject
{
    private bool isIncluded = isIncluded;

    public RepositoryCardEditor Repository { get; } = repository;

    public bool IsIncluded
    {
        get => isIncluded;
        set
        {
            if (SetProperty(ref isIncluded, value))
            {
                changed();
            }
        }
    }
}

public sealed record FolderPreviewRow(int Number, IReadOnlyList<FolderPreview> Folders)
{
    public bool WillClone => Folders.Any(f => f.WillClone);
}

/// <summary>Una cartella di un ambiente locale come la vedrebbe la configurazione: Salva la clona se manca e ha un URL di clone.</summary>
public sealed record FolderPreview(string Path, string FolderName, string CloneUrl, bool WillClone)
{
    public static FolderPreview Of(EnvironmentRepository repository)
    {
        var path = repository.Path;
        var url = repository.Repository.CloneUrl;
        return new FolderPreview(path, System.IO.Path.GetFileName(path), url, WillClone: url.Length > 0 && !Directory.Exists(path));
    }
}

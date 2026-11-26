using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using Soffritto.Core;
using Soffritto.Core.AzureDevOps;
using Soffritto.Core.Configuration;
using Soffritto.Desktop.Infrastructure;

namespace Soffritto.Desktop.ViewModels;

/// <summary>An entry of the repository type dropdown.</summary>
public sealed record TypeOption(RepositoryType Value, string Label);

/// <summary>
/// The setup panel: it works on a copy of the active configuration (repository catalog and
/// local environments) and validates it as you type. Docker environments stay in the copy but
/// are not shown: they are created and destroyed from the main window. Saving writes the configuration under the lock of
/// <see cref="ConfigurationFiles.Update"/> (Docker entries and seed.path values written while the app
/// is open are kept), clones the missing folders and source clones, creates the missing dev tunnels
/// and asks the app to restart, postponed if a Docker operation is in progress.
/// </summary>
public sealed partial class SetupViewModel : ObservableObject
{
    /// <summary>The tooltip of the fields locked by existing Docker environments.</summary>
    public const string DockerLockToolTip = "Locked: Docker environments use it, and changing it would move worktrees, databases or ports already in use. Destroy those environments first with “Destroy” in their column.";

    private readonly Func<string?> restart;

    // The seed.path values of config.json when opened (and after every Save): they tell whether
    // "Align seed" changed them while the setup was open.
    private readonly Dictionary<string, string> openedSeedPaths = new(StringComparer.OrdinalIgnoreCase);
    private AppConfiguration draft;
    private string root = string.Empty;
    private string newPat = string.Empty;
    private string errorsText = string.Empty;
    private string warningsText = string.Empty;
    private string azureStatus = string.Empty;
    private string notice;
    private bool isWorking;

    /// <param name="restart">Asks the app to restart: null if it started, otherwise why it is postponed.</param>
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

    /// <summary>Docker environments exist: the global fields that would move them (worktrees, database ports) are read-only.</summary>
    public bool HasDockerEnvironments => draft.DockerEnvironments.Any();

    public bool IsDockerGlobalEditable => !HasDockerEnvironments;

    public string? DockerGlobalToolTip => HasDockerEnvironments ? DockerLockToolTip : null;

    /// <summary>The template of Docker environments as text: empty = the first local environment.</summary>
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

    public string WorktreeRootHint => $"Empty = {DockerDefaults.WorktreeRoot(root.Length > 0 ? root : "<clones folder>")}";

    private DockerPanelViewModel? pool;

    /// <summary>
    /// The Docker actions that take effect immediately, outside the draft: the tunnel pool of the
    /// “Tunnels” tab, seed, disk space and log of the “Docker” tab. After every pool action the draft
    /// picks up the tunnels of the active configuration again, so Save neither restores a deleted
    /// tunnel nor removes an added one. A new seed from “Align seed” goes into the backend card
    /// if the setup has not changed its path (on Save it wins anyway, see Merge).
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

    /// <summary>The pool tunnels, matched by Description and created on Save if the account does not have them.</summary>
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

    /// <summary>The configuration being edited: fields with no effect on the preview bind directly here.</summary>
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

    /// <summary>The catalog, one card per repository: the order is that of the cards in the columns.</summary>
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

    /// <summary>The PAT typed in the box: empty keeps the one already saved.</summary>
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
        ? $"New {AzureDevOpsConnection.DescribePat(newPat)}: on save it is encrypted with your Windows user."
        : !string.IsNullOrEmpty(draft.AzureDevOps.PatProtected)
            ? "PAT saved and encrypted with your Windows user: leave empty to keep it."
            : "No PAT saved.";

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

    /// <summary>Valid configurations worth knowing about (duplicate types, docker environments, VSIX): they do not block saving.</summary>
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

    /// <summary>Outcome of startup, import or save, and the cloning steps.</summary>
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

    public string SaveText => Preview.Any(r => r.WillClone) ? "Save, clone and restart" : "Save and restart";

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

    /// <summary>Replaces the fields with a json file chosen by the user; it is saved only with Save.</summary>
    public void Import(string path)
    {
        try
        {
            Draft = ConfigurationFiles.Parse(File.ReadAllText(path));
            LoadDraft();
            Notice = $"Loaded {path}: check the fields and press Save to apply it, or Cancel to leave the configuration as it is.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Notice = $"{path} cannot be read: {ex.Message}";
        }
    }

    /// <summary>Call this when a field bound directly to <see cref="Draft"/> or to a catalog entry changes.</summary>
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

    // A repository included in a Docker environment has its folder, source clone and compose
    // service read-only: changing them would move worktrees and containers already created.
    private RepositoryCardEditor NewEditor(RepositoryConfiguration repository)
        => new(
            repository,
            Refresh,
            editor => editor.Type != RepositoryType.Generic && Repositories.FirstOrDefault(r => r.Type == editor.Type) == editor,
            draft.DockerEnvironments.Any(e => e.Repositories.Contains(repository.Id, StringComparer.OrdinalIgnoreCase)),
            () => draft.DockerWorktreeRoot,
            VerifySeedAsync,
            PreviewEnv);

    // The preview resolves the repository's env on a dummy Docker slot (the highest free number,
    // without pool ports) with the draft's template environment. It shows names, classes and origins
    // but not values, because the template's constants may contain connection strings with passwords.
    private string PreviewEnv(RepositoryCardEditor editor)
    {
        try
        {
            var configuration = Clone(Collect());
            if (configuration.Repository(editor.Id) is not { } repository)
            {
                return "Preview cannot be computed: invalid repository id.";
            }

            if (repository.Env is null)
            {
                return "No env: choose an adapter or press “Suggested values”.";
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

            var lines = new List<string> { $"Dummy slot {number}, template environment {configuration.TemplateNumber()?.ToString() ?? "none"}, without pool ports." };
            lines.Add($"Worktree ({resolution.Local.Count}):");
            lines.AddRange(resolution.Local.Select(Line));
            lines.Add($"Container ({resolution.Container.Count}):");
            lines.AddRange(resolution.Container.Select(Line));
            lines.Add(resolution.Warnings.Count == 0 ? "No warnings." : "Warnings:");
            lines.AddRange(resolution.Warnings.Select(w => $"  ⚠ {w}"));
            return string.Join(Environment.NewLine, lines);
        }
        catch (Exception ex)
        {
            return $"Preview cannot be computed: {ex.Message}";
        }

        static string Line(Soffritto.Core.Env.EnvVariable variable) => variable.Class switch
        {
            Soffritto.Core.Env.EnvClass.SharedSecret => $"  {variable.Name} · (secret from {variable.Origin})",
            Soffritto.Core.Env.EnvClass.Constant => $"  {variable.Name} · constant, from {variable.Origin}",
            _ => $"  {variable.Name} · per slot",
        };
    }

    // "Verify seed" on the draft, even unsaved: a temporary SQL Server restores the seed and
    // compares it with the code's migrations. It does not start while fields need fixing.
    private async Task VerifySeedAsync(RepositoryCardEditor editor)
    {
        var configuration = Collect();
        if (configuration.Validate() is { Count: > 0 } errors)
        {
            editor.SeedVerifyText = $"Verification not started, the setup has fields to fix: {string.Join(" ", errors)}";
            return;
        }

        if (editor.Current.Seed is not { Path.Length: > 0 } seed)
        {
            editor.SeedVerifyText = "Verification not started: the seed path is missing.";
            return;
        }

        if (!ReferenceEquals(configuration.PrimarySeed(), editor.Current))
        {
            editor.SeedVerifyText = "Verification not started: it applies to the default backend's seed.";
            return;
        }

        if (!File.Exists(seed.Path))
        {
            editor.SeedVerifyText = $"the seed file does not exist: {seed.Path}";
            return;
        }

        editor.IsVerifyingSeed = true;
        editor.SeedVerifyText = "Verify seed: starting…";
        var copy = Clone(configuration);
        try
        {
            var result = await Task.Run(() => Soffritto.Core.Docker.Seed.SeedVerifier.VerifyAsync(
                copy,
                line => UiThread.Run(() => editor.SeedVerifyText = $"Verify seed: {line}")));
            editor.SeedVerifyText = Describe(result);
        }
        catch (Exception ex)
        {
            editor.SeedVerifyText = $"Seed verification failed: {ex.Message}";
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
            return $"Seed verification failed: {error}";
        }

        var unknown = result.Contexts.SelectMany(c => c.Unknown).Distinct(StringComparer.Ordinal).ToList();
        var contexts = result.Contexts.Select(c => $"{c.Context}: last {c.LastApplied ?? "none"}, {c.MissingOnDev} dev migrations missing");
        return $"{(result.Compatible ? "compatible" : "not compatible")}. {string.Join("; ", contexts)}."
            + (unknown.Count > 0 ? $" Seed migrations the code does not know: {string.Join(", ", unknown)}." : string.Empty);
    }

    // The first free one among generico, generico2, ...: a valid, unique id, to be renamed at will.
    private void AddRepository()
    {
        var id = Enumerable.Range(1, 99)
            .Select(i => i == 1 ? "generic" : $"generic{i}")
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

    // A new environment takes the first number after the last local one that no Docker environment uses, and
    // the inclusions of the highest-numbered local environment: usually you add an environment like the others.
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

    // Fields with a view of their own (catalog, local environments, lists) go back into the draft only
    // here; the draft's Docker environments, which the setup does not show, stay as they were.
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
            AzureStatus = "Enter the collection URL first.";
            return;
        }

        var pat = UsesPat ? (newPat.Length > 0 ? newPat : ConfigurationStore.PatOf(azure)) : null;
        if (UsesPat && pat is null)
        {
            AzureStatus = "The PAT is missing.";
            return;
        }

        AzureStatus = "Testing the connection…";
        try
        {
            using var connection = new AzureDevOpsConnection(azure.CollectionUrl, azure.Project, pat);
            var who = await connection.WhoAmIAsync();
            AzureStatus = $"Connected as {who}.";
        }
        catch (Exception ex)
        {
            AzureStatus = ex is TaskCanceledException ? "Azure DevOps did not respond within 30 seconds." : ex.Message;
        }
    }

    private async Task SaveAsync()
    {
        if (AppSettings.Current.IsDemo)
        {
            Notice = "In demo mode the configuration is not saved: leave the demo with Ctrl+D.";
            return;
        }

        var configuration = Collect();
        var errors = configuration.Validate();
        if (errors.Count > 0)
        {
            Revalidate();
            Notice = "Some fields need fixing before saving.";
            return;
        }

        IsWorking = true;
        try
        {
            if (newPat.Length > 0)
            {
                configuration.AzureDevOps.Pat = newPat;
            }

            // Save a copy with the PAT already encrypted: the draft stays as it is if the write is
            // rejected and you try again.
            var saved = Clone(configuration);
            saved.AzureDevOps.Pat = configuration.AzureDevOps.Pat;
            ConfigurationStore.ProtectPat(saved.AzureDevOps);
            configuration.AzureDevOps.Pat = null;

            var log = new List<string>();
            var fileReadable = ConfigurationFiles.TryLoad(out _) is not null;
            AppConfiguration? written = null;
            var updateError = await Task.Run(() => ConfigurationFiles.Update(
                file => (written = Merge(file, saved, fileReadable, log)) is null ? "configuration not composed" : null,
                ConfigurationFileFallback.Replace,
                log.Add));
            if (updateError is not null)
            {
                log.Insert(0, $"Configuration not saved: {updateError}");
                Notice = string.Join(Environment.NewLine, log);
                return;
            }

            // The setup stays open if the restart is postponed: it restarts from what was written,
            // including the file's Docker entries and seed.path values.
            RememberSeedPaths(written);
            Draft = Clone(written!);
            LoadDraft();
            configuration = draft;
            NewPat = string.Empty;
            OnPropertyChanged(nameof(PatHint));

            log.Insert(0, "Configuration saved.");
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
                    ? $"✓ {row.FolderName} cloned"
                    : $"✗ {row.FolderName}: {outcome.Tail(2)}";
                Notice = string.Join(Environment.NewLine, log);
                failed |= !outcome.Success;
            }

            failed |= await CreateMissingSourceClonesAsync(configuration, log);

            if (failed)
            {
                Refresh();
                log.Add("Some clones failed: the configuration is saved, fix the problem and press Save again (it clones only what is missing).");
                Notice = string.Join(Environment.NewLine, log);
                return;
            }

            var (createdPoolTunnels, tunnelsFailed) = await CreateMissingPoolTunnelsAsync(configuration, log);
            if (createdPoolTunnels || tunnelsFailed)
            {
                Refresh();
                log.Add(tunnelsFailed
                    ? "Some tunnels were not created: the configuration is saved, fix the problem and press Save again (it creates only what is missing)."
                    : "Configuration saved. Press Save again to restart.");
                Notice = string.Join(Environment.NewLine, log);
                return;
            }

            log.Add("Restarting with the new configuration…");
            Notice = string.Join(Environment.NewLine, log);
            if (restart() is { } postponed)
            {
                log[^1] = $"Restart postponed: {postponed}.";
                Notice = string.Join(Environment.NewLine, log);
            }
        }
        catch (Exception ex)
        {
            Notice = $"Save failed: {ex.Message}";
        }
        finally
        {
            IsWorking = false;
        }
    }

    // Inside the config.json lock: the reread file becomes the draft, except what others write
    // while the app is open. Docker entries stay as in the file (creation and destruction); a
    // seed.path changed in the file since the setup opened wins if the setup has not touched it.
    // With the file missing or unreadable (fileReadable false) the draft is written as it is.
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
                    log.Add($"seed.path of {repository.Id} taken from config.json ({inFile}): “Align seed” wrote it while the setup was open.");
                }
                else
                {
                    log.Add($"seed.path of {repository.Id}: the setup value wins ({inDraft}); config.json had {inFile}, written while the setup was open.");
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

    // A configured source clone that is missing is created bare with a fetch of origin: the Docker
    // environments' worktrees start from it. One that exists is left alone.
    private async Task<bool> CreateMissingSourceClonesAsync(AppConfiguration configuration, List<string> log)
    {
        var failed = false;
        foreach (var repository in configuration.Repositories.Where(r => r.SourceClone is { Length: > 0 } source && !Directory.Exists(source)))
        {
            if (repository.CloneUrl.Length == 0)
            {
                log.Add($"✗ {repository.Id}: source clone not created, the clone URL is missing");
                failed = true;
                continue;
            }

            log.Add($"source clone of {repository.Id} in {repository.SourceClone} (git init --bare and fetch)…");
            Notice = string.Join(Environment.NewLine, log);

            var error = await Task.Run(() => Soffritto.Core.Docker.SourceCloneRepository.InitAsync(repository.SourceClone!, repository.CloneUrl));
            log[^1] = error is null ? $"✓ {repository.Id} source clone created" : $"✗ {repository.Id}: {error}";
            Notice = string.Join(Environment.NewLine, log);
            failed |= error is not null;
        }

        return failed;
    }

    // The pool tunnels the account does not have are created here, "Owner only" and without ports: the
    // ports and per-port anonymous access come when “Tunnel” is turned on in an environment header.
    private async Task<(bool Created, bool Failed)> CreateMissingPoolTunnelsAsync(AppConfiguration configuration, List<string> log)
    {
        if (configuration.TunnelPool.Tunnels.Count == 0)
        {
            return (false, false);
        }

        log.Add("Checking the dev tunnels (devtunnel list)…");
        Notice = string.Join(Environment.NewLine, log);

        var (tunnels, error) = await Task.Run(() => DevTunnelProvisioner.ListTunnelsAsync());
        if (tunnels is null)
        {
            log[^1] = $"✗ dev tunnels not checked: {error}";
            return (false, true);
        }

        log.RemoveAt(log.Count - 1);

        var created = false;
        var failed = false;
        var descriptions = tunnels.Select(t => t.Description).ToHashSet(StringComparer.Ordinal);
        foreach (var description in DevTunnelProvisioner.MissingPoolDescriptions(configuration, descriptions))
        {
            var label = configuration.TunnelPool.Tunnels.First(t => t.Description == description).Label;
            log.Add($"devtunnel create {description} (pool tunnel, Owner only, no ports)…");
            Notice = string.Join(Environment.NewLine, log);

            var (tunnelId, createError) = await Task.Run(() => DevTunnelProvisioner.CreatePoolTunnelAsync(description, label));
            log[^1] = createError is null
                ? $"✓ pool tunnel {description} created: {tunnelId}"
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
/// A catalog card in the setup. The entry changes class when the type changes: the instances of
/// every type stay in memory, so switching back to a type brings its fields back.
/// </summary>
public sealed class RepositoryCardEditor : ObservableObject
{
    private readonly Action changed;
    private readonly Func<RepositoryCardEditor, bool> isPrimary;
    private readonly Dictionary<RepositoryType, RepositoryConfiguration> cache = [];
    private readonly Func<string> worktreeRoot;
    private string portsText;
    private string appSettingsText = string.Empty;

    /// <param name="isDockerLocked">The repository is in a Docker environment: folder, source clone, compose, service and ports are read-only.</param>
    /// <param name="worktreeRoot">The draft's Docker worktree folder, used to suggest the source clone.</param>
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

    /// <summary>The catalog entry with the chosen type; fields with a view of their own go back into it with <see cref="Collect"/>.</summary>
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

    /// <summary>The repository can be shared through a tunnel pool port.</summary>
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

    /// <summary>The manual ports, "8443/https, 3000/http": empty = the type's defaults.</summary>
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

    /// <summary>The repository is in a Docker environment: the fields that would move worktrees and containers are read-only.</summary>
    public bool IsDockerLocked { get; }

    public bool IsDockerEditable => !IsDockerLocked;

    public string? DockerLockToolTip => IsDockerLocked ? SetupViewModel.DockerLockToolTip : null;

    /// <summary>The bare clone the Docker environments' worktrees are created from; empty = the repository is not part of Docker environments.</summary>
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

    // The Docker section fields show the effective value (the type's default if the field is
    // empty) and write only when changed: a field left as it is stays empty in the file.
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

    /// <summary>The published ports "base:port", comma-separated.</summary>
    public string DockerPublishedPortsText
    {
        get => string.Join(", ", Current.DockerPublishedPorts);
        set => EditDocker(d => d.PublishedPorts = SplitList(value, [',', ';']));
    }

    /// <summary>The files copied from the template environment to the worktree, one per line.</summary>
    public string DockerLocalFilesText
    {
        get => string.Join(Environment.NewLine, Current.Docker?.LocalFiles ?? []);
        set => EditDocker(d => d.LocalFiles = SplitList(value, ['\r', '\n']));
    }

    /// <summary>The seed persistence systems: currently only SQL Server.</summary>
    public static IReadOnlyList<SeedKindOption> SeedKindOptions { get; } = [new(SeedKind.SqlServer, "SQL Server")];

    // Without a seed the fields show the values of a new seed; the first field changed creates it.
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

    /// <summary>The containers' SQL Server password: not a secret, it only protects containers on 127.0.0.1.</summary>
    public string SeedPassword
    {
        get => ShownSqlServer.Password;
        set => EditSeed(s => s.SqlServer.Password = value ?? string.Empty);
    }

    /// <summary>SQL Server memory in MB, as text: an unreadable value becomes 0 and validation flags it.</summary>
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

    /// <summary>Outcome of “Verify seed”, or why it did not start.</summary>
    public string SeedVerifyText { get => seedVerifyText; set => SetProperty(ref seedVerifyText, value); }

    // A new seed starts from the suggested password (G-7): the user writes the rest.
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

    /// <summary>The appsettings files to read the connection string from, one per line.</summary>
    public string AppSettingsText
    {
        get => appSettingsText;
        set => SetProperty(ref appSettingsText, value ?? string.Empty);
    }

    /// <summary>The database and server names to highlight on the environment card, comma-separated.</summary>
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

    // With Docker environments the backend's connection variable stays as it is (G-1, B30): the
    // api container and the database guard depend on it.
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

    // The type's suggested values replace the section; a locked connection row stays as it is.
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

    /// <summary>The adapter dropdown entries: "none" = the repository has no env.</summary>
    public static IReadOnlyList<EnvAdapterOption> EnvAdapterOptions { get; } =
    [
        new("none", "(none)"),
        new(nameof(EnvAdapterKind.Manual), "manual"),
        new(nameof(EnvAdapterKind.Bat), ".bat"),
        new(nameof(EnvAdapterKind.Dotenv), "dotenv"),
        new(nameof(EnvAdapterKind.AppSettings), "appsettings"),
    ];

    /// <summary>The secrets source dropdown entries: secrets are never typed by hand.</summary>
    public static IReadOnlyList<EnvAdapterOption> SecretsAdapterOptions { get; } =
    [
        new("none", "(none)"),
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

    /// <summary>The prefixes excluded from the container constants, comma-separated (e.g. Kestrel:).</summary>
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

    /// <summary>The env preview on a dummy slot: names, classes and origins, never values.</summary>
    public string EnvPreviewText { get => envPreviewText; private set => SetProperty(ref envPreviewText, value); }

    // The env sections exist only with an adapter chosen: without one, the fields stay disabled.
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

    // A port that cannot be parsed becomes port 0: validation flags it along with the rest.
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

/// <summary>A local environment in the setup: number, included repositories, optional root and protected database.</summary>
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

    /// <summary>This environment's clones folder; empty = the common root.</summary>
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

    /// <summary>The backend database is protected: migrations and schema are not touched.</summary>
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

    /// <summary>Realigns the checkboxes with the catalog after an addition, a removal or a move.</summary>
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

/// <summary>An entry of the env adapter dropdowns: the key is the adapter name or "none".</summary>
public sealed record EnvAdapterOption(string Key, string Label);

/// <summary>A per-slot variable in the setup; the backend's connection variable is locked when Docker environments exist.</summary>
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

/// <summary>An entry of the seed type dropdown.</summary>
public sealed record SeedKindOption(SeedKind Value, string Label);

/// <summary>A pool tunnel in the setup: the Description it is matched by, and the label.</summary>
public sealed class PoolTunnelEditor(PoolTunnelConfiguration tunnel, Action changed) : ObservableObject
{
    /// <summary>The suggested label for a new pool tunnel.</summary>
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

/// <summary>A catalog repository, included in an environment or not.</summary>
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

/// <summary>A local environment folder as the configuration would see it: Save clones it if it is missing and has a clone URL.</summary>
public sealed record FolderPreview(string Path, string FolderName, string CloneUrl, bool WillClone)
{
    public static FolderPreview Of(EnvironmentRepository repository)
    {
        var path = repository.Path;
        var url = repository.Repository.CloneUrl;
        return new FolderPreview(path, System.IO.Path.GetFileName(path), url, WillClone: url.Length > 0 && !Directory.Exists(path));
    }
}

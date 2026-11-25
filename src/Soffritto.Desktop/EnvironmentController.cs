using Soffritto.Core;
using Soffritto.Core.Configuration;
using Soffritto.Desktop.Infrastructure;
using Soffritto.Desktop.ViewModels;
using Soffritto.Presentation;

namespace Soffritto.Desktop;

/// <summary>
/// Compone una sessione per ogni card dell'ambiente: git per tutte, migrazioni per i backend, il
/// dev tunnel per le card che ne hanno uno. Un gate di scrittura per repository: un'azione blocca
/// solo i pulsanti del repository su cui agisce, gli altri restano liberi.
/// </summary>
public sealed class EnvironmentController
{
    // La CLI devtunnel dei tunnel di backend e frontend di tutti gli ambienti viene interrogata
    // da una lettura alla volta: non e' verificato che regga chiamate concorrenti sulla stessa login.
    private static readonly SemaphoreSlim TunnelGate = new(1, 1);

    private readonly EnvironmentViewModel vm;
    private readonly CancellationToken lifetime;
    private readonly List<(RepoCardViewModel Card, RepoSession Session, WriteGate Gate)> repos = [];

    public EnvironmentController(
        EnvironmentViewModel vm,
        IUserPrompt prompt,
        IClipboard clipboard,
        CancellationToken lifetime)
    {
        this.vm = vm;
        this.lifetime = lifetime;

        foreach (var card in vm.Repos)
        {
            var isBackend = card.Type == RepositoryType.Backend;
            var tunnel = card.Tunnel?.State;
            var location = card.Location;
            var sink = new WpfRepoSink(vm, card, card.Migrations, tunnel, lifetime);
            var gate = new WriteGate();

            gate.Changed += (_, _) => UiThread.Run(() =>
            {
                card.IsNotBusy = gate.IsNotBusy;

                // Il database update e' un'azione del backend: il suo bottone segue quel gate.
                if (card.Migrations is { } migrations)
                {
                    migrations.IsNotBusy = gate.IsNotBusy;
                }

                // Per la conferma alla chiusura: c'e' una scrittura in corso nell'ambiente.
                vm.IsNotBusy = repos.All(r => r.Gate.IsNotBusy);
            });

            var probe = card.Location.Settings is BackendConfiguration backend
                ? AppConfiguration.ToProbeOptions(backend)
                : ActiveConfiguration.Current.ToProbeOptions();
            var session = new RepoSession(
                _ => Task.FromResult(card.Location.Exists() ? card.Location.Path : null),
                new RepoSessionOptions(
                    probe,
                    ReadsMigrations: isBackend,
                    ReadsTunnel: tunnel is not null,
                    TunnelGate: tunnel is not null ? TunnelGate : null,
                    EnvironmentLabel: $"ambiente {vm.Number}",
                    RootUnavailableHeadline: null,
                    RootUnavailableMessage: null,
                    // Riletta a ogni lettura del tunnel: le porte si assegnano e si rilasciano a exe aperto.
                    PoolAssignmentOf: _ => Soffritto.Core.Docker.TunnelPool.AssignmentOf(location.Entry.Number, location.Id),
                    DatabaseGuard: vm.Docker is { } docker ? GuardOf(docker, probe) : null,
                    // Il database dello slot Docker (il proprio, o per un locale quello di uno slot a cui
                    // punta la connessione): spento o in avvio non si tenta la connessione.
                    DatabaseOffline: isBackend && card.IsPrimary ? () => DatabaseOffline?.Invoke() : null,
                    SchemaLock: isBackend && vm.SchemaLock is { } schemaLock ? _ => schemaLock : null),
                sink,
                prompt,
                clipboard,
                gate,
                lifetime);

            sink.Session = session;

            card.FetchCommand = new AsyncCommand(() => session.FetchAsync(lifetime));
            card.PullCommand = new AsyncCommand(() => session.PullAsync(lifetime));
            card.PruneCommand = new AsyncCommand(() => session.PruneBranchesAsync(lifetime));
            card.BranchPicker.BranchSelected = branch => _ = session.SwitchBranchAsync(branch, lifetime);

            if (tunnel is not null)
            {
                sink.ApplyTunnel(ViewBuilder.Tunnel(null, DateTimeOffset.Now));
            }

            repos.Add((card, session, gate));
        }

        vm.RefreshCommand = new AsyncCommand(() =>
        {
            Refresh(full: true);
            return Task.CompletedTask;
        });

        vm.ResetCommand = new AsyncCommand(async () =>
        {
            vm.Report("reset: spengo i servizi, controllo i repository e fetch…");
            var outcome = await CheckoutAsync([], alwaysStop: true).ConfigureAwait(false);
            vm.Report(outcome);
        });
    }

    private int aligning;

    // Riallinea gli env senza chiedere: le sole variabili per slot che valgono {apiHostUrl}
    // (l'api su Kestrel) o dipendono da {poolUrl}. Si scrive solo se qualcosa
    // cambia; l'esito torna nella riga di attivita' dopo la rilettura, che la spegnerebbe, perche'
    // dice di riavviare il dev server o Metro.
    private async Task AlignThenRefreshAsync()
    {
        var outcome = await AlignEnvAsync().ConfigureAwait(false);
        try
        {
            await RefreshAsync(full: true).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
        }

        if (outcome is not null)
        {
            vm.Report(outcome);
        }
    }

    // L'esito da mostrare, o null se gli env erano gia' allineati.
    private async Task<string?> AlignEnvAsync()
    {
        if (Interlocked.Exchange(ref aligning, 1) == 1)
        {
            return null;
        }

        try
        {
            var configuration = ActiveConfiguration.Current;
            var plan = await Task.Run(
                () => Soffritto.Core.Env.EnvPoolSync.PlanAsync(configuration, vm.Number, Soffritto.Core.Env.EnvSyncScope.All, ct: lifetime),
                lifetime).ConfigureAwait(false);
            if (!plan.Any(c => c.Outcome == Soffritto.Core.Env.EnvPoolSync.Written))
            {
                return null;
            }

            var lines = new List<string>();
            await Task.Run(
                () => Soffritto.Core.Env.EnvPoolSync.ApplyAsync(configuration, vm.Number, Soffritto.Core.Env.EnvSyncScope.All, lines.Add, ct: lifetime),
                lifetime).ConfigureAwait(false);
            return $"env allineati: {string.Join(" · ", lines)}";
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            return $"allineamento degli env non riuscito: {ex.Message}";
        }
        finally
        {
            Interlocked.Exchange(ref aligning, 0);
        }
    }

    /// <summary>
    /// Ferma tutti i servizi dell'ambiente: l'esito e se si e' fermato tutto (vedi
    /// <see cref="DevToolsController.StopEverythingAsync"/>).
    /// </summary>
    public Func<Task<(string Outcome, bool AllStopped)>>? StopServices { get; set; }

    /// <summary>Perche' il database dello slot non si legge ora (vedi <see cref="DevToolsController.DatabaseOffline"/>); per un locale, quello dello slot Docker a cui punta, se c'e'.</summary>
    public Func<string?>? DatabaseOffline { get; set; }

    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(120);

    // Nelle sessioni di un ambiente Docker ogni lettura delle migrazioni, database update,
    // rollback e copia del comando passa prima da qui: il worktree deve puntare al SQL Server dello slot.
    private static Func<string, string?> GuardOf(DockerEnvironmentViewModel docker, ProbeOptions probe)
        => docker.Slot is { } slot
            ? root => Soffritto.Core.Docker.DatabaseGuard.Check(root, probe, slot.DatabasePort, slot.DatabaseName)
            : _ => docker.SlotError ?? "slot non calcolabile";

    /// <summary>Rilegge il solo dev tunnel di un repository dell'ambiente, se la sua cartella c'e'.</summary>
    public void RefreshTunnel(RepoCardViewModel target)
    {
        var (card, session, _) = repos.First(r => ReferenceEquals(r.Card, target));
        if (card.Location.Exists())
        {
            _ = session.RefreshTunnelAsync(lifetime);
        }
    }

    /// <summary>
    /// Spegne l'ambiente e porta ogni repository sul branch indicato in <paramref name="targets"/>
    /// o, se non c'e', su <see cref="GitConventions.DevelopmentBranch"/>: il reset non indica niente, il pannello PR
    /// indica i branch delle PR di un work item. Si controllano tutti i repository (fetch
    /// compreso): se anche uno solo ha modifiche, un'operazione git a meta', un'azione in corso o
    /// il branch non c'e' non si cambia niente. Solo a controlli superati si fermano tutti i
    /// servizi (app compresa), poi git switch e, se il branch locale e' indietro, pull in
    /// fast-forward. Con <paramref name="alwaysStop"/> (il Reset) i servizi si fermano prima dei
    /// controlli, anche se poi i repository restano dove sono. Restituisce l'esito da mostrare
    /// all'utente. I target sono per nome del repository sul server; i repository generici
    /// seguono le stesse regole, quindi uno sporco o senza il branch blocca l'ambiente come gli altri.
    /// </summary>
    public async Task<string> CheckoutAsync(IReadOnlyList<(string Repository, string Branch)> targets, bool alwaysStop = false)
    {
        var problems = new List<string>();
        var stopped = "niente fermato";
        if (alwaysStop)
        {
            var (outcome, stopProblem) = await StopForCheckoutAsync().ConfigureAwait(false);
            stopped = outcome;
            if (stopProblem is not null)
            {
                problems.Add(stopProblem);
            }
        }

        var plan = await PlanCheckoutAsync(targets, problems).ConfigureAwait(false);

        // «Apri in» spegne solo quando potra' cambiare tutti i branch: un rifiuto lascia
        // l'ambiente acceso com'era.
        if (!alwaysStop && problems.Count == 0)
        {
            var (outcome, stopProblem) = await StopForCheckoutAsync().ConfigureAwait(false);
            stopped = outcome;
            if (stopProblem is not null)
            {
                problems.Add(stopProblem);
            }
        }

        if (problems.Count > 0)
        {
            return $"Ambiente {vm.Number}: {stopped}. Repository non toccati: {string.Join("; ", problems)}";
        }

        var done = new List<string>();
        foreach (var (card, session, branch, _) in plan)
        {
            var (pulled, problem) = await session.SwitchAndPullAsync(branch, lifetime).ConfigureAwait(false);
            done.Add(problem is not null
                ? $"{card.Clone}: {problem}, vedi la card"
                : $"{card.Clone} su {branch}{(pulled ? " (aggiornato col pull)" : string.Empty)}");
        }

        return $"Ambiente {vm.Number}: {stopped}; {string.Join(", ", done)}";
    }

    // Lo stop dei container di un ambiente Docker puo' fallire o non finire: in quel caso
    // nessun repository cambia branch sotto un servizio ancora acceso.
    private async Task<(string Outcome, string? Problem)> StopForCheckoutAsync()
    {
        if (StopServices is not { } stop)
        {
            return ("servizi non fermati", null);
        }

        var stopping = Task.Run(stop);
        if (await Task.WhenAny(stopping, Task.Delay(StopTimeout, lifetime)).ConfigureAwait(false) != stopping)
        {
            return ("servizi non fermati in tempo", "servizi non fermati in tempo");
        }

        var (outcome, allStopped) = await stopping.ConfigureAwait(false);
        return allStopped ? (outcome, null) : ("servizi non fermati", $"servizi non fermati: {outcome}");
    }

    private async Task<List<(RepoCardViewModel Card, RepoSession Session, string Branch, GitStatus Status)>> PlanCheckoutAsync(
        IReadOnlyList<(string Repository, string Branch)> targets,
        List<string> problems)
    {
        var plan = new List<(RepoCardViewModel Card, RepoSession Session, string Branch, GitStatus Status)>();
        var worktreeBranches = new Dictionary<string, IReadOnlyDictionary<string, string>?>(StringComparer.OrdinalIgnoreCase);

        foreach (var (card, session, gate) in repos)
        {
            var requested = targets
                .Where(t => string.Equals(t.Repository, card.RepositoryName, StringComparison.OrdinalIgnoreCase))
                .Select(t => t.Branch)
                .FirstOrDefault();
            var branch = requested ?? GitConventions.DevelopmentBranch;

            // Un clone che manca non blocca il reset; blocca solo se gli e' chiesto un branch.
            if (!card.Location.Exists())
            {
                if (requested is not null)
                {
                    problems.Add($"{card.Clone}: cartella non trovata");
                }

                continue;
            }

            if (!gate.IsNotBusy)
            {
                problems.Add($"{card.Clone}: c'e' gia' un'azione in corso");
                continue;
            }

            var status = await GitStatusReader.ReadAsync(card.Path, fetch: true, lifetime).ConfigureAwait(false);
            var problem = status.Error
                ?? (status.FetchError is { } fetchError ? $"fetch non riuscito ({fetchError})" : null)
                ?? GitStatusReader.ReadOperationInProgress(card.Path)
                ?? (status.IsDetached ? "HEAD staccato" : null)
                ?? (status.DirtyFiles > 0 ? $"{status.DirtyFiles} file modificati o non tracciati" : null)
                ?? (!status.AllBranches.Contains(branch, StringComparer.OrdinalIgnoreCase) ? $"il branch {branch} non esiste" : null)
                ?? (card.Location.IsDocker ? await HeldElsewhereAsync(card, branch, worktreeBranches).ConfigureAwait(false) : null);

            if (problem is not null)
            {
                problems.Add($"{card.Clone}: {problem}");
                continue;
            }

            plan.Add((card, session, branch, status));
        }

        return plan;
    }

    // Git porta un branch in un solo worktree dello stesso clone sorgente: se un altro worktree
    // lo tiene, git switch fallirebbe a meta' del checkout. Una lettura per clone sorgente.
    private async Task<string?> HeldElsewhereAsync(
        RepoCardViewModel card,
        string branch,
        Dictionary<string, IReadOnlyDictionary<string, string>?> cache)
    {
        if (card.Location.Settings.SourceClone is not { Length: > 0 } sourceClone)
        {
            return null;
        }

        if (!cache.TryGetValue(sourceClone, out var held))
        {
            held = await Soffritto.Core.Docker.SourceCloneRepository.WorktreeBranchesAsync(sourceClone, lifetime).ConfigureAwait(false);
            cache[sourceClone] = held;
        }

        if (held is null)
        {
            return $"worktree del clone sorgente {sourceClone} non leggibili";
        }

        return held.TryGetValue(branch, out var path) && !SamePath(path, card.Path)
            ? $"il branch {branch} e' gia' nel worktree {path}"
            : null;
    }

    private static bool SamePath(string a, string b)
        => string.Equals(
            System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(a)),
            System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(b)),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Rilegge le card esistenti. Una cartella mancante non avvia letture: con il backend
    /// mancante non partono ne' le migrazioni ne' il suo tunnel, con il frontend mancante non
    /// parte il tunnel del frontend. Un repository con un'azione in
    /// corso salta la rilettura (la sessione esce da sola) e si rilegge a fine azione.
    /// </summary>
    public void Refresh(bool full)
    {
        // Gli env di un ambiente locale si riallineano una volta sola, alla prima rilettura completa
        // dopo l'avvio dell'exe: un valore cambiato a mano dopo (un altro Kestrel, IIS Express, il
        // tunnel di un collega) resta fino al prossimo avvio.
        if (full && !vm.IsDocker && Interlocked.Exchange(ref alignedAtStartup, 1) == 0)
        {
            _ = AlignThenRefreshAsync();
            return;
        }

        _ = RefreshAsync(full);
    }

    private int alignedAtStartup;

    private Task RefreshAsync(bool full)
    {
        var existing = new List<RepoSession>();

        UiThread.Run(() =>
        {
            foreach (var (card, session, _) in repos)
            {
                if (card.RefreshExistence())
                {
                    existing.Add(session);
                }
            }

            // Nessuna sessione partira': la riga di attivita' non deve restare su "avvio…".
            if (existing.Count == 0)
            {
                vm.ActivityLine = StatusText.IdleActivity;
            }
        });

        var reads = existing.Select(session => session.RefreshAsync(full, lifetime)).ToList();

        if (full && vm.Docker is { Slot: not null } docker)
        {
            reads.Add(RefreshDockerAsync(docker));
        }

        return Task.WhenAll(reads);
    }

    // Gli avvisi degli env dello slot (come una variabile {poolUrl:<id>} senza porta del pool) e le porte
    // del pool assegnate: letture di file e di tunnel-pool.json, nessuna scrittura.
    private async Task RefreshDockerAsync(DockerEnvironmentViewModel docker)
    {
        try
        {
            var configuration = ActiveConfiguration.Current;
            var slot = docker.Slot!;
            var poolUrls = await Soffritto.Core.Docker.DockerEnvironmentProvisioner.PoolUrlsAsync(vm.Number, lifetime).ConfigureAwait(false);
            var warnings = await Task.Run(
                () => slot.Repositories
                    .SelectMany(entry => Soffritto.Core.Env.EnvResolver.Resolve(configuration, entry, slot, poolUrls, targetIsNew: false).Warnings)
                    .ToList(),
                lifetime).ConfigureAwait(false);

            UiThread.Run(() => docker.EnvWarningText = string.Join(Environment.NewLine, warnings));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            UiThread.Run(() => docker.EnvWarningText = $"avvisi degli env non letti: {ex.Message}");
        }
    }
}

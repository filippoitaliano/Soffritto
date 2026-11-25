using Soffritto.Core;

namespace Soffritto.Presentation;

/// <summary>
/// Orchestrazione del pannello per un repository: letture per sezione, refresh, azioni di
/// scrittura con le loro guardie e i refresh che le seguono. L'host fornisce solo come si
/// mostra (sink), come si conferma (prompt), come si copia (clipboard) e dove sta la radice.
/// </summary>
public sealed class RepoSession
{
    private readonly Func<CancellationToken, Task<string?>> resolveRoot;
    private readonly RepoSessionOptions options;
    private readonly IStatusSink sink;
    private readonly IUserPrompt prompt;
    private readonly IClipboard clipboard;
    private readonly WriteGate gate;

    // Le letture sopravvivono al comando che le ha chieste: si fermano solo con l'host.
    private readonly CancellationToken lifetime;

    // Una lettura per sezione, indipendenti tra loro: le migrazioni non aspettano il fetch git
    // ne' il dev tunnel, e nessuna lettura blocca le azioni, che hanno il loro unico gate nel
    // WriteGate.
    private readonly SectionLoader migrationsLoader = new();
    private readonly SectionLoader gitLoader = new();
    private readonly SectionLoader tunnelLoader = new();

    private readonly ProblemBuffer problems = new();

    // Ultimi server e database letti: la conferma del database update li riporta.
    private string server = StatusText.Unresolved;
    private string database = StatusText.Unresolved;

    // Protetto dal gate: una sola azione alla volta lo scrive.
    private bool actingOnMigrations;

    public RepoSession(
        Func<CancellationToken, Task<string?>> resolveRoot,
        RepoSessionOptions options,
        IStatusSink sink,
        IUserPrompt prompt,
        IClipboard clipboard,
        WriteGate gate,
        CancellationToken lifetime)
    {
        this.resolveRoot = resolveRoot;
        this.options = options;
        this.sink = sink;
        this.prompt = prompt;
        this.clipboard = clipboard;
        this.gate = gate;
        this.lifetime = lifetime;
    }

    /// <param name="full">
    /// Vero per la lettura completa (fetch git e probe del tunnel compresi), falso per quella
    /// locale che si limita a migrazioni e stato git del disco.
    /// </param>
    public async Task RefreshAsync(bool full, CancellationToken cancellationToken)
    {
        if (!gate.IsNotBusy)
        {
            return;
        }

        try
        {
            var root = await resolveRoot(cancellationToken).ConfigureAwait(false);
            if (root is null)
            {
                if (options.RootUnavailableHeadline is not null)
                {
                    Fail(options.RootUnavailableHeadline, options.RootUnavailableMessage ?? string.Empty);
                }

                return;
            }

            // Inizio di un refresh: l'esito dell'ultima azione e' superato dalla nuova lettura.
            // Ogni sezione segnala da se' che sta leggendo, quindi la riga di attivita' si spegne.
            sink.BeginRefresh();
            problems.Action = string.Empty;
            SetActivity(string.Empty);
            sink.SetErrorLine(problems.Compose());

            // Il probe del tunnel viaggia con il fetch: entrambi toccano la rete, e la lettura
            // locale (dopo un database update) li salta lasciando la sezione tunnel com'e'
            // invece di azzerarla a "non letto".
            if (options.ReadsMigrations)
            {
                migrationsLoader.Request(() => LoadMigrationsAsync(root));
            }

            gitLoader.Request(() => LoadGitAsync(root, fetch: full));

            if (full && options.ReadsTunnel)
            {
                tunnelLoader.Request(() => LoadTunnelAsync(root));
            }
        }
        catch (OperationCanceledException)
        {
            // Chiusura dell'host durante la risoluzione della radice.
        }
        catch (Exception ex)
        {
            Fail(StatusText.ReadFailed, ex.Message);
        }
    }

    /// <summary>Errore nato fuori dalla sessione (es. l'attesa della soluzione scaduta nella VSIX).</summary>
    public void ReportFailure(string headline, string message) => Fail(headline, message);

    /// <summary>Rilegge la sola sezione del dev tunnel, senza fetch git ne' migrazioni.</summary>
    public async Task RefreshTunnelAsync(CancellationToken cancellationToken)
    {
        if (!options.ReadsTunnel)
        {
            return;
        }

        var root = await resolveRoot(cancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return;
        }

        tunnelLoader.Request(() => LoadTunnelAsync(root));
    }

    // Il fetch e' una lettura: aggiorna i remote-tracking ref e rilegge la sola sezione git.
    public async Task FetchAsync(CancellationToken cancellationToken)
    {
        if (!gate.IsNotBusy)
        {
            return;
        }

        var root = await resolveRoot(cancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return;
        }

        gitLoader.Request(() => LoadGitAsync(root, fetch: true));
    }

    public async Task PullAsync(CancellationToken cancellationToken)
    {
        if (!gate.IsNotBusy)
        {
            return;
        }

        var root = await resolveRoot(cancellationToken).ConfigureAwait(false);
        if (root is null || !BeginAction("git pull --ff-only…"))
        {
            return;
        }

        try
        {
            var outcome = await GitStatusReader
                .FastForwardAsync(root, Progress, cancellationToken)
                .ConfigureAwait(false);

            if (!outcome.Success)
            {
                Fail("pull non riuscito", outcome.Tail());
                return;
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Fail("pull non riuscito", ex.Message);
            return;
        }
        finally
        {
            EndAction();
        }

        // Dopo un pull le migrazioni sul disco possono essere cambiate: rileggere e' obbligatorio,
        // e si rilegge tutto come il pulsante Aggiorna.
        await RefreshAsync(full: true, cancellationToken).ConfigureAwait(false);
    }

    // Nessuna conferma: la scelta nella tendina e' gia' l'intenzione, e git switch rifiuta da
    // solo se le modifiche locali entrerebbero in conflitto. Se qualcosa impedisce lo switch la
    // tendina torna sul branch corrente.
    public async Task SwitchBranchAsync(string branch, CancellationToken cancellationToken)
    {
        if (!gate.IsNotBusy)
        {
            sink.RestoreBranchSelection();
            return;
        }

        var root = await resolveRoot(cancellationToken).ConfigureAwait(false);
        if (root is null || !BeginAction($"git switch {branch}…"))
        {
            sink.RestoreBranchSelection();
            return;
        }

        try
        {
            var outcome = await GitStatusReader
                .SwitchAsync(root, branch, Progress, cancellationToken)
                .ConfigureAwait(false);

            if (!outcome.Success)
            {
                Fail("cambio branch non riuscito", outcome.Tail());
                sink.RestoreBranchSelection();
                return;
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Fail("cambio branch non riuscito", ex.Message);
            sink.RestoreBranchSelection();
            return;
        }
        finally
        {
            EndAction();
        }

        // Branch diverso: migrazioni sul disco diverse e upstream diverso da confrontare, quindi
        // refresh completo come il pulsante Aggiorna.
        await RefreshAsync(full: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Switch (se serve) e pull in fast-forward (se il branch e' indietro) dentro la stessa azione,
    /// poi una sola rilettura completa. Con <see cref="SwitchBranchAsync"/> seguito da
    /// <see cref="PullAsync"/> la rilettura dopo lo switch faceva un git fetch in background
    /// mentre partiva il pull, e i due fetch potevano contendersi i lock dei ref. Il fetch lo ha
    /// gia' fatto chi chiama. Restituisce cosa e' successo, o il problema.
    /// </summary>
    public async Task<(bool Pulled, string? Problem)> SwitchAndPullAsync(string branch, CancellationToken cancellationToken)
    {
        var root = await resolveRoot(cancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return (false, "cartella non disponibile");
        }

        if (!BeginAction($"git switch {branch}…"))
        {
            return (false, "c'e' gia' un'azione in corso");
        }

        var pulled = false;
        string? problem = null;

        try
        {
            var status = await GitStatusReader.ReadAsync(root, fetch: false, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(status.Branch, branch, StringComparison.Ordinal))
            {
                var switched = await GitStatusReader.SwitchAsync(root, branch, Progress, cancellationToken).ConfigureAwait(false);
                if (!switched.Success)
                {
                    problem = "switch non riuscito";
                    Fail("cambio branch non riuscito", switched.Tail());
                    sink.RestoreBranchSelection();
                    return (false, problem);
                }

                status = await GitStatusReader.ReadAsync(root, fetch: false, cancellationToken).ConfigureAwait(false);
            }

            if (status.Behind > 0)
            {
                SetActivity("git pull --ff-only…");
                var outcome = await GitStatusReader.FastForwardAsync(root, Progress, cancellationToken).ConfigureAwait(false);
                if (!outcome.Success)
                {
                    problem = "pull non riuscito";
                    Fail(problem, outcome.Tail());
                    return (false, problem);
                }

                pulled = true;
            }
        }
        catch (OperationCanceledException)
        {
            return (false, "annullato");
        }
        catch (Exception ex)
        {
            Fail("cambio branch non riuscito", ex.Message);
            return (false, ex.Message);
        }
        finally
        {
            EndAction();
        }

        await RefreshAsync(full: true, cancellationToken).ConfigureAwait(false);
        return (pulled, null);
    }

    // Eliminare branch e' irreversibile per quanto riguarda i ref locali, quindi la conferma
    // arriva a candidati gia' noti e li elenca per nome: e' l'unico punto in cui ci si accorge
    // che in mezzo c'e' un branch che si credeva ancora vivo sul remote.
    public async Task PruneBranchesAsync(CancellationToken cancellationToken)
    {
        if (!gate.IsNotBusy)
        {
            return;
        }

        var root = await resolveRoot(cancellationToken).ConfigureAwait(false);
        if (root is null || !BeginAction("git fetch --prune…"))
        {
            return;
        }

        PrunableBranches candidates;

        try
        {
            candidates = await GitStatusReader
                .ReadPrunableBranchesAsync(root, Progress, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Fail("pulizia branch non riuscita", ex.Message);
            return;
        }
        finally
        {
            EndAction();
        }

        if (candidates.Error is not null)
        {
            Fail("pulizia branch non riuscita", candidates.Error);
            return;
        }

        if (candidates.Branches.Count == 0)
        {
            // Il --prune puo' aver comunque tolto dei remote-tracking ref: la tendina va riletta.
            // Il messaggio arriva dopo, perche' l'inizio del refresh azzera la riga di attivita'.
            await RefreshAsync(full: false, cancellationToken).ConfigureAwait(false);
            SetActivity("nessun branch locale da eliminare");
            return;
        }

        var confirmed = await prompt.ConfirmAsync(
            StatusText.PruneConfirmation(
                candidates.Branches.Count,
                candidates.Branches,
                StatusText.WorkingFolder(root),
                options.EnvironmentLabel),
            cancellationToken).ConfigureAwait(false);

        if (!confirmed || !BeginAction($"git branch -D — {candidates.Branches.Count} branch…"))
        {
            return;
        }

        try
        {
            var outcome = await GitStatusReader
                .DeleteBranchesAsync(root, candidates.Branches, Progress, cancellationToken)
                .ConfigureAwait(false);

            if (!outcome.Success)
            {
                Fail("pulizia branch non riuscita", outcome.Tail());
                return;
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Fail("pulizia branch non riuscita", ex.Message);
            return;
        }
        finally
        {
            EndAction();
        }

        // Il fetch e' gia' stato fatto dal --prune: basta rileggere lo stato locale e la tendina.
        await RefreshAsync(full: false, cancellationToken).ConfigureAwait(false);
        SetActivity($"{candidates.Branches.Count} branch eliminati");
    }

    public async Task ApplyMigrationsAsync(string contextName, CancellationToken cancellationToken)
    {
        if (!gate.IsNotBusy)
        {
            return;
        }

        var definition = options.Probe.Contexts.FirstOrDefault(c => c.Name == contextName);
        if (definition is null)
        {
            return;
        }

        var root = await resolveRoot(cancellationToken).ConfigureAwait(false);
        if (root is null || GuardRefuses(root) || SchemaLockRefuses(root))
        {
            return;
        }

        var confirmed = await prompt.ConfirmAsync(
            StatusText.ApplyConfirmation(
                contextName,
                StatusText.WorkingFolder(root),
                options.EnvironmentLabel,
                server,
                database),
            cancellationToken).ConfigureAwait(false);

        if (!confirmed || GuardRefuses(root) || !BeginAction("dotnet ef database update…", onMigrations: true))
        {
            return;
        }

        try
        {
            var outcome = await MigrationApplier
                .UpdateDatabaseAsync(root, definition, targetMigrationId: null, Progress, cancellationToken)
                .ConfigureAwait(false);

            if (!outcome.Success)
            {
                Fail("aggiornamento non riuscito", await MigrationApplier.FailureReasonAsync(root, definition, outcome, cancellationToken).ConfigureAwait(false));
                return;
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Fail("aggiornamento non riuscito", ex.Message);
            return;
        }
        finally
        {
            EndAction();
        }

        // Solo il database e' cambiato: ne' git ne' il tunnel hanno motivo di essere riletti.
        await RefreshAsync(full: false, cancellationToken).ConfigureAwait(false);
    }

    // Il piano si ricalcola da una lettura fresca e non dalle righe mostrate: tra l'ultimo refresh
    // e il clic qualcun altro puo' aver migrato, e un rollback calcolato su uno stato vecchio
    // annullerebbe migrazioni diverse da quelle elencate nella conferma.
    public async Task UpdateToMigrationAsync(
        string contextName,
        string migrationId,
        CancellationToken cancellationToken)
    {
        if (!gate.IsNotBusy)
        {
            return;
        }

        var definition = options.Probe.Contexts.FirstOrDefault(c => c.Name == contextName);
        if (definition is null)
        {
            return;
        }

        var root = await resolveRoot(cancellationToken).ConfigureAwait(false);
        if (root is null || GuardRefuses(root) || SchemaLockRefuses(root) || !BeginAction("lettura delle migrazioni…", onMigrations: true))
        {
            return;
        }

        StatusReport report;

        try
        {
            report = await StatusProbe
                .RunMigrationsAsync(root, options.Probe, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Fail("lettura migrazioni fallita", ex.Message);
            return;
        }
        finally
        {
            EndAction();
        }

        if (report.DatabaseError is not null || report.ConfigurationError is not null)
        {
            Fail("lettura migrazioni fallita", report.DatabaseError ?? report.ConfigurationError!);
            return;
        }

        var plan = report.Contexts
            .FirstOrDefault(c => c.ContextName == contextName)?
            .PlanUpdateTo(migrationId);

        if (plan is null)
        {
            Fail("migrazione non trovata", $"{migrationId} non e' piu' tra le migrazioni di {contextName}: aggiorna il pannello.");
            return;
        }

        if (plan.IsNoOp)
        {
            SetActivity($"database gia' fermo a {plan.Target.DisplayName}");
            return;
        }

        var confirmed = await prompt.ConfirmAsync(
            StatusText.UpdateToConfirmation(
                contextName,
                plan,
                StatusText.WorkingFolder(root),
                options.EnvironmentLabel,
                report.Server ?? StatusText.Unresolved,
                report.Database ?? StatusText.Unresolved),
            cancellationToken).ConfigureAwait(false);

        if (!confirmed || GuardRefuses(root) || !BeginAction($"dotnet ef database update {plan.Target.DisplayName}…", onMigrations: true))
        {
            return;
        }

        try
        {
            var outcome = await MigrationApplier
                .UpdateDatabaseAsync(root, definition, migrationId, Progress, cancellationToken)
                .ConfigureAwait(false);

            if (!outcome.Success)
            {
                Fail(plan.IsRollback ? "rollback non riuscito" : "aggiornamento non riuscito", await MigrationApplier.FailureReasonAsync(root, definition, outcome, cancellationToken).ConfigureAwait(false));
                return;
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Fail(plan.IsRollback ? "rollback non riuscito" : "aggiornamento non riuscito", ex.Message);
            return;
        }
        finally
        {
            EndAction();
        }

        await RefreshAsync(full: false, cancellationToken).ConfigureAwait(false);
    }

    // Copiare non tocca niente: nessuna conferma, a differenza del bottone che il comando lo
    // esegue davvero. La riga copiata porta con se' l'ambiente Development. La guardia del
    // database degli ambienti Docker vale anche qui: la riga incollata migrerebbe davvero.
    /// <summary>
    /// Copia negli appunti il comando di update fino a <paramref name="migrationId"/>. Null se
    /// non c'era niente da copiare (context sconosciuto o radice non risolta): nessun feedback.
    /// </summary>
    public async Task<bool?> CopyUpdateCommandAsync(
        string contextName,
        string migrationId,
        CancellationToken cancellationToken)
    {
        var definition = options.Probe.Contexts.FirstOrDefault(c => c.Name == contextName);
        if (definition is null)
        {
            return null;
        }

        var root = await resolveRoot(cancellationToken).ConfigureAwait(false);
        if (root is null || GuardRefuses(root) || SchemaLockRefuses(root))
        {
            return null;
        }

        return clipboard.TrySetText(MigrationApplier.BuildUpdateCommandLine(root, definition, migrationId));
    }

    // Negli ambienti Docker un comando sul database parte solo se la connessione del worktree
    // punta al SQL Server dello slot: altrimenti migrerebbe il database di qualcun altro.
    private bool GuardRefuses(string root)
    {
        if (options.DatabaseGuard?.Invoke(root) is not { } error)
        {
            return false;
        }

        Fail("database fuori dallo slot", error);
        return true;
    }

    // Un database protetto si legge ma non si migra: il blocco vale per ogni scrittura di schema,
    // copia del comando compresa, perche' la riga incollata migrerebbe davvero.
    private bool SchemaLockRefuses(string root)
    {
        if (options.SchemaLock?.Invoke(root) is not { } reason)
        {
            return false;
        }

        Fail("database protetto", reason);
        return true;
    }

    public bool CopyText(string text) => clipboard.TrySetText(text);

    private bool BeginAction(string message, bool onMigrations = false)
    {
        if (!gate.TryEnter())
        {
            return false;
        }

        actingOnMigrations = onMigrations;
        sink.SetBusy(true);
        sink.BeginOperation();
        SetActivity(message);

        if (onMigrations)
        {
            sink.SetMigrationsAction(message);
        }

        return true;
    }

    private void EndAction()
    {
        if (actingOnMigrations)
        {
            actingOnMigrations = false;
            sink.SetMigrationsAction(null);
        }

        gate.Exit();
        sink.SetBusy(false);
        sink.EndOperation();
    }

    private void Fail(string headline, string message)
    {
        SetActivity(headline);
        problems.Action = message;
        sink.SetErrorLine(problems.Compose());
    }

    private void SetActivity(string line)
        => sink.SetActivity(line.Length > 0 ? line : StatusText.IdleActivity);

    private void Progress(string line) => SetActivity(StatusText.Progress(line));

    private async Task LoadMigrationsAsync(string root)
    {
        sink.SetMigrationsLoading();
        sink.BeginOperation();

        try
        {
            if (options.DatabaseGuard?.Invoke(root) is { } refused)
            {
                SetActivity("database fuori dallo slot");
                problems.Migrations = refused;
                sink.FailMigrations();
                sink.SetErrorLine(problems.Compose());
                return;
            }

            var report = await StatusProbe
                .RunMigrationsAsync(root, options.Probe, lifetime, options.DatabaseOffline?.Invoke())
                .ConfigureAwait(false);

            var view = ViewBuilder.Migrations(report, DateTimeOffset.Now, options.SchemaLock?.Invoke(root));
            server = view.Server;
            database = view.Database;
            problems.Migrations = view.Problems;
            sink.ApplyMigrations(view);
            sink.SetErrorLine(problems.Compose());
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            // Chiusura dell'host durante la lettura.
        }
        catch (Exception ex)
        {
            SetActivity("lettura migrazioni fallita");
            problems.Migrations = ex.Message;
            sink.FailMigrations();
            sink.SetErrorLine(problems.Compose());
        }
        finally
        {
            sink.EndOperation();
        }
    }

    private async Task LoadGitAsync(string root, bool fetch)
    {
        sink.SetRepoLoading(ViewBuilder.RepoLoading(root, fetch));
        sink.BeginOperation();

        try
        {
            GitStatus git;

            try
            {
                git = await GitStatusReader.ReadAsync(root, fetch, lifetime).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                // Chiusura dell'host durante la lettura.
                return;
            }
            catch (Exception ex)
            {
                git = new GitStatus(Error: ex.Message);
            }

            var view = ViewBuilder.Git(git, root, DateTimeOffset.Now);
            problems.Git = view.Problems;
            sink.ApplyGit(view);
            sink.SetErrorLine(problems.Compose());
        }
        finally
        {
            sink.EndOperation();
        }
    }

    private async Task LoadTunnelAsync(string root)
    {
        var tunnelGate = options.TunnelGate;

        try
        {
            // Con piu' sessioni la CLI devtunnel viene interrogata da una alla volta: la sezione
            // dice di essere in coda finche' non tocca a lei. L'attesa sta fuori dal try che
            // rilascia, cosi' una cancellazione durante l'attesa non rilascia un gate mai preso.
            if (tunnelGate is not null)
            {
                sink.SetTunnelQueued();
                await tunnelGate.WaitAsync(lifetime).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            return;
        }

        try
        {
            sink.BeginOperation();
            sink.SetTunnelLoading();

            DevTunnelStatus tunnel;

            try
            {
                tunnel = options.PoolAssignmentOf is null
                    ? await DevTunnelReader.ReadAsync(root, cancellationToken: lifetime).ConfigureAwait(false)
                    : options.PoolAssignmentOf(root) is { } assignment
                        ? await DevTunnelReader.ReadAsync(root, assignment.TunnelDescription, lifetime, assignment.Port).ConfigureAwait(false)
                        : DevTunnelStatus.NotShared("Accendi «Tunnel» nell'intestazione dell'ambiente per avere un URL da dare ai colleghi");
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                // Chiusura dell'host durante la lettura.
                return;
            }
            catch (Exception ex)
            {
                tunnel = new DevTunnelStatus(Error: ex.Message);
            }

            sink.ApplyTunnel(ViewBuilder.Tunnel(tunnel, DateTimeOffset.Now));
        }
        finally
        {
            sink.EndOperation();
            tunnelGate?.Release();
        }
    }
}

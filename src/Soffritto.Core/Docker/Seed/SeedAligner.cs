using System.Text.RegularExpressions;
using Soffritto.Core.Configuration;
using Soffritto.Core.Env;

namespace Soffritto.Core.Docker.Seed;

/// <summary>
/// «Allinea seed»: ripristina il seed configurato in un container temporaneo, applica le migrazioni
/// di origin/dev dal worktree del seed, lo compatta e lo salva come file nuovo accanto al
/// configurato, con la data nel nome; seed.path passa al file nuovo, il precedente resta.
/// </summary>
public static partial class SeedAligner
{
    [GeneratedRegex(@"-\d{8}-\d{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex DateSuffix();

    /// <summary>
    /// &lt;cartella del seed&gt;\&lt;nome base&gt;-&lt;yyyyMMdd-HHmmss&gt;.bak, con il nome base senza
    /// estensione e senza un suffisso di data gia' presente.
    /// </summary>
    public static string TargetPathOf(string seedPath, DateTime now)
    {
        var name = DateSuffix().Replace(Path.GetFileNameWithoutExtension(seedPath), string.Empty);
        return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(seedPath))!, $"{name}-{now:yyyyMMdd-HHmmss}.bak");
    }

    /// <summary>
    /// Allinea il seed a origin/dev e chiama <paramref name="onNewSeedPath"/> con il file nuovo dopo
    /// averlo scritto in seed.path. Una migrazione fallita lascia il seed com'e'. Errore o null.
    /// </summary>
    public static async Task<string?> AlignAsync(AppConfiguration c, Action<string> onNewSeedPath, Action<string> log, CancellationToken ct = default)
    {
        if (c.PrimarySeed() is not { Seed: { } seed } backend)
        {
            return "nessun backend ha un seed configurato";
        }

        if (backend.SourceClone is not { Length: > 0 } bare || !SourceCloneRepository.IsBare(bare))
        {
            return $"{backend.Id} non ha un clone sorgente: «Allinea seed» applica le migrazioni di {GitConventions.RemoteDevelopmentBranch}";
        }

        var (seedLock, lockError) = SeedLock.Acquire();
        if (seedLock is null)
        {
            return lockError;
        }

        var provider = SeedProviders.For(seed.Kind);
        var started = false;
        try
        {
            if (await SourceCloneRepository.FetchAsync(bare, log, ct).ConfigureAwait(false) is { } fetchError)
            {
                return fetchError;
            }

            var slot = DockerSlot.Seed(c);
            var entry = slot.Backend!;
            if (await PrepareWorktreeAsync(bare, entry.Path, log, ct).ConfigureAwait(false) is { } worktreeError)
            {
                return worktreeError;
            }

            // Il worktree del seed punta al container temporaneo: la stessa connessione va a
            // --connection e a ConnectionStrings__<nome> del processo dotnet ef.
            var target = entry.Repository.Env is { Target.Length: > 0 } env ? Path.Combine(entry.Path, env.Target) : null;
            var resolution = EnvResolver.Resolve(c, entry, slot, poolUrls: null, targetIsNew: target is null || !File.Exists(target));
            foreach (var line in await EnvResolver.WriteLocalAsync(c, entry, resolution, ct).ConfigureAwait(false))
            {
                log(line);
            }

            var (bak, prepareError) = await SeedArchive.PrepareAsync(seed.Path, log, ct).ConfigureAwait(false);
            if (bak is null)
            {
                return prepareError;
            }

            started = true;
            if (await provider.StartTemporaryAsync(c, log, ct).ConfigureAwait(false) is { } startError)
            {
                return startError;
            }

            if (await provider.WaitReadyAsync(slot.HostConnectionString, ct: ct).ConfigureAwait(false) is { } readyError)
            {
                return readyError;
            }

            if (await provider.RestoreAsync(DockerNames.SeedContainer, bak, slot.HostConnectionString, slot.DatabaseName, log, ct).ConfigureAwait(false) is { } restoreError)
            {
                return restoreError;
            }

            var probe = AppConfiguration.ToProbeOptions(backend);
            foreach (var startup in probe.Contexts.Select(x => x.RelativeStartupProjectPath).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                // Su un worktree appena creato o spostato dotnet ef fallisce senza project.assets.json (NETSDK1004).
                var restore = await ProcessRunner.RunAsync("dotnet", $"restore \"{Path.Combine(entry.Path, startup)}\"", entry.Path, 600, cancellationToken: ct).ConfigureAwait(false);
                if (!restore.Success)
                {
                    return $"dotnet restore di {startup}: {restore.BuildError(3)}";
                }
            }

            foreach (var context in probe.Contexts)
            {
                if (DatabaseGuard.Check(entry.Path, probe, slot.DatabasePort, slot.DatabaseName) is { } guard)
                {
                    return guard;
                }

                log($"migrazioni di {context.Name} da {GitConventions.RemoteDevelopmentBranch}");
                var update = await MigrationApplier.UpdateDatabaseAsync(entry.Path, context, null, log, ct, slot.HostConnectionString, backend.ConnectionStringName).ConfigureAwait(false);
                if (!update.Success)
                {
                    return $"migrazioni di {context.Name} non applicate, il seed resta com'e': {await MigrationApplier.FailureReasonAsync(entry.Path, context, update, ct).ConfigureAwait(false)}";
                }
            }

            var history = await provider.ReadAppliedMigrationsAsync(slot.HostConnectionString, ct).ConfigureAwait(false);
            if (history.Error is not null)
            {
                return $"history del seed allineato: {history.Error}";
            }

            var (dev, devError) = await SeedMigrations.DevMigrationsAsync(backend, ct).ConfigureAwait(false);
            if (dev is null)
            {
                return devError;
            }

            var newSeed = TargetPathOf(seed.Path, DateTime.Now);
            if (await provider.BackupAsync(DockerNames.SeedContainer, slot.HostConnectionString, slot.DatabaseName, newSeed, log, ct).ConfigureAwait(false) is { } backupError)
            {
                return backupError;
            }

            var applied = history.Applied.Select(a => a.MigrationId).ToHashSet(StringComparer.Ordinal);
            var allKnown = dev.Values.SelectMany(ids => ids).ToHashSet(StringComparer.Ordinal);
            var last = dev
                .Select(p => (p.Key, Last: p.Value.Where(applied.Contains).Order(StringComparer.Ordinal).LastOrDefault()))
                .Where(p => p.Last is not null)
                .ToDictionary(p => p.Key, p => p.Last!, StringComparer.Ordinal);
            var unknown = applied.Where(id => !allKnown.Contains(id)).Order(StringComparer.Ordinal).ToList();
            var devCommit = await SourceCloneRepository.RevParseAsync(bare, SeedMigrations.DevReference, ct).ConfigureAwait(false);
            SeedInfo.Write(new SeedInfo(newSeed, await SeedInfo.HashAsync(newSeed, ct).ConfigureAwait(false), DateTimeOffset.Now, GitConventions.RemoteDevelopmentBranch, devCommit, last, unknown));

            var backendId = backend.Id;
            var pathError = ConfigurationFiles.Update(
                configuration =>
                {
                    if (configuration.Repositories.FirstOrDefault(r => string.Equals(r.Id, backendId, StringComparison.OrdinalIgnoreCase)) is not BackendConfiguration { Seed: { } saved })
                    {
                        return $"{backendId} non ha piu' un seed in config.json";
                    }

                    saved.Path = newSeed;
                    return null;
                },
                ConfigurationFileFallback.Refuse);
            if (pathError is not null)
            {
                return $"seed nuovo scritto in {newSeed}, ma seed.path non aggiornato: {pathError}";
            }

            log($"seed.path ora e' {newSeed}; il precedente {seed.Path} resta");
            onNewSeedPath(newSeed);
            return null;
        }
        finally
        {
            if (started && await provider.StopTemporaryAsync(log, CancellationToken.None).ConfigureAwait(false) is { } stopError)
            {
                log(stopError);
            }

            seedLock.Dispose();
        }
    }

    // Il worktree del seed, staccato su origin/dev: si crea se manca, si ferma se ha modifiche.
    private static async Task<string?> PrepareWorktreeAsync(string bare, string worktree, Action<string> log, CancellationToken ct)
    {
        const string dev = GitConventions.RemoteDevelopmentBranch;
        if (!GitDirectory.Exists(worktree))
        {
            var add = await SourceCloneRepository.AddDetachedWorktreeAsync(bare, worktree, dev, log, ct).ConfigureAwait(false);
            return add.Success ? null : $"worktree del seed non creato: {add.GitError()}";
        }

        var status = await GitStatusReader.ReadAsync(worktree, fetch: false, ct).ConfigureAwait(false);
        if (status.Error is not null)
        {
            return $"worktree del seed {worktree}: {status.Error}";
        }

        if (status.DirtyFiles > 0 || GitStatusReader.ReadOperationInProgress(worktree) is not null)
        {
            return $"il worktree del seed {worktree} ha modifiche o un'operazione git a meta': sistemalo prima di allineare";
        }

        var checkout = await SourceCloneRepository.CheckoutDetachedAsync(worktree, dev, log, ct).ConfigureAwait(false);
        return checkout.Success ? null : $"worktree del seed non portato su {dev}: {checkout.GitError()}";
    }
}

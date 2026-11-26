using System.Text.RegularExpressions;
using Soffritto.Core.Configuration;
using Soffritto.Core.Env;

namespace Soffritto.Core.Docker.Seed;

/// <summary>
/// "Align seed": restores the configured seed into a temporary container, applies the origin/dev
/// migrations from the seed worktree, shrinks it and saves it as a new file next to the
/// configured one, with the date in its name; seed.path moves to the new file, the previous one stays.
/// </summary>
public static partial class SeedAligner
{
    [GeneratedRegex(@"-\d{8}-\d{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex DateSuffix();

    /// <summary>
    /// &lt;seed folder&gt;\&lt;base name&gt;-&lt;yyyyMMdd-HHmmss&gt;.bak, with the base name stripped of its
    /// extension and of any existing date suffix.
    /// </summary>
    public static string TargetPathOf(string seedPath, DateTime now)
    {
        var name = DateSuffix().Replace(Path.GetFileNameWithoutExtension(seedPath), string.Empty);
        return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(seedPath))!, $"{name}-{now:yyyyMMdd-HHmmss}.bak");
    }

    /// <summary>
    /// Aligns the seed to origin/dev and calls <paramref name="onNewSeedPath"/> with the new file after
    /// writing it to seed.path. A failed migration leaves the seed as it is. Error or null.
    /// </summary>
    public static async Task<string?> AlignAsync(AppConfiguration c, Action<string> onNewSeedPath, Action<string> log, CancellationToken ct = default)
    {
        if (c.PrimarySeed() is not { Seed: { } seed } backend)
        {
            return "no backend has a seed configured";
        }

        if (backend.SourceClone is not { Length: > 0 } bare || !SourceCloneRepository.IsBare(bare))
        {
            return $"{backend.Id} has no source clone: \"Align seed\" applies the migrations of {GitConventions.RemoteDevelopmentBranch}";
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

            // The seed worktree points to the temporary container: the same connection goes to
            // --connection and to ConnectionStrings__<name> of the dotnet ef process.
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
                // On a freshly created or moved worktree dotnet ef fails without project.assets.json (NETSDK1004).
                var restore = await ProcessRunner.RunAsync("dotnet", $"restore \"{Path.Combine(entry.Path, startup)}\"", entry.Path, 600, cancellationToken: ct).ConfigureAwait(false);
                if (!restore.Success)
                {
                    return $"dotnet restore of {startup}: {restore.BuildError(3)}";
                }
            }

            foreach (var context in probe.Contexts)
            {
                if (DatabaseGuard.Check(entry.Path, probe, slot.DatabasePort, slot.DatabaseName) is { } guard)
                {
                    return guard;
                }

                log($"{context.Name} migrations from {GitConventions.RemoteDevelopmentBranch}");
                var update = await MigrationApplier.UpdateDatabaseAsync(entry.Path, context, null, log, ct, slot.HostConnectionString, backend.ConnectionStringName).ConfigureAwait(false);
                if (!update.Success)
                {
                    return $"{context.Name} migrations not applied, the seed stays as it is: {await MigrationApplier.FailureReasonAsync(entry.Path, context, update, ct).ConfigureAwait(false)}";
                }
            }

            var history = await provider.ReadAppliedMigrationsAsync(slot.HostConnectionString, ct).ConfigureAwait(false);
            if (history.Error is not null)
            {
                return $"history of the aligned seed: {history.Error}";
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
                        return $"{backendId} no longer has a seed in config.json";
                    }

                    saved.Path = newSeed;
                    return null;
                },
                ConfigurationFileFallback.Refuse);
            if (pathError is not null)
            {
                return $"new seed written to {newSeed}, but seed.path was not updated: {pathError}";
            }

            log($"seed.path is now {newSeed}; the previous {seed.Path} is kept");
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

    // The seed worktree, detached on origin/dev: created if missing, stopped if it has changes.
    private static async Task<string?> PrepareWorktreeAsync(string bare, string worktree, Action<string> log, CancellationToken ct)
    {
        const string dev = GitConventions.RemoteDevelopmentBranch;
        if (!GitDirectory.Exists(worktree))
        {
            var add = await SourceCloneRepository.AddDetachedWorktreeAsync(bare, worktree, dev, log, ct).ConfigureAwait(false);
            return add.Success ? null : $"seed worktree not created: {add.GitError()}";
        }

        var status = await GitStatusReader.ReadAsync(worktree, fetch: false, ct).ConfigureAwait(false);
        if (status.Error is not null)
        {
            return $"seed worktree {worktree}: {status.Error}";
        }

        if (status.DirtyFiles > 0 || GitStatusReader.ReadOperationInProgress(worktree) is not null)
        {
            return $"the seed worktree {worktree} has changes or an unfinished git operation: fix it before aligning";
        }

        var checkout = await SourceCloneRepository.CheckoutDetachedAsync(worktree, dev, log, ct).ConfigureAwait(false);
        return checkout.Success ? null : $"seed worktree not moved to {dev}: {checkout.GitError()}";
    }
}

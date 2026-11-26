using Soffritto.Core.Configuration;

namespace Soffritto.Core.Docker.Seed;

/// <summary>Outcome of "Verify seed": compatible if the seed has no migrations the code does not know.</summary>
public sealed record SeedVerification(bool Compatible, IReadOnlyList<ContextSeedResult> Contexts, string? Error);

/// <summary>
/// For one context: the last migration the seed applied, the unknown ones (history rows that
/// no context in the code knows: the table is shared, so they are the same for every
/// context) and how many of the code's migrations the seed is missing.
/// </summary>
public sealed record ContextSeedResult(string Context, string? LastApplied, IReadOnlyList<string> Unknown, int MissingOnDev);

/// <summary>"Verify seed": restores the seed into a temporary container and compares it with the code's migrations.</summary>
public static class SeedVerifier
{
    public static async Task<SeedVerification> VerifyAsync(AppConfiguration c, Action<string> log, CancellationToken ct = default)
    {
        if (c.PrimarySeed() is not { Seed: { } seed } backend)
        {
            return Failed("no backend has a seed configured");
        }

        var (seedLock, lockError) = SeedLock.Acquire();
        if (seedLock is null)
        {
            return Failed(lockError!);
        }

        var provider = SeedProviders.For(seed.Kind);
        var started = false;
        try
        {
            var (bak, prepareError) = await SeedArchive.PrepareAsync(seed.Path, log, ct).ConfigureAwait(false);
            if (bak is null)
            {
                return Failed(prepareError!);
            }

            var slot = DockerSlot.Seed(c);
            started = true;
            if (await provider.StartTemporaryAsync(c, log, ct).ConfigureAwait(false) is { } startError)
            {
                return Failed(startError);
            }

            if (await provider.WaitReadyAsync(slot.HostConnectionString, ct: ct).ConfigureAwait(false) is { } readyError)
            {
                return Failed(readyError);
            }

            if (await provider.RestoreAsync(DockerNames.SeedContainer, bak, slot.HostConnectionString, slot.DatabaseName, log, ct).ConfigureAwait(false) is { } restoreError)
            {
                return Failed(restoreError);
            }

            var history = await provider.ReadAppliedMigrationsAsync(slot.HostConnectionString, ct).ConfigureAwait(false);
            if (history.Error is not null)
            {
                return Failed($"seed history: {history.Error}");
            }

            var (code, source, devCommit, codeError) = await CodeMigrationsAsync(c, backend, log, ct).ConfigureAwait(false);
            if (code is null)
            {
                return Failed(codeError!);
            }

            var applied = history.Applied.Select(a => a.MigrationId).ToHashSet(StringComparer.Ordinal);
            var allKnown = code.Values.SelectMany(ids => ids).ToHashSet(StringComparer.Ordinal);
            var unknown = applied.Where(id => !allKnown.Contains(id)).Order(StringComparer.Ordinal).ToList();
            var results = new List<ContextSeedResult>();
            var last = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (context, ids) in code)
            {
                var lastApplied = ids.Where(applied.Contains).Order(StringComparer.Ordinal).LastOrDefault();
                if (lastApplied is not null)
                {
                    last[context] = lastApplied;
                }

                var missing = SeedMigrations.After(ids, lastApplied).Count;
                results.Add(new ContextSeedResult(context, lastApplied, unknown, missing));
                log($"{context}: last in seed {lastApplied ?? "none"}, {missing} missing compared to {source}");
            }

            if (unknown.Count > 0)
            {
                log($"seed migrations unknown to {source}: {string.Join(", ", unknown)}");
            }

            SeedInfo.Write(new SeedInfo(seed.Path, await SeedInfo.HashAsync(seed.Path, ct).ConfigureAwait(false), DateTimeOffset.Now, source, devCommit, last, unknown));
            log(unknown.Count == 0 ? "seed compatible" : "seed not compatible");
            return new SeedVerification(unknown.Count == 0, results, null);
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

    // The code to compare against: origin/dev of the source clone (after a fetch), or the
    // template environment's backend if there is no source clone.
    private static async Task<(IReadOnlyDictionary<string, IReadOnlyList<string>>? Code, string Source, string? DevCommit, string? Error)> CodeMigrationsAsync(
        AppConfiguration c,
        BackendConfiguration backend,
        Action<string> log,
        CancellationToken ct)
    {
        if (backend.SourceClone is { Length: > 0 } bare && SourceCloneRepository.IsBare(bare))
        {
            if (await SourceCloneRepository.FetchAsync(bare, ct: ct).ConfigureAwait(false) is { } fetchError)
            {
                log($"source clone fetch failed, comparing with the last {GitConventions.RemoteDevelopmentBranch} read: {fetchError}");
            }

            var (dev, error) = await SeedMigrations.DevMigrationsAsync(backend, ct).ConfigureAwait(false);
            var commit = await SourceCloneRepository.RevParseAsync(bare, SeedMigrations.DevReference, ct).ConfigureAwait(false);
            return (dev, GitConventions.RemoteDevelopmentBranch, commit, error);
        }

        if (c.TemplateNumber() is not { } template || c.Environment(template) is not { } environment)
        {
            return (null, string.Empty, null, "no source clone and no template environment to read the migrations from");
        }

        var root = c.FolderOf(backend, environment);
        var code = backend.Contexts.ToDictionary(
            ctx => ctx.Name,
            ctx => (IReadOnlyList<string>)MigrationScanner.Scan(Path.Combine(root, ctx.Migrations)).Select(m => m.MigrationId).ToList(),
            StringComparer.Ordinal);
        return (code, $"template environment {template}", null, null);
    }

    private static SeedVerification Failed(string error) => new(false, [], error);
}

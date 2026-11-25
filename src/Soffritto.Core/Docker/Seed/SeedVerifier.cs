using Soffritto.Core.Configuration;

namespace Soffritto.Core.Docker.Seed;

/// <summary>Esito di «Verifica seed»: compatibile se il seed non ha migrazioni che il codice non conosce.</summary>
public sealed record SeedVerification(bool Compatible, IReadOnlyList<ContextSeedResult> Contexts, string? Error);

/// <summary>
/// Per un context: l'ultima migrazione applicata dal seed, le sconosciute (righe della history che
/// nessun context del codice conosce: la tabella e' condivisa, quindi sono le stesse per ogni
/// context) e quante migrazioni del codice mancano al seed.
/// </summary>
public sealed record ContextSeedResult(string Context, string? LastApplied, IReadOnlyList<string> Unknown, int MissingOnDev);

/// <summary>«Verifica seed»: ripristina il seed in un container temporaneo e lo confronta con le migrazioni del codice.</summary>
public static class SeedVerifier
{
    public static async Task<SeedVerification> VerifyAsync(AppConfiguration c, Action<string> log, CancellationToken ct = default)
    {
        if (c.PrimarySeed() is not { Seed: { } seed } backend)
        {
            return Failed("nessun backend ha un seed configurato");
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
                return Failed($"history del seed: {history.Error}");
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
                log($"{context}: ultima del seed {lastApplied ?? "nessuna"}, {missing} mancanti rispetto a {source}");
            }

            if (unknown.Count > 0)
            {
                log($"migrazioni del seed che {source} non conosce: {string.Join(", ", unknown)}");
            }

            SeedInfo.Write(new SeedInfo(seed.Path, await SeedInfo.HashAsync(seed.Path, ct).ConfigureAwait(false), DateTimeOffset.Now, source, devCommit, last, unknown));
            log(unknown.Count == 0 ? "seed compatibile" : "seed non compatibile");
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

    // Il codice con cui confrontare: origin/dev del clone sorgente (dopo un fetch), oppure il
    // backend dell'ambiente modello se il clone sorgente non c'e'.
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
                log($"fetch del clone sorgente non riuscito, confronto con l'ultimo {GitConventions.RemoteDevelopmentBranch} letto: {fetchError}");
            }

            var (dev, error) = await SeedMigrations.DevMigrationsAsync(backend, ct).ConfigureAwait(false);
            var commit = await SourceCloneRepository.RevParseAsync(bare, SeedMigrations.DevReference, ct).ConfigureAwait(false);
            return (dev, GitConventions.RemoteDevelopmentBranch, commit, error);
        }

        if (c.TemplateNumber() is not { } template || c.Environment(template) is not { } environment)
        {
            return (null, string.Empty, null, "nessun clone sorgente e nessun ambiente modello da cui leggere le migrazioni");
        }

        var root = c.FolderOf(backend, environment);
        var code = backend.Contexts.ToDictionary(
            ctx => ctx.Name,
            ctx => (IReadOnlyList<string>)MigrationScanner.Scan(Path.Combine(root, ctx.Migrations)).Select(m => m.MigrationId).ToList(),
            StringComparer.Ordinal);
        return (code, $"ambiente modello {template}", null, null);
    }

    private static SeedVerification Failed(string error) => new(false, [], error);
}

namespace Soffritto.Core.Docker;

/// <summary>
/// Il clone sorgente bare di un repository e i suoi worktree. Fetch, comandi worktree ed
/// esclusioni passano dal lock del clone (<see cref="GitDirectory.LockOf"/>): i worktree
/// condividono ref e cartella comune.
/// </summary>
public static class SourceCloneRepository
{
    public const string Remote = "origin";

    /// <summary>Crea il clone bare: init, remote origin e fetch. Errore o null.</summary>
    public static async Task<string?> InitAsync(string bare, string cloneUrl, Action<string>? log = null, CancellationToken ct = default)
    {
        if (Directory.Exists(bare) && Directory.EnumerateFileSystemEntries(bare).Any())
        {
            return $"la cartella {bare} esiste e non e' vuota";
        }

        Directory.CreateDirectory(bare);
        return await LockedAsync(bare, async () =>
        {
            var init = await GitAsync(bare, "init --bare", 60, ct).ConfigureAwait(false);
            if (!init.Success)
            {
                return $"git init --bare: {init.GitError()}";
            }

            var remote = await GitAsync(bare, $"remote add {Remote} \"{cloneUrl}\"", 30, ct).ConfigureAwait(false);
            if (!remote.Success)
            {
                return $"git remote add: {remote.GitError()}";
            }

            log?.Invoke($"fetch di {cloneUrl} in {bare}");
            var fetch = await GitAsync(bare, $"fetch --prune {Remote}", 1800, ct, log).ConfigureAwait(false);
            return fetch.Success ? null : $"git fetch: {fetch.GitError()}";
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Aggiorna i remote-tracking ref del clone sorgente. Errore o null.</summary>
    public static async Task<string?> FetchAsync(string bare, Action<string>? log = null, CancellationToken ct = default)
        => await LockedAsync(bare, async () =>
        {
            var fetch = await GitAsync(bare, $"fetch --prune {Remote}", 600, ct, log).ConfigureAwait(false);
            return fetch.Success ? null : $"git fetch: {fetch.GitError()}";
        }, ct).ConfigureAwait(false);

    /// <summary>I branch di origin (senza "origin/"); null se git fallisce.</summary>
    public static async Task<IReadOnlyList<string>?> RemoteBranchesAsync(string bare, CancellationToken ct = default)
    {
        var refs = await GitAsync(bare, $"for-each-ref --format=%(refname) refs/remotes/{Remote}", 30, ct).ConfigureAwait(false);
        if (!refs.Success)
        {
            return null;
        }

        var prefix = $"refs/remotes/{Remote}/";
        return Lines(refs.StandardOutput)
            .Where(r => r.StartsWith(prefix, StringComparison.Ordinal))
            .Select(r => r[prefix.Length..])
            .Where(n => n != "HEAD")
            .ToList();
    }

    /// <summary>I branch locali del clone sorgente; null se git fallisce.</summary>
    public static async Task<IReadOnlyList<string>?> LocalBranchesAsync(string bare, CancellationToken ct = default)
    {
        var refs = await GitAsync(bare, "for-each-ref --format=%(refname:short) refs/heads", 30, ct).ConfigureAwait(false);
        return refs.Success ? Lines(refs.StandardOutput).ToList() : null;
    }

    /// <summary>I branch portati da un worktree, con la cartella del worktree; null se git fallisce.</summary>
    public static async Task<IReadOnlyDictionary<string, string>?> WorktreeBranchesAsync(string bare, CancellationToken ct = default)
    {
        var list = await GitAsync(bare, "worktree list --porcelain", 30, ct).ConfigureAwait(false);
        if (!list.Success)
        {
            return null;
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        string? path = null;
        foreach (var line in Lines(list.StandardOutput))
        {
            if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                path = Path.GetFullPath(line["worktree ".Length..]);
            }
            else if (line.StartsWith("branch refs/heads/", StringComparison.Ordinal) && path is not null)
            {
                result[line["branch refs/heads/".Length..]] = path;
            }
        }

        return result;
    }

    /// <summary>
    /// Un worktree sul branch: se il branch e' gia' in un worktree rifiuta (git porta un branch in
    /// un solo worktree) senza creare nulla; se esiste in locale lo usa; se esiste solo su origin
    /// crea il branch locale che lo traccia; altrimenti lo crea da origin/&lt;base&gt; senza upstream.
    /// </summary>
    public static async Task<ProcessOutcome> AddWorktreeAsync(
        string bare, string path, string branch, string baseBranch = GitConventions.DevelopmentBranch, Action<string>? log = null, CancellationToken ct = default)
    {
        if (branch.Contains('"') || path.Contains('"'))
        {
            return Refused("branch e cartella non possono contenere virgolette");
        }

        return await LockedAsync(bare, async () =>
        {
            var worktrees = await WorktreeBranchesAsync(bare, ct).ConfigureAwait(false);
            var locals = await LocalBranchesAsync(bare, ct).ConfigureAwait(false);
            var remotes = await RemoteBranchesAsync(bare, ct).ConfigureAwait(false);
            if (worktrees is null || locals is null || remotes is null)
            {
                return Refused($"lettura dei branch di {bare} fallita");
            }

            if (worktrees.TryGetValue(branch, out var occupied))
            {
                return Refused($"il branch {branch} e' gia' nel worktree {occupied}");
            }

            string arguments;
            if (locals.Contains(branch, StringComparer.Ordinal))
            {
                arguments = $"worktree add \"{path}\" \"{branch}\"";
                log?.Invoke($"worktree su {branch}, branch locale esistente");
            }
            else if (remotes.Contains(branch, StringComparer.Ordinal))
            {
                arguments = $"worktree add --track -b \"{branch}\" \"{path}\" \"{Remote}/{branch}\"";
                log?.Invoke($"worktree su {branch}, nuovo branch locale che traccia {Remote}/{branch}");
            }
            else
            {
                arguments = $"worktree add --no-track -b \"{branch}\" \"{path}\" \"{Remote}/{baseBranch}\"";
                log?.Invoke($"worktree su {branch}, nuovo branch da {Remote}/{baseBranch} senza upstream");
            }

            return await GitAsync(bare, arguments, 600, ct, log).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Un worktree staccato su un commit o un ref (es. il worktree del seed su origin/dev).</summary>
    public static async Task<ProcessOutcome> AddDetachedWorktreeAsync(string bare, string path, string commitish, Action<string>? log = null, CancellationToken ct = default)
        => await LockedAsync(bare, () => GitAsync(bare, $"worktree add --detach \"{path}\" \"{commitish}\"", 600, ct, log), ct).ConfigureAwait(false);

    /// <summary>Porta un worktree staccato su un altro commit; git rifiuta se le modifiche locali andrebbero perse.</summary>
    public static async Task<ProcessOutcome> CheckoutDetachedAsync(string worktree, string commitish, Action<string>? log = null, CancellationToken ct = default)
        => await LockedAsync(worktree, () => GitAsync(worktree, $"checkout --detach \"{commitish}\"", 300, ct, log), ct).ConfigureAwait(false);

    /// <summary>Toglie un worktree senza --force (git rifiuta se e' sporco), poi prune.</summary>
    public static async Task<ProcessOutcome> RemoveWorktreeAsync(string bare, string path, Action<string>? log = null, CancellationToken ct = default)
        => await LockedAsync(bare, async () =>
        {
            var remove = await GitAsync(bare, $"worktree remove \"{path}\"", 300, ct, log).ConfigureAwait(false);
            if (remove.Success)
            {
                await GitAsync(bare, "worktree prune", 60, ct).ConfigureAwait(false);
            }

            return remove;
        }, ct).ConfigureAwait(false);

    /// <summary>Toglie dal clone le registrazioni dei worktree la cui cartella non c'e' piu'.</summary>
    public static async Task<ProcessOutcome> PruneWorktreesAsync(string bare, CancellationToken ct = default)
        => await LockedAsync(bare, () => GitAsync(bare, "worktree prune", 60, ct), ct).ConfigureAwait(false);

    /// <summary>I commit di HEAD che non stanno su nessun remote ("sha soggetto"); Error se git fallisce.</summary>
    public static async Task<(IReadOnlyList<string> Commits, string? Error)> UnpushedCommitsAsync(string worktree, CancellationToken ct = default)
    {
        var log = await GitAsync(worktree, "log --oneline HEAD --not --remotes", 30, ct).ConfigureAwait(false);
        return log.Success ? (Lines(log.StandardOutput).ToList(), null) : ([], log.GitError());
    }

    /// <summary>I nomi dei file di una cartella del repository a un ref, senza checkout.</summary>
    public static async Task<(IReadOnlyList<string> Names, string? Error)> ListFilesAsync(string repository, string reference, string folder, CancellationToken ct = default)
    {
        var path = folder.Replace('\\', '/').Trim('/');
        var tree = await GitAsync(repository, $"ls-tree --name-only \"{reference}:{path}\"", 60, ct).ConfigureAwait(false);
        return tree.Success ? (Lines(tree.StandardOutput).ToList(), null) : ([], tree.GitError());
    }

    /// <summary>Lo sha completo di un ref; null se non esiste.</summary>
    public static async Task<string?> RevParseAsync(string repository, string reference, CancellationToken ct = default)
    {
        var rev = await GitAsync(repository, $"rev-parse --verify --quiet \"{reference}^{{commit}}\"", 30, ct).ConfigureAwait(false);
        return rev.Success ? rev.StandardOutput.Trim() : null;
    }

    /// <summary>Vero se la cartella e' un repository bare (HEAD e objects senza .git).</summary>
    public static bool IsBare(string path)
        => File.Exists(Path.Combine(path, "HEAD")) && Directory.Exists(Path.Combine(path, "objects")) && !GitDirectory.Exists(path);

    /// <summary>
    /// Vero se git ignora il percorso (relativo alla radice del worktree), anche se il file non
    /// c'e'; un file versionato non e' mai ignorato. Null se git fallisce.
    /// </summary>
    public static async Task<bool?> IsIgnoredAsync(string worktree, string relativePath, CancellationToken ct = default)
    {
        var check = await GitAsync(worktree, $"check-ignore -q -- \"{relativePath.Replace('\\', '/')}\"", 30, ct).ConfigureAwait(false);
        return check.StartError is null && !check.TimedOut && check.ExitCode is 0 or 1 ? check.ExitCode == 0 : null;
    }

    /// <summary>
    /// Aggiunge le righe a info\exclude della cartella comune del clone (valgono per tutti i suoi
    /// worktree), senza duplicarle. Errore o null.
    /// </summary>
    public static async Task<string?> ExcludeAsync(string worktree, IReadOnlyList<string> patterns, CancellationToken ct = default)
    {
        if ((GitDirectory.Common(worktree) ?? GitDirectory.Resolve(worktree)) is not { } gitDirectory)
        {
            return $"{worktree} non e' un repository git";
        }

        return await LockedAsync(worktree, () =>
        {
            try
            {
                var file = Path.Combine(gitDirectory, "info", "exclude");
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                var text = File.Exists(file) ? File.ReadAllText(file) : string.Empty;
                var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToHashSet(StringComparer.Ordinal);
                var missing = patterns.Where(p => !lines.Contains(p)).Distinct(StringComparer.Ordinal).ToList();
                if (missing.Count == 0)
                {
                    return Task.FromResult<string?>(null);
                }

                var separator = text.Length > 0 && !text.EndsWith('\n') ? "\n" : string.Empty;
                var temporary = file + ".tmp";
                File.WriteAllText(temporary, text + separator + string.Join("\n", missing) + "\n");
                File.Move(temporary, file, overwrite: true);
                return Task.FromResult<string?>(null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Task.FromResult<string?>($"info\\exclude di {gitDirectory}: {ex.Message}");
            }
        }, ct).ConfigureAwait(false);
    }

    private static async Task<T> LockedAsync<T>(string folder, Func<Task<T>> action, CancellationToken ct)
    {
        var gate = GitDirectory.LockOf(folder);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private static ProcessOutcome Refused(string reason) => new(-1, string.Empty, string.Empty, false, reason);

    private static IEnumerable<string> Lines(string output)
        => output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).Where(l => l.Length > 0);

    private static Task<ProcessOutcome> GitAsync(string folder, string arguments, int timeoutSeconds, CancellationToken ct, Action<string>? log = null)
        => ProcessRunner.RunAsync("git", arguments, folder, timeoutSeconds, onOutputLine: log, cancellationToken: ct);
}

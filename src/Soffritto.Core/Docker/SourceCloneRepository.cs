namespace Soffritto.Core.Docker;

/// <summary>
/// A repository's bare source clone and its worktrees. Fetch, worktree commands and
/// excludes go through the clone's lock (<see cref="GitDirectory.LockOf"/>): worktrees
/// share refs and the common folder.
/// </summary>
public static class SourceCloneRepository
{
    public const string Remote = "origin";

    /// <summary>Creates the bare clone: init, remote origin and fetch. Error or null.</summary>
    public static async Task<string?> InitAsync(string bare, string cloneUrl, Action<string>? log = null, CancellationToken ct = default)
    {
        if (Directory.Exists(bare) && Directory.EnumerateFileSystemEntries(bare).Any())
        {
            return $"folder {bare} exists and is not empty";
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

            log?.Invoke($"fetching {cloneUrl} into {bare}");
            var fetch = await GitAsync(bare, $"fetch --prune {Remote}", 1800, ct, log).ConfigureAwait(false);
            return fetch.Success ? null : $"git fetch: {fetch.GitError()}";
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Updates the source clone's remote-tracking refs. Error or null.</summary>
    public static async Task<string?> FetchAsync(string bare, Action<string>? log = null, CancellationToken ct = default)
        => await LockedAsync(bare, async () =>
        {
            var fetch = await GitAsync(bare, $"fetch --prune {Remote}", 600, ct, log).ConfigureAwait(false);
            return fetch.Success ? null : $"git fetch: {fetch.GitError()}";
        }, ct).ConfigureAwait(false);

    /// <summary>The branches on origin (without "origin/"); null if git fails.</summary>
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

    /// <summary>The source clone's local branches; null if git fails.</summary>
    public static async Task<IReadOnlyList<string>?> LocalBranchesAsync(string bare, CancellationToken ct = default)
    {
        var refs = await GitAsync(bare, "for-each-ref --format=%(refname:short) refs/heads", 30, ct).ConfigureAwait(false);
        return refs.Success ? Lines(refs.StandardOutput).ToList() : null;
    }

    /// <summary>The branches checked out in a worktree, with the worktree's folder; null if git fails.</summary>
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
    /// A worktree on the branch: if the branch is already in a worktree it refuses (git checks out a branch in
    /// only one worktree) without creating anything; if it exists locally it uses it; if it exists only on origin
    /// it creates the local branch tracking it; otherwise it creates it from origin/&lt;base&gt; without an upstream.
    /// </summary>
    public static async Task<ProcessOutcome> AddWorktreeAsync(
        string bare, string path, string branch, string baseBranch = GitConventions.DevelopmentBranch, Action<string>? log = null, CancellationToken ct = default)
    {
        if (branch.Contains('"') || path.Contains('"'))
        {
            return Refused("branch and folder cannot contain quotes");
        }

        return await LockedAsync(bare, async () =>
        {
            var worktrees = await WorktreeBranchesAsync(bare, ct).ConfigureAwait(false);
            var locals = await LocalBranchesAsync(bare, ct).ConfigureAwait(false);
            var remotes = await RemoteBranchesAsync(bare, ct).ConfigureAwait(false);
            if (worktrees is null || locals is null || remotes is null)
            {
                return Refused($"could not read the branches of {bare}");
            }

            if (worktrees.TryGetValue(branch, out var occupied))
            {
                return Refused($"branch {branch} is already in worktree {occupied}");
            }

            string arguments;
            if (locals.Contains(branch, StringComparer.Ordinal))
            {
                arguments = $"worktree add \"{path}\" \"{branch}\"";
                log?.Invoke($"worktree on {branch}, existing local branch");
            }
            else if (remotes.Contains(branch, StringComparer.Ordinal))
            {
                arguments = $"worktree add --track -b \"{branch}\" \"{path}\" \"{Remote}/{branch}\"";
                log?.Invoke($"worktree on {branch}, new local branch tracking {Remote}/{branch}");
            }
            else
            {
                arguments = $"worktree add --no-track -b \"{branch}\" \"{path}\" \"{Remote}/{baseBranch}\"";
                log?.Invoke($"worktree on {branch}, new branch from {Remote}/{baseBranch} without upstream");
            }

            return await GitAsync(bare, arguments, 600, ct, log).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>A worktree detached on a commit or a ref (e.g. the seed worktree on origin/dev).</summary>
    public static async Task<ProcessOutcome> AddDetachedWorktreeAsync(string bare, string path, string commitish, Action<string>? log = null, CancellationToken ct = default)
        => await LockedAsync(bare, () => GitAsync(bare, $"worktree add --detach \"{path}\" \"{commitish}\"", 600, ct, log), ct).ConfigureAwait(false);

    /// <summary>Moves a detached worktree to another commit; git refuses if local changes would be lost.</summary>
    public static async Task<ProcessOutcome> CheckoutDetachedAsync(string worktree, string commitish, Action<string>? log = null, CancellationToken ct = default)
        => await LockedAsync(worktree, () => GitAsync(worktree, $"checkout --detach \"{commitish}\"", 300, ct, log), ct).ConfigureAwait(false);

    /// <summary>Removes a worktree without --force (git refuses if it is dirty), then prunes.</summary>
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

    /// <summary>Removes from the clone the registrations of worktrees whose folder no longer exists.</summary>
    public static async Task<ProcessOutcome> PruneWorktreesAsync(string bare, CancellationToken ct = default)
        => await LockedAsync(bare, () => GitAsync(bare, "worktree prune", 60, ct), ct).ConfigureAwait(false);

    /// <summary>The commits of HEAD that are on no remote ("sha subject"); Error if git fails.</summary>
    public static async Task<(IReadOnlyList<string> Commits, string? Error)> UnpushedCommitsAsync(string worktree, CancellationToken ct = default)
    {
        var log = await GitAsync(worktree, "log --oneline HEAD --not --remotes", 30, ct).ConfigureAwait(false);
        return log.Success ? (Lines(log.StandardOutput).ToList(), null) : ([], log.GitError());
    }

    /// <summary>The file names of a repository folder at a ref, without checkout.</summary>
    public static async Task<(IReadOnlyList<string> Names, string? Error)> ListFilesAsync(string repository, string reference, string folder, CancellationToken ct = default)
    {
        var path = folder.Replace('\\', '/').Trim('/');
        var tree = await GitAsync(repository, $"ls-tree --name-only \"{reference}:{path}\"", 60, ct).ConfigureAwait(false);
        return tree.Success ? (Lines(tree.StandardOutput).ToList(), null) : ([], tree.GitError());
    }

    /// <summary>The full sha of a ref; null if it does not exist.</summary>
    public static async Task<string?> RevParseAsync(string repository, string reference, CancellationToken ct = default)
    {
        var rev = await GitAsync(repository, $"rev-parse --verify --quiet \"{reference}^{{commit}}\"", 30, ct).ConfigureAwait(false);
        return rev.Success ? rev.StandardOutput.Trim() : null;
    }

    /// <summary>True if the folder is a bare repository (HEAD and objects without .git).</summary>
    public static bool IsBare(string path)
        => File.Exists(Path.Combine(path, "HEAD")) && Directory.Exists(Path.Combine(path, "objects")) && !GitDirectory.Exists(path);

    /// <summary>
    /// True if git ignores the path (relative to the worktree root), even if the file does not
    /// exist; a versioned file is never ignored. Null if git fails.
    /// </summary>
    public static async Task<bool?> IsIgnoredAsync(string worktree, string relativePath, CancellationToken ct = default)
    {
        var check = await GitAsync(worktree, $"check-ignore -q -- \"{relativePath.Replace('\\', '/')}\"", 30, ct).ConfigureAwait(false);
        return check.StartError is null && !check.TimedOut && check.ExitCode is 0 or 1 ? check.ExitCode == 0 : null;
    }

    /// <summary>
    /// Adds the lines to info\exclude in the clone's common folder (they apply to all its
    /// worktrees), without duplicating them. Error or null.
    /// </summary>
    public static async Task<string?> ExcludeAsync(string worktree, IReadOnlyList<string> patterns, CancellationToken ct = default)
    {
        if ((GitDirectory.Common(worktree) ?? GitDirectory.Resolve(worktree)) is not { } gitDirectory)
        {
            return $"{worktree} is not a git repository";
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
                return Task.FromResult<string?>($"info\\exclude in {gitDirectory}: {ex.Message}");
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

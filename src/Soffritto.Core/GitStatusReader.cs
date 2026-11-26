namespace Soffritto.Core;

public static class GitStatusReader
{
    /// <summary>
    /// Repository status via git.exe. With <paramref name="fetch"/> on it runs
    /// a fetch first: it updates only the remote-tracking refs, never the working tree.
    /// </summary>
    public static async Task<GitStatus> ReadAsync(
        string repositoryRoot,
        bool fetch,
        CancellationToken cancellationToken = default)
    {
        if (!GitDirectory.Exists(repositoryRoot))
        {
            return new GitStatus(Error: "No git repository in this folder.");
        }

        var head = await GitAsync(repositoryRoot, "rev-parse --abbrev-ref HEAD", 10, cancellationToken).ConfigureAwait(false);
        if (!head.Success)
        {
            var reason = head.Tail(2);
            return new GitStatus(Error: reason.Length > 0 ? reason : "git is not available in PATH.");
        }

        var branch = head.StandardOutput.Trim();
        var detached = branch.Equals("HEAD", StringComparison.Ordinal);
        if (detached)
        {
            var describe = await GitAsync(repositoryRoot, "rev-parse --short HEAD", 10, cancellationToken).ConfigureAwait(false);
            branch = describe.Success ? $"(detached @ {describe.StandardOutput.Trim()})" : "(detached)";
        }

        string? fetchError = null;
        if (fetch && !detached)
        {
            var fetched = await SerializedAsync(
                repositoryRoot,
                () => GitAsync(repositoryRoot, "fetch --quiet", 60, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            if (!fetched.Success)
            {
                var reason = fetched.Tail(2);
                fetchError = reason.Length > 0 ? reason : "fetch failed (timeout or unreachable remote).";
            }
        }

        var ahead = 0;
        var behind = 0;
        var hasUpstream = false;

        if (!detached)
        {
            var counts = await GitAsync(repositoryRoot, "rev-list --left-right --count HEAD...@{upstream}", 15, cancellationToken).ConfigureAwait(false);
            if (counts.Success)
            {
                var parts = counts.StandardOutput.Split(['\t', ' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && int.TryParse(parts[0], out ahead) && int.TryParse(parts[1], out behind))
                {
                    hasUpstream = true;
                }
            }
        }

        var status = await GitAsync(repositoryRoot, "status --porcelain", 30, cancellationToken).ConfigureAwait(false);
        var dirty = status.Success
            ? status.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length
            : 0;

        var refs = await GitAsync(repositoryRoot, "for-each-ref --format=%(refname) refs/heads refs/remotes", 15, cancellationToken).ConfigureAwait(false);

        return new GitStatus(
            Branch: branch,
            Ahead: ahead,
            Behind: behind,
            DirtyFiles: dirty,
            HasUpstream: hasUpstream,
            IsDetached: detached,
            FetchError: fetchError,
            Branches: refs.Success ? ParseBranches(refs.StandardOutput) : null);
    }

    /// <summary>
    /// The git operation left unfinished in the repository (merge, rebase, cherry-pick, revert), or
    /// null. status --porcelain does not show it once the conflicts have been resolved.
    /// </summary>
    public static string? ReadOperationInProgress(string repositoryRoot)
    {
        if (GitDirectory.Resolve(repositoryRoot) is not { } git)
        {
            return null;
        }

        return Directory.Exists(Path.Combine(git, "rebase-merge")) || Directory.Exists(Path.Combine(git, "rebase-apply")) ? "rebase in progress"
            : File.Exists(Path.Combine(git, "MERGE_HEAD")) ? "merge in progress"
            : File.Exists(Path.Combine(git, "CHERRY_PICK_HEAD")) ? "cherry-pick in progress"
            : File.Exists(Path.Combine(git, "REVERT_HEAD")) ? "revert in progress"
            : null;
    }

    /// <summary>
    /// Switches branch with <c>git switch</c>: it refuses on its own if local changes would
    /// conflict, and for a name present only on the remote it creates the local branch tracking it.
    /// </summary>
    public static Task<ProcessOutcome> SwitchAsync(
        string repositoryRoot,
        string branch,
        Action<string>? onOutputLine = null,
        CancellationToken cancellationToken = default)
    {
        return ProcessRunner.RunAsync(
            "git",
            $"switch \"{branch}\"",
            repositoryRoot,
            timeoutSeconds: 120,
            onOutputLine: onOutputLine,
            cancellationToken: cancellationToken);
    }

    // refs/heads/<name> and refs/remotes/<remote>/<name>: the name is what gets picked in the
    // dropdown, the remote does not matter. The remote's symbolic HEAD pointer is not a branch.
    private static List<string> ParseBranches(string output)
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var reference = raw.Trim();

            if (reference.StartsWith("refs/heads/", StringComparison.Ordinal))
            {
                names.Add(reference["refs/heads/".Length..]);
                continue;
            }

            if (reference.StartsWith("refs/remotes/", StringComparison.Ordinal))
            {
                var rest = reference["refs/remotes/".Length..];
                var slash = rest.IndexOf('/');
                if (slash > 0 && slash < rest.Length - 1)
                {
                    var name = rest[(slash + 1)..];
                    if (name != "HEAD")
                    {
                        names.Add(name);
                    }
                }
            }
        }

        return [.. names];
    }

    /// <summary>
    /// A pure fast-forward pull. <c>--ff-only</c> is not a cosmetic precaution: if a
    /// fast-forward is not possible git refuses instead of creating a merge or conflicts.
    /// </summary>
    public static Task<ProcessOutcome> FastForwardAsync(
        string repositoryRoot,
        Action<string>? onOutputLine = null,
        CancellationToken cancellationToken = default)
    {
        return SerializedAsync(
            repositoryRoot,
            () => ProcessRunner.RunAsync(
                "git",
                "pull --ff-only",
                repositoryRoot,
                timeoutSeconds: 120,
                onOutputLine: onOutputLine,
                cancellationToken: cancellationToken),
            cancellationToken);
    }

    /// <summary>
    /// Local branches left without their upstream on the remote. <c>--prune</c> is part of the
    /// read: as long as deleted remote-tracking refs stay on disk git marks no
    /// upstream as <c>gone</c>, and the list would always be empty. The current branch never
    /// appears: git would refuse to delete it anyway.
    /// </summary>
    public static async Task<PrunableBranches> ReadPrunableBranchesAsync(
        string repositoryRoot,
        Action<string>? onOutputLine = null,
        CancellationToken cancellationToken = default)
    {
        var fetched = await SerializedAsync(
            repositoryRoot,
            () => ProcessRunner.RunAsync(
                "git",
                "fetch --prune",
                repositoryRoot,
                timeoutSeconds: 120,
                onOutputLine: onOutputLine,
                cancellationToken: cancellationToken),
            cancellationToken).ConfigureAwait(false);

        if (!fetched.Success)
        {
            var reason = fetched.GitError();
            return new PrunableBranches([], reason.Length > 0 ? reason : "fetch --prune failed.");
        }

        var head = await GitAsync(repositoryRoot, "rev-parse --abbrev-ref HEAD", 10, cancellationToken).ConfigureAwait(false);
        var current = head.Success ? head.StandardOutput.Trim() : null;

        var refs = await GitAsync(
            repositoryRoot,
            "for-each-ref --format=%(refname:short)%09%(upstream:track) refs/heads",
            15,
            cancellationToken).ConfigureAwait(false);

        if (!refs.Success)
        {
            var reason = refs.Tail(2);
            return new PrunableBranches([], reason.Length > 0 ? reason : "could not read the local branches.");
        }

        return new PrunableBranches(ParseGoneBranches(refs.StandardOutput, current));
    }

    /// <summary>
    /// Deletes the given local branches with <c>branch -D</c>. <c>-d</c> is not an alternative:
    /// with history merged via squash or rebase it would refuse exactly the branches already integrated,
    /// which are the normal case here. The user's confirmation happens upstream.
    /// </summary>
    public static Task<ProcessOutcome> DeleteBranchesAsync(
        string repositoryRoot,
        IEnumerable<string> branches,
        Action<string>? onOutputLine = null,
        CancellationToken cancellationToken = default)
    {
        var names = string.Join(" ", branches.Select(b => $"\"{b}\""));

        return ProcessRunner.RunAsync(
            "git",
            $"branch -D {names}",
            repositoryRoot,
            timeoutSeconds: 60,
            onOutputLine: onOutputLine,
            cancellationToken: cancellationToken);
    }

    // Lines "<branch>\t[gone]" or "<branch>\t[ahead 2]": the gone marker says the
    // configured upstream no longer exists. A branch without an upstream has an empty column and
    // is not a candidate: it did not "disappear from the remote", it was never there.
    private static List<string> ParseGoneBranches(string output, string? currentBranch)
    {
        var names = new List<string>();

        foreach (var raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = raw.TrimEnd('\r').Split('\t');
            if (parts.Length < 2)
            {
                continue;
            }

            var name = parts[0].Trim();
            if (name.Length == 0 || name.Equals(currentBranch, StringComparison.Ordinal))
            {
                continue;
            }

            if (parts[1].Contains("gone", StringComparison.Ordinal))
            {
                names.Add(name);
            }
        }

        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    // Worktrees of the same source clone share refs: two fetches at once would give
    // "cannot lock ref". A normal clone has its own refs and waits for nobody.
    private static async Task<ProcessOutcome> SerializedAsync(string repositoryRoot, Func<Task<ProcessOutcome>> run, CancellationToken cancellationToken)
    {
        if (!GitDirectory.IsWorktree(repositoryRoot))
        {
            return await run().ConfigureAwait(false);
        }

        var gate = GitDirectory.LockOf(repositoryRoot);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await run().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private static Task<ProcessOutcome> GitAsync(
        string repositoryRoot,
        string arguments,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        return ProcessRunner.RunAsync("git", arguments, repositoryRoot, timeoutSeconds, cancellationToken: cancellationToken);
    }
}

namespace Soffritto.Core;

public static class GitStatusReader
{
    /// <summary>
    /// Stato del repository tramite git.exe. Con <paramref name="fetch"/> attivo esegue
    /// prima un fetch: aggiorna solo i remote-tracking ref, mai il working tree.
    /// </summary>
    public static async Task<GitStatus> ReadAsync(
        string repositoryRoot,
        bool fetch,
        CancellationToken cancellationToken = default)
    {
        if (!GitDirectory.Exists(repositoryRoot))
        {
            return new GitStatus(Error: "Nessun repository git in questa cartella.");
        }

        var head = await GitAsync(repositoryRoot, "rev-parse --abbrev-ref HEAD", 10, cancellationToken).ConfigureAwait(false);
        if (!head.Success)
        {
            var reason = head.Tail(2);
            return new GitStatus(Error: reason.Length > 0 ? reason : "git non disponibile nel PATH.");
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
                fetchError = reason.Length > 0 ? reason : "fetch fallito (timeout o remote irraggiungibile).";
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
    /// L'operazione git lasciata a meta' nel repository (merge, rebase, cherry-pick, revert), o
    /// null. Lo stato --porcelain non la mostra se i conflitti sono gia' stati risolti.
    /// </summary>
    public static string? ReadOperationInProgress(string repositoryRoot)
    {
        if (GitDirectory.Resolve(repositoryRoot) is not { } git)
        {
            return null;
        }

        return Directory.Exists(Path.Combine(git, "rebase-merge")) || Directory.Exists(Path.Combine(git, "rebase-apply")) ? "rebase in corso"
            : File.Exists(Path.Combine(git, "MERGE_HEAD")) ? "merge in corso"
            : File.Exists(Path.Combine(git, "CHERRY_PICK_HEAD")) ? "cherry-pick in corso"
            : File.Exists(Path.Combine(git, "REVERT_HEAD")) ? "revert in corso"
            : null;
    }

    /// <summary>
    /// Cambio branch con <c>git switch</c>: rifiuta da solo se le modifiche locali entrerebbero
    /// in conflitto, e per un nome presente solo sul remote crea il branch locale che lo traccia.
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

    // refs/heads/<nome> e refs/remotes/<remote>/<nome>: il nome e' quello che si sceglie nella
    // tendina, il remote non interessa. Il puntatore simbolico HEAD del remote non e' un branch.
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
    /// Pull in fast-forward puro. <c>--ff-only</c> non e' una precauzione cosmetica: se il
    /// fast-forward non e' possibile git rifiuta invece di creare un merge o dei conflitti.
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
    /// Branch locali rimasti senza il loro upstream sul remote. Il <c>--prune</c> fa parte della
    /// lettura: finche' i remote-tracking ref cancellati restano sul disco git non marca nessun
    /// upstream come <c>gone</c>, e l'elenco risulterebbe sempre vuoto. Il branch corrente non
    /// compare mai: git rifiuterebbe comunque di eliminarlo.
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
            return new PrunableBranches([], reason.Length > 0 ? reason : "fetch --prune fallito.");
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
            return new PrunableBranches([], reason.Length > 0 ? reason : "lettura dei branch locali fallita.");
        }

        return new PrunableBranches(ParseGoneBranches(refs.StandardOutput, current));
    }

    /// <summary>
    /// Elimina i branch locali indicati con <c>branch -D</c>. Il <c>-d</c> non e' un'alternativa:
    /// con la storia mergiata via squash o rebase rifiuterebbe proprio i branch gia' integrati,
    /// che sono il caso normale qui. La conferma dell'utente sta a monte.
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

    // Righe "<branch>\t[gone]" oppure "<branch>\t[ahead 2]": il marcatore gone dice che
    // l'upstream configurato non esiste piu'. Un branch senza upstream ha la colonna vuota e
    // non e' un candidato: non e' "sparito dal remote", non c'e' mai stato.
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

    // I worktree dello stesso clone sorgente condividono i ref: due fetch insieme darebbero
    // "cannot lock ref". Un clone normale ha i ref suoi e non aspetta nessuno.
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

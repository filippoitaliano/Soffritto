using System.Text.Json;
using Soffritto.Core.Configuration;

namespace Soffritto.Core.Docker;

/// <summary>A pool tunnel port assigned to a repository of an environment.</summary>
public sealed record PoolAssignment(string TunnelDescription, string TunnelId, int Port, int Environment, string Repository);

/// <summary>A pool tunnel as devtunnel sees it, with the differences between ports and assignments.</summary>
public sealed record PoolTunnelState(
    int Index,
    string Description,
    string? TunnelId,
    IReadOnlyList<int> Ports,
    IReadOnlyList<PoolAssignment> Assignments,
    IReadOnlyList<string> Differences,
    string? Error);

/// <summary>
/// The pool of user-created dev tunnels: ports assigned to environments on request, with
/// anonymous access per port, and released when the environment is destroyed. Assignments live in
/// &lt;data folder&gt;\tunnel-pool.json. Tunnels are added, removed and renewed from here.
/// </summary>
public static class TunnelPool
{
    /// <summary>R-a of P02: 10 ports per tunnel measured, one kept as margin.</summary>
    public const int MaxPortsPerTunnel = 9;

    /// <summary>R-b of P02: a running host does not serve ports added after it started.</summary>
    public const bool RestartHostOnAssign = true;

    /// <summary>R-c (B3): pool tunnels are "Owner only"; anonymous access is granted per port, as in local environments.</summary>
    public const bool GrantPortAccess = true;

    public const string HostService = "tunnel-pool";

    /// <summary>The <see cref="AssignAsync"/> error when no tunnel can host the environment's port.</summary>
    public const string PoolFull = "pool full: every usable tunnel already belongs to another environment; add a tunnel to the pool in setup";

    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string FilePath => Path.Combine(ConfigurationFiles.DataFolder, "tunnel-pool.json");

    // Assignments and releases also come from other processes (the app and the MCP server): Gate serializes
    // this process's ones, the lock file the cross-process ones.
    private static string LockPath => Path.Combine(ConfigurationFiles.DataFolder, "tunnel-pool.lock");

    private static readonly TimeSpan LockTimeout = TimeSpan.FromMinutes(3);

    /// <summary>Service and key of the console marker of the host of the tunnel with that Description: "tunnel-pool", "pool-1".</summary>
    public static (string Service, string Key) HostKey(string description) => (HostService, description);

    /// <summary>The assignment of the environment's repository in tunnel-pool.json; null if it is not shared.</summary>
    public static PoolAssignment? AssignmentOf(int environment, string repositoryId)
        => Read().FirstOrDefault(a => a.Environment == environment && SameId(a.Repository, repositoryId));

    /// <summary>
    /// The positions in <paramref name="states"/> of the tunnels to try the port on, in pool
    /// order. A tunnel belongs to a single environment: if the environment already has ports on a readable
    /// tunnel only its tunnels count, otherwise only those without assignments. Tunnels that are
    /// not found, in error, full or already have the port are left out.
    /// </summary>
    public static IReadOnlyList<int> Order(IReadOnlyList<PoolTunnelState> states, int environment, int port)
    {
        var readable = states
            .Select((state, position) => (State: state, Position: position))
            .Where(x => x.State.TunnelId is not null && x.State.Error is null)
            .ToList();
        var own = readable.Where(x => x.State.Assignments.Any(a => a.Environment == environment)).ToList();
        return (own.Count > 0 ? own : readable.Where(x => x.State.Assignments.Count == 0))
            .Where(x => !x.State.Ports.Contains(port) && x.State.Ports.Count < MaxPortsPerTunnel)
            .Select(x => x.Position)
            .ToList();
    }

    /// <summary>The label of a new pool tunnel, if the pool does not have one yet.</summary>
    public const string DefaultLabel = "SoffrittoPool";

    private const string DescriptionPrefix = "pool-";

    /// <summary>
    /// Creates a pool tunnel on the first "pool-K" Description that neither <paramref name="c"/> nor
    /// the account has, and adds it to config.json; <paramref name="c"/> does not change, the caller
    /// updates it. The created tunnel or the error (including an exhausted account quota).
    /// </summary>
    public static async Task<(PoolTunnelConfiguration? Tunnel, string? Error)> AddTunnelAsync(AppConfiguration c, Action<string> log, CancellationToken ct = default)
    {
        var (account, listError) = await DevTunnelProvisioner.ListTunnelsAsync(ct).ConfigureAwait(false);
        if (account is null)
        {
            return (null, listError);
        }

        var taken = account.Select(t => t.Description).Concat(c.TunnelPool.Tunnels.Select(t => t.Description)).ToHashSet(StringComparer.Ordinal);
        var k = 1;
        while (taken.Contains($"{DescriptionPrefix}{k}"))
        {
            k++;
        }

        var tunnel = new PoolTunnelConfiguration
        {
            Description = $"{DescriptionPrefix}{k}",
            Label = c.TunnelPool.Tunnels.Select(t => t.Label).FirstOrDefault(l => l.Length > 0) ?? DefaultLabel,
        };
        var (tunnelId, createError) = await DevTunnelProvisioner.CreatePoolTunnelAsync(tunnel.Description, tunnel.Label, ct).ConfigureAwait(false);
        if (tunnelId is null)
        {
            return (null, createError);
        }

        log($"pool tunnel {tunnel.Description} created: {tunnelId}");
        var written = ConfigurationFiles.Update(
            file =>
            {
                if (file.TunnelPool.Tunnels.All(t => t.Description != tunnel.Description))
                {
                    file.TunnelPool.Tunnels.Add(new PoolTunnelConfiguration { Description = tunnel.Description, Label = tunnel.Label });
                }

                return null;
            },
            ConfigurationFileFallback.Refuse);
        return written is null
            ? (tunnel, null)
            : (null, $"tunnel {tunnel.Description} created but not written to config.json ({written}): add it from setup");
    }

    /// <summary>
    /// Removes the tunnel with that Description from the pool and deletes it from the account; refuses if it has
    /// assigned ports. <paramref name="c"/> does not change, the caller updates it. Error or null.
    /// </summary>
    public static async Task<string?> RemoveTunnelAsync(AppConfiguration c, string description, Action<string> log, CancellationToken ct = default)
    {
        if (Read().Where(a => a.TunnelDescription == description).ToList() is { Count: > 0 } assigned)
        {
            return $"{description} has {assigned.Count} assigned ports: release them first";
        }

        var (account, listError) = await DevTunnelProvisioner.ListTunnelsAsync(ct).ConfigureAwait(false);
        if (account is null)
        {
            return listError;
        }

        if (account.FirstOrDefault(t => t.Description == description) is { Id.Length: > 0 } found)
        {
            if (await DevTunnelProvisioner.DeleteTunnelAsync(found.Id, ct).ConfigureAwait(false) is { } deleteError)
            {
                return deleteError;
            }

            log($"tunnel {description} ({found.Id}) deleted from the account");
        }
        else
        {
            log($"tunnel {description} not found in the account: removed from the pool only");
        }

        return ConfigurationFiles.Update(
            file =>
            {
                file.TunnelPool.Tunnels.RemoveAll(t => t.Description == description);
                return null;
            },
            ConfigurationFileFallback.Refuse);
    }

    /// <summary>
    /// Resets to the maximum the expiration of the pool tunnels the account has: a tunnel expires after
    /// 30 days without use and the service accepts no longer expiration.
    /// </summary>
    public static async Task RenewAsync(AppConfiguration c, Action<string> log, CancellationToken ct = default)
    {
        if (c.TunnelPool.Tunnels.Count == 0)
        {
            return;
        }

        var (account, listError) = await DevTunnelProvisioner.ListTunnelsAsync(ct).ConfigureAwait(false);
        if (account is null)
        {
            log($"pool tunnel expiration not renewed: {listError}");
            return;
        }

        var renewed = 0;
        foreach (var tunnel in account.Where(t => c.TunnelPool.Tunnels.Any(p => p.Description == t.Description)))
        {
            if (await DevTunnelProvisioner.RenewAsync(tunnel.Id, ct).ConfigureAwait(false) is { } error)
            {
                log($"expiration of {tunnel.Description} not renewed: {error}");
            }
            else
            {
                renewed++;
            }
        }

        log($"expiration renewed to {DevTunnelProvisioner.MaxExpiration} for {renewed} pool tunnels");
    }

    /// <summary>
    /// True if the assignment no longer has its port: the tunnel with that Description is missing
    /// from the account, has another id or lacks the port. False if the tunnel's state could not be
    /// read (devtunnel list or show error): a failed read never discards live assignments.
    /// </summary>
    public static bool IsOrphan(PoolAssignment a, IReadOnlyList<PoolTunnelState> states)
    {
        var state = StateOf(a, states);
        if (state is null || ReadError(state) is not null)
        {
            return false;
        }

        return state.TunnelId is null || state.TunnelId != a.TunnelId || !state.Ports.Contains(a.Port);
    }

    /// <summary>
    /// Polls <paramref name="urlOf"/> (default <see cref="UrlOfAsync"/>) every 2 s up to
    /// <paramref name="timeout"/> (default 30 s): the URL of the port as soon as it is published, or null.
    /// </summary>
    public static async Task<string?> WaitUrlAsync(
        PoolAssignment a,
        CancellationToken ct = default,
        Func<PoolAssignment, CancellationToken, Task<string?>>? urlOf = null,
        TimeSpan? timeout = null)
    {
        urlOf ??= UrlOfAsync;
        var limit = timeout ?? TimeSpan.FromSeconds(30);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            if (await urlOf(a, ct).ConfigureAwait(false) is { Length: > 0 } url)
            {
                return url;
            }

            if (elapsed.Elapsed >= limit)
            {
                return null;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }
    }

    public static IReadOnlyList<PoolAssignment> Read()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<PoolFile>(File.ReadAllText(FilePath), Json)?.Assignments ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    public static void Write(IReadOnlyList<PoolAssignment> assignments)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new PoolFile { Assignments = assignments.ToList() }, Json));
        File.Move(temporary, FilePath, overwrite: true);
    }

    /// <summary>
    /// The pool port for the environment's repository: the one already assigned if it is still on
    /// its tunnel, or a new one on the tunnel chosen by <see cref="Order"/>, with the local port's
    /// protocol and anonymous access on the port. An orphan assignment is discarded; with
    /// a read error on its tunnel the file does not change and the outcome is that error.
    /// <paramref name="restartHost"/> receives the tunnel index and restarts its host if it is running
    /// (the Desktop does it, since it owns the consoles): true if it restarted it.
    /// </summary>
    public static async Task<(PoolAssignment? Assignment, string? Error)> AssignAsync(
        AppConfiguration c,
        EnvironmentRepository entry,
        Action<string> log,
        CancellationToken ct = default,
        Func<int, bool>? restartHost = null)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        if (await LockAsync(ct).ConfigureAwait(false) is not { } fileLock)
        {
            Gate.Release();
            return (null, $"{FilePath} is busy in another process: try again");
        }

        try
        {
            var assignments = Read().ToList();
            var number = entry.Environment.Number;
            if (DevTunnelReader.ResolveExecutable() is not { } exe)
            {
                return (null, "devtunnel.exe not found");
            }

            var states = await ReadStatesAsync(c, assignments, exe, ct).ConfigureAwait(false);
            foreach (var state in states)
            {
                foreach (var line in state.Differences.Append(state.Error).OfType<string>())
                {
                    log(line);
                }
            }

            if (assignments.FirstOrDefault(a => a.Environment == number && SameId(a.Repository, entry.Id)) is { } existing)
            {
                if (StateOf(existing, states) is { } existingState && ReadError(existingState) is { } readError)
                {
                    return (null, readError);
                }

                if (!IsOrphan(existing, states))
                {
                    return (existing, null);
                }

                assignments.Remove(existing);
                Write(assignments);
                log($"orphan assignment of port {existing.Port} on {existing.TunnelDescription} discarded");
            }

            if (DevTunnelProvisioner.PortsFor(entry, c).FirstOrDefault() is not { } port)
            {
                return (null, $"{entry.Id} of environment {number} has no port to expose");
            }

            foreach (var state in Order(states, number, port.Number).Select(i => states[i]))
            {
                var id = state.TunnelId!;
                var created = await DevTunnelReader.DevTunnelAsync(exe, $"port create {id} -p {port.Number} --protocol {port.Protocol}", Temp, ct).ConfigureAwait(false);
                if (!created.Success)
                {
                    // Past the maximum the CLI refuses the port with "Rate limit exceeded" (P02): it is a
                    // full tunnel, not a rate limit, so we move on to the next tunnel.
                    log($"port {port.Number} not created on {state.Description}: {created.Tail(2)}");
                    continue;
                }

                if (GrantPortAccess)
                {
                    var access = await DevTunnelReader.DevTunnelAsync(exe, $"access create {id} -p {port.Number} --anonymous", Temp, ct).ConfigureAwait(false);
                    if (!access.Success)
                    {
                        log($"anonymous access for port {port.Number} on {state.Description} not granted: {access.Tail(2)}");
                        var delete = await DevTunnelReader.DevTunnelAsync(exe, $"port delete {id} -p {port.Number}", Temp, ct).ConfigureAwait(false);
                        if (!delete.Success)
                        {
                            log($"port {port.Number} on {state.Description} not deleted: {delete.Tail(2)}");
                        }

                        return (null, $"port {port.Number} on {state.Description} has no anonymous access: assignment cancelled");
                    }
                }

                var assignment = new PoolAssignment(state.Description, id, port.Number, number, entry.Id);
                assignments.Add(assignment);
                Write(assignments);
                log($"port {port.Number} of tunnel {state.Description} assigned to {entry.Id} of environment {number}");
                if (RestartHostOnAssign && restartHost?.Invoke(state.Index) == true)
                {
                    log($"host of tunnel {state.Index + 1} restarted: the other environments on the tunnel reconnect");
                }

                return (assignment, null);
            }

            return (null, PoolFull);
        }
        finally
        {
            fileLock.Dispose();
            Gate.Release();
        }
    }

    /// <summary>
    /// Releases the environment's ports, or only the one of repository <paramref name="repositoryId"/>:
    /// anonymous access removed, port deleted, assignment removed. An orphan assignment leaves
    /// the file without commands; with a read error on its tunnel it stays. Error or null.
    /// <paramref name="states"/>: the tunnel states already read; null = read with devtunnel.
    /// </summary>
    public static async Task<string?> ReleaseAsync(
        AppConfiguration c,
        int number,
        Action<string> log,
        CancellationToken ct = default,
        string? repositoryId = null,
        IReadOnlyList<PoolTunnelState>? states = null)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        if (await LockAsync(ct).ConfigureAwait(false) is not { } fileLock)
        {
            Gate.Release();
            return $"{FilePath} is busy in another process: try again";
        }

        try
        {
            var assignments = Read().ToList();
            var mine =assignments.Where(a => a.Environment == number && (repositoryId is null || SameId(a.Repository, repositoryId))).ToList();
            if (mine.Count == 0)
            {
                return null;
            }

            var exe = DevTunnelReader.ResolveExecutable();
            if (states is null)
            {
                if (exe is null)
                {
                    return "devtunnel.exe not found";
                }

                states = await ReadStatesAsync(c, assignments, exe, ct).ConfigureAwait(false);
            }

            string? error = null;
            var changed = false;
            foreach (var assignment in mine)
            {
                if (StateOf(assignment, states) is { } state && ReadError(state) is { } readError)
                {
                    error = readError;
                    log($"port {assignment.Port} on {assignment.TunnelDescription} not released: {readError}");
                    continue;
                }

                if (IsOrphan(assignment, states))
                {
                    assignments.Remove(assignment);
                    changed = true;
                    log($"port {assignment.Port} on {assignment.TunnelDescription} already released");
                    continue;
                }

                if (exe is null)
                {
                    error = "devtunnel.exe not found";
                    continue;
                }

                if (GrantPortAccess)
                {
                    var reset = await DevTunnelReader.DevTunnelAsync(exe, $"access reset {assignment.TunnelId} -p {assignment.Port}", Temp, ct).ConfigureAwait(false);
                    if (!reset.Success)
                    {
                        log($"access for port {assignment.Port} on {assignment.TunnelDescription} not removed: {reset.Tail(2)}");
                    }
                }

                var delete = await DevTunnelReader.DevTunnelAsync(exe, $"port delete {assignment.TunnelId} -p {assignment.Port}", Temp, ct).ConfigureAwait(false);
                if (delete.Success)
                {
                    assignments.Remove(assignment);
                    changed = true;
                    log($"port {assignment.Port} of tunnel {assignment.TunnelDescription} released");
                }
                else
                {
                    error = $"port {assignment.Port} on {assignment.TunnelDescription} not deleted: {delete.Tail(2)}";
                    log(error);
                }
            }

            if (changed)
            {
                Write(assignments);
            }

            return error;
        }
        finally
        {
            fileLock.Dispose();
            Gate.Release();
        }
    }

    // Null if the cross-process lock is not acquired within LockTimeout.
    private static async Task<CrossProcessLock?> LockAsync(CancellationToken ct)
    {
        try
        {
            return await CrossProcessLock.AcquireAsync(LockPath, LockTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    /// <summary>The port's public URL (portUri from devtunnel show); null until a host has served it.</summary>
    public static async Task<string?> UrlOfAsync(PoolAssignment assignment, CancellationToken ct = default)
    {
        if (DevTunnelReader.ResolveExecutable() is not { } exe)
        {
            return null;
        }

        var (ports, _) = await ShowAsync(exe, assignment.TunnelId, ct).ConfigureAwait(false);
        return ports?.FirstOrDefault(p => p.Number == assignment.Port).Uri is { Length: > 0 } uri ? uri : null;
    }

    /// <summary>The state of every pool tunnel, with the differences between ports and assignments (F-14).</summary>
    public static async Task<IReadOnlyList<PoolTunnelState>> StatusAsync(AppConfiguration c, CancellationToken ct = default)
    {
        if (DevTunnelReader.ResolveExecutable() is not { } exe)
        {
            return c.TunnelPool.Tunnels.Select((t, i) => new PoolTunnelState(i, t.Description, null, [], [], [], "devtunnel.exe not found")).ToList();
        }

        return await ReadStatesAsync(c, Read(), exe, ct).ConfigureAwait(false);
    }

    private static string Temp => Path.GetTempPath();

    private static async Task<IReadOnlyList<PoolTunnelState>> ReadStatesAsync(AppConfiguration c, IReadOnlyList<PoolAssignment> assignments, string exe, CancellationToken ct)
    {
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        var list = await DevTunnelReader.DevTunnelAsync(exe, "list --json", Temp, ct).ConfigureAwait(false);
        string? listError = list.Success ? null : $"devtunnel list: {list.Tail(2)}";
        using (var doc = list.Success ? DevTunnelReader.ParseJson(list.StandardOutput) : null)
        {
            if (doc is not null && doc.RootElement.TryGetProperty("tunnels", out var tunnels) && tunnels.ValueKind == JsonValueKind.Array)
            {
                foreach (var tunnel in tunnels.EnumerateArray())
                {
                    ids.TryAdd(DevTunnelReader.GetString(tunnel, "description"), DevTunnelReader.GetString(tunnel, "tunnelId"));
                }
            }
            else if (list.Success)
            {
                // An unreadable list is not an account without tunnels: assignments do not become orphans.
                listError = "devtunnel list: unrecognized output";
            }
        }

        var states = new List<PoolTunnelState>();
        for (var i = 0; i < c.TunnelPool.Tunnels.Count; i++)
        {
            var description = c.TunnelPool.Tunnels[i].Description;
            var mine = assignments.Where(a => a.TunnelDescription == description).ToList();
            var gone = Gone(c, mine);
            if (listError is not null)
            {
                states.Add(new PoolTunnelState(i, description, null, [], mine, gone, listError));
                continue;
            }

            if (!ids.TryGetValue(description, out var id))
            {
                states.Add(new PoolTunnelState(i, description, null, [], mine, gone, MissingError(description)));
                continue;
            }

            var (ports, error) = await ShowAsync(exe, id, ct).ConfigureAwait(false);
            if (ports is null)
            {
                states.Add(new PoolTunnelState(i, description, id, [], mine, gone, error));
                continue;
            }

            var numbers = ports.Select(p => p.Number).ToList();
            var differences = numbers.Where(n => mine.All(a => a.Port != n)).Select(n => $"port {n} on {description} without assignment")
                .Concat(mine.Where(a => !numbers.Contains(a.Port)).Select(a => $"assignment of port {a.Port} (environment {a.Environment}, {a.Repository}) without a port on {description}"))
                .Concat(gone)
                .ToList();
            states.Add(new PoolTunnelState(i, description, id, numbers, mine, differences, null));
        }

        // Assignments on tunnels removed from the pool in setup: states appended at the end, in error (Order
        // skips them) but visible and releasable; ports read if the tunnel is still in the account.
        var outside = assignments
            .Where(a => !c.TunnelPool.Tunnels.Any(t => t.Description == a.TunnelDescription))
            .GroupBy(a => a.TunnelDescription, StringComparer.Ordinal)
            .ToList();
        for (var j = 0; j < outside.Count; j++)
        {
            var description = outside[j].Key;
            var mine = outside[j].ToList();
            var index = c.TunnelPool.Tunnels.Count + j;
            if (listError is not null)
            {
                states.Add(new PoolTunnelState(index, description, null, [], mine, Gone(c, mine), listError));
                continue;
            }

            if (!ids.TryGetValue(description, out var id))
            {
                states.Add(new PoolTunnelState(index, description, null, [], mine, Gone(c, mine), OutOfPoolError(description)));
                continue;
            }

            var (ports, error) = await ShowAsync(exe, id, ct).ConfigureAwait(false);
            states.Add(new PoolTunnelState(index, description, id, ports?.Select(p => p.Number).ToList() ?? [], mine, Gone(c, mine), ports is null ? error : OutOfPoolError(description)));
        }

        return states;
    }

    private static List<string> Gone(AppConfiguration c, IEnumerable<PoolAssignment> assignments)
        => assignments
            .Where(a => c.Environment(a.Environment) is not { } e || !e.Repositories.Contains(a.Repository, StringComparer.OrdinalIgnoreCase))
            .Select(a => $"assignment of port {a.Port} (environment {a.Environment}, {a.Repository}) of an environment that no longer exists")
            .ToList();

    private static string MissingError(string description) => $"no tunnel with Description {description}: create it or remove it from the pool";

    private static string OutOfPoolError(string description) => $"tunnel {description} no longer in the pool";

    private static PoolTunnelState? StateOf(PoolAssignment a, IReadOnlyList<PoolTunnelState> states)
        => states.FirstOrDefault(s => s.Description == a.TunnelDescription);

    // The tunnel's read error (devtunnel list or show failed), which makes it impossible to tell whether
    // an assignment is an orphan. A tunnel missing from the account or outside the pool counts as read.
    private static string? ReadError(PoolTunnelState state)
        => state.Error is { } error && error != MissingError(state.Description) && error != OutOfPoolError(state.Description) ? error : null;

    private static async Task<(IReadOnlyList<(int Number, string Uri)>? Ports, string? Error)> ShowAsync(string exe, string id, CancellationToken ct)
    {
        var show = await DevTunnelReader.DevTunnelAsync(exe, $"show {id} --json", Temp, ct).ConfigureAwait(false);
        using var doc = show.Success ? DevTunnelReader.ParseJson(show.StandardOutput) : null;
        if (doc is null || !doc.RootElement.TryGetProperty("tunnel", out var tunnel))
        {
            return (null, $"devtunnel show {id}: {show.Tail(2)}");
        }

        var ports = tunnel.TryGetProperty("ports", out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray()
                .Select(p => (Number: p.TryGetProperty("portNumber", out var n) && n.ValueKind == JsonValueKind.Number ? n.GetInt32() : 0, Uri: DevTunnelReader.GetString(p, "portUri")))
                .Where(p => p.Number > 0)
                .ToList()
            : [];
        return (ports, null);
    }

    private static bool SameId(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private sealed class PoolFile
    {
        public List<PoolAssignment> Assignments { get; set; } = [];
    }
}

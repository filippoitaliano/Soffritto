using System.Text.Json;
using Soffritto.Core.Configuration;

namespace Soffritto.Core.Docker;

/// <summary>Una porta di un tunnel del pool assegnata a un repository di un ambiente.</summary>
public sealed record PoolAssignment(string TunnelDescription, string TunnelId, int Port, int Environment, string Repository);

/// <summary>Un tunnel del pool come lo vede devtunnel, con le differenze tra porte e assegnazioni.</summary>
public sealed record PoolTunnelState(
    int Index,
    string Description,
    string? TunnelId,
    IReadOnlyList<int> Ports,
    IReadOnlyList<PoolAssignment> Assignments,
    IReadOnlyList<string> Differences,
    string? Error);

/// <summary>
/// Il pool di dev tunnel creati dall'utente: porte assegnate su richiesta agli ambienti, con
/// accesso anonimo per porta, e rilasciate alla distruzione. Le assegnazioni stanno in
/// &lt;cartella dati&gt;\tunnel-pool.json. Un tunnel si aggiunge, si toglie e si rinnova da qui.
/// </summary>
public static class TunnelPool
{
    /// <summary>R-a di P02: 10 porte per tunnel misurate, una di margine.</summary>
    public const int MaxPortsPerTunnel = 9;

    /// <summary>R-b di P02: un host acceso non ospita le porte aggiunte dopo il suo avvio.</summary>
    public const bool RestartHostOnAssign = true;

    /// <summary>R-c (B3): i tunnel del pool sono «Owner only»; l'accesso anonimo si da' per porta, come gli ambienti locali.</summary>
    public const bool GrantPortAccess = true;

    public const string HostService = "tunnel-pool";

    /// <summary>L'errore di <see cref="AssignAsync"/> quando nessun tunnel puo' ospitare la porta dell'ambiente.</summary>
    public const string PoolFull = "pool pieno: ogni tunnel utilizzabile e' gia' di un altro ambiente; aggiungi un tunnel al pool nel setup";

    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string FilePath => Path.Combine(ConfigurationFiles.DataFolder, "tunnel-pool.json");

    // Assegnazioni e rilasci arrivano anche da altri processi (exe e server MCP): Gate serializza
    // quelli di questo processo, il file di lock quelli fra processi.
    private static string LockPath => Path.Combine(ConfigurationFiles.DataFolder, "tunnel-pool.lock");

    private static readonly TimeSpan LockTimeout = TimeSpan.FromMinutes(3);

    /// <summary>Servizio e chiave del marcatore della console dell'host del tunnel con quella Description: "tunnel-pool", "pool-1".</summary>
    public static (string Service, string Key) HostKey(string description) => (HostService, description);

    /// <summary>L'assegnazione del repository dell'ambiente in tunnel-pool.json; null se non e' condiviso.</summary>
    public static PoolAssignment? AssignmentOf(int environment, string repositoryId)
        => Read().FirstOrDefault(a => a.Environment == environment && SameId(a.Repository, repositoryId));

    /// <summary>
    /// Le posizioni in <paramref name="states"/> dei tunnel su cui provare la porta, nell'ordine
    /// del pool. Un tunnel e' di un solo ambiente: se l'ambiente ha gia' porte su un tunnel
    /// leggibile valgono solo i suoi tunnel, altrimenti solo quelli senza assegnazioni. Fuori i
    /// tunnel non trovati, in errore, pieni o che hanno gia' la porta.
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

    /// <summary>La label di un tunnel del pool nuovo, se il pool non ne ha gia' una.</summary>
    public const string DefaultLabel = "SoffrittoPool";

    private const string DescriptionPrefix = "pool-";

    /// <summary>
    /// Crea un tunnel del pool sulla prima Description «pool-K» che ne' <paramref name="c"/> ne'
    /// l'account hanno, e lo aggiunge a config.json; <paramref name="c"/> non cambia, lo aggiorna
    /// chi chiama. Il tunnel creato o l'errore (anche la quota dell'account esaurita).
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

        log($"tunnel del pool {tunnel.Description} creato: {tunnelId}");
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
            : (null, $"tunnel {tunnel.Description} creato ma non scritto in config.json ({written}): aggiungilo dal setup");
    }

    /// <summary>
    /// Toglie dal pool il tunnel con quella Description e lo cancella dall'account; rifiuta se ha
    /// porte assegnate. <paramref name="c"/> non cambia, lo aggiorna chi chiama. Errore o null.
    /// </summary>
    public static async Task<string?> RemoveTunnelAsync(AppConfiguration c, string description, Action<string> log, CancellationToken ct = default)
    {
        if (Read().Where(a => a.TunnelDescription == description).ToList() is { Count: > 0 } assigned)
        {
            return $"{description} ha {assigned.Count} porte assegnate: rilasciale prima";
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

            log($"tunnel {description} ({found.Id}) cancellato dall'account");
        }
        else
        {
            log($"tunnel {description} non trovato nell'account: tolto solo dal pool");
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
    /// Riporta al massimo la scadenza dei tunnel del pool che l'account ha: un tunnel scade dopo
    /// 30 giorni senza uso e il servizio non ne accetta di piu' lunghe.
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
            log($"scadenza dei tunnel del pool non rinnovata: {listError}");
            return;
        }

        var renewed = 0;
        foreach (var tunnel in account.Where(t => c.TunnelPool.Tunnels.Any(p => p.Description == t.Description)))
        {
            if (await DevTunnelProvisioner.RenewAsync(tunnel.Id, ct).ConfigureAwait(false) is { } error)
            {
                log($"scadenza di {tunnel.Description} non rinnovata: {error}");
            }
            else
            {
                renewed++;
            }
        }

        log($"scadenza rinnovata a {DevTunnelProvisioner.MaxExpiration} per {renewed} tunnel del pool");
    }

    /// <summary>
    /// Vero se l'assegnazione non ha piu' la sua porta: il tunnel con quella Description manca
    /// dall'account, ha un altro id o non ha la porta. Falso se lo stato del tunnel non e' stato
    /// letto (errore di devtunnel list o show): una lettura fallita non scarta assegnazioni vive.
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
    /// Interroga <paramref name="urlOf"/> (default <see cref="UrlOfAsync"/>) ogni 2 s fino a
    /// <paramref name="timeout"/> (default 30 s): l'URL della porta appena pubblicato, o null.
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
    /// La porta del pool per il repository dell'ambiente: quella gia' assegnata se e' ancora sul
    /// suo tunnel, oppure una nuova sul tunnel scelto da <see cref="Order"/>, con il protocollo
    /// della porta locale e l'accesso anonimo della porta. Un'assegnazione orfana si scarta; con
    /// un errore di lettura del suo tunnel il file non cambia e l'esito e' quell'errore.
    /// <paramref name="restartHost"/> riceve l'indice del tunnel e riavvia il suo host se e' acceso
    /// (lo fa il Desktop, che possiede le console): vero se l'ha riavviato.
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
            return (null, $"{FilePath} e' occupato da un altro processo: riprova");
        }

        try
        {
            var assignments = Read().ToList();
            var number = entry.Environment.Number;
            if (DevTunnelReader.ResolveExecutable() is not { } exe)
            {
                return (null, "devtunnel.exe non trovato");
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
                log($"assegnazione orfana della porta {existing.Port} su {existing.TunnelDescription} scartata");
            }

            if (DevTunnelProvisioner.PortsFor(entry, c).FirstOrDefault() is not { } port)
            {
                return (null, $"{entry.Id} dell'ambiente {number} non ha una porta da esporre");
            }

            foreach (var state in Order(states, number, port.Number).Select(i => states[i]))
            {
                var id = state.TunnelId!;
                var created = await DevTunnelReader.DevTunnelAsync(exe, $"port create {id} -p {port.Number} --protocol {port.Protocol}", Temp, ct).ConfigureAwait(false);
                if (!created.Success)
                {
                    // La CLI rifiuta la porta oltre il massimo con «Rate limit exceeded» (P02): e' un
                    // tunnel pieno, non un limite di frequenza, quindi si passa al tunnel dopo.
                    log($"porta {port.Number} non creata su {state.Description}: {created.Tail(2)}");
                    continue;
                }

                if (GrantPortAccess)
                {
                    var access = await DevTunnelReader.DevTunnelAsync(exe, $"access create {id} -p {port.Number} --anonymous", Temp, ct).ConfigureAwait(false);
                    if (!access.Success)
                    {
                        log($"accesso anonimo della porta {port.Number} su {state.Description} non dato: {access.Tail(2)}");
                        var delete = await DevTunnelReader.DevTunnelAsync(exe, $"port delete {id} -p {port.Number}", Temp, ct).ConfigureAwait(false);
                        if (!delete.Success)
                        {
                            log($"porta {port.Number} su {state.Description} non cancellata: {delete.Tail(2)}");
                        }

                        return (null, $"porta {port.Number} su {state.Description} senza accesso anonimo: assegnazione annullata");
                    }
                }

                var assignment = new PoolAssignment(state.Description, id, port.Number, number, entry.Id);
                assignments.Add(assignment);
                Write(assignments);
                log($"porta {port.Number} del tunnel {state.Description} assegnata a {entry.Id} dell'ambiente {number}");
                if (RestartHostOnAssign && restartHost?.Invoke(state.Index) == true)
                {
                    log($"host del tunnel {state.Index + 1} riavviato: gli altri ambienti sul tunnel si ricollegano");
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
    /// Rilascia le porte dell'ambiente, o la sola del repository <paramref name="repositoryId"/>:
    /// accesso anonimo tolto, porta cancellata, assegnazione rimossa. Un'assegnazione orfana esce
    /// dal file senza comandi; con un errore di lettura del suo tunnel resta. Errore o null.
    /// <paramref name="states"/>: gli stati dei tunnel gia' letti; null = lettura con devtunnel.
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
            return $"{FilePath} e' occupato da un altro processo: riprova";
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
                    return "devtunnel.exe non trovato";
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
                    log($"porta {assignment.Port} su {assignment.TunnelDescription} non rilasciata: {readError}");
                    continue;
                }

                if (IsOrphan(assignment, states))
                {
                    assignments.Remove(assignment);
                    changed = true;
                    log($"porta {assignment.Port} su {assignment.TunnelDescription} gia' rilasciata");
                    continue;
                }

                if (exe is null)
                {
                    error = "devtunnel.exe non trovato";
                    continue;
                }

                if (GrantPortAccess)
                {
                    var reset = await DevTunnelReader.DevTunnelAsync(exe, $"access reset {assignment.TunnelId} -p {assignment.Port}", Temp, ct).ConfigureAwait(false);
                    if (!reset.Success)
                    {
                        log($"accesso della porta {assignment.Port} su {assignment.TunnelDescription} non tolto: {reset.Tail(2)}");
                    }
                }

                var delete = await DevTunnelReader.DevTunnelAsync(exe, $"port delete {assignment.TunnelId} -p {assignment.Port}", Temp, ct).ConfigureAwait(false);
                if (delete.Success)
                {
                    assignments.Remove(assignment);
                    changed = true;
                    log($"porta {assignment.Port} del tunnel {assignment.TunnelDescription} rilasciata");
                }
                else
                {
                    error = $"porta {assignment.Port} su {assignment.TunnelDescription} non cancellata: {delete.Tail(2)}";
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

    // Null se il lock fra processi non arriva entro LockTimeout.
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

    /// <summary>L'URL pubblico della porta (portUri di devtunnel show); null finche' un host non l'ha ospitata.</summary>
    public static async Task<string?> UrlOfAsync(PoolAssignment assignment, CancellationToken ct = default)
    {
        if (DevTunnelReader.ResolveExecutable() is not { } exe)
        {
            return null;
        }

        var (ports, _) = await ShowAsync(exe, assignment.TunnelId, ct).ConfigureAwait(false);
        return ports?.FirstOrDefault(p => p.Number == assignment.Port).Uri is { Length: > 0 } uri ? uri : null;
    }

    /// <summary>Lo stato di ogni tunnel del pool, con le differenze tra porte e assegnazioni (F-14).</summary>
    public static async Task<IReadOnlyList<PoolTunnelState>> StatusAsync(AppConfiguration c, CancellationToken ct = default)
    {
        if (DevTunnelReader.ResolveExecutable() is not { } exe)
        {
            return c.TunnelPool.Tunnels.Select((t, i) => new PoolTunnelState(i, t.Description, null, [], [], [], "devtunnel.exe non trovato")).ToList();
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
                // Un elenco illeggibile non e' un account senza tunnel: le assegnazioni non diventano orfane.
                listError = "devtunnel list: output non riconosciuto";
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
            var differences = numbers.Where(n => mine.All(a => a.Port != n)).Select(n => $"porta {n} su {description} senza assegnazione")
                .Concat(mine.Where(a => !numbers.Contains(a.Port)).Select(a => $"assegnazione della porta {a.Port} (ambiente {a.Environment}, {a.Repository}) senza porta su {description}"))
                .Concat(gone)
                .ToList();
            states.Add(new PoolTunnelState(i, description, id, numbers, mine, differences, null));
        }

        // Assegnazioni su tunnel tolti dal pool nel setup: stati in coda, in errore (Order li
        // salta) ma visibili e rilasciabili; porte lette se il tunnel e' ancora nell'account.
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
            .Select(a => $"assegnazione della porta {a.Port} (ambiente {a.Environment}, {a.Repository}) di un ambiente che non c'e' piu'")
            .ToList();

    private static string MissingError(string description) => $"nessun tunnel con Description {description}: crealo o toglilo dal pool";

    private static string OutOfPoolError(string description) => $"tunnel {description} non piu' nel pool";

    private static PoolTunnelState? StateOf(PoolAssignment a, IReadOnlyList<PoolTunnelState> states)
        => states.FirstOrDefault(s => s.Description == a.TunnelDescription);

    // L'errore di lettura del tunnel (devtunnel list o show non riusciti), che impedisce di dire se
    // un'assegnazione e' orfana. Un tunnel che manca dall'account o che e' fuori dal pool e' letto.
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

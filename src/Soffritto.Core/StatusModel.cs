namespace Soffritto.Core;

public enum MigrationState
{
    Applied,

    Pending,

    /// <summary>
    /// Non applicata, ma con timestamp precedente all'ultima applicata: tipico rientro
    /// da un branch parallelo. EF la applicherebbe comunque, ma lo schema risultante
    /// puo' differire da quello prodotto applicandole in ordine.
    /// </summary>
    PendingOutOfOrder,

    /// <summary>
    /// Presente in __EFMigrationsHistory ma assente dai file di tutti i context noti.
    /// </summary>
    Unknown,
}

public sealed record MigrationEntry(
    string MigrationId,
    MigrationState State,
    string? ProductVersion = null,
    string? MismatchedFileName = null)
{
    public string DisplayName => MigrationId.Length > 15 && MigrationId[14] == '_'
        ? MigrationId[15..]
        : MigrationId;

    public string? Timestamp => MigrationId.Length >= 14 && MigrationId[..14].All(char.IsDigit)
        ? MigrationId[..14]
        : null;
}

public sealed record ContextStatus(
    string ContextName,
    string MigrationsDirectory,
    IReadOnlyList<MigrationEntry> Entries)
{
    public int AppliedCount => Entries.Count(e => e.State == MigrationState.Applied);

    public IReadOnlyList<MigrationEntry> Pending => Entries
        .Where(e => e.State is MigrationState.Pending or MigrationState.PendingOutOfOrder)
        .ToList();

    public IReadOnlyList<MigrationEntry> OutOfOrder => Entries
        .Where(e => e.State == MigrationState.PendingOutOfOrder)
        .ToList();

    public string? LastApplied => Entries
        .Where(e => e.State == MigrationState.Applied)
        .Select(e => e.MigrationId)
        .LastOrDefault();

    public bool IsUpToDate => Pending.Count == 0;

    public IReadOnlyList<MigrationEntry> FileNameMismatches => Entries
        .Where(e => e.MismatchedFileName is not null)
        .ToList();

    /// <summary>
    /// Caso "in avanti e senza sorprese": ci sono migrazioni da applicare e sono TUTTE successive
    /// all'ultima applicata. Con anche una sola fuori ordine l'aggiornamento automatico non e'
    /// piu' banale (il buco viene da un branch parallelo e lo schema risultante puo' divergere),
    /// quindi la decisione torna a te e il bottone non compare.
    /// Le migrazioni sconosciute NON bloccano: sono righe inerti sul database, non impediscono a
    /// EF di applicare correttamente le pendenti.
    /// </summary>
    public bool CanApplyPending => Pending.Count > 0 && OutOfOrder.Count == 0;

    /// <summary>
    /// Cosa fa <c>dotnet ef database update</c> fermandosi a <paramref name="targetMigrationId"/>:
    /// come EF, annulla le applicate successive al target (dalla piu' recente) e applica le non
    /// applicate fino al target compreso, fuori ordine incluse. Null se il target non e' tra le
    /// migrazioni del context.
    /// </summary>
    public UpdatePlan? PlanUpdateTo(string targetMigrationId)
    {
        var target = Entries.ToList().FindIndex(e => e.MigrationId == targetMigrationId);
        if (target < 0)
        {
            return null;
        }

        var toRevert = Entries
            .Skip(target + 1)
            .Where(e => e.State == MigrationState.Applied)
            .Reverse()
            .ToList();

        var toApply = Entries
            .Take(target + 1)
            .Where(e => e.State is MigrationState.Pending or MigrationState.PendingOutOfOrder)
            .ToList();

        return new UpdatePlan(Entries[target], toApply, toRevert);
    }
}

/// <param name="ToRevert">Dalla piu' recente: e' l'ordine in cui EF esegue i Down.</param>
public sealed record UpdatePlan(
    MigrationEntry Target,
    IReadOnlyList<MigrationEntry> ToApply,
    IReadOnlyList<MigrationEntry> ToRevert)
{
    public bool IsRollback => ToRevert.Count > 0;

    public bool IsNoOp => ToApply.Count == 0 && ToRevert.Count == 0;
}

public sealed record GitStatus(
    string? Branch = null,
    int Ahead = 0,
    int Behind = 0,
    int DirtyFiles = 0,
    bool HasUpstream = false,
    bool IsDetached = false,
    string? FetchError = null,
    string? Error = null,
    IReadOnlyList<string>? Branches = null)
{
    public bool HasPendingPull => Behind > 0;

    /// <summary>
    /// Nomi di branch selezionabili: locali piu' quelli presenti solo sul remote, senza il
    /// prefisso del remote, ordinati e senza duplicati. Vuoto se git non e' disponibile.
    /// </summary>
    public IReadOnlyList<string> AllBranches => Branches ?? [];

    public bool IsClean => DirtyFiles == 0;

    /// <summary>
    /// Pull banale: ci sono commit da prendere, nessuno da mandare, working tree pulito e un
    /// upstream configurato. Con <c>Ahead > 0</c> il pull sarebbe un merge o un rebase, non un
    /// fast-forward, quindi non e' piu' l'operazione "standard senza conflitti" e il bottone
    /// non compare.
    /// </summary>
    public bool CanFastForward =>
        Error is null
        && HasUpstream
        && !IsDetached
        && Behind > 0
        && Ahead == 0
        && IsClean;
}

/// <summary>
/// Branch locali il cui upstream e' sparito dal remote, come li vede git dopo una potatura
/// dei remote-tracking ref.
/// </summary>
public sealed record PrunableBranches(IReadOnlyList<string> Branches, string? Error = null);

public sealed record DevTunnelPort(int PortNumber, string Protocol, string PortUri)
{
    /// <summary>Esito del probe HTTP; null se il probe non e' stato eseguito (host non connesso).</summary>
    public bool? Reachable { get; init; }
}

/// <summary>Riga di 'devtunnel list': quanto basta per riconoscere un tunnel e capire se e' vivo.</summary>
public sealed record DevTunnelSummary(string TunnelId, string Description, int HostConnections, int PortCount)
{
    public bool IsHostConnected => HostConnections > 0;
}

public sealed record DevTunnelStatus(
    string? TunnelId = null,
    string? Description = null,
    int HostConnections = 0,
    IReadOnlyList<DevTunnelPort>? Ports = null,
    string? Error = null,
    IReadOnlyList<DevTunnelSummary>? Available = null)
{
    public IReadOnlyList<DevTunnelPort> AllPorts => Ports ?? [];

    /// <summary>
    /// I tunnel dell'account quando nessuno corrisponde al clone: valorizzato solo insieme a
    /// <see cref="Error"/>, perche' a corrispondenza trovata il dettaglio del tunnel basta.
    /// </summary>
    public IReadOnlyList<DevTunnelSummary> AllAvailable => Available ?? [];

    public bool Shared { get; init; } = true;

    /// <summary>Un repository senza porta del pool: nessuna lettura della CLI.</summary>
    public static DevTunnelStatus NotShared(string message) => new(Error: message) { Shared = false };

    /// <summary>
    /// La raggiungibilita' primaria e' questa, non un probe HTTP: la gateway devtunnels tiene
    /// appesa la richiesta verso un tunnel non hostato invece di rifiutarla, quindi senza host
    /// connesso il probe puo' solo scadere in timeout.
    /// </summary>
    public bool IsHostConnected => Error is null && HostConnections > 0;
}

public sealed record StatusReport(
    string SolutionRoot,
    string? Server,
    string? Database,
    IReadOnlyList<ContextStatus> Contexts,
    IReadOnlyList<MigrationEntry> Unknown,
    GitStatus Git,
    DateTimeOffset TimestampLocal,
    string? DatabaseError = null,
    string? ConfigurationError = null,
    DevTunnelStatus? DevTunnel = null,
    string? DatabaseOffline = null,
    bool DatabaseReached = false)
{
    public bool IsEnvironmentUpToDate =>
        DatabaseError is null
        && DatabaseOffline is null
        && ConfigurationError is null
        && Contexts.All(c => c.IsUpToDate)
        && Unknown.Count == 0;
}

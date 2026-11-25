using Soffritto.Core;
using Soffritto.Core.Docker;

namespace Soffritto.Presentation;

/// <summary>
/// Come l'host mostra le sezioni. Ogni sezione viene applicata appena la sua lettura finisce:
/// le migrazioni non aspettano il fetch git ne' la risoluzione del dev tunnel.
/// </summary>
public interface IStatusSink
{
    /// <summary>Inizio di un refresh, dopo che la radice e' stata risolta.</summary>
    void BeginRefresh();

    void SetRepoLoading(RepoLoadingView view);

    void ApplyGit(GitView view);

    /// <summary>Le righe dei context restano leggibili durante la rilettura: cambia solo il timestamp.</summary>
    void SetMigrationsLoading();

    void ApplyMigrations(MigrationsView view);

    void FailMigrations();

    /// <summary>
    /// Azione in corso sulle migrazioni ("dotnet ef database update…"), mostrata al posto del
    /// timestamp dei context; null a fine azione, quando il timestamp torna quello dell'ultima lettura.
    /// </summary>
    void SetMigrationsAction(string? action);

    void SetTunnelQueued();

    /// <summary>Le porte dell'ultima lettura restano visibili finche' non arriva la nuova.</summary>
    void SetTunnelLoading();

    void ApplyTunnel(TunnelView view);

    void SetActivity(string line);

    void SetErrorLine(string errorLine);

    void SetBusy(bool busy);

    void BeginOperation();

    void EndOperation();

    /// <summary>Riporta la tendina sul branch corrente dopo uno switch non eseguito.</summary>
    void RestoreBranchSelection();
}

public interface IUserPrompt
{
    Task<bool> ConfirmAsync(Confirmation confirmation, CancellationToken cancellationToken);
}

public interface IClipboard
{
    bool TrySetText(string text);
}

/// <param name="TunnelGate">
/// Serializza le letture dev tunnel tra piu' sessioni (null: nessuna serializzazione).
/// </param>
/// <param name="EnvironmentLabel">Nome dell'ambiente nelle conferme (null: nessuna menzione).</param>
/// <param name="RootUnavailableHeadline">
/// Titolo dell'errore quando la radice non si risolve durante un refresh (null: uscita silenziosa).
/// </param>
/// <param name="PoolAssignmentOf">
/// Dalla radice, la porta del pool assegnata al repository: la card legge quel tunnel e la sola
/// porta assegnata, e senza assegnazione mostra «non condiviso» senza interrogare la CLI
/// (funzione assente: la lettura per suffisso numerico della cartella).
/// </param>
/// <param name="DatabaseGuard">
/// Dalla radice, l'errore che ferma lettura delle migrazioni, database update, rollback e copia
/// del comando, o null se il database e' quello giusto (null: nessuna guardia, gli ambienti locali).
/// </param>
/// <param name="SchemaLock">
/// Dalla radice, il motivo per cui database update, rollback e copia del comando sono rifiutati
/// (database protetto), o null se lo schema si puo' toccare. La lettura delle migrazioni resta
/// libera (null: nessun blocco).
/// </param>
public sealed record RepoSessionOptions(
    ProbeOptions Probe,
    bool ReadsMigrations,
    bool ReadsTunnel,
    SemaphoreSlim? TunnelGate,
    string? EnvironmentLabel,
    string? RootUnavailableHeadline,
    string? RootUnavailableMessage,
    Func<string, PoolAssignment?>? PoolAssignmentOf = null,
    Func<string, string?>? DatabaseGuard = null,
    Func<string?>? DatabaseOffline = null,
    Func<string, string?>? SchemaLock = null);

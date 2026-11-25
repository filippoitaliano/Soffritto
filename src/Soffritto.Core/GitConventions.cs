namespace Soffritto.Core;

/// <summary>Le convenzioni git dello strumento, uguali per ogni repository del catalogo.</summary>
public static class GitConventions
{
    /// <summary>
    /// Il branch di sviluppo principale: Reset ci riporta i repository, i branch nuovi degli
    /// ambienti Docker ne nascono, «Verifica seed» e «Allinea seed» si confrontano con le sue
    /// migrazioni. Ogni repository del catalogo lo deve avere su origin.
    /// </summary>
    public const string DevelopmentBranch = "dev";

    /// <summary>Il remote-tracking branch di <see cref="DevelopmentBranch"/>.</summary>
    public const string RemoteDevelopmentBranch = "origin/" + DevelopmentBranch;

    /// <summary>Il ref completo di <see cref="RemoteDevelopmentBranch"/>.</summary>
    public const string RemoteDevelopmentReference = "refs/remotes/" + RemoteDevelopmentBranch;
}

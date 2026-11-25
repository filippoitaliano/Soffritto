namespace Soffritto.Presentation;

/// <summary>
/// Buffer per ricomporre la riga degli errori man mano che le sezioni arrivano, senza che
/// l'errore di una sezione gia' applicata venga perso da quella successiva.
/// </summary>
public sealed class ProblemBuffer
{
    public string Migrations { get; set; } = string.Empty;

    public string Git { get; set; } = string.Empty;

    /// <summary>Esito negativo dell'ultima azione; azzerato dal refresh successivo.</summary>
    public string Action { get; set; } = string.Empty;

    public string Compose() => string.Join(
        Environment.NewLine,
        new[] { Migrations, Git, Action }.Where(p => p.Length > 0));
}

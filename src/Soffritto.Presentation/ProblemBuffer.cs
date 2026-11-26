namespace Soffritto.Presentation;

/// <summary>
/// Buffer that rebuilds the error line as the sections arrive, so that the error of a section
/// already applied is not lost when the next one comes in.
/// </summary>
public sealed class ProblemBuffer
{
    public string Migrations { get; set; } = string.Empty;

    public string Git { get; set; } = string.Empty;

    /// <summary>Failure of the last action; cleared by the next refresh.</summary>
    public string Action { get; set; } = string.Empty;

    public string Compose() => string.Join(
        Environment.NewLine,
        new[] { Migrations, Git, Action }.Where(p => p.Length > 0));
}

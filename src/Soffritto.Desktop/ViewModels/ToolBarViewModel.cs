namespace Soffritto.Desktop.ViewModels;

/// <summary>
/// Gli strumenti dell'ambiente nella testata della colonna. Quelli di ogni repository stanno nella
/// sua card (<see cref="RepoCardViewModel.Tool"/>, <see cref="RepoCardViewModel.Tunnel"/>).
/// </summary>
public sealed class ToolBarViewModel
{
    /// <summary>Avvia/ferma d'ambiente: backend, frontend, generici e i loro tunnel insieme, non l'app.</summary>
    public ToolGroupViewModel Stack { get; } = new(string.Empty, string.Empty);
}

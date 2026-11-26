namespace Soffritto.Desktop.ViewModels;

/// <summary>
/// The environment tools in the column header. Each repository's tools live in its
/// card (<see cref="RepoCardViewModel.Tool"/>, <see cref="RepoCardViewModel.Tunnel"/>).
/// </summary>
public sealed class ToolBarViewModel
{
    /// <summary>Environment start/stop: backend, frontend, generic repositories and their tunnels together, not the app.</summary>
    public ToolGroupViewModel Stack { get; } = new(string.Empty, string.Empty);
}

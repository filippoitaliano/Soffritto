using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.Shell;

namespace Soffritto.Vsix;

[VisualStudioContribution]
internal sealed class ShowStatusWindowCommand : Command
{
    public override CommandConfiguration CommandConfiguration => new("%Soffritto.ShowWindow.DisplayName%")
    {
        Placements = [CommandPlacement.KnownPlacements.ViewOtherWindowsMenu],
        Icon = new(ImageMoniker.KnownValues.Database, IconSettings.IconAndText),
    };

    public override Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken)
        => this.Extensibility.Shell().ShowToolWindowAsync<StatusWindow>(activate: true, cancellationToken);
}

using Microsoft.VisualStudio.Extensibility;

namespace Soffritto.Vsix;

[VisualStudioContribution]
internal sealed class DevEnvironmentExtension : Extension
{
    public override ExtensionConfiguration ExtensionConfiguration => new()
    {
        Metadata = new(
            id: "Soffritto.5f1c9a2e",
            version: this.ExtensionAssemblyVersion,
            publisherName: "Filippo Italiano",
            displayName: "Soffritto",
            description: "Status of the backend clone: EF Core migrations of the configured contexts, git, dev tunnel and Kestrel. Pull, branch switch and cleanup, database update and Kestrel start, each with its safeguards.")
        {
            // Without this, an AnyCPU project produces two InstallationTargets (Amd64|Arm64) and
            // VSIXInstaller rejects the package with "Multiple targets are currently not supported".
            InstallationTargetArchitecture = VisualStudioArchitecture.Amd64,
            Icon = "Images\\icon.png",
            PreviewImage = "Images\\preview.png",
        },
    };
}

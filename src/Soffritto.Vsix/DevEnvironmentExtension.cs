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
            description: "Stato del clone del backend: migrazioni EF Core dei context configurati, git, dev tunnel e Kestrel. Pull, cambio e pulizia branch, database update e avvio di Kestrel con le loro guardie.")
        {
            // Senza questo, un progetto AnyCPU genera due InstallationTarget (Amd64|Arm64) e
            // VSIXInstaller rifiuta il pacchetto con "Multiple targets are currently not supported".
            InstallationTargetArchitecture = VisualStudioArchitecture.Amd64,
            Icon = "Images\\icon.png",
            PreviewImage = "Images\\preview.png",
        },
    };
}

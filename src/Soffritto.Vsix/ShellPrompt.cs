using Soffritto.Presentation;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Shell;

namespace Soffritto.Vsix;

internal sealed class ShellPrompt(VisualStudioExtensibility extensibility) : IUserPrompt
{
    // Il prompt della Shell non ha titolo ne' elenchi: la conferma arriva in testo semplice, titolo in testa.
    public Task<bool> ConfirmAsync(Confirmation confirmation, CancellationToken cancellationToken)
        => extensibility.Shell().ShowPromptAsync(confirmation.ToPlainText(), PromptOptions.OKCancel, cancellationToken);
}

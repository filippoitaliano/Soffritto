using Soffritto.Presentation;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Shell;

namespace Soffritto.Vsix;

internal sealed class ShellPrompt(VisualStudioExtensibility extensibility) : IUserPrompt
{
    // The Shell prompt has neither a title nor lists: the confirmation arrives as plain text, title first.
    public Task<bool> ConfirmAsync(Confirmation confirmation, CancellationToken cancellationToken)
        => extensibility.Shell().ShowPromptAsync(confirmation.ToPlainText(), PromptOptions.OKCancel, cancellationToken);
}

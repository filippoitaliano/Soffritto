using System.Windows;
using Soffritto.Presentation;

namespace Soffritto.Desktop.Infrastructure;

public sealed class DialogPrompt : IUserPrompt
{
    public async Task<bool> ConfirmAsync(Confirmation confirmation, CancellationToken cancellationToken)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
        {
            return false;
        }

        try
        {
            return await dispatcher.InvokeAsync(() => ConfirmDialog.Ask(Application.Current?.MainWindow, confirmation));
        }
        catch (TaskCanceledException)
        {
            return false;
        }
    }
}

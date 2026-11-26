using System.Windows.Input;

namespace Soffritto.Desktop.Infrastructure;

// Button enabling goes through the IsEnabled binding, as in the VSIX: the command can always
// execute and the real guards live in the session.
public sealed class AsyncCommand(Func<Task> execute) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => _ = RunAsync();

    private async Task RunAsync()
    {
        try
        {
            await execute();
        }
        catch
        {
            // The session reports its own errors in the panel: an escaped exception must not
            // bring down the application.
        }
    }
}

using System.Windows.Input;

namespace Soffritto.Desktop.Infrastructure;

// L'abilitazione dei bottoni passa dal binding IsEnabled, come nella VSIX: il comando e' sempre
// eseguibile e le guardie vere stanno nella sessione.
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
            // La sessione riporta i propri errori nel pannello: un'eccezione sfuggita non deve
            // abbattere l'applicazione.
        }
    }
}

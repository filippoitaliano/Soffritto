using System.Windows.Input;

namespace Soffritto.Desktop.Infrastructure;

/// <summary>Un comando sincrono con parametro, per i bottoni che agiscono sulla riga in cui stanno.</summary>
public sealed class RelayCommand(Action<object?> execute) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => execute(parameter);
}

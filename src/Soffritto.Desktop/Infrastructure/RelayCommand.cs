using System.Windows.Input;

namespace Soffritto.Desktop.Infrastructure;

/// <summary>A synchronous command with a parameter, for buttons that act on the row they sit in.</summary>
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

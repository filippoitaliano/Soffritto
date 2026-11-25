using System.Windows;

namespace Soffritto.Desktop.Infrastructure;

/// <summary>
/// La sessione condivisa chiama i sink da thread del pool, ma i ViewModel legati a WPF (le
/// ObservableCollection in particolare) vanno toccati solo dal thread della UI.
/// </summary>
public static class UiThread
{
    public static void Run(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
        {
            return;
        }

        if (dispatcher.CheckAccess())
        {
            action();
            return;
        }

        try
        {
            dispatcher.Invoke(action);
        }
        catch (TaskCanceledException)
        {
            // Dispatcher fermato tra il controllo e l'invocazione: chiusura in corso.
        }
    }
}

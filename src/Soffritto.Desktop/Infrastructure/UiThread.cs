using System.Windows;

namespace Soffritto.Desktop.Infrastructure;

/// <summary>
/// The shared session calls the sinks from thread-pool threads, but WPF-bound ViewModels
/// (ObservableCollections in particular) must be touched only from the UI thread.
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
            // Dispatcher stopped between the check and the invocation: shutdown in progress.
        }
    }
}

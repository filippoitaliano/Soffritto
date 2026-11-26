using Soffritto.Desktop.ViewModels;

namespace Soffritto.Desktop;

/// <summary>
/// The operation logs of the Docker environments, by number. They live outside the columns because
/// creation writes before the column exists, and the column picks them up when it is born. On the UI thread.
/// </summary>
public static class OperationLogs
{
    private static readonly Dictionary<int, LogSectionViewModel> logs = [];

    public static LogSectionViewModel For(int number)
    {
        if (!logs.TryGetValue(number, out var log))
        {
            log = new LogSectionViewModel("Operations");
            logs[number] = log;
        }

        return log;
    }

    /// <summary>A new environment with that number starts from an empty log.</summary>
    public static LogSectionViewModel Reset(int number)
    {
        logs.Remove(number);
        return For(number);
    }

    public static void Remove(int number) => logs.Remove(number);
}

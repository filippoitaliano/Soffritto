using Soffritto.Desktop.ViewModels;

namespace Soffritto.Desktop;

/// <summary>
/// I log delle operazioni degli ambienti Docker, per numero. Vivono fuori dalle colonne perche' la
/// creazione scrive prima che la colonna esista, e la colonna li ritrova quando nasce. Sul thread della UI.
/// </summary>
public static class OperationLogs
{
    private static readonly Dictionary<int, LogSectionViewModel> logs = [];

    public static LogSectionViewModel For(int number)
    {
        if (!logs.TryGetValue(number, out var log))
        {
            log = new LogSectionViewModel("Operazioni");
            logs[number] = log;
        }

        return log;
    }

    /// <summary>Un ambiente nuovo con quel numero parte da un log vuoto.</summary>
    public static LogSectionViewModel Reset(int number)
    {
        logs.Remove(number);
        return For(number);
    }

    public static void Remove(int number) => logs.Remove(number);
}

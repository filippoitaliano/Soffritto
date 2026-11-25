namespace Soffritto.Presentation;

public static class CopyFeedback
{
    /// <summary>
    /// Feedback transitorio della copia al posto dell'etichetta della riga, poi si ripristina
    /// <paramref name="restore"/> se nel frattempo un refresh non ha gia' sostituito il valore.
    /// </summary>
    public static async Task ShowAsync(
        bool copied,
        Func<string> read,
        Action<string> write,
        string restore,
        CancellationToken cancellationToken)
    {
        var feedback = copied ? StatusText.Copied : StatusText.CopyFailed;
        write(feedback);

        await Task.Delay(TimeSpan.FromSeconds(2.5), cancellationToken).ConfigureAwait(false);

        if (read() == feedback)
        {
            write(restore);
        }
    }
}

namespace Soffritto.Presentation;

public static class CopyFeedback
{
    /// <summary>
    /// Transient copy feedback in place of the row's label, then <paramref name="restore"/> is put
    /// back unless a refresh has already replaced the value in the meantime.
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

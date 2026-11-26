namespace Soffritto.Presentation;

/// <summary>
/// Runs the reads of a panel section one at a time. A request that arrives while
/// a read is in progress does not start a second one in parallel (two concurrent git fetches
/// fight over the repository lock, two devtunnel calls are just waste): it is queued and starts
/// as soon as the current one finishes, keeping only the last request received.
/// </summary>
public sealed class SectionLoader
{
    private readonly object gate = new();
    private Func<Task>? queued;
    private bool running;

    public void Request(Func<Task> work)
    {
        lock (gate)
        {
            if (running)
            {
                queued = work;
                return;
            }

            running = true;
        }

        _ = RunAsync(work);
    }

    private async Task RunAsync(Func<Task> work)
    {
        while (true)
        {
            try
            {
                await work().ConfigureAwait(false);
            }
            catch
            {
                // Section work reports its own errors in the panel: this only prevents
                // an escaped exception from leaving the section stuck on "running" forever.
            }

            lock (gate)
            {
                if (queued is null)
                {
                    running = false;
                    return;
                }

                work = queued;
                queued = null;
            }
        }
    }
}

namespace Soffritto.Presentation;

/// <summary>
/// Esegue la lettura di una sezione del pannello una alla volta. Una richiesta arrivata mentre
/// una lettura e' in corso non ne avvia una seconda in parallelo (due git fetch concorrenti si
/// contendono il lock del repository, due devtunnel sono solo spreco): resta accodata e parte
/// appena la corrente finisce, conservando solo l'ultima richiesta ricevuta.
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
                // Il lavoro di sezione riporta i propri errori nel pannello: qui si evita solo
                // che un'eccezione sfuggita lasci la sezione bloccata per sempre su "running".
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

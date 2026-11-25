namespace Soffritto.Presentation;

/// <summary>
/// Una sola azione di scrittura alla volta (pull, switch, pulizia branch, database update). La
/// portata la decide chi possiede il gate: la finestra nella VSIX, l'ambiente nell'exe.
/// </summary>
public sealed class WriteGate
{
    private int busy;

    public event EventHandler? Changed;

    public bool IsNotBusy => Volatile.Read(ref busy) == 0;

    public bool TryEnter()
    {
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0)
        {
            return false;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Exit()
    {
        Interlocked.Exchange(ref busy, 0);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

namespace Soffritto.Presentation;

/// <summary>
/// Only one write action at a time (pull, switch, branch cleanup, database update). The
/// scope is decided by whoever owns the gate: the window in the VSIX, the environment in the app.
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

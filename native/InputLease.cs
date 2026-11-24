namespace AgyComputerUse;

/** Serializes input across our MCP/CLI helpers. A terminated owner abandons the mutex. */
internal sealed class InputLease : IDisposable
{
    private readonly Mutex mutex;
    private bool acquired;
    public InputLease()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var user = identity.User?.Value ?? Environment.UserName;
        mutex = new Mutex(false, "Local\\AgyComputerUse.Input." + user);
        try { acquired = mutex.WaitOne(1500); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) { mutex.Dispose(); throw new DesktopError("INPUT_BUSY", "Another computer-use instance is operating the desktop. No new input was sent."); }
    }
    public void Dispose() { if (acquired) { mutex.ReleaseMutex(); acquired = false; } mutex.Dispose(); }
}

using System.Runtime.ExceptionServices;
using System.Windows.Forms;

namespace AgyComputerUse;

internal sealed class StaDispatcher : IDisposable
{
    private static readonly Lazy<StaDispatcher> Shared = new(() => new StaDispatcher());
    private readonly Thread thread;
    private readonly ManualResetEventSlim ready = new();
    private Control? control;
    private Exception? startupError;
    private StaDispatcher()
    {
        thread = new Thread(() =>
        {
            try
            {
                control = new Control();
                _ = control.Handle;
                ready.Set();
                Application.Run();
                control.Dispose();
            }
            catch (Exception error) { startupError = error; ready.Set(); }
        }) { IsBackground = true, Name = "AGY clipboard and shell STA" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!ready.Wait(2000)) throw new DesktopError("UI_THREAD_TIMEOUT", "STA message loop did not start.");
        if (startupError != null) throw new DesktopError("UI_THREAD_ERROR", "STA message loop could not be initialized.");
    }
    internal static bool IsCurrent => Shared.IsValueCreated && Environment.CurrentManagedThreadId == Shared.Value.thread.ManagedThreadId;
    internal static IntPtr ClipboardOwner => IsCurrent ? Shared.Value.control!.Handle : throw new InvalidOperationException("Clipboard owner requires the STA thread.");
    internal static T Run<T>(Func<T> callback)
    {
        var dispatcher = Shared.Value;
        if (IsCurrent) return callback();
        try { return (T)dispatcher.control!.Invoke(callback)!; }
        catch (System.Reflection.TargetInvocationException error) when (error.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
    }
    internal static void Shutdown() { if (Shared.IsValueCreated) Shared.Value.Dispose(); }
    internal static void Post(Action callback)
    {
        if (!Shared.IsValueCreated) return;
        var dispatcher = Shared.Value;
        if (dispatcher.control is { IsDisposed: false }) dispatcher.control.BeginInvoke(callback);
    }
    public void Dispose()
    {
        if (control == null || control.IsDisposed) return;
        control.BeginInvoke(new Action(Application.ExitThread));
        thread.Join(1500);
        ready.Dispose();
    }
}

using System.Runtime.ExceptionServices;
using System.Windows.Forms;

namespace AgyComputerUse;

// Rendering and the Escape hook must not wait behind clipboard/OLE work.
internal sealed class PresentationDispatcher
{
    private static readonly Lazy<PresentationDispatcher> Shared = new(() => new());
    private readonly Thread thread;
    private readonly ManualResetEventSlim ready = new();
    private Control? control;
    private Exception? failure;
    private volatile bool closed;
    private PresentationDispatcher()
    {
        thread = new Thread(() =>
        {
            try { control = new Control(); _ = control.Handle; ready.Set(); Application.Run(); control.Dispose(); }
            catch (Exception error) { failure = error; ready.Set(); }
        }) { IsBackground = true, Name = "AGY presentation and Escape STA" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!ready.Wait(3000) || failure != null) throw new DesktopError("PRESENTATION_UNAVAILABLE", "Presentation thread could not start. No desktop action was started.");
    }
    internal static T Run<T>(Func<T> work)
    {
        var dispatcher = Shared.Value;
        if (dispatcher.closed) throw new DesktopError("PRESENTATION_UNAVAILABLE", "Presentation thread has closed.");
        if (Environment.CurrentManagedThreadId == dispatcher.thread.ManagedThreadId) return work();
        try { return (T)dispatcher.control!.Invoke(work)!; }
        catch (System.Reflection.TargetInvocationException error) when (error.InnerException != null) { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
    internal static void Post(Action work)
    {
        var dispatcher = Shared.Value;
        if (dispatcher.closed) return;
        try { dispatcher.control!.BeginInvoke(work); } catch (InvalidOperationException) when (dispatcher.closed) { }
    }
    internal static void Shutdown()
    {
        if (!Shared.IsValueCreated) return;
        var dispatcher = Shared.Value; dispatcher.closed = true;
        dispatcher.control?.BeginInvoke(new Action(Application.ExitThread)); dispatcher.thread.Join(1500);
    }
}

using System.Text.Json;

namespace AgyComputerUse;

internal sealed partial class DesktopService
{
    private InputLease? workflowLease;
    private string? workflowLeaseToken;
    private DateTime workflowLeaseDeadline;
    private IDisposable? workflowPresentation;
    private object WorkflowLease(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object || args.EnumerateObject().Any(item => item.Name is not ("operation" or "token"))) throw new DesktopError("INVALID_ARGUMENT", "Invalid internal lease request.");
        if (args.Text("operation") == "begin")
        {
            EnsureDesktop();
            ControlPresentation.Check();
            if (workflowLease != null) throw new DesktopError("INPUT_BUSY", "A workflow already owns this helper's input lease.");
            workflowLease = new InputLease(); workflowLeaseToken = Guid.NewGuid().ToString("N"); workflowLeaseDeadline = DateTime.UtcNow.AddSeconds(35);
            try { workflowPresentation = ControlPresentation.Begin(); } catch { ReleaseWorkflowLease(); throw; }
            return new { token = workflowLeaseToken };
        }
        if (args.Text("operation") != "end" || workflowLeaseToken == null || args.Text("token") != workflowLeaseToken) throw new DesktopError("INVALID_ARGUMENT", "Unknown internal lease token.");
        ReleaseWorkflowLease(); return new { released = true };
    }
    private void ReleaseWorkflowLease() { workflowPresentation?.Dispose(); workflowPresentation = null; workflowLease?.Dispose(); workflowLease = null; workflowLeaseToken = null; }
}

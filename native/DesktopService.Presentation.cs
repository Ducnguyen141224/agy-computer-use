using System.Text.Json;

namespace AgyComputerUse;

internal sealed partial class DesktopService
{
    // Internal bridge protocol: visible lifetime covers browser input, never model/tool idle time.
    private readonly Dictionary<string, IDisposable> presentationLeases = new();
    private object PresentationLease(JsonElement args)
    {
        if(args.ValueKind != JsonValueKind.Object || args.EnumerateObject().Any(item => item.Name is not ("operation" or "token"))) throw new DesktopError("INVALID_ARGUMENT", "Invalid presentation lease.");
        if(args.Text("operation") == "begin")
        {
            EnsureDesktop(); ControlPresentation.Check();
            if(presentationLeases.Count >= 8) throw new DesktopError("INPUT_BUSY", "Too many active presentation leases.");
            var token = Guid.NewGuid().ToString("N");
            presentationLeases[token] = ControlPresentation.Begin();
            return new { token };
        }
        var key = args.Text("token") ?? "";
        if(args.Text("operation") != "end" || !presentationLeases.Remove(key, out var activity)) throw new DesktopError("INVALID_ARGUMENT", "Unknown presentation lease.");
        activity.Dispose(); return new { released = true };
    }
}

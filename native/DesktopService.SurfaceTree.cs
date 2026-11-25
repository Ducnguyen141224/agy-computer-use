using System.Diagnostics;
using System.Text.Json;
using System.Windows.Automation;

namespace AgyComputerUse;

internal sealed partial class DesktopService
{
    private sealed record SurfaceDescriptor(string Key, string Kind, int Pid, IntPtr Handle, string Name, AutomationElement? Element = null);
    private static string? EmbeddedSurfaceKind(AutomationElement element)
    {
        if (element.Current.IsOffscreen) return null;
        var type = element.Current.ControlType;
        if (type == ControlType.Menu) return "menu";
        if (type == ControlType.ToolTip) return "tooltip";
        if (NativeProperty(element, 30101) is string aria)
        {
            var roles = aria.Split(' ');
            if (roles.Contains("alert") || roles.Contains("alertdialog")) return "alert";
            if (roles.Contains("dialog")) return "sheet";
        }
        if (NativeBoolean(element, 30174) == true) return "sheet";
        if (type == ControlType.Window && element.TryGetCurrentPattern(WindowPattern.Pattern, out var pattern) && ((WindowPattern)pattern).Current.IsModal) return "dialog";
        return null;
    }
    private static (List<SurfaceDescriptor> Items, bool Complete) ReadSurfaceDescriptors(WindowEntry? selected, string? filter, string? query)
    {
        var result = new List<SurfaceDescriptor>(); var complete = true;
        void Add(SurfaceDescriptor item)
        {
            if ((filter == null || item.Kind == filter || item.Kind == "shell_panel" && ShellPanelType(item.Handle) == filter) && (query == null || item.Name.Contains(query, StringComparison.OrdinalIgnoreCase))) result.Add(item);
        }
        Native.EnumerateAllWindows((handle, _) =>
        {
            try
            {
                if (handle == CursorOverlay.WindowHandle || ControlPresentation.OwnsWindow(handle) || !Native.IsWindowVisible(handle) || Native.IsCloaked(handle)) return true;
                Native.GetWindowThreadProcessId(handle, out var pid);
                if (selected != null && pid != selected.Pid) return true;
                var kind = SurfaceKind(handle, (int)pid);
                if (kind != null)
                {
                    var text = new System.Text.StringBuilder(1024); Native.GetWindowText(handle, text, text.Capacity);
                    Add(new($"{pid}:{ProcessStarted((int)pid)}:{handle}", kind, (int)pid, handle, Trim(text.ToString(), 200)));
                }
            }
            catch { complete = false; }
            if (result.Count >= 60) { complete = false; return false; }
            return true;
        }, IntPtr.Zero);
        if (selected == null) return (result, complete);
        var clock = Stopwatch.StartNew(); var visits = 0;
        var pending = new Queue<(AutomationElement Element, int Depth)>();
        try
        {
            var root = AutomationElement.FromHandle(selected.Handle);
            pending.Enqueue((root, 0));
            while (pending.Count > 0 && visits++ < 500 && clock.ElapsedMilliseconds < 1500)
            {
                var (element, depth) = pending.Dequeue();
                var current = element.Current;
                if (depth > 0 && !current.IsOffscreen)
                {
                    var kind = EmbeddedSurfaceKind(element);
                    // A separate HWND is already covered by native surface enumeration.
                    if (kind != null && (current.NativeWindowHandle == 0 || new IntPtr(current.NativeWindowHandle) == selected.Handle)) Add(new(RuntimeId(element), kind, current.ProcessId, selected.Handle, Trim(current.Name, 200), element));
                }
                var child = TreeWalker.ControlViewWalker.GetFirstChild(element);
                if (depth >= 12) { if (child != null) complete = false; continue; }
                while (child != null && pending.Count < 500) { pending.Enqueue((child, depth + 1)); child = TreeWalker.ControlViewWalker.GetNextSibling(child); }
                if (child != null) complete = false;
            }
            if (pending.Count > 0) complete = false;
        }
        catch { complete = false; }
        return (result, complete);
    }
    private object ListScopedSurfaces(JsonElement args)
    {
        var selected = args.Text("window_id") is string id ? GetWindow(id) : null;
        var requested = args.Text("surface");
        if (requested == "focused")
        {
            var handle = Native.GetForegroundWindow();
            var owner = selected ?? RegisterWindow(handle);
            var focus = JsonSerializer.SerializeToElement(Query(JsonSerializer.SerializeToElement(new { operation = "focused", window_id = owner.Id })), Program.Json);
            var entry = focus.GetProperty("item").GetProperty("ref").GetString();
            return new { surfaces = new[] { new { kind = "focused", root_ref = entry, window = WindowInfo(owner) } }, complete = true };
        }
        var snapshot = ReadSurfaceDescriptors(selected, requested, args.Text("text"));
        var items = new List<object>();
        foreach (var item in snapshot.Items)
        {
            try
            {
                var window = item.Element == null ? RegisterWindow(item.Handle) : selected!;
                items.Add(new { kind = item.Kind, surface = item.Kind == "shell_panel" ? ShellPanelType(item.Handle) : null, name = item.Name, root_ref = item.Element == null ? null : AddRef(window, item.Element), window = WindowInfo(window) });
            }
            catch { snapshot.Complete = false; }
        }
        return new { surfaces = items, complete = snapshot.Complete, scope = selected == null ? "visible top-level surfaces; choose window_id to include in-window menus, sheets and alerts" : "selected app top-level surfaces and a bounded subtree of the selected window" };
    }
    private object CreateSurfaceWatch(JsonElement args, string kind)
    {
        var selected = args.Text("window_id") is string id ? GetWindow(id) : null;
        var watch = new EventWatch { Kind = kind, Root = AutomationElement.RootElement, Created = DateTime.UtcNow };
        var monitor = new SurfaceMonitor(watch, selected, args.Text("surface"), args.Text("query"));
        try { monitor.Start(); } catch { monitor.Dispose(); throw; }
        watch.NativeWatch = monitor;
        var token = $"watch-{epoch}-{++sequence}"; watches[token] = watch;
        return new { token, @event = kind, expires_in_ms = 60000, armed = true, source = "bounded UIA surface snapshots every 150 ms", note = "Only observed transitions count. A surface that appears and disappears entirely between samples can be missed." };
    }
    private sealed class SurfaceMonitor(EventWatch watch, WindowEntry? selected, string? filter, string? query) : IDisposable
    {
        private readonly ManualResetEventSlim stop = new();
        private readonly ManualResetEventSlim ready = new();
        private Thread? thread;
        internal void Start()
        {
            thread = new Thread(Run) { IsBackground = true, Name = "AGY surface watch MTA" }; thread.SetApartmentState(ApartmentState.MTA); thread.Start();
            if (!ready.Wait(2500) || watch.Failure != null) throw new DesktopError("EVENT_UNAVAILABLE", watch.Failure ?? "Surface watch could not establish its initial baseline.");
        }
        private void Run()
        {
            try
            {
                var previous = ReadSurfaceDescriptors(selected, filter, query);
                if (!previous.Complete) { watch.Failure = "Initial surface scope was incomplete; transitions cannot be proved. Narrow the window/surface filter."; return; }
                ready.Set();
                while (!stop.Wait(150) && DateTime.UtcNow - watch.Created < TimeSpan.FromMinutes(1))
                {
                    if (!Native.InteractiveDesktop()) { watch.Failure = "Interactive desktop became unavailable."; return; }
                    var current = ReadSurfaceDescriptors(selected, filter, query);
                    var before = previous.Items.Select(item => item.Key).ToHashSet(); var after = current.Items.Select(item => item.Key).ToHashSet();
                    var transitions = watch.Kind == "surface_appeared" ? current.Items.Where(item => !before.Contains(item.Key)) : current.Complete ? previous.Items.Where(item => !after.Contains(item.Key)) : Enumerable.Empty<SurfaceDescriptor>();
                    foreach (var item in transitions)
                    {
                        while (watch.Events.Count >= 20) watch.Events.TryDequeue(out _);
                        watch.Events.Enqueue(new { @event = watch.Kind, kind = item.Kind, name = item.Name, pid = item.Pid, source = "UIA surface snapshot transition", note = "List surfaces again to obtain current guarded refs." });
                    }
                    if (current.Complete) previous = current;
                }
            }
            catch { watch.Failure = "Surface watch lost access to its UIA scope."; }
            finally { ready.Set(); }
        }
        public void Dispose() { stop.Set(); if (thread == null || thread.Join(500)) { stop.Dispose(); ready.Dispose(); } /* A stalled provider is contained by the helper process deadline. */ }
    }
}

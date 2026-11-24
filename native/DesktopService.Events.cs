using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Windows.Automation;

namespace AgyComputerUse;

internal sealed partial class DesktopService
{
    private sealed class EventWatch
    {
        public required string Kind;
        public required AutomationElement Root;
        public required DateTime Created;
        public AutomationEvent? Event;
        public AutomationEventHandler? Handler;
        public StructureChangedEventHandler? StructureHandler;
        public AutomationFocusChangedEventHandler? FocusHandler;
        public IDisposable? NativeWatch;
        public string? Failure;
        public readonly ConcurrentQueue<object> Events = new();
    }
    private readonly Dictionary<string, EventWatch> watches = new();
    private object CreateWatch(JsonElement args)
    {
        if (watches.Count >= 8) throw new DesktopError("CACHE_LIMIT", "Consume an event watch before creating another.");
        var kind = args.Text("event") ?? throw new DesktopError("INVALID_ARGUMENT", "watch needs an event kind.");
        if (kind is "app_launched" or "app_terminated") return CreateProcessWatch(args, kind);
        if (kind is "surface_appeared" or "surface_dismissed") return CreateSurfaceWatch(args, kind);
        if (kind is not ("window_opened" or "window_closed" or "structure_changed" or "focus_changed")) throw new DesktopError("INVALID_ARGUMENT", "Unsupported event kind.");
        var windowId = args.Text("window_id");
        var selected = windowId == null ? null : GetWindow(windowId);
        var root = selected == null ? AutomationElement.RootElement : AutomationElement.FromHandle(selected.Handle);
        var query = args.Text("query");
        var watch = new EventWatch { Kind = kind, Root = root, Created = DateTime.UtcNow };
        var closedSources = new Dictionary<string, object>();
        bool Accept(AutomationElement element)
        {
            if (selected != null)
            {
                var parent = element;
                var belongs = false;
                for (var depth = 0; parent != null && depth < 24; depth++)
                {
                    if (parent.Current.NativeWindowHandle != 0 && Native.GetAncestor(new IntPtr(parent.Current.NativeWindowHandle), 2) == selected.Handle) { belongs = true; break; }
                    parent = TreeWalker.ControlViewWalker.GetParent(parent);
                }
                if (!belongs) return false;
            }
            return query == null || element.Current.Name.Contains(query, StringComparison.OrdinalIgnoreCase);
        }
        void Record(AutomationElement element)
        {
            try
            {
                if (!Accept(element)) return;
                while (watch.Events.Count >= 20) watch.Events.TryDequeue(out _);
                watch.Events.Enqueue(new { @event = kind, name = Trim(element.Current.Name, 180), pid = element.Current.ProcessId, role = Role(element) });
            }
            catch (ElementNotAvailableException) { /* Closed sources are matched by their captured runtime id below. */ }
            catch { /* Providers can remove an event source before its properties are read. */ }
        }
        if (kind == "structure_changed")
        {
            watch.StructureHandler = (sender, _) => { if (sender is AutomationElement element) Record(element); };
            Automation.AddStructureChangedEventHandler(root, TreeScope.Subtree, watch.StructureHandler);
        }
        else if (kind == "focus_changed")
        {
            watch.FocusHandler = (sender, _) => { if (sender is AutomationElement element) Record(element); };
            Automation.AddAutomationFocusChangedEventHandler(watch.FocusHandler);
        }
        else
        {
            watch.Event = kind == "window_opened" ? WindowPattern.WindowOpenedEvent : WindowPattern.WindowClosedEvent;
            if (kind == "window_closed")
            {
                Native.EnumerateAllWindows((handle, _) =>
                {
                    try
                    {
                        if (selected != null && handle != selected.Handle) return true;
                        if (selected == null && (!Native.IsWindowVisible(handle) || Native.IsCloaked(handle))) return true;
                        if (query != null)
                        {
                            var title = new System.Text.StringBuilder(1024); Native.GetWindowText(handle, title, title.Capacity);
                            if (!title.ToString().Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
                        }
                        var element = AutomationElement.FromHandle(handle);
                        if (Accept(element)) closedSources[RuntimeId(element)] = new { @event = kind, name = Trim(element.Current.Name, 180), pid = element.Current.ProcessId };
                    }
                    catch { }
                    return true;
                }, IntPtr.Zero);
            }
            watch.Handler = (sender, info) =>
            {
                if (kind == "window_closed" && info is WindowClosedEventArgs closed && closedSources.TryGetValue(string.Join(".", closed.GetRuntimeId()), out var source))
                {
                    while (watch.Events.Count >= 20) watch.Events.TryDequeue(out _);
                    watch.Events.Enqueue(source);
                }
                else if (sender is AutomationElement element) Record(element);
            };
            Automation.AddAutomationEventHandler(watch.Event, root, TreeScope.Subtree, watch.Handler);
        }
        var token = $"watch-{epoch}-{++sequence}";
        watches[token] = watch;
        return new { token, @event = kind, expires_in_ms = 60000, armed = true, note = "Subscription is active before this response. Perform the action, then wait_event with this token." };
    }
    private object WaitEvent(JsonElement args)
    {
        var token = args.Text("token") ?? "";
        if (!watches.TryGetValue(token, out var watch) || DateTime.UtcNow - watch.Created > TimeSpan.FromMinutes(1)) throw new DesktopError("STALE_TOKEN", "Event watch expired or is unavailable in this session.");
        var timeout = args.Number("timeout_ms", 3000, 0, 10000);
        var timer = Stopwatch.StartNew();
        do
        {
            if (watch.Failure != null) { var reason = watch.Failure; RemoveWatch(token); return new { verification = "failed", reason, elapsed_ms = timer.ElapsedMilliseconds }; }
            if (watch.Events.TryDequeue(out var item))
            {
                RemoveWatch(token);
                return new { verification = "passed", observed = item, elapsed_ms = timer.ElapsedMilliseconds };
            }
            if (timer.ElapsedMilliseconds >= timeout) break;
            Thread.Sleep(30);
        } while (true);
        RemoveWatch(token);
        return new { verification = "failed", reason = "No matching subscribed event was received before timeout.", elapsed_ms = timer.ElapsedMilliseconds };
    }
    private void RemoveWatch(string token)
    {
        if (!watches.Remove(token, out var watch)) return;
        try
        {
            watch.NativeWatch?.Dispose();
            if (watch.Handler != null) Automation.RemoveAutomationEventHandler(watch.Event!, watch.Root, watch.Handler);
            if (watch.StructureHandler != null) Automation.RemoveStructureChangedEventHandler(watch.Root, watch.StructureHandler);
            if (watch.FocusHandler != null) Automation.RemoveAutomationFocusChangedEventHandler(watch.FocusHandler);
        }
        catch { /* A terminated provider must not prevent releasing the rest of the session. */ }
    }
    private void ClearWatches() { foreach (var token in watches.Keys.ToArray()) RemoveWatch(token); }
}

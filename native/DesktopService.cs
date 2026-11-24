using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;

namespace AgyComputerUse;

internal sealed partial class DesktopService : IDisposable
{
    private readonly string epoch = Guid.NewGuid().ToString("N")[..8];
    private readonly Dictionary<string, WindowEntry> windows = new();
    private readonly Dictionary<string, ElementEntry> references = new();
    private readonly Dictionary<string, Observation> observations = new();
    private readonly Dictionary<string, CaptureEntry> captures = new();
    private long sequence;
    private static readonly TimeSpan RefLifetime = TimeSpan.FromMinutes(2);
    private sealed record WindowEntry(string Id, IntPtr Handle, int Pid, long Started);
    private sealed record ElementEntry(string Ref, WindowEntry Window, AutomationElement Element, string RuntimeId, int ElementPid, long ElementStarted, int Type, string Name, string AutomationId, string Ancestry, DateTime Expires);
    private sealed record Observation(string WindowId, string Scope, bool Complete, List<UiItem> Items, DateTime Created);
    private sealed record CaptureEntry(WindowEntry? Window, Native.Rect Rect, int ImageWidth, int ImageHeight, DateTime Created, string? DisplayId = null, Dictionary<string, Native.Rect>? WindowBounds = null);
    private sealed record ItemBounds(int X, int Y, int Width, int Height);
    private sealed record UiItem(string Ref, string Role, string Name, string? Context, string? Value, bool? Enabled, bool? Offscreen, bool? Password, bool? Checked, bool? Selected, string? Expanded, bool? More, ItemBounds? Bounds);

    public object Dispatch(string method, JsonElement args)
    {
        if (workflowLease != null && DateTime.UtcNow >= workflowLeaseDeadline) { ReleaseWorkflowLease(); throw new DesktopError("WORKFLOW_EXPIRED", "Workflow lease expired. Inspect UI; no action was retried."); }
        if (method == "lease") return WorkflowLease(args);
        using var input = Program.Mutating(method) ? new InputLease() : null;
        Prune();
        ValidateCommand(method, args);
        var metadata = method == "system" && args.Text("operation") is "status" or "permissions" or "version";
        if (!metadata) EnsureDesktop();
        using var activity = IsControlMutation(method, args) ? ControlPresentation.Begin() : null;
        return method switch
        {
            "windows" => ListWindows(args),
            "observe" => Observe(args, false),
            "find" => Observe(args, true),
            "act" => Act(args),
            "capture" => Capture(args),
            "system" => SystemOperation(args),
            "query" => Query(args),
            "batch" => Batch(args),
            "session" => Session(args),
            _ => throw new DesktopError("INVALID_ARGUMENT", "Unknown desktop operation.")
        };
    }
    private static bool IsControlMutation(string method, JsonElement args) => args.Flag("open") || method is "act" or "batch" || method == "system" && args.Text("operation") is not ("status" or "permissions" or "version" or "list_apps" or "list_displays" or "window_info" or "clipboard_get" or "list_surfaces" or "list_notifications" or "cursor_status" or "cursor_config" or "cursor_remove" or "control_status" or "control_resume" or "control_stop");
    public void Dispose() { ReleaseWorkflowLease(); ControlPresentation.Close(); CursorOverlay.Close(); Native.ReleaseOwnedInput(); ClearWatches(); Native.CloseShellCommandWindow(); StaDispatcher.Shutdown(); }

    private static void EnsureDesktop()
    {
        if (!Native.InteractiveDesktop()) throw new DesktopError("DESKTOP_UNAVAILABLE", "The default interactive desktop is unavailable, locked, or differs from the helper desktop.");
    }

    private object ListWindows(JsonElement args)
    {
        var query = args.Text("query") ?? "";
        var limit = args.Number("limit", 30, 1, 60);
        var items = new List<object>();
        var found = new Dictionary<string, WindowEntry>();
        Native.EnumerateAllWindows((handle, _) =>
        {
            try
            {
                if (handle == CursorOverlay.WindowHandle || handle == ControlPresentation.WindowHandle || !Native.IsWindowVisible(handle) || Native.IsCloaked(handle)) return true;
                var titleBuffer = new StringBuilder(1024);
                Native.GetWindowText(handle, titleBuffer, titleBuffer.Capacity);
                var title = titleBuffer.ToString();
                if (string.IsNullOrWhiteSpace(title)) return true;
                Native.GetWindowThreadProcessId(handle, out var pid);
                using var process = Process.GetProcessById((int)pid);
                var app = process.ProcessName;
                if (!title.Contains(query, StringComparison.OrdinalIgnoreCase) && !app.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
                var start = process.StartTime.ToUniversalTime().Ticks;
                var id = $"w-{pid}-{start:x}-{handle.ToInt64():x}";
                var entry = new WindowEntry(id, handle, (int)pid, start);
                found[id] = entry;
                if (items.Count < limit) items.Add(new { id, app, title = Trim(title, 200), pid, minimized = Native.IsIconic(handle), foreground = Native.GetForegroundWindow() == handle });
            }
            catch { /* An application can exit while the window list is being read. */ }
            return true;
        }, IntPtr.Zero);
        foreach (var entry in found) windows[entry.Key] = entry.Value;
        foreach (var key in windows.Keys.ToArray()) if (!Native.IsWindow(windows[key].Handle)) windows.Remove(key);
        return new { windows = items, total = found.Count, complete = found.Count <= limit };
    }

    private WindowEntry GetWindow(string? id)
    {
        if (id == null || !windows.TryGetValue(id, out var entry)) throw new DesktopError("WINDOW_NOT_FOUND", "Choose a window_id returned by desktop_windows in this server session.");
        if (!Native.IsWindow(entry.Handle)) throw new DesktopError("WINDOW_NOT_FOUND", "The window has closed. List windows again.");
        Native.GetWindowThreadProcessId(entry.Handle, out var pid);
        if (pid != entry.Pid) throw new DesktopError("WINDOW_CHANGED", "The window owner changed. List windows again.");
        if (ProcessStarted(entry.Pid) != entry.Started)
            throw new DesktopError("WINDOW_CHANGED", "The window owner changed. List windows again.");
        return entry;
    }

    private ElementEntry Resolve(string? reference, bool checkName = true)
    {
        if (reference == null || !references.TryGetValue(reference, out var entry) || entry.Expires < DateTime.UtcNow)
            throw new DesktopError("STALE_REF", "Ref is missing or expired. Observe again.");
        GetWindow(entry.Window.Id);
        var current = entry.Element.Current;
        if (current.ProcessId != entry.ElementPid || ProcessStarted(entry.ElementPid) != entry.ElementStarted || RuntimeId(entry.Element) != entry.RuntimeId || current.ControlType.Id != entry.Type || current.AutomationId != entry.AutomationId || Ancestry(entry.Element, entry.Window) != entry.Ancestry || (checkName && current.Name != entry.Name))
            throw new DesktopError("STALE_REF", "Element identity changed. Observe again.");
        return entry;
    }

    private string AddRef(WindowEntry window, AutomationElement element)
    {
        var current = element.Current;
        var runtime = RuntimeId(element);
        // Refs remain snapshot-scoped. A diff includes replacement refs for every changed item.
        var reference = $"@{epoch}:{++sequence}";
        references[reference] = new(reference, window, element, runtime, current.ProcessId, ProcessStarted(current.ProcessId), current.ControlType.Id, current.Name, current.AutomationId, Ancestry(element, window), DateTime.UtcNow + RefLifetime);
        return reference;
    }

    private static string RuntimeId(AutomationElement element) => string.Join(".", element.GetRuntimeId());
    private static long ProcessStarted(int pid)
    {
        if (pid == 0) return 0;
        try { using var process = Process.GetProcessById(pid); return process.StartTime.ToUniversalTime().Ticks; }
        catch (ArgumentException) { throw new DesktopError("STALE_REF", "The owning process exited. Observe again."); }
        catch (System.ComponentModel.Win32Exception) { throw new DesktopError("PERMISSION_DENIED", "The process generation could not be read, so its control identity cannot be proven."); }
    }
    private static string Ancestry(AutomationElement element, WindowEntry window)
    {
        var parts = new List<string>();
        var parent = TreeWalker.ControlViewWalker.GetParent(element);
        for (var depth = 0; parent != null && depth < 64; depth++)
        {
            var current = parent.Current;
            parts.Add($"{RuntimeId(parent)}:{current.ControlType.Id}:{current.AutomationId}:{current.Name}");
            if (current.NativeWindowHandle == window.Handle.ToInt64()) break;
            parent = TreeWalker.ControlViewWalker.GetParent(parent);
        }
        return string.Join("|", parts);
    }
    private static string Role(AutomationElement element) => element.Current.ControlType.ProgrammaticName.Replace("ControlType.", "").ToLowerInvariant();
    private static string Trim(string? value, int max) => value == null ? "" : value.Length <= max ? value : value[..max] + "…";

    private object Observe(JsonElement args, bool find)
    {
        var rootRef = args.Text("root_ref");
        ElementEntry? rootEntry = rootRef == null ? null : Resolve(rootRef);
        var window = GetWindow(args.Text("window_id") ?? rootEntry?.Window.Id);
        if (rootEntry != null && rootEntry.Window.Id != window.Id) throw new DesktopError("INVALID_ARGUMENT", "root_ref belongs to another window.");
        if (Native.IsIconic(window.Handle)) throw new DesktopError("WINDOW_MINIMIZED", "Restore the window with desktop_act focus_window before observing.");
        var root = rootEntry?.Element ?? AutomationElement.FromHandle(window.Handle);
        var depth = args.Number("depth", find ? 12 : 3, 1, 50);
        var limit = args.Number("limit", find ? 12 : 40, 1, 80);
        var role = args.Text("role") is string requestedRole ? NormalizeRole(requestedRole) : null;
        var name = args.Text("name");
        var exact = args.Flag("exact", true);
        var interactiveOnly = args.Flag("interactive_only", !find);
        var includeBounds = args.Flag("include_bounds");
        var scope = $"{(find ? "find" : "observe")}|{rootRef ?? "window"}|{depth}|{limit}|{role}|{name}|{exact}|{interactiveOnly}|{includeBounds}";
        var items = new List<UiItem>();
        var visited = 0;
        var matchesCount = 0;
        var pick = find ? args.Text("pick") ?? "all" : "all";
        var requestedIndex = args.Number("index", 0, 0, 699);
        var complete = true;
        var stopwatch = Stopwatch.StartNew();
        var budgetMs = args.Number("budget_ms", 2500, 200, 5000);
        var queue = new Queue<(AutomationElement Element, int Depth, string Context)>();
        queue.Enqueue((root, 0, ""));
        var walker = TreeWalker.ControlViewWalker;
        while (queue.Count != 0)
        {
            if (visited >= 700 || stopwatch.ElapsedMilliseconds > budgetMs) { complete = false; break; }
            var (element, level, context) = queue.Dequeue();
            try
            {
                visited++;
                var current = element.Current;
                var elementRole = Role(element);
                var elementName = current.Name;
                var isInteractive = IsInteractive(element);
                var roleMatches = role == null || elementRole == role;
                var nameMatches = name == null || (exact ? string.Equals(elementName, name, StringComparison.OrdinalIgnoreCase) : elementName.Contains(name, StringComparison.OrdinalIgnoreCase));
                var child = walker.GetFirstChild(element);
                var more = child != null && level >= depth;
                var include = roleMatches && nameMatches && (!find || ExtraFilters(element, args, exact, ref complete)) && (!interactiveOnly || isInteractive || more || level == 0);
                if (include)
                {
                    var matchIndex = matchesCount++;
                    var selected = pick switch { "count" => false, "first" => matchIndex == 0, "nth" => matchIndex == requestedIndex, _ => true };
                    if (pick == "last") items.Clear();
                    if (selected)
                    {
                        if (items.Count == limit) { complete = false; break; }
                        items.Add(ReadItem(AddRef(window, element), element, context, more, window, includeBounds));
                    }
                }
                if (level >= depth) { if (child != null) complete = false; continue; }
                var nextContext = string.IsNullOrWhiteSpace(elementName) || level == 0 ? context : Trim(string.IsNullOrEmpty(context) ? elementName : $"{context} / {elementName}", 200);
                while (child != null)
                {
                    if (queue.Count + visited >= 700 || stopwatch.ElapsedMilliseconds > budgetMs) { complete = false; break; }
                    queue.Enqueue((child, level + 1, nextContext));
                    child = walker.GetNextSibling(child);
                }
            }
            catch (ElementNotAvailableException) { complete = false; }
            catch (System.Runtime.InteropServices.COMException) { complete = false; }
            catch (DesktopError error) when (error.Code is "STALE_REF" or "PERMISSION_DENIED") { complete = false; }
        }
        if (find && pick == "last" && !complete) items.Clear();
        var snapshot = $"s-{epoch}-{++sequence}";
        observations[snapshot] = new(window.Id, scope, complete, items, DateTime.UtcNow);
        var since = args.Text("since");
        if (!find && since != null && observations.TryGetValue(since, out var previous) && previous.WindowId == window.Id && previous.Scope == scope && previous.Complete && complete && DateTime.UtcNow - previous.Created < TimeSpan.FromSeconds(30))
        {
            // Compare by native runtime identity, never by name or screen position.
            string Key(UiItem item) => references[item.Ref].RuntimeId;
            var oldItems = previous.Items.ToDictionary(Key);
            var newItems = items.ToDictionary(Key);
            var oldOrder = previous.Items.Select(Key).Where(newItems.ContainsKey);
            var newOrder = items.Select(Key).Where(oldItems.ContainsKey);
            if (!oldOrder.SequenceEqual(newOrder))
                return new { snapshot, window_id = window.Id, mode = "full", complete, items, visited, diff_reset = "Control order changed; replace the previous observation." };
            var changed = items.Where(item => !oldItems.TryGetValue(Key(item), out var old) || item with { Ref = "" } != old with { Ref = "" }).ToList();
            var unchanged = items.Where(item => oldItems.TryGetValue(Key(item), out var old) && item with { Ref = "" } == old with { Ref = "" }).ToList();
            var changedKeys = changed.Select(Key).ToHashSet();
            // Keep unchanged refs usable for the next two minutes, and retain those refs in the baseline.
            foreach (var item in unchanged)
            {
                var old = oldItems[Key(item)];
                var entry = references[old.Ref];
                references[old.Ref] = entry with { Expires = DateTime.UtcNow + RefLifetime };
                var index = items.IndexOf(item);
                items[index] = item with { Ref = old.Ref };
            }
            return new { snapshot, window_id = window.Id, mode = "diff", base_snapshot = since, complete, items = changed, removed = previous.Items.Where(item => !newItems.ContainsKey(Key(item)) || changedKeys.Contains(Key(item))).Select(item => item.Ref).ToArray(), unchanged = unchanged.Count, visited };
        }
        return new { snapshot, window_id = window.Id, mode = "full", complete, items, visited, match_count = find ? matchesCount : (int?)null, needs_narrowing = find && (!complete || pick != "count" && items.Count != 1), diff_reset = since != null ? "Baseline missing, incomplete, expired, or scope changed; use this full result." : null };
    }

    private static bool IsInteractive(AutomationElement element)
    {
        var type = element.Current.ControlType;
        return type == ControlType.Button || type == ControlType.Edit || type == ControlType.CheckBox || type == ControlType.ComboBox || type == ControlType.ListItem || type == ControlType.MenuItem || type == ControlType.RadioButton || type == ControlType.TabItem || type == ControlType.TreeItem || type == ControlType.Hyperlink || type == ControlType.Slider || type == ControlType.Spinner || type == ControlType.DataItem || type == ControlType.ScrollBar;
    }

    private static UiItem ReadItem(string reference, AutomationElement element, string context, bool more, WindowEntry window, bool includeBounds)
    {
        var current = element.Current;
        string? value = null;
        bool? check = null, selected = null;
        string? expanded = null;
        if (!current.IsPassword && element.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePattern)) value = Trim(((ValuePattern)valuePattern).Current.Value, 180);
        if (element.TryGetCurrentPattern(TogglePattern.Pattern, out var toggle)) check = ((TogglePattern)toggle).Current.ToggleState == ToggleState.On;
        if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selection)) selected = ((SelectionItemPattern)selection).Current.IsSelected;
        if (element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expand)) expanded = ((ExpandCollapsePattern)expand).Current.ExpandCollapseState.ToString().ToLowerInvariant();
        ItemBounds? bounds = null;
        if (includeBounds && Native.GetWindowRect(window.Handle, out var rect))
        {
            var area = current.BoundingRectangle;
            if (!area.IsEmpty) bounds = new((int)area.X - rect.Left, (int)area.Y - rect.Top, (int)area.Width, (int)area.Height);
        }
        return new(reference, Role(element), Trim(current.Name, 180), string.IsNullOrEmpty(context) ? null : context, value, current.IsEnabled ? null : false, current.IsOffscreen ? true : null, current.IsPassword ? true : null, check, selected, expanded, more ? true : null, bounds);
    }

    private object Act(JsonElement args)
    {
        var action = args.Text("action") ?? throw new DesktopError("INVALID_ARGUMENT", "action is required.");
        SelectCursorAgent(args);
        var timeout = args.Number("timeout_ms", 1800, 0, 5000);
        // Parse and resolve the postcondition before dispatch: malformed expectations never cause input.
        var expectation = args.TryGetProperty("expect", out var expect) ? ValidateExpectation(expect) : (Expectation?)null;
        ElementEntry? target = null;
        WindowEntry window;
        if (action == "focus_window" || action is "click_point" or "mouse_move" or "mouse_wheel" or "drag_point") window = GetWindow(args.Text("window_id"));
        else
        {
            target = Resolve(args.Text("ref"));
            window = target.Window;
            if (args.Text("window_id") is string requested && requested != window.Id) throw new DesktopError("INVALID_ARGUMENT", "Ref belongs to another window.");
        }
        var destinationWindow = action is "drag" or "drag_point" ? DragDestinationWindow(args, window) : null;
        if (expectation != null)
        {
            if (expectation.Ref != null)
            {
                var verifyTarget = Resolve(expectation.Ref);
                if (verifyTarget.Window.Id != window.Id && verifyTarget.Window.Id != destinationWindow?.Id) throw new DesktopError("INVALID_ARGUMENT", "Verification target must belong to the action's source or drag destination window.");
            }
            else if (target == null && expectation.Property != "window_title") throw new DesktopError("INVALID_ARGUMENT", "This expectation needs a ref.");
            // Unsupported readback patterns must fail before the requested action runs.
            _ = ReadExpectation(expectation, target, window);
        }
        var text = args.Text("text", 10000);
        var key = args.Text("key", 30);
        if ((action == "set_value" || action == "type_text") && text == null) throw new DesktopError("INVALID_ARGUMENT", "text is required.");
        if (action == "press_key" && key == null) throw new DesktopError("INVALID_ARGUMENT", "key is required.");
        if (target != null && (!target.Element.Current.IsEnabled || (target.Element.Current.IsOffscreen && action != "scroll_into_view"))) throw new DesktopError("NOT_ACTIONABLE", "Control is disabled or offscreen. Observe its current state.");
        if (target?.Element.Current.IsPassword == true) throw new DesktopError("PASSWORD_FIELD", "Password fields are excluded from this plugin.");
        Native.RequireInputPrivilege(window.Pid);
        if (target != null && target.ElementPid != window.Pid) Native.RequireInputPrivilege(target.ElementPid);
        if (target != null)
        {
            CursorOverlay.Highlight(target.Element.Current.BoundingRectangle, action is "focus" or "type_text" or "set_value" or "press_key", action is "click" or "double_click" or "triple_click", target.Element.Current.ControlType == ControlType.Button || target.Element.Current.ControlType == ControlType.Hyperlink || target.Element.Current.ControlType == ControlType.MenuItem);
            CursorOverlay.WaitBeforeAction();
            _ = Resolve(target.Ref); // Presentation delays cannot waive the normal identity guard.
        }
        var delivery = "sent";
        ControlPresentation.Check();
        switch (action)
        {
            case "focus_window":
                if (!Native.ActivateWindow(window.Handle)) throw new DesktopError("FOCUS_DENIED", "Windows did not grant foreground focus.");
                break;
            case "click":
                if (args.TryGetProperty("modifiers", out var pointerModifiers) && pointerModifiers.GetArrayLength() > 0) PhysicalElementAction(args, target!, action);
                else if (target!.Element.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke)) ((InvokePattern)invoke).Invoke();
                else LegacyDefaultAction(target);
                break;
            case "clear":
            case "set_value":
                if (action == "clear") text = "";
                if (!target!.Element.TryGetCurrentPattern(ValuePattern.Pattern, out _) && target.Element.TryGetCurrentPattern(RangeValuePattern.Pattern, out var rawRange))
                {
                    var range = (RangeValuePattern)rawRange;
                    if (range.IsReadOnly) throw new DesktopError("READ_ONLY", "Numeric control is read-only.");
                    if (!double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number) || number < range.Minimum || number > range.Maximum) throw new DesktopError("INVALID_ARGUMENT", "Numeric value must be finite and within the control's native range.");
                    if (range.Value == number) delivery = "already_satisfied"; else range.SetValue(number);
                    break;
                }
                var value = RequirePattern<ValuePattern>(target!, ValuePattern.Pattern);
                if (value.Current.IsReadOnly) throw new DesktopError("READ_ONLY", "The field is read-only.");
                if (value.Current.Value == text) delivery = "already_satisfied";
                else value.SetValue(text!);
                break;
            case "check":
            case "uncheck":
            case "toggle":
                var toggle = RequirePattern<TogglePattern>(target!, TogglePattern.Pattern);
                if (action == "toggle") { toggle.Toggle(); break; }
                var desired = action == "check" ? ToggleState.On : ToggleState.Off;
                if (toggle.Current.ToggleState == desired) delivery = "already_satisfied";
                else
                {
                    var seen = new HashSet<ToggleState>();
                    while (toggle.Current.ToggleState != desired && seen.Count < 3)
                    {
                        var previous = toggle.Current.ToggleState;
                        if (!seen.Add(previous)) break;
                        ControlPresentation.Check(); toggle.Toggle();
                        var transition = Stopwatch.StartNew();
                        while (toggle.Current.ToggleState == previous && transition.ElapsedMilliseconds < 500) ControlPresentation.Delay(30);
                        if (toggle.Current.ToggleState == previous) break;
                    }
                    if (toggle.Current.ToggleState != desired) throw new DesktopError("STATE_UNVERIFIED", "Toggle was sent but the requested state was not reached. Observe before another action.", new { delivery = "sent", retry = "observe_first" });
                }
                break;
            case "select":
                if (args.Text("option", 1000) is string option) delivery = SelectOption(target!, option);
                else if (target!.Element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var item)) ((SelectionItemPattern)item).Select();
                else if (target.Element.Current.ControlType == ControlType.MenuItem && target.Element.TryGetCurrentPattern(InvokePattern.Pattern, out var selectMenu)) ((InvokePattern)selectMenu).Invoke();
                else throw new DesktopError("ACTION_UNSUPPORTED", "Use an item ref, or pass option with a list/combobox container ref.");
                break;
            case "expand": RequirePattern<ExpandCollapsePattern>(target!, ExpandCollapsePattern.Pattern).Expand(); break;
            case "collapse": RequirePattern<ExpandCollapsePattern>(target!, ExpandCollapsePattern.Pattern).Collapse(); break;
            case "scroll_into_view": RequirePattern<ScrollItemPattern>(target!, ScrollItemPattern.Pattern).ScrollIntoView(); break;
            case "scroll":
                var direction = args.Text("direction");
                var amount = args.Text("amount") ?? "small";
                if (direction is not ("up" or "down" or "left" or "right") || amount is not ("small" or "large")) throw new DesktopError("INVALID_ARGUMENT", "Scroll needs a supported direction and amount.");
                var increment = amount == "large" ? ScrollAmount.LargeIncrement : ScrollAmount.SmallIncrement;
                var decrement = amount == "large" ? ScrollAmount.LargeDecrement : ScrollAmount.SmallDecrement;
                var scrolling = RequirePattern<ScrollPattern>(target!, ScrollPattern.Pattern);
                for (var unit = 0; unit < args.Number("steps", 1, 1, 30); unit++)
                {
                    _ = Resolve(target!.Ref);
                    ControlPresentation.Check(); scrolling.Scroll(direction == "right" ? increment : direction == "left" ? decrement : ScrollAmount.NoAmount, direction == "down" ? increment : direction == "up" ? decrement : ScrollAmount.NoAmount);
                }
                break;
            case "focus":
                EnsureForeground(window);
                target!.Element.SetFocus();
                break;
            case "type_text":
            case "press_key":
                EnsureForeground(window);
                var focused = AutomationElement.FocusedElement;
                if (focused == null || RuntimeId(focused) != target!.RuntimeId) throw new DesktopError("FOCUS_MISMATCH", "Focus is not on this ref. Use focus and verify before typing.");
                if (action == "type_text" && args.TryGetProperty("interval_ms", out _))
                {
                    var interval = args.Number("interval_ms", 1, 1, 2000);
                    foreach (var rune in text!.EnumerateRunes())
                    {
                        EnsureForeground(window);
                        if (AutomationElement.FocusedElement is not AutomationElement typingFocus || RuntimeId(typingFocus) != target!.RuntimeId) throw new DesktopError("FOCUS_CHANGED", "Typing was interrupted after partial delivery because focus changed. Inspect text before continuing.", new { delivery = "unknown" });
                        Native.TypeText(rune.ToString()); ControlPresentation.Delay(interval);
                    }
                }
                else if (action == "type_text") Native.TypeText(text!); else Native.KeyChord(key!, args.Flag("force"));
                break;
            case "right_click":
            case "double_click":
            case "triple_click":
            case "hover":
            case "drag":
                PhysicalElementAction(args, target!, action);
                break;
            case "click_point":
            case "mouse_move":
            case "mouse_wheel":
            case "drag_point": PointAction(args, window, action); break;
            default: throw new DesktopError("INVALID_ARGUMENT", "Unsupported action.");
        }
        if (action is "focus" or "type_text" or "set_value" or "press_key") CursorOverlay.Caret(Native.CaretRectangle(window.Handle));
        if (expectation == null) return new { delivery, verification = "not_requested", retry = "observe_first", window_id = window.Id };
        var timer = Stopwatch.StartNew();
        object? actual = null;
        do
        {
            actual = ReadExpectation(expectation, target, window);
            if (Equals(actual, expectation.Expected)) return new { delivery, verification = "passed", property = expectation.Property, actual = ShortActual(actual), window_id = window.Id };
            if (timer.ElapsedMilliseconds >= timeout) break;
            ControlPresentation.Delay(70);
        } while (true);
        return new { delivery, verification = "failed", property = expectation.Property, actual = ShortActual(actual), reason = "Postcondition was not observed before timeout. Do not repeat input without reading current state.", retry = "observe_first", window_id = window.Id };
    }

    private static T RequirePattern<T>(ElementEntry entry, AutomationPattern pattern) where T : class
    {
        if (!entry.Element.TryGetCurrentPattern(pattern, out var result)) throw new DesktopError("ACTION_UNSUPPORTED", "The control does not support this semantic action.");
        return (T)result;
    }
    private static void EnsureForeground(WindowEntry window)
    {
        if (!Native.ForegroundOwns(window.Handle)) throw new DesktopError("WINDOW_NOT_FOREGROUND", "Use focus_window explicitly before physical input or capture.");
    }
    private static object? ShortActual(object? actual) => actual is string text ? Trim(text, 180) : actual;
    private sealed record Expectation(string? Ref, string Property, object Expected);
    private Expectation ValidateExpectation(JsonElement expect)
    {
        var property = expect.Text("property") ?? throw new DesktopError("INVALID_ARGUMENT", "Expectation property is required.");
        if (!new[] { "value", "name", "checked", "selected", "expanded", "exists", "focused", "window_title" }.Contains(property)) throw new DesktopError("INVALID_ARGUMENT", "Unsupported expectation property.");
        if (!expect.TryGetProperty("equals", out var equal)) throw new DesktopError("INVALID_ARGUMENT", "Expectation equals is required.");
        var boolean = property is "checked" or "selected" or "exists" or "focused";
        object expected;
        if (boolean && equal.ValueKind is JsonValueKind.True or JsonValueKind.False) expected = equal.GetBoolean();
        else if (!boolean && equal.ValueKind == JsonValueKind.String && (equal.GetString()?.Length ?? 0) <= 10000) expected = equal.GetString()!;
        else throw new DesktopError("INVALID_ARGUMENT", "Expectation equals has the wrong type or length.");
        return new(expect.Text("ref"), property, expected);
    }
    private object? ReadExpectation(Expectation expect, ElementEntry? defaultTarget, WindowEntry window)
    {
        try
        {
            if (expect.Property == "exists") return ElementExists(expect.Ref ?? defaultTarget?.Ref ?? "");
            if (expect.Property == "window_title")
            {
                if (expect.Ref != null) window = Resolve(expect.Ref, false).Window;
                var buffer = new StringBuilder(1024);
                Native.GetWindowText(window.Handle, buffer, buffer.Capacity);
                return buffer.ToString();
            }
            var target = expect.Ref != null ? Resolve(expect.Ref, false) : Resolve(defaultTarget?.Ref, false);
            var element = target.Element;
            return expect.Property switch
            {
                "exists" => true,
                "focused" => AutomationElement.FocusedElement is AutomationElement focused && RuntimeId(focused) == target.RuntimeId,
                "name" => element.Current.Name,
                "value" => element.Current.IsPassword ? null : ReadValue(target),
                "checked" => RequirePattern<TogglePattern>(target, TogglePattern.Pattern).Current.ToggleState == ToggleState.On,
                "selected" => RequirePattern<SelectionItemPattern>(target, SelectionItemPattern.Pattern).Current.IsSelected,
                "expanded" => RequirePattern<ExpandCollapsePattern>(target, ExpandCollapsePattern.Pattern).Current.ExpandCollapseState.ToString().ToLowerInvariant(),
                _ => null
            };
        }
        catch (ElementNotAvailableException) { return expect.Property == "exists" ? false : null; }
        catch (DesktopError ex) when (ex.Code is "STALE_REF" or "WINDOW_NOT_FOUND" or "WINDOW_CHANGED") { return null; }
    }

    private object Capture(JsonElement args)
    {
        if (args.Text("display_id") is string displayId) return CaptureDisplay(args, displayId);
        var window = GetWindow(args.Text("window_id"));
        var mode = args.Text("mode") ?? "window";
        if (mode is not ("window" or "screen")) throw new DesktopError("INVALID_ARGUMENT", "Capture mode must be window or screen.");
        if (mode == "screen") EnsureForeground(window);
        if (Native.IsIconic(window.Handle) || !Native.GetWindowRect(window.Handle, out var rect) || rect.Width <= 0 || rect.Height <= 0) throw new DesktopError("CAPTURE_UNAVAILABLE", "Window is minimized or has no capture area.");
        var frameRect = mode == "window" ? WindowCapture.Bounds(window.Handle) : rect;
        var visible = new Rectangle(frameRect.Left, frameRect.Top, frameRect.Width, frameRect.Height);
        if (mode == "screen") visible = Rectangle.Intersect(visible, System.Windows.Forms.SystemInformation.VirtualScreen);
        if (visible.Width <= 0 || visible.Height <= 0) throw new DesktopError("CAPTURE_UNAVAILABLE", "Window is outside the visible desktop.");
        if ((long)visible.Width * visible.Height > 16000000) throw new DesktopError("CAPTURE_TOO_LARGE", "Choose a smaller window.");
        using var raw = mode == "window" ? WindowCapture.Capture(window.Handle) : new Bitmap(visible.Width, visible.Height, PixelFormat.Format24bppRgb);
        if (mode == "screen") { using var graphics = Graphics.FromImage(raw); graphics.CopyFromScreen(visible.Location, System.Drawing.Point.Empty, visible.Size); }
        else visible.Size = raw.Size;
        var fullSource = visible;
        if (args.TryGetProperty("region", out var region))
        {
            var x = region.Number("x", 0, 0, 32768);
            var y = region.Number("y", 0, 0, 32768);
            var width = region.Number("width", 0, 1, 8192);
            var height = region.Number("height", 0, 1, 8192);
            var crop = new Rectangle(rect.Left + x, rect.Top + y, width, height);
            if (!visible.Contains(crop)) throw new DesktopError("INVALID_ARGUMENT", "Capture region must be inside the visible window rectangle.");
            visible = crop;
        }
        if ((long)visible.Width * visible.Height > 16000000) throw new DesktopError("CAPTURE_TOO_LARGE", "Choose a smaller capture region.");
        var maxDimension = args.Number("max_dimension", 1000, 200, 1600);
        using var bitmap = raw.Clone(new Rectangle(visible.X - fullSource.X, visible.Y - fullSource.Y, visible.Width, visible.Height), raw.PixelFormat);
        var scale = Math.Min(1.0, (double)maxDimension / Math.Max(bitmap.Width, bitmap.Height));
        var imageWidth = Math.Max(1, (int)Math.Round(bitmap.Width * scale));
        var imageHeight = Math.Max(1, (int)Math.Round(bitmap.Height * scale));
        using var resized = new Bitmap(bitmap, imageWidth, imageHeight);
        using var stream = new MemoryStream();
        resized.Save(stream, ImageFormat.Png);
        if (!Native.GetWindowRect(window.Handle, out var after) || !SameRect(rect, after)) throw new DesktopError("WINDOW_CHANGED", "Window geometry changed during capture. Capture again.");
        var id = $"c-{epoch}-{++sequence}";
        var captureRect = new Native.Rect { Left = visible.Left, Top = visible.Top, Right = visible.Right, Bottom = visible.Bottom };
        captures[id] = new(window, captureRect, imageWidth, imageHeight, DateTime.UtcNow);
        windowGeometry[id] = rect;
        return new { capture_id = id, window_id = window.Id, mode, width = imageWidth, height = imageHeight, source_region = new { x = visible.Left - rect.Left, y = visible.Top - rect.Top, width = visible.Width, height = visible.Height }, expires_in_ms = 10000, image = Convert.ToBase64String(stream.ToArray()), note = mode == "window" ? "Window pixels from Windows.Graphics.Capture, including when occluded. Physical input still requires foreground and an unobscured point." : "Visible screen pixels; overlays can appear. Point clicks use image coordinates, require foreground, and consume this capture." };
    }

    private Native.Point CapturePoint(JsonElement args, WindowEntry window)
    {
        var id = args.Text("capture_id");
        var point = MapCapturePoint(id, window, args.Number("x", -1, 0, 1599), args.Number("y", -1, 0, 1599), true);
        ConsumeCapture(id!);
        return point;
    }
    private void ConsumeCapture(string id) { captures.Remove(id); windowGeometry.Remove(id); }
    private Native.Point MapCapturePoint(string? id, WindowEntry window, int x, int y, bool foreground)
    {
        if (id == null || !captures.TryGetValue(id, out var capture) || capture.Window != null && capture.Window.Id != window.Id || DateTime.UtcNow - capture.Created > TimeSpan.FromSeconds(10)) throw new DesktopError("STALE_CAPTURE", "Capture is missing, expired, used, or belongs to another window. Capture again.");
        if (foreground) EnsureForeground(window);
        if (!Native.GetWindowRect(window.Handle, out var current)) throw new DesktopError("WINDOW_CHANGED", "Window geometry is unavailable.");
        Native.Rect original;
        if (capture.Window != null)
        {
            if (!windowGeometry.TryGetValue(id, out original) || !SameRect(current, original)) throw new DesktopError("WINDOW_CHANGED", "Window moved or resized after capture. Capture again.");
        }
        else
        {
            var display = System.Windows.Forms.Screen.AllScreens.FirstOrDefault(item => item.DeviceName == capture.DisplayId);
            if (display == null || !windowGeometry.TryGetValue(id, out var displayBounds) || display.Bounds.Left != displayBounds.Left || display.Bounds.Top != displayBounds.Top || display.Bounds.Width != displayBounds.Width || display.Bounds.Height != displayBounds.Height) throw new DesktopError("DISPLAY_CHANGED", "Display geometry changed. Capture again.");
            if (capture.WindowBounds == null || !capture.WindowBounds.TryGetValue(window.Id, out original) || !SameRect(current, original)) throw new DesktopError("WINDOW_CHANGED", "Selected window was not in this display capture or its geometry changed. Capture again.");
        }
        if (x < 0 || y < 0 || x >= capture.ImageWidth || y >= capture.ImageHeight) throw new DesktopError("INVALID_ARGUMENT", "Point must be inside the returned image.");
        var screenX = capture.Rect.Left + (int)((x + 0.5) * capture.Rect.Width / capture.ImageWidth);
        var screenY = capture.Rect.Top + (int)((y + 0.5) * capture.Rect.Height / capture.ImageHeight);
        var hit = Native.GetAncestor(Native.WindowFromPoint(new Native.Point(screenX, screenY)), 2);
        if (hit != window.Handle) throw new DesktopError("POINT_OBSCURED", "The point is covered by another window. Capture the current UI again.");
        return new Native.Point(screenX, screenY);
    }
    private readonly Dictionary<string, Native.Rect> windowGeometry = new();
    private static bool SameRect(Native.Rect a, Native.Rect b) => a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    private void Prune()
    {
        var now = DateTime.UtcNow;
        foreach (var token in watches.Where(pair => now - pair.Value.Created > TimeSpan.FromMinutes(1)).Select(pair => pair.Key).ToArray()) RemoveWatch(token);
        foreach (var key in references.Where(pair => pair.Value.Expires < now).Select(pair => pair.Key).ToArray()) references.Remove(key);
        foreach (var key in observations.Where(pair => now - pair.Value.Created > TimeSpan.FromSeconds(30)).Select(pair => pair.Key).ToArray()) observations.Remove(key);
        foreach (var key in captures.Where(pair => now - pair.Value.Created > TimeSpan.FromSeconds(10)).Select(pair => pair.Key).ToArray()) { captures.Remove(key); windowGeometry.Remove(key); }
        if (references.Count > 4000) { references.Clear(); observations.Clear(); }
        if (observations.Count > 16) observations.Remove(observations.Keys.First());
        if (captures.Count > 4) { var key = captures.Keys.First(); captures.Remove(key); windowGeometry.Remove(key); }
    }
}

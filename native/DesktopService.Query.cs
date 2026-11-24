using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Automation;

namespace AgyComputerUse;

internal sealed partial class DesktopService
{
    private object Query(JsonElement args)
    {
        var operation = args.Text("operation") ?? "get";
        if (operation == "focused")
        {
            var window = GetWindow(args.Text("window_id"));
            var element = AutomationElement.FocusedElement;
            if (element == null) throw new DesktopError("FOCUS_MISMATCH", "No current UIA focus was available.");
            var parent = element;
            IntPtr nativeRoot = IntPtr.Zero;
            for (var level = 0; level < 20 && parent != null; level++)
            {
                if (parent.Current.NativeWindowHandle != 0) { nativeRoot = Native.GetAncestor(new IntPtr(parent.Current.NativeWindowHandle), 2); break; }
                parent = TreeWalker.ControlViewWalker.GetParent(parent);
            }
            if (nativeRoot != window.Handle) throw new DesktopError("FOCUS_MISMATCH", "Focus belongs to another window of this app.");
            return new { item = ReadItem(AddRef(window, element), element, "", false, window, false) };
        }
        if (operation == "surface_wait") return WaitSurface(args);
        if (operation == "text_wait") return WaitText(args);
        if (operation == "time_wait") { var clock = Stopwatch.StartNew(); Thread.Sleep(args.Number("timeout_ms", 1000, 0, 10000)); return new { verification = "passed", elapsed_ms = clock.ElapsedMilliseconds }; }
        if (operation == "notification_wait") return WaitNotification(args);
        if (operation == "watch") return CreateWatch(args);
        if (operation == "wait_event") return WaitEvent(args);
        var property = args.Text("property") ?? "exists";
        var limit = args.Number("limit", 1000, 1, 12000);
        if (operation == "window_wait") return WaitWindow(args);
        if (operation is not ("get" or "is" or "wait")) throw new DesktopError("INVALID_ARGUMENT", "Unknown query operation.");
        var reference = args.Text("ref") ?? throw new DesktopError("INVALID_ARGUMENT", "This query needs a ref.");
        var properties = new[] { "exists", "visible", "actionable", "value", "name", "role", "automation_id", "class_name", "process_id", "help_text", "enabled", "offscreen", "focused", "checked", "selected", "selected_items", "expanded", "readonly", "text", "selected_text", "bounds", "actions", "scroll_vertical", "scroll_horizontal" };
        if (!properties.Contains(property)) throw new DesktopError("INVALID_ARGUMENT", "Unknown control property.");
        if (operation != "get" && !args.TryGetProperty("equals", out _) && property != "exists") throw new DesktopError("INVALID_ARGUMENT", "is/wait require equals for this property.");
        var timeout = operation == "wait" ? args.Number("timeout_ms", 3000, 0, 10000) : 0;
        var timer = Stopwatch.StartNew();
        object? value;
        bool matches;
        do
        {
            value = ReadProperty(reference, property, args.Text("action"));
            matches = Matches(value, args);
            if (operation == "get" || matches || timer.ElapsedMilliseconds >= timeout) break;
            Thread.Sleep(60);
        } while (true);
        var text = value as string;
        return new { property, value = text == null ? value : Trim(text, limit), complete = text == null || text.Length <= limit, matches = operation == "get" ? (bool?)null : matches, verification = operation == "get" ? null : matches ? "passed" : "failed", elapsed_ms = timer.ElapsedMilliseconds };
    }

    private object? ReadProperty(string reference, string property, string? action = null)
    {
        if (property == "exists") return ElementExists(reference);
        ElementEntry entry;
        try { entry = Resolve(reference, false); }
        catch (ElementNotAvailableException) { if (property == "exists") return false; throw; }
        catch (DesktopError ex) when (ex.Code is "STALE_REF" or "WINDOW_NOT_FOUND" or "WINDOW_CHANGED") { if (property == "exists") return false; throw; }
        var element = entry.Element;
        var current = element.Current;
        if (current.IsPassword && property is "value" or "text" or "selected_text") throw new DesktopError("PASSWORD_FIELD", "Password content is excluded from queries.");
        return property switch
        {
            "exists" => true,
            "visible" => !current.IsOffscreen && !current.BoundingRectangle.IsEmpty && current.BoundingRectangle.Width > 0 && current.BoundingRectangle.Height > 0,
            "actionable" => IsActionable(entry, action ?? "click"),
            "value" => ReadValue(entry),
            "name" => current.Name,
            "role" => Role(element),
            "automation_id" => current.AutomationId,
            "class_name" => current.ClassName,
            "process_id" => current.ProcessId,
            "help_text" => current.HelpText,
            "enabled" => current.IsEnabled,
            "offscreen" => current.IsOffscreen,
            "focused" => AutomationElement.FocusedElement is AutomationElement focus && RuntimeId(focus) == entry.RuntimeId,
            "checked" => RequirePattern<TogglePattern>(entry, TogglePattern.Pattern).Current.ToggleState == ToggleState.On,
            "selected" => RequirePattern<SelectionItemPattern>(entry, SelectionItemPattern.Pattern).Current.IsSelected,
            "selected_items" => RequirePattern<SelectionPattern>(entry, SelectionPattern.Pattern).Current.GetSelection().Take(30).Select(item => ReadItem(AddRef(entry.Window, item), item, current.Name, false, entry.Window, false)).ToArray(),
            "expanded" => RequirePattern<ExpandCollapsePattern>(entry, ExpandCollapsePattern.Pattern).Current.ExpandCollapseState.ToString().ToLowerInvariant(),
            "readonly" => ReadOnly(element),
            "text" => ReadDocumentText(entry),
            "selected_text" => string.Join("", RequirePattern<TextPattern>(entry, TextPattern.Pattern).GetSelection().Select(range => range.GetText(12000))),
            "bounds" => ElementBounds(entry),
            "actions" => element.GetSupportedPatterns().Select(pattern => pattern.ProgrammaticName.Replace("PatternIdentifiers.Pattern", "").Replace("Pattern.Pattern", "")).ToArray(),
            "scroll_vertical" => RequirePattern<ScrollPattern>(entry, ScrollPattern.Pattern).Current.VerticalScrollPercent,
            "scroll_horizontal" => RequirePattern<ScrollPattern>(entry, ScrollPattern.Pattern).Current.HorizontalScrollPercent,
            _ => null
        };
    }
    private static object ElementBounds(ElementEntry entry)
    {
        var rect = entry.Element.Current.BoundingRectangle;
        Native.GetWindowRect(entry.Window.Handle, out var window);
        return new { x = (int)rect.X - window.Left, y = (int)rect.Y - window.Top, width = (int)rect.Width, height = (int)rect.Height };
    }
    private static bool ReadOnly(AutomationElement element)
    {
        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var value)) return ((ValuePattern)value).IsReadOnly;
        if (element.TryGetCurrentPattern(RangeValuePattern.Pattern, out var range)) return ((RangeValuePattern)range).IsReadOnly;
        throw new DesktopError("ACTION_UNSUPPORTED", "Control exposes no readable editability pattern.");
    }
    private static bool IsActionable(ElementEntry entry, string action)
    {
        var element = entry.Element; var current = element.Current; var rect = current.BoundingRectangle;
        if (!current.IsEnabled || current.IsOffscreen || current.IsPassword || rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0) return false;
        if (action is "set_value" or "clear") { try { return !ReadOnly(element) && (action != "clear" || element.TryGetCurrentPattern(ValuePattern.Pattern, out _)); } catch (DesktopError) { return false; } }
        if (action == "type_text") return Native.ForegroundOwns(entry.Window.Handle) && AutomationElement.FocusedElement is AutomationElement focused && RuntimeId(focused) == entry.RuntimeId && NativeBoolean(element, 30009) == true && FilterState(element, "readonly") == false;
        if (element.TryGetCurrentPattern(InvokePattern.Pattern, out _)) return true;
        if (NativeProperty(element, 30100) is not string primary || string.IsNullOrWhiteSpace(primary)) return false;
        try { var point = ElementPoint(entry, false); return RuntimeId(AutomationElement.FromPoint(new System.Windows.Point(point.X, point.Y))) == entry.RuntimeId; }
        catch (DesktopError) { return false; }
    }
    private static string ReadValue(ElementEntry entry) => ReadElementValue(entry.Element);
    private static string ReadElementValue(AutomationElement element)
    {
        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var value)) return ((ValuePattern)value).Current.Value;
        if (element.TryGetCurrentPattern(RangeValuePattern.Pattern, out var range)) return ((RangeValuePattern)range).Current.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        if (element.Current.ControlType == ControlType.ComboBox && element.TryGetCurrentPattern(SelectionPattern.Pattern, out var selection))
        {
            var selected = ((SelectionPattern)selection).Current.GetSelection().ToArray();
            if (selected.Length == 1) return selected[0].Current.Name;
        }
        throw new DesktopError("ACTION_UNSUPPORTED", "This control does not expose a readable value or a single combobox selection.");
    }
    private static string ReadDocumentText(ElementEntry entry)
    {
        if (entry.Element.TryGetCurrentPattern(TextPattern.Pattern, out var text)) return ((TextPattern)text).DocumentRange.GetText(12001);
        if (entry.Element.TryGetCurrentPattern(ValuePattern.Pattern, out var value)) return ((ValuePattern)value).Current.Value;
        return entry.Element.Current.Name;
    }
    private bool ElementExists(string reference)
    {
        if (!references.TryGetValue(reference, out var entry) || entry.Expires < DateTime.UtcNow) throw new DesktopError("STALE_REF", "An unknown or expired ref cannot prove that a control is absent. Observe again.");
        if (!Native.IsWindow(entry.Window.Handle)) return false;
        try
        {
            GetWindow(entry.Window.Id);
            return RuntimeId(entry.Element) == entry.RuntimeId && entry.Element.Current.ProcessId == entry.ElementPid && ProcessStarted(entry.ElementPid) == entry.ElementStarted;
        }
        catch (ElementNotAvailableException) { return false; }
        catch (DesktopError error) when (error.Code is "WINDOW_NOT_FOUND" or "WINDOW_CHANGED") { return false; }
    }
    private static bool Matches(object? value, JsonElement args)
    {
        if (!args.TryGetProperty("equals", out var expected)) return value is true;
        var predicate = args.Text("predicate") ?? "equals";
        if (predicate == "contains") return value is string actual && expected.ValueKind == JsonValueKind.String && actual.Contains(expected.GetString()!, StringComparison.Ordinal);
        if (predicate is not ("equals" or "not_equals")) throw new DesktopError("INVALID_ARGUMENT", "Unsupported predicate.");
        var equal = expected.ValueKind switch
        {
            JsonValueKind.String => value is string text && text == expected.GetString(),
            JsonValueKind.True or JsonValueKind.False => value is bool boolean && boolean == expected.GetBoolean(),
            JsonValueKind.Number => value is int or uint or long or ulong or float or double or decimal && Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture) == expected.GetDouble(),
            _ => false
        };
        return predicate == "not_equals" ? !equal : equal;
    }
    private object WaitWindow(JsonElement args)
    {
        var query = args.Text("query") ?? throw new DesktopError("INVALID_ARGUMENT", "window_wait needs a title/app query.");
        var present = args.Flag("present", true);
        var timeout = args.Number("timeout_ms", 3000, 0, 10000);
        var timer = Stopwatch.StartNew();
        JsonElement result;
        do
        {
            result = JsonSerializer.SerializeToElement(ListWindows(JsonSerializer.SerializeToElement(new { query, limit = 60 })), Program.Json);
            var count = result.GetProperty("total").GetInt32();
            if ((count > 0) == present) return new { verification = "passed", windows = result.GetProperty("windows"), elapsed_ms = timer.ElapsedMilliseconds };
            if (timer.ElapsedMilliseconds >= timeout) break;
            Thread.Sleep(80);
        } while (true);
        return new { verification = "failed", windows = result.GetProperty("windows"), elapsed_ms = timer.ElapsedMilliseconds, reason = "Window condition was not observed before timeout." };
    }
    private object WaitSurface(JsonElement args)
    {
        var kind = args.Text("surface") ?? throw new DesktopError("INVALID_ARGUMENT", "surface_wait needs a surface kind.");
        var owner = args.Text("window_id") is string id ? GetWindow(id) : null;
        var timeout = args.Number("timeout_ms", 3000, 0, 10000);
        var present = args.Flag("present", true);
        var timer = Stopwatch.StartNew();
        JsonElement[] matches;
        do
        {
            var surfaces = JsonSerializer.SerializeToElement(ListSurfaces(JsonSerializer.SerializeToElement(new { window_id = owner?.Id }, Program.Json)), Program.Json);
            matches = surfaces.GetProperty("surfaces").EnumerateArray().Where(item => (item.GetProperty("kind").GetString() == kind || item.TryGetProperty("surface", out var subtype) && subtype.GetString() == kind) && (owner == null || item.GetProperty("window").GetProperty("pid").GetInt32() == owner.Pid)).Select(item => item.Clone()).ToArray();
            if ((matches.Length > 0) == present && (present || surfaces.GetProperty("complete").GetBoolean())) return new { verification = "passed", surfaces = matches, elapsed_ms = timer.ElapsedMilliseconds };
            if (timer.ElapsedMilliseconds >= timeout) break;
            Thread.Sleep(60);
        } while (true);
        return new { verification = "failed", surfaces = matches, elapsed_ms = timer.ElapsedMilliseconds, reason = "Surface condition was not observed before timeout." };
    }

    private object Batch(JsonElement args)
    {
        var commands = args.GetProperty("commands");
        if (commands.ValueKind != JsonValueKind.Array || commands.GetArrayLength() is < 1 or > 15) throw new DesktopError("INVALID_ARGUMENT", "Batch needs 1-15 prevalidated commands.");
        foreach (var command in commands.EnumerateArray())
        {
            var method = command.Text("method");
            if (method is not ("windows" or "observe" or "find" or "act" or "query" or "system" or "capture")) throw new DesktopError("INVALID_ARGUMENT", "This method cannot be batched.");
            ValidateCommand(method, command.GetProperty("params"));
        }
        if (commands.EnumerateArray().Count(command => command.Text("method") == "capture") > 4) throw new DesktopError("INVALID_ARGUMENT", "A batch can contain at most four captures.");
        var stop = args.Flag("stop_on_error", true);
        var timeout = args.Number("timeout_ms", 8000, 100, 10000);
        var timer = Stopwatch.StartNew();
        var results = new List<object>();
        var index = 0;
        foreach (var command in commands.EnumerateArray())
        {
            if (timer.ElapsedMilliseconds >= timeout) break;
            var parameters = JsonNode.Parse(command.GetProperty("params").GetRawText())!.AsObject();
            var method = command.Text("method")!;
            if (method is "act" or "query") parameters["timeout_ms"] = Math.Min(parameters["timeout_ms"]?.GetValue<int>() ?? (method == "act" ? 1800 : 3000), Math.Max(0, timeout - (int)timer.ElapsedMilliseconds));
            try
            {
                var data = Dispatch(method, JsonSerializer.SerializeToElement(parameters));
                var json = JsonSerializer.SerializeToElement(data, Program.Json);
                var failed = json.TryGetProperty("verification", out var verification) && verification.GetString() == "failed";
                results.Add(new { index, ok = !failed, data });
                index++;
                if (failed && stop) break;
            }
            catch (DesktopError error)
            {
                results.Add(new { index, ok = false, error = new { code = error.Code, message = error.Message, retry = "observe_first" } });
                index++;
                if (stop) break;
            }
            catch
            {
                results.Add(new { index, ok = false, error = new { code = "DESKTOP_ERROR", message = "Command outcome is unknown. Observe before retrying.", retry = "observe_first" } });
                index++;
                break;
            }
        }
        return new { results, completed = index, not_started = commands.GetArrayLength() - index, elapsed_ms = timer.ElapsedMilliseconds, atomic = false };
    }
}

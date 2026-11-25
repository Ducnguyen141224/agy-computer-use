using System.Text.Json;

namespace AgyComputerUse;

internal sealed partial class DesktopService
{
    // Native callers (C ABI/CLI) receive the same pre-dispatch guarantees as the MCP adapter.
    private static void ValidateCommand(string method, JsonElement args)
    {
        SchemaValidation.Command(method, args);
        if (args.ValueKind != JsonValueKind.Object) throw new DesktopError("INVALID_ARGUMENT", "Command arguments must be an object.");
        if (method == "capture")
        {
            if (args.TryGetProperty("window_id", out _) == args.TryGetProperty("display_id", out _) || args.TryGetProperty("display_id", out _) && args.TryGetProperty("mode", out _)) throw new DesktopError("INVALID_ARGUMENT", "Capture needs exactly one window_id or display_id; display capture does not take mode.");
            if (args.Flag("annotate") && args.TryGetProperty("display_id", out _)) throw new DesktopError("INVALID_ARGUMENT", "Annotated Set-of-Marks capture requires window_id.");
            _ = args.Number("max_marks", 40, 1, 60);
        }
        void Require(params string[] fields) { foreach (var field in fields) if (!args.TryGetProperty(field, out _)) throw new DesktopError("INVALID_ARGUMENT", $"{field} is required for this operation."); }
        if (method == "windows") { _ = args.Number("limit", 30, 1, 60); _ = args.Text("query"); }
        if (method == "read")
        {
            var operation = args.Text("operation");
            if (operation == "snapshot" && ((args.Text("window_id") == null && args.Text("root_ref") == null) || new[] { "reader_id", "direction", "max_pages", "pause_ms" }.Any(key => args.TryGetProperty(key, out _)))) throw new DesktopError("INVALID_ARGUMENT", "snapshot needs window_id or root_ref and no collection fields.");
            if (operation == "collect" && ((args.Text("root_ref") != null) == (args.Text("reader_id") != null) || args.Text("window_id") != null || args.Text("reader_id") != null && args.Text("direction") != null)) throw new DesktopError("INVALID_ARGUMENT", "collect needs exactly one root_ref or reader_id; continuation direction is fixed.");
        }
        if (method == "act")
        {
            var action = args.Text("action") ?? "";
            if (!new[] { "click", "right_click", "double_click", "triple_click", "hover", "drag", "set_value", "clear", "check", "uncheck", "toggle", "select", "expand", "collapse", "scroll", "scroll_into_view", "focus", "focus_window", "type_text", "press_key", "click_point", "mouse_move", "mouse_wheel", "drag_point", "click_mark" }.Contains(action)) throw new DesktopError("INVALID_ARGUMENT", "Unsupported action.");
            var point = new[] { "click_point", "mouse_move", "mouse_wheel", "drag_point" }.Contains(action);
            _ = Native.ValidatePointerModifiers(args);
            if (args.TryGetProperty("modifiers", out _) && action is not ("click" or "right_click" or "double_click" or "triple_click" or "click_point" or "mouse_wheel" or "drag" or "drag_point" or "click_mark")) throw new DesktopError("INVALID_ARGUMENT", "Modifiers apply to click, wheel or drag gestures.");
            Require(point || action is "focus_window" or "click_mark" ? "window_id" : "ref");
            if (point) { Require("capture_id", "x", "y"); _ = args.Number("x", 0, 0, 1599); _ = args.Number("y", 0, 0, 1599); }
            if (action == "click_mark") { Require("capture_id", "mark"); _ = args.Number("mark", 1, 1, 60); }
            else if (args.TryGetProperty("mark", out _)) throw new DesktopError("INVALID_ARGUMENT", "mark applies to click_mark only.");
            if (args.Text("observe_after") is string observeAfter && observeAfter is not ("none" or "diff" or "snapshot")) throw new DesktopError("INVALID_ARGUMENT", "Unsupported observe_after mode.");
            _ = args.Number("settle_ms", 120, 0, 1500);
            if (action is "drag" or "drag_point")
            {
                if (args.TryGetProperty("to_ref", out _))
                {
                    if (new[] { "to_window_id", "to_capture_id", "to_x", "to_y" }.Any(key => args.TryGetProperty(key, out _))) throw new DesktopError("INVALID_ARGUMENT", "Choose a destination ref or capture point, not both.");
                }
                else
                {
                    Require("to_x", "to_y");
                    if (action == "drag" || args.TryGetProperty("to_window_id", out _) || args.TryGetProperty("to_capture_id", out _)) Require("to_window_id", "to_capture_id");
                }
            }
            if (action is "type_text" or "set_value") { Require("text"); _ = args.Text("text", 10000); }
            if (args.TryGetProperty("interval_ms", out _) && (action != "type_text" || (long)(args.Text("text", 10000) ?? "").EnumerateRunes().Count() * args.Number("interval_ms", 1, 1, 2000) > 8000)) throw new DesktopError("INVALID_ARGUMENT", "interval_ms applies to type_text with a total typing delay at most 8000 ms.");
            if (args.TryGetProperty("interval_ms", out _) && (long)(args.Text("text", 10000) ?? "").EnumerateRunes().Count() * args.Number("interval_ms", 1, 1, 2000) + args.Number("timeout_ms", 1800, 0, 5000) > 10000) throw new DesktopError("INVALID_ARGUMENT", "Typing delay plus verification timeout must fit 10000 ms.");
            if (action == "press_key") { Require("key"); Native.ValidateChord(args.Text("key", 50) ?? "", args.Flag("force")); }
            if (action == "scroll") { Require("direction"); if (args.Text("direction") is not ("up" or "down" or "left" or "right")) throw new DesktopError("INVALID_ARGUMENT", "Unsupported scroll direction."); }
            if (args.TryGetProperty("steps", out _) && action != "scroll") throw new DesktopError("INVALID_ARGUMENT", "steps applies to semantic scroll only.");
            if (args.TryGetProperty("expect", out var expect))
            {
                var property = expect.Text("property") ?? "";
                var boolean = property is "checked" or "selected" or "exists" or "focused";
                if (!new[] { "value", "name", "checked", "selected", "expanded", "exists", "focused", "window_title" }.Contains(property) || !expect.TryGetProperty("equals", out var value) || (boolean ? value.ValueKind is not (JsonValueKind.True or JsonValueKind.False) : value.ValueKind != JsonValueKind.String)) throw new DesktopError("INVALID_ARGUMENT", "Invalid postcondition.");
                if (!boolean && value.GetString()!.Length > 10000) throw new DesktopError("INVALID_ARGUMENT", "Postcondition string is too long.");
            }
            _ = args.Number("timeout_ms", 1800, 0, 5000);
            _ = args.Number("duration_ms", 350, 80, 2000);
            _ = args.Number("drop_delay_ms", 500, 0, 2000);
            _ = args.Number("count", 1, 1, 3);
            _ = args.Number("vertical", 0, -2400, 2400); _ = args.Number("horizontal", 0, -2400, 2400);
            if (args.Text("button") is string button && button is not ("left" or "right" or "middle")) throw new DesktopError("INVALID_ARGUMENT", "Unsupported button.");
            if (args.Text("amount") is string amount && amount is not ("small" or "large")) throw new DesktopError("INVALID_ARGUMENT", "Unsupported scroll amount.");
        }
        if (method == "find" && (args.Text("pick") == "nth" ? !args.TryGetProperty("index", out _) : args.TryGetProperty("index", out _))) throw new DesktopError("INVALID_ARGUMENT", "index is required only for pick=nth.");
        if (method is "observe" or "find")
        {
            if (!args.TryGetProperty("window_id", out _) && !args.TryGetProperty("root_ref", out _)) throw new DesktopError("INVALID_ARGUMENT", "window_id or root_ref is required.");
            _ = args.Number("depth", 3, 1, 50); _ = args.Number("limit", 40, 1, 80); _ = args.Number("budget_ms", 2500, 200, 5000);
            if (method == "find" && !new[] { "role", "name", "value", "automation_id", "class_name", "text", "description", "states" }.Any(key => args.TryGetProperty(key, out _))) throw new DesktopError("INVALID_ARGUMENT", "find requires a search filter.");
        }
        if (method == "query")
        {
            var operation = args.Text("operation") ?? "get";
            if (!new[] { "get", "is", "wait", "focused", "window_wait", "surface_wait", "notification_wait", "text_wait", "time_wait", "watch", "wait_event" }.Contains(operation)) throw new DesktopError("INVALID_ARGUMENT", "Unsupported query operation.");
            if (operation == "notification_wait") _ = args.Number("limit", 30, 1, 80);
            if (operation == "notification_wait" && args.Flag("new_only") && !args.Flag("present", true)) throw new DesktopError("INVALID_ARGUMENT", "new_only waits for an arrival; omit present=false.");
            if (operation == "focused") Require("window_id");
            if (operation is "get" or "is" or "wait") Require("ref");
            if (operation is "get" or "is" or "wait")
            {
                var property = args.Text("property") ?? "exists";
                if (!new[] { "exists", "visible", "actionable", "value", "name", "role", "automation_id", "class_name", "process_id", "help_text", "enabled", "offscreen", "focused", "checked", "selected", "selected_items", "expanded", "readonly", "text", "selected_text", "bounds", "actions", "scroll_vertical", "scroll_horizontal" }.Contains(property)) throw new DesktopError("INVALID_ARGUMENT", "Unknown property.");
                if (operation != "get" && property != "exists") Require("equals");
            }
            if (operation == "watch") Require("event");
            if (operation == "watch" && args.Text("event") is not ("window_opened" or "window_closed" or "structure_changed" or "focus_changed" or "app_launched" or "app_terminated" or "surface_appeared" or "surface_dismissed")) throw new DesktopError("INVALID_ARGUMENT", "Unknown event kind.");
            if (operation == "wait_event") Require("token");
            if (operation == "window_wait") Require("query");
            if (operation == "surface_wait") Require("surface");
            if (operation == "text_wait")
            {
                Require("text");
                if (!args.TryGetProperty("window_id", out _) && !args.TryGetProperty("root_ref", out _)) throw new DesktopError("INVALID_ARGUMENT", "text_wait needs window_id or root_ref.");
                if (args.TryGetProperty("count", out _) && args.TryGetProperty("present", out _)) throw new DesktopError("INVALID_ARGUMENT", "Use count or present, not both.");
            }
            _ = args.Number("timeout_ms", 3000, 0, 10000); _ = args.Number("limit", 1000, 1, 12000);
            if (args.Text("predicate") is string predicate && predicate is not ("equals" or "not_equals" or "contains")) throw new DesktopError("INVALID_ARGUMENT", "Unsupported predicate.");
        }
        if (method == "system")
        {
            var operation = args.Text("operation") ?? "status";
            if (!new[] { "status", "permissions", "version", "list_displays", "list_apps", "launch", "clipboard_get", "clipboard_set", "clipboard_clear", "clipboard_save", "clipboard_restore", "list_surfaces", "open_surface", "close_surface", "list_notifications", "dismiss_notification", "dismiss_all_notifications", "notification_action", "window_info", "focus_window", "move", "resize", "minimize", "maximize", "restore", "close_window", "close_app", "cursor_config", "cursor_status", "cursor_remove", "control_status", "control_stop", "control_resume", "control_start", "control_end" }.Contains(operation)) throw new DesktopError("INVALID_ARGUMENT", "Unsupported system operation.");
            if (new[] { "window_info", "focus_window", "move", "resize", "minimize", "maximize", "restore", "close_window", "close_app", "close_surface" }.Contains(operation)) Require("window_id");
            if (operation == "launch") { Require("app"); _ = args.Text("app", 2048); }
            if (operation == "launch")
            {
                if (args.Text("app", 2048)!.Contains('\0')) throw new DesktopError("INVALID_ARGUMENT", "Executable name cannot contain NUL.");
                if (args.Text("cwd", 2048) is string directory && (!System.IO.Path.IsPathFullyQualified(directory) || !System.IO.Directory.Exists(directory))) throw new DesktopError("INVALID_ARGUMENT", "cwd must be an existing absolute directory.");
                if (args.TryGetProperty("env", out var environment))
                {
                    var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var variable in environment.EnumerateObject()) if (!keys.Add(variable.Name) || variable.Value.GetString()!.Contains('\0')) throw new DesktopError("INVALID_ARGUMENT", "Environment names must be unique and values cannot contain NUL.");
                }
                if (args.Flag("cdp") && (args.Flag("attach") || args.Text("profile_dir", 2048) == null)) throw new DesktopError("INVALID_ARGUMENT", "CDP requires an explicit empty profile_dir and a fresh launch.");
                if (!args.Flag("cdp") && (args.TryGetProperty("profile_dir", out _) || args.TryGetProperty("cdp_port", out _))) throw new DesktopError("INVALID_ARGUMENT", "CDP fields require cdp=true.");
                if (args.Flag("cdp") && args.TryGetProperty("args", out var browserArgs) && browserArgs.EnumerateArray().Any(item => System.Text.RegularExpressions.Regex.IsMatch(item.GetString()!, "^--(remote-debugging-|user-data-dir(=|$))", System.Text.RegularExpressions.RegexOptions.IgnoreCase))) throw new DesktopError("INVALID_ARGUMENT", "Use the dedicated CDP fields instead of conflicting browser flags.");
                if (args.Text("profile_dir", 2048) is string profile && (!System.IO.Path.IsPathFullyQualified(profile) || System.IO.File.Exists(profile) || System.IO.Directory.Exists(profile) && System.IO.Directory.EnumerateFileSystemEntries(profile).Any())) throw new DesktopError("INVALID_ARGUMENT", "CDP needs a new or empty absolute profile directory; the caller owns its lifetime.");
                if (args.Flag("activate") && args.TryGetProperty("args", out var launchArgs) && launchArgs.EnumerateArray().Any(item => item.GetString()!.StartsWith("--headless", StringComparison.OrdinalIgnoreCase))) throw new DesktopError("INVALID_ARGUMENT", "A headless launch cannot request foreground activation.");
            }
            if (operation == "launch" && args.TryGetProperty("args", out var arguments))
            {
                if (arguments.ValueKind != JsonValueKind.Array || arguments.GetArrayLength() > 30 || arguments.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String || item.GetString()!.Length > 2048 || item.GetString()!.Contains('\0'))) throw new DesktopError("INVALID_ARGUMENT", "Invalid launch argument array.");
            }
            if (operation == "clipboard_restore") Require("token");
            if (operation == "clipboard_set" && new[] { "text", "image_path", "file_paths" }.Count(key => args.TryGetProperty(key, out _)) != 1) throw new DesktopError("INVALID_ARGUMENT", "clipboard_set requires exactly one of text, image_path or file_paths.");
            if (operation == "clipboard_set" && args.Text("text", 60000)?.Contains('\0') == true) throw new DesktopError("INVALID_ARGUMENT", "Clipboard Unicode text cannot contain embedded NUL.");
            if (operation == "open_surface") Require("surface");
            if (operation == "open_surface" && args.Text("surface") is not ("start_menu" or "taskbar" or "system_tray" or "system_tray_overflow" or "action_center" or "quick_settings")) throw new DesktopError("INVALID_ARGUMENT", "Unknown shell surface.");
            if (new[] { "dismiss_notification", "notification_action" }.Contains(operation) || operation == "dismiss_all_notifications" && !args.TryGetProperty("app", out _)) Require("ref");
            if (operation == "notification_action") Require("action_ref");
            if (operation == "list_notifications") _ = args.Number("limit", 30, 1, 80);
            if (operation == "dismiss_all_notifications" && args.TryGetProperty("text", out _)) throw new DesktopError("INVALID_ARGUMENT", "Bulk dismissal supports an app filter. Dismiss text-filtered cards individually.");
            if (operation == "dismiss_all_notifications" && args.Text("app", 2048) is string appFilter && string.IsNullOrWhiteSpace(appFilter)) throw new DesktopError("INVALID_ARGUMENT", "The app filter must not be empty.");
            _ = args.Number("width", 100, 100, 8192); _ = args.Number("height", 80, 80, 8192);
            _ = args.Number("x", 0, -32768, 32767); _ = args.Number("y", 0, -32768, 32767);
        }
    }
}

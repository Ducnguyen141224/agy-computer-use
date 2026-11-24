using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;

namespace AgyComputerUse;

internal sealed partial class DesktopService
{
    private static bool IsActiveSurface(WindowEntry window)
    {
        if (Native.ForegroundOwns(window.Handle)) return true;
        if (ProcessName(window.Pid).Equals("StartMenuExperienceHost", StringComparison.OrdinalIgnoreCase) && IsShellHostActive(window.Handle)) return true;
        try
        {
            var rootId = RuntimeId(AutomationElement.FromHandle(window.Handle));
            var focused = AutomationElement.FocusedElement;
            for (var depth = 0; focused != null && depth < 20; depth++)
            {
                if (RuntimeId(focused) == rootId) return true;
                if (focused.Current.NativeWindowHandle != 0 && Native.GetAncestor(new IntPtr(focused.Current.NativeWindowHandle), 2) == window.Handle) return true;
                focused = TreeWalker.ControlViewWalker.GetParent(focused);
            }
        }
        catch { }
        return false;
    }
    private void ClickShellControl(WindowEntry window, AutomationElement target)
    {
        Native.RequireInputPrivilege(window.Pid);
        if (!Native.ActivateWindow(window.Handle)) throw new DesktopError("FOCUS_DENIED", "Windows did not activate the selected shell surface.");
        var rectangle = target.Current.BoundingRectangle;
        if (rectangle.IsEmpty) throw new DesktopError("NOT_ACTIONABLE", "Shell control has no visible geometry.");
        if (!Native.GetWindowRect(window.Handle, out var bounds)) throw new DesktopError("WINDOW_CHANGED", "Shell geometry is unavailable.");
        // XAML and MSAA can expose different hit-test peers for the same shell control.
        // Use the normal fresh-capture point-input route instead of treating peer ids as interchangeable.
        var capture = JsonSerializer.SerializeToElement(Capture(JsonSerializer.SerializeToElement(new { window_id = window.Id, max_dimension = 200, region = new { x = (int)rectangle.X - bounds.Left, y = (int)rectangle.Y - bounds.Top, width = (int)rectangle.Width, height = (int)rectangle.Height } })), Program.Json);
        if (target.Current.BoundingRectangle != rectangle) throw new DesktopError("WINDOW_CHANGED", "Shell control moved during capture. Observe again.");
        _ = Act(JsonSerializer.SerializeToElement(new { action = "click_point", window_id = window.Id, capture_id = capture.GetProperty("capture_id").GetString(), x = capture.GetProperty("width").GetInt32() / 2, y = capture.GetProperty("height").GetInt32() / 2 }));
    }
    private WindowEntry RegisterWindow(IntPtr handle)
    {
        Native.GetWindowThreadProcessId(handle, out var pid);
        using var process = Process.GetProcessById((int)pid);
        var start = process.StartTime.ToUniversalTime().Ticks;
        var id = $"w-{pid}-{start:x}-{handle.ToInt64():x}";
        var entry = new WindowEntry(id, handle, (int)pid, start);
        windows[id] = entry;
        return entry;
    }
    private object ListSurfaces(JsonElement args)
    {
        if (!args.Flag("diagnostic")) return ListScopedSurfaces(args);
        var selected = args.Text("window_id");
        var diagnostic = args.Flag("diagnostic");
        var items = new List<object>();
        Native.EnumerateAllWindows((handle, _) =>
        {
            var visible = Native.IsWindowVisible(handle) && !Native.IsCloaked(handle);
            if (!visible && !diagnostic) return true;
            try
            {
                var entry = RegisterWindow(handle);
                if (selected != null && entry.Id != selected) return true;
                if (diagnostic)
                {
                    if (!visible) return true;
                    var app = ProcessName(entry.Pid).ToLowerInvariant();
                    if (app is "explorer" or "startmenuexperiencehost" or "shellexperiencehost" or "shellhost")
                    {
                        var className = new StringBuilder(256); Native.GetClassName(handle, className, className.Capacity);
                        Native.GetWindowRect(handle, out var rectangle);
                        bool? offscreen = null;
                        try { offscreen = AutomationElement.FromHandle(handle).Current.IsOffscreen; } catch { }
                        items.Add(new { kind = "diagnostic", app, class_name = className.ToString(), visible, uia_offscreen = offscreen, width = rectangle.Width, height = rectangle.Height, foreground = Native.GetForegroundWindow() == handle, window = new { id = entry.Id, pid = entry.Pid } });
                    }
                    return items.Count < 60;
                }
                var kind = SurfaceKind(handle, entry.Pid);
                if (kind != null) items.Add(new { kind, window = WindowInfo(entry) });
                else if (selected != null) items.Add(new { kind = "window", window = WindowInfo(entry) });
            }
            catch { }
            return items.Count < 60;
        }, IntPtr.Zero);
        return new { surfaces = items, note = "Only currently visible top-level surfaces are listed. Menus/dialogs can be separate windows; inspect a returned window before acting." };
    }
    private static string? SurfaceKind(IntPtr handle, int pid)
    {
        if (!Native.GetWindowRect(handle, out var rectangle) || rectangle.Width <= 0 || rectangle.Height <= 0) return null;
        var className = new StringBuilder(256);
        Native.GetClassName(handle, className, className.Capacity);
        var type = className.ToString();
        var app = ProcessName(pid).ToLowerInvariant();
        if ((type == "Shell_TrayWnd" || type == "Shell_SecondaryTrayWnd") && TrustedShell(pid, "explorer")) return "taskbar";
        if (type.Contains("Overflow", StringComparison.OrdinalIgnoreCase) && TrustedShell(pid, "explorer")) return "system_tray_overflow";
        if (type == "#32768") return "menu";
        if (type == "#32770") return "dialog";
        if (app == "startmenuexperiencehost" && TrustedShell(pid, app)) return IsShellHostActive(handle) ? "start_menu" : null;
        if ((app is "shellexperiencehost" or "shellhost") && TrustedShell(pid, app)) return IsShellHostActive(handle) ? "shell_panel" : null;
        if (app == "explorer" && TrustedShell(pid, app) && (type.Contains("Immersive", StringComparison.OrdinalIgnoreCase) || type.Contains("ContentBridge", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var root = AutomationElement.FromHandle(handle);
                foreach (var processName in new[] { "StartMenuExperienceHost", "ShellExperienceHost", "ShellHost" })
                    foreach (var process in Process.GetProcessesByName(processName))
                    {
                        using (process)
                        {
                            if (!TrustedShell(process.Id, processName)) continue;
                            var visible = new AndCondition(new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id), new PropertyCondition(AutomationElement.IsOffscreenProperty, false));
                            if (root.FindFirst(TreeScope.Subtree, visible) != null && IsShellHostActive(handle)) return processName == "StartMenuExperienceHost" ? "start_menu" : "shell_panel";
                        }
                    }
            }
            catch { }
        }
        if (!string.IsNullOrEmpty(type))
        {
            try
            {
                var element = AutomationElement.FromHandle(handle);
                if (element.Current.ControlType == ControlType.Menu) return "menu";
                if (element.Current.ControlType == ControlType.ToolTip) return "tooltip";
                if (element.TryGetCurrentPattern(WindowPattern.Pattern, out var pattern) && ((WindowPattern)pattern).Current.IsModal) return "dialog";
            }
            catch { }
        }
        return null;
    }
    private static bool TrustedShell(int pid, string name)
    {
        var executable = ExecutablePath(pid);
        if (executable == null || !System.IO.Path.GetFileNameWithoutExtension(executable).Equals(name, StringComparison.OrdinalIgnoreCase)) return false;
        var directory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var path = System.IO.Path.GetFullPath(executable);
        return name.Equals("explorer", StringComparison.OrdinalIgnoreCase)
            ? path.Equals(System.IO.Path.Combine(directory, "explorer.exe"), StringComparison.OrdinalIgnoreCase)
            : path.StartsWith(System.IO.Path.Combine(directory, "SystemApps") + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || path.StartsWith(System.IO.Path.Combine(directory, "System32") + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
    private static bool IsShellHostActive(IntPtr window)
    {
        if (Native.GetForegroundWindow() == window) return true;
        try
        {
            // Start can transfer focus to a separate SearchHost CoreWindow while
            // remaining on screen. Native foreground equality alone misses it.
            var roots = AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition);
            foreach (var item in roots)
                if (item.Current.NativeWindowHandle == window.ToInt64() && !item.Current.IsOffscreen) return true;
            var root = RuntimeId(AutomationElement.FromHandle(window));
            var focused = AutomationElement.FocusedElement;
            for (var depth = 0; focused != null && depth < 24; depth++)
            {
                if (RuntimeId(focused) == root) return true;
                if (focused.Current.NativeWindowHandle != 0 && Native.GetAncestor(new IntPtr(focused.Current.NativeWindowHandle), 2) == window) return true;
                focused = TreeWalker.ControlViewWalker.GetParent(focused);
            }
        }
        catch { }
        return false;
    }
    private object OpenSurface(JsonElement args)
    {
        var surface = args.Text("surface") ?? throw new DesktopError("INVALID_ARGUMENT", "surface is required.");
        if (surface is "action_center" or "system_tray_overflow" or "quick_settings")
        {
            var visible = JsonSerializer.SerializeToElement(ListSurfaces(JsonSerializer.SerializeToElement(new { })), Program.Json);
            foreach (var item in visible.GetProperty("surfaces").EnumerateArray())
            {
                var kind = item.GetProperty("kind").GetString();
                if (surface == "system_tray_overflow" && kind == "system_tray_overflow" || kind == "shell_panel" && ShellPanelType(GetWindow(item.GetProperty("window").GetProperty("id").GetString()).Handle) == surface)
                    return new { delivery = "already_satisfied", verification = "passed", surface = item.Clone() };
            }
        }
        if (surface == "start_menu")
        {
            var existing = JsonSerializer.SerializeToElement(ListSurfaces(JsonSerializer.SerializeToElement(new { })), Program.Json).GetProperty("surfaces").EnumerateArray().FirstOrDefault(item => item.GetProperty("kind").GetString() == "start_menu");
            if (existing.ValueKind != JsonValueKind.Undefined) return new { delivery = "already_satisfied", verification = "passed", surface = existing };
            var taskbar = Native.FindWindow("Shell_TrayWnd", null);
            if (taskbar == IntPtr.Zero) throw new DesktopError("SURFACE_NOT_FOUND", "Taskbar window was not found.");
            Native.GetWindowThreadProcessId(taskbar, out var taskbarPid);
            Native.RequireInputPrivilege((int)taskbarPid);
            if (Native.ActivateStartMenu())
            {
                Thread.Sleep(180);
                return new { delivery = "sent", verification = "not_requested", requested_surface = surface, mechanism = "DEF_WINDOW_PROC_SC_TASKLIST", surfaces = ListSurfaces(JsonSerializer.SerializeToElement(new { })) };
            }
            var root = AutomationElement.FromHandle(taskbar);
            var buttons = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "StartButton"));
            if (buttons.Count == 1)
            {
                var entry = RegisterWindow(taskbar);
                var target = Resolve(AddRef(entry, buttons[0]));
                if (buttons[0].TryGetCurrentPattern(InvokePattern.Pattern, out var invoke)) ((InvokePattern)invoke).Invoke();
                else
                {
                    try { LegacyDefaultAction(target); }
                    catch (DesktopError error) when (error.Code == "ACTION_UNSUPPORTED") { ClickShellControl(entry, buttons[0]); }
                }
            }
            else Native.KeyChord("Win");
            Thread.Sleep(180);
            return new { delivery = "sent", verification = "not_requested", requested_surface = surface, surfaces = ListSurfaces(JsonSerializer.SerializeToElement(new { })) };
        }
        if (surface == "taskbar")
        {
            var taskbar = Native.FindWindow("Shell_TrayWnd", null);
            if (taskbar == IntPtr.Zero) throw new DesktopError("SURFACE_NOT_FOUND", "Taskbar window was not found.");
            Native.KeyChord("Win+T");
            var entry = RegisterWindow(taskbar);
            for (var attempt = 0; attempt < 15 && !IsActiveSurface(entry); attempt++) Thread.Sleep(20);
            if (!IsActiveSurface(entry) && !Native.ActivateWindow(taskbar)) throw new DesktopError("FOCUS_DENIED", "Taskbar did not receive focus.");
            return new { delivery = "sent", window = WindowInfo(entry) };
        }
        if (surface == "system_tray_overflow")
        {
            var taskbar = Native.FindWindow("Shell_TrayWnd", null);
            if (taskbar == IntPtr.Zero) throw new DesktopError("SURFACE_NOT_FOUND", "Taskbar window was not found.");
            var root = AutomationElement.FromHandle(taskbar);
            var matches = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ClassNameProperty, "SystemTray.ChevronIconView"));
            if (matches.Count == 0) matches = root.FindAll(TreeScope.Descendants, new AndCondition(new PropertyCondition(AutomationElement.AutomationIdProperty, "SystemTrayIcon"), new PropertyCondition(AutomationElement.ClassNameProperty, "SystemTray.NormalButton")));
            if (matches.Count != 1) throw new DesktopError("AMBIGUOUS_TARGET", "Could not identify one overflow chevron. Observe the taskbar and choose its control explicitly.");
            if (matches[0].TryGetCurrentPattern(InvokePattern.Pattern, out var invoke)) ((InvokePattern)invoke).Invoke();
            else ClickShellControl(RegisterWindow(taskbar), matches[0]);
        }
        else
        {
            var taskbar = Native.FindWindow("Shell_TrayWnd", null);
            if (taskbar == IntPtr.Zero || !Native.ActivateWindow(taskbar)) throw new DesktopError("FOCUS_DENIED", "Taskbar could not be activated for this shell shortcut.");
            Native.KeyChord(surface switch
            {
            "start_menu" => "Win",
            "system_tray" => "Win+B",
            "action_center" => Environment.OSVersion.Version.Build >= 22000 ? "Win+N" : "Win+A",
            "quick_settings" => Environment.OSVersion.Version.Build >= 22000 ? "Win+A" : throw new DesktopError("SURFACE_NOT_SUPPORTED", "This Windows version has no separate quick settings panel."),
            _ => throw new DesktopError("INVALID_ARGUMENT", "Unknown system surface.")
            });
        }
        Thread.Sleep(180);
        var foreground = Native.GetForegroundWindow();
        var selectedWindow = foreground == IntPtr.Zero ? null : RegisterWindow(foreground);
        return new { delivery = "sent", verification = "not_requested", requested_surface = surface, foreground = selectedWindow == null ? null : WindowInfo(selectedWindow), surfaces = ListSurfaces(JsonSerializer.SerializeToElement(new { })) };
    }
    private static string? ShellPanelType(IntPtr window)
    {
        try
        {
            var root = AutomationElement.FromHandle(window);
            if (NotificationReader.Read(root).Recognized) return "action_center";
            if (root.Current.ClassName == "ControlCenterWindow" || root.FindFirst(TreeScope.Subtree, new PropertyCondition(AutomationElement.AutomationIdProperty, "ControlCenterRegion")) != null) return "quick_settings";
        }
        catch { }
        return null;
    }

}

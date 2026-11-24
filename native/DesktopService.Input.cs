using System.Text.Json;

namespace AgyComputerUse;

internal sealed partial class DesktopService
{
    private static void LegacyDefaultAction(ElementEntry target)
    {
        var area = target.Element.Current.BoundingRectangle;
        if (area.IsEmpty) throw new DesktopError("ACTION_UNSUPPORTED", "Control has neither InvokePattern nor usable legacy geometry.");
        var point = new Native.Point((int)(area.X + area.Width / 2), (int)(area.Y + area.Height / 2));
        // A legacy action is used only when the visible point resolves to this exact UIA control.
        var underPoint = AutomationElement.FromPoint(new System.Windows.Point(point.X, point.Y));
        if (RuntimeId(underPoint) != target.RuntimeId || Native.GetAncestor(Native.WindowFromPoint(point), 2) != target.Window.Handle)
            throw new DesktopError("ACTION_UNSUPPORTED", "Legacy fallback could not prove the target at its visible point. Observe before choosing physical input.");
        if (Native.AccessibleObjectFromPoint(point, out var accessible, out var child) != 0 || accessible == null)
            throw new DesktopError("ACTION_UNSUPPORTED", "No legacy accessibility primary action was available.");
        try
        {
            if (accessible.get_accName(child) != target.Element.Current.Name || string.IsNullOrWhiteSpace(accessible.get_accDefaultAction(child)))
                throw new DesktopError("ACTION_UNSUPPORTED", "Legacy target identity or default action could not be proven.");
            accessible.accDoDefaultAction(child);
        }
        finally { if (System.Runtime.InteropServices.Marshal.IsComObject(accessible)) System.Runtime.InteropServices.Marshal.ReleaseComObject(accessible); }
    }
    private static Native.Point ElementPoint(ElementEntry entry, bool foreground = true)
    {
        if (foreground) EnsureForeground(entry.Window);
        var bounds = entry.Element.Current.BoundingRectangle;
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) throw new DesktopError("NOT_ACTIONABLE", "Control has no visible bounding rectangle.");
        var point = new Native.Point((int)(bounds.X + bounds.Width / 2), (int)(bounds.Y + bounds.Height / 2));
        CheckPoint(entry.Window, point, foreground);
        var hit = AutomationElement.FromPoint(new System.Windows.Point(point.X, point.Y));
        var matches = false;
        for (var depth = 0; hit != null && depth < 64; depth++, hit = TreeWalker.ControlViewWalker.GetParent(hit))
            if (RuntimeId(hit) == entry.RuntimeId) { matches = true; break; }
        if (!matches) throw new DesktopError("POINT_OBSCURED", "Another accessibility control covers the ref's input point. Observe again or use a fresh capture-point target.");
        return point;
    }
    private static void CheckPoint(WindowEntry window, Native.Point point, bool foreground = true)
    {
        if (foreground) EnsureForeground(window);
        var hit = Native.GetAncestor(Native.WindowFromPoint(point), 2);
        if (hit != window.Handle)
        {
            Native.GetWindowThreadProcessId(hit, out var pid);
            var type = new System.Text.StringBuilder(256); Native.GetClassName(hit, type, type.Capacity);
            throw new DesktopError("POINT_OBSCURED", "The input point is covered by a different window.", new { x = point.X, y = point.Y, covering_app = ProcessName((int)pid), covering_class = type.ToString(), delivery = "not_sent" });
        }
    }
    private void PhysicalElementAction(JsonElement args, ElementEntry target, string action)
    {
        var from = ElementPoint(target);
        if (action == "hover")
        {
            Native.PositionPointer(from.X, from.Y);
            return;
        }
        if (action == "drag")
        {
            var destination = DragDestinationWindow(args, target.Window);
            var to = DragDestinationPoint(args, target.Window, destination);
            SendDrag(args, target.Window, destination, from, to);
            return;
        }
        Native.WithPointerModifiers(args, () => Native.ClickAt(from.X, from.Y, action == "right_click" ? "right" : "left", action == "double_click" ? 2 : action == "triple_click" ? 3 : 1));
    }
    private WindowEntry DragDestinationWindow(JsonElement args, WindowEntry source) => args.Text("to_ref") is string reference ? Resolve(reference).Window : args.Text("to_window_id") is string id ? GetWindow(id) : source;
    private Native.Point DragDestinationPoint(JsonElement args, WindowEntry source, WindowEntry destination)
    {
        Native.RequireInputPrivilege(destination.Pid);
        if (args.Text("to_ref") is string reference)
        {
            var entry = Resolve(reference);
            if (!entry.Element.Current.IsEnabled || entry.Element.Current.IsPassword || entry.Element.Current.IsOffscreen) throw new DesktopError("NOT_ACTIONABLE", "Drag destination must be enabled, in view and non-password.");
            Native.RequireInputPrivilege(entry.ElementPid);
            return ElementPoint(entry, false);
        }
        return MapCapturePoint(args.Text("to_capture_id") ?? args.Text("capture_id"), destination, args.Number("to_x", -1, 0, 1599), args.Number("to_y", -1, 0, 1599), false);
    }
    private void SendDrag(JsonElement args, WindowEntry source, WindowEntry destination, Native.Point from, Native.Point to)
    {
        if (!Native.GetWindowRect(source.Handle, out var sourceRect) || !Native.GetWindowRect(destination.Handle, out var destinationRect)) throw new DesktopError("WINDOW_CHANGED", "Drag endpoint geometry is unavailable.");
        // Both endpoints pass identity/geometry/hit checks before either capture is consumed.
        foreach (var id in new[] { args.Text("capture_id"), args.Text("to_capture_id") }.OfType<string>().Distinct()) ConsumeCapture(id);
        Native.WithPointerModifiers(args, () => Native.DragBetween(from, to, args.Number("duration_ms", 350, 80, 2000), () => DragContext(source, destination, sourceRect, destinationRect), args.Number("drop_delay_ms", 500, 0, 2000)));
    }
    private static bool DragContext(WindowEntry source, WindowEntry destination, Native.Rect sourceRect, Native.Rect destinationRect)
    {
        if (!Native.IsWindow(source.Handle) || !Native.IsWindow(destination.Handle) || !Native.GetWindowRect(source.Handle, out var currentSource) || !Native.GetWindowRect(destination.Handle, out var currentDestination) || !SameRect(sourceRect, currentSource) || !SameRect(destinationRect, currentDestination)) return false;
        Native.GetWindowThreadProcessId(source.Handle, out var sourcePid); Native.GetWindowThreadProcessId(destination.Handle, out var destinationPid);
        if (sourcePid != source.Pid || destinationPid != destination.Pid) return false;
        try { if (ProcessStarted(source.Pid) != source.Started || ProcessStarted(destination.Pid) != destination.Started) return false; }
        catch (DesktopError) { return false; }
        var foreground = Native.GetForegroundWindow();
        for (var depth = 0; foreground != IntPtr.Zero && depth < 20; depth++, foreground = Native.GetOwnedWindow(foreground, 4)) if (foreground == source.Handle || foreground == destination.Handle) return true;
        return Native.ForegroundOwns(source.Handle) || Native.ForegroundOwns(destination.Handle);
    }
    private void PointAction(JsonElement args, WindowEntry window, string action)
    {
        // Validate all action-specific arguments before consuming the capture or sending input.
        var button = args.Text("button") ?? "left";
        var count = args.Number("count", 1, 1, 3);
        var vertical = args.Number("vertical", 0, -2400, 2400);
        var horizontal = args.Number("horizontal", 0, -2400, 2400);
        var duration = args.Number("duration_ms", 350, 80, 2000);
        if (button is not ("left" or "right" or "middle")) throw new DesktopError("INVALID_ARGUMENT", "Invalid mouse button.");
        if (action == "drag_point")
        {
            var destination = DragDestinationWindow(args, window);
            var to = DragDestinationPoint(args, window, destination);
            var from = MapCapturePoint(args.Text("capture_id"), window, args.Number("x", -1, 0, 1599), args.Number("y", -1, 0, 1599), true);
            SendDrag(args, window, destination, from, to);
            return;
        }
        var point = CapturePoint(args, window);
        Native.WithPointerModifiers(args, () => { switch (action)
        {
            case "click_point": Native.ClickAt(point.X, point.Y, button, count); break;
            case "mouse_move": Native.PositionPointer(point.X, point.Y); break;
            case "mouse_wheel": Native.WheelAt(point.X, point.Y, vertical, horizontal); break;
        } });
    }
}

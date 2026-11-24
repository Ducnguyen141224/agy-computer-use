using System.Runtime.InteropServices;
using System.Text;

namespace AgyComputerUse;

internal static partial class Native
{
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] internal static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] internal static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] internal static extern uint GetDoubleClickTime();
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] internal static extern bool GetPhysicalCursorPos(out Point point);
    [DllImport("user32.dll")] internal static extern bool GetClipCursor(out Rect rect);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] internal static extern IntPtr GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")] internal static extern int GetAwarenessFromDpiAwarenessContext(IntPtr context);
    internal static bool ForegroundOwns(IntPtr window)
    {
        var foreground = GetForegroundWindow();
        for (var depth = 0; window != IntPtr.Zero && depth < 20; depth++, window = GetOwnedWindow(window, 4)) if (window == foreground) return true;
        return false;
    }
    internal static void PositionPointer(int x, int y)
    {
        if (GetClipCursor(out var clip) && (x < clip.Left || x >= clip.Right || y < clip.Top || y >= clip.Bottom)) throw new DesktopError("INPUT_CLIPPED", "The requested point is outside the current OS cursor clip. No click was sent.");
        var left = GetSystemMetrics(76); var top = GetSystemMetrics(77);
        var width = GetSystemMetrics(78); var height = GetSystemMetrics(79);
        if (width <= 0 || height <= 0 || x < left || x >= (long)left + width || y < top || y >= (long)top + height) throw new DesktopError("INPUT_CLIPPED", "Point is outside the virtual desktop. No input was sent.");
        // Route movement through the input stream as well as the cursor position. XAML
        // hit testing must receive the movement before the subsequent button event.
        Send([new() { Type = 0, Data = new() { Mouse = new() { X = (int)Math.Clamp(((long)x - left) * 65536 / width + 32768 / width, 0, 65535), Y = (int)Math.Clamp(((long)y - top) * 65536 / height + 32768 / height, 0, 65535), Flags = 0xC001 } } }]);
        if (!GetPhysicalCursorPos(out var actual) || Math.Abs(actual.X - x) > 1 || Math.Abs(actual.Y - y) > 1) throw new DesktopError("INPUT_POSITION_CHANGED", "The physical pointer did not reach the requested point. No click was sent.");
        CursorOverlay.Pointer(x, y);
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(IntPtr hwnd, StringBuilder result, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr FindWindow(string className, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? className, string? title);
    internal static bool EnumerateAllWindows(EnumWindowsProc callback, IntPtr data)
    {
        var handles = new HashSet<IntPtr>();
        EnumWindows((window, _) => { handles.Add(window); return true; }, IntPtr.Zero);
        var after = IntPtr.Zero;
        for (var count = 0; count < 1500; count++)
        {
            var window = FindWindowEx(IntPtr.Zero, after, null, null);
            if (window == IntPtr.Zero || window == after) break;
            handles.Add(window); after = window;
        }
        var taskbar = FindWindow("Shell_TrayWnd", null);
        if (taskbar != IntPtr.Zero) handles.Add(taskbar);
        // Immersive shell windows can appear as UIA desktop children while neither
        // EnumWindows nor the native sibling walk yields their HWND.
        try
        {
            var children = AutomationElement.RootElement.FindAll(System.Windows.Automation.TreeScope.Children, System.Windows.Automation.Condition.TrueCondition);
            foreach (var child in children.Take(100))
            {
                var window = new IntPtr(child.Current.NativeWindowHandle);
                if (window != IntPtr.Zero && IsWindow(window)) handles.Add(window);
            }
        }
        catch (System.Runtime.InteropServices.COMException) { }
        catch (System.Windows.Automation.ElementNotAvailableException) { }
        foreach (var window in handles) if (!callback(window, data)) return false;
        return true;
    }
    [DllImport("oleacc.dll")] internal static extern int AccessibleObjectFromPoint(Point point, [MarshalAs(UnmanagedType.Interface)] out Accessibility.IAccessible accessible, [MarshalAs(UnmanagedType.Struct)] out object child);
    [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] internal static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("user32.dll")] internal static extern bool BringWindowToTop(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern void SwitchToThisWindow(IntPtr window, bool altTab);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowEx(uint extended, string className, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    private static IntPtr commandWindow;
    internal static bool ActivateStartMenu() => StaDispatcher.Run(() =>
    {
        if (commandWindow == IntPtr.Zero) commandWindow = CreateWindowEx(0, "STATIC", "AGY shell command", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (commandWindow == IntPtr.Zero) return false;
        DefWindowProc(commandWindow, 0x0112, new IntPtr(0xF130), IntPtr.Zero);
        return true;
    });
    internal static void CloseShellCommandWindow()
    {
        if (commandWindow != IntPtr.Zero) StaDispatcher.Run(() => { DestroyWindow(commandWindow); commandWindow = IntPtr.Zero; return true; });
    }
    [DllImport("user32.dll", EntryPoint = "GetWindow")] internal static extern IntPtr GetOwnedWindow(IntPtr window, uint command);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
    internal static bool IsCloaked(IntPtr window) => DwmGetWindowAttribute(window, 14, out var value, sizeof(int)) == 0 && value != 0;
    [DllImport("user32.dll")] internal static extern bool PeekMessage(out Message message, IntPtr window, uint min, uint max, uint remove);
    [StructLayout(LayoutKind.Sequential)] internal struct Message { public IntPtr Window; public uint Id; public UIntPtr WParam; public IntPtr LParam; public uint Time; public Point Point; public uint Private; }
    internal static bool ActivateWindow(IntPtr window)
    {
        if (IsIconic(window)) ShowWindow(window, 9);
        if (GetForegroundWindow() == window) return true;
        SetForegroundWindow(window);
        if (GetForegroundWindow() == window) return true;
        try { AutomationElement.FromHandle(window).SetFocus(); }
        catch (InvalidOperationException) { }
        catch (System.Runtime.InteropServices.COMException) { }
        if (GetForegroundWindow() == window) return true;
        try
        {
            SwitchToThisWindow(window, false);
            for (var attempt = 0; attempt < 6; attempt++) { if (GetForegroundWindow() == window) return true; Thread.Sleep(25); }
        }
        catch (EntryPointNotFoundException) { }
        // Join the foreground input queue only for this explicit focus operation; always detach.
        PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
        var current = GetCurrentThreadId();
        var foreground = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var target = GetWindowThreadProcessId(window, out _);
        var attachedForeground = foreground != 0 && foreground != current && AttachThreadInput(current, foreground, true);
        var attachedTarget = target != 0 && target != current && target != foreground && AttachThreadInput(current, target, true);
        try
        {
            BringWindowToTop(window);
            SetForegroundWindow(window);
            for (var attempt = 0; attempt < 10; attempt++) { if (GetForegroundWindow() == window) return true; Thread.Sleep(30); }
            return false;
        }
        finally
        {
            if (attachedTarget) AttachThreadInput(current, target, false);
            if (attachedForeground) AttachThreadInput(current, foreground, false);
        }
    }

    private static Input KeyInput(ushort key, bool up, bool extended = false) => new() { Type = 1, Data = new() { Keyboard = new() { Vk = key, Flags = (up ? 2u : 0u) | (extended || key is 0x5B or 0x5C or 0x5D ? 1u : 0u) } } };
    private static void Send(Input[] events)
    {
        if (SendInput((uint)events.Length, events, Marshal.SizeOf<Input>()) != events.Length)
            throw new DesktopError("INPUT_UNKNOWN", "Input may have been partially delivered. Read the live UI before retrying.", new { delivery = "unknown" });
    }
    internal static void RequireReleasedModifiers()
    {
        foreach (var key in new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C })
            if (ownedModifiers?.Contains((ushort)key) != true && (GetAsyncKeyState(key) & 0x8000) != 0) throw new DesktopError("INPUT_BUSY", "A modifier is currently held by the user or another application. No input was sent.");
    }
    [ThreadStatic] private static HashSet<ushort>? ownedModifiers;
    internal static ushort[] ValidatePointerModifiers(System.Text.Json.JsonElement args)
    {
        if (!args.TryGetProperty("modifiers", out var values)) return [];
        var keys = values.EnumerateArray().Select(value => value.GetString() switch { "ctrl" => (ushort)0x11, "alt" => (ushort)0x12, "shift" => (ushort)0x10, "win" => (ushort)0x5B, _ => throw new DesktopError("INVALID_ARGUMENT", "Unknown pointer modifier.") }).ToArray();
        if (keys.Distinct().Count() != keys.Length) throw new DesktopError("INVALID_ARGUMENT", "Pointer modifiers must be unique.");
        return keys;
    }
    internal static void WithPointerModifiers(System.Text.Json.JsonElement args, Action gesture)
    {
        var keys = ValidatePointerModifiers(args); RequireReleasedModifiers();
        var held = new List<ushort>(); ownedModifiers = new();
        try { foreach (var key in keys) { held.Add(key); ownedModifiers.Add(key); Send([KeyInput(key, false)]); } gesture(); }
        finally { foreach (var key in held.AsEnumerable().Reverse()) SendInput(1, [KeyInput(key, true)], Marshal.SizeOf<Input>()); ownedModifiers = null; }
    }
    internal static void KeyChord(string chord, bool force = false)
    {
        var (modifiers, key) = ParseChord(chord);
        ValidateShortcut(modifiers, key, force);
        RequireReleasedModifiers();
        var held = new List<ushort>();
        var keyDown = false;
        try
        {
            foreach (var modifier in modifiers) { held.Add(modifier); Send([KeyInput(modifier, false)]); }
            keyDown = true;
            Send([KeyInput(key, false, key is >= 0x21 and <= 0x2E)]);
            Thread.Sleep(30);
        }
        finally
        {
            if (keyDown) SendInput(1, [KeyInput(key, true, key is >= 0x21 and <= 0x2E)], Marshal.SizeOf<Input>());
            foreach (var modifier in held.AsEnumerable().Reverse()) SendInput(1, [KeyInput(modifier, true)], Marshal.SizeOf<Input>());
        }
    }
    internal static void ValidateChord(string chord, bool force = false) { var (modifiers, key) = ParseChord(chord); ValidateShortcut(modifiers, key, force); }
    private static void ValidateShortcut(List<ushort> modifiers, ushort key, bool force)
    {
        var ctrl = modifiers.Contains(0x11); var alt = modifiers.Contains(0x12); var win = modifiers.Contains(0x5B); var shift = modifiers.Contains(0x10);
        if (ctrl && alt && key == 0x2E) throw new DesktopError("ACTION_UNSUPPORTED", "Windows secure attention cannot be synthesized. No input was sent.");
        var system = alt && key is 0x73 or 0x09 || win && key is 0x4C or 0x44 or 0x52 or 0x58 or 0x45 || ctrl && shift && key == 0x1B;
        if (system && !force) throw new DesktopError("SYSTEM_SHORTCUT", "This chord affects the desktop or app lifecycle. Use force only when that effect is intended. No input was sent.");
    }
    private static (List<ushort> Modifiers, ushort Key) ParseChord(string chord)
    {
        var tokens = chord.Split('+', StringSplitOptions.TrimEntries);
        if (tokens.Length == 0 || tokens.Any(string.IsNullOrWhiteSpace) || tokens.Length > 5) throw new DesktopError("INVALID_ARGUMENT", "Use a chord such as Ctrl+Shift+S or a single key.");
        var modifierMap = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase) { ["Ctrl"] = 0x11, ["Control"] = 0x11, ["Alt"] = 0x12, ["Option"] = 0x12, ["Shift"] = 0x10, ["Win"] = 0x5B, ["Windows"] = 0x5B, ["Cmd"] = 0x5B, ["Command"] = 0x5B, ["Super"] = 0x5B, ["Meta"] = 0x5B };
        var modifiers = new List<ushort>();
        foreach (var token in tokens[..^1])
        {
            if (!modifierMap.TryGetValue(token, out var modifier) || modifiers.Contains(modifier)) throw new DesktopError("INVALID_ARGUMENT", "Chord contains an unknown or repeated modifier.");
            modifiers.Add(modifier);
        }
        var named = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
        {
            ["Enter"] = 0x0D, ["Return"] = 0x0D, ["Tab"] = 9, ["Escape"] = 0x1B, ["Esc"] = 0x1B,
            ["Up"] = 0x26, ["Down"] = 0x28, ["Left"] = 0x25, ["Right"] = 0x27, ["Space"] = 0x20,
            ["Backspace"] = 8, ["Delete"] = 0x2E, ["Insert"] = 0x2D, ["Home"] = 0x24, ["End"] = 0x23,
            ["PageUp"] = 0x21, ["PageDown"] = 0x22, ["Win"] = 0x5B, ["Windows"] = 0x5B, ["Cmd"] = 0x5B, ["Meta"] = 0x5B, ["Apps"] = 0x5D,
            ["Plus"] = 0xBB, ["Minus"] = 0xBD, ["Comma"] = 0xBC, ["Period"] = 0xBE
        };
        var keyToken = tokens[^1];
        ushort key;
        if (keyToken.Length == 1 && char.IsAsciiLetterOrDigit(keyToken[0])) key = char.ToUpperInvariant(keyToken[0]);
        else if (keyToken.Length > 1 && char.ToUpperInvariant(keyToken[0]) == 'F' && int.TryParse(keyToken.AsSpan(1), out var function) && function is >= 1 and <= 24) key = (ushort)(0x70 + function - 1);
        else if (!named.TryGetValue(keyToken, out key)) throw new DesktopError("INVALID_ARGUMENT", "Unsupported key. Use letters, digits, F1-F24 or a documented named key.");
        if (modifiers.Count == 0 && keyToken.Length == 1 && char.IsAsciiLetterUpper(keyToken[0])) modifiers.Add(0x10);
        return (modifiers, key);
    }

    private static Input MouseEvent(uint flags, uint data = 0) => new() { Type = 0, Data = new() { Mouse = new() { Flags = flags, MouseData = data } } };
    internal static void ClickAt(int x, int y, string button = "left", int count = 1)
    {
        if (button is not ("left" or "right" or "middle") || count is < 1 or > 3) throw new DesktopError("INVALID_ARGUMENT", "Invalid mouse button or click count.");
        RequireReleasedModifiers();
        PositionPointer(x, y);
        CursorOverlay.Pointer(x, y, true);
        Thread.Sleep(20);
        var down = button == "left" ? 2u : button == "right" ? 8u : 0x20u;
        var up = down * 2;
        for (var index = 0; index < count; index++)
        {
            try { Send([MouseEvent(down)]); Thread.Sleep(35); }
            finally { SendInput(1, [MouseEvent(up)], Marshal.SizeOf<Input>()); }
            if (index + 1 < count) Thread.Sleep((int)Math.Clamp(GetDoubleClickTime() / 4, 20, 70));
        }
    }
    internal static void WheelAt(int x, int y, int vertical, int horizontal)
    {
        RequireReleasedModifiers();
        PositionPointer(x, y);
        if (vertical != 0) Send([MouseEvent(0x0800, unchecked((uint)vertical))]);
        if (horizontal != 0) Send([MouseEvent(0x1000, unchecked((uint)horizontal))]);
    }
    internal static void DragBetween(Point from, Point to, int durationMs, Func<bool> stillTarget, int dropDelayMs = 500)
    {
        RequireReleasedModifiers();
        PositionPointer(from.X, from.Y);
        try
        {
            Send([MouseEvent(2)]);
            var steps = Math.Clamp(durationMs / 16, 5, 100);
            for (var step = 1; step <= steps; step++)
            {
                if (!stillTarget()) throw new DesktopError("FOCUS_CHANGED", "Focus changed during drag; the button was released. Read current UI.");
                var x = from.X + (to.X - from.X) * step / steps;
                var y = from.Y + (to.Y - from.Y) * step / steps;
                PositionPointer(x, y);
                Thread.Sleep(Math.Max(1, durationMs / steps));
            }
            var dwell = System.Diagnostics.Stopwatch.StartNew();
            while (dwell.ElapsedMilliseconds < dropDelayMs)
            {
                if (!stillTarget()) throw new DesktopError("FOCUS_CHANGED", "The drag context changed over the destination; the button was released.");
                Thread.Sleep(20);
            }
        }
        finally { SendInput(1, [MouseEvent(4)], Marshal.SizeOf<Input>()); }
    }
}

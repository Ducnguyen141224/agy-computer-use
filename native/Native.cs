using System.Runtime.InteropServices;

namespace AgyComputerUse;

internal static partial class Native
{
    internal delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr data);
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr data);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(IntPtr hwnd, System.Text.StringBuilder title, int count);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] internal static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] internal static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll", EntryPoint = "SetPhysicalCursorPos", SetLastError = true)] internal static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] internal static extern IntPtr SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll", EntryPoint = "SendInput", SetLastError = true)] private static extern uint SendInputNative(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] internal static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] internal static extern bool CloseDesktop(IntPtr desktop);

    [StructLayout(LayoutKind.Sequential)] internal struct Rect
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; public Point(int x, int y) { X = x; Y = y; } }
    [StructLayout(LayoutKind.Sequential)] internal struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] internal struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct MouseInput
    {
        public int X, Y;
        public uint MouseData, Flags, Time;
        public UIntPtr ExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct KeyboardInput
    {
        public ushort Vk, Scan;
        public uint Flags, Time;
        public UIntPtr ExtraInfo;
    }
    internal static void MouseClick(int x, int y)
    {
        if (!SetCursorPos(x, y)) throw new DesktopError("INPUT_FAILED", "Could not position the pointer.");
        Input[] events = [new() { Type = 0, Data = new() { Mouse = new() { Flags = 0x0002 } } }, new() { Type = 0, Data = new() { Mouse = new() { Flags = 0x0004 } } }];
        if (SendInput(2, events, Marshal.SizeOf<Input>()) != 2)
            throw new DesktopError("INPUT_UNKNOWN", "Pointer input may have been partially delivered. Observe before retrying.");
    }
    internal static void TypeText(string text)
    {
        RequireReleasedModifiers();
        var events = new List<Input>();
        foreach (var character in text)
        {
            events.Add(new() { Type = 1, Data = new() { Keyboard = new() { Scan = character, Flags = 0x0004 } } });
            events.Add(new() { Type = 1, Data = new() { Keyboard = new() { Scan = character, Flags = 0x0006 } } });
        }
        // Small complete down/up chunks leave cancellation checkpoints during long text.
        foreach (var chunk in events.Chunk(32))
            if (SendInput((uint)chunk.Length, chunk, Marshal.SizeOf<Input>()) != chunk.Length)
                throw new DesktopError("INPUT_UNKNOWN", "Text input may have been partially delivered. Read the field before retrying.");
    }
    internal static void PressKey(string key)
    {
        var keys = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
        {
            ["Enter"] = 0x0D, ["Tab"] = 0x09, ["Escape"] = 0x1B,
            ["Up"] = 0x26, ["Down"] = 0x28, ["Left"] = 0x25, ["Right"] = 0x27,
            ["Space"] = 0x20, ["Backspace"] = 0x08, ["Delete"] = 0x2E,
            ["Home"] = 0x24, ["End"] = 0x23
        };
        if (!keys.TryGetValue(key, out var vk)) throw new DesktopError("INVALID_ARGUMENT", "Unsupported key. Use a documented single navigation key.");
        Input[] events = [new() { Type = 1, Data = new() { Keyboard = new() { Vk = vk } } }, new() { Type = 1, Data = new() { Keyboard = new() { Vk = vk, Flags = 0x0002 } } }];
        if (SendInput(2, events, Marshal.SizeOf<Input>()) != 2) throw new DesktopError("INPUT_UNKNOWN", "Key input may have been partially delivered. Observe before retrying.");
    }
}

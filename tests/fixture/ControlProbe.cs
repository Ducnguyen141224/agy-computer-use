using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Automation;

namespace DesktopFixture;

// Independent observer/injector for the disposable fixture only; all pixels stay in RAM.
internal static class ControlProbe
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? type, string title);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern IntPtr SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] input, int size);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint raster);
    [StructLayout(LayoutKind.Sequential)] private struct Input { internal uint Type; internal InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] internal Key Key; [FieldOffset(0)] internal Mouse Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct Key { internal ushort Vk, Scan; internal uint Flags, Time; internal UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct Mouse { internal int X, Y; internal uint Data, Flags, Time; internal UIntPtr Extra; }
    internal static void Run(int expectedPid)
    {
        SetProcessDpiAwarenessContext(new IntPtr(-4));
        Console.InputEncoding = System.Text.Encoding.UTF8; Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        string? line;
        while ((line = Console.ReadLine()) != null)
        {
            try
            {
                GetWindowThreadProcessId(GetForegroundWindow(), out var foreground);
                object data;
                if (line == "escape")
                {
                    if (foreground != expectedPid) throw new InvalidOperationException("Disposable fixture is not foreground; no Escape was injected.");
                    Input[] keys = [new() { Type = 1, Data = new() { Key = new() { Vk = 0x1B } } }, new() { Type = 1, Data = new() { Key = new() { Vk = 0x1B, Flags = 2 } } }];
                    data = new { sent = SendInput(2, keys, Marshal.SizeOf<Input>()) == 2 };
                }
                else if (line == "read")
                {
                    var root = AutomationElement.RootElement.FindFirst(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, expectedPid));
                    var field = root.FindFirst(TreeScope.Descendants, new AndCondition(new PropertyCondition(AutomationElement.NameProperty, "Message"), new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)));
                    data = new { value = ((ValuePattern)field.GetCurrentPattern(ValuePattern.Pattern)).Current.Value };
                }
                else if (line == "held") data = new { ctrl = (GetAsyncKeyState(0x11) & 0x8000) != 0, shift = (GetAsyncKeyState(0x10) & 0x8000) != 0, left = (GetAsyncKeyState(1) & 0x8000) != 0 };
                else if (line == "visual")
                {
                    var window = FindWindow(null, "AGY control indicator"); var styles = window == IntPtr.Zero ? 0L : GetWindowLongPtr(window, -20).ToInt64();
                    var samples = Screen.AllScreens.Select(screen =>
                    {
                        using var bitmap = new Bitmap(1, 1); using var graphics = Graphics.FromImage(bitmap);
                        var desktop = GetDC(IntPtr.Zero); var target = graphics.GetHdc();
                        try { if (!BitBlt(target, 0, 0, 1, 1, desktop, screen.Bounds.Left + 2, screen.Bounds.Top + screen.Bounds.Height / 2, 0x40CC0020)) throw new InvalidOperationException("Layered display pixel capture failed."); }
                        finally { graphics.ReleaseHdc(target); ReleaseDC(IntPtr.Zero, desktop); }
                        var color = bitmap.GetPixel(0, 0); return new[] { (int)color.R, color.G, color.B };
                    }).ToArray();
                    var pointer = FindWindow(null, "AGY animated pointer");
                    data = new { rendered = window != IntPtr.Zero && IsWindowVisible(window), pointer_visible = pointer != IntPtr.Zero && IsWindowVisible(pointer), click_through = (styles & 0x20) != 0, no_activate = (styles & 0x08000000) != 0, foreground, borders = samples };
                }
                else throw new InvalidOperationException("Unknown probe command.");
                Console.WriteLine(JsonSerializer.Serialize(data));
            }
            catch (Exception ex) { Console.WriteLine(JsonSerializer.Serialize(new { error = ex.Message })); }
        }
    }
}

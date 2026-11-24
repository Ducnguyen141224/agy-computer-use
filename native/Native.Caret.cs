using System.Runtime.InteropServices;

namespace AgyComputerUse;

internal static partial class Native
{
    [StructLayout(LayoutKind.Sequential)] private struct GuiThread { internal uint Size, Flags; internal IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret; internal Rect CaretRect; }
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThread info);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref Point point);
    internal static System.Windows.Rect? CaretRectangle(IntPtr window)
    {
        var thread = GetWindowThreadProcessId(window, out _);
        var info = new GuiThread { Size = (uint)Marshal.SizeOf<GuiThread>() };
        if (!GetGUIThreadInfo(thread, ref info) || info.Caret == IntPtr.Zero || GetAncestor(info.Caret, 2) != window || info.CaretRect.Height <= 0) return null;
        var point = new Point(info.CaretRect.Left, info.CaretRect.Top);
        if (!ClientToScreen(info.Caret, ref point)) return null;
        return new(point.X, point.Y, Math.Max(2, info.CaretRect.Width), info.CaretRect.Height);
    }
}

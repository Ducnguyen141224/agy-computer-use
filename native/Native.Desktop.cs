using System.Runtime.InteropServices;
using System.Text;

namespace AgyComputerUse;

internal static partial class Native
{
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDesktop(uint thread);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder value, uint bytes, out uint needed);

    private static string? DesktopName(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return null;
        var name = new StringBuilder(256);
        return GetUserObjectInformation(handle, 2, name, (uint)name.Capacity * 2, out _) ? name.ToString() : null;
    }

    internal static bool InteractiveDesktop()
    {
        var input = OpenInputDesktop(0, false, 0x0001);
        if (input == IntPtr.Zero) return false;
        try
        {
            var current = DesktopName(GetThreadDesktop(GetCurrentThreadId()));
            var receiving = DesktopName(input);
            // Never switch to another desktop or claim that a readable secure desktop is usable.
            return current != null && current.Equals("Default", StringComparison.OrdinalIgnoreCase)
                && current.Equals(receiving, StringComparison.OrdinalIgnoreCase) && Environment.UserInteractive;
        }
        finally { CloseDesktop(input); }
    }
}

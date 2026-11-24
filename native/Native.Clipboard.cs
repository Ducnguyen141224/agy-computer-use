using System.Runtime.InteropServices;

namespace AgyComputerUse;

internal static partial class Native
{
    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint format, IntPtr data);
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, nuint size);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr data);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr data);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr data);
    [DllImport("kernel32.dll")] private static extern nuint GlobalSize(IntPtr data);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint DragQueryFile(IntPtr drop, uint index, System.Text.StringBuilder? path, uint length);
    internal static uint WriteClipboardFiles(string[] paths, IntPtr owner)
    {
        var payload = System.Text.Encoding.Unicode.GetBytes(string.Join('\0', paths) + "\0\0");
        var memory = GlobalAlloc(0x42, (nuint)(20 + payload.Length));
        if (memory == IntPtr.Zero) throw new OutOfMemoryException();
        try
        {
            var pointer = GlobalLock(memory); if (pointer == IntPtr.Zero) throw new OutOfMemoryException();
            try { Marshal.WriteInt32(pointer, 20); Marshal.WriteInt32(pointer, 16, 1); Marshal.Copy(payload, 0, pointer + 20, payload.Length); }
            finally { GlobalUnlock(memory); }
            LockClipboard(owner);
            try
            {
                if (!EmptyClipboard()) throw new DesktopError("CLIPBOARD_WRITE_FAILED", "Windows refused to clear the clipboard.", new { delivery = "unknown" });
                if (SetClipboardData(15, memory) == IntPtr.Zero) throw new DesktopError("CLIPBOARD_WRITE_FAILED", "Clipboard was cleared but file references were refused.", new { delivery = "sent" });
                memory = IntPtr.Zero;
                var actual = ReadLockedFiles();
                if (!actual.Complete || !actual.Paths.SequenceEqual(paths, StringComparer.OrdinalIgnoreCase)) throw new DesktopError("CLIPBOARD_WRITE_FAILED", "File references were sent but their locked readback did not match.", new { delivery = "sent" });
                return GetClipboardSequenceNumber();
            }
            finally { CloseClipboard(); }
        }
        finally { if (memory != IntPtr.Zero) GlobalFree(memory); }
    }
    private static (string[] Paths, bool Complete) ReadLockedFiles()
    {
        if (!IsClipboardFormatAvailable(15)) return ([], true);
        var drop = GetClipboardData(15); if (drop == IntPtr.Zero) throw new DesktopError("CLIPBOARD_FORMAT", "File references were advertised but unreadable.");
        var count = DragQueryFile(drop, uint.MaxValue, null, 0); var paths = new List<string>(); var complete = count <= 64;
        for (uint index = 0; index < Math.Min(count, 64); index++)
        {
            var length = DragQueryFile(drop, index, null, 0);
            if (length > 2048) { complete = false; continue; }
            var path = new System.Text.StringBuilder((int)length + 1);
            if (DragQueryFile(drop, index, path, length + 1) != length) { complete = false; continue; }
            paths.Add(path.ToString());
        }
        return (paths.ToArray(), complete);
    }
    internal static object ReadClipboardFiles()
    {
        LockClipboard(IntPtr.Zero);
        try { var files = ReadLockedFiles(); return new { file_paths = files.Paths, complete = files.Complete, format = "files", sequence = GetClipboardSequenceNumber() }; }
        finally { CloseClipboard(); }
    }
    private static void LockClipboard(IntPtr owner)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!OpenClipboard(owner))
        {
            if (clock.ElapsedMilliseconds >= 600) throw new DesktopError("CLIPBOARD_BUSY", "Another process holds the clipboard. No clipboard change was sent.", new { delivery = "not_sent" });
            Thread.Sleep(20); // Only retry acquiring the lock; never repeat a write.
        }
    }
    internal static uint WriteClipboardText(string text, IntPtr owner)
    {
        IntPtr memory = IntPtr.Zero;
        try
        {
            if (text.Length > 0)
            {
                memory = GlobalAlloc(2, (nuint)((text.Length + 1) * 2));
                if (memory == IntPtr.Zero) throw new OutOfMemoryException();
                var pointer = GlobalLock(memory); if (pointer == IntPtr.Zero) throw new OutOfMemoryException();
                try { var chars = (text + '\0').ToCharArray(); Marshal.Copy(chars, 0, pointer, chars.Length); }
                finally { GlobalUnlock(memory); }
            }
            LockClipboard(owner);
            try
            {
                if (!EmptyClipboard()) throw new DesktopError("CLIPBOARD_WRITE_FAILED", "Windows refused to clear the clipboard. Inspect it before retrying.", new { delivery = "unknown" });
                if (memory != IntPtr.Zero)
                {
                    if (SetClipboardData(13, memory) == IntPtr.Zero) throw new DesktopError("CLIPBOARD_WRITE_FAILED", "Clipboard was cleared but Windows refused the text. No write was repeated.", new { delivery = "sent" });
                    memory = IntPtr.Zero; // Windows now owns the global memory.
                    if (ReadLockedText(text.Length + 1) != text) throw new DesktopError("CLIPBOARD_WRITE_FAILED", "Text was sent but its locked readback did not match.", new { delivery = "sent" });
                }
                return GetClipboardSequenceNumber();
            }
            finally { CloseClipboard(); }
        }
        finally { if (memory != IntPtr.Zero) GlobalFree(memory); }
    }
    private static string? ReadLockedText(int limit)
    {
        if (!IsClipboardFormatAvailable(13)) return null;
        var data = GetClipboardData(13);
        if (data == IntPtr.Zero) throw new DesktopError("CLIPBOARD_FORMAT", "Unicode text was advertised but not readable.");
        var pointer = GlobalLock(data);
        if (pointer == IntPtr.Zero) throw new DesktopError("CLIPBOARD_FORMAT", "Clipboard Unicode text could not be locked.");
        try
        {
            var length = (int)Math.Min((ulong)GlobalSize(data) / 2, (ulong)limit);
            var text = Marshal.PtrToStringUni(pointer, length) ?? "";
            var end = text.IndexOf('\0'); return end < 0 ? text : text[..end];
        }
        finally { GlobalUnlock(data); }
    }
    internal static object ReadClipboardText(int limit)
    {
        LockClipboard(IntPtr.Zero);
        try
        {
            var text = ReadLockedText(limit + 1);
            return new { text = text?.Length > limit ? text[..limit] + "…" : text, complete = text == null || text.Length <= limit, has_image = IsClipboardFormatAvailable(2) || IsClipboardFormatAvailable(8) || IsClipboardFormatAvailable(17), has_files = IsClipboardFormatAvailable(15), sequence = GetClipboardSequenceNumber() };
        }
        finally { CloseClipboard(); }
    }
}

using System.Diagnostics;
using System.IO;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;

namespace AgyComputerUse;

internal static partial class Native
{
    [StructLayout(LayoutKind.Sequential)] private struct Startup
    {
        internal uint Size; internal IntPtr Reserved, Desktop, Title;
        internal uint X, Y, Width, Height, CharsX, CharsY, Fill, Flags;
        internal ushort Show, ReservedBytes; internal IntPtr ReservedData, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupEx { internal Startup Info; internal IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct Started { internal IntPtr Process, Thread; internal uint Id, ThreadId; }
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { internal uint Size; internal IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] internal bool Inherit; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref nuint bytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, nuint attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity, bool inherit, uint flags, IntPtr environment, string? directory, ref StartupEx startup, out Started process);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateFile(string path, uint access, uint sharing, ref SecurityAttributes security, uint disposition, uint attributes, IntPtr template);
    [DllImport("kernel32.dll")] private static extern bool GetExitCodeProcess(IntPtr process, out uint code);
    [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(IntPtr process, out long created, out long exited, out long kernel, out long user);
    internal sealed class LaunchedProcess(IntPtr handle, int id, DateTime started) : IDisposable
    {
        internal IntPtr Handle => handle;
        internal int Id => id;
        internal DateTime StartTime => started;
        internal bool HasExited => !GetExitCodeProcess(handle, out var code) || code != 259;
        public void Dispose() => CloseHandle(handle);
    }
    private static string QuoteArgument(string value)
    {
        var result = new StringBuilder("\""); var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes); result.Append(character); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
    internal static LaunchedProcess LaunchApplication(ProcessStartInfo start)
    {
        var console = false;
        try { using var file = File.OpenRead(start.FileName); using var image = new PEReader(file); console = (int?)image.PEHeaders.PEHeader?.Subsystem == 3; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or BadImageFormatException) { }
        var count = console ? 1 : 2;
        nuint bytes = 0; InitializeProcThreadAttributeList(IntPtr.Zero, count, 0, ref bytes);
        var list = Marshal.AllocHGlobal(checked((int)bytes));
        var policy = Marshal.AllocHGlobal(4); var handles = Marshal.AllocHGlobal(IntPtr.Size);
        var variables = Marshal.StringToHGlobalUni(string.Join('\0', start.Environment.Where(item => item.Value != null).OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase).Select(item => item.Key + "=" + item.Value)) + "\0\0");
        var initialized = false; var sink = IntPtr.Zero;
        try
        {
            if (!InitializeProcThreadAttributeList(list, count, 0, ref bytes)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            initialized = true; Marshal.WriteInt32(policy, 1);
            if (!UpdateProcThreadAttribute(list, 0, 0x20012, policy, 4, IntPtr.Zero, IntPtr.Zero)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var startup = new StartupEx { Info = new Startup { Size = (uint)Marshal.SizeOf<StartupEx>() }, Attributes = list };
            if (!console)
            {
                var security = new SecurityAttributes { Size = (uint)Marshal.SizeOf<SecurityAttributes>(), Inherit = true };
                sink = CreateFile("NUL", 0xC0000000, 3, ref security, 3, 0, IntPtr.Zero);
                if (sink == new IntPtr(-1)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                Marshal.WriteIntPtr(handles, sink);
                if (!UpdateProcThreadAttribute(list, 0, 0x20002, handles, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                startup.Info.Flags = 0x100; startup.Info.Input = startup.Info.Output = startup.Info.Error = sink;
            }
            var command = new StringBuilder(QuoteArgument(start.FileName) + " " + string.Join(' ', start.ArgumentList.Select(QuoteArgument)));
            // Standard handles never point at the MCP transport. GUI output is
            // discarded; console programs get their own interactive console.
            if (!CreateProcess(start.FileName, command, IntPtr.Zero, IntPtr.Zero, !console, 0x80400u | (console ? 0x10u : 0u), variables, string.IsNullOrEmpty(start.WorkingDirectory) ? null : start.WorkingDirectory, ref startup, out var created)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            CloseHandle(created.Thread);
            if (!GetProcessTimes(created.Process, out var time, out _, out _, out _)) { CloseHandle(created.Process); throw new DesktopError("LAUNCH_UNKNOWN", "Process started but its generation could not be read. Inspect before launching again."); }
            return new(created.Process, (int)created.Id, DateTime.FromFileTimeUtc(time));
        }
        finally
        {
            if (initialized) DeleteProcThreadAttributeList(list);
            if (sink != IntPtr.Zero && sink != new IntPtr(-1)) CloseHandle(sink);
            Marshal.FreeHGlobal(variables); Marshal.FreeHGlobal(handles); Marshal.FreeHGlobal(policy); Marshal.FreeHGlobal(list);
        }
    }
}

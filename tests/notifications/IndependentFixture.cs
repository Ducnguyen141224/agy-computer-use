using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Runtime.InteropServices;

internal static class IndependentFixture
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        internal uint Size; internal IntPtr Reserved, Desktop, Title;
        internal uint X, Y, Width, Height, CharsX, CharsY, Fill, Flags;
        internal ushort Show, ReservedBytes; internal IntPtr ReservedData, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { internal StartupInfo Info; internal IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { internal IntPtr Process, Thread; internal uint Id, ThreadId; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref nuint bytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, nuint attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string? directory, ref StartupInfoEx startup, out ProcessInfo process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    private static Process Start(string reportName)
    {
        nuint bytes = 0;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref bytes);
        var attributes = Marshal.AllocHGlobal(checked((int)bytes));
        var policy = Marshal.AllocHGlobal(4);
        var initialized = false;
        try
        {
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref bytes)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            initialized = true;
            // Use the documented desktop-app identity policy for this test child.
            // No parent process, access token, job policy, or privilege is changed.
            Marshal.WriteInt32(policy, 1);
            if (!UpdateProcThreadAttribute(attributes, 0, 0x20012, policy, 4, IntPtr.Zero, IntPtr.Zero)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var startup = new StartupInfoEx { Info = new StartupInfo { Size = (uint)Marshal.SizeOf<StartupInfoEx>() }, Attributes = attributes };
            var executable = Environment.ProcessPath!;
            var command = new StringBuilder('"' + executable + "\" . --shell --result-pipe " + reportName);
            if (!CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, false, 0x08080000, IntPtr.Zero, null, ref startup, out var created)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try { var worker = Process.GetProcessById((int)created.Id); _ = worker.Handle; return worker; }
            finally { CloseHandle(created.Process); CloseHandle(created.Thread); }
        }
        finally { if (initialized) DeleteProcThreadAttributeList(attributes); Marshal.FreeHGlobal(attributes); Marshal.FreeHGlobal(policy); }
    }
    internal static int Run()
    {
        var name = "agy-notification-test-" + Guid.NewGuid().ToString("N");
        using var report = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var worker = Start(name);
        try
        {
            report.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
            using var reader = new StreamReader(report, Encoding.UTF8, false, 4096, true);
            for (;;)
            {
                var line = reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(45)).GetAwaiter().GetResult();
                if (line == null) break;
                if (line.Length > 16000) throw new Exception("Fixture report exceeded the line budget.");
                Console.WriteLine(line);
            }
            if (!worker.WaitForExit(5000)) throw new Exception("Independent fixture did not exit after closing its report pipe.");
            return worker.ExitCode;
        }
        finally { if (!worker.HasExited) worker.Kill(); }
    }
    internal static int Report(string name, string[] args)
    {
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.CurrentUserOnly);
        pipe.Connect(5000);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
        Console.SetOut(writer); Console.SetError(writer);
        try { return LiveNotificationSmoke.Run(args); }
        catch (Exception error) { Console.WriteLine(error); return 1; }
    }
}

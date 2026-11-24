using System.Runtime.InteropServices;
using System.Text;

namespace AgyComputerUse;

internal static partial class Native
{
    internal static string? ProcessUser(int pid)
    {
        var process = OpenProcess(0x1000, false, (uint)pid); if (process == IntPtr.Zero) return null;
        IntPtr token = IntPtr.Zero, buffer = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(process, 8, out token)) return null;
            GetTokenInformation(token, 1, IntPtr.Zero, 0, out var length);
            if (length == 0 || length > 65536) return null;
            buffer = Marshal.AllocHGlobal((int)length);
            if (!GetTokenInformation(token, 1, buffer, length, out _)) return null;
            return new System.Security.Principal.SecurityIdentifier(Marshal.ReadIntPtr(buffer)).Value;
        }
        finally { if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer); if (token != IntPtr.Zero) CloseHandle(token); CloseHandle(process); }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetCurrentPackageFullName(ref uint count, StringBuilder? name);
    internal static string? PackageIdentity()
    {
        uint count = 0;
        if (GetCurrentPackageFullName(ref count, null) != 122 || count == 0 || count > 32768) return null;
        var name = new StringBuilder((int)count);
        return GetCurrentPackageFullName(ref count, name) == 0 ? name.ToString() : null;
    }
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("advapi32.dll")] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll")] private static extern bool GetTokenInformation(IntPtr token, int kind, IntPtr info, uint size, out uint needed);
    [DllImport("advapi32.dll")] private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
    [DllImport("advapi32.dll")] private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);
    [DllImport("kernel32.dll")] private static extern bool QueryInformationJobObject(IntPtr job, int kind, out uint info, uint size, out uint written);
    internal static uint? JobUiLimits() => QueryInformationJobObject(IntPtr.Zero, 4, out var flags, sizeof(uint), out _) ? flags : null;
    internal static object TokenFlags(int pid) => new { restricted = TokenFlag(pid, 21), ui_access = TokenFlag(pid, 26), app_container = TokenFlag(pid, 29), session_id = TokenFlag(pid, 12) };
    private static int? TokenFlag(int pid, int kind)
    {
        var process = OpenProcess(0x1000, false, (uint)pid);
        if (process == IntPtr.Zero) return null;
        IntPtr token = IntPtr.Zero, buffer = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(process, 8, out token)) return null;
            buffer = Marshal.AllocHGlobal(4);
            Marshal.WriteInt32(buffer, 0);
            return GetTokenInformation(token, kind, buffer, 4, out _) ? Marshal.ReadInt32(buffer) : null;
        }
        finally { if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer); if (token != IntPtr.Zero) CloseHandle(token); CloseHandle(process); }
    }
    internal static int? Integrity(int pid)
    {
        var process = OpenProcess(0x1000, false, (uint)pid);
        if (process == IntPtr.Zero) return null;
        IntPtr token = IntPtr.Zero, buffer = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(process, 8, out token)) return null;
            GetTokenInformation(token, 25, IntPtr.Zero, 0, out var size);
            if (size == 0 || size > 65536) return null;
            buffer = Marshal.AllocHGlobal((int)size);
            if (!GetTokenInformation(token, 25, buffer, size, out _)) return null;
            var sid = Marshal.ReadIntPtr(buffer);
            var count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
            return count == 0 ? null : Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)count - 1));
        }
        finally { if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer); if (token != IntPtr.Zero) CloseHandle(token); CloseHandle(process); }
    }
    internal static void RequireInputPrivilege(int pid)
    {
        var current = Integrity(Environment.ProcessId);
        var target = Integrity(pid);
        if (current != null && target > current) throw new DesktopError("PERMISSION_DENIED", "The target has a higher Windows integrity level. Input was not sent.", new { helper_integrity = current, target_integrity = target });
    }
}

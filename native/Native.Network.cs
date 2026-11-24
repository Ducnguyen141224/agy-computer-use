using System.Net;
using System.Runtime.InteropServices;

namespace AgyComputerUse;

internal static partial class Native
{
    [DllImport("iphlpapi.dll")] private static extern uint GetExtendedTcpTable(IntPtr table, ref uint size, bool sorted, int family, int kind, uint reserved);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        internal uint Size, Usage, Id; internal UIntPtr Heap; internal uint Module, Threads, Parent;
        internal int Priority; internal uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string Executable;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint process);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);
    internal sealed record Listener(int Pid, bool Loopback);
    internal static Listener[] ListeningPort(int port)
    {
        var result = new List<Listener>();
        foreach (var family in new[] { 2, 23 })
        {
            uint size = 0; _ = GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, 3, 0);
            if (size < 4 || size > 1048576) throw new DesktopError("NETWORK_METADATA_UNAVAILABLE", "The bounded TCP ownership table is unavailable.");
            var memory = Marshal.AllocHGlobal((int)size);
            try
            {
                if (GetExtendedTcpTable(memory, ref size, false, family, 3, 0) != 0) throw new DesktopError("NETWORK_METADATA_UNAVAILABLE", "Windows could not verify TCP endpoint ownership.");
                var count = Marshal.ReadInt32(memory);
                var stride = family == 2 ? 24 : 56;
                if (count < 0 || (long)count * stride + 4 > size) throw new DesktopError("NETWORK_METADATA_UNAVAILABLE", "Windows returned an invalid TCP table size.");
                for (var index = 0; index < count; index++)
                {
                    var row = memory + 4 + index * stride;
                    var encodedPort = Marshal.ReadInt32(row, family == 2 ? 8 : 20);
                    var actualPort = ((encodedPort & 255) << 8) | ((encodedPort >> 8) & 255);
                    if (actualPort != port) continue;
                    var address = new byte[family == 2 ? 4 : 16];
                    Marshal.Copy(row + (family == 2 ? 4 : 0), address, 0, address.Length);
                    result.Add(new(Marshal.ReadInt32(row, family == 2 ? 20 : 52), IPAddress.IsLoopback(new IPAddress(address))));
                }
            }
            finally { Marshal.FreeHGlobal(memory); }
        }
        return result.ToArray();
    }
    internal static bool IsDescendantProcess(int candidate, int root)
    {
        if (candidate == root) return true;
        var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == new IntPtr(-1)) return false;
        try
        {
            var parents = new Dictionary<uint, uint>();
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            if (Process32First(snapshot, ref entry))
                do { parents[entry.Id] = entry.Parent; } while (parents.Count < 10000 && Process32Next(snapshot, ref entry));
            for (var depth = 0; candidate != 0 && depth < 40; depth++)
            {
                if (candidate == root) return true;
                if (!parents.TryGetValue((uint)candidate, out var parent) || parent == candidate) return false;
                candidate = (int)parent;
            }
            return false;
        }
        finally { CloseHandle(snapshot); }
    }
}

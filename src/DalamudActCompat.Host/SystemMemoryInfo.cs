using System.Runtime.InteropServices;

namespace DalamudActCompat.Host;

internal static class SystemMemoryInfo
{
    public static long GetAvailablePhysicalMemoryBytes()
    {
        var status = new MemoryStatusEx
        {
            Length = checked((uint)Marshal.SizeOf<MemoryStatusEx>()),
        };
        if (!GlobalMemoryStatusEx(ref status))
        {
            return 0;
        }

        return status.AvailablePhysicalMemory > long.MaxValue
            ? long.MaxValue
            : checked((long)status.AvailablePhysicalMemory);
    }

    // The Host is Windows-only. Querying the kernel avoids treating the managed GC budget
    // as system headroom when deciding whether a leaking compatibility process is urgent.
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysicalMemory;
        public ulong AvailablePhysicalMemory;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }
}

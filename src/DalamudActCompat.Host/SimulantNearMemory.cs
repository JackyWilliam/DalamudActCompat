using System.Buffers.Binary;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace DalamudActCompat.Host;

internal static class SimulantNearMemory
{
    internal static IntPtr Allocate(IntPtr process, IntPtr entry, int size)
    {
        if (size is <= 0 or > 4096) throw new ArgumentOutOfRangeException(nameof(size));
        // A five-byte entry jump leaves the existing hook's continuation at +7/+10
        // intact. Its cave must therefore be within signed rel32 reach of the entry.
        const long granularity = 0x10000; // Windows x64 allocation granularity.
        var low = Math.Max(granularity, entry.ToInt64() - 0x7FFF0000);
        var high = Math.Min(0x7FFFFFFEFFFF, entry.ToInt64() + 0x7FFF0000);
        var requirements = Marshal.AllocHGlobal(24);
        try
        {
            Marshal.WriteInt64(requirements, 0, (low + granularity - 1) & -granularity);
            Marshal.WriteInt64(requirements, 8, (high & -granularity) - 1);
            Marshal.WriteInt64(requirements, 16, 0);
            var parameter = new ExtendedParameter { Type = 1, Pointer = requirements };
            var address = VirtualAlloc2(process, IntPtr.Zero, 4096, 0x3000, 0x40, ref parameter, 1);
            if (address == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法分配发包 Hook 附近的代码洞。");
            return address;
        }
        finally { Marshal.FreeHGlobal(requirements); }
    }

    internal static byte[] CreateEntryPatch(IntPtr entry, IntPtr cave)
    {
        var displacement = cave.ToInt64() - entry.ToInt64() - 5;
        if (displacement is < int.MinValue or > int.MaxValue)
            throw new InvalidDataException("发包 Hook 代码洞超出短入口补丁范围。");
        var patch = new byte[5];
        patch[0] = 0xE9;
        BinaryPrimitives.WriteInt32LittleEndian(patch.AsSpan(1), (int)displacement);
        return patch;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedParameter { internal ulong Type; internal IntPtr Pointer; }

    [DllImport("KernelBase.dll", SetLastError = true)]
    private static extern IntPtr VirtualAlloc2(IntPtr process, IntPtr address, nuint size,
        uint allocationType, uint protection, ref ExtendedParameter parameter, uint count);
}

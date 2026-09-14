using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using DalamudActCompat.Host;
using Mono.Cecil;
using System.Runtime.InteropServices;

internal static class SimulantEntryPointSmokeTests
{
    internal static void Run()
    {
        RunNativeHookChain();
        var header = new byte[256];
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0x3C), 128);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(176), 0x140000000);
        var liveHeader = header.ToArray();
        var moduleBase = new IntPtr(0x7FF603240000);
        BinaryPrimitives.WriteInt64LittleEndian(liveHeader.AsSpan(176), moduleBase.ToInt64());
        SimulantEntryPointCompatibility.ValidateImageHeaders(header, liveHeader, moduleBase);
        liveHeader[100] = 1;
        Reject(() => SimulantEntryPointCompatibility.ValidateImageHeaders(header, liveHeader, moduleBase));
        liveHeader[100] = 0;
        BinaryPrimitives.WriteInt64LittleEndian(liveHeader.AsSpan(176), moduleBase.ToInt64() + 1);
        Reject(() => SimulantEntryPointCompatibility.ValidateImageHeaders(header, liveHeader, moduleBase));
        var original = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
        var address = new IntPtr(0x100000);
        const long destination = 0x200000;
        var live = original.ToArray();
        byte[] Read(IntPtr pointer, int count)
        {
            if (pointer.ToInt64() >= address.ToInt64() && pointer.ToInt64() + count <= address.ToInt64() + live.Length)
                return live.AsSpan((int)(pointer.ToInt64() - address.ToInt64()), count).ToArray();
            if (pointer.ToInt64() == destination && count == 1) return [0xC3];
            if (pointer.ToInt64() == 0x3FF00788 && count == 8) return BitConverter.GetBytes(destination);
            throw new IOException("Unreadable fixture address.");
        }
        SimulantEntryPointCompatibility.ValidateEntry(original, live, address, Read);
        live[0] = 0xE9;
        BinaryPrimitives.WriteInt32LittleEndian(live.AsSpan(1), (int)(destination - address.ToInt64() - 5));
        ValidateJump();
        live = original.ToArray();
        live[0] = 0xFF; live[1] = 0x25;
        live.AsSpan(2, 4).Clear();
        BinaryPrimitives.WriteInt64LittleEndian(live.AsSpan(6), destination);
        ValidateJump();
        live = original.ToArray();
        new byte[] { 0xFF, 0x24, 0x25, 0x88, 0x07, 0xF0, 0x3F }.CopyTo(live, 0);
        ValidateJump();
        live[20] ^= 0xFF;
        Reject(() => SimulantEntryPointCompatibility.ValidateEntry(original, live, address, Read));
        live = original.ToArray(); live[0] = 0xCC;
        Reject(() => SimulantEntryPointCompatibility.ValidateEntry(original, live, address, Read));
        var unique = SimulantEntryPointCompatibility.FindUniqueOffset(original, "05 06 ?? 08 09 0A 0B 0C");
        if (unique != 5) throw new InvalidOperationException("Wildcard scan lost its unique offset.");
        if (SimulantEntryPointCompatibility.FindUniqueOffset(original, "FF FE FD FC FB FA F9 F8") != -1)
            throw new InvalidOperationException("Missing signature was accepted.");
        Reject(() => SimulantEntryPointCompatibility.FindUniqueOffset([.. original, .. original], "05 06 ?? 08 09 0A 0B 0C"));
        Console.WriteLine("PASS: Simulant hooked-entry validation, relative/indirect jump relocation, unchanged backups, and signature rejection.");
        RunGameImage();

        void ValidateJump()
        {
            var backup = live.ToArray();
            SimulantEntryPointCompatibility.ValidateEntry(original, live, address, Read);
            var jump = SimulantEntryPointCompatibility.RelocateJump(live.AsSpan(0, 15), address, Read);
            if (jump.Length != 14 || jump[0] != 0xFF || jump[1] != 0x25 ||
                BinaryPrimitives.ReadInt64LittleEndian(jump.AsSpan(6)) != destination || !live.SequenceEqual(backup))
                throw new InvalidOperationException("Relocated hook changed its target or restoration backup.");
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NativeSend(IntPtr context, IntPtr packet);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFree(IntPtr address, nuint size, uint type);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FlushInstructionCache(IntPtr process, IntPtr address, nuint size);

    private static void RunNativeHookChain()
    {
        // Execute only a synthetic function in this test process. The old 15-byte patch
        // corrupts the +10 continuation; the near jump must preserve a real call/return.
        var memory = SimulantNearMemory.Allocate(new IntPtr(-1), new IntPtr(0x140000000), 4096);
        var slot = IntPtr.Zero;
        try
        {
            slot = SimulantNearMemory.Allocate(new IntPtr(-1), new IntPtr(0x10000), 4096);
            var entry = memory + 0x100;
            var existingHook = memory + 0x200;
            var trampoline = memory + 0x300;
            var cave = memory + 0x400;
            var packet = memory + 0x500;
            byte[] prologue = [0x48, 0x89, 0x5C, 0x24, 0x08, 0x48, 0x89, 0x74, 0x24, 0x10, 0x4C, 0x89, 0x64, 0x24, 0x18];
            byte[] function = [.. prologue, 0xB8, 42, 0, 0, 0, 0xC3];
            void Write(IntPtr at, byte[] bytes)
            {
                Marshal.Copy(bytes, 0, at, bytes.Length);
                if (!FlushInstructionCache(new IntPtr(-1), at, (nuint)bytes.Length)) throw new InvalidOperationException("Fixture instruction cache flush failed.");
            }
            byte[] Read(IntPtr at, int length) { var bytes = new byte[length]; Marshal.Copy(at, bytes, 0, length); return bytes; }
            Write(entry, function);
            Write(existingHook, SimulantNearMemory.CreateEntryPatch(existingHook, trampoline));
            Write(trampoline, [.. prologue[..10], .. SimulantNearMemory.CreateEntryPatch(trampoline + 10, entry + 10)]);
            Marshal.WriteIntPtr(slot, existingHook);
            Write(entry, [0xFF, 0x24, 0x25, .. BitConverter.GetBytes(checked((int)slot.ToInt64())), 0x90, 0x90, 0x90]);
            var backup = Read(entry, 15);
            var relocated = SimulantEntryPointCompatibility.RelocateJump(backup, entry, Read);
            // Same heartbeat predicate and block/pass paths as the upstream cave.
            Write(cave, [0x66, 0x81, 0x3A, 0x10, 0x02, 0x75, (byte)relocated.Length, .. relocated, 0x31, 0xC0, 0xC3]);
            var send = Marshal.GetDelegateForFunctionPointer<NativeSend>(entry);
            Marshal.WriteInt16(packet, 0x210);
            if (send(IntPtr.Zero, packet) != 42) throw new InvalidOperationException("Original hook fixture failed.");
            Write(entry, SimulantNearMemory.CreateEntryPatch(entry, cave));
            if (!Read(entry + 5, 10).SequenceEqual(backup[5..])) throw new InvalidOperationException("Existing trampoline continuation was overwritten.");
            if (send(IntPtr.Zero, packet) != 42) throw new InvalidOperationException("Heartbeat lost the existing native hook chain.");
            Marshal.WriteInt16(packet, 0x211);
            if (send(IntPtr.Zero, packet) != 0) throw new InvalidOperationException("Non-heartbeat packet was not blocked.");
            Write(entry, backup);
            if (send(IntPtr.Zero, packet) != 42 || !Read(entry, 15).SequenceEqual(backup))
                throw new InvalidOperationException("Native send entry did not restore.");
            Reject(() => SimulantNearMemory.CreateEntryPatch(entry, new IntPtr(entry.ToInt64() + 0x100000000L)));
            Console.WriteLine("PASS: native heartbeat/block/restore through an existing hook returning at entry +10, in the test process only.");
        }
        finally
        {
            var slotReleased = slot == IntPtr.Zero || VirtualFree(slot, 0, 0x8000);
            if (!VirtualFree(memory, 0, 0x8000) || !slotReleased) throw new InvalidOperationException("Fixture allocation did not release.");
        }
    }

    private static void RunGameImage()
    {
        var gamePath = Environment.GetEnvironmentVariable("ACTCOMPAT_SIMULANT_GAME_EXE");
        var dllPath = Environment.GetEnvironmentVariable("ACTCOMPAT_SIMULANT_DLL");
        if (string.IsNullOrEmpty(gamePath) || string.IsNullOrEmpty(dllPath)) return;
        using var game = File.OpenRead(gamePath);
        using var pe = new PEReader(game);
        var section = pe.PEHeaders.SectionHeaders.Single(s => s.Name == ".text");
        var code = pe.GetSectionData(section.VirtualAddress).GetContent().ToArray();
        using var dll = AssemblyDefinition.ReadAssembly(dllPath);
        var properties = dll.MainModule.Types.SelectMany(type => type.Properties).Where(property =>
            property.DeclaringType.FullName + "." + property.Name is
                "Simulant.Game.AddressStore.OnSendPacketFuncPtr" or
                "Simulant.Game.FFCS.Client.Game.ActionManager.UpdateFuncPtr").ToArray();
        if (properties.Length != 2) throw new InvalidOperationException("Original entry-point fixture changed.");
        foreach (var property in properties)
        {
            var attribute = property.CustomAttributes.Single(a => a.AttributeType.Name == "SigPatternAttribute");
            var pattern = (string)((CustomAttributeArgument[])attribute.ConstructorArguments[0].Value)[0].Value;
            var offset = SimulantEntryPointCompatibility.FindUniqueOffset(code, pattern);
            if (offset < 0) throw new InvalidOperationException("Original game signature is absent.");
            var address = new IntPtr(0x140000000L + section.VirtualAddress + offset);
            var original = code.AsSpan(offset, 64).ToArray();
            var live = original.ToArray();
            var target = address.ToInt64() + 0x100000;
            live[0] = 0xE9;
            BinaryPrimitives.WriteInt32LittleEndian(live.AsSpan(1), (int)(target - address.ToInt64() - 5));
            // Simulate the entry patch in a private byte array, never in the game process.
            SimulantEntryPointCompatibility.ValidateEntry(original, live, address,
                (pointer, count) => pointer.ToInt64() == target && count == 1 ? [0xC3] : throw new IOException());
            Console.WriteLine($"PASS: installed game image {property.Name}, unique RVA 0x{section.VirtualAddress + offset:X}, simulated entry hook accepted.");
        }
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Unsafe signature or entry was accepted.");
    }
}

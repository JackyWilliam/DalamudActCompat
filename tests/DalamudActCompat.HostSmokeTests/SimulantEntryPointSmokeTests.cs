using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using DalamudActCompat.Host;
using Mono.Cecil;

internal static class SimulantEntryPointSmokeTests
{
    internal static void Run()
    {
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

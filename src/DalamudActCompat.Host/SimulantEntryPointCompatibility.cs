using System.Buffers.Binary;
using System.IO;
using System.Reflection;
using System.Reflection.PortableExecutable;

namespace DalamudActCompat.Host;

internal static class SimulantEntryPointCompatibility
{
    private static readonly string[] RecoverableProperties =
    [
        "Simulant.Game.AddressStore.OnSendPacketFuncPtr",
        "Simulant.Game.FFCS.Client.Game.ActionManager.UpdateFuncPtr",
    ];

    internal static void Recover(Dictionary<string, string> result)
    {
        foreach (var key in RecoverableProperties.Where(key =>
                     result.TryGetValue(key, out var error) && !string.IsNullOrEmpty(error)))
        {
            try
            {
                SimulantCompatibility.EnsureNativeAccess();
                var assembly = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "Simulant");
                var separator = key.LastIndexOf('.');
                var property = assembly.GetType(key[..separator])!.GetProperty(key[(separator + 1)..])!;
                var attribute = property.CustomAttributes.Single(a => a.AttributeType.Name == "SigPatternAttribute");
                var patterns = ((IEnumerable<CustomAttributeTypedArgument>)attribute.ConstructorArguments[0].Value!)
                    .Select(value => (string)value.Value!).ToArray();
                var process = HostPluginBridge.FfxivRepository.GetCurrentFFXIVProcess()
                              ?? throw new InvalidOperationException("游戏进程尚未就绪。");
                var module = process.MainModule ?? throw new InvalidOperationException("游戏模块尚未就绪。");
                using var file = File.OpenRead(module.FileName);
                using var pe = new PEReader(file);
                var header = pe.PEHeaders.PEHeader ?? throw new InvalidDataException("游戏 PE 头无效。");
                var headerSize = header.SizeOfHeaders;
                if (headerSize <= 0 || headerSize > 65536 || header.SizeOfImage != module.ModuleMemorySize)
                    throw new InvalidDataException("运行中的游戏与磁盘映像大小不一致。");
                file.Position = 0;
                var headers = new byte[headerSize];
                file.ReadExactly(headers);
                if (header.Magic != PEMagic.PE32Plus) throw new InvalidDataException("游戏不是 x64 映像。");
                ValidateImageHeaders(headers, ReadMemory(module.BaseAddress, headerSize), module.BaseAddress);
                var section = pe.PEHeaders.SectionHeaders.Single(s => s.Name == ".text");
                var code = pe.GetSectionData(section.VirtualAddress).GetContent().ToArray();
                var offsets = patterns.Select(pattern => FindUniqueOffset(code, pattern)).Distinct().ToArray();
                var valid = offsets.Where(offset => offset >= 0).ToArray();
                if (valid.Length != 1) throw new InvalidDataException("原始代码中没有唯一签名匹配。");
                var offset = valid[0];
                if (offset > code.Length - 64) throw new InvalidDataException("签名距离代码段末尾过近。");
                var address = module.BaseAddress + section.VirtualAddress + offset;
                ValidateEntry(code.AsSpan(offset, 64), ReadMemory(address, 64), address, ReadMemory);
                property.SetValue(null, address);
                result[key] = null!;
                Console.WriteLine($"Simulant resolved {key} from matching game image; live entry and body verified.");
            }
            catch (Exception exception)
            {
                result[key] += "；入口兼容检查失败：" + exception.GetBaseException().Message;
            }
        }
    }

    internal static byte[] ReadMemory(IntPtr address, int count)
    {
        dynamic plugin = Advanced_Combat_Tracker.ActGlobals.oFormActMain.ActPlugins
            .Select(p => p.pluginObj).First(p => p?.GetType().FullName == "PostNamazu.PostNamazu")!;
        byte[] bytes = plugin.Memory.ReadBytes(address, count);
        if (bytes.Length != count) throw new IOException("未能完整读取游戏函数。");
        return bytes;
    }

    internal static void ValidateImageHeaders(ReadOnlySpan<byte> disk, ReadOnlySpan<byte> live, IntPtr moduleBase)
    {
        if (disk.Length < 64 || disk.Length != live.Length) throw new InvalidDataException("游戏 PE 头长度不一致。");
        var imageBaseOffset = checked(BinaryPrimitives.ReadInt32LittleEndian(disk[0x3C..]) + 48);
        if (imageBaseOffset < 64 || imageBaseOffset > disk.Length - 8) throw new InvalidDataException("游戏 PE 基址字段无效。");
        var diskBase = BinaryPrimitives.ReadInt64LittleEndian(disk[imageBaseOffset..]);
        var liveBase = BinaryPrimitives.ReadInt64LittleEndian(live[imageBaseOffset..]);
        // The live loader can record its ASLR address in ImageBase. Accept only that
        // observed module address (or the original preference); every other byte must match.
        if ((liveBase != diskBase && liveBase != moduleBase.ToInt64()) ||
            !disk[..imageBaseOffset].SequenceEqual(live[..imageBaseOffset]) ||
            !disk[(imageBaseOffset + 8)..].SequenceEqual(live[(imageBaseOffset + 8)..]))
            throw new InvalidDataException("运行中的游戏与磁盘 PE 头不一致。");
    }

    internal static int FindUniqueOffset(ReadOnlySpan<byte> code, string pattern)
    {
        var bytes = pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token is "?" or "??" ? -1 : Convert.ToInt32(token, 16)).ToArray();
        if (bytes.Length < 8 || bytes.All(value => value < 0))
            throw new InvalidDataException("函数入口签名无效。");
        var found = -1;
        for (var offset = 0; offset <= code.Length - bytes.Length; offset++)
        {
            var matches = true;
            for (var index = 0; index < bytes.Length; index++)
                if (bytes[index] >= 0 && code[offset + index] != bytes[index]) { matches = false; break; }
            if (!matches) continue;
            if (found >= 0) throw new InvalidDataException("函数入口签名存在多个匹配。");
            found = offset;
        }
        return found;
    }

    internal static void ValidateEntry(ReadOnlySpan<byte> original, ReadOnlySpan<byte> live,
        IntPtr address, Func<IntPtr, int, byte[]> read)
    {
        if (original.Length < 64 || live.Length != original.Length)
            throw new InvalidDataException("函数入口验证数据不足。");
        if (original.SequenceEqual(live)) return;
        // Only the stolen entry bytes may differ. This is not a fallback for a different
        // game build or arbitrary patches in the function body.
        if (!original[15..].SequenceEqual(live[15..]))
            throw new InvalidDataException("函数体与原始代码不一致。");
        _ = RelocateJump(live[..15], address, read);
    }

    internal static byte[] RelocateJump(ReadOnlySpan<byte> entry, IntPtr address,
        Func<IntPtr, int, byte[]> read)
    {
        long destination;
        if (entry.Length >= 5 && entry[0] == 0xE9)
            destination = checked(address.ToInt64() + 5 + BinaryPrimitives.ReadInt32LittleEndian(entry[1..5]));
        else if (entry.Length >= 14 && entry[0] == 0xFF && entry[1] == 0x25)
        {
            var slot = checked(address.ToInt64() + 6 + BinaryPrimitives.ReadInt32LittleEndian(entry[2..6]));
            destination = BinaryPrimitives.ReadInt64LittleEndian(read(new IntPtr(slot), 8));
        }
        else if (entry.Length >= 7 && entry[0] == 0xFF && entry[1] == 0x24 && entry[2] == 0x25)
        {
            // Dalamud's live x64 hook uses a SIB absolute disp32 pointer slot. It is
            // neither RIP-relative FF25 nor an inline target address.
            var slot = BinaryPrimitives.ReadInt32LittleEndian(entry[3..7]);
            destination = BinaryPrimitives.ReadInt64LittleEndian(read(new IntPtr(slot), 8));
        }
        else throw new InvalidDataException("不支持的函数入口修改，模拟保持关闭。");
        if (destination <= 0 || (destination >= address.ToInt64() && destination < address.ToInt64() + 15))
            throw new InvalidDataException("函数入口跳转目标无效。");
        if (read(new IntPtr(destination), 1).Length != 1) throw new IOException("函数跳转目标不可读。");
        // Relative E9/RIP-indirect jumps cannot be copied verbatim into the remote cave.
        // An absolute jump preserves the existing hook chain without relocating its code.
        var jump = new byte[14];
        jump[0] = 0xFF;
        jump[1] = 0x25;
        BinaryPrimitives.WriteInt64LittleEndian(jump.AsSpan(6), destination);
        return jump;
    }
}

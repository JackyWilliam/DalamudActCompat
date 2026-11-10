using System.Buffers.Binary;
using System.Reflection;
using IINACT.Network;
using Machina.FFXIV;
using Machina.FFXIV.Headers.Opcodes;
using Unscrambler.Constants;
using Unscrambler.Unscramble.Versions;

internal static class Patch758SmokeTests
{
    public static void Run()
    {
        // CN, Global and KR all shipped 7.58 as the same 2026.11.03 build.
        const string version = "2026.11.03.0000.0000";
        // Measured from the CN 7.58 ffxiv_dx11.exe: OnReceivePacket indexes a 34-entry key
        // table at RVA 0x231F9A0 (followed directly by module pointers).
        const uint keyTableOffset = 0x231F9A0;
        const int tableSize = 34 * sizeof(int);

        // Unscrambler.XIV 7.57.1 has no 7.58 profile. Until it publishes one, no region may
        // claim the ranking-safe path; the parser must take the warned runtime fallback.
        Require(!VersionConstants.Constants.ContainsKey(version),
            "Unscrambler now ships a 7.58 profile; replace the fallback expectations with its published constants.");
        var hookType = typeof(ZoneDownHookManager);
        var bundledPolicy = hookType.GetMethod("CanUseBundledVersionConstants", BindingFlags.NonPublic | BindingFlags.Static)!;
        var regionalPolicy = hookType.GetMethod("CanUseChineseRuntimeVersionConstants", BindingFlags.NonPublic | BindingFlags.Static)!;
        Require(bundledPolicy.Invoke(null, [GameRegion.Global, version]) is false &&
                bundledPolicy.Invoke(null, [GameRegion.Chinese, version]) is false &&
                regionalPolicy.Invoke(null, [GameRegion.Chinese, version, tableSize]) is false,
            "7.58 was treated as a verified Unscrambler profile without published constants.");

        // The fallback restores exactly the packets Machina parses, so its obfuscated opcodes
        // must be the 7.58 Machina values in every region.
        var expected = new Dictionary<string, int>
        {
            ["PlayerSpawn"] = 0x2FC, ["NpcSpawn"] = 0x176, ["NpcSpawn2"] = 0x2FD,
            ["ActionEffect01"] = 0x190, ["ActionEffect08"] = 0x233,
            ["ActionEffect16"] = 0x119, ["ActionEffect24"] = 0x39C, ["ActionEffect32"] = 0xDD,
            ["StatusEffectList"] = 0x15D, ["StatusEffectList3"] = 0x35D,
            ["ActorControl"] = 0x2DF, ["ActorCast"] = 0x160,
        };
        var previousRegion = OpcodeManager.Instance.GameRegion;
        try
        {
            foreach (var region in new[] { GameRegion.Chinese, GameRegion.Global, GameRegion.Korean })
            {
                OpcodeManager.Instance.SetRegion(region);
                var constants = ZoneDownHookManager.GetFallbackVersionConstant(keyTableOffset, tableSize);
                Require(constants.OpcodeKeyTableOffset == keyTableOffset && constants.OpcodeKeyTableSize == tableSize &&
                        expected.All(pair => constants.ObfuscatedOpcodes[pair.Key] == pair.Value),
                    $"{region} 7.58 fallback constants do not match the Machina 7.58 packets.");
                ValidateSyntheticDecode(region, constants);
            }
        }
        finally
        {
            OpcodeManager.Instance.SetRegion(previousRegion);
        }
        Console.WriteLine("7.58 CN/Global/KR: no unverified Unscrambler profile, Machina-aligned fallback and synthetic ActorCast/ActionEffect decoding passed.");
    }

    private static void ValidateSyntheticDecode(GameRegion region, VersionConstants constants)
    {
        var unscrambler = new Unscrambler73();
        unscrambler.Initialize(constants);
        var keys = new byte[] { 7, 11, 19 };
        var table = Enumerable.Range(0, constants.OpcodeKeyTableSize / sizeof(int))
            .Select(index => 0x1234 + index * 17)
            .ToArray();
        // Same encoding as the 7.57 regression: compare whole packets so opcode selection,
        // 34-entry table indexing and collateral writes are all covered.
        foreach (var actionId in new uint[] { 0x1E, 0x2B, 0xE36, 0x3F18, 0x123456 })
        foreach (var (opcode, fieldOffset) in new[] { ((ushort)0x160, 20), ((ushort)0x190, 24) })
        {
            var expectedPacket = Enumerable.Repeat((byte)0xA5, 128).ToArray();
            BinaryPrimitives.WriteUInt16LittleEndian(expectedPacket.AsSpan(2), opcode);
            BinaryPrimitives.WriteUInt32LittleEndian(expectedPacket.AsSpan(fieldOffset), actionId);
            var isEffect = opcode == 0x190;
            if (isEffect)
                for (var index = 0; index < 8; index++)
                    BinaryPrimitives.WriteUInt16LittleEndian(expectedPacket.AsSpan(64 + index * 8), (ushort)(1234 + index * 2395));

            var encoded = (byte[])expectedPacket.Clone();
            var baseKey = keys[opcode % 3];
            var opcodeKey = table[(opcode + baseKey) % table.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(encoded.AsSpan(fieldOffset), actionId + baseKey);
            if (isEffect)
                for (var index = 0; index < 8; index++)
                {
                    var field = encoded.AsSpan(64 + index * 8);
                    BinaryPrimitives.WriteUInt16LittleEndian(field,
                        (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(field) ^ (ushort)(baseKey + opcodeKey)));
                }
            unscrambler.Unscramble(encoded, keys[0], keys[1], keys[2], table);
            Require(encoded.AsSpan().SequenceEqual(expectedPacket),
                $"{region} 7.58 opcode {opcode:X} did not restore action {actionId:X}/damage fields exactly.");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}

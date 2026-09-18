using System.Buffers.Binary;
using System.Reflection;
using DalamudActCompat.Plugin;
using DalamudActCompat.Protocol;
using IINACT.Network;
using Machina.FFXIV;
using Machina.FFXIV.Headers.Opcodes;
using Unscrambler;
using Unscrambler.Constants;
using Unscrambler.Unscramble.Versions;

internal static class Patch756SmokeTests
{
    public static void Run()
    {
        const string version = "2026.09.15.0000.0000";
        const int tableSize = 165 * sizeof(int);
        const uint regionalOffset = 0x123400;
        var bundled = VersionConstants.ForGameVersion(version);
        var expected = new Dictionary<string, int>
        {
            ["PlayerSpawn"] = 0x1C4, ["NpcSpawn"] = 0x20C, ["NpcSpawn2"] = 0x1BD,
            ["ActionEffect01"] = 0x313, ["ActionEffect08"] = 0x21C,
            ["ActionEffect16"] = 0x8C, ["ActionEffect24"] = 0x30A, ["ActionEffect32"] = 0x3AA,
            ["StatusEffectList"] = 0x83, ["StatusEffectList3"] = 0x3DC,
            ["Examine"] = 0x1F2, ["UpdateGearset"] = 0x10F, ["UpdateParty"] = 0x191,
            ["ActorControl"] = 0x25F, ["ActorCast"] = 0x162,
            ["UnknownEffect01"] = 0x3BD, ["UnknownEffect16"] = 0x27D,
            ["ActionEffect02"] = 0x30B, ["ActionEffect04"] = 0x292,
        };
        Require(bundled.OpcodeKeyTableSize == tableSize && bundled.OpcodeKeyTableOffset == 0x2312740 &&
                bundled.InitZoneOpcode == 0x32B && bundled.UnknownObfuscationInitOpcode == 0x2A4 &&
                bundled.ObfuscationEnabledMode == 176 &&
                expected.Count == bundled.ObfuscatedOpcodes.Count &&
                expected.All(pair => bundled.ObfuscatedOpcodes[pair.Key] == pair.Value),
            "Unscrambler 7.56h profile differs from the published constants.");
        Require(UnscramblerFactory.ForGameVersion(version) is Unscrambler73,
            "The official 7.56h factory cannot initialize its embedded resources.");

        var hookType = typeof(ZoneDownHookManager);
        var bundledPolicy = hookType.GetMethod("CanUseBundledVersionConstants", BindingFlags.NonPublic | BindingFlags.Static)!;
        var regionalPolicy = hookType.GetMethod("CanUseChineseRuntimeVersionConstants", BindingFlags.NonPublic | BindingFlags.Static)!;
        var regionalFactory = hookType.GetMethod("GetChineseRuntimeVersionConstant", BindingFlags.NonPublic | BindingFlags.Static)!;
        Require(bundledPolicy.Invoke(null, [GameRegion.Global, version]) is true &&
                bundledPolicy.Invoke(null, [GameRegion.Chinese, version]) is false &&
                regionalPolicy.Invoke(null, [GameRegion.Chinese, version, tableSize]) is true,
            "7.56h selected a fallback or reused Global memory addresses on CN.");
        foreach (var size in new[] { tableSize - 4, tableSize + 4, 193 * 4, 89 * 4 })
            Require(regionalPolicy.Invoke(null, [GameRegion.Chinese, version, size]) is false,
                "CN 7.56h accepted a mismatched key table, including the old 7.55 size.");
        Require(regionalPolicy.Invoke(null, [GameRegion.Chinese, "2099.01.01.0000.0000", tableSize]) is false,
            "An unknown patch was treated as verified 7.56h.");

        // A made-up regional RVA proves the factory uses the scanned address rather than
        // accidentally borrowing the Global executable's address from the NuGet resource.
        var regional = (VersionConstants)regionalFactory.Invoke(null, [version, regionalOffset, tableSize])!;
        new Unscrambler73().Initialize(regional);
        Require(regional.OpcodeKeyTableOffset == regionalOffset && regional.OpcodeKeyTableSize == tableSize &&
                regional.TableOffsets.Length == 0 && regional.MidTableOffset == 0 && regional.DayTableOffset == 0 &&
                expected.Count == regional.ObfuscatedOpcodes.Count &&
                expected.All(pair => regional.ObfuscatedOpcodes[pair.Key] == pair.Value),
            "CN 7.56h runtime constants lost opcodes or reused Global lookup tables.");

        var mapping = new Dictionary<string, string>
        {
            ["PlayerSpawn"] = "PlayerSpawn", ["NpcSpawn"] = "NpcSpawn", ["NpcSpawn2"] = "NpcSpawn2",
            ["ActionEffect01"] = "Ability1", ["ActionEffect08"] = "Ability8",
            ["ActionEffect16"] = "Ability16", ["ActionEffect24"] = "Ability24", ["ActionEffect32"] = "Ability32",
            ["StatusEffectList"] = "StatusEffectList", ["StatusEffectList3"] = "StatusEffectList3",
            ["ActorControl"] = "ActorControl", ["ActorCast"] = "ActorCast",
        };
        var previousRegion = OpcodeManager.Instance.GameRegion;
        try
        {
            ValidateInternationalPath(version, bundledPolicy, regional);
            foreach (var region in new[] { GameRegion.Global, GameRegion.Chinese, GameRegion.Korean })
            {
                OpcodeManager.Instance.SetRegion(region);
                Require(mapping.All(pair => regional.ObfuscatedOpcodes[pair.Key] == OpcodeManager.Instance.CurrentOpcodes[pair.Value]),
                    $"Unscrambler and Machina disagree on {region} 7.56h packets.");
            }
        }
        finally
        {
            OpcodeManager.Instance.SetRegion(previousRegion);
        }
        Console.WriteLine("7.56h official resources, CN runtime keys, region/opcode alignment and fallback boundaries passed.");
    }

    private static void ValidateInternationalPath(string version, MethodInfo bundledPolicy, VersionConstants regional)
    {
        foreach (var languageName in new[] { "Japanese", "English", "German", "French", "ChineseSimplified" })
        {
            var language = Enum.TryParse<Dalamud.Game.ClientLanguage>(languageName, out var parsed)
                ? parsed : Dalamud.Game.ClientLanguage.English;
            for (byte nativeCode = 0; nativeCode < 4; nativeCode++)
            {
                var selection = GameRegionResolver.Resolve(GameRegionMode.Auto, languageName, nativeCode);
                IINACT.FfxivActPluginWrapper.ConfigureRegion(language,
                    selection.EffectiveRegion == HostGameRegion.Chinese);
                Require(OpcodeManager.Instance.GameRegion == GameRegion.Global &&
                        bundledPolicy.Invoke(null, [OpcodeManager.Instance.GameRegion, version]) is true,
                    "An international client, including translated clients, missed the Global 7.56h profile.");
            }
        }

        var chineseDecoder = new Unscrambler73();
        chineseDecoder.Initialize(regional);
        var keys = new byte[] { 7, 11, 19 };
        var table = Enumerable.Range(0, 165).Select(index => 0x1234 + index * 17).ToArray();
        // Encode known fields independently in synthetic IPC packets. Compare the whole
        // decoded packet so opcode selection, table indexing and collateral writes are checked.
        // Include all four tank invulnerability action IDs from the reported missing
        // callouts, using both regional profiles. This checks the input to Ability matching.
        foreach (var unscrambler in new[] { UnscramblerFactory.ForGameVersion(version), chineseDecoder })
        foreach (var actionId in new uint[] { 0x1E, 0x2B, 0xE36, 0x3F18, 0x123456 })
        foreach (var (opcode, fieldOffset) in new[] { ((ushort)0x162, 20), ((ushort)0x313, 24) })
        {
            var expectedPacket = Enumerable.Repeat((byte)0xA5, 128).ToArray();
            BinaryPrimitives.WriteUInt16LittleEndian(expectedPacket.AsSpan(2), opcode);
            BinaryPrimitives.WriteUInt32LittleEndian(expectedPacket.AsSpan(fieldOffset), actionId);
            if (opcode == 0x313)
                for (var index = 0; index < 8; index++)
                    BinaryPrimitives.WriteUInt16LittleEndian(expectedPacket.AsSpan(64 + index * 8), (ushort)(1234 + index * 2395));

            var encoded = (byte[])expectedPacket.Clone();
            var baseKey = keys[opcode % 3];
            var opcodeKey = table[(opcode + baseKey) % table.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(encoded.AsSpan(fieldOffset), actionId + baseKey);
            if (opcode == 0x313)
                for (var index = 0; index < 8; index++)
                {
                    var field = encoded.AsSpan(64 + index * 8);
                    BinaryPrimitives.WriteUInt16LittleEndian(field,
                        (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(field) ^ (ushort)(baseKey + opcodeKey)));
                }
            unscrambler.Unscramble(encoded, keys[0], keys[1], keys[2], table);
            Require(encoded.AsSpan().SequenceEqual(expectedPacket),
                $"7.56h opcode {opcode:X} did not restore action {actionId:X}/damage fields exactly.");
        }
        Console.WriteLine("7.56h CN/Global: language routing, four tank invulnerability IDs and synthetic ActorCast/ActionEffect decoding passed.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}

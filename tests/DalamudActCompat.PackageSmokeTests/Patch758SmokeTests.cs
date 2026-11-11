using System.Buffers.Binary;
using System.Reflection;
using DalamudActCompat.Plugin;
using DalamudActCompat.Protocol;
using IINACT.Network;
using Machina.FFXIV;
using Machina.FFXIV.Headers.Opcodes;
using Unscrambler;
using Unscrambler.Constants;
using Unscrambler.Unscramble;
using Unscrambler.Unscramble.Versions;

internal static class Patch758SmokeTests
{
    // CN and Global both shipped 7.58 as the same 2026.11.03 build.
    private const string Version = "2026.11.03.0000.0000";
    private const int TableSize = 34 * sizeof(int);

    public static void Run()
    {
        // Measured from the CN 7.58 ffxiv_dx11.exe: OnReceivePacket indexes a 34-entry key
        // table at RVA 0x231F9A0 (followed directly by module pointers). Unscrambler's Global
        // profile has the same size at 0x2322350, so CN must keep its own scanned address.
        const uint chineseKeyTableOffset = 0x231F9A0;
        const uint globalKeyTableOffset = 0x2322350;

        // Unscrambler.XIV 7.58.0 (perchbirdd/unscrambler c5826c2, Constants758). The CN values
        // were checked item by item against the public CN 7.58 opcode list
        // (Yarukon/FFXIVNetworkOpcodes 725b4b0, CN_2026.11.03) and are identical.
        var expected = new Dictionary<string, int>
        {
            ["PlayerSpawn"] = 0x2FC, ["NpcSpawn"] = 0x176, ["NpcSpawn2"] = 0x2FD,
            ["ActionEffect01"] = 0x190, ["ActionEffect08"] = 0x233,
            ["ActionEffect16"] = 0x119, ["ActionEffect24"] = 0x39C, ["ActionEffect32"] = 0xDD,
            ["StatusEffectList"] = 0x15D, ["StatusEffectList3"] = 0x35D,
            ["Examine"] = 0x289, ["UpdateGearset"] = 0x172, ["UpdateParty"] = 0x3E3,
            ["ActorControl"] = 0x2DF, ["ActorCast"] = 0x160,
            ["UnknownEffect01"] = 0x351, ["UnknownEffect16"] = 0x385,
            ["ActionEffect02"] = 0x326, ["ActionEffect04"] = 0x222,
        };
        var bundled = VersionConstants.ForGameVersion(Version);
        Require(bundled.OpcodeKeyTableSize == TableSize && bundled.OpcodeKeyTableOffset == globalKeyTableOffset &&
                bundled.InitZoneOpcode == 0xAB && bundled.UnknownObfuscationInitOpcode == 0x2F5 &&
                bundled.ObfuscationEnabledMode == 157 &&
                expected.Count == bundled.ObfuscatedOpcodes.Count &&
                expected.All(pair => bundled.ObfuscatedOpcodes[pair.Key] == pair.Value),
            "Unscrambler 7.58 profile differs from the published constants.");
        var globalDecoder = UnscramblerFactory.ForGameVersion(Version);
        Require(globalDecoder is Unscrambler73,
            "The official 7.58 factory cannot initialize its embedded resources.");

        var hookType = typeof(ZoneDownHookManager);
        var bundledPolicy = hookType.GetMethod("CanUseBundledVersionConstants", BindingFlags.NonPublic | BindingFlags.Static)!;
        var regionalPolicy = hookType.GetMethod("CanUseChineseRuntimeVersionConstants", BindingFlags.NonPublic | BindingFlags.Static)!;
        var regionalFactory = hookType.GetMethod("GetChineseRuntimeVersionConstant", BindingFlags.NonPublic | BindingFlags.Static)!;
        Require(bundledPolicy.Invoke(null, [GameRegion.Global, Version]) is true &&
                bundledPolicy.Invoke(null, [GameRegion.Chinese, Version]) is false &&
                regionalPolicy.Invoke(null, [GameRegion.Chinese, Version, TableSize]) is true &&
                regionalPolicy.Invoke(null, [GameRegion.Global, Version, TableSize]) is false,
            "7.58 fell back to the warned path or reused Global memory addresses on CN.");
        // 83 entries is the 7.57 table: a stale CN layout must not pass as verified 7.58.
        foreach (var size in new[] { TableSize - 4, TableSize + 4, 83 * 4, 89 * 4 })
            Require(regionalPolicy.Invoke(null, [GameRegion.Chinese, Version, size]) is false,
                "CN 7.58 accepted a mismatched key table, including the 7.57 size.");

        var regional = (VersionConstants)regionalFactory.Invoke(null, [Version, chineseKeyTableOffset, TableSize])!;
        var chineseDecoder = new Unscrambler73();
        chineseDecoder.Initialize(regional);
        Require(regional.OpcodeKeyTableOffset == chineseKeyTableOffset && regional.OpcodeKeyTableSize == TableSize &&
                regional.TableOffsets.Length == 0 && regional.MidTableOffset == 0 && regional.DayTableOffset == 0 &&
                expected.Count == regional.ObfuscatedOpcodes.Count &&
                expected.All(pair => regional.ObfuscatedOpcodes[pair.Key] == pair.Value),
            "CN 7.58 runtime constants lost opcodes or reused Global lookup tables.");

        var previousRegion = OpcodeManager.Instance.GameRegion;
        try
        {
            // Machina parses the restored packets with its own table, so every packet both sides
            // know must carry the same 7.58 opcode in each region DACT ships for.
            var machinaNames = new Dictionary<string, string>
            {
                ["PlayerSpawn"] = "PlayerSpawn", ["NpcSpawn"] = "NpcSpawn", ["NpcSpawn2"] = "NpcSpawn2",
                ["ActionEffect01"] = "Ability1", ["ActionEffect08"] = "Ability8",
                ["ActionEffect16"] = "Ability16", ["ActionEffect24"] = "Ability24", ["ActionEffect32"] = "Ability32",
                ["StatusEffectList"] = "StatusEffectList", ["StatusEffectList3"] = "StatusEffectList3",
                ["ActorControl"] = "ActorControl", ["ActorCast"] = "ActorCast",
            };
            foreach (var region in new[] { GameRegion.Chinese, GameRegion.Global })
            {
                OpcodeManager.Instance.SetRegion(region);
                var machina = OpcodeManager.Instance.CurrentOpcodes;
                Require(machinaNames.All(pair => machina[pair.Value] == expected[pair.Key]),
                    $"{region} Machina 7.58 opcodes disagree with Unscrambler 7.58.");
            }

            ValidateInternationalPath(bundledPolicy);
        }
        finally
        {
            OpcodeManager.Instance.SetRegion(previousRegion);
        }

        ValidateSyntheticDecode("Global", globalDecoder);
        ValidateSyntheticDecode("CN", chineseDecoder);
        Console.WriteLine("7.58 CN/Global: official Unscrambler 7.58.0 profile, CN runtime key table, Machina alignment and synthetic ActorCast/ActionEffect decoding passed.");
    }

    private static void ValidateInternationalPath(MethodInfo bundledPolicy)
    {
        // Translated clients (for example ChineseSimplified text on a Global install) must still
        // pick the Global profile; only the CN client may take the runtime key-table path.
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
                        bundledPolicy.Invoke(null, [OpcodeManager.Instance.GameRegion, Version]) is true,
                    "An international client, including translated clients, missed the Global 7.58 profile.");
            }
        }
    }

    private static void ValidateSyntheticDecode(string label, IUnscrambler unscrambler)
    {
        var keys = new byte[] { 7, 11, 19 };
        var table = Enumerable.Range(0, TableSize / sizeof(int))
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
                $"{label} 7.58 opcode {opcode:X} did not restore action {actionId:X}/damage fields exactly.");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}

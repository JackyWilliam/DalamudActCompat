using System.Reflection;
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
        const string version = "2026.09.01.0000.0000";
        const int tableSize = 193 * sizeof(int);
        const uint regionalOffset = 0x123400;
        var bundled = VersionConstants.ForGameVersion(version);
        var expected = new Dictionary<string, int>
        {
            ["PlayerSpawn"] = 0x3B2, ["NpcSpawn"] = 0x1C4, ["NpcSpawn2"] = 0x26A,
            ["ActionEffect01"] = 0x2EC, ["ActionEffect08"] = 0xFD,
            ["ActionEffect16"] = 0x357, ["ActionEffect24"] = 0xB4, ["ActionEffect32"] = 0x14E,
            ["StatusEffectList"] = 0x248, ["StatusEffectList3"] = 0x20D,
            ["Examine"] = 0x69, ["UpdateGearset"] = 0x374, ["UpdateParty"] = 0x1DF,
            ["ActorControl"] = 0x38C, ["ActorCast"] = 0x10A,
            ["UnknownEffect01"] = 0x1DB, ["UnknownEffect16"] = 0x34C,
            ["ActionEffect02"] = 0x356, ["ActionEffect04"] = 0x371,
        };
        Require(bundled.OpcodeKeyTableSize == tableSize && bundled.OpcodeKeyTableOffset == 0x2312040 &&
                bundled.InitZoneOpcode == 0x3A1 && bundled.UnknownObfuscationInitOpcode == 0x66 &&
                bundled.ObfuscationEnabledMode == 12 &&
                expected.Count == bundled.ObfuscatedOpcodes.Count &&
                expected.All(pair => bundled.ObfuscatedOpcodes[pair.Key] == pair.Value),
            "Unscrambler 7.56 profile differs from the published constants.");
        Require(UnscramblerFactory.ForGameVersion(version) is Unscrambler73,
            "The official 7.56 factory cannot initialize its embedded resources.");

        var hookType = typeof(ZoneDownHookManager);
        var bundledPolicy = hookType.GetMethod("CanUseBundledVersionConstants", BindingFlags.NonPublic | BindingFlags.Static)!;
        var regionalPolicy = hookType.GetMethod("CanUseChineseRuntimeVersionConstants", BindingFlags.NonPublic | BindingFlags.Static)!;
        var regionalFactory = hookType.GetMethod("GetChineseRuntimeVersionConstant", BindingFlags.NonPublic | BindingFlags.Static)!;
        Require(bundledPolicy.Invoke(null, [GameRegion.Global, version]) is true &&
                bundledPolicy.Invoke(null, [GameRegion.Chinese, version]) is false &&
                regionalPolicy.Invoke(null, [GameRegion.Chinese, version, tableSize]) is true,
            "7.56 selected a fallback or reused Global memory addresses on CN.");
        foreach (var size in new[] { tableSize - 4, tableSize + 4, 89 * 4 })
            Require(regionalPolicy.Invoke(null, [GameRegion.Chinese, version, size]) is false,
                "CN 7.56 accepted a mismatched key table, including the old 7.55 size.");
        Require(regionalPolicy.Invoke(null, [GameRegion.Chinese, "2099.01.01.0000.0000", tableSize]) is false,
            "An unknown patch was treated as verified 7.56.");

        // A made-up regional RVA proves the factory uses the scanned address rather than
        // accidentally borrowing the Global executable's address from the NuGet resource.
        var regional = (VersionConstants)regionalFactory.Invoke(null, [version, regionalOffset, tableSize])!;
        new Unscrambler73().Initialize(regional);
        Require(regional.OpcodeKeyTableOffset == regionalOffset && regional.OpcodeKeyTableSize == tableSize &&
                regional.TableOffsets.Length == 0 && regional.MidTableOffset == 0 && regional.DayTableOffset == 0 &&
                expected.Count == regional.ObfuscatedOpcodes.Count &&
                expected.All(pair => regional.ObfuscatedOpcodes[pair.Key] == pair.Value),
            "CN 7.56 runtime constants lost opcodes or reused Global lookup tables.");

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
            foreach (var region in new[] { GameRegion.Global, GameRegion.Chinese, GameRegion.Korean })
            {
                OpcodeManager.Instance.SetRegion(region);
                Require(mapping.All(pair => regional.ObfuscatedOpcodes[pair.Key] == OpcodeManager.Instance.CurrentOpcodes[pair.Value]),
                    $"Unscrambler and Machina disagree on {region} 7.56 packets.");
            }
        }
        finally
        {
            OpcodeManager.Instance.SetRegion(previousRegion);
        }
        Console.WriteLine("7.56 official resources, CN runtime keys, region/opcode alignment and fallback boundaries passed.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}

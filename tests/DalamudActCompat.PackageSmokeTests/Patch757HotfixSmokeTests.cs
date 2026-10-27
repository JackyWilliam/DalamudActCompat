using System.Reflection;
using IINACT.Network;
using Machina.FFXIV;
using Machina.FFXIV.Headers.Opcodes;
using Unscrambler;
using Unscrambler.Constants;
using Unscrambler.Unscramble.Versions;

internal static class Patch757HotfixSmokeTests
{
    public static void Run()
    {
        const string hotfix = "2026.10.16.0000.0000";
        const int tableSize = 233 * sizeof(int);
        // Unscrambler.XIV 7.57.1 publishes this profile. Its key-table RVA and size are also
        // what the reporting Global client discovered at runtime (0x232A700, 0x3A4 bytes).
        var bundled = VersionConstants.ForGameVersion(hotfix);
        var expected = new Dictionary<string, int>
        {
            ["PlayerSpawn"] = 0x17C, ["NpcSpawn"] = 0x17D, ["NpcSpawn2"] = 0x1AB,
            ["ActionEffect01"] = 0x363, ["ActionEffect08"] = 0x262,
            ["ActionEffect16"] = 0x299, ["ActionEffect24"] = 0x245, ["ActionEffect32"] = 0x385,
            ["StatusEffectList"] = 0xD4, ["StatusEffectList3"] = 0x28E,
            ["Examine"] = 0x19E, ["UpdateGearset"] = 0x330, ["UpdateParty"] = 0x2E7,
            ["ActorControl"] = 0x229, ["ActorCast"] = 0x7F,
            ["UnknownEffect01"] = 0x26F, ["UnknownEffect16"] = 0x368,
            ["ActionEffect02"] = 0x362, ["ActionEffect04"] = 0x2E9,
        };
        Require(bundled.OpcodeKeyTableSize == tableSize && bundled.OpcodeKeyTableOffset == 0x232A700 &&
                bundled.InitZoneOpcode == 0x9C && bundled.UnknownObfuscationInitOpcode == 0x34E &&
                bundled.ObfuscationEnabledMode == 47 &&
                expected.Count == bundled.ObfuscatedOpcodes.Count &&
                expected.All(pair => bundled.ObfuscatedOpcodes[pair.Key] == pair.Value),
            "Unscrambler 7.57h profile differs from the published constants.");
        Require(UnscramblerFactory.ForGameVersion(hotfix) is Unscrambler73,
            "The official 7.57h factory cannot initialize its embedded resources.");

        var hookType = typeof(ZoneDownHookManager);
        var bundledPolicy = hookType.GetMethod("CanUseBundledVersionConstants", BindingFlags.NonPublic | BindingFlags.Static)!;
        var regionalPolicy = hookType.GetMethod("CanUseChineseRuntimeVersionConstants", BindingFlags.NonPublic | BindingFlags.Static)!;
        Require(bundledPolicy.Invoke(null, [GameRegion.Global, hotfix]) is true &&
                bundledPolicy.Invoke(null, [GameRegion.Chinese, hotfix]) is false,
            "Global 7.57h fell back to unverified runtime constants.");
        // CN has not shipped 7.57h; borrowing Global opcodes for it would be a guess.
        foreach (var size in new[] { 83 * sizeof(int), tableSize })
            Require(regionalPolicy.Invoke(null, [GameRegion.Chinese, hotfix, size]) is false,
                "An unverified CN 7.57h build was treated as a verified regional profile.");

        // Machina parses the opcodes Unscrambler restores, so both must describe the same client.
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
            OpcodeManager.Instance.SetRegion(GameRegion.Global);
            Require(mapping.All(pair => bundled.ObfuscatedOpcodes[pair.Key] == OpcodeManager.Instance.CurrentOpcodes[pair.Value]),
                "Unscrambler and Machina disagree on Global 7.57h packets.");
        }
        finally
        {
            OpcodeManager.Instance.SetRegion(previousRegion);
        }
        Console.WriteLine("7.57h Global: published Unscrambler profile, bundled-profile selection, CN boundary and Machina alignment passed.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}

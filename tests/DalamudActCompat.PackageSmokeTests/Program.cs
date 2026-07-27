using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Windows.Forms;
using System.Xml;
using Advanced_Combat_Tracker;
using DalamudActCompat.ActRuntime;
using DalamudActCompat.Compatibility.PluginHost;
using DalamudActCompat.Compatibility.Cactbot;
using DalamudActCompat.Core.Models;
using DalamudActCompat.Core.State;
using DalamudActCompat.Infrastructure.Storage;
using DalamudActCompat.Meter;
using DalamudActCompat.Parser;
using Machina.FFXIV;
using Machina.FFXIV.Headers.Opcodes;
using Newtonsoft.Json.Linq;
using RainbowMage.OverlayPlugin.EventSources;
using RainbowMage.OverlayPlugin.MemoryProcessors;
using RainbowMage.OverlayPlugin.MemoryProcessors.InCombat;
using RainbowMage.OverlayPlugin.MemoryProcessors.Party;

var testRoot = Path.Combine(Path.GetTempPath(), $"DalamudActCompat-{Guid.NewGuid():N}");
Directory.CreateDirectory(testRoot);

try
{
    ValidateSettingsSerializerMemberTypes();
    ValidateActPluginDataCompatibility();
    ValidatePlayerIdentityResolution();
    ValidateDalamudGameStateBridge();
    ValidateCactbotSpokenAlertDefaults();
    ValidateOverlayInitialStateEvents();
    ValidateHtmlOverlayDefaults();
    ValidateChinese751bOpcodes();
    ValidateMeterRows();

    var packagePath = Path.Combine(testRoot, "valid.zip");
    await CreatePackageAsync(packagePath, "example.plugin", "1.0.0");
    var paths = new PluginPaths(Path.Combine(testRoot, "config"));
    Directory.CreateDirectory(paths.ActPluginDirectory);
    var installer = new ActPluginPackageInstaller(paths);

    var cactbotPackage = Path.Combine(testRoot, "cactbot.zip");
    using (var archive = ZipFile.Open(cactbotPackage, ZipArchiveMode.Create))
    {
        await WriteArchiveEntryAsync(archive, "cactbot/cactbot/CactbotOverlay.dll", [1, 2, 3]);
        await WriteArchiveEntryAsync(archive, "cactbot/cactbot/ui/raidboss/raidboss.html", "<html></html>"u8.ToArray());
        await WriteArchiveEntryAsync(archive, "cactbot/cactbot/resources/test.txt", "ok"u8.ToArray());
    }
    var cactbotInstaller = new CactbotPackageInstaller(paths);
    await cactbotInstaller.InstallAsync(cactbotPackage, CancellationToken.None);
    Assert(cactbotInstaller.IsInstalled, "Official Cactbot package layout was not installed.");
    Assert(File.Exists(Path.Combine(paths.CactbotDirectory, "resources", "test.txt")),
        "Cactbot resources were not preserved.");

    var installed = await installer.InstallAsync(packagePath, CancellationToken.None);
    Assert(installed.Manifest.Id == "example.plugin", "Valid package id was not preserved.");
    Assert(File.Exists(Path.Combine(installed.InstallDirectory, "Example.Plugin.dll")), "Entry assembly was not installed.");

    await CreatePackageAsync(packagePath, "example.plugin", "1.1.0");
    installed = await installer.InstallAsync(packagePath, CancellationToken.None);
    Assert(installed.Manifest.Version == "1.1.0", "Upgrade did not replace the installed package.");
    Assert(Directory.EnumerateDirectories(paths.PluginBackupDirectory).Any(), "Upgrade did not preserve a backup.");

    var knownPackagePath = Path.Combine(testRoot, "postnamazu.zip");
    using (var archive = ZipFile.Open(knownPackagePath, ZipArchiveMode.Create))
    {
        var entry = archive.CreateEntry("release/PostNamazu.dll");
        await using var stream = entry.Open();
        await stream.WriteAsync(new byte[] { 1, 2, 3 });
    }

    var knownPlugin = await installer.InstallAsync(knownPackagePath, CancellationToken.None);
    Assert(knownPlugin.Manifest.Id == "postnamazu", "Known third-party package was not recognized.");
    Assert(knownPlugin.Manifest.EntryType == "PostNamazu.PostNamazu", "Known plugin entry type is incorrect.");

    var loosePluginDirectory = Path.Combine(testRoot, "loose-plugin");
    Directory.CreateDirectory(loosePluginDirectory);
    var loosePluginPath = Path.Combine(loosePluginDirectory, "Triggernometry.dll");
    var builtRuntimeDirectory = Path.Combine(
        FindProjectRoot(),
        "src",
        "DalamudActCompat",
        "bin",
        "Release");
    File.Copy(Path.Combine(builtRuntimeDirectory, "DalamudActCompat.ActRuntime.dll"), loosePluginPath);
    File.Copy(
        Path.Combine(builtRuntimeDirectory, "Advanced Combat Tracker.dll"),
        Path.Combine(loosePluginDirectory, "Advanced Combat Tracker.dll"));
    var translationPath = Path.Combine(loosePluginDirectory, "zh-CN.triglations.xml");
    await File.WriteAllTextAsync(translationPath, "<Triggernometry />");
    var loosePlugin = await installer.InstallAsync(loosePluginPath, CancellationToken.None);
    Assert(loosePlugin.Manifest.Id == "triggernometry", "Loose Triggernometry DLL was not recognized.");
    Assert(File.Exists(Path.Combine(loosePlugin.InstallDirectory, "Triggernometry.dll")),
        "Loose ACT plugin DLL was not installed.");
    Assert(File.Exists(Path.Combine(loosePlugin.InstallDirectory, "zh-CN.triglations.xml")),
        "Triggernometry translation companion was not preserved.");
    Assert(File.Exists(Path.Combine(loosePlugin.InstallDirectory, "Advanced Combat Tracker.dll")),
        "Loose ACT plugin managed dependency was not preserved.");

    var unknownDllPath = Path.Combine(loosePluginDirectory, "UnknownPlugin.dll");
    await File.WriteAllBytesAsync(unknownDllPath, new byte[] { 0x4d, 0x5a });
    try
    {
        await installer.InstallAsync(unknownDllPath, CancellationToken.None);
        throw new InvalidOperationException("Unknown loose DLL was accepted.");
    }
    catch (InvalidDataException)
    {
    }

    var unsafePackagePath = Path.Combine(testRoot, "unsafe.zip");
    using (var archive = ZipFile.Open(unsafePackagePath, ZipArchiveMode.Create))
    {
        var entry = archive.CreateEntry("../escape.dll");
        await using var stream = entry.Open();
        await stream.WriteAsync(new byte[] { 1, 2, 3 });
    }

    try
    {
        await installer.InstallAsync(unsafePackagePath, CancellationToken.None);
        throw new InvalidOperationException("Unsafe package was accepted.");
    }
    catch (InvalidDataException)
    {
    }

    ValidateFfxivModuleInitializer();
    ValidateFfxivPluginConstructor();
    ValidateFfxivRuntimeAssemblies();
    ValidateLegacyResourceRuntimeDependencies();
    ValidateActEncounterMapping();
    ValidateChineseCombatChatParsing();

    Console.WriteLine("Package and FFXIV_ACT_Plugin smoke tests passed.");
    return 0;
}

finally
{
    if (Directory.Exists(testRoot))
    {
        Directory.Delete(testRoot, true);
    }
}

static void ValidateMeterRows()
{
    var start = DateTimeOffset.UtcNow.AddSeconds(-10);
    var encounter = new Encounter(
        Guid.NewGuid(),
        start,
        start.AddSeconds(10),
        "Test Zone",
        "Test Enemy",
        [
            new Combatant("tank", "Tank@Alpha", "PLD", true, 100_000, 10_000, 1, 11_000, 10_000, 10_000),
            new Combatant("healer", "Healer@Beta", "WHM", false, 20_000, 200_000, 0, 2_500, 2_000, 2_000),
        ],
        [],
        [],
        [],
        [],
        []);
    var state = new EncounterStateStore();
    state.Replace(encounter, []);
    var settings = new MeterSettings
    {
        RefreshIntervalMs = 250,
        DpsMetric = DpsMetric.EncDps,
        SortMode = MeterSortMode.Dps,
    };
    var meter = new MeterService(state, settings);
    var rows = meter.GetRows();
    Assert(rows[0].Name == "Tank@Alpha", "DPS sorting did not preserve the player server name.");
    Assert(rows[0].Job == "PLD", "The resolved player job was not preserved.");
    Assert(rows[0].Deaths == 1, "The resolved player death count was not preserved.");
    Assert(rows[0].Dps == 10_000, "EncDPS did not use the ACT encounter-duration field.");

    settings.SortMode = MeterSortMode.Hps;
    Thread.Sleep(settings.RefreshIntervalMs + 20);
    rows = meter.GetRows();
    Assert(rows[0].Name == "Healer@Beta", "HPS sorting did not promote the highest-healing player.");
    Assert(rows[0].Hps == 20_000, "HPS did not use encounter duration.");
}

static void ValidateChinese751bOpcodes()
{
    OpcodeManager.Instance.SetRegion(GameRegion.Chinese);
    var opcodes = OpcodeManager.Instance.CurrentOpcodes;
    var expected = new Dictionary<string, ushort>
    {
        ["Ability1"] = 0x037D,
        ["Ability8"] = 0x0350,
        ["Ability16"] = 0x027E,
        ["Ability24"] = 0x01A4,
        ["Ability32"] = 0x02A2,
        ["ActorCast"] = 0x01C9,
        ["EffectResult"] = 0x02EF,
        ["ActorControl"] = 0x019F,
        ["ActorControlSelf"] = 0x0164,
        ["ActorControlTarget"] = 0x02D1,
        ["StatusEffectList"] = 0x0132,
        ["StatusEffectList2"] = 0x0078,
        ["StatusEffectList3"] = 0x028B,
    };

    foreach (var pair in expected)
    {
        Assert(
            opcodes.TryGetValue(pair.Key, out var actual) && actual == pair.Value,
            $"Chinese 7.51b opcode {pair.Key} was {actual:X}, expected {pair.Value:X}.");
    }
}

static void ValidatePlayerIdentityResolution()
{
    ActPlayerIdentity[] identities =
    [
        new("Same Name", "Alpha", "PLD", true, false),
        new("Same Name", "Beta", "WHM", false, false),
        new("Unique Name", "Gamma", "DRG", false, true),
    ];

    Assert(
        ActPlayerIdentityResolver.Resolve(identities, "Same Name@Beta")?.Job == "WHM",
        "Cross-world player identity did not resolve by UserName@ServerName.");
    Assert(
        ActPlayerIdentityResolver.Resolve(identities, "Same Name") is null,
        "Ambiguous cross-world player name should not be guessed.");
    Assert(
        ActPlayerIdentityResolver.Resolve(identities, "Unique Name")?.DisplayName == "Unique Name@Gamma",
        "Unique player name did not resolve to its world-qualified display name.");
    Assert(
        ActPlayerIdentityResolver.Resolve(identities, "YOU")?.DisplayName == "Same Name@Alpha",
        "ACT's YOU alias did not resolve to the local player.");
    Assert(
        ActPlayerIdentityResolver.Resolve(identities, "Summon") is null,
        "Unknown pets or NPCs must not resolve as players.");
}

static void ValidateDalamudGameStateBridge()
{
    var provider = new CachedDalamudGameStateProvider();
    ActPlayerIdentity[] identities =
    [
        new("Local Player", "Alpha", "PLD", true, false)
        {
            EntityId = 0x10000001,
            ContentId = 0x12345678,
            WorldId = 21,
            JobId = 19,
            Level = 100,
            CurrentHp = 120_000,
            MaxHp = 120_000,
            CurrentMp = 10_000,
            MaxMp = 10_000,
            TerritoryId = 1234,
        },
        new("Party Member", "Beta", "WHM", false, false)
        {
            EntityId = 0x10000002,
            ContentId = 0x87654321,
            WorldId = 22,
            JobId = 24,
            Level = 100,
            CurrentHp = 80_000,
            MaxHp = 80_000,
            CurrentMp = 9_000,
            MaxMp = 10_000,
            TerritoryId = 1234,
        },
    ];

    provider.Update(identities, true);
    Assert(provider.Snapshot.GameExists, "Dalamud game state did not report the active game.");
    Assert(provider.Snapshot.InGameCombat, "Dalamud combat state was not preserved.");
    Assert(provider.Snapshot.Player?.JobId == 19, "Dalamud local player job was not preserved.");
    Assert(provider.Snapshot.Party.Count == 2, "Dalamud party members were not preserved.");

    var overlayAssembly = typeof(IDalamudGameStateProvider).Assembly;
    var partyAdapterType = overlayAssembly.GetType(
        "RainbowMage.OverlayPlugin.MemoryProcessors.Party.DalamudPartyMemory",
        throwOnError: true)!;
    var partyAdapter = (IPartyMemory)Activator.CreateInstance(
        partyAdapterType,
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
        binder: null,
        [provider],
        culture: null)!;
    var party = partyAdapter.GetPartyLists();
    Assert(party.memberCount == 2, "OverlayPlugin PartyChanged bridge lost party members.");
    Assert(party.partyMembers[0].classJob == 19, "OverlayPlugin PartyChanged bridge lost the local job.");
    Assert(party.partyMembers[1].name == "Party Member", "OverlayPlugin PartyChanged bridge lost a party name.");

    var combatAdapterType = overlayAssembly.GetType(
        "RainbowMage.OverlayPlugin.MemoryProcessors.InCombat.DalamudInCombatMemory",
        throwOnError: true)!;
    var combatAdapter = (IInCombatMemory)Activator.CreateInstance(
        combatAdapterType,
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
        binder: null,
        [provider],
        culture: null)!;
    Assert(combatAdapter.IsValid(), "OverlayPlugin InCombat bridge did not see the game.");
    Assert(combatAdapter.GetInCombat(), "OverlayPlugin InCombat bridge lost the combat state.");

    provider.Clear();
    Assert(!provider.Snapshot.GameExists, "Clearing the Dalamud game state left the game active.");
}

static void ValidateHtmlOverlayDefaults()
{
    var settings = new HtmlOverlayWindowSettings();
    Assert(settings.IsClickThrough, "HTML overlays must be click-through by default.");
    Assert(settings.IsLocked, "HTML overlays must be locked by default.");
    Assert(!settings.IsEditing, "HTML overlays must not start in editing mode.");
    Assert(settings.ZoomFactor == 1.0f, "HTML overlay default zoom changed unexpectedly.");
    settings.SetEditing(true);
    Assert(
        settings.IsEditing && !settings.IsClickThrough && !settings.IsLocked,
        "HTML overlay editing mode did not disable click-through and locking together.");
    settings.SetEditing(false);
    Assert(
        !settings.IsEditing && settings.IsClickThrough && settings.IsLocked,
        "Finishing HTML overlay editing did not restore click-through and locking together.");

    var formType = typeof(HtmlOverlayWindowSettings).Assembly.GetType(
                       "DalamudActCompat.ActRuntime.HtmlOverlayForm")
                   ?? throw new InvalidOperationException("HTML overlay form was not found.");
    var isRaidbossPage = formType.GetMethod(
                             "IsCactbotRaidbossPage",
                             BindingFlags.Static | BindingFlags.NonPublic)
                         ?? throw new InvalidOperationException(
                             "Cactbot raidboss page detector was not found.");
    Assert(
        isRaidbossPage.Invoke(
            null,
            [new Uri("file:///C:/cactbot/ui/raidboss/raidboss.html?OVERLAY_WS=ws://127.0.0.1")])
            as bool? == true,
        "The Cactbot raidboss page was not recognized for responsive alert layout.");
    Assert(
        isRaidbossPage.Invoke(
            null,
            [new Uri("file:///C:/cactbot/ui/config/config.html")])
            as bool? == false,
        "The responsive raidboss layout leaked into another Cactbot page.");
    var layoutScript = formType.GetField(
                           "CactbotResponsiveAlertLayoutScript",
                           BindingFlags.Static | BindingFlags.NonPublic)
                       ?.GetRawConstantValue() as string;
    Assert(
        layoutScript?.Contains("#popup-text-info", StringComparison.Ordinal) == true &&
        layoutScript.Contains("max-height: 220px", StringComparison.Ordinal),
        "The Cactbot responsive layout no longer protects info text from clipping.");
}

static void ValidateCactbotSpokenAlertDefaults()
{
    var method = typeof(CactbotEventSource).GetMethod(
        "EnsureDefaultAlertOutput",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Cactbot alert-output migration helper was not found.");
    var empty = new Dictionary<string, JToken>();
    Assert(
        method.Invoke(null, [empty]) as bool? == true,
        "A new Cactbot configuration did not select text and TTS output.");
    Assert(
        empty["options"]["raidboss"]?["DefaultAlertOutput"]?.Value<string>() == "ttsAndText" &&
        empty["options"]["raidboss"]?["SpokenAlertsEnabled"] is null,
        "The initial Cactbot output mode was written to an option raidboss does not consume.");

    var legacyEnabled = new Dictionary<string, JToken>
    {
        ["options"] = JObject.FromObject(new
        {
            raidboss = new
            {
                SpokenAlertsEnabled = true,
            },
        }),
    };
    Assert(
        method.Invoke(null, [legacyEnabled]) as bool? == true &&
        legacyEnabled["options"]["raidboss"]?["DefaultAlertOutput"]?.Value<string>() ==
            "ttsAndText" &&
        legacyEnabled["options"]["raidboss"]?["SpokenAlertsEnabled"] is null,
        "The v0.2.24 spoken-alert setting was not migrated to text and TTS output.");

    var legacyDisabled = new Dictionary<string, JToken>
    {
        ["options"] = JObject.FromObject(new
        {
            raidboss = new
            {
                SpokenAlertsEnabled = false,
            },
        }),
    };
    Assert(
        method.Invoke(null, [legacyDisabled]) as bool? == true &&
        legacyDisabled["options"]["raidboss"]?["DefaultAlertOutput"]?.Value<string>() ==
            "textAndSound" &&
        legacyDisabled["options"]["raidboss"]?["SpokenAlertsEnabled"] is null,
        "A legacy Cactbot spoken-alert opt-out was not preserved during migration.");

    var existing = new Dictionary<string, JToken>
    {
        ["options"] = JObject.FromObject(new
        {
            raidboss = new
            {
                DefaultAlertOutput = "textOnly",
            },
        }),
    };
    Assert(
        method.Invoke(null, [existing]) as bool? == false &&
        existing["options"]["raidboss"]?["DefaultAlertOutput"]?.Value<string>() == "textOnly",
        "An explicit Cactbot default output mode was overwritten.");
}

static void ValidateOverlayInitialStateEvents()
{
    var eventSource = typeof(CactbotEventSource).Assembly.GetType(
                          "RainbowMage.OverlayPlugin.EventSources.FFXIVOptionalEventSource")
                      ?? throw new InvalidOperationException(
                          "OverlayPlugin optional event source was not found.");
    var createZone = eventSource.GetMethod(
                         "CreateChangeZoneEvent",
                         BindingFlags.Static | BindingFlags.NonPublic)
                     ?? throw new InvalidOperationException(
                         "Initial ChangeZone event factory was not found.");
    var zone = (JObject)createZone.Invoke(null, [0x155u, "The Goblet"])!;
    Assert(
        zone["type"]?.Value<string>() == "ChangeZone" &&
        zone["zoneID"]?.Value<uint>() == 0x155u &&
        zone["zoneName"]?.Value<string>() == "The Goblet",
        "Initial ChangeZone event did not match the OverlayPlugin protocol.");

    var createPlayer = eventSource.GetMethod(
                           "CreateChangePrimaryPlayerEvent",
                           BindingFlags.Static | BindingFlags.NonPublic)
                       ?? throw new InvalidOperationException(
                           "Initial ChangePrimaryPlayer event factory was not found.");
    var player = (JObject)createPlayer.Invoke(null, [0x100227F7u, "Test Player"])!;
    Assert(
        player["type"]?.Value<string>() == "ChangePrimaryPlayer" &&
        player["charID"]?.Value<uint>() == 0x100227F7u &&
        player["charName"]?.Value<string>() == "Test Player",
        "Initial ChangePrimaryPlayer event did not match the OverlayPlugin protocol.");
}

static void ValidateSettingsSerializerMemberTypes()
{
    var owner = new EnumSettingsOwner();
    using var serializer = new SettingsSerializer(owner);
    serializer.AddIntSetting(nameof(EnumSettingsOwner.PluginIntegration));
    using var textReader = new StringReader(
        "<SettingsSerializer><PluginIntegration>Auto</PluginIntegration></SettingsSerializer>");
    using var reader = new XmlTextReader(textReader);
    reader.ReadToFollowing("SettingsSerializer");
    serializer.ImportFromXml(reader);
    Assert(
        owner.PluginIntegration == PluginIntegrationMode.Auto,
        "SettingsSerializer did not use the member's enum type for a legacy integer registration.");
}

static void ValidateActPluginDataCompatibility()
{
    var type = typeof(ActPluginData);
    Assert(
        type.GetField("pPluginInfo")?.FieldType == typeof(Panel),
        "ActPluginData.pPluginInfo compatibility field is missing.");
    Assert(
        type.GetField("btnXButton")?.FieldType == typeof(Button),
        "ActPluginData.btnXButton compatibility field is missing.");
}

static void ValidateLegacyResourceRuntimeDependencies()
{
    var releaseDirectory = Path.Combine(
        FindProjectRoot(),
        "src",
        "DalamudActCompat",
        "bin",
        "Release");
    var nrbfPath = Path.Combine(
        releaseDirectory,
        "System.Formats.Nrbf.dll");
    Assert(
        File.Exists(nrbfPath),
        "Safe NRBF resource decoder was not copied to the plugin output.");

    var packagePath = Path.Combine(
        releaseDirectory,
        "DalamudActCompat",
        "latest.zip");
    Assert(File.Exists(packagePath), $"Dalamud release package was not found at {packagePath}.");
    using var archive = ZipFile.OpenRead(packagePath);
    Assert(
        archive.Entries.Any(entry => string.Equals(
            entry.Name,
            "System.Formats.Nrbf.dll",
            StringComparison.OrdinalIgnoreCase)),
        "Safe NRBF resource decoder is missing from the Dalamud release package.");
    Assert(
        archive.Entries.All(entry => !string.Equals(
            entry.Name,
            "System.Runtime.Serialization.Formatters.dll",
            StringComparison.OrdinalIgnoreCase)),
        "The removed BinaryFormatter compatibility package must not be shipped.");
}

static void ValidateFfxivModuleInitializer()
{
    var projectRoot = FindProjectRoot();
    var assemblyPath = Path.Combine(
        projectRoot,
        "src",
        "DalamudActCompat",
        "bin",
        "Release",
        "FFXIV_ACT_Plugin.dll");
    Assert(File.Exists(assemblyPath), $"FFXIV_ACT_Plugin.dll was not found at {assemblyPath}.");

    using var stream = File.OpenRead(assemblyPath);
    using var peReader = new PEReader(stream);
    var metadata = peReader.GetMetadataReader();
    foreach (var typeHandle in metadata.TypeDefinitions)
    {
        var type = metadata.GetTypeDefinition(typeHandle);
        if (metadata.GetString(type.Name) != "<Module>")
        {
            continue;
        }

        foreach (var methodHandle in type.GetMethods())
        {
            var method = metadata.GetMethodDefinition(methodHandle);
            if (metadata.GetString(method.Name) != ".cctor")
            {
                continue;
            }

            var il = peReader.GetMethodBody(method.RelativeVirtualAddress).GetILBytes();
            Assert(il is [0x2a], "FFXIV_ACT_Plugin module initializer is not a single ret instruction.");
            return;
        }
    }

    throw new InvalidOperationException("FFXIV_ACT_Plugin module initializer was not found.");
}

static void ValidateFfxivPluginConstructor()
{
    var assemblyPath = Path.Combine(
        FindProjectRoot(),
        "vendor",
        "IINACT",
        "external_dependencies",
        "FFXIV_ACT_Plugin.dll");
    Assert(File.Exists(assemblyPath), $"FFXIV_ACT_Plugin.dll was not found at {assemblyPath}.");

    using var stream = File.OpenRead(assemblyPath);
    using var peReader = new PEReader(stream);
    var metadata = peReader.GetMetadataReader();

    foreach (var typeHandle in metadata.TypeDefinitions)
    {
        var type = metadata.GetTypeDefinition(typeHandle);
        if (metadata.GetString(type.Namespace) != "FFXIV_ACT_Plugin" ||
            metadata.GetString(type.Name) != "FFXIV_ACT_Plugin")
            continue;

        foreach (var methodHandle in type.GetMethods())
        {
            var method = metadata.GetMethodDefinition(methodHandle);
            if (metadata.GetString(method.Name) != ".ctor" || method.RelativeVirtualAddress == 0)
                continue;

            var il = peReader.GetMethodBody(method.RelativeVirtualAddress).GetILBytes();
            Assert(il is not [0x14, 0x7a],
                "FFXIV_ACT_Plugin constructor is a protected throw-null stub.");
            return;
        }
    }

    throw new InvalidOperationException("FFXIV_ACT_Plugin constructor was not found.");
}

static void ValidateFfxivRuntimeAssemblies()
{
    var runtimeDirectory = Path.Combine(
        FindProjectRoot(),
        "src",
        "DalamudActCompat",
        "bin",
        "Release");
    var assemblyNames = new[]
    {
        "FFXIV_ACT_Plugin.Common.dll",
        "FFXIV_ACT_Plugin.Config.dll",
        "FFXIV_ACT_Plugin.Logfile.dll",
        "FFXIV_ACT_Plugin.Memory.dll",
        "FFXIV_ACT_Plugin.Network.dll",
        "FFXIV_ACT_Plugin.Parse.dll",
        "FFXIV_ACT_Plugin.Resource.dll",
    };

    foreach (var assemblyName in assemblyNames)
    {
        var assemblyPath = Path.Combine(runtimeDirectory, assemblyName);
        Assert(File.Exists(assemblyPath), $"{assemblyName} was not found at {assemblyPath}.");

        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        var metadata = peReader.GetMetadataReader();
        foreach (var typeHandle in metadata.TypeDefinitions)
        {
            var type = metadata.GetTypeDefinition(typeHandle);
            foreach (var methodHandle in type.GetMethods())
            {
                var method = metadata.GetMethodDefinition(methodHandle);
                if (method.RelativeVirtualAddress == 0)
                    continue;

                var il = peReader.GetMethodBody(method.RelativeVirtualAddress).GetILBytes();
                Assert(il is not [0x14, 0x7a],
                    $"{assemblyName} contains a protected throw-null method: " +
                    $"{metadata.GetString(type.Namespace)}.{metadata.GetString(type.Name)}." +
                    $"{metadata.GetString(method.Name)}.");
            }
        }
    }
}

static void ValidateActEncounterMapping()
{
    var id = Guid.NewGuid();
    var start = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(10);
    var snapshot = new ActEncounterSnapshot(
        id,
        start,
        null,
        "Test Zone",
        "Test Enemy",
        [
            new ActCombatantSnapshot("local", "You", "SAM", true, 120_000, 2_000, 0, 13_000, 12_000, 12_000),
            new ActCombatantSnapshot("healer", "Healer", "WHM", false, 20_000, 90_000, 1),
        ]);

    var encounter = ActEncounterMapper.Map(snapshot);
    Assert(encounter.Id == id, "ACT encounter id was not preserved.");
    Assert(encounter.StartTime == start, "ACT encounter start time was not preserved.");
    Assert(encounter.IsActive, "Active ACT encounter was mapped as finished.");
    Assert(encounter.TotalDamage == 140_000, "ACT combatant damage totals were not mapped.");
    Assert(encounter.TotalHealing == 92_000, "ACT combatant healing totals were not mapped.");
    Assert(encounter.TotalDeaths == 1, "ACT combatant deaths were not mapped.");
    Assert(encounter.Combatants.Single(static combatant => combatant.IsLocalPlayer).Name == "You",
        "ACT local player marker was not mapped.");
    Assert(encounter.JobSummaries.Count == 2, "ACT job summaries were not generated.");
    var local = encounter.Combatants.Single(static combatant => combatant.IsLocalPlayer);
    Assert(local.Dps == 13_000 && local.EncDps == 12_000 && local.ExtDps == 12_000,
        "ACT DPS metric fields were not mapped.");
}

static void ValidateChineseCombatChatParsing()
{
    Assert(
        ChineseCombatChatParser.TryParse(
            "埃斯蒂尼安丿发动攻击 \uE06F 木人受到了3714点伤害。",
            string.Empty,
            out var actor,
            out var target,
            out var damage),
        "Chinese auto-attack combat chat was not parsed.");
    Assert(actor == "埃斯蒂尼安丿" && target == "木人" && damage == 3714,
        "Chinese auto-attack fields were mapped incorrectly.");
    Assert(
        ChineseCombatChatParser.TryParse(
            "  \uE06F 暴击！ 木人受到了39870(+56%)点伤害。",
            actor,
            out var inheritedActor,
            out target,
            out damage),
        "Chinese ability damage chat was not parsed.");
    Assert(inheritedActor == actor && target == "木人" && damage == 39870,
        "Chinese ability damage fields were mapped incorrectly.");
}

static string FindProjectRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "DalamudActCompat.slnx")))
        {
            return directory.FullName;
        }

        directory = directory.Parent;
    }

    throw new DirectoryNotFoundException("Could not find the DalamudActCompat project root.");
}

static async Task CreatePackageAsync(string packagePath, string id, string version)
{
    File.Delete(packagePath);
    using var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create);
    var manifestEntry = archive.CreateEntry(ActPluginManifest.FileName);
    await using (var stream = manifestEntry.Open())
    {
        await JsonSerializer.SerializeAsync(stream, new ActPluginManifest
        {
            Id = id,
            Name = "Example Plugin",
            Version = version,
            EntryAssembly = "Example.Plugin.dll",
            EntryType = "Example.Plugin.EntryPoint",
            HostApiVersion = 1,
        });
    }

    var assemblyEntry = archive.CreateEntry("Example.Plugin.dll");
    await using var assemblyStream = assemblyEntry.Open();
    await assemblyStream.WriteAsync(new byte[] { 0x4d, 0x5a });
}

static async Task WriteArchiveEntryAsync(ZipArchive archive, string path, byte[] content)
{
    var entry = archive.CreateEntry(path);
    await using var stream = entry.Open();
    await stream.WriteAsync(content);
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

internal enum PluginIntegrationMode
{
    Disabled,
    Auto,
}

internal sealed class EnumSettingsOwner
{
    public PluginIntegrationMode PluginIntegration { get; set; }
}

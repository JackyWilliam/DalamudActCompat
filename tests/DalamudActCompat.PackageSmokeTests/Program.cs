using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Forms;
using System.Xml;
using Advanced_Combat_Tracker;
using Dalamud.Plugin.Services;
using DalamudActCompat.ActRuntime;
using DalamudActCompat.Compatibility.PluginHost;
using DalamudActCompat.Compatibility.Cactbot;
using DalamudActCompat.Core.Models;
using DalamudActCompat.Core.State;
using DalamudActCompat.Fflogs;
using DalamudActCompat.Infrastructure.Storage;
using DalamudActCompat.Infrastructure.Ipc;
using DalamudActCompat.Meter;
using DalamudActCompat.Parser;
using DalamudActCompat.Plugin;
using DalamudActCompat.Protocol;
using DalamudActCompat.UI;
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
    ValidateActCustomTriggerCompatibility();
    ValidateSynchronousActInvocation();
    ValidatePostNamazuRawLogCompatibility();
    ValidateActTtsDispatch();
    ValidateFoxTtsBridge();
    ValidateRuntimePluginStartupOrder();
    ValidateBoundedHostQueue();
    ValidateBoundedNotActQueues();
    ValidateActCallbackCircuitBreaker();
    ValidatePlayerIdentityResolution();
    ValidateDalamudGameStateBridge();
    ValidateCactbotSpokenAlertDefaults();
    ValidateOverlayInitialStateEvents();
    ValidateHtmlOverlayDefaults();
    ValidateParserDependencyVersions();
    ValidateChinese755Opcodes();
    ValidateMeterRows();
    ValidateCompactMeterLayout();
    ValidateFflogsEstimateCurve();

    var packagePath = Path.Combine(testRoot, "valid.zip");
    await CreatePackageAsync(packagePath, "example.plugin", "1.0.0");
    var paths = new PluginPaths(Path.Combine(testRoot, "config"));
    Directory.CreateDirectory(paths.ActPluginDirectory);
    var installer = new ActPluginPackageInstaller(paths);
    await ValidateBundledPluginDisclosureAsync(testRoot);
    if (string.Equals(
            Environment.GetEnvironmentVariable("ACTCOMPAT_ONLINE_UPDATE_SMOKE"),
            "1",
            StringComparison.Ordinal))
    {
        await ValidateLiveBundledPluginUpdateCheckAsync(testRoot);
    }

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
    var customCactbotUserFile = Path.Combine(paths.CactbotDirectory, "user", "custom.js");
    Directory.CreateDirectory(Path.GetDirectoryName(customCactbotUserFile)!);
    await File.WriteAllTextAsync(customCactbotUserFile, "custom");
    await cactbotInstaller.InstallAsync(cactbotPackage, CancellationToken.None);
    Assert(
        await File.ReadAllTextAsync(customCactbotUserFile) == "custom",
        "Cactbot upgrade overwrote the user's custom files.");
    await ValidateBundledCactbotPortableInstallAsync(testRoot);
    await ValidateOfficialBundledCactbotAsync(testRoot);

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

static void ValidateBoundedHostQueue()
{
    using var queue = new BoundedHostMessageQueue();
    var session = Guid.NewGuid().ToString("N");
    for (var index = 0; index < HostProtocol.ControlQueueCapacity; index++)
    {
        Assert(
            queue.TryEnqueue(HostEnvelope.Create(
                session,
                index + 1,
                HostMessageTypes.Heartbeat,
                HostMessagePriority.Control,
                new { index })),
            "Control queue rejected an item before reaching its configured capacity.");
    }

    Assert(
        !queue.TryEnqueue(HostEnvelope.Create(
            session,
            HostProtocol.ControlQueueCapacity + 1,
            HostMessageTypes.Heartbeat,
            HostMessagePriority.Control,
            new { overflow = true })),
        "Control queue did not reject overflow.");

    queue.Clear();
    for (var index = 0; index <= HostProtocol.DataQueueCapacity; index++)
    {
        Assert(
            queue.TryEnqueue(HostEnvelope.Create(
                session,
                index + 1,
                HostMessageTypes.LogBatch,
                HostMessagePriority.Data,
                new { index })),
            "Data queue rejected a low-priority item instead of applying drop-oldest backpressure.");
    }

    Assert(
        queue.DataCount == HostProtocol.DataQueueCapacity,
        "Data queue exceeded its configured bound.");
    Assert(
        queue.DroppedDataMessages == 1,
        "Data queue did not record its dropped oldest item.");
    queue.Clear();
    Assert(
        queue.TryEnqueue(HostEnvelope.Create(
            session,
            1,
            HostMessageTypes.Snapshot,
            HostMessagePriority.State,
            new { value = 1 })) &&
        queue.TryEnqueue(HostEnvelope.Create(
            session,
            2,
            HostMessageTypes.Snapshot,
            HostMessagePriority.State,
            new { value = 2 })) &&
        queue.DataCount == 1,
        "State messages were not coalesced to the latest value.");
}

static void ValidateBoundedNotActQueues()
{
    var actMain = (FormActMain)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
        typeof(FormActMain));
    typeof(FormActMain).GetField(
            "<PluginLog>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic)!
        .SetValue(actMain, new TestActLogger());
    typeof(FormActMain).GetField(
            "<LogQueue>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic)!
        .SetValue(actMain, new System.Collections.Concurrent.ConcurrentQueue<string>());
    typeof(FormActMain).GetField(
            "afterActionsQueue",
            BindingFlags.Instance | BindingFlags.NonPublic)!
        .SetValue(
            actMain,
            new System.Collections.Concurrent.ConcurrentQueue<MasterSwing>());

    var enqueueLogLine = typeof(FormActMain).GetMethod(
                             "EnqueueLogLine",
                             BindingFlags.Instance | BindingFlags.NonPublic)
                         ?? throw new MissingMethodException(
                             typeof(FormActMain).FullName,
                             "EnqueueLogLine");
    for (var index = 0; index < 8193; index++)
    {
        enqueueLogLine.Invoke(actMain, [$"line-{index}"]);
    }

    Assert(actMain.LogQueue.Count == 8192, "NotACT log queue exceeded 8192 entries.");
    Assert(actMain.DroppedLogLines == 1, "NotACT log queue did not report drop-oldest.");

    var enqueueCombatAction = typeof(FormActMain).GetMethod(
                                  "EnqueueCombatAction",
                                  BindingFlags.Instance | BindingFlags.NonPublic)
                              ?? throw new MissingMethodException(
                                  typeof(FormActMain).FullName,
                                  "EnqueueCombatAction");
    for (var index = 0; index < 4097; index++)
    {
        enqueueCombatAction.Invoke(
            actMain,
            [System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(MasterSwing))]);
    }

    Assert(
        actMain.DroppedCombatActions == 1,
        "NotACT combat action queue did not report drop-oldest.");
}

static void ValidateActCustomTriggerCompatibility()
{
    var property = typeof(FormActMain).GetProperty(nameof(FormActMain.CustomTriggers));
    Assert(
        property?.PropertyType == typeof(SortedList<string, CustomTrigger>) &&
        property.GetMethod is not null,
        "ACT CustomTriggers getter is missing or has an incompatible ABI.");

    var expectedProperties = new Dictionary<string, Type>
    {
        [nameof(CustomTrigger.Active)] = typeof(bool),
        [nameof(CustomTrigger.RestrictToCategoryZone)] = typeof(bool),
        [nameof(CustomTrigger.Tabbed)] = typeof(bool),
        [nameof(CustomTrigger.Timer)] = typeof(bool),
        [nameof(CustomTrigger.SoundType)] = typeof(int),
        [nameof(CustomTrigger.Category)] = typeof(string),
        [nameof(CustomTrigger.ShortRegexString)] = typeof(string),
        [nameof(CustomTrigger.SoundData)] = typeof(string),
        [nameof(CustomTrigger.TimerName)] = typeof(string),
    };
    foreach (var expected in expectedProperties)
    {
        Assert(
            typeof(CustomTrigger).GetProperty(expected.Key)?.PropertyType == expected.Value,
            $"ACT CustomTrigger.{expected.Key} is missing or has an incompatible type.");
    }
}

static void ValidateSynchronousActInvocation()
{
    var actMain = (FormActMain)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
        typeof(FormActMain));
    actMain.InvokeSynchronously = true;
    var calls = 0;
    var result = actMain.Invoke((Func<int>)(() => ++calls));
    var asyncResult = actMain.BeginInvoke((Func<int>)(() => ++calls), null);
    var asyncValue = actMain.EndInvoke(asyncResult);
    Assert(
        result is 1 && asyncValue is 2 && calls == 2,
        "Handle-free ACT invocation did not execute synchronously in the in-process runtime.");
}

static void ValidateActCallbackCircuitBreaker()
{
    var actMain = (FormActMain)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
        typeof(FormActMain));
    typeof(FormActMain).GetField(
            "<PluginLog>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic)!
        .SetValue(actMain, new TestActLogger());
    var healthField = typeof(FormActMain).GetField(
                          "callbackHealth",
                          BindingFlags.Instance | BindingFlags.NonPublic)
                      ?? throw new MissingFieldException(
                          typeof(FormActMain).FullName,
                          "callbackHealth");
    healthField.SetValue(
        actMain,
        Activator.CreateInstance(healthField.FieldType)
        ?? throw new InvalidOperationException("Could not create callback health dictionary."));
    var invokeTracked = typeof(FormActMain).GetMethod(
                            "InvokeTracked",
                            BindingFlags.Instance | BindingFlags.NonPublic)
                        ?? throw new MissingMethodException(
                            typeof(FormActMain).FullName,
                            "InvokeTracked");
    var calls = 0;
    LogLineEventDelegate handler = (_, _) =>
    {
        calls++;
        throw new InvalidOperationException("injected callback failure");
    };
    Action<Delegate> invoke = callback =>
        ((LogLineEventDelegate)callback)(false, null!);
    for (var index = 0; index < 6; index++)
    {
        invokeTracked.Invoke(actMain, [handler, "FaultInjection", invoke]);
    }

    var health = actMain.GetCallbackHealth().Single();
    Assert(calls == 5, "Callback circuit did not stop the sixth failing invocation.");
    Assert(
        health.Exceptions == 5 && health.CircuitOpen,
        "Five consecutive callback exceptions did not open the per-plugin circuit.");
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

    var configuration = new PluginConfiguration
    {
        UiLanguage = "zh-CN",
    };
    var text = new UiText(configuration);
    settings.PlayerIdentityMode = PlayerIdentityMode.Job;
    Assert(
        PlayerIdentityFormatter.Format(encounter.Combatants[0], encounter.Combatants, settings, text) == "骑士",
        "Job identity mode did not replace the player ID with the localized job.");
    settings.PlayerIdentityMode = PlayerIdentityMode.Anonymous;
    settings.LocalPlayerAlias = "我";
    Assert(
        PlayerIdentityFormatter.Format(encounter.Combatants[0], encounter.Combatants, settings, text) == "我" &&
        PlayerIdentityFormatter.Format(encounter.Combatants[1], encounter.Combatants, settings, text) == "玩家 1",
        "Anonymous identity mode did not produce stable local and party aliases.");
    Assert(
        encounter.Combatants[0].Name == "Tank@Alpha",
        "Display-only player ID masking mutated the encounter data.");
}

static void ValidateCompactMeterLayout()
{
    var method = typeof(MeterWindow).GetMethod(
                     "CalculateSingleCombatantWindowSize",
                     BindingFlags.Static | BindingFlags.NonPublic)
                 ?? throw new InvalidOperationException(
                     "Single-combatant Meter layout helper was not found.");
    var compact = (System.Numerics.Vector2)(method.Invoke(
        null,
        [new System.Numerics.Vector2(500, 420), true, 1.0f])
        ?? throw new InvalidOperationException("Compact Meter layout returned no size."));
    Assert(
        compact.X is >= 320 and <= 440,
        "Single-combatant Meter width is not compact or readable.");
    Assert(
        compact.Y is >= 90 and <= 120,
        "Single-combatant Meter kept the oversized multi-player height.");
}

static void ValidateFflogsEstimateCurve()
{
    var estimatePercentile = typeof(FflogsEstimateService).GetMethod(
        "EstimatePercentile",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("FFLogs percentile estimator was not found.");
    var curve = new FflogsCurvePoint[]
    {
        new(0, 1_000),
        new(50, 2_000),
        new(75, 3_000),
        new(100, 5_000),
    };
    var midpoint = (double)estimatePercentile.Invoke(null, [curve, 2_500d])!;
    Assert(
        Math.Abs(midpoint - 62.5) < 0.001,
        "FFLogs estimate did not interpolate between public ranking samples.");
    Assert(
        (double)estimatePercentile.Invoke(null, [curve, 100d])! == 0 &&
        (double)estimatePercentile.Invoke(null, [curve, 9_000d])! == 100,
        "FFLogs estimate did not clamp values outside the sampled curve.");

    var legendary = FflogsEstimateService.ColorForPercentile(100);
    var pink = FflogsEstimateService.ColorForPercentile(99);
    var orange = FflogsEstimateService.ColorForPercentile(95);
    Assert(
        legendary != pink && pink != orange,
        "FFLogs estimate color thresholds collapsed distinct ranking tiers.");
}

static void ValidateChinese755Opcodes()
{
    OpcodeManager.Instance.SetRegion(GameRegion.Chinese);
    var opcodes = OpcodeManager.Instance.CurrentOpcodes;
    var expected = new Dictionary<string, ushort>
    {
        ["Ability1"] = 0x01F3,
        ["Ability8"] = 0x0114,
        ["Ability16"] = 0x02CD,
        ["Ability24"] = 0x00ED,
        ["Ability32"] = 0x02C7,
        ["ActorCast"] = 0x016B,
        ["EffectResult"] = 0x0238,
        ["ActorControl"] = 0x0112,
        ["ActorControlSelf"] = 0x020E,
        ["ActorControlTarget"] = 0x01A2,
        ["StatusEffectList"] = 0x014C,
        ["StatusEffectList2"] = 0x0201,
        ["StatusEffectList3"] = 0x02E8,
    };

    foreach (var pair in expected)
    {
        Assert(
            opcodes.TryGetValue(pair.Key, out var actual) && actual == pair.Value,
            $"Chinese 7.55 opcode {pair.Key} was {actual:X}, expected {pair.Value:X}.");
    }
}

static void ValidatePostNamazuRawLogCompatibility()
{
    var method = typeof(FormActMain).GetMethod(
        "ParseRawLogLine",
        BindingFlags.Instance | BindingFlags.Public,
        binder: null,
        [typeof(bool), typeof(DateTime), typeof(string)],
        modifiers: null);
    Assert(
        method is not null && method.ReturnType == typeof(void),
        "The ACT three-argument ParseRawLogLine ABI required by PostNamazu is missing.");
}

static void ValidateParserDependencyVersions()
{
    Assert(
        typeof(IINACT.Plugin).Assembly.GetName().Version == new Version(2, 10, 3, 4),
        "IINACT is not at 2.10.3.4.");
    Assert(
        typeof(FFXIVMemory).Assembly.GetName().Version == new Version(0, 19, 103, 0),
        "OverlayPlugin Core is not at 0.19.103.");

    var runtimeDirectory = Path.Combine(
        FindProjectRoot(),
        "src",
        "DalamudActCompat",
        "bin",
        "Release");
    AssertFileVersion(
        Path.Combine(runtimeDirectory, "Unscrambler.dll"),
        "7.55.0.0",
        "Unscrambler.XIV");
    AssertFileVersion(
        Path.Combine(runtimeDirectory, "FFXIV_ACT_Plugin.dll"),
        "3.0.2.5",
        "FFXIV_ACT_Plugin");

    var overlayAssembly = typeof(FFXIVMemory).Assembly;
    var opcodeResource = overlayAssembly
        .GetManifestResourceNames()
        .Single(name => name.EndsWith("opcodes.jsonc", StringComparison.OrdinalIgnoreCase));
    using var opcodeStream = overlayAssembly.GetManifestResourceStream(opcodeResource)
                             ?? throw new InvalidOperationException(
                                 "OverlayPlugin opcode resource was not found.");
    using var document = JsonDocument.Parse(
        opcodeStream,
        new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });
    var chinese755 = document.RootElement
        .GetProperty("Chinese")
        .GetProperty("2026.07.16.0001.0000");
    Assert(
        chinese755.GetProperty("MapEffect").GetProperty("opcode").GetInt32() == 887 &&
        chinese755.GetProperty("ActorMove").GetProperty("opcode").GetInt32() == 552,
        "OverlayPlugin Chinese 7.55 opcodes are stale.");
}

static void AssertFileVersion(string path, string expected, string component)
{
    Assert(File.Exists(path), $"{component} assembly is missing: {path}");
    var actual = System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileVersion;
    Assert(actual == expected, $"{component} version was {actual}, expected {expected}.");
}

static async Task ValidateBundledPluginDisclosureAsync(string testRoot)
{
    var bundleParent = Path.Combine(
        FindProjectRoot(),
        "src",
        "DalamudActCompat",
        "bin",
        "Release");
    var paths = new PluginPaths(Path.Combine(testRoot, "bundled-config"));
    var installer = new ActPluginPackageInstaller(paths);
    var configuration = new DalamudActCompat.Plugin.PluginConfiguration();
    var manager = new BundledActPluginManager(
        bundleParent,
        "0.2.31.0",
        installer,
        configuration);

    var pending = manager.GetPendingDisclosures();
    Assert(pending.Count == 3, "A new install did not require all three bundled DLL disclosures.");
    Assert(
        pending.Any(plugin =>
            plugin.Id == "triggernometry" &&
            plugin.Author == "Paissa Heavy Industries" &&
            plugin.Maintainer.Contains("MnFeN", StringComparison.Ordinal) &&
            plugin.DownloadUrl.StartsWith("https://", StringComparison.Ordinal)),
        "Triggernometry author, maintainer, or DLL URL disclosure is incomplete.");
    Assert(
        pending.Any(plugin =>
            plugin.Id == "act.foxtts" &&
            plugin.Author == "Noisyfox" &&
            plugin.ProjectUrl == "https://github.com/Noisyfox/ACT.FoxTTS"),
        "ACT.FoxTTS author or project URL disclosure is incomplete.");
    Assert(
        pending.Any(plugin =>
            plugin.Id == "postnamazu" &&
            plugin.Author == "Natsukage" &&
            plugin.DownloadUrl.Contains("/releases/download/", StringComparison.Ordinal)),
        "PostNamazu author or DLL URL disclosure is incomplete.");

    await manager.InstallAndAcknowledgeAsync(pending, CancellationToken.None);
    Assert(
        manager.GetPendingDisclosures().Count == 0,
        "Acknowledged current bundled DLLs still required disclosure.");
    var installed = installer.Discover(configuration.DisabledActPluginIds);
    Assert(installed.Count == 3, "Not all bundled DLLs were installed.");
    Assert(
        installed.All(manager.IsAllowedToLoad),
        "Acknowledged current bundled DLLs were not enabled for runtime loading.");

    var triggernometry = installed.Single(plugin => plugin.Manifest.Id == "triggernometry");
    var duplicateDirectory = Path.Combine(paths.ActPluginDirectory, "triggernometry-old-copy");
    Directory.CreateDirectory(duplicateDirectory);
    foreach (var file in Directory.EnumerateFiles(triggernometry.InstallDirectory))
    {
        File.Copy(file, Path.Combine(duplicateDirectory, Path.GetFileName(file)));
    }

    Assert(
        manager.GetPendingDisclosures().Count == 0,
        "A duplicate legacy plugin id interrupted bundled DLL disclosure checks.");

    using (var httpClient = new HttpClient(
               new BundledPluginUpdateHandler(manager.Plugins)))
    using (var updateChecker = new BundledActPluginUpdateChecker(
               paths.BundledPluginUpdateCacheDirectory,
               httpClient))
    {
        var check = await updateChecker.CheckAsync(
            manager.Plugins,
            CancellationToken.None);
        Assert(
            check.Failures.Count == 0 && check.Updates.Count == 3,
            "Runtime author-source checking did not find all simulated DLL updates.");
        Assert(
            check.Updates.All(plugin =>
                plugin.IsOnlineUpdate &&
                plugin.DownloadUrl.StartsWith("https://", StringComparison.Ordinal) &&
                File.Exists(plugin.AssemblyPath)),
            "Runtime DLL update candidates were not cached with disclosure URLs.");
        Assert(
            manager.ApplyOnlineUpdates(check.Updates) == 3,
            "Runtime DLL update candidates were not applied to the disclosure gate.");
        var onlinePending = manager.GetPendingDisclosures();
        Assert(
            onlinePending.Count == 3 &&
            onlinePending.All(plugin => plugin.IsOnlineUpdate),
            "Online DLL updates did not require a new disclosure.");
        Assert(
            installed.All(plugin => !manager.IsAllowedToLoad(plugin)),
            "Old DLLs remained loadable after an online update was discovered.");

        await manager.InstallAndAcknowledgeAsync(
            onlinePending,
            CancellationToken.None);
        Assert(
            manager.GetPendingDisclosures().Count == 0 &&
            configuration.BundledPluginUpdateRecords.Count == 3,
            "Online DLL updates were not installed, acknowledged, and persisted.");
    }

    configuration = Newtonsoft.Json.JsonConvert.DeserializeObject<
                        DalamudActCompat.Plugin.PluginConfiguration>(
                        Newtonsoft.Json.JsonConvert.SerializeObject(configuration))
                    ?? throw new InvalidOperationException(
                        "Plugin configuration update records did not round-trip.");
    var persistedOnline = new BundledActPluginManager(
        bundleParent,
        "0.2.31.0",
        installer,
        configuration);
    Assert(
        persistedOnline.GetPendingDisclosures().Count == 0,
        "An accepted online DLL update was lost after recreating the manager.");
    Assert(
        installer
            .Discover(configuration.DisabledActPluginIds)
            .Where(plugin => configuration.BundledPluginUpdateRecords.ContainsKey(
                plugin.Manifest.Id))
            .Any(persistedOnline.IsAllowedToLoad),
        "Persisted online DLL updates were not loadable while offline.");

    var nextRelease = new BundledActPluginManager(
        bundleParent,
        "0.2.32.0",
        installer,
        configuration);
    Assert(
        nextRelease.GetPendingDisclosures().Count == 3,
        "A DalamudActCompat update did not require the bundled DLL disclosure again.");
    Assert(
        installed.All(plugin => !nextRelease.IsAllowedToLoad(plugin)),
        "Bundled DLLs were loadable before acknowledging the new host release notice.");
}

static async Task ValidateBundledCactbotPortableInstallAsync(string testRoot)
{
    var pluginDirectory = Path.Combine(testRoot, "portable-plugin");
    var bundleDirectory = Path.Combine(
        pluginDirectory,
        BundledCactbotManager.DirectoryName);
    Directory.CreateDirectory(bundleDirectory);
    var archiveName = "cactbot-test.zip";
    var archivePath = Path.Combine(bundleDirectory, archiveName);
    var builtRuntime = Path.Combine(
        FindProjectRoot(),
        "src",
        "DalamudActCompat",
        "bin",
        "Release",
        "DalamudActCompat.ActRuntime.dll");
    var assemblyBytes = await File.ReadAllBytesAsync(builtRuntime);
    using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
    {
        await WriteArchiveEntryAsync(
            archive,
            "cactbot/cactbot/CactbotOverlay.dll",
            assemblyBytes);
        await WriteArchiveEntryAsync(
            archive,
            "cactbot/cactbot/ui/raidboss/raidboss.html",
            "<html></html>"u8.ToArray());
    }

    var version = System.Diagnostics.FileVersionInfo.GetVersionInfo(builtRuntime).FileVersion!;
    var sha256 = Convert
        .ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(archivePath)))
        .ToLowerInvariant();
    var lockPath = Path.Combine(
        bundleDirectory,
        BundledCactbotManager.LockFileName);
    await File.WriteAllTextAsync(
        lockPath,
        JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            version,
            projectUrl = "https://github.com/OverlayPlugin/cactbot",
            downloadUrl = "https://example.invalid/cactbot-test.zip",
            relativeArchive = archiveName,
            sha256,
        }));

    var otherUserConfig = Path.Combine(testRoot, "other-user", "plugin-config");
    var paths = new PluginPaths(otherUserConfig);
    var installer = new CactbotPackageInstaller(paths);
    var manager = new BundledCactbotManager(pluginDirectory, installer);
    Assert(
        await manager.EnsureCurrentAsync(CancellationToken.None),
        "Fresh user did not receive bundled Cactbot.");
    Assert(
        installer.IsInstalled &&
        File.Exists(Path.Combine(
            otherUserConfig,
            "cactbot",
            "ui",
            "raidboss",
            "raidboss.html")),
        "Bundled Cactbot was not installed under the supplied user's config path.");
    Assert(
        !await manager.EnsureCurrentAsync(CancellationToken.None),
        "Current bundled Cactbot was unnecessarily reinstalled.");
}

static async Task ValidateOfficialBundledCactbotAsync(string testRoot)
{
    var pluginOutputDirectory = Path.Combine(
        FindProjectRoot(),
        "src",
        "DalamudActCompat",
        "bin",
        "Release");
    var paths = new PluginPaths(Path.Combine(
        testRoot,
        "official-bundle-user",
        "plugin-config"));
    var installer = new CactbotPackageInstaller(paths);
    var manager = new BundledCactbotManager(pluginOutputDirectory, installer);
    Assert(
        await manager.EnsureCurrentAsync(CancellationToken.None),
        "Official bundled Cactbot was not installed for a fresh user.");
    Assert(
        installer.IsInstalled &&
        installer.InstalledVersion is { } installedVersion &&
        installedVersion >= Version.Parse(manager.BundledVersion),
        "Official bundled Cactbot version or layout is invalid after installation.");
}

static async Task ValidateLiveBundledPluginUpdateCheckAsync(string testRoot)
{
    var bundleParent = Path.Combine(
        FindProjectRoot(),
        "src",
        "DalamudActCompat",
        "bin",
        "Release");
    var paths = new PluginPaths(Path.Combine(testRoot, "live-update-config"));
    var installer = new ActPluginPackageInstaller(paths);
    var configuration = new DalamudActCompat.Plugin.PluginConfiguration();
    var manager = new BundledActPluginManager(
        bundleParent,
        "0.2.31.0",
        installer,
        configuration);
    using var checker = new BundledActPluginUpdateChecker(
        paths.BundledPluginUpdateCacheDirectory);
    var check = await checker.CheckAsync(
        manager.Plugins,
        CancellationToken.None);
    Assert(
        check.Failures.Count == 0,
        "Live author-source DLL checking failed: " +
        string.Join("; ", check.Failures));
    Assert(
        check.Updates.Count == 0,
        "The bundled DLL lock is stale compared with the live author sources.");
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
    Assert(!settings.IsVisible, "HTML overlays must remain closed until explicitly opened.");
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

    var interactionType = formType.GetNestedType(
                              "OverlayInteraction",
                              BindingFlags.NonPublic)
                          ?? throw new InvalidOperationException(
                              "HTML overlay interaction mode was not found.");
    var getInteraction = formType.GetMethod(
                             "GetOverlayInteraction",
                             BindingFlags.Static | BindingFlags.NonPublic)
                         ?? throw new InvalidOperationException(
                             "HTML overlay cursor interaction helper was not found.");
    var move = Enum.Parse(interactionType, "Move");
    var resize = Enum.Parse(interactionType, "Resize");
    Assert(
        Equals(
            getInteraction.Invoke(
                null,
                [new System.Drawing.Size(900, 320), new System.Drawing.Point(450, 160)]),
            move),
        "HTML overlay cursor polling no longer treats the client area as a drag target.");
    Assert(
        Equals(
            getInteraction.Invoke(
                null,
                [new System.Drawing.Size(900, 320), new System.Drawing.Point(895, 315)]),
            resize),
        "HTML overlay cursor polling no longer exposes a bottom-right resize target.");
    var shouldBeginInteraction = formType.GetMethod(
                                     "ShouldBeginOverlayInteraction",
                                     BindingFlags.Static | BindingFlags.NonPublic)
                                 ?? throw new InvalidOperationException(
                                     "HTML overlay interaction transition helper was not found.");
    Assert(
        shouldBeginInteraction.Invoke(
            null,
            [true, true, true, false, true]) as bool? == true,
        "A fresh left click inside a visible editing overlay did not begin an interaction.");
    Assert(
        shouldBeginInteraction.Invoke(
            null,
            [true, true, true, true, true]) as bool? == false,
        "Holding the settings click while entering edit mode incorrectly began a drag.");
    Assert(
        shouldBeginInteraction.Invoke(
            null,
            [true, true, true, false, false]) as bool? == false,
        "A click outside the overlay incorrectly began an interaction.");
    var tryAcquireInteraction = formType.GetMethod(
                                    "TryAcquireInteraction",
                                    BindingFlags.Static | BindingFlags.NonPublic)
                                ?? throw new InvalidOperationException(
                                    "HTML overlay interaction ownership helper was not found.");
    var releaseInteraction = formType.GetMethod(
                                 "ReleaseInteraction",
                                 BindingFlags.Static | BindingFlags.NonPublic)
                             ?? throw new InvalidOperationException(
                                 "HTML overlay interaction release helper was not found.");
    var firstWindow = (nint)101;
    var secondWindow = (nint)202;
    Assert(
        tryAcquireInteraction.Invoke(null, [firstWindow]) as bool? == true,
        "The first editing overlay could not acquire mouse interaction ownership.");
    Assert(
        tryAcquireInteraction.Invoke(null, [secondWindow]) as bool? == false,
        "Two editing overlays acquired the same mouse interaction.");
    releaseInteraction.Invoke(null, [secondWindow]);
    Assert(
        tryAcquireInteraction.Invoke(null, [secondWindow]) as bool? == false,
        "A non-owner released another overlay's mouse interaction.");
    releaseInteraction.Invoke(null, [firstWindow]);
    Assert(
        tryAcquireInteraction.Invoke(null, [secondWindow]) as bool? == true,
        "Mouse interaction ownership was not released after dragging ended.");
    releaseInteraction.Invoke(null, [secondWindow]);
    var calculateBounds = formType.GetMethod(
                              "CalculateInteractionBounds",
                              BindingFlags.Static | BindingFlags.NonPublic)
                          ?? throw new InvalidOperationException(
                              "HTML overlay interaction calculation was not found.");
    var startBounds = new System.Drawing.Rectangle(100, 200, 900, 320);
    var startCursor = new System.Drawing.Point(400, 300);
    var currentCursor = new System.Drawing.Point(430, 350);
    Assert(
        calculateBounds.Invoke(null, [startBounds, startCursor, currentCursor, move])
            is System.Drawing.Rectangle { X: 130, Y: 250, Width: 900, Height: 320 },
        "HTML overlay dragging no longer follows the global cursor delta.");
    Assert(
        calculateBounds.Invoke(null, [startBounds, startCursor, currentCursor, resize])
            is System.Drawing.Rectangle { X: 100, Y: 200, Width: 930, Height: 370 },
        "HTML overlay resizing no longer follows the global cursor delta.");
    var shouldEnableBrowserInput = formType.GetMethod(
                                       "ShouldEnableBrowserInput",
                                       BindingFlags.Static | BindingFlags.NonPublic)
                                   ?? throw new InvalidOperationException(
                                       "HTML overlay browser input routing helper was not found.");
    settings.SetEditing(true);
    Assert(
        shouldEnableBrowserInput.Invoke(null, [settings]) as bool? == false,
        "Windowed WebView2 still captures mouse input while editing an overlay.");
    settings.IsLocked = true;
    Assert(
        shouldEnableBrowserInput.Invoke(null, [settings]) as bool? == true,
        "Windowed WebView2 did not resume page interaction after the overlay was locked.");
    var shieldType = typeof(MeterService).Assembly.GetType(
                         "DalamudActCompat.UI.OverlayEditShield",
                         throwOnError: true)
                     ?? throw new InvalidOperationException(
                         "The transparent overlay edit shield was not found.");
    var isShieldRequired = shieldType.GetMethod(
                               "IsRequired",
                               BindingFlags.Static | BindingFlags.NonPublic)
                           ?? throw new InvalidOperationException(
                               "The transparent overlay edit shield condition was not found.");
    Assert(
        isShieldRequired.Invoke(null, [false]) as bool? == false,
        "The transparent edit shield remained active without a visible editing overlay.");
    Assert(
        isShieldRequired.Invoke(null, [true]) as bool? == true,
        "The transparent edit shield did not activate for a visible editing overlay.");
}

static void ValidateActTtsDispatch()
{
    var actMain = (FormActMain)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
        typeof(FormActMain));
    string? spoken = null;
    actMain.PlayTtsMethod = message => spoken = message;
    actMain.TTS("FoxTTS bridge");
    Assert(
        spoken == "FoxTTS bridge",
        "The ACT TTS entry point bypassed the standard PlayTtsMethod delegate.");
}

static void ValidateFoxTtsBridge()
{
    var previousActMain = ActGlobals.oFormActMain;
    var actMain = (FormActMain)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
        typeof(FormActMain));
    var restored = false;
    actMain.PlayTtsMethod = _ => restored = true;
    ActGlobals.oFormActMain = actMain;
    try
    {
        using var foxTts = new FoxTtsProbe();
        var log = DispatchProxy.Create<IPluginLog, NoOpPluginLogProxy>();
        var removeBridge = LoadedActPlugin.InstallFoxTtsBridge(foxTts, log);
        actMain.TTS("Cactbot alert");
        Assert(
            foxTts.Wait(TimeSpan.FromSeconds(5)) && foxTts.LastMessage == "Cactbot alert",
            "The ACT.FoxTTS bridge did not forward a TTS request to FoxTTS Speak.");

        removeBridge();
        actMain.TTS("restored");
        Assert(restored, "Removing the ACT.FoxTTS bridge did not restore the previous TTS delegate.");
    }
    finally
    {
        ActGlobals.oFormActMain = previousActMain;
    }
}

static void ValidateRuntimePluginStartupOrder()
{
    var mustLoadBeforeOverlay = typeof(IinactAdapter).GetMethod(
                                    "MustLoadBeforeOverlay",
                                    BindingFlags.Static | BindingFlags.NonPublic)
                                ?? throw new InvalidOperationException(
                                    "Runtime plugin startup phase selector was not found.");
    var foxTts = new RuntimePluginSpec(
        "ACT.FoxTTS",
        "C:\\plugins\\foxtts",
        "ACT.FoxTTS.dll",
        "ACT.FoxTTS.FoxTTSPlugin");
    var triggernometry = new RuntimePluginSpec(
        "triggernometry",
        "C:\\plugins\\triggernometry",
        "Triggernometry.dll",
        "Triggernometry.Plugin");
    Assert(
        mustLoadBeforeOverlay.Invoke(null, [foxTts]) as bool? == true,
        "FoxTTS is no longer guaranteed to own the ACT TTS dispatcher before Cactbot starts.");
    Assert(
        mustLoadBeforeOverlay.Invoke(null, [triggernometry]) as bool? == false,
        "An unrelated ACT extension was moved ahead of OverlayPlugin startup.");
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
        empty["options"]["raidboss"]?["DefaultPlayerLabel"]?.Value<string>() == "jobFull" &&
        empty["options"]["raidboss"]?["SpokenAlertsEnabled"] is null,
        "The initial Cactbot output or full-job player label was written incorrectly.");

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
                DefaultPlayerLabel = "name",
            },
        }),
    };
    Assert(
        method.Invoke(null, [existing]) as bool? == false &&
        existing["options"]["raidboss"]?["DefaultAlertOutput"]?.Value<string>() == "textOnly" &&
        existing["options"]["raidboss"]?["DefaultPlayerLabel"]?.Value<string>() == "name",
        "An explicit Cactbot output mode or player label was overwritten.");
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

static ZipArchiveEntry? FindArchiveEntry(ZipArchive archive, string normalizedPath)
{
    return archive.Entries.SingleOrDefault(entry => string.Equals(
        entry.FullName.Replace('\\', '/'),
        normalizedPath,
        StringComparison.OrdinalIgnoreCase));
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
    Assert(
        archive.Entries.Any(entry => string.Equals(
            entry.Name,
            "SharpCompress.dll",
            StringComparison.OrdinalIgnoreCase)),
        "The runtime ACT.FoxTTS 7z reader is missing from the release package.");

    var hostSpeech = FindArchiveEntry(archive, "host/System.Speech.dll");
    var runtimeSpeech = FindArchiveEntry(
        archive,
        "runtimes/win/lib/net10.0/System.Speech.dll");
    Assert(
        hostSpeech is not null && runtimeSpeech is not null,
        "The Compatibility Host or Windows System.Speech runtime implementation is missing.");
    using (var hostSpeechStream = hostSpeech!.Open())
    using (var runtimeSpeechStream = runtimeSpeech!.Open())
    {
        Assert(
            SHA256.HashData(hostSpeechStream).AsSpan()
                .SequenceEqual(SHA256.HashData(runtimeSpeechStream)),
            "The Compatibility Host packaged the System.Speech reference assembly instead of its Windows runtime implementation.");
    }

    var requiredBundledPluginFiles = new[]
    {
        "BundledActPlugins/bundled-plugins.lock.json",
        "BundledActPlugins/triggernometry/Triggernometry.dll",
        "BundledActPlugins/triggernometry/zh-CN.triglations.xml",
        "BundledActPlugins/triggernometry/LICENSE.txt",
        "BundledActPlugins/act.foxtts/ACT.FoxTTS.dll",
        "BundledActPlugins/act.foxtts/LICENSE.txt",
        "BundledActPlugins/postnamazu/PostNamazu.dll",
    };
    foreach (var required in requiredBundledPluginFiles)
    {
        Assert(
            archive.Entries.Any(entry =>
                string.Equals(
                    entry.FullName.Replace('\\', '/'),
                    required,
                    StringComparison.OrdinalIgnoreCase)),
            $"Bundled third-party plugin file is missing from the release package: {required}.");
    }

    var bundledLockEntry = FindArchiveEntry(
        archive,
        "BundledActPlugins/bundled-plugins.lock.json")
        ?? throw new InvalidDataException("Bundled ACT plugin lock is missing from the release package.");
    using var bundledLockStream = bundledLockEntry.Open();
    using var bundledLock = JsonDocument.Parse(bundledLockStream);
    foreach (var plugin in bundledLock.RootElement.GetProperty("plugins").EnumerateArray())
    {
        var relativeAssembly = plugin.GetProperty("relativeAssembly").GetString()
            ?? throw new InvalidDataException("Bundled ACT plugin assembly path is empty.");
        var expectedHash = plugin.GetProperty("sha256").GetString()
            ?? throw new InvalidDataException("Bundled ACT plugin SHA-256 is empty.");
        var assemblyEntry = FindArchiveEntry(
            archive,
            $"BundledActPlugins/{relativeAssembly}")
            ?? throw new InvalidDataException(
                $"Bundled ACT plugin assembly is missing: {relativeAssembly}.");
        using var assemblyStream = assemblyEntry.Open();
        var actualHash = Convert.ToHexString(SHA256.HashData(assemblyStream));
        Assert(
            string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase),
            $"Bundled ACT plugin does not match its locked SHA-256: {relativeAssembly}.");
    }
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
            new ActCombatantSnapshot(
                "early",
                "Early Pull",
                "DRG",
                false,
                1,
                0,
                0,
                double.PositiveInfinity,
                double.NegativeInfinity,
                double.NaN),
        ]);

    var encounter = ActEncounterMapper.Map(snapshot);
    Assert(encounter.Id == id, "ACT encounter id was not preserved.");
    Assert(encounter.StartTime == start, "ACT encounter start time was not preserved.");
    Assert(encounter.IsActive, "Active ACT encounter was mapped as finished.");
    Assert(encounter.TotalDamage == 140_001, "ACT combatant damage totals were not mapped.");
    Assert(encounter.TotalHealing == 92_000, "ACT combatant healing totals were not mapped.");
    Assert(encounter.TotalDeaths == 1, "ACT combatant deaths were not mapped.");
    Assert(encounter.Combatants.Single(static combatant => combatant.IsLocalPlayer).Name == "You",
        "ACT local player marker was not mapped.");
    Assert(encounter.JobSummaries.Count == 3, "ACT job summaries were not generated.");
    var local = encounter.Combatants.Single(static combatant => combatant.IsLocalPlayer);
    Assert(local.Dps == 13_000 && local.EncDps == 12_000 && local.ExtDps == 12_000,
        "ACT DPS metric fields were not mapped.");
    var early = encounter.Combatants.Single(static combatant => combatant.Name == "Early Pull");
    Assert(early.Dps == 0 && early.EncDps == 0 && early.ExtDps == 0,
        "Non-finite ACT rates were not normalized before persistence.");
    Assert(
        !string.IsNullOrWhiteSpace(JsonSerializer.Serialize(encounter)),
        "A mapped ACT encounter with early-pull rates could not be serialized.");
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

internal sealed class FoxTtsProbe : IDisposable
{
    private readonly ManualResetEventSlim spoken = new();

    public string? LastMessage { get; private set; }

    public void Speak(string message)
    {
        LastMessage = message;
        spoken.Set();
    }

    public bool Wait(TimeSpan timeout) => spoken.Wait(timeout);

    public void Dispose() => spoken.Dispose();
}

public class NoOpPluginLogProxy : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var returnType = targetMethod?.ReturnType;
        return returnType is null || returnType == typeof(void)
            ? null
            : returnType.IsValueType
                ? Activator.CreateInstance(returnType)
                : null;
    }
}

internal sealed class TestActLogger : IActLogger
{
    public void Error(Exception exception, string message)
    {
    }

    public void Verbose(Exception exception, string message)
    {
    }

    public void Warning(string message)
    {
    }
}

internal sealed class BundledPluginUpdateHandler : HttpMessageHandler
{
    private const string TriggerTranslationUrl =
        "https://1824544011.v.123pan.cn/1824544011/Triggernometry_Release_CN/zh-CN.triglations.xml";
    private const string FoxApiUrl =
        "https://api.github.com/repos/Noisyfox/ACT.FoxTTS/releases/latest";
    private const string PostApiUrl =
        "https://api.github.com/repos/Natsukage/PostNamazu/releases/latest";
    private const string FoxDownloadUrl = "https://downloads.example/ACT.FoxTTS.7z";
    private const string PostDownloadUrl = "https://downloads.example/PostNamazu.zip";

    private readonly string triggerDownloadUrl;
    private readonly byte[] triggerAssembly;
    private readonly byte[] foxArchive;
    private readonly byte[] postArchive;

    public BundledPluginUpdateHandler(
        IReadOnlyList<BundledActPluginDescriptor> bundled)
    {
        var trigger = bundled.Single(plugin => plugin.Id == "triggernometry");
        var fox = bundled.Single(plugin => plugin.Id == "act.foxtts");
        var post = bundled.Single(plugin => plugin.Id == "postnamazu");
        triggerDownloadUrl = trigger.DownloadUrl;
        triggerAssembly = CreateChangedAssembly(trigger.AssemblyPath);
        foxArchive = CreateArchive(
            "release/ACT.FoxTTS.dll",
            CreateChangedAssembly(fox.AssemblyPath));
        postArchive = CreateArchive(
            "release/PostNamazu.dll",
            CreateChangedAssembly(post.AssemblyPath));
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var url = request.RequestUri?.AbsoluteUri ?? string.Empty;
        var response = url switch
        {
            var value when value == triggerDownloadUrl =>
                Bytes(triggerAssembly),
            TriggerTranslationUrl =>
                Bytes("<translations />"u8.ToArray()),
            FoxApiUrl =>
                Json(new
                {
                    assets = new[]
                    {
                        new
                        {
                            name = "ACT.FoxTTS-Test-Release.7z",
                            browser_download_url = FoxDownloadUrl,
                        },
                    },
                }),
            PostApiUrl =>
                Json(new
                {
                    assets = new[]
                    {
                        new
                        {
                            name = "PostNamazu.zip",
                            browser_download_url = PostDownloadUrl,
                        },
                    },
                }),
            FoxDownloadUrl =>
                Bytes(foxArchive),
            PostDownloadUrl =>
                Bytes(postArchive),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
        return Task.FromResult(response);
    }

    private static HttpResponseMessage Json<T>(T value)
        => Bytes(JsonSerializer.SerializeToUtf8Bytes(value));

    private static HttpResponseMessage Bytes(byte[] value)
        => new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(value),
        };

    private static byte[] CreateChangedAssembly(string path)
    {
        var original = File.ReadAllBytes(path);
        Array.Resize(ref original, original.Length + 1);
        original[^1] = 0x5a;
        return original;
    }

    private static byte[] CreateArchive(string path, byte[] content)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(
                   output,
                   ZipArchiveMode.Create,
                   leaveOpen: true))
        {
            var entry = archive.CreateEntry(path);
            using var stream = entry.Open();
            stream.Write(content);
        }

        return output.ToArray();
    }
}

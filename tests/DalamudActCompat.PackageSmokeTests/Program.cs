using System.IO.Compression;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using System.Xml;
using Advanced_Combat_Tracker;
using Dalamud.Plugin.Services;
using DalamudActCompat.ActRuntime;
using DalamudActCompat.Compatibility.PluginHost;
using DalamudActCompat.Compatibility.Cactbot;
using DalamudActCompat.Core.Models;
using DalamudActCompat.Core.Interfaces;
using DalamudActCompat.Core.State;
using DalamudActCompat.Encounters;
using DalamudActCompat.Fflogs;
using DalamudActCompat.Infrastructure.Logging;
using DalamudActCompat.Infrastructure.Storage;
using DalamudActCompat.Infrastructure.Ipc;
using DalamudActCompat.Infrastructure.Processes;
using DalamudActCompat.Meter;
using DalamudActCompat.Overlay;
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
    ValidateCombatEventScoping();
    ValidateEncounterParticipantsSurvivePartyDeparture();
    ValidateDalamudGameStateBridge();
    ValidateCactbotSpokenAlertDefaults();
    ValidateOverlayInitialStateEvents();
    ValidateHtmlOverlayDefaults();
    if (string.Equals(
            Environment.GetEnvironmentVariable("ACTCOMPAT_WEBVIEW_INPUT_SMOKE"),
            "1",
            StringComparison.Ordinal))
    {
        await ValidateLiveHtmlOverlayInputAsync(testRoot);
    }
    ValidateParserDependencyVersions();
    ValidatePluginRepositoryMetadata();
    ValidateChinese755hOpcodes();
    ValidateMeterRows();
    ValidateMeterLayout();
    ValidatePictoActOverlayCommands();
    ValidateDutyEncounterAggregation();
    ValidateDutyEncounterPartySizes();
    ValidateControlCenterPresentation();
    ValidateFflogsEstimateCurve();
    ValidateFflogsCurrentEncounterTable();
    ValidateFflogsConcurrencyBoundaries();
    await ValidateFflogsCacheWritersAsync(testRoot);
    await ValidateBundledPluginInstallLifecycleAsync();
    await ValidateEncounterShutdownFlushAsync(testRoot);
    await ValidateAtomicEncounterStateUpdatesAsync();
    await ValidateFactoryResetRollbackAsync(testRoot);

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
    await Task.WhenAll(
        cactbotInstaller.InstallAsync(cactbotPackage, CancellationToken.None),
        cactbotInstaller.InstallAsync(cactbotPackage, CancellationToken.None));
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
    cactbotInstaller.DeleteCommittedBackupAsync = _ =>
        Task.FromException(new IOException("simulated backup cleanup failure"));
    await cactbotInstaller.InstallAsync(cactbotPackage, CancellationToken.None);
    Assert(
        cactbotInstaller.IsInstalled &&
        Directory.EnumerateDirectories(
                paths.ConfigDirectory,
                $"{Path.GetFileName(paths.CactbotDirectory)}.backup-*")
            .Any(),
        "A committed Cactbot install was reported as failed when only backup cleanup was blocked.");
    cactbotInstaller.DeleteCommittedBackupAsync = backup =>
        Task.Run(() => Directory.Delete(backup, recursive: true));
    await cactbotInstaller.InstallAsync(cactbotPackage, CancellationToken.None);
    Assert(
        !Directory.EnumerateDirectories(
                paths.ConfigDirectory,
                $"{Path.GetFileName(paths.CactbotDirectory)}.backup-*")
            .Any(),
        "A stale Cactbot backup was not cleaned on the next successful install.");
    await ValidateCactbotMissingTargetBackupRecoveryAsync(testRoot, cactbotPackage);
    await ValidateCactbotPostShutdownPublicationGuardAsync();
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

static void ValidatePictoActOverlayCommands()
{
    var beforeParse = DateTimeOffset.UtcNow;
    var commands = PictoActOverlayService.Parse(
        "Omen: Circle\nTag: ACTCOMPAT_SMOKE\nt: 5\nPos: <1.25, 2.5, -3.75>\n" +
        "Scale: 5, 5, 1\nColor: 0.2, 1, 0.3, 0.65\n---\n" +
        "Omen: Rect\nDelay: 2.5\nt: 5\nPos: 1, 2, 3\n" +
        "Angle: 0 + pi/2\nScale: 2.5, 28, 1\n---\n" +
        "Omen: Fan90\nt: 5\nPos: 1, 2, 3\nAngle: 90\u00b0\nScale: 12\n---\n" +
        "Action: Remove\nRegex: ^Auto$\n---\n" +
        "Action: Remove");
    Assert(
        commands.Count == 5 &&
        commands[0] is
        {
            Tag: "ACTCOMPAT_SMOKE",
            Remove: false,
            Shape:
            {
                Kind: PictoActShapeKind.Circle,
                PrimaryScale: 5,
                Position.X: 1.25f,
                Position.Y: -3.75f,
                Position.Z: 2.5f,
            },
        } &&
        commands[1] is
        {
            Tag: null,
            Remove: false,
            Shape:
            {
                Kind: PictoActShapeKind.Rectangle,
                PrimaryScale: 2.5f,
                SecondaryScale: 28,
                Color: { X: 1, Y: 1, Z: 1, W: 1 },
            },
        } &&
        commands[1].Shape!.StartsAt >= beforeParse.AddSeconds(2.4) &&
        MathF.Abs(commands[1].Shape!.Angle - MathF.PI / 2) < 0.0001f &&
        commands[2] is
        {
            Tag: null,
            Remove: false,
            Shape:
            {
                Kind: PictoActShapeKind.Fan,
                PrimaryScale: 12,
            },
        } &&
        MathF.Abs(commands[2].Shape!.FanRadians - MathF.PI / 2) < 0.0001f &&
        MathF.Abs(commands[2].Shape!.Angle - MathF.PI / 2) < 0.0001f &&
        commands[3] is { Tag: null, Regex: not null, Remove: true, Shape: null } &&
        commands[4] is { Tag: null, Regex: null, Remove: true, Shape: null },
        "Game-side PictoACT base-shape, Auto-tag, delay, or remove parsing failed.");

    var overlay = new PictoActOverlayService(null!);
    overlay.Apply(
        "Omen: Circle\nt: 5\nPos: 1, 2, 3\nScale: 3\n---\n" +
        "Omen: Circle\nt: 5\nPos: 1, 2, 3\nScale: 5");
    Assert(
        overlay.ShapeCount == 2,
        "PictoACT Auto-tag creates unexpectedly replaced one another.");
    overlay.Apply(
        "Omen: Circle\nTag: ACTCOMPAT_REPLACE\nt: 5\nPos: 1, 2, 3\nScale: 3");
    overlay.Apply(
        "Omen: Circle\nTag: ACTCOMPAT_REPLACE\nt: 5\nPos: 1, 2, 3\nScale: 5");
    Assert(
        overlay.ShapeCount == 3,
        "PictoACT explicit-tag replacement no longer preserves one shape per tag.");
    overlay.Apply("Action: Remove\nRegex: ^Auto$");
    Assert(
        overlay.ShapeCount == 1,
        "PictoACT Regex removal did not remove all matching Auto-tag shapes.");
    overlay.Apply("Action: Remove");
    Assert(
        overlay.ShapeCount == 0,
        "PictoACT unfiltered removal did not clear every shape.");
}

static void ValidatePluginRepositoryMetadata()
{
    var projectRoot = FindProjectRoot();
    using var document = JsonDocument.Parse(File.ReadAllText(
        Path.Combine(projectRoot, "repo", "pluginmaster.json")));
    var entry = document.RootElement.EnumerateArray().Single();
    var iconUrl = entry.GetProperty("IconUrl").GetString();
    Assert(
        Uri.TryCreate(iconUrl, UriKind.Absolute, out var iconUri) &&
        iconUri.Scheme == Uri.UriSchemeHttps &&
        iconUri.Host == "raw.githubusercontent.com" &&
        iconUri.AbsolutePath.EndsWith(
            "/src/DalamudActCompat/Assets/act-logo.jpg",
            StringComparison.Ordinal),
        "Dalamud custom repository does not expose the ACT logo through a public HTTPS IconUrl.");
    Assert(
        File.Exists(Path.Combine(
            projectRoot,
            "src",
            "DalamudActCompat",
            "Assets",
            "act-logo.jpg")),
        "The public custom-repository IconUrl target is missing from the source repository.");
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
    var defaultColor = new MeterSettings().LocalPlayerColor;
    var requestedDefault = new System.Numerics.Vector4(
        0x8B / 255f,
        0x57 / 255f,
        0x33 / 255f,
        0x73 / 255f);
    Assert(
        System.Numerics.Vector4.DistanceSquared(defaultColor, requestedDefault) < 0.000001f,
        "The Combat Meter local-player highlight is not #8B573373 by default.");

    var legacyConfiguration = new PluginConfiguration
    {
        Version = 1,
        Meter = new MeterSettings
        {
            LocalPlayerColor = new System.Numerics.Vector4(0.25f, 0.42f, 0.55f, 0.45f),
        },
    };
    Assert(
        legacyConfiguration.ApplyMigrations() &&
        legacyConfiguration.Version == 3 &&
        legacyConfiguration.EnableParsing &&
        legacyConfiguration.AutoStartParser &&
        System.Numerics.Vector4.DistanceSquared(
            legacyConfiguration.Meter.LocalPlayerColor,
            requestedDefault) < 0.000001f,
        "The previous Combat Meter default highlight or parser startup was not migrated.");
    var customColor = new System.Numerics.Vector4(0.1f, 0.2f, 0.3f, 0.4f);
    var customizedConfiguration = new PluginConfiguration
    {
        Version = 1,
        Meter = new MeterSettings { LocalPlayerColor = customColor },
    };
    customizedConfiguration.ApplyMigrations();
    Assert(
        customizedConfiguration.Meter.LocalPlayerColor == customColor,
        "The default-color migration overwrote a user-customized highlight.");
    var parserMigration = new PluginConfiguration
    {
        Version = 2,
        EnableParsing = false,
        AutoStartParser = false,
        BundledPluginDisclosureKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["act.foxtts"] = "already-acknowledged",
        },
    };
    Assert(
        parserMigration.ApplyMigrations() &&
        parserMigration.Version == 3 &&
        parserMigration.EnableParsing &&
        parserMigration.AutoStartParser,
        "An existing user with prior third-party acknowledgements did not receive the one-time parser startup migration.");
    parserMigration.EnableParsing = false;
    parserMigration.AutoStartParser = false;
    Assert(
        !parserMigration.ApplyMigrations() &&
        !parserMigration.EnableParsing &&
        !parserMigration.AutoStartParser,
        "A post-migration manual parser preference was overwritten.");
    var newConfiguration = new PluginConfiguration();
    Assert(
        newConfiguration.Version == 3 &&
        newConfiguration.EnableParsing &&
        newConfiguration.AutoStartParser,
        "A new installation does not start the parser independently of third-party confirmation.");

    var start = DateTimeOffset.UtcNow.AddSeconds(-10);
    var encounter = new Encounter(
        Guid.NewGuid(),
        start,
        start.AddSeconds(10),
        "Test Zone",
        "Test Enemy",
        [
            new Combatant(
                "tank", "Tank@Alpha", "PLD", true, 100_000, 10_000, 1,
                11_000, 10_000, 10_000,
                DamageHits: 20, CriticalHits: 5, CriticalDirectHits: 2),
            new Combatant("healer", "Healer@Beta", "WHM", false, 20_000, 200_000, 3, 2_500, 2_000, 2_000),
            new Combatant("Limit Break", "Limit Break", "", false, 50_000, 0, 0, 5_000, 5_000, 5_000),
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
    Assert(
        rows.Count == 3 &&
        rows.Any(static row => row.Name == "Limit Break" && row.TotalDamage == 50_000),
        "Limit Break damage is missing from the Meter rows.");
    Assert(
        MeterWindow.LimitBreakDisplayName == "LB (Limit Break)",
        "The Limit Break row does not show the requested player-column label.");
    Assert(
        encounter.Combatants.Any(static combatant => combatant.Name == "Limit Break"),
        "Hiding Limit Break from the Meter mutated the underlying encounter data.");
    Assert(rows[0].Name == "Tank@Alpha", "DPS sorting did not preserve the player server name.");
    Assert(rows[0].Job == "PLD", "The resolved player job was not preserved.");
    Assert(rows[0].Deaths == 1, "The resolved player death count was not preserved.");
    Assert(rows[0].Dps == 10_000, "EncDPS did not use the ACT encounter-duration field.");
    Assert(
        rows[0].CriticalHitPercent == 25 && rows[0].CriticalDirectHitPercent == 10,
        "The ACT Meter did not calculate critical and critical-direct rates from hit counts.");
    Assert(
        Math.Abs(rows.Sum(row => row.DamagePercent) - 100) < 0.01,
        "Meter damage percentages did not cover the encounter total.");

    settings.SortMode = MeterSortMode.Hps;
    Thread.Sleep(settings.RefreshIntervalMs + 20);
    rows = meter.GetRows();
    Assert(rows[0].Name == "Healer@Beta", "HPS sorting did not promote the highest-healing player.");
    Assert(rows[0].Hps == 20_000, "HPS did not use encounter duration.");

    settings.SortMode = MeterSortMode.Deaths;
    Thread.Sleep(settings.RefreshIntervalMs + 20);
    rows = meter.GetRows();
    Assert(
        rows[0].Name == "Tank@Alpha",
        "Legacy death sorting did not safely fall back to DPS.");
    Assert(
        MeterSortModeOptions.Supported.SequenceEqual(
            [MeterSortMode.Dps, MeterSortMode.Hps]),
        "Meter exposed a sort mode other than DPS or HPS.");
    Assert(
        JobDisplayFormatter.FormatText("WHM", JobDisplayStyle.Abbreviation) == "WHM" &&
        JobDisplayFormatter.FormatText("WHM", JobDisplayStyle.ChineseAbbreviation) == "白魔" &&
        JobDisplayFormatter.SupportedJobCodes.Count == 32 &&
        JobDisplayFormatter.SupportedJobCodes.Distinct(StringComparer.Ordinal).Count() == 32 &&
        JobDisplayFormatter.UsesIcon(JobDisplayStyle.MinimalIcon) &&
        JobDisplayFormatter.UsesIcon(JobDisplayStyle.ClassicIcon) &&
        JobDisplayFormatter.UsesIcon(JobDisplayStyle.FlatIcon),
        "The five job display styles were not preserved.");

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

    var emptyCompletion = encounter with
    {
        EndTime = encounter.EndTime?.AddSeconds(1),
        Combatants = [],
    };
    state.UpdateCurrent(emptyCompletion);
    Assert(
        state.GetDisplayEncounter()?.Id == encounter.Id &&
        state.GetDisplayEncounter()?.TotalDamage == encounter.TotalDamage,
        "An empty combat completion replaced the latest displayable Meter encounter.");
    state.UpdateCurrent(null);
    Assert(
        state.GetDisplayEncounter()?.TotalDamage == encounter.TotalDamage,
        "Clearing a transient current snapshot discarded the latest displayable Meter encounter.");

    var activeEncounter = encounter with
    {
        Id = Guid.NewGuid(),
        EndTime = null,
    };
    state.UpdateCurrent(activeEncounter);
    state.UpdateCurrent(null);
    Assert(
        state.GetDisplayEncounter() is { IsActive: false } retainedEncounter &&
        retainedEncounter.Id == activeEncounter.Id &&
        retainedEncounter.TotalDamage == activeEncounter.TotalDamage,
        "A cleared live snapshot did not retain a completed Meter encounter.");
    state.ResetCurrent();
    Assert(
        state.GetDisplayEncounter() is null,
        "An explicit Meter reset did not clear the retained encounter.");
}

static void ValidateMeterLayout()
{
    Assert(
        MeterWindow.CombatantRowSpacing == 3,
        "Combat Meter player rows are not separated by the requested three pixels.");
    Assert(
        File.Exists(Path.Combine(
            FindProjectRoot(),
            "src",
            "DalamudActCompat",
            "Assets",
            "JobIcons",
            "LimitBreak.png")),
        "The supplied Limit Break icon is missing from plugin assets.");

    var rowHeightMethod = typeof(MeterWindow).GetMethod(
                              "CalculateCombatantRowHeight",
                              BindingFlags.Static | BindingFlags.NonPublic)
                          ?? throw new InvalidOperationException(
                              "Meter row-height helper was not found.");
    var rowHeight = (float)(rowHeightMethod.Invoke(null, [19.0f])
                            ?? throw new InvalidOperationException(
                                "Combat Meter returned no row height."));
    Assert(
        rowHeight <= 30,
        "Combat Meter did not keep each player on one compact line.");

    var iconSizeMethod = typeof(MeterWindow).GetMethod(
                             "CalculateJobIconSize",
                             BindingFlags.Static | BindingFlags.NonPublic)
                         ?? throw new InvalidOperationException(
                             "Meter job-icon sizing helper was not found.");
    var iconSize = (float)(iconSizeMethod.Invoke(null, [rowHeight, 17.0f])
                           ?? throw new InvalidOperationException(
                               "Meter returned no job-icon size."));
    Assert(
        iconSize is >= 20 and <= 24 && iconSize <= rowHeight - 4,
        "Job icons were not normalized to the one-line Meter slot.");

    var rateLabelMethod = typeof(MeterWindow).GetMethod(
                              "PrimaryRateLabel",
                              BindingFlags.Static | BindingFlags.NonPublic)
                          ?? throw new InvalidOperationException(
                              "Meter primary-rate label helper was not found.");
    var settings = new MeterSettings { DpsMetric = DpsMetric.EncDps };
    Assert(
        Equals(rateLabelMethod.Invoke(null, [MeterSortMode.Dps, settings]), "eDPS"),
        "The ACT Meter still labels encounter DPS as EncDPS instead of eDPS.");

    var rateColorMethod = typeof(MeterWindow).GetMethod(
                              "PrimaryRateColor",
                              BindingFlags.Static | BindingFlags.NonPublic)
                          ?? throw new InvalidOperationException(
                              "Meter primary-rate color helper was not found.");
    var ownRateColor = (System.Numerics.Vector4)rateColorMethod.Invoke(null, [true])!;
    var otherRateColor = (System.Numerics.Vector4)rateColorMethod.Invoke(null, [false])!;
    Assert(
        ownRateColor.X + ownRateColor.Y + ownRateColor.Z >
        otherRateColor.X + otherRateColor.Y + otherRateColor.Z,
        "The local player's DPS/HPS value is not brighter than party values.");

    var hitRateTextMethod = typeof(MeterWindow).GetMethod(
                                "FormatHitRateValue",
                                BindingFlags.Static | BindingFlags.NonPublic)
                            ?? throw new InvalidOperationException(
                                "Meter hit-rate formatter was not found.");
    Assert(
        Equals(hitRateTextMethod.Invoke(null, [25.0]), "25.0%") &&
        Equals(hitRateTextMethod.Invoke(null, [null]), "--"),
        "The Meter does not keep critical-rate values compact below their column headers.");

    var localizerType = typeof(MeterWindow).Assembly.GetType(
                            "DalamudActCompat.Meter.ZoneNameLocalizer")
                        ?? throw new InvalidOperationException(
                            "Meter zone-name localizer was not found.");
    var resolveZoneName = localizerType.GetMethod(
                              "Resolve",
                              BindingFlags.Static | BindingFlags.NonPublic)
                          ?? throw new InvalidOperationException(
                              "Meter zone-name resolver was not found.");
    var zoneNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Middle La Noscea"] = "中拉诺西亚",
    };
    Assert(
        Equals(resolveZoneName.Invoke(null, ["Middle La Noscea", zoneNames]), "中拉诺西亚") &&
        Equals(resolveZoneName.Invoke(null, ["Unknown Zone", zoneNames]), "Unknown Zone"),
        "Meter zone names no longer localize with a safe ACT-name fallback.");
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
    var roundedOrange = FflogsEstimateService.ColorForPercentile(94.6);
    Assert(
        legendary != pink && pink != orange && roundedOrange == orange,
        "FFLogs estimate colors did not match the rounded score shown by the Meter.");
}

static void ValidateFflogsCurrentEncounterTable()
{
    var tableType = typeof(FflogsEstimateService).Assembly.GetType(
                        "DalamudActCompat.Fflogs.CurrentFflogsEncounterTable")
                    ?? throw new InvalidOperationException(
                        "The current FFLogs encounter table was not found.");
    var tryResolve = tableType.GetMethod(
                         "TryResolve",
                         BindingFlags.Static | BindingFlags.NonPublic)
                     ?? throw new InvalidOperationException(
                         "The current FFLogs duty resolver was not found.");
    var observePhase = tableType.GetMethod(
                           "ObservePhase",
                           BindingFlags.Static | BindingFlags.NonPublic)
                       ?? throw new InvalidOperationException(
                           "The FFLogs phase observer was not found.");

    object?[] normalM4Arguments = [1326u, 1, null];
    Assert(
        Equals(tryResolve.Invoke(null, normalM4Arguments), true) &&
        ReadEncounterValue(normalM4Arguments[2], "EncounterId") == 104 &&
        ReadEncounterValue(normalM4Arguments[2], "Difficulty") == 100,
        "AAC Heavyweight M4 did not resolve to the current Lindwurm Normal ranking.");

    object?[] oldDutyArguments = [1269u, 1, null];
    Assert(
        Equals(tryResolve.Invoke(null, oldDutyArguments), false),
        "A duty outside the current FFLogs tier was allowed to resolve.");

    var phaseTwo = (int)(observePhase.Invoke(
        null,
        [1327u, 1, "21|2026-08-08T00:00:00.0000000+08:00|40000001|Lindwurm|BBD8|Mindless Flesh"])
        ?? 0);
    object?[] savageM4Arguments = [1327u, phaseTwo, null];
    Assert(
        phaseTwo == 2 &&
        Equals(tryResolve.Invoke(null, savageM4Arguments), true) &&
        ReadEncounterValue(savageM4Arguments[2], "EncounterId") == 105 &&
        ReadEncounterValue(savageM4Arguments[2], "Difficulty") == 101,
        "M4S did not switch to the Lindwurm II Savage ranking after a phase-two action.");

    static int ReadEncounterValue(object? encounter, string propertyName)
        => (int)(encounter?.GetType().GetProperty(propertyName)?.GetValue(encounter)
                 ?? throw new InvalidOperationException($"Resolved FFLogs encounter has no {propertyName}."));
}

static void ValidateEncounterParticipantsSurvivePartyDeparture()
{
    ActGlobals.Init();
    var encounter = new EncounterData("Player 1", "Test Duty", false, null!);
    var allies = Enumerable.Range(1, 8)
        .Select(index => new CombatantData($"Player {index}", encounter))
        .ToList();
    encounter.SetAllies(allies);

    var cachedIdentities = Enumerable.Range(1, 8)
        .Select(index => new ActPlayerIdentity(
            $"Player {index}",
            "Test World",
            index <= 2 ? "WAR" : "DPS",
            index == 1,
            false)
        {
            ContentId = (ulong)index,
        })
        .ToArray();
    var liveIdentities = cachedIdentities.Take(5).ToArray();

    var resolved = SelfHostedActRuntime.ResolveEncounterCombatants(
        encounter,
        liveIdentities,
        cachedIdentities);

    Assert(
        resolved.Count == 8,
        "A completed eight-player encounter lost combatants who left the live party before publication.");
    Assert(
        resolved.All(static item => item.Identity is not null),
        "An encounter-scoped player identity was not retained after a party departure.");
    Assert(
        resolved.Skip(5).Select(static item => item.Identity!.Name)
            .SequenceEqual(["Player 6", "Player 7", "Player 8"]),
        "The departed players were not restored from the encounter identity cache.");

    var withoutMetadata = SelfHostedActRuntime.ResolveEncounterCombatants(
        encounter,
        liveIdentities,
        []);
    Assert(
        withoutMetadata.Count == 8 && withoutMetadata.Skip(5).All(static item => item.Identity is null),
        "ACT allies without live identity metadata were silently removed from the encounter.");
}

static void ValidateDutyEncounterAggregation()
{
    var start = new DateTimeOffset(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);
    var accumulator = new DutyEncounterAccumulator();
    var first = CreateDutySegment(
        Guid.NewGuid(),
        start,
        start.AddMinutes(2),
        "测试副本",
        "Boss A",
        damage: 100,
        healing: 20,
        deaths: 1,
        damageHits: 10,
        criticalHits: 3,
        criticalDirectHits: 1);
    var afterFirst = accumulator.Update(first, finished: true, start.AddMinutes(2));
    Assert(
        afterFirst.IsActive && afterFirst.TotalDamage == 100,
        "A completed boss incorrectly ended or reset the active duty session.");
    Assert(
        afterFirst.FflogsRankingEncounter?.EnemyName == "Boss A" &&
        afterFirst.FflogsRankingEncounter.TotalDamage == 100,
        "Duty aggregation did not retain the concrete boss segment for FFLogs estimation.");
    var duplicateFirst = accumulator.Update(first, finished: true, start.AddMinutes(3));
    Assert(
        duplicateFirst.TotalDamage == 100,
        "A repeated completed boss snapshot was counted twice in the duty session.");

    var secondId = Guid.NewGuid();
    var secondActive = CreateDutySegment(
        secondId,
        start.AddMinutes(4),
        endTime: null,
        "测试副本",
        "Boss B",
        damage: 50,
        healing: 10,
        deaths: 0,
        damageHits: 5,
        criticalHits: 1,
        criticalDirectHits: 1);
    var combinedActive = accumulator.Update(secondActive, finished: false, start.AddMinutes(5));
    Assert(
        combinedActive.Id == afterFirst.Id &&
        combinedActive.TotalDamage == 150 &&
        combinedActive.EnemyName == "测试副本",
        "The next boss did not continue the same duty-wide ACT encounter.");
    Assert(
        combinedActive.FflogsRankingEncounter?.Id == secondId &&
        combinedActive.FflogsRankingEncounter.TotalDamage == 50,
        "FFLogs estimation received cumulative duty totals instead of the active boss segment.");
    Assert(
        !JsonSerializer.Serialize(combinedActive).Contains(
            nameof(Encounter.FflogsRankingEncounter),
            StringComparison.Ordinal),
        "The transient FFLogs ranking segment leaked into persisted encounter JSON.");

    var secondFinished = secondActive with
    {
        EndTime = start.AddMinutes(6),
        Combatants =
        [
            secondActive.Combatants[0] with
            {
                TotalDamage = 80,
                TotalHealing = 15,
                DamageHits = 8,
                CriticalHits = 2,
                CriticalDirectHits = 1,
            },
        ],
    };
    _ = accumulator.Update(secondFinished, finished: true, start.AddMinutes(6));
    var completed = accumulator.Complete(start.AddMinutes(7))
                    ?? throw new InvalidOperationException(
                        "Leaving the duty produced no completed ACT encounter.");
    Assert(
        !completed.IsActive &&
        completed.TotalDamage == 180 &&
        completed.TotalHealing == 35 &&
        completed.TotalDeaths == 1 &&
        completed.Combatants[0].DamageHits == 18 &&
        completed.Combatants[0].CriticalHits == 5 &&
        completed.Combatants[0].CriticalDirectHits == 2,
        "Leaving the duty did not finalize the accumulated boss totals exactly once.");
    Assert(
        completed.Duration == TimeSpan.FromMinutes(7) &&
        completed.EffectiveDuration == TimeSpan.FromMinutes(4) &&
        Math.Abs(completed.Combatants[0].EncDps - 0.75) < 0.0001,
        "Duty DPS included travel or waiting time between completed combat segments.");
    Assert(
        completed.FflogsRankingEncounter?.Id == secondId &&
        completed.FflogsRankingEncounter.TotalDamage == 80,
        "The completed duty did not retain its latest boss segment for FFLogs estimation.");

    var serialized = JsonSerializer.Serialize(completed);
    var restored = JsonSerializer.Deserialize<Encounter>(serialized)
                   ?? throw new InvalidOperationException(
                       "The accumulated duty encounter could not be restored from JSON.");
    Assert(
        restored.CombatDuration == TimeSpan.FromMinutes(4) &&
        restored.EffectiveDuration == TimeSpan.FromMinutes(4),
        "The accumulated active-combat duration was not preserved in encounter history.");
}

static void ValidateDutyEncounterPartySizes()
{
    foreach (var partySize in new[] { 4, 8, 24 })
    {
        var start = new DateTimeOffset(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);
        var first = CreatePartySegment(
            Guid.NewGuid(),
            start,
            start.AddSeconds(30),
            partySize,
            damage: 3_000,
            dps: 150,
            encDps: 100,
            extDps: 75);
        var second = CreatePartySegment(
            Guid.NewGuid(),
            start.AddSeconds(90),
            start.AddSeconds(110),
            partySize,
            damage: 2_000,
            dps: 200,
            encDps: 100,
            extDps: 50);
        var accumulator = new DutyEncounterAccumulator();
        _ = accumulator.Update(first, finished: true, first.EndTime!.Value);
        _ = accumulator.Update(second, finished: true, second.EndTime!.Value);
        var completed = accumulator.Complete(start.AddSeconds(120))
                        ?? throw new InvalidOperationException(
                            $"The {partySize}-player duty produced no accumulated encounter.");

        Assert(
            completed.Combatants.Count == partySize &&
            completed.EffectiveDuration == TimeSpan.FromSeconds(50) &&
            completed.Duration == TimeSpan.FromSeconds(120),
            $"The {partySize}-player duty did not preserve its roster or active-combat duration.");
        Assert(
            completed.Combatants.All(combatant =>
                Math.Abs(combatant.Dps - (5_000d / 30)) < 0.0001 &&
                Math.Abs(combatant.EncDps - 100) < 0.0001 &&
                Math.Abs(combatant.ExtDps - 62.5) < 0.0001),
            $"The {partySize}-player duty did not retain distinct DPS, EncDPS, and ExtDPS durations.");
    }
}

static Encounter CreatePartySegment(
    Guid id,
    DateTimeOffset start,
    DateTimeOffset end,
    int partySize,
    long damage,
    double dps,
    double encDps,
    double extDps)
    => new(
        id,
        start,
        end,
        "测试副本",
        "测试首领",
        Enumerable.Range(1, partySize)
            .Select(index => new Combatant(
                $"player-{index}",
                $"Player {index}",
                index <= 2 ? "PLD" : "DPS",
                index == 1,
                damage,
                0,
                0,
                dps,
                encDps,
                extDps))
            .ToArray(),
        [],
        [],
        [],
        [],
        []);

static Encounter CreateDutySegment(
    Guid id,
    DateTimeOffset start,
    DateTimeOffset? endTime,
    string zone,
    string enemy,
    long damage,
    long healing,
    int deaths,
    int damageHits = 0,
    int criticalHits = 0,
    int criticalDirectHits = 0)
    => new(
        id,
        start,
        endTime,
        zone,
        enemy,
        [new Combatant(
            "local", "Player", "PLD", true, damage, healing, deaths,
            DamageHits: damageHits,
            CriticalHits: criticalHits,
            CriticalDirectHits: criticalDirectHits)],
        Array.Empty<DamageEvent>(),
        Array.Empty<HealEvent>(),
        Array.Empty<DeathEvent>(),
        Array.Empty<ActionSummary>(),
        Array.Empty<JobSummary>());

static void ValidateControlCenterPresentation()
{
    Assert(
        ControlCenterWindow.EaseInOut(0) == 0 &&
        Math.Abs(ControlCenterWindow.EaseInOut(0.5f) - 0.5f) < 0.001f &&
        ControlCenterWindow.EaseInOut(1) == 1,
        "The ACT control center visibility transition is not a bounded ease-in-out curve.");
    Assert(
        ControlCenterWindow.FormatVersionLabel(new Version(0, 3, 7, 0)) == "v0.3.7.0",
        "The ACT control center no longer displays the full four-part assembly version.");
    Assert(
        !ControlCenterWindow.IsResetConfirmationExpired(11_000, 10_999) &&
        ControlCenterWindow.IsResetConfirmationExpired(11_000, 11_000),
        "The two-step encounter reset confirmation does not expire after ten seconds.");
    Assert(
        ThirdPartyPluginNoticeWindow.ShouldOpenUpdateResult(1, failed: false, userInitiated: false) &&
        !ThirdPartyPluginNoticeWindow.ShouldOpenUpdateResult(0, failed: false, userInitiated: true) &&
        ThirdPartyPluginNoticeWindow.ShouldOpenUpdateResult(0, failed: true, userInitiated: true) &&
        !ThirdPartyPluginNoticeWindow.ShouldOpenUpdateResult(0, failed: true, userInitiated: false),
        "The DLL update-check window does not stay hidden when a successful check finds no updates.");
    Assert(
        ThirdPartyPluginNoticeWindow.ShouldShowCloseButton(
            ThirdPartyNoticeOpenMode.ManualDisclosure) &&
        ThirdPartyPluginNoticeWindow.ShouldShowCloseButton(
            ThirdPartyNoticeOpenMode.ManualUpdateCheck) &&
        !ThirdPartyPluginNoticeWindow.ShouldShowCloseButton(
            ThirdPartyNoticeOpenMode.RequiredAfterPluginUpdate),
        "The required post-update third-party acknowledgement is not separated from manually opened DLL windows.");
    Assert(
        Plugin.ShouldEnableBundledCapability(
            enableFullFunctionality: false,
            ActCapability.TextToSpeech) &&
        !Plugin.ShouldEnableBundledCapability(
            enableFullFunctionality: false,
            ActCapability.NetworkRequest) &&
        Plugin.ShouldEnableBundledCapability(
            enableFullFunctionality: true,
            ActCapability.NetworkRequest),
        "Declining full permissions does not restore safe defaults after a previous release granted them.");
    Assert(
        Plugin.ShouldOfferFoxTtsPro(suppressPrompt: false, isPro: false) &&
        !Plugin.ShouldOfferFoxTtsPro(suppressPrompt: false, isPro: true) &&
        !Plugin.ShouldOfferFoxTtsPro(suppressPrompt: true, isPro: false),
        "The Cafe TTS Pro prompt ignores the current engine or the user's never-remind choice.");
    var ttsPromptConfiguration = Newtonsoft.Json.JsonConvert.DeserializeObject<PluginConfiguration>(
        Newtonsoft.Json.JsonConvert.SerializeObject(new PluginConfiguration
        {
            SuppressFoxTtsProPrompt = true,
        })) ?? throw new InvalidOperationException(
            "The FoxTTS prompt configuration did not deserialize.");
    Assert(
        ttsPromptConfiguration.SuppressFoxTtsProPrompt,
        "The FoxTTS never-remind choice is not persisted across plugin updates.");
    ttsPromptConfiguration.ResetToDefaults(Path.GetTempPath());
    Assert(
        !ttsPromptConfiguration.SuppressFoxTtsProPrompt &&
        ttsPromptConfiguration.EnableParsing &&
        ttsPromptConfiguration.AutoStartParser,
        "Factory reset does not restore the FoxTTS prompt or independent parser startup defaults.");

    var combatant = new Combatant(
        "local",
        "Player",
        "WHM",
        true,
        120_000,
        60_000,
        1,
        Dps: 2_000,
        EncDps: 1_500,
        ExtDps: 1_250);
    Assert(
        EncounterWindow.ResolveRate(combatant, 60, MeterSortMode.Dps, DpsMetric.EncDps) == 1_500 &&
        EncounterWindow.ResolveRate(combatant, 60, MeterSortMode.Dps, DpsMetric.ExtDps) == 1_250 &&
        EncounterWindow.ResolveRate(combatant, 60, MeterSortMode.Hps, DpsMetric.EncDps) == 1_000,
        "Combat History no longer follows the Combat Meter DPS metric and DPS/HPS sort mode.");

    var projectRoot = FindProjectRoot();
    var controlCenterSource = File.ReadAllText(Path.Combine(
        projectRoot, "src", "DalamudActCompat", "UI", "ControlCenterWindow.cs"));
    var historySource = File.ReadAllText(Path.Combine(
        projectRoot, "src", "DalamudActCompat", "Encounters", "EncounterWindow.cs"));
    var thirdPartySource = File.ReadAllText(Path.Combine(
        projectRoot, "src", "DalamudActCompat", "UI", "ThirdPartyPluginNoticeWindow.cs"));
    var statusSource = File.ReadAllText(Path.Combine(
        projectRoot, "src", "DalamudActCompat", "UI", "StatusWindow.cs"));
    var settingsSource = File.ReadAllText(Path.Combine(
        projectRoot, "src", "DalamudActCompat", "UI", "SettingsWindow.cs"));
    var pluginSource = File.ReadAllText(Path.Combine(
        projectRoot, "src", "DalamudActCompat", "Plugin", "Plugin.cs"));
    var configurationSource = File.ReadAllText(Path.Combine(
        projectRoot, "src", "DalamudActCompat", "Plugin", "PluginConfiguration.cs"));
    var hostSupervisorSource = File.ReadAllText(Path.Combine(
        projectRoot,
        "src",
        "DalamudActCompat",
        "Infrastructure",
        "Processes",
        "ActHostSupervisor.cs"));
    Assert(
        controlCenterSource.Contains("text.Get(\"主页\", \"Home\")", StringComparison.Ordinal) &&
        controlCenterSource.Contains("(Page.Diagnostics, text.Get(\"设置\", \"Settings\"))", StringComparison.Ordinal) &&
        controlCenterSource.Contains("恢复出厂设置...", StringComparison.Ordinal) &&
        typeof(ControlCenterWindow).GetConstructors().Single().GetParameters().Any(parameter =>
            parameter.Name == "factoryReset" && parameter.ParameterType == typeof(Func<Task<string>>)),
        "The home/settings labels or guarded factory-reset action are missing from the new settings UI.");
    var navigationIndex = controlCenterSource.IndexOf(
        "DrawPageTabs();",
        StringComparison.Ordinal);
    var scrollContentIndex = controlCenterSource.IndexOf(
        "control-center-page-content",
        StringComparison.Ordinal);
    Assert(
        navigationIndex >= 0 &&
        scrollContentIndex > navigationIndex &&
        controlCenterSource.Contains("overview-parser-card", StringComparison.Ordinal) &&
        controlCenterSource.Contains("overview-quick-actions-card", StringComparison.Ordinal) &&
        controlCenterSource.Contains("overview-general-card", StringComparison.Ordinal) &&
        controlCenterSource.Contains("关闭运行状态", StringComparison.Ordinal),
        "The fixed navigation, gold overview cards, or runtime-status toggle is missing.");
    Assert(
        historySource.Contains("BrandedWindowChrome.Draw", StringComparison.Ordinal) &&
        historySource.Contains("combat-history-navigation", StringComparison.Ordinal) &&
        historySource.Contains("ImGuiStyleVar.WindowRounding", StringComparison.Ordinal) &&
        historySource.Contains("ImGuiWindowFlags.NoTitleBar", StringComparison.Ordinal),
        "Combat History does not use the rounded branded frame and shared navigation rail.");
    var chromeSource = File.ReadAllText(Path.Combine(
        projectRoot, "src", "DalamudActCompat", "UI", "BrandedWindowChrome.cs"));
    Assert(
        controlCenterSource.Contains("control-center-navigation", StringComparison.Ordinal) &&
        chromeSource.Contains("DrawNavigationRail", StringComparison.Ordinal) &&
        chromeSource.Contains("NavigationIndicatorPositions", StringComparison.Ordinal) &&
        chromeSource.Contains("AdvanceNavigationIndicator", StringComparison.Ordinal),
        "Page-level navigation does not share the animated navigation rail design.");
    var animatedIndicator = BrandedWindowChrome.AdvanceNavigationIndicator(0, 1, 1f / 60f);
    Assert(
        animatedIndicator > 0 && animatedIndicator < 1 &&
        BrandedWindowChrome.AdvanceNavigationIndicator(0.9999f, 1, 1f / 60f) == 1,
        "The navigation indicator no longer eases toward its selected page.");
    var historyNavigationIndex = historySource.IndexOf(
        "combat-history-navigation",
        StringComparison.Ordinal);
    var historyContentIndex = historySource.IndexOf(
        "combat-history-page-content",
        StringComparison.Ordinal);
    Assert(
        historyNavigationIndex >= 0 && historyContentIndex > historyNavigationIndex,
        "Combat History navigation is no longer fixed above its scrolling page content.");
    var permissionChoiceIndex = thirdPartySource.IndexOf(
        "private void DrawPermissionChoiceModal",
        StringComparison.Ordinal);
    var ttsChoiceIndex = thirdPartySource.IndexOf(
        "private void DrawTtsProChoiceModal",
        StringComparison.Ordinal);
    var updateStatusIndex = thirdPartySource.IndexOf(
        "private void DrawUpdateStatusBanner",
        StringComparison.Ordinal);
    Assert(
        permissionChoiceIndex >= 0 &&
        ttsChoiceIndex > permissionChoiceIndex &&
        updateStatusIndex > ttsChoiceIndex,
        "The third-party permission or TTS prompt method could not be located.");
    var permissionChoiceMethod = thirdPartySource[permissionChoiceIndex..ttsChoiceIndex];
    var ttsChoiceMethod = thirdPartySource[ttsChoiceIndex..updateStatusIndex];
    Assert(
        thirdPartySource.Contains("Size = new Vector2(1200, 650);", StringComparison.Ordinal) &&
        thirdPartySource.Contains("\"third-party-plugin-cards\"", StringComparison.Ordinal) &&
        thirdPartySource.Contains("ImGuiTableFlags.SizingStretchSame", StringComparison.Ordinal) &&
        thirdPartySource.Contains("BrandedWindowChrome.Draw", StringComparison.Ordinal) &&
        thirdPartySource.Contains("DrawPermissionChoiceModal", StringComparison.Ordinal) &&
        thirdPartySource.Contains("DrawTtsProChoiceModal", StringComparison.Ordinal) &&
        thirdPartySource.Contains("ImGui.BeginPopupModal", StringComparison.Ordinal) &&
        thirdPartySource.Contains("不同意完整权限，保持安全模式", StringComparison.Ordinal) &&
        thirdPartySource.Contains("是否将 FoxTTS 改为 Cafe TTS Pro？", StringComparison.Ordinal) &&
        thirdPartySource.Contains("本次不更改", StringComparison.Ordinal) &&
        thirdPartySource.Contains("不再提醒", StringComparison.Ordinal) &&
        permissionChoiceMethod.Contains("ImGui.BeginPopupModal", StringComparison.Ordinal) &&
        !permissionChoiceMethod.Contains("ref ", StringComparison.Ordinal) &&
        ttsChoiceMethod.Contains("ref ttsPopupOpen", StringComparison.Ordinal) &&
        thirdPartySource.Contains("showCloseButton: ShouldShowCloseButton(openMode)", StringComparison.Ordinal) &&
        !thirdPartySource.Contains("稍后处理", StringComparison.Ordinal) &&
        thirdPartySource.Contains("third-party-update-status", StringComparison.Ordinal) &&
        pluginSource.Contains("thirdPartyPluginNoticeWindow.OpenManualDisclosure", StringComparison.Ordinal) &&
        pluginSource.Contains("OpenRequiredAfterPluginUpdateWhenPending();", StringComparison.Ordinal) &&
        pluginSource.Contains("BeginUpdateCheck(userInitiated: true)", StringComparison.Ordinal) &&
        pluginSource.Contains("BeginUpdateCheck(userInitiated: false)", StringComparison.Ordinal) &&
        pluginSource.Contains("userInitiated: openWindow);", StringComparison.Ordinal) &&
        pluginSource.Contains("更新检查已经在进行中", StringComparison.Ordinal) &&
        pluginSource.Contains("services.NotificationManager.AddNotification", StringComparison.Ordinal),
        "The update notice is not a landscape branded window with a top modal and visible manual-check feedback.");
    var installBundledPluginsIndex = pluginSource.IndexOf(
        "private async Task InstallBundledPluginsAsync",
        StringComparison.Ordinal);
    var startBundledUpdateCheckIndex = pluginSource.IndexOf(
        "private void StartBundledPluginUpdateCheck",
        installBundledPluginsIndex,
        StringComparison.Ordinal);
    var bundledInstallMethod = pluginSource[
        installBundledPluginsIndex..startBundledUpdateCheckIndex];
    var configurationMigrationIndex = pluginSource.IndexOf(
        "configuration.ApplyMigrations();",
        StringComparison.Ordinal);
    var lifecycleStartIndex = pluginSource.IndexOf(
        "lifecycle.Start();",
        StringComparison.Ordinal);
    Assert(
        configurationSource.Contains("public bool EnableParsing { get; set; } = true;", StringComparison.Ordinal) &&
        configurationSource.Contains("public bool AutoStartParser { get; set; } = true;", StringComparison.Ordinal) &&
        configurationMigrationIndex >= 0 &&
        lifecycleStartIndex > configurationMigrationIndex &&
        !bundledInstallMethod.Contains("configuration.EnableParsing =", StringComparison.Ordinal) &&
        !bundledInstallMethod.Contains("configuration.AutoStartParser =", StringComparison.Ordinal),
        "Parser startup remains coupled to third-party extension acknowledgement.");
    var buildBundledUpdateMessageIndex = pluginSource.IndexOf(
        "private static string BuildBundledPluginUpdateMessage",
        startBundledUpdateCheckIndex,
        StringComparison.Ordinal);
    var bundledUpdateCheckMethod = pluginSource[
        startBundledUpdateCheckIndex..buildBundledUpdateMessageIndex];
    Assert(
        !bundledUpdateCheckMethod.Contains("parserEngine.StopAsync", StringComparison.Ordinal) &&
        !bundledUpdateCheckMethod.Contains("hostSupervisor.StopAsync", StringComparison.Ordinal) &&
        bundledInstallMethod.Contains("BundledPluginInstallCoordinator.ExecuteAsync", StringComparison.Ordinal),
        "Checking for bundled DLL updates still pauses ACT services before installation begins.");
    var pluginConstructorEnd = pluginSource.IndexOf(
        "public string Name",
        StringComparison.Ordinal);
    var pluginConstructor = pluginSource[..pluginConstructorEnd];
    var lifecycleStartForCactbot = pluginConstructor.IndexOf(
        "lifecycle.Start();",
        StringComparison.Ordinal);
    var cactbotInitializationStart = pluginConstructor.IndexOf(
        "StartBundledCactbotInitialization",
        StringComparison.Ordinal);
    Assert(
        lifecycleStartForCactbot >= 0 &&
        cactbotInitializationStart > lifecycleStartForCactbot &&
        !pluginConstructor.Contains("EnsureCurrentAsync(timeout.Token).GetAwaiter()", StringComparison.Ordinal) &&
        pluginSource.Contains("ShutdownCactbotOperationsAsync", StringComparison.Ordinal) &&
        controlCenterSource.Contains("CactbotOperationState.Checking", StringComparison.Ordinal) &&
        controlCenterSource.Contains("CactbotOperationState.Installing", StringComparison.Ordinal),
        "Cactbot validation still blocks plugin startup or lacks tracked status/shutdown handling.");
    Assert(
        statusSource.Contains("BrandedWindowChrome.Draw", StringComparison.Ordinal) &&
        statusSource.Contains("runtime-status-content", StringComparison.Ordinal) &&
        statusSource.Contains("BeginGoldCard", StringComparison.Ordinal) &&
        statusSource.Contains("ImGuiWindowFlags.NoTitleBar", StringComparison.Ordinal),
        "The ACT compatibility status window does not use the new rounded branded card design.");
    var configurePermissionsIndex = pluginSource.IndexOf(
        "private void ConfigureBundledPluginPermissions",
        StringComparison.Ordinal);
    var permissionSaveIndex = pluginSource.IndexOf(
        "SaveConfiguration();",
        configurePermissionsIndex,
        StringComparison.Ordinal);
    var completeBundledSetupIndex = pluginSource.IndexOf(
        "private void CompleteBundledPluginSetup",
        StringComparison.Ordinal);
    var finalPermissionRestartIndex = pluginSource.IndexOf(
        "ApplyActPermissionChanges(choice == FoxTtsProChoice.EnablePro);",
        completeBundledSetupIndex,
        StringComparison.Ordinal);
    var permissionRefreshMethodIndex = pluginSource.IndexOf(
        "private void ApplyActPermissionChanges(bool switchFoxTtsToPro = false)",
        StringComparison.Ordinal);
    var stopHostBeforeFoxTtsIndex = pluginSource.IndexOf(
        "await hostSupervisor.StopAsync(timeout.Token)",
        permissionRefreshMethodIndex,
        StringComparison.Ordinal);
    var setFoxTtsProIndex = pluginSource.IndexOf(
        "FoxTtsConfigurationDefaults.SetPro(paths.ConfigDirectory);",
        permissionRefreshMethodIndex,
        StringComparison.Ordinal);
    var startHostAfterFoxTtsIndex = pluginSource.IndexOf(
        "await hostSupervisor.StartAsync(timeout.Token)",
        setFoxTtsProIndex,
        StringComparison.Ordinal);
    Assert(
        typeof(ActHostSupervisor).GetMethod(nameof(ActHostSupervisor.RestartAsync))?.ReturnType ==
        typeof(Task<bool>) &&
        Regex.Matches(
            controlCenterSource,
            Regex.Escape("applyPermissionChanges();"),
            RegexOptions.CultureInvariant).Count == 1 &&
        Regex.Matches(
            settingsSource,
            Regex.Escape("applyPermissionChanges();"),
            RegexOptions.CultureInvariant).Count == 1 &&
        controlCenterSource.Contains(
            "changed |= permissionsChanged;",
            StringComparison.Ordinal) &&
        settingsSource.Contains(
            "changed |= permissionsChanged;",
            StringComparison.Ordinal) &&
        configurePermissionsIndex >= 0 &&
        permissionSaveIndex > configurePermissionsIndex &&
        completeBundledSetupIndex > permissionSaveIndex &&
        finalPermissionRestartIndex > completeBundledSetupIndex &&
        Regex.Matches(
            pluginSource,
            Regex.Escape("ApplyActPermissionChanges(choice == FoxTtsProChoice.EnablePro);"),
            RegexOptions.CultureInvariant).Count == 1 &&
        permissionRefreshMethodIndex > finalPermissionRestartIndex &&
        stopHostBeforeFoxTtsIndex > permissionRefreshMethodIndex &&
        setFoxTtsProIndex > stopHostBeforeFoxTtsIndex &&
        startHostAfterFoxTtsIndex > setFoxTtsProIndex &&
        hostSupervisorSource.Contains(
            "public async Task<bool> RestartAsync",
            StringComparison.Ordinal),
        "ACT permissions are not saved before the final Host refresh, or FoxTTS Pro is not written between Host stop and start.");

    var configurationPathPattern = new Regex(
        @"configuration(?:\.[A-Za-z_][A-Za-z0-9_]*)+",
        RegexOptions.CultureInvariant);
    var legacyConfigurationPaths = configurationPathPattern
        .Matches(settingsSource)
        .Select(match => match.Value)
        .ToHashSet(StringComparer.Ordinal);
    var controlCenterConfigurationPaths = configurationPathPattern
        .Matches(controlCenterSource)
        .Select(match => match.Value)
        .ToHashSet(StringComparer.Ordinal);
    var missingLegacyPaths = legacyConfigurationPaths
        .Except(controlCenterConfigurationPaths, StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToArray();
    Assert(
        missingLegacyPaths.Length == 0,
        $"The new control center lost legacy setting paths: {string.Join(", ", missingLegacyPaths)}");
}

static void ValidateChinese755hOpcodes()
{
    OpcodeManager.Instance.SetRegion(GameRegion.Chinese);
    var opcodes = OpcodeManager.Instance.CurrentOpcodes;
    var expected = new Dictionary<string, ushort>
    {
        ["Ability1"] = 0x0296,
        ["Ability8"] = 0x0164,
        ["Ability16"] = 0x01B1,
        ["Ability24"] = 0x039B,
        ["Ability32"] = 0x0372,
        ["ActorCast"] = 0x018C,
        ["EffectResult"] = 0x02F5,
        ["EffectResultBasic"] = 0x03A3,
        ["ActorControl"] = 0x01DA,
        ["ActorControlSelf"] = 0x035D,
        ["ActorControlTarget"] = 0x013C,
        ["StatusEffectList"] = 0x01F1,
        ["StatusEffectList2"] = 0x009B,
        ["StatusEffectList3"] = 0x0153,
        ["BossStatusEffectList"] = 0x0320,
        ["StatusEffectListForay3"] = 0x00DC,
        ["PlayerSpawn"] = 0x0398,
        ["NpcSpawn"] = 0x006F,
        ["NpcSpawn2"] = 0x0287,
        ["ActorMove"] = 0x038D,
        ["ActorSetPos"] = 0x03DF,
        ["ActorGauge"] = 0x0221,
        ["PresetWaymark"] = 0x0149,
        ["Waymark"] = 0x0171,
        ["SystemLogMessage"] = 0x01E7,
    };

    foreach (var pair in expected)
    {
        Assert(
            opcodes.TryGetValue(pair.Key, out var actual) && actual == pair.Value,
            $"Chinese 7.55h opcode {pair.Key} was {actual:X}, expected {pair.Value:X}.");
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
    var chinese755h = document.RootElement
        .GetProperty("Chinese")
        .GetProperty("2026.08.05.0000.0000");
    Assert(
        chinese755h.GetProperty("MapEffect").GetProperty("opcode").GetInt32() == 188 &&
        chinese755h.GetProperty("RSVData").GetProperty("opcode").GetInt32() == 979 &&
        chinese755h.GetProperty("Countdown").GetProperty("opcode").GetInt32() == 802 &&
        chinese755h.GetProperty("ActorMove").GetProperty("opcode").GetInt32() == 909 &&
        chinese755h.GetProperty("ActorSetPos").GetProperty("opcode").GetInt32() == 991,
        "OverlayPlugin Chinese 7.55h opcodes are stale.");
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
        manager.GetDisclosures().Count == 3,
        "The third-party notice did not expose every bundled DLL source.");
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
    Assert(
        manager.GetDisclosures().Count == 3 &&
        manager.GetDisclosures().All(plugin =>
            !string.IsNullOrWhiteSpace(plugin.Author) &&
            Uri.TryCreate(plugin.ProjectUrl, UriKind.Absolute, out _)),
        "Acknowledged DLL author and project URL disclosures disappeared from the notice.");
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

static async Task ValidateCactbotMissingTargetBackupRecoveryAsync(
    string testRoot,
    string validPackage)
{
    var paths = new PluginPaths(Path.Combine(testRoot, "cactbot-missing-target-recovery"));
    paths.EnsureCreated();
    await File.WriteAllBytesAsync(
        Path.Combine(paths.CactbotDirectory, "CactbotOverlay.dll"),
        [9, 8, 7]);
    var raidboss = Path.Combine(paths.CactbotDirectory, "ui", "raidboss", "raidboss.html");
    Directory.CreateDirectory(Path.GetDirectoryName(raidboss)!);
    await File.WriteAllTextAsync(raidboss, "old-raidboss");
    var userFile = Path.Combine(paths.CactbotDirectory, "user", "custom.js");
    Directory.CreateDirectory(Path.GetDirectoryName(userFile)!);
    await File.WriteAllTextAsync(userFile, "old-user-data");

    var installer = new CactbotPackageInstaller(paths);
    installer.MoveDirectory = (source, destination) =>
    {
        if (destination.Equals(paths.CactbotDirectory, StringComparison.OrdinalIgnoreCase))
        {
            if (source.StartsWith(paths.PluginStagingDirectory, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("simulated Cactbot commit failure");
            }

            if (source.StartsWith(
                    paths.CactbotDirectory + ".backup-",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("simulated Cactbot rollback failure");
            }
        }

        Directory.Move(source, destination);
    };

    AggregateException? failedReplacement = null;
    try
    {
        await installer.InstallAsync(validPackage, CancellationToken.None);
    }
    catch (AggregateException ex)
    {
        failedReplacement = ex;
    }

    var preservedBackup = Directory
        .EnumerateDirectories(
            paths.ConfigDirectory,
            $"{Path.GetFileName(paths.CactbotDirectory)}.backup-*")
        .Single();
    Assert(
        failedReplacement is not null &&
        !Directory.Exists(paths.CactbotDirectory) &&
        File.Exists(Path.Combine(preservedBackup, "CactbotOverlay.dll")) &&
        File.Exists(Path.Combine(preservedBackup, "user", "custom.js")),
        "A failed Cactbot commit/rollback did not preserve the previous installation as the recovery backup.");

    var invalidPackage = Path.Combine(testRoot, "cactbot-invalid-after-missing-target.zip");
    using (var archive = ZipFile.Open(invalidPackage, ZipArchiveMode.Create))
    {
        await WriteArchiveEntryAsync(archive, "not-cactbot/readme.txt", "invalid"u8.ToArray());
    }

    try
    {
        await installer.InstallAsync(invalidPackage, CancellationToken.None);
    }
    catch (InvalidDataException)
    {
    }
    Assert(
        !Directory.Exists(paths.CactbotDirectory) &&
        Directory.Exists(preservedBackup) &&
        File.Exists(Path.Combine(preservedBackup, "CactbotOverlay.dll")),
        "A transient recovery failure deleted the only Cactbot backup or created a false target directory.");

    installer.MoveDirectory = Directory.Move;
    var invalidFailed = false;
    try
    {
        await installer.InstallAsync(invalidPackage, CancellationToken.None);
    }
    catch (InvalidDataException)
    {
        invalidFailed = true;
    }

    Assert(
        invalidFailed &&
        installer.IsInstalled &&
        await File.ReadAllTextAsync(Path.Combine(paths.CactbotDirectory, "ui", "raidboss", "raidboss.html")) ==
            "old-raidboss" &&
        await File.ReadAllTextAsync(Path.Combine(paths.CactbotDirectory, "user", "custom.js")) ==
            "old-user-data",
        "The only recoverable Cactbot backup was deleted before the next package reached a valid staged state.");
}

static async Task ValidateCactbotPostShutdownPublicationGuardAsync()
{
    var publishCount = 0;
    await CactbotOperationLifecycle.PublishIfActiveAsync(
        shutdownStarted: false,
        CancellationToken.None,
        () =>
        {
            publishCount++;
            return Task.CompletedTask;
        });
    await CactbotOperationLifecycle.PublishIfActiveAsync(
        shutdownStarted: true,
        CancellationToken.None,
        () =>
        {
            publishCount++;
            return Task.CompletedTask;
        });
    using var canceled = new CancellationTokenSource();
    canceled.Cancel();
    await CactbotOperationLifecycle.PublishIfActiveAsync(
        shutdownStarted: false,
        canceled.Token,
        () =>
        {
            publishCount++;
            return Task.CompletedTask;
        });
    Assert(
        publishCount == 1,
        "Cactbot completion publication ran after plugin shutdown or cancellation.");

    var pluginSource = File.ReadAllText(Path.Combine(
        FindProjectRoot(), "src", "DalamudActCompat", "Plugin", "Plugin.cs"));
    Assert(
        pluginSource.Contains("PublishCactbotInstalledNotificationAsync", StringComparison.Ordinal) &&
        pluginSource.Contains("services.Framework.RunOnFrameworkThread", StringComparison.Ordinal) &&
        pluginSource.Contains("cancellationToken.ThrowIfCancellationRequested();", StringComparison.Ordinal),
        "Cactbot completion is not guarded and explicitly dispatched to the framework thread.");
}

static void ValidateFflogsConcurrencyBoundaries()
{
    var original = new FflogsSettings
    {
        Enabled = true,
        ClientId = "client",
        ClientSecret = "secret",
        EncounterMappings = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Boss"] = 1,
        },
    };
    var snapshot = original.Snapshot();
    snapshot.ClientId = "changed";
    snapshot.EncounterMappings["Boss"] = 2;
    snapshot.EncounterMappings["Other"] = 3;
    Assert(
        original.ClientId == "client" &&
        original.EncounterMappings["Boss"] == 1 &&
        !original.EncounterMappings.ContainsKey("Other"),
        "FFLogs configuration snapshots still share mutable UI state.");

    Assert(
        typeof(IAsyncDisposable).IsAssignableFrom(typeof(FflogsEstimateService)) &&
        typeof(FflogsEstimateService).GetMethod(nameof(FflogsEstimateService.BeginShutdown)) is not null,
        "FFLogs background work does not expose a cancellable, awaitable shutdown lifecycle.");

    var projectRoot = FindProjectRoot();
    var serviceSource = File.ReadAllText(Path.Combine(
        projectRoot, "src", "DalamudActCompat", "Fflogs", "FflogsEstimateService.cs"));
    var controlCenterSource = File.ReadAllText(Path.Combine(
        projectRoot, "src", "DalamudActCompat", "UI", "ControlCenterWindow.cs"));
    Assert(
        serviceSource.Contains("TryStartBackgroundTask", StringComparison.Ordinal) &&
        serviceSource.Contains("SaveCacheAsync", StringComparison.Ordinal) &&
        serviceSource.Contains("cacheWriteGate", StringComparison.Ordinal) &&
        !serviceSource.Contains("_ = Task.Run", StringComparison.Ordinal) &&
        controlCenterSource.Contains("UpdateFflogsSettings", StringComparison.Ordinal) &&
        controlCenterSource.Contains("configuration.Fflogs = next;", StringComparison.Ordinal),
        "FFLogs UI ownership, task tracking, or serialized cache persistence regressed.");
}

static async Task ValidateFflogsCacheWritersAsync(string testRoot)
{
    var cacheDirectory = Path.Combine(testRoot, "fflogs-cache-writers");
    var cachePath = Path.Combine(cacheDirectory, "fflogs-cache.json");
    Directory.CreateDirectory(cacheDirectory);
    var seededCache = JsonSerializer.Serialize(new FflogsCacheDocument(
            DateTimeOffset.UtcNow,
            [
                new FflogsEncounterCatalogEntry(100, "Howling Blade", "AAC Cruiserweight", false),
                new FflogsEncounterCatalogEntry(104, "Lindwurm", "AAC Heavyweight", false),
            ],
            [
                new FflogsCurveCacheEntry(100, "Howling Blade", "Paladin", DateTimeOffset.UtcNow, [], 101),
                new FflogsCurveCacheEntry(104, "Lindwurm", "Paladin", DateTimeOffset.UtcNow, [], 0),
                new FflogsCurveCacheEntry(104, "Lindwurm", "Paladin", DateTimeOffset.UtcNow, [], 100),
            ]));
    await File.WriteAllTextAsync(
        cachePath,
        seededCache.Replace(",\"Difficulty\":0", string.Empty, StringComparison.Ordinal));
    var log = DispatchProxy.Create<IPluginLog, NoOpPluginLogProxy>();
    var service = new FflogsEstimateService(
        () => new FflogsSettings(),
        cachePath,
        new PluginLogger(log));
    try
    {
        var firstTemporaryPath = FflogsEstimateService.CreateCacheTemporaryPath(cachePath);
        var secondTemporaryPath = FflogsEstimateService.CreateCacheTemporaryPath(cachePath);
        Assert(
            !firstTemporaryPath.Equals(secondTemporaryPath, StringComparison.OrdinalIgnoreCase),
            "FFLogs cache writers still share one temporary-file path.");

        await service.SaveCacheAsync(CancellationToken.None);
        await service.SaveCacheAsync(CancellationToken.None);
        await Task.WhenAll(
            service.SaveCacheAsync(CancellationToken.None),
            service.SaveCacheAsync(CancellationToken.None));

        using var cacheDocument = JsonDocument.Parse(await File.ReadAllTextAsync(cachePath));
        var cachedEncounterIds = cacheDocument.RootElement.GetProperty("Encounters")
            .EnumerateArray()
            .Select(static encounter => encounter.GetProperty("Id").GetInt32())
            .ToArray();
        var cachedCurveIds = cacheDocument.RootElement.GetProperty("Curves")
            .EnumerateArray()
            .Select(static curve => curve.GetProperty("EncounterId").GetInt32())
            .ToArray();
        Assert(
            cacheDocument.RootElement.TryGetProperty("CatalogFetchedAt", out _) &&
            cachedEncounterIds.SequenceEqual([104]) &&
            cachedCurveIds.SequenceEqual([104]) &&
            cacheDocument.RootElement.GetProperty("Curves")[0].GetProperty("Difficulty").GetInt32() == 100 &&
            !Directory.EnumerateFiles(cacheDirectory, "*.tmp").Any(),
            "FFLogs cache persistence retained an old ranking tier, corrupted the cache, or left temp files behind.");
    }
    finally
    {
        await service.DisposeAsync();
    }
}

static async Task ValidateFactoryResetRollbackAsync(string testRoot)
{
    var configRoot = Path.Combine(testRoot, "factory-reset-rollback");
    var paths = new PluginPaths(configRoot);
    paths.EnsureCreated();
    var originalFile = Path.Combine(configRoot, "user-data.txt");
    await File.WriteAllTextAsync(originalFile, "original");
    var configuration = new PluginConfiguration
    {
        DebugMode = true,
        HistoryLimit = 42,
        UiLanguage = "en",
    };
    var parser = new TestParserEngine(ParserState.Running);
    var log = DispatchProxy.Create<IPluginLog, NoOpPluginLogProxy>();
    var persistedConfiguration = Path.Combine(configRoot, "persisted-configuration.txt");
    var saveAttempts = 0;
    var service = new FactoryResetService(
        parser,
        paths,
        configuration,
        new PluginLogger(log),
        () =>
        {
            saveAttempts++;
            File.WriteAllText(
                persistedConfiguration,
                $"{configuration.DebugMode}|{configuration.HistoryLimit}|{configuration.UiLanguage}");
            if (saveAttempts == 1)
            {
                throw new IOException("simulated configuration commit failure");
            }
        });

    var failed = false;
    try
    {
        await service.ResetAsync(CancellationToken.None);
    }
    catch (IOException)
    {
        failed = true;
    }

    Assert(failed, "The simulated factory-reset commit failure was not observed.");
    Assert(
        File.Exists(originalFile) &&
        await File.ReadAllTextAsync(originalFile) == "original",
        "Factory-reset rollback did not restore files moved before the failed commit.");
    Assert(
        configuration.DebugMode &&
        configuration.HistoryLimit == 42 &&
        configuration.UiLanguage == "en",
        "Factory-reset rollback did not restore the in-memory configuration.");
    Assert(
        saveAttempts == 2 &&
        await File.ReadAllTextAsync(persistedConfiguration) == "True|42|en",
        "Factory-reset rollback did not persist the restored configuration snapshot.");
    Assert(
        parser.StopCount == 1 &&
        parser.StartCount == 1 &&
        parser.Status.State == ParserState.Running,
        "Factory-reset failure left a previously running parser stopped.");

    var partialRoot = Path.Combine(testRoot, "factory-reset-partial-staging");
    var partialPaths = new PluginPaths(partialRoot);
    partialPaths.EnsureCreated();
    var partialFiles = new Dictionary<string, string>
    {
        [Path.Combine(partialRoot, "first.cfg")] = "first",
        [Path.Combine(partialRoot, "second.cfg")] = "second",
        [Path.Combine(partialRoot, "third.cfg")] = "third",
    };
    foreach (var (path, contents) in partialFiles)
    {
        await File.WriteAllTextAsync(path, contents);
    }

    var partialConfiguration = new PluginConfiguration { HistoryLimit = 77 };
    var partialParser = new TestParserEngine(ParserState.Stopped);
    var partialSaveCalled = false;
    var partialService = new FactoryResetService(
        partialParser,
        partialPaths,
        partialConfiguration,
        new PluginLogger(log),
        () => partialSaveCalled = true);
    var stagedOriginalEntries = 0;
    partialService.StageEntry = (source, destination) =>
    {
        if (partialFiles.ContainsKey(source))
        {
            stagedOriginalEntries++;
            if (stagedOriginalEntries == 2)
            {
                throw new IOException("simulated partial staging failure");
            }
        }

        MoveTestEntry(source, destination);
    };

    var partialFailed = false;
    try
    {
        await partialService.ResetAsync(CancellationToken.None);
    }
    catch (IOException ex) when (ex.Message == "simulated partial staging failure")
    {
        partialFailed = true;
    }

    Assert(
        partialFailed && stagedOriginalEntries == 2 && !partialSaveCalled,
        "The partial factory-reset staging failure did not occur before defaults were applied.");
    foreach (var (path, contents) in partialFiles)
    {
        Assert(
            File.Exists(path) && await File.ReadAllTextAsync(path) == contents,
            $"Factory-reset partial staging rollback lost an original entry: {Path.GetFileName(path)}");
    }
    Assert(
        partialConfiguration.HistoryLimit == 77,
        "Factory-reset partial staging rollback changed the original in-memory configuration.");

    var doubleFailureRoot = Path.Combine(testRoot, "factory-reset-persist-rollback-failure");
    var doubleFailurePaths = new PluginPaths(doubleFailureRoot);
    doubleFailurePaths.EnsureCreated();
    var doubleFailureFile = Path.Combine(doubleFailureRoot, "original.cfg");
    await File.WriteAllTextAsync(doubleFailureFile, "original");
    var doubleFailureConfiguration = new PluginConfiguration { HistoryLimit = 88 };
    var doubleFailureService = new FactoryResetService(
        new TestParserEngine(ParserState.Stopped),
        doubleFailurePaths,
        doubleFailureConfiguration,
        new PluginLogger(log),
        () => throw new IOException("simulated persistent configuration failure"));
    AggregateException? rollbackAggregate = null;
    try
    {
        await doubleFailureService.ResetAsync(CancellationToken.None);
    }
    catch (AggregateException ex)
    {
        rollbackAggregate = ex;
    }

    Assert(
        rollbackAggregate is not null &&
        rollbackAggregate.Flatten().InnerExceptions.Any(error =>
            error.Message.Contains("persist", StringComparison.OrdinalIgnoreCase)) &&
        doubleFailureConfiguration.HistoryLimit == 88 &&
        File.Exists(doubleFailureFile),
        "A failed persisted rollback was swallowed or did not retain the restored in-memory/filesystem state.");

    var successRoot = Path.Combine(testRoot, "factory-reset-commit");
    var successPaths = new PluginPaths(successRoot);
    successPaths.EnsureCreated();
    var successOriginalFile = Path.Combine(successRoot, "user-data.txt");
    await File.WriteAllTextAsync(successOriginalFile, "backup-me");
    var successConfiguration = new PluginConfiguration
    {
        DebugMode = true,
        HistoryLimit = 99,
    };
    var successParser = new TestParserEngine(ParserState.Running);
    var successService = new FactoryResetService(
        successParser,
        successPaths,
        successConfiguration,
        new PluginLogger(log),
        () => File.WriteAllText(Path.Combine(successRoot, "configuration-saved.marker"), "saved"));
    var backup = await successService.ResetAsync(CancellationToken.None);
    Assert(
        File.Exists(Path.Combine(backup, "user-data.txt")) &&
        !File.Exists(successOriginalFile) &&
        File.Exists(Path.Combine(successRoot, "configuration-saved.marker")),
        "Factory reset did not commit the new layout while preserving the original files in its backup.");
    Assert(
        !successConfiguration.DebugMode &&
        successConfiguration.HistoryLimit == 20 &&
        successParser.StopCount == 1 &&
        successParser.StartCount == 0,
        "Successful factory reset changed its existing stop-and-reset semantics.");

    var shutdownRoot = Path.Combine(testRoot, "factory-reset-shutdown");
    var shutdownPaths = new PluginPaths(shutdownRoot);
    shutdownPaths.EnsureCreated();
    await File.WriteAllTextAsync(Path.Combine(shutdownRoot, "original.cfg"), "original");
    var shutdownParser = new TestParserEngine(ParserState.Running);
    using var pluginShutdown = new CancellationTokenSource();
    var shutdownService = new FactoryResetService(
        shutdownParser,
        shutdownPaths,
        new PluginConfiguration(),
        new PluginLogger(log),
        () => { });
    shutdownService.StageEntry = (_, _) =>
    {
        pluginShutdown.Cancel();
        throw new IOException("simulated staging failure during shutdown");
    };
    try
    {
        await shutdownService.ResetAsync(CancellationToken.None, pluginShutdown.Token);
        throw new InvalidOperationException("Factory reset did not expose the shutdown staging failure.");
    }
    catch (IOException ex) when (ex.Message == "simulated staging failure during shutdown")
    {
    }

    Assert(
        shutdownParser.StopCount == 1 && shutdownParser.StartCount == 0,
        "Factory-reset rollback restarted the parser after plugin shutdown began.");

    var operationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var cancellationObserved = false;
    var operationCount = 0;
    var coordinator = new FactoryResetOperationCoordinator(async shutdownToken =>
    {
        operationCount++;
        operationStarted.TrySetResult();
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, shutdownToken);
            return "unexpected";
        }
        catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
        {
            cancellationObserved = true;
            throw;
        }
    });
    var firstReset = coordinator.Start();
    var duplicateReset = coordinator.Start();
    Assert(
        ReferenceEquals(firstReset, duplicateReset),
        "Factory-reset coordinator allowed more than one simultaneous reset task.");
    await operationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
    Assert(
        await coordinator.WaitForShutdownAsync(TimeSpan.FromSeconds(2)) &&
        cancellationObserved &&
        operationCount == 1 &&
        coordinator.Start().IsCanceled,
        "Factory-reset coordinator did not cancel and join its active task during shutdown.");
}

static void MoveTestEntry(string source, string destination)
{
    if (Directory.Exists(source))
    {
        Directory.Move(source, destination);
    }
    else
    {
        File.Move(source, destination);
    }
}

static async Task ValidateBundledPluginInstallLifecycleAsync()
{
    var events = new List<string>();
    await BundledPluginInstallCoordinator.ExecuteAsync(
        hostWasRunning: true,
        parserWasRunning: true,
        _ => RecordAsync("stop-host"),
        _ => RecordAsync("stop-parser"),
        _ => RecordAsync("install"),
        _ => RecordAsync("start-host"),
        _ => RecordAsync("start-parser"),
        () => true,
        CancellationToken.None,
        TimeSpan.FromSeconds(1));
    Assert(
        events.SequenceEqual(
            ["stop-host", "stop-parser", "install", "start-host", "start-parser"]),
        "Bundled DLL installation did not pause and restore running ACT services in order.");

    events.Clear();
    var failed = false;
    try
    {
        await BundledPluginInstallCoordinator.ExecuteAsync(
            hostWasRunning: true,
            parserWasRunning: true,
            _ => RecordAsync("stop-host"),
            _ => RecordAsync("stop-parser"),
            _ => throw new InvalidOperationException("expected install failure"),
            _ => RecordAsync("start-host"),
            _ => RecordAsync("start-parser"),
            () => true,
            CancellationToken.None,
            TimeSpan.FromSeconds(1));
    }
    catch (InvalidOperationException ex) when (ex.Message == "expected install failure")
    {
        failed = true;
    }

    Assert(failed, "A bundled DLL installation failure was not observed by the caller.");
    Assert(
        events.SequenceEqual(
            ["stop-host", "stop-parser", "start-host", "start-parser"]),
        "ACT services were not restored after a bundled DLL installation failure.");

    events.Clear();
    await BundledPluginInstallCoordinator.ExecuteAsync(
        hostWasRunning: false,
        parserWasRunning: false,
        _ => RecordAsync("stop-host"),
        _ => RecordAsync("stop-parser"),
        _ => RecordAsync("install"),
        _ => RecordAsync("start-host"),
        _ => RecordAsync("start-parser"),
        () => true,
        CancellationToken.None,
        TimeSpan.FromSeconds(1));
    Assert(
        events.SequenceEqual(["install"]),
        "Bundled DLL installation changed ACT services that were already stopped.");

    Task RecordAsync(string value)
    {
        events.Add(value);
        return Task.CompletedTask;
    }
}

static async Task ValidateEncounterShutdownFlushAsync(string testRoot)
{
    var root = Path.Combine(testRoot, "encounter-shutdown-flush");
    var paths = new PluginPaths(root);
    var repository = new EncounterRepository(new JsonFileStore(), paths);
    var stateStore = new EncounterStateStore();
    var configuration = new PluginConfiguration { HistoryLimit = 5 };
    var log = DispatchProxy.Create<IPluginLog, NoOpPluginLogProxy>();
    var service = new EncounterService(
        repository,
        stateStore,
        configuration,
        new PluginLogger(log),
        paths);
    var encounter = SampleEncounterFactory.Create(DateTimeOffset.UtcNow) with
    {
        EndTime = DateTimeOffset.UtcNow,
    };

    service.QueueFinishedEncounter(encounter);
    await service.DisposeAsync();
    await service.DisposeAsync();

    var persisted = await repository.LoadRecentAsync(CancellationToken.None);
    Assert(
        persisted.Count == 1 && persisted[0].Id == encounter.Id,
        "An encounter submitted immediately before shutdown was not flushed to history.");
    Assert(
        Directory.EnumerateFiles(paths.EncounterLogDirectory, "*.json").Count() == 1,
        "An encounter submitted immediately before shutdown did not write its individual log.");
}

static async Task ValidateAtomicEncounterStateUpdatesAsync()
{
    var stateStore = new EncounterStateStore();
    var start = DateTimeOffset.UtcNow;
    var current = SampleEncounterFactory.Create(start);
    var recent = SampleEncounterFactory.Create(start.AddMinutes(-1)) with
    {
        EndTime = start,
    };
    using var gate = new ManualResetEventSlim();

    var updateCurrent = Task.Run(() =>
    {
        gate.Wait();
        for (var iteration = 0; iteration < 1000; iteration++)
        {
            stateStore.UpdateCurrent(current);
        }
    });
    var updateRecent = Task.Run(() =>
    {
        gate.Wait();
        for (var iteration = 0; iteration < 1000; iteration++)
        {
            stateStore.UpdateRecent([recent]);
        }
    });

    gate.Set();
    await Task.WhenAll(updateCurrent, updateRecent);
    var snapshot = stateStore.GetSnapshot();
    Assert(
        snapshot.Current?.Id == current.Id &&
        snapshot.Recent.Count == 1 &&
        snapshot.Recent[0].Id == recent.Id,
        "Concurrent Current and Recent encounter updates overwrote one another.");
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

static void ValidateCombatEventScoping()
{
    ActPlayerIdentity[] identities =
    [
        new("Local Player", "Alpha", "PLD", true, false),
        new("Party Member", "Beta", "WHM", false, false),
    ];

    Assert(
        SelfHostedActRuntime.IsTrackedCombatantEvent(
            "Local Player@Alpha",
            "Training Dummy",
            identities),
        "A local-player combat event was rejected by encounter scoping.");
    Assert(
        SelfHostedActRuntime.IsTrackedCombatantEvent(
            "Enemy",
            "Party Member@Beta",
            identities),
        "An event targeting a party member was rejected by encounter scoping.");
    Assert(
        !SelfHostedActRuntime.IsTrackedCombatantEvent(
            "Nearby Stranger",
            "Training Dummy",
            identities),
        "A nearby stranger can still extend the local ACT encounter.");

    var runtimeSource = File.ReadAllText(Path.Combine(
        FindProjectRoot(),
        "src",
        "DalamudActCompat.ActRuntime",
        "SelfHostedActRuntime.cs"));
    Assert(
        runtimeSource.Contains("ConditionFlag.BoundByDuty", StringComparison.Ordinal) &&
        runtimeSource.Contains("lastRelevantCombatAction", StringComparison.Ordinal) &&
        runtimeSource.Contains("ActGlobals.oFormActMain.EndCombat(true)", StringComparison.Ordinal),
        "Open-world combat does not end independently while duty encounters remain protected.");
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
    Assert(
        typeof(SelfHostedActRuntime).GetMethod(
            nameof(SelfHostedActRuntime.DeleteHtmlOverlay),
            BindingFlags.Instance | BindingFlags.Public,
            null,
            [typeof(string)],
            null)?.ReturnType == typeof(bool),
        "The HTML overlay runtime no longer exposes explicit window deletion.");
    Assert(
        typeof(ControlCenterWindow).GetConstructors()
            .Single()
            .GetParameters()
            .Any(parameter =>
                parameter.Name == "deleteHtmlOverlay" &&
                parameter.ParameterType == typeof(Action<string>)),
        "The control center no longer exposes the created-overlay delete action.");
    Assert(
        typeof(ControlCenterWindow).GetConstructors()
            .Single()
            .GetParameters()
            .Any(parameter =>
                parameter.Name == "resetCurrentEncounter" &&
                parameter.ParameterType == typeof(Action)),
        "The control center no longer owns the guarded encounter-reset action.");

    var controlCenterSource = File.ReadAllText(Path.Combine(
        FindProjectRoot(),
        "src",
        "DalamudActCompat",
        "UI",
        "ControlCenterWindow.cs"));
    var createdOverlayIndex = controlCenterSource.IndexOf(
        "changed |= DrawCreatedHtmlOverlays();",
        StringComparison.Ordinal);
    var templateOverlayIndex = controlCenterSource.IndexOf(
        "从模板创建",
        StringComparison.Ordinal);
    Assert(
        createdOverlayIndex >= 0 && templateOverlayIndex > createdOverlayIndex &&
        controlCenterSource.Contains("HTML 悬浮窗", StringComparison.Ordinal) &&
        controlCenterSource.Contains("从网址创建", StringComparison.Ordinal) &&
        controlCenterSource.Contains("只添加你信任的悬浮窗页面", StringComparison.Ordinal),
        "Created/custom HTML overlays are not listed above the template form or the trust warning regressed.");
    Assert(
        controlCenterSource.Contains(
            "private const int ResetConfirmationMilliseconds = 10_000;",
            StringComparison.Ordinal),
        "The encounter-reset confirmation is not configured to close after ten seconds.");
    var fflogsApiCardIndex = controlCenterSource.IndexOf(
        "fflogs-api-refresh-card",
        StringComparison.Ordinal);
    var fflogsAutomaticCardIndex = controlCenterSource.IndexOf(
        "fflogs-automatic-encounter-card",
        StringComparison.Ordinal);
    Assert(
        fflogsApiCardIndex >= 0 && fflogsAutomaticCardIndex > fflogsApiCardIndex &&
        controlCenterSource.Contains("API 凭据与数据刷新", StringComparison.Ordinal) &&
        controlCenterSource.Contains("当前副本自动识别", StringComparison.Ordinal) &&
        !controlCenterSource.Contains("绑定当前战斗", StringComparison.Ordinal),
        "FFLogs automatic duty matching is missing or manual encounter binding is still exposed.");
    Assert(
        !controlCenterSource.Contains("control-center-sidebar", StringComparison.Ordinal) &&
        !controlCenterSource.Contains("parser-state-card", StringComparison.Ordinal) &&
        controlCenterSource.Contains("ImGuiStyleVar.WindowRounding", StringComparison.Ordinal) &&
        controlCenterSource.Contains("VersionLabel", StringComparison.Ordinal),
        "The control center did not move branding, centered parser state, version, and close control into rounded top chrome.");
    var meterWindowSource = File.ReadAllText(Path.Combine(
        FindProjectRoot(),
        "src",
        "DalamudActCompat",
        "Meter",
        "MeterWindow.cs"));
    Assert(
        !meterWindowSource.Contains("ResetCurrent", StringComparison.Ordinal) &&
        !meterWindowSource.Contains("清空当前战斗", StringComparison.Ordinal) &&
        !meterWindowSource.Contains("DrawControls(", StringComparison.Ordinal) &&
        meterWindowSource.Contains("meter-column-header", StringComparison.Ordinal) &&
        meterWindowSource.Contains("#  玩家", StringComparison.Ordinal) &&
        meterWindowSource.Contains("jobIcons.GetLimitBreak()", StringComparison.Ordinal),
        "The Meter overlay still exposes controls, its table header is missing, or Limit Break lacks its icon.");

    var settings = new HtmlOverlayWindowSettings();
    Assert(!settings.IsVisible, "HTML overlays must remain closed until explicitly opened.");
    Assert(!settings.OpenOnStartup, "HTML overlays must not auto-open before the user opens them.");
    Assert(settings.IsClickThrough, "HTML overlays must be click-through by default.");
    Assert(settings.IsLocked, "HTML overlays must be locked by default.");
    Assert(!settings.IsEditing, "HTML overlays must not start in editing mode.");
    Assert(settings.ZoomFactor == 1.0f, "HTML overlay default zoom changed unexpectedly.");
    Assert(string.IsNullOrEmpty(settings.SourceUrl), "Template overlays unexpectedly received a custom source URL.");
    settings.SetEditing(true);
    Assert(
        settings.IsEditing && !settings.IsClickThrough && !settings.IsLocked,
        "HTML overlay editing mode did not disable click-through and locking together.");
    settings.SetEditing(false);
    Assert(
        !settings.IsEditing && !settings.IsClickThrough && settings.IsLocked,
        "Finishing HTML overlay editing did not lock the layout while preserving page input.");

    settings.OpenOnStartup = true;
    var serializedOverlaySettings = Newtonsoft.Json.JsonConvert.SerializeObject(settings);
    var restoredOverlaySettings = Newtonsoft.Json.JsonConvert.DeserializeObject<HtmlOverlayWindowSettings>(
                                      serializedOverlaySettings)
                                  ?? throw new InvalidOperationException(
                                      "HTML overlay settings did not deserialize.");
    Assert(
        serializedOverlaySettings.Contains(nameof(HtmlOverlayWindowSettings.OpenOnStartup), StringComparison.Ordinal) &&
        !serializedOverlaySettings.Contains(nameof(HtmlOverlayWindowSettings.IsVisible), StringComparison.Ordinal) &&
        restoredOverlaySettings.OpenOnStartup,
        "The user-requested HTML overlay startup state was not persisted independently of runtime visibility.");
    var overlaysToRestore = SelfHostedActRuntime.SelectHtmlOverlaysToRestore(
        new Dictionary<string, HtmlOverlayWindowSettings>(StringComparer.OrdinalIgnoreCase)
        {
            ["Skills"] = restoredOverlaySettings,
            ["Closed"] = new HtmlOverlayWindowSettings(),
            [SelfHostedActRuntime.CactbotOverlayName] = new HtmlOverlayWindowSettings
            {
                OpenOnStartup = true,
            },
        });
    Assert(
        overlaysToRestore.SequenceEqual(["Skills"]),
        "HTML overlay startup restoration did not preserve only the user-open custom windows.");

    Assert(
        SelfHostedActRuntime.TryBuildCustomOverlayUri(
            "https://souma.diemoe.net/ff14-overlay-vue/#/teamWatch",
            new Uri("ws://127.0.0.1:10501/ws"),
            out var customOverlayUri) &&
        customOverlayUri.AbsoluteUri.Contains(
            "#/teamWatch?OVERLAY_WS=ws://127.0.0.1:10501/ws&HOST_PORT=ws://127.0.0.1:10501",
            StringComparison.Ordinal),
        "Hash-routed custom overlays did not receive usable OverlayPlugin WebSocket parameters.");
    Assert(
        !SelfHostedActRuntime.TryNormalizeCustomOverlayUri(
            "javascript:alert(1)",
            out _),
        "Custom overlays accepted a non-http/file executable URL scheme.");
    Assert(
        new PluginConfiguration().AutoCheckBundledPluginUpdates,
        "Existing users no longer retain the startup extension-update check by default.");

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
                       ?.GetRawConstantValue() as string
                       ?? throw new InvalidOperationException(
                           "Cactbot responsive alert layout script was not found.");
    Assert(
        layoutScript.Contains("#popup-text-container", StringComparison.Ordinal) &&
        layoutScript.Contains("z-index: 2147483646", StringComparison.Ordinal) &&
        layoutScript.Contains(
            "#container:not(.hide-alerts).dalamud-act-compat-alert-repair",
            StringComparison.Ordinal) &&
        layoutScript.Contains("new MutationObserver", StringComparison.Ordinal) &&
        layoutScript.Contains("window.chrome?.webview?.postMessage", StringComparison.Ordinal) &&
        layoutScript.Contains("alertsDisabled", StringComparison.Ordinal) &&
        layoutScript.Contains("#popup-text-info", StringComparison.Ordinal) &&
        layoutScript.Contains("max-height: 220px", StringComparison.Ordinal),
        "The Cactbot layout no longer repairs and reports unexpectedly invisible alert text.");
    Assert(
        !layoutScript.Contains(
            ".hide-alerts #popup-text-container { display: block",
            StringComparison.Ordinal) &&
        !layoutScript.Contains(
            "#container.hide-alerts.dalamud-act-compat-alert-repair",
            StringComparison.Ordinal),
        "The Cactbot visibility repair can override a user's explicit disabled-alert setting.");
    var editIndicatorScript = formType.GetField(
                                  "OverlayEditIndicatorScript",
                                  BindingFlags.Static | BindingFlags.NonPublic)
                              ?.GetRawConstantValue() as string;
    Assert(
        editIndicatorScript?.Contains(
            "data-dalamud-act-compat-editing='true'",
            StringComparison.Ordinal) == true &&
        editIndicatorScript.Contains("编辑模式", StringComparison.Ordinal) &&
        editIndicatorScript.Contains("repeating-linear-gradient", StringComparison.Ordinal) &&
        editIndicatorScript.Contains("cursor: nwse-resize", StringComparison.Ordinal),
        "Transparent HTML overlays no longer expose a visible edit boundary and resize grip.");
    var isTransientWebViewFailure = formType.GetMethod(
                                        "IsTransientWebViewInitializationFailure",
                                        BindingFlags.Static | BindingFlags.NonPublic)
                                    ?? throw new InvalidOperationException(
                                        "WebView2 transient initialization detector was not found.");
    Assert(
        isTransientWebViewFailure.Invoke(
            null,
            [new System.Runtime.InteropServices.COMException(
                "The operation was aborted.",
                unchecked((int)0x80004004))]) as bool? == true &&
        isTransientWebViewFailure.Invoke(
            null,
            [new InvalidOperationException("permanent")]) as bool? == false,
        "Cactbot WebView2 startup no longer retries only the transient E_ABORT failure.");

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
                [new System.Drawing.Size(900, 320), new System.Drawing.Point(875, 295)]),
            resize),
        "HTML overlay cursor polling no longer exposes an accessible bottom-right resize target.");
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
    var hasExceededDragThreshold = formType.GetMethod(
                                       "HasExceededDragThreshold",
                                       BindingFlags.Static | BindingFlags.NonPublic)
                                   ?? throw new InvalidOperationException(
                                       "HTML overlay drag threshold helper was not found.");
    Assert(
        hasExceededDragThreshold.Invoke(
            null,
            [new System.Drawing.Point(400, 300), new System.Drawing.Point(404, 304)]) as bool? == false &&
        hasExceededDragThreshold.Invoke(
            null,
            [new System.Drawing.Point(400, 300), new System.Drawing.Point(406, 300)]) as bool? == true,
        "HTML overlay clicks are no longer separated from intentional drag gestures.");
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
        shouldEnableBrowserInput.Invoke(null, [settings]) as bool? == true,
        "Windowed WebView2 did not keep page clicks enabled while editing an overlay.");
    settings.SetEditing(false);
    Assert(
        shouldEnableBrowserInput.Invoke(null, [settings]) as bool? == true,
        "Windowed WebView2 did not preserve page interaction after the overlay was locked.");
    var calculateExtendedStyle = formType.GetMethod(
                                     "CalculateOverlayExtendedStyle",
                                     BindingFlags.Static | BindingFlags.NonPublic)
                                 ?? throw new InvalidOperationException(
                                     "HTML overlay extended-style helper was not found.");
    var clickThroughStyle = (nint)calculateExtendedStyle.Invoke(
        null,
        [(nint)0x00000100, true])!;
    Assert(
        (clickThroughStyle & (nint)0x00080000) != nint.Zero &&
        (clickThroughStyle & (nint)0x00000080) != nint.Zero &&
        (clickThroughStyle & (nint)0x00040000) == nint.Zero &&
        (clickThroughStyle & (nint)0x00000020) != nint.Zero &&
        (clickThroughStyle & (nint)0x08000000) != nint.Zero,
        "A click-through HTML overlay no longer remains a hidden tool window that is layered, transparent, and non-activating.");
    var interactiveStyle = (nint)calculateExtendedStyle.Invoke(
        null,
        [clickThroughStyle, false])!;
    Assert(
        (interactiveStyle & (nint)0x00080000) != nint.Zero &&
        (interactiveStyle & (nint)0x00000080) != nint.Zero &&
        (interactiveStyle & (nint)0x00040000) == nint.Zero &&
        (interactiveStyle & (nint)0x00000020) == nint.Zero &&
        (interactiveStyle & (nint)0x08000000) == nint.Zero,
        "An interactive HTML overlay entered the task switcher, passed clicks through, or refused activation.");
    var htmlOverlayFormSource = File.ReadAllText(Path.Combine(
        FindProjectRoot(),
        "src",
        "DalamudActCompat.ActRuntime",
        "HtmlOverlayForm.cs"));
    Assert(
        htmlOverlayFormSource.Contains(
            "SwpNoSize | SwpNoMove | SwpNoActivate | SwpFrameChanged",
            StringComparison.Ordinal),
        "HTML overlay style changes are no longer flushed through SWP_FRAMECHANGED.");
    var calculateBrowserInputPoint = formType.GetMethod(
                                         "CalculateBrowserInputPoint",
                                         BindingFlags.Static | BindingFlags.NonPublic)
                                     ?? throw new InvalidOperationException(
                                         "HTML overlay browser-input coordinate helper was not found.");
    Assert(
        calculateBrowserInputPoint.Invoke(
            null,
            [
                new System.Drawing.Size(1000, 500),
                new System.Drawing.SizeF(500, 250),
                new System.Drawing.Point(250, 100),
            ]) is System.Drawing.PointF { X: 125, Y: 50 },
        "HTML overlay proxy clicks no longer map to browser viewport coordinates.");
    var calculateInputProxyRectangles = formType.GetMethod(
                                            "CalculateInputProxyRectangles",
                                            BindingFlags.Static | BindingFlags.NonPublic)
                                        ?? throw new InvalidOperationException(
                                            "HTML overlay dynamic input-region mapper was not found.");
    var mappedInputRectangles = calculateInputProxyRectangles.Invoke(
        null,
        [
            new System.Drawing.Size(400, 200),
            new System.Drawing.SizeF(200, 100),
            new[] { new System.Drawing.RectangleF(50, 20, 100, 40) },
        ]) as System.Drawing.Rectangle[];
    Assert(
        mappedInputRectangles is [{ X: 98, Y: 38, Width: 204, Height: 84 }],
        "HTML overlay visible browser regions no longer map to the input proxy with safe padding.");
    var inputRegionScript = formType.GetField(
                                "OverlayInputRegionScript",
                                BindingFlags.Static | BindingFlags.NonPublic)
                            ?.GetRawConstantValue() as string
                            ?? throw new InvalidOperationException(
                                "HTML overlay dynamic input-region script was not found.");
    Assert(
        htmlOverlayFormSource.Contains("Opacity = 0.01", StringComparison.Ordinal) &&
        htmlOverlayFormSource.Contains("inputProxy.Show(form)", StringComparison.Ordinal) &&
        htmlOverlayFormSource.Contains("Input.dispatchMouseEvent", StringComparison.Ordinal) &&
        htmlOverlayFormSource.Contains("inputProxy.Region = nextRegion", StringComparison.Ordinal) &&
        inputRegionScript.Contains("ResizeObserver", StringComparison.Ordinal) &&
        inputRegionScript.Contains("MutationObserver", StringComparison.Ordinal) &&
        inputRegionScript.Contains("dalamud-act-compat:input-regions:", StringComparison.Ordinal),
        "The transparent HTML overlay no longer has a content-shaped dynamic input proxy.");
    Assert(
        inputRegionScript.Contains("performance.now()", StringComparison.Ordinal) &&
        inputRegionScript.Contains("diagnostics.refreshCount", StringComparison.Ordinal) &&
        inputRegionScript.Contains("scannedElements", StringComparison.Ordinal) &&
        inputRegionScript.Contains("peakFinalRectangles", StringComparison.Ordinal) &&
        inputRegionScript.Contains("diagnosticWindowMilliseconds >= 10000", StringComparison.Ordinal) &&
        htmlOverlayFormSource.Contains(
            "window.__dalamudActCompatInputRegionDiagnostics",
            StringComparison.Ordinal) &&
        htmlOverlayFormSource.Contains("RecordRegionRebuildDiagnostic", StringComparison.Ordinal) &&
        htmlOverlayFormSource.Contains("Windows input-region rebuild diagnostic", StringComparison.Ordinal),
        "HTML dynamic hit-region profiling is missing, unthrottled, or no longer debug-only.");
    var disabledDiagnosticBranch = inputRegionScript.IndexOf(
        "if (diagnostics === null)",
        StringComparison.Ordinal);
    var enabledDiagnosticMeasurement = inputRegionScript.IndexOf(
        "const startedAt = performance.now();",
        StringComparison.Ordinal);
    var disabledFastPath = disabledDiagnosticBranch >= 0 &&
                           enabledDiagnosticMeasurement > disabledDiagnosticBranch
        ? inputRegionScript[disabledDiagnosticBranch..enabledDiagnosticMeasurement]
        : string.Empty;
    Assert(
        inputRegionScript.Contains("let diagnostics = null;", StringComparison.Ordinal) &&
        inputRegionScript.Contains(
            "if (window.__dalamudActCompatInputRegionDiagnostics === true)",
            StringComparison.Ordinal) &&
        disabledFastPath.Contains("rectangles: collectRegions()", StringComparison.Ordinal) &&
        disabledFastPath.Contains("return;", StringComparison.Ordinal) &&
        !disabledFastPath.Contains("performance.now()", StringComparison.Ordinal) &&
        !disabledFastPath.Contains("Math.max", StringComparison.Ordinal) &&
        htmlOverlayFormSource.IndexOf("if (!debugMode)", StringComparison.Ordinal) <
        htmlOverlayFormSource.IndexOf(
            "var diagnosticTimer = Stopwatch.StartNew();",
            StringComparison.Ordinal),
        "HTML input-region diagnostics still execute measurement work on the disabled fast path.");
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

static async Task ValidateLiveHtmlOverlayInputAsync(string testRoot)
{
    var projectRoot = FindProjectRoot();
    var loaderPath = Path.Combine(
        projectRoot,
        "src",
        "DalamudActCompat.ActRuntime",
        "bin",
        "Release",
        "net10.0-windows",
        "win-x64",
        "WebView2Loader.dll");
    Assert(File.Exists(loaderPath), "The live WebView2 input smoke could not find WebView2Loader.dll.");

    var pagePath = Path.Combine(testRoot, "html-overlay-input-smoke.html");
    await File.WriteAllTextAsync(
        pagePath,
        """
        <!doctype html>
        <html>
        <body style="margin:0;background:transparent">
          <div id="panel" style="position:absolute;left:20px;top:20px;width:200px;height:100px;
                                 background:rgba(20,30,40,.9)">
            <button id="probe" style="position:absolute;left:20px;top:20px;width:120px;height:50px"
                    onclick="document.documentElement.dataset.clicked='true'">Click</button>
          </div>
          <script>
            window.collapseProbe = () => {
              const panel = document.getElementById('panel');
              panel.style.width = '60px';
              panel.style.height = '30px';
              document.getElementById('probe').style.display = 'none';
            };
          </script>
        </body>
        </html>
        """);

    var formType = typeof(HtmlOverlayWindowSettings).Assembly.GetType(
                       "DalamudActCompat.ActRuntime.HtmlOverlayForm")
                   ?? throw new InvalidOperationException("HTML overlay form was not found.");
    var settings = new HtmlOverlayWindowSettings
    {
        IsClickThrough = false,
        IsLocked = true,
        Left = 80,
        Top = 80,
        Width = 320,
        Height = 200,
    };
    var log = DispatchProxy.Create<IPluginLog, NoOpPluginLogProxy>();
    var instance = Activator.CreateInstance(
                       formType,
                       BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                       binder: null,
                       [
                           new Uri(pagePath),
                           Path.Combine(testRoot, "webview2-input-smoke"),
                           loaderPath,
                           "HTML Input Proxy Smoke",
                           true,
                           settings,
                           new System.Drawing.Size(320, 200),
                           false,
                           log,
                       ],
                       culture: null)
                   ?? throw new InvalidOperationException("HTML overlay input smoke form was not created.");
    var webViewField = formType.GetField("webView", BindingFlags.Instance | BindingFlags.NonPublic)
                       ?? throw new InvalidOperationException("HTML overlay WebView field was not found.");
    var formField = formType.GetField("form", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("HTML overlay host form field was not found.");
    var proxyField = formType.GetField("inputProxy", BindingFlags.Instance | BindingFlags.NonPublic)
                     ?? throw new InvalidOperationException("HTML overlay input proxy field was not found.");
    var show = formType.GetMethod("Show", BindingFlags.Instance | BindingFlags.Public)
               ?? throw new InvalidOperationException("HTML overlay show method was not found.");
    var applySettings = formType.GetMethod("ApplySettings", BindingFlags.Instance | BindingFlags.Public)
                        ?? throw new InvalidOperationException(
                            "HTML overlay ApplySettings method was not found.");

    NativeInputProbe.GetCursorPos(out var originalCursor);
    try
    {
        show.Invoke(instance, null);
        Control? webView = null;
        Form? hostForm = null;
        Form? proxy = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            webView = webViewField.GetValue(instance) as Control;
            hostForm = formField.GetValue(instance) as Form;
            proxy = proxyField.GetValue(instance) as Form;
            if (webView?.IsHandleCreated == true && hostForm?.IsHandleCreated == true &&
                proxy?.IsHandleCreated == true &&
                await ExecuteBrowserScriptAsync(webView, "document.readyState") == "\"complete\"")
            {
                break;
            }

            await Task.Delay(100);
        }

        Assert(
            webView is not null && hostForm is not null && proxy is not null,
            "The live HTML input smoke did not create its windows.");
        var liveWebView = webView!;
        var liveHostForm = hostForm!;
        var liveProxy = proxy!;
        var proxyHandle = await InvokeControlAsync(liveProxy, () => liveProxy.Handle);
        var hostHandle = await InvokeControlAsync(liveHostForm, () => liveHostForm.Handle);
        var hostStyle = NativeInputProbe.GetWindowLongPtr(hostHandle, NativeInputProbe.GwlExStyle);
        var proxyStyle = NativeInputProbe.GetWindowLongPtr(proxyHandle, NativeInputProbe.GwlExStyle);
        Assert(
            (hostStyle & (nint)0x00000080) != nint.Zero &&
            (hostStyle & (nint)0x00040000) == nint.Zero &&
            (proxyStyle & (nint)0x00000080) != nint.Zero &&
            (proxyStyle & (nint)0x00040000) == nint.Zero,
            "The live HTML overlay host or input proxy is still eligible for Alt+Tab or Task View.");
        var initialRegionReady = false;
        deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var contentWindow = await WindowAtProxyPointAsync(
                liveProxy,
                new System.Drawing.Point(100, 65));
            var blankWindow = await WindowAtProxyPointAsync(
                liveProxy,
                new System.Drawing.Point(280, 160));
            initialRegionReady = contentWindow == proxyHandle &&
                                 blankWindow != proxyHandle &&
                                 blankWindow != hostHandle;
            if (initialRegionReady)
            {
                break;
            }

            await Task.Delay(100);
        }

        Assert(
            initialRegionReady,
            "The live HTML input proxy did not exclude transparent space outside visible content.");
        var clickPoint = await InvokeControlAsync(
            liveProxy,
            () => liveProxy.PointToScreen(new System.Drawing.Point(100, 65)));
        NativeInputProbe.SetCursorPos(clickPoint.X, clickPoint.Y);
        NativeInputProbe.MouseEvent(NativeInputProbe.LeftDown, 0, 0, 0, UIntPtr.Zero);
        await Task.Delay(80);
        NativeInputProbe.MouseEvent(NativeInputProbe.LeftUp, 0, 0, 0, UIntPtr.Zero);

        var clicked = false;
        deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            clicked = await ExecuteBrowserScriptAsync(
                          liveWebView,
                          "document.documentElement.dataset.clicked || ''") == "\"true\"";
            if (clicked)
            {
                break;
            }

            await Task.Delay(100);
        }

        Assert(clicked, "A physical proxy click did not reach the live WebView2 button.");

        await ExecuteBrowserScriptAsync(liveWebView, "window.collapseProbe(); true");
        var collapsedRegionReady = false;
        deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var collapsedContentWindow = await WindowAtProxyPointAsync(
                liveProxy,
                new System.Drawing.Point(40, 30));
            var releasedWindow = await WindowAtProxyPointAsync(
                liveProxy,
                new System.Drawing.Point(100, 65));
            collapsedRegionReady = collapsedContentWindow == proxyHandle &&
                                   releasedWindow != proxyHandle &&
                                   releasedWindow != hostHandle;
            if (collapsedRegionReady)
            {
                break;
            }

            await Task.Delay(100);
        }

        Assert(
            collapsedRegionReady,
            "The live HTML input proxy did not shrink after the visible page content collapsed.");

        settings.SetEditing(true);
        applySettings.Invoke(instance, null);
        var editingUsesFullRegion = false;
        deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            editingUsesFullRegion =
                await WindowAtProxyPointAsync(liveProxy, new System.Drawing.Point(280, 160)) ==
                proxyHandle;
            if (editingUsesFullRegion)
            {
                break;
            }

            await Task.Delay(100);
        }

        Assert(
            editingUsesFullRegion,
            "HTML overlay editing mode did not restore the full drag and resize region.");

        settings.SetEditing(false);
        applySettings.Invoke(instance, null);
        var lockedRestoresContentRegion = false;
        deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var lockedWindow = await WindowAtProxyPointAsync(
                liveProxy,
                new System.Drawing.Point(100, 65));
            lockedRestoresContentRegion = lockedWindow != proxyHandle &&
                                          lockedWindow != hostHandle;
            if (lockedRestoresContentRegion)
            {
                break;
            }

            await Task.Delay(100);
        }

        Assert(
            lockedRestoresContentRegion,
            "HTML overlay locking did not restore the collapsed content-shaped input region.");
    }
    finally
    {
        NativeInputProbe.SetCursorPos(originalCursor.X, originalCursor.Y);
        ((IDisposable)instance).Dispose();
    }
}

static async Task<nint> WindowAtProxyPointAsync(Form proxy, System.Drawing.Point clientPoint)
{
    var screenPoint = await InvokeControlAsync(proxy, () => proxy.PointToScreen(clientPoint));
    return NativeInputProbe.WindowFromPoint(new NativeInputProbe.Point
    {
        X = screenPoint.X,
        Y = screenPoint.Y,
    });
}

static Task<T> InvokeControlAsync<T>(Control control, Func<T> action)
{
    var completion = new TaskCompletionSource<T>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    control.BeginInvoke(() =>
    {
        try
        {
            completion.SetResult(action());
        }
        catch (Exception ex)
        {
            completion.SetException(ex);
        }
    });
    return completion.Task;
}

static Task<string> ExecuteBrowserScriptAsync(Control webView, string script)
{
    var completion = new TaskCompletionSource<string>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    webView.BeginInvoke(async () =>
    {
        try
        {
            var coreWebView = webView.GetType()
                .GetProperty("CoreWebView2", BindingFlags.Instance | BindingFlags.Public)
                ?.GetValue(webView);
            if (coreWebView is null)
            {
                completion.SetResult(string.Empty);
                return;
            }

            var executeScript = coreWebView.GetType().GetMethod(
                                    "ExecuteScriptAsync",
                                    BindingFlags.Instance | BindingFlags.Public,
                                    binder: null,
                                    [typeof(string)],
                                    modifiers: null)
                                ?? throw new InvalidOperationException(
                                    "WebView2 ExecuteScriptAsync was not found.");
            var result = executeScript.Invoke(coreWebView, [script]) as Task<string>
                         ?? throw new InvalidOperationException(
                             "WebView2 ExecuteScriptAsync did not return a string task.");
            completion.SetResult(await result);
        }
        catch (Exception ex)
        {
            completion.SetException(ex);
        }
    });
    return completion.Task;
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

    var guardType = typeof(CactbotEventSource).Assembly.GetType(
                        "RainbowMage.OverlayPlugin.EventSources.CactbotTtsDuplicateGuard",
                        throwOnError: true)!;
    var guard = Activator.CreateInstance(
                    guardType,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    binder: null,
                    [TimeSpan.FromMilliseconds(500)],
                    culture: null)
                ?? throw new InvalidOperationException("Cactbot TTS duplicate guard was not created.");
    var tryAccept = guardType.GetMethod(
                        "TryAccept",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("Cactbot TTS duplicate guard has no acceptance method.");
    var start = Stopwatch.GetTimestamp();
    Assert(
        tryAccept.Invoke(guard, ["Stack marker", start]) as bool? == true &&
        tryAccept.Invoke(guard, ["Stack marker", start + Stopwatch.Frequency / 10]) as bool? == false &&
        tryAccept.Invoke(guard, ["Spread marker", start + Stopwatch.Frequency / 5]) as bool? == true &&
        tryAccept.Invoke(guard, ["Stack marker", start + Stopwatch.Frequency]) as bool? == true,
        "Two Cactbot windows no longer collapse the same simultaneous TTS request to one playback.");
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
    Assert(
        FindArchiveEntry(archive, "host/dnlib.dll") is not null,
        "The mixed-mode GreyMagic compatibility rewriter is missing from the Host package.");
    Assert(
        FindArchiveEntry(archive, "LICENSES/dnlib-MIT.txt") is not null,
        "The dnlib MIT license is missing from the release package.");

    foreach (var style in new[] { "Minimal", "Classic", "Flat" })
    {
        foreach (var job in JobDisplayFormatter.SupportedJobCodes)
        {
            Assert(
                FindArchiveEntry(archive, $"Assets/JobIcons/{style}/{job}.png") is not null,
                $"Release package is missing the {style} {job} job icon.");
        }
    }

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
            new ActCombatantSnapshot(
                "local", "You", "SAM", true, 120_000, 2_000, 0,
                13_000, 12_000, 12_000,
                DamageHits: 40, CriticalHits: 12, CriticalDirectHits: 4),
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
    Assert(
        local.DamageHits == 40 && local.CriticalHits == 12 && local.CriticalDirectHits == 4,
        "ACT critical and critical-direct hit counts were not mapped.");
    var early = encounter.Combatants.Single(static combatant => combatant.Name == "Early Pull");
    Assert(early.Dps == 0 && early.EncDps == 0 && early.ExtDps == 0,
        "Non-finite ACT rates were not normalized before persistence.");
    Assert(
        !string.IsNullOrWhiteSpace(JsonSerializer.Serialize(encounter)),
        "A mapped ACT encounter with early-pull rates could not be serialized.");

    var legacyCombatant = JsonSerializer.Deserialize<Combatant>(
        """
        {
          "Id": "legacy",
          "Name": "Legacy Player",
          "Job": "PLD",
          "IsLocalPlayer": true,
          "TotalDamage": 1,
          "TotalHealing": 0,
          "Deaths": 0,
          "Dps": 1,
          "EncDps": 1,
          "ExtDps": 1
        }
        """);
    Assert(
        legacyCombatant is { DamageHits: 0, CriticalHits: 0, CriticalDirectHits: 0 },
        "Legacy combatant JSON without hit-count fields is no longer compatible.");
}

static void ValidateChineseCombatChatParsing()
{
    Assert(
        ChineseCombatChatParser.TryExtractActor(
            "附近玩家发动了技能。",
            out var announcedActor) &&
        announcedActor == "附近玩家",
        "Chinese split combat chat did not update its announced actor before the damage line.");
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

    var context = new ChineseCombatChatContext();
    var observedAt = DateTimeOffset.UtcNow;
    Assert(
        !context.TryParse(
            "本地玩家发动了技能。",
            observedAt,
            out _,
            out _,
            out _),
        "A split combat actor announcement was mistaken for a complete damage line.");
    Assert(
        !context.TryParse(
            "本地玩家向附近玩家挥了挥手。",
            observedAt.AddMilliseconds(50),
            out _,
            out _,
            out _),
        "A player emote was mistaken for combat damage.");
    Assert(
        !context.TryParse(
            "  \uE06F 附近玩家受到了900点伤害。",
            observedAt.AddMilliseconds(100),
            out _,
            out _,
            out _),
        "Damage after an unrelated emote inherited the local player's stale attacker context.");
    Assert(
        !context.TryParse(
            "队友发动了技能。",
            observedAt.AddSeconds(1),
            out _,
            out _,
            out _) &&
        context.TryParse(
            "  \uE06F 木人受到了1200点伤害。",
            observedAt.AddSeconds(1.1),
            out var contextualActor,
            out _,
            out _) &&
        contextualActor == "队友",
        "Adjacent split combat lines no longer retain their legitimate attacker context.");

    var limitBreakContext = new ChineseCombatChatContext(
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "终结时刻" });
    Assert(
        !limitBreakContext.TryParse(
            "埃斯蒂尼安丿发动了“终结时刻”。",
            observedAt,
            out _,
            out _,
            out _) &&
        limitBreakContext.TryParse(
            "  \uE06F 木人受到了936686点伤害。",
            observedAt.AddMilliseconds(10),
            out var limitBreakActor,
            out _,
            out var limitBreakDamage) &&
        limitBreakActor == ChineseCombatChatContext.LimitBreakActorName &&
        limitBreakDamage == 936686,
        "Chinese Limit Break damage was attributed to the player instead of the synthetic LB combatant.");

    var snapshotTime = DateTimeOffset.UtcNow;
    var emptyActSnapshot = new ActEncounterSnapshot(
        Guid.NewGuid(),
        snapshotTime,
        snapshotTime,
        "Middle La Noscea",
        "木人",
        [new ActCombatantSnapshot("player", "Player", "PLD", true, 0, 0, 0)]);
    var chatFallbackSnapshot = new ActEncounterSnapshot(
        emptyActSnapshot.Id,
        snapshotTime,
        snapshotTime,
        "Middle La Noscea",
        "木人",
        [new ActCombatantSnapshot("player", "Player", "PLD", true, 936686, 0, 0)]);
    Assert(
        SelfHostedActRuntime.ShouldPreferChatFallback(emptyActSnapshot, chatFallbackSnapshot) &&
        !SelfHostedActRuntime.ShouldPreferChatFallback(chatFallbackSnapshot, emptyActSnapshot),
        "A zero-damage ACT completion snapshot can still overwrite a valid chat fallback snapshot.");
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

internal sealed class TestParserEngine : IParserEngine
{
    public TestParserEngine(ParserState initialState)
    {
        Status = new ParserStatus(initialState, initialState.ToString(), DateTimeOffset.UtcNow);
    }

    public event EventHandler<ParserStatus>? StatusChanged;

    public ParserStatus Status { get; private set; }

    public int StartCount { get; private set; }

    public int StopCount { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StartCount++;
        SetStatus(ParserState.Running);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StopCount++;
        SetStatus(ParserState.Stopped);
        return Task.CompletedTask;
    }

    public async Task RestartAsync(CancellationToken cancellationToken)
    {
        await StopAsync(cancellationToken);
        await StartAsync(cancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private void SetStatus(ParserState state)
    {
        Status = new ParserStatus(state, state.ToString(), DateTimeOffset.UtcNow);
        StatusChanged?.Invoke(this, Status);
    }
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

public class NoOpDataManagerProxy : DispatchProxy
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

internal static class NativeInputProbe
{
    public const int GwlExStyle = -20;
    public const uint LeftDown = 0x0002;
    public const uint LeftUp = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    public static extern nint WindowFromPoint(Point point);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern nint GetWindowLongPtr(nint windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "mouse_event")]
    public static extern void MouseEvent(
        uint flags,
        uint dx,
        uint dy,
        uint data,
        UIntPtr extraInfo);
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

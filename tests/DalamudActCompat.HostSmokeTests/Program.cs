using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Reflection;
using System.Runtime.Loader;
using System.Windows.Forms;
using System.Xml.Linq;
using Advanced_Combat_Tracker;
using DalamudActCompat.Host;
using DalamudActCompat.Protocol;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Newtonsoft.Json.Linq;

var focusedProbe = args.Length == 4 ? args[0] : null;
var entityTimingProbe = string.Equals(
    focusedProbe,
    "--probe-triggernometry-entity-timing",
    StringComparison.Ordinal);
var mapEffectProbe = string.Equals(
    focusedProbe,
    "--probe-triggernometry-mapeffect",
    StringComparison.Ordinal);
var effectiveArgs = entityTimingProbe || mapEffectProbe ? args[1..] : args;
if (effectiveArgs.Length is not (1 or 2 or 3))
{
    throw new ArgumentException(
        "Pass Host.exe and optionally <triggernometry.dll>, or <plugin-root> <config-root>. " +
        "Use --probe-triggernometry-entity-timing or --probe-triggernometry-mapeffect " +
        "with the three-path form for a focused probe.");
}

var hostExecutable = Path.GetFullPath(effectiveArgs[0]);
var processLogs = new ConcurrentDictionary<int, ProcessLog>();
var pluginRoot = effectiveArgs.Length == 3 ? Path.GetFullPath(effectiveArgs[1]) : null;
var configRoot = effectiveArgs.Length == 3 ? Path.GetFullPath(effectiveArgs[2]) : null;
var triggernometryAssembly = effectiveArgs.Length == 2
    ? Path.GetFullPath(effectiveArgs[1])
    : pluginRoot is null
        ? null
        : Path.Combine(pluginRoot, "triggernometry", "Triggernometry.dll");
var postNamazuSmokePort = 0;
if (!File.Exists(hostExecutable))
{
    throw new FileNotFoundException("ACT Host executable was not found.", hostExecutable);
}

await ValidateAuthorizedTtsCadenceAsync();
ValidateFfxivEntityDeltaRepository();
ValidateFfxivRegionContext();

if (entityTimingProbe)
{
    if (pluginRoot is null || configRoot is null)
    {
        throw new ArgumentException(
            "The entity timing probe requires Host.exe, <plugin-root>, and <config-root>.");
    }

    await ValidateTriggernometryEntityTimingProbeAsync();
    return;
}

if (mapEffectProbe)
{
    if (pluginRoot is null || configRoot is null)
    {
        throw new ArgumentException(
            "The MapEffect probe requires Host.exe, <plugin-root>, and <config-root>.");
    }

    await ValidateTriggernometryMapEffectProbeAsync();
    return;
}

await ValidateHandshakeCommandBoundaryAndShutdownAsync();
ValidateForegroundNotificationRouting();
ValidateFoxTtsDefaultConfiguration();
ValidateTriggernometryConfigurationRecovery();
await ValidateSequenceRegressionTerminatesHostAsync();
await ValidateExpiredMessageIsDroppedAsync();
await ValidateHostCrashBreaksOnlyPipeAsync();
await ValidateDedicatedHostCrashDoesNotAffectSharedHostAsync();
await ValidateAbruptClientDisconnectAsync();
await ValidateBlockedReaderRemainsOutOfProcessAsync();
await ValidateGenericPluginLoadsOnlyAfterConsentAsync();
ValidateLargePostNamazuCopyReturnsQuickly();
ValidatePostNamazuNativeProcessPermissionGate();
ValidatePostNamazuMarkPayloadNormalization();
ValidatePictoActActorRemovalExtraction();
ValidatePostNamazuQueueBreakAllCompatibility();
await ValidatePostNamazuQueueExecutionLifecycleAsync();
ValidatePostNamazu1366And1367SurfaceCompatibility();
ValidateOverlayPluginCompatibilityTypeName();
ValidateTriggernometryCompatibilityNoticeFilter();
ValidateTriggernometryWebAddressLaunchUsesShell();
ValidateTriggernometryPlaceholderProcessTestIsSkipped();
ValidateTriggernometryU6bCompatibilityPatch();
ValidateTriggernometryAndPostNamazuPermissionGates();
if (triggernometryAssembly is not null)
{
    ValidateTriggernometryAssemblyRewrite(triggernometryAssembly);
}
var silverDasherRoot = Environment.GetEnvironmentVariable("ACTCOMPAT_SILVERDASHER_ROOT");
if (!string.IsNullOrWhiteSpace(silverDasherRoot) && Directory.Exists(silverDasherRoot))
{
    ValidateSilverDasherAssemblyRewrite(silverDasherRoot);
    await ValidateSilverDasherLoadsOutOfProcessAsync(silverDasherRoot);
}
var matchaPackage = Path.Combine(
    FindProjectRoot(),
    "vendor",
    "BundledActPlugins",
    "matcha",
    "Cafe.Matcha-26.8.12.1622-dact3.zip");
if (File.Exists(matchaPackage))
{
    ValidateMatchaAssemblyContract(matchaPackage);
    await ValidateMatchaLoadsOutOfProcessAsync(matchaPackage);
}
if (pluginRoot is not null && configRoot is not null)
{
    await ValidateLegacyPluginsLoadOutOfProcessAsync();
}
await ValidatePostNamazuQueueShutdownLifecycleAsync();
var completion =
    "Host handshake, sequence/deadline validation, bounded IPC, command denial, " +
    "crash isolation, disconnect, and 100k-line PostNamazu copy tests passed.";
if (triggernometryAssembly is not null)
{
    completion += " The real Triggernometry assembly rewrite test passed.";
}
if (!string.IsNullOrWhiteSpace(silverDasherRoot))
{
    completion += " The original SilverDasher loader/core rewrite test passed without changing its DLLs.";
}
if (File.Exists(matchaPackage))
{
    completion += " The hash-pinned Matcha source bridge, real dedicated-Host load, bidirectional network, notification, and unload tests passed.";
}
if (pluginRoot is not null)
{
    completion +=
        " Real Triggernometry log/network/zone/combat/TTS/_me entity and ACT-legacy " +
        "log paths, plus PostNamazu mark/waymark/preset/sendkey/original queue/PictoACT " +
        "closed-loop tests passed.";
}
Console.WriteLine(completion);

void ValidateFfxivRegionContext()
{
    var repository = new FfxivDataRepository();
    repository.SetGameContext(new HostGameContext(
        HostGameRegion.Global,
        HostClientLanguage.Japanese));
    Assert(
        repository.GetGameRegion() == (byte)HostGameRegion.Global &&
        repository.GetSelectedLanguageID() == FFXIV_ACT_Plugin.Common.Language.Japanese,
        "The independent ACT Host did not expose the selected Global region and client language.");

    repository.SetGameContext(new HostGameContext(
        HostGameRegion.Chinese,
        HostClientLanguage.Chinese));
    Assert(
        repository.GetGameRegion() == (byte)HostGameRegion.Chinese &&
        repository.GetSelectedLanguageID() == FFXIV_ACT_Plugin.Common.Language.Chinese,
        "The independent ACT Host did not switch back to the Chinese region and language.");
}

void ValidateForegroundNotificationRouting()
{
    var detector = typeof(HostPluginBridge).Assembly.GetType(
                       "DalamudActCompat.Host.GameForegroundDetector")
                   ?? throw new TypeLoadException("Game foreground detector was not found.");
    var isForegroundProcess = detector.GetMethod(
                                  "IsForegroundProcess",
                                  BindingFlags.Static | BindingFlags.NonPublic)
                              ?? throw new MissingMethodException(
                                  detector.FullName,
                                  "IsForegroundProcess");
    bool Invoke(int gameProcessId, int foregroundProcessId)
        => isForegroundProcess.Invoke(null, [gameProcessId, foregroundProcessId]) as bool? == true;
    Assert(
        Invoke(4242, 4242) &&
        !Invoke(4242, 5252) &&
        !Invoke(0, 0),
        "Notification routing no longer selects the Dalamud channel only when the game process owns the foreground window.");
}

void ValidateFoxTtsDefaultConfiguration()
{
    var temporaryRoot = Path.Combine(
        Path.GetTempPath(),
        $"DalamudActCompat-FoxTts-{Guid.NewGuid():N}");
    try
    {
        if (!FoxTtsConfigurationDefaults.Ensure(temporaryRoot))
        {
            throw new InvalidOperationException("FoxTTS default configuration was not created.");
        }

        var configurationPath = Path.Combine(
            temporaryRoot,
            "Config",
            "ACT.FoxTTS.config.xml");
        var document = XDocument.Load(configurationPath);
        var engine = document.Descendants("TTSEngine").SingleOrDefault()?.Value;
        if (!string.Equals(engine, FoxTtsConfigurationDefaults.DefaultEngine, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("FoxTTS did not default to Cafe TTS Pro.");
        }

        document.Descendants("TTSEngine").Single().Value = "ttsEngineSAPI5";
        document.Save(configurationPath);
        if (FoxTtsConfigurationDefaults.Ensure(temporaryRoot))
        {
            throw new InvalidOperationException("Existing FoxTTS configuration was overwritten.");
        }

        document = XDocument.Load(configurationPath);
        engine = document.Descendants("TTSEngine").Single().Value;
        if (!string.Equals(engine, "ttsEngineSAPI5", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Existing FoxTTS engine selection was not preserved.");
        }

        if (FoxTtsConfigurationDefaults.IsPro(temporaryRoot))
        {
            throw new InvalidOperationException("FoxTTS SAPI5 was incorrectly detected as Cafe TTS Pro.");
        }
        if (!FoxTtsConfigurationDefaults.SetPro(temporaryRoot) ||
            !FoxTtsConfigurationDefaults.IsPro(temporaryRoot))
        {
            throw new InvalidOperationException("An existing FoxTTS engine was not switched to Cafe TTS Pro.");
        }
        if (FoxTtsConfigurationDefaults.SetPro(temporaryRoot))
        {
            throw new InvalidOperationException("Cafe TTS Pro was rewritten even though it was already selected.");
        }

        document = XDocument.Load(configurationPath);
        if (document.Descendants("PluginIntegration").SingleOrDefault()?.Value != "Auto")
        {
            throw new InvalidOperationException("Switching FoxTTS engines overwrote unrelated settings.");
        }

        var mismatchedEncodingXml =
            "<?xml version=\"1.0\" encoding=\"utf-16\" standalone=\"yes\"?>\r\n" +
            "<Config><SettingsSerializer>" +
            "<TTSEngine>ttsEngineCafe</TTSEngine>" +
            "<PluginIntegration>Manual</PluginIntegration>" +
            "</SettingsSerializer></Config>";
        File.WriteAllText(
            configurationPath,
            mismatchedEncodingXml,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        if (FoxTtsConfigurationDefaults.IsPro(temporaryRoot))
        {
            throw new InvalidOperationException(
                "A UTF-8 FoxTTS file with a stale UTF-16 declaration was incorrectly detected as Pro.");
        }
        if (!FoxTtsConfigurationDefaults.SetPro(temporaryRoot) ||
            !FoxTtsConfigurationDefaults.IsPro(temporaryRoot))
        {
            throw new InvalidOperationException(
                "A UTF-8 FoxTTS file with a stale UTF-16 declaration could not be switched to Pro.");
        }

        document = XDocument.Load(configurationPath);
        if (document.Descendants("PluginIntegration").SingleOrDefault()?.Value != "Manual")
        {
            throw new InvalidOperationException(
                "Repairing a stale FoxTTS encoding declaration overwrote unrelated settings.");
        }
    }
    finally
    {
        if (Directory.Exists(temporaryRoot))
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }
}

void ValidateTriggernometryConfigurationRecovery()
{
    var temporaryRoot = Path.Combine(
        Path.GetTempPath(),
        $"DalamudActCompat-Triggernometry-Recovery-{Guid.NewGuid():N}");
    try
    {
        var configurationDirectory = Path.Combine(temporaryRoot, "Config");
        Directory.CreateDirectory(configurationDirectory);
        var configurationPath = Path.Combine(
            configurationDirectory,
            "Triggernometry.config.xml");
        var previousPath = configurationPath + ".previous";
        File.WriteAllText(configurationPath, "<Configuration><Root>");
        File.WriteAllText(
            previousPath,
            "<?xml version=\"1.0\"?><Configuration><Root /></Configuration>");

        var recoveryType = typeof(HostPluginBridge).Assembly.GetType(
                               "DalamudActCompat.Host.TriggernometryConfigurationRecovery")
                           ?? throw new TypeLoadException(
                               "Triggernometry configuration recovery was not found.");
        var recover = recoveryType.GetMethod(
                          "TryRecover",
                          BindingFlags.Static | BindingFlags.NonPublic)
                      ?? throw new MissingMethodException(recoveryType.FullName, "TryRecover");
        var result = recover.Invoke(null, [temporaryRoot]) as string;
        Assert(
            result?.Contains("Recovered", StringComparison.Ordinal) == true &&
            XDocument.Load(configurationPath).Root?.Name.LocalName == "Configuration" &&
            File.Exists(previousPath) &&
            Directory.EnumerateFiles(
                    configurationDirectory,
                    "Triggernometry.config.xml.corrupt-*.xml")
                .Count() == 1,
            "Triggernometry did not atomically restore a valid previous configuration while preserving the damaged bytes.");

        Assert(
            recover.Invoke(null, [temporaryRoot]) is null,
            "Triggernometry recovery rewrote an already valid primary configuration.");
    }
    finally
    {
        if (Directory.Exists(temporaryRoot))
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }
}

void ValidateLargePostNamazuCopyReturnsQuickly()
{
    Environment.SetEnvironmentVariable("ACTCOMPAT_ENABLE_TEST_HOOKS", "1");
    var configurePermissions = typeof(HostPluginBridge).GetMethod(
                                   "ConfigurePermissions",
                                   BindingFlags.Static | BindingFlags.NonPublic)
                               ?? throw new MissingMethodException(
                                   typeof(HostPluginBridge).FullName,
                                   "ConfigurePermissions");
    configurePermissions.Invoke(
        null,
        [
            new HostPermissionSnapshot(
                new Dictionary<string, IReadOnlyList<string>>
                {
                    ["postnamazu"] = ["Clipboard"],
                },
                ["postnamazu"]),
        ]);
    var clipboardCompletion = new TaskCompletionSource<int>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var configureClipboardWriter = typeof(HostPluginBridge).GetMethod(
                                       "ConfigureClipboardWriterForTests",
                                       BindingFlags.Static | BindingFlags.NonPublic)
                                   ?? throw new MissingMethodException(
                                       typeof(HostPluginBridge).FullName,
                                       "ConfigureClipboardWriterForTests");
    configureClipboardWriter.Invoke(
        null,
        [(Action<string>)(text => clipboardCompletion.TrySetResult(text.Length))]);

    Exception? failure = null;
    var elapsed = TimeSpan.Zero;
    var thread = new Thread(() =>
    {
        try
        {
            using var list = new ListBox();
            list.BeginUpdate();
            for (var index = 0; index < 100_000; index++)
            {
                list.Items.Add($"PostNamazu smoke log {index:D6}");
            }
            list.EndUpdate();
            var stopwatch = Stopwatch.StartNew();
            HostPluginBridge.CopyPostNamazuLog(list, copyAll: true);
            stopwatch.Stop();
            elapsed = stopwatch.Elapsed;
        }
        catch (Exception ex)
        {
            failure = ex;
        }
    })
    {
        IsBackground = true,
        Name = "PostNamazu 100k copy smoke",
    };
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    Assert(
        thread.Join(TimeSpan.FromSeconds(10)),
        "PostNamazu 100k-line copy adapter did not return within ten seconds.");
    if (failure is not null)
    {
        throw new InvalidOperationException(
            "PostNamazu 100k-line copy adapter failed.",
            failure);
    }

    Assert(
        elapsed < TimeSpan.FromSeconds(1),
        $"PostNamazu clipboard enqueue blocked its UI path for {elapsed.TotalMilliseconds:0} ms.");
    Assert(
        clipboardCompletion.Task.Wait(TimeSpan.FromSeconds(5)),
        "PostNamazu 100k-line background text assembly did not complete within five seconds.");
    Assert(
        clipboardCompletion.Task.Result > 2_000_000,
        "PostNamazu 100k-line background clipboard payload was unexpectedly truncated.");
    Environment.SetEnvironmentVariable("ACTCOMPAT_ENABLE_TEST_HOOKS", null);
}

void ValidatePostNamazuNativeProcessPermissionGate()
{
    var configurePermissions = typeof(HostPluginBridge).GetMethod(
                                   "ConfigurePermissions",
                                   BindingFlags.Static | BindingFlags.NonPublic)
                               ?? throw new MissingMethodException(
                                   typeof(HostPluginBridge).FullName,
                                   "ConfigurePermissions");
    var configureGameProcess = typeof(HostPluginBridge).GetMethod(
                                   "ConfigureGameProcess",
                                   BindingFlags.Static | BindingFlags.NonPublic)
                               ?? throw new MissingMethodException(
                                   typeof(HostPluginBridge).FullName,
                                   "ConfigureGameProcess");
    var repositoryProperty = typeof(HostPluginBridge).GetProperty(
                                 "FfxivRepository",
                                 BindingFlags.Static | BindingFlags.NonPublic)
                             ?? throw new MissingMemberException(
                                 typeof(HostPluginBridge).FullName,
                                 "FfxivRepository");
    var repository = repositoryProperty.GetValue(null)
                     ?? throw new InvalidOperationException(
                         "Host FFXIV repository is unavailable.");
    var getProcess = repository.GetType().GetMethod("GetCurrentFFXIVProcess")
                     ?? throw new MissingMethodException(
                         repository.GetType().FullName,
                         "GetCurrentFFXIVProcess");

    configureGameProcess.Invoke(null, [Environment.ProcessId]);
    configurePermissions.Invoke(
        null,
        [
            new HostPermissionSnapshot(
                new Dictionary<string, IReadOnlyList<string>>
                {
                    ["postnamazu"] = ["GameCommand", "NativeGameMemory"],
                    ["silverdasher"] = ["NativeGameMemory"],
                },
                ["postnamazu", "silverdasher"]),
        ]);
    var allowed = (Process?)getProcess.Invoke(repository, null);
    using var silverDasherProcess = HostPluginBridge.GetSilverDasherGameProcess();
    Assert(
        allowed?.Id == Environment.ProcessId,
        "PostNamazu full permission did not expose the exact game process to its original runtime.");
    Assert(
        silverDasherProcess?.Id == Environment.ProcessId &&
        !ReferenceEquals(allowed, silverDasherProcess),
        "SilverDasher did not receive a plugin-scoped Process facade independent from PostNamazu.");

    configurePermissions.Invoke(
        null,
        [
            new HostPermissionSnapshot(
                new Dictionary<string, IReadOnlyList<string>>
                {
                    ["postnamazu"] = ["GameCommand"],
                },
                ["postnamazu"]),
        ]);
    Assert(
        getProcess.Invoke(repository, null) is null,
        "PostNamazu native process remained visible after NativeGameMemory permission was revoked.");
}

void ValidatePostNamazuQueueBreakAllCompatibility()
{
    var queueIds = (List<string>?)typeof(HostPluginBridge)
                       .GetField(
                           "PostNamazuQueueIds",
                           BindingFlags.Static | BindingFlags.NonPublic)
                       ?.GetValue(null)
                   ?? throw new MissingFieldException(
                       typeof(HostPluginBridge).FullName,
                       "PostNamazuQueueIds");
    var queueLock = typeof(HostPluginBridge).GetField(
                        "PostNamazuQueueLock",
                        BindingFlags.Static | BindingFlags.NonPublic)
                    ?.GetValue(null)
                    ?? throw new MissingFieldException(
                        typeof(HostPluginBridge).FullName,
                        "PostNamazuQueueLock");
    lock (queueLock)
    {
        queueIds.Clear();
        queueIds.AddRange(["ACTCOMPAT_QUEUE_ONE", "ACTCOMPAT_QUEUE_TWO"]);
    }

    HostPluginBridge.BreakPostNamazuQueue("ALL");
    Assert(
        queueIds.Count == 0,
        "PostNamazu stop=all did not preserve the original clear-all queue behavior.");
}

async Task ValidatePostNamazuQueueShutdownLifecycleAsync()
{
    var bridgeType = typeof(HostPluginBridge);
    var configurePermissions = bridgeType.GetMethod(
                                   "ConfigurePermissions",
                                   BindingFlags.Static | BindingFlags.NonPublic)
                               ?? throw new MissingMethodException(
                                   bridgeType.FullName,
                                   "ConfigurePermissions");
    configurePermissions.Invoke(
        null,
        [
            new HostPermissionSnapshot(
                new Dictionary<string, IReadOnlyList<string>>
                {
                    ["postnamazu"] = ["GameCommand"],
                },
                ["postnamazu"]),
        ]);

    BlockingPostNamazuPlugin.Reset();
    HostPluginBridge.SendPostNamazuQueue(
        new BlockingPostNamazuModule(),
        "[{\"C\":\"command\",\"P\":\"shutdown-test\",\"D\":0}]");
    var tasks = (HashSet<Task>?)bridgeType.GetField(
                    "PostNamazuQueueTasks",
                    BindingFlags.Static | BindingFlags.NonPublic)
                ?.GetValue(null)
                ?? throw new MissingFieldException(bridgeType.FullName, "PostNamazuQueueTasks");
    Assert(
        BlockingPostNamazuPlugin.Started.Wait(TimeSpan.FromSeconds(1)) &&
        SpinWait.SpinUntil(
            () =>
            {
                lock (tasks)
                {
                    return tasks.Count == 1;
                }
            },
            TimeSpan.FromSeconds(1)),
        "The compatibility bridge did not track its PostNamazu queue task.");

    var stop = bridgeType.GetMethod(
                   "StopPostNamazuQueuesAsync",
                   BindingFlags.Static | BindingFlags.NonPublic)
               ?? throw new MissingMethodException(
                   bridgeType.FullName,
                   "StopPostNamazuQueuesAsync");
    try
    {
        var stopwatch = Stopwatch.StartNew();
        var allTasksStopped = await ((Task<bool>?)stop.Invoke(null, null)
            ?? throw new InvalidOperationException("PostNamazu shutdown returned no task."));
        stopwatch.Stop();
        Assert(
            !allTasksStopped && stopwatch.Elapsed < TimeSpan.FromSeconds(3),
            "PostNamazu shutdown did not report a long synchronous action still using the plugin runtime.");

        var hostProgramSource = File.ReadAllText(Path.Combine(
            FindProjectRoot(), "src", "DalamudActCompat.Host", "Program.cs"));
        Assert(
            hostProgramSource.Contains("if (postNamazuQueuesStopped &&", StringComparison.Ordinal) &&
            hostProgramSource.Contains("pluginRuntime?.Dispose();", StringComparison.Ordinal),
            "Host shutdown can still dispose the plugin runtime while a PostNamazu action is executing.");
    }
    finally
    {
        BlockingPostNamazuPlugin.Release.Set();
    }

    Assert(
        SpinWait.SpinUntil(
            () =>
            {
                lock (tasks)
                {
                    return tasks.Count == 0;
                }
            },
            TimeSpan.FromSeconds(2)),
        "The long synchronous PostNamazu action remained tracked after it was released.");
}

void ValidatePostNamazuMarkPayloadNormalization()
{
    const string triggernometryPayload =
        "{\"ActorID\":\"0x10021EE7\",\"MarkType\":\"attack1\",\"LocalOnly\":true}";
    var normalized = HostPluginBridge.NormalizePostNamazuMarkPayload(triggernometryPayload);
    using var document = JsonDocument.Parse(normalized);
    Assert(
        document.RootElement.GetProperty("ActorID").GetUInt32() == 0x10021EE7,
        "Triggernometry's hexadecimal entity ID was not normalized for PostNamazu UInt32 deserialization.");

    const string numericPayload =
        "{\"ActorID\":3758096384,\"MarkType\":\"attack1\",\"LocalOnly\":true}";
    Assert(
        HostPluginBridge.NormalizePostNamazuMarkPayload(numericPayload) == numericPayload,
        "An already numeric PostNamazu ActorID payload was unexpectedly rewritten.");

    try
    {
        _ = HostPluginBridge.NormalizePostNamazuMarkPayload(
            "{\"ActorID\":\"0xNOT_HEX\",\"MarkType\":\"attack1\"}");
        throw new InvalidOperationException(
            "An invalid hexadecimal PostNamazu ActorID was accepted.");
    }
    catch (InvalidDataException)
    {
        // Expected: malformed ActorIDs must fail closed before entering the native runtime.
    }
}

void ValidatePictoActActorRemovalExtraction()
{
    var extracted = HostPluginBridge.ExtractPictoActActorRemovalCommands(
        "Action: Remove\nType: Static\nTag: STATIC\n---\n" +
        "Action: Remove\nType: ActorVfx\nTag: ACTOR\n---\n" +
        "Omen: Circle\nTag: CREATE\n---\n" +
        "Action: Remove\nRegex: ^ALL$");
    Assert(
        !extracted.Contains("STATIC", StringComparison.Ordinal) &&
        !extracted.Contains("CREATE", StringComparison.Ordinal) &&
        extracted.Contains("ACTOR", StringComparison.Ordinal) &&
        extracted.Contains("^ALL$", StringComparison.Ordinal) &&
        System.Text.RegularExpressions.Regex.Matches(
            extracted,
            "Action: Remove",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant).Count == 2,
        "PictoACT ActorVfx/all removal routing did not preserve the upstream-owned subset.");
}

void ValidateOverlayPluginCompatibilityTypeName()
{
    var facadeType = typeof(HostPluginBridge).Assembly.GetType("OverlayPlugin")
                     ?? throw new TypeLoadException(
                         "The ACT OverlayPlugin compatibility identity must be named exactly OverlayPlugin.");
    Assert(
        facadeType.FullName == "OverlayPlugin" &&
        typeof(Advanced_Combat_Tracker.IActPluginV1).IsAssignableFrom(facadeType),
        $"ACT OverlayPlugin compatibility identity was '{facadeType.FullName}'.");
}

void ValidatePostNamazu1366And1367SurfaceCompatibility()
{
    var rewriter = typeof(HostPluginBridge).Assembly.GetType(
                       "DalamudActCompat.Host.LegacyAssemblyRewriter")
                   ?? throw new TypeLoadException(
                       "DalamudActCompat.Host.LegacyAssemblyRewriter");
    var validate = rewriter.GetMethod(
                       "ValidatePostNamazuActionSurface",
                       BindingFlags.Static | BindingFlags.NonPublic)
                   ?? throw new MissingMethodException(
                       rewriter.FullName,
                       "ValidatePostNamazuActionSurface");
    string[] currentModules =
    [
        "PostNamazu.Actions.Command",
        "PostNamazu.Actions.Mark",
        "PostNamazu.Actions.Preset",
        "PostNamazu.Actions.Queue",
        "PostNamazu.Actions.SendKey",
        "PostNamazu.Actions.WayMark",
    ];
    string[] legacyModules =
    [
        .. currentModules,
        "PostNamazu.Actions.NormalCommand",
    ];
    string[] commonCommands =
    [
        "PostNamazu.Actions.Command.DoTextCommand:command",
        "PostNamazu.Actions.Command.DoTextCommand:DoTextCommand",
        "PostNamazu.Actions.Mark.DoMarking:mark",
        "PostNamazu.Actions.Preset.DoInsertPreset:preset",
        "PostNamazu.Actions.Preset.DoInsertPreset:DoInsertPreset",
        "PostNamazu.Actions.Queue.DoQueue:queue",
        "PostNamazu.Actions.Queue.DoQueue:DoQueueActions",
        "PostNamazu.Actions.Queue.BreakQueue:stop",
        "PostNamazu.Actions.Queue.BreakQueue:break",
        "PostNamazu.Actions.Queue.BreakQueue:BreakQueueActions",
        "PostNamazu.Actions.SendKey.DoSendKey:sendkey",
        "PostNamazu.Actions.WayMark.DoWaymarks:place",
        "PostNamazu.Actions.WayMark.DoWaymarks:DoWaymarks",
    ];
    string[] currentCommands =
    [
        .. commonCommands,
        "PostNamazu.Actions.Command.DoNormalTextCommand:normalcommand",
        "PostNamazu.Actions.Command.DoNormalTextCommand:DoNormalTextCommand",
    ];
    string[] legacyCommands =
    [
        .. commonCommands,
        "PostNamazu.Actions.NormalCommand.DoNormalTextCommand:normalcommand",
        "PostNamazu.Actions.NormalCommand.DoNormalTextCommand:DoNormalTextCommand",
    ];

    Invoke(currentModules, currentCommands);
    Invoke(legacyModules, legacyCommands);

    var mixedSurfaceRejected = false;
    try
    {
        Invoke(currentModules, legacyCommands);
    }
    catch (InvalidOperationException)
    {
        mixedSurfaceRejected = true;
    }

    Assert(
        mixedSurfaceRejected,
        "PostNamazu surface validation accepted mismatched 1.3.6.6/1.3.6.7 command owners.");

    void Invoke(string[] modules, string[] commands)
    {
        try
        {
            validate.Invoke(null, [modules, commands]);
        }
        catch (TargetInvocationException exception)
            when (exception.InnerException is InvalidOperationException inner)
        {
            throw inner;
        }
    }
}

void ValidateTriggernometryCompatibilityNoticeFilter()
{
    Assert(
        HostPluginBridge.CheckTriggernometryPostNamazuAdministratorRequirement(),
        "Triggernometry's PostNamazu compatibility-only administrator notice was not suppressed.");
    Assert(
        HostPluginBridge.IsExpectedTriggernometryCompatibilityNotice(
            "[鲶鱼精邮差扩展] 警告：ACT 未以管理员权限运行。如果遇到游戏崩溃，请尝试右键 ACT 程序 - 属性 - 兼容性，开启管理员身份运行。"),
        "Triggernometry's known compatibility-only administrator notice was not recognized.");
    Assert(
        !HostPluginBridge.IsExpectedTriggernometryCompatibilityNotice(
            "脚本启动失败：此操作需要管理员权限。"),
        "A real administrator-related failure was incorrectly suppressed.");
}

void ValidateTriggernometryWebAddressLaunchUsesShell()
{
    var prepare = typeof(HostPluginBridge).GetMethod(
                      "PrepareTriggernometryStartInfo",
                      BindingFlags.Static | BindingFlags.NonPublic)
                  ?? throw new MissingMethodException(
                      typeof(HostPluginBridge).FullName,
                      "PrepareTriggernometryStartInfo");
    var webStartInfo = new ProcessStartInfo(
        "https://space.bilibili.com/83429972/channel/collectiondetail?sid=2967544")
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
    };
    var normalizedWeb = (ProcessStartInfo?)prepare.Invoke(null, [webStartInfo])
                        ?? throw new InvalidOperationException(
                            "Triggernometry web launch normalization returned null.");
    Assert(
        normalizedWeb.UseShellExecute &&
        !normalizedWeb.RedirectStandardInput &&
        !normalizedWeb.RedirectStandardOutput &&
        !normalizedWeb.RedirectStandardError &&
        !normalizedWeb.CreateNoWindow,
        "Triggernometry web actions are not routed through the Windows browser shell.");

    var executableStartInfo = new ProcessStartInfo("notepad.exe")
    {
        UseShellExecute = false,
        CreateNoWindow = true,
    };
    var normalizedExecutable = (ProcessStartInfo?)prepare.Invoke(null, [executableStartInfo]);
    Assert(
        ReferenceEquals(executableStartInfo, normalizedExecutable) &&
        !executableStartInfo.UseShellExecute &&
        executableStartInfo.CreateNoWindow,
        "Triggernometry executable actions were unexpectedly changed by URL normalization.");
}

void ValidateTriggernometryPlaceholderProcessTestIsSkipped()
{
    var shouldSkip = typeof(HostPluginBridge).GetMethod(
                         "ShouldSkipTriggernometryPlaceholderProcess",
                         BindingFlags.Static | BindingFlags.NonPublic)
                     ?? throw new MissingMethodException(
                         typeof(HostPluginBridge).FullName,
                         "ShouldSkipTriggernometryPlaceholderProcess");
    Assert(
        (bool)shouldSkip.Invoke(null, [new ProcessStartInfo("test")])!,
        "Triggernometry's conventional placeholder process target was not skipped.");
    Assert(
        !(bool)shouldSkip.Invoke(
            null,
            [new ProcessStartInfo("test") { Arguments = "real-argument" }])!,
        "A process target with real arguments was mistaken for a placeholder test.");
    Assert(
        !(bool)shouldSkip.Invoke(
            null,
            [new ProcessStartInfo(
                "https://space.bilibili.com/83429972/channel/collectiondetail?sid=2967544")])!,
        "A real web target was mistaken for a placeholder test.");
}

void ValidateTriggernometryAndPostNamazuPermissionGates()
{
    var bridgeType = typeof(HostPluginBridge);
    var configurePermissions = bridgeType.GetMethod(
                                   "ConfigurePermissions",
                                   BindingFlags.Static | BindingFlags.NonPublic)
                               ?? throw new MissingMethodException(
                                   bridgeType.FullName,
                                   "ConfigurePermissions");
    configurePermissions.Invoke(
        null,
        [
            new HostPermissionSnapshot(
                new Dictionary<string, IReadOnlyList<string>>
                {
                    ["triggernometry"] = [],
                    ["postnamazu"] = [],
                },
                ["triggernometry", "postnamazu"]),
        ]);

    Assert(
        !HostPluginBridge.IsTriggernometryNetworkAllowed() &&
        !HostPluginBridge.IsTriggernometryHighRiskScriptAllowed(),
        "Triggernometry network or high-risk script execution bypassed a denied capability.");
    AssertThrows<UnauthorizedAccessException>(
        () => HostPluginBridge.StartTriggernometryProcess(new ProcessStartInfo("test")),
        "Triggernometry launched a process without LaunchExternalProcess permission.");
    AssertThrows<UnauthorizedAccessException>(
        () => HostPluginBridge.SendPostNamazuQueue(
            new RecordingPostNamazuModule(),
            "[{\"C\":\"command\",\"P\":\"denied\",\"D\":0}]"),
        "PostNamazu started a compatibility queue without GameCommand permission.");
    AssertThrows<UnauthorizedAccessException>(
        () => HostPluginBridge.SendPostNamazuSetHeading((IntPtr)0x12345678, 1.25f),
        "PostNamazu requested a player heading without NativeGameMemory permission.");

    var configureSender = bridgeType.GetMethod(
                              "Configure",
                              BindingFlags.Static | BindingFlags.NonPublic)
                          ?? throw new MissingMethodException(bridgeType.FullName, "Configure");
    string? capturedType = null;
    HostMessagePriority? capturedPriority = null;
    object? capturedPayload = null;
    var capturedHeadingCount = 0;
    Func<string, HostMessagePriority, object, string?, DateTimeOffset?, bool> capture =
        (type, priority, payload, _, _) =>
        {
            capturedType = type;
            capturedPriority = priority;
            capturedPayload = payload;
            Interlocked.Increment(ref capturedHeadingCount);
            return true;
        };
    configureSender.Invoke(null, [capture]);
    configurePermissions.Invoke(
        null,
        [
            new HostPermissionSnapshot(
                new Dictionary<string, IReadOnlyList<string>>
                {
                    ["postnamazu"] = ["NativeGameMemory"],
                },
                ["postnamazu"]),
        ]);
    HostPluginBridge.SendPostNamazuSetHeading((IntPtr)0x12345678, 1.25f);
    Assert(
        SpinWait.SpinUntil(
            () => capturedPayload is HostPostNamazuHeading,
            TimeSpan.FromSeconds(1)),
        "PostNamazu heading dispatcher did not flush its latest state.");
    var capturedHeading = capturedPayload as HostPostNamazuHeading;
    Assert(
        capturedType == HostMessageTypes.PostNamazuSetHeading &&
        capturedPriority == HostMessagePriority.State &&
        capturedHeading is
        {
            Address: 0x12345678,
            Heading: 1.25f,
        },
        "PostNamazu heading did not use the coalesced, typed state bridge.");

    Thread.Sleep(40);
    capturedPayload = null;
    Interlocked.Exchange(ref capturedHeadingCount, 0);
    for (var headingIndex = 0; headingIndex < 100; headingIndex++)
    {
        HostPluginBridge.SendPostNamazuSetHeading(
            (IntPtr)0x12345678,
            headingIndex);
    }

    Assert(
        SpinWait.SpinUntil(
            () => capturedPayload is HostPostNamazuHeading { Heading: 99f },
            TimeSpan.FromSeconds(1)) &&
        Volatile.Read(ref capturedHeadingCount) < 10,
        "PostNamazu's 10 ms U6b heading loop was not coalesced to the latest frame state.");

    configurePermissions.Invoke(
        null,
        [
            new HostPermissionSnapshot(
                new Dictionary<string, IReadOnlyList<string>>
                {
                    ["triggernometry"] = ["NetworkRequest", "HighRiskScript"],
                    ["postnamazu"] = ["GameCommand"],
                },
                ["triggernometry", "postnamazu"]),
        ]);
    Assert(
        HostPluginBridge.IsTriggernometryNetworkAllowed() &&
        HostPluginBridge.IsTriggernometryHighRiskScriptAllowed(),
        "Triggernometry explicit network or high-risk script permission was not honored.");
}

void ValidateTriggernometryU6bCompatibilityPatch()
{
    const string p1Condition =
        "<Condition Enabled=\"true\" Grouping=\"Or\">" +
        "<ConditionSingle Enabled=\"true\" ExpressionL=\"${_entity[${tid}].HP}\" " +
        "ExpressionTypeL=\"String\" ExpressionR=\"0\" ExpressionTypeR=\"String\" " +
        "ConditionType=\"NumericGreater\" /></Condition>";
    var source =
        "<TriggernometryExport>" +
        "<RepositoryItem Id=\"0b7c968a-c565-49ca-a02e-6ac25e096be1\" />" +
        "<Trigger Id=\"0b7c968a-c565-49ca-a02e-6ac25e096be1\">" +
        "<Actions><Action ActionType=\"Loop\"><LoopActions />" + p1Condition +
        "</Action></Actions></Trigger>" +
        "<Trigger Id=\"df719e7c-6138-4c4e-9d15-bacbf41d88c9\" " +
        "RegularExpression=\"^101:.{8}:0002....:$\"></Trigger>" +
        "<Trigger Id=\"unrelated\" RegularExpression=\"101:.{8}:0002\">" +
        p1Condition + "</Trigger></TriggernometryExport>";

    var patched = HostPluginBridge.PatchTriggernometryExportXml(source);
    Assert(
        !patched.Contains(
            "Id=\"0b7c968a-c565-49ca-a02e-6ac25e096be1\"><Actions>" +
            "<Action ActionType=\"Loop\"><LoopActions />" + p1Condition,
            StringComparison.Ordinal) &&
        patched.Contains(
            "Id=\"df719e7c-6138-4c4e-9d15-bacbf41d88c9\" " +
            "RegularExpression=\"^101:[^:]*:0002....:$\"",
            StringComparison.Ordinal) &&
        patched.Contains(
            "Id=\"unrelated\" RegularExpression=\"101:.{8}:0002\">" + p1Condition,
            StringComparison.Ordinal),
        "The U6b compatibility patch changed the wrong trigger or missed a targeted assumption.");
    Assert(
        string.Equals(
            patched,
            HostPluginBridge.PatchTriggernometryExportXml(patched),
            StringComparison.Ordinal),
        "The U6b compatibility patch is not idempotent.");
}

void ValidateTriggernometryAssemblyRewrite(string assemblyPath)
{
    if (!File.Exists(assemblyPath))
    {
        throw new FileNotFoundException(
            "The Triggernometry assembly for patch validation was not found.",
            assemblyPath);
    }

    var loadContext = new AssemblyLoadContext(
        $"Triggernometry patch smoke {Guid.NewGuid():N}",
        isCollectible: true);
    try
    {
        _ = LegacyAssemblyRewriter.LoadTriggernometry(assemblyPath, loadContext);
        ValidateTriggernometryLaunchProcessPatch();
    }
    finally
    {
        loadContext.Unload();
    }
}

async Task ValidateSequenceRegressionTerminatesHostAsync()
{
    var (host, pipe, session) = await StartConnectedHostAsync();
    await using (pipe)
    using (host)
    {
        _ = await ReadWithTimeoutAsync(pipe);
        var hello = HostEnvelope.Create(
            session,
            1,
            HostMessageTypes.Hello,
            HostMessagePriority.Control,
            new HostHello("test", "1", Environment.ProcessId, [HostProtocol.CurrentVersion]));
        await HostFrameCodec.WriteAsync(pipe.Writer, hello, CancellationToken.None);
        await ReadUntilAsync(pipe, HostMessageTypes.HelloAck);
        await HostFrameCodec.WriteAsync(pipe.Writer, hello, CancellationToken.None);
        await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
        var (output, error) = ReadProcessLog(host);
        Assert(
            host.ExitCode != 0 &&
            error.Contains("IPC sequence regressed", StringComparison.Ordinal),
            "Host accepted a regressed IPC sequence." +
            $"{Environment.NewLine}{output}{Environment.NewLine}{error}");
    }
}

async Task ValidateExpiredMessageIsDroppedAsync()
{
    var (host, pipe, session) = await StartConnectedHostAsync();
    await using (pipe)
    using (host)
    {
        _ = await ReadWithTimeoutAsync(pipe);
        await HostFrameCodec.WriteAsync(
            pipe.Writer,
            HostEnvelope.Create(
                session,
                1,
                HostMessageTypes.Hello,
                HostMessagePriority.Control,
                new HostHello("test", "1", Environment.ProcessId, [HostProtocol.CurrentVersion])),
            CancellationToken.None);
        await ReadUntilAsync(pipe, HostMessageTypes.HelloAck);
        await HostFrameCodec.WriteAsync(
            pipe.Writer,
            HostEnvelope.Create(
                session,
                2,
                HostMessageTypes.CommandRequest,
                HostMessagePriority.Control,
                new HostCommandRequest(
                    "untrusted.test",
                    "powershell",
                    new Dictionary<string, string>()),
                "expired-command",
                DateTimeOffset.UtcNow.AddSeconds(-1)),
            CancellationToken.None);
        var heartbeatEnvelope = await ReadUntilAsync(pipe, HostMessageTypes.Heartbeat);
        var heartbeat = heartbeatEnvelope.Payload.Deserialize<HostHeartbeat>()
                        ?? throw new InvalidDataException("Host returned no heartbeat.");
        Assert(
            heartbeat.LastReceivedSequence == 2,
            "Host did not acknowledge receipt of the expired frame.");
        Assert(
            heartbeat.PrivateBytes > 0 &&
            heartbeat.AvailablePhysicalMemoryBytes > 0,
            "Host heartbeat did not publish private bytes and system memory headroom.");
        await HostFrameCodec.WriteAsync(
            pipe.Writer,
            HostEnvelope.Create(
                session,
                3,
                HostMessageTypes.Shutdown,
                HostMessagePriority.Control,
                new HostHealth("stopping", "test", DateTimeOffset.UtcNow),
                "expired-test-shutdown"),
            CancellationToken.None);
        await ReadUntilAsync(pipe, HostMessageTypes.ShutdownAck);
        await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
        var (output, error) = ReadProcessLog(host);
        Assert(
            !output.Contains("command result correlation=expired-command", StringComparison.Ordinal) &&
            error.Contains("Expired game-side IPC message dropped", StringComparison.Ordinal),
            "Host executed or failed to report an expired command frame." +
            $"{Environment.NewLine}{output}{Environment.NewLine}{error}");
    }
}

async Task ValidateHandshakeCommandBoundaryAndShutdownAsync()
{
    var (host, pipe, session) = await StartConnectedHostAsync();
    await using (pipe)
    using (host)
    {
        var first = await ReadWithTimeoutAsync(pipe);
        Assert(first.Type == HostMessageTypes.Hello, "Host did not send hello first.");

        await HostFrameCodec.WriteAsync(
            pipe.Writer,
            HostEnvelope.Create(
                session,
                1,
                HostMessageTypes.Hello,
                HostMessagePriority.Control,
                new HostHello("test", "1", Environment.ProcessId, [HostProtocol.CurrentVersion]),
                "hello-test"),
            CancellationToken.None);
        await ReadUntilAsync(pipe, HostMessageTypes.HelloAck);
        await ReadUntilAsync(pipe, HostMessageTypes.Heartbeat);

        await HostFrameCodec.WriteAsync(
            pipe.Writer,
            HostEnvelope.Create(
                session,
                2,
                HostMessageTypes.CommandRequest,
                HostMessagePriority.Control,
                new HostCommandRequest(
                    "untrusted.test",
                    "powershell",
                    new Dictionary<string, string> { ["script"] = "should-not-run" }),
                "command-test"),
            CancellationToken.None);
        var denial = await ReadUntilAsync(pipe, HostMessageTypes.CommandResult);
        var result = denial.Payload.Deserialize<HostCommandResult>()
                     ?? throw new InvalidDataException("Host returned no command result.");
        Assert(!result.Success && result.Status == "denied",
            "Host did not deny an arbitrary command request.");

        await HostFrameCodec.WriteAsync(
            pipe.Writer,
            HostEnvelope.Create(
                session,
                3,
                HostMessageTypes.Shutdown,
                HostMessagePriority.Control,
                new HostHealth("stopping", "test", DateTimeOffset.UtcNow),
                "shutdown-test"),
            CancellationToken.None);
        await ReadUntilAsync(pipe, HostMessageTypes.ShutdownAck);
        await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert(host.ExitCode == 0, $"Host clean shutdown exit code was {host.ExitCode}.");
    }
}

async Task ValidateHostCrashBreaksOnlyPipeAsync()
{
    var (host, pipe, _) = await StartConnectedHostAsync();
    await using (pipe)
    using (host)
    {
        _ = await ReadWithTimeoutAsync(pipe);
        host.Kill(entireProcessTree: true);
        await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            var closed = await HostFrameCodec.ReadAsync(pipe.Reader, CancellationToken.None)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert(closed is null, "Killed Host left a readable IPC frame.");
        }
        catch (IOException)
        {
            // A broken pipe is the expected alternate result.
        }
    }
}

async Task ValidateDedicatedHostCrashDoesNotAffectSharedHostAsync()
{
    var (sharedHost, sharedPipe, sharedSession) = await StartConnectedHostAsync();
    var (dedicatedHost, dedicatedPipe, dedicatedSession) = await StartConnectedHostAsync();
    await using (sharedPipe)
    await using (dedicatedPipe)
    using (sharedHost)
    using (dedicatedHost)
    {
        _ = await ReadWithTimeoutAsync(sharedPipe);
        await HostFrameCodec.WriteAsync(
            sharedPipe.Writer,
            HostEnvelope.Create(
                sharedSession,
                1,
                HostMessageTypes.Hello,
                HostMessagePriority.Control,
                new HostHello(
                    "shared-host-smoke",
                    "1",
                    Environment.ProcessId,
                    [HostProtocol.CurrentVersion])),
            CancellationToken.None);
        await ReadUntilAsync(sharedPipe, HostMessageTypes.HelloAck);

        _ = await ReadWithTimeoutAsync(dedicatedPipe);
        await HostFrameCodec.WriteAsync(
            dedicatedPipe.Writer,
            HostEnvelope.Create(
                dedicatedSession,
                1,
                HostMessageTypes.Hello,
                HostMessagePriority.Control,
                new HostHello(
                    "matcha-host-smoke",
                    "1",
                    Environment.ProcessId,
                    [HostProtocol.CurrentVersion])),
            CancellationToken.None);
        await ReadUntilAsync(dedicatedPipe, HostMessageTypes.HelloAck);

        dedicatedHost.Kill(entireProcessTree: true);
        await dedicatedHost.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));

        await HostFrameCodec.WriteAsync(
            sharedPipe.Writer,
            HostEnvelope.Create(
                sharedSession,
                2,
                HostMessageTypes.CommandRequest,
                HostMessagePriority.Control,
                new HostCommandRequest(
                    "untrusted.matcha-crash-smoke",
                    "powershell",
                    new Dictionary<string, string>()),
                "shared-after-matcha-crash"),
            CancellationToken.None);
        var response = await ReadUntilAsync(sharedPipe, HostMessageTypes.CommandResult);
        var result = response.Payload.Deserialize<HostCommandResult>()
                     ?? throw new InvalidDataException(
                         "Shared Host returned no command result after the Matcha Host crash.");
        Assert(
            !result.Success && result.Status == "denied",
            "The shared Host stopped responding after the dedicated Matcha Host crashed.");

        await HostFrameCodec.WriteAsync(
            sharedPipe.Writer,
            HostEnvelope.Create(
                sharedSession,
                3,
                HostMessageTypes.Shutdown,
                HostMessagePriority.Control,
                new HostHealth(
                    "stopping",
                    "dual-host crash isolation smoke",
                    DateTimeOffset.UtcNow)),
            CancellationToken.None);
        await ReadUntilAsync(sharedPipe, HostMessageTypes.ShutdownAck);
        await sharedHost.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert(
            sharedHost.ExitCode == 0,
            $"Shared Host exit code after Matcha crash was {sharedHost.ExitCode}.");
    }
}

async Task ValidateAbruptClientDisconnectAsync()
{
    var (host, pipe, _) = await StartConnectedHostAsync();
    using (host)
    {
        await pipe.DisposeAsync();
        await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
        var (output, error) = ReadProcessLog(host);
        Assert(
            host.ExitCode == 0,
            $"Host disconnect exit code was {host.ExitCode}.{Environment.NewLine}" +
            $"stdout:{Environment.NewLine}{output}{Environment.NewLine}" +
            $"stderr:{Environment.NewLine}{error}");
    }
}

async Task ValidateBlockedReaderRemainsOutOfProcessAsync()
{
    var (host, pipe, session) = await StartConnectedHostAsync(faultInjection: true);
    await using (pipe)
    using (host)
    {
        var isolationStopwatch = Stopwatch.StartNew();
        _ = await ReadWithTimeoutAsync(pipe);
        await HostFrameCodec.WriteAsync(
            pipe.Writer,
            HostEnvelope.Create(
                session,
                1,
                HostMessageTypes.Hello,
                HostMessagePriority.Control,
                new HostHello("test", "1", Environment.ProcessId, [HostProtocol.CurrentVersion])),
            CancellationToken.None);
        await ReadUntilAsync(pipe, HostMessageTypes.HelloAck);
        await HostFrameCodec.WriteAsync(
            pipe.Writer,
            HostEnvelope.Create(
                session,
                2,
                HostMessageTypes.FaultInject,
                HostMessagePriority.Control,
                new HostFaultInjection("block-reader", 30_000)),
            CancellationToken.None);
        var blockedDataWrite = HostFrameCodec.WriteAsync(
            pipe.Writer,
            HostEnvelope.Create(
                session,
                3,
                HostMessageTypes.LogBatch,
                HostMessagePriority.Data,
                Array.Empty<HostLogEvent>()),
            CancellationToken.None).AsTask();

        HostHeartbeat? stalled = null;
        for (var index = 0; index < 5; index++)
        {
            var heartbeat = await ReadUntilAsync(pipe, HostMessageTypes.Heartbeat);
            stalled = heartbeat.Payload.Deserialize<HostHeartbeat>();
            if (stalled?.LastReceivedSequence == 2)
            {
                break;
            }
        }

        var exposedStall = stalled?.LastReceivedSequence == 2;
        var bridgeProgressed =
            isolationStopwatch.Elapsed < TimeSpan.FromSeconds(10);
        host.Kill(entireProcessTree: true);
        await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            await blockedDataWrite.WaitAsync(TimeSpan.FromSeconds(1));
        }
        catch (Exception ex) when (ex is IOException or TimeoutException)
        {
            // Killing the fault-injected Host is expected to break a pending data write.
        }
        var (output, error) = ReadProcessLog(host);
        Assert(
            exposedStall,
            "Fault-injected Host reader did not expose stalled processing progress. " +
            $"ack={stalled?.LastReceivedSequence}.{Environment.NewLine}{output}{Environment.NewLine}{error}");
        Assert(
            bridgeProgressed,
            "Blocked Host prevented the independent bridge test from making progress. " +
            $"elapsed={isolationStopwatch.Elapsed}.{Environment.NewLine}{output}{Environment.NewLine}{error}");
    }
}

async Task ValidateLegacyPluginsLoadOutOfProcessAsync()
{
    await PrepareLegacySmokeConfigurationAsync();
    var (host, pipe, session) = await StartConnectedHostAsync(
        loadPlugins: true,
        faultInjection: true);
    await using (pipe)
    using (host)
    {
        try
        {
            _ = await ReadWithTimeoutAsync(pipe);
            await HostFrameCodec.WriteAsync(
            pipe.Writer,
            HostEnvelope.Create(
                session,
                1,
                HostMessageTypes.Hello,
                HostMessagePriority.Control,
                new HostHello("test", "1", Environment.ProcessId, [HostProtocol.CurrentVersion]),
                "legacy-hello"),
            CancellationToken.None);
            await ReadUntilAsync(pipe, HostMessageTypes.HelloAck, 90);
            await HostFrameCodec.WriteAsync(
                pipe.Writer,
                HostEnvelope.Create(
                    session,
                    2,
                    HostMessageTypes.Permissions,
                    HostMessagePriority.Control,
                    new HostPermissionSnapshot(
                        new Dictionary<string, IReadOnlyList<string>>
                        {
                            ["triggernometry"] =
                            [
                                "ReadCombatLogs",
                                "ReadLocalConfiguration",
                                "TextToSpeech",
                                "Clipboard",
                                "HighRiskScript",
                            ],
                            ["postnamazu"] =
                            [
                                "ReadCombatLogs",
                                "ReadLocalConfiguration",
                                "Clipboard",
                                "NetworkRequest",
                                "GameCommand",
                            ],
                        },
                        ["triggernometry", "postnamazu", "act.foxtts"]),
                    "legacy-permissions"),
                CancellationToken.None);
            // Reproduce the real startup race: combat may arrive before both plugin
            // initialization and the first zone, but neither state may be discarded.
            await HostFrameCodec.WriteAsync(
                pipe.Writer,
                HostEnvelope.Create(
                    session,
                    3,
                    HostMessageTypes.CombatStarted,
                    HostMessagePriority.Critical,
                    new HostCombatEvent(true, DateTimeOffset.UtcNow)),
                CancellationToken.None);
            await HostFrameCodec.WriteAsync(
                pipe.Writer,
                HostEnvelope.Create(
                    session,
                    4,
                    HostMessageTypes.ZoneChanged,
                    HostMessagePriority.Critical,
                    new HostZoneEvent(1, "Host Smoke Zone", DateTimeOffset.UtcNow)),
                CancellationToken.None);
            await HostFrameCodec.WriteAsync(
                pipe.Writer,
                HostEnvelope.Create(
                    session,
                    5,
                    HostMessageTypes.LogBatch,
                    HostMessagePriority.Data,
                    new[]
                    {
                        new HostLogEvent(
                            DateTimeOffset.UtcNow,
                            "00|2026-07-31T00:00:00.9000000+08:00|0000|ACTCOMPAT_ZONE_LINE|",
                            false),
                    }),
                CancellationToken.None);
            var healthEnvelope = await ReadUntilAsync(pipe, HostMessageTypes.Health, 90);
            var health = healthEnvelope.Payload.Deserialize<HostHealth>()
                         ?? throw new InvalidDataException("Host returned no plugin health.");
            Assert(
                health.State == "plugins.ready",
                $"Legacy plugin runtime did not become ready: {health.Detail}");
            var clientSequence = 6L;
            await ReadAndCompleteExpectedTtsSetAsync(
                "ACTCOMPAT_COMBAT_START",
                "ACTCOMPAT_ZONE_MATCH");
            await HostFrameCodec.WriteAsync(
                pipe.Writer,
                HostEnvelope.Create(
                    session,
                    clientSequence++,
                    HostMessageTypes.CombatEnded,
                    HostMessagePriority.Critical,
                    new HostCombatEvent(false, DateTimeOffset.UtcNow)),
                CancellationToken.None);
            await ReadAndCompleteExpectedTtsAsync("ACTCOMPAT_COMBAT_END");
            ValidateTriggernometryLaunchProcessPatch();
            var persistedConfiguration = new System.Xml.XmlDocument();
            persistedConfiguration.Load(Path.Combine(
                configRoot!,
                "Config",
                "Triggernometry.config.xml"));
            var persistedRoot = persistedConfiguration.DocumentElement
                                ?? throw new InvalidDataException(
                                    "Triggernometry persisted configuration has no root element.");
            Assert(
                persistedRoot.GetAttribute("PreviousNotifiedPluginVersion") == "2.1.2.2" &&
                persistedRoot.GetAttribute("PluginVersion") == "2.1.2.2",
                "Triggernometry did not persist its acknowledged version state during startup.");
            var hasFoxTts = File.Exists(Path.Combine(
                pluginRoot!,
                "act.foxtts",
                "actcompat.plugin.json"));
            Assert(
                hasFoxTts,
                "The real legacy Host smoke requires a standard installed ACT.FoxTTS manifest.");
            var httpPayload = await WaitForPostNamazuListenerAsync();
            Assert(
                httpPayload == "ACTCOMPAT_HTTP_BRIDGE",
                $"PostNamazu loopback HTTP endpoint returned '{httpPayload}'.");
            await HostFrameCodec.WriteAsync(
                pipe.Writer,
                HostEnvelope.Create(
                    session,
                    clientSequence++,
                    HostMessageTypes.FfxivEntities,
                    HostMessagePriority.State,
                    CreateTestFfxivSnapshot()),
                CancellationToken.None);
            await HostFrameCodec.WriteAsync(
                pipe.Writer,
                HostEnvelope.Create(
                    session,
                    clientSequence++,
                    HostMessageTypes.LogBatch,
                    HostMessagePriority.Data,
                    new[]
                    {
                        new HostLogEvent(
                            DateTimeOffset.UtcNow,
                            "00|2026-07-31T00:00:00.0000000+08:00|0000|ACTCOMPAT_SMOKE_LINE|",
                            false),
                        new HostLogEvent(
                            DateTimeOffset.UtcNow,
                            "00|2026-07-31T00:00:00.1000000+08:00|0038||echo test <se.10>|",
                            false,
                            "2026-07-31 00:00:00.100 00:0038::echo test <se.10>"),
                    }),
                CancellationToken.None);
            var ttsRequests = new List<(string CorrelationId, HostCommandRequest Request)>();
            while (ttsRequests.Count < 3)
            {
                var requestEnvelope = await ReadTriggerCommandAsync(pipe);
                var request = requestEnvelope.Payload.Deserialize<HostCommandRequest>()
                              ?? throw new InvalidDataException(
                                  "Triggernometry sent an invalid TTS request.");
                Assert(
                    request.PluginId == "triggernometry" && request.Command == "tts",
                    "Triggernometry closed-loop request did not retain plugin identity and TTS semantics.");
                Assert(
                    !string.IsNullOrWhiteSpace(requestEnvelope.CorrelationId),
                    "Triggernometry TTS request had no correlation identifier.");
                ttsRequests.Add((requestEnvelope.CorrelationId!, request));
            }

            var ttsTexts = ttsRequests
                .Select(item => item.Request.Arguments["text"])
                .ToHashSet(StringComparer.Ordinal);
            Assert(
                ttsTexts.SetEquals([
                    "ACTCOMPAT_LOG_MATCH",
                    "ACTCOMPAT_NETWORK_MATCH",
                    "ACTCOMPAT_LEGACY_ECHO_MATCH",
                ]),
                "Triggernometry did not complete standard-log, ACT-legacy-log, and FFXIV-network-equivalent regex/TTS paths.");
            foreach (var request in ttsRequests)
            {
                await HostFrameCodec.WriteAsync(
                    pipe.Writer,
                    HostEnvelope.Create(
                        session,
                        clientSequence++,
                        HostMessageTypes.CommandResult,
                        HostMessagePriority.Control,
                        new HostCommandResult(true, "completed", "smoke"),
                        request.CorrelationId),
                    CancellationToken.None);
            }

            await HostFrameCodec.WriteAsync(
                pipe.Writer,
                HostEnvelope.Create(
                    session,
                    clientSequence++,
                    HostMessageTypes.LogBatch,
                    HostMessagePriority.Data,
                    new[]
                    {
                        new HostLogEvent(
                            DateTimeOffset.UtcNow,
                            "00|2026-07-31T00:00:00.5000000+08:00|0000|ACTCOMPAT_SCRIPT_LINE|",
                            false),
                    }),
                CancellationToken.None);
            await WaitForProcessOutputAsync(
                host,
                "ACTCOMPAT_SCRIPT_REFERENCE_OK",
                TimeSpan.FromSeconds(15));
            await HostFrameCodec.WriteAsync(
                pipe.Writer,
                HostEnvelope.Create(
                    session,
                    clientSequence++,
                    HostMessageTypes.LogBatch,
                    HostMessagePriority.Data,
                    new[]
                    {
                        new HostLogEvent(
                            DateTimeOffset.UtcNow,
                            "00|2026-07-31T00:00:00.6250000+08:00|0000|ACTCOMPAT_OVERLAY_HANDLER_LINE|",
                            false),
                    }),
                CancellationToken.None);
            var overlayEnvelope = await ReadTriggerCommandAsync(pipe);
            var overlayRequest = overlayEnvelope.Payload.Deserialize<HostCommandRequest>()
                                 ?? throw new InvalidDataException(
                                     "Triggernometry sent an invalid OverlayPlugin request.");
            Assert(
                overlayRequest.PluginId == "triggernometry" &&
                overlayRequest.Command == "triggernometry.overlay" &&
                overlayRequest.Arguments.TryGetValue("payload", out var overlayPayload) &&
                overlayPayload.Contains("\"call\":\"getLanguage\"", StringComparison.Ordinal),
                "Triggernometry OverlayPlugin handler call did not retain its JSON semantics.");
            Assert(
                !string.IsNullOrWhiteSpace(overlayEnvelope.CorrelationId),
                "Triggernometry OverlayPlugin request had no correlation identifier.");
            await HostFrameCodec.WriteAsync(
                pipe.Writer,
                HostEnvelope.Create(
                    session,
                    clientSequence++,
                    HostMessageTypes.CommandResult,
                    HostMessagePriority.Control,
                    new HostCommandResult(
                        true,
                        "completed",
                        "{\"language\":\"ACTCOMPAT_OVERLAY_OK\"}"),
                    overlayEnvelope.CorrelationId),
                CancellationToken.None);
            await WaitForProcessOutputAsync(
                host,
                "ACTCOMPAT_OVERLAY_HANDLER_OK:ACTCOMPAT_OVERLAY_OK",
                TimeSpan.FromSeconds(15));
            await HostFrameCodec.WriteAsync(
                pipe.Writer,
                HostEnvelope.Create(
                    session,
                    clientSequence++,
                    HostMessageTypes.LogBatch,
                    HostMessagePriority.Data,
                    new[]
                    {
                        new HostLogEvent(
                            DateTimeOffset.UtcNow,
                            "00|2026-07-31T00:00:00.7000000+08:00|0000|ACTCOMPAT_PLUGIN_ORDER_LINE|",
                            false),
                    }),
                CancellationToken.None);
            await WaitForProcessOutputAsync(
                host,
                "ACTCOMPAT_PLUGIN_ORDER:FFXIV_ACT_Plugin.dll>OverlayPlugin.dll>Triggernometry.dll>PostNamazu.dll>ACT.FoxTTS.dll",
                TimeSpan.FromSeconds(15));
            await HostFrameCodec.WriteAsync(
                pipe.Writer,
                HostEnvelope.Create(
                    session,
                    clientSequence++,
                    HostMessageTypes.LogBatch,
                    HostMessagePriority.Data,
                    new[]
                    {
                        new HostLogEvent(
                            DateTimeOffset.UtcNow,
                            "00|2026-07-31T00:00:00.7500000+08:00|0000|ACTCOMPAT_PICTO_LINE|",
                            false),
                    }),
                CancellationToken.None);
            await ReadAndCompleteExpectedPostNamazuAsync(
                "postnamazu.pictoact",
                "Omen: Rect",
                "Delay: 2.5",
                "Angle: 0 + pi/2",
                "Pos: 1.25, -3.75, 2.5");
            if (hasFoxTts)
            {
                await HostFrameCodec.WriteAsync(
                    pipe.Writer,
                    HostEnvelope.Create(
                        session,
                        clientSequence++,
                        HostMessageTypes.TtsRequest,
                        HostMessagePriority.Control,
                        new HostTtsRequest("ACTCOMPAT_GAME_TTS", "host-smoke")),
                    CancellationToken.None);
            }

            await HostFrameCodec.WriteAsync(
                pipe.Writer,
                HostEnvelope.Create(
                    session,
                    clientSequence++,
                    HostMessageTypes.LogBatch,
                    HostMessagePriority.Data,
                    new[]
                    {
                        new HostLogEvent(
                            DateTimeOffset.UtcNow,
                            "00|2026-07-31T00:00:01.5000000+08:00|0000|ACTCOMPAT_MARK_LINE|",
                            false),
                    }),
                CancellationToken.None);
            await ReadAndCompleteExpectedPostNamazuAsync(
                "postnamazu.mark",
                "\"ActorID\":\"0x10001234\"");
            await HostFrameCodec.WriteAsync(
                pipe.Writer,
                HostEnvelope.Create(
                    session,
                    clientSequence++,
                    HostMessageTypes.LogBatch,
                    HostMessagePriority.Data,
                    new[]
                    {
                        new HostLogEvent(
                            DateTimeOffset.UtcNow,
                            "00|2026-07-31T00:00:01.7500000+08:00|0000|ACTCOMPAT_PLACE_LINE|",
                            false),
                    }),
                CancellationToken.None);
            await ReadAndCompleteExpectedPostNamazuAsync(
                "postnamazu.place",
                "\"X\":1.25,\"Y\":2.5,\"Z\":-3.75");
            await HostFrameCodec.WriteAsync(
                pipe.Writer,
                HostEnvelope.Create(
                    session,
                    clientSequence++,
                    HostMessageTypes.LogBatch,
                    HostMessagePriority.Data,
                    new[]
                    {
                        new HostLogEvent(
                            DateTimeOffset.UtcNow,
                            "00|2026-07-31T00:00:02.0000000+08:00|0000|ACTCOMPAT_PRESET_LINE|",
                            false),
                    }),
                CancellationToken.None);
            await ReadAndCompleteExpectedPostNamazuAsync(
                "postnamazu.preset",
                "\"Name\":\"Slot 30\"",
                "\"MapID\":777");
            await HostFrameCodec.WriteAsync(
                pipe.Writer,
                HostEnvelope.Create(
                    session,
                    clientSequence++,
                    HostMessageTypes.LogBatch,
                    HostMessagePriority.Data,
                    new[]
                    {
                        new HostLogEvent(
                            DateTimeOffset.UtcNow,
                            "00|2026-07-31T00:00:02.2500000+08:00|0000|ACTCOMPAT_SENDKEY_LINE|",
                            false),
                    }),
                CancellationToken.None);
            await ReadAndCompleteExpectedPostNamazuAsync(
                "postnamazu.sendkey",
                "65");
            await HostFrameCodec.WriteAsync(
                pipe.Writer,
                HostEnvelope.Create(
                    session,
                    clientSequence++,
                    HostMessageTypes.LogBatch,
                    HostMessagePriority.Data,
                    new[]
                    {
                        new HostLogEvent(
                            DateTimeOffset.UtcNow,
                            "00|2026-07-31T00:00:02.5000000+08:00|0000|ACTCOMPAT_QUEUE_LINE|",
                            false),
                    }),
                CancellationToken.None);
            await ReadAndCompleteExpectedPostNamazuCommandAsync("//e ACTCOMPAT_QUEUE");
            await HostFrameCodec.WriteAsync(
                pipe.Writer,
                HostEnvelope.Create(
                    session,
                    clientSequence++,
                    HostMessageTypes.PluginInvoke,
                    HostMessagePriority.Control,
                    new HostPluginInvocation(
                        "postnamazu",
                        "overlay",
                        new Dictionary<string, string>
                        {
                            ["command"] = "NamazuLog",
                            ["payload"] = "ACTCOMPAT_OVERLAY_BRIDGE",
                        })),
                CancellationToken.None);
            await HostFrameCodec.WriteAsync(
                pipe.Writer,
                HostEnvelope.Create(
                    session,
                    clientSequence++,
                    HostMessageTypes.Shutdown,
                    HostMessagePriority.Control,
                    new HostHealth("stopping", "legacy test", DateTimeOffset.UtcNow),
                    "legacy-shutdown"),
                CancellationToken.None);
            await ReadUntilAsync(pipe, HostMessageTypes.ShutdownAck, 90);
            await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var (output, errors) = ReadProcessLog(host);
            Assert(
                output.Contains(
                    "Legacy plugin 'triggernometry' loaded out-of-process.",
                    StringComparison.Ordinal),
                $"Triggernometry did not load out-of-process." +
                $"{Environment.NewLine}{output}{Environment.NewLine}{errors}");
            Assert(
                output.Contains(
                    "Triggernometry startup update check delegated to the managed bundled-plugin updater.",
                    StringComparison.Ordinal),
                "Triggernometry still ran its competing startup self-update check." +
                $"{Environment.NewLine}{output}{Environment.NewLine}{errors}");
            Assert(
                output.Contains("ACTCOMPAT_SCRIPT_REFERENCE_OK", StringComparison.Ordinal),
                "Triggernometry could not compile and execute a C# action that references its " +
                $"own assembly.{Environment.NewLine}{output}{Environment.NewLine}{errors}");
            Assert(
                output.Contains(
                    "Triggernometry PictoACT callback registered through the game-side drawing broker.",
                    StringComparison.Ordinal),
                "Triggernometry did not register PictoACT automatically during normal startup." +
                $"{Environment.NewLine}{output}{Environment.NewLine}{errors}");
            Assert(
                output.Contains(
                    "OverlayPlugin compatibility identity registered before Triggernometry.",
                    StringComparison.Ordinal),
                "OverlayPlugin compatibility identity was not registered in ACT plugin order." +
                $"{Environment.NewLine}{output}{Environment.NewLine}{errors}");
            Assert(
                output.Contains(
                    "PostNamazu GreyMagic EventWaitHandle ACL compatibility shim loaded.",
                    StringComparison.Ordinal),
                "PostNamazu GreyMagic compatibility shim was not loaded." +
                $"{Environment.NewLine}{output}{Environment.NewLine}{errors}");
            Assert(
                output.Contains(
                    "ACTCOMPAT_OVERLAY_HANDLER_OK:ACTCOMPAT_OVERLAY_OK",
                    StringComparison.Ordinal),
                "Triggernometry OverlayPlugin handler response did not close the Host IPC loop." +
                $"{Environment.NewLine}{output}{Environment.NewLine}{errors}");
            Assert(
                output.Contains(
                    "ACTCOMPAT_PLUGIN_ORDER:FFXIV_ACT_Plugin.dll>OverlayPlugin.dll>Triggernometry.dll>PostNamazu.dll>ACT.FoxTTS.dll",
                    StringComparison.Ordinal),
                "ACT plugin list order did not match Triggernometry's required first three entries." +
                $"{Environment.NewLine}{output}{Environment.NewLine}{errors}");
            Assert(
                !output.Contains("OverlayPlugin not found", StringComparison.OrdinalIgnoreCase) &&
                !errors.Contains("OverlayPlugin not found", StringComparison.OrdinalIgnoreCase) &&
                !output.Contains("ReflectionNotFoundException", StringComparison.Ordinal) &&
                !errors.Contains("ReflectionNotFoundException", StringComparison.Ordinal),
                "Triggernometry still reported the removed in-process OverlayPlugin reflection path." +
                $"{Environment.NewLine}{output}{Environment.NewLine}{errors}");
            Assert(
                !output.Contains("ICombatantMemory", StringComparison.Ordinal) &&
                !errors.Contains("ICombatantMemory", StringComparison.Ordinal),
                "Triggernometry still probed the obsolete OverlayPlugin combatant-memory shape." +
                $"{Environment.NewLine}{output}{Environment.NewLine}{errors}");
            Assert(
                output.Contains("Legacy plugin 'postnamazu' loaded out-of-process.", StringComparison.Ordinal),
                $"PostNamazu did not load out-of-process.{Environment.NewLine}{output}{Environment.NewLine}{errors}");
            Assert(
                output.Contains(
                    "PostNamazu HTTP listener started:",
                    StringComparison.Ordinal) &&
                output.Contains($":{postNamazuSmokePort}/", StringComparison.Ordinal),
                $"PostNamazu did not start its configured HTTP listener." +
                $"{Environment.NewLine}{output}{Environment.NewLine}{errors}");
            Assert(
                output.Split("test TTS output suppressed:", StringSplitOptions.None).Length - 1 ==
                7,
                "Authorized Triggernometry TTS requests did not reach the isolated Host output " +
                $"provider.{Environment.NewLine}{output}{Environment.NewLine}{errors}");
            Assert(
                output.Contains(
                    "PostNamazu selected the cross-process game-side OverlayPlugin adapter.",
                    StringComparison.Ordinal),
                $"PostNamazu did not select the real cross-process OverlayPlugin adapter." +
                $"{Environment.NewLine}{output}{Environment.NewLine}{errors}");
            Assert(
                output.Contains(
                    "Invoked legacy plugin 'postnamazu' action 'overlay'.",
                    StringComparison.Ordinal),
                $"PostNamazu OverlayPlugin invocation did not cross the Host boundary." +
                $"{Environment.NewLine}{output}{Environment.NewLine}{errors}");
            if (hasFoxTts)
            {
                Assert(
                    output.Contains(
                        "Legacy plugin 'act.foxtts' loaded out-of-process.",
                        StringComparison.Ordinal),
                        $"Manifest ACT plugin did not load out-of-process." +
                        $"{Environment.NewLine}{output}{Environment.NewLine}{errors}");
                Assert(
                    output.Contains(
                        "ACT.FoxTTS speech bridge ready in the external Host.",
                        StringComparison.Ordinal),
                    $"ACT.FoxTTS loaded without exposing its real Speak bridge." +
                    $"{Environment.NewLine}{output}{Environment.NewLine}{errors}");
                Assert(
                    output.Contains(
                        "test TTS output suppressed: ACTCOMPAT_GAME_TTS",
                        StringComparison.Ordinal),
                    $"Game-side ACT/Cactbot TTS did not reach isolated FoxTTS." +
                    $"{Environment.NewLine}{output}{Environment.NewLine}{errors}");
            }

            async Task ReadAndCompleteExpectedTtsAsync(string expectedText)
            {
                var envelope = await ReadTriggerCommandAsync(pipe);
                var request = envelope.Payload.Deserialize<HostCommandRequest>()
                              ?? throw new InvalidDataException(
                                  "Triggernometry sent an invalid TTS request.");
                Assert(
                    request.PluginId == "triggernometry" &&
                    request.Command == "tts" &&
                    request.Arguments.TryGetValue("text", out var text) &&
                    text == expectedText,
                    $"Expected Triggernometry TTS '{expectedText}', received " +
                    $"'{request.Arguments.GetValueOrDefault("text", "<missing>")}'.");
                Assert(
                    !string.IsNullOrWhiteSpace(envelope.CorrelationId),
                    "Triggernometry TTS request had no correlation identifier.");
                await HostFrameCodec.WriteAsync(
                    pipe.Writer,
                    HostEnvelope.Create(
                        session,
                        clientSequence++,
                        HostMessageTypes.CommandResult,
                        HostMessagePriority.Control,
                        new HostCommandResult(true, "completed", "smoke"),
                        envelope.CorrelationId),
                    CancellationToken.None);
            }

            async Task ReadAndCompleteExpectedTtsSetAsync(params string[] expectedTexts)
            {
                var receivedTexts = new HashSet<string>(StringComparer.Ordinal);
                for (var index = 0; index < expectedTexts.Length; index++)
                {
                    var envelope = await ReadTriggerCommandAsync(pipe);
                    var request = envelope.Payload.Deserialize<HostCommandRequest>()
                                  ?? throw new InvalidDataException(
                                      "Triggernometry sent an invalid TTS request.");
                    var receivedText = request.Arguments.GetValueOrDefault("text");
                    Assert(
                        request.PluginId == "triggernometry" &&
                        request.Command == "tts" &&
                        receivedText is not null,
                        "Triggernometry startup replay sent a non-TTS command.");
                    receivedTexts.Add(receivedText!);
                    Assert(
                        !string.IsNullOrWhiteSpace(envelope.CorrelationId),
                        "Triggernometry TTS request had no correlation identifier.");
                    await HostFrameCodec.WriteAsync(
                        pipe.Writer,
                        HostEnvelope.Create(
                            session,
                            clientSequence++,
                            HostMessageTypes.CommandResult,
                            HostMessagePriority.Control,
                            new HostCommandResult(true, "completed", "smoke"),
                            envelope.CorrelationId),
                        CancellationToken.None);
                }

                Assert(
                    receivedTexts.SetEquals(expectedTexts),
                    $"Triggernometry startup replay expected [{string.Join(", ", expectedTexts)}], " +
                    $"received [{string.Join(", ", receivedTexts)}].");
            }

            async Task ReadAndCompleteExpectedPostNamazuAsync(
                string expectedAction,
                params string[] expectedPayloadFragments)
            {
                var envelope = await ReadTriggerCommandAsync(pipe);
                var request = envelope.Payload.Deserialize<HostCommandRequest>()
                              ?? throw new InvalidDataException(
                                  "PostNamazu sent an invalid semantic request.");
                Assert(
                    request.PluginId == "postnamazu" &&
                    request.Command == expectedAction &&
                    request.Arguments.TryGetValue("payload", out var payload) &&
                    expectedPayloadFragments.All(fragment =>
                        payload.Contains(fragment, StringComparison.Ordinal)),
                    $"Expected PostNamazu semantic action '{expectedAction}' with payload " +
                    $"fragments [{string.Join(", ", expectedPayloadFragments)}].");
                Assert(
                    !string.IsNullOrWhiteSpace(envelope.CorrelationId),
                    "PostNamazu semantic request had no correlation identifier.");
                await HostFrameCodec.WriteAsync(
                    pipe.Writer,
                    HostEnvelope.Create(
                        session,
                        clientSequence++,
                        HostMessageTypes.CommandResult,
                        HostMessagePriority.Control,
                        new HostCommandResult(true, "completed", "smoke"),
                        envelope.CorrelationId),
                    CancellationToken.None);
            }

            async Task ReadAndCompleteExpectedPostNamazuCommandAsync(string expectedText)
            {
                var envelope = await ReadTriggerCommandAsync(pipe);
                var request = envelope.Payload.Deserialize<HostCommandRequest>()
                              ?? throw new InvalidDataException(
                                  "PostNamazu sent an invalid command request.");
                Assert(
                    request.PluginId == "postnamazu" &&
                    request.Command == "postnamazu.chat" &&
                    request.Arguments.TryGetValue("text", out var text) &&
                    text == expectedText,
                    $"Expected queued PostNamazu command '{expectedText}'.");
                await HostFrameCodec.WriteAsync(
                    pipe.Writer,
                    HostEnvelope.Create(
                        session,
                        clientSequence++,
                        HostMessageTypes.CommandResult,
                        HostMessagePriority.Control,
                        new HostCommandResult(true, "completed", "smoke"),
                        envelope.CorrelationId),
                    CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            if (!host.HasExited)
            {
                host.Kill(entireProcessTree: true);
            }
            await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var (output, errors) = ReadProcessLog(host);
            throw new InvalidOperationException(
                $"External legacy plugin Host test failed.{Environment.NewLine}" +
                $"stdout:{Environment.NewLine}{output}{Environment.NewLine}" +
                $"stderr:{Environment.NewLine}{errors}",
                ex);
        }
    }
}

async Task ValidatePostNamazuQueueExecutionLifecycleAsync()
{
    var bridgeType = typeof(HostPluginBridge);
    var configurePermissions = bridgeType.GetMethod(
                                   "ConfigurePermissions",
                                   BindingFlags.Static | BindingFlags.NonPublic)
                               ?? throw new MissingMethodException(
                                   bridgeType.FullName,
                                   "ConfigurePermissions");
    configurePermissions.Invoke(
        null,
        [
            new HostPermissionSnapshot(
                new Dictionary<string, IReadOnlyList<string>>
                {
                    ["postnamazu"] = ["GameCommand"],
                },
                ["postnamazu"]),
        ]);

    var tasks = (HashSet<Task>?)bridgeType.GetField(
                    "PostNamazuQueueTasks",
                    BindingFlags.Static | BindingFlags.NonPublic)
                ?.GetValue(null)
                ?? throw new MissingFieldException(bridgeType.FullName, "PostNamazuQueueTasks");
    var taskLock = bridgeType.GetField(
                       "PostNamazuTaskLock",
                       BindingFlags.Static | BindingFlags.NonPublic)
                   ?.GetValue(null)
                   ?? throw new MissingFieldException(bridgeType.FullName, "PostNamazuTaskLock");
    bool QueuesFinished()
    {
        lock (taskLock)
        {
            return tasks.Count == 0;
        }
    }

    RecordingPostNamazuPlugin.Reset();
    HostPluginBridge.SendPostNamazuQueue(
        new RecordingPostNamazuModule(),
        "[{\"C\":\"qid\",\"P\":\"ACTCOMPAT_CANCEL\",\"D\":0}," +
        "{\"C\":\"command\",\"P\":\"must-not-run\",\"D\":500}]");
    var queueIds = (List<string>?)bridgeType.GetField(
                       "PostNamazuQueueIds",
                       BindingFlags.Static | BindingFlags.NonPublic)
                   ?.GetValue(null)
                   ?? throw new MissingFieldException(bridgeType.FullName, "PostNamazuQueueIds");
    var queueLock = bridgeType.GetField(
                        "PostNamazuQueueLock",
                        BindingFlags.Static | BindingFlags.NonPublic)
                    ?.GetValue(null)
                    ?? throw new MissingFieldException(bridgeType.FullName, "PostNamazuQueueLock");
    Assert(
        SpinWait.SpinUntil(
            () =>
            {
                lock (queueLock)
                {
                    return queueIds.Contains("ACTCOMPAT_CANCEL");
                }
            },
            TimeSpan.FromSeconds(1)),
        "PostNamazu queue did not register its qid before the delayed action.");
    HostPluginBridge.BreakPostNamazuQueue("ACTCOMPAT_CANCEL");
    Assert(
        SpinWait.SpinUntil(QueuesFinished, TimeSpan.FromSeconds(2)) &&
        RecordingPostNamazuPlugin.Invocations.IsEmpty,
        "PostNamazu qid cancellation still dispatched a delayed action.");

    HostPluginBridge.SendPostNamazuQueue(
        new RecordingPostNamazuModule(),
        "[{\"C\":\"qid\",\"P\":\"ACTCOMPAT_RESTART\",\"D\":0}," +
        "{\"C\":\"command\",\"P\":\"first\",\"D\":0}]");
    Assert(
        SpinWait.SpinUntil(
            () => RecordingPostNamazuPlugin.Invocations.Count == 1 && QueuesFinished(),
            TimeSpan.FromSeconds(2)),
        "PostNamazu queue did not finish its first run.");
    HostPluginBridge.SendPostNamazuQueue(
        new RecordingPostNamazuModule(),
        "[{\"C\":\"qid\",\"P\":\"ACTCOMPAT_RESTART\",\"D\":0}," +
        "{\"C\":\"command\",\"P\":\"second\",\"D\":0}]");
    Assert(
        SpinWait.SpinUntil(
            () => RecordingPostNamazuPlugin.Invocations.Count == 2 && QueuesFinished(),
            TimeSpan.FromSeconds(2)) &&
        RecordingPostNamazuPlugin.Invocations.Select(call => call.Payload)
            .SequenceEqual(["first", "second"]),
        "PostNamazu queue could not restart a completed qid in order.");

    await Task.CompletedTask;
}

async Task ValidateTriggernometryEntityTimingProbeAsync()
{
    await PrepareLegacySmokeConfigurationAsync();
    var (host, pipe, session) = await StartConnectedHostAsync(
        loadPlugins: true,
        faultInjection: true);
    await using (pipe)
    using (host)
    {
        try
        {
            _ = await ReadWithTimeoutAsync(pipe);
            var clientSequence = 1L;
            await HostFrameCodec.WriteAsync(
                pipe.Writer,
                HostEnvelope.Create(
                    session,
                    clientSequence++,
                    HostMessageTypes.Hello,
                    HostMessagePriority.Control,
                    new HostHello("entity-timing-probe", "1", Environment.ProcessId, [HostProtocol.CurrentVersion])),
                CancellationToken.None);
            await ReadUntilAsync(pipe, HostMessageTypes.HelloAck, 90);
            await HostFrameCodec.WriteAsync(
                pipe.Writer,
                HostEnvelope.Create(
                    session,
                    clientSequence++,
                    HostMessageTypes.Permissions,
                    HostMessagePriority.Control,
                    new HostPermissionSnapshot(
                        new Dictionary<string, IReadOnlyList<string>>
                        {
                            ["triggernometry"] =
                            [
                                "ReadCombatLogs",
                                "ReadLocalConfiguration",
                                "TextToSpeech",
                                "Clipboard",
                                "HighRiskScript",
                            ],
                            ["postnamazu"] =
                            [
                                "ReadCombatLogs",
                                "ReadLocalConfiguration",
                                "Clipboard",
                                "NetworkRequest",
                                "GameCommand",
                            ],
                        },
                        ["triggernometry", "postnamazu", "act.foxtts"]),
                    "entity-timing-permissions"),
                CancellationToken.None);
            await HostFrameCodec.WriteAsync(
                pipe.Writer,
                HostEnvelope.Create(
                    session,
                    clientSequence++,
                    HostMessageTypes.ZoneChanged,
                    HostMessagePriority.Critical,
                    new HostZoneEvent(1, "Host Smoke Zone", DateTimeOffset.UtcNow)),
                CancellationToken.None);
            var healthEnvelope = await ReadUntilAsync(pipe, HostMessageTypes.Health, 90);
            var health = healthEnvelope.Payload.Deserialize<HostHealth>()
                         ?? throw new InvalidDataException("Host returned no plugin health.");
            Assert(
                health.State == "plugins.ready",
                $"Legacy plugin runtime did not become ready: {health.Detail}");

            HostFfxivEntitySnapshot? latestFullSnapshot = null;
            async Task PublishSnapshotAsync(bool includeTarget)
            {
                latestFullSnapshot = CreateTestFfxivSnapshot(includeTarget);
                await HostFrameCodec.WriteAsync(
                    pipe.Writer,
                    HostEnvelope.Create(
                        session,
                        clientSequence++,
                        HostMessageTypes.FfxivEntities,
                        HostMessagePriority.State,
                        latestFullSnapshot),
                    CancellationToken.None);
            }

            async Task PublishTargetDeltaAsync()
            {
                var baseline = latestFullSnapshot
                               ?? throw new InvalidOperationException(
                                   "Entity timing delta has no full baseline.");
                var current = CreateTestFfxivSnapshot(includeTarget: true);
                var target = current.Combatants.Single(combatant => combatant.Id == 0x10005678);
                await HostFrameCodec.WriteAsync(
                    pipe.Writer,
                    HostEnvelope.Create(
                        session,
                        clientSequence++,
                        HostMessageTypes.FfxivEntityDelta,
                        HostMessagePriority.State,
                        new HostFfxivEntityDelta(
                            current.TerritoryId,
                            current.CurrentPlayerId,
                            baseline.Timestamp,
                            current.Timestamp,
                            [target],
                            [])),
                    CancellationToken.None);
            }

            async Task PublishProbeLogAsync(string text)
            {
                await HostFrameCodec.WriteAsync(
                    pipe.Writer,
                    HostEnvelope.Create(
                        session,
                        clientSequence++,
                        HostMessageTypes.LogBatch,
                        HostMessagePriority.Data,
                        new[]
                        {
                            new HostLogEvent(
                                DateTimeOffset.UtcNow,
                                $"00|2026-08-20T00:00:00.0000000+08:00|0000|{text}|",
                                false),
                        }),
                    CancellationToken.None);
            }

            async Task CompleteCommandAsync(HostEnvelope envelope)
            {
                Assert(
                    !string.IsNullOrWhiteSpace(envelope.CorrelationId),
                    "Entity timing probe received a command without a correlation identifier.");
                await HostFrameCodec.WriteAsync(
                    pipe.Writer,
                    HostEnvelope.Create(
                        session,
                        clientSequence++,
                        HostMessageTypes.CommandResult,
                        HostMessagePriority.Control,
                        new HostCommandResult(true, "completed", "entity-timing-probe"),
                        envelope.CorrelationId),
                    CancellationToken.None);
            }

            async Task<(int Creates, int Changes)> ReadScenarioAsync(string mode)
            {
                var creates = 0;
                var changes = 0;
                for (var commandIndex = 0; commandIndex < 100; commandIndex++)
                {
                    var envelope = await ReadTriggerCommandAsync(pipe);
                    var request = envelope.Payload.Deserialize<HostCommandRequest>()
                                  ?? throw new InvalidDataException(
                                      "Entity timing probe received an invalid command request.");
                    var hasPayload = request.Arguments.TryGetValue("payload", out var payload);
                    Assert(
                        request.PluginId == "postnamazu" &&
                        request.Command == "postnamazu.pictoact" &&
                        hasPayload && payload is not null,
                        "Entity timing probe received an unexpected command.");
                    payload ??= string.Empty;

                    await CompleteCommandAsync(envelope);
                    if (payload.Contains($"Tag: ACTCOMPAT_ENTITY_{mode}_DONE", StringComparison.Ordinal))
                    {
                        return (creates, changes);
                    }

                    if (!payload.Contains($"Tag: ACTCOMPAT_ENTITY_{mode}_10005678", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (payload.Contains("Action: Change", StringComparison.Ordinal))
                    {
                        changes++;
                        Assert(
                            payload.Contains("Pos: 91.25, 108.75, 0", StringComparison.Ordinal),
                            $"Entity coordinates were not mapped to ACT's X/Y ground plane: {payload}");
                        // Heading is consumed by facing-sensitive trigger packs independently
                        // of XY, so verify it survives the same real Triggernometry expression path.
                        Assert(
                            payload.Contains("Theta: 1.5", StringComparison.Ordinal),
                            $"Entity heading was not exposed through Triggernometry: {payload}");
                        Assert(
                            payload.Contains("PlayerHeading: 0.5", StringComparison.Ordinal) &&
                            payload.Contains("PlayerAddress: 305419896", StringComparison.Ordinal),
                            $"P3 player heading/address inputs were not exposed through Triggernometry: {payload}");
                    }
                    else
                    {
                        creates++;
                    }
                }

                throw new InvalidOperationException(
                    $"Entity timing probe did not reach the {mode} completion marker.");
            }

            await PublishSnapshotAsync(includeTarget: true);
            await PublishProbeLogAsync("ACTCOMPAT_ENTITY_ACTUAL:10005678");
            var present = await ReadScenarioAsync("ACTUAL");

            await PublishSnapshotAsync(includeTarget: false);
            await PublishProbeLogAsync("ACTCOMPAT_ENTITY_ACTUAL:10005678");
            var firstActualCommand = await ReadTriggerCommandAsync(pipe);
            var firstActualRequest = firstActualCommand.Payload.Deserialize<HostCommandRequest>()
                                     ?? throw new InvalidDataException(
                                         "Delayed actual-chain probe received an invalid command.");
            Assert(
                firstActualRequest.Arguments.GetValueOrDefault("payload")?.Contains(
                    "Tag: ACTCOMPAT_ENTITY_ACTUAL_10005678",
                    StringComparison.Ordinal) == true,
                "Delayed actual-chain probe did not create its initial unpositioned drawing.");
            await CompleteCommandAsync(firstActualCommand);
            // Waiting here reproduces the entity-late race that previously ended the loop before
            // the next game-side update could expose the target.
            await Task.Delay(50);
            await PublishTargetDeltaAsync();
            var delayedActual = await ReadScenarioAsync("ACTUAL");

            await PublishSnapshotAsync(includeTarget: false);
            await PublishProbeLogAsync("ACTCOMPAT_ENTITY_RETRY:10005678");
            var firstRetryCommand = await ReadTriggerCommandAsync(pipe);
            var firstRetryRequest = firstRetryCommand.Payload.Deserialize<HostCommandRequest>()
                                    ?? throw new InvalidDataException(
                                        "Delayed retry-chain probe received an invalid command.");
            Assert(
                firstRetryRequest.Arguments.GetValueOrDefault("payload")?.Contains(
                    "Tag: ACTCOMPAT_ENTITY_RETRY_10005678",
                    StringComparison.Ordinal) == true,
                "Delayed retry-chain probe did not create its initial unpositioned drawing.");
            await CompleteCommandAsync(firstRetryCommand);
            await Task.Delay(50);
            await PublishTargetDeltaAsync();
            var delayedRetry = await ReadScenarioAsync("RETRY");

            Assert(
                present.Creates == 1 && present.Changes > 0,
                $"An entity present before activation was not resolved: create={present.Creates}, change={present.Changes}.");
            Assert(
                delayedRetry.Changes > 0,
                "Triggernometry did not observe a later DACT entity delta from inside an active loop.");
            Assert(
                delayedActual.Changes > 0,
                "The targeted U6b P1 compatibility patch did not recover an entity-late loop.");

            Console.WriteLine(
                "TRIGGERNOMETRY_ENTITY_TIMING_RESULT=FIXED; " +
                $"present(create={present.Creates},change={present.Changes}); " +
                $"actual-delayed(change={delayedActual.Changes}); " +
                $"retry-delayed(change={delayedRetry.Changes})");

            await HostFrameCodec.WriteAsync(
                pipe.Writer,
                HostEnvelope.Create(
                    session,
                    clientSequence++,
                    HostMessageTypes.Shutdown,
                    HostMessagePriority.Control,
                    new HostHealth("stopping", "entity timing probe", DateTimeOffset.UtcNow),
                    "entity-timing-shutdown"),
                CancellationToken.None);
            await ReadUntilAsync(pipe, HostMessageTypes.ShutdownAck, 90);
            await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch
        {
            var (output, error) = processLogs.TryGetValue(host.Id, out var log)
                ? log.Snapshot()
                : (string.Empty, string.Empty);
            Console.Error.WriteLine(
                $"Entity timing Host output:{Environment.NewLine}{output}" +
                $"{Environment.NewLine}Entity timing Host errors:{Environment.NewLine}{error}");
            throw;
        }
        finally
        {
            if (!host.HasExited)
            {
                host.Kill(entireProcessTree: true);
                await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }
}

async Task ValidateTriggernometryMapEffectProbeAsync()
{
    await PrepareLegacySmokeConfigurationAsync();
    var (host, pipe, session) = await StartConnectedHostAsync(
        loadPlugins: true,
        faultInjection: true);
    await using (pipe)
    using (host)
    {
        try
        {
            _ = await ReadWithTimeoutAsync(pipe);
            var clientSequence = 1L;
            await HostFrameCodec.WriteAsync(
                pipe.Writer,
                HostEnvelope.Create(
                    session,
                    clientSequence++,
                    HostMessageTypes.Hello,
                    HostMessagePriority.Control,
                    new HostHello("mapeffect-probe", "1", Environment.ProcessId, [HostProtocol.CurrentVersion])),
                CancellationToken.None);
            await ReadUntilAsync(pipe, HostMessageTypes.HelloAck, 90);
            await HostFrameCodec.WriteAsync(
                pipe.Writer,
                HostEnvelope.Create(
                    session,
                    clientSequence++,
                    HostMessageTypes.Permissions,
                    HostMessagePriority.Control,
                    new HostPermissionSnapshot(
                        new Dictionary<string, IReadOnlyList<string>>
                        {
                            ["triggernometry"] =
                            [
                                "ReadCombatLogs",
                                "ReadLocalConfiguration",
                                "HighRiskScript",
                            ],
                            ["postnamazu"] =
                            [
                                "ReadCombatLogs",
                                "ReadLocalConfiguration",
                                "GameCommand",
                            ],
                        },
                        ["triggernometry", "postnamazu"]),
                    "mapeffect-permissions"),
                CancellationToken.None);
            await HostFrameCodec.WriteAsync(
                pipe.Writer,
                HostEnvelope.Create(
                    session,
                    clientSequence++,
                    HostMessageTypes.ZoneChanged,
                    HostMessagePriority.Critical,
                    new HostZoneEvent(1122, "The Omega Protocol (Ultimate)", DateTimeOffset.UtcNow)),
                CancellationToken.None);
            var healthEnvelope = await ReadUntilAsync(pipe, HostMessageTypes.Health, 90);
            var health = healthEnvelope.Payload.Deserialize<HostHealth>()
                         ?? throw new InvalidDataException("Host returned no plugin health.");
            Assert(
                health.State == "plugins.ready",
                $"Legacy plugin runtime did not become ready: {health.Detail}");

            async Task PublishLegacyLineAsync(string actLine)
            {
                await HostFrameCodec.WriteAsync(
                    pipe.Writer,
                    HostEnvelope.Create(
                        session,
                        clientSequence++,
                        HostMessageTypes.LogBatch,
                        HostMessagePriority.Data,
                        new[]
                        {
                            // The Host intentionally prefers ActLine for Triggernometry. This lets the
                            // probe exercise the exact 257/101 text shape consumed by the U6b regex.
                            new HostLogEvent(
                                DateTimeOffset.UtcNow,
                                "101|2026-08-20T00:00:00.0000000+08:00|probe|",
                                false,
                                actLine),
                        }),
                    CancellationToken.None);
            }

            async Task CompleteCommandAsync(HostEnvelope envelope)
            {
                Assert(
                    !string.IsNullOrWhiteSpace(envelope.CorrelationId),
                    "MapEffect probe received a command without a correlation identifier.");
                await HostFrameCodec.WriteAsync(
                    pipe.Writer,
                    HostEnvelope.Create(
                        session,
                        clientSequence++,
                        HostMessageTypes.CommandResult,
                        HostMessagePriority.Control,
                        new HostCommandResult(true, "completed", "mapeffect-probe"),
                        envelope.CorrelationId),
                    CancellationToken.None);
            }

            async Task<HashSet<string>> ReadMarkersAsync(int minimumCount, TimeSpan quietWindow)
            {
                var markers = new HashSet<string>(StringComparer.Ordinal);
                var quietUntil = DateTimeOffset.UtcNow + quietWindow;
                while (markers.Count < minimumCount || DateTimeOffset.UtcNow < quietUntil)
                {
                    var remaining = markers.Count < minimumCount
                        ? TimeSpan.FromSeconds(3)
                        : quietUntil - DateTimeOffset.UtcNow;
                    if (remaining <= TimeSpan.Zero)
                    {
                        break;
                    }

                    HostEnvelope? envelope;
                    using (var readCancellation = new CancellationTokenSource(remaining))
                    {
                        try
                        {
                            envelope = await HostFrameCodec.ReadAsync(
                                pipe.Reader,
                                readCancellation.Token);
                        }
                        catch (OperationCanceledException) when (readCancellation.IsCancellationRequested)
                        {
                            break;
                        }
                    }

                    if (envelope is null)
                    {
                        throw new EndOfStreamException("Host pipe closed during the MapEffect probe.");
                    }

                    if (envelope.Type != HostMessageTypes.CommandRequest)
                    {
                        continue;
                    }

                    var request = envelope.Payload.Deserialize<HostCommandRequest>()
                                  ?? throw new InvalidDataException(
                                      "MapEffect probe received an invalid command request.");
                    var payload = request.Arguments.GetValueOrDefault("payload") ?? string.Empty;
                    Assert(
                        request.PluginId == "postnamazu" &&
                        request.Command == "postnamazu.pictoact" &&
                        payload.Contains("Tag: ACTCOMPAT_MAP_", StringComparison.Ordinal),
                        $"MapEffect probe received an unexpected command: {request.PluginId}/{request.Command} {payload}");
                    await CompleteCommandAsync(envelope);

                    if (payload.Contains("Tag: ACTCOMPAT_MAP_STRICT_", StringComparison.Ordinal))
                    {
                        markers.Add("strict");
                    }
                    if (payload.Contains("Tag: ACTCOMPAT_MAP_RELAXED_", StringComparison.Ordinal))
                    {
                        markers.Add("relaxed");
                    }

                    // Once one callback arrives, wait a full scheduling window for a sibling
                    // trigger before concluding that the strict expression remained silent.
                    if (markers.Count >= minimumCount)
                    {
                        quietUntil = DateTimeOffset.UtcNow + quietWindow;
                    }
                }

                Assert(
                    markers.Count >= minimumCount,
                    $"MapEffect probe received only {string.Join(",", markers)} marker(s).");
                return markers;
            }

            await PublishLegacyLineAsync("[02:09:21.900] 257 101:800375AC:00020001:02::");
            var single = await ReadMarkersAsync(2, TimeSpan.FromMilliseconds(150));

            var multiResults = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var scenario in new[]
                     {
                         (Name: "map4", Direction: "04"),
                         (Name: "map8", Direction: "08"),
                         (Name: "map12", Direction: "07"),
                     })
            {
                await PublishLegacyLineAsync(
                    $"[02:09:22.000] 257 101::00020001:{scenario.Direction}::");
                multiResults[scenario.Name] = await ReadMarkersAsync(
                    2,
                    TimeSpan.FromMilliseconds(500));
            }

            Assert(
                single.SetEquals(["strict", "relaxed"]),
                "A single MapEffect line did not match both U6b-style expressions.");
            foreach (var (scenario, markers) in multiResults)
            {
                Assert(
                    markers.SetEquals(["strict", "relaxed"]),
                    $"{scenario} did not match both the patched U6b trigger and control: " +
                    string.Join(",", markers));
            }

            Console.WriteLine(
                "TRIGGERNOMETRY_MAPEFFECT_RESULT=FIXED; " +
                "single(strict=1,relaxed=1); " +
                string.Join(
                    "; ",
                    multiResults.Select(result =>
                        $"{result.Key}(strict={(result.Value.Contains("strict") ? 1 : 0)}," +
                        $"relaxed={(result.Value.Contains("relaxed") ? 1 : 0)})")));

            await HostFrameCodec.WriteAsync(
                pipe.Writer,
                HostEnvelope.Create(
                    session,
                    clientSequence++,
                    HostMessageTypes.Shutdown,
                    HostMessagePriority.Control,
                    new HostHealth("stopping", "MapEffect probe", DateTimeOffset.UtcNow),
                    "mapeffect-shutdown"),
                CancellationToken.None);
            await ReadUntilAsync(pipe, HostMessageTypes.ShutdownAck, 90);
            await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch
        {
            var (output, error) = processLogs.TryGetValue(host.Id, out var log)
                ? log.Snapshot()
                : (string.Empty, string.Empty);
            Console.Error.WriteLine(
                $"MapEffect Host output:{Environment.NewLine}{output}" +
                $"{Environment.NewLine}MapEffect Host errors:{Environment.NewLine}{error}");
            throw;
        }
        finally
        {
            if (!host.HasExited)
            {
                host.Kill(entireProcessTree: true);
                await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }
}

void ValidateTriggernometryLaunchProcessPatch()
{
    var patchDirectory = Path.Combine(
        Path.GetTempPath(),
        "DalamudActCompat",
        "triggernometry");
    var patchedAssembly = new DirectoryInfo(patchDirectory)
                              .EnumerateFiles("TriggernometryPlugin-*.dll")
                              .OrderByDescending(file => file.LastWriteTimeUtc)
                              .FirstOrDefault()
                          ?? throw new FileNotFoundException(
                              "No patched Triggernometry implementation was produced.",
                              patchDirectory);
    using var definition = AssemblyDefinition.ReadAssembly(patchedAssembly.FullName);
    var repositoryType = definition.MainModule.Types
        .SelectMany(EnumerateCecilTypes)
        .Single(type => type.FullName == "Triggernometry.Core.Repository");
    var realPluginType = definition.MainModule.Types
        .SelectMany(EnumerateCecilTypes)
        .Single(type => type.FullName == "Triggernometry.Core.RealPlugin");
    var loadDefaultRepository = realPluginType.Methods.Single(method =>
        method.Name == "LoadDefaultRepoCN" && method.Parameters.Count == 1);
    var tryLoadLocalBackup = repositoryType.Methods.Single(method =>
        method.Name == "TryLoadLocalBackup" && method.Parameters.Count == 0);
    var saveLocalBackup = repositoryType.Methods.Single(method =>
        method.Name == "SaveLocalBackup" && method.Parameters.Count == 1);
    var repositoryUpdateMoveNext = repositoryType.NestedTypes
        .Single(type => type.Name.StartsWith("<CheckAndUpdateAsync>d__", StringComparison.Ordinal))
        .Methods
        .Single(method => method.Name == "MoveNext");
    static int CountCalls(MethodDefinition method, string declaringType, string methodName)
        => method.Body.Instructions.Count(instruction =>
            instruction.Operand is MethodReference called &&
            called.DeclaringType.FullName == declaringType &&
            called.Name == methodName);

    Assert(
        CountCalls(
            loadDefaultRepository,
            typeof(HostPluginBridge).FullName!,
            nameof(HostPluginBridge.IsTriggernometryNetworkAllowed)) == 1 &&
        CountCalls(tryLoadLocalBackup, typeof(File).FullName!, nameof(File.Exists)) == 1 &&
        CountCalls(tryLoadLocalBackup, typeof(File).FullName!, nameof(File.ReadAllBytes)) == 1 &&
        CountCalls(
            tryLoadLocalBackup,
            "Triggernometry.Core.TriggernometryExport",
            "Unserialize") == 1 &&
        CountCalls(
            tryLoadLocalBackup,
            "Triggernometry.Core.Repository",
            "AddContentFromExport") == 1 &&
        CountCalls(saveLocalBackup, typeof(Directory).FullName!, nameof(Directory.CreateDirectory)) == 1 &&
        CountCalls(saveLocalBackup, typeof(File).FullName!, nameof(File.WriteAllBytes)) == 1 &&
        CountCalls(
            repositoryUpdateMoveNext,
            "Triggernometry.Core.Repository",
            "get_KeepLocalBackup") >= 2 &&
        CountCalls(
            repositoryUpdateMoveNext,
            "Triggernometry.Core.Repository",
            "TryLoadLocalBackup") >= 2,
        "Triggernometry's repository path bypassed the network gate or no longer preserves its local XML backup on startup/update failure.");

    var exportUnserialize = definition.MainModule.Types
        .SelectMany(EnumerateCecilTypes)
        .Single(type => type.FullName == "Triggernometry.Core.TriggernometryExport")
        .Methods
        .Single(method =>
            method.Name == "Unserialize" &&
            method.Parameters.Count == 1 &&
            method.Parameters[0].ParameterType.MetadataType == MetadataType.String);
    var entitySetHeading = definition.MainModule.Types
        .SelectMany(EnumerateCecilTypes)
        .Single(type => type.FullName ==
            "Triggernometry.PluginBridges.BridgeNamazu.Modules.EntityModule")
        .Methods
        .Single(method =>
            method.Name == "SetHeading" &&
            method.Parameters.Count == 2 &&
            method.Parameters[0].ParameterType.FullName == typeof(IntPtr).FullName &&
            method.Parameters[1].ParameterType.MetadataType == MetadataType.Single);
    Assert(
        CountCalls(
            exportUnserialize,
            typeof(HostPluginBridge).FullName!,
            nameof(HostPluginBridge.PatchTriggernometryExportXml)) == 1 &&
        CountCalls(
            entitySetHeading,
            typeof(HostPluginBridge).FullName!,
            nameof(HostPluginBridge.SendPostNamazuSetHeading)) == 1 &&
        entitySetHeading.Body.Instructions.Count == 4,
        "Triggernometry did not route U6b repository import and local-player heading through the guarded compatibility bridge.");

    var launchMethod = definition.MainModule.Types
        .SelectMany(EnumerateCecilTypes)
        .Single(type =>
            type.FullName == "Triggernometry.Core.Actions.ActionLaunchProcess")
        .Methods
        .Single(method =>
            method.Name == "ExecuteImplementation" &&
            method.Parameters.Count == 1);
    var bridgeCalls = launchMethod.Body.Instructions.Count(instruction =>
        instruction.Operand is MethodReference called &&
        called.DeclaringType.FullName == typeof(HostPluginBridge).FullName &&
        called.Name == nameof(HostPluginBridge.StartTriggernometryProcess) &&
        called.Parameters.Count == 1 &&
        called.Parameters[0].ParameterType.FullName == typeof(ProcessStartInfo).FullName);
    var legacyInstanceStarts = launchMethod.Body.Instructions.Count(instruction =>
        instruction.Operand is MethodReference called &&
        called.DeclaringType.FullName == typeof(Process).FullName &&
        called.Name == nameof(Process.Start) &&
        called.HasThis &&
        called.Parameters.Count == 0);
    var hasNullGuard = launchMethod.Body.Instructions.Any(instruction =>
        instruction.OpCode.Code is Code.Brfalse or Code.Brfalse_S &&
        instruction.Operand is Instruction target &&
        target.OpCode.Code == Code.Ret);
    Assert(
        bridgeCalls == 1 && legacyInstanceStarts == 0 && hasNullGuard,
        "Triggernometry LaunchProcess did not route its instance Start path through " +
        $"the guarded Host bridge: bridge={bridgeCalls}, legacy={legacyInstanceStarts}, " +
        $"nullGuard={hasNullGuard}.");

    var bridgeNamazuInitializer = definition.MainModule.Types
        .SelectMany(EnumerateCecilTypes)
        .Single(type =>
            type.FullName == "Triggernometry.PluginBridges.BridgeNamazu.BridgeNamazu")
        .Methods
        .Single(method => method.IsConstructor && method.IsStatic);
    var noticeBridgeCalls = bridgeNamazuInitializer.Body.Instructions.Count(instruction =>
        instruction.Operand is MethodReference called &&
        called.DeclaringType.FullName == typeof(HostPluginBridge).FullName &&
        called.Name == nameof(
            HostPluginBridge.CheckTriggernometryPostNamazuAdministratorRequirement));
    var realAdministratorChecks = bridgeNamazuInitializer.Body.Instructions.Count(instruction =>
        instruction.Operand is MethodReference called &&
        called.DeclaringType.FullName == "Triggernometry.Core.RealPlugin" &&
        called.Name == "IsAdmin" &&
        called.Parameters.Count == 0);
    Assert(
        noticeBridgeCalls == 1 && realAdministratorChecks == 0,
        "The Triggernometry/PostNamazu administrator notice did not route through " +
        $"the native-runtime-aware token check: bridge={noticeBridgeCalls}, " +
        $"realChecks={realAdministratorChecks}.");

    var bridgeNamazu = definition.MainModule.Types
        .SelectMany(EnumerateCecilTypes)
        .Single(type =>
            type.FullName == "Triggernometry.PluginBridges.BridgeNamazu.BridgeNamazu");
    var wrappedPluginGetter = bridgeNamazu.Methods.Single(method =>
        method.Name == "get_WrappedPlugin" && method.Parameters.Count == 0);
    var pluginObjectReads = wrappedPluginGetter.Body.Instructions.Count(instruction =>
        instruction.Operand is MethodReference called &&
        called.DeclaringType.FullName == "Triggernometry.Core.RealPlugin/PluginWrapper" &&
        called.Name == "get_pluginObj");
    var instanceHookCalls = wrappedPluginGetter.Body.Instructions.Count(instruction =>
        instruction.Operand is MethodReference called &&
        called.DeclaringType.FullName == "Triggernometry.Core.RealPlugin/InstanceDelegate" &&
        called.Name == "Invoke");
    var correctedPluginNames = wrappedPluginGetter.Body.Instructions.Count(instruction =>
        instruction.OpCode.Code == Code.Ldstr &&
        string.Equals(instruction.Operand as string, "PostNamazu.dll", StringComparison.Ordinal));
    var misspelledPluginNames = wrappedPluginGetter.Body.Instructions.Count(instruction =>
        instruction.OpCode.Code == Code.Ldstr &&
        string.Equals(instruction.Operand as string, "PostNamzu.dll", StringComparison.Ordinal));
    Assert(
        pluginObjectReads == 1 &&
        instanceHookCalls == 1 &&
        correctedPluginNames == 1 &&
        misspelledPluginNames == 0,
        "Triggernometry did not retry the PostNamazu wrapper after an early empty lookup: " +
        $"pluginObjectReads={pluginObjectReads}, hooks={instanceHookCalls}, " +
        $"correctNames={correctedPluginNames}, misspelledNames={misspelledPluginNames}.");

    var disableCactbotTriggerSetTts = definition.MainModule.Types
        .SelectMany(EnumerateCecilTypes)
        .Single(type => type.FullName == "Triggernometry.PluginBridges.BridgeCactbot")
        .Methods
        .Single(method =>
            method.Name == "DisableTriggerSetTts" && method.Parameters.Count == 2);
    var compatibilityBridgeCalls = disableCactbotTriggerSetTts.Body.Instructions.Count(instruction =>
        instruction.Operand is MethodReference called &&
        called.DeclaringType.FullName == typeof(HostPluginBridge).FullName);

    // U7b intentionally delegates duplicate-suppression to Triggernometry. Comparing the real
    // upstream and rewritten method bodies ensures the compatibility layer preserves that behavior.
    using var outerDefinition = AssemblyDefinition.ReadAssembly(triggernometryAssembly!);
    var implementationResource = outerDefinition.MainModule.Resources
                                     .OfType<EmbeddedResource>()
                                     .Single(resource => resource.Name ==
                                         "costura.triggernometryplugin.dll.compressed");
    using var compressedImplementation = implementationResource.GetResourceStream();
    using var decompressor = new DeflateStream(
        compressedImplementation,
        CompressionMode.Decompress);
    using var originalImage = new MemoryStream();
    decompressor.CopyTo(originalImage);
    originalImage.Position = 0;
    using var originalDefinition = AssemblyDefinition.ReadAssembly(originalImage);
    var originalDisableCactbotTriggerSetTts = originalDefinition.MainModule.Types
        .SelectMany(EnumerateCecilTypes)
        .Single(type => type.FullName == "Triggernometry.PluginBridges.BridgeCactbot")
        .Methods
        .Single(method =>
            method.Name == "DisableTriggerSetTts" && method.Parameters.Count == 2);
    var originalBody = string.Join(
        '\n',
        originalDisableCactbotTriggerSetTts.Body.Instructions.Select(static instruction =>
            instruction.ToString()));
    var rewrittenBody = string.Join(
        '\n',
        disableCactbotTriggerSetTts.Body.Instructions.Select(static instruction =>
            instruction.ToString()));
    Assert(
        compatibilityBridgeCalls == 0 &&
        string.Equals(originalBody, rewrittenBody, StringComparison.Ordinal),
        "Triggernometry's Cactbot TTS control was changed by the compatibility rewrite.");
}

void ValidateMatchaAssemblyContract(string packagePath)
{
    var temporaryRoot = Path.Combine(
        Path.GetTempPath(),
        $"DalamudActCompat-Matcha-Rewrite-{Guid.NewGuid():N}");
    Directory.CreateDirectory(temporaryRoot);
    try
    {
        ZipFile.ExtractToDirectory(packagePath, temporaryRoot);
        var assemblyPath = Path.Combine(
            temporaryRoot,
            "Plugins",
            "Cafe.Matcha",
            "Cafe.Matcha.dll");
        var upstreamPath = Path.Combine(
            temporaryRoot,
            "Plugins",
            "Cafe.Matcha",
            "upstream",
            "Cafe.Matcha.Upstream.dll");
        var runtimeDataPath = Path.Combine(
            temporaryRoot,
            "Plugins",
            "Cafe.Matcha",
            "upstream",
            "Cafe.Matcha.Runtime.bin");
        var originalHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(assemblyPath)));
        Assert(
            originalHash == "3DF088E73DD8A314A08A1B302A2FEFE9BFEFC1A52FCE54032F719421CF7810FA",
            "The bundled Matcha entry DLL does not match its disclosed fixed hash.");
        Assert(
            Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(upstreamPath))) ==
            "EF485B027FE84150768A8498331BEFCE5C997047FADF7B38B766EC9703818ED6",
            "The bundled Matcha upstream companion does not match its disclosed fixed hash.");
        Assert(
            Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(runtimeDataPath))) ==
            "D8D134DDBBE60E82C6C3C28C8058446380F5C6BABD73A2666E9575E1E0C44200",
            "The sealed Matcha runtime data does not match its disclosed fixed hash.");

        ValidateLoadedMatchaRuntime(assemblyPath);

        using var definition = AssemblyDefinition.ReadAssembly(assemblyPath);
        var bridgeType = definition.MainModule.Types
            .SelectMany(EnumerateCecilTypes)
            .Single(type => type.FullName == "Cafe.Matcha.Utils.DactBridge");
        Assert(
            bridgeType.Fields.Any(field =>
                field.Name == "ContractVersion" &&
                field.IsLiteral &&
                string.Equals(field.Constant as string, "3", StringComparison.Ordinal)),
            "Matcha does not disclose the expected DACT bridge contract.");
        var methods = definition.MainModule.Types
            .SelectMany(EnumerateCecilTypes)
            .SelectMany(type => type.Methods)
            .Where(method => method.HasBody)
            .ToArray();
        var callsOutsideBridge = methods
            .Where(method => method.DeclaringType != bridgeType)
            .SelectMany(method => method.Body.Instructions)
            .Select(instruction => instruction.Operand)
            .OfType<MethodReference>()
            .ToArray();
        var readBridge = callsOutsideBridge.Count(call =>
            call.DeclaringType.FullName == bridgeType.FullName &&
            call.Name == "ReadAllText");
        var writeBridge = callsOutsideBridge.Count(call =>
            call.DeclaringType.FullName == bridgeType.FullName &&
            call.Name == "WriteAllText");
        var userReadBridge = callsOutsideBridge.Count(call =>
            call.DeclaringType.FullName == bridgeType.FullName &&
            call.Name == "ReadUserTextFile");
        var userWriteBridge = callsOutsideBridge.Count(call =>
            call.DeclaringType.FullName == bridgeType.FullName &&
            call.Name == "WriteUserTextFile");
        var processBridge = callsOutsideBridge.Count(call =>
            call.DeclaringType.FullName == bridgeType.FullName &&
            call.Name == "StartProcess");
        var networkDemand = callsOutsideBridge.Count(call =>
            call.DeclaringType.FullName == bridgeType.FullName &&
            call.Name == "Demand");
        var notificationBridge = callsOutsideBridge.Count(call =>
            call.DeclaringType.FullName == bridgeType.FullName &&
            call.Name == "SendNotification");
        var directFileIo = callsOutsideBridge.Count(call =>
            call.DeclaringType.FullName == typeof(File).FullName &&
            call.Name is nameof(File.ReadAllText) or nameof(File.WriteAllText));
        var directProcessStarts = callsOutsideBridge.Count(call =>
            call.DeclaringType.FullName == typeof(Process).FullName &&
            call.Name == nameof(Process.Start));
        Assert(
            readBridge == 2 && writeBridge == 1 &&
            userReadBridge == 1 && userWriteBridge == 1 && processBridge == 4 &&
            networkDemand == 1 && notificationBridge == 1 &&
            directFileIo == 0 && directProcessStarts == 0,
            "Matcha did not preserve its exact source bridge surface: " +
            $"read={readBridge}, write={writeBridge}, " +
            $"userRead={userReadBridge}, userWrite={userWriteBridge}, process={processBridge}, " +
            $"network={networkDemand}, notification={notificationBridge}, " +
            $"directFile={directFileIo}, directProcess={directProcessStarts}.");
        Assert(
            Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(assemblyPath))) ==
            originalHash,
            "Matcha compatibility validation changed the installed DLL.");

        ValidateMatchaNotificationRouting();
        ValidateMatchaUserTemplateFileBoundary();
    }
    finally
    {
        Directory.Delete(temporaryRoot, recursive: true);
    }

    static void ValidateLoadedMatchaRuntime(string assemblyPath)
    {
        var context = new AssemblyLoadContext(
            $"matcha-contract-{Guid.NewGuid():N}",
            isCollectible: true);
        var loadedAssembly = LegacyAssemblyRewriter.LoadMatcha(assemblyPath, context);
        if (loadedAssembly.GetName().Name != "Cafe.Matcha" ||
            !string.IsNullOrEmpty(loadedAssembly.Location))
        {
            throw new InvalidOperationException(
                "Matcha was not loaded from its validated non-locking image.");
        }

        var runtimeConstants = loadedAssembly
            .GetType("Cafe.Matcha.Constant.Secret", throwOnError: true)!
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(string))
            .ToDictionary(
                field => field.Name,
                field => field.GetValue(null) as string ?? string.Empty,
                StringComparer.Ordinal);
        if (!runtimeConstants.TryGetValue("TelemetryRoot", out var telemetryRoot) ||
            !Uri.TryCreate(telemetryRoot, UriKind.Absolute, out var telemetryUri) ||
            telemetryUri.Scheme != Uri.UriSchemeHttps ||
            !runtimeConstants.TryGetValue("UniversalisKey", out var universalisKey) ||
            string.IsNullOrWhiteSpace(universalisKey) ||
            !runtimeConstants.TryGetValue("TelemetryFate", out var telemetryFate) ||
            !Guid.TryParse(telemetryFate, out _) ||
            !runtimeConstants.TryGetValue("TelemetryNpc", out var telemetryNpc) ||
            !Guid.TryParse(telemetryNpc, out _))
        {
            throw new InvalidOperationException(
                "Matcha did not receive the hash-pinned upstream runtime constants.");
        }

        var opcodeStorage = loadedAssembly.GetType(
                                "Cafe.Matcha.Constant.OpcodeStorage",
                                throwOnError: true)!
                            ?? throw new TypeLoadException(
                                "Cafe.Matcha.Constant.OpcodeStorage");
        var globalOpcodes = opcodeStorage.GetField(
                                "Global",
                                BindingFlags.Public | BindingFlags.Static)!
                                .GetValue(null) as IDictionary
                            ?? throw new InvalidDataException(
                                "Matcha Global opcode storage is not a dictionary.");
        var expectedGlobalOpcodes = new Dictionary<ushort, string>
        {
            [0x0096] = "ActorControl",
            [0x037C] = "ActorControlSelf",
            [0x027D] = "CEDirector",
            [0x012E] = "CompanyAirshipStatus",
            [0x03AF] = "CompanySubmersibleStatus",
            [0x0197] = "ContentFinderNotifyPop",
            [0x02C7] = "ResumeEventScene32",
            [0x01A5] = "EventPlay",
            [0x0278] = "EventStart",
            [0x0097] = "Examine",
            [0x0161] = "InitZone",
            [0x0104] = "InventoryTransaction",
            [0x0204] = "ItemInfo",
            [0x0190] = "MarketBoardItemListing",
            [0x022F] = "MarketBoardItemListingCount",
            [0x017B] = "MarketBoardItemListingHistory",
            [0x835B] = "MarketBoardRequestItemListingInfo",
            [0x00E9] = "NpcSpawn",
            [0x00A6] = "PlayerSetup",
            [0x032D] = "PlayerSpawn",
            [0x01A2] = "SubmarineStatusList",
        };
        Assert(
            globalOpcodes.Count == expectedGlobalOpcodes.Count &&
            expectedGlobalOpcodes.All(pair =>
                string.Equals(
                    globalOpcodes[pair.Key]?.ToString(),
                    pair.Value,
                    StringComparison.Ordinal)),
            "Matcha Global opcode storage was not normalized to the verified 7.55h2 table.");

        var bridge = loadedAssembly.GetType(
                         "Cafe.Matcha.Utils.DactBridge",
                         throwOnError: true)!
                     ?? throw new TypeLoadException("Matcha DACT bridge was not loaded.");
        var isAvailable = bridge.GetProperty(
                              "IsAvailable",
                              BindingFlags.Public | BindingFlags.Static)
                          ?? throw new MissingMemberException(
                              bridge.FullName,
                              "IsAvailable");
        Assert(
            isAvailable.GetValue(null) is true,
            "Matcha loaded from a byte image could not find the already-running Host bridge.");

        var configurePermissions = typeof(HostPluginBridge).GetMethod(
                                       "ConfigurePermissions",
                                       BindingFlags.Static | BindingFlags.NonPublic)
                                   ?? throw new MissingMethodException(
                                       typeof(HostPluginBridge).FullName,
                                       "ConfigurePermissions");
        var configureSender = typeof(HostPluginBridge).GetMethod(
                                  "Configure",
                                  BindingFlags.Static | BindingFlags.NonPublic)
                              ?? throw new MissingMethodException(
                                  typeof(HostPluginBridge).FullName,
                                  "Configure");
        configurePermissions.Invoke(null, [new HostPermissionSnapshot(
            new Dictionary<string, IReadOnlyList<string>>
            {
                ["matcha"] = ["ReadCombatLogs"],
            },
            ["matcha"])]);
        var notificationPayloads = new List<HostMatchaNotification>();
        Func<string, HostMessagePriority, object, string?, DateTimeOffset?, bool> sender =
            (type, _, payload, _, _) =>
            {
                if (type == HostMessageTypes.MatchaNotification &&
                    payload is HostMatchaNotification notification)
                {
                    notificationPayloads.Add(notification);
                }
                return true;
            };
        configureSender.Invoke(null, [sender]);
        var matchaEventType = loadedAssembly.GetType(
                                  "Cafe.Matcha.Constant.EventType",
                                  throwOnError: true)!
                              ?? throw new TypeLoadException("Cafe.Matcha.Constant.EventType");
        var sendNativeToast = loadedAssembly
                                  .GetType("Cafe.Matcha.Utils.Output", throwOnError: true)!
                                  .GetMethod(
                                      "SendNativeToast",
                                      BindingFlags.Static | BindingFlags.NonPublic,
                                      binder: null,
                                      types: [typeof(string), matchaEventType],
                                      modifiers: null)
                              ?? throw new MissingMethodException(
                                  "Cafe.Matcha.Utils.Output",
                                  "SendNativeToast");
        sendNativeToast.Invoke(null, [
            "Matcha world bridge smoke",
            Enum.Parse(matchaEventType, "InitZone"),
        ]);
        sendNativeToast.Invoke(null, [
            "Matcha duty bridge smoke",
            Enum.Parse(matchaEventType, "MatchAlert"),
        ]);
        Assert(
            notificationPayloads is
            [
                {
                    Message: "Matcha world bridge smoke",
                    Kind: HostMatchaNotificationKind.WorldChanged,
                },
                {
                    Message: "Matcha duty bridge smoke",
                    Kind: HostMatchaNotificationKind.DutyEntered,
                },
            ],
            "Matcha's real native-toast entry point did not preserve world/duty event kinds on the typed Host notification route.");

        context.Unload();
    }
}

async Task ValidateGenericPluginLoadsOnlyAfterConsentAsync()
{
    var temporaryRoot = Path.Combine(
        Path.GetTempPath(),
        $"DalamudActCompat-Generic-Host-{Guid.NewGuid():N}");
    var temporaryPluginRoot = Path.Combine(temporaryRoot, "plugins");
    var temporaryConfigRoot = Path.Combine(temporaryRoot, "config");
    Directory.CreateDirectory(temporaryPluginRoot);
    Directory.CreateDirectory(Path.Combine(temporaryConfigRoot, "Config"));
    try
    {
        foreach (var pluginId in new[] { "community.allowed", "community.denied" })
        {
            var installRoot = Path.Combine(temporaryPluginRoot, pluginId);
            Directory.CreateDirectory(installRoot);
            var entryAssembly = Path.Combine(
                installRoot,
                Path.GetFileName(Assembly.GetExecutingAssembly().Location));
            File.Copy(Assembly.GetExecutingAssembly().Location, entryAssembly);
            await File.WriteAllTextAsync(
                Path.Combine(installRoot, "actcompat.plugin.json"),
                JsonSerializer.Serialize(new
                {
                    id = pluginId,
                    name = pluginId,
                    version = "1.0.0",
                    entryAssembly = Path.GetFileName(entryAssembly),
                    entryType = typeof(GenericActPluginHostFixture).FullName,
                    hostApiVersion = 1,
                }));
        }

        var (host, pipe, session) = await StartConnectedHostAsync(
            loadPlugins: true,
            pluginRootOverride: temporaryPluginRoot,
            configRootOverride: temporaryConfigRoot);
        await using (pipe)
        using (host)
        {
            _ = await ReadWithTimeoutAsync(pipe);
            await HostFrameCodec.WriteAsync(
                pipe.Writer,
                HostEnvelope.Create(
                    session,
                    1,
                    HostMessageTypes.Hello,
                    HostMessagePriority.Control,
                    new HostHello(
                        "game-bridge",
                        "1",
                        Environment.ProcessId,
                        [HostProtocol.CurrentVersion])),
                CancellationToken.None);
            await ReadUntilAsync(pipe, HostMessageTypes.HelloAck);
            await HostFrameCodec.WriteAsync(
                pipe.Writer,
                HostEnvelope.Create(
                    session,
                    2,
                    HostMessageTypes.Permissions,
                    HostMessagePriority.Control,
                    new HostPermissionSnapshot(
                        new Dictionary<string, IReadOnlyList<string>>
                        {
                            ["community.allowed"] =
                            [
                                "ReadCombatLogs",
                                "ReadLocalConfiguration",
                            ],
                        },
                        ["community.allowed"])),
                CancellationToken.None);
            var healthEnvelope = await ReadUntilAsync(pipe, HostMessageTypes.Health, 30);
            var health = healthEnvelope.Payload.Deserialize<HostHealth>()
                         ?? throw new InvalidDataException(
                             "Generic Host returned no health state.");
            Assert(
                health.State == "plugins.ready" &&
                health.Detail.Contains("community.allowed", StringComparison.OrdinalIgnoreCase),
                $"Authorized generic plugin did not initialize: {health.State}: {health.Detail}");

            var heartbeatEnvelope = await ReadUntilAsync(pipe, HostMessageTypes.Heartbeat, 30);
            var heartbeat = heartbeatEnvelope.Payload.Deserialize<HostHeartbeat>()
                            ?? throw new InvalidDataException(
                                "Generic Host returned no heartbeat state.");
            Assert(
                heartbeat.Stages.Any(stage =>
                    stage.PluginId == "community.allowed" &&
                    stage.Stage == "InitPlugin" &&
                    stage.State == "success"),
                "Authorized generic plugin did not report a successful InitPlugin stage.");
            Assert(
                !heartbeat.Stages.Any(stage =>
                    stage.PluginId == "community.denied" &&
                    stage.State == "success"),
                "Untrusted generic plugin entered the shared Host's successful stage set.");

            await HostFrameCodec.WriteAsync(
                pipe.Writer,
                HostEnvelope.Create(
                    session,
                    3,
                    HostMessageTypes.Shutdown,
                    HostMessagePriority.Control,
                    new HostHealth(
                        "stopping",
                        "generic plugin consent smoke",
                        DateTimeOffset.UtcNow)),
                CancellationToken.None);
            await ReadUntilAsync(pipe, HostMessageTypes.ShutdownAck, 30);
            await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var (output, error) = ReadProcessLog(host);
            Assert(
                host.ExitCode == 0 &&
                output.Contains(
                    "Legacy plugin 'community.allowed' loaded out-of-process.",
                    StringComparison.Ordinal) &&
                !output.Contains(
                    "Legacy plugin 'community.denied' loaded out-of-process.",
                    StringComparison.Ordinal),
                $"Generic Host consent boundary failed.{Environment.NewLine}{output}{Environment.NewLine}{error}");
        }
    }
    finally
    {
        await DeleteTemporaryDirectoryWithRetryAsync(temporaryRoot);
    }
}

static async Task DeleteTemporaryDirectoryWithRetryAsync(string path)
{
    for (var attempt = 0; attempt < 10; attempt++)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
            return;
        }
        catch (Exception ex) when (
            attempt < 9 && ex is IOException or UnauthorizedAccessException)
        {
            // Windows can retain a just-unloaded plugin image briefly after Host exit.
            await Task.Delay(100);
        }
    }
}

void ValidateMatchaNotificationRouting()
{
    var configurePermissions = typeof(HostPluginBridge).GetMethod(
                                   "ConfigurePermissions",
                                   BindingFlags.Static | BindingFlags.NonPublic)
                               ?? throw new MissingMethodException(
                                   typeof(HostPluginBridge).FullName,
                                   "ConfigurePermissions");
    var configureWindowsWriter = typeof(HostPluginBridge).GetMethod(
                                     "ConfigureMatchaNotificationWriter",
                                     BindingFlags.Static | BindingFlags.NonPublic)
                                 ?? throw new MissingMethodException(
                                     typeof(HostPluginBridge).FullName,
                                     "ConfigureMatchaNotificationWriter");
    var configureSender = typeof(HostPluginBridge).GetMethod(
                              "Configure",
                              BindingFlags.Static | BindingFlags.NonPublic)
                          ?? throw new MissingMethodException(
                              typeof(HostPluginBridge).FullName,
                              "Configure");
    configurePermissions.Invoke(null, [new HostPermissionSnapshot(
        new Dictionary<string, IReadOnlyList<string>>
        {
            ["matcha"] = ["ReadCombatLogs"],
        },
        ["matcha"])]);

    var windowsCalls = 0;
    var fallbackCalls = 0;
    string? fallbackType = null;
    object? fallbackPayload = null;
    Func<string, HostMessagePriority, object, string?, DateTimeOffset?, bool> sender =
        (type, _, payload, _, _) =>
        {
            fallbackCalls++;
            fallbackType = type;
            fallbackPayload = payload;
            return true;
        };
    configureSender.Invoke(null, [sender]);
    configureWindowsWriter.Invoke(null, [(Func<string, bool>)(_ =>
    {
        windowsCalls++;
        return true;
    })]);
    Assert(
        HostPluginBridge.SendMatchaNotification("Windows first") &&
        windowsCalls == 1 && fallbackCalls == 0,
        "Matcha notification did not prefer its dedicated Host Windows channel.");

    configureWindowsWriter.Invoke(null, [(Func<string, bool>)(_ => false)]);
    Assert(
        HostPluginBridge.SendMatchaNotification("Typed fallback", "MatchAlert") &&
        fallbackCalls == 1 &&
        fallbackType == HostMessageTypes.MatchaNotification &&
        fallbackPayload is HostMatchaNotification
        {
            Message: "Typed fallback",
            Kind: HostMatchaNotificationKind.DutyEntered,
        },
        "Matcha notification did not use its typed game-side fallback channel.");

    Func<string, HostMessagePriority, object, string?, DateTimeOffset?, bool> rejectingSender =
        (_, _, _, _, _) => false;
    configureSender.Invoke(null, [rejectingSender]);
    var previousError = Console.Error;
    using var errorOutput = new StringWriter();
    try
    {
        Console.SetError(errorOutput);
        Assert(
            !HostPluginBridge.SendMatchaNotification("Rejected fallback") &&
            errorOutput.ToString().Contains(
                "typed game-side notification fallback rejected",
                StringComparison.Ordinal),
            "Matcha did not log a rejected typed notification fallback.");
    }
    finally
    {
        // Console.Error is process-wide, so restore it before later smoke checks run.
        Console.SetError(previousError);
    }

    configureSender.Invoke(null, [sender]);
    configureWindowsWriter.Invoke(null, [null]);
}

void ValidateMatchaUserTemplateFileBoundary()
{
    var root = Path.Combine(
        Path.GetTempPath(),
        $"DalamudActCompat-Matcha-Template-{Guid.NewGuid():N}");
    var pluginRoot = Path.Combine(root, "plugin");
    var configRoot = Path.Combine(root, "config");
    var userRoot = Path.Combine(root, "user-selected");
    Directory.CreateDirectory(pluginRoot);
    Directory.CreateDirectory(configRoot);
    Directory.CreateDirectory(userRoot);
    var configurePermissions = typeof(HostPluginBridge).GetMethod(
                                   "ConfigurePermissions",
                                   BindingFlags.Static | BindingFlags.NonPublic)
                               ?? throw new MissingMethodException(
                                   typeof(HostPluginBridge).FullName,
                                   "ConfigurePermissions");
    var configureContext = typeof(HostPluginBridge).GetMethod(
                               "ConfigureMatchaContext",
                               BindingFlags.Static | BindingFlags.NonPublic)
                           ?? throw new MissingMethodException(
                               typeof(HostPluginBridge).FullName,
                               "ConfigureMatchaContext");
    var clearContext = typeof(HostPluginBridge).GetMethod(
                           "ClearMatchaContext",
                           BindingFlags.Static | BindingFlags.NonPublic)
                       ?? throw new MissingMethodException(
                           typeof(HostPluginBridge).FullName,
                           "ClearMatchaContext");
    try
    {
        configurePermissions.Invoke(null, [new HostPermissionSnapshot(
            new Dictionary<string, IReadOnlyList<string>>
            {
                ["matcha"] = ["ReadLocalConfiguration"],
            },
            ["matcha"])]);
        configureContext.Invoke(null, [pluginRoot, configRoot, null]);

        var configurationPath = Path.Combine(configRoot, "Config", "Cafe.Matcha.config");
        HostPluginBridge.WriteMatchaTextFile(configurationPath, "{\"telemetry\":false}");
        Assert(
            File.ReadAllText(configurationPath) == "{\"telemetry\":false}",
            "Matcha could not persist its path-confined configuration without arbitrary file-write permission.");

        var templatePath = Path.Combine(userRoot, "watch-list.json");
        AssertThrows<UnauthorizedAccessException>(
            () => HostPluginBridge.WriteMatchaUserTextFile(templatePath, "[1,2,3]"),
            "Matcha exported a user-selected JSON template without WriteFiles permission.");
        configurePermissions.Invoke(null, [new HostPermissionSnapshot(
            new Dictionary<string, IReadOnlyList<string>>
            {
                ["matcha"] = ["ReadLocalConfiguration", "WriteFiles"],
            },
            ["matcha"])]);
        HostPluginBridge.WriteMatchaUserTextFile(templatePath, "[1,2,3]");
        Assert(
            HostPluginBridge.ReadMatchaUserTextFile(templatePath) == "[1,2,3]",
            "Matcha could not round-trip a JSON template explicitly selected by the user.");

        AssertThrows<UnauthorizedAccessException>(
            () => HostPluginBridge.WriteMatchaUserTextFile(
                Path.Combine(userRoot, "watch-list.txt"),
                "not-json"),
            "Matcha user-selected file bridge accepted a non-JSON path.");
        AssertThrows<UnauthorizedAccessException>(
            () => HostPluginBridge.WriteMatchaTextFile(
                Path.Combine(userRoot, "generic-write.json"),
                "{}"),
            "Matcha generic configuration bridge escaped its assigned config root.");
    }
    finally
    {
        clearContext.Invoke(null, null);
        configurePermissions.Invoke(null, [new HostPermissionSnapshot(
            new Dictionary<string, IReadOnlyList<string>>(),
            [])]);
        Directory.Delete(root, recursive: true);
    }
}

void ValidateSilverDasherAssemblyRewrite(string sourceRoot)
{
    var loaderPath = Directory.EnumerateFiles(
            sourceRoot,
            "SilverDasher.dll",
            SearchOption.AllDirectories)
        .Single();
    var corePath = Directory.EnumerateFiles(
            sourceRoot,
            "SilverDasher.Core.dll",
            SearchOption.AllDirectories)
        .Single();
    var zodiarkPath = Directory.EnumerateFiles(
            sourceRoot,
            "SilverDasher.ManagedZodiark.dll",
            SearchOption.AllDirectories)
        .Single();
    var weaverPath = Directory.EnumerateFiles(
            sourceRoot,
            "SilverDasher.Weaver.dll",
            SearchOption.AllDirectories)
        .Single();
    var loaderHash = Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(loaderPath)));
    var coreHash = Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(corePath)));
    var zodiarkHash = Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(zodiarkPath)));
    var weaverHash = Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(weaverPath)));

    _ = LegacyAssemblyRewriter.LoadSilverDasher(loaderPath, AssemblyLoadContext.Default);
    var rewrittenCore = LegacyAssemblyRewriter.LoadSilverDasherCore(corePath);
    _ = LegacyAssemblyRewriter.LoadSilverDasherManagedZodiark(zodiarkPath);

    var configurePermissions = typeof(HostPluginBridge).GetMethod(
                                   "ConfigurePermissions",
                                   BindingFlags.Static | BindingFlags.NonPublic)
                               ?? throw new MissingMethodException(
                                   typeof(HostPluginBridge).FullName,
                                   "ConfigurePermissions");
    configurePermissions.Invoke(
        null,
        [
            new HostPermissionSnapshot(
                new Dictionary<string, IReadOnlyList<string>>
                {
                    ["silverdasher"] =
                    [
                        "NativeGameMemory",
                        "ReadCombatLogs",
                        "ReadLocalConfiguration",
                        "NetworkRequest",
                    ],
                },
                ["silverdasher"]),
        ]);
    ValidateSilverDasherNotificationRouting();
    var normalizedPayload = JObject.Parse(
        HostPluginBridge.NormalizeSilverDasherMqttPayload(
            "{\"i\":\"0\",\"hp\":\"100\",\"m\":\"816\",\"c\":{\"x\":\"958\",\"y\":\"3112\"}}"));
    Assert(
        normalizedPayload["i"]?.Type == JTokenType.Integer &&
        normalizedPayload["hp"]?.Type == JTokenType.Integer &&
        normalizedPayload["m"]?.Type == JTokenType.Integer &&
        normalizedPayload["c"]?["x"]?.Type == JTokenType.Integer &&
        normalizedPayload["c"]?["y"]?.Type == JTokenType.Integer,
        "SilverDasher MQTT integer-string compatibility did not normalize the exact known fields.");
    var normalizedOpcodePayload = JObject.Parse(
        HostPluginBridge.NormalizeSilverDasherOpcodePayload(
            "{\"version\":\"1\",\"data\":[" +
            "{\"name\":\"InitZone\",\"cn\":\"0x1\",\"global\":\"0x2\"}," +
            "{\"name\":\"FateInfo\",\"cn\":\"0x3\",\"global\":\"0x4\"}," +
            "{\"name\":\"ActorControlSelf\",\"cn\":\"0x5\",\"global\":\"0x6\"}]}"));
    var normalizedOpcodes = normalizedOpcodePayload["data"]!
        .Children<JObject>()
        .ToDictionary(item => item.Value<string>("name")!, StringComparer.Ordinal);
    Assert(
        normalizedOpcodePayload.Value<string>("version") == "20260830" &&
        normalizedOpcodes["InitZone"].Value<string>("cn") == "0x028D" &&
        normalizedOpcodes["InitZone"].Value<string>("global") == "0x0161" &&
        normalizedOpcodes["FateInfo"].Value<string>("cn") == "0x00E9" &&
        normalizedOpcodes["FateInfo"].Value<string>("global") == "0xF009" &&
        normalizedOpcodes["ActorControlSelf"].Value<string>("cn") == "0x035D" &&
        normalizedOpcodes["ActorControlSelf"].Value<string>("global") == "0x037C",
        "SilverDasher opcode data was not normalized to Chinese 7.55h / Global 7.55h2.");
    var judge = rewrittenCore
                       .GetType("SilverDasher.ACT.Doppelgangers.Tailor", throwOnError: true)!
                       .GetMethod(
                           "Judge",
                           BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                   ?? throw new MissingMethodException(
                       "SilverDasher.ACT.Doppelgangers.Tailor",
                       "Judge");
    Assert(
        judge.Invoke(null, null) is string { Length: > 0 },
        "SilverDasher Weaver loaded but did not complete its managed authentication-seal entry point.");

    var cacheRoot = Path.Combine(Path.GetTempPath(), "DalamudActCompat", "silverdasher");
    var patchedLoader = new DirectoryInfo(cacheRoot)
        .EnumerateFiles("SilverDasher.Loader-*.dll")
        .OrderByDescending(file => file.LastWriteTimeUtc)
        .FirstOrDefault()
        ?? throw new FileNotFoundException("No patched SilverDasher loader was produced.", cacheRoot);
    var patchedCore = new DirectoryInfo(cacheRoot)
        .EnumerateFiles("SilverDasher.Core-*.dll")
        .OrderByDescending(file => file.LastWriteTimeUtc)
        .FirstOrDefault()
        ?? throw new FileNotFoundException("No patched SilverDasher core was produced.", cacheRoot);
    var patchedZodiark = new DirectoryInfo(cacheRoot)
        .EnumerateFiles("SilverDasher.ManagedZodiark-*.dll")
        .OrderByDescending(file => file.LastWriteTimeUtc)
        .FirstOrDefault()
        ?? throw new FileNotFoundException("No patched SilverDasher Zodiark was produced.", cacheRoot);
    var patchedWeaver = new FileInfo(Path.Combine(
        cacheRoot,
        "SilverDasher.Weaver-CD77EC62F7802C50BE02EC99AA83DFD5DE6CED7A41A4A866F80FB8A7509E26E1.dll"));
    if (!patchedWeaver.Exists)
    {
        throw new FileNotFoundException(
            "No exact hash-pinned SilverDasher Weaver compatibility copy was produced.",
            patchedWeaver.FullName);
    }

    var originalWeaverImage = File.ReadAllBytes(weaverPath);
    var patchedWeaverImage = File.ReadAllBytes(patchedWeaver.FullName);
    Assert(
        originalWeaverImage.Length == patchedWeaverImage.Length,
        "SilverDasher Weaver compatibility changed the native image length.");
    var changedWeaverOffsets = originalWeaverImage
        .Select((value, index) => (value, index))
        .Where(pair => pair.value != patchedWeaverImage[pair.index])
        .Select(pair => pair.index)
        .ToArray();
    Assert(
        changedWeaverOffsets.SequenceEqual([0x3254]) &&
        originalWeaverImage[0x3254] == 0x0B &&
        patchedWeaverImage[0x3254] == 0x05 &&
        Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(patchedWeaverImage)) ==
        "CD77EC62F7802C50BE02EC99AA83DFD5DE6CED7A41A4A866F80FB8A7509E26E1",
        "SilverDasher Weaver compatibility changed more than its exact process-attach branch.");

    using (var loader = AssemblyDefinition.ReadAssembly(patchedLoader.FullName))
    {
        var load = loader.MainModule.GetType("SilverDasher.Loader.Loader")
                       ?.Methods.Single(method =>
                           method.Name == "Load" &&
                           !method.IsStatic &&
                           method.Parameters.Count == 1)
                   ?? throw new MissingMethodException("SilverDasher.Loader.Loader", "Load(string)");
        var bridgeCalls = load.Body.Instructions.Count(instruction =>
            instruction.Operand is MethodReference called &&
            called.DeclaringType.FullName == typeof(HostPluginBridge).FullName &&
            called.Name == nameof(HostPluginBridge.LoadSilverDasherAssembly));
        var directLoads = load.Body.Instructions.Count(instruction =>
            instruction.Operand is MethodReference called &&
            called.DeclaringType.FullName == typeof(Assembly).FullName &&
            called.Name == nameof(Assembly.Load));
        Assert(
            bridgeCalls == 1 && directLoads == 0,
            $"SilverDasher dependency loader did not route through its scoped bridge: bridge={bridgeCalls}, direct={directLoads}.");
    }

    using (var core = AssemblyDefinition.ReadAssembly(patchedCore.FullName))
    {
        var methods = core.MainModule.Types
            .SelectMany(EnumerateCecilTypes)
            .SelectMany(type => type.Methods)
            .Where(method => method.HasBody)
            .ToArray();
        var calls = methods
            .SelectMany(method => method.Body.Instructions)
            .Select(instruction => instruction.Operand)
            .OfType<MethodReference>()
            .ToArray();
        var processBridgeCalls = calls.Count(called =>
            called.DeclaringType.FullName == typeof(HostPluginBridge).FullName &&
            called.Name == nameof(HostPluginBridge.GetSilverDasherGameProcess));
        var legacyProcessCalls = calls.Count(called =>
            called.DeclaringType.FullName == "FFXIV_ACT_Plugin.Common.IDataRepository" &&
            called.Name == "GetCurrentFFXIVProcess");
        var ttsBridgeCalls = calls.Count(called =>
            called.DeclaringType.FullName == typeof(HostPluginBridge).FullName &&
            called.Name == nameof(HostPluginBridge.SendSilverDasherTts));
        var legacyTtsCalls = calls.Count(called =>
            called.DeclaringType.FullName == "Advanced_Combat_Tracker.FormActMain" &&
            called.Name == "TTS");
        var notificationBridgeCalls = calls.Count(called =>
            called.DeclaringType.FullName == typeof(HostPluginBridge).FullName &&
            called.Name == nameof(HostPluginBridge.SendSilverDasherNotification));
        var nativeNotificationCalls = calls.Count(called =>
            called.DeclaringType.FullName is
                "Windows.UI.Notifications.ToastNotificationManager" or
                "Windows.UI.Notifications.ToastNotifier" or
                "Microsoft.Toolkit.Uwp.Notifications.ToastContentBuilder");
        var scanMobs = core.MainModule
            .GetType("SilverDasher.ACT.Doppelgangers.Negotiator")
            .Methods.Single(method => method.Name == "ScanMobs" && method.Parameters.Count == 0);
        var combatantRepositoryCalls = scanMobs.Body.Instructions.Count(instruction =>
            instruction.Operand is MethodReference called &&
            called.DeclaringType.FullName == "FFXIV_ACT_Plugin.Common.IDataRepository" &&
            called.Name == "GetCombatantList" &&
            called.Parameters.Count == 0);
        var mqttNormalizationCalls = calls.Count(called =>
            called.DeclaringType.FullName == typeof(HostPluginBridge).FullName &&
            called.Name == nameof(HostPluginBridge.NormalizeSilverDasherMqttPayload));
        var opcodeDataBridgeCalls = calls.Count(called =>
            called.DeclaringType.FullName == typeof(HostPluginBridge).FullName &&
            called.Name == nameof(HostPluginBridge.ReadSilverDasherDataFile));
        var dynamicCombatantNames = scanMobs.Body.Instructions.Count(instruction =>
                instruction.OpCode.Code == Code.Ldstr &&
                instruction.Operand is string name &&
                name is "DataRepository" or "GetCombatantList");
        var wpfDispatchCalls = calls.Count(called =>
            called.DeclaringType.FullName == "System.Windows.Threading.Dispatcher" &&
            called.Name == "Invoke" &&
            called.Parameters.Count == 1);
        var winFormsUiDispatchCalls = calls.Count(called =>
            called.DeclaringType.FullName == "System.Windows.Forms.Control" &&
            called.Name is "get_InvokeRequired" or "Invoke");
        var networkReceive = core.MainModule
            .GetType("SilverDasher.ACT.Doppelgangers.Overseer")
            .Methods.Single(method => method.Name == "OnNetworkReceive");
        var opcodeStoreIndex = networkReceive.Body.Instructions
            .Select((instruction, index) => (instruction, index))
            .Single(pair => pair.instruction.OpCode.Code == Code.Stloc_0)
            .index;
        var unknownOpcodeGuard =
            networkReceive.Body.Instructions[opcodeStoreIndex + 1].OpCode.Code == Code.Ldloc &&
            networkReceive.Body.Instructions[opcodeStoreIndex + 2].OpCode.FlowControl == FlowControl.Cond_Branch &&
            networkReceive.Body.Instructions[opcodeStoreIndex + 3].OpCode.Code == Code.Ret;
        Assert(
            processBridgeCalls == 1 && legacyProcessCalls == 0 &&
            ttsBridgeCalls == 2 && legacyTtsCalls == 0 &&
            notificationBridgeCalls == 1 && nativeNotificationCalls == 0 &&
            combatantRepositoryCalls == 1 && dynamicCombatantNames == 0 &&
            mqttNormalizationCalls == 1 && opcodeDataBridgeCalls == 1 &&
            wpfDispatchCalls >= 4 && winFormsUiDispatchCalls == 0 &&
            unknownOpcodeGuard,
            "SilverDasher core did not isolate its exact process/TTS/notification/data call sites: " +
            $"processBridge={processBridgeCalls}, processLegacy={legacyProcessCalls}, " +
            $"ttsBridge={ttsBridgeCalls}, ttsLegacy={legacyTtsCalls}, " +
            $"notificationBridge={notificationBridgeCalls}, notificationNative={nativeNotificationCalls}, " +
            $"combatantRepository={combatantRepositoryCalls}, dynamicCombatant={dynamicCombatantNames}, " +
            $"mqttNormalization={mqttNormalizationCalls}, opcodeDataBridge={opcodeDataBridgeCalls}, " +
            $"wpfDispatch={wpfDispatchCalls}, winFormsDispatch={winFormsUiDispatchCalls}, " +
            $"unknownOpcodeGuard={unknownOpcodeGuard}.");
    }

    using (var zodiark = AssemblyDefinition.ReadAssembly(patchedZodiark.FullName))
    {
        var processType = zodiark.MainModule.GetType("Zodiark.ZodiarkProcess");
        var openProcess = processType.Methods.Single(method =>
            method.Name == "OpenProcess" && method.Parameters.Count == 1);
        var privilegeCheck = processType.Methods.Single(method =>
            method.Name == "CheckSeDebugPrivilege" && method.Parameters.Count == 1);
        var enterDebugModeCalls = openProcess.Body.Instructions.Count(instruction =>
            instruction.Operand is MethodReference called &&
            called.DeclaringType.FullName == typeof(Process).FullName &&
            called.Name == nameof(Process.EnterDebugMode));
        var readOnlyAccessConstants = openProcess.Body.Instructions.Count(instruction =>
            instruction.OpCode.Code == Code.Ldc_I4 &&
            instruction.Operand is int value &&
            value == 0x0410);
        var allAccessConstants = openProcess.Body.Instructions.Count(instruction =>
            instruction.OpCode.Code == Code.Ldc_I4 &&
            instruction.Operand is int value &&
            value == 0x001F0FFF);
        var privilegeInstructions = privilegeCheck.Body.Instructions;
        Assert(
            enterDebugModeCalls == 0 &&
            readOnlyAccessConstants == 1 &&
            allAccessConstants == 0 &&
            privilegeInstructions.Count == 5 &&
            privilegeInstructions[0].OpCode.Code == Code.Ldarg_1 &&
            privilegeInstructions[1].OpCode.Code == Code.Ldc_I4_1 &&
            privilegeInstructions[2].OpCode.Code == Code.Stind_I1 &&
            privilegeInstructions[3].OpCode.Code == Code.Ldc_I4_0 &&
            privilegeInstructions[4].OpCode.Code == Code.Ret,
            "SilverDasher Zodiark retained debug privilege or process-all-access requirements.");
    }

    Assert(
        Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(loaderPath))) == loaderHash &&
        Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(corePath))) == coreHash &&
        Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(zodiarkPath))) == zodiarkHash &&
        Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(weaverPath))) == weaverHash,
        "SilverDasher runtime rewriting changed the user's original DLL files.");
}

void ValidateSilverDasherNotificationRouting()
{
    var configureWindowsWriter = typeof(HostPluginBridge).GetMethod(
                                     "ConfigureSilverDasherNotificationWriter",
                                     BindingFlags.Static | BindingFlags.NonPublic)
                                 ?? throw new MissingMethodException(
                                     typeof(HostPluginBridge).FullName,
                                     "ConfigureSilverDasherNotificationWriter");
    var configureSender = typeof(HostPluginBridge).GetMethod(
                              "Configure",
                              BindingFlags.Static | BindingFlags.NonPublic)
                          ?? throw new MissingMethodException(
                              typeof(HostPluginBridge).FullName,
                              "Configure");
    var windowsCalls = 0;
    var fallbackCalls = 0;
    string? fallbackType = null;
    HostSilverDasherNotification? fallbackPayload = null;
    Func<string, HostMessagePriority, object, string?, DateTimeOffset?, bool> fallback =
        (type, _, payload, _, _) =>
        {
            fallbackCalls++;
            fallbackType = type;
            fallbackPayload = payload as HostSilverDasherNotification;
            return true;
        };

    try
    {
        configureSender.Invoke(null, [fallback]);
        configureWindowsWriter.Invoke(
            null,
            [
                (Func<string, string, bool>)((message, detail) =>
                {
                    windowsCalls++;
                    return message == "Windows first" && detail == "detail";
                }),
            ]);
        Assert(
            HostPluginBridge.SendSilverDasherNotification("Windows first", "detail") &&
            windowsCalls == 1 &&
            fallbackCalls == 0,
            "SilverDasher notification did not prefer its Host-only Windows notification writer.");

        configureWindowsWriter.Invoke(
            null,
            [(Func<string, string, bool>)((_, _) => false)]);
        Assert(
            HostPluginBridge.SendSilverDasherNotification("Fallback", "game") &&
            fallbackCalls == 1 &&
            fallbackType == HostMessageTypes.SilverDasherNotification &&
            fallbackPayload is { Message: "Fallback", Detail: "game" },
            "SilverDasher notification did not preserve its typed game-side fallback channel.");
    }
    finally
    {
        configureWindowsWriter.Invoke(null, [null]);
        configureSender.Invoke(null, [null]);
    }
}

async Task ValidateMatchaLoadsOutOfProcessAsync(string packagePath)
{
    var temporaryRoot = Path.Combine(
        Path.GetTempPath(),
        $"DalamudActCompat-Matcha-Host-{Guid.NewGuid():N}");
    var temporaryPluginRoot = Path.Combine(temporaryRoot, "plugins");
    var temporaryConfigRoot = Path.Combine(temporaryRoot, "config");
    var matchaInstallRoot = Path.Combine(temporaryPluginRoot, "matcha");
    Directory.CreateDirectory(matchaInstallRoot);
    Directory.CreateDirectory(Path.Combine(temporaryConfigRoot, "Config"));
    try
    {
        ZipFile.ExtractToDirectory(packagePath, matchaInstallRoot);
        var entryAssembly = Path.Combine(
            matchaInstallRoot,
            "Plugins",
            "Cafe.Matcha",
            "Cafe.Matcha.dll");
        await File.WriteAllTextAsync(
            Path.Combine(matchaInstallRoot, "actcompat.plugin.json"),
            JsonSerializer.Serialize(new
            {
                id = "matcha",
                name = "Cafe.Matcha",
                version = "26.8.12.1622",
                entryAssembly = Path.GetRelativePath(matchaInstallRoot, entryAssembly),
                entryType = "Cafe.Matcha.MatchaInit",
                hostApiVersion = 1,
            }));

        var pluginDirectory = Path.GetDirectoryName(entryAssembly)!;
        var pathHash = Convert.ToHexString(
            System.Security.Cryptography.MD5.HashData(
                Encoding.UTF8.GetBytes(pluginDirectory)));
        await File.WriteAllTextAsync(
            Path.Combine(temporaryConfigRoot, "Config", "Cafe.Matcha.config"),
            JsonSerializer.Serialize(new
            {
                Hash = pathHash,
                telemetry = new
                {
                    enable = false,
                    uuid = (string?)null,
                    agreement = "no",
                },
                output = new
                {
                    toast = false,
                    tts = false,
                },
            }));

        var (host, pipe, session) = await StartConnectedHostAsync(
            loadPlugins: true,
            pluginRootOverride: temporaryPluginRoot,
            configRootOverride: temporaryConfigRoot);
        await using (pipe)
        using (host)
        {
            try
            {
                _ = await ReadWithTimeoutAsync(pipe);
                await HostFrameCodec.WriteAsync(
                    pipe.Writer,
                    HostEnvelope.Create(
                        session,
                        1,
                        HostMessageTypes.Hello,
                        HostMessagePriority.Control,
                        new HostHello(
                            "game-bridge",
                            "1",
                            Environment.ProcessId,
                            [HostProtocol.CurrentVersion])),
                    CancellationToken.None);
                await ReadUntilAsync(pipe, HostMessageTypes.HelloAck);
                await HostFrameCodec.WriteAsync(
                    pipe.Writer,
                    HostEnvelope.Create(
                        session,
                        2,
                        HostMessageTypes.Permissions,
                        HostMessagePriority.Control,
                        new HostPermissionSnapshot(
                            new Dictionary<string, IReadOnlyList<string>>
                            {
                                ["matcha"] =
                                [
                                    "ReadCombatLogs",
                                    "ReadLocalConfiguration",
                                    "TextToSpeech",
                                    "NetworkRequest",
                                    "LaunchExternalProcess",
                                    "WriteFiles",
                                ],
                            },
                            ["matcha"])),
                    CancellationToken.None);

                var healthEnvelope = await ReadUntilAsync(pipe, HostMessageTypes.Health, 90);
                var health = healthEnvelope.Payload.Deserialize<HostHealth>()
                             ?? throw new InvalidDataException(
                                 "Matcha Host returned no health state.");
                Assert(
                    health.State == "plugins.ready" &&
                    health.Detail.Contains("matcha", StringComparison.OrdinalIgnoreCase),
                    $"Matcha did not initialize in its dedicated Host: {health.State}: {health.Detail}");

                var packet = new byte[64];
                await HostFrameCodec.WriteAsync(
                    pipe.Writer,
                    HostEnvelope.Create(
                        session,
                        3,
                        HostMessageTypes.MatchaNetworkReceived,
                        HostMessagePriority.Data,
                        new HostMatchaNetworkEvent("down", 1, packet)),
                    CancellationToken.None);
                await HostFrameCodec.WriteAsync(
                    pipe.Writer,
                    HostEnvelope.Create(
                        session,
                        4,
                        HostMessageTypes.MatchaNetworkSent,
                        HostMessagePriority.Data,
                        new HostMatchaNetworkEvent("up", 2, packet)),
                    CancellationToken.None);

                HostHeartbeat? heartbeatState = null;
                for (var attempt = 0; attempt < 5; attempt++)
                {
                    var heartbeat = await ReadUntilAsync(pipe, HostMessageTypes.Heartbeat);
                    heartbeatState = heartbeat.Payload.Deserialize<HostHeartbeat>();
                    if (heartbeatState?.LastReceivedSequence >= 4)
                    {
                        break;
                    }
                }
                Assert(
                    heartbeatState is not null &&
                    heartbeatState.LastReceivedSequence >= 4 &&
                    heartbeatState.Stages.Any(stage =>
                        stage.PluginId == "matcha" &&
                        stage.Stage == "InitPlugin" &&
                        stage.State == "success") &&
                    heartbeatState.Stages.Any(stage =>
                        stage.PluginId == "matcha" &&
                        stage.Stage == "Bidirectional network" &&
                        stage.State == "success") &&
                    heartbeatState.Stages.All(stage =>
                        stage.PluginId != "matcha" || stage.State != "failed"),
                    "Matcha RX/TX frames or dedicated compatibility stages failed.");

                await HostFrameCodec.WriteAsync(
                    pipe.Writer,
                    HostEnvelope.Create(
                        session,
                        5,
                        HostMessageTypes.Shutdown,
                        HostMessagePriority.Control,
                        new HostHealth(
                            "stopping",
                            "Matcha smoke",
                            DateTimeOffset.UtcNow)),
                    CancellationToken.None);
                await ReadUntilAsync(pipe, HostMessageTypes.ShutdownAck);
                await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Assert(
                    host.ExitCode == 0,
                    $"Matcha dedicated Host exited with code {host.ExitCode}.");
            }
            catch (Exception ex)
            {
                if (!host.HasExited)
                {
                    host.Kill(entireProcessTree: true);
                }
                await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                var (output, errors) = ReadProcessLog(host);
                throw new InvalidOperationException(
                    $"Matcha dedicated Host load failed.{Environment.NewLine}" +
                    $"stdout:{Environment.NewLine}{output}{Environment.NewLine}" +
                    $"stderr:{Environment.NewLine}{errors}",
                    ex);
            }
        }
    }
    finally
    {
        if (Directory.Exists(temporaryRoot))
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }
}

async Task ValidateSilverDasherLoadsOutOfProcessAsync(string sourceRoot)
{
    var temporaryRoot = Path.Combine(
        Path.GetTempPath(),
        $"DalamudActCompat-SilverDasher-Host-{Guid.NewGuid():N}");
    var temporaryPluginRoot = Path.Combine(temporaryRoot, "plugins");
    var temporaryConfigRoot = Path.Combine(temporaryRoot, "config");
    var silverInstallRoot = Path.Combine(temporaryPluginRoot, "silverdasher");
    var probeInstallRoot = Path.Combine(temporaryPluginRoot, "zz-load-order-probe");
    Directory.CreateDirectory(silverInstallRoot);
    Directory.CreateDirectory(probeInstallRoot);
    Directory.CreateDirectory(temporaryConfigRoot);
    try
    {
        foreach (var directory in Directory.EnumerateDirectories(
                     sourceRoot,
                     "*",
                     SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(
                silverInstallRoot,
                Path.GetRelativePath(sourceRoot, directory)));
        }
        foreach (var file in Directory.EnumerateFiles(
                     sourceRoot,
                     "*",
                     SearchOption.AllDirectories))
        {
            var destination = Path.Combine(
                silverInstallRoot,
                Path.GetRelativePath(sourceRoot, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }

        var installedLoader = Directory.EnumerateFiles(
                silverInstallRoot,
                "SilverDasher.dll",
                SearchOption.AllDirectories)
            .Single();
        await File.WriteAllTextAsync(
            Path.Combine(silverInstallRoot, "actcompat.plugin.json"),
            JsonSerializer.Serialize(new
            {
                id = "silverdasher",
                name = "SilverDasher",
                version = "0.6.0.4",
                entryAssembly = Path.GetRelativePath(silverInstallRoot, installedLoader),
                entryType = "SilverDasher.Loader.Loader",
                hostApiVersion = 1,
            }));
        var probeAssembly = Assembly.GetExecutingAssembly().Location;
        var installedProbe = Path.Combine(probeInstallRoot, Path.GetFileName(probeAssembly));
        File.Copy(probeAssembly, installedProbe);
        await File.WriteAllTextAsync(
            Path.Combine(probeInstallRoot, "actcompat.plugin.json"),
            JsonSerializer.Serialize(new
            {
                id = "zz-load-order-probe",
                name = "ZZ load order probe",
                version = "1.0.0",
                entryAssembly = Path.GetFileName(installedProbe),
                entryType = typeof(SilverDasherLoadOrderProbe).FullName,
                hostApiVersion = 1,
            }));

        var (host, pipe, session) = await StartConnectedHostAsync(
            loadPlugins: true,
            pluginRootOverride: temporaryPluginRoot,
            configRootOverride: temporaryConfigRoot);
        await using (pipe)
        using (host)
        {
            try
            {
                _ = await ReadWithTimeoutAsync(pipe);
                await HostFrameCodec.WriteAsync(
                    pipe.Writer,
                    HostEnvelope.Create(
                        session,
                        1,
                        HostMessageTypes.Hello,
                        HostMessagePriority.Control,
                        new HostHello("test", "1", Environment.ProcessId, [HostProtocol.CurrentVersion])),
                    CancellationToken.None);
                await ReadUntilAsync(pipe, HostMessageTypes.HelloAck);
                await HostFrameCodec.WriteAsync(
                    pipe.Writer,
                    HostEnvelope.Create(
                        session,
                        2,
                        HostMessageTypes.Permissions,
                        HostMessagePriority.Control,
                        new HostPermissionSnapshot(
                            new Dictionary<string, IReadOnlyList<string>>
                            {
                                ["zz-load-order-probe"] = [],
                                ["silverdasher"] =
                                [
                                    "ReadCombatLogs",
                                    "ReadLocalConfiguration",
                                    "TextToSpeech",
                                    "NetworkRequest",
                                    "WriteFiles",
                                    "NativeGameMemory",
                                ],
                            },
                            ["zz-load-order-probe", "silverdasher"])),
                    CancellationToken.None);
                var healthEnvelope = await ReadUntilAsync(pipe, HostMessageTypes.Health, 90);
                var health = healthEnvelope.Payload.Deserialize<HostHealth>()
                             ?? throw new InvalidDataException("SilverDasher Host returned no health state.");
                Assert(
                    health.Detail.Contains(
                        "zz-load-order-probe, silverdasher",
                        StringComparison.OrdinalIgnoreCase),
                    $"SilverDasher did not initialize in the isolated Host: {health.State}: {health.Detail}");

                var message = new byte[64];
                message[18] = 0xff;
                message[19] = 0xff;
                await HostFrameCodec.WriteAsync(
                    pipe.Writer,
                    HostEnvelope.Create(
                        session,
                        3,
                        HostMessageTypes.SilverDasherNetworkReceived,
                        HostMessagePriority.SilverDasherData,
                        new HostSilverDasherNetworkEvent("down", 1, message)),
                    CancellationToken.None);
                await HostFrameCodec.WriteAsync(
                    pipe.Writer,
                    HostEnvelope.Create(
                        session,
                        4,
                        HostMessageTypes.SilverDasherZoneChanged,
                        HostMessagePriority.SilverDasherState,
                        new HostZoneEvent(777, "SilverDasher smoke", DateTimeOffset.UtcNow)),
                    CancellationToken.None);
                var heartbeat = await ReadUntilAsync(pipe, HostMessageTypes.Heartbeat);
                var heartbeatState = heartbeat.Payload.Deserialize<HostHeartbeat>()
                                     ?? throw new InvalidDataException(
                                         "SilverDasher Host returned no heartbeat state.");
                Assert(
                    heartbeatState.LastReceivedSequence >= 4 &&
                    heartbeatState.Stages.Any(stage =>
                        stage.PluginId == "silverdasher" &&
                        stage.Stage == "InitPlugin" &&
                        stage.State == "success") &&
                    heartbeatState.Stages.Any(stage =>
                        stage.PluginId == "silverdasher" &&
                        stage.Stage == "Isolated event channel" &&
                        stage.State == "success") &&
                    heartbeatState.Stages.All(stage =>
                        stage.PluginId != "silverdasher" || stage.State != "failed"),
                    "SilverDasher dedicated event frames did not reach the isolated Host.");
                await HostFrameCodec.WriteAsync(
                    pipe.Writer,
                    HostEnvelope.Create(
                        session,
                        5,
                        HostMessageTypes.Shutdown,
                        HostMessagePriority.Control,
                        new HostHealth("stopping", "SilverDasher smoke", DateTimeOffset.UtcNow)),
                    CancellationToken.None);
                await ReadUntilAsync(pipe, HostMessageTypes.ShutdownAck);
                await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (Exception ex)
            {
                if (!host.HasExited)
                {
                    host.Kill(entireProcessTree: true);
                }
                await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                var (output, errors) = ReadProcessLog(host);
                throw new InvalidOperationException(
                    $"SilverDasher isolated Host load failed.{Environment.NewLine}" +
                    $"stdout:{Environment.NewLine}{output}{Environment.NewLine}" +
                    $"stderr:{Environment.NewLine}{errors}",
                    ex);
            }
        }
    }
    finally
    {
        if (Directory.Exists(temporaryRoot))
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }
}

async Task ValidateAuthorizedTtsCadenceAsync()
{
    var elapsed = Stopwatch.StartNew();
    var dispatches = new ConcurrentQueue<TimeSpan>();
    var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    await using var queue = new AuthorizedTtsQueue(
        8,
        () => _ =>
        {
            dispatches.Enqueue(elapsed.Elapsed);
            if (dispatches.Count == 3)
            {
                completed.TrySetResult();
            }
        },
        exception => completed.TrySetException(exception));

    var requests = new List<string>();
    for (var index = 0; index < 3; index++)
    {
        var now = DateTimeOffset.UtcNow;
        requests.Add(queue.Reserve($"cadence-{index}", now, now.AddSeconds(3)));
        if (index < 2)
        {
            await Task.Delay(70);
        }
    }

    var authorizedAt = DateTimeOffset.UtcNow;
    foreach (var request in requests.AsEnumerable().Reverse())
    {
        Assert(
            queue.Complete(request, allowed: true, authorizedAt),
            "The TTS cadence probe could not complete a reserved authorization.");
    }

    await completed.Task.WaitAsync(TimeSpan.FromSeconds(3));
    var samples = dispatches.ToArray();
    Assert(
        samples.Length == 3 &&
        samples[1] - samples[0] >= TimeSpan.FromMilliseconds(45) &&
        samples[2] - samples[1] >= TimeSpan.FromMilliseconds(45),
        "Bunched TTS authorizations compressed the original request cadence.");
}

void ValidateFfxivEntityDeltaRepository()
{
    var baselineAt = DateTimeOffset.UtcNow;
    var baseline = CreateTestFfxivSnapshot(timestamp: baselineAt);
    var repository = new FfxivDataRepository();
    repository.Apply(baseline);

    var target = CreateTestFfxivSnapshot(
        includeTarget: true,
        timestamp: baselineAt.AddMilliseconds(30)).Combatants.Single(combatant =>
            combatant.Id == 0x10005678);
    Assert(
        repository.ApplyDelta(new HostFfxivEntityDelta(
            baseline.TerritoryId,
            baseline.CurrentPlayerId,
            baseline.Timestamp,
            baselineAt.AddMilliseconds(30),
            [target],
            [])) &&
        repository.GetCombatantList().Any(combatant => combatant.ID == target.Id),
        "A newly observed entity did not reach the Host through the incremental path.");

    Assert(
        repository.ApplyDelta(new HostFfxivEntityDelta(
            baseline.TerritoryId,
            baseline.CurrentPlayerId,
            baseline.Timestamp,
            baselineAt.AddMilliseconds(60),
            [],
            [])) &&
        repository.GetCombatantList().All(combatant => combatant.ID != target.Id),
        "A coalesced latest delta retained an entity that was absent from its full baseline overlay.");

    var fallback = CreateTestFfxivSnapshot(
        includeTarget: true,
        timestamp: baselineAt.AddMilliseconds(500));
    repository.Apply(fallback);
    var latestTarget = target with { PosX = target.PosX + 10 };
    var invalidPlaceholder = target with
    {
        Id = 0xE0000000,
        Name = "Invalid Placeholder",
    };
    repository.Apply(fallback with
    {
        Timestamp = baselineAt.AddMilliseconds(510),
        Combatants =
        [
            .. fallback.Combatants,
            latestTarget,
            invalidPlaceholder,
            invalidPlaceholder,
        ],
    });
    var normalizedCombatants = repository.GetCombatantList();
    Assert(
        normalizedCombatants.Count(combatant => combatant.ID == target.Id) == 1 &&
        normalizedCombatants.Single(combatant => combatant.ID == target.Id).PosX == latestTarget.PosX &&
        normalizedCombatants.All(combatant => combatant.ID != invalidPlaceholder.Id) &&
        !repository.ApplyDelta(new HostFfxivEntityDelta(
            baseline.TerritoryId,
            baseline.CurrentPlayerId,
            baseline.Timestamp,
            baselineAt.AddMilliseconds(530),
            [],
            [])),
        "The full snapshot did not normalize duplicate/invalid actors or supersede an old delta.");
}

IEnumerable<TypeDefinition> EnumerateCecilTypes(TypeDefinition type)
{
    yield return type;
    foreach (var nested in type.NestedTypes.SelectMany(EnumerateCecilTypes))
    {
        yield return nested;
    }
}

HostFfxivEntitySnapshot CreateTestFfxivSnapshot(
    bool includeTarget = false,
    DateTimeOffset? timestamp = null)
    => new(
        TerritoryId: 1,
        CurrentPlayerId: 0x10001234,
        Timestamp: timestamp ?? DateTimeOffset.UtcNow,
        Combatants: new[]
        {
            new HostFfxivCombatant(
                Id: 0x10001234,
                OwnerId: 0,
                Type: 1,
                Job: 19,
                Level: 100,
                Name: "Host Smoke Player",
                CurrentHp: 100_000,
                MaxHp: 100_000,
                CurrentMp: 10_000,
                MaxMp: 10_000,
                CurrentCp: 0,
                MaxCp: 0,
                CurrentGp: 0,
                MaxGp: 0,
                IsCasting: false,
                CastId: 0,
                CastTargetId: 0,
                CastTime: 0,
                MaxCastTime: 0,
                PosX: 1.25f,
                PosY: 2.5f,
                PosZ: -3.75f,
                Heading: 0.5f,
                CurrentWorldId: 21,
                WorldId: 21,
                WorldName: "Ravana",
                BNpcNameId: 0,
                BNpcId: 0,
                TargetId: 0,
                EffectiveDistance: 0,
                PartyType: 1,
                Address: 0x12345678,
                Statuses: [new HostFfxivStatus(1191, 1, 20, 0x10001234)]),
        }.Concat(includeTarget
            ?
            [
                new HostFfxivCombatant(
                    Id: 0x10005678,
                    OwnerId: 0,
                    Type: 2,
                    Job: 0,
                    Level: 100,
                    Name: "Host Smoke Target",
                    CurrentHp: 5_000_000,
                    MaxHp: 5_000_000,
                    CurrentMp: 0,
                    MaxMp: 0,
                    CurrentCp: 0,
                    MaxCp: 0,
                    CurrentGp: 0,
                    MaxGp: 0,
                    IsCasting: false,
                    CastId: 0,
                    CastTargetId: 0,
                    CastTime: 0,
                    MaxCastTime: 0,
                    PosX: 91.25f,
                    PosY: 2.5f,
                    PosZ: 108.75f,
                    Heading: 1.5f,
                    CurrentWorldId: 0,
                    WorldId: 0,
                    WorldName: string.Empty,
                    BNpcNameId: 12345,
                    BNpcId: 67890,
                    TargetId: 0,
                    EffectiveDistance: 5,
                    PartyType: 0,
                    Address: 0x23456789,
                    Statuses: []),
            ]
            : []).ToArray());

async Task PrepareLegacySmokeConfigurationAsync()
{
    var configurationDirectory = Path.Combine(configRoot!, "Config");
    Directory.CreateDirectory(configurationDirectory);
    var configurationPath = Path.Combine(
        configurationDirectory,
        "Triggernometry.config.xml");
    const string configuration = """
        <?xml version="1.0" encoding="utf-8"?>
        <Configuration DebugLevel="Verbose" LogNormalEvents="true" FfxivLogNetwork="true" ShowWelcome="false" WarnAdmin="true" UpdateNotifications="Yes" UpdateCheckMethod="External" UpdateExternalChannelUrl="http://127.0.0.1:1/should-not-run.xml" StartupTriggerType="Trigger" StartupTriggerId="00000000-0000-0000-0000-000000000000" TtsMethod="ACT" StartEndpointOnLaunch="false" AutosaveEnabled="false" Language="English (en)" PreviousNotifiedPluginVersion="2.1.1.2" PluginVersion="2.1.1.2">
          <Root Id="5eef94df-0eaf-41c7-9364-73857a7825e8" Enabled="true" Name="Host smoke">
            <Triggers>
              <Trigger Enabled="true" Id="7bddbd49-ec9e-47ea-b6a3-2613cd86128c" Name="Standard log closed loop" RegularExpression="ACTCOMPAT_SMOKE_LINE" Source="Log">
                <Actions>
                  <Action ActionType="UseTTS" OrderNumber="1" UseTTSTextExpression="ACTCOMPAT_LOG_MATCH" />
                </Actions>
              </Trigger>
              <Trigger Enabled="true" Id="7a268e31-303b-46f7-9a12-188638097b61" Name="Network equivalent closed loop" RegularExpression="ACTCOMPAT_SMOKE_LINE" Source="FFXIVNetwork">
                <Actions>
                  <Action ActionType="UseTTS" OrderNumber="1" UseTTSTextExpression="ACTCOMPAT_NETWORK_MATCH" />
                </Actions>
              </Trigger>
              <Trigger Enabled="true" Id="8f111f41-1aa4-45cc-a426-88c991842a21" Name="ACT legacy echo line closed loop" RegularExpression="^.{15}\S+ 00:0038::echo test &lt;se\.10&gt;$" Source="Log">
                <Actions>
                  <Action ActionType="UseTTS" OrderNumber="1" UseTTSTextExpression="ACTCOMPAT_LEGACY_ECHO_MATCH" />
                </Actions>
              </Trigger>
              <Trigger Enabled="true" Id="51997209-fb1d-4c07-80d5-d6542feeeacb" Name="C# self-reference regression" RegularExpression="ACTCOMPAT_SCRIPT_LINE" Source="Log">
                <Actions>
                  <Action ActionType="ExecuteScript" OrderNumber="1" ExecScriptExpression="using System.Windows.Forms;&#xD;&#xA;&#xD;&#xA;_ = typeof(MessageBox);&#xD;&#xA;Triggernometry.Core.Scripting.ScriptHelper.SetScalarVariable(false, &quot;ACTCOMPAT_SCRIPT_OK&quot;, 1);&#xD;&#xA;System.Console.WriteLine(&quot;ACTCOMPAT_SCRIPT_REFERENCE_OK&quot;);" />
                </Actions>
              </Trigger>
              <Trigger Enabled="true" Id="1751009c-1494-44f1-9708-37af4d4dc618" Name="OverlayPlugin handler IPC closed loop" RegularExpression="ACTCOMPAT_OVERLAY_HANDLER_LINE" Source="Log">
                <Actions>
                  <Action ActionType="ExecuteScript" OrderNumber="1" ExecScriptExpression="using Newtonsoft.Json.Linq;&#xD;&#xA;using Triggernometry.PluginBridges;&#xD;&#xA;&#xD;&#xA;var response = ModuleEvents.CallOverlayHandler(JObject.Parse(&quot;{\&quot;call\&quot;:\&quot;getLanguage\&quot;}&quot;));&#xD;&#xA;System.Console.WriteLine(&quot;ACTCOMPAT_OVERLAY_HANDLER_OK:&quot; + response[&quot;language&quot;]);" />
                </Actions>
              </Trigger>
              <Trigger Enabled="true" Id="d97e5f16-f275-44d9-be68-c46e646e5a42" Name="Required ACT plugin order closed loop" RegularExpression="ACTCOMPAT_PLUGIN_ORDER_LINE" Source="Log">
                <Actions>
                  <Action ActionType="ExecuteScript" OrderNumber="1" ExecScriptExpression="using System.Linq;&#xD;&#xA;using Advanced_Combat_Tracker;&#xD;&#xA;&#xD;&#xA;System.Console.WriteLine(&quot;ACTCOMPAT_PLUGIN_ORDER:&quot; + string.Join(&quot;&gt;&quot;, ActGlobals.oFormActMain.ActPlugins.Select(plugin =&gt; plugin.pluginFile.Name)));" />
                </Actions>
              </Trigger>
              <Trigger Enabled="true" Id="744a6947-da25-49a7-8353-738a88c4086e" Name="PictoACT _me callback closed loop" RegularExpression="ACTCOMPAT_PICTO_LINE" Source="Log">
                <Actions>
                  <Action ActionType="NamedCallback" OrderNumber="1" NamedCallbackName="PictoACT" NamedCallbackParam="Omen: Rect&#xD;&#xA;Delay: 2.5&#xD;&#xA;t: 5&#xD;&#xA;Pos: ${_me.Pos}&#xD;&#xA;Angle: 0 + pi/2&#xD;&#xA;Scale: 2.5, 28, 1" />
                </Actions>
              </Trigger>
              <Trigger Enabled="true" Id="0b7c968a-c565-49ca-a02e-6ac25e096be1" Name="P1 actual entity timing probe" RegularExpression="ACTCOMPAT_ENTITY_ACTUAL:(?&lt;tid&gt;1.{7})" Source="Log" Sequential="True">
                <Actions>
                  <Action ActionType="NamedCallback" OrderNumber="1" NamedCallbackName="PictoACT" NamedCallbackParam="Omen: Circle&#xD;&#xA;Tag: ACTCOMPAT_ENTITY_ACTUAL_${tid}&#xD;&#xA;t: 1&#xD;&#xA;Scale: 15" />
                  <Action ActionType="Loop" OrderNumber="2" LoopDelayExpression="10">
                    <LoopCondition Enabled="true" Grouping="Or">
                      <ConditionSingle Enabled="true" ExpressionL="${_sincems}" ExpressionTypeL="Numeric" ExpressionR="300" ExpressionTypeR="Numeric" ConditionType="NumericLessEqual" />
                    </LoopCondition>
                    <LoopActions>
                      <Action ActionType="NamedCallback" OrderNumber="1" NamedCallbackName="PictoACT" NamedCallbackParam="Action: Change&#xD;&#xA;Tag: ACTCOMPAT_ENTITY_ACTUAL_${tid}&#xD;&#xA;Pos: ${_entity[${tid}].XY}, 0&#xD;&#xA;Theta: ${_entity[${tid}].Heading}&#xD;&#xA;PlayerHeading: ${_me.Heading}&#xD;&#xA;PlayerAddress: ${_me.Address}">
                        <Condition Enabled="true" Grouping="Or">
                          <ConditionSingle Enabled="true" ExpressionL="${_entity[${tid}].Exist}" ExpressionTypeL="String" ExpressionR="1" ExpressionTypeR="String" ConditionType="StringEqualCase" />
                        </Condition>
                      </Action>
                    </LoopActions>
                    <Condition Enabled="true" Grouping="Or">
                      <ConditionSingle Enabled="true" ExpressionL="${_entity[${tid}].HP}" ExpressionTypeL="String" ExpressionR="0" ExpressionTypeR="String" ConditionType="NumericGreater" />
                    </Condition>
                  </Action>
                  <Action ActionType="NamedCallback" OrderNumber="3" NamedCallbackName="PictoACT" NamedCallbackParam="Action: Remove&#xD;&#xA;Tag: ACTCOMPAT_ENTITY_ACTUAL_DONE" />
                </Actions>
              </Trigger>
              <Trigger Enabled="true" Id="534a9334-c2a2-4c2b-9edf-f3486098fe22" Name="P1 per-iteration entity timing probe" RegularExpression="ACTCOMPAT_ENTITY_RETRY:(?&lt;tid&gt;1.{7})" Source="Log" Sequential="True">
                <Actions>
                  <Action ActionType="NamedCallback" OrderNumber="1" NamedCallbackName="PictoACT" NamedCallbackParam="Omen: Circle&#xD;&#xA;Tag: ACTCOMPAT_ENTITY_RETRY_${tid}&#xD;&#xA;t: 1&#xD;&#xA;Scale: 15" />
                  <Action ActionType="Loop" OrderNumber="2" LoopDelayExpression="10">
                    <LoopCondition Enabled="true" Grouping="Or">
                      <ConditionSingle Enabled="true" ExpressionL="${_sincems}" ExpressionTypeL="Numeric" ExpressionR="300" ExpressionTypeR="Numeric" ConditionType="NumericLessEqual" />
                    </LoopCondition>
                    <LoopActions>
                      <Action ActionType="NamedCallback" OrderNumber="1" NamedCallbackName="PictoACT" NamedCallbackParam="Action: Change&#xD;&#xA;Tag: ACTCOMPAT_ENTITY_RETRY_${tid}&#xD;&#xA;Pos: ${_entity[${tid}].XY}, 0&#xD;&#xA;Theta: ${_entity[${tid}].Heading}&#xD;&#xA;PlayerHeading: ${_me.Heading}&#xD;&#xA;PlayerAddress: ${_me.Address}">
                        <Condition Enabled="true" Grouping="Or">
                          <ConditionSingle Enabled="true" ExpressionL="${_entity[${tid}].Exist}" ExpressionTypeL="String" ExpressionR="1" ExpressionTypeR="String" ConditionType="StringEqualCase" />
                        </Condition>
                      </Action>
                    </LoopActions>
                  </Action>
                  <Action ActionType="NamedCallback" OrderNumber="3" NamedCallbackName="PictoACT" NamedCallbackParam="Action: Remove&#xD;&#xA;Tag: ACTCOMPAT_ENTITY_RETRY_DONE" />
                </Actions>
              </Trigger>
              <Trigger Enabled="true" Id="df719e7c-6138-4c4e-9d15-bacbf41d88c9" Name="U6b P2 strict MapEffect probe" RegularExpression="^.{15}\S+ 101:.{8}:0002....:(?&lt;dir&gt;0[1-8]):" Source="Log">
                <Actions>
                  <Action ActionType="NamedCallback" OrderNumber="1" NamedCallbackName="PictoACT" NamedCallbackParam="Action: Remove&#xD;&#xA;Tag: ACTCOMPAT_MAP_STRICT_${dir}" />
                </Actions>
              </Trigger>
              <Trigger Enabled="true" Id="a60b0098-abaa-471c-b8fd-9c295a87ce2f" Name="U6b relaxed MapEffect control" RegularExpression="^.{15}\S+ 101:[^:]*:0002....:(?&lt;dir&gt;0[1-8]):" Source="Log">
                <Actions>
                  <Action ActionType="NamedCallback" OrderNumber="1" NamedCallbackName="PictoACT" NamedCallbackParam="Action: Remove&#xD;&#xA;Tag: ACTCOMPAT_MAP_RELAXED_${dir}" />
                </Actions>
              </Trigger>
              <Trigger Enabled="true" Id="80d5ffc0-d534-4fcb-95a7-1ee3b72519b0" Name="Mark _me expression callback closed loop" RegularExpression="ACTCOMPAT_MARK_LINE" Source="Log">
                <Actions>
                  <Action ActionType="NamedCallback" OrderNumber="1" NamedCallbackName="mark" NamedCallbackParam="{&quot;ActorID&quot;:&quot;0x${_me.id}&quot;,&quot;MarkType&quot;:&quot;attack1&quot;,&quot;LocalOnly&quot;:true}" />
                </Actions>
              </Trigger>
              <Trigger Enabled="true" Id="a9c32bf3-3346-40e9-878e-1cbb944247c8" Name="Waymark _me expression callback closed loop" RegularExpression="ACTCOMPAT_PLACE_LINE" Source="Log">
                <Actions>
                  <Action ActionType="NamedCallback" OrderNumber="1" NamedCallbackName="place" NamedCallbackParam="{&quot;LocalOnly&quot;:true,&quot;A&quot;:{&quot;X&quot;:${_me.x},&quot;Y&quot;:${_me.z},&quot;Z&quot;:${_me.y},&quot;Active&quot;:true}}" />
                </Actions>
              </Trigger>
              <Trigger Enabled="true" Id="2d40782b-ee43-43fc-b41c-dff46fdc8761" Name="Preset callback closed loop" RegularExpression="ACTCOMPAT_PRESET_LINE" Source="Log">
                <Actions>
                  <Action ActionType="NamedCallback" OrderNumber="1" NamedCallbackName="preset" NamedCallbackParam="{&quot;Name&quot;:&quot;Slot 30&quot;,&quot;MapID&quot;:777,&quot;A&quot;:{&quot;X&quot;:1.25,&quot;Y&quot;:2.5,&quot;Z&quot;:-3.75,&quot;Active&quot;:true}}" />
                </Actions>
              </Trigger>
              <Trigger Enabled="true" Id="cb7e1d82-04fd-4a93-866e-7f1f5f538d71" Name="SendKey callback closed loop" RegularExpression="ACTCOMPAT_SENDKEY_LINE" Source="Log">
                <Actions>
                  <Action ActionType="NamedCallback" OrderNumber="1" NamedCallbackName="sendkey" NamedCallbackParam="65" />
                </Actions>
              </Trigger>
              <Trigger Enabled="true" Id="4e30f383-db3f-480a-b7ec-d0ff20918cdd" Name="Queue callback closed loop" RegularExpression="ACTCOMPAT_QUEUE_LINE" Source="Log">
                <Actions>
                  <Action ActionType="NamedCallback" OrderNumber="1" NamedCallbackName="queue" NamedCallbackParam="[{&quot;C&quot;:&quot;qid&quot;,&quot;P&quot;:&quot;ACTCOMPAT_QUEUE_ID&quot;,&quot;D&quot;:0},{&quot;C&quot;:&quot;command&quot;,&quot;P&quot;:&quot;//e ACTCOMPAT_QUEUE&quot;,&quot;D&quot;:0}]" />
                </Actions>
              </Trigger>
              <Trigger Enabled="true" Id="0b595eff-da67-45ce-ab7c-1e4f7477d6d2" Name="Combat start closed loop" RegularExpression="^OnCombatStart$" Source="ACT">
                <Actions>
                  <Action ActionType="UseTTS" OrderNumber="1" UseTTSTextExpression="ACTCOMPAT_COMBAT_START" />
                </Actions>
              </Trigger>
              <Trigger Enabled="true" Id="bc61e907-5ff7-4a44-8e08-395545e1dcb4" Name="Combat end closed loop" RegularExpression="^OnCombatEnd$" Source="ACT">
                <Actions>
                  <Action ActionType="UseTTS" OrderNumber="1" UseTTSTextExpression="ACTCOMPAT_COMBAT_END" />
                </Actions>
              </Trigger>
            </Triggers>
            <Folders>
              <Folder Enabled="true" Id="851063af-2d80-46d1-936c-95f75f9e67bc" Name="Zone restricted" ZoneFilterEnabled="true" ZoneFilterRegularExpression="^Host Smoke Zone$">
                <Triggers>
                  <Trigger Enabled="true" Id="72e0426f-6d28-4656-b815-eb42e9ef82f6" Name="Zone change closed loop" RegularExpression="ACTCOMPAT_ZONE_LINE" Source="Log">
                    <Actions>
                      <Action ActionType="UseTTS" OrderNumber="1" UseTTSTextExpression="ACTCOMPAT_ZONE_MATCH" />
                    </Actions>
                  </Trigger>
                </Triggers>
              </Folder>
            </Folders>
          </Root>
          <RepositoryRoot Name="Remote triggers" Enabled="true">
            <Repositories />
          </RepositoryRoot>
        </Configuration>
        """;
    // Repository content reaches this compatibility method through TriggernometryExport.Unserialize.
    // The smoke fixture is a local configuration, so apply the same boundary explicitly before
    // exercising Triggernometry's real action scheduler.
    await File.WriteAllTextAsync(
        configurationPath,
        HostPluginBridge.PatchTriggernometryExportXml(configuration.Trim()));

    using (var reservation = new TcpListener(IPAddress.Loopback, 0))
    {
        reservation.Start();
        postNamazuSmokePort = ((IPEndPoint)reservation.LocalEndpoint).Port;
    }

    var postNamazuConfigurationPath = Path.Combine(
        configurationDirectory,
        "PostNamazu.config.xml");
    var postNamazuConfiguration = $$"""
        <?xml version="1.0" encoding="utf-8" standalone="yes"?>
        <Config>
          <Port>{{postNamazuSmokePort}}</Port>
          <AutoStart>True</AutoStart>
          <Language>EN</Language>
          <Actions>
            <Command>True</Command>
            <Mark>True</Mark>
            <NormalCommand>True</NormalCommand>
            <Preset>True</Preset>
            <Queue>True</Queue>
            <SendKey>True</SendKey>
            <WayMark>True</WayMark>
          </Actions>
        </Config>
        """;
    await File.WriteAllTextAsync(
        postNamazuConfigurationPath,
        postNamazuConfiguration.Trim());
}

async Task<string> WaitForPostNamazuListenerAsync()
{
    using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(500) };
    Exception? lastFailure = null;
    for (var attempt = 0; attempt < 20; attempt++)
    {
        try
        {
            using var content = new StringContent("ACTCOMPAT_HTTP_BRIDGE", Encoding.UTF8);
            using var response = await client.PostAsync(
                $"http://127.0.0.1:{postNamazuSmokePort}/NamazuLog",
                content);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            lastFailure = ex;
            await Task.Delay(100);
        }
    }

    throw new InvalidOperationException(
        $"PostNamazu did not listen on loopback port {postNamazuSmokePort}.",
        lastFailure);
}

async Task<(Process Host, HostTestPipe Pipe, string Session)> StartConnectedHostAsync(
    bool loadPlugins = false,
    bool faultInjection = false,
    string? pluginRootOverride = null,
    string? configRootOverride = null)
{
    var session = Guid.NewGuid().ToString("N");
    var pipeName = $"DalamudActCompat-Test-{session}";
    var startInfo = new ProcessStartInfo(hostExecutable)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };
    startInfo.Environment["ACTCOMPAT_HOST_PICTO_POS"] = "<1.25, 2.5, -3.75>";
    startInfo.ArgumentList.Add("--pipe");
    startInfo.ArgumentList.Add(pipeName);
    startInfo.ArgumentList.Add("--session");
    startInfo.ArgumentList.Add(session);
    if (loadPlugins)
    {
        startInfo.ArgumentList.Add("--plugin-root");
        startInfo.ArgumentList.Add(pluginRootOverride ?? pluginRoot!);
        startInfo.ArgumentList.Add("--config-root");
        startInfo.ArgumentList.Add(configRootOverride ?? configRoot!);
    }
    if (faultInjection)
    {
        startInfo.ArgumentList.Add("--enable-fault-injection");
    }
    var host = Process.Start(startInfo)
               ?? throw new InvalidOperationException("ACT Host did not start.");
    var processLog = new ProcessLog();
    processLogs[host.Id] = processLog;
    host.OutputDataReceived += (_, eventArgs) => processLog.AppendOutput(eventArgs.Data);
    host.ErrorDataReceived += (_, eventArgs) => processLog.AppendError(eventArgs.Data);
    host.BeginOutputReadLine();
    host.BeginErrorReadLine();
    var reader = new NamedPipeClientStream(
        ".",
        $"{pipeName}-h2g",
        PipeDirection.In,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    var writer = new NamedPipeClientStream(
        ".",
        $"{pipeName}-g2h",
        PipeDirection.Out,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    var pipe = new HostTestPipe(reader, writer);
    try
    {
        await Task.WhenAll(
            reader.ConnectAsync(TimeSpan.FromSeconds(5), CancellationToken.None),
            writer.ConnectAsync(TimeSpan.FromSeconds(5), CancellationToken.None));
        return (host, pipe, session);
    }
    catch
    {
        await pipe.DisposeAsync();
        if (!host.HasExited)
        {
            host.Kill(entireProcessTree: true);
        }

        host.Dispose();
        throw;
    }
}

(string Output, string Error) ReadProcessLog(Process process)
{
    process.WaitForExit();
    return processLogs.TryGetValue(process.Id, out var log)
        ? log.Snapshot()
        : (string.Empty, string.Empty);
}

async Task WaitForProcessOutputAsync(
    Process process,
    string expected,
    TimeSpan timeout)
{
    var deadline = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < deadline)
    {
        var (output, error) = processLogs.TryGetValue(process.Id, out var log)
            ? log.Snapshot()
            : (string.Empty, string.Empty);
        if (output.Contains(expected, StringComparison.Ordinal))
        {
            return;
        }

        if (process.HasExited)
        {
            throw new InvalidOperationException(
                $"Host exited before emitting '{expected}'.{Environment.NewLine}" +
                $"stdout:{Environment.NewLine}{output}{Environment.NewLine}" +
                $"stderr:{Environment.NewLine}{error}");
        }

        await Task.Delay(50);
    }

    var (finalOutput, finalError) = processLogs.TryGetValue(process.Id, out var finalLog)
        ? finalLog.Snapshot()
        : (string.Empty, string.Empty);
    throw new TimeoutException(
        $"Host did not emit '{expected}' within {timeout.TotalSeconds:N0} seconds." +
        $"{Environment.NewLine}stdout:{Environment.NewLine}{finalOutput}" +
        $"{Environment.NewLine}stderr:{Environment.NewLine}{finalError}");
}

async Task<HostEnvelope> ReadWithTimeoutAsync(HostTestPipe pipe)
    => await HostFrameCodec.ReadAsync(pipe.Reader, CancellationToken.None)
           .AsTask()
           .WaitAsync(TimeSpan.FromSeconds(3))
       ?? throw new EndOfStreamException("Host pipe closed before a frame arrived.");

async Task<HostEnvelope> ReadUntilAsync(
    HostTestPipe pipe,
    string type,
    int maximumFrames = 10)
{
    for (var index = 0; index < maximumFrames; index++)
    {
        var envelope = await ReadWithTimeoutAsync(pipe);
        if (envelope.Type == type)
        {
            return envelope;
        }
    }

    throw new InvalidOperationException(
        $"Host did not send {type} within {maximumFrames} frames.");
}

async Task<HostEnvelope> ReadTriggerCommandAsync(HostTestPipe pipe)
{
    const int maximumFrames = 90;
    HostHeartbeat? lastHeartbeat = null;
    for (var index = 0; index < maximumFrames; index++)
    {
        var envelope = await ReadWithTimeoutAsync(pipe);
        if (envelope.Type == HostMessageTypes.CommandRequest)
        {
            return envelope;
        }

        if (envelope.Type == HostMessageTypes.Heartbeat)
        {
            lastHeartbeat = envelope.Payload.Deserialize<HostHeartbeat>();
        }
    }

    var runtime = lastHeartbeat?.Stages.FirstOrDefault(stage =>
        stage.PluginId == "triggernometry" && stage.Stage == "Runtime queues");
    var logBridge = lastHeartbeat?.Stages.FirstOrDefault(stage =>
        stage.PluginId == "act-host" && stage.Stage == "Log bridge");
    var callbacks = lastHeartbeat?.Plugins.Count > 0
        ? string.Join(
            "; ",
            lastHeartbeat.Plugins.Select(plugin =>
                $"{plugin.PluginId}:{plugin.CompletedEvents}/{plugin.Exceptions}"))
        : "none";
    throw new InvalidOperationException(
        $"Host did not send Triggernometry command.request within {maximumFrames} frames. " +
        $"Runtime={runtime?.Detail ?? "unavailable"}; " +
        $"bridge={logBridge?.Detail ?? "unavailable"}; callbacks={callbacks}.");
}

string FindProjectRoot()
{
    var current = new DirectoryInfo(Path.GetDirectoryName(hostExecutable)!);
    while (current is not null)
    {
        if (File.Exists(Path.Combine(current.FullName, "DalamudActCompat.slnx")))
        {
            return current.FullName;
        }

        current = current.Parent;
    }

    throw new DirectoryNotFoundException("Could not locate the DalamudActCompat repository root.");
}

static void AssertThrows<TException>(Action action, string message)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException(message);
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

internal class BlockingPostNamazuModuleBase
{
    public static BlockingPostNamazuPlugin PostNamazu { get; } = new();
}

internal sealed class BlockingPostNamazuModule : BlockingPostNamazuModuleBase;

internal sealed class BlockingPostNamazuPlugin
{
    public static ManualResetEventSlim Started { get; } = new(initialState: false);

    public static ManualResetEventSlim Release { get; } = new(initialState: false);

    public static void Reset()
    {
        Started.Reset();
        Release.Reset();
    }

    public void DoAction(string command, string payload)
    {
        Started.Set();
        Release.Wait();
    }
}

public sealed class SilverDasherLoadOrderProbe : IActPluginV1
{
    public void InitPlugin(TabPage pluginScreenSpace, Label pluginStatusText)
    {
        pluginScreenSpace.Text = "Load order probe";
        pluginStatusText.Text = "probe ready";
    }

    public void DeInitPlugin()
    {
    }
}

public sealed class GenericActPluginHostFixture : IActPluginV1
{
    public void InitPlugin(TabPage pluginScreenSpace, Label pluginStatusText)
    {
        pluginScreenSpace.Text = "Generic Host fixture";
        pluginStatusText.Text = "generic ready";
    }

    public void DeInitPlugin()
    {
    }
}

internal sealed class ProcessLog
{
    private readonly object sync = new();
    private readonly StringBuilder output = new();
    private readonly StringBuilder error = new();

    public void AppendOutput(string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (sync)
        {
            output.AppendLine(line);
        }
    }

    public void AppendError(string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (sync)
        {
            error.AppendLine(line);
        }
    }

    public (string Output, string Error) Snapshot()
    {
        lock (sync)
        {
            return (output.ToString(), error.ToString());
        }
    }
}

internal class RecordingPostNamazuModuleBase
{
    public static RecordingPostNamazuPlugin PostNamazu { get; } = new();
}

internal sealed class RecordingPostNamazuModule : RecordingPostNamazuModuleBase;

internal sealed class RecordingPostNamazuPlugin
{
    public static ConcurrentQueue<(string Command, string Payload)> Invocations { get; } = new();

    public static void Reset() => Invocations.Clear();

    public void DoAction(string command, string payload)
        => Invocations.Enqueue((command, payload));
}

internal sealed class HostTestPipe : IAsyncDisposable
{
    public HostTestPipe(
        NamedPipeClientStream reader,
        NamedPipeClientStream writer)
    {
        Reader = reader;
        Writer = writer;
    }

    public NamedPipeClientStream Reader { get; }

    public NamedPipeClientStream Writer { get; }

    public async ValueTask DisposeAsync()
    {
        await Reader.DisposeAsync();
        await Writer.DisposeAsync();
    }
}

using System.Collections.Concurrent;
using System.Diagnostics;
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

if (args.Length is not (1 or 2 or 3))
{
    throw new ArgumentException(
        "Pass Host.exe and optionally <triggernometry.dll>, or <plugin-root> <config-root>.");
}

var hostExecutable = Path.GetFullPath(args[0]);
var processLogs = new ConcurrentDictionary<int, ProcessLog>();
var pluginRoot = args.Length == 3 ? Path.GetFullPath(args[1]) : null;
var configRoot = args.Length == 3 ? Path.GetFullPath(args[2]) : null;
var triggernometryAssembly = args.Length == 2
    ? Path.GetFullPath(args[1])
    : pluginRoot is null
        ? null
        : Path.Combine(pluginRoot, "triggernometry", "Triggernometry.dll");
var postNamazuSmokePort = 0;
if (!File.Exists(hostExecutable))
{
    throw new FileNotFoundException("ACT Host executable was not found.", hostExecutable);
}

await ValidateHandshakeCommandBoundaryAndShutdownAsync();
ValidateFoxTtsDefaultConfiguration();
await ValidateSequenceRegressionTerminatesHostAsync();
await ValidateExpiredMessageIsDroppedAsync();
await ValidateHostCrashBreaksOnlyPipeAsync();
await ValidateAbruptClientDisconnectAsync();
await ValidateBlockedReaderRemainsOutOfProcessAsync();
ValidateLargePostNamazuCopyReturnsQuickly();
ValidatePostNamazuNativeProcessPermissionGate();
ValidatePostNamazuMarkPayloadNormalization();
ValidatePostNamazuQueueBreakAllCompatibility();
ValidatePostNamazu1366And1367SurfaceCompatibility();
ValidateOverlayPluginCompatibilityTypeName();
ValidateTriggernometryCompatibilityNoticeFilter();
ValidateTriggernometryWebAddressLaunchUsesShell();
ValidateTriggernometryPlaceholderProcessTestIsSkipped();
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
if (pluginRoot is not null)
{
    completion +=
        " Real Triggernometry log/network/zone/combat/TTS/_me entity and ACT-legacy " +
        "log paths, plus PostNamazu mark/waymark/preset/sendkey/original queue/PictoACT " +
        "closed-loop tests passed.";
}
Console.WriteLine(completion);

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
    lock (queueIds)
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
            var healthEnvelope = await ReadUntilAsync(pipe, HostMessageTypes.Health, 90);
            var health = healthEnvelope.Payload.Deserialize<HostHealth>()
                         ?? throw new InvalidDataException("Host returned no plugin health.");
            Assert(
                health.State == "plugins.ready",
                $"Legacy plugin runtime did not become ready: {health.Detail}");
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
                    3,
                    HostMessageTypes.FfxivEntities,
                    HostMessagePriority.State,
                    CreateTestFfxivSnapshot()),
                CancellationToken.None);
            await HostFrameCodec.WriteAsync(
                pipe.Writer,
                HostEnvelope.Create(
                    session,
                    4,
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
            var clientSequence = 5L;
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
            await HostFrameCodec.WriteAsync(
                pipe.Writer,
                HostEnvelope.Create(
                    session,
                    clientSequence++,
                    HostMessageTypes.ZoneChanged,
                    HostMessagePriority.Critical,
                    new HostZoneEvent(1, "Host Smoke Zone", DateTimeOffset.UtcNow)),
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
                            "00|2026-07-31T00:00:01.0000000+08:00|0000|ACTCOMPAT_ZONE_LINE|",
                            false),
                    }),
                CancellationToken.None);
            await ReadAndCompleteExpectedTtsAsync("ACTCOMPAT_ZONE_MATCH");
            await HostFrameCodec.WriteAsync(
                pipe.Writer,
                HostEnvelope.Create(
                    session,
                    clientSequence++,
                    HostMessageTypes.CombatStarted,
                    HostMessagePriority.Critical,
                    new HostCombatEvent(true, DateTimeOffset.UtcNow)),
                CancellationToken.None);
            await ReadAndCompleteExpectedTtsAsync("ACTCOMPAT_COMBAT_START");
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
    var loaderHash = Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(loaderPath)));
    var coreHash = Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(corePath)));

    _ = LegacyAssemblyRewriter.LoadSilverDasher(loaderPath, AssemblyLoadContext.Default);
    _ = LegacyAssemblyRewriter.LoadSilverDasherCore(corePath);

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
        var wpfDispatchCalls = calls.Count(called =>
            called.DeclaringType.FullName == "System.Windows.Threading.Dispatcher" &&
            called.Name == "Invoke" &&
            called.Parameters.Count == 1);
        var winFormsUiDispatchCalls = calls.Count(called =>
            called.DeclaringType.FullName == "System.Windows.Forms.Control" &&
            called.Name is "get_InvokeRequired" or "Invoke");
        Assert(
            processBridgeCalls == 1 && legacyProcessCalls == 0 &&
            ttsBridgeCalls == 2 && legacyTtsCalls == 0 &&
            wpfDispatchCalls >= 4 && winFormsUiDispatchCalls == 0,
            "SilverDasher core did not isolate its exact process/TTS call sites: " +
            $"processBridge={processBridgeCalls}, processLegacy={legacyProcessCalls}, " +
            $"ttsBridge={ttsBridgeCalls}, ttsLegacy={legacyTtsCalls}, " +
            $"wpfDispatch={wpfDispatchCalls}, winFormsDispatch={winFormsUiDispatchCalls}.");
    }

    Assert(
        Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(loaderPath))) == loaderHash &&
        Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(corePath))) == coreHash,
        "SilverDasher runtime rewriting changed the user's original DLL files.");
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

IEnumerable<TypeDefinition> EnumerateCecilTypes(TypeDefinition type)
{
    yield return type;
    foreach (var nested in type.NestedTypes.SelectMany(EnumerateCecilTypes))
    {
        yield return nested;
    }
}

HostFfxivEntitySnapshot CreateTestFfxivSnapshot()
    => new(
        TerritoryId: 1,
        CurrentPlayerId: 0x10001234,
        Timestamp: DateTimeOffset.UtcNow,
        Combatants:
        [
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
        ]);

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
                  <Action ActionType="ExecuteScript" OrderNumber="1" ExecScriptExpression="using System.Windows.Forms;&#xD;&#xA;using Triggernometry.PluginBridges.BridgeNamazu;&#xD;&#xA;&#xD;&#xA;_ = BridgeNamazu.NamazuPlugin;&#xD;&#xA;_ = typeof(MessageBox);&#xD;&#xA;Triggernometry.Core.Scripting.ScriptHelper.SetScalarVariable(false, &quot;ACTCOMPAT_SCRIPT_OK&quot;, 1);&#xD;&#xA;System.Console.WriteLine(&quot;ACTCOMPAT_SCRIPT_REFERENCE_OK&quot;);" />
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
    await File.WriteAllTextAsync(configurationPath, configuration.Trim());

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

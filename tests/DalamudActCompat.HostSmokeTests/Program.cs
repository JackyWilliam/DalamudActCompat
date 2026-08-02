using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Reflection;
using System.Windows.Forms;
using DalamudActCompat.Host;
using DalamudActCompat.Protocol;

if (args.Length is not (1 or 3))
{
    throw new ArgumentException(
        "Pass Host.exe and optionally <plugin-root> <config-root>.");
}

var hostExecutable = Path.GetFullPath(args[0]);
var processLogs = new ConcurrentDictionary<int, ProcessLog>();
var pluginRoot = args.Length == 3 ? Path.GetFullPath(args[1]) : null;
var configRoot = args.Length == 3 ? Path.GetFullPath(args[2]) : null;
if (!File.Exists(hostExecutable))
{
    throw new FileNotFoundException("ACT Host executable was not found.", hostExecutable);
}

await ValidateHandshakeCommandBoundaryAndShutdownAsync();
await ValidateSequenceRegressionTerminatesHostAsync();
await ValidateExpiredMessageIsDroppedAsync();
await ValidateHostCrashBreaksOnlyPipeAsync();
await ValidateAbruptClientDisconnectAsync();
await ValidateBlockedReaderRemainsOutOfProcessAsync();
ValidateLargePostNamazuCopyReturnsQuickly();
if (pluginRoot is not null && configRoot is not null)
{
    await ValidateLegacyPluginsLoadOutOfProcessAsync();
}
Console.WriteLine(
    "Host handshake, sequence/deadline validation, bounded IPC, command denial, crash isolation, disconnect, 100k-line PostNamazu copy, and real Triggernometry log/network/zone/combat/TTS closed-loop tests passed.");

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
                            ],
                            ["postnamazu"] =
                            [
                                "ReadCombatLogs",
                                "ReadLocalConfiguration",
                                "Clipboard",
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
            await HostFrameCodec.WriteAsync(
                pipe.Writer,
                HostEnvelope.Create(
                    session,
                    3,
                    HostMessageTypes.LogBatch,
                    HostMessagePriority.Data,
                    new[]
                    {
                        new HostLogEvent(
                            DateTimeOffset.UtcNow,
                            "00|2026-07-31T00:00:00.0000000+08:00|0000|ACTCOMPAT_SMOKE_LINE|",
                            false),
                    }),
                CancellationToken.None);
            var ttsRequests = new List<(string CorrelationId, HostCommandRequest Request)>();
            while (ttsRequests.Count < 2)
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
                ttsTexts.SetEquals(["ACTCOMPAT_LOG_MATCH", "ACTCOMPAT_NETWORK_MATCH"]),
                "Triggernometry did not complete both standard-log and FFXIV-network-equivalent regex/TTS paths.");
            var clientSequence = 4L;
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
            var hasFoxTts = File.Exists(Path.Combine(
                pluginRoot!,
                "act.foxtts",
                "actcompat.plugin.json"));
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
                output.Contains("Legacy plugin 'postnamazu' loaded out-of-process.", StringComparison.Ordinal),
                $"PostNamazu did not load out-of-process.{Environment.NewLine}{output}{Environment.NewLine}{errors}");
            Assert(
                output.Split("test TTS output suppressed:", StringSplitOptions.None).Length - 1 ==
                (hasFoxTts ? 6 : 5),
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

async Task PrepareLegacySmokeConfigurationAsync()
{
    var configurationDirectory = Path.Combine(configRoot!, "Config");
    Directory.CreateDirectory(configurationDirectory);
    var configurationPath = Path.Combine(
        configurationDirectory,
        "Triggernometry.config.xml");
    const string configuration = """
        <?xml version="1.0" encoding="utf-8"?>
        <Configuration DebugLevel="Verbose" LogNormalEvents="true" FfxivLogNetwork="true" ShowWelcome="false" WarnAdmin="true" UpdateNotifications="No" StartupTriggerType="Trigger" StartupTriggerId="00000000-0000-0000-0000-000000000000" TtsMethod="ACT" StartEndpointOnLaunch="false" AutosaveEnabled="false" Language="English (en)" PreviousNotifiedPluginVersion="2.1.1.2" PluginVersion="2.1.1.2">
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
}

async Task<(Process Host, HostTestPipe Pipe, string Session)> StartConnectedHostAsync(
    bool loadPlugins = false,
    bool faultInjection = false)
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
    startInfo.ArgumentList.Add("--pipe");
    startInfo.ArgumentList.Add(pipeName);
    startInfo.ArgumentList.Add("--session");
    startInfo.ArgumentList.Add(session);
    if (loadPlugins)
    {
        startInfo.ArgumentList.Add("--plugin-root");
        startInfo.ArgumentList.Add(pluginRoot!);
        startInfo.ArgumentList.Add("--config-root");
        startInfo.ArgumentList.Add(configRoot!);
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
    HostHeartbeat? lastHeartbeat = null;
    for (var index = 0; index < 10; index++)
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
        "Host did not send Triggernometry command.request within ten frames. " +
        $"Runtime={runtime?.Detail ?? "unavailable"}; " +
        $"bridge={logBridge?.Detail ?? "unavailable"}; callbacks={callbacks}.");
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
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

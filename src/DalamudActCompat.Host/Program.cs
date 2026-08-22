using System.Diagnostics;
using System.IO.Pipes;
using System.IO;
using System.Collections.Concurrent;
using System.Text.Json;
using DalamudActCompat.Protocol;

namespace DalamudActCompat.Host;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var options = HostOptions.Parse(args);
        if (string.IsNullOrWhiteSpace(options.PipeName) ||
            string.IsNullOrWhiteSpace(options.SessionId))
        {
            Console.Error.WriteLine("Missing --pipe <name> or --session <id>.");
            return 2;
        }

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };

        try
        {
            await RunPipeServerAsync(options, shutdown).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static async Task RunPipeServerAsync(
        HostOptions options,
        CancellationTokenSource shutdown)
    {
        await using var bridgeInput = new NamedPipeServerStream(
            $"{options.PipeName}-g2h",
            PipeDirection.In,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await using var bridgeOutput = new NamedPipeServerStream(
            $"{options.PipeName}-h2g",
            PipeDirection.Out,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        Console.WriteLine(
            $"ACT Host protocol {HostProtocol.CurrentVersion}; session {options.SessionId}; waiting.");
        try
        {
            await Task.WhenAll(
                    bridgeInput.WaitForConnectionAsync(shutdown.Token),
                    bridgeOutput.WaitForConnectionAsync(shutdown.Token))
                .ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            Console.WriteLine(
                $"Dalamud bridge disconnected before handshake completed: {ex.Message}");
            return;
        }
        Console.WriteLine("Dalamud bridge connected.");

        using var outbound = new BlockingCollection<HostEnvelope>(
            new ConcurrentQueue<HostEnvelope>(),
            HostProtocol.ControlQueueCapacity);
        long sendSequence = 0;
        long receivedSequence = 0;
        var writer = Task.Factory.StartNew(
            () => WriteLoop(bridgeOutput, outbound, shutdown.Token),
            shutdown.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        HostPluginBridge.Configure((type, priority, payload, correlationId, deadline) =>
        {
            var envelope = HostEnvelope.Create(
                options.SessionId,
                Interlocked.Increment(ref sendSequence),
                type,
                priority,
                payload,
                correlationId,
                deadline);
            return TryAddOutbound(outbound, envelope);
        });
        await EnqueueControlAsync(
            outbound,
            HostEnvelope.Create(
                options.SessionId,
                Interlocked.Increment(ref sendSequence),
                HostMessageTypes.Hello,
                HostMessagePriority.Control,
                new HostHello(
                    "host",
                    typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown",
                    Environment.ProcessId,
                    [HostProtocol.CurrentVersion])),
            shutdown.Token).ConfigureAwait(false);
        LegacyPluginRuntime? pluginRuntime = null;
        var pluginRuntimeReady = 0;
        var pluginRuntimeDisposed = 0;
        var pluginStartupStarted = 0;
        var criticalStateSync = new object();
        var pendingRuntimeEvents = new LinkedList<(
            Action<LegacyPluginRuntime> Replay,
            int LogCount)>();
        var pendingRuntimeLogCount = 0;
        var heartbeat = Task.Factory.StartNew(
            () => HeartbeatLoop(
                options,
                outbound,
                () => Volatile.Read(ref receivedSequence),
                () => Interlocked.Increment(ref sendSequence),
                () => Volatile.Read(ref pluginRuntimeReady) == 1
                    ? pluginRuntime?.GetPluginHealth() ?? []
                    : [],
                () => Volatile.Read(ref pluginRuntimeReady) == 1
                    ? pluginRuntime?.GetStages() ?? []
                    : [],
                shutdown.Token),
            shutdown.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        try
        {
            while (!shutdown.IsCancellationRequested)
            {
                var envelope = await HostFrameCodec.ReadAsync(bridgeInput, shutdown.Token)
                    .ConfigureAwait(false);
                if (envelope is null)
                {
                    Console.WriteLine("Dalamud bridge disconnected.");
                    break;
                }

                if (!string.Equals(
                        envelope.SessionId,
                        options.SessionId,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException("IPC session identifier mismatch.");
                }

                var previousSequence = Volatile.Read(ref receivedSequence);
                if (envelope.Sequence <= previousSequence)
                {
                    throw new InvalidDataException(
                        $"IPC sequence regressed from {previousSequence} to {envelope.Sequence}.");
                }

                Volatile.Write(ref receivedSequence, envelope.Sequence);
                if (envelope.Deadline is { } deadline && deadline < DateTimeOffset.UtcNow)
                {
                    Console.Error.WriteLine(
                        $"Expired game-side IPC message dropped: {envelope.Type}.");
                    continue;
                }

                switch (envelope.Type)
                {
                    case HostMessageTypes.Hello:
                        var hello = envelope.Payload.Deserialize<HostHello>()
                                    ?? throw new InvalidDataException(
                                        "Host hello payload is invalid.");
                        if (string.Equals(
                                hello.Role,
                                "game-bridge",
                                StringComparison.OrdinalIgnoreCase))
                        {
                            HostPluginBridge.ConfigureGameProcess(hello.ProcessId);
                        }

                        await EnqueueControlAsync(
                            outbound,
                            HostEnvelope.Create(
                                options.SessionId,
                                Interlocked.Increment(ref sendSequence),
                                HostMessageTypes.HelloAck,
                                HostMessagePriority.Control,
                                new HostHealth(
                                    "ready",
                                    "独立 Host 已建立双向、有限队列 IPC；传统插件迁移状态由健康页单独报告。",
                                    DateTimeOffset.UtcNow),
                                envelope.CorrelationId),
                            shutdown.Token).ConfigureAwait(false);
                        break;
                    case HostMessageTypes.Permissions:
                        var permissions =
                            envelope.Payload.Deserialize<HostPermissionSnapshot>()
                            ?? throw new InvalidDataException(
                                "Host permission snapshot is invalid.");
                        if (Interlocked.Exchange(ref pluginStartupStarted, 1) != 0)
                        {
                            throw new InvalidDataException(
                                "Host permission snapshot may only be configured once per session.");
                        }

                        HostPluginBridge.ConfigurePermissions(permissions);
                        if (!string.IsNullOrWhiteSpace(options.PluginRoot) &&
                            !string.IsNullOrWhiteSpace(options.ConfigRoot))
                        {
                            pluginRuntime = new LegacyPluginRuntime(
                                options.PluginRoot,
                                options.ConfigRoot,
                                permissions.AllowedPluginIds,
                                options.FaultInjectionEnabled);
                            var runtime = pluginRuntime;
                            _ = Task.Run(
                                () => StartLegacyPluginsAsync(
                                    runtime,
                                    options,
                                    outbound,
                                    () => Interlocked.Increment(ref sendSequence),
                                    () =>
                                    {
                                        lock (criticalStateSync)
                                        {
                                            Interlocked.Exchange(ref pluginRuntimeReady, 1);
                                            foreach (var pending in pendingRuntimeEvents)
                                            {
                                                try
                                                {
                                                    pending.Replay(runtime);
                                                }
                                                catch (Exception ex)
                                                {
                                                    HostPluginBridge.ReportException(
                                                        "act-host",
                                                        "Startup event replay",
                                                        ex);
                                                }
                                            }

                                            pendingRuntimeEvents.Clear();
                                            pendingRuntimeLogCount = 0;
                                        }
                                    },
                                    shutdown.Token),
                                CancellationToken.None);
                            _ = MonitorPluginStartupAsync(
                                () => Volatile.Read(ref pluginRuntimeReady) == 1,
                                shutdown);
                        }
                        break;
                    case HostMessageTypes.Shutdown:
                        await EnqueueControlAsync(
                            outbound,
                            HostEnvelope.Create(
                                options.SessionId,
                                Interlocked.Increment(ref sendSequence),
                                HostMessageTypes.ShutdownAck,
                                HostMessagePriority.Control,
                                new HostHealth("stopping", "Shutdown acknowledged.", DateTimeOffset.UtcNow),
                                envelope.CorrelationId),
                            shutdown.Token).ConfigureAwait(false);
                        CompleteOutbound(outbound);
                        await writer.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                        var postNamazuQueuesStopped = await HostPluginBridge
                            .StopPostNamazuQueuesAsync()
                            .ConfigureAwait(false);
                        if (postNamazuQueuesStopped &&
                            Volatile.Read(ref pluginRuntimeReady) == 1 &&
                            Interlocked.Exchange(ref pluginRuntimeDisposed, 1) == 0)
                        {
                            pluginRuntime?.Dispose();
                        }
                        else if (!postNamazuQueuesStopped)
                        {
                            Console.Error.WriteLine(
                                "Skipping legacy plugin runtime disposal because a PostNamazu action is still executing; the isolated Host process will exit instead.");
                        }
                        shutdown.Cancel();
                        return;
                    case HostMessageTypes.CommandRequest:
                        await EnqueueControlAsync(
                            outbound,
                            HostEnvelope.Create(
                                options.SessionId,
                                Interlocked.Increment(ref sendSequence),
                                HostMessageTypes.CommandResult,
                                HostMessagePriority.Control,
                                new HostCommandResult(
                                    false,
                                    "denied",
                                    "Host 拒绝未经插件注册表与权限校验的直接命令包；已注册的插件动作使用各自兼容入口。"),
                                envelope.CorrelationId),
                            shutdown.Token).ConfigureAwait(false);
                        break;
                    case HostMessageTypes.CommandResult:
                        if (envelope.Payload.Deserialize<HostCommandResult>() is { } commandResult)
                        {
                            HostPluginBridge.CompleteCommand(
                                envelope.CorrelationId,
                                commandResult);
                            Console.WriteLine(
                                $"command result correlation={envelope.CorrelationId ?? "none"} " +
                                $"status={commandResult.Status} success={commandResult.Success} " +
                                $"detail={commandResult.Detail ?? string.Empty}");
                        }
                        break;
                    case HostMessageTypes.LogBatch:
                        var logs = envelope.Payload.Deserialize<IReadOnlyList<HostLogEvent>>() ?? [];
                        lock (criticalStateSync)
                        {
                            if (Volatile.Read(ref pluginRuntimeReady) == 1)
                            {
                                pluginRuntime?.AcceptLogs(logs);
                            }
                            else
                            {
                                while (pendingRuntimeLogCount + logs.Count >
                                       HostProtocol.DataQueueCapacity)
                                {
                                    var oldestLogs = pendingRuntimeEvents.First;
                                    while (oldestLogs is not null && oldestLogs.Value.LogCount == 0)
                                    {
                                        oldestLogs = oldestLogs.Next;
                                    }

                                    if (oldestLogs is null)
                                    {
                                        break;
                                    }

                                    pendingRuntimeLogCount -= oldestLogs.Value.LogCount;
                                    pendingRuntimeEvents.Remove(oldestLogs);
                                }

                                if (logs.Count <= HostProtocol.DataQueueCapacity)
                                {
                                    pendingRuntimeEvents.AddLast((
                                        runtime => runtime.AcceptLogs(logs),
                                        logs.Count));
                                    pendingRuntimeLogCount += logs.Count;
                                }
                            }
                        }
                        break;
                    case HostMessageTypes.SilverDasherLogBatch:
                        HostPluginBridge.PublishSilverDasherLogs(
                            envelope.Payload.Deserialize<IReadOnlyList<HostLogEvent>>() ?? []);
                        break;
                    case HostMessageTypes.ZoneChanged:
                        if (envelope.Payload.Deserialize<HostZoneEvent>() is { } zone)
                        {
                            lock (criticalStateSync)
                            {
                                if (Volatile.Read(ref pluginRuntimeReady) == 1)
                                {
                                    pluginRuntime?.ChangeZone(zone.TerritoryId, zone.ZoneName);
                                }
                                else
                                {
                                    pendingRuntimeEvents.AddLast((
                                        runtime => runtime.ChangeZone(
                                            zone.TerritoryId,
                                            zone.ZoneName),
                                        0));
                                }
                            }
                        }
                        break;
                    case HostMessageTypes.SilverDasherZoneChanged:
                        if (envelope.Payload.Deserialize<HostZoneEvent>() is { } silverZone)
                        {
                            HostPluginBridge.PublishSilverDasherZone(silverZone);
                        }
                        break;
                    case HostMessageTypes.SilverDasherNetworkReceived:
                        var silverNetwork =
                            envelope.Payload.Deserialize<HostSilverDasherNetworkEvent>()
                            ?? throw new InvalidDataException(
                                "SilverDasher network event payload is invalid.");
                        HostPluginBridge.PublishSilverDasherNetwork(silverNetwork);
                        break;
                    case HostMessageTypes.MatchaNetworkReceived:
                    case HostMessageTypes.MatchaNetworkSent:
                        var matchaNetwork =
                            envelope.Payload.Deserialize<HostMatchaNetworkEvent>()
                            ?? throw new InvalidDataException(
                                "Matcha network event payload is invalid.");
                        HostPluginBridge.PublishMatchaNetwork(
                            matchaNetwork,
                            envelope.Type == HostMessageTypes.MatchaNetworkSent);
                        break;
                    case HostMessageTypes.CombatStarted:
                        lock (criticalStateSync)
                        {
                            if (Volatile.Read(ref pluginRuntimeReady) == 1)
                            {
                                pluginRuntime?.SetCombatState(true);
                            }
                            else
                            {
                                pendingRuntimeEvents.AddLast((
                                    runtime => runtime.SetCombatState(true),
                                    0));
                            }
                        }
                        break;
                    case HostMessageTypes.CombatEnded:
                        lock (criticalStateSync)
                        {
                            if (Volatile.Read(ref pluginRuntimeReady) == 1)
                            {
                                pluginRuntime?.SetCombatState(false);
                            }
                            else
                            {
                                pendingRuntimeEvents.AddLast((
                                    runtime => runtime.SetCombatState(false),
                                    0));
                            }
                        }
                        break;
                    case HostMessageTypes.PluginOpen:
                        var pluginId = envelope.Payload.GetProperty("pluginId").GetString();
                        if (!string.IsNullOrWhiteSpace(pluginId))
                        {
                            if (Volatile.Read(ref pluginRuntimeReady) == 1)
                            {
                                if (pluginRuntime?.OpenPluginUi(pluginId) == true)
                                {
                                    Console.WriteLine(
                                        $"Opened legacy plugin '{pluginId}' configuration window.");
                                }
                                else
                                {
                                    Console.Error.WriteLine(
                                        $"Legacy plugin '{pluginId}' configuration window is unavailable.");
                                }
                            }
                        }
                        break;
                    case HostMessageTypes.PluginInvoke:
                        var invocation = envelope.Payload.Deserialize<HostPluginInvocation>()
                                         ?? throw new InvalidDataException(
                                             "Plugin invocation payload is invalid.");
                        if (Volatile.Read(ref pluginRuntimeReady) == 1 &&
                            pluginRuntime?.InvokePlugin(invocation) == true)
                        {
                            Console.WriteLine(
                                $"Invoked legacy plugin '{invocation.PluginId}' action '{invocation.Action}'.");
                        }
                        else
                        {
                            Console.Error.WriteLine(
                                $"Legacy plugin '{invocation.PluginId}' rejected action '{invocation.Action}'.");
                        }
                        break;
                    case HostMessageTypes.TtsRequest:
                        var ttsRequest = envelope.Payload.Deserialize<HostTtsRequest>()
                                         ?? throw new InvalidDataException(
                                             "TTS request payload is invalid.");
                        if (Volatile.Read(ref pluginRuntimeReady) == 1 &&
                            pluginRuntime?.PlayTts(ttsRequest.Text) == true)
                        {
                            Console.WriteLine(
                                $"Game-side TTS request reached the isolated provider; source={ttsRequest.Source}.");
                        }
                        else
                        {
                            Console.Error.WriteLine(
                                $"Game-side TTS request was rejected; source={ttsRequest.Source}.");
                        }
                        break;
                    case HostMessageTypes.FfxivEntities:
                        var entitySnapshot =
                            envelope.Payload.Deserialize<HostFfxivEntitySnapshot>()
                            ?? throw new InvalidDataException(
                                "Game-side FFXIV entity snapshot is invalid.");
                        HostPluginBridge.ApplyFfxivEntitySnapshot(entitySnapshot);
                        break;
                    case HostMessageTypes.FfxivEntityDelta:
                        var entityDelta =
                            envelope.Payload.Deserialize<HostFfxivEntityDelta>()
                            ?? throw new InvalidDataException(
                                "Game-side FFXIV entity delta is invalid.");
                        HostPluginBridge.ApplyFfxivEntityDelta(entityDelta);
                        break;
                    case HostMessageTypes.Snapshot:
                        // Phase-two bridge receives ordered events here. Plugin
                        // execution will move behind this boundary incrementally.
                        break;
                    case HostMessageTypes.FaultInject when options.FaultInjectionEnabled:
                        var fault = envelope.Payload.Deserialize<HostFaultInjection>()
                                    ?? throw new InvalidDataException(
                                        "Fault-injection payload is invalid.");
                        if (fault.Kind != "block-reader" ||
                            fault.DurationMilliseconds is < 1 or > 30_000)
                        {
                            throw new InvalidDataException(
                                "Only a 1..30000 ms block-reader fault is supported.");
                        }

                        Console.WriteLine(
                            $"TEST ONLY: blocking Host event reader for " +
                            $"{fault.DurationMilliseconds} ms.");
                        Thread.Sleep(fault.DurationMilliseconds);
                        break;
                    default:
                        Console.Error.WriteLine($"Unsupported IPC message type: {envelope.Type}");
                        break;
                }
            }
        }
        catch (Exception ex) when (
            ex is IOException or ObjectDisposedException &&
            !shutdown.IsCancellationRequested)
        {
            Console.WriteLine($"Dalamud bridge disconnected: {ex.Message}");
        }
        finally
        {
            var postNamazuQueuesStopped = await HostPluginBridge
                .StopPostNamazuQueuesAsync()
                .ConfigureAwait(false);
            if (postNamazuQueuesStopped &&
                Volatile.Read(ref pluginRuntimeReady) == 1 &&
                Interlocked.Exchange(ref pluginRuntimeDisposed, 1) == 0)
            {
                pluginRuntime?.Dispose();
            }
            else if (!postNamazuQueuesStopped)
            {
                Console.Error.WriteLine(
                    "Legacy plugin runtime remains undisposed because a PostNamazu action is still executing; process exit is the safe fallback.");
            }
            CompleteOutbound(outbound);
            shutdown.Cancel();
            await Task.WhenAll(
                    IgnoreCancellationAsync(writer),
                    IgnoreCancellationAsync(heartbeat))
                .ConfigureAwait(false);
        }
    }

    private static async Task StartLegacyPluginsAsync(
        LegacyPluginRuntime runtime,
        HostOptions options,
        BlockingCollection<HostEnvelope> outbound,
        Func<long> nextSequence,
        Action markReady,
        CancellationToken cancellationToken)
    {
        try
        {
            runtime.Start();
            var degraded = runtime.GetStages().Any(stage =>
                string.Equals(stage.State, "failed", StringComparison.OrdinalIgnoreCase));
            await EnqueueControlAsync(
                outbound,
                HostEnvelope.Create(
                    options.SessionId,
                    nextSequence(),
                    HostMessageTypes.Health,
                    HostMessagePriority.Control,
                    new HostHealth(
                        degraded ? "plugins.degraded" : "plugins.ready",
                        $"Loaded out-of-process: {string.Join(", ", runtime.LoadedPluginIds)}",
                        DateTimeOffset.UtcNow)),
                cancellationToken).ConfigureAwait(false);
            try
            {
                markReady();
            }
            catch (Exception ex)
            {
                HostPluginBridge.ReportException(
                    "act-host",
                    "Startup critical-state replay",
                    ex);
                Console.Error.WriteLine($"ACT Host critical-state replay failed: {ex}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Legacy plugin runtime failed to start: {ex}");
            await EnqueueControlAsync(
                outbound,
                HostEnvelope.Create(
                    options.SessionId,
                    nextSequence(),
                    HostMessageTypes.Health,
                    HostMessagePriority.Control,
                    new HostHealth("plugins.failed", ex.ToString(), DateTimeOffset.UtcNow)),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task MonitorPluginStartupAsync(
        Func<bool> isReady,
        CancellationTokenSource shutdown)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15), shutdown.Token).ConfigureAwait(false);
            if (!isReady())
            {
                Console.Error.WriteLine(
                    "Traditional ACT plugin startup exceeded fifteen seconds; " +
                    "the isolated Host will exit so the game-side supervisor can recover.");
                shutdown.Cancel();
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
        }
    }

    private static void HeartbeatLoop(
        HostOptions options,
        BlockingCollection<HostEnvelope> outbound,
        Func<long> lastReceivedSequence,
        Func<long> nextSequence,
        Func<IReadOnlyList<HostPluginHealth>> pluginHealth,
        Func<IReadOnlyList<HostPluginStage>> pluginStages,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.WaitHandle.WaitOne(HostProtocol.HeartbeatInterval))
        {
            using var process = Process.GetCurrentProcess();
            var heartbeat = HostEnvelope.Create(
                    options.SessionId,
                    nextSequence(),
                    HostMessageTypes.Heartbeat,
                    HostMessagePriority.Control,
                    new HostHeartbeat(
                        lastReceivedSequence(),
                        outbound.Count,
                        0,
                        0,
                        process.WorkingSet64,
                        process.PrivateMemorySize64,
                        SystemMemoryInfo.GetAvailablePhysicalMemoryBytes(),
                        process.Threads.Count,
                        pluginHealth(),
                        pluginStages()));
            if (!TryAddOutbound(outbound, heartbeat))
            {
                Console.Error.WriteLine(
                    "ACT Host heartbeat queue is full; supervisor will treat this as unhealthy.");
            }
        }
    }

    private static void WriteLoop(
        Stream output,
        BlockingCollection<HostEnvelope> outbound,
        CancellationToken cancellationToken)
    {
        long wireSequence = 0;
        foreach (var queued in outbound.GetConsumingEnumerable(cancellationToken))
        {
            var envelope = queued with
            {
                Sequence = ++wireSequence,
            };
            HostFrameCodec.WriteAsync(output, envelope, cancellationToken)
                .AsTask()
                .GetAwaiter()
                .GetResult();
        }
    }

    private static ValueTask EnqueueControlAsync(
        BlockingCollection<HostEnvelope> writer,
        HostEnvelope envelope,
        CancellationToken cancellationToken)
    {
        try
        {
            writer.Add(envelope, cancellationToken);
        }
        catch (InvalidOperationException) when (writer.IsAddingCompleted)
        {
            // Shutdown won the race with a late plugin-startup or heartbeat message.
        }

        return ValueTask.CompletedTask;
    }

    private static bool TryAddOutbound(
        BlockingCollection<HostEnvelope> outbound,
        HostEnvelope envelope)
    {
        if (outbound.IsAddingCompleted)
        {
            return false;
        }

        try
        {
            return outbound.TryAdd(envelope);
        }
        catch (InvalidOperationException) when (outbound.IsAddingCompleted)
        {
            return false;
        }
    }

    private static void CompleteOutbound(BlockingCollection<HostEnvelope> outbound)
    {
        if (!outbound.IsAddingCompleted)
        {
            outbound.CompleteAdding();
        }
    }

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            Console.WriteLine($"IPC loop stopped after disconnect: {ex.Message}");
        }
    }
}

internal sealed record HostOptions(
    string PipeName,
    string SessionId,
    string PluginRoot,
    string ConfigRoot,
    bool FaultInjectionEnabled)
{
    public static HostOptions Parse(string[] args)
    {
        var pipeName = string.Empty;
        var sessionId = string.Empty;
        var pluginRoot = string.Empty;
        var configRoot = string.Empty;
        var faultInjectionEnabled = false;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--pipe" when index + 1 < args.Length:
                    pipeName = args[++index];
                    break;
                case "--session" when index + 1 < args.Length:
                    sessionId = args[++index];
                    break;
                case "--plugin-root" when index + 1 < args.Length:
                    pluginRoot = args[++index];
                    break;
                case "--config-root" when index + 1 < args.Length:
                    configRoot = args[++index];
                    break;
                case "--enable-fault-injection":
                    faultInjectionEnabled = true;
                    break;
            }
        }

        return new HostOptions(
            pipeName,
            sessionId,
            pluginRoot,
            configRoot,
            faultInjectionEnabled);
    }
}

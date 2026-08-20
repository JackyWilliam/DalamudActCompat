using System.Collections.Concurrent;
using System.Threading.Channels;
using DalamudActCompat.Infrastructure.Ipc;
using DalamudActCompat.Infrastructure.Logging;
using DalamudActCompat.Protocol;

namespace DalamudActCompat.Infrastructure.Processes;

public sealed class ActHostSupervisor : IAsyncDisposable
{
    private const int MaximumLogBatchItems = 128;
    private const int MaximumLogBatchCharacters = 64 * 1024;
    private const int MaximumPendingLogs = HostProtocol.DataQueueCapacity;
    private readonly CompatibilityHostAssets assets;
    private readonly CompatibilityHostProcess process;
    private readonly HostIpcClient ipc;
    private readonly PluginLogger logger;
    private readonly Func<bool> silverDasherEventsEnabled;
    private readonly Func<bool> matchaEventsEnabled;
    private readonly string hostExecutable;
    private readonly string pluginDirectory;
    private readonly string configDirectory;
    private readonly SemaphoreSlim lifecycleLock = new(1, 1);
    private readonly ConcurrentQueue<HostLogEvent> pendingLogs = new();
    private readonly Channel<HostSilverDasherNetworkEvent> silverDasherNetworkEvents;
    private readonly Task silverDasherNetworkWorker;
    private readonly Channel<MatchaNetworkDispatch> matchaNetworkEvents;
    private readonly Task matchaNetworkWorker;
    private readonly Timer logFlushTimer;
    private readonly Queue<DateTimeOffset> restartHistory = new();
    private CancellationTokenSource lifetime = new();
    private string sessionId = string.Empty;
    private string pipeName = string.Empty;
    private int pendingLogCount;
    private int logFlushActive;
    private long droppedPendingLogs;
    private long droppedMatchaNetworkEvents;
    private int restartScheduled;
    private volatile bool manuallyStopped = true;
    private volatile HostSupervisorState state = HostSupervisorState.Stopped;
    private bool combatState;
    private bool combatStateKnown;
    private HostZoneEvent? lastZone;

    public ActHostSupervisor(
        string hostDirectory,
        string pluginDirectory,
        string configDirectory,
        HostIpcClient ipc,
        PluginLogger logger,
        Func<bool>? silverDasherEventsEnabled = null,
        Func<bool>? matchaEventsEnabled = null)
    {
        assets = new CompatibilityHostAssets(hostDirectory, logger);
        process = new CompatibilityHostProcess(logger);
        this.ipc = ipc;
        this.logger = logger;
        this.silverDasherEventsEnabled = silverDasherEventsEnabled ?? (static () => false);
        this.matchaEventsEnabled = matchaEventsEnabled ?? (static () => false);
        // A versioned asset directory lets a new plugin build start even when an
        // older Host process still has the previous executable mapped by Windows.
        hostExecutable = Path.Combine(
            assets.TargetDirectory,
            "DalamudActCompat.Host.exe");
        this.pluginDirectory = pluginDirectory;
        this.configDirectory = configDirectory;
        ipc.Faulted += OnIpcFaulted;
        ipc.CommandRequested += OnCommandRequested;
        ipc.PostNamazuHeadingRequested += OnPostNamazuHeadingRequested;
        ipc.SilverDasherNotificationRequested += OnSilverDasherNotificationRequested;
        ipc.MatchaNotificationRequested += OnMatchaNotificationRequested;
        ipc.MatchaLogLineRequested += OnMatchaLogLineRequested;
        ipc.MatchaTtsRequested += OnMatchaTtsRequested;
        logFlushTimer = new Timer(
            _ => FlushLogs(),
            null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
        silverDasherNetworkEvents = Channel.CreateBounded<HostSilverDasherNetworkEvent>(
            new BoundedChannelOptions(HostProtocol.SilverDasherQueueCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest,
                AllowSynchronousContinuations = false,
            });
        silverDasherNetworkWorker = Task.Run(
            () => FlushSilverDasherNetworkAsync(lifetime.Token));
        matchaNetworkEvents = Channel.CreateBounded<MatchaNetworkDispatch>(
            new BoundedChannelOptions(HostProtocol.MatchaNetworkQueueCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest,
                AllowSynchronousContinuations = false,
            });
        matchaNetworkWorker = Task.Run(
            () => FlushMatchaNetworkAsync(lifetime.Token));
    }

    public HostSupervisorSnapshot Snapshot
        => new(
            state,
            process.IsRunning,
            ipc.Status,
            ipc.ControlQueueLength,
            ipc.DataQueueLength,
            ipc.DroppedDataMessages + Interlocked.Read(ref droppedPendingLogs),
            sessionId,
            ipc.LastWrittenSequence,
            ipc.HostLastReceivedSequence,
            ipc.HostWorkingSetBytes,
            ipc.HostThreadCount,
            ipc.HostHealthState,
            ipc.HostHealthDetail,
            ipc.PluginHealth,
            ipc.Diagnostics,
            ipc.PluginStages)
        {
            MatchaNetworkQueueLength = matchaNetworkEvents.Reader.Count,
            DroppedMatchaNetworkMessages = Interlocked.Read(ref droppedMatchaNetworkEvents),
        };

    public event EventHandler<HostCommandInvocation>? CommandRequested;

    public event EventHandler<HostPostNamazuHeading>? PostNamazuHeadingRequested;

    public event EventHandler<HostSilverDasherNotification>? SilverDasherNotificationRequested;

    public event EventHandler<HostMatchaNotification>? MatchaNotificationRequested;

    public event EventHandler<HostMatchaLogLine>? MatchaLogLineRequested;

    public event EventHandler<HostTtsRequest>? MatchaTtsRequested;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (manuallyStopped || state == HostSupervisorState.CircuitOpen)
            {
                lock (restartHistory)
                {
                    restartHistory.Clear();
                }
            }

            manuallyStopped = false;
            await StartUnlockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lifecycleLock.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            manuallyStopped = true;
            state = HostSupervisorState.Stopping;
            logFlushTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            await ipc.StopAsync(cancellationToken).ConfigureAwait(false);
            await process.StopAsync(cancellationToken).ConfigureAwait(false);
            ClearPendingLogs();
            ClearPendingSilverDasherNetwork();
            ClearPendingMatchaNetwork();
            state = HostSupervisorState.Stopped;
        }
        finally
        {
            lifecycleLock.Release();
        }
    }

    public async Task<bool> RestartAsync(CancellationToken cancellationToken)
    {
        await lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (manuallyStopped)
            {
                return false;
            }

            manuallyStopped = true;
            state = HostSupervisorState.Stopping;
            logFlushTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            await ipc.StopAsync(cancellationToken).ConfigureAwait(false);
            await process.StopAsync(cancellationToken).ConfigureAwait(false);
            ClearPendingLogs();
            ClearPendingSilverDasherNetwork();
            ClearPendingMatchaNetwork();
            state = HostSupervisorState.Stopped;

            manuallyStopped = false;
            await StartUnlockedAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            manuallyStopped = false;
            state = HostSupervisorState.Faulted;
            throw;
        }
        finally
        {
            lifecycleLock.Release();
        }
    }

    public void PublishLog(
        DateTimeOffset timestamp,
        string rawLine,
        string actLine,
        bool isImport)
    {
        if (state != HostSupervisorState.Running || isImport)
        {
            return;
        }

        while (Volatile.Read(ref pendingLogCount) >= MaximumPendingLogs &&
               pendingLogs.TryDequeue(out _))
        {
            Interlocked.Decrement(ref pendingLogCount);
            Interlocked.Increment(ref droppedPendingLogs);
        }

        pendingLogs.Enqueue(new HostLogEvent(timestamp, rawLine, isImport, actLine));
        Interlocked.Increment(ref pendingLogCount);
    }

    public void PublishZone(uint territoryId, string zoneName)
    {
        lastZone = new HostZoneEvent(territoryId, zoneName, DateTimeOffset.UtcNow);
        TryPublishState(HostMessageTypes.ZoneChanged, lastZone);
        PublishSilverDasherZone(lastZone);
    }

    public bool PublishSilverDasherNetwork(string connection, long epoch, byte[] message)
        => silverDasherEventsEnabled() &&
           state == HostSupervisorState.Running &&
           message.Length > 0 &&
           silverDasherNetworkEvents.Writer.TryWrite(
               new HostSilverDasherNetworkEvent(connection, epoch, message.ToArray()));

    public bool PublishMatchaNetworkReceived(string connection, long epoch, byte[] message)
        => PublishMatchaNetwork(sent: false, connection, epoch, message);

    public bool PublishMatchaNetworkSent(string connection, long epoch, byte[] message)
        => PublishMatchaNetwork(sent: true, connection, epoch, message);

    public void PublishEncounter(bool finished)
    {
        var nextCombatState = !finished;
        if (combatState == nextCombatState)
        {
            return;
        }

        combatState = nextCombatState;
        combatStateKnown = true;
        TryPublishState(
            finished ? HostMessageTypes.CombatEnded : HostMessageTypes.CombatStarted,
            new HostCombatEvent(nextCombatState, DateTimeOffset.UtcNow));
    }

    public bool PublishFfxivEntities(HostFfxivEntitySnapshot snapshot)
        => ipc.TryEnqueue(
            HostMessageTypes.FfxivEntities,
            HostMessagePriority.State,
            snapshot,
            deadline: DateTimeOffset.UtcNow.AddSeconds(2));

    public bool OpenPluginUi(string pluginId)
        => ipc.TryEnqueue(
            HostMessageTypes.PluginOpen,
            HostMessagePriority.Control,
            new { pluginId },
            deadline: DateTimeOffset.UtcNow.AddSeconds(2));

    public bool InvokePluginAction(
        string pluginId,
        string action,
        IReadOnlyDictionary<string, string> arguments)
        => ipc.TryEnqueue(
            HostMessageTypes.PluginInvoke,
            HostMessagePriority.Control,
            new HostPluginInvocation(pluginId, action, arguments),
            deadline: DateTimeOffset.UtcNow.AddSeconds(2));

    public bool RequestTts(string text, string source)
        => !string.IsNullOrWhiteSpace(text) &&
           ipc.TryEnqueue(
               HostMessageTypes.TtsRequest,
               HostMessagePriority.Control,
               new HostTtsRequest(text, source),
               deadline: DateTimeOffset.UtcNow.AddSeconds(2));

    public bool ReplyCommand(
        string correlationId,
        bool success,
        string status,
        string? detail)
        => ipc.TryEnqueue(
            HostMessageTypes.CommandResult,
            HostMessagePriority.Control,
            new HostCommandResult(success, status, detail),
            correlationId,
            DateTimeOffset.UtcNow.AddSeconds(2));

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        silverDasherNetworkEvents.Writer.TryComplete();
        matchaNetworkEvents.Writer.TryComplete();
        try
        {
            await silverDasherNetworkWorker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        try
        {
            await matchaNetworkWorker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await StopAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            logger.Warning("ACT Host supervisor disposal exceeded two seconds.");
        }

        ipc.Faulted -= OnIpcFaulted;
        ipc.CommandRequested -= OnCommandRequested;
        ipc.PostNamazuHeadingRequested -= OnPostNamazuHeadingRequested;
        ipc.SilverDasherNotificationRequested -= OnSilverDasherNotificationRequested;
        ipc.MatchaNotificationRequested -= OnMatchaNotificationRequested;
        ipc.MatchaLogLineRequested -= OnMatchaLogLineRequested;
        ipc.MatchaTtsRequested -= OnMatchaTtsRequested;
        logFlushTimer.Dispose();
        await ipc.DisposeAsync().ConfigureAwait(false);
        await process.DisposeAsync().ConfigureAwait(false);
        lifetime.Dispose();
        lifecycleLock.Dispose();
    }

    private async Task FlushSilverDasherNetworkAsync(CancellationToken cancellationToken)
    {
        await foreach (var networkEvent in silverDasherNetworkEvents.Reader.ReadAllAsync(
                           cancellationToken).ConfigureAwait(false))
        {
            if (state != HostSupervisorState.Running || !silverDasherEventsEnabled())
            {
                continue;
            }

            _ = ipc.TryEnqueue(
                HostMessageTypes.SilverDasherNetworkReceived,
                HostMessagePriority.SilverDasherData,
                networkEvent);
        }
    }

    private void ClearPendingSilverDasherNetwork()
    {
        while (silverDasherNetworkEvents.Reader.TryRead(out _))
        {
        }
    }

    public async Task WaitForPluginStartupAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (state is HostSupervisorState.Faulted or HostSupervisorState.CircuitOpen or
                HostSupervisorState.Stopped)
            {
                throw new InvalidOperationException(
                    $"ACT Host stopped before plugin startup completed: {state}.");
            }

            var health = ipc.HostHealthState;
            if (health is "plugins.ready" or "plugins.degraded")
            {
                return;
            }

            if (health == "plugins.failed")
            {
                throw new InvalidOperationException(
                    $"ACT Host plugin startup failed: {ipc.HostHealthDetail}");
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<HostPluginStage> WaitForPluginStageAsync(
        string pluginId,
        string stageName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stageName);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (state is HostSupervisorState.Faulted or HostSupervisorState.CircuitOpen or
                HostSupervisorState.Stopped)
            {
                throw new InvalidOperationException(
                    $"ACT Host stopped before {pluginId}/{stageName} was reported: {state}.");
            }

            var stage = ipc.PluginStages.LastOrDefault(candidate =>
                string.Equals(candidate.PluginId, pluginId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.Stage, stageName, StringComparison.OrdinalIgnoreCase));
            if (stage is not null)
            {
                return stage;
            }

            if (ipc.HostHealthState == "plugins.failed")
            {
                throw new InvalidOperationException(
                    $"ACT Host plugin startup failed: {ipc.HostHealthDetail}");
            }

            // Health is sent immediately, while per-plugin stages arrive on the next
            // heartbeat. Waiting here prevents every generic plugin from being rejected
            // merely because those two valid messages crossed the process boundary apart.
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }

    private bool PublishMatchaNetwork(
        bool sent,
        string connection,
        long epoch,
        byte[] message)
    {
        if (!matchaEventsEnabled() ||
            state != HostSupervisorState.Running ||
            message.Length == 0)
        {
            return false;
        }

        if (matchaNetworkEvents.Reader.Count >= HostProtocol.MatchaNetworkQueueCapacity)
        {
            Interlocked.Increment(ref droppedMatchaNetworkEvents);
        }

        return matchaNetworkEvents.Writer.TryWrite(
            new MatchaNetworkDispatch(
                sent,
                new HostMatchaNetworkEvent(connection, epoch, message.ToArray())));
    }

    private async Task FlushMatchaNetworkAsync(CancellationToken cancellationToken)
    {
        await foreach (var item in matchaNetworkEvents.Reader.ReadAllAsync(
                           cancellationToken).ConfigureAwait(false))
        {
            if (state != HostSupervisorState.Running || !matchaEventsEnabled())
            {
                continue;
            }

            if (!ipc.TryEnqueue(
                    item.Sent
                        ? HostMessageTypes.MatchaNetworkSent
                        : HostMessageTypes.MatchaNetworkReceived,
                    HostMessagePriority.Data,
                    item.Event))
            {
                Interlocked.Increment(ref droppedMatchaNetworkEvents);
            }
        }
    }

    private void ClearPendingMatchaNetwork()
    {
        while (matchaNetworkEvents.Reader.TryRead(out _))
        {
        }
    }

    private async Task StartUnlockedAsync(CancellationToken cancellationToken)
    {
        if (state == HostSupervisorState.Running && process.IsRunning)
        {
            return;
        }

        state = HostSupervisorState.Starting;
        assets.EnsureExtracted();
        sessionId = Guid.NewGuid().ToString("N");
        pipeName = $"DalamudActCompat-{Environment.ProcessId}-{sessionId}";
        await process.StartAsync(
                HostLaunchSpec.ForExecutable(hostExecutable),
                [
                    "--pipe", pipeName,
                    "--session", sessionId,
                    "--plugin-root", pluginDirectory,
                    "--config-root", configDirectory,
                ],
                lifetime.Token)
            .ConfigureAwait(false);
        try
        {
            await ipc.ConnectAsync(pipeName, sessionId, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await process.StopAsync(CancellationToken.None).ConfigureAwait(false);
            state = HostSupervisorState.Faulted;
            throw;
        }

        state = HostSupervisorState.Running;
        RepublishCriticalState();
        logFlushTimer.Change(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20));
        logger.Information(
            "Independent ACT Host connected. Legacy plugin migration remains separately diagnosable; " +
            "the Host command channel denies arbitrary game commands.");
    }

    private void FlushLogs()
    {
        if (state != HostSupervisorState.Running || pendingLogs.IsEmpty ||
            Interlocked.Exchange(ref logFlushActive, 1) != 0)
        {
            return;
        }

        try
        {
            var batch = new List<HostLogEvent>(MaximumLogBatchItems);
            var characters = 0;
            while (batch.Count < MaximumLogBatchItems &&
                   pendingLogs.TryPeek(out var next) &&
                   (batch.Count == 0 ||
                    characters + next.Line.Length + (next.ActLine?.Length ?? 0) <= MaximumLogBatchCharacters) &&
                   pendingLogs.TryDequeue(out next))
            {
                Interlocked.Decrement(ref pendingLogCount);
                batch.Add(next);
                characters += next.Line.Length + (next.ActLine?.Length ?? 0);
            }

            if (batch.Count == 0)
            {
                return;
            }

            if (!ipc.TryEnqueue(
                    HostMessageTypes.LogBatch,
                    HostMessagePriority.Data,
                    batch,
                    deadline: DateTimeOffset.UtcNow.AddSeconds(2)))
            {
                Interlocked.Add(ref droppedPendingLogs, batch.Count);
            }

            if (silverDasherEventsEnabled())
            {
                _ = ipc.TryEnqueue(
                    HostMessageTypes.SilverDasherLogBatch,
                    HostMessagePriority.SilverDasherData,
                    batch);
            }
        }
        finally
        {
            Volatile.Write(ref logFlushActive, 0);
        }
    }

    private void OnIpcFaulted(object? sender, Exception exception)
    {
        state = HostSupervisorState.Faulted;
        if (manuallyStopped || lifetime.IsCancellationRequested ||
            Interlocked.Exchange(ref restartScheduled, 1) != 0)
        {
            return;
        }

        _ = Task.Run(() => RestartAfterFaultAsync(exception), CancellationToken.None);
    }

    private void OnCommandRequested(object? sender, HostCommandInvocation command)
        => CommandRequested?.Invoke(this, command);

    private void OnPostNamazuHeadingRequested(
        object? sender,
        HostPostNamazuHeading heading)
        => PostNamazuHeadingRequested?.Invoke(this, heading);

    private void OnSilverDasherNotificationRequested(
        object? sender,
        HostSilverDasherNotification notification)
        => SilverDasherNotificationRequested?.Invoke(this, notification);

    private void OnMatchaNotificationRequested(
        object? sender,
        HostMatchaNotification notification)
        => MatchaNotificationRequested?.Invoke(this, notification);

    private void OnMatchaLogLineRequested(
        object? sender,
        HostMatchaLogLine logLine)
        => MatchaLogLineRequested?.Invoke(this, logLine);

    private void OnMatchaTtsRequested(
        object? sender,
        HostTtsRequest request)
        => MatchaTtsRequested?.Invoke(this, request);

    private async Task RestartAfterFaultAsync(Exception exception)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            lock (restartHistory)
            {
                while (restartHistory.TryPeek(out var oldest) &&
                       now - oldest > TimeSpan.FromMinutes(10))
                {
                    restartHistory.Dequeue();
                }

                restartHistory.Enqueue(now);
                if (restartHistory.Count > 3)
                {
                    state = HostSupervisorState.CircuitOpen;
                    manuallyStopped = true;
                    logger.Error(
                        exception,
                        "ACT Host failed more than three times in ten minutes; automatic restart is disabled.");
                    return;
                }
            }

            var attempts = restartHistory.Count;
            var delay = attempts switch
            {
                1 => TimeSpan.FromSeconds(1),
                2 => TimeSpan.FromSeconds(2),
                3 => TimeSpan.FromSeconds(5),
                _ => TimeSpan.FromSeconds(15),
            };
            logger.Warning($"ACT Host will restart after {delay.TotalSeconds:0} seconds.");
            await Task.Delay(delay, lifetime.Token).ConfigureAwait(false);
            if (manuallyStopped)
            {
                return;
            }

            await lifecycleLock.WaitAsync(lifetime.Token).ConfigureAwait(false);
            try
            {
                if (manuallyStopped)
                {
                    return;
                }

                await ipc.StopAsync(CancellationToken.None).ConfigureAwait(false);
                await process.StopAsync(CancellationToken.None).ConfigureAwait(false);
                await StartUnlockedAsync(lifetime.Token).ConfigureAwait(false);
            }
            finally
            {
                lifecycleLock.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception restartError)
        {
            logger.Error(restartError, "ACT Host automatic restart failed.");
            Interlocked.Exchange(ref restartScheduled, 0);
            OnIpcFaulted(this, restartError);
            return;
        }
        finally
        {
            Interlocked.Exchange(ref restartScheduled, 0);
        }
    }

    private void ClearPendingLogs()
    {
        while (pendingLogs.TryDequeue(out _))
        {
        }

        Interlocked.Exchange(ref pendingLogCount, 0);
    }

    private void RepublishCriticalState()
    {
        if (lastZone is not null)
        {
            TryPublishState(HostMessageTypes.ZoneChanged, lastZone);
            PublishSilverDasherZone(lastZone);
        }

        if (combatStateKnown)
        {
            TryPublishState(
                combatState ? HostMessageTypes.CombatStarted : HostMessageTypes.CombatEnded,
                new HostCombatEvent(combatState, DateTimeOffset.UtcNow));
        }
    }

    private void TryPublishState<T>(string type, T payload)
    {
        if (!ipc.TryEnqueue(
                type,
                HostMessagePriority.State,
                payload,
                deadline: DateTimeOffset.UtcNow.AddSeconds(2)))
        {
            Interlocked.Increment(ref droppedPendingLogs);
            logger.Warning(
                $"ACT Host state event '{type}' was not queued; latest state will be replayed after reconnect.");
        }
    }

    private void PublishSilverDasherZone(HostZoneEvent zone)
    {
        if (!silverDasherEventsEnabled())
        {
            return;
        }

        _ = ipc.TryEnqueue(
            HostMessageTypes.SilverDasherZoneChanged,
            HostMessagePriority.SilverDasherState,
            zone);
    }

    private sealed record MatchaNetworkDispatch(
        bool Sent,
        HostMatchaNetworkEvent Event);
}

public enum HostSupervisorState
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Faulted,
    CircuitOpen,
}

public sealed record HostSupervisorSnapshot(
    HostSupervisorState State,
    bool ProcessRunning,
    HostConnectionStatus IpcStatus,
    int ControlQueueLength,
    int DataQueueLength,
    long DroppedMessages,
    string SessionId,
    long LastWrittenSequence,
    long HostAcknowledgedSequence,
    long HostWorkingSetBytes,
    int HostThreadCount,
    string HealthState,
    string HealthDetail,
    IReadOnlyList<HostPluginHealth> PluginHealth,
    IReadOnlyList<HostDiagnostic> Diagnostics,
    IReadOnlyList<HostPluginStage> PluginStages)
{
    public int MatchaNetworkQueueLength { get; init; }

    public long DroppedMatchaNetworkMessages { get; init; }
}

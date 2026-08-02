using System.Collections.Concurrent;
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
    private readonly string hostExecutable;
    private readonly string pluginDirectory;
    private readonly string configDirectory;
    private readonly SemaphoreSlim lifecycleLock = new(1, 1);
    private readonly ConcurrentQueue<HostLogEvent> pendingLogs = new();
    private readonly Timer logFlushTimer;
    private readonly Queue<DateTimeOffset> restartHistory = new();
    private CancellationTokenSource lifetime = new();
    private string sessionId = string.Empty;
    private string pipeName = string.Empty;
    private int pendingLogCount;
    private int logFlushActive;
    private long droppedPendingLogs;
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
        PluginLogger logger)
    {
        assets = new CompatibilityHostAssets(hostDirectory, logger);
        process = new CompatibilityHostProcess(logger);
        this.ipc = ipc;
        this.logger = logger;
        hostExecutable = Path.Combine(hostDirectory, "DalamudActCompat.Host.exe");
        this.pluginDirectory = pluginDirectory;
        this.configDirectory = configDirectory;
        ipc.Faulted += OnIpcFaulted;
        ipc.CommandRequested += OnCommandRequested;
        logFlushTimer = new Timer(
            _ => FlushLogs(),
            null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
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
            ipc.PluginStages);

    public event EventHandler<HostCommandInvocation>? CommandRequested;

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
            state = HostSupervisorState.Stopped;
        }
        finally
        {
            lifecycleLock.Release();
        }
    }

    public void PublishLog(DateTimeOffset timestamp, string line, bool isImport)
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

        pendingLogs.Enqueue(new HostLogEvent(timestamp, line, isImport));
        Interlocked.Increment(ref pendingLogCount);
    }

    public void PublishZone(uint territoryId, string zoneName)
    {
        lastZone = new HostZoneEvent(territoryId, zoneName, DateTimeOffset.UtcNow);
        TryPublishCritical(HostMessageTypes.ZoneChanged, lastZone);
    }

    public void PublishEncounter(bool finished)
    {
        var nextCombatState = !finished;
        if (combatState == nextCombatState)
        {
            return;
        }

        combatState = nextCombatState;
        combatStateKnown = true;
        TryPublishCritical(
            finished ? HostMessageTypes.CombatEnded : HostMessageTypes.CombatStarted,
            new HostCombatEvent(nextCombatState, DateTimeOffset.UtcNow));
    }

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
        logFlushTimer.Dispose();
        await ipc.DisposeAsync().ConfigureAwait(false);
        await process.DisposeAsync().ConfigureAwait(false);
        lifetime.Dispose();
        lifecycleLock.Dispose();
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
                   (batch.Count == 0 || characters + next.Line.Length <= MaximumLogBatchCharacters) &&
                   pendingLogs.TryDequeue(out next))
            {
                Interlocked.Decrement(ref pendingLogCount);
                batch.Add(next);
                characters += next.Line.Length;
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
            TryPublishCritical(HostMessageTypes.ZoneChanged, lastZone);
        }

        if (combatStateKnown)
        {
            TryPublishCritical(
                combatState ? HostMessageTypes.CombatStarted : HostMessageTypes.CombatEnded,
                new HostCombatEvent(combatState, DateTimeOffset.UtcNow));
        }
    }

    private void TryPublishCritical<T>(string type, T payload)
    {
        if (!ipc.TryEnqueue(
                type,
                HostMessagePriority.Critical,
                payload,
                deadline: DateTimeOffset.UtcNow.AddSeconds(2)))
        {
            Interlocked.Increment(ref droppedPendingLogs);
            logger.Warning(
                $"Critical ACT Host event '{type}' was not queued; latest state will be replayed after reconnect.");
        }
    }
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
    IReadOnlyList<HostPluginStage> PluginStages);

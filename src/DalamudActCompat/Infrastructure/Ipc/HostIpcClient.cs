using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DalamudActCompat.Core.Models;
using DalamudActCompat.Core.State;
using DalamudActCompat.Infrastructure.Logging;
using DalamudActCompat.Protocol;

[assembly: InternalsVisibleTo("DalamudActCompat.PackageSmokeTests")]

namespace DalamudActCompat.Infrastructure.Ipc;

public sealed class HostIpcClient : IAsyncDisposable
{
    private readonly EncounterStateStore stateStore;
    private readonly PluginLogger logger;
    private readonly Func<HostPermissionSnapshot> permissionSnapshot;
    private readonly SemaphoreSlim lifecycleLock = new(1, 1);
    private readonly BoundedHostMessageQueue outbound = new();
    private readonly ConcurrentQueue<HostDiagnostic> diagnostics = new();
    private CancellationTokenSource? sessionCancellation;
    private NamedPipeClientStream? bridgeInput;
    private NamedPipeClientStream? bridgeOutput;
    private Task? writerLoop;
    private Task? readerLoop;
    private Task? watchdogLoop;
    private TaskCompletionSource? shutdownAcknowledged;
    private string sessionId = string.Empty;
    private long sendSequence;
    private long lastWrittenSequence;
    private long receivedSequence;
    private long hostLastReceivedSequence;
    private long lastHeartbeatTicks;
    private long lastHostProgressTicks;
    private long hostWorkingSetBytes;
    private int hostThreadCount;
    private volatile string hostHealthState = "stopped";
    private volatile string hostHealthDetail = string.Empty;
    private IReadOnlyList<HostPluginHealth> pluginHealth = [];
    private IReadOnlyList<HostPluginStage> pluginStages = [];
    private volatile HostConnectionStatus status = HostConnectionStatus.Stopped;

    public HostIpcClient(
        EncounterStateStore stateStore,
        PluginLogger logger,
        Func<HostPermissionSnapshot>? permissionSnapshot = null)
    {
        this.stateStore = stateStore;
        this.logger = logger;
        this.permissionSnapshot = permissionSnapshot
                                  ?? (() => new HostPermissionSnapshot(
                                      new Dictionary<string, IReadOnlyList<string>>(),
                                      []));
    }

    public event EventHandler<Exception>? Faulted;

    public event EventHandler<HostCommandInvocation>? CommandRequested;

    public event EventHandler<HostPostNamazuHeading>? PostNamazuHeadingRequested;

    public event EventHandler<HostSilverDasherNotification>? SilverDasherNotificationRequested;

    public event EventHandler<HostMatchaNotification>? MatchaNotificationRequested;

    public event EventHandler<HostMatchaLogLine>? MatchaLogLineRequested;

    public event EventHandler<HostTtsRequest>? MatchaTtsRequested;

    public HostConnectionStatus Status => status;

    public int ControlQueueLength => outbound.ControlCount;

    public int DataQueueLength => outbound.DataCount;

    public long DroppedDataMessages => outbound.DroppedDataMessages;

    internal int SilverDasherQueueLength => outbound.SilverDasherCount;

    internal long DroppedSilverDasherMessages => outbound.DroppedSilverDasherMessages;

    public long LastWrittenSequence => Volatile.Read(ref lastWrittenSequence);

    public long HostLastReceivedSequence => Volatile.Read(ref hostLastReceivedSequence);

    public long HostWorkingSetBytes => Volatile.Read(ref hostWorkingSetBytes);

    public int HostThreadCount => Volatile.Read(ref hostThreadCount);

    public string HostHealthState => hostHealthState;

    public string HostHealthDetail => hostHealthDetail;

    public IReadOnlyList<HostPluginHealth> PluginHealth
        => Volatile.Read(ref pluginHealth);

    public IReadOnlyList<HostDiagnostic> Diagnostics => diagnostics.ToArray();

    public IReadOnlyList<HostPluginStage> PluginStages
        => Volatile.Read(ref pluginStages);

    public async Task ConnectAsync(
        string pipeName,
        string expectedSessionId,
        CancellationToken cancellationToken)
    {
        await lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopUnlockedAsync(CancellationToken.None).ConfigureAwait(false);
            sessionId = expectedSessionId;
            sendSequence = 0;
            lastWrittenSequence = 0;
            receivedSequence = 0;
            hostLastReceivedSequence = 0;
            lastHeartbeatTicks = DateTimeOffset.UtcNow.UtcTicks;
            lastHostProgressTicks = lastHeartbeatTicks;
            hostWorkingSetBytes = 0;
            hostThreadCount = 0;
            hostHealthState = "connecting";
            hostHealthDetail = string.Empty;
            pluginHealth = [];
            pluginStages = [];
            status = HostConnectionStatus.Connecting;
            bridgeInput = new NamedPipeClientStream(
                ".",
                $"{pipeName}-h2g",
                PipeDirection.In,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            bridgeOutput = new NamedPipeClientStream(
                ".",
                $"{pipeName}-g2h",
                PipeDirection.Out,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await Task.WhenAll(
                    bridgeInput.ConnectAsync(TimeSpan.FromSeconds(10), cancellationToken),
                    bridgeOutput.ConnectAsync(TimeSpan.FromSeconds(10), cancellationToken))
                .ConfigureAwait(false);
            sessionCancellation = new CancellationTokenSource();
            writerLoop = Task.Run(
                () => WriteLoopAsync(bridgeOutput, sessionCancellation.Token),
                CancellationToken.None);
            readerLoop = Task.Run(
                () => ReadLoopAsync(bridgeInput, sessionCancellation.Token),
                CancellationToken.None);
            watchdogLoop = Task.Run(
                () => WatchdogLoopAsync(sessionCancellation.Token),
                CancellationToken.None);
            status = HostConnectionStatus.Connected;
            if (!TryEnqueue(
                    HostMessageTypes.Hello,
                    HostMessagePriority.Control,
                    new HostHello(
                        "game-bridge",
                        typeof(HostIpcClient).Assembly.GetName().Version?.ToString() ?? "unknown",
                        Environment.ProcessId,
                        [HostProtocol.CurrentVersion])))
            {
                throw new InvalidOperationException("Host control queue rejected the hello message.");
            }

            if (!TryEnqueue(
                    HostMessageTypes.Permissions,
                    HostMessagePriority.Control,
                    this.permissionSnapshot()))
            {
                throw new InvalidOperationException(
                    "Host control queue rejected the permission snapshot.");
            }
        }
        catch
        {
            status = HostConnectionStatus.Faulted;
            await StopUnlockedAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            lifecycleLock.Release();
        }
    }

    public bool TryEnqueue<T>(
        string type,
        HostMessagePriority priority,
        T payload,
        string? correlationId = null,
        DateTimeOffset? deadline = null)
    {
        if (Status is not (HostConnectionStatus.Connected or HostConnectionStatus.Suspect))
        {
            return false;
        }

        var envelope = HostEnvelope.Create(
            sessionId,
            0,
            type,
            priority,
            payload,
            correlationId,
            deadline);
        return outbound.TryEnqueue(envelope);
    }

    public void ApplySnapshot(Encounter? current, IReadOnlyList<Encounter> recent)
        => stateStore.Replace(current, recent);

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Status is HostConnectionStatus.Connected or HostConnectionStatus.Suspect)
            {
                shutdownAcknowledged = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var queued = TryEnqueue(
                    HostMessageTypes.Shutdown,
                    HostMessagePriority.Control,
                    new HostHealth("stopping", "Game bridge requested shutdown.", DateTimeOffset.UtcNow),
                    Guid.NewGuid().ToString("N"),
                    DateTimeOffset.UtcNow.AddSeconds(1));
                if (queued)
                {
                    try
                    {
                        await shutdownAcknowledged.Task
                            .WaitAsync(TimeSpan.FromSeconds(1), cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        logger.Warning("ACT Host did not acknowledge shutdown within one second.");
                    }
                }
            }

            await StopUnlockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lifecycleLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        lifecycleLock.Dispose();
        outbound.Dispose();
    }

    private async Task StopUnlockedAsync(CancellationToken cancellationToken)
    {
        var cancellation = sessionCancellation;
        sessionCancellation = null;
        if (cancellation is not null)
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
        }

        bridgeInput?.Dispose();
        bridgeInput = null;
        bridgeOutput?.Dispose();
        bridgeOutput = null;
        var loops = new[] { writerLoop, readerLoop, watchdogLoop }
            .Where(static task => task is not null)
            .Cast<Task>()
            .ToArray();
        writerLoop = null;
        readerLoop = null;
        watchdogLoop = null;
        if (loops.Length > 0)
        {
            try
            {
                await Task.WhenAll(loops)
                    .WaitAsync(TimeSpan.FromSeconds(1), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception) when (cancellation?.IsCancellationRequested == true)
            {
                // Cancelling the session and disposing both pipe handles is the
                // normal stop path for all three loops.
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                logger.Warning($"Host IPC loops did not stop cleanly: {ex.Message}");
            }
            catch (Exception ex)
            {
                logger.Warning($"Host IPC loop stopped with an error: {ex.Message}");
            }
        }

        cancellation?.Dispose();
        shutdownAcknowledged = null;
        outbound.Clear();
        hostHealthState = "stopped";
        hostHealthDetail = string.Empty;
        pluginHealth = [];
        pluginStages = [];
        status = HostConnectionStatus.Stopped;
    }

    private async Task WriteLoopAsync(Stream target, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var queued = await outbound.DequeueAsync(cancellationToken).ConfigureAwait(false);
            if (queued.Deadline is { } deadline && deadline < DateTimeOffset.UtcNow)
            {
                logger.Warning($"Expired Host IPC message dropped: {queued.Type}.");
                continue;
            }

            var envelope = queued with
            {
                Sequence = Interlocked.Increment(ref sendSequence),
            };
            await HostFrameCodec.WriteAsync(target, envelope, cancellationToken)
                .ConfigureAwait(false);
            var previousWritten = Interlocked.Exchange(
                ref lastWrittenSequence,
                envelope.Sequence);
            if (previousWritten <= Volatile.Read(ref hostLastReceivedSequence))
            {
                // Start the processing-stall budget when a new unacknowledged
                // window opens. Otherwise an idle connection would make the
                // next legitimate event appear five seconds overdue instantly.
                Volatile.Write(ref lastHostProgressTicks, DateTimeOffset.UtcNow.UtcTicks);
            }
        }
    }

    private async Task ReadLoopAsync(Stream source, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var envelope = await HostFrameCodec.ReadAsync(source, cancellationToken)
                    .ConfigureAwait(false);
                if (envelope is null)
                {
                    throw new EndOfStreamException("ACT Host IPC pipe closed.");
                }

                if (!string.Equals(envelope.SessionId, sessionId, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("ACT Host IPC session identifier mismatch.");
                }

                if (envelope.Sequence <= Volatile.Read(ref receivedSequence))
                {
                    throw new InvalidDataException(
                        $"ACT Host IPC sequence regressed from {receivedSequence} to {envelope.Sequence}.");
                }

                Volatile.Write(ref receivedSequence, envelope.Sequence);
                if (envelope.Deadline is { } deadline && deadline < DateTimeOffset.UtcNow)
                {
                    logger.Warning($"Expired ACT Host message dropped: {envelope.Type}.");
                    continue;
                }

                ApplyMessage(envelope);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            MarkFaulted(ex);
        }
    }

    private async Task WatchdogLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            var elapsed = DateTimeOffset.UtcNow -
                          new DateTimeOffset(Volatile.Read(ref lastHeartbeatTicks), TimeSpan.Zero);
            if (elapsed >= HostProtocol.DeadAfter)
            {
                MarkFaulted(new TimeoutException(
                    $"ACT Host heartbeat missing for {elapsed.TotalSeconds:0.0} seconds."));
                return;
            }

            var lastWritten = Volatile.Read(ref lastWrittenSequence);
            var hostAcknowledged = Volatile.Read(ref hostLastReceivedSequence);
            var progressElapsed = DateTimeOffset.UtcNow -
                                  new DateTimeOffset(
                                      Volatile.Read(ref lastHostProgressTicks),
                                      TimeSpan.Zero);
            if (lastWritten > hostAcknowledged &&
                progressElapsed >= HostProtocol.DeadAfter)
            {
                MarkFaulted(new TimeoutException(
                    "ACT Host heartbeat is alive but its event reader has not advanced for " +
                    $"{progressElapsed.TotalSeconds:0.0} seconds " +
                    $"(written={lastWritten}, acknowledged={hostAcknowledged})."));
                return;
            }

            status = elapsed >= HostProtocol.SuspectAfter
                ? HostConnectionStatus.Suspect
                : HostConnectionStatus.Connected;
        }
    }

    private void ApplyMessage(HostEnvelope envelope)
    {
        switch (envelope.Type)
        {
            case HostMessageTypes.Heartbeat:
                Volatile.Write(ref lastHeartbeatTicks, DateTimeOffset.UtcNow.UtcTicks);
                var heartbeat = envelope.Payload.Deserialize<HostHeartbeat>()
                                ?? throw new InvalidDataException(
                                    "ACT Host sent an invalid heartbeat.");
                var previousAcknowledged = Volatile.Read(ref hostLastReceivedSequence);
                if (heartbeat.LastReceivedSequence < previousAcknowledged ||
                    heartbeat.LastReceivedSequence > Volatile.Read(ref sendSequence))
                {
                    throw new InvalidDataException(
                        "ACT Host heartbeat contains an invalid received sequence " +
                        $"{heartbeat.LastReceivedSequence} " +
                        $"(previous={previousAcknowledged}, sent={sendSequence}).");
                }

                if (heartbeat.LastReceivedSequence > previousAcknowledged)
                {
                    Volatile.Write(
                        ref hostLastReceivedSequence,
                        heartbeat.LastReceivedSequence);
                    Volatile.Write(
                        ref lastHostProgressTicks,
                        DateTimeOffset.UtcNow.UtcTicks);
                }

                Volatile.Write(ref hostWorkingSetBytes, heartbeat.WorkingSetBytes);
                Volatile.Write(ref hostThreadCount, heartbeat.ThreadCount);
                Volatile.Write(ref pluginHealth, heartbeat.Plugins ?? []);
                Volatile.Write(ref pluginStages, heartbeat.Stages ?? []);
                if (heartbeat.WorkingSetBytes > HostProtocol.MaximumHostWorkingSetBytes)
                {
                    throw new InvalidDataException(
                        $"ACT Host working set {heartbeat.WorkingSetBytes} exceeds " +
                        $"{HostProtocol.MaximumHostWorkingSetBytes} bytes.");
                }

                if (heartbeat.ThreadCount > HostProtocol.MaximumHostThreadCount)
                {
                    throw new InvalidDataException(
                        $"ACT Host thread count {heartbeat.ThreadCount} exceeds " +
                        $"{HostProtocol.MaximumHostThreadCount}.");
                }
                break;
            case HostMessageTypes.Hello:
            case HostMessageTypes.HelloAck:
                Volatile.Write(ref lastHeartbeatTicks, DateTimeOffset.UtcNow.UtcTicks);
                logger.Information($"ACT Host handshake completed for session {sessionId}.");
                break;
            case HostMessageTypes.ShutdownAck:
                shutdownAcknowledged?.TrySetResult();
                break;
            case HostMessageTypes.Health:
                var health = envelope.Payload.Deserialize<HostHealth>();
                if (health is not null)
                {
                    hostHealthState = health.State;
                    hostHealthDetail = health.Detail;
                    logger.Information(
                        $"ACT Host health changed to {health.State}: {health.Detail}");
                }
                break;
            case HostMessageTypes.Diagnostic:
                var diagnostic = envelope.Payload.Deserialize<HostDiagnostic>();
                if (diagnostic is not null)
                {
                    diagnostics.Enqueue(diagnostic);
                    while (diagnostics.Count > 100 && diagnostics.TryDequeue(out _))
                    {
                    }

                    logger.Warning(
                        $"ACT Host diagnostic [{diagnostic.PluginId}/{diagnostic.Phase}] " +
                        $"{diagnostic.ExceptionType}: {diagnostic.Message} " +
                        $"(repeat={diagnostic.RepeatCount}, thread={diagnostic.ThreadId}).");
                }
                break;
            case HostMessageTypes.Snapshot:
                var message = envelope.Payload.Deserialize<HostIpcMessage>();
                if (message?.Current is not null)
                {
                    ApplySnapshot(
                        HostIpcMapper.ToEncounter(message.Current),
                        message.Recent?.Select(HostIpcMapper.ToEncounter).ToArray()
                        ?? Array.Empty<Encounter>());
                }
                break;
            case HostMessageTypes.CommandRequest:
                var request = envelope.Payload.Deserialize<HostCommandRequest>();
                if (request is null || string.IsNullOrWhiteSpace(envelope.CorrelationId))
                {
                    logger.Warning("ACT Host sent an invalid command request; request denied.");
                    break;
                }

                CommandRequested?.Invoke(
                    this,
                    new HostCommandInvocation(envelope.CorrelationId, request));
                break;
            case HostMessageTypes.PostNamazuSetHeading:
                var heading = envelope.Payload.Deserialize<HostPostNamazuHeading>();
                var headingReceivedAt = DateTimeOffset.UtcNow;
                if (heading is null ||
                    heading.Address == 0 ||
                    !float.IsFinite(heading.Heading) ||
                    heading.Timestamp > headingReceivedAt.AddSeconds(1) ||
                    heading.Timestamp < headingReceivedAt.AddSeconds(-2))
                {
                    logger.Warning("ACT Host sent an invalid or stale PostNamazu heading; request denied.");
                    break;
                }

                PostNamazuHeadingRequested?.Invoke(this, heading);
                break;
            case HostMessageTypes.SilverDasherNotification:
                var notification = envelope.Payload.Deserialize<HostSilverDasherNotification>();
                if (notification is null ||
                    string.IsNullOrWhiteSpace(notification.Message) ||
                    notification.Message.Length > 512 ||
                    notification.Detail is null ||
                    notification.Detail.Length > 512)
                {
                    logger.Warning("ACT Host sent an invalid SilverDasher notification; request denied.");
                    break;
                }

                SilverDasherNotificationRequested?.Invoke(this, notification);
                break;
            case HostMessageTypes.MatchaNotification:
                var matchaNotification = envelope.Payload.Deserialize<HostMatchaNotification>();
                if (matchaNotification is null ||
                    string.IsNullOrWhiteSpace(matchaNotification.Message) ||
                    matchaNotification.Message.Length > 1024 ||
                    !Enum.IsDefined(matchaNotification.Kind))
                {
                    logger.Warning("Matcha Host sent an invalid notification; request denied.");
                    break;
                }

                MatchaNotificationRequested?.Invoke(this, matchaNotification);
                break;
            case HostMessageTypes.MatchaLogLine:
                var matchaLogLine = envelope.Payload.Deserialize<HostMatchaLogLine>();
                if (matchaLogLine is null ||
                    string.IsNullOrWhiteSpace(matchaLogLine.Line) ||
                    matchaLogLine.Line.Length > 64 * 1024)
                {
                    logger.Warning("Matcha Host sent an invalid log line; request denied.");
                    break;
                }

                MatchaLogLineRequested?.Invoke(this, matchaLogLine);
                break;
            case HostMessageTypes.MatchaTtsRequest:
                var matchaTts = envelope.Payload.Deserialize<HostTtsRequest>();
                if (matchaTts is null ||
                    !string.Equals(matchaTts.Source, "matcha", StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace(matchaTts.Text) ||
                    matchaTts.Text.Length > 2000)
                {
                    logger.Warning("Matcha Host sent an invalid TTS request; request denied.");
                    break;
                }

                MatchaTtsRequested?.Invoke(this, matchaTts);
                break;
        }
    }

    private void MarkFaulted(Exception exception)
    {
        if (Status == HostConnectionStatus.Faulted)
        {
            return;
        }

        status = HostConnectionStatus.Faulted;
        logger.Error(exception, "ACT Host IPC faulted; FFXIV remains isolated.");
        Faulted?.Invoke(this, exception);
    }
}

public sealed record HostCommandInvocation(
    string CorrelationId,
    HostCommandRequest Request);

public enum HostConnectionStatus
{
    Stopped,
    Connecting,
    Connected,
    Suspect,
    Faulted,
}

internal sealed class BoundedHostMessageQueue : IDisposable
{
    private readonly object syncRoot = new();
    private readonly Queue<HostEnvelope> control = new();
    private readonly Queue<HostEnvelope> data = new();
    private readonly Dictionary<string, HostEnvelope> state = new(StringComparer.Ordinal);
    private readonly Queue<HostEnvelope> silverDasherData = new();
    private readonly Dictionary<string, HostEnvelope> silverDasherState = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim available = new(0);
    private long droppedDataMessages;
    private long droppedSilverDasherMessages;
    private bool disposed;

    public int ControlCount
    {
        get
        {
            lock (syncRoot)
            {
                return control.Count;
            }
        }
    }

    public int DataCount
    {
        get
        {
            lock (syncRoot)
            {
                return data.Count + state.Count;
            }
        }
    }

    public long DroppedDataMessages => Interlocked.Read(ref droppedDataMessages);

    public int SilverDasherCount
    {
        get
        {
            lock (syncRoot)
            {
                return silverDasherData.Count + silverDasherState.Count;
            }
        }
    }

    public long DroppedSilverDasherMessages
        => Interlocked.Read(ref droppedSilverDasherMessages);

    public bool TryEnqueue(HostEnvelope envelope)
    {
        var added = false;
        lock (syncRoot)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            switch (envelope.Priority)
            {
                case HostMessagePriority.Control:
                case HostMessagePriority.Critical:
                    if (control.Count >= HostProtocol.ControlQueueCapacity)
                    {
                        return false;
                    }

                    control.Enqueue(envelope);
                    added = true;
                    break;
                case HostMessagePriority.State:
                    var stateKey = envelope.Type is
                        HostMessageTypes.CombatStarted or HostMessageTypes.CombatEnded
                            ? "event.combat.state"
                            : envelope.Type;
                    // Combat start/end are two wire messages for one logical state. Using one
                    // key preserves the latest transition when a noisy open-world encounter
                    // produces faster than the isolated Host can consume.
                    added = !state.ContainsKey(stateKey);
                    state[stateKey] = envelope;
                    break;
                case HostMessagePriority.SilverDasherState:
                    added = !silverDasherState.ContainsKey(envelope.Type);
                    silverDasherState[envelope.Type] = envelope;
                    break;
                case HostMessagePriority.SilverDasherData:
                    if (silverDasherData.Count >= HostProtocol.SilverDasherQueueCapacity)
                    {
                        silverDasherData.Dequeue();
                        Interlocked.Increment(ref droppedSilverDasherMessages);
                    }
                    else
                    {
                        added = true;
                    }

                    silverDasherData.Enqueue(envelope);
                    break;
                default:
                    if (data.Count >= HostProtocol.DataQueueCapacity)
                    {
                        data.Dequeue();
                        Interlocked.Increment(ref droppedDataMessages);
                    }
                    else
                    {
                        added = true;
                    }

                    data.Enqueue(envelope);
                    break;
            }
        }

        if (added)
        {
            available.Release();
        }

        return true;
    }

    public async ValueTask<HostEnvelope> DequeueAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await available.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (syncRoot)
            {
                if (control.TryDequeue(out var controlMessage))
                {
                    return controlMessage;
                }

                if (state.Count > 0)
                {
                    var pair = state.First();
                    state.Remove(pair.Key);
                    return pair.Value;
                }

                if (data.TryDequeue(out var dataMessage))
                {
                    return dataMessage;
                }

                if (silverDasherState.Count > 0)
                {
                    var pair = silverDasherState.First();
                    silverDasherState.Remove(pair.Key);
                    return pair.Value;
                }

                if (silverDasherData.TryDequeue(out var silverDasherMessage))
                {
                    return silverDasherMessage;
                }
            }
        }
    }

    public void Clear()
    {
        lock (syncRoot)
        {
            control.Clear();
            data.Clear();
            state.Clear();
            silverDasherData.Clear();
            silverDasherState.Clear();
            while (available.Wait(0))
            {
            }
        }
    }

    public void Dispose()
    {
        lock (syncRoot)
        {
            disposed = true;
        }

        available.Dispose();
    }
}

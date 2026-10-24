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
    // The Host reader also replays startup-buffered events and runs plugin callbacks, so a
    // busy but healthy reader may hold a frame for a while. Match the Host's own 15 s plugin
    // startup limit and fault only a reader that is genuinely stuck.
    private static readonly TimeSpan ReaderStallLimit = TimeSpan.FromSeconds(15);
    private readonly EncounterStateStore stateStore;
    private readonly PluginLogger logger;
    private readonly Func<HostPermissionSnapshot> permissionSnapshot;
    private readonly Func<HostGameContext?> gameContext;
    private readonly Func<string?> combatLogDirectory;
    private string? publishedCombatLogDirectory;
    private readonly string expectedTtsSource;
    private readonly SemaphoreSlim lifecycleLock = new(1, 1);
    private readonly object statusLock = new();
    private readonly BoundedHostMessageQueue outbound;
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
    private long pendingWriteStartedTicks;
    private long hostWorkingSetBytes;
    private long hostPrivateBytes;
    private long availablePhysicalMemoryBytes;
    private int hostThreadCount;
    private volatile string hostHealthState = "stopped";
    private volatile string hostHealthDetail = string.Empty;
    private IReadOnlyList<HostPluginHealth> pluginHealth = [];
    private IReadOnlyList<HostPluginStage> pluginStages = [];
    private volatile HostConnectionStatus status = HostConnectionStatus.Stopped;

    public HostIpcClient(
        EncounterStateStore stateStore,
        PluginLogger logger,
        Func<HostPermissionSnapshot>? permissionSnapshot = null,
        Func<HostGameContext?>? gameContext = null,
        Func<string?>? combatLogDirectory = null,
        string expectedTtsSource = "matcha")
    {
        this.stateStore = stateStore;
        this.logger = logger;
        outbound = new BoundedHostMessageQueue(
            type => logger.Warning($"Expired Host IPC message dropped: {type}."));
        this.permissionSnapshot = permissionSnapshot
                                   ?? (() => new HostPermissionSnapshot(
                                       new Dictionary<string, IReadOnlyList<string>>(),
                                       []));
        this.gameContext = gameContext ?? (static () => null);
        this.combatLogDirectory = combatLogDirectory ?? (static () => null);
        this.expectedTtsSource = expectedTtsSource;
    }

    public event EventHandler<Exception>? Faulted;

    public event EventHandler<HostCommandInvocation>? CommandRequested;

    public event EventHandler<HostPostNamazuHeading>? PostNamazuHeadingRequested;

    public event EventHandler<HostSilverDasherNotification>? SilverDasherNotificationRequested;

    public event EventHandler<HostMatchaNotification>? MatchaNotificationRequested;

    public event EventHandler<HostMatchaLogLine>? MatchaLogLineRequested;

    public event EventHandler<HostTtsRequest>? MatchaTtsRequested;

    public event EventHandler<HostResourceSample>? ResourceSampleReceived;

    public HostConnectionStatus Status => status;

    public int ControlQueueLength => outbound.ControlCount;

    public int DataQueueLength => outbound.DataCount;

    public long DroppedDataMessages => outbound.DroppedDataMessages;

    internal int SilverDasherQueueLength => outbound.SilverDasherCount;

    internal long DroppedSilverDasherMessages => outbound.DroppedSilverDasherMessages;

    public long LastWrittenSequence => Volatile.Read(ref lastWrittenSequence);

    public long HostLastReceivedSequence => Volatile.Read(ref hostLastReceivedSequence);

    public long HostWorkingSetBytes => Volatile.Read(ref hostWorkingSetBytes);

    public long HostPrivateBytes => Volatile.Read(ref hostPrivateBytes);

    public long AvailablePhysicalMemoryBytes
        => Volatile.Read(ref availablePhysicalMemoryBytes);

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
            Volatile.Write(ref pendingWriteStartedTicks, 0);
            hostWorkingSetBytes = 0;
            hostPrivateBytes = 0;
            availablePhysicalMemoryBytes = 0;
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
            // A quick stop/reconnect may replace the fields before Task.Run starts.
            // Each loop must retain the stream and token of its own session.
            var input = bridgeInput;
            var output = bridgeOutput;
            var token = sessionCancellation.Token;
            // Publish Connected before loops can report a fault, so startup cannot
            // overwrite an immediate disconnect with a healthy-looking status.
            status = HostConnectionStatus.Connected;
            writerLoop = Task.Run(
                () => ObserveLoopAsync(() => WriteLoopAsync(output, token), token),
                CancellationToken.None);
            readerLoop = Task.Run(
                () => ReadLoopAsync(input, token),
                CancellationToken.None);
            watchdogLoop = Task.Run(
                () => ObserveLoopAsync(() => WatchdogLoopAsync(token), token),
                CancellationToken.None);
            if (!TryEnqueue(
                    HostMessageTypes.Hello,
                    HostMessagePriority.Control,
                    new HostHello(
                        "game-bridge",
                        typeof(HostIpcClient).Assembly.GetName().Version?.ToString() ?? "unknown",
                        Environment.ProcessId,
                        [HostProtocol.CurrentVersion],
                        gameContext(),
                        publishedCombatLogDirectory = combatLogDirectory())))
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
        }
        finally
        {
            // Cancelling the graceful ACK wait must not cancel mandatory pipe cleanup.
            try { await StopUnlockedAsync(CancellationToken.None).ConfigureAwait(false); }
            finally { lifecycleLock.Release(); }
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
                outbound.RecordExpired(queued);
                continue;
            }

            var envelope = queued with
            {
                Sequence = Interlocked.Increment(ref sendSequence),
            };
            // The pipes have no buffer, so a write completes only when the Host reads it.
            // While its reader is stuck in a plugin callback, this write stays pending and
            // the acknowledged sequence keeps matching the written one; time the write itself.
            Volatile.Write(ref pendingWriteStartedTicks, DateTimeOffset.UtcNow.UtcTicks);
            try
            {
                await HostFrameCodec.WriteAsync(target, envelope, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                Volatile.Write(ref pendingWriteStartedTicks, 0);
            }
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

    private async Task ObserveLoopAsync(Func<Task> loop, CancellationToken cancellationToken)
    {
        try { await loop().ConfigureAwait(false); }
        catch (Exception) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            // A serialization/write or watchdog failure can leave incoming heartbeats
            // healthy forever. Report it immediately so the supervisor can recover.
            MarkFaulted(ex);
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
        catch (Exception) when (cancellationToken.IsCancellationRequested)
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
            if (Status == HostConnectionStatus.Faulted) return;
            // Follow the parser's actual writer, including a failed folder change that
            // leaves the old writer active. A coalesced state frame cannot flood IPC.
            var directory = combatLogDirectory();
            if (!string.Equals(directory, publishedCombatLogDirectory, StringComparison.Ordinal) &&
                TryEnqueue(HostMessageTypes.CombatLogDirectory, HostMessagePriority.State, directory))
                publishedCombatLogDirectory = directory;
            var elapsed = DateTimeOffset.UtcNow -
                          new DateTimeOffset(Volatile.Read(ref lastHeartbeatTicks), TimeSpan.Zero);
            if (elapsed >= HostProtocol.DeadAfter)
            {
                MarkFaulted(new TimeoutException(
                    $"ACT Host heartbeat missing for {elapsed.TotalSeconds:0.0} seconds."));
                return;
            }

            var pendingWriteSince = Volatile.Read(ref pendingWriteStartedTicks);
            var pendingWriteElapsed = pendingWriteSince == 0
                ? TimeSpan.Zero
                : DateTimeOffset.UtcNow - new DateTimeOffset(pendingWriteSince, TimeSpan.Zero);
            if (pendingWriteElapsed >= ReaderStallLimit)
            {
                MarkFaulted(new TimeoutException(
                    "ACT Host heartbeat is alive but it has not read the next IPC frame for " +
                    $"{pendingWriteElapsed.TotalSeconds:0.0} seconds; its event reader is blocked."));
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

            lock (statusLock)
            {
                // A writer may fail during this tick. A healthy heartbeat must not
                // undo that terminal fault or admit more unsendable messages.
                if (Status == HostConnectionStatus.Faulted) return;
                status = elapsed >= HostProtocol.SuspectAfter
                    ? HostConnectionStatus.Suspect
                    : HostConnectionStatus.Connected;
            }
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
                Volatile.Write(ref hostPrivateBytes, heartbeat.PrivateBytes);
                Volatile.Write(
                    ref availablePhysicalMemoryBytes,
                    heartbeat.AvailablePhysicalMemoryBytes);
                Volatile.Write(ref hostThreadCount, heartbeat.ThreadCount);
                Volatile.Write(ref pluginHealth, heartbeat.Plugins ?? []);
                Volatile.Write(ref pluginStages, heartbeat.Stages ?? []);
                ResourceSampleReceived?.Invoke(
                    this,
                    new HostResourceSample(
                        DateTimeOffset.UtcNow,
                        heartbeat.WorkingSetBytes,
                        heartbeat.PrivateBytes,
                        heartbeat.AvailablePhysicalMemoryBytes,
                        heartbeat.ThreadCount));

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
                    !string.Equals(matchaTts.Source, expectedTtsSource, StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace(matchaTts.Text) ||
                    matchaTts.Text.Length > 2000)
                {
                    logger.Warning("ACT Host sent an invalid TTS request; request denied.");
                    break;
                }

                MatchaTtsRequested?.Invoke(this, matchaTts);
                break;
        }
    }

    private void MarkFaulted(Exception exception)
    {
        lock (statusLock)
        {
            if (Status == HostConnectionStatus.Faulted) return;
            status = HostConnectionStatus.Faulted;
        }
        logger.Error(exception, "ACT Host IPC faulted; FFXIV remains isolated.");
        Faulted?.Invoke(this, exception);
    }
}

public sealed record HostResourceSample(
    DateTimeOffset Timestamp,
    long WorkingSetBytes,
    long PrivateBytes,
    long AvailablePhysicalMemoryBytes,
    int ThreadCount);

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

internal sealed class BoundedHostMessageQueue(Action<string>? expired = null) : IDisposable
{
    private readonly object syncRoot = new();
    private readonly Queue<HostEnvelope> control = new();
    private readonly LinkedList<HostEnvelope> data = new();
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
            if (IsOrderedState(envelope.Type))
            {
                added = EnqueueOrderedStateLocked(envelope);
            }
            else switch (envelope.Priority)
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
                    added = !state.ContainsKey(envelope.Type);
                    state[envelope.Type] = envelope;
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
                        DropOldestDataLocked();
                    }
                    else
                    {
                        added = true;
                    }

                    data.AddLast(envelope);
                    break;
            }
        }

        if (added)
        {
            available.Release();
        }

        return true;
    }

    /// <summary>Counts a message discarded because its deadline passed before it was written.</summary>
    public void RecordExpired(HostEnvelope envelope)
    {
        Interlocked.Increment(ref droppedDataMessages);
        expired?.Invoke(envelope.Type);
    }

    public async ValueTask<HostEnvelope> DequeueAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await available.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (syncRoot)
            {
                while (TryTakeLocked(out var envelope))
                {
                    // Discard stale messages here, where they are counted, instead of letting
                    // the writer skip them invisibly after a stalled Host read.
                    if (envelope.Deadline is { } deadline && deadline < DateTimeOffset.UtcNow)
                    {
                        RecordExpired(envelope);
                        continue;
                    }

                    return envelope;
                }
            }
        }
    }

    private bool TryTakeLocked(out HostEnvelope envelope)
    {
        if (control.TryDequeue(out envelope!))
        {
            return true;
        }

        if (state.Count > 0)
        {
            var pair = state.First();
            state.Remove(pair.Key);
            envelope = pair.Value;
            return true;
        }

        if (data.First is { } next)
        {
            data.RemoveFirst();
            envelope = next.Value;
            return true;
        }

        if (silverDasherState.Count > 0)
        {
            var pair = silverDasherState.First();
            silverDasherState.Remove(pair.Key);
            envelope = pair.Value;
            return true;
        }

        return silverDasherData.TryDequeue(out envelope!);
    }

    // Triggernometry tags every log line with the zone and combat state current when the
    // Host processes it, so these transitions travel in the log lane at their exact place.
    private static bool IsOrderedState(string type)
        => type is HostMessageTypes.ZoneChanged or HostMessageTypes.CombatStarted or HostMessageTypes.CombatEnded;

    private static bool IsCombatState(string type)
        => type is HostMessageTypes.CombatStarted or HostMessageTypes.CombatEnded;

    private bool EnqueueOrderedStateLocked(HostEnvelope envelope)
    {
        if (IsCombatState(envelope.Type) && data.Last is { } tail && IsCombatState(tail.Value.Type))
        {
            if (tail.Value.Type == envelope.Type)
            {
                tail.Value = envelope;
                return false;
            }

            // Back-to-back transitions with no logs between them come from noisy open-world
            // combat detection. Collapse whole start/end cycles, but keep the first and the
            // newest edge so TN still sees the end-then-start that resets per-pull state.
            if (tail.Previous is { } middle && IsCombatState(middle.Value.Type) &&
                middle.Previous is { } first && IsCombatState(first.Value.Type))
            {
                data.Remove(middle);
                data.Remove(tail);
            }
        }

        var added = true;
        if (data.Count >= HostProtocol.DataQueueCapacity)
        {
            DropOldestDataLocked();
            added = false;
        }

        data.AddLast(envelope);
        return added;
    }

    private void DropOldestDataLocked()
    {
        // Ordered zone/combat state costs a few bytes and must never be shed for log volume.
        var node = data.First;
        while (node is not null && IsOrderedState(node.Value.Type))
        {
            node = node.Next;
        }

        data.Remove(node ?? data.First!);
        Interlocked.Increment(ref droppedDataMessages);
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

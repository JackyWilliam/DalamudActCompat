using System.Buffers.Binary;
using System.Text.Json;

namespace DalamudActCompat.Protocol;

public static class HostProtocol
{
    public const int CurrentVersion = 4;
    public const int MaximumFrameBytes = 1024 * 1024;
    public const int ControlQueueCapacity = 256;
    public const int DataQueueCapacity = 8192;
    public const int SilverDasherQueueCapacity = 512;
    public const int MatchaNetworkQueueCapacity = 1024;
    public const long MaximumHostWorkingSetBytes = 1536L * 1024 * 1024;
    public const int MaximumHostThreadCount = 256;
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan SuspectAfter = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan DeadAfter = TimeSpan.FromSeconds(5);
}

public enum HostMessagePriority
{
    Control,
    Critical,
    Data,
    State,
    SilverDasherData,
    SilverDasherState,
}

public static class HostMessageTypes
{
    public const string Hello = "hello";
    public const string HelloAck = "hello.ack";
    public const string Heartbeat = "heartbeat";
    public const string Health = "health";
    public const string LogBatch = "event.log.batch";
    public const string ZoneChanged = "event.zone";
    public const string CombatStarted = "event.combat.start";
    public const string CombatEnded = "event.combat.end";
    public const string FfxivEntities = "state.ffxiv.entities";
    public const string PostNamazuSetHeading = "postnamazu.state.heading";
    public const string Snapshot = "snapshot";
    public const string Shutdown = "shutdown";
    public const string ShutdownAck = "shutdown.ack";
    public const string CommandRequest = "command.request";
    public const string CommandResult = "command.result";
    public const string PluginOpen = "plugin.ui.open";
    public const string PluginInvoke = "plugin.invoke";
    public const string TtsRequest = "tts.request";
    public const string Permissions = "permission.snapshot";
    public const string FaultInject = "test.fault.inject";
    public const string Diagnostic = "diagnostic.exception";
    public const string SilverDasherLogBatch = "silverdasher.event.log.batch";
    public const string SilverDasherZoneChanged = "silverdasher.event.zone";
    public const string SilverDasherNetworkReceived = "silverdasher.event.network.received";
    public const string SilverDasherNotification = "silverdasher.notification";
    public const string MatchaNetworkReceived = "matcha.event.network.received";
    public const string MatchaNetworkSent = "matcha.event.network.sent";
    public const string MatchaNotification = "matcha.notification";
    public const string MatchaLogLine = "matcha.log-line";
    public const string MatchaTtsRequest = "matcha.tts.request";
}

public sealed record HostEnvelope(
    int ProtocolVersion,
    string SessionId,
    long Sequence,
    string Type,
    HostMessagePriority Priority,
    string? CorrelationId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? Deadline,
    JsonElement Payload)
{
    public static HostEnvelope Create<T>(
        string sessionId,
        long sequence,
        string type,
        HostMessagePriority priority,
        T payload,
        string? correlationId = null,
        DateTimeOffset? deadline = null)
        => new(
            HostProtocol.CurrentVersion,
            sessionId,
            sequence,
            type,
            priority,
            correlationId,
            DateTimeOffset.UtcNow,
            deadline,
            JsonSerializer.SerializeToElement(payload));
}

public sealed record HostHello(
    string Role,
    string Version,
    int ProcessId,
    IReadOnlyList<int> SupportedProtocolVersions);

public sealed record HostHeartbeat(
    long LastReceivedSequence,
    int ControlQueueLength,
    int DataQueueLength,
    long DroppedDataMessages,
    long WorkingSetBytes,
    int ThreadCount,
    IReadOnlyList<HostPluginHealth> Plugins,
    IReadOnlyList<HostPluginStage> Stages);

public sealed record HostPluginHealth(
    string PluginId,
    string State,
    long CompletedEvents,
    long Exceptions,
    long SlowCalls,
    long LastDurationMilliseconds,
    string? ActiveCallback,
    long ActiveMilliseconds,
    bool CircuitOpen);

public sealed record HostPluginStage(
    string PluginId,
    string Stage,
    string State,
    string Detail,
    DateTimeOffset UpdatedAt);

public sealed record HostHealth(
    string State,
    string Detail,
    DateTimeOffset Since);

public sealed record HostLogEvent(
    DateTimeOffset Timestamp,
    string Line,
    bool IsImport,
    string? ActLine = null);

public sealed record HostZoneEvent(
    uint TerritoryId,
    string ZoneName,
    DateTimeOffset Timestamp);

public sealed record HostSilverDasherNetworkEvent(
    string Connection,
    long Epoch,
    byte[] Message);

public sealed record HostSilverDasherNotification(
    string Message,
    string Detail);

public sealed record HostMatchaNetworkEvent(
    string Connection,
    long Epoch,
    byte[] Message);

public enum HostMatchaNotificationKind
{
    General,
    WorldChanged,
    DutyEntered,
}

public sealed record HostMatchaNotification(
    string Message,
    HostMatchaNotificationKind Kind = HostMatchaNotificationKind.General);

public sealed record HostMatchaLogLine(string Line);

public sealed record HostCombatEvent(
    bool InCombat,
    DateTimeOffset Timestamp);

public sealed record HostFfxivEntitySnapshot(
    uint TerritoryId,
    uint CurrentPlayerId,
    DateTimeOffset Timestamp,
    IReadOnlyList<HostFfxivCombatant> Combatants);

public sealed record HostPostNamazuHeading(
    long Address,
    float Heading,
    DateTimeOffset Timestamp);

public sealed record HostFfxivCombatant(
    uint Id,
    uint OwnerId,
    byte Type,
    int Job,
    int Level,
    string Name,
    uint CurrentHp,
    uint MaxHp,
    uint CurrentMp,
    uint MaxMp,
    uint CurrentCp,
    uint MaxCp,
    uint CurrentGp,
    uint MaxGp,
    bool IsCasting,
    uint CastId,
    uint CastTargetId,
    float CastTime,
    float MaxCastTime,
    float PosX,
    float PosY,
    float PosZ,
    float Heading,
    uint CurrentWorldId,
    uint WorldId,
    string WorldName,
    uint BNpcNameId,
    uint BNpcId,
    uint TargetId,
    byte EffectiveDistance,
    int PartyType,
    long Address,
    IReadOnlyList<HostFfxivStatus> Statuses);

public sealed record HostFfxivStatus(
    ushort Id,
    ushort Param,
    float RemainingTime,
    uint SourceId);

public sealed record HostCommandRequest(
    string PluginId,
    string Command,
    IReadOnlyDictionary<string, string> Arguments);

public sealed record HostCommandResult(
    bool Success,
    string Status,
    string? Detail);

public sealed record HostPluginInvocation(
    string PluginId,
    string Action,
    IReadOnlyDictionary<string, string> Arguments);

public sealed record HostTtsRequest(
    string Text,
    string Source);

public sealed record HostPermissionSnapshot(
    IReadOnlyDictionary<string, IReadOnlyList<string>> AllowedCapabilities,
    IReadOnlyList<string> AllowedPluginIds);

public sealed record HostFaultInjection(
    string Kind,
    int DurationMilliseconds);

public sealed record HostDiagnostic(
    DateTimeOffset Timestamp,
    string PluginId,
    string Phase,
    string ExceptionType,
    string Message,
    string StackTrace,
    string SourceAssembly,
    string SourceType,
    string SourceMethod,
    int ThreadId,
    string? ThreadName,
    bool IsWindowsFormsThread,
    long RepeatCount);

public static class HostFrameCodec
{
    public static async ValueTask WriteAsync(
        Stream stream,
        HostEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(envelope);
        if (payload.Length > HostProtocol.MaximumFrameBytes)
        {
            throw new InvalidDataException(
                $"IPC frame is {payload.Length} bytes; maximum is {HostProtocol.MaximumFrameBytes}.");
        }

        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<HostEnvelope?> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var header = new byte[sizeof(int)];
        if (!await ReadExactlyOrEofAsync(stream, header, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > HostProtocol.MaximumFrameBytes)
        {
            throw new InvalidDataException(
                $"IPC frame length {length} is outside the allowed range.");
        }

        var payload = new byte[length];
        if (!await ReadExactlyOrEofAsync(stream, payload, cancellationToken).ConfigureAwait(false))
        {
            throw new EndOfStreamException("IPC stream ended in the middle of a frame.");
        }

        var envelope = JsonSerializer.Deserialize<HostEnvelope>(payload)
                       ?? throw new InvalidDataException("IPC frame contains no envelope.");
        if (envelope.ProtocolVersion != HostProtocol.CurrentVersion)
        {
            throw new InvalidDataException(
                $"Unsupported IPC protocol {envelope.ProtocolVersion}; expected {HostProtocol.CurrentVersion}.");
        }

        if (string.IsNullOrWhiteSpace(envelope.SessionId) ||
            string.IsNullOrWhiteSpace(envelope.Type))
        {
            throw new InvalidDataException("IPC envelope is missing session or message type.");
        }

        return envelope;
    }

    private static async ValueTask<bool> ReadExactlyOrEofAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                return offset == 0 ? false : throw new EndOfStreamException();
            }

            offset += read;
        }

        return true;
    }
}

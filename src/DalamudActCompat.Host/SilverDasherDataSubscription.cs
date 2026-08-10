using System.Collections.Concurrent;
using System.Diagnostics;
using FFXIV_ACT_Plugin.Common;

namespace DalamudActCompat.Host;

internal sealed class SilverDasherDataSubscription : IDataSubscription, IDisposable
{
    private const int DispatchQueueCapacity = 512;
    private const string SilverDasherCoreAssembly = "SilverDasher.Core";
    private readonly object handlerLock = new();
    private readonly BlockingCollection<SilverEvent> dispatchQueue =
        new(new ConcurrentQueue<SilverEvent>(), DispatchQueueCapacity);
    private readonly Thread dispatchThread;
    private NetworkReceivedDelegate? networkReceived;
    private NetworkSentDelegate? networkSent;
    private CombatantAddedDelegate? combatantAdded;
    private CombatantRemovedDelegate? combatantRemoved;
    private PrimaryPlayerDelegate? primaryPlayerChanged;
    private ZoneChangedDelegate? zoneChanged;
    private PlayerStatsChangedDelegate? playerStatsChanged;
    private PartyListChangedDelegate? partyListChanged;
    private LogLineDelegate? logLine;
    private ParsedLogLineDelegate? parsedLogLine;
    private ProcessChangedDelegate? processChanged;
    private long droppedEvents;
    private int disposed;

    public SilverDasherDataSubscription()
    {
        dispatchThread = new Thread(DispatchLoop)
        {
            IsBackground = true,
            Name = "SilverDasher isolated event dispatcher",
            Priority = ThreadPriority.BelowNormal,
        };
        dispatchThread.Start();
    }

    public event NetworkReceivedDelegate NetworkReceived
    {
        add => AddHandler(ref networkReceived, value, "ReadCombatLogs");
        remove => RemoveHandler(ref networkReceived, value);
    }

    public event NetworkSentDelegate NetworkSent
    {
        add => AddHandler(ref networkSent, value, "ReadCombatLogs");
        remove => RemoveHandler(ref networkSent, value);
    }

    public event CombatantAddedDelegate CombatantAdded
    {
        add => AddHandler(ref combatantAdded, value, "ReadCombatLogs");
        remove => RemoveHandler(ref combatantAdded, value);
    }

    public event CombatantRemovedDelegate CombatantRemoved
    {
        add => AddHandler(ref combatantRemoved, value, "ReadCombatLogs");
        remove => RemoveHandler(ref combatantRemoved, value);
    }

    public event PrimaryPlayerDelegate PrimaryPlayerChanged
    {
        add => AddHandler(ref primaryPlayerChanged, value, "ReadCombatLogs");
        remove => RemoveHandler(ref primaryPlayerChanged, value);
    }

    public event ZoneChangedDelegate ZoneChanged
    {
        add => AddHandler(ref zoneChanged, value, "ReadCombatLogs");
        remove => RemoveHandler(ref zoneChanged, value);
    }

    public event PlayerStatsChangedDelegate PlayerStatsChanged
    {
        add => AddHandler(ref playerStatsChanged, value, "ReadCombatLogs");
        remove => RemoveHandler(ref playerStatsChanged, value);
    }

    public event PartyListChangedDelegate PartyListChanged
    {
        add => AddHandler(ref partyListChanged, value, "ReadCombatLogs");
        remove => RemoveHandler(ref partyListChanged, value);
    }

    public event LogLineDelegate LogLine
    {
        add => AddHandler(ref logLine, value, "ReadCombatLogs");
        remove => RemoveHandler(ref logLine, value);
    }

    public event ParsedLogLineDelegate ParsedLogLine
    {
        add => AddHandler(ref parsedLogLine, value, "ReadCombatLogs");
        remove => RemoveHandler(ref parsedLogLine, value);
    }

    public event ProcessChangedDelegate ProcessChanged
    {
        add => AddHandler(ref processChanged, value, "NativeGameMemory");
        remove => RemoveHandler(ref processChanged, value);
    }

    public long DroppedEvents => Interlocked.Read(ref droppedEvents);

    public void PublishNetwork(string connection, long epoch, byte[] message)
        => Enqueue(new NetworkEvent(connection, epoch, message));

    public void PublishZone(uint territoryId, string zoneName)
        => Enqueue(new ZoneEvent(territoryId, zoneName));

    public void PublishProcess(Process process)
        => Enqueue(new ProcessEvent(process));

    public void PublishLogs(IReadOnlyList<DalamudActCompat.Protocol.HostLogEvent> logs)
    {
        foreach (var entry in logs)
        {
            var seconds = unchecked((uint)Math.Max(0, entry.Timestamp.ToUnixTimeSeconds()));
            Enqueue(new LogEvent(ParseEventType(entry), seconds, entry.Line));
        }
    }

    private static uint ParseEventType(DalamudActCompat.Protocol.HostLogEvent entry)
    {
        var line = string.IsNullOrWhiteSpace(entry.ActLine) ? entry.Line : entry.ActLine;
        var separator = line.IndexOf('|');
        var field = separator < 0 ? line : line[..separator];
        return uint.TryParse(
            field,
            System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture,
            out var eventType)
            ? eventType
            : 0;
    }

    private void Enqueue(SilverEvent item)
    {
        if (Volatile.Read(ref disposed) == 1)
        {
            return;
        }

        try
        {
            if (dispatchQueue.TryAdd(item))
            {
                return;
            }

            if (dispatchQueue.TryTake(out _))
            {
                Interlocked.Increment(ref droppedEvents);
            }

            _ = dispatchQueue.TryAdd(item);
        }
        catch (InvalidOperationException) when (Volatile.Read(ref disposed) == 1)
        {
        }
    }

    private void DispatchLoop()
    {
        try
        {
            foreach (var item in dispatchQueue.GetConsumingEnumerable())
            {
                switch (item)
                {
                    case NetworkEvent network:
                        InvokeNetwork(network);
                        break;
                    case ZoneEvent zone:
                        InvokeZone(zone);
                        break;
                    case ProcessEvent process:
                        InvokeProcess(process);
                        break;
                    case LogEvent log:
                        InvokeLog(log);
                        break;
                }
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void InvokeNetwork(NetworkEvent item)
    {
        NetworkReceivedDelegate? handlers;
        lock (handlerLock)
        {
            handlers = networkReceived;
        }

        foreach (NetworkReceivedDelegate handler in handlers?.GetInvocationList() ?? [])
        {
            InvokeSafely(
                "NetworkReceived",
                () => handler(item.Connection, item.Epoch, item.Message));
        }
    }

    private void InvokeZone(ZoneEvent item)
    {
        ZoneChangedDelegate? handlers;
        lock (handlerLock)
        {
            handlers = zoneChanged;
        }

        foreach (ZoneChangedDelegate handler in handlers?.GetInvocationList() ?? [])
        {
            InvokeSafely("ZoneChanged", () => handler(item.TerritoryId, item.ZoneName));
        }
    }

    private void InvokeProcess(ProcessEvent item)
    {
        ProcessChangedDelegate? handlers;
        lock (handlerLock)
        {
            handlers = processChanged;
        }

        foreach (ProcessChangedDelegate handler in handlers?.GetInvocationList() ?? [])
        {
            InvokeSafely("ProcessChanged", () => handler(item.Process));
        }
    }

    private void InvokeLog(LogEvent item)
    {
        LogLineDelegate? handlers;
        lock (handlerLock)
        {
            handlers = logLine;
        }

        foreach (LogLineDelegate handler in handlers?.GetInvocationList() ?? [])
        {
            InvokeSafely("LogLine", () => handler(item.EventType, item.Seconds, item.Line));
        }
    }

    private static void InvokeSafely(string callback, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            HostPluginBridge.ReportException("silverdasher", callback, ex);
        }
    }

    private void AddHandler<T>(ref T? field, T handler, string capability)
        where T : Delegate
    {
        ArgumentNullException.ThrowIfNull(handler);
        var assemblyName = handler.Method.Module.Assembly.GetName().Name;
        if (!string.Equals(
                assemblyName,
                SilverDasherCoreAssembly,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException(
                $"SilverDasher event subscriptions reject handler assembly '{assemblyName ?? "unknown"}'.");
        }

        HostPluginBridge.DemandSilverDasherCapability(capability);
        lock (handlerLock)
        {
            field = (T?)Delegate.Combine(field, handler);
        }
    }

    private void RemoveHandler<T>(ref T? field, T handler)
        where T : Delegate
    {
        lock (handlerLock)
        {
            field = (T?)Delegate.Remove(field, handler);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 1)
        {
            return;
        }

        dispatchQueue.CompleteAdding();
        if (!dispatchThread.Join(TimeSpan.FromSeconds(1)))
        {
            Console.Error.WriteLine(
                "SilverDasher event dispatcher did not stop within one second; Host process exit will contain it.");
        }

        dispatchQueue.Dispose();
    }

    private abstract record SilverEvent;

    private sealed record NetworkEvent(string Connection, long Epoch, byte[] Message) : SilverEvent;

    private sealed record ZoneEvent(uint TerritoryId, string ZoneName) : SilverEvent;

    private sealed record ProcessEvent(Process Process) : SilverEvent;

    private sealed record LogEvent(uint EventType, uint Seconds, string Line) : SilverEvent;
}

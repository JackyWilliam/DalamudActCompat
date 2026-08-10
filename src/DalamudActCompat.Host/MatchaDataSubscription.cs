using System.Collections.Concurrent;
using FFXIV_ACT_Plugin.Common;

namespace DalamudActCompat.Host;

internal sealed class MatchaDataSubscription : IDataSubscription, IDisposable
{
    private const int DispatchQueueCapacity = 1024;
    private const string MatchaAssembly = "Cafe.Matcha";
    private readonly object handlerLock = new();
    private readonly BlockingCollection<MatchaEvent> dispatchQueue =
        new(new ConcurrentQueue<MatchaEvent>(), DispatchQueueCapacity);
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

    public MatchaDataSubscription()
    {
        dispatchThread = new Thread(DispatchLoop)
        {
            IsBackground = true,
            Name = "Matcha isolated event dispatcher",
            Priority = ThreadPriority.BelowNormal,
        };
        dispatchThread.Start();
    }

    public event NetworkReceivedDelegate NetworkReceived
    {
        add => AddHandler(ref networkReceived, value);
        remove => RemoveHandler(ref networkReceived, value);
    }

    public event NetworkSentDelegate NetworkSent
    {
        add => AddHandler(ref networkSent, value);
        remove => RemoveHandler(ref networkSent, value);
    }

    public event CombatantAddedDelegate CombatantAdded
    {
        add => AddHandler(ref combatantAdded, value);
        remove => RemoveHandler(ref combatantAdded, value);
    }

    public event CombatantRemovedDelegate CombatantRemoved
    {
        add => AddHandler(ref combatantRemoved, value);
        remove => RemoveHandler(ref combatantRemoved, value);
    }

    public event PrimaryPlayerDelegate PrimaryPlayerChanged
    {
        add => AddHandler(ref primaryPlayerChanged, value);
        remove => RemoveHandler(ref primaryPlayerChanged, value);
    }

    public event ZoneChangedDelegate ZoneChanged
    {
        add => AddHandler(ref zoneChanged, value);
        remove => RemoveHandler(ref zoneChanged, value);
    }

    public event PlayerStatsChangedDelegate PlayerStatsChanged
    {
        add => AddHandler(ref playerStatsChanged, value);
        remove => RemoveHandler(ref playerStatsChanged, value);
    }

    public event PartyListChangedDelegate PartyListChanged
    {
        add => AddHandler(ref partyListChanged, value);
        remove => RemoveHandler(ref partyListChanged, value);
    }

    public event LogLineDelegate LogLine
    {
        add => AddHandler(ref logLine, value);
        remove => RemoveHandler(ref logLine, value);
    }

    public event ParsedLogLineDelegate ParsedLogLine
    {
        add => AddHandler(ref parsedLogLine, value);
        remove => RemoveHandler(ref parsedLogLine, value);
    }

    public event ProcessChangedDelegate ProcessChanged
    {
        add => AddHandler(ref processChanged, value);
        remove => RemoveHandler(ref processChanged, value);
    }

    public long DroppedEvents => Interlocked.Read(ref droppedEvents);

    public void PublishReceived(string connection, long epoch, byte[] message)
        => Enqueue(new NetworkEvent(Sent: false, connection, epoch, message));

    public void PublishSent(string connection, long epoch, byte[] message)
        => Enqueue(new NetworkEvent(Sent: true, connection, epoch, message));

    private void Enqueue(MatchaEvent item)
    {
        if (Volatile.Read(ref disposed) != 0)
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
        catch (InvalidOperationException) when (Volatile.Read(ref disposed) != 0)
        {
        }
    }

    private void DispatchLoop()
    {
        try
        {
            foreach (var item in dispatchQueue.GetConsumingEnumerable())
            {
                if (item is NetworkEvent network)
                {
                    InvokeNetwork(network);
                }
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void InvokeNetwork(NetworkEvent item)
    {
        Delegate? handlers;
        lock (handlerLock)
        {
            handlers = item.Sent ? networkSent : networkReceived;
        }

        foreach (var handler in handlers?.GetInvocationList() ?? [])
        {
            try
            {
                if (item.Sent)
                {
                    ((NetworkSentDelegate)handler)(item.Connection, item.Epoch, item.Message);
                }
                else
                {
                    ((NetworkReceivedDelegate)handler)(item.Connection, item.Epoch, item.Message);
                }
            }
            catch (Exception ex)
            {
                HostPluginBridge.ReportException(
                    "matcha",
                    item.Sent ? "NetworkSent" : "NetworkReceived",
                    ex);
            }
        }
    }

    private static void ValidateHandler(Delegate handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var assemblyName = handler.Method.Module.Assembly.GetName().Name;
        if (!string.Equals(assemblyName, MatchaAssembly, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException(
                $"Matcha event subscriptions reject handler assembly '{assemblyName ?? "unknown"}'.");
        }

        HostPluginBridge.DemandMatchaCapability("ReadCombatLogs");
    }

    private void AddHandler<T>(ref T? field, T handler)
        where T : Delegate
    {
        ValidateHandler(handler);
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
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        dispatchQueue.CompleteAdding();
        if (!dispatchThread.Join(TimeSpan.FromSeconds(1)))
        {
            Console.Error.WriteLine(
                "Matcha event dispatcher did not stop within one second; its dedicated Host process will contain it.");
        }

        dispatchQueue.Dispose();
    }

    private abstract record MatchaEvent;

    private sealed record NetworkEvent(
        bool Sent,
        string Connection,
        long Epoch,
        byte[] Message) : MatchaEvent;
}

using DalamudActCompat.Protocol;

namespace DalamudActCompat.Host;

/// <summary>Holds game events that arrive while the legacy plugins are still starting.</summary>
internal sealed class StartupEventBuffer<TRuntime>(int logCapacity)
{
    // Live log batches expire after 2 s on the game side. Replaying an older buffered line
    // after a slow startup would announce a mechanic that has already resolved.
    internal static readonly TimeSpan MaximumReplayAge = TimeSpan.FromSeconds(2);

    private readonly LinkedList<(Action<TRuntime>? Event, IReadOnlyList<HostLogEvent>? Logs)> entries = new();
    private int logCount;

    internal int LogCount => logCount;

    internal void AddLogs(IReadOnlyList<HostLogEvent> logs)
    {
        // Only log batches yield to the bound; zone and combat state must survive in order.
        while (logCount + logs.Count > logCapacity)
        {
            var oldest = entries.First;
            while (oldest is not null && oldest.Value.Logs is null)
            {
                oldest = oldest.Next;
            }

            if (oldest is null)
            {
                break;
            }

            logCount -= oldest.Value.Logs!.Count;
            entries.Remove(oldest);
        }

        if (logs.Count <= logCapacity)
        {
            entries.AddLast((null, logs));
            logCount += logs.Count;
        }
    }

    internal void AddEvent(Action<TRuntime> replay) => entries.AddLast((replay, null));

    /// <summary>Replays buffered events in arrival order and returns the stale lines discarded.</summary>
    internal int Replay(
        TRuntime runtime,
        Action<TRuntime, IReadOnlyList<HostLogEvent>> acceptLogs,
        DateTimeOffset now,
        Action<Exception> report)
    {
        var discarded = 0;
        foreach (var (replay, logs) in entries)
        {
            try
            {
                if (replay is not null)
                {
                    replay(runtime);
                    continue;
                }

                var fresh = logs!.Where(log => now - log.Timestamp <= MaximumReplayAge).ToArray();
                discarded += logs!.Count - fresh.Length;
                if (fresh.Length != 0)
                {
                    acceptLogs(runtime, fresh);
                }
            }
            catch (Exception ex)
            {
                report(ex);
            }
        }

        entries.Clear();
        logCount = 0;
        return discarded;
    }
}

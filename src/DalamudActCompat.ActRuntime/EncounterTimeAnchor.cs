using System.Diagnostics;

namespace DalamudActCompat.ActRuntime;

// The log timestamp is retained for history and event correlation. A monotonic
// anchor advances the live view without comparing it to the Windows wall clock.
public readonly record struct EncounterTimeAnchor(DateTimeOffset Time, long Timestamp)
{
    public DateTimeOffset Now => Time + Stopwatch.GetElapsedTime(Timestamp);
}

internal sealed class EncounterEventClock(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;
    private readonly object sync = new();
    private DateTimeOffset? observedTime;
    private long observedTimestamp;

    public DateTimeOffset Now { get { lock (sync) return NowUnsafe(); } }

    public void Observe(DateTimeOffset timestamp)
    {
        lock (sync)
        {
            // Late/duplicate packets must not move the clock backwards, while an
            // actual newer event must already fit inside the measured duration.
            if (observedTime is null || timestamp > NowUnsafe())
            {
                observedTime = timestamp;
                observedTimestamp = time.GetTimestamp();
            }
        }
    }

    public EncounterTimeAnchor? Capture()
    {
        lock (sync)
            return observedTime is null ? null : new(NowUnsafe(), Stopwatch.GetTimestamp());
    }

    public void Reset() { lock (sync) observedTime = null; }

    private DateTimeOffset NowUnsafe() => observedTime is { } start
        ? start + time.GetElapsedTime(observedTimestamp, time.GetTimestamp())
        : time.GetUtcNow();

    // Startup, memory and chat lines may use local time. Only network event
    // timestamps establish the parser clock; ACT damage actions are also observed.
    public static bool IsNetworkEvent(string line)
    {
        var separator = line.IndexOf('|');
        return separator == 2 && line[..separator] is
            "20" or "21" or "22" or "24" or "25" or "26" or "30" or "34" or "37" or "38" or "39";
    }
}

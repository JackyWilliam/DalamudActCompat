using DalamudActCompat.Protocol;

namespace DalamudActCompat.Host;

/// <summary>
/// Measures how far the Host clock runs from the game's packet clock. Parser lines carry the
/// server time of their packet, so on a machine whose clock is not synced every such line
/// looks seconds old (or seconds ahead) when it arrives, although it just happened.
/// </summary>
internal sealed class GameClockOffset
{
    // The smallest arrival gap in a window is clock offset plus the minimal IPC delay; larger
    // gaps are Host stalls that event time must keep compensating. Two halves let a corrected
    // clock age out without leaving the estimate empty during quiet periods.
    private static readonly TimeSpan HalfWindow = TimeSpan.FromSeconds(15);
    // Imports and wrongly dated lines are not a clock offset worth learning.
    private static readonly TimeSpan MaximumOffset = TimeSpan.FromMinutes(10);
    private readonly object sync = new();
    private DateTimeOffset halfStartedAt;
    private TimeSpan? current;
    private TimeSpan? previous;

    internal static GameClockOffset Shared { get; } = new();

    internal TimeSpan? Offset
    {
        get
        {
            lock (sync) return Min(current, previous);
        }
    }

    internal void Observe(IReadOnlyList<HostLogEvent> logs, DateTimeOffset receivedAt)
    {
        TimeSpan? smallest = null;
        foreach (var log in logs)
        {
            if (!log.IsImport && IsPacketStamped(log))
            {
                smallest = Min(smallest, receivedAt - log.Timestamp);
            }
        }

        if (smallest is { } gap)
        {
            Observe(gap, receivedAt);
        }
    }

    internal void Observe(DateTimeOffset gameTime, DateTimeOffset receivedAt)
        => Observe(receivedAt - gameTime, receivedAt);

    /// <summary>
    /// Converts a line's own timestamp to the Host clock, or returns null before any packet
    /// line has been measured.
    /// </summary>
    internal DateTimeOffset? ToHostTime(DateTimeOffset stamped, DateTimeOffset now)
    {
        if (Offset is not { } offset)
        {
            return null;
        }

        // Packet lines arrive near the measured offset; chat and lines a trigger writes back
        // through ACT carry the Host clock and arrive near zero. Only the former are shifted.
        var gap = now - stamped;
        return (gap - offset).Duration() < gap.Duration() ? stamped + offset : stamped;
    }

    private void Observe(TimeSpan gap, DateTimeOffset receivedAt)
    {
        if (gap.Duration() > MaximumOffset)
        {
            return;
        }

        lock (sync)
        {
            // A Host clock stepped backwards starts a new half instead of stretching this one.
            if (receivedAt - halfStartedAt >= HalfWindow || receivedAt < halfStartedAt)
            {
                previous = current ?? previous;
                current = null;
                halfStartedAt = receivedAt;
            }

            current = Min(current, gap);
        }
    }

    // Lines built from combat packets (casts, abilities, statuses, ticks, HP) carry the packet's
    // whole-millisecond server time. Chat and plugin lines use the local clock and are skipped.
    private static bool IsPacketStamped(HostLogEvent log)
    {
        var line = log.Line;
        return line.Length > 3 && line[2] == '|' &&
               (line[..2] is "20" or "21" or "22" or "24" or "25" or "26" or "30" or "34" or "37" or "38" or "39") &&
               log.Timestamp.Ticks % TimeSpan.TicksPerMillisecond == 0;
    }

    private static TimeSpan? Min(TimeSpan? left, TimeSpan? right)
        => left is null ? right : right is null ? left : left < right ? left : right;
}

using System.Globalization;

namespace DalamudActCompat.Host;

public static partial class HostPluginBridge
{
    // A game timestamp this far from the Host clock is malformed or comes from an old
    // file; such a line keeps upstream's receipt-time semantics.
    private static readonly TimeSpan MaximumEventTimeSkew = TimeSpan.FromMinutes(10);
    // TestTrigger runs Fire synchronously on the same thread. A recorded time older than
    // this belongs to an earlier event and must not leak into an unrelated fire.
    private const long FireTimeValidityMilliseconds = 1_000;
    [ThreadStatic] private static DateTime triggernometryFireTime;
    [ThreadStatic] private static long triggernometryFireRecordedAt;

    /// <summary>Replaces TN's queue-time <c>DateTime.Now</c> with the line's game time on the Host clock.</summary>
    public static DateTime ResolveTriggernometryLogTime(string? text)
        => ResolveLogTime(text, DateTime.Now, GameClockOffset.Shared);

    /// <summary>Called on entry to TN's TestTrigger with the event being tested.</summary>
    public static void BeginTriggernometryFire(DateTime eventTime)
    {
        // This runs once per trigger per event, so it avoids reading the wall clock.
        triggernometryFireTime = eventTime;
        triggernometryFireRecordedAt = Environment.TickCount64;
    }

    /// <summary>Replaces TN's refire and scheduling <c>DateTime.Now</c> during a fire.</summary>
    public static DateTime TriggernometryFireNow()
    {
        var eventTime = triggernometryFireTime;
        return eventTime != default &&
               Environment.TickCount64 - triggernometryFireRecordedAt <= FireTimeValidityMilliseconds
            ? eventTime
            : DateTime.Now;
    }

    internal static DateTime ResolveLogTime(string? text, DateTime now, GameClockOffset clock)
    {
        if (!TryParseLogTime(text, now, out var parsed) || (now - parsed).Duration() > MaximumEventTimeSkew)
        {
            return now;
        }

        // Packet lines carry server time. Read raw on a machine whose clock runs ahead, they
        // look seconds old and every delayed action of their triggers fires that much early.
        var eventTime = clock.ToHostTime(new DateTimeOffset(parsed), new DateTimeOffset(now))?.LocalDateTime
                        ?? parsed;
        return eventTime < now ? eventTime : now;
    }

    private static bool TryParseLogTime(string? text, DateTime now, out DateTime parsed)
    {
        parsed = default;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        // ACT form "[HH:mm:ss.fff] ..." carries only the local time of day.
        if (text.Length >= 14 && text[0] == '[' && text[13] == ']' &&
            TimeSpan.TryParseExact(
                text.AsSpan(1, 12),
                @"hh\:mm\:ss\.fff",
                CultureInfo.InvariantCulture,
                out var timeOfDay))
        {
            parsed = now.Date + timeOfDay;
            // A line logged just before midnight is processed just after it.
            if (parsed - now > TimeSpan.FromMinutes(1))
            {
                parsed = parsed.AddDays(-1);
            }

            return true;
        }

        // Network form "TT|2026-10-24T08:34:06.4660000+08:00|...".
        var first = text.IndexOf('|');
        if (first is > 0 and <= 3)
        {
            var second = text.IndexOf('|', first + 1);
            if (second > first &&
                DateTimeOffset.TryParse(
                    text.AsSpan(first + 1, second - first - 1),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var offset))
            {
                parsed = offset.LocalDateTime;
                return true;
            }
        }

        return false;
    }
}

namespace DalamudActCompat.Host;

/// <summary>
/// Keeps a log line away from Triggernometry until the entity capture taken after that line
/// has been applied, so <c>_entity</c> never describes the game before the event being handled.
/// </summary>
/// <remarks>
/// Player report: U7b decides the P1 knockback target from the tether source's height, read
/// when the tether line arrives. The line reached TN ahead of the capture that carried the
/// source's current position, the lookup failed, and the target heard the other group's corner.
/// The existing actor wait only covers actors that were never seen; this covers every line,
/// for the one capture interval it takes the Host to catch up. Lines stay in arrival order and
/// are forwarded unchanged if the capture does not come (fail open, as before this existed).
/// </remarks>
internal sealed class TriggernometryEntityFreshnessBarrier(
    Func<long, bool> epochReached,
    Action<bool, string, string> deliver,
    Action<string> diagnostic,
    Func<long>? clock = null)
{
    // Captures follow every 30 ms; several missed in a row mean the game is stalled or the
    // sender does not publish, and holding triggers longer would cost more than stale data.
    internal const int MaximumWaitMilliseconds = 150;
    internal const int MaximumLines = 1024;
    internal const int MaximumCharacters = 1024 * 1024;
    // A stalled sender expires every line; one summary per interval keeps the log readable.
    internal const int DiagnosticIntervalMilliseconds = 10_000;
    private readonly Queue<PendingLine> pending = new();
    private readonly Func<long> now = clock ?? (() => Environment.TickCount64);
    private int characters;
    private int expiredLines;
    private long nextDiagnosticAt;
    private bool delivering;

    internal bool HasPending => pending.Count != 0;

    internal void Accept(bool isImport, string line, string zone, long epoch)
    {
        // Lines raised while one is being delivered are part of that delivery (TN's own
        // log-derived callbacks); imports replay history and have no live entities to match.
        if (delivering || isImport || string.IsNullOrEmpty(line))
        {
            deliver(isImport, line, zone);
            return;
        }

        Drain(force: false);
        if (pending.Count == 0 && IsCurrent(epoch))
        {
            Deliver(new(isImport, line, zone, epoch, 0));
            return;
        }

        if (pending.Count >= MaximumLines || characters + (long)line.Length > MaximumCharacters)
        {
            diagnostic("Entity freshness wait reached its queue limit; forwarding original logs.");
            Drain(force: true);
            Deliver(new(isImport, line, zone, epoch, 0));
            return;
        }

        pending.Enqueue(new(isImport, line, zone, epoch, now() + MaximumWaitMilliseconds));
        characters += line.Length;
    }

    internal void Pump() => Drain(force: false);

    /// <summary>
    /// Hands every held line on now. A zone or combat transition must not reach TN ahead of
    /// lines that were produced before it; slightly stale entities are the lesser error.
    /// </summary>
    internal void Flush() => Drain(force: true);

    internal void Reset()
    {
        pending.Clear();
        characters = 0;
    }

    // Epoch 0 comes from senders or internal sources that make no ordering promise.
    private bool IsCurrent(long epoch) => epoch <= 0 || epochReached(epoch);

    private void Drain(bool force)
    {
        if (delivering) return;
        while (pending.TryPeek(out var next))
        {
            if (!force && !IsCurrent(next.Epoch))
            {
                if (now() < next.Deadline) break;
                expiredLines++;
            }

            pending.Dequeue();
            characters -= next.Line.Length;
            Deliver(next);
        }

        if (expiredLines != 0 && now() >= nextDiagnosticAt)
        {
            diagnostic($"Entity snapshot did not catch up within {MaximumWaitMilliseconds} ms for " +
                       $"{expiredLines} log line(s); forwarded them without waiting.");
            expiredLines = 0;
            nextDiagnosticAt = now() + DiagnosticIntervalMilliseconds;
        }
    }

    private void Deliver(PendingLine item)
    {
        delivering = true;
        try { deliver(item.IsImport, item.Line, item.Zone); }
        finally { delivering = false; }
    }

    private sealed record PendingLine(bool IsImport, string Line, string Zone, long Epoch, long Deadline);
}

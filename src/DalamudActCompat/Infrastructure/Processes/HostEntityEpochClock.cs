namespace DalamudActCompat.Infrastructure.Processes;

/// <summary>
/// Orders forwarded log lines against captures of the game's object table.
/// </summary>
/// <remarks>
/// Log lines are produced from network packets as they arrive, while entity state reaches the
/// Host from a capture on a later game frame. Triggernometry reads <c>_entity</c> at the moment
/// it handles a line, so a line that outruns its capture is evaluated against the previous
/// position of an actor that was just moved or spawned (U7b reads a tether source's height
/// this way to pick the knockback target, and announced the opposite corner). Each line
/// therefore names the first capture that begins after it was produced, and the Host holds
/// the line until that capture has been applied.
/// </remarks>
public sealed class HostEntityEpochClock
{
    private long current;
    private long awaited;
    private long published;

    /// <summary>
    /// Reserves the epoch for a capture. Call on the framework thread before reading the object
    /// table: packets are handled on that thread too, so every line stamped with this epoch
    /// belongs to a packet the game finished applying before the capture starts.
    /// </summary>
    public long BeginCapture() => Interlocked.Increment(ref current);

    /// <summary>The epoch a line produced now has to wait for.</summary>
    public long RequireNextCapture()
    {
        var next = Volatile.Read(ref current) + 1;
        RaiseTo(ref awaited, next);
        return next;
    }

    /// <summary>
    /// Whether some forwarded line still waits for a capture that has not been sent. An
    /// unchanged object table must then be published anyway, or that line would sit in the
    /// Host until its wait expires.
    /// </summary>
    public bool IsCaptureAwaited => Volatile.Read(ref awaited) > Volatile.Read(ref published);

    public void MarkPublished(long epoch) => RaiseTo(ref published, epoch);

    private static void RaiseTo(ref long target, long value)
    {
        long seen;
        while ((seen = Volatile.Read(ref target)) < value &&
               Interlocked.CompareExchange(ref target, value, seen) != seen)
        {
        }
    }
}

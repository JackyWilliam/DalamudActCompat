namespace DalamudActCompat.ActRuntime;

// 「脱战隐藏」 with an optional delay (player request, 2026-11-10): an HTML overlay or combat
// meter may stay up for 30 or 60 seconds after combat ends so the last pull can still be read,
// then hide. 0 keeps the original behaviour of hiding at once; re-entering combat cancels the
// countdown. Shared by the event-driven HTML overlays and the per-frame meter windows.
public static class CombatAutoHidePolicy
{
    public const int MaximumDelaySeconds = 600;

    public static int ClampDelay(int seconds) => Math.Clamp(seconds, 0, MaximumDelaySeconds);

    // lastInCombatTicks is Environment.TickCount64 at the last moment combat was seen, or null
    // when there has been no combat since the window appeared (then it hides straight away).
    public static bool IsHidden(bool autoHide, int delaySeconds, bool inCombat, long? lastInCombatTicks, long nowTicks)
        => autoHide && !inCombat && RemainingMilliseconds(delaySeconds, lastInCombatTicks, nowTicks) <= 0;

    // How long the delay still keeps the window up; 0 or less once it should hide.
    public static long RemainingMilliseconds(int delaySeconds, long? lastInCombatTicks, long nowTicks)
    {
        var delay = ClampDelay(delaySeconds);
        if (delay == 0 || lastInCombatTicks is not { } last) return 0;
        return delay * 1000L - (nowTicks - last);
    }
}

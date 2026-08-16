namespace DalamudActCompat.Parser;

internal sealed class OpenWorldCombatResetTracker
{
    private bool wasBoundByDuty;
    private bool wasInCombat;

    public OpenWorldCombatResetTracker(bool boundByDuty, bool inCombat)
    {
        wasBoundByDuty = boundByDuty;
        wasInCombat = inCombat;
    }

    public bool Observe(bool boundByDuty, bool inCombat)
    {
        // Both sides of the transition must be outside a duty. This prevents leaving a duty
        // or crossing a duty boundary from being mistaken for an open-world combat end.
        var shouldReset = !wasBoundByDuty &&
                          !boundByDuty &&
                          wasInCombat &&
                          !inCombat;
        wasBoundByDuty = boundByDuty;
        wasInCombat = inCombat;
        return shouldReset;
    }
}

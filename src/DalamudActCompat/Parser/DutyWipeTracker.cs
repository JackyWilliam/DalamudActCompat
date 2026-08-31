namespace DalamudActCompat.Parser;

internal sealed class DutyWipeTracker
{
    private bool wipePending;
    private bool wasPartyWiped;

    public bool Observe(bool trackDutyAttempt, bool inCombat, bool partyWiped)
    {
        if (!trackDutyAttempt)
        {
            Reset();
            return false;
        }

        if (partyWiped && !wasPartyWiped)
        {
            wipePending = true;
        }
        wasPartyWiped = partyWiped;

        // InCombat also falls during ordinary downtime and phase boundaries. Requiring a
        // latched all-party death prevents those transitions from splitting live totals.
        if (inCombat || !wipePending)
        {
            return false;
        }

        wipePending = false;
        return true;
    }

    public void Reset(bool partyWiped = false)
    {
        wipePending = false;
        wasPartyWiped = partyWiped;
    }
}

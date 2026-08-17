namespace DalamudActCompat.ActRuntime;

internal static class OpenWorldEncounterEndPolicy
{
    internal static readonly TimeSpan InactivityGrace = TimeSpan.FromSeconds(5);

    public static bool ShouldEnd(
        bool boundByDuty,
        bool localPlayerInCombat,
        DateTimeOffset lastRelevantCombatAction,
        DateTimeOffset now)
    {
        // A nearby party member can create the ACT encounter while the local combat flag
        // remains false. Activity, rather than that local-only flag, therefore owns the
        // outdoor encounter until the party has stopped producing damage for a full grace.
        return !boundByDuty &&
               !localPlayerInCombat &&
               lastRelevantCombatAction != default &&
               now - lastRelevantCombatAction >= InactivityGrace;
    }
}

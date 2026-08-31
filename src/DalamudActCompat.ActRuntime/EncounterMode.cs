namespace DalamudActCompat.ActRuntime;

public enum EncounterMode
{
    OpenWorld,
    DutyAttempt,
    LargeScaleFieldDuty,
}

public readonly record struct EncounterModeSnapshot(
    uint TerritoryId,
    uint MapId,
    EncounterMode Mode,
    bool InCombat,
    bool DutyPartyWiped,
    bool IsLoading);

public static class EncounterModePolicy
{
    public static EncounterMode Resolve(
        EncounterMode previousMode,
        bool isLoading,
        bool territoryKnown,
        bool explorationTerritory,
        bool boundByDuty,
        bool largeScaleDynamicEventInside,
        bool baldesionArsenalInside)
    {
        // Loading can temporarily clear the native event and map values. Keeping an already
        // confirmed large-scale duty avoids splitting one run during an internal floor change.
        if (isLoading && previousMode == EncounterMode.LargeScaleFieldDuty)
        {
            return EncounterMode.LargeScaleFieldDuty;
        }

        if (largeScaleDynamicEventInside || baldesionArsenalInside)
        {
            return EncounterMode.LargeScaleFieldDuty;
        }

        if (territoryKnown && explorationTerritory)
        {
            return EncounterMode.OpenWorld;
        }

        // Unknown territory metadata preserves the old BoundByDuty behavior instead of
        // silently changing encounter boundaries after a game-data update.
        return boundByDuty
            ? EncounterMode.DutyAttempt
            : EncounterMode.OpenWorld;
    }

    public static bool AccumulatesSegments(EncounterMode mode)
        => mode is EncounterMode.DutyAttempt or EncounterMode.LargeScaleFieldDuty;
}

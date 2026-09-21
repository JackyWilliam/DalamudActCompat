using Advanced_Combat_Tracker;

namespace DalamudActCompat.ActRuntime;

internal sealed class StatisticsEncounterSegments
{
    private EncounterData? source;
    private EncounterData? view;
    private DateTime cutoff;
    private bool sourceFinished;

    public void Reset() { source = view = null; sourceFinished = false; cutoff = default; }

    public void Cut(EncounterData encounter, DateTimeOffset now)
    {
        if (!ReferenceEquals(encounter, view)) source = encounter;
        view = null;
        sourceFinished = false;
        cutoff = now.LocalDateTime;
    }

    public MasterSwing? Map(MasterSwing action)
    {
        if (source is null || !ReferenceEquals(action.ParentEncounter, source)) return action;
        if (sourceFinished || action.Time <= cutoff) return null;
        // A meter reset must not call ACT.EndCombat: Cactbot and legacy plugins
        // observe that lifecycle. Aggregate copied swings into a private ACT view
        // so subsequent totals, hit maxima and DPS start fresh without those events.
        if (view is null)
        {
            view = new EncounterData(source!.CharName, source.ZoneName, source.Parent) { Active = true };
            view.StartTimes.Add(action.Time);
        }
        var copy = new MasterSwing(action.SwingType, action.Critical, action.Special, action.Damage,
            action.Time, action.TimeSorter, action.AttackType, action.Attacker, action.DamageType, action.Victim)
        { Tags = new Dictionary<string, object>(action.Tags) };
        view.AddCombatAction(copy);
        return copy;
    }

    public EncounterData? Finish(EncounterData encounter)
    {
        if (!ReferenceEquals(encounter, source)) return encounter;
        sourceFinished = true;
        view?.EndCombat(Finalize: true);
        return view;
    }
}

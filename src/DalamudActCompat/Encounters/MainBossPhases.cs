namespace DalamudActCompat.Encounters;

// The phase count for fights FflogsPhases has no table for: each main boss (the targetable enemy
// with the largest max HP) met in order is a phase. ProgressionTracker feeds it one sample at a
// time; it holds no game state so the rule can be tested on its own.
internal sealed class MainBossPhases
{
    // A new boss name must hold this long, and a phase must last this long, before it counts;
    // otherwise a big add or a boss going briefly untargetable would look like a phase change.
    private const double BossSettleSeconds = 2;
    private const double MinimumPhaseSeconds = 30;
    // An enemy that shared the arena with the phase's enemies for this long belongs to the phase.
    // Two bosses fought together take turns as the largest target whenever one cannot be
    // targeted, and each turn used to count as a new phase (players saw the number run ahead in
    // every fight with several targets). Shorter overlaps still change phase, so a boss that
    // lingers a moment while its successor appears does not swallow the next phase.
    private const double CompanionSeconds = 10;
    // Samples come every quarter second; after a hitch or a cutscene the gap must not be
    // credited as time spent together.
    private const double LongestSampleSeconds = 1;

    private readonly List<string> bosses = [];
    // BNpcName ids of the current phase's enemies, and how long the others have been beside them.
    private readonly HashSet<uint> members = [];
    private readonly Dictionary<uint, double> together = [];
    private DateTimeOffset lastPhaseAt;
    private DateTimeOffset lastSampleAt;
    private string? candidate;
    private DateTimeOffset candidateSince;

    // One name per phase, in order. A phase is listed under whichever of its enemies was the
    // main boss last, so the name matches the HP the pull is saved with.
    internal IReadOnlyList<string> Bosses => bosses;

    internal void Reset(DateTimeOffset now)
    {
        bosses.Clear();
        members.Clear();
        together.Clear();
        candidate = null;
        lastPhaseAt = now;
        lastSampleAt = now;
    }

    // One sample: the main boss, and the name ids of every targetable enemy (the main boss among
    // them). Returns whether the main boss belongs to the current phase, in which case its HP is
    // how far the phase has got.
    internal bool See(DateTimeOffset now, uint mainId, string mainName, HashSet<uint> present)
    {
        var elapsed = Math.Clamp((now - lastSampleAt).TotalSeconds, 0, LongestSampleSeconds);
        lastSampleAt = now;
        if (bosses.Count == 0)
        {
            Begin(now, mainId, mainName);
            return true;
        }

        // Time together only counts while one of the phase's own enemies is there to share it.
        if (members.Overlaps(present))
        {
            foreach (var id in present)
            {
                if (members.Contains(id)) continue;
                var seconds = together.GetValueOrDefault(id) + elapsed;
                if (seconds >= CompanionSeconds) members.Add(id);
                else together[id] = seconds;
            }
        }

        // The same boss under another name row is still the same boss.
        if (mainName == bosses[^1]) members.Add(mainId);
        if (members.Contains(mainId))
        {
            bosses[^1] = mainName;
            candidate = null;
            return true;
        }

        if (candidate != mainName)
        {
            candidate = mainName;
            candidateSince = now;
        }
        else if ((now - candidateSince).TotalSeconds >= BossSettleSeconds &&
                 (now - lastPhaseAt).TotalSeconds >= MinimumPhaseSeconds)
        {
            Begin(now, mainId, mainName);
            return true;
        }
        return false;
    }

    private void Begin(DateTimeOffset now, uint mainId, string mainName)
    {
        bosses.Add(mainName);
        members.Clear();
        members.Add(mainId);
        together.Clear();
        candidate = null;
        lastPhaseAt = now;
    }
}

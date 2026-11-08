namespace DalamudActCompat.Encounters;

// Phase numbers as FFLogs gives them, for the fights where counting "the main boss changed" (what
// ProgressionTracker does elsewhere) disagrees with it: an intermission's adds and a boss coming
// back for a later phase each looked like one more phase, so Futures Rewritten read up to P7.
// Here a phase starts only when that phase's own boss shows up.
internal static class FflogsPhases
{
    // TerritoryType → the BNpcName ids whose appearance starts P2, P3, … in order (P1 is the pull).
    private static readonly Dictionary<uint, uint[][]> Starts = new()
    {
        // Futures Rewritten (Ultimate): FFLogs splits a kill into P1 Fatebreaker … P5 Pandora.
        // The Ice Veil / crystals intermission stays in P2. P4 brings back Usurper of Frost
        // (12809) beside Oracle of Darkness (9832); 13700 is the pair's shared name.
        [1238] = [[12809], [9832], [12809, 13700], [13561]],
    };

    internal static bool Covers(uint territory) => Starts.ContainsKey(territory);

    // The phase to be in after seeing a boss for which `isBoss` holds, starting from `current`.
    // A boss of the current phase never moves it on, so Usurper of Frost during P2 stays P2 even
    // though it also starts P4; any other boss moves to the first later phase it starts.
    internal static int Next(uint territory, int current, Func<uint, bool> isBoss)
    {
        if (!Starts.TryGetValue(territory, out var starts)) return current;
        if (current >= 2 && current - 2 < starts.Length && starts[current - 2].Any(isBoss)) return current;
        for (var index = Math.Max(0, current - 1); index < starts.Length; index++)
            if (starts[index].Any(isBoss)) return index + 2;
        return current;
    }

    // Pulls recorded before this table only kept the main boss names in order. Replaying them
    // through the same rule, with the names this client gives those ids, recovers FFLogs' phase;
    // a pull whose names do not match (recorded in another client language) keeps its old count.
    internal static ProgressionPull Restore(ProgressionPull pull, Func<uint, string?> bossName)
    {
        if (pull.FflogsPhase > 0 || !Covers(pull.TerritoryId)) return pull;
        var phase = 1;
        var matched = false;
        foreach (var boss in pull.Bosses)
        {
            var next = Next(pull.TerritoryId, phase, id => string.Equals(bossName(id)?.Trim(), boss.Trim(), StringComparison.OrdinalIgnoreCase));
            if (next == phase) continue;
            phase = next;
            matched = true;
        }
        return matched || pull.Bosses.Count <= 1 ? pull with { FflogsPhase = phase } : pull;
    }
}

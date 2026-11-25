using DalamudActCompat.Core.Models;

namespace DalamudActCompat.Encounters;

// Which saved fights the 近期战斗 list shows. Saved with the configuration so the list reopens
// the way the player left it.
public enum HistoryFilter
{
    All,
    Duty,
    OpenWorld,
}

// One row of the 近期战斗 list. Entry is a saved record or, for a run of open-world fights, a
// folder that exists only for display; its SegmentRecords are the rows shown under it when open.
internal sealed record HistoryRow(Encounter Entry, bool OpenWorld)
{
    public bool IsOpenWorldRun => OpenWorld && Entry.SegmentRecords.Count > 0;
}

internal static class EncounterHistoryRules
{
    // Open-world fights in one map with no longer pause than this read as one outing: a FATE
    // chain, a hunt mark, a few goes at a striking dummy. A longer pause starts a new folder,
    // so yesterday's practice does not merge with today's.
    internal static readonly TimeSpan OpenWorldRunGap = TimeSpan.FromMinutes(30);

    // A duty (and a large-scale field duty) is always saved as one folder holding its pulls,
    // so a record saved without children is an open-world fight. This holds for top-level
    // history entries only: the pulls inside a folder have no children either.
    internal static bool IsOpenWorld(Encounter entry) => entry.SegmentRecords.Count == 0;

    // Duties and open-world fights each keep their own newest "limit" entries. With one shared
    // allowance an evening of FATEs or hunt marks pushed every raid record out of history.
    internal static IReadOnlyList<Encounter> Retain(IEnumerable<Encounter> newestFirst, int limit)
    {
        var kept = new List<Encounter>();
        var duties = 0;
        var openWorld = 0;
        foreach (var entry in newestFirst)
        {
            var count = IsOpenWorld(entry) ? ++openWorld : ++duties;
            if (count <= limit) kept.Add(entry);
        }

        return kept;
    }

    internal static IReadOnlyList<HistoryRow> BuildRows(IReadOnlyList<Encounter> newestFirst, HistoryFilter filter)
    {
        // Runs are formed on the whole list before filtering, so a duty between two fights
        // splits them in every view and a folder holds the same fights whichever filter is on.
        var rows = new List<HistoryRow>();
        var run = new List<Encounter>();
        foreach (var entry in newestFirst)
        {
            if (!IsOpenWorld(entry))
            {
                FlushRun(rows, run);
                rows.Add(new HistoryRow(entry, false));
                continue;
            }

            if (run.Count > 0 && !ContinuesRun(run[^1], entry)) FlushRun(rows, run);
            run.Add(entry);
        }

        FlushRun(rows, run);
        return filter switch
        {
            HistoryFilter.Duty => rows.Where(static row => !row.OpenWorld).ToArray(),
            HistoryFilter.OpenWorld => rows.Where(static row => row.OpenWorld).ToArray(),
            _ => rows,
        };
    }

    private static bool ContinuesRun(Encounter newer, Encounter older)
        => SameMap(newer, older) && newer.StartTime - (older.EndTime ?? older.StartTime) <= OpenWorldRunGap;

    private static bool SameMap(Encounter first, Encounter second)
        => first.TerritoryId is > 0 && second.TerritoryId is > 0
            ? first.TerritoryId == second.TerritoryId
            // Records saved before territory ids were kept only have the zone name to go by.
            : string.Equals(first.ZoneName, second.ZoneName, StringComparison.OrdinalIgnoreCase);

    private static void FlushRun(List<HistoryRow> rows, List<Encounter> run)
    {
        if (run.Count == 0) return;
        // A lone fight stays an ordinary row; a folder around one record would only add a click.
        rows.Add(new HistoryRow(run.Count == 1 ? run[0] : BuildRunFolder(run), true));
        run.Clear();
    }

    private static Encounter BuildRunFolder(List<Encounter> newestFirst)
    {
        // Oldest first, like the pulls of a duty folder, so "record 1" is the first fight.
        var fights = Enumerable.Reverse(newestFirst).ToArray();
        var zone = fights.Select(static fight => fight.ZoneName).LastOrDefault(static name => !string.IsNullOrWhiteSpace(name)) ?? string.Empty;
        return new Encounter(
            RunFolderId(fights[0].Id),
            fights[0].StartTime,
            fights.Max(static fight => fight.EndTime ?? fight.StartTime),
            zone,
            // The map names the folder, as the duty names a duty folder; each fight keeps its
            // own enemy name on its row.
            string.IsNullOrWhiteSpace(zone) ? fights[^1].EnemyName : zone,
            Array.Empty<Combatant>(),
            Array.Empty<DamageEvent>(),
            Array.Empty<HealEvent>(),
            Array.Empty<DeathEvent>(),
            Array.Empty<ActionSummary>(),
            Array.Empty<JobSummary>())
        {
            TerritoryId = fights.Select(static fight => fight.TerritoryId).LastOrDefault(static id => id is > 0),
            SegmentRecords = fights,
            CombatDuration = TimeSpan.FromTicks(fights.Sum(static fight => fight.EffectiveDuration.Ticks)),
        };
    }

    // The folder is never saved, so it needs an id of its own for the list. Deriving it from
    // the first fight keeps it the same from frame to frame, and flipping bits keeps it from
    // colliding with that fight's own row.
    private static Guid RunFolderId(Guid firstFight)
    {
        var bytes = firstFight.ToByteArray();
        for (var index = 0; index < bytes.Length; index++) bytes[index] ^= 0x5A;
        return new Guid(bytes);
    }
}

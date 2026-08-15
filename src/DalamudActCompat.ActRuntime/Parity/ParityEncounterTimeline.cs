namespace DalamudActCompat.ActRuntime.Parity;

internal static class ParityEncounterTimeline
{
    public static ParityDurationDiagnostic BuildDurations(
        DateTimeOffset? fightStart,
        DateTimeOffset? fightEnd,
        IReadOnlyList<ParityDamageLedgerEntry> included,
        IReadOnlyList<ParityReplayEvent> targetability,
        ParityActorRegistry actors)
    {
        DateTimeOffset? damageStart = included.Count == 0
            ? null
            : included.Min(static item => item.Timestamp);
        DateTimeOffset? damageEnd = included.Count == 0
            ? null
            : included.Max(static item => item.Timestamp);
        var wallSeconds = ResolveSeconds(damageStart, damageEnd);
        var targets = included
            .Where(static item =>
                !string.IsNullOrWhiteSpace(item.TargetId) ||
                !string.IsNullOrWhiteSpace(item.TargetName))
            // Friendly-fire damage belongs in the damage ledger, but a party member
            // must never become an encounter target for downtime intersection.
            .Where(item => !actors.IsPartyActor(item.TargetId, item.TargetName))
            .Select(static item => ParityActorRegistry.ActorKey(item.TargetId, item.TargetName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var perTarget = BuildTargetIntervals(targets, targetability, damageStart, damageEnd);
        var union = MergeIntervals(perTarget.SelectMany(static pair => pair.Value));
        var allUnavailable = IntersectAllTargets(perTarget, targets.Count);
        var unionSeconds = union.Sum(static item => (item.End - item.Start).TotalSeconds);
        var allUnavailableSeconds = allUnavailable.Sum(static item => (item.End - item.Start).TotalSeconds);
        return new ParityDurationDiagnostic(
            fightStart,
            fightEnd,
            ResolveSeconds(fightStart, fightEnd),
            damageStart,
            damageEnd,
            wallSeconds,
            unionSeconds,
            allUnavailableSeconds,
            Math.Max(0, wallSeconds - unionSeconds),
            Math.Max(0, wallSeconds - allUnavailableSeconds),
            "supplied encounter boundary; no idle timeout is inferred by the analyzer",
            "current DACT union downtime and diagnostic all-targets-unavailable candidate are both reported; neither changes the production metric path");
    }

    public static IReadOnlyList<ParityDowntimeInterval> BuildDowntimeDiagnostics(
        IReadOnlyList<ParityDamageLedgerEntry> included,
        IReadOnlyList<ParityReplayEvent> targetability,
        ParityDurationDiagnostic durations,
        ParityActorRegistry actors)
    {
        if (durations.DamageMetricStart is null || durations.DamageMetricEnd is null)
        {
            return [];
        }

        var targetNames = included
            // Keep ledger accounting and encounter-target classification separate:
            // FFLogs may retain friendly-fire events without treating players as bosses.
            .Where(item => !actors.IsPartyActor(item.TargetId, item.TargetName))
            .GroupBy(
                static item => ParityActorRegistry.ActorKey(item.TargetId, item.TargetName),
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => (group.Last().TargetId, group.Last().TargetName),
                StringComparer.OrdinalIgnoreCase);
        var intervals = BuildTargetIntervals(
            targetNames.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase),
            targetability,
            durations.DamageMetricStart,
            durations.DamageMetricEnd);
        var result = new List<ParityDowntimeInterval>();
        var ordinal = 0;
        foreach (var pair in intervals.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            var (targetId, targetName) = targetNames.GetValueOrDefault(pair.Key);
            foreach (var interval in pair.Value)
            {
                ordinal++;
                result.Add(new ParityDowntimeInterval(
                    interval.Start,
                    interval.End,
                    (interval.End - interval.Start).TotalSeconds,
                    targetId,
                    targetName,
                    $"observed-transition-{ordinal}",
                    "per-target NameToggle",
                    "Phase name is observational because FFLogs phase mapping is not public in the supplied data."));
            }
        }

        AddAggregate("current-union", MergeIntervals(intervals.SelectMany(static pair => pair.Value)));
        AddAggregate("all-targets-unavailable", IntersectAllTargets(intervals, intervals.Count));
        return result
            .OrderBy(static item => item.Start)
            .ThenBy(static item => item.Measurement, StringComparer.Ordinal)
            .ToArray();

        void AddAggregate(string measurement, IReadOnlyList<TimeInterval> aggregate)
        {
            var aggregateOrdinal = 0;
            foreach (var interval in aggregate)
            {
                aggregateOrdinal++;
                result.Add(new ParityDowntimeInterval(
                    interval.Start,
                    interval.End,
                    (interval.End - interval.Start).TotalSeconds,
                    string.Empty,
                    string.Join(", ", targetNames.Values
                        .Select(static value => value.TargetName)
                        .Distinct()),
                    $"{measurement}-{aggregateOrdinal}",
                    measurement,
                    measurement == "current-union"
                        ? "Mirrors RaidDpsEstimator: any observed damage target being untargetable removes time."
                        : "Diagnostic candidate: time is removed only while every observed damage target is unavailable."));
            }
        }
    }

    private static Dictionary<string, List<TimeInterval>> BuildTargetIntervals(
        IReadOnlySet<string> targetKeys,
        IReadOnlyList<ParityReplayEvent> targetability,
        DateTimeOffset? rangeStart,
        DateTimeOffset? rangeEnd)
    {
        var result = targetKeys.ToDictionary(
            static key => key,
            static _ => new List<TimeInterval>(),
            StringComparer.OrdinalIgnoreCase);
        if (rangeStart is null || rangeEnd is null || rangeEnd <= rangeStart)
        {
            return result;
        }

        foreach (var targetKey in targetKeys)
        {
            DateTimeOffset? unavailableStart = null;
            foreach (var item in targetability
                         .Where(item => string.Equals(
                             ParityActorRegistry.ActorKey(item.TargetId, item.TargetName),
                             targetKey,
                             StringComparison.OrdinalIgnoreCase))
                         .OrderBy(static item => item.Timestamp)
                         .ThenBy(static item => item.Sequence))
            {
                if (!item.Targetable)
                {
                    unavailableStart ??= item.Timestamp;
                }
                else if (unavailableStart is DateTimeOffset start)
                {
                    AddClamped(result[targetKey], start, item.Timestamp, rangeStart.Value, rangeEnd.Value);
                    unavailableStart = null;
                }
            }
            if (unavailableStart is DateTimeOffset openStart)
            {
                AddClamped(result[targetKey], openStart, rangeEnd.Value, rangeStart.Value, rangeEnd.Value);
            }
        }
        return result;
    }

    private static IReadOnlyList<TimeInterval> MergeIntervals(IEnumerable<TimeInterval> intervals)
    {
        var ordered = intervals.OrderBy(static item => item.Start).ThenBy(static item => item.End).ToArray();
        if (ordered.Length == 0)
        {
            return [];
        }
        var result = new List<TimeInterval> { ordered[0] };
        foreach (var item in ordered.Skip(1))
        {
            var previous = result[^1];
            if (item.Start <= previous.End)
            {
                result[^1] = previous with
                {
                    End = item.End > previous.End ? item.End : previous.End,
                };
            }
            else
            {
                result.Add(item);
            }
        }
        return result;
    }

    private static IReadOnlyList<TimeInterval> IntersectAllTargets(
        IReadOnlyDictionary<string, List<TimeInterval>> intervals,
        int targetCount)
    {
        if (targetCount == 0 || intervals.Count != targetCount)
        {
            return [];
        }
        var points = intervals.Values
            .SelectMany(static values => values.SelectMany(static value => new[]
            {
                new SweepPoint(value.Start, 1),
                new SweepPoint(value.End, -1),
            }))
            .GroupBy(static point => point.Time)
            .Select(static group => new SweepPoint(group.Key, group.Sum(static point => point.Delta)))
            .OrderBy(static point => point.Time)
            .ToArray();
        var result = new List<TimeInterval>();
        var unavailable = 0;
        DateTimeOffset? start = null;
        foreach (var point in points)
        {
            var wasAllUnavailable = unavailable == targetCount;
            unavailable += point.Delta;
            var isAllUnavailable = unavailable == targetCount;
            if (!wasAllUnavailable && isAllUnavailable)
            {
                start = point.Time;
            }
            else if (wasAllUnavailable && !isAllUnavailable && start is DateTimeOffset intervalStart)
            {
                result.Add(new TimeInterval(intervalStart, point.Time));
                start = null;
            }
        }
        return result;
    }

    private static void AddClamped(
        ICollection<TimeInterval> result,
        DateTimeOffset start,
        DateTimeOffset end,
        DateTimeOffset rangeStart,
        DateTimeOffset rangeEnd)
    {
        var clampedStart = start < rangeStart ? rangeStart : start;
        var clampedEnd = end > rangeEnd ? rangeEnd : end;
        if (clampedEnd > clampedStart)
        {
            result.Add(new TimeInterval(clampedStart, clampedEnd));
        }
    }

    private static double ResolveSeconds(DateTimeOffset? start, DateTimeOffset? end)
        => start is not null && end is not null && end > start
            ? (end.Value - start.Value).TotalSeconds
            : 0;

    private sealed record TimeInterval(DateTimeOffset Start, DateTimeOffset End);

    private sealed record SweepPoint(DateTimeOffset Time, int Delta);
}

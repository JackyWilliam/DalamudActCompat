using DalamudActCompat.Core.Models;

namespace DalamudActCompat.Parser;

internal sealed class DutyEncounterAccumulator
{
    private readonly Dictionary<string, CombatantTotals> completedCombatants =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Guid> completedSegmentIds = [];
    private readonly HashSet<Guid> segmentIds = [];
    private double completedDurationSeconds;
    private Guid sessionId;
    private DateTimeOffset? startTime;
    private uint? territoryId;
    private string zoneName = string.Empty;
    private Encounter? latestSegment;

    public bool HasData => latestSegment is not null || completedCombatants.Count > 0;

    public string ZoneName => zoneName;

    public IReadOnlyCollection<Guid> SegmentIds => segmentIds;

    public Encounter Update(Encounter segment, bool finished, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(segment);
        BeginOrUpdateSession(segment);
        latestSegment = segment;
        segmentIds.Add(segment.Id);
        if (finished && completedSegmentIds.Add(segment.Id))
        {
            var segmentDurationSeconds = ResolveSegmentDurationSeconds(segment, now);
            AddCombatants(segment.Combatants, segmentDurationSeconds);
            completedDurationSeconds += segmentDurationSeconds;
        }

        var activeSegment = !finished && !completedSegmentIds.Contains(segment.Id)
            ? segment
            : null;
        return Build(activeSegment, endTime: null, now);
    }

    public Encounter? Complete(DateTimeOffset endTime)
    {
        if (!HasData || startTime is null)
        {
            Reset();
            return null;
        }

        if (latestSegment is not null && completedSegmentIds.Add(latestSegment.Id))
        {
            var segmentDurationSeconds = ResolveSegmentDurationSeconds(latestSegment, endTime);
            AddCombatants(latestSegment.Combatants, segmentDurationSeconds);
            completedDurationSeconds += segmentDurationSeconds;
        }

        var completed = Build(activeSegment: null, endTime, endTime);
        Reset();
        return completed;
    }

    public void Reset()
    {
        completedCombatants.Clear();
        completedSegmentIds.Clear();
        segmentIds.Clear();
        completedDurationSeconds = 0;
        sessionId = Guid.Empty;
        startTime = null;
        territoryId = null;
        zoneName = string.Empty;
        latestSegment = null;
    }

    private void BeginOrUpdateSession(Encounter segment)
    {
        if (sessionId == Guid.Empty)
        {
            sessionId = Guid.NewGuid();
            startTime = segment.StartTime;
        }
        else if (segment.StartTime < startTime)
        {
            startTime = segment.StartTime;
        }

        if (!string.IsNullOrWhiteSpace(segment.ZoneName))
        {
            zoneName = segment.ZoneName;
        }
        if (segment.TerritoryId is > 0)
        {
            territoryId = segment.TerritoryId;
        }
    }

    private void AddCombatants(
        IEnumerable<Combatant> combatants,
        double encounterDurationSeconds)
    {
        foreach (var combatant in combatants)
        {
            var key = string.IsNullOrWhiteSpace(combatant.Id)
                ? combatant.Name
                : combatant.Id;
            if (!completedCombatants.TryGetValue(key, out var totals))
            {
                totals = new CombatantTotals();
                completedCombatants.Add(key, totals);
            }

            totals.Add(combatant, encounterDurationSeconds);
        }
    }

    private Encounter Build(
        Encounter? activeSegment,
        DateTimeOffset? endTime,
        DateTimeOffset now)
    {
        var activeDurationSeconds = activeSegment is null
            ? 0
            : ResolveSegmentDurationSeconds(activeSegment, now);
        var merged = completedCombatants.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Clone(),
            StringComparer.OrdinalIgnoreCase);
        if (activeSegment is not null)
        {
            foreach (var combatant in activeSegment.Combatants)
            {
                var key = string.IsNullOrWhiteSpace(combatant.Id)
                    ? combatant.Name
                    : combatant.Id;
                if (!merged.TryGetValue(key, out var totals))
                {
                    totals = new CombatantTotals();
                    merged.Add(key, totals);
                }

                totals.Add(combatant, activeDurationSeconds);
            }
        }

        var sessionStart = startTime ?? now;
        var durationSeconds = Math.Max(
            1,
            completedDurationSeconds + activeDurationSeconds);
        var combatants = merged.Values
            .Select(totals => totals.ToCombatant(durationSeconds))
            .ToArray();
        var jobs = combatants
            .Where(static combatant => !string.IsNullOrWhiteSpace(combatant.Job))
            .GroupBy(static combatant => combatant.Job, StringComparer.OrdinalIgnoreCase)
            .Select(static group => new JobSummary(
                group.Key,
                group.Sum(static combatant => combatant.TotalDamage),
                group.Sum(static combatant => combatant.TotalHealing),
                group.Count()))
            .ToArray();
        var displayName = string.IsNullOrWhiteSpace(zoneName)
            ? activeSegment?.EnemyName ?? "Duty"
            : zoneName;
        return new Encounter(
            sessionId,
            sessionStart,
            endTime,
            zoneName,
            displayName,
            combatants,
            Array.Empty<DamageEvent>(),
            Array.Empty<HealEvent>(),
            Array.Empty<DeathEvent>(),
            Array.Empty<ActionSummary>(),
            jobs)
        {
            // Duty totals remain on the meter, while FFLogs comparisons must use one
            // concrete boss segment instead of cumulative damage from the whole duty.
            FflogsRankingEncounter = activeSegment ?? latestSegment,
            TerritoryId = territoryId,
            // ACT treats merged encounters as the sum of their active encounter
            // durations. Travel, cutscenes, and waits between pulls must not lower DPS.
            CombatDuration = TimeSpan.FromSeconds(durationSeconds),
        };
    }

    private static double ResolveSegmentDurationSeconds(
        Encounter segment,
        DateTimeOffset fallbackEndTime)
        => segment.CombatDuration is { } combatDuration && combatDuration > TimeSpan.Zero
            ? Math.Max(1, combatDuration.TotalSeconds)
            : Math.Max(
                1,
                (segment.EndTime.GetValueOrDefault(fallbackEndTime) - segment.StartTime)
                .TotalSeconds);

    private sealed class CombatantTotals
    {
        private string id = string.Empty;
        private string name = string.Empty;
        private string job = string.Empty;
        private bool isLocalPlayer;
        private long totalDamage;
        private long totalHealing;
        private int deaths;
        private int damageHits;
        private int criticalHits;
        private int criticalDirectHits;
        private double? fflogsPercentile;
        private string? fflogsEncounterName;
        private double personalDamageDurationSeconds;
        private double externalDamageDurationSeconds;

        public void Add(Combatant combatant, double encounterDurationSeconds)
        {
            if (!string.IsNullOrWhiteSpace(combatant.Id))
            {
                id = combatant.Id;
            }
            if (!string.IsNullOrWhiteSpace(combatant.Name))
            {
                name = combatant.Name;
            }
            if (!string.IsNullOrWhiteSpace(combatant.Job))
            {
                job = combatant.Job;
            }
            isLocalPlayer |= combatant.IsLocalPlayer;
            totalDamage += combatant.TotalDamage;
            totalHealing += combatant.TotalHealing;
            deaths += combatant.Deaths;
            damageHits += combatant.DamageHits;
            criticalHits += combatant.CriticalHits;
            criticalDirectHits += combatant.CriticalDirectHits;
            if (combatant.FflogsPercentile is { } percentile &&
                double.IsFinite(percentile) &&
                percentile >= 0 &&
                percentile <= 100 &&
                !string.IsNullOrWhiteSpace(combatant.FflogsEncounterName))
            {
                fflogsPercentile = percentile;
                fflogsEncounterName = combatant.FflogsEncounterName;
            }
            personalDamageDurationSeconds += ResolveDamageDuration(
                combatant.TotalDamage,
                combatant.Dps,
                encounterDurationSeconds);
            externalDamageDurationSeconds += ResolveDamageDuration(
                combatant.TotalDamage,
                combatant.ExtDps,
                encounterDurationSeconds);
        }

        public CombatantTotals Clone()
            => new()
            {
                id = id,
                name = name,
                job = job,
                isLocalPlayer = isLocalPlayer,
                totalDamage = totalDamage,
                totalHealing = totalHealing,
                deaths = deaths,
                damageHits = damageHits,
                criticalHits = criticalHits,
                criticalDirectHits = criticalDirectHits,
                fflogsPercentile = fflogsPercentile,
                fflogsEncounterName = fflogsEncounterName,
                personalDamageDurationSeconds = personalDamageDurationSeconds,
                externalDamageDurationSeconds = externalDamageDurationSeconds,
            };

        public Combatant ToCombatant(double durationSeconds)
        {
            var encounterRate = totalDamage / durationSeconds;
            var personalRate = totalDamage / Math.Max(
                1,
                personalDamageDurationSeconds > 0
                    ? personalDamageDurationSeconds
                    : durationSeconds);
            var externalRate = totalDamage / Math.Max(
                1,
                externalDamageDurationSeconds > 0
                    ? externalDamageDurationSeconds
                    : durationSeconds);
            return new Combatant(
                id,
                name,
                job,
                isLocalPlayer,
                totalDamage,
                totalHealing,
                deaths,
                personalRate,
                encounterRate,
                externalRate,
                damageHits,
                criticalHits,
                criticalDirectHits,
                fflogsPercentile,
                fflogsEncounterName);
        }

        private static double ResolveDamageDuration(
            long damage,
            double rate,
            double encounterDurationSeconds)
            => damage > 0 && double.IsFinite(rate) && rate > 0
                ? damage / rate
                : damage > 0
                    ? encounterDurationSeconds
                    : 0;
    }
}

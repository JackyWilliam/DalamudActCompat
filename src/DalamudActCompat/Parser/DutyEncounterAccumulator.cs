using DalamudActCompat.ActRuntime;
using DalamudActCompat.Core.Models;

namespace DalamudActCompat.Parser;

internal sealed class DutyEncounterAccumulator
{
    private readonly Dictionary<string, CombatantTotals> completedCombatants =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Guid> completedSegmentIds = [];
    private readonly HashSet<Guid> segmentIds = [];
    private readonly List<string> displayRoster = [];
    private double completedDurationSeconds;
    private int partyCapacity;
    private Guid sessionId;
    private DateTimeOffset? startTime;
    private uint? territoryId;
    private string zoneName = string.Empty;
    private Encounter? latestSegment;

    public bool HasData => latestSegment is not null || completedCombatants.Count > 0;

    public string ZoneName => zoneName;

    public IReadOnlyCollection<Guid> SegmentIds => segmentIds;

    public Encounter Update(
        Encounter segment,
        bool finished,
        DateTimeOffset now,
        IReadOnlyList<string>? currentPartyMemberIds = null,
        int observedPartyCapacity = 0)
    {
        ArgumentNullException.ThrowIfNull(segment);
        BeginOrUpdateSession(segment);
        UpdateDisplayRoster(segment, currentPartyMemberIds, observedPartyCapacity);
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
        displayRoster.Clear();
        completedDurationSeconds = 0;
        partyCapacity = 0;
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
            var key = CombatantKey(combatant);
            if (!completedCombatants.TryGetValue(key, out var totals))
            {
                totals = new CombatantTotals();
                completedCombatants.Add(key, totals);
            }

            totals.Add(combatant, encounterDurationSeconds);
        }
    }

    private void UpdateDisplayRoster(
        Encounter segment,
        IReadOnlyList<string>? currentPartyMemberIds,
        int observedPartyCapacity)
    {
        if (currentPartyMemberIds is null)
        {
            return;
        }

        var currentRoster = currentPartyMemberIds
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        partyCapacity = Math.Max(
            partyCapacity,
            Math.Max(observedPartyCapacity, currentRoster.Length));
        if (currentRoster.Length == 0 || partyCapacity == 0)
        {
            return;
        }

        var nextRoster = new List<string>(partyCapacity);
        AddRosterMembers(nextRoster, currentRoster);
        if (nextRoster.Count < partyCapacity)
        {
            // During a vacancy the most recent roster fills the empty slot. Once a replacement
            // makes the live roster full, this branch is skipped and the departed member leaves
            // the duty-wide ranking without transferring their totals to another player.
            AddRosterMembers(nextRoster, displayRoster);
            AddRosterMembers(
                nextRoster,
                segment.Combatants
                    .Where(static combatant => !IsLimitBreak(combatant))
                    .Select(CombatantKey));
        }

        displayRoster.Clear();
        displayRoster.AddRange(nextRoster);
    }

    private void AddRosterMembers(List<string> target, IEnumerable<string> candidates)
    {
        foreach (var candidate in candidates)
        {
            if (target.Count == partyCapacity)
            {
                return;
            }
            if (string.IsNullOrWhiteSpace(candidate) ||
                target.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            target.Add(candidate);
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
                var key = CombatantKey(combatant);
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
        var visibleRoster = displayRoster.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var combatants = merged
            .Where(pair =>
                visibleRoster.Count == 0 ||
                visibleRoster.Contains(pair.Key) ||
                pair.Value.IsLimitBreak)
            .Select(pair => pair.Value.ToCombatant(durationSeconds))
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
            IsTransitioning = activeSegment?.IsTransitioning == true,
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

    private static string CombatantKey(Combatant combatant)
        => string.IsNullOrWhiteSpace(combatant.Id)
            ? combatant.Name
            : combatant.Id;

    private static bool IsLimitBreak(Combatant combatant)
        => string.Equals(
            combatant.Name,
            ChineseCombatChatContext.LimitBreakActorName,
            StringComparison.OrdinalIgnoreCase);

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
        private DateTimeOffset? fflogsDataUpdatedAt;
        private string? fflogsMetric;
        private bool fflogsDataStale;
        private double personalDamageDurationSeconds;
        private double externalDamageDurationSeconds;
        private double raidContributionDamage;

        public bool IsLimitBreak
            => string.Equals(
                name,
                ChineseCombatChatContext.LimitBreakActorName,
                StringComparison.OrdinalIgnoreCase);

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
                fflogsDataUpdatedAt = combatant.FflogsDataUpdatedAt;
                fflogsMetric = combatant.FflogsMetric;
                fflogsDataStale = combatant.FflogsDataStale;
            }
            personalDamageDurationSeconds += ResolveDamageDuration(
                combatant.TotalDamage,
                combatant.Dps,
                encounterDurationSeconds);
            externalDamageDurationSeconds += ResolveDamageDuration(
                combatant.TotalDamage,
                combatant.ExtDps,
                encounterDurationSeconds);
            raidContributionDamage += (combatant.Rdps > 0
                    ? combatant.Rdps
                    : combatant.EncDps > 0
                        ? combatant.EncDps
                        : combatant.TotalDamage / Math.Max(1, encounterDurationSeconds)) *
                encounterDurationSeconds;
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
                fflogsDataUpdatedAt = fflogsDataUpdatedAt,
                fflogsMetric = fflogsMetric,
                fflogsDataStale = fflogsDataStale,
                personalDamageDurationSeconds = personalDamageDurationSeconds,
                externalDamageDurationSeconds = externalDamageDurationSeconds,
                raidContributionDamage = raidContributionDamage,
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
                fflogsEncounterName,
                raidContributionDamage / durationSeconds,
                fflogsDataUpdatedAt,
                fflogsMetric,
                fflogsDataStale);
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

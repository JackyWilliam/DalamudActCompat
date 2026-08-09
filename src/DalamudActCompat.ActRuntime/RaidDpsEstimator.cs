using Advanced_Combat_Tracker;
using System.Globalization;

namespace DalamudActCompat.ActRuntime;

/// <summary>
/// Estimates FFLogs-style raid-contributing damage from ACT damage swings and
/// network status add/remove lines. The calculation follows FFLogs' published
/// percentage, critical-hit, and direct-hit attribution formulas. Player base
/// critical/direct chances are inferred conservatively from unbuffed hits.
/// </summary>
internal sealed class RaidDpsEstimator
{
    private const double DefaultCriticalChance = 0.25;
    private const double DefaultDirectHitChance = 0.25;
    private const double BaselinePriorHits = 40;
    private readonly object syncRoot = new();
    private readonly Dictionary<RaidStatusKey, RaidStatusInterval> activeStatuses = [];
    private readonly List<RaidStatusInterval> statusHistory = [];
    private readonly Dictionary<string, double> damageAdjustments =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HitBaseline> hitBaselines =
        new(StringComparer.OrdinalIgnoreCase);
    private bool encounterActive;
    private DateTimeOffset firstObservedDamage;
    private DateTimeOffset lastObservedDamage;

    private static readonly IReadOnlyDictionary<uint, RaidBuffDefinition> StatusesById =
        new Dictionary<uint, RaidBuffDefinition>
        {
            // Percentage damage buffs.
            [0x756] = RaidBuffDefinition.Damage(1.06), // Divination
            [0x4A1] = RaidBuffDefinition.Damage(1.05), // Brotherhood
            [0xA8F] = RaidBuffDefinition.Damage(1.05), // Searing Light
            [0xA27] = RaidBuffDefinition.Damage(1.03), // Arcane Circle
            [0x511] = RaidBuffDefinition.Damage(1.05), // Embolden (party)
            [0xE65] = RaidBuffDefinition.Damage(1.05), // Starry Muse
            [0x71E] = RaidBuffDefinition.Damage(1.05), // Technical Finish
            [0x839] = RaidBuffDefinition.Damage(1.05), // Standard Finish (partner)
            [0xF09] = RaidBuffDefinition.Damage(1.05), // Dokumori

            // Critical/direct-hit buffs.
            [0x312] = RaidBuffDefinition.Critical(0.10), // Battle Litany
            [0x4C5] = RaidBuffDefinition.Critical(0.10), // Chain Stratagem
            [0x721] = RaidBuffDefinition.CriticalDirect(0.20, 0.20), // Devilment
            [0x08D] = RaidBuffDefinition.Direct(0.20), // Battle Voice
        };

    private static readonly IReadOnlyDictionary<string, RaidBuffDefinition> StatusesByName =
        new Dictionary<string, RaidBuffDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["Divination"] = RaidBuffDefinition.Damage(1.06),
            ["占卜"] = RaidBuffDefinition.Damage(1.06),
            ["Brotherhood"] = RaidBuffDefinition.Damage(1.05),
            ["义结金兰：攻击"] = RaidBuffDefinition.Damage(1.05),
            ["Searing Light"] = RaidBuffDefinition.Damage(1.05),
            ["灼热之光"] = RaidBuffDefinition.Damage(1.05),
            ["Arcane Circle"] = RaidBuffDefinition.Damage(1.03),
            ["神秘环"] = RaidBuffDefinition.Damage(1.03),
            ["Embolden"] = RaidBuffDefinition.Damage(1.05),
            ["鼓励"] = RaidBuffDefinition.Damage(1.05),
            ["Starry Muse"] = RaidBuffDefinition.Damage(1.05),
            ["星空构想"] = RaidBuffDefinition.Damage(1.05),
            ["Technical Finish"] = RaidBuffDefinition.Damage(1.05),
            ["技巧舞步结束"] = RaidBuffDefinition.Damage(1.05),
            ["Standard Finish"] = RaidBuffDefinition.Damage(1.05),
            ["标准舞步结束"] = RaidBuffDefinition.Damage(1.05),
            ["Dokumori"] = RaidBuffDefinition.Damage(1.05),
            ["介毒之术"] = RaidBuffDefinition.Damage(1.05),
            ["Radiant Finale"] = RaidBuffDefinition.Damage(1.04),
            ["光明神的最终乐章"] = RaidBuffDefinition.Damage(1.04),
            ["Battle Litany"] = RaidBuffDefinition.Critical(0.10),
            ["战斗连祷"] = RaidBuffDefinition.Critical(0.10),
            ["Chain Stratagem"] = RaidBuffDefinition.Critical(0.10),
            ["连环计"] = RaidBuffDefinition.Critical(0.10),
            ["Devilment"] = RaidBuffDefinition.CriticalDirect(0.20, 0.20),
            ["进攻之探戈"] = RaidBuffDefinition.CriticalDirect(0.20, 0.20),
            ["Battle Voice"] = RaidBuffDefinition.Direct(0.20),
            ["战斗之声"] = RaidBuffDefinition.Direct(0.20),
        };

    public void StartEncounter(DateTimeOffset startTime)
    {
        lock (syncRoot)
        {
            PruneExpiredStatuses(startTime);
            statusHistory.Clear();
            statusHistory.AddRange(activeStatuses.Values.Where(status => status.EndTime > startTime));
            damageAdjustments.Clear();
            hitBaselines.Clear();
            firstObservedDamage = default;
            lastObservedDamage = default;
            encounterActive = true;
        }
    }

    public void FinishEncounter()
    {
        lock (syncRoot)
        {
            encounterActive = false;
        }
    }

    public void Reset()
    {
        lock (syncRoot)
        {
            encounterActive = false;
            activeStatuses.Clear();
            statusHistory.Clear();
            damageAdjustments.Clear();
            hitBaselines.Clear();
            firstObservedDamage = default;
            lastObservedDamage = default;
        }
    }

    public void ObserveStatusLine(DateTimeOffset timestamp, string rawLine)
    {
        if (string.IsNullOrWhiteSpace(rawLine) ||
            (!rawLine.StartsWith("26|", StringComparison.Ordinal) &&
             !rawLine.StartsWith("30|", StringComparison.Ordinal)))
        {
            return;
        }

        var fields = rawLine.Split('|');
        if (fields.Length < 9 ||
            !uint.TryParse(fields[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var statusId) ||
            !ResolveDefinition(statusId, fields[3], out var definition))
        {
            return;
        }

        var sourceId = fields.Length > 5 ? fields[5] : string.Empty;
        var sourceName = fields.Length > 6 ? fields[6] : string.Empty;
        var targetId = fields.Length > 7 ? fields[7] : string.Empty;
        var targetName = fields.Length > 8 ? fields[8] : string.Empty;
        if (string.IsNullOrWhiteSpace(sourceName) || string.IsNullOrWhiteSpace(targetName))
        {
            return;
        }

        var key = new RaidStatusKey(statusId, sourceId, targetId);
        lock (syncRoot)
        {
            PruneExpiredStatuses(timestamp);
            if (fields[0] == "30")
            {
                if (activeStatuses.Remove(key, out var removed))
                {
                    removed.EndTime = timestamp < removed.EndTime ? timestamp : removed.EndTime;
                }
                return;
            }

            if (!double.TryParse(
                    fields[4],
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var durationSeconds) ||
                !double.IsFinite(durationSeconds) ||
                durationSeconds <= 0)
            {
                return;
            }

            var endTime = timestamp.AddSeconds(Math.Min(durationSeconds, 600));
            if (activeStatuses.TryGetValue(key, out var existing))
            {
                if (endTime > existing.EndTime)
                {
                    existing.EndTime = endTime;
                }
                return;
            }

            var interval = new RaidStatusInterval(
                key,
                NormalizeActorName(sourceName),
                NormalizeActorName(targetName),
                timestamp,
                endTime,
                definition);
            activeStatuses[key] = interval;
            if (encounterActive)
            {
                statusHistory.Add(interval);
            }
        }
    }

    public void ObserveDamage(MasterSwing swing, string attackerName, string victimName)
    {
        ArgumentNullException.ThrowIfNull(swing);
        var directHit = swing.Tags.TryGetValue("DirectHit", out var directValue) &&
                        string.Equals(directValue?.ToString(), "True", StringComparison.OrdinalIgnoreCase);
        var isDot = swing.AttackType.Contains("DoT", StringComparison.OrdinalIgnoreCase) ||
                    swing.DamageType.Contains("DoT", StringComparison.OrdinalIgnoreCase) ||
                    (swing.Tags.TryGetValue("DoT", out var dotValue) &&
                     string.Equals(dotValue?.ToString(), "True", StringComparison.OrdinalIgnoreCase));
        ObserveDamage(
            swing.Time == default ? DateTimeOffset.Now : new DateTimeOffset(swing.Time),
            attackerName,
            victimName,
            swing.Damage.Number,
            swing.Critical,
            directHit,
            isDot);
    }

    public void ObserveDamage(
        DateTimeOffset timestamp,
        string attackerName,
        string victimName,
        long damage,
        bool critical,
        bool directHit,
        bool isDot = false)
    {
        if (damage <= 0 || string.IsNullOrWhiteSpace(attackerName))
        {
            return;
        }

        attackerName = NormalizeActorName(attackerName);
        victimName = NormalizeActorName(victimName);
        lock (syncRoot)
        {
            if (!encounterActive)
            {
                return;
            }

            if (firstObservedDamage == default || timestamp < firstObservedDamage)
            {
                firstObservedDamage = timestamp;
            }
            if (lastObservedDamage == default || timestamp > lastObservedDamage)
            {
                lastObservedDamage = timestamp;
            }

            var externalBuffs = statusHistory
                .Where(status => status.StartTime <= timestamp && timestamp < status.EndTime)
                .Where(status => SameActor(status.TargetName, attackerName) ||
                                 (!string.IsNullOrWhiteSpace(victimName) &&
                                  SameActor(status.TargetName, victimName)))
                .Where(status => !SameActor(status.SourceName, attackerName))
                .ToArray();

            var percentageBuffs = externalBuffs
                .Where(static status => status.Definition.DamageMultiplier > 1)
                .ToArray();
            var damageAfterPercentageRemoval = (double)damage;
            if (percentageBuffs.Length > 0)
            {
                var combinedMultiplier = percentageBuffs.Aggregate(
                    1.0,
                    static (current, status) => current * status.Definition.DamageMultiplier);
                if (combinedMultiplier > 1)
                {
                    damageAfterPercentageRemoval = damage / combinedMultiplier;
                    var lostDamage = damage - damageAfterPercentageRemoval;
                    var combinedLog = Math.Log(combinedMultiplier);
                    foreach (var status in percentageBuffs)
                    {
                        var contribution = lostDamage *
                                           Math.Log(status.Definition.DamageMultiplier) /
                                           combinedLog;
                        TransferContribution(attackerName, status.SourceName, contribution);
                    }
                }
            }

            var criticalBuffs = externalBuffs
                .Where(static status => status.Definition.CriticalChance > 0)
                .ToArray();
            var directBuffs = externalBuffs
                .Where(static status => status.Definition.DirectHitChance > 0)
                .ToArray();
            var baseline = hitBaselines.GetValueOrDefault(attackerName) ?? new HitBaseline();
            hitBaselines[attackerName] = baseline;
            var unbuffedCriticalChance = baseline.ResolveCriticalChance();
            var unbuffedDirectChance = baseline.ResolveDirectHitChance();

            if (!isDot && critical && criticalBuffs.Length > 0)
            {
                var criticalMultiplier = 1.35 + unbuffedCriticalChance;
                var combinedHitMultiplier = criticalMultiplier * (directHit ? 1.25 : 1);
                var criticalPortion = LogWeightedBonusPortion(
                    damageAfterPercentageRemoval,
                    criticalMultiplier,
                    combinedHitMultiplier);
                var buffedCriticalChance = Math.Clamp(
                    unbuffedCriticalChance + criticalBuffs.Sum(static status => status.Definition.CriticalChance),
                    0.01,
                    1);
                foreach (var status in criticalBuffs)
                {
                    TransferContribution(
                        attackerName,
                        status.SourceName,
                        criticalPortion * status.Definition.CriticalChance / buffedCriticalChance);
                }
            }

            if (!isDot && directHit && directBuffs.Length > 0)
            {
                var criticalMultiplier = critical ? 1.35 + unbuffedCriticalChance : 1;
                var combinedHitMultiplier = criticalMultiplier * 1.25;
                var directPortion = LogWeightedBonusPortion(
                    damageAfterPercentageRemoval,
                    1.25,
                    combinedHitMultiplier);
                var buffedDirectChance = Math.Clamp(
                    unbuffedDirectChance + directBuffs.Sum(static status => status.Definition.DirectHitChance),
                    0.01,
                    1);
                foreach (var status in directBuffs)
                {
                    TransferContribution(
                        attackerName,
                        status.SourceName,
                        directPortion * status.Definition.DirectHitChance / buffedDirectChance);
                }
            }

            if (!isDot && criticalBuffs.Length == 0 && directBuffs.Length == 0)
            {
                baseline.Observe(critical, directHit);
            }
        }
    }

    public double ResolveRate(
        string actorName,
        long rawDamage,
        double encounterDurationSeconds,
        bool useObservedDamageWindow = false)
    {
        if (rawDamage <= 0 || !double.IsFinite(encounterDurationSeconds) || encounterDurationSeconds <= 0)
        {
            return 0;
        }

        actorName = NormalizeActorName(actorName);
        lock (syncRoot)
        {
            var adjustedDamage = rawDamage + damageAdjustments.GetValueOrDefault(actorName);
            var durationSeconds = encounterDurationSeconds;
            if (useObservedDamageWindow &&
                firstObservedDamage != default &&
                lastObservedDamage > firstObservedDamage)
            {
                durationSeconds = (lastObservedDamage - firstObservedDamage).TotalSeconds;
            }
            return Math.Max(0, adjustedDamage) / durationSeconds;
        }
    }

    internal double ResolveObservedDamageDurationSeconds()
    {
        lock (syncRoot)
        {
            return firstObservedDamage != default && lastObservedDamage > firstObservedDamage
                ? (lastObservedDamage - firstObservedDamage).TotalSeconds
                : 0;
        }
    }

    internal double ResolveDamageAdjustment(string actorName)
    {
        lock (syncRoot)
        {
            return damageAdjustments.GetValueOrDefault(NormalizeActorName(actorName));
        }
    }

    private static bool ResolveDefinition(
        uint statusId,
        string statusName,
        out RaidBuffDefinition definition)
        => StatusesById.TryGetValue(statusId, out definition) ||
           StatusesByName.TryGetValue(statusName.Trim(), out definition);

    private void TransferContribution(string attackerName, string sourceName, double amount)
    {
        if (!double.IsFinite(amount) || amount <= 0 || SameActor(attackerName, sourceName))
        {
            return;
        }

        damageAdjustments[attackerName] = damageAdjustments.GetValueOrDefault(attackerName) - amount;
        damageAdjustments[sourceName] = damageAdjustments.GetValueOrDefault(sourceName) + amount;
    }

    private void PruneExpiredStatuses(DateTimeOffset timestamp)
    {
        foreach (var pair in activeStatuses
                     .Where(pair => pair.Value.EndTime <= timestamp)
                     .ToArray())
        {
            activeStatuses.Remove(pair.Key);
        }
    }

    private static double LogWeightedBonusPortion(
        double damage,
        double componentMultiplier,
        double combinedMultiplier)
    {
        if (damage <= 0 || componentMultiplier <= 1 || combinedMultiplier <= 1)
        {
            return 0;
        }

        var bonusDamage = damage - (damage / combinedMultiplier);
        return Math.Abs(componentMultiplier - combinedMultiplier) < 0.000001
            ? bonusDamage
            : bonusDamage * Math.Log(componentMultiplier) / Math.Log(combinedMultiplier);
    }

    private static string NormalizeActorName(string value)
    {
        value = value.Trim();
        var separator = value.IndexOf('@');
        return separator > 0 ? value[..separator].Trim() : value;
    }

    private static bool SameActor(string left, string right)
        => string.Equals(
            NormalizeActorName(left),
            NormalizeActorName(right),
            StringComparison.OrdinalIgnoreCase);

    private sealed class HitBaseline
    {
        private int hits;
        private int criticalHits;
        private int directHits;

        public void Observe(bool critical, bool direct)
        {
            hits++;
            if (critical)
            {
                criticalHits++;
            }
            if (direct)
            {
                directHits++;
            }
        }

        public double ResolveCriticalChance()
            => Math.Clamp(
                ((BaselinePriorHits * DefaultCriticalChance) + criticalHits) /
                (BaselinePriorHits + hits),
                0.05,
                0.50);

        public double ResolveDirectHitChance()
            => Math.Clamp(
                ((BaselinePriorHits * DefaultDirectHitChance) + directHits) /
                (BaselinePriorHits + hits),
                0.05,
                0.50);
    }

    private readonly record struct RaidStatusKey(uint StatusId, string SourceId, string TargetId);

    private sealed record RaidStatusInterval(
        RaidStatusKey Key,
        string SourceName,
        string TargetName,
        DateTimeOffset StartTime,
        DateTimeOffset InitialEndTime,
        RaidBuffDefinition Definition)
    {
        public DateTimeOffset EndTime { get; set; } = InitialEndTime;
    }

    private readonly record struct RaidBuffDefinition(
        double DamageMultiplier,
        double CriticalChance,
        double DirectHitChance)
    {
        public static RaidBuffDefinition Damage(double multiplier) => new(multiplier, 0, 0);

        public static RaidBuffDefinition Critical(double chance) => new(1, chance, 0);

        public static RaidBuffDefinition Direct(double chance) => new(1, 0, chance);

        public static RaidBuffDefinition CriticalDirect(double critical, double direct)
            => new(1, critical, direct);
    }
}

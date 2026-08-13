using Advanced_Combat_Tracker;
using System.Globalization;

namespace DalamudActCompat.ActRuntime;

/// <summary>
/// Estimates FFLogs-style raid-contributing damage from committed effective
/// damage and network status add/remove lines. The calculation follows FFLogs'
/// published percentage, critical-hit, and direct-hit attribution formulas.
/// Player base critical/direct chances are inferred conservatively from unbuffed hits.
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
    private readonly Dictionary<string, double> receivedBuffDamage =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> contributedBuffDamage =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HitBaseline> hitBaselines =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> actorNamesById =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> ownerIdsByActorId =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<PeriodicSnapshotKey, PeriodicBuffSnapshotInterval>
        activePeriodicSnapshots = [];
    private readonly List<PeriodicBuffSnapshotInterval> periodicSnapshotHistory = [];
    private bool encounterActive;

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
            [0xF2F] = RaidBuffDefinition.Damage(1.06), // The Balance
            [0xF31] = RaidBuffDefinition.Damage(1.06), // The Spear

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
            ["The Balance"] = RaidBuffDefinition.Damage(1.06),
            ["太阳神之衡"] = RaidBuffDefinition.Damage(1.06),
            ["The Spear"] = RaidBuffDefinition.Damage(1.06),
            ["战争神之枪"] = RaidBuffDefinition.Damage(1.06),
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
            receivedBuffDamage.Clear();
            contributedBuffDamage.Clear();
            hitBaselines.Clear();
            activePeriodicSnapshots.Clear();
            periodicSnapshotHistory.Clear();
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
            receivedBuffDamage.Clear();
            contributedBuffDamage.Clear();
            hitBaselines.Clear();
            actorNamesById.Clear();
            ownerIdsByActorId.Clear();
            activePeriodicSnapshots.Clear();
            periodicSnapshotHistory.Clear();
        }
    }

    public bool ObserveNetworkLine(DateTimeOffset timestamp, string rawLine)
    {
        if (string.IsNullOrWhiteSpace(rawLine))
        {
            return false;
        }

        if (rawLine.StartsWith("03|", StringComparison.Ordinal))
        {
            var fields = rawLine.Split('|');
            if (fields.Length >= 7)
            {
                lock (syncRoot)
                {
                    RememberActor(fields[2], fields[3]);
                    if (IsActorId(fields[6]))
                    {
                        ownerIdsByActorId[fields[2]] = fields[6];
                    }
                }
            }
            return false;
        }

        ObserveStatusLine(timestamp, rawLine);
        return false;
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
            !uint.TryParse(fields[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var statusId))
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
        var periodicKey = CreatePeriodicSnapshotKey(
            sourceId,
            sourceName,
            targetId,
            targetName,
            fields[3]);
        var isRaidBuff = ResolveDefinition(statusId, fields[3], out var definition);
        lock (syncRoot)
        {
            RememberActor(sourceId, sourceName);
            RememberActor(targetId, targetName);
            PruneExpiredStatuses(timestamp);
            PruneExpiredPeriodicSnapshots(timestamp);
            if (fields[0] == "30")
            {
                if (isRaidBuff && activeStatuses.Remove(key, out var removed))
                {
                    removed.EndTime = timestamp < removed.EndTime ? timestamp : removed.EndTime;
                }
                ClosePeriodicSnapshot(periodicKey, timestamp);
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
            if (!isRaidBuff)
            {
                if (encounterActive)
                {
                    // Unknown statuses are retained only until an effective periodic event
                    // proves the matching DoT or self-status ground effect needs the snapshot.
                    ClosePeriodicSnapshot(periodicKey, timestamp);
                    var damageActorName = ResolveOwnerName(sourceId, sourceName);
                    var percentageBuffs = ResolveExternalBuffs(
                            timestamp,
                            damageActorName,
                            sourceName,
                            string.Empty)
                        .Where(static status => status.Definition.DamageMultiplier > 1)
                        .Select(static status => new PercentageBuffSnapshot(
                            status.SourceName,
                            status.Definition.DamageMultiplier))
                        .ToArray();
                    var snapshot = new PeriodicBuffSnapshotInterval(
                        periodicKey,
                        timestamp,
                        endTime,
                        percentageBuffs);
                    activePeriodicSnapshots[periodicKey] = snapshot;
                    periodicSnapshotHistory.Add(snapshot);
                }
                return;
            }

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

    internal static bool IsDamageSwing(MasterSwing swing)
        => IsDamageSwingType(swing.SwingType);

    internal static bool IsDamageSwingType(int swingType)
        => swingType is 1 or 2;

    internal bool TryResolvePetOwner(string actorName, out string ownerName)
    {
        actorName = NormalizeActorName(actorName);
        lock (syncRoot)
        {
            var owners = ownerIdsByActorId
                .Where(pair =>
                    actorNamesById.TryGetValue(pair.Key, out var mappedName) &&
                    SameActor(mappedName, actorName) &&
                    actorNamesById.ContainsKey(pair.Value))
                .Select(pair => actorNamesById[pair.Value])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(2)
                .ToArray();
            if (owners.Length == 1)
            {
                ownerName = owners[0];
                return true;
            }
        }

        ownerName = string.Empty;
        return false;
    }

    public void ObserveDamage(
        DateTimeOffset timestamp,
        string attackerName,
        string victimName,
        long damage,
        bool critical,
        bool directHit,
        bool isDot = false,
        string? damageSourceName = null)
        => ObserveDamage(
            timestamp,
            attackerName,
            victimName,
            damage,
            critical,
            directHit,
            isDot,
            damageSourceName,
            periodicKey: null);

    internal void ObserveEffectiveDamage(EffectiveDamageEvent item, string ownerName)
    {
        ArgumentNullException.ThrowIfNull(item);
        ObserveDamage(
            item.Timestamp,
            ownerName,
            item.TargetName,
            item.Amount,
            item.Critical,
            item.DirectHit,
            item.IsPeriodic,
            item.SourceName,
            item.IsPeriodic
                ? CreatePeriodicSnapshotKey(
                    item.SourceId,
                    item.SourceName,
                    item.TargetId,
                    item.TargetName,
                    item.AbilityName)
                : null);
    }

    private void ObserveDamage(
        DateTimeOffset timestamp,
        string attackerName,
        string victimName,
        long damage,
        bool critical,
        bool directHit,
        bool isDot,
        string? damageSourceName,
        PeriodicSnapshotKey? periodicKey)
    {
        if (damage <= 0 || string.IsNullOrWhiteSpace(attackerName))
        {
            return;
        }

        attackerName = NormalizeActorName(attackerName);
        damageSourceName = NormalizeActorName(damageSourceName ?? attackerName);
        victimName = NormalizeActorName(victimName);
        lock (syncRoot)
        {
            if (!encounterActive)
            {
                return;
            }

            var externalBuffs = ResolveExternalBuffs(
                timestamp,
                attackerName,
                damageSourceName,
                victimName);
            IReadOnlyList<PercentageBuffSnapshot> percentageBuffs;
            if (isDot)
            {
                // A tick must never inherit a later raid-buff window. If no matching
                // application was observed, an empty snapshot is safer than tick-time attribution.
                percentageBuffs = periodicKey is { } key &&
                                  TryResolvePeriodicPercentageBuffs(key, timestamp, out var snapshotted)
                    ? snapshotted
                    : [];
            }
            else
            {
                percentageBuffs = externalBuffs
                    .Where(static status => status.Definition.DamageMultiplier > 1)
                    .Select(static status => new PercentageBuffSnapshot(
                        status.SourceName,
                        status.Definition.DamageMultiplier))
                    .ToArray();
            }
            var damageAfterPercentageRemoval = (double)damage;
            if (percentageBuffs.Count > 0)
            {
                var combinedMultiplier = percentageBuffs.Aggregate(
                    1.0,
                    static (current, status) => current * status.DamageMultiplier);
                if (combinedMultiplier > 1)
                {
                    damageAfterPercentageRemoval = damage / combinedMultiplier;
                    var lostDamage = damage - damageAfterPercentageRemoval;
                    var combinedLog = Math.Log(combinedMultiplier);
                    foreach (var status in percentageBuffs)
                    {
                        var contribution = lostDamage *
                                           Math.Log(status.DamageMultiplier) /
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

            if (isDot && (criticalBuffs.Length > 0 || directBuffs.Length > 0))
            {
                TransferDotCriticalDirectContribution(
                    attackerName,
                    damageAfterPercentageRemoval,
                    unbuffedCriticalChance,
                    unbuffedDirectChance,
                    criticalBuffs,
                    directBuffs);
            }
            else if (critical && criticalBuffs.Length > 0)
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
        double damageMetricDurationSeconds)
    {
        if (rawDamage <= 0 ||
            !double.IsFinite(damageMetricDurationSeconds) ||
            damageMetricDurationSeconds <= 0)
        {
            return 0;
        }

        actorName = NormalizeActorName(actorName);
        lock (syncRoot)
        {
            var adjustedDamage = rawDamage + damageAdjustments.GetValueOrDefault(actorName);
            // Damage and duration are authoritative inputs; this estimator owns attribution only.
            return Math.Max(0, adjustedDamage) / damageMetricDurationSeconds;
        }
    }

    internal double ResolveDamageAdjustment(string actorName)
    {
        lock (syncRoot)
        {
            return damageAdjustments.GetValueOrDefault(NormalizeActorName(actorName));
        }
    }

    internal double ResolveReceivedDamage(string actorName)
    {
        lock (syncRoot)
        {
            return receivedBuffDamage.GetValueOrDefault(NormalizeActorName(actorName));
        }
    }

    internal double ResolveContributedDamage(string actorName)
    {
        lock (syncRoot)
        {
            return contributedBuffDamage.GetValueOrDefault(NormalizeActorName(actorName));
        }
    }

    internal (double Received, double Contributed) ResolveAttributionTotals()
    {
        lock (syncRoot)
        {
            return (receivedBuffDamage.Values.Sum(), contributedBuffDamage.Values.Sum());
        }
    }

    private static bool ResolveDefinition(
        uint statusId,
        string statusName,
        out RaidBuffDefinition definition)
        => StatusesById.TryGetValue(statusId, out definition) ||
           StatusesByName.TryGetValue(statusName.Trim(), out definition);

    private RaidStatusInterval[] ResolveExternalBuffs(
        DateTimeOffset timestamp,
        string attackerName,
        string damageSourceName,
        string victimName)
        => statusHistory
            .Where(status => status.StartTime <= timestamp && timestamp < status.EndTime)
            .Where(status => SameActor(status.TargetName, attackerName) ||
                             SameActor(status.TargetName, damageSourceName) ||
                             (!string.IsNullOrWhiteSpace(victimName) &&
                              SameActor(status.TargetName, victimName)))
            .Where(status => !SameActor(status.SourceName, attackerName))
            .GroupBy(status => (
                status.Key.StatusId,
                SourceName: NormalizeActorName(status.SourceName)))
            .Select(static group => group.First())
            .ToArray();

    private string ResolveOwnerName(string sourceId, string sourceName)
    {
        if (ownerIdsByActorId.TryGetValue(sourceId, out var ownerId) &&
            actorNamesById.TryGetValue(ownerId, out var ownerName))
        {
            return ownerName;
        }
        return actorNamesById.GetValueOrDefault(sourceId, NormalizeActorName(sourceName));
    }

    private bool TryResolvePeriodicPercentageBuffs(
        PeriodicSnapshotKey key,
        DateTimeOffset timestamp,
        out IReadOnlyList<PercentageBuffSnapshot> buffs)
    {
        var snapshot = periodicSnapshotHistory.LastOrDefault(item =>
            (item.Key == key ||
             item.Key.SourceKey == key.SourceKey &&
             item.Key.TargetKey == item.Key.SourceKey &&
             item.Key.EffectName == key.EffectName) &&
            item.StartTime <= timestamp &&
            timestamp < item.EndTime);
        if (snapshot is not null)
        {
            buffs = snapshot.PercentageBuffs;
            return true;
        }
        buffs = [];
        return false;
    }

    private void ClosePeriodicSnapshot(PeriodicSnapshotKey key, DateTimeOffset timestamp)
    {
        if (activePeriodicSnapshots.Remove(key, out var snapshot))
        {
            snapshot.EndTime = timestamp < snapshot.EndTime ? timestamp : snapshot.EndTime;
        }
    }

    private void PruneExpiredPeriodicSnapshots(DateTimeOffset timestamp)
    {
        foreach (var pair in activePeriodicSnapshots
                     .Where(pair => pair.Value.EndTime <= timestamp)
                     .ToArray())
        {
            activePeriodicSnapshots.Remove(pair.Key);
        }
    }

    private static PeriodicSnapshotKey CreatePeriodicSnapshotKey(
        string sourceId,
        string sourceName,
        string targetId,
        string targetName,
        string effectName)
        => new(
            ActorSnapshotKey(sourceId, sourceName),
            ActorSnapshotKey(targetId, targetName),
            NormalizePeriodicEffectName(effectName));

    private static string ActorSnapshotKey(string actorId, string actorName)
        => IsActorId(actorId)
            ? actorId.Trim().ToUpperInvariant()
            : $"NAME:{NormalizeActorName(actorName).ToUpperInvariant()}";

    private static string NormalizePeriodicEffectName(string effectName)
    {
        effectName = effectName.Trim();
        foreach (var suffix in new[] { " (*)", " (DoT)" })
        {
            if (effectName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                effectName = effectName[..^suffix.Length].TrimEnd();
            }
        }
        return effectName.ToUpperInvariant();
    }

    private void TransferContribution(string attackerName, string sourceName, double amount)
    {
        if (!double.IsFinite(amount) || amount <= 0 || SameActor(attackerName, sourceName))
        {
            return;
        }

        damageAdjustments[attackerName] = damageAdjustments.GetValueOrDefault(attackerName) - amount;
        damageAdjustments[sourceName] = damageAdjustments.GetValueOrDefault(sourceName) + amount;
        receivedBuffDamage[attackerName] = receivedBuffDamage.GetValueOrDefault(attackerName) + amount;
        contributedBuffDamage[sourceName] = contributedBuffDamage.GetValueOrDefault(sourceName) + amount;
    }

    private void TransferDotCriticalDirectContribution(
        string attackerName,
        double damage,
        double unbuffedCriticalChance,
        double unbuffedDirectChance,
        IReadOnlyList<RaidStatusInterval> criticalBuffs,
        IReadOnlyList<RaidStatusInterval> directBuffs)
    {
        var buffedCriticalChance = Math.Clamp(
            unbuffedCriticalChance + criticalBuffs.Sum(static status => status.Definition.CriticalChance),
            0.01,
            1);
        var buffedDirectChance = Math.Clamp(
            unbuffedDirectChance + directBuffs.Sum(static status => status.Definition.DirectHitChance),
            0.01,
            1);
        var criticalMultiplier = 1.35 + unbuffedCriticalChance;
        const double directMultiplier = 1.25;
        var combinedMultiplier = criticalMultiplier * directMultiplier;
        var noCritical = 1 - buffedCriticalChance;
        var noDirect = 1 - buffedDirectChance;
        var totalMultiplier =
            (noCritical * noDirect) +
            (buffedCriticalChance * noDirect * criticalMultiplier) +
            (noCritical * buffedDirectChance * directMultiplier) +
            (buffedCriticalChance * buffedDirectChance * combinedMultiplier);
        if (totalMultiplier <= 0)
        {
            return;
        }

        var criticalPortion =
            ((buffedCriticalChance * noDirect * criticalMultiplier) +
             ((Math.Log(criticalMultiplier) / Math.Log(combinedMultiplier)) *
              buffedCriticalChance * buffedDirectChance * combinedMultiplier)) *
            damage / totalMultiplier;
        var directPortion =
            ((buffedDirectChance * noCritical * directMultiplier) +
             ((Math.Log(directMultiplier) / Math.Log(combinedMultiplier)) *
              buffedCriticalChance * buffedDirectChance * combinedMultiplier)) *
            damage / totalMultiplier;

        foreach (var status in criticalBuffs)
        {
            TransferContribution(
                attackerName,
                status.SourceName,
                criticalPortion * status.Definition.CriticalChance / buffedCriticalChance);
        }
        foreach (var status in directBuffs)
        {
            TransferContribution(
                attackerName,
                status.SourceName,
                directPortion * status.Definition.DirectHitChance / buffedDirectChance);
        }
    }

    private void RememberActor(string actorId, string actorName)
    {
        if (IsActorId(actorId) && !string.IsNullOrWhiteSpace(actorName))
        {
            actorNamesById[actorId] = NormalizeActorName(actorName);
        }
    }

    private static bool IsActorId(string value)
        => !string.IsNullOrWhiteSpace(value) &&
           !string.Equals(value, "0", StringComparison.OrdinalIgnoreCase) &&
           !string.Equals(value, "0000", StringComparison.OrdinalIgnoreCase) &&
           !string.Equals(value, "00000000", StringComparison.OrdinalIgnoreCase) &&
           !string.Equals(value, "E0000000", StringComparison.OrdinalIgnoreCase);

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

    private readonly record struct PeriodicSnapshotKey(
        string SourceKey,
        string TargetKey,
        string EffectName);

    private readonly record struct PercentageBuffSnapshot(
        string SourceName,
        double DamageMultiplier);

    private sealed record PeriodicBuffSnapshotInterval(
        PeriodicSnapshotKey Key,
        DateTimeOffset StartTime,
        DateTimeOffset InitialEndTime,
        IReadOnlyList<PercentageBuffSnapshot> PercentageBuffs)
    {
        public DateTimeOffset EndTime { get; set; } = InitialEndTime;
    }

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

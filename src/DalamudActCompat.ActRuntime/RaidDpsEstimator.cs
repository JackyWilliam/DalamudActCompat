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
    private static readonly TimeSpan PendingActionLifetime = TimeSpan.FromSeconds(2);
    private const uint LifeSurgeStatusId = 0x74;
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
    private readonly Dictionary<(string ActorName, AttributionKind Kind), double>
        receivedBuffDamageByKind = [];
    private readonly Dictionary<(string ActorName, AttributionKind Kind), double>
        contributedBuffDamageByKind = [];
    private readonly Dictionary<string, HitBaseline> hitBaselines =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> actorNamesById =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, uint> actorJobIdsById =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> ownerIdsByActorId =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<PeriodicSnapshotKey, PeriodicBuffSnapshotInterval>
        activePeriodicSnapshots = [];
    private readonly List<PeriodicBuffSnapshotInterval> periodicSnapshotHistory = [];
    private readonly Dictionary<DamageActionKey, Queue<GuaranteedHitDimensions>>
        pendingGuaranteedActions = [];
    private readonly Dictionary<string, TechnicalFinishApplication>
        technicalFinishApplicationsBySource = [];
    private readonly Dictionary<string, DateTimeOffset> reassembleByActorId =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ReassembleAction> consumedReassembleByActorId =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> lifeSurgeByActorId =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> pendingLifeSurgeRemovalByActorId =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ContextualGuaranteedAction> consumedLifeSurgeByActorId =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<uint, bool> isWeaponskillAction;
    private bool encounterActive;

    public RaidDpsEstimator(Func<uint, bool>? isWeaponskillAction = null)
    {
        // The network action-effect line has no action category. Fail closed when a host
        // cannot provide game metadata rather than promoting arbitrary same-timestamp Crits.
        this.isWeaponskillAction = isWeaponskillAction ?? (static _ => false);
    }

    [Flags]
    private enum GuaranteedHitDimensions
    {
        None = 0,
        Critical = 1,
        DirectHit = 2,
    }

    internal enum AttributionKind
    {
        Percentage,
        Critical,
        DirectHit,
    }

    private static readonly IReadOnlyDictionary<uint, GuaranteedHitDimensions>
        GuaranteedActionsById = new Dictionary<uint, GuaranteedHitDimensions>
        {
            // These action IDs are protocol-stable; localized names are deliberately not used.
            [0x1D3F] = GuaranteedHitDimensions.Critical, // Midare Setsugekka
            [0x4066] = GuaranteedHitDimensions.Critical, // Kaeshi: Setsugekka
            [0x64B5] = GuaranteedHitDimensions.Critical, // Ogi Namikiri
            [0x64B6] = GuaranteedHitDimensions.Critical, // Kaeshi: Namikiri
            [0x9066] = GuaranteedHitDimensions.Critical, // Tendo Setsugekka
            [0x9068] = GuaranteedHitDimensions.Critical, // Tendo Kaeshi Setsugekka
            [0x9076] = GuaranteedHitDimensions.Critical | GuaranteedHitDimensions.DirectHit, // Full Metal Field
            [0x64C0] = GuaranteedHitDimensions.Critical | GuaranteedHitDimensions.DirectHit, // Starfall Dance
        };

    private static readonly HashSet<uint> ReassembleWeaponskills =
    [
        // Reassemble is contextual, but the action category is represented by stable IDs too.
        0x4072, // Drill
        0x4073, // Bioblaster
        0x4074, // Air Anchor
        0x64BC, // Chain Saw
        0x9075, // Excavator
    ];

    private static readonly IReadOnlyDictionary<uint, double>
        TechnicalFinishMultipliersByActionId = new Dictionary<uint, double>
        {
            [0x81C1] = 1.03, // Three-step Technical Finish
            [0x81C2] = 1.05, // Four-step Technical Finish
        };

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
            [0x839] = RaidBuffDefinition.Damage(1.05), // Standard Finish (partner)
            [0xF09] = RaidBuffDefinition.Damage(1.05), // Dokumori
            [0xF2F] = RaidBuffDefinition.Damage(1.06), // The Balance
            [0xF31] = RaidBuffDefinition.Damage(1.06), // The Spear
            [0x8A9] = RaidBuffDefinition.Damage(1.01), // Mage's Ballad

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
            ["Standard Finish"] = RaidBuffDefinition.Damage(1.05),
            ["标准舞步结束"] = RaidBuffDefinition.Damage(1.05),
            ["Dokumori"] = RaidBuffDefinition.Damage(1.05),
            ["介毒之术"] = RaidBuffDefinition.Damage(1.05),
            ["The Balance"] = RaidBuffDefinition.Damage(1.06),
            ["太阳神之衡"] = RaidBuffDefinition.Damage(1.06),
            ["The Spear"] = RaidBuffDefinition.Damage(1.06),
            ["Mage's Ballad"] = RaidBuffDefinition.Damage(1.01),
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
            receivedBuffDamageByKind.Clear();
            contributedBuffDamageByKind.Clear();
            hitBaselines.Clear();
            activePeriodicSnapshots.Clear();
            periodicSnapshotHistory.Clear();
            PrunePendingGuaranteedActions(startTime);
            PruneTechnicalFinishApplications(startTime);
            PruneReassemble(startTime);
            PruneLifeSurge(startTime);
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
            receivedBuffDamageByKind.Clear();
            contributedBuffDamageByKind.Clear();
            hitBaselines.Clear();
            actorNamesById.Clear();
            actorJobIdsById.Clear();
            ownerIdsByActorId.Clear();
            activePeriodicSnapshots.Clear();
            periodicSnapshotHistory.Clear();
            pendingGuaranteedActions.Clear();
            technicalFinishApplicationsBySource.Clear();
            reassembleByActorId.Clear();
            consumedReassembleByActorId.Clear();
            lifeSurgeByActorId.Clear();
            pendingLifeSurgeRemovalByActorId.Clear();
            consumedLifeSurgeByActorId.Clear();
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
                    RememberActor(fields[2], fields[3], fields[4]);
                    if (IsActorId(fields[6]))
                    {
                        ownerIdsByActorId[fields[2]] = fields[6];
                    }
                }
            }
            return false;
        }

        if (rawLine.StartsWith("21|", StringComparison.Ordinal) ||
            rawLine.StartsWith("22|", StringComparison.Ordinal))
        {
            ObserveActionLine(timestamp, rawLine);
            return false;
        }

        if (rawLine.StartsWith("25|", StringComparison.Ordinal))
        {
            var fields = rawLine.Split('|');
            if (fields.Length >= 3)
            {
                lock (syncRoot)
                {
                    ClearLifeSurge(NormalizeActorId(fields[2]));
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
        lock (syncRoot)
        {
            RememberActor(sourceId, sourceName);
            RememberActor(targetId, targetName);
            PruneExpiredStatuses(timestamp);
            PruneExpiredPeriodicSnapshots(timestamp);
            PruneTechnicalFinishApplications(timestamp);
            PruneReassemble(timestamp);
            PruneLifeSurge(timestamp);
            if (fields[0] == "30")
            {
                if (statusId == 0x353)
                {
                    reassembleByActorId.Remove(NormalizeActorId(sourceId));
                }
                if (IsContextualGuaranteedCriticalStatus(statusId))
                {
                    ObserveLifeSurgeRemoval(timestamp, NormalizeActorId(targetId));
                }
                if (activeStatuses.Remove(key, out var removed))
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

            var isRaidBuff = ResolveDefinition(
                timestamp,
                statusId,
                fields[3],
                sourceId,
                sourceName,
                targetId,
                out var definition);
            if (!isRaidBuff && IsTechnicalFinishStatus(statusId, fields[3]))
            {
                // A carrier without its application action has no observable step count;
                // treating it as a fixed 5% window would recreate the attribution bug.
                return;
            }
            var endTime = timestamp.AddSeconds(Math.Min(durationSeconds, 600));
            if (statusId == 0x353)
            {
                reassembleByActorId[NormalizeActorId(sourceId)] = endTime;
            }
            if (IsContextualGuaranteedCriticalStatus(statusId))
            {
                var actorId = NormalizeActorId(targetId);
                if (!string.IsNullOrEmpty(actorId))
                {
                    lifeSurgeByActorId[actorId] = endTime;
                    pendingLifeSurgeRemovalByActorId.Remove(actorId);
                    consumedLifeSurgeByActorId.Remove(actorId);
                }
            }
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
                            targetName)
                        .Where(static status => status.Definition.DamageMultiplier > 1)
                        .Select(static status => new PercentageBuffSnapshot(
                            status.SourceName,
                            status.Definition.DamageMultiplier))
                        .ToArray();
                    var criticalDirectBuffs = ResolveExternalBuffs(
                            timestamp,
                            damageActorName,
                            sourceName,
                            targetName)
                        .Where(static status =>
                            status.Definition.CriticalChance > 0 ||
                            status.Definition.DirectHitChance > 0)
                        .Select(static status => new CriticalDirectBuffSnapshot(
                            status.SourceName,
                            status.Definition.CriticalChance,
                            status.Definition.DirectHitChance))
                        .ToArray();
                    var snapshot = new PeriodicBuffSnapshotInterval(
                        periodicKey,
                        timestamp,
                        endTime,
                        percentageBuffs,
                        criticalDirectBuffs);
                    activePeriodicSnapshots[periodicKey] = snapshot;
                    periodicSnapshotHistory.Add(snapshot);
                }
                return;
            }

            if (activeStatuses.TryGetValue(key, out var existing))
            {
                if (existing.Definition == definition)
                {
                    if (endTime > existing.EndTime)
                    {
                        existing.EndTime = endTime;
                    }
                    return;
                }

                existing.EndTime = timestamp < existing.EndTime ? timestamp : existing.EndTime;
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

    internal static bool IsContextualGuaranteedCriticalStatus(uint statusId)
        => statusId == LifeSurgeStatusId;

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
        GuaranteedHitDimensions guaranteedDimensions;
        lock (syncRoot)
        {
            guaranteedDimensions = item.IsPeriodic
                ? GuaranteedHitDimensions.None
                : ConsumeGuaranteedDimensions(item);
        }
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
                : null,
            guaranteedDimensions);
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
        PeriodicSnapshotKey? periodicKey,
        GuaranteedHitDimensions guaranteedDimensions = GuaranteedHitDimensions.None)
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
            IReadOnlyList<CriticalDirectBuffSnapshot> criticalDirectBuffs;
            if (isDot)
            {
                // An observed application is authoritative for every later tick. Percentage
                // attribution keeps its existing conservative empty fallback; Crit/DH can
                // distinguish unmatched ledger-normalized direct/auto events below.
                if (periodicKey is { } key &&
                    TryResolvePeriodicBuffSnapshot(key, timestamp, out var snapshot))
                {
                    percentageBuffs = snapshot.PercentageBuffs;
                    criticalDirectBuffs = snapshot.CriticalDirectBuffs;
                }
                else
                {
                    percentageBuffs = [];
                    // The ledger can conservatively label a source-normalized event periodic
                    // without a matching status application. Preserve direct/auto hit-time
                    // semantics in that case; a real observed DoT always has a snapshot,
                    // including an explicitly empty one.
                    criticalDirectBuffs = externalBuffs
                        .Where(static status =>
                            status.Definition.CriticalChance > 0 ||
                            status.Definition.DirectHitChance > 0)
                        .Select(static status => new CriticalDirectBuffSnapshot(
                            status.SourceName,
                            status.Definition.CriticalChance,
                            status.Definition.DirectHitChance))
                        .ToArray();
                }
            }
            else
            {
                percentageBuffs = externalBuffs
                    .Where(static status => status.Definition.DamageMultiplier > 1)
                    .Select(static status => new PercentageBuffSnapshot(
                        status.SourceName,
                        status.Definition.DamageMultiplier))
                    .ToArray();
                criticalDirectBuffs = externalBuffs
                    .Where(static status =>
                        status.Definition.CriticalChance > 0 ||
                        status.Definition.DirectHitChance > 0)
                    .Select(static status => new CriticalDirectBuffSnapshot(
                        status.SourceName,
                        status.Definition.CriticalChance,
                        status.Definition.DirectHitChance))
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
                        TransferContribution(
                            attackerName,
                            status.SourceName,
                            contribution,
                            AttributionKind.Percentage);
                    }
                }
            }

            var criticalBuffs = criticalDirectBuffs
                .Where(static status => status.CriticalChance > 0)
                .ToArray();
            var directBuffs = criticalDirectBuffs
                .Where(static status => status.DirectHitChance > 0)
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
            else
            {
                TransferGuaranteedCriticalDirectContribution(
                    attackerName,
                    damageAfterPercentageRemoval,
                    unbuffedCriticalChance,
                    guaranteedDimensions,
                    criticalBuffs,
                    directBuffs);

                if ((guaranteedDimensions & GuaranteedHitDimensions.Critical) == 0 &&
                    critical && criticalBuffs.Length > 0)
                {
                    var criticalMultiplier = 1.35 + unbuffedCriticalChance;
                    var combinedHitMultiplier = criticalMultiplier * (directHit ? 1.25 : 1);
                    var criticalPortion = LogWeightedBonusPortion(
                        damageAfterPercentageRemoval,
                        criticalMultiplier,
                        combinedHitMultiplier);
                    var buffedCriticalChance = Math.Clamp(
                        unbuffedCriticalChance + criticalBuffs.Sum(static status => status.CriticalChance),
                        0.01,
                        1);
                    foreach (var status in criticalBuffs)
                    {
                        TransferContribution(
                            attackerName,
                            status.SourceName,
                            criticalPortion * status.CriticalChance / buffedCriticalChance,
                            AttributionKind.Critical);
                    }
                }

                if ((guaranteedDimensions & GuaranteedHitDimensions.DirectHit) == 0 &&
                    directHit && directBuffs.Length > 0)
                {
                    var criticalMultiplier = critical ? 1.35 + unbuffedCriticalChance : 1;
                    var combinedHitMultiplier = criticalMultiplier * 1.25;
                    var directPortion = LogWeightedBonusPortion(
                        damageAfterPercentageRemoval,
                        1.25,
                        combinedHitMultiplier);
                    var buffedDirectChance = Math.Clamp(
                        unbuffedDirectChance + directBuffs.Sum(static status => status.DirectHitChance),
                        0.01,
                        1);
                    foreach (var status in directBuffs)
                    {
                        TransferContribution(
                            attackerName,
                            status.SourceName,
                            directPortion * status.DirectHitChance / buffedDirectChance,
                            AttributionKind.DirectHit);
                    }
                }
            }

            if (!isDot && criticalBuffs.Length == 0 && directBuffs.Length == 0)
            {
                baseline.Observe(
                    critical,
                    directHit,
                    (guaranteedDimensions & GuaranteedHitDimensions.Critical) == 0,
                    (guaranteedDimensions & GuaranteedHitDimensions.DirectHit) == 0);
            }
        }
    }

    private void ObserveActionLine(DateTimeOffset timestamp, string rawLine)
    {
        var fields = rawLine.TrimEnd('\r', '\n').Split('|');
        if (fields.Length < 24 ||
            !uint.TryParse(fields[4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var actionId))
        {
            return;
        }

        lock (syncRoot)
        {
            PrunePendingGuaranteedActions(timestamp);
            PruneTechnicalFinishApplications(timestamp);
            PruneReassemble(timestamp);
            PruneLifeSurge(timestamp);
            var sourceId = NormalizeActorId(fields[2]);
            if (TechnicalFinishMultipliersByActionId.TryGetValue(actionId, out var multiplier))
            {
                // Status 0x71E is shared by every finish rank; only the application action
                // carries the completed-step multiplier that each target window must retain.
                technicalFinishApplicationsBySource[ActorSnapshotKey(fields[2], fields[3])] =
                    new TechnicalFinishApplication(timestamp, multiplier);
            }
            var key = new DamageActionKey(
                timestamp,
                ActorSnapshotKey(fields[2], fields[3]),
                ActorSnapshotKey(fields[6], fields[7]),
                NormalizePeriodicEffectName(fields[5]));
            var damageEffectCount = 0;
            var hasCriticalDamage = false;
            for (var effectIndex = 8; effectIndex < 24; effectIndex += 2)
            {
                if (FfxivActionEffectDecoder.TryDecodeDamage(
                        fields[effectIndex],
                        fields[effectIndex + 1],
                        out var amount,
                        out var critical,
                        out _) && amount > 0)
                {
                    damageEffectCount++;
                    hasCriticalDamage |= critical;
                }
            }

            var dimensions = GuaranteedActionsById.GetValueOrDefault(actionId);
            if (dimensions == GuaranteedHitDimensions.None &&
                ReassembleWeaponskills.Contains(actionId))
            {
                if (reassembleByActorId.Remove(sourceId))
                {
                    dimensions = GuaranteedHitDimensions.Critical | GuaranteedHitDimensions.DirectHit;
                    consumedReassembleByActorId[sourceId] = new ReassembleAction(timestamp, actionId);
                }
                else if (consumedReassembleByActorId.TryGetValue(sourceId, out var consumed) &&
                         consumed.Timestamp == timestamp && consumed.ActionId == actionId)
                {
                    // A single AoE action has one raw line per target; every target shares
                    // the guarantee even though Reassemble itself is consumed only once.
                    dimensions = GuaranteedHitDimensions.Critical | GuaranteedHitDimensions.DirectHit;
                }
            }
            if (dimensions == GuaranteedHitDimensions.None)
            {
                dimensions = ResolveLifeSurgeDimensions(
                    timestamp,
                    sourceId,
                    actionId,
                    damageEffectCount,
                    hasCriticalDamage);
            }
            if (dimensions == GuaranteedHitDimensions.None)
            {
                return;
            }

            EnqueueGuaranteedDimensions(key, damageEffectCount, dimensions);
        }
    }

    private GuaranteedHitDimensions ResolveLifeSurgeDimensions(
        DateTimeOffset timestamp,
        string sourceId,
        uint actionId,
        int damageEffectCount,
        bool hasCriticalDamage)
    {
        if (string.IsNullOrEmpty(sourceId) ||
            damageEffectCount == 0 ||
            !hasCriticalDamage ||
            !isWeaponskillAction(actionId))
        {
            return GuaranteedHitDimensions.None;
        }

        if (consumedLifeSurgeByActorId.TryGetValue(sourceId, out var consumed) &&
            consumed.Timestamp == timestamp && consumed.ActionId == actionId)
        {
            // AoE targets arrive as separate action lines but share one Life Surge consume.
            return GuaranteedHitDimensions.Critical;
        }

        if (pendingLifeSurgeRemovalByActorId.TryGetValue(sourceId, out var removal) &&
            removal == timestamp)
        {
            pendingLifeSurgeRemovalByActorId.Remove(sourceId);
            consumedLifeSurgeByActorId[sourceId] = new ContextualGuaranteedAction(timestamp, actionId);
            return GuaranteedHitDimensions.Critical;
        }

        if (!lifeSurgeByActorId.Remove(sourceId, out var endTime) || endTime <= timestamp)
        {
            return GuaranteedHitDimensions.None;
        }
        consumedLifeSurgeByActorId[sourceId] = new ContextualGuaranteedAction(timestamp, actionId);
        return GuaranteedHitDimensions.Critical;
    }

    private void ObserveLifeSurgeRemoval(DateTimeOffset timestamp, string actorId)
    {
        if (string.IsNullOrEmpty(actorId) || !lifeSurgeByActorId.Remove(actorId))
        {
            return;
        }

        // FFLogs can order the remove before its packet-correlated damage action. Retain
        // only the exact timestamp; the weaponskill metadata gate still rejects autos/oGCDs.
        pendingLifeSurgeRemovalByActorId[actorId] = timestamp;
    }

    private void EnqueueGuaranteedDimensions(
        DamageActionKey key,
        int effectCount,
        GuaranteedHitDimensions dimensions)
    {
        if (effectCount <= 0 || dimensions == GuaranteedHitDimensions.None)
        {
            return;
        }
        if (!pendingGuaranteedActions.TryGetValue(key, out var queue))
        {
            queue = new Queue<GuaranteedHitDimensions>();
            pendingGuaranteedActions.Add(key, queue);
        }
        for (var index = 0; index < effectCount; index++)
        {
            queue.Enqueue(dimensions);
        }
    }

    private GuaranteedHitDimensions ConsumeGuaranteedDimensions(EffectiveDamageEvent item)
    {
        PrunePendingGuaranteedActions(item.Timestamp);
        var key = new DamageActionKey(
            item.Timestamp,
            ActorSnapshotKey(item.SourceId, item.SourceName),
            ActorSnapshotKey(item.TargetId, item.TargetName),
            NormalizePeriodicEffectName(item.AbilityName));
        if (!pendingGuaranteedActions.TryGetValue(key, out var queue) || queue.Count == 0)
        {
            return GuaranteedHitDimensions.None;
        }

        var dimensions = queue.Dequeue();
        if (queue.Count == 0)
        {
            pendingGuaranteedActions.Remove(key);
        }
        return dimensions;
    }

    private void PrunePendingGuaranteedActions(DateTimeOffset timestamp)
    {
        foreach (var key in pendingGuaranteedActions.Keys
                     .Where(key => timestamp - key.Timestamp > PendingActionLifetime)
                     .ToArray())
        {
            pendingGuaranteedActions.Remove(key);
        }
    }

    private void PruneTechnicalFinishApplications(DateTimeOffset timestamp)
    {
        foreach (var pair in technicalFinishApplicationsBySource
                     .Where(pair => timestamp - pair.Value.Timestamp > PendingActionLifetime)
                     .ToArray())
        {
            technicalFinishApplicationsBySource.Remove(pair.Key);
        }
    }

    private void PruneReassemble(DateTimeOffset timestamp)
    {
        foreach (var pair in reassembleByActorId.Where(pair => pair.Value <= timestamp).ToArray())
        {
            reassembleByActorId.Remove(pair.Key);
        }
        foreach (var pair in consumedReassembleByActorId
                     .Where(pair => timestamp - pair.Value.Timestamp > PendingActionLifetime)
                     .ToArray())
        {
            consumedReassembleByActorId.Remove(pair.Key);
        }
    }

    private void PruneLifeSurge(DateTimeOffset timestamp)
    {
        foreach (var pair in lifeSurgeByActorId.Where(pair => pair.Value <= timestamp).ToArray())
        {
            ClearLifeSurge(pair.Key);
        }
        foreach (var pair in pendingLifeSurgeRemovalByActorId
                     .Where(pair => timestamp - pair.Value > PendingActionLifetime)
                     .ToArray())
        {
            pendingLifeSurgeRemovalByActorId.Remove(pair.Key);
        }
        foreach (var pair in consumedLifeSurgeByActorId
                     .Where(pair => timestamp - pair.Value.Timestamp > PendingActionLifetime)
                     .ToArray())
        {
            consumedLifeSurgeByActorId.Remove(pair.Key);
        }
    }

    private void ClearLifeSurge(string actorId)
    {
        if (string.IsNullOrEmpty(actorId))
        {
            return;
        }
        lifeSurgeByActorId.Remove(actorId);
        pendingLifeSurgeRemovalByActorId.Remove(actorId);
        consumedLifeSurgeByActorId.Remove(actorId);
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

    internal double ResolveReceivedDamage(string actorName, AttributionKind kind)
    {
        lock (syncRoot)
        {
            return receivedBuffDamageByKind.GetValueOrDefault(AttributionKey(actorName, kind));
        }
    }

    internal double ResolveContributedDamage(string actorName, AttributionKind kind)
    {
        lock (syncRoot)
        {
            return contributedBuffDamageByKind.GetValueOrDefault(AttributionKey(actorName, kind));
        }
    }

    internal (double Received, double Contributed) ResolveAttributionTotals()
    {
        lock (syncRoot)
        {
            return (receivedBuffDamage.Values.Sum(), contributedBuffDamage.Values.Sum());
        }
    }

    internal (double Received, double Contributed) ResolveAttributionTotals(AttributionKind kind)
    {
        lock (syncRoot)
        {
            return (
                receivedBuffDamageByKind
                    .Where(pair => pair.Key.Kind == kind)
                    .Sum(static pair => pair.Value),
                contributedBuffDamageByKind
                    .Where(pair => pair.Key.Kind == kind)
                    .Sum(static pair => pair.Value));
        }
    }

    internal HitBaselineSnapshot ResolveHitBaseline(string actorName)
    {
        lock (syncRoot)
        {
            var baseline = hitBaselines.GetValueOrDefault(NormalizeActorName(actorName)) ?? new HitBaseline();
            return baseline.Snapshot();
        }
    }

    private bool ResolveDefinition(
        DateTimeOffset timestamp,
        uint statusId,
        string statusName,
        string sourceId,
        string sourceName,
        string targetId,
        out RaidBuffDefinition definition)
    {
        if (IsTechnicalFinishStatus(statusId, statusName))
        {
            if (technicalFinishApplicationsBySource.TryGetValue(
                    ActorSnapshotKey(sourceId, sourceName),
                    out var application) &&
                application.Timestamp <= timestamp &&
                timestamp - application.Timestamp <= PendingActionLifetime)
            {
                definition = RaidBuffDefinition.Damage(application.DamageMultiplier);
                return true;
            }

            definition = default;
            return false;
        }

        if (IsAstrologianCard(statusId, statusName))
        {
            var targetJobId = ResolveActorJobId(targetId);
            var isBalance = statusId == 0xF2F ||
                            string.Equals(statusName.Trim(), "The Balance", StringComparison.OrdinalIgnoreCase);
            var isFullStrength = isBalance
                ? IsMeleeOrTank(targetJobId)
                : IsRangedOrHealer(targetJobId);
            // AddCombatant normally provides the target job before status traffic. Retain
            // the historical 6% fallback when a third-party parser omits that metadata.
            definition = RaidBuffDefinition.Damage(targetJobId == 0 || isFullStrength ? 1.06 : 1.03);
            return true;
        }

        return StatusesById.TryGetValue(statusId, out definition) ||
               StatusesByName.TryGetValue(statusName.Trim(), out definition);
    }

    private static bool IsAstrologianCard(uint statusId, string statusName)
        => statusId is 0xF2F or 0xF31 ||
           string.Equals(statusName.Trim(), "The Balance", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(statusName.Trim(), "The Spear", StringComparison.OrdinalIgnoreCase);

    private uint ResolveActorJobId(string actorId)
    {
        var normalized = NormalizeActorId(actorId);
        if (actorJobIdsById.TryGetValue(normalized, out var jobId))
        {
            return jobId;
        }
        if (ownerIdsByActorId.TryGetValue(actorId, out var ownerId) ||
            ownerIdsByActorId.TryGetValue(normalized, out ownerId))
        {
            return actorJobIdsById.GetValueOrDefault(NormalizeActorId(ownerId));
        }
        return 0;
    }

    private static bool IsMeleeOrTank(uint jobId)
        // AddCombatant carries stable ClassJob row IDs, so role selection remains
        // language-independent and does not depend on display-name metadata.
        => jobId is 19 or 20 or 21 or 22 or 30 or 32 or 34 or 37 or 39 or 41;

    private static bool IsRangedOrHealer(uint jobId)
        => jobId is 23 or 24 or 25 or 27 or 28 or 31 or 33 or 35 or 38 or 40 or 42;

    private static bool IsTechnicalFinishStatus(uint statusId, string statusName)
        => statusId == 0x71E ||
           string.Equals(statusName.Trim(), "Technical Finish", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(statusName.Trim(), "技巧舞步结束", StringComparison.OrdinalIgnoreCase);

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

    private bool TryResolvePeriodicBuffSnapshot(
        PeriodicSnapshotKey key,
        DateTimeOffset timestamp,
        out PeriodicBuffSnapshotInterval snapshot)
    {
        var resolved = periodicSnapshotHistory.LastOrDefault(item =>
            (item.Key == key ||
             item.Key.SourceKey == key.SourceKey &&
             item.Key.TargetKey == item.Key.SourceKey &&
             item.Key.EffectName == key.EffectName) &&
            item.StartTime <= timestamp &&
            timestamp < item.EndTime);
        if (resolved is not null)
        {
            snapshot = resolved;
            return true;
        }
        snapshot = null!;
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

    private static string NormalizeActorId(string value)
        => uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var actorId) &&
           actorId != 0
            ? actorId.ToString("X8", CultureInfo.InvariantCulture)
            : string.Empty;

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

    private void TransferContribution(
        string attackerName,
        string sourceName,
        double amount,
        AttributionKind kind)
    {
        if (!double.IsFinite(amount) || amount <= 0 || SameActor(attackerName, sourceName))
        {
            return;
        }

        damageAdjustments[attackerName] = damageAdjustments.GetValueOrDefault(attackerName) - amount;
        damageAdjustments[sourceName] = damageAdjustments.GetValueOrDefault(sourceName) + amount;
        receivedBuffDamage[attackerName] = receivedBuffDamage.GetValueOrDefault(attackerName) + amount;
        contributedBuffDamage[sourceName] = contributedBuffDamage.GetValueOrDefault(sourceName) + amount;
        var receivedKey = AttributionKey(attackerName, kind);
        var contributedKey = AttributionKey(sourceName, kind);
        receivedBuffDamageByKind[receivedKey] =
            receivedBuffDamageByKind.GetValueOrDefault(receivedKey) + amount;
        contributedBuffDamageByKind[contributedKey] =
            contributedBuffDamageByKind.GetValueOrDefault(contributedKey) + amount;
    }

    private void TransferGuaranteedCriticalDirectContribution(
        string attackerName,
        double damage,
        double unbuffedCriticalChance,
        GuaranteedHitDimensions dimensions,
        IReadOnlyList<CriticalDirectBuffSnapshot> criticalBuffs,
        IReadOnlyList<CriticalDirectBuffSnapshot> directBuffs)
    {
        var criticalChanceIncrease =
            (dimensions & GuaranteedHitDimensions.Critical) != 0
                ? criticalBuffs.Sum(static status => status.CriticalChance)
                : 0;
        var directChanceIncrease =
            (dimensions & GuaranteedHitDimensions.DirectHit) != 0
                ? directBuffs.Sum(static status => status.DirectHitChance)
                : 0;
        var criticalMultiplier = 1.35 + unbuffedCriticalChance;
        var criticalRatio = criticalChanceIncrease > 0
            ? (criticalMultiplier + criticalChanceIncrease * (criticalMultiplier - 1)) /
              criticalMultiplier
            : 1;
        var directRatio = directChanceIncrease > 0
            ? (1.25 + directChanceIncrease * (1.25 - 1)) / 1.25
            : 1;
        var combinedRatio = criticalRatio * directRatio;
        if (combinedRatio <= 1)
        {
            return;
        }

        // Guaranteed hits still scale with rate buffs. Only their probability sample is
        // deterministic; attribution is the incremental multiplier embedded in the hit.
        if (criticalRatio > 1)
        {
            var portion = LogWeightedBonusPortion(damage, criticalRatio, combinedRatio);
            foreach (var status in criticalBuffs)
            {
                TransferContribution(
                    attackerName,
                    status.SourceName,
                    portion * status.CriticalChance / criticalChanceIncrease,
                    AttributionKind.Critical);
            }
        }
        if (directRatio > 1)
        {
            var portion = LogWeightedBonusPortion(damage, directRatio, combinedRatio);
            foreach (var status in directBuffs)
            {
                TransferContribution(
                    attackerName,
                    status.SourceName,
                    portion * status.DirectHitChance / directChanceIncrease,
                    AttributionKind.DirectHit);
            }
        }
    }

    private void TransferDotCriticalDirectContribution(
        string attackerName,
        double damage,
        double unbuffedCriticalChance,
        double unbuffedDirectChance,
        IReadOnlyList<CriticalDirectBuffSnapshot> criticalBuffs,
        IReadOnlyList<CriticalDirectBuffSnapshot> directBuffs)
    {
        var buffedCriticalChance = Math.Clamp(
            unbuffedCriticalChance + criticalBuffs.Sum(static status => status.CriticalChance),
            0.01,
            1);
        var buffedDirectChance = Math.Clamp(
            unbuffedDirectChance + directBuffs.Sum(static status => status.DirectHitChance),
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
                criticalPortion * status.CriticalChance / buffedCriticalChance,
                AttributionKind.Critical);
        }
        foreach (var status in directBuffs)
        {
            TransferContribution(
                attackerName,
                status.SourceName,
                directPortion * status.DirectHitChance / buffedDirectChance,
                AttributionKind.DirectHit);
        }
    }

    private void RememberActor(string actorId, string actorName, string jobId = "")
    {
        if (IsActorId(actorId) && !string.IsNullOrWhiteSpace(actorName))
        {
            actorNamesById[actorId] = NormalizeActorName(actorName);
            if (uint.TryParse(jobId, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsedJobId) &&
                parsedJobId > 0)
            {
                actorJobIdsById[NormalizeActorId(actorId)] = parsedJobId;
            }
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

    private static (string ActorName, AttributionKind Kind) AttributionKey(
        string actorName,
        AttributionKind kind)
        => (NormalizeActorName(actorName).ToUpperInvariant(), kind);

    private sealed class HitBaseline
    {
        private int criticalSamples;
        private int directSamples;
        private int criticalHits;
        private int directHits;

        public void Observe(
            bool critical,
            bool direct,
            bool observeCritical,
            bool observeDirect)
        {
            if (observeCritical)
            {
                criticalSamples++;
                if (critical)
                {
                    criticalHits++;
                }
            }
            if (observeDirect)
            {
                directSamples++;
                if (direct)
                {
                    directHits++;
                }
            }
        }

        public double ResolveCriticalChance()
            => Math.Clamp(
                ((BaselinePriorHits * DefaultCriticalChance) + criticalHits) /
                (BaselinePriorHits + criticalSamples),
                0.05,
                0.50);

        public double ResolveDirectHitChance()
            => Math.Clamp(
                ((BaselinePriorHits * DefaultDirectHitChance) + directHits) /
                (BaselinePriorHits + directSamples),
                0.05,
                0.50);

        public HitBaselineSnapshot Snapshot()
            => new(
                criticalSamples,
                criticalHits,
                ResolveCriticalChance(),
                directSamples,
                directHits,
                ResolveDirectHitChance());
    }

    private readonly record struct RaidStatusKey(uint StatusId, string SourceId, string TargetId);

    private readonly record struct PeriodicSnapshotKey(
        string SourceKey,
        string TargetKey,
        string EffectName);

    private readonly record struct DamageActionKey(
        DateTimeOffset Timestamp,
        string SourceKey,
        string TargetKey,
        string AbilityName);

    private readonly record struct ReassembleAction(DateTimeOffset Timestamp, uint ActionId);

    private readonly record struct ContextualGuaranteedAction(DateTimeOffset Timestamp, uint ActionId);

    private readonly record struct TechnicalFinishApplication(
        DateTimeOffset Timestamp,
        double DamageMultiplier);

    private readonly record struct PercentageBuffSnapshot(
        string SourceName,
        double DamageMultiplier);

    private readonly record struct CriticalDirectBuffSnapshot(
        string SourceName,
        double CriticalChance,
        double DirectHitChance);

    private sealed record PeriodicBuffSnapshotInterval(
        PeriodicSnapshotKey Key,
        DateTimeOffset StartTime,
        DateTimeOffset InitialEndTime,
        IReadOnlyList<PercentageBuffSnapshot> PercentageBuffs,
        IReadOnlyList<CriticalDirectBuffSnapshot> CriticalDirectBuffs)
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

internal readonly record struct HitBaselineSnapshot(
    int CriticalSamples,
    int CriticalHits,
    double CriticalChance,
    int DirectHitSamples,
    int DirectHits,
    double DirectHitChance);

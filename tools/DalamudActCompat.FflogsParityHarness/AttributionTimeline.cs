namespace DalamudActCompat.FflogsParityHarness;

internal enum AttributionTimelineSemantics
{
    CurrentProduction,
    PacketOrdered,
    ExplicitRemoval,
    ObservedEventState,
    CausalRemoveGrace,
    CausalCohortRemoveGrace,
}

internal enum AttributionStatusEndReason
{
    MissingRemoveNominalFallback,
    ExplicitRemove,
    RefreshOrOverwrite,
    RecipientDeath,
}

internal sealed record AttributionStatusLifecycle(
    long StatusId,
    string StatusName,
    int SourceActorId,
    int TargetActorId,
    double Start,
    long StartSequence,
    double NominalEnd,
    double NominalCausalEnd,
    long NominalCausalEndSequence,
    double OracleEnd,
    long OracleEndSequence,
    double CausalGraceEnd,
    long CausalGraceEndSequence,
    double CausalCohortEnd,
    long CausalCohortEndSequence,
    AttributionStatusEndReason EndReason);

internal sealed record MatrixBuffExposureEntry(
    OffensiveBuffDefinition Definition,
    int SourceActorId,
    string SourceActor,
    string SourceJob,
    int TargetActorId,
    bool IsSelfSourced,
    double DamageMultiplier,
    double LegacyDamageMultiplier,
    double WindowStart,
    double WindowEnd,
    long WindowStartSequence,
    long WindowEndSequence,
    double NominalWindowEnd,
    string StatusStateSource,
    AttributionStatusEndReason StatusEndReason);

internal sealed record MatrixEventAttributionState(
    IReadOnlyList<MatrixBuffExposureEntry> Buffs,
    double PercentageMultiplier,
    double ExternalCriticalRateIncrease,
    double ExternalDirectRateIncrease,
    double SelfCriticalRateIncrease,
    double SelfDirectRateIncrease,
    string AttributionTiming)
{
    public IReadOnlyList<MatrixBuffExposureEntry> RateBuffs => Buffs
        .Where(static item => item.Definition.Dimension != OffensiveBuffDimension.PercentageDamage)
        .ToArray();
}

internal sealed class AttributionTimeline
{
    // Production already uses two seconds as the bounded action/status correlation
    // lifetime. Reusing that protocol horizon tests a live-safe fallback without
    // fitting a new duration to FFLogs residuals.
    internal const double CausalRemoveGraceMilliseconds = 2_000;

    private static readonly IReadOnlySet<long> ReassembleWeaponskills = new HashSet<long>
    {
        16498, 16499, 16500, 25788, 36981,
    };

    private readonly NormalizedAttributionFight fight;
    private readonly IReadOnlyList<StatusInterval> statuses;
    private readonly IReadOnlyDictionary<ActionKey, ProbeGuaranteedDimensions> lifeSurgeActions;
    private readonly IReadOnlyDictionary<ActionKey, ProbeGuaranteedDimensions> reassembleActions;

    public AttributionTimeline(NormalizedAttributionFight fight)
    {
        this.fight = fight;
        statuses = BuildStatusIntervals(fight);
        lifeSurgeActions = ResolveLifeSurgeActions(fight, statuses);
        reassembleActions = ResolveReassembleActions(fight, statuses);
    }

    public IReadOnlySet<uint> LifeSurgeWeaponskillActionIds => lifeSurgeActions.Keys
        .Select(static key => checked((uint)key.AbilityId))
        .ToHashSet();

    public IReadOnlyList<AttributionStatusLifecycle> StatusLifecycles => statuses
        .Select(static status => new AttributionStatusLifecycle(
            status.AbilityId,
            status.AbilityName,
            status.SourceId,
            status.TargetId,
            status.Start,
            status.StartSequence,
            status.NominalEnd,
            status.NominalCausalEnd,
            status.NominalCausalEndSequence,
            status.OracleEnd,
            status.OracleEndSequence,
            status.CausalGraceEnd,
            status.CausalGraceEndSequence,
            status.CausalCohortEnd,
            status.CausalCohortEndSequence,
            status.EndReason))
        .ToArray();

    public MatrixEventAttributionState Resolve(
        NormalizedFflogsEvent item,
        FflogsActor owner)
        => Resolve(item, owner, AttributionTimelineSemantics.CurrentProduction);

    public MatrixEventAttributionState Resolve(
        NormalizedFflogsEvent item,
        FflogsActor owner,
        AttributionTimelineSemantics semantics)
        => Resolve(item, owner, semantics, snapshotPeriodic: true);

    public MatrixEventAttributionState ResolveAtHitTime(
        NormalizedFflogsEvent item,
        FflogsActor owner,
        AttributionTimelineSemantics semantics)
        => Resolve(item, owner, semantics, snapshotPeriodic: false);

    private MatrixEventAttributionState Resolve(
        NormalizedFflogsEvent item,
        FflogsActor owner,
        AttributionTimelineSemantics semantics,
        bool snapshotPeriodic)
    {
        var attributionTimestamp = item.Timestamp;
        var attributionSequence = item.AttributionSequence;
        var timing = "hit_time";
        if (snapshotPeriodic && item.IsPeriodic && TryResolvePeriodicApplication(item, out var application))
        {
            attributionTimestamp = application.Start;
            attributionSequence = application.StartSequence;
            timing = "dot_snapshot";
        }

        var buffs = ResolveApplicableStatuses(
                attributionTimestamp,
                attributionSequence,
                owner.Id,
                item.SourceId,
                item.TargetId,
                semantics)
            .Where(status => OffensiveBuffRegistry.ByStatusId.ContainsKey(status.AbilityId))
            .Where(status =>
                !OffensiveBuffRegistry.PercentageDenominatorOnlyStatusIds.Contains(status.AbilityId) ||
                status.SourceId == owner.Id)
            .Where(status => OffensiveBuffRegistry.ByStatusId[status.AbilityId].AffectsAutoAttacks ||
                             item.AbilityId != 7)
            .GroupBy(static status => (status.AbilityId, status.SourceId))
            .Select(group =>
            {
                var status = group.First();
                var definition = OffensiveBuffRegistry.ByStatusId[status.AbilityId];
                var source = fight.Actors.GetValueOrDefault(status.SourceId);
                return new MatrixBuffExposureEntry(
                    definition,
                    status.SourceId,
                    source?.Name ?? $"Actor {status.SourceId}",
                    source?.Job ?? definition.ProviderJob,
                    status.TargetId,
                    status.SourceId == owner.Id,
                    ResolvePercentageMultiplier(status, definition, owner.Job),
                    ResolveLegacyPercentageMultiplier(status, definition),
                    status.Start,
                    ResolveEnd(status, semantics),
                    status.StartSequence,
                    ResolveEndSequence(status, semantics),
                    status.NominalEnd,
                    semantics switch
                    {
                        AttributionTimelineSemantics.ObservedEventState or
                            AttributionTimelineSemantics.ExplicitRemoval => "oracle_explicit_packet",
                        AttributionTimelineSemantics.CausalRemoveGrace => "causal_individual_2s_fallback",
                        AttributionTimelineSemantics.CausalCohortRemoveGrace => "causal_cohort_evidence_2s_fallback",
                        _ => "packet_nominal",
                    },
                    status.EndReason);
            })
            .ToArray();
        var external = buffs.Where(static buff => !buff.IsSelfSourced).ToArray();
        var self = buffs.Where(static buff => buff.IsSelfSourced).ToArray();
        var percentageMultiplier = buffs
            .Where(static buff => buff.Definition.Dimension == OffensiveBuffDimension.PercentageDamage)
            .Aggregate(1d, static (current, buff) => current * buff.DamageMultiplier);
        return new MatrixEventAttributionState(
            buffs,
            percentageMultiplier,
            external.Sum(static buff => buff.Definition.CriticalRateIncrease),
            external.Sum(static buff => buff.Definition.DirectHitRateIncrease),
            self.Sum(static buff => buff.Definition.CriticalRateIncrease),
            self.Sum(static buff => buff.Definition.DirectHitRateIncrease),
            timing);
    }

    public (ProbeGuaranteedDimensions Dimensions, string Source) ResolveGuaranteed(
        NormalizedFflogsEvent item,
        FflogsActor owner)
    {
        if (GuaranteedHitRegistry.StableByActionId.TryGetValue(item.AbilityId, out var stable) &&
            string.Equals(stable.Job, ToJobAbbreviation(owner.Job), StringComparison.OrdinalIgnoreCase))
        {
            return (stable.Dimensions, "authoritative stable action registry");
        }
        if (reassembleActions.TryGetValue(ActionKey.From(item), out var reassemble))
        {
            return (reassemble, "Reassemble contextual status");
        }
        if (lifeSurgeActions.TryGetValue(ActionKey.From(item), out var lifeSurge))
        {
            return (lifeSurge, "Life Surge contextual status");
        }
        if (item.AbilityId is 3549 or 3550 &&
            string.Equals(ToJobAbbreviation(owner.Job), "WAR", StringComparison.OrdinalIgnoreCase) &&
            ResolveApplicableStatuses(item.Timestamp, owner.Id, item.SourceId, item.TargetId)
                .Any(status => status.AbilityId == 1177 && status.SourceId == owner.Id))
        {
            return (
                ProbeGuaranteedDimensions.Critical | ProbeGuaranteedDimensions.DirectHit,
                "Inner Release contextual status");
        }
        return (ProbeGuaranteedDimensions.None, "normal or unresolved contextual action");
    }

    private double ResolvePercentageMultiplier(
        StatusInterval status,
        OffensiveBuffDefinition definition,
        string recipientJob)
    {
        if (definition.StatusId != 1822)
        {
            return OffensiveBuffRegistry.ResolveDamageMultiplier(definition, recipientJob);
        }
        return ResolveTechnicalFinishMultiplier(status);
    }

    private double ResolveLegacyPercentageMultiplier(
        StatusInterval status,
        OffensiveBuffDefinition definition)
        => definition.StatusId == 1822
            ? ResolveTechnicalFinishMultiplier(status)
            : definition.DamageMultiplier ?? 1;

    private double ResolveTechnicalFinishMultiplier(StatusInterval status)
    {
        var finish = fight.Events
            .Where(item => item.SourceId == status.SourceId &&
                           item.AbilityId is 16193 or 16194 or 16195 or 16196 or
                               33216 or 33217 or 33218)
            .OrderBy(item => Math.Abs(item.Timestamp - status.Start))
            .FirstOrDefault();
        return finish?.AbilityId switch
        {
            16193 => 1.01,
            16194 or 33216 => 1.02,
            16195 or 33217 => 1.03,
            16196 or 33218 => 1.05,
            // A missing nearby rank is retained as an explicit maximum-rank
            // assumption and surfaced by the control residual, never back-fitted.
            _ => 1.05,
        };
    }

    private IReadOnlyList<StatusInterval> ResolveApplicableStatuses(
        double timestamp,
        long attributionSequence,
        int ownerId,
        int sourceId,
        int targetId,
        AttributionTimelineSemantics semantics)
        => statuses
            .Where(status => IsActive(status, timestamp, attributionSequence, semantics))
            .Where(status => status.TargetId == ownerId ||
                             status.TargetId == sourceId ||
                             status.TargetId == targetId)
            .ToArray();

    private static bool IsActive(
        StatusInterval status,
        double timestamp,
        long attributionSequence,
        AttributionTimelineSemantics semantics)
    {
        var sequenceAware = semantics is not AttributionTimelineSemantics.ExplicitRemoval;
        var end = ResolveEnd(status, semantics);
        if (!sequenceAware)
        {
            return status.Start <= timestamp && timestamp < end;
        }

        // FFLogs calculateddamage can share a timestamp with apply/remove events.
        // Preserving packet order avoids treating a pre-application action as buffed.
        var afterStart = timestamp > status.Start ||
                         timestamp == status.Start && attributionSequence >= status.StartSequence;
        var endSequence = ResolveEndSequence(status, semantics);
        var beforeEnd = timestamp < end ||
                        timestamp == end && attributionSequence < endSequence;
        return afterStart && beforeEnd;
    }

    private static double ResolveEnd(
        StatusInterval status,
        AttributionTimelineSemantics semantics)
        => semantics switch
        {
            AttributionTimelineSemantics.ExplicitRemoval or
                AttributionTimelineSemantics.ObservedEventState => status.OracleEnd,
            AttributionTimelineSemantics.CausalRemoveGrace => status.CausalGraceEnd,
            AttributionTimelineSemantics.CausalCohortRemoveGrace => status.CausalCohortEnd,
            _ => status.ProductionEnd,
        };

    private static long ResolveEndSequence(
        StatusInterval status,
        AttributionTimelineSemantics semantics)
        => semantics switch
        {
            AttributionTimelineSemantics.ExplicitRemoval or
                AttributionTimelineSemantics.ObservedEventState => status.OracleEndSequence,
            AttributionTimelineSemantics.CausalRemoveGrace => status.CausalGraceEndSequence,
            AttributionTimelineSemantics.CausalCohortRemoveGrace => status.CausalCohortEndSequence,
            _ => long.MinValue,
        };

    private IReadOnlyList<StatusInterval> ResolveApplicableStatuses(
        double timestamp,
        int ownerId,
        int sourceId,
        int targetId)
        => ResolveApplicableStatuses(
            timestamp,
            long.MaxValue,
            ownerId,
            sourceId,
            targetId,
            AttributionTimelineSemantics.CurrentProduction);

    private bool TryResolvePeriodicApplication(
        NormalizedFflogsEvent item,
        out StatusInterval application)
    {
        var effectName = NormalizeEffectName(item.AbilityName);
        var resolved = statuses
            .Where(status => status.SourceId == item.SourceId && status.TargetId == item.TargetId)
            .Where(status => NormalizeEffectName(status.AbilityName) == effectName)
            .Where(status => status.Start <= item.Timestamp && item.Timestamp < status.ProductionEnd)
            .OrderByDescending(static status => status.Start)
            .FirstOrDefault();
        if (resolved is not null)
        {
            application = resolved;
            return true;
        }
        application = null!;
        return false;
    }

    private static IReadOnlyList<StatusInterval> BuildStatusIntervals(NormalizedAttributionFight fight)
    {
        var result = new List<StatusInterval>();
        var active = new Dictionary<(long AbilityId, int SourceId, int TargetId), StatusInterval>();
        foreach (var item in DactRdpsReplay.OrderEventsForAttribution(fight.Events))
        {
            if (string.Equals(item.Type, "death", StringComparison.OrdinalIgnoreCase))
            {
                // Recipient statuses do not survive death. Provider death is intentionally
                // not used here because already-applied timed raid buffs can remain active.
                foreach (var keyToClear in active.Keys
                             .Where(key => key.TargetId == item.TargetId)
                             .ToArray())
                {
                    var cleared = active[keyToClear];
                    cleared.ObserveRemoval(
                        item.Timestamp,
                        item.AttributionSequence,
                        AttributionStatusEndReason.RecipientDeath);
                    active.Remove(keyToClear);
                }
                continue;
            }
            var key = (item.AbilityId, item.SourceId, item.TargetId);
            if (FflogsEventNormalizer.IsStatusApply(item.Type))
            {
                if (active.Remove(key, out var previous))
                {
                    previous.ObserveRemoval(
                        item.Timestamp,
                        item.AttributionSequence,
                        AttributionStatusEndReason.RefreshOrOverwrite);
                }
                var duration = item.DurationMilliseconds > 0
                    ? item.DurationMilliseconds
                    : fight.Fight.EndTime - item.Timestamp;
                var interval = new StatusInterval(
                    item.AbilityId,
                    item.AbilityName,
                    item.SourceId,
                    item.TargetId,
                    item.Timestamp,
                    item.AttributionSequence,
                    Math.Min(fight.Fight.EndTime, item.Timestamp + Math.Max(1, duration)));
                active[key] = interval;
                result.Add(interval);
            }
            else if (FflogsEventNormalizer.IsStatusRemove(item.Type) &&
                     item.Type is not "removebuffstack" and not "removedebuffstack" &&
                     active.Remove(key, out var removed))
            {
                // A stack decrement is not status expiry. Closing the interval at the
                // first consumed Inner Release stack would hide later guaranteed hits.
                removed.ObserveRemoval(
                    item.Timestamp,
                    item.AttributionSequence,
                    AttributionStatusEndReason.ExplicitRemove);
                removed.Removed = true;
            }
        }
        ApplyCohortRemoveEvidence(result);
        return result;
    }

    private static void ApplyCohortRemoveEvidence(IReadOnlyList<StatusInterval> intervals)
    {
        foreach (var cohort in intervals.GroupBy(static status =>
                     (status.AbilityId, status.SourceId, status.Start)))
        {
            var firstPostNominalTransition = cohort
                .Where(static status => status.HasObservedTransition)
                .Where(status => status.OracleEnd >= status.NominalEnd)
                .OrderBy(static status => status.OracleEnd)
                .ThenBy(static status => status.OracleEndSequence)
                .FirstOrDefault();
            if (firstPostNominalTransition is null)
            {
                continue;
            }

            foreach (var status in cohort)
            {
                // A party-wide status is emitted once per recipient. The first sibling
                // transition is causal evidence that removal fan-out has begun, while
                // the independent two-second cap still bounds a missing final packet.
                status.ObserveCohortRemoval(
                    firstPostNominalTransition.OracleEnd,
                    firstPostNominalTransition.OracleEndSequence);
            }
        }
    }

    private static IReadOnlyDictionary<ActionKey, ProbeGuaranteedDimensions> ResolveLifeSurgeActions(
        NormalizedAttributionFight fight,
        IReadOnlyList<StatusInterval> statuses)
    {
        var result = new Dictionary<ActionKey, ProbeGuaranteedDimensions>();
        foreach (var surge in statuses.Where(static status => status.AbilityId == 116 && status.Removed))
        {
            var candidates = fight.Events
                .Where(item => FflogsEventNormalizer.IsDamageEvent(item) &&
                               !item.IsPeriodic && item.AbilityId != 7 && item.Critical &&
                               Math.Abs(item.Timestamp - surge.OracleEnd) <= 1)
                .Where(item => FflogsEventNormalizer.ResolveOwnerActor(item.SourceId, fight.Actors)?.Id == surge.TargetId)
                .OrderBy(item => Math.Abs(item.Timestamp - surge.OracleEnd))
                .ToArray();
            if (candidates.Length == 0)
            {
                continue;
            }
            var selected = candidates[0];
            foreach (var item in fight.Events.Where(item =>
                         item.SourceId == selected.SourceId &&
                         item.AbilityId == selected.AbilityId &&
                         item.Timestamp == selected.Timestamp))
            {
                result[ActionKey.From(item)] = ProbeGuaranteedDimensions.Critical;
            }
        }
        return result;
    }

    private static IReadOnlyDictionary<ActionKey, ProbeGuaranteedDimensions> ResolveReassembleActions(
        NormalizedAttributionFight fight,
        IReadOnlyList<StatusInterval> statuses)
    {
        var result = new Dictionary<ActionKey, ProbeGuaranteedDimensions>();
        foreach (var reassemble in statuses.Where(static status => status.AbilityId == 851))
        {
            var selected = fight.Events
                .Where(item => FflogsEventNormalizer.IsDamageEvent(item) &&
                               !item.IsPeriodic && item.SourceId == reassemble.SourceId &&
                               ReassembleWeaponskills.Contains(item.AbilityId) &&
                               item.Timestamp >= reassemble.Start &&
                               item.Timestamp <= reassemble.OracleEnd + 1)
                .OrderBy(static item => item.Timestamp)
                .FirstOrDefault();
            if (selected is null)
            {
                continue;
            }
            foreach (var item in fight.Events.Where(item =>
                         item.SourceId == selected.SourceId &&
                         item.AbilityId == selected.AbilityId &&
                         item.Timestamp == selected.Timestamp))
            {
                result[ActionKey.From(item)] =
                    ProbeGuaranteedDimensions.Critical | ProbeGuaranteedDimensions.DirectHit;
            }
        }
        return result;
    }

    private static string NormalizeEffectName(string value)
    {
        value = value.Trim();
        foreach (var suffix in new[] { " (*)", " (DoT)" })
        {
            if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                value = value[..^suffix.Length].TrimEnd();
            }
        }
        return value.ToUpperInvariant();
    }

    private static string ToJobAbbreviation(string job)
        => job switch
        {
            "Samurai" => "SAM",
            "Dragoon" => "DRG",
            "Monk" => "MNK",
            "Dancer" => "DNC",
            "Machinist" => "MCH",
            "Pictomancer" => "PCT",
            "Warrior" => "WAR",
            _ => job.ToUpperInvariant(),
        };

    private sealed record StatusInterval(
        long AbilityId,
        string AbilityName,
        int SourceId,
        int TargetId,
        double Start,
        long StartSequence,
        double InitialNominalEnd)
    {
        public double NominalEnd { get; } = InitialNominalEnd;

        public double ProductionEnd { get; private set; } = InitialNominalEnd;

        public double OracleEnd => HasObservedTransition ? observedTransitionEnd : NominalEnd;

        public long OracleEndSequence => HasObservedTransition
            ? observedTransitionEndSequence
            : long.MinValue;

        public double NominalCausalEnd => Math.Min(
            HasObservedTransition ? observedTransitionEnd : double.PositiveInfinity,
            NominalEnd);

        public long NominalCausalEndSequence => HasObservedTransition &&
                                                observedTransitionEnd <= NominalEnd
            ? observedTransitionEndSequence
            : long.MinValue;

        public double CausalGraceEnd => Math.Min(
            HasObservedTransition ? observedTransitionEnd : double.PositiveInfinity,
            NominalEnd + CausalRemoveGraceMilliseconds);

        public long CausalGraceEndSequence => HasObservedTransition &&
                                              observedTransitionEnd <= NominalEnd + CausalRemoveGraceMilliseconds
            ? observedTransitionEndSequence
            : long.MinValue;

        public double CausalCohortEnd { get; private set; } =
            InitialNominalEnd + CausalRemoveGraceMilliseconds;

        public long CausalCohortEndSequence { get; private set; } = long.MinValue;

        public AttributionStatusEndReason EndReason { get; private set; } =
            AttributionStatusEndReason.MissingRemoveNominalFallback;

        public bool HasObservedTransition { get; private set; }

        public bool Removed { get; set; }

        private double observedTransitionEnd = InitialNominalEnd;

        private long observedTransitionEndSequence = long.MinValue;

        public void ObserveRemoval(
            double timestamp,
            long sequence,
            AttributionStatusEndReason reason)
        {
            // The explicit status transition is authoritative even when it arrives
            // slightly after the duration estimate carried by applybuff.
            observedTransitionEnd = timestamp;
            observedTransitionEndSequence = sequence;
            HasObservedTransition = true;
            EndReason = reason;
            ProductionEnd = Math.Min(ProductionEnd, timestamp);
            CausalCohortEnd = Math.Min(timestamp, NominalEnd + CausalRemoveGraceMilliseconds);
            CausalCohortEndSequence = timestamp <= NominalEnd + CausalRemoveGraceMilliseconds
                ? sequence
                : long.MinValue;
        }

        public void ObserveCohortRemoval(double timestamp, long sequence)
        {
            if (timestamp > CausalCohortEnd ||
                timestamp == CausalCohortEnd && sequence >= CausalCohortEndSequence)
            {
                return;
            }
            CausalCohortEnd = timestamp;
            CausalCohortEndSequence = sequence;
        }
    }

    private readonly record struct ActionKey(double Timestamp, int SourceId, int TargetId, long AbilityId)
    {
        public static ActionKey From(NormalizedFflogsEvent item)
            => new(item.Timestamp, item.SourceId, item.TargetId, item.AbilityId);
    }
}

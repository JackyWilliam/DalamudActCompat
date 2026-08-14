namespace DalamudActCompat.FflogsParityHarness;

internal sealed record MatrixBuffExposureEntry(
    OffensiveBuffDefinition Definition,
    int SourceActorId,
    string SourceActor,
    string SourceJob,
    int TargetActorId,
    bool IsSelfSourced,
    double DamageMultiplier);

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

    public MatrixEventAttributionState Resolve(
        NormalizedFflogsEvent item,
        FflogsActor owner)
    {
        var attributionTimestamp = item.Timestamp;
        var timing = "hit_time";
        if (item.IsPeriodic && TryResolvePeriodicApplication(item, out var application))
        {
            attributionTimestamp = application.Start;
            timing = "dot_snapshot";
        }

        var buffs = ResolveApplicableStatuses(
                attributionTimestamp,
                owner.Id,
                item.SourceId,
                item.TargetId)
            .Where(status => OffensiveBuffRegistry.ByStatusId.ContainsKey(status.AbilityId))
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
                    ResolvePercentageMultiplier(status, definition));
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
        OffensiveBuffDefinition definition)
    {
        if (definition.StatusId != 1822)
        {
            return definition.DamageMultiplier ?? 1;
        }
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
        int ownerId,
        int sourceId,
        int targetId)
        => statuses
            .Where(status => status.Start <= timestamp && timestamp < status.End)
            .Where(status => status.TargetId == ownerId ||
                             status.TargetId == sourceId ||
                             status.TargetId == targetId)
            .ToArray();

    private bool TryResolvePeriodicApplication(
        NormalizedFflogsEvent item,
        out StatusInterval application)
    {
        var effectName = NormalizeEffectName(item.AbilityName);
        var resolved = statuses
            .Where(status => status.SourceId == item.SourceId && status.TargetId == item.TargetId)
            .Where(status => NormalizeEffectName(status.AbilityName) == effectName)
            .Where(status => status.Start <= item.Timestamp && item.Timestamp < status.End)
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
        foreach (var item in fight.Events)
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
                    cleared.End = Math.Min(cleared.End, item.Timestamp);
                    active.Remove(keyToClear);
                }
                continue;
            }
            var key = (item.AbilityId, item.SourceId, item.TargetId);
            if (FflogsEventNormalizer.IsStatusApply(item.Type))
            {
                if (active.Remove(key, out var previous))
                {
                    previous.End = Math.Min(previous.End, item.Timestamp);
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
                removed.End = Math.Min(removed.End, item.Timestamp);
                removed.Removed = true;
            }
        }
        return result;
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
                               Math.Abs(item.Timestamp - surge.End) <= 1)
                .Where(item => FflogsEventNormalizer.ResolveOwnerActor(item.SourceId, fight.Actors)?.Id == surge.TargetId)
                .OrderBy(item => Math.Abs(item.Timestamp - surge.End))
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
                               item.Timestamp >= reassemble.Start && item.Timestamp <= reassemble.End + 1)
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
        double InitialEnd)
    {
        public double End { get; set; } = InitialEnd;

        public bool Removed { get; set; }
    }

    private readonly record struct ActionKey(double Timestamp, int SourceId, int TargetId, long AbilityId)
    {
        public static ActionKey From(NormalizedFflogsEvent item)
            => new(item.Timestamp, item.SourceId, item.TargetId, item.AbilityId);
    }
}

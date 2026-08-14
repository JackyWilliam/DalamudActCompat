using System.Collections;
using System.Reflection;
using DalamudActCompat.ActRuntime;

namespace DalamudActCompat.FflogsParityHarness;

[Flags]
internal enum ProbeGuaranteedDimensions
{
    None = 0,
    Critical = 1,
    DirectHit = 2,
}

internal static class ProductionGuaranteedMetadata
{
    public static IReadOnlyDictionary<long, ProbeGuaranteedDimensions> ReadStableActions()
    {
        var field = typeof(RaidDpsEstimator).GetField(
            "GuaranteedActionsById",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Production guaranteed-action metadata field was not found.");
        if (field.GetValue(null) is not IEnumerable values)
        {
            throw new InvalidOperationException("Production guaranteed-action metadata was not enumerable.");
        }

        var result = new Dictionary<long, ProbeGuaranteedDimensions>();
        foreach (var item in values)
        {
            var itemType = item!.GetType();
            var key = Convert.ToInt64(itemType.GetProperty("Key")!.GetValue(item));
            var dimensions = Convert.ToInt32(itemType.GetProperty("Value")!.GetValue(item));
            result[key] = (ProbeGuaranteedDimensions)dimensions;
        }
        return result;
    }
}

internal sealed class FightAttributionTimeline
{
    private static readonly IReadOnlyDictionary<long, double> PercentageMultipliers =
        new Dictionary<long, double>
        {
            [0x756] = 1.06,
            [0x4A1] = 1.05,
            [0xA8F] = 1.05,
            [0xA27] = 1.03,
            [0x511] = 1.05,
            [0xE65] = 1.05,
            [0x839] = 1.05,
            [0xF09] = 1.05,
            [0xF2F] = 1.06,
            [0xF31] = 1.06,
        };

    private static readonly IReadOnlyDictionary<long, (double Critical, double Direct)> RateBuffs =
        new Dictionary<long, (double Critical, double Direct)>
        {
            [0x312] = (0.10, 0),
            [0x4C5] = (0.10, 0),
            [0x721] = (0.20, 0.20),
            [0x08D] = (0, 0.20),
        };

    private readonly NormalizedFight fight;
    private readonly IReadOnlyList<ProbeStatusInterval> statuses;
    private readonly IReadOnlyDictionary<ProbeActionKey, ProbeGuaranteedDimensions> lifeSurgeActions;
    private readonly IReadOnlyDictionary<(int SourceId, int TargetId), IReadOnlyList<ProbeStatusInterval>>
        devilmentWindows;

    public FightAttributionTimeline(NormalizedFight fight)
    {
        this.fight = fight;
        statuses = BuildStatusIntervals(fight);
        lifeSurgeActions = ResolveLifeSurgeActions(fight, statuses);
        devilmentWindows = statuses
            .Where(status => status.AbilityId == 0x721 &&
                             status.SourceId == fight.Dancer.Id &&
                             status.TargetId != fight.Dancer.Id)
            .GroupBy(status => (status.SourceId, status.TargetId))
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<ProbeStatusInterval>)group
                    .OrderBy(static status => status.Start)
                    .Select((status, index) => status with { Window = index + 1 })
                    .ToArray());
    }

    public IReadOnlyList<ProbeStatusInterval> DevilmentWindows
        => devilmentWindows.Values.SelectMany(static windows => windows).ToArray();

    public IReadOnlySet<int> DevilmentTargetIds
        => devilmentWindows.Keys.Select(static key => key.TargetId).ToHashSet();

    public bool TryResolveLifeSurgeAction(
        NormalizedFflogsEvent item,
        out ProbeGuaranteedDimensions dimensions)
        => lifeSurgeActions.TryGetValue(ProbeActionKey.From(item), out dimensions);

    public ProbeAttributionState Resolve(NormalizedFflogsEvent item, FflogsActor owner)
    {
        var attributionTimestamp = item.Timestamp;
        var timing = "hit_time";
        if (item.IsPeriodic && TryResolvePeriodicApplication(item, out var application))
        {
            attributionTimestamp = application.Start;
            timing = "dot_snapshot";
        }

        var external = ResolveExternalStatuses(
            attributionTimestamp,
            owner.Id,
            item.SourceId,
            item.TargetId);
        var percentageMultiplier = external.Aggregate(
            1d,
            (current, status) => current * ResolvePercentageMultiplier(status));
        var criticalIncrease = 0d;
        var directIncrease = 0d;
        var devilmentCritical = 0d;
        var devilmentDirect = 0d;
        ProbeStatusInterval? attributedWindow = null;
        foreach (var status in external)
        {
            if (!RateBuffs.TryGetValue(status.AbilityId, out var rates))
            {
                continue;
            }
            criticalIncrease += rates.Critical;
            directIncrease += rates.Direct;
            if (status.AbilityId == 0x721 && status.SourceId == fight.Dancer.Id)
            {
                devilmentCritical += rates.Critical;
                devilmentDirect += rates.Direct;
                attributedWindow = ResolveNumberedDevilmentWindow(status);
            }
        }

        var activeAtHit = ResolveExternalStatuses(
                item.Timestamp,
                owner.Id,
                item.SourceId,
                item.TargetId)
            .Any(status => status.AbilityId == 0x721 && status.SourceId == fight.Dancer.Id);
        return new ProbeAttributionState(
            percentageMultiplier,
            criticalIncrease,
            directIncrease,
            devilmentCritical,
            devilmentDirect,
            activeAtHit,
            attributedWindow?.Window,
            timing);
    }

    private IReadOnlyList<ProbeStatusInterval> ResolveExternalStatuses(
        double timestamp,
        int ownerId,
        int sourceId,
        int targetId)
        => statuses
            .Where(status => status.Start <= timestamp && timestamp < status.End)
            .Where(status => status.TargetId == ownerId ||
                             status.TargetId == sourceId ||
                             status.TargetId == targetId)
            .Where(status => status.SourceId != ownerId)
            .Where(status => PercentageMultipliers.ContainsKey(status.AbilityId) ||
                             status.AbilityId == 0x71E ||
                             RateBuffs.ContainsKey(status.AbilityId))
            .GroupBy(static status => (status.AbilityId, status.SourceId))
            .Select(static group => group.First())
            .ToArray();

    private double ResolvePercentageMultiplier(ProbeStatusInterval status)
    {
        if (PercentageMultipliers.TryGetValue(status.AbilityId, out var multiplier))
        {
            return multiplier;
        }
        if (status.AbilityId != 0x71E)
        {
            return 1;
        }

        var action = fight.Events
            .Where(item => item.SourceId == status.SourceId && item.AbilityId is 0x81C1 or 0x81C2)
            .OrderBy(item => Math.Abs(item.Timestamp - status.Start))
            .FirstOrDefault();
        return action?.AbilityId == 0x81C1 ? 1.03 : 1.05;
    }

    private bool TryResolvePeriodicApplication(
        NormalizedFflogsEvent item,
        out ProbeStatusInterval application)
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

    private ProbeStatusInterval? ResolveNumberedDevilmentWindow(ProbeStatusInterval status)
        => devilmentWindows.GetValueOrDefault((status.SourceId, status.TargetId))?
            .FirstOrDefault(window => window.Start == status.Start && window.End == status.End);

    private static IReadOnlyList<ProbeStatusInterval> BuildStatusIntervals(NormalizedFight fight)
    {
        var result = new List<ProbeStatusInterval>();
        var active = new Dictionary<(long AbilityId, int SourceId, int TargetId), ProbeStatusInterval>();
        foreach (var item in fight.Events)
        {
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
                var interval = new ProbeStatusInterval(
                    item.AbilityId,
                    item.AbilityName,
                    item.SourceId,
                    item.TargetId,
                    item.Timestamp,
                    Math.Min(fight.Fight.EndTime, item.Timestamp + Math.Max(1, duration)),
                    Window: null);
                active[key] = interval;
                result.Add(interval);
            }
            else if (FflogsEventNormalizer.IsStatusRemove(item.Type) && active.Remove(key, out var removed))
            {
                removed.End = Math.Min(removed.End, item.Timestamp);
                removed.Removed = true;
            }
        }
        return result;
    }

    private static IReadOnlyDictionary<ProbeActionKey, ProbeGuaranteedDimensions> ResolveLifeSurgeActions(
        NormalizedFight fight,
        IReadOnlyList<ProbeStatusInterval> statuses)
    {
        var result = new Dictionary<ProbeActionKey, ProbeGuaranteedDimensions>();
        foreach (var surge in statuses.Where(static status => status.AbilityId == 0x74))
        {
            if (!surge.Removed)
            {
                continue;
            }
            var ownerId = surge.TargetId;
            var candidates = fight.Events
                .Where(item => FflogsEventNormalizer.IsDamageEvent(item) &&
                               !item.IsPeriodic &&
                               item.AbilityId != 7 &&
                               item.Critical &&
                               Math.Abs(item.Timestamp - surge.End) <= 1)
                .Where(item => FflogsEventNormalizer.ResolveOwnerActor(item.SourceId, fight.Actors)?.Id == ownerId)
                .OrderBy(item => Math.Abs(item.Timestamp - surge.End))
                .ToArray();
            if (candidates.Length == 0)
            {
                continue;
            }

            // Consumption removes Life Surge on the action packet. Matching that boundary
            // avoids pretending that an earlier random critical oGCD consumed a weaponskill buff.
            var selected = candidates[0];
            foreach (var item in fight.Events.Where(item =>
                         item.SourceId == selected.SourceId &&
                         item.AbilityId == selected.AbilityId &&
                         item.Timestamp == selected.Timestamp))
            {
                result[ProbeActionKey.From(item)] = ProbeGuaranteedDimensions.Critical;
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

    internal sealed record ProbeStatusInterval(
        long AbilityId,
        string AbilityName,
        int SourceId,
        int TargetId,
        double Start,
        double InitialEnd,
        int? Window)
    {
        public double End { get; set; } = InitialEnd;

        public bool Removed { get; set; }
    }

    private readonly record struct ProbeActionKey(
        double Timestamp,
        int SourceId,
        int TargetId,
        long AbilityId)
    {
        public static ProbeActionKey From(NormalizedFflogsEvent item)
            => new(item.Timestamp, item.SourceId, item.TargetId, item.AbilityId);
    }
}

internal readonly record struct ProbeAttributionState(
    double PercentageMultiplier,
    double CriticalIncrease,
    double DirectIncrease,
    double DevilmentCriticalIncrease,
    double DevilmentDirectIncrease,
    bool DevilmentActiveAtHit,
    int? DevilmentWindow,
    string AttributionTiming);

internal static class DevilmentContributionMath
{
    public static (double Critical, double Direct) Calculate(
        NormalizedFflogsEvent item,
        ProbeAttributionState state,
        double unbuffedCriticalChance,
        double unbuffedDirectChance,
        ProbeGuaranteedDimensions dimensions)
    {
        if (state.DevilmentCriticalIncrease <= 0 && state.DevilmentDirectIncrease <= 0)
        {
            return (0, 0);
        }

        var damage = item.Amount / Math.Max(1, state.PercentageMultiplier);
        if (item.IsPeriodic)
        {
            return CalculateDot(
                damage,
                state,
                unbuffedCriticalChance,
                unbuffedDirectChance);
        }

        var guaranteed = CalculateGuaranteed(
            damage,
            state,
            unbuffedCriticalChance,
            dimensions);
        var critical = guaranteed.Critical;
        var direct = guaranteed.Direct;
        if ((dimensions & ProbeGuaranteedDimensions.Critical) == 0 && item.Critical)
        {
            var criticalMultiplier = 1.35 + unbuffedCriticalChance;
            var combined = criticalMultiplier * (item.DirectHit ? 1.25 : 1);
            var portion = LogWeightedBonusPortion(damage, criticalMultiplier, combined);
            var buffedChance = Math.Clamp(
                unbuffedCriticalChance + state.CriticalIncrease,
                0.01,
                1);
            critical += portion * state.DevilmentCriticalIncrease / buffedChance;
        }
        if ((dimensions & ProbeGuaranteedDimensions.DirectHit) == 0 && item.DirectHit)
        {
            var criticalMultiplier = item.Critical ? 1.35 + unbuffedCriticalChance : 1;
            var combined = criticalMultiplier * 1.25;
            var portion = LogWeightedBonusPortion(damage, 1.25, combined);
            var buffedChance = Math.Clamp(
                unbuffedDirectChance + state.DirectIncrease,
                0.01,
                1);
            direct += portion * state.DevilmentDirectIncrease / buffedChance;
        }
        return (critical, direct);
    }

    private static (double Critical, double Direct) CalculateGuaranteed(
        double damage,
        ProbeAttributionState state,
        double unbuffedCriticalChance,
        ProbeGuaranteedDimensions dimensions)
    {
        var criticalIncrease = (dimensions & ProbeGuaranteedDimensions.Critical) != 0
            ? state.CriticalIncrease
            : 0;
        var directIncrease = (dimensions & ProbeGuaranteedDimensions.DirectHit) != 0
            ? state.DirectIncrease
            : 0;
        var criticalMultiplier = 1.35 + unbuffedCriticalChance;
        var criticalRatio = criticalIncrease > 0
            ? (criticalMultiplier + criticalIncrease * (criticalMultiplier - 1)) / criticalMultiplier
            : 1;
        var directRatio = directIncrease > 0
            ? (1.25 + directIncrease * 0.25) / 1.25
            : 1;
        var combined = criticalRatio * directRatio;
        var critical = criticalRatio > 1
            ? LogWeightedBonusPortion(damage, criticalRatio, combined) *
              state.DevilmentCriticalIncrease / criticalIncrease
            : 0;
        var direct = directRatio > 1
            ? LogWeightedBonusPortion(damage, directRatio, combined) *
              state.DevilmentDirectIncrease / directIncrease
            : 0;
        return (critical, direct);
    }

    private static (double Critical, double Direct) CalculateDot(
        double damage,
        ProbeAttributionState state,
        double unbuffedCriticalChance,
        double unbuffedDirectChance)
    {
        var buffedCritical = Math.Clamp(unbuffedCriticalChance + state.CriticalIncrease, 0.01, 1);
        var buffedDirect = Math.Clamp(unbuffedDirectChance + state.DirectIncrease, 0.01, 1);
        var criticalMultiplier = 1.35 + unbuffedCriticalChance;
        const double directMultiplier = 1.25;
        var combined = criticalMultiplier * directMultiplier;
        var noCritical = 1 - buffedCritical;
        var noDirect = 1 - buffedDirect;
        var totalMultiplier =
            (noCritical * noDirect) +
            (buffedCritical * noDirect * criticalMultiplier) +
            (noCritical * buffedDirect * directMultiplier) +
            (buffedCritical * buffedDirect * combined);
        var criticalPortion =
            ((buffedCritical * noDirect * criticalMultiplier) +
             (Math.Log(criticalMultiplier) / Math.Log(combined) *
              buffedCritical * buffedDirect * combined)) * damage / totalMultiplier;
        var directPortion =
            ((buffedDirect * noCritical * directMultiplier) +
             (Math.Log(directMultiplier) / Math.Log(combined) *
              buffedCritical * buffedDirect * combined)) * damage / totalMultiplier;
        return (
            criticalPortion * state.DevilmentCriticalIncrease / buffedCritical,
            directPortion * state.DevilmentDirectIncrease / buffedDirect);
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
        var bonusDamage = damage - damage / combinedMultiplier;
        return Math.Abs(componentMultiplier - combinedMultiplier) < 0.000001
            ? bonusDamage
            : bonusDamage * Math.Log(componentMultiplier) / Math.Log(combinedMultiplier);
    }
}

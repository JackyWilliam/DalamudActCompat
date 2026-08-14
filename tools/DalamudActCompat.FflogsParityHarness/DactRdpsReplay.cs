using System.Globalization;
using DalamudActCompat.ActRuntime;

namespace DalamudActCompat.FflogsParityHarness;

/// <summary>
/// Replays normalized API events through the production RaidDpsEstimator. This class
/// adapts event identity and pipe-line shape only; it never reads FFLogs final rDPS.
/// </summary>
internal static class DactRdpsReplay
{
    public static ParitySampleResult Replay(NormalizedFight fight)
    {
        var estimator = new RaidDpsEstimator();
        estimator.Reset();
        foreach (var actor in fight.Actors.Values)
        {
            estimator.ObserveNetworkLine(
                ToTimestamp(fight.ReportStartTime, fight.Fight.StartTime),
                BuildActorLine(actor));
        }

        estimator.StartEncounter(ToTimestamp(fight.ReportStartTime, fight.Fight.StartTime));
        var partyIds = fight.Party.Select(static actor => actor.Id).ToHashSet();
        long dancerDamage = 0;
        var damageEventCount = 0;
        var statusEventCount = 0;
        var matchedCalculatedDamageCount = 0;
        var unmatchedCalculatedDamageCount = 0;
        var unmatchedDirectDamageCount = 0;
        var periodicDamageEventCount = 0;
        foreach (var item in fight.Events)
        {
            var timestamp = ToTimestamp(fight.ReportStartTime, item.Timestamp);
            if (FflogsEventNormalizer.IsStatusApply(item.Type))
            {
                statusEventCount++;
                if (item.AbilityId == 0x71E &&
                    TryResolveTechnicalFinishAction(fight, item, out var technicalAction))
                {
                    // FFLogs exposes action and status as separate events; DACT's production
                    // protocol joins them within two seconds to preserve the three/four-step rank.
                    estimator.ObserveNetworkLine(
                        ToTimestamp(fight.ReportStartTime, technicalAction.Timestamp),
                        BuildActionLine(technicalAction, fight.Actors));
                }
                estimator.ObserveStatusLine(
                    timestamp,
                    BuildStatusLine(item, fight, remove: false));
                continue;
            }

            if (FflogsEventNormalizer.IsStatusRemove(item.Type))
            {
                statusEventCount++;
                estimator.ObserveStatusLine(
                    timestamp,
                    BuildStatusLine(item, fight, remove: true));
                continue;
            }

            if (!FflogsEventNormalizer.IsDamageEvent(item) || item.Amount <= 0)
            {
                continue;
            }

            var source = fight.Actors.GetValueOrDefault(item.SourceId);
            var owner = FflogsEventNormalizer.ResolveOwnerActor(item.SourceId, fight.Actors);
            if (source is null || owner is null || !partyIds.Contains(owner.Id) || partyIds.Contains(item.TargetId))
            {
                continue;
            }

            damageEventCount++;
            if (item.MatchedCalculatedDamage)
            {
                matchedCalculatedDamageCount++;
            }
            else
            {
                unmatchedCalculatedDamageCount++;
            }
            if (item.IsPeriodic)
            {
                periodicDamageEventCount++;
            }
            else if (!item.MatchedCalculatedDamage)
            {
                unmatchedDirectDamageCount++;
            }
            estimator.ObserveNetworkLine(timestamp, BuildActionLine(item, fight.Actors));
            var target = fight.Actors.GetValueOrDefault(item.TargetId);
            estimator.ObserveEffectiveDamage(
                new EffectiveDamageEvent(
                    timestamp,
                    FormatActorId(source.Id),
                    source.Name,
                    source.PetOwnerId is { } ownerId ? FormatActorId(ownerId) : string.Empty,
                    FormatActorId(item.TargetId),
                    target?.Name ?? $"Actor {item.TargetId}",
                    item.AbilityName,
                    item.Amount,
                    item.Critical,
                    item.DirectHit,
                    item.IsPeriodic),
                owner.Name);
            if (owner.Id == fight.Dancer.Id)
            {
                dancerDamage += item.Amount;
            }
        }

        estimator.FinishEncounter();
        var duration = fight.FflogsMetricDurationSeconds;
        if (duration <= 0)
        {
            throw new InvalidDataException("FFLogs DamageDone table duration is missing or non-positive.");
        }

        var received = estimator.ResolveReceivedDamage(fight.Dancer.Name);
        var given = estimator.ResolveContributedDamage(fight.Dancer.Name);
        var percentageReceived = estimator.ResolveReceivedDamage(
            fight.Dancer.Name,
            RaidDpsEstimator.AttributionKind.Percentage);
        var percentageGiven = estimator.ResolveContributedDamage(
            fight.Dancer.Name,
            RaidDpsEstimator.AttributionKind.Percentage);
        var critReceived = estimator.ResolveReceivedDamage(
            fight.Dancer.Name,
            RaidDpsEstimator.AttributionKind.Critical);
        var critGiven = estimator.ResolveContributedDamage(
            fight.Dancer.Name,
            RaidDpsEstimator.AttributionKind.Critical);
        var directReceived = estimator.ResolveReceivedDamage(
            fight.Dancer.Name,
            RaidDpsEstimator.AttributionKind.DirectHit);
        var directGiven = estimator.ResolveContributedDamage(
            fight.Dancer.Name,
            RaidDpsEstimator.AttributionKind.DirectHit);
        var fflogsDps = fight.FflogsMetrics.RawDamage / duration;
        var fflogsRdps = fight.FflogsMetrics.RdpsTotal / duration;
        double? fflogsAdps = fight.FflogsMetrics.AdpsTotal > 0
            ? fight.FflogsMetrics.AdpsTotal / duration
            : null;
        double? fflogsNdps = fight.FflogsMetrics.NdpsTotal > 0
            ? fight.FflogsMetrics.NdpsTotal / duration
            : null;
        if (fflogsRdps <= 0)
        {
            throw new InvalidDataException("FFLogs DamageDone table totalRDPS is missing or non-positive.");
        }

        var fflogsTechnical = ResolveFflogsGiven(fight.FflogsMetrics, 0x71E);
        var fflogsStandard = ResolveFflogsGiven(fight.FflogsMetrics, 0x839);
        var fflogsDevilment = ResolveFflogsGiven(fight.FflogsMetrics, 0x721);
        var dactDps = dancerDamage / duration;
        var dactRdps = estimator.ResolveRate(fight.Dancer.Name, dancerDamage, duration);
        var delta = dactRdps - fflogsRdps;
        var displayDelta = RoundForDisplay(dactRdps) - RoundForDisplay(fflogsRdps);
        var warnings = fight.NormalizationWarnings.ToList();
        var normalizationDelta = dancerDamage - fight.DancerDamageTableTotal;
        if (normalizationDelta != 0)
        {
            warnings.Add(
                $"DNC event damage {dancerDamage} differs from DamageDone table {fight.DancerDamageTableTotal} by {normalizationDelta}.");
        }
        var fflogsRdpsIdentity = fight.FflogsMetrics.RawDamage -
                                 fight.FflogsMetrics.ExternalBuffContributionReceived +
                                 fight.FflogsMetrics.OwnBuffContributionGiven;
        if (Math.Abs(fflogsRdpsIdentity - fight.FflogsMetrics.RdpsTotal) > 0.01)
        {
            warnings.Add(
                $"FFLogs totalRDPS identity differs by {fflogsRdpsIdentity - fight.FflogsMetrics.RdpsTotal:R} damage.");
        }

        return new ParitySampleResult
        {
            Report = fight.Seed.ReportCode,
            FightId = fight.Fight.Id,
            ActorId = fight.Dancer.Id,
            Actor = fight.Dancer.Name,
            Encounter = fight.Fight.Name,
            EncounterId = fight.Fight.EncounterId,
            Duration = duration,
            DurationSource = "FFLogs DamageDone table totalTime",
            PartyComposition = fight.PartyComposition,
            FflogsDps = fflogsDps,
            FflogsRdps = fflogsRdps,
            FflogsAdps = fflogsAdps,
            FflogsNdps = fflogsNdps,
            RankingPdps = fight.Seed.FflogsDps,
            RankingRdps = fight.Seed.FflogsRdps,
            RankingAdps = fight.Seed.FflogsAdps,
            RankingNdps = fight.Seed.FflogsNdps,
            DactDps = dactDps,
            DactRdps = dactRdps,
            DeltaRdps = delta,
            DisplayDeltaRdps = displayDelta,
            DeltaPercent = delta / fflogsRdps * 100,
            RawDamage = dancerDamage,
            FflogsDamageTableAmount = fight.DancerDamageTableTotal,
            DamageNormalizationDelta = normalizationDelta,
            ExternalBuffContributionReceived = received,
            OwnBuffContributionGiven = given,
            FflogsExternalBuffContributionReceived = fight.FflogsMetrics.ExternalBuffContributionReceived,
            FflogsOwnBuffContributionGiven = fight.FflogsMetrics.OwnBuffContributionGiven,
            FflogsGivenBreakdown = fight.FflogsMetrics.Given,
            FflogsTakenBreakdown = fight.FflogsMetrics.Taken,
            ExternalBuffContributionReceivedDelta = received - fight.FflogsMetrics.ExternalBuffContributionReceived,
            OwnBuffContributionGivenDelta = given - fight.FflogsMetrics.OwnBuffContributionGiven,
            // Production currently aggregates both percentage buffs by source actor.
            TechnicalFinishContribution = null,
            StandardFinishContribution = null,
            TechnicalAndStandardContribution = percentageGiven,
            DevilmentContribution = critGiven + directGiven,
            FflogsTechnicalFinishContribution = fflogsTechnical,
            FflogsStandardFinishContribution = fflogsStandard,
            FflogsTechnicalAndStandardContribution = fflogsTechnical + fflogsStandard,
            FflogsDevilmentContribution = fflogsDevilment,
            TechnicalAndStandardContributionDelta = percentageGiven - fflogsTechnical - fflogsStandard,
            DevilmentContributionDelta = critGiven + directGiven - fflogsDevilment,
            CritContributionReceived = critReceived,
            DirectHitContributionReceived = directReceived,
            CritDirectHitContributionReceived = critReceived + directReceived,
            CritContributionGiven = critGiven,
            DirectHitContributionGiven = directGiven,
            CritDirectHitContributionGiven = critGiven + directGiven,
            PercentageContributionReceived = percentageReceived,
            PercentageContributionGiven = percentageGiven,
            TechnicalFinishPresent = fight.TechnicalFinishPresent,
            StandardFinishPresent = fight.StandardFinishPresent,
            DevilmentPresent = fight.DevilmentPresent,
            MultiRaidBuffOverlap = fight.MultiRaidBuffOverlap,
            MaximumRaidBuffOverlap = fight.MaximumRaidBuffOverlap,
            DancePartnerJob = fight.DancePartnerJob,
            WallDuration = fight.WallDurationSeconds,
            Downtime = fight.DowntimeSeconds,
            DeathCount = fight.DeathCount,
            ResurrectionCount = fight.ResurrectionCount,
            HasPetJob = fight.HasPetJob,
            PetJobs = fight.PetJobs,
            HasDotJob = fight.HasDotJob,
            DotJobs = fight.DotJobs,
            DamageEventCount = damageEventCount,
            StatusEventCount = statusEventCount,
            MatchedCalculatedDamageCount = matchedCalculatedDamageCount,
            UnmatchedCalculatedDamageCount = unmatchedCalculatedDamageCount,
            UnmatchedDirectDamageCount = unmatchedDirectDamageCount,
            PeriodicDamageEventCount = periodicDamageEventCount,
            TechnicalFinishRankResolvedCount = fight.TechnicalFinishRankResolvedCount,
            NormalizationWarnings = warnings,
        };
    }

    private static double ResolveFflogsGiven(FflogsDamageTableMetrics metrics, long abilityId)
        => metrics.Given
            .Where(item => item.AbilityId == abilityId)
            .Sum(static item => item.Amount);

    internal static string BuildActorLine(FflogsActor actor)
        => string.Join(
            '|',
            "03",
            "time",
            FormatActorId(actor.Id),
            Sanitize(actor.Name),
            "00",
            "00",
            actor.PetOwnerId is { } ownerId ? FormatActorId(ownerId) : "0000",
            "00",
            string.Empty);

    internal static string BuildStatusLine(
        NormalizedFflogsEvent item,
        NormalizedFight fight,
        bool remove)
    {
        var source = fight.Actors.GetValueOrDefault(item.SourceId);
        var target = fight.Actors.GetValueOrDefault(item.TargetId);
        var remainingSeconds = Math.Clamp(
            item.DurationMilliseconds > 0
                ? item.DurationMilliseconds / 1000d
                : (fight.Fight.EndTime - item.Timestamp) / 1000d,
            0.001,
            600);
        return string.Join(
            '|',
            remove ? "30" : "26",
            "time",
            item.AbilityId.ToString("X", CultureInfo.InvariantCulture),
            Sanitize(item.AbilityName),
            remove ? "0.00" : remainingSeconds.ToString("F3", CultureInfo.InvariantCulture),
            FormatActorId(item.SourceId),
            Sanitize(source?.Name ?? $"Actor {item.SourceId}"),
            FormatActorId(item.TargetId),
            Sanitize(target?.Name ?? $"Actor {item.TargetId}"),
            string.Empty);
    }

    internal static string BuildActionLine(
        NormalizedFflogsEvent item,
        IReadOnlyDictionary<int, FflogsActor> actors)
    {
        var fields = Enumerable.Repeat("0", 24).ToArray();
        fields[0] = "21";
        fields[1] = "time";
        fields[2] = FormatActorId(item.SourceId);
        fields[3] = Sanitize(actors.GetValueOrDefault(item.SourceId)?.Name ?? $"Actor {item.SourceId}");
        fields[4] = item.AbilityId.ToString("X", CultureInfo.InvariantCulture);
        fields[5] = Sanitize(item.AbilityName);
        fields[6] = FormatActorId(item.TargetId);
        fields[7] = Sanitize(actors.GetValueOrDefault(item.TargetId)?.Name ?? $"Actor {item.TargetId}");
        // A single decoded damage slot is enough for the production guarantee queue;
        // the authoritative amount still comes from EffectiveDamageEvent below.
        fields[8] = "000003";
        fields[9] = "00640000";
        return string.Join('|', fields);
    }

    internal static bool TryResolveTechnicalFinishAction(
        NormalizedFight fight,
        NormalizedFflogsEvent status,
        out NormalizedFflogsEvent action)
    {
        var resolved = fight.Events
            .Where(candidate =>
                candidate.SourceId == status.SourceId &&
                candidate.AbilityId is 0x81C1 or 0x81C2 &&
                Math.Abs(candidate.Timestamp - status.Timestamp) <= 2000)
            .OrderBy(candidate => Math.Abs(candidate.Timestamp - status.Timestamp))
            .FirstOrDefault();
        if (resolved is not null)
        {
            action = resolved;
            return true;
        }

        action = null!;
        return false;
    }

    internal static DateTimeOffset ToTimestamp(long reportStartTime, double relativeMilliseconds)
        => DateTimeOffset.FromUnixTimeMilliseconds(
            reportStartTime + (long)Math.Round(relativeMilliseconds, MidpointRounding.AwayFromZero));

    internal static string FormatActorId(int actorId)
        => unchecked((uint)Math.Max(0, actorId)).ToString("X8", CultureInfo.InvariantCulture);

    private static string Sanitize(string value) => value.Replace('|', '¦').Trim();

    internal static double RoundForDisplay(double value)
        => Math.Round(value, 1, MidpointRounding.AwayFromZero);
}

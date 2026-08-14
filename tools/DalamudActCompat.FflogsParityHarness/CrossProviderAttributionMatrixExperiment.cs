using DalamudActCompat.ActRuntime;

namespace DalamudActCompat.FflogsParityHarness;

internal static class CrossProviderAttributionMatrixExperiment
{
    public static AttributionMatrixReport Run(
        FflogsSampleCollector collector,
        CacheManifest manifest,
        IReadOnlyList<CachedFightSample>? targetedSamples = null,
        int newlyMinedFightCount = 0)
    {
        var samples = manifest.Seeds
            .Select(seed => (Sample: collector.ReadCachedSample(seed), Source: "existing-100-cache"))
            .Concat((targetedSamples ?? []).Select(sample =>
                (Sample: sample, Source: "targeted-matrix-cache")))
            .DistinctBy(static item => $"{item.Sample.Seed.ReportCode}:{item.Sample.Seed.FightId}")
            .ToArray();
        var constraints = new List<MatrixConstraintResult>();
        for (var index = 0; index < samples.Length; index++)
        {
            var (sample, source) = samples[index];
            var fight = FflogsEventNormalizer.NormalizeAttribution(sample);
            constraints.AddRange(AnalyzeFight(fight, source));
            if ((index + 1) % 10 == 0 || index + 1 == samples.Length)
            {
                Console.WriteLine($"Attribution matrix analyzed {index + 1}/{samples.Length} fights.");
            }
        }

        var matrix = BuildMatrix(constraints);
        var matched = BuildMatchedGroups(constraints);
        var rankings = BuildCandidateRankings(constraints, matched);
        var percentageControl = BuildPercentageControl(constraints);
        var rejected = BuildRejectedCandidates(rankings);
        var (equationStatus, statusReason) = ResolveEquationStatus(
            matrix,
            rankings,
            matched,
            percentageControl);
        return new AttributionMatrixReport(
            DateTimeOffset.UtcNow,
            "Provider → recipient DamageDone taken[] aggregate constraints; Patch 7.5 / partition 9",
            equationStatus,
            statusReason,
            OffensiveBuffRegistry.All,
            GuaranteedHitRegistry.All,
            matrix,
            constraints,
            matched,
            rankings,
            percentageControl,
            rejected,
            BuildCrossProviderFindings(constraints, rankings),
            BuildRemainingUnknowns(matrix),
            BuildMinimumDataNeeds(matrix, matched, percentageControl),
            [
                $"Raw damage is validated actor-by-actor against FFLogs DamageDone for all {samples.Length} fights; the matrix aborts on any mismatch. Distinct normalization warnings={constraints.SelectMany(static item => item.Warnings).Distinct(StringComparer.Ordinal).Count()}.",
                "FFLogs provides provider→recipient→buff fight aggregates in DamageDone taken[], not per-event contribution. Matrix-cell residuals therefore reuse only aggregate constraints that contain that hit type; they are non-additive across cells.",
                "No FFLogs per-action truth is fabricated or back-solved. Event-level candidate values are summed forward into the published aggregate reference.",
                "Cu/Du are production HitBaseline snapshots immediately before each event; actual FFLogs Cu/Du remain unavailable.",
                "Authoritative Patch 7.5 registry and production coverage are separate. CurrentProduction uses only production-covered statuses/actions; other candidates use the authoritative registry.",
                "Radiant Finale has a 2%/4%/6% Coda-dependent multiplier that cached status events do not encode; it remains in the registry but is excluded from fixed-magnitude percentage control residuals.",
                "The current PvE Job Guides expose no guaranteed-DH-only action. An empty G-DH column is a game-mechanic evidence gap, not a failed random-log search.",
                "MNK Opo-opo/Formless guaranteed-Crit actions are authoritative registry entries but the required form state is not normalized, so they remain in the Normal matrix bucket and are excluded from claimed G-Crit evidence.",
            ],
            manifest.Seeds.Count,
            targetedSamples?.Count ?? 0,
            newlyMinedFightCount);
    }

    private static IReadOnlyList<MatrixConstraintResult> AnalyzeFight(
        NormalizedAttributionFight fight,
        string sourceCache)
    {
        var timeline = new AttributionTimeline(fight);
        var estimator = new RaidDpsEstimator(timeline.LifeSurgeWeaponskillActionIds.Contains);
        estimator.Reset();
        var encounterStart = DactRdpsReplay.ToTimestamp(fight.ReportStartTime, fight.Fight.StartTime);
        foreach (var actor in fight.Actors.Values)
        {
            estimator.ObserveNetworkLine(encounterStart, DactRdpsReplay.BuildActorLine(actor));
        }
        estimator.StartEncounter(encounterStart);

        var productionStable = ProductionGuaranteedMetadata.ReadStableActions();
        var partyIds = fight.Party.Select(static actor => actor.Id).ToHashSet();
        ValidateRawDamageParity(fight, partyIds);
        var accumulators = new Dictionary<ConstraintKey, ConstraintAccumulator>();
        foreach (var item in DactRdpsReplay.OrderEventsForAttribution(fight.Events))
        {
            var timestamp = DactRdpsReplay.ToTimestamp(fight.ReportStartTime, item.Timestamp);
            if (FflogsEventNormalizer.IsStatusApply(item.Type))
            {
                if (item.AbilityId == 1822)
                {
                    var finish = fight.Events
                        .Where(candidate => candidate.SourceId == item.SourceId &&
                                            candidate.AbilityId is 33217 or 33218 &&
                                            Math.Abs(candidate.Timestamp - item.Timestamp) <= 2000)
                        .OrderBy(candidate => Math.Abs(candidate.Timestamp - item.Timestamp))
                        .FirstOrDefault();
                    if (finish is not null)
                    {
                        estimator.ObserveNetworkLine(
                            DactRdpsReplay.ToTimestamp(fight.ReportStartTime, finish.Timestamp),
                            DactRdpsReplay.BuildActionLine(finish, fight.Actors));
                    }
                }
                estimator.ObserveStatusLine(
                    timestamp,
                    DactRdpsReplay.BuildStatusLine(item, fight.Fight, fight.Actors, remove: false));
                continue;
            }
            if (FflogsEventNormalizer.IsStatusRemove(item.Type))
            {
                estimator.ObserveStatusLine(
                    timestamp,
                    DactRdpsReplay.BuildStatusLine(item, fight.Fight, fight.Actors, remove: true));
                continue;
            }
            if (!FflogsEventNormalizer.IsDamageEvent(item) || item.Amount <= 0)
            {
                continue;
            }

            var source = fight.Actors.GetValueOrDefault(item.SourceId);
            var owner = FflogsEventNormalizer.ResolveOwnerActor(item.SourceId, fight.Actors);
            if (source is null || owner is null ||
                !partyIds.Contains(owner.Id) || partyIds.Contains(item.TargetId))
            {
                continue;
            }

            var baseline = estimator.ResolveHitBaseline(owner.Name);
            var state = timeline.Resolve(item, owner);
            var (authoritativeDimensions, guaranteeSource) = timeline.ResolveGuaranteed(item, owner);
            var productionDimensions = productionStable.GetValueOrDefault(item.AbilityId);
            if (productionDimensions == ProbeGuaranteedDimensions.None &&
                guaranteeSource is "Reassemble contextual status" or "Life Surge contextual status")
            {
                productionDimensions = authoritativeDimensions;
            }

            foreach (var providerBuff in state.Buffs
                         .Where(static buff => !buff.IsSelfSourced)
                         // A variable-rank buff without a recoverable rank cannot be a numeric
                         // control constraint; retaining it would manufacture a zero prediction.
                         .Where(static buff => buff.Definition.Dimension !=
                                               OffensiveBuffDimension.PercentageDamage ||
                                               buff.Definition.DamageMultiplier is not null))
            {
                var key = new ConstraintKey(owner.Id, providerBuff.SourceActorId, providerBuff.Definition.StatusId);
                if (!accumulators.TryGetValue(key, out var accumulator))
                {
                    accumulator = new ConstraintAccumulator(owner, providerBuff);
                    accumulators.Add(key, accumulator);
                }
                accumulator.ObserveCoverage(
                    item,
                    authoritativeDimensions,
                    baseline,
                    state);
                foreach (var definition in GuaranteedHitCandidateMath.Definitions)
                {
                    var contribution = providerBuff.Definition.Dimension ==
                                       OffensiveBuffDimension.PercentageDamage
                        ? providerBuff.Definition.DamageMultiplier is null
                            ? 0
                            : definition.Name == GuaranteedHitCandidateMath.CurrentProduction &&
                              !providerBuff.Definition.CoveredByProduction
                                ? 0
                                : AttributionContributionMath.CalculatePercentageContribution(
                                    item,
                                    state,
                                    providerBuff,
                                    definition.Name == GuaranteedHitCandidateMath.CurrentProduction)
                        : AttributionContributionMath.CalculateRateContribution(
                            definition.Name,
                            item,
                            state,
                            providerBuff,
                            baseline.CriticalChance,
                            baseline.DirectHitChance,
                            definition.Name == GuaranteedHitCandidateMath.CurrentProduction
                                ? productionDimensions
                                : authoritativeDimensions,
                            definition.Name == GuaranteedHitCandidateMath.CurrentProduction);
                    accumulator.AddCandidate(definition.Name, contribution);
                }
            }

            estimator.ObserveNetworkLine(timestamp, DactRdpsReplay.BuildActionLine(item, fight.Actors));
            var target = fight.Actors.GetValueOrDefault(item.TargetId);
            estimator.ObserveEffectiveDamage(
                new EffectiveDamageEvent(
                    timestamp,
                    DactRdpsReplay.FormatActorId(source.Id),
                    source.Name,
                    source.PetOwnerId is { } ownerId
                        ? DactRdpsReplay.FormatActorId(ownerId)
                        : string.Empty,
                    DactRdpsReplay.FormatActorId(item.TargetId),
                    target?.Name ?? $"Actor {item.TargetId}",
                    item.AbilityName,
                    item.Amount,
                    item.Critical,
                    item.DirectHit,
                    item.IsPeriodic),
                owner.Name);
        }
        estimator.FinishEncounter();

        var results = new List<MatrixConstraintResult>();
        foreach (var accumulator in accumulators.Values)
        {
            if (!fight.DamageTableActors.TryGetValue(accumulator.Recipient.Id, out var tableActor))
            {
                continue;
            }
            var reference = tableActor.Taken
                .Where(item => item.AbilityId == accumulator.ProviderBuff.Definition.StatusId)
                .Sum(static item => item.Amount);
            if (reference <= 0)
            {
                continue;
            }
            results.Add(accumulator.Build(fight, reference, sourceCache));
        }
        return results;
    }

    private static void ValidateRawDamageParity(
        NormalizedAttributionFight fight,
        IReadOnlySet<int> partyIds)
    {
        var replayTotals = fight.Events
            .Where(static item => FflogsEventNormalizer.IsDamageEvent(item) && item.Amount > 0)
            .Where(item => !partyIds.Contains(item.TargetId))
            .Select(item => (Item: item, Owner: FflogsEventNormalizer.ResolveOwnerActor(
                item.SourceId,
                fight.Actors)))
            .Where(static pair => pair.Owner is not null)
            .GroupBy(static pair => pair.Owner!.Id)
            .ToDictionary(static group => group.Key, static group => group.Sum(pair => pair.Item.Amount));
        foreach (var actor in fight.Party)
        {
            if (!fight.DamageTableActors.TryGetValue(actor.Id, out var reference))
            {
                continue;
            }
            var replay = replayTotals.GetValueOrDefault(actor.Id);
            if (replay != reference.RawDamage)
            {
                throw new InvalidDataException(
                    $"Cross-provider raw damage parity failed for {fight.Seed.ReportCode}:" +
                    $"{fight.Fight.Id} actor {actor.Name}: replay={replay}, FFLogs={reference.RawDamage}.");
            }
        }
    }

    private static IReadOnlyList<AttributionMatrixCell> BuildMatrix(
        IReadOnlyList<MatrixConstraintResult> constraints)
        => Enum.GetValues<OffensiveBuffDimension>()
            .SelectMany(dimension => Enum.GetValues<RecipientHitType>().Select(hitType =>
            {
                var scoped = constraints
                    .Where(item => item.BuffDimension == dimension && EventCount(item, hitType) > 0)
                    .ToArray();
                return new AttributionMatrixCell(
                    dimension,
                    hitType,
                    scoped.Length,
                    scoped.Select(static item => (item.Report, item.FightId)).Distinct().Count(),
                    scoped.Sum(item => EventCount(item, hitType)),
                    scoped.Sum(item => RawDamage(item, hitType)),
                    scoped.Select(static item => (item.ProviderJob, item.BuffStatusId)).Distinct().Count(),
                    scoped.Select(static item => item.RecipientJob).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    scoped.Select(static item => item.RecipientActor).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    scoped.Select(static item => item.EncounterId).Distinct().Count(),
                    string.Join("/", scoped.Select(static item => item.ProviderJob)
                        .Distinct(StringComparer.OrdinalIgnoreCase).Order()),
                    string.Join("/", scoped.Select(static item => item.RecipientJob)
                        .Distinct(StringComparer.OrdinalIgnoreCase).Order()),
                    scoped.Length == 0
                        ? "unavailable: no cached constraint"
                        : "FFLogs provider→recipient→buff aggregate; no per-hit-type reference",
                    GuaranteedHitCandidateMath.Definitions.ToDictionary(
                        static candidate => candidate.Name,
                        candidate => CalculateStatistics(scoped.Select(item =>
                            item.CandidateResiduals[candidate.Name]).ToArray()),
                        StringComparer.Ordinal));
            }))
            .ToArray();

    private static IReadOnlyList<MatchedActorGroup> BuildMatchedGroups(
        IReadOnlyList<MatrixConstraintResult> constraints)
    {
        var fightRows = constraints
            .Where(static item => item.BuffDimension != OffensiveBuffDimension.PercentageDamage)
            .GroupBy(static item => new
            {
                item.RecipientActor,
                item.RecipientActorId,
                item.RecipientJob,
                item.Partition,
                item.Report,
                item.FightId,
                item.EncounterId,
                item.Encounter,
                item.PartyComposition,
            })
            .Select(group =>
            {
                var values = group.ToArray();
                return new MatchedFightAggregate(
                    group.Key.RecipientActor,
                    group.Key.RecipientActorId,
                    group.Key.RecipientJob,
                    group.Key.Partition,
                    group.Key.Report,
                    group.Key.FightId,
                    group.Key.EncounterId,
                    group.Key.Encounter,
                    group.Key.PartyComposition,
                    string.Join(" + ", values.Select(static item => $"{item.ProviderJob}:{item.BuffName}")
                        .Distinct(StringComparer.OrdinalIgnoreCase).Order()),
                    WeightedAverage(values, static item => item.CriticalChanceProxy),
                    WeightedAverage(values, static item => item.DirectChanceProxy),
                    ResolveGroupHitType(values),
                    GuaranteedHitCandidateMath.Definitions.ToDictionary(
                        static candidate => candidate.Name,
                        candidate => values.Sum(item => item.CandidateResiduals[candidate.Name]),
                        StringComparer.Ordinal));
            })
            .ToArray();
        var verified = fightRows
            .GroupBy(static item => (item.Actor, item.ActorId, item.Job, item.Partition, item.Report))
            .Where(static group => group.Count() >= 2 &&
                                   group.Select(static item => item.RateComposition).Distinct().Count() >= 2 &&
                                   group.Any(static item => item.HitType != RecipientHitType.Normal))
            .Select(group => CreateMatchedGroup(
                group.ToArray(),
                identityVerified: true,
                "same report actor ID"))
            .ToArray();
        var verifiedActors = verified
            .Select(static item => (item.Actor, item.Job, item.Partition))
            .ToHashSet();
        var crossReportNameMatches = fightRows
            .GroupBy(static item => (item.Actor, item.Job, item.Partition))
            .Where(static group => group.Count() >= 2 &&
                                   group.Select(static item => item.RateComposition).Distinct().Count() >= 2 &&
                                   group.Any(static item => item.HitType != RecipientHitType.Normal))
            .Where(group => !verifiedActors.Contains(group.Key))
            .Select(group => CreateMatchedGroup(
                group.ToArray(),
                identityVerified: false,
                "cross-report actor-name match; server/world identity unavailable in the existing cache"))
            .ToArray();
        return verified
            .Concat(crossReportNameMatches)
            .OrderBy(static item => item.MatchQuality)
            .ThenByDescending(static item => item.Fights.Count)
            .ThenBy(static item => item.Actor, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static MatchedActorGroup CreateMatchedGroup(
        IReadOnlyList<MatchedFightAggregate> values,
        bool identityVerified,
        string identityReason)
    {
        var fights = values.OrderBy(static item => item.Report).ThenBy(static item => item.FightId).ToArray();
        var sameEncounter = fights.GroupBy(static item => item.EncounterId)
            .Any(static group => group.Count() >= 2);
        var criticalRange = fights.Max(static item => item.CriticalChanceProxy) -
                            fights.Min(static item => item.CriticalChanceProxy);
        var directRange = fights.Max(static item => item.DirectChanceProxy) -
                          fights.Min(static item => item.DirectChanceProxy);
        var quality = !identityVerified ? "C" :
            sameEncounter && criticalRange <= 0.02 && directRange <= 0.02 ? "A" :
            criticalRange <= 0.03 && directRange <= 0.03 ? "B" : "C";
        return new MatchedActorGroup(
            fights[0].Actor,
            fights[0].Job,
            fights[0].Partition,
            quality,
            $"{identityReason}; sameEncounter={sameEncounter}; Cu range={criticalRange:P2}; Du range={directRange:P2}",
            fights.Select(static item => item.HitType).Max(),
            fights.Select(static item => new MatchedActorFight(
                item.Report,
                item.FightId,
                item.EncounterId,
                item.Encounter,
                item.PartyComposition,
                item.RateComposition,
                item.CriticalChanceProxy,
                item.DirectChanceProxy,
                item.CandidateResiduals)).ToArray(),
            string.Join(" || ", fights.Select(static item => item.RateComposition).Distinct().Order()),
            GuaranteedHitCandidateMath.Definitions.ToDictionary(
                static candidate => candidate.Name,
                candidate =>
                {
                    var residuals = fights.Select(item => item.CandidateResiduals[candidate.Name]).ToArray();
                    return residuals.Max() - residuals.Min();
                },
                StringComparer.Ordinal));
    }

    private static IReadOnlyList<AttributionMatrixCandidateRanking> BuildCandidateRankings(
        IReadOnlyList<MatrixConstraintResult> constraints,
        IReadOnlyList<MatchedActorGroup> matched)
    {
        var rateConstraints = constraints
            .Where(static item => item.BuffDimension != OffensiveBuffDimension.PercentageDamage)
            .ToArray();
        var matchedFights = matched.Where(static item => item.MatchQuality is "A" or "B")
            .SelectMany(group => group.Fights.Select(fight =>
                (group.Actor, group.Job, fight.Report, fight.FightId)))
            .ToHashSet();
        return GuaranteedHitCandidateMath.Definitions.Select(candidate =>
        {
            var scopes = new List<MatrixCandidateScopeResult>();
            foreach (var group in rateConstraints.GroupBy(static item => item.BuffDimension.ToString()))
            {
                scopes.Add(Scope(candidate.Name, "buffDimension", group.Key, group));
            }
            foreach (var group in rateConstraints.GroupBy(static item => item.ProviderJob))
            {
                scopes.Add(Scope(candidate.Name, "provider", group.Key, group));
            }
            foreach (var hitType in Enum.GetValues<RecipientHitType>().Where(static hit => hit != RecipientHitType.Normal))
            {
                scopes.Add(Scope(candidate.Name, "hitType", hitType.ToString(),
                    rateConstraints.Where(item => EventCount(item, hitType) > 0)));
            }
            foreach (var group in rateConstraints
                         .Where(static item => item.GuaranteedCriticalEventCount > 0 ||
                                               item.GuaranteedDirectEventCount > 0 ||
                                               item.GuaranteedCriticalDirectEventCount > 0)
                         .SelectMany(item => Enum.GetValues<RecipientHitType>()
                             .Where(hit => hit != RecipientHitType.Normal && EventCount(item, hit) > 0)
                             .Select(hit => (Item: item, Hit: hit)))
                         .GroupBy(static pair => $"{pair.Item.ProviderJob}/{pair.Hit}"))
            {
                scopes.Add(Scope(candidate.Name, "providerHitType", group.Key,
                    group.Select(static pair => pair.Item)));
            }
            scopes.Add(Scope(candidate.Name, "matchQuality", "A/B same-actor",
                rateConstraints.Where(item => matchedFights.Contains(
                    (item.RecipientActor, item.RecipientJob, item.Report, item.FightId)))));
            var overall = CalculateStatistics(rateConstraints.Select(item =>
                item.CandidateResiduals[candidate.Name]).ToArray());
            return new AttributionMatrixCandidateRanking(
                candidate.Name,
                overall,
                scopes,
                "Diagnostic only",
                "Acceptance requires stable provider-, hit-type-, and A/B matched evidence; aggregate MAE alone is insufficient.");
        }).OrderBy(static item => item.Overall.MeanAbsoluteResidual).ToArray();
    }

    private static MatrixCandidateScopeResult Scope(
        string candidate,
        string dimension,
        string value,
        IEnumerable<MatrixConstraintResult> constraints)
        => new(
            candidate,
            dimension,
            value,
            CalculateStatistics(constraints.Select(item => item.CandidateResiduals[candidate]).ToArray()));

    private static IReadOnlyList<string> BuildRejectedCandidates(
        IReadOnlyList<AttributionMatrixCandidateRanking> rankings)
    {
        if (rankings.Count == 0)
        {
            return [];
        }
        var current = rankings.SingleOrDefault(static item =>
            item.Candidate == GuaranteedHitCandidateMath.CurrentProduction);
        return current is null
            ? []
            :
            [
                $"CurrentProduction as a universal guaranteed-Crit rule remains ruled out by the prior independent 75-fight DNC→SAM aggregate experiment; this matrix reports its cross-provider MAE {current.Overall.MeanAbsoluteResidual:F1} but does not back-solve a replacement.",
                "No additional hidden-rate candidate is conclusively eliminated here: percentage-damage control is not at parity and FFLogs exposes only whole provider→recipient aggregates, not a per-hit-type split.",
            ];
    }

    private static PercentageControlResult BuildPercentageControl(
        IReadOnlyList<MatrixConstraintResult> constraints)
    {
        var statistics = CalculateStatistics(constraints
            .Where(static item => item.BuffDimension == OffensiveBuffDimension.PercentageDamage)
            .Select(item => item.CandidateResiduals[GuaranteedHitCandidateMath.ObservedHitRegular])
            .ToArray());
        var passed = statistics.ConstraintCount > 0 &&
                     statistics.MaximumAbsoluteResidual <= 0.05;
        return new PercentageControlResult(
            passed ? "PASS" : "FAIL",
            statistics,
            passed
                ? "Every fixed-magnitude percentage-buff aggregate matches at one-decimal display precision."
                : "Fixed-magnitude percentage-buff aggregates do not match FFLogs. Hidden rate-equation acceptance is blocked until the common percentage/event attribution path is reconciled; variable-rank Radiant Finale is excluded.");
    }

    private static (string Status, string Reason) ResolveEquationStatus(
        IReadOnlyList<AttributionMatrixCell> matrix,
        IReadOnlyList<AttributionMatrixCandidateRanking> rankings,
        IReadOnlyList<MatchedActorGroup> matched,
        PercentageControlResult percentageControl)
    {
        var hasGuaranteedDirect = matrix.Any(static item =>
            item.HitType == RecipientHitType.GuaranteedDirectHit && item.EventCount > 0);
        var hasGuaranteedCdhDh = matrix.Any(static item =>
            item.HitType == RecipientHitType.GuaranteedCriticalDirectHit &&
            item.BuffDimension == OffensiveBuffDimension.DirectHitRate && item.EventCount > 0);
        var strongMatches = matched.Count(static item => item.MatchQuality is "A" or "B");
        if (percentageControl.Status != "PASS")
        {
            return ("Not determined",
                $"Percentage-damage control failed (N={percentageControl.PublishedMathStatistics.ConstraintCount}, " +
                $"MAE={percentageControl.PublishedMathStatistics.MeanAbsoluteResidual:F1}, " +
                $"max={percentageControl.PublishedMathStatistics.MaximumAbsoluteResidual:F1}). " +
                "The hidden guaranteed-rate branch cannot be accepted while its control path is not at parity.");
        }
        if (!hasGuaranteedDirect)
        {
            return ("Not determined",
                $"Current PvE exposes no guaranteed-DH-only action; G-CDH under DH-rate exists={hasGuaranteedCdhDh}, A/B matched groups={strongMatches}. Crit symmetry cannot prove the DH-only branch.");
        }
        return ("Not determined",
            "No candidate is promoted solely from aggregate constraints; cross-scope residual structure still requires manual review.");
    }

    private static IReadOnlyList<string> BuildCrossProviderFindings(
        IReadOnlyList<MatrixConstraintResult> constraints,
        IReadOnlyList<AttributionMatrixCandidateRanking> rankings)
    {
        var providers = constraints
            .Where(static item => item.BuffDimension != OffensiveBuffDimension.PercentageDamage)
            .Select(static item => $"{item.ProviderJob}:{item.BuffName}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order()
            .ToArray();
        var best = rankings.FirstOrDefault();
        var findings = new List<string>
        {
            $"Independent rate providers represented: {string.Join(", ", providers)}.",
            best is null
                ? "No rate reference was available."
                : $"Lowest aggregate MAE is {best.Candidate} ({best.Overall.MeanAbsoluteResidual:F1} damage), but this is not an acceptance decision.",
            "Provider identity and buff dimension are reported separately; SAM appears only as a recipient/damage actor.",
        };
        var targeted = constraints
            .Where(static item => item.SourceCache == "targeted-matrix-cache")
            .ToArray();
        if (targeted.Length > 0)
        {
            findings.Add(
                $"Targeted mining added {targeted.Select(static item => (item.Report, item.FightId)).Distinct().Count()} BRD fights, " +
                $"{targeted.Where(static item => item.GuaranteedCriticalDirectEventCount > 0).Select(static item => item.RecipientActor).Distinct(StringComparer.OrdinalIgnoreCase).Count()} G-CDH actors, " +
                $"and {targeted.Where(static item => item.GuaranteedCriticalDirectEventCount > 0).Select(static item => item.RecipientJob).Distinct(StringComparer.OrdinalIgnoreCase).Count()} G-CDH recipient jobs; no random DNC expansion was performed.");
        }
        var percentageGroups = constraints
            .Where(static item => item.BuffDimension == OffensiveBuffDimension.PercentageDamage)
            .GroupBy(static item => item.BuffName)
            .Select(group => new
            {
                Buff = group.Key,
                Mean = group.Average(item =>
                    item.CandidateResiduals[GuaranteedHitCandidateMath.ObservedHitRegular]),
            })
            .OrderByDescending(static item => Math.Abs(item.Mean))
            .Take(3)
            .ToArray();
        findings.Add(
            "Percentage control has cross-provider structure, not a DNC-only failure: " +
            string.Join(", ", percentageGroups.Select(static item =>
                $"{item.Buff} mean={item.Mean:+0.0;-0.0;0.0}")) + ".");
        foreach (var provider in constraints
                     .Where(static item => item.BuffDimension != OffensiveBuffDimension.PercentageDamage)
                     .GroupBy(static item => item.ProviderJob)
                     .OrderBy(static group => group.Key))
        {
            var winner = rankings
                .Select(ranking => ranking.Scopes.Single(scope =>
                    scope.ScopeDimension == "provider" && scope.ScopeValue == provider.Key))
                .OrderBy(static scope => scope.Statistics.MeanAbsoluteResidual)
                .First();
            findings.Add(
                $"{provider.Key}: diagnostic minimum is {winner.Candidate}, " +
                $"N={winner.Statistics.ConstraintCount}, MAE={winner.Statistics.MeanAbsoluteResidual:F1}. " +
                "Different provider minima are evidence against promoting an aggregate-only winner.");
        }
        findings.Add(
            "Provider identity is not proven to be a mathematical input: provider groups also differ in recipient jobs, hit mix, self-rate exposure, and percentage-control residual. The current data therefore supports neither a provider-specific branch nor a provider-independent universal equation.");
        return findings;
    }

    private static IReadOnlyList<string> BuildRemainingUnknowns(IReadOnlyList<AttributionMatrixCell> matrix)
    {
        var result = new List<string>
        {
            "FFLogs actual Cu/Du and per-event contribution remain unavailable from the public API.",
            "The exact FFLogs guaranteed Crit/DH/CDH attribution branch is not publicly documented.",
            "FFLogs treatment of self-sourced rate effects in allocation denominators remains undocumented.",
        };
        foreach (var cell in matrix.Where(static item => item.ConstraintCount == 0))
        {
            result.Add($"Empty matrix cell: {cell.BuffDimension} × {cell.HitType}.");
        }
        return result;
    }

    private static IReadOnlyList<string> BuildMinimumDataNeeds(
        IReadOnlyList<AttributionMatrixCell> matrix,
        IReadOnlyList<MatchedActorGroup> matched,
        PercentageControlResult percentageControl)
    {
        var needs = new List<string>();
        if (percentageControl.Status != "PASS")
        {
            needs.Add(
                "Before any new log collection: reconcile the fixed-magnitude percentage-damage control to display-precision parity. New guaranteed-hit logs cannot distinguish a hidden rate equation from a shared event/window/percentage-normalization error.");
        }
        if (!GuaranteedHitRegistry.HasGuaranteedDirectOnly)
        {
            needs.Add("Guaranteed DH only: no current authoritative PvE action exists, so live Patch 7.5 logs cannot identify this column; a future action or an FFLogs-authored synthetic fixture is required.");
        }
        var dhCdh = matrix.Single(item =>
            item.BuffDimension == OffensiveBuffDimension.DirectHitRate &&
            item.HitType == RecipientHitType.GuaranteedCriticalDirectHit);
        if (dhCdh.FightCount < 5)
        {
            needs.Add($"DH-only provider → G-CDH recipient: need {Math.Max(0, 5 - dhCdh.FightCount)} more targeted BRD fights with WAR/MCH/PCT/DNC G-CDH events, preferably 2 same-actor pairs, to distinguish Crit/DH-separate from combined-multiplier candidates.");
        }
        if (matched.Count(static item => item.MatchQuality is "A" or "B") < 5)
        {
            needs.Add("Matched evidence: target only enough public fights to reach five A/B same-actor groups with different rate composition; do not expand random DNC samples.");
        }
        if (!matched.Any(static item =>
                item.MatchQuality is "A" or "B" &&
                item.BuffDifference.Contains("Bard:", StringComparison.OrdinalIgnoreCase) &&
                (item.BuffDifference.Contains("Dancer:", StringComparison.OrdinalIgnoreCase) ||
                 item.BuffDifference.Contains("Scholar:", StringComparison.OrdinalIgnoreCase) ||
                 item.BuffDifference.Contains("Dragoon:", StringComparison.OrdinalIgnoreCase))))
        {
            needs.Add(
                "After the control passes: 2–3 same-actor PCT/WAR/DNC groups with a BRD DH-only fight paired against DNC Crit+DH or SCH/DRG Crit-only composition. This directly separates dimension-wise DH/CDH allocation from a combined guaranteed multiplier; the two cached 百合咲 PCT fights both have the same BRD+SCH rate composition and are not discriminating pairs.");
        }
        return needs;
    }

    private static MatrixResidualStatistics CalculateStatistics(IReadOnlyList<double> residuals)
    {
        if (residuals.Count == 0)
        {
            return new MatrixResidualStatistics(0, 0, 0, 0, 0, 0, 0, 0, 0);
        }
        var ordered = residuals.Order().ToArray();
        var median = ordered.Length % 2 == 1
            ? ordered[ordered.Length / 2]
            : (ordered[(ordered.Length / 2) - 1] + ordered[ordered.Length / 2]) / 2;
        return new MatrixResidualStatistics(
            ordered.Length,
            ordered.Average(),
            median,
            ordered.Average(Math.Abs),
            Math.Sqrt(ordered.Average(static value => value * value)),
            ordered.Max(value => Math.Abs(value)),
            ordered.Count(static value => value < -0.000_001),
            ordered.Count(static value => Math.Abs(value) <= 0.000_001),
            ordered.Count(static value => value > 0.000_001));
    }

    private static int EventCount(MatrixConstraintResult item, RecipientHitType hitType)
        => hitType switch
        {
            RecipientHitType.Normal => item.NormalEventCount,
            RecipientHitType.GuaranteedCritical => item.GuaranteedCriticalEventCount,
            RecipientHitType.GuaranteedDirectHit => item.GuaranteedDirectEventCount,
            RecipientHitType.GuaranteedCriticalDirectHit => item.GuaranteedCriticalDirectEventCount,
            _ => 0,
        };

    private static long RawDamage(MatrixConstraintResult item, RecipientHitType hitType)
        => hitType switch
        {
            RecipientHitType.Normal => item.NormalRawDamage,
            RecipientHitType.GuaranteedCritical => item.GuaranteedCriticalRawDamage,
            RecipientHitType.GuaranteedDirectHit => item.GuaranteedDirectRawDamage,
            RecipientHitType.GuaranteedCriticalDirectHit => item.GuaranteedCriticalDirectRawDamage,
            _ => 0,
        };

    private static RecipientHitType ResolveGroupHitType(IReadOnlyList<MatrixConstraintResult> values)
        => values.Sum(static item => item.GuaranteedCriticalDirectEventCount) > 0
            ? RecipientHitType.GuaranteedCriticalDirectHit
            : values.Sum(static item => item.GuaranteedDirectEventCount) > 0
                ? RecipientHitType.GuaranteedDirectHit
                : values.Sum(static item => item.GuaranteedCriticalEventCount) > 0
                    ? RecipientHitType.GuaranteedCritical
                    : RecipientHitType.Normal;

    private static double WeightedAverage(
        IReadOnlyList<MatrixConstraintResult> values,
        Func<MatrixConstraintResult, double> selector)
    {
        var weight = values.Sum(static item => item.RawDamage);
        return weight > 0
            ? values.Sum(item => selector(item) * item.RawDamage) / weight
            : values.Select(selector).DefaultIfEmpty().Average();
    }

    private readonly record struct ConstraintKey(int RecipientId, int ProviderId, long StatusId);

    private sealed class ConstraintAccumulator(
        FflogsActor recipient,
        MatrixBuffExposureEntry providerBuff)
    {
        private readonly Dictionary<string, double> candidateTotals =
            GuaranteedHitCandidateMath.Definitions.ToDictionary(
                static item => item.Name,
                static _ => 0d,
                StringComparer.Ordinal);
        private readonly HashSet<string> rateComposition = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> warnings = [];
        private double criticalWeighted;
        private double directWeighted;

        public FflogsActor Recipient { get; } = recipient;

        public MatrixBuffExposureEntry ProviderBuff { get; } = providerBuff;

        public int EventCount { get; private set; }

        public long RawDamage { get; private set; }

        public int NormalEventCount { get; private set; }

        public long NormalRawDamage { get; private set; }

        public int GuaranteedCriticalEventCount { get; private set; }

        public long GuaranteedCriticalRawDamage { get; private set; }

        public int GuaranteedDirectEventCount { get; private set; }

        public long GuaranteedDirectRawDamage { get; private set; }

        public int GuaranteedCriticalDirectEventCount { get; private set; }

        public long GuaranteedCriticalDirectRawDamage { get; private set; }

        public void ObserveCoverage(
            NormalizedFflogsEvent item,
            ProbeGuaranteedDimensions dimensions,
            HitBaselineSnapshot baseline,
            MatrixEventAttributionState state)
        {
            EventCount++;
            RawDamage += item.Amount;
            criticalWeighted += baseline.CriticalChance * item.Amount;
            directWeighted += baseline.DirectHitChance * item.Amount;
            foreach (var buff in state.RateBuffs.Where(static buff => !buff.IsSelfSourced))
            {
                rateComposition.Add($"{buff.SourceJob}:{buff.Definition.ActionName}:{buff.Definition.Magnitude}");
            }
            switch (dimensions)
            {
                case ProbeGuaranteedDimensions.Critical:
                    GuaranteedCriticalEventCount++;
                    GuaranteedCriticalRawDamage += item.Amount;
                    break;
                case ProbeGuaranteedDimensions.DirectHit:
                    GuaranteedDirectEventCount++;
                    GuaranteedDirectRawDamage += item.Amount;
                    break;
                case ProbeGuaranteedDimensions.Critical | ProbeGuaranteedDimensions.DirectHit:
                    GuaranteedCriticalDirectEventCount++;
                    GuaranteedCriticalDirectRawDamage += item.Amount;
                    break;
                default:
                    NormalEventCount++;
                    NormalRawDamage += item.Amount;
                    break;
            }
        }

        public void AddCandidate(string candidate, double contribution)
            => candidateTotals[candidate] += contribution;

        public MatrixConstraintResult Build(
            NormalizedAttributionFight fight,
            double reference,
            string sourceCache)
        {
            var residuals = candidateTotals.ToDictionary(
                static pair => pair.Key,
                pair => pair.Value - reference,
                StringComparer.Ordinal);
            return new MatrixConstraintResult(
                fight.Seed.ReportCode,
                fight.Fight.Id,
                fight.Fight.EncounterId,
                fight.Fight.Name,
                fight.Partition,
                fight.PartyComposition,
                ProviderBuff.SourceActorId,
                ProviderBuff.SourceActor,
                ProviderBuff.SourceJob,
                Recipient.Id,
                Recipient.Name,
                ToJobAbbreviation(Recipient.Job),
                ProviderBuff.Definition.StatusId,
                ProviderBuff.Definition.ActionName,
                ProviderBuff.Definition.Dimension,
                ProviderBuff.Definition.Magnitude,
                reference,
                "available: FFLogs DamageDone recipient taken[] aggregate",
                EventCount,
                RawDamage,
                NormalEventCount,
                NormalRawDamage,
                GuaranteedCriticalEventCount,
                GuaranteedCriticalRawDamage,
                GuaranteedDirectEventCount,
                GuaranteedDirectRawDamage,
                GuaranteedCriticalDirectEventCount,
                GuaranteedCriticalDirectRawDamage,
                RawDamage > 0 ? criticalWeighted / RawDamage : 0,
                RawDamage > 0 ? directWeighted / RawDamage : 0,
                new Dictionary<string, double>(candidateTotals, StringComparer.Ordinal),
                residuals,
                rateComposition.Order().ToArray(),
                sourceCache,
                warnings.Concat(fight.NormalizationWarnings)
                    .Distinct(StringComparer.Ordinal)
                    .Order()
                    .ToArray());
        }
    }

    private sealed record MatchedFightAggregate(
        string Actor,
        int ActorId,
        string Job,
        string Partition,
        string Report,
        int FightId,
        int EncounterId,
        string Encounter,
        string PartyComposition,
        string RateComposition,
        double CriticalChanceProxy,
        double DirectChanceProxy,
        RecipientHitType HitType,
        IReadOnlyDictionary<string, double> CandidateResiduals);

    private static string ToJobAbbreviation(string job)
        => job switch
        {
            "DarkKnight" => "DRK", "Gunbreaker" => "GNB", "Paladin" => "PLD", "Warrior" => "WAR",
            "WhiteMage" => "WHM", "Scholar" => "SCH", "Astrologian" => "AST", "Sage" => "SGE",
            "Monk" => "MNK", "Dragoon" => "DRG", "Ninja" => "NIN", "Samurai" => "SAM",
            "Reaper" => "RPR", "Viper" => "VPR", "Bard" => "BRD", "Machinist" => "MCH",
            "Dancer" => "DNC", "BlackMage" => "BLM", "Summoner" => "SMN", "RedMage" => "RDM",
            "Pictomancer" => "PCT", _ => job.ToUpperInvariant(),
        };
}

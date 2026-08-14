using DalamudActCompat.ActRuntime;

namespace DalamudActCompat.FflogsParityHarness;

internal static class GuaranteedHitAttributionExperiment
{
    private const int SelectedSamCount = 30;
    private const string PureDrgScope = "Pure DRG guaranteed-owner fights";
    private static readonly IReadOnlyDictionary<string, long[]> ActionFamilies =
        new Dictionary<string, long[]>(StringComparer.Ordinal)
        {
            ["Midare"] = [0x1D3F],
            ["Tendo Setsugekka"] = [0x9066],
            ["Tendo Kaeshi Setsugekka"] = [0x9068],
            ["Ogi Namikiri"] = [0x64B5],
            ["Kaeshi: Namikiri"] = [0x64B6],
            ["Kaeshi: Setsugekka"] = [0x4066],
        };

    public static GuaranteedHitAttributionExperimentReport Run(
        FflogsSampleCollector collector,
        CacheManifest manifest)
    {
        var stableGuarantees = ProductionGuaranteedMetadata.ReadStableActions();
        var analyses = new List<AnalyzedFight>(manifest.Seeds.Count);
        foreach (var seed in manifest.Seeds)
        {
            var fight = FflogsEventNormalizer.Normalize(collector.ReadCachedSample(seed));
            analyses.Add(AnalyzeFight(fight, stableGuarantees));
            if (analyses.Count % 10 == 0 || analyses.Count == manifest.Seeds.Count)
            {
                Console.WriteLine($"Guaranteed-hit experiment analyzed {analyses.Count}/{manifest.Seeds.Count} fights.");
            }
        }

        var primarySam = analyses
            .Where(static analysis => analysis.Parity.DancePartnerJob == "SAM" &&
                                      analysis.GuaranteedEvents.Count > 0)
            .ToArray();
        var mixedSam = primarySam.Where(static analysis =>
            !HasOnlyGuaranteedOwnerJob(analysis, "Samurai")).ToArray();
        if (mixedSam.Length > 0)
        {
            // FFLogs exposes only fight-level given.total. A mixed guaranteed-owner fight
            // cannot be decomposed into a trustworthy SAM equation target.
            throw new InvalidDataException(
                $"{mixedSam.Length} primary-SAM fights contain non-SAM guaranteed owners; " +
                "exclude or classify those partner switches before running the experiment.");
        }
        var eligibleSam = primarySam;
        var selected = eligibleSam
            .OrderByDescending(static analysis => analysis.GuaranteedRawDamage)
            .ThenByDescending(static analysis => analysis.GuaranteedDamageShare)
            .ThenBy(static analysis => analysis.Fight.Seed.ReportCode, StringComparer.Ordinal)
            .Take(SelectedSamCount)
            .ToArray();
        if (selected.Length < 20)
        {
            throw new InvalidDataException(
                $"Only {selected.Length} cached SAM fights contain guaranteed actions under Devilment; at least 20 are required.");
        }

        var calibration = BuildCalibration(analyses);
        if (!calibration.Passed)
        {
            var mismatch = analyses
                .SelectMany(analysis => analysis.GuaranteedEvents.Select(item => new
                {
                    analysis.Fight.Seed.ReportCode,
                    analysis.Fight.Fight.Id,
                    Event = item,
                    Residual = item.CandidateContributions[GuaranteedHitCandidateMath.CurrentProduction] -
                               item.ProductionContribution,
                }))
                .OrderByDescending(static item => Math.Abs(item.Residual))
                .First();
            throw new InvalidOperationException(
                $"CurrentProduction offline calibration failed: event={calibration.MaximumAbsoluteEventResidual:R}, " +
                $"fight={calibration.MaximumAbsoluteFightResidual:R}; max at " +
                $"{mismatch.ReportCode}:{mismatch.Id} t={mismatch.Event.Timestamp:R} " +
                $"action={mismatch.Event.ActionName}/{mismatch.Event.ActionId} " +
                $"production={mismatch.Event.ProductionContribution:R} " +
                $"offline={mismatch.Event.CandidateContributions[GuaranteedHitCandidateMath.CurrentProduction]:R}.");
        }

        var fightResults = BuildFightResults(selected);
        var rankings = BuildRankings(selected);
        var best = ResolveBestCanonicalCandidate(eligibleSam);
        var residualDecomposition = BuildResidualDecomposition(eligibleSam, selected);
        var candidateScopeValidation = BuildCandidateScopeValidation(
            analyses,
            eligibleSam,
            selected);
        var (equationStatus, equationReason) = ResolveEquationStatus(
            eligibleSam,
            selected,
            candidateScopeValidation,
            best);
        return new GuaranteedHitAttributionExperimentReport(
            DateTimeOffset.UtcNow,
            analyses.Count,
            eligibleSam.Length,
            selected.Length,
            selected.Select(static analysis => analysis.Parity.Actor).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            selected.Select(static analysis => analysis.Parity.EncounterId).Distinct().Count(),
            GuaranteedHitCandidateMath.Definitions,
            calibration,
            selected.Select(BuildSelection).ToArray(),
            fightResults,
            rankings,
            BuildCohortValidation(eligibleSam, selected),
            BuildActionFamilyValidation(selected),
            BuildBuffConditionValidation(selected),
            BuildDimensionEvidence(analyses),
            best,
            equationStatus,
            equationReason,
            BuildFullReplay(analyses, best),
            BuildRemainingUnknowns(analyses),
            [
                "FFLogs public API supplies Devilment given.total only; no per-action or per-window FFLogs contribution is fabricated or back-solved.",
                "Candidate totals keep production's measured regular-action contribution fixed and replace only production-identified guaranteed-event contribution.",
                "Cu/Du are DACT observation-inference snapshots immediately before each event; FFLogs actual Cu/Du are unavailable from the public API.",
                "The selected SAM set is the 30 highest guaranteed-damage fights from the existing 100-sample cache; this mode performs no network request.",
                "A low aggregate residual can reject candidates but cannot by itself prove a hidden per-action equation when independent FFLogs per-action truth is unavailable.",
                "Production and the probe identify self-source differently (normalized actor name versus actor ID); the calibrated cache shows no resulting contribution drift, but the identity distinction remains an audit caveat.",
                "Army's Paeon and the Wanderer's Minuet are absent from production rate metadata. The cache contains one BRD-party/MNK-partner fight and no BRD party in the 61 SAM fights, so this coverage boundary cannot explain the SAM split.",
                "All 61 primary-SAM fights pass a pure guaranteed-owner invariant. One primary-DRG fight switches to SAM and is excluded only from the pure-DRG component scope.",
                "Fight-level rate-exposure fractions are weighted by guaranteed raw damage; they are not elapsed-time or event-count fractions.",
                "The probe status timeline uses half-open millisecond intervals. CurrentProduction calibration covers the external guaranteed path, but same-timestamp apply/remove ordering for newly exposed self rates is not independently calibrated.",
            ],
            residualDecomposition,
            candidateScopeValidation,
            BuildActorAnalysis(eligibleSam),
            BuildActorStability(eligibleSam),
            BuildCohortFeatureDistributions(eligibleSam, selected),
            BuildCohortCategoryDistributions(eligibleSam, selected),
            BuildPartialCorrelations(eligibleSam, selected),
            BuildAllCandidateCounterfactuals(analyses),
            BuildRateBuffDenominatorAudit(),
            BuildAcceptanceChecks(analyses, eligibleSam, selected),
            BuildResidualFindings(eligibleSam, selected));
    }

    private static AnalyzedFight AnalyzeFight(
        NormalizedFight fight,
        IReadOnlyDictionary<long, ProbeGuaranteedDimensions> stableGuarantees)
    {
        // Guaranteed equation identification remains paused and must not absorb the
        // separately chosen cross-component ownership model. Keep this diagnostic on
        // its calibrated PercentageFirst isolation boundary.
        var parity = DactRdpsReplay.Replay(fight, RaidDpsOwnershipModel.PercentageFirst);
        var timeline = new FightAttributionTimeline(fight);
        var estimator = new RaidDpsEstimator(
            timeline.LifeSurgeWeaponskillActionIds.Contains,
            RaidDpsOwnershipModel.PercentageFirst);
        estimator.Reset();
        var encounterStart = DactRdpsReplay.ToTimestamp(fight.ReportStartTime, fight.Fight.StartTime);
        foreach (var actor in fight.Actors.Values)
        {
            estimator.ObserveNetworkLine(encounterStart, DactRdpsReplay.BuildActorLine(actor));
        }
        estimator.StartEncounter(encounterStart);

        var partyIds = fight.Party.Select(static actor => actor.Id).ToHashSet();
        var targetIds = timeline.DevilmentTargetIds;
        var guaranteedEvents = new List<GuaranteedEventAnalysis>();
        long devilmentWindowRawDamage = 0;
        long partnerTotalRawDamage = 0;
        foreach (var item in DactRdpsReplay.OrderEventsForAttribution(fight.Events))
        {
            var timestamp = DactRdpsReplay.ToTimestamp(fight.ReportStartTime, item.Timestamp);
            if (FflogsEventNormalizer.IsStatusApply(item.Type))
            {
                if (item.AbilityId == 0x71E &&
                    DactRdpsReplay.TryResolveTechnicalFinishAction(fight, item, out var technicalAction))
                {
                    estimator.ObserveNetworkLine(
                        DactRdpsReplay.ToTimestamp(fight.ReportStartTime, technicalAction.Timestamp),
                        DactRdpsReplay.BuildActionLine(technicalAction, fight.Actors));
                }
                estimator.ObserveStatusLine(timestamp, DactRdpsReplay.BuildStatusLine(item, fight, remove: false));
                continue;
            }
            if (FflogsEventNormalizer.IsStatusRemove(item.Type))
            {
                estimator.ObserveStatusLine(timestamp, DactRdpsReplay.BuildStatusLine(item, fight, remove: true));
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

            if (targetIds.Contains(owner.Id))
            {
                partnerTotalRawDamage += item.Amount;
            }

            var baseline = estimator.ResolveHitBaseline(owner.Name);
            var beforeCritical = estimator.ResolveContributedDamage(
                fight.Dancer.Name,
                RaidDpsEstimator.AttributionKind.Critical);
            var beforeDirect = estimator.ResolveContributedDamage(
                fight.Dancer.Name,
                RaidDpsEstimator.AttributionKind.DirectHit);
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
            var productionContribution =
                estimator.ResolveContributedDamage(
                    fight.Dancer.Name,
                    RaidDpsEstimator.AttributionKind.Critical) - beforeCritical +
                estimator.ResolveContributedDamage(
                    fight.Dancer.Name,
                    RaidDpsEstimator.AttributionKind.DirectHit) - beforeDirect;
            var state = timeline.Resolve(item, owner);
            var rateExposure = timeline.ResolveRateBuffExposure(item, owner);
            if (targetIds.Contains(owner.Id) &&
                (state.DevilmentCriticalIncrease > 0 || state.DevilmentDirectIncrease > 0))
            {
                devilmentWindowRawDamage += item.Amount;
            }

            var dimensions = stableGuarantees.GetValueOrDefault(item.AbilityId);
            var sourceKind = dimensions != ProbeGuaranteedDimensions.None
                ? "stable action metadata"
                : string.Empty;
            if (dimensions == ProbeGuaranteedDimensions.None &&
                timeline.TryResolveReassembleAction(item, out var reassembleDimensions))
            {
                dimensions = reassembleDimensions;
                sourceKind = "Reassemble contextual status";
            }
            if (dimensions == ProbeGuaranteedDimensions.None &&
                timeline.TryResolveLifeSurgeAction(item, out var lifeSurgeDimensions))
            {
                dimensions = lifeSurgeDimensions;
                sourceKind = "Life Surge contextual status";
            }
            if (dimensions == ProbeGuaranteedDimensions.None ||
                (state.DevilmentCriticalIncrease <= 0 && state.DevilmentDirectIncrease <= 0))
            {
                continue;
            }

            var input = new GuaranteedHitCandidateInput(
                item.Amount / Math.Max(1, state.PercentageMultiplier),
                item.Critical,
                item.DirectHit,
                baseline.CriticalChance,
                baseline.DirectHitChance,
                state.CriticalIncrease,
                state.DirectIncrease,
                state.DevilmentCriticalIncrease,
                state.DevilmentDirectIncrease,
                dimensions,
                rateExposure.SelfCriticalRateIncrease,
                rateExposure.SelfDirectRateIncrease);
            var candidateContributions = GuaranteedHitCandidateMath.Definitions.ToDictionary(
                static definition => definition.Name,
                definition =>
                {
                    var calculated = GuaranteedHitCandidateMath.Calculate(definition.Name, input);
                    return calculated.Critical + calculated.Direct;
                },
                StringComparer.Ordinal);
            guaranteedEvents.Add(new GuaranteedEventAnalysis(
                item.Timestamp,
                item.AbilityId,
                item.AbilityName,
                owner.Name,
                owner.Job,
                item.Amount,
                dimensions,
                sourceKind,
                productionContribution,
                candidateContributions,
                baseline.CriticalChance,
                baseline.DirectHitChance,
                state.RateBuffIds,
                rateExposure));
        }

        estimator.FinishEncounter();
        var productionGuaranteed = guaranteedEvents.Sum(static item => item.ProductionContribution);
        var totals = GuaranteedHitCandidateMath.Definitions.ToDictionary(
            static definition => definition.Name,
            definition => parity.DevilmentContribution - productionGuaranteed +
                          guaranteedEvents.Sum(item => item.CandidateContributions[definition.Name]),
            StringComparer.Ordinal);
        var guaranteedRaw = guaranteedEvents.Sum(static item => item.RawDamage);
        var overlapCount = guaranteedEvents.Count(static item =>
            item.RateBuffIds.Any(static id => id != 0x721));
        return new AnalyzedFight(
            fight,
            parity,
            guaranteedEvents,
            totals,
            guaranteedRaw,
            guaranteedEvents.Count,
            partnerTotalRawDamage,
            devilmentWindowRawDamage > 0 ? (double)guaranteedRaw / devilmentWindowRawDamage : 0,
            WeightedAverage(guaranteedEvents, static item => item.CriticalChanceProxy),
            WeightedAverage(guaranteedEvents, static item => item.DirectChanceProxy),
            guaranteedEvents.Count > 0 ? (double)overlapCount / guaranteedEvents.Count : 0,
            ResolveBuffConditions(guaranteedEvents));
    }

    private static GuaranteedHitCalibrationResult BuildCalibration(IReadOnlyList<AnalyzedFight> analyses)
    {
        var eventResiduals = analyses
            .SelectMany(static analysis => analysis.GuaranteedEvents)
            .Select(item => item.CandidateContributions[GuaranteedHitCandidateMath.CurrentProduction] -
                           item.ProductionContribution)
            .ToArray();
        var fightResiduals = analyses
            .Select(analysis => analysis.CandidateTotals[GuaranteedHitCandidateMath.CurrentProduction] -
                               analysis.Parity.DevilmentContribution)
            .ToArray();
        var maximumEvent = eventResiduals.Select(Math.Abs).DefaultIfEmpty().Max();
        var maximumFight = fightResiduals.Select(Math.Abs).DefaultIfEmpty().Max();
        return new GuaranteedHitCalibrationResult(
            eventResiduals.Length,
            maximumEvent,
            maximumFight,
            maximumEvent <= 0.000_001 && maximumFight <= 0.000_001);
    }

    private static GuaranteedHitExperimentSelection BuildSelection(AnalyzedFight analysis)
        => new(
            analysis.Fight.Seed.ReportCode,
            analysis.Fight.Fight.Id,
            analysis.Parity.Actor,
            analysis.Parity.Encounter,
            analysis.Parity.EncounterId,
            analysis.Parity.Duration,
            analysis.Parity.PartyComposition,
            analysis.GuaranteedRawDamage,
            analysis.GuaranteedEventCount,
            analysis.GuaranteedDamageShare,
            analysis.CriticalChanceProxy,
            analysis.DirectChanceProxy,
            analysis.RateBuffOverlapFraction,
            "Top cached SAM fight by guaranteed damage, then guaranteed damage share; no network request");

    private static IReadOnlyList<GuaranteedHitCandidateFightResult> BuildFightResults(
        IReadOnlyList<AnalyzedFight> selected)
        => selected.SelectMany(analysis => GuaranteedHitCandidateMath.Definitions.Select(definition =>
        {
            var total = analysis.CandidateTotals[definition.Name];
            var residual = total - analysis.Parity.FflogsDevilmentContribution;
            return new GuaranteedHitCandidateFightResult(
                analysis.Fight.Seed.ReportCode,
                analysis.Fight.Fight.Id,
                analysis.Parity.Actor,
                analysis.Parity.Encounter,
                analysis.Parity.EncounterId,
                analysis.Parity.DancePartnerJob,
                analysis.Parity.Duration,
                analysis.Parity.PartyComposition,
                analysis.Parity.FflogsDevilmentContribution,
                analysis.Parity.DevilmentContribution,
                analysis.Parity.DevilmentContributionDelta,
                definition.Name,
                total,
                residual,
                analysis.Parity.DeltaRdps +
                (total - analysis.Parity.DevilmentContribution) / analysis.Parity.Duration,
                analysis.GuaranteedRawDamage,
                analysis.GuaranteedEventCount,
                analysis.GuaranteedDamageShare,
                analysis.CriticalChanceProxy,
                analysis.DirectChanceProxy,
                analysis.RateBuffOverlapFraction,
                string.Join(';', analysis.BuffConditions));
        })).ToArray();

    private static IReadOnlyList<GuaranteedHitCandidateRanking> BuildRankings(
        IReadOnlyList<AnalyzedFight> selected)
    {
        return GuaranteedHitCandidateMath.Definitions
            .Select(definition =>
            {
                var stats = CalculateStatistics(selected, definition.Name);
                return new GuaranteedHitCandidateRanking(
                    definition.Name,
                    definition.Family,
                    stats,
                    DescribeSystematicBias(stats),
                    "Diagnostic candidate; aggregate evidence is evaluated after structural checks");
            })
            .OrderBy(static result => result.Statistics.MeanAbsoluteResidual)
            .ThenBy(static result => result.Statistics.RootMeanSquareResidual)
            .Select((result, index) => result with
            {
                Verdict = index == 0
                    ? "Best aggregate diagnostic candidate"
                    : result.Candidate == GuaranteedHitCandidateMath.CurrentProduction
                        ? "Calibrated production baseline"
                        : "Rejected relative to the best candidate on selected-fight MAE",
            })
            .ToArray();
    }

    private static IReadOnlyList<GuaranteedHitActionFamilyValidation> BuildActionFamilyValidation(
        IReadOnlyList<AnalyzedFight> selected)
        => GuaranteedHitCandidateMath.Definitions.SelectMany(definition =>
            ActionFamilies.Select(family =>
            {
                var familyRaw = selected.Select(analysis => analysis.GuaranteedEvents
                    .Where(item => family.Value.Contains(item.ActionId))
                    .Sum(static item => (double)item.RawDamage)).ToArray();
                var observed = familyRaw.Count(static value => value > 0);
                var heavyCount = Math.Max(1, (int)Math.Ceiling(selected.Count / 4d));
                var heavy = selected.Zip(familyRaw)
                    .Where(static item => item.Second > 0)
                    .OrderByDescending(static item => item.Second)
                    .Take(heavyCount)
                    .Select(static item => item.First)
                    .ToArray();
                var remaining = selected.Except(heavy).ToArray();
                var residuals = selected.Select(analysis => ResolveResidual(analysis, definition.Name)).ToArray();
                return new GuaranteedHitActionFamilyValidation(
                    definition.Name,
                    family.Key,
                    family.Value,
                    observed,
                    (long)familyRaw.Sum(),
                    heavy.Length,
                    CalculateStatistics(heavy, definition.Name),
                    CalculateStatistics(remaining, definition.Name),
                    Correlation(residuals, familyRaw));
            })).ToArray();

    private static IReadOnlyList<GuaranteedHitCohortValidation> BuildCohortValidation(
        IReadOnlyList<AnalyzedFight> eligibleSam,
        IReadOnlyList<AnalyzedFight> selected)
    {
        var selectedKeys = selected.Select(static item =>
            (item.Fight.Seed.ReportCode, item.Fight.Fight.Id)).ToHashSet();
        var holdout = eligibleSam.Where(item => !selectedKeys.Contains(
            (item.Fight.Seed.ReportCode, item.Fight.Fight.Id))).ToArray();
        return GuaranteedHitCandidateMath.Definitions.SelectMany(definition => new[]
        {
            new GuaranteedHitCohortValidation(
                definition.Name,
                "High-information selected SAM",
                CalculateStatistics(selected, definition.Name)),
            new GuaranteedHitCohortValidation(
                definition.Name,
                "Lower-guaranteed-damage holdout SAM",
                CalculateStatistics(holdout, definition.Name)),
            new GuaranteedHitCohortValidation(
                definition.Name,
                "All eligible cached SAM",
                CalculateStatistics(eligibleSam, definition.Name)),
        }).ToArray();
    }

    private static IReadOnlyList<GuaranteedHitBuffConditionValidation> BuildBuffConditionValidation(
        IReadOnlyList<AnalyzedFight> selected)
    {
        var conditions = selected.SelectMany(static analysis => analysis.BuffConditions)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return GuaranteedHitCandidateMath.Definitions.SelectMany(definition =>
            conditions.Select(condition => new GuaranteedHitBuffConditionValidation(
                definition.Name,
                condition,
                CalculateStatistics(
                    selected.Where(analysis => analysis.BuffConditions.Contains(condition)).ToArray(),
                    definition.Name)))).ToArray();
    }

    private static IReadOnlyList<GuaranteedHitDimensionEvidence> BuildDimensionEvidence(
        IReadOnlyList<AnalyzedFight> analyses)
        => new[]
        {
            ("Guaranteed Crit", ProbeGuaranteedDimensions.Critical),
            ("Guaranteed DH", ProbeGuaranteedDimensions.DirectHit),
            ("Guaranteed Crit+DH", ProbeGuaranteedDimensions.Critical | ProbeGuaranteedDimensions.DirectHit),
        }.Select(dimension =>
        {
            var matches = analyses.SelectMany(static analysis => analysis.GuaranteedEvents.Select(item =>
                    (Analysis: analysis, Event: item)))
                .Where(item => item.Event.Dimensions == dimension.Item2)
                .ToArray();
            var fightCount = matches.Select(item =>
                    (item.Analysis.Fight.Seed.ReportCode, item.Analysis.Fight.Fight.Id))
                .Distinct()
                .Count();
            var verdict = dimension.Item2 == ProbeGuaranteedDimensions.Critical && fightCount >= 20
                ? "supported aggregate evidence"
                : fightCount >= 20
                    ? "aggregate evidence available but not independently isolated"
                    : "insufficient evidence";
            return new GuaranteedHitDimensionEvidence(
                dimension.Item1,
                fightCount,
                matches.Length,
                matches.Sum(static item => item.Event.RawDamage),
                verdict);
        }).ToArray();

    private static IReadOnlyList<GuaranteedHitFullReplayComparison> BuildFullReplay(
        IReadOnlyList<AnalyzedFight> analyses,
        string bestCandidate)
    {
        var groups = analyses.GroupBy(static analysis => analysis.Parity.DancePartnerJob)
            .Select(group => (Name: group.Key, Values: group.ToArray()))
            .Append((Name: "Overall", Values: analyses.ToArray()));
        return groups.Select(group =>
        {
            var current = group.Values.Select(static analysis => analysis.Parity.DeltaRdps).ToArray();
            var counterfactual = group.Values.Select(analysis =>
                analysis.Parity.DeltaRdps +
                (analysis.CandidateTotals[bestCandidate] - analysis.Parity.DevilmentContribution) /
                analysis.Parity.Duration).ToArray();
            return new GuaranteedHitFullReplayComparison(
                group.Name,
                group.Values.Length,
                current.Average(),
                Median(current),
                current.Select(Math.Abs).Average(),
                current.Select(Math.Abs).Max(),
                counterfactual.Average(),
                Median(counterfactual),
                counterfactual.Select(Math.Abs).Average(),
                counterfactual.Select(Math.Abs).Max());
        }).OrderByDescending(static item => item.PartnerJob == "Overall")
          .ThenByDescending(static item => item.SampleCount)
          .ThenBy(static item => item.PartnerJob, StringComparer.Ordinal)
          .ToArray();
    }

    private static string ResolveBestCanonicalCandidate(IReadOnlyList<AnalyzedFight> eligibleSam)
    {
        string[] canonicalCandidates =
        [
            GuaranteedHitCandidateMath.CurrentProduction,
            GuaranteedHitCandidateMath.ObservedHitRegular,
            GuaranteedHitCandidateMath.UnscaledObservedHit,
            GuaranteedHitCandidateMath.ObservedAllActiveDenominator,
            GuaranteedHitCandidateMath.ObservedExcludeSelfEverywhere,
            GuaranteedHitCandidateMath.UnscaledAllActiveDenominator,
            GuaranteedHitCandidateMath.UnscaledSelfScalingExternalDenominator,
            GuaranteedHitCandidateMath.OtherExternalOverlapObservedElseUnscaled,
            GuaranteedHitCandidateMath.OtherExternalOverlapUnscaledElseObserved,
        ];
        return canonicalCandidates
            .OrderBy(candidate => CalculateStatistics(eligibleSam, candidate).MeanAbsoluteResidual)
            .ThenBy(candidate => CalculateStatistics(eligibleSam, candidate).RootMeanSquareResidual)
            .First();
    }

    private static (AnalyzedFight[] High, AnalyzedFight[] Holdout) ResolveSamCohorts(
        IReadOnlyList<AnalyzedFight> eligibleSam,
        IReadOnlyList<AnalyzedFight> selected)
    {
        var selectedKeys = selected.Select(static item =>
            (item.Fight.Seed.ReportCode, item.Fight.Fight.Id)).ToHashSet();
        return (
            selected.ToArray(),
            eligibleSam.Where(item => !selectedKeys.Contains(
                (item.Fight.Seed.ReportCode, item.Fight.Fight.Id))).ToArray());
    }

    private static IReadOnlyList<GuaranteedHitResidualDecomposition> BuildResidualDecomposition(
        IReadOnlyList<AnalyzedFight> eligibleSam,
        IReadOnlyList<AnalyzedFight> selected)
    {
        var cohorts = ResolveSamCohorts(eligibleSam, selected);
        var highKeys = cohorts.High.Select(static item =>
            (item.Fight.Seed.ReportCode, item.Fight.Fight.Id)).ToHashSet();
        return eligibleSam.Select(analysis => new GuaranteedHitResidualDecomposition(
            analysis.Fight.Seed.ReportCode,
            analysis.Fight.Fight.Id,
            analysis.Parity.Actor,
            analysis.GuaranteedEvents.Select(static item => item.PartnerActor)
                .FirstOrDefault() ?? "unavailable",
            analysis.Parity.Encounter,
            analysis.Parity.EncounterId,
            highKeys.Contains((analysis.Fight.Seed.ReportCode, analysis.Fight.Fight.Id))
                ? "High-information 30"
                : "Holdout 31",
            analysis.Parity.Duration,
            analysis.Parity.PartyComposition,
            analysis.PartnerTotalRawDamage,
            analysis.GuaranteedRawDamage,
            analysis.GuaranteedTotalRawRatio,
            analysis.GuaranteedDamageShare,
            analysis.ResolveActionDamage(0x1D3F),
            analysis.ResolveActionDamage(0x9066),
            analysis.ResolveActionDamage(0x9068),
            analysis.ResolveActionDamage(0x64B5),
            analysis.ResolveActionDamage(0x64B6),
            analysis.ResolveActionDamage(0x4066),
            analysis.CriticalChanceProxy,
            analysis.DirectChanceProxy,
            analysis.GuaranteedEvents.Select(static item => item.CriticalChanceProxy).DefaultIfEmpty().Min(),
            analysis.GuaranteedEvents.Select(static item => item.CriticalChanceProxy).DefaultIfEmpty().Max(),
            analysis.GuaranteedEvents.Select(static item => item.DirectChanceProxy).DefaultIfEmpty().Min(),
            analysis.GuaranteedEvents.Select(static item => item.DirectChanceProxy).DefaultIfEmpty().Max(),
            ResolveRateBuffComposition(analysis, critical: true),
            ResolveRateBuffComposition(analysis, critical: false),
            ResolveSelfRateExposure(analysis),
            ResolveExternalRateExposure(analysis),
            ResolveExternalCriticalOverlap(analysis),
            ResolveExternalDirectOverlap(analysis),
            analysis.ResolveRawWeightedRate(static exposure =>
                exposure.SelfCriticalRateIncrease),
            analysis.ResolveRawWeightedRate(static exposure =>
                exposure.SelfDirectRateIncrease),
            analysis.ResolveRawWeightedRate(static exposure =>
                exposure.ExternalCriticalRateIncrease),
            analysis.ResolveRawWeightedRate(static exposure =>
                exposure.ExternalDirectRateIncrease),
            analysis.Parity.FflogsDevilmentContribution,
            analysis.Parity.DevilmentContribution,
            ResolveResidual(analysis, GuaranteedHitCandidateMath.CurrentProduction),
            ResolveResidual(analysis, GuaranteedHitCandidateMath.ObservedHitRegular),
            ResolveResidual(analysis, GuaranteedHitCandidateMath.UnscaledObservedHit),
            GuaranteedHitCandidateMath.Definitions.ToDictionary(
                static definition => definition.Name,
                definition => ResolveResidual(analysis, definition.Name),
                StringComparer.Ordinal)))
            .OrderBy(static item => item.Cohort, StringComparer.Ordinal)
            .ThenByDescending(static item => item.GuaranteedRawDamage)
            .ToArray();
    }

    private static string ResolveRateBuffComposition(AnalyzedFight analysis, bool critical)
    {
        var groups = analysis.GuaranteedEvents
            .SelectMany(item => item.RateExposure.Statuses.Select(status =>
                (Event: item, Status: status)))
            .Where(item => critical
                ? item.Status.CriticalRateIncrease > 0
                : item.Status.DirectRateIncrease > 0)
            .GroupBy(item => (
                item.Status.AbilityId,
                item.Status.AbilityName,
                item.Status.SourceId,
                item.Status.SourceName,
                item.Status.SourceJob,
                item.Status.IsSelfSourced))
            .OrderBy(static group => group.Key.AbilityId)
            .Select(group =>
            {
                var raw = group.Sum(static item => item.Event.RawDamage);
                var share = analysis.GuaranteedRawDamage > 0
                    ? (double)raw / analysis.GuaranteedRawDamage
                    : 0;
                return $"0x{group.Key.AbilityId:X}:{group.Key.AbilityName}" +
                       $"[{group.Key.SourceName}#{group.Key.SourceId}/{group.Key.SourceJob}," +
                       $"{(group.Key.IsSelfSourced ? "self" : "external")}]={share:P1}";
            });
        return string.Join(';', groups);
    }

    private static double ResolveSelfRateExposure(AnalyzedFight analysis)
        => analysis.ResolveRawWeightedExposure(static item =>
            item.RateExposure.SelfCriticalRateIncrease > 0 ||
            item.RateExposure.SelfDirectRateIncrease > 0);

    private static double ResolveExternalRateExposure(AnalyzedFight analysis)
        => analysis.ResolveRawWeightedExposure(static item =>
            item.RateExposure.ExternalCriticalRateIncrease > 0 ||
            item.RateExposure.ExternalDirectRateIncrease > 0);

    private static double ResolveExternalCriticalOverlap(AnalyzedFight analysis)
        => analysis.ResolveRawWeightedExposure(static item =>
            item.RateExposure.ExternalCriticalRateIncrease >
            item.RateExposure.Statuses
                .Where(static status => status.AbilityId == 0x721 && !status.IsSelfSourced)
                .Sum(static status => status.CriticalRateIncrease));

    private static double ResolveExternalDirectOverlap(AnalyzedFight analysis)
        => analysis.ResolveRawWeightedExposure(static item =>
            item.RateExposure.ExternalDirectRateIncrease >
            item.RateExposure.Statuses
                .Where(static status => status.AbilityId == 0x721 && !status.IsSelfSourced)
                .Sum(static status => status.DirectRateIncrease));

    private static double ResolveActionRatio(AnalyzedFight analysis, params long[] actionIds)
        => analysis.GuaranteedRawDamage > 0
            ? (double)analysis.ResolveActionDamage(actionIds) / analysis.GuaranteedRawDamage
            : 0;

    private static bool HasOnlyGuaranteedOwnerJob(AnalyzedFight analysis, string job)
        => analysis.GuaranteedEvents.Count > 0 &&
           analysis.GuaranteedEvents.All(item =>
               string.Equals(item.PartnerJob, job, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<GuaranteedHitCandidateScopeValidation> BuildCandidateScopeValidation(
        IReadOnlyList<AnalyzedFight> analyses,
        IReadOnlyList<AnalyzedFight> eligibleSam,
        IReadOnlyList<AnalyzedFight> selected)
    {
        var cohorts = ResolveSamCohorts(eligibleSam, selected);
        var drg = analyses.Where(static analysis =>
            analysis.Parity.DancePartnerJob == "DRG" &&
            HasOnlyGuaranteedOwnerJob(analysis, "Dragoon")).ToArray();
        return GuaranteedHitCandidateMath.Definitions.SelectMany(definition => new[]
        {
            new GuaranteedHitCandidateScopeValidation(
                definition.Name,
                "High-information SAM 30",
                "Devilment contribution damage",
                CalculateStatistics(cohorts.High, definition.Name)),
            new GuaranteedHitCandidateScopeValidation(
                definition.Name,
                "Holdout SAM 31",
                "Devilment contribution damage",
                CalculateStatistics(cohorts.Holdout, definition.Name)),
            new GuaranteedHitCandidateScopeValidation(
                definition.Name,
                "All SAM 61",
                "Devilment contribution damage",
                CalculateStatistics(eligibleSam, definition.Name)),
            new GuaranteedHitCandidateScopeValidation(
                definition.Name,
                PureDrgScope,
                "Devilment contribution damage",
                CalculateStatistics(drg, definition.Name)),
        }).ToArray();
    }

    private static IReadOnlyList<GuaranteedHitActorAnalysis> BuildActorAnalysis(
        IReadOnlyList<AnalyzedFight> eligibleSam)
        => eligibleSam.GroupBy(static analysis => analysis.Parity.Actor, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var fights = group.ToArray();
                var allCompositions = fights.SelectMany(static analysis =>
                        analysis.GuaranteedEvents.SelectMany(static item => item.RateExposure.Statuses))
                    .Select(static status =>
                        $"0x{status.AbilityId:X}:{status.AbilityName}" +
                        $"[{status.SourceName}#{status.SourceId}/{status.SourceJob}," +
                        $"{(status.IsSelfSourced ? "self" : "external")}]")
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal);
                return new GuaranteedHitActorAnalysis(
                    group.Key,
                    fights.Length,
                    fights.Select(static item => item.Parity.EncounterId).Distinct().Count(),
                    string.Join(';', fights.Select(static item => item.Parity.Encounter)
                        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)),
                    fights.Average(item => ResolveResidual(item, GuaranteedHitCandidateMath.CurrentProduction)),
                    fights.Average(item => ResolveResidual(item, GuaranteedHitCandidateMath.ObservedHitRegular)),
                    fights.Average(item => ResolveResidual(item, GuaranteedHitCandidateMath.UnscaledObservedHit)),
                    fights.Min(static item => item.GuaranteedEvents
                        .Select(static action => action.CriticalChanceProxy).DefaultIfEmpty().Min()),
                    fights.Max(static item => item.GuaranteedEvents
                        .Select(static action => action.CriticalChanceProxy).DefaultIfEmpty().Max()),
                    fights.Min(static item => item.GuaranteedEvents
                        .Select(static action => action.DirectChanceProxy).DefaultIfEmpty().Min()),
                    fights.Max(static item => item.GuaranteedEvents
                        .Select(static action => action.DirectChanceProxy).DefaultIfEmpty().Max()),
                    fights.Average(static item => item.GuaranteedTotalRawRatio),
                    fights.Average(static item => ResolveActionRatio(item, 0x9066)),
                    fights.Average(ResolveExternalCriticalOverlap),
                    fights.Average(ResolveExternalDirectOverlap),
                    fights.Average(ResolveSelfRateExposure),
                    string.Join(';', allCompositions),
                    GuaranteedHitCandidateMath.Definitions.ToDictionary(
                        static definition => definition.Name,
                        definition => fights.Average(item => ResolveResidual(item, definition.Name)),
                        StringComparer.Ordinal));
            })
            .OrderByDescending(static item => item.FightCount)
            .ThenBy(static item => item.Actor, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IReadOnlyList<GuaranteedHitActorStability> BuildActorStability(
        IReadOnlyList<AnalyzedFight> eligibleSam)
        => GuaranteedHitCandidateMath.Definitions.Select(definition =>
        {
            var groups = eligibleSam.GroupBy(static item => item.Parity.Actor, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.ToArray())
                .Where(static group => group.Length > 1)
                .ToArray();
            var stableSign = groups.Count(group =>
            {
                var residuals = group.Select(item => ResolveResidual(item, definition.Name)).ToArray();
                return residuals.All(static value => value > 0) ||
                       residuals.All(static value => value < 0);
            });
            var withinStandardDeviations = groups.Select(group =>
            {
                var values = group.Select(item => ResolveResidual(item, definition.Name)).ToArray();
                var mean = values.Average();
                return Math.Sqrt(values.Select(value => (value - mean) * (value - mean)).Average());
            }).ToArray();
            return new GuaranteedHitActorStability(
                definition.Name,
                groups.Length,
                groups.Count(static group => group.Select(item => item.Parity.EncounterId).Distinct().Count() > 1),
                stableSign,
                withinStandardDeviations.DefaultIfEmpty().Average(),
                CalculateStatistics(eligibleSam, definition.Name).ActorEtaSquared);
        }).ToArray();

    private static IReadOnlyList<GuaranteedHitCohortFeatureDistribution> BuildCohortFeatureDistributions(
        IReadOnlyList<AnalyzedFight> eligibleSam,
        IReadOnlyList<AnalyzedFight> selected)
    {
        var cohorts = ResolveSamCohorts(eligibleSam, selected);
        var groups = new[]
        {
            (Name: "High-information 30", Values: cohorts.High),
            (Name: "Holdout 31", Values: cohorts.Holdout),
            (Name: "All SAM 61", Values: eligibleSam.ToArray()),
        };
        (string Name, Func<AnalyzedFight, double> Resolve)[] features =
        [
            ("Guaranteed raw damage", static item => item.GuaranteedRawDamage),
            ("Guaranteed / fight partner raw", static item => item.GuaranteedTotalRawRatio),
            ("Guaranteed / Devilment-window raw", static item => item.GuaranteedDamageShare),
            ("Tendo / guaranteed raw", static item => ResolveActionRatio(item, 0x9066)),
            ("Tendo Kaeshi / guaranteed raw", static item => ResolveActionRatio(item, 0x9068)),
            ("Ogi / guaranteed raw", static item => ResolveActionRatio(item, 0x64B5)),
            ("Inferred Cu", static item => item.CriticalChanceProxy),
            ("Inferred Du", static item => item.DirectChanceProxy),
            ("Fight duration seconds", static item => item.Parity.Duration),
            ("External Crit overlap exposure (guaranteed-raw weighted)", ResolveExternalCriticalOverlap),
            ("External DH overlap exposure (guaranteed-raw weighted)", ResolveExternalDirectOverlap),
            ("Self-rate exposure (guaranteed-raw weighted)", ResolveSelfRateExposure),
            ("Any external rate exposure (guaranteed-raw weighted)", ResolveExternalRateExposure),
        ];
        return groups.SelectMany(group => features.Select(feature =>
        {
            var values = group.Values.Select(feature.Resolve).Order().ToArray();
            return new GuaranteedHitCohortFeatureDistribution(
                group.Name,
                feature.Name,
                values.Length,
                values.DefaultIfEmpty().Average(),
                Median(values),
                Quantile(values, 0.25),
                Quantile(values, 0.75),
                values.DefaultIfEmpty().Min(),
                values.DefaultIfEmpty().Max());
        })).ToArray();
    }

    private static IReadOnlyList<GuaranteedHitCohortCategoryDistribution> BuildCohortCategoryDistributions(
        IReadOnlyList<AnalyzedFight> eligibleSam,
        IReadOnlyList<AnalyzedFight> selected)
    {
        var cohorts = ResolveSamCohorts(eligibleSam, selected);
        var groups = new[]
        {
            (Name: "High-information 30", Values: cohorts.High),
            (Name: "Holdout 31", Values: cohorts.Holdout),
            (Name: "All SAM 61", Values: eligibleSam.ToArray()),
        };
        (string Name, Func<AnalyzedFight, string> Resolve)[] dimensions =
        [
            ("Dancer actor", static item => item.Parity.Actor),
            ("Encounter", static item => item.Parity.Encounter),
            ("Party composition", static item => item.Parity.PartyComposition),
            ("Crit-rate composition", static item => ResolveRateBuffComposition(item, critical: true)),
            ("DH-rate composition", static item => ResolveRateBuffComposition(item, critical: false)),
        ];
        return groups.SelectMany(group => dimensions.SelectMany(dimension =>
            group.Values.GroupBy(dimension.Resolve, StringComparer.OrdinalIgnoreCase)
                .Select(values => new GuaranteedHitCohortCategoryDistribution(
                    group.Name,
                    dimension.Name,
                    values.Key,
                    values.Count(),
                    group.Values.Length > 0 ? (double)values.Count() / group.Values.Length : 0))))
            .OrderBy(static item => item.Cohort, StringComparer.Ordinal)
            .ThenBy(static item => item.Dimension, StringComparer.Ordinal)
            .ThenByDescending(static item => item.FightCount)
            .ThenBy(static item => item.Value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<GuaranteedHitPartialCorrelation> BuildPartialCorrelations(
        IReadOnlyList<AnalyzedFight> eligibleSam,
        IReadOnlyList<AnalyzedFight> selected)
    {
        var cohorts = ResolveSamCohorts(eligibleSam, selected);
        var scopes = new[]
        {
            (Name: "High-information 30", Values: cohorts.High),
            (Name: "Holdout 31", Values: cohorts.Holdout),
            (Name: "All SAM 61", Values: eligibleSam.ToArray()),
        };
        (string Name, Func<AnalyzedFight, double> Resolve)[] variables =
        [
            ("Guaranteed raw damage", static item => item.GuaranteedRawDamage),
            ("Guaranteed damage ratio", static item => item.GuaranteedTotalRawRatio),
            ("Tendo raw damage", static item => item.ResolveActionDamage(0x9066)),
            ("Tendo damage ratio", static item => ResolveActionRatio(item, 0x9066)),
            ("Tendo Kaeshi raw damage", static item => item.ResolveActionDamage(0x9068)),
            ("Tendo Kaeshi damage ratio", static item => ResolveActionRatio(item, 0x9068)),
            ("Ogi damage ratio", static item => ResolveActionRatio(item, 0x64B5)),
            ("Fight duration", static item => item.Parity.Duration),
            ("Inferred Cu", static item => item.CriticalChanceProxy),
            ("Inferred Du", static item => item.DirectChanceProxy),
            ("External Crit overlap", ResolveExternalCriticalOverlap),
            ("External DH overlap", ResolveExternalDirectOverlap),
            ("Self-rate exposure", ResolveSelfRateExposure),
            ("Any external rate exposure", ResolveExternalRateExposure),
        ];
        (string Name, Func<AnalyzedFight, double> Resolve)[] numericControls =
        [
            ("Guaranteed raw damage", static item => item.GuaranteedRawDamage),
            ("Guaranteed damage ratio", static item => item.GuaranteedTotalRawRatio),
            ("Fight duration", static item => item.Parity.Duration),
            ("Inferred Cu", static item => item.CriticalChanceProxy),
            ("Inferred Du", static item => item.DirectChanceProxy),
            ("External Crit overlap", ResolveExternalCriticalOverlap),
            ("External DH overlap", ResolveExternalDirectOverlap),
            ("Self-rate exposure", ResolveSelfRateExposure),
        ];

        var result = new List<GuaranteedHitPartialCorrelation>();
        foreach (var definition in GuaranteedHitCandidateMath.Definitions)
        {
            foreach (var scope in scopes)
            {
                var actorGroups = scope.Values.Select(static item => item.Parity.Actor).ToArray();
                var encounterGroups = scope.Values.Select(static item => item.Parity.Encounter).ToArray();
                foreach (var variable in variables)
                {
                    var residuals = scope.Values.Select(item => ResolveResidual(item, definition.Name)).ToArray();
                    var values = scope.Values.Select(variable.Resolve).ToArray();
                    Func<AnalyzedFight, double> guaranteedControlSelector =
                        variable.Name == "Guaranteed damage ratio"
                            ? static item => item.GuaranteedRawDamage
                            : static item => item.GuaranteedTotalRawRatio;
                    var guaranteedControl = scope.Values.Select(guaranteedControlSelector).ToArray();
                    var controls = numericControls
                        .Where(control => control.Name != variable.Name)
                        .Select(control => scope.Values.Select(control.Resolve).ToArray())
                        .ToList();
                    var fullControls = controls.ToList();
                    // High and holdout contain many singleton actors. Adding actor and
                    // encounter fixed effects together would exhaust the residual degrees
                    // of freedom and manufacture +/-1 correlations, so actor is reported
                    // separately as the within-actor estimate.
                    AddGroupDummyControls(fullControls, encounterGroups);
                    result.Add(new GuaranteedHitPartialCorrelation(
                        definition.Name,
                        scope.Name,
                        variable.Name,
                        scope.Values.Length,
                        ResolveWithinGroupObservationCount(actorGroups),
                        ResolveWithinGroupObservationCount(encounterGroups),
                        Correlation(residuals, values),
                        PartialCorrelation(residuals, values, [guaranteedControl]),
                        PartialCorrelation(residuals, values, controls),
                        WithinGroupCorrelation(residuals, values, actorGroups),
                        WithinGroupCorrelation(residuals, values, encounterGroups),
                        PartialCorrelation(residuals, values, fullControls)));
                }
            }
        }
        return result;
    }

    private static int ResolveWithinGroupObservationCount(IReadOnlyList<string> groups)
        => groups.GroupBy(static group => group, StringComparer.OrdinalIgnoreCase)
            .Where(static group => group.Count() > 1)
            .Sum(static group => group.Count());

    private static void AddGroupDummyControls(
        ICollection<double[]> controls,
        IReadOnlyList<string> groups)
    {
        foreach (var group in groups.Distinct(StringComparer.OrdinalIgnoreCase).Skip(1))
        {
            controls.Add(groups.Select(value =>
                string.Equals(value, group, StringComparison.OrdinalIgnoreCase) ? 1d : 0d).ToArray());
        }
    }

    private static double PartialCorrelation(
        IReadOnlyList<double> left,
        IReadOnlyList<double> right,
        IReadOnlyList<double[]> controls)
    {
        if (left.Count != right.Count || left.Count < 3)
        {
            return 0;
        }
        var basis = BuildOrthonormalBasis(left.Count, controls);
        return Correlation(RemoveProjection(left, basis), RemoveProjection(right, basis));
    }

    private static IReadOnlyList<double[]> BuildOrthonormalBasis(
        int count,
        IReadOnlyList<double[]> controls)
    {
        var basis = new List<double[]> { Enumerable.Repeat(1d / Math.Sqrt(count), count).ToArray() };
        foreach (var source in controls.Where(control => control.Length == count))
        {
            var vector = source.ToArray();
            foreach (var axis in basis)
            {
                var projection = Dot(vector, axis);
                for (var index = 0; index < vector.Length; index++)
                {
                    vector[index] -= projection * axis[index];
                }
            }
            var norm = Math.Sqrt(Dot(vector, vector));
            // Collinear controls carry no additional information and would make the
            // partial correlation depend on an arbitrary matrix regularizer.
            if (norm <= Math.Sqrt(count) * 1e-10)
            {
                continue;
            }
            for (var index = 0; index < vector.Length; index++)
            {
                vector[index] /= norm;
            }
            basis.Add(vector);
        }
        return basis;
    }

    private static double[] RemoveProjection(
        IReadOnlyList<double> values,
        IReadOnlyList<double[]> basis)
    {
        var result = values.ToArray();
        foreach (var axis in basis)
        {
            var projection = Dot(result, axis);
            for (var index = 0; index < result.Length; index++)
            {
                result[index] -= projection * axis[index];
            }
        }
        return result;
    }

    private static double WithinGroupCorrelation(
        IReadOnlyList<double> left,
        IReadOnlyList<double> right,
        IReadOnlyList<string> groups)
    {
        var adjustedLeft = new List<double>();
        var adjustedRight = new List<double>();
        foreach (var indexes in groups.Select((value, index) => (value, index))
                     .GroupBy(static item => item.value, StringComparer.OrdinalIgnoreCase)
                     .Select(static group => group.Select(static item => item.index).ToArray())
                     .Where(static indexes => indexes.Length > 1))
        {
            var leftMean = indexes.Average(index => left[index]);
            var rightMean = indexes.Average(index => right[index]);
            adjustedLeft.AddRange(indexes.Select(index => left[index] - leftMean));
            adjustedRight.AddRange(indexes.Select(index => right[index] - rightMean));
        }
        return Correlation(adjustedLeft, adjustedRight);
    }

    private static double Dot(IReadOnlyList<double> left, IReadOnlyList<double> right)
    {
        var result = 0d;
        for (var index = 0; index < left.Count; index++)
        {
            result += left[index] * right[index];
        }
        return result;
    }

    private static IReadOnlyList<GuaranteedHitCounterfactualStatistics> BuildAllCandidateCounterfactuals(
        IReadOnlyList<AnalyzedFight> analyses)
    {
        var groups = analyses.GroupBy(static analysis => analysis.Parity.DancePartnerJob)
            .Select(group => (Name: group.Key, Values: group.ToArray()))
            .Append((Name: "Overall", Values: analyses.ToArray()))
            .ToArray();
        return GuaranteedHitCandidateMath.Definitions.SelectMany(definition => groups.Select(group =>
        {
            var deltas = group.Values.Select(analysis =>
                analysis.Parity.DeltaRdps +
                (analysis.CandidateTotals[definition.Name] - analysis.Parity.DevilmentContribution) /
                analysis.Parity.Duration).ToArray();
            return new GuaranteedHitCounterfactualStatistics(
                definition.Name,
                group.Name,
                deltas.Length,
                deltas.DefaultIfEmpty().Average(),
                Median(deltas),
                deltas.Select(Math.Abs).DefaultIfEmpty().Average(),
                Math.Sqrt(deltas.Select(static value => value * value).DefaultIfEmpty().Average()),
                deltas.Select(Math.Abs).DefaultIfEmpty().Max(),
                deltas.Count(static value => value < -0.000_001),
                deltas.Count(static value => Math.Abs(value) <= 0.000_001),
                deltas.Count(static value => value > 0.000_001));
        })).ToArray();
    }

    private static IReadOnlyList<RateBuffDenominatorAudit> BuildRateBuffDenominatorAudit()
        =>
        [
            new(
                0x721,
                "Devilment",
                0.20,
                0.20,
                "DNC -> self and Dance Partner",
                "Partner carrier enters Cext and Dext.",
                "DNC self carrier is removed before rate arrays are built.",
                false,
                "When external, enters both Crit and DH provider denominators; when self, enters neither.",
                "Not publicly documented for self-source denominator membership."),
            new(
                0x312,
                "Battle Litany",
                0.10,
                0,
                "DRG -> self and nearby party",
                "Ally carrier enters Cext.",
                "DRG self carrier is removed before rate arrays are built.",
                false,
                "When external, dilutes other Crit providers; when self, does not.",
                "Not publicly documented for self-source denominator membership."),
            new(
                0x4C5,
                "Chain Stratagem",
                0.10,
                0,
                "SCH -> enemy target debuff",
                "For non-SCH attackers, the enemy carrier enters Cext.",
                "For SCH damage, provider equals attacker and the debuff is removed from rate arrays.",
                false,
                "When external, dilutes other Crit providers; when self, does not.",
                "Not publicly documented for self-source denominator membership."),
            new(
                0x08D,
                "Battle Voice",
                0,
                0.20,
                "BRD -> self and nearby party",
                "Ally carrier enters Dext.",
                "BRD self carrier is removed before rate arrays are built.",
                false,
                "When external, dilutes other DH providers; when self, does not.",
                "Not publicly documented for self-source denominator membership."),
        ];

    private static IReadOnlyList<GuaranteedHitAcceptanceCheck> BuildAcceptanceChecks(
        IReadOnlyList<AnalyzedFight> analyses,
        IReadOnlyList<AnalyzedFight> eligibleSam,
        IReadOnlyList<AnalyzedFight> selected)
    {
        var cohorts = ResolveSamCohorts(eligibleSam, selected);
        var drgAll = analyses.Where(static item => item.Parity.DancePartnerJob == "DRG").ToArray();
        // A primary-partner label can span a mid-fight partner swap. Component checks therefore
        // retain only fights whose guaranteed events are all owned by a DRG, while the final-rDPS
        // guard still covers every cached fight classified under the DRG partner cohort.
        var drgGuaranteedPure = drgAll.Where(static analysis =>
            HasOnlyGuaranteedOwnerJob(analysis, "Dragoon")).ToArray();
        var currentHigh = CalculateStatistics(cohorts.High, GuaranteedHitCandidateMath.CurrentProduction);
        var currentHoldout = CalculateStatistics(cohorts.Holdout, GuaranteedHitCandidateMath.CurrentProduction);
        var currentSam = CalculateStatistics(eligibleSam, GuaranteedHitCandidateMath.CurrentProduction);
        var currentDrgComponent = CalculateStatistics(
            drgGuaranteedPure,
            GuaranteedHitCandidateMath.CurrentProduction);
        var currentDrgFinalMae = drgAll.Select(static item => Math.Abs(item.Parity.DeltaRdps)).Average();
        var result = new List<GuaranteedHitAcceptanceCheck>();
        foreach (var definition in GuaranteedHitCandidateMath.Definitions)
        {
            var high = CalculateStatistics(cohorts.High, definition.Name);
            var holdout = CalculateStatistics(cohorts.Holdout, definition.Name);
            var sam = CalculateStatistics(eligibleSam, definition.Name);
            var drgComponent = CalculateStatistics(drgGuaranteedPure, definition.Name);
            var drgFinalMae = drgAll.Select(item => Math.Abs(
                item.Parity.DeltaRdps +
                (item.CandidateTotals[definition.Name] - item.Parity.DevilmentContribution) /
                item.Parity.Duration)).Average();
            result.AddRange(
            [
                new(definition.Name, "High cohort improves over production",
                    high.MeanAbsoluteResidual < currentHigh.MeanAbsoluteResidual,
                    $"MAE {high.MeanAbsoluteResidual:F1} vs {currentHigh.MeanAbsoluteResidual:F1}"),
                new(definition.Name, "Holdout improves without one-sided overrun",
                    holdout.MeanAbsoluteResidual < currentHoldout.MeanAbsoluteResidual &&
                    Math.Max(holdout.NegativeCount, holdout.PositiveCount) < holdout.FightCount * 0.8,
                    $"mean/MAE/sign N-P {holdout.MeanResidual:F1}/{holdout.MeanAbsoluteResidual:F1}/{holdout.NegativeCount}-{holdout.PositiveCount}"),
                new(definition.Name, "All SAM improves over production",
                    sam.MeanAbsoluteResidual < currentSam.MeanAbsoluteResidual,
                    $"MAE {sam.MeanAbsoluteResidual:F1} vs {currentSam.MeanAbsoluteResidual:F1}"),
                new(definition.Name, "DRG component and final counterfactual are not worse",
                    drgComponent.MeanAbsoluteResidual <= currentDrgComponent.MeanAbsoluteResidual &&
                    drgFinalMae <= currentDrgFinalMae,
                    $"pure-owner component N={drgGuaranteedPure.Length}, MAE " +
                    $"{drgComponent.MeanAbsoluteResidual:F1} vs {currentDrgComponent.MeanAbsoluteResidual:F1}; " +
                    $"primary-partner final N={drgAll.Length}, rDPS MAE " +
                    $"{drgFinalMae:F3} vs {currentDrgFinalMae:F3}"),
                new(definition.Name, "Guaranteed-damage structure removed",
                    Math.Abs(sam.ResidualVsGuaranteedDamageCorrelation) <= 0.2,
                    $"corr {sam.ResidualVsGuaranteedDamageCorrelation:F3}"),
                new(definition.Name, "Tendo family structure removed",
                    Math.Abs(sam.ResidualVsTendoDamageRatioCorrelation) <= 0.2 &&
                    Math.Abs(sam.ResidualVsTendoKaeshiDamageRatioCorrelation) <= 0.2,
                    $"ratio corr {sam.ResidualVsTendoDamageRatioCorrelation:F3}/{sam.ResidualVsTendoKaeshiDamageRatioCorrelation:F3}"),
                new(definition.Name, "No dominant actor grouping",
                    sam.ActorEtaSquared <= 0.5,
                    $"actor eta-squared {sam.ActorEtaSquared:F3}"),
                new(definition.Name, "No dominant encounter grouping",
                    sam.EncounterEtaSquared <= 0.5,
                    $"encounter eta-squared {sam.EncounterEtaSquared:F3}"),
                new(definition.Name, "No fitted constants or job/encounter coefficients", true,
                    "Candidate definition contains no free fitted parameter."),
                new(definition.Name, "Ordinary published Crit/DH path unchanged", true,
                    "Offline replacement is restricted to production-identified guaranteed events."),
            ]);
        }
        return result;
    }

    private static IReadOnlyList<string> BuildResidualFindings(
        IReadOnlyList<AnalyzedFight> eligibleSam,
        IReadOnlyList<AnalyzedFight> selected)
    {
        var cohorts = ResolveSamCohorts(eligibleSam, selected);
        var observedHigh = CalculateStatistics(cohorts.High, GuaranteedHitCandidateMath.ObservedHitRegular);
        var observedHoldout = CalculateStatistics(cohorts.Holdout, GuaranteedHitCandidateMath.ObservedHitRegular);
        var noOverlap = eligibleSam.Where(item => ResolveExternalCriticalOverlap(item) == 0).ToArray();
        var partialOverlap = eligibleSam.Where(item => ResolveExternalCriticalOverlap(item) > 0 &&
                                                       ResolveExternalCriticalOverlap(item) < 0.95).ToArray();
        var nearFullOverlap = eligibleSam.Where(item => ResolveExternalCriticalOverlap(item) >= 0.95).ToArray();
        var highNoOverlap = cohorts.High.Where(item => ResolveExternalCriticalOverlap(item) == 0).ToArray();
        var holdoutNoOverlap = cohorts.Holdout.Where(item => ResolveExternalCriticalOverlap(item) == 0).ToArray();
        var holdoutOther = cohorts.Holdout.Except(holdoutNoOverlap).ToArray();
        var totalHoldoutOverrun = cohorts.Holdout.Sum(item =>
            ResolveResidual(item, GuaranteedHitCandidateMath.ObservedHitRegular));
        var noOverlapOverrun = holdoutNoOverlap.Sum(item =>
            ResolveResidual(item, GuaranteedHitCandidateMath.ObservedHitRegular));
        var highActors = cohorts.High.Select(static item => item.Parity.Actor)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var holdoutActors = cohorts.Holdout.Select(static item => item.Parity.Actor)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var highParties = cohorts.High.Select(static item => item.Parity.PartyComposition)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var holdoutParties = cohorts.Holdout.Select(static item => item.Parity.PartyComposition)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var repeatedActors = eligibleSam.GroupBy(static item => item.Parity.Actor, StringComparer.OrdinalIgnoreCase)
            .Where(static group => group.Count() > 1)
            .ToArray();
        var stableObservedActors = repeatedActors.Count(group =>
        {
            var residuals = group.Select(item =>
                ResolveResidual(item, GuaranteedHitCandidateMath.ObservedHitRegular)).ToArray();
            return residuals.All(static value => value > 0) || residuals.All(static value => value < 0);
        });
        var repeatedPairs = cohorts.Holdout
            .GroupBy(static item => (
                Dancer: item.Parity.Actor,
                Partner: item.GuaranteedEvents.Select(static action => action.PartnerActor)
                    .FirstOrDefault() ?? "unavailable"))
            .Where(static group => group.Count() > 1)
            .OrderByDescending(group => Math.Abs(group.Sum(item =>
                ResolveResidual(item, GuaranteedHitCandidateMath.ObservedHitRegular))))
            .Take(3)
            .Select(group => $"{group.Key.Dancer}/{group.Key.Partner}:N={group.Count()},sum=" +
                             $"{group.Sum(item => ResolveResidual(item, GuaranteedHitCandidateMath.ObservedHitRegular)):F1}")
            .ToArray();
        return
        [
            $"ObservedHitRegular high mean/MAE={observedHigh.MeanResidual:F1}/{observedHigh.MeanAbsoluteResidual:F1}; holdout={observedHoldout.MeanResidual:F1}/{observedHoldout.MeanAbsoluteResidual:F1} damage.",
            $"Configured self-rate exposure across SAM: max={eligibleSam.Select(ResolveSelfRateExposure).DefaultIfEmpty().Max():P3}; external-DH overlap max={eligibleSam.Select(ResolveExternalDirectOverlap).DefaultIfEmpty().Max():P3}.",
            $"Observed residual by external-Crit exposure: none N={noOverlap.Length}, mean={CalculateStatistics(noOverlap, GuaranteedHitCandidateMath.ObservedHitRegular).MeanResidual:F1}; partial N={partialOverlap.Length}, mean={CalculateStatistics(partialOverlap, GuaranteedHitCandidateMath.ObservedHitRegular).MeanResidual:F1}; >=95% N={nearFullOverlap.Length}, mean={CalculateStatistics(nearFullOverlap, GuaranteedHitCandidateMath.ObservedHitRegular).MeanResidual:F1}.",
            $"Holdout no-Crit-overlap fights: N={holdoutNoOverlap.Length}, mean={CalculateStatistics(holdoutNoOverlap, GuaranteedHitCandidateMath.ObservedHitRegular).MeanResidual:F1}, signed overrun share={(totalHoldoutOverrun != 0 ? noOverlapOverrun / totalHoldoutOverrun : 0):P1}; remaining holdout mean={CalculateStatistics(holdoutOther, GuaranteedHitCandidateMath.ObservedHitRegular).MeanResidual:F1}.",
            $"The same no-Crit-overlap condition is not a stable equation boundary: high N={highNoOverlap.Length}, mean={CalculateStatistics(highNoOverlap, GuaranteedHitCandidateMath.ObservedHitRegular).MeanResidual:F1}; holdout N={holdoutNoOverlap.Length}, mean={CalculateStatistics(holdoutNoOverlap, GuaranteedHitCandidateMath.ObservedHitRegular).MeanResidual:F1}.",
            $"High/holdout actor diversity={highActors.Count}/{holdoutActors.Count}, shared={highActors.Intersect(holdoutActors).Count()}; party-composition diversity={highParties.Count}/{holdoutParties.Count}, shared={highParties.Intersect(holdoutParties).Count()}.",
            $"Repeated Dancer actors with one residual sign across fights: {stableObservedActors}/{repeatedActors.Length}; holdout repeated-pair clusters: {string.Join("; ", repeatedPairs)}.",
            "Observed variants C and D are mathematically identical because observed N already embeds self-rate game scaling; Unscaled variants B and C are identical because both use the external-only set for scaling restoration and denominator.",
            "Self-source denominator membership is not identifiable from SAM because every SAM guaranteed event has zero configured self-rate exposure; DRG Life Surge plus own Battle Litany is the only cached controlled self-rate axis.",
            "One primary-DRG fight switches Devilment to a SAM mid-fight; pure DRG component validation excludes that mixed-owner aggregate because FFLogs exposes no per-target given.total split.",
        ];
    }

    private static (string Status, string Reason) ResolveEquationStatus(
        IReadOnlyList<AnalyzedFight> eligibleSam,
        IReadOnlyList<AnalyzedFight> selected,
        IReadOnlyList<GuaranteedHitCandidateScopeValidation> scopeValidation,
        string best)
    {
        var cohorts = ResolveSamCohorts(eligibleSam, selected);
        var current = CalculateStatistics(eligibleSam, GuaranteedHitCandidateMath.CurrentProduction);
        var winner = CalculateStatistics(eligibleSam, best);
        var highWinner = CalculateStatistics(cohorts.High, best);
        var highCurrent = CalculateStatistics(cohorts.High, GuaranteedHitCandidateMath.CurrentProduction);
        var holdoutWinner = CalculateStatistics(cohorts.Holdout, best);
        var holdoutCurrent = CalculateStatistics(cohorts.Holdout, GuaranteedHitCandidateMath.CurrentProduction);
        var drgWinner = scopeValidation.Single(item =>
            item.Candidate == best && item.Scope == PureDrgScope).Statistics;
        var drgCurrent = scopeValidation.Single(item =>
            item.Candidate == GuaranteedHitCandidateMath.CurrentProduction &&
            item.Scope == PureDrgScope).Statistics;
        var multiplePlayers = selected.Select(static item => item.Parity.Actor)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() >= 10;
        var multipleEncounters = selected.Select(static item => item.Parity.EncounterId).Distinct().Count() >= 3;
        var signStructured = Math.Max(winner.NegativeCount, winner.PositiveCount) <=
                             Math.Ceiling(winner.FightCount * 0.8);
        var familyStructured = Math.Abs(winner.ResidualVsTendoDamageRatioCorrelation) <= 0.2 &&
                               Math.Abs(winner.ResidualVsTendoKaeshiDamageRatioCorrelation) <= 0.2;
        var strong = best != GuaranteedHitCandidateMath.CurrentProduction &&
                     highWinner.MeanAbsoluteResidual < highCurrent.MeanAbsoluteResidual &&
                     holdoutWinner.MeanAbsoluteResidual < holdoutCurrent.MeanAbsoluteResidual &&
                     winner.MeanAbsoluteResidual <= current.MeanAbsoluteResidual * 0.65 &&
                     Math.Abs(winner.ResidualVsGuaranteedDamageCorrelation) <= 0.2 &&
                     Math.Abs(holdoutWinner.ResidualVsGuaranteedDamageCorrelation) <= 0.2 &&
                     drgWinner.MeanAbsoluteResidual <= drgCurrent.MeanAbsoluteResidual &&
                     winner.ActorEtaSquared <= 0.5 && winner.EncounterEtaSquared <= 0.5 &&
                     multiplePlayers && multipleEncounters && signStructured && familyStructured;
        return strong
            ? ("Strongly supported",
                "The best parameter-free equation improves high, holdout, all-SAM, and DRG cohorts while removing guaranteed-volume, Tendo-family, actor, and encounter structure; aggregate-only evidence cannot elevate it to Confirmed.")
            : ("Not determined",
                "No candidate satisfies every high/holdout, cross-job, sign-balance, guaranteed-volume, Tendo-family, actor, and encounter structural check. Aggregate totals reject models but do not uniquely identify FFLogs' hidden guaranteed-Crit equation.");
    }

    private static IReadOnlyList<string> BuildRemainingUnknowns(IReadOnlyList<AnalyzedFight> analyses)
    {
        var dimensionEvidence = BuildDimensionEvidence(analyses).ToDictionary(static item => item.Dimension);
        var result = new List<string>
        {
            "FFLogs actual Cu/Du is not exposed by the public API; DACT inferred snapshots are proxies only.",
            "Whether self-sourced Crit/DH rate buffs enter FFLogs Cb/Db denominators is not publicly documented. SAM has zero self-rate exposure, while cached DRG Life Surge plus own Battle Litany supplies only aggregate, confounded evidence.",
            "DRG denominator variants reuse DACT inferred Cu. Production filters self rate before the baseline-observation gate, so own-Litany observations can contaminate that proxy and prevent clean self-denominator identification.",
            "FFLogs per-action and per-window Devilment allocations remain unavailable, so aggregate cancellation cannot be excluded.",
            "ObservedHitRegular's no-overlap residual is concentrated in a few Dancer/partner pairs; actual partner stats and any FFLogs hidden action/state inputs remain unavailable.",
            "Army's Paeon and the Wanderer's Minuet are not present in production rate metadata; this affects one cached BRD-party/MNK-partner fight but none of the 61 SAM fights.",
        };
        if (dimensionEvidence["Guaranteed DH"].FightCount < 20)
        {
            result.Add("Guaranteed DH has insufficient independent high-information fights.");
        }
        if (dimensionEvidence["Guaranteed Crit+DH"].FightCount < 20)
        {
            result.Add("Guaranteed Crit+DH has insufficient independent high-information fights.");
        }
        return result;
    }

    private static GuaranteedHitResidualStatistics CalculateStatistics(
        IReadOnlyList<AnalyzedFight> analyses,
        string candidate)
    {
        if (analyses.Count == 0)
        {
            return new GuaranteedHitResidualStatistics(
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0);
        }
        var residuals = analyses.Select(item => ResolveResidual(item, candidate)).ToArray();
        return new GuaranteedHitResidualStatistics(
            analyses.Count,
            residuals.Average(),
            Median(residuals),
            residuals.Select(Math.Abs).Average(),
            Math.Sqrt(residuals.Select(static value => value * value).Average()),
            residuals.Select(Math.Abs).Max(),
            residuals.Count(static value => value < -0.000_001),
            residuals.Count(static value => Math.Abs(value) <= 0.000_001),
            residuals.Count(static value => value > 0.000_001),
            Correlation(residuals, analyses.Select(static item => item.Parity.Duration).ToArray()),
            Correlation(residuals, analyses.Select(static item => (double)item.GuaranteedRawDamage).ToArray()),
            Correlation(residuals, analyses.Select(static item => item.CriticalChanceProxy).ToArray()),
            Correlation(residuals, analyses.Select(static item => item.DirectChanceProxy).ToArray()),
            Correlation(residuals, analyses.Select(static item => item.RateBuffOverlapFraction).ToArray()),
            Correlation(residuals, analyses.Select(static item => item.GuaranteedTotalRawRatio).ToArray()),
            Correlation(residuals, analyses.Select(static item => ResolveActionRatio(item, 0x9066)).ToArray()),
            Correlation(residuals, analyses.Select(static item => ResolveActionRatio(item, 0x9068)).ToArray()),
            Correlation(residuals, analyses.Select(static item => ResolveActionRatio(item, 0x64B5)).ToArray()),
            Correlation(residuals, analyses.Select(ResolveSelfRateExposure).ToArray()),
            Correlation(residuals, analyses.Select(ResolveExternalCriticalOverlap).ToArray()),
            Correlation(residuals, analyses.Select(ResolveExternalDirectOverlap).ToArray()),
            EtaSquared(
                residuals,
                analyses.Select(static item => item.Parity.Actor).ToArray(),
                minimumGroupSize: 2),
            EtaSquared(
                residuals,
                analyses.Select(static item => item.Parity.Encounter).ToArray(),
                minimumGroupSize: 1));
    }

    private static string DescribeSystematicBias(GuaranteedHitResidualStatistics stats)
    {
        var sign = stats.FightCount == 0
            ? "no samples"
            : stats.NegativeCount >= stats.FightCount * 0.8
                ? "stable negative"
                : stats.PositiveCount >= stats.FightCount * 0.8
                    ? "stable positive"
                    : "mixed sign";
        return $"{sign}; corr(residual, guaranteed damage)={stats.ResidualVsGuaranteedDamageCorrelation:F3}";
    }

    private static double ResolveResidual(AnalyzedFight analysis, string candidate)
        => analysis.CandidateTotals[candidate] - analysis.Parity.FflogsDevilmentContribution;

    private static double WeightedAverage(
        IReadOnlyList<GuaranteedEventAnalysis> items,
        Func<GuaranteedEventAnalysis, double> selector)
    {
        var weight = items.Sum(static item => item.RawDamage);
        return weight > 0 ? items.Sum(item => selector(item) * item.RawDamage) / weight : 0;
    }

    private static IReadOnlyList<string> ResolveBuffConditions(
        IReadOnlyList<GuaranteedEventAnalysis> events)
    {
        var result = new List<string>();
        var ids = events.SelectMany(static item => item.RateBuffIds).ToHashSet();
        var overlap = ids.Where(static id => id != 0x721).ToArray();
        if (overlap.Length == 0)
        {
            result.Add("Devilment only");
        }
        if (ids.Contains(0x312))
        {
            result.Add("Devilment + Battle Litany");
        }
        if (ids.Contains(0x4C5))
        {
            result.Add("Devilment + Chain Stratagem");
        }
        if (ids.Contains(0x08D))
        {
            result.Add("Devilment + Battle Voice");
        }
        if (overlap.Any(static id => id is 0x312 or 0x4C5))
        {
            result.Add("Crit-rate overlaps");
        }
        if (overlap.Contains(0x08D))
        {
            result.Add("DH-rate overlaps");
        }
        if (overlap.Any(static id => id is 0x312 or 0x4C5) && overlap.Contains(0x08D))
        {
            result.Add("Crit + DH overlaps");
        }
        return result;
    }

    private static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }
        var ordered = values.Order().ToArray();
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2
            : ordered[middle];
    }

    private static double Quantile(IReadOnlyList<double> orderedValues, double probability)
    {
        if (orderedValues.Count == 0)
        {
            return 0;
        }
        var ordered = orderedValues.Order().ToArray();
        var position = Math.Clamp(probability, 0, 1) * (ordered.Length - 1);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        return lower == upper
            ? ordered[lower]
            : ordered[lower] + (ordered[upper] - ordered[lower]) * (position - lower);
    }

    private static double EtaSquared(
        IReadOnlyList<double> values,
        IReadOnlyList<string> groups,
        int minimumGroupSize)
    {
        if (values.Count != groups.Count || values.Count < 2)
        {
            return 0;
        }
        var retained = groups.Select((group, index) => (group, Value: values[index]))
            .GroupBy(static item => item.group, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() >= minimumGroupSize)
            .SelectMany(static group => group)
            .ToArray();
        if (retained.Length < 2)
        {
            return 0;
        }
        var mean = retained.Average(static item => item.Value);
        var total = retained.Sum(item => (item.Value - mean) * (item.Value - mean));
        if (total <= 0)
        {
            return 0;
        }
        var between = retained
            .GroupBy(static item => item.group, StringComparer.OrdinalIgnoreCase)
            .Sum(group =>
            {
                var groupMean = group.Average(static item => item.Value);
                return group.Count() * (groupMean - mean) * (groupMean - mean);
            });
        return between / total;
    }

    private static double Correlation(IReadOnlyList<double> left, IReadOnlyList<double> right)
    {
        if (left.Count != right.Count || left.Count < 2)
        {
            return 0;
        }
        var leftMean = left.Average();
        var rightMean = right.Average();
        var covariance = 0d;
        var leftVariance = 0d;
        var rightVariance = 0d;
        for (var index = 0; index < left.Count; index++)
        {
            var leftDelta = left[index] - leftMean;
            var rightDelta = right[index] - rightMean;
            covariance += leftDelta * rightDelta;
            leftVariance += leftDelta * leftDelta;
            rightVariance += rightDelta * rightDelta;
        }
        var denominator = Math.Sqrt(leftVariance * rightVariance);
        return denominator > 0 ? covariance / denominator : 0;
    }

    private sealed record GuaranteedEventAnalysis(
        double Timestamp,
        long ActionId,
        string ActionName,
        string PartnerActor,
        string PartnerJob,
        long RawDamage,
        ProbeGuaranteedDimensions Dimensions,
        string GuaranteeSource,
        double ProductionContribution,
        IReadOnlyDictionary<string, double> CandidateContributions,
        double CriticalChanceProxy,
        double DirectChanceProxy,
        IReadOnlyList<long> RateBuffIds,
        ProbeRateBuffExposure RateExposure);

    private sealed record AnalyzedFight(
        NormalizedFight Fight,
        ParitySampleResult Parity,
        IReadOnlyList<GuaranteedEventAnalysis> GuaranteedEvents,
        IReadOnlyDictionary<string, double> CandidateTotals,
        long GuaranteedRawDamage,
        int GuaranteedEventCount,
        long PartnerTotalRawDamage,
        double GuaranteedDamageShare,
        double CriticalChanceProxy,
        double DirectChanceProxy,
        double RateBuffOverlapFraction,
        IReadOnlyList<string> BuffConditions)
    {
        public double GuaranteedTotalRawRatio
            => PartnerTotalRawDamage > 0 ? (double)GuaranteedRawDamage / PartnerTotalRawDamage : 0;

        public long ResolveActionDamage(params long[] actionIds)
            => GuaranteedEvents.Where(item => actionIds.Contains(item.ActionId))
                .Sum(static item => item.RawDamage);

        public double ResolveRawWeightedExposure(Func<GuaranteedEventAnalysis, bool> predicate)
            => GuaranteedRawDamage > 0
                ? (double)GuaranteedEvents.Where(predicate).Sum(static item => item.RawDamage) /
                  GuaranteedRawDamage
                : 0;

        public double ResolveRawWeightedRate(
            Func<ProbeRateBuffExposure, double> selector)
            => GuaranteedRawDamage > 0
                ? GuaranteedEvents.Sum(item => selector(item.RateExposure) * item.RawDamage) /
                  GuaranteedRawDamage
                : 0;
    }
}

using DalamudActCompat.ActRuntime;

namespace DalamudActCompat.FflogsParityHarness;

internal static class GuaranteedHitAttributionExperiment
{
    private const int SelectedSamCount = 30;
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

        var eligibleSam = analyses
            .Where(static analysis => analysis.Parity.DancePartnerJob == "SAM" &&
                                      analysis.GuaranteedEvents.Count > 0)
            .ToArray();
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
            throw new InvalidOperationException(
                $"CurrentProduction offline calibration failed: event={calibration.MaximumAbsoluteEventResidual:R}, " +
                $"fight={calibration.MaximumAbsoluteFightResidual:R}.");
        }

        var fightResults = BuildFightResults(selected);
        var rankings = BuildRankings(selected);
        var best = rankings[0].Candidate;
        var (equationStatus, equationReason) = ResolveEquationStatus(
            eligibleSam,
            selected,
            rankings,
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
            ]);
    }

    private static AnalyzedFight AnalyzeFight(
        NormalizedFight fight,
        IReadOnlyDictionary<long, ProbeGuaranteedDimensions> stableGuarantees)
    {
        var parity = DactRdpsReplay.Replay(fight);
        var timeline = new FightAttributionTimeline(fight);
        var estimator = new RaidDpsEstimator(timeline.LifeSurgeWeaponskillActionIds.Contains);
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
        foreach (var item in fight.Events)
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
                dimensions);
            var candidateContributions = GuaranteedHitCandidateMath.Definitions.ToDictionary(
                static definition => definition.Name,
                definition =>
                {
                    var calculated = GuaranteedHitCandidateMath.Calculate(definition.Name, input);
                    return calculated.Critical + calculated.Direct;
                },
                StringComparer.Ordinal);
            guaranteedEvents.Add(new GuaranteedEventAnalysis(
                item.AbilityId,
                item.AbilityName,
                item.Amount,
                dimensions,
                sourceKind,
                productionContribution,
                candidateContributions,
                baseline.CriticalChance,
                baseline.DirectHitChance,
                state.RateBuffIds));
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

    private static (string Status, string Reason) ResolveEquationStatus(
        IReadOnlyList<AnalyzedFight> eligibleSam,
        IReadOnlyList<AnalyzedFight> selected,
        IReadOnlyList<GuaranteedHitCandidateRanking> rankings,
        string best)
    {
        var current = rankings.Single(static item =>
            item.Candidate == GuaranteedHitCandidateMath.CurrentProduction).Statistics;
        var winner = rankings[0].Statistics;
        var selectedKeys = selected.Select(static item =>
            (item.Fight.Seed.ReportCode, item.Fight.Fight.Id)).ToHashSet();
        var holdout = eligibleSam.Where(item => !selectedKeys.Contains(
            (item.Fight.Seed.ReportCode, item.Fight.Fight.Id))).ToArray();
        var holdoutWinner = CalculateStatistics(holdout, best);
        var holdoutCurrent = CalculateStatistics(holdout, GuaranteedHitCandidateMath.CurrentProduction);
        var holdoutBestCandidate = GuaranteedHitCandidateMath.Definitions
            .OrderBy(definition => CalculateStatistics(holdout, definition.Name).MeanAbsoluteResidual)
            .First().Name;
        var multiplePlayers = selected.Select(static item => item.Parity.Actor)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() >= 10;
        var multipleEncounters = selected.Select(static item => item.Parity.EncounterId).Distinct().Count() >= 3;
        var signStructured = Math.Max(winner.NegativeCount, winner.PositiveCount) <=
                             Math.Ceiling(winner.FightCount * 0.8);
        var familyStructured = ActionFamilies.All(family =>
        {
            var residuals = selected.Select(item => ResolveResidual(item, best)).ToArray();
            var damage = selected.Select(item => item.GuaranteedEvents
                .Where(action => family.Value.Contains(action.ActionId))
                .Sum(static action => (double)action.RawDamage)).ToArray();
            return Math.Abs(Correlation(residuals, damage)) <= 0.35;
        });
        var strong = best != GuaranteedHitCandidateMath.CurrentProduction &&
                     winner.MeanAbsoluteResidual <= current.MeanAbsoluteResidual * 0.65 &&
                     Math.Abs(winner.ResidualVsGuaranteedDamageCorrelation) <= 0.2 &&
                     holdoutBestCandidate == best &&
                     holdoutWinner.MeanAbsoluteResidual <= holdoutCurrent.MeanAbsoluteResidual * 0.65 &&
                     Math.Abs(holdoutWinner.ResidualVsGuaranteedDamageCorrelation) <= 0.2 &&
                     multiplePlayers && multipleEncounters && signStructured && familyStructured;
        return strong
            ? ("Strongly supported",
                "The best universal equation materially reduces MAE and removes the selected set's guaranteed-damage and action-family residual structure; aggregate-only evidence cannot elevate it to Confirmed.")
            : ("Not determined",
                "No candidate satisfies every cross-player, cross-encounter, sign-balance, guaranteed-damage, and action-family structural acceptance check. Aggregate totals can reject models but do not identify a unique hidden equation here.");
    }

    private static IReadOnlyList<string> BuildRemainingUnknowns(IReadOnlyList<AnalyzedFight> analyses)
    {
        var dimensionEvidence = BuildDimensionEvidence(analyses).ToDictionary(static item => item.Dimension);
        var result = new List<string>
        {
            "FFLogs actual Cu/Du is not exposed by the public API; DACT inferred snapshots are proxies only.",
            "Whether self-sourced Crit/DH rate buffs enter FFLogs Cb/Db denominators is not independently observable in this cache.",
            "FFLogs per-action and per-window Devilment allocations remain unavailable, so aggregate cancellation cannot be excluded.",
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
            return new GuaranteedHitResidualStatistics(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
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
            Correlation(residuals, analyses.Select(static item => item.RateBuffOverlapFraction).ToArray()));
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
        long ActionId,
        string ActionName,
        long RawDamage,
        ProbeGuaranteedDimensions Dimensions,
        string GuaranteeSource,
        double ProductionContribution,
        IReadOnlyDictionary<string, double> CandidateContributions,
        double CriticalChanceProxy,
        double DirectChanceProxy,
        IReadOnlyList<long> RateBuffIds);

    private sealed record AnalyzedFight(
        NormalizedFight Fight,
        ParitySampleResult Parity,
        IReadOnlyList<GuaranteedEventAnalysis> GuaranteedEvents,
        IReadOnlyDictionary<string, double> CandidateTotals,
        long GuaranteedRawDamage,
        int GuaranteedEventCount,
        double GuaranteedDamageShare,
        double CriticalChanceProxy,
        double DirectChanceProxy,
        double RateBuffOverlapFraction,
        IReadOnlyList<string> BuffConditions);
}

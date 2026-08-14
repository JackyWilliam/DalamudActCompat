using DalamudActCompat.ActRuntime;

namespace DalamudActCompat.FflogsParityHarness;

internal static class PercentageOrderingExperiment
{
    private const double ExactTolerance = 0.05;

    public static PercentageOrderingReport Run(
        FflogsSampleCollector collector,
        CacheManifest manifest,
        IReadOnlyList<CachedFightSample>? targetedSamples = null)
    {
        var samples = manifest.Seeds
            .Select(seed => (Sample: collector.ReadCachedSample(seed), Source: "existing-100-cache"))
            .Concat((targetedSamples ?? []).Select(sample =>
                (Sample: sample, Source: "targeted-matrix-cache")))
            .DistinctBy(static item => $"{item.Sample.Seed.ReportCode}:{item.Sample.Seed.FightId}")
            .ToArray();
        var constraints = new List<PercentageOrderingConstraintRow>();
        var events = new List<RateOverlapEventProbeRow>();
        var expiry = new List<StatusExpiryAuditRow>();
        var technical = new List<TechnicalEligibilityAuditRow>();
        var conservation = new List<PercentageRateConservationRow>();
        var percentageCalibration = new List<double>();
        var criticalCalibration = new List<double>();
        var directCalibration = new List<double>();
        var matchedInputs = new List<MatchedEventInput>();

        for (var index = 0; index < samples.Length; index++)
        {
            var (sample, source) = samples[index];
            var fight = FflogsEventNormalizer.NormalizeAttribution(sample);
            AnalyzeFight(
                fight,
                source,
                constraints,
                events,
                percentageCalibration,
                criticalCalibration,
                directCalibration,
                matchedInputs,
                conservation);
            BuildExpiryAudit(fight, expiry, technical);
            if ((index + 1) % 10 == 0 || index + 1 == samples.Length)
            {
                Console.WriteLine($"Percentage ordering analyzed {index + 1}/{samples.Length} fights.");
            }
        }

        var statistics = BuildStatistics(constraints);
        var providerComparison = BuildProviderComparison(constraints);
        var matched = BuildMatchedControls(matchedInputs);
        var rate = constraints.Where(static item => item.RateOverlapEventCount > 0).ToArray();
        var observed = CalculateStatistics(
            rate.Select(static item => item.ObservedPercentageFirstDelta));
        var rateFirst = CalculateStatistics(
            rate.Select(static item => item.ObservedRateFirstDelta));
        var shared = CalculateStatistics(
            rate.Select(static item => item.ObservedSharedShapleyDelta));
        var sharedLog = CalculateStatistics(
            rate.Select(static item => item.ObservedSharedLogDelta));
        var nominalSharedLog = CalculateStatistics(
            rate.Select(static item => item.NominalSharedLogDelta));
        var percentageStateCalibration = CalculateStatistics(percentageCalibration);
        var criticalStateCalibration = CalculateStatistics(criticalCalibration);
        var directStateCalibration = CalculateStatistics(directCalibration);
        var technicalObservedDamage = technical.Sum(static item => item.DamageObservedOnly);
        var technicalCurrentDamage = technical.Sum(static item => item.DamageCurrentOnly);
        return new PercentageOrderingReport(
            DateTimeOffset.UtcNow,
            samples.Length,
            constraints.Count,
            rate.Length,
            events.Count,
            PercentageOrderingCandidates.Definitions,
            BuildProductionPipeline(),
            percentageStateCalibration,
            criticalStateCalibration,
            directStateCalibration,
            constraints,
            events,
            statistics,
            providerComparison,
            matched,
            expiry,
            technical,
            conservation,
            [
                $"The actual CurrentProduction variant is read directly from production before/after counters. The independent timeline clone matches {percentageStateCalibration.ZeroCount}/{percentageStateCalibration.ConstraintCount} percentage, {criticalStateCalibration.ZeroCount}/{criticalStateCalibration.ConstraintCount} Crit, and {directStateCalibration.ZeroCount}/{directStateCalibration.ConstraintCount} DH contributing events; the nonzero state residuals prevent promoting the clone itself to an exact production oracle.",
                $"Observed percentage-first rate-overlap: N={observed.ConstraintCount}, mean={observed.MeanResidual:+0.000;-0.000;0.000}, MAE={observed.MeanAbsoluteResidual:F3}.",
                $"Observed rate-first rate-overlap: N={rateFirst.ConstraintCount}, mean={rateFirst.MeanResidual:+0.000;-0.000;0.000}, MAE={rateFirst.MeanAbsoluteResidual:F3}.",
                $"Observed shared-order Shapley rate-overlap: N={shared.ConstraintCount}, mean={shared.MeanResidual:+0.000;-0.000;0.000}, MAE={shared.MeanAbsoluteResidual:F3}.",
                $"Observed shared-log rate-overlap: N={sharedLog.ConstraintCount}, mean={sharedLog.MeanResidual:+0.000;-0.000;0.000}, MAE={sharedLog.MeanAbsoluteResidual:F3}.",
                $"Holding SharedBaseLog fixed, packet/nominal state has rate-overlap MAE={nominalSharedLog.MeanAbsoluteResidual:F3}; packet/explicit state reduces it to {sharedLog.MeanAbsoluteResidual:F3}. Ordering and state are therefore independent contributors.",
                $"Technical eligibility differs by {technical.Sum(static item => item.EventsObservedOnly)} observed-only events/{technicalObservedDamage} damage versus {technical.Sum(static item => item.EventsCurrentOnly)} nominal-only events/{technicalCurrentDamage} damage. Same-timestamp decisions use AttributionSequence rather than timestamp sorting.",
                "Current production is percentage-first: percentage uses N, every rate path uses N/M. It does not reuse the full N for rate contribution and therefore does not double-remove the interaction.",
                "All ordering variants conserve the same percentage+rate removal event by event. Their different component residuals are redistribution of the interaction, not creation or loss of damage.",
                "SharedBaseLog is the strongest cache-only diagnostic but retains signed structure, especially Crit+DH and multiple-provider groups. It is not accepted as the FFLogs rule and is not a production patch.",
            ],
            [
                "FFLogs exposes percentage contribution only at provider/recipient/fight aggregate. Event and window rows are forward predictions; no per-event FFLogs truth is fabricated.",
                "The ordering counterfactual deliberately retains the current production rate equation. Guaranteed-equation error can therefore confound guaranteed-event subgroups, but cannot explain normal-hit controls.",
                "ObservedEventState uses explicit status transitions and packet sequence. It is non-causal only in the narrow sense that an offline replay knows a later remove exists; production cannot infer a missing future packet.",
                "Periodic state uses application-time snapshots. Hit-time rows are diagnostic only and are not substituted into the main ordering candidates.",
                "RawDamage and EffectiveDamage are identical here because the normalized FFLogs damage amount is the validated effective numerator; the API cache has no separate per-event rDPS damage basis.",
            ]);
    }

    private static void AnalyzeFight(
        NormalizedAttributionFight fight,
        string sourceCache,
        ICollection<PercentageOrderingConstraintRow> results,
        ICollection<RateOverlapEventProbeRow> eventRows,
        ICollection<double> percentageCalibration,
        ICollection<double> criticalCalibration,
        ICollection<double> directCalibration,
        ICollection<MatchedEventInput> matchedInputs,
        ICollection<PercentageRateConservationRow> conservation)
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

        var partyIds = fight.Party.Select(static actor => actor.Id).ToHashSet();
        var stable = ProductionGuaranteedMetadata.ReadStableActions();
        var accumulators = new Dictionary<ConstraintKey, ConstraintAccumulator>();
        var recipientTotals = new Dictionary<int, RecipientCandidateTotals>();
        foreach (var item in DactRdpsReplay.OrderEventsForAttribution(fight.Events))
        {
            var timestamp = DactRdpsReplay.ToTimestamp(fight.ReportStartTime, item.Timestamp);
            if (FflogsEventNormalizer.IsStatusApply(item.Type))
            {
                if (item.AbilityId == 1822 && TryResolveTechnicalFinishAction(fight, item, out var finish))
                {
                    estimator.ObserveNetworkLine(
                        DactRdpsReplay.ToTimestamp(fight.ReportStartTime, finish.Timestamp),
                        DactRdpsReplay.BuildActionLine(finish, fight.Actors));
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
            if (source is null || owner is null || !partyIds.Contains(owner.Id) || partyIds.Contains(item.TargetId))
            {
                continue;
            }

            var baseline = estimator.ResolveHitBaseline(owner.Name);
            var currentState = timeline.Resolve(item, owner, AttributionTimelineSemantics.CausalRemoveGrace);
            var nominalState = timeline.Resolve(item, owner, AttributionTimelineSemantics.PacketOrdered);
            var observedState = timeline.Resolve(item, owner, AttributionTimelineSemantics.ObservedEventState);
            var authoritativeDimensions = timeline.ResolveGuaranteed(item, owner).Dimensions;
            var productionDimensions = stable.GetValueOrDefault(item.AbilityId);
            if (productionDimensions == ProbeGuaranteedDimensions.None &&
                authoritativeDimensions != ProbeGuaranteedDimensions.None &&
                timeline.ResolveGuaranteed(item, owner).Source is
                    "Reassemble contextual status" or "Life Surge contextual status")
            {
                productionDimensions = authoritativeDimensions;
            }

            var beforePercentage = estimator.ResolveReceivedDamage(
                owner.Name, RaidDpsEstimator.AttributionKind.Percentage);
            var beforeCritical = estimator.ResolveReceivedDamage(
                owner.Name, RaidDpsEstimator.AttributionKind.Critical);
            var beforeDirect = estimator.ResolveReceivedDamage(
                owner.Name, RaidDpsEstimator.AttributionKind.DirectHit);

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

            var measuredPercentage = estimator.ResolveReceivedDamage(
                owner.Name, RaidDpsEstimator.AttributionKind.Percentage) - beforePercentage;
            var measuredCritical = estimator.ResolveReceivedDamage(
                owner.Name, RaidDpsEstimator.AttributionKind.Critical) - beforeCritical;
            var measuredDirect = estimator.ResolveReceivedDamage(
                owner.Name, RaidDpsEstimator.AttributionKind.DirectHit) - beforeDirect;
            var offlineProductionPercentage = SumPercentage(
                item, currentState, PercentageCalculationMode.CurrentProduction);
            var offlineProductionRate = SumRate(
                item,
                currentState,
                baseline,
                productionDimensions,
                productionInputs: true);
            if (offlineProductionPercentage > 0 || measuredPercentage > 0)
                percentageCalibration.Add(offlineProductionPercentage - measuredPercentage);
            if (offlineProductionRate.Critical > 0 || measuredCritical > 0)
                criticalCalibration.Add(offlineProductionRate.Critical - measuredCritical);
            if (offlineProductionRate.Direct > 0 || measuredDirect > 0)
                directCalibration.Add(offlineProductionRate.Direct - measuredDirect);

            var observedPercentageBuffs = ResolveExternalPercentageBuffs(observedState, productionInputs: false);
            var currentPercentageBuffs = ResolveExternalPercentageBuffs(currentState, productionInputs: true);
            var nominalPercentageBuffs = ResolveExternalPercentageBuffs(nominalState, productionInputs: false);
            var observedRate = SumRate(
                item,
                observedState,
                baseline,
                authoritativeDimensions,
                productionInputs: false);
            var observedPercentageMultiplier = ResolveExternalPercentageMultiplier(
                observedState,
                productionInputs: false);
            var observedOrdering = CalculateOrderingTotals(
                item.Amount,
                observedPercentageMultiplier,
                observedRate.Critical + observedRate.Direct);
            var nominalRate = SumRate(
                item,
                nominalState,
                baseline,
                authoritativeDimensions,
                productionInputs: false);
            var nominalPercentageMultiplier = ResolveExternalPercentageMultiplier(
                nominalState,
                productionInputs: false);
            var nominalOrdering = CalculateOrderingTotals(
                item.Amount,
                nominalPercentageMultiplier,
                nominalRate.Critical + nominalRate.Direct);
            if (!recipientTotals.TryGetValue(owner.Id, out var recipientTotal))
            {
                recipientTotal = new RecipientCandidateTotals();
                recipientTotals.Add(owner.Id, recipientTotal);
            }
            recipientTotal.Observe(observedOrdering, observedRate.Critical + observedRate.Direct);
            var providers = currentPercentageBuffs
                .Concat(nominalPercentageBuffs)
                .Concat(observedPercentageBuffs)
                .DistinctBy(static buff => (buff.SourceActorId, buff.Definition.StatusId))
                .ToArray();
            if (providers.Length == 0)
            {
                continue;
            }

            var overlapGroups = ResolveOverlapGroups(observedState, authoritativeDimensions);

            foreach (var provider in providers)
            {
                var key = new ConstraintKey(owner.Id, provider.SourceActorId, provider.Definition.StatusId);
                if (!accumulators.TryGetValue(key, out var accumulator))
                {
                    accumulator = new ConstraintAccumulator(owner, provider, sourceCache);
                    accumulators.Add(key, accumulator);
                }

                var currentProvider = FindProvider(currentState, provider, productionInputs: true);
                var observedProvider = FindProvider(observedState, provider, productionInputs: false);
                var currentContribution = currentProvider is null
                    ? 0
                    : AttributionContributionMath.CalculatePercentageContribution(
                        item, currentState, currentProvider, PercentageCalculationMode.CurrentProduction);
                var observedFirst = observedProvider is null
                    ? 0
                    : AttributionContributionMath.CalculatePercentageContribution(
                        item, observedState, observedProvider, PercentageCalculationMode.AuthoritativeMetadata);
                var providerShare = observedProvider is null
                    ? 0
                    : ResolveProviderLogShare(
                        observedProvider.DamageMultiplier,
                        observedPercentageMultiplier);
                var nominalProvider = FindProvider(nominalState, provider, productionInputs: false);
                var nominalProviderShare = nominalProvider is null
                    ? 0
                    : ResolveProviderLogShare(
                        nominalProvider.DamageMultiplier,
                        nominalPercentageMultiplier);
                accumulator.Observe(
                    item,
                    currentContribution,
                    nominalOrdering.SharedLogPercentage * nominalProviderShare,
                    observedFirst,
                    observedOrdering.RateFirstPercentage * providerShare,
                    observedOrdering.SharedShapleyPercentage * providerShare,
                    observedOrdering.SharedLogPercentage * providerShare,
                    observedState,
                    authoritativeDimensions,
                    overlapGroups,
                    ResolveStateSignature(currentState) != ResolveStateSignature(observedState));
            }

            if (observedPercentageBuffs.Length > 0)
            {
                matchedInputs.Add(new MatchedEventInput(
                    fight.Seed.ReportCode,
                    fight.Fight.Id,
                    fight.Fight.Name,
                    owner.Name,
                    ToJobAbbreviation(owner.Job),
                    item.AbilityId,
                    item.AbilityName,
                    item.Amount,
                    string.Join(" + ", observedPercentageBuffs.Select(FormatBuff).Order()),
                    ResolveRateClass(observedState),
                    observedOrdering.PercentageFirstPercentage));
            }

            if (observedPercentageBuffs.Length > 0 && observedState.RateBuffs.Any(static buff => !buff.IsSelfSourced))
            {
                var currentM = ResolveExternalPercentageMultiplier(currentState, productionInputs: true);
                var currentTotal = CalculateOrderingTotals(
                    item.Amount,
                    currentM,
                    offlineProductionRate.Critical + offlineProductionRate.Direct);
                eventRows.Add(new RateOverlapEventProbeRow(
                    fight.Seed.ReportCode,
                    fight.Fight.Id,
                    fight.Fight.Name,
                    item.Timestamp,
                    item.AttributionSequence,
                    owner.Id,
                    owner.Name,
                    ToJobAbbreviation(owner.Job),
                    item.SourceId,
                    source.Name,
                    item.TargetId,
                    item.AbilityId,
                    item.AbilityName,
                    item.Amount,
                    item.Amount,
                    item.Critical,
                    item.DirectHit,
                    authoritativeDimensions == ProbeGuaranteedDimensions.Critical,
                    authoritativeDimensions == ProbeGuaranteedDimensions.DirectHit,
                    authoritativeDimensions ==
                        (ProbeGuaranteedDimensions.Critical | ProbeGuaranteedDimensions.DirectHit),
                    item.IsPeriodic,
                    string.Join(" + ", observedPercentageBuffs.Select(FormatBuff).Order()),
                    string.Join(" + ", ResolveRateBuffs(observedState, critical: true).Select(FormatBuff).Order()),
                    string.Join(" + ", ResolveRateBuffs(observedState, critical: false).Select(FormatBuff).Order()),
                    observedPercentageMultiplier,
                    measuredPercentage,
                    measuredCritical,
                    measuredDirect,
                    authoritativeDimensions == ProbeGuaranteedDimensions.None
                        ? 0
                        : measuredCritical + measuredDirect,
                    offlineProductionPercentage,
                    offlineProductionRate.Critical,
                    offlineProductionRate.Direct,
                    offlineProductionPercentage - measuredPercentage,
                    offlineProductionRate.Critical - measuredCritical,
                    offlineProductionRate.Direct - measuredDirect,
                    item.Amount,
                    currentM > 1 ? item.Amount / currentM : item.Amount,
                    observedRate.Critical + observedRate.Direct,
                    observedOrdering.RateOnRaw,
                    observedOrdering.PercentageFirstPercentage,
                    observedOrdering.RateFirstPercentage,
                    observedOrdering.SharedShapleyPercentage,
                    observedOrdering.SharedLogPercentage,
                    currentTotal.PercentageFirstPercentage + offlineProductionRate.Critical +
                        offlineProductionRate.Direct,
                    observedOrdering.RateFirstPercentage + observedOrdering.RateOnRaw,
                    observedOrdering.SharedShapleyPercentage + observedOrdering.SharedShapleyRate,
                    observedOrdering.SharedLogPercentage + observedOrdering.SharedLogRate,
                    string.Join(" + ", overlapGroups),
                    item.IsPeriodic ? "application_snapshot" : "packet_ordered_hit_state"));
            }
        }
        estimator.FinishEncounter();

        foreach (var accumulator in accumulators.Values)
        {
            if (!fight.DamageTableActors.TryGetValue(accumulator.Recipient.Id, out var tableActor))
            {
                continue;
            }
            var reference = tableActor.Taken
                .Where(item => item.AbilityId == accumulator.Provider.Definition.StatusId)
                .Sum(static item => item.Amount);
            if (reference > 0)
            {
                var built = accumulator.Build(fight, reference);
                results.Add(built);
            }
        }

        foreach (var actor in fight.Party)
        {
            if (!fight.DamageTableActors.TryGetValue(actor.Id, out var tableActor))
            {
                continue;
            }
            var ffPercentage = tableActor.Taken
                .Where(static item => OffensiveBuffRegistry.ByStatusId.TryGetValue(
                    item.AbilityId, out var definition) &&
                    definition.Dimension == OffensiveBuffDimension.PercentageDamage &&
                    definition.DamageMultiplier is not null)
                .Sum(static item => item.Amount);
            var ffRate = tableActor.Taken
                .Where(static item => OffensiveBuffRegistry.ByStatusId.TryGetValue(
                    item.AbilityId, out var definition) &&
                    definition.Dimension != OffensiveBuffDimension.PercentageDamage)
                .Sum(static item => item.Amount);
            if (ffPercentage <= 0 && ffRate <= 0)
            {
                continue;
            }
            var totals = recipientTotals.GetValueOrDefault(actor.Id) ?? new RecipientCandidateTotals();
            var productionPercentage = estimator.ResolveReceivedDamage(
                actor.Name, RaidDpsEstimator.AttributionKind.Percentage);
            var productionCritical = estimator.ResolveReceivedDamage(
                actor.Name, RaidDpsEstimator.AttributionKind.Critical);
            var productionDirect = estimator.ResolveReceivedDamage(
                actor.Name, RaidDpsEstimator.AttributionKind.DirectHit);
            var ffCombined = ffPercentage + ffRate;
            var productionCombined = productionPercentage + productionCritical + productionDirect;
            conservation.Add(new PercentageRateConservationRow(
                fight.Seed.ReportCode,
                fight.Fight.Id,
                fight.Fight.Name,
                actor.Id,
                actor.Name,
                ToJobAbbreviation(actor.Job),
                ffPercentage,
                ffRate,
                ffCombined,
                productionPercentage,
                productionCritical,
                productionDirect,
                productionCombined,
                productionCombined - ffCombined,
                totals.PercentageFirst,
                totals.RateFirst,
                totals.SharedShapley,
                totals.SharedLog,
                totals.RateAfterPercentage,
                totals.PercentageFirst + totals.RateAfterPercentage - ffCombined,
                totals.RateFirst + totals.RateOnRaw - ffCombined,
                totals.SharedShapley + totals.SharedShapleyRate - ffCombined,
                totals.SharedLog + totals.SharedLogRate - ffCombined));
        }
    }

    private static IReadOnlyList<string> BuildProductionPipeline()
        =>
        [
            "N_raw = EffectiveDamageEvent.Damage. In the FFLogs replay this is the validated normalized event amount; raw actor totals remain exact.",
            "ResolveExternalBuffs(timestamp, owner/source/target) removes self-sourced statuses before component arrays are formed.",
            "Direct hits use hit-time percentage/rate state. Periodic hits use the application snapshot when one exists; unmatched periodic normalization keeps an empty percentage fallback.",
            "M = product of external fixed percentage multipliers. N_after_percentage_removal = N_raw / M.",
            "Percentage total P = N_raw - N_raw/M; provider i receives P*ln(mi)/ln(M). Thus N_used_for_percentage_allocation = N_raw.",
            "Cu/Du are resolved immediately after percentage transfer. The baseline is not changed by percentage removal.",
            "Regular Crit and DH, simulated DoT, and guaranteed Crit/DH all receive N_after_percentage_removal. Thus N_used_for_regular_crit = N_used_for_regular_dh = N_used_for_guaranteed = N_raw/M.",
            "TransferContribution subtracts each component from the recipient adjustment and adds the same amount to the provider. Per-kind counters do not change the event damage numerator.",
            "Final actor rDPS applies the accumulated adjustment to raw damage. Current ordering conserves P + R(N/M); it contains no second percentage removal.",
        ];

    private static (double Critical, double Direct) SumRate(
        NormalizedFflogsEvent item,
        MatrixEventAttributionState state,
        HitBaselineSnapshot baseline,
        ProbeGuaranteedDimensions dimensions,
        bool productionInputs)
    {
        var critical = 0d;
        var direct = 0d;
        foreach (var provider in state.RateBuffs
                     .Where(static buff => !buff.IsSelfSourced)
                     .Where(buff => !productionInputs || buff.Definition.CoveredByProduction))
        {
            var parts = AttributionContributionMath.CalculateRateContributionParts(
                GuaranteedHitCandidateMath.CurrentProduction,
                item,
                state,
                provider,
                baseline.CriticalChance,
                baseline.DirectHitChance,
                dimensions,
                productionInputs);
            critical += parts.Critical;
            direct += parts.Direct;
        }
        return (critical, direct);
    }

    private static double SumPercentage(
        NormalizedFflogsEvent item,
        MatrixEventAttributionState state,
        PercentageCalculationMode mode)
        => ResolveExternalPercentageBuffs(
                state,
                mode == PercentageCalculationMode.CurrentProduction)
            .Sum(provider => AttributionContributionMath.CalculatePercentageContribution(
                item, state, provider, mode));

    private static OrderingTotals CalculateOrderingTotals(
        double damage,
        double percentageMultiplier,
        double rateAfterPercentage)
    {
        if (damage <= 0 || percentageMultiplier <= 1)
        {
            return new OrderingTotals(
                0,
                0,
                0,
                0,
                rateAfterPercentage,
                rateAfterPercentage,
                rateAfterPercentage);
        }
        var afterPercentage = damage / percentageMultiplier;
        var percentageFirst = damage - afterPercentage;
        var rateOnRaw = Math.Clamp(rateAfterPercentage * percentageMultiplier, 0, damage);
        var afterRate = Math.Max(0, damage - rateOnRaw);
        var rateFirst = afterRate - afterRate / percentageMultiplier;
        var sharedShapley = (percentageFirst + rateFirst) / 2;
        var sharedShapleyRate = percentageFirst + rateAfterPercentage - sharedShapley;
        var afterBoth = Math.Max(0, afterPercentage - rateAfterPercentage);
        var totalRemoval = damage - afterBoth;
        var rateMultiplier = afterBoth > 0 ? afterPercentage / afterBoth : double.PositiveInfinity;
        var combinedLog = double.IsFinite(rateMultiplier)
            ? Math.Log(percentageMultiplier * rateMultiplier)
            : 0;
        var sharedLog = combinedLog > 0
            ? totalRemoval * Math.Log(percentageMultiplier) / combinedLog
            : percentageFirst;
        return new OrderingTotals(
            percentageFirst,
            rateFirst,
            sharedShapley,
            sharedLog,
            rateOnRaw,
            sharedShapleyRate,
            totalRemoval - sharedLog);
    }

    internal static (double PercentageFirst, double RateFirst, double SharedShapley,
        double SharedLog, double CurrentTotal, double RateFirstTotal,
        double SharedShapleyTotal, double SharedLogTotal) CalculateOrderingTotalsForTest(
        double damage,
        double percentageMultiplier,
        double rateAfterPercentage)
    {
        var result = CalculateOrderingTotals(damage, percentageMultiplier, rateAfterPercentage);
        return (
            result.PercentageFirstPercentage,
            result.RateFirstPercentage,
            result.SharedShapleyPercentage,
            result.SharedLogPercentage,
            result.PercentageFirstPercentage + rateAfterPercentage,
            result.RateFirstPercentage + result.RateOnRaw,
            result.SharedShapleyPercentage + result.SharedShapleyRate,
            result.SharedLogPercentage + result.SharedLogRate);
    }

    private static MatrixBuffExposureEntry[] ResolveExternalPercentageBuffs(
        MatrixEventAttributionState state,
        bool productionInputs)
        => state.Buffs
            .Where(static buff => !buff.IsSelfSourced &&
                                  buff.Definition.Dimension == OffensiveBuffDimension.PercentageDamage &&
                                  buff.DamageMultiplier > 1)
            .Where(buff => !productionInputs || buff.Definition.CoveredByProduction)
            .ToArray();

    private static double ResolveExternalPercentageMultiplier(
        MatrixEventAttributionState state,
        bool productionInputs)
        => ResolveExternalPercentageBuffs(state, productionInputs)
            .Aggregate(1d, static (current, buff) => current * buff.DamageMultiplier);

    private static MatrixBuffExposureEntry? FindProvider(
        MatrixEventAttributionState state,
        MatrixBuffExposureEntry expected,
        bool productionInputs)
        => ResolveExternalPercentageBuffs(state, productionInputs).FirstOrDefault(buff =>
            buff.SourceActorId == expected.SourceActorId &&
            buff.Definition.StatusId == expected.Definition.StatusId);

    private static double ResolveProviderLogShare(double providerMultiplier, double combinedMultiplier)
        => providerMultiplier > 1 && combinedMultiplier > 1
            ? Math.Log(providerMultiplier) / Math.Log(combinedMultiplier)
            : 0;

    private static MatrixBuffExposureEntry[] ResolveRateBuffs(
        MatrixEventAttributionState state,
        bool critical)
        => state.RateBuffs
            .Where(static buff => !buff.IsSelfSourced)
            .Where(buff => critical
                ? buff.Definition.CriticalRateIncrease > 0
                : buff.Definition.DirectHitRateIncrease > 0)
            .ToArray();

    private static IReadOnlyList<string> ResolveOverlapGroups(
        MatrixEventAttributionState state,
        ProbeGuaranteedDimensions dimensions)
    {
        var critical = ResolveRateBuffs(state, critical: true);
        var direct = ResolveRateBuffs(state, critical: false);
        var result = new List<string>();
        if (critical.Length > 0 && direct.Length == 0) result.Add("A.Percentage+Crit-only");
        if (critical.Length == 0 && direct.Length > 0) result.Add("B.Percentage+DH-only");
        if (critical.Length > 0 && direct.Length > 0) result.Add("C.Percentage+Crit+DH");
        if (critical.Select(static buff => buff.SourceActorId).Distinct().Count() > 1)
            result.Add("D.Percentage+multiple Crit providers");
        if (direct.Select(static buff => buff.SourceActorId).Distinct().Count() > 1)
            result.Add("E.Percentage+multiple DH providers");
        if (critical.Length > 0 && direct.Length > 0 &&
            !critical.Select(static buff => buff.SourceActorId)
                .Intersect(direct.Select(static buff => buff.SourceActorId)).Any())
            result.Add("F.Percentage+Crit+DH different providers");
        if (dimensions == ProbeGuaranteedDimensions.Critical)
            result.Add("G.Percentage+guaranteed Crit");
        if (dimensions == ProbeGuaranteedDimensions.DirectHit)
            result.Add("H1.Percentage+guaranteed DH");
        if (dimensions == (ProbeGuaranteedDimensions.Critical | ProbeGuaranteedDimensions.DirectHit))
            result.Add("H2.Percentage+guaranteed CDH");
        if (result.Count == 0) result.Add("No external rate");
        return result;
    }

    private static string ResolveRateClass(MatrixEventAttributionState state)
    {
        var critical = ResolveRateBuffs(state, critical: true).Length > 0;
        var direct = ResolveRateBuffs(state, critical: false).Length > 0;
        return (critical, direct) switch
        {
            (false, false) => "PercentageOnly",
            (true, false) => "CriticalOnly",
            (false, true) => "DirectOnly",
            _ => "CriticalAndDirect",
        };
    }

    private static IReadOnlyList<PercentageOrderingStatisticsRow> BuildStatistics(
        IReadOnlyList<PercentageOrderingConstraintRow> constraints)
    {
        var result = new List<PercentageOrderingStatisticsRow>();
        AddScope(result, "All", "All", constraints);
        AddScope(result, "RatePresence", "NoRate",
            constraints.Where(static item => item.RateOverlapEventCount == 0));
        AddScope(result, "RatePresence", "RateOverlap",
            constraints.Where(static item => item.RateOverlapEventCount > 0));
        AddScope(result, "EventKind", "DirectOnly",
            constraints.Where(static item => item.PeriodicEventCount == 0));
        AddScope(result, "EventKind", "ContainsPeriodic",
            constraints.Where(static item => item.PeriodicEventCount > 0));
        AddScope(result, "StateEligibility", "Stable",
            constraints.Where(static item => item.StateDifferenceEventCount == 0));
        AddScope(result, "StateEligibility", "BoundaryDifference",
            constraints.Where(static item => item.StateDifferenceEventCount > 0));
        AddScope(result, "RateDirect", "NormalOnly",
            constraints.Where(static item => item.RateOverlapEventCount > 0 &&
                                             item.PeriodicEventCount == 0 &&
                                             item.GuaranteedCriticalEventCount == 0 &&
                                             item.GuaranteedDirectHitEventCount == 0 &&
                                             item.GuaranteedCriticalDirectHitEventCount == 0));
        foreach (var group in constraints.SelectMany(ParseGroups).Distinct().Order())
        {
            AddScope(result, "OverlapGroup", group,
                constraints.Where(item => ParseGroups(item).Contains(group)));
        }
        foreach (var provider in constraints.Select(static item => item.BuffName).Distinct().Order())
        {
            AddScope(result, "Provider", provider,
                constraints.Where(item => item.BuffName == provider));
        }
        return result;
    }

    private static void AddScope(
        ICollection<PercentageOrderingStatisticsRow> destination,
        string scope,
        string value,
        IEnumerable<PercentageOrderingConstraintRow> source)
    {
        var rows = source.ToArray();
        foreach (var candidate in PercentageOrderingCandidates.Definitions)
        {
            destination.Add(new PercentageOrderingStatisticsRow(
                scope,
                value,
                candidate.Name,
                CalculateStatistics(rows.Select(item => ResolveDelta(item, candidate.Name)))));
        }
    }

    private static double ResolveDelta(PercentageOrderingConstraintRow item, string candidate)
        => candidate switch
        {
            PercentageOrderingCandidates.CurrentProduction => item.CurrentProductionDelta,
            PercentageOrderingCandidates.NominalSharedLog => item.NominalSharedLogDelta,
            PercentageOrderingCandidates.ObservedPercentageFirst => item.ObservedPercentageFirstDelta,
            PercentageOrderingCandidates.ObservedRateFirst => item.ObservedRateFirstDelta,
            PercentageOrderingCandidates.ObservedSharedShapley => item.ObservedSharedShapleyDelta,
            PercentageOrderingCandidates.ObservedSharedLog => item.ObservedSharedLogDelta,
            _ => throw new ArgumentOutOfRangeException(nameof(candidate), candidate, "Unknown ordering candidate."),
        };

    private static IReadOnlyList<PercentageProviderRateComparisonRow> BuildProviderComparison(
        IReadOnlyList<PercentageOrderingConstraintRow> constraints)
        => constraints.GroupBy(static item => (item.ProviderJob, item.BuffName))
            .Select(group =>
            {
                var noRate = group.Where(static item => item.RateOverlapEventCount == 0).ToArray();
                var rate = group.Where(static item => item.RateOverlapEventCount > 0).ToArray();
                return new PercentageProviderRateComparisonRow(
                    group.Key.ProviderJob,
                    group.Key.BuffName,
                    noRate.Length,
                    CalculateStatistics(noRate.Select(static item => item.ObservedPercentageFirstDelta)),
                    rate.Length,
                    CalculateStatistics(rate.Select(static item => item.ObservedPercentageFirstDelta)));
            })
            .OrderByDescending(static item => item.RateOverlap.MeanAbsoluteResidual)
            .ToArray();

    private static IReadOnlyList<PercentageMatchedControlRow> BuildMatchedControls(
        IReadOnlyList<MatchedEventInput> inputs)
        => inputs.GroupBy(static item => new
        {
            item.Actor,
            item.Job,
            item.Report,
            item.FightId,
            item.Encounter,
            item.ActionId,
            item.ActionName,
            item.PercentageComposition,
        })
            .Select(group =>
            {
                var values = group.ToArray();
                var percentageOnly = values.Where(static item => item.RateClass == "PercentageOnly").ToArray();
                var critical = values.Where(static item => item.RateClass == "CriticalOnly").ToArray();
                var direct = values.Where(static item => item.RateClass == "DirectOnly").ToArray();
                var both = values.Where(static item => item.RateClass == "CriticalAndDirect").ToArray();
                return new PercentageMatchedControlRow(
                    group.Key.Actor,
                    group.Key.Job,
                    group.Key.Report,
                    group.Key.FightId,
                    group.Key.Encounter,
                    group.Key.ActionId,
                    group.Key.ActionName,
                    group.Key.PercentageComposition,
                    percentageOnly.Length,
                    critical.Length,
                    direct.Length,
                    both.Length,
                    MeanPerDamage(percentageOnly),
                    MeanPerDamage(critical),
                    MeanPerDamage(direct),
                    MeanPerDamage(both),
                    "A: same actor/report/fight/encounter/action family",
                    "FFLogs aggregate only; pair proves state-dependent forward basis, not per-event residual");
            })
            .Where(static item => item.PercentageOnlyEvents > 0 &&
                                  item.CriticalOnlyEvents + item.DirectOnlyEvents +
                                      item.CriticalAndDirectEvents > 0)
            .OrderByDescending(static item => item.PercentageOnlyEvents + item.CriticalOnlyEvents +
                                              item.DirectOnlyEvents + item.CriticalAndDirectEvents)
            .ToArray();

    private static double MeanPerDamage(IEnumerable<MatchedEventInput> values)
    {
        var rows = values.ToArray();
        var damage = rows.Sum(static item => item.Damage);
        return damage > 0 ? rows.Sum(static item => item.PercentageContribution) / damage : 0;
    }

    private static MatrixResidualStatistics CalculateStatistics(IEnumerable<double> source)
    {
        var values = source.ToArray();
        if (values.Length == 0)
        {
            return new MatrixResidualStatistics(0, 0, 0, 0, 0, 0, 0, 0, 0);
        }
        var ordered = values.Order().ToArray();
        var median = ordered.Length % 2 == 1
            ? ordered[ordered.Length / 2]
            : (ordered[ordered.Length / 2 - 1] + ordered[ordered.Length / 2]) / 2;
        return new MatrixResidualStatistics(
            values.Length,
            values.Average(),
            median,
            values.Average(static value => Math.Abs(value)),
            Math.Sqrt(values.Average(static value => value * value)),
            values.Max(static value => Math.Abs(value)),
            values.Count(static value => value < -ExactTolerance),
            values.Count(static value => Math.Abs(value) <= ExactTolerance),
            values.Count(static value => value > ExactTolerance));
    }

    private static IReadOnlyList<string> ParseGroups(PercentageOrderingConstraintRow item)
        => item.OverlapGroups.Split(" | ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool TryResolveTechnicalFinishAction(
        NormalizedAttributionFight fight,
        NormalizedFflogsEvent status,
        out NormalizedFflogsEvent action)
    {
        var resolved = fight.Events
            .Where(candidate => candidate.SourceId == status.SourceId &&
                                candidate.AbilityId is 0x81C1 or 0x81C2 &&
                                Math.Abs(candidate.Timestamp - status.Timestamp) <= 2000)
            .OrderBy(candidate => Math.Abs(candidate.Timestamp - status.Timestamp))
            .FirstOrDefault();
        action = resolved!;
        return resolved is not null;
    }

    private static string FormatBuff(MatrixBuffExposureEntry buff)
        => $"{buff.SourceJob}:{buff.Definition.ActionName}:{buff.Definition.Magnitude}";

    private static string ResolveStateSignature(MatrixEventAttributionState state)
        => string.Join("|", state.Buffs
            .Where(static buff => !buff.IsSelfSourced)
            .Select(static buff => $"{buff.SourceActorId}:{buff.Definition.StatusId}:{buff.DamageMultiplier:R}")
            .Order(StringComparer.Ordinal));

    private static string ToJobAbbreviation(string job)
        => job switch
        {
            "Paladin" => "PLD",
            "Warrior" => "WAR",
            "DarkKnight" => "DRK",
            "Gunbreaker" => "GNB",
            "Monk" => "MNK",
            "Dragoon" => "DRG",
            "Ninja" => "NIN",
            "Samurai" => "SAM",
            "Reaper" => "RPR",
            "Viper" => "VPR",
            "Bard" => "BRD",
            "Machinist" => "MCH",
            "Dancer" => "DNC",
            "BlackMage" => "BLM",
            "Summoner" => "SMN",
            "RedMage" => "RDM",
            "Pictomancer" => "PCT",
            "WhiteMage" => "WHM",
            "Scholar" => "SCH",
            "Astrologian" => "AST",
            "Sage" => "SGE",
            _ => job.ToUpperInvariant(),
        };

    private static string ResolveProviderType(OffensiveBuffDefinition definition)
        => definition.DebuffOnEnemy
            ? "EnemyTargetDebuff"
            : definition.ActionName.StartsWith("Standard Finish", StringComparison.Ordinal)
                ? "PartnerScoped"
                : definition.SingleTarget
                    ? "SingleTarget"
                    : definition.PartyWide
                        ? "PartyWide"
                        : "OtherScoped";

    private static void BuildExpiryAudit(
        NormalizedAttributionFight fight,
        ICollection<StatusExpiryAuditRow> expiry,
        ICollection<TechnicalEligibilityAuditRow> technical)
    {
        var intervals = BuildObservedIntervals(fight);
        foreach (var interval in intervals.Where(static item =>
                     OffensiveBuffRegistry.ByStatusId.TryGetValue(item.StatusId, out var definition) &&
                     definition.Dimension == OffensiveBuffDimension.PercentageDamage &&
                     definition.DamageMultiplier is not null))
        {
            var owner = fight.Actors.GetValueOrDefault(interval.TargetId);
            var source = fight.Actors.GetValueOrDefault(interval.SourceId);
            var matchingDamage = fight.Events
                .Where(static item => FflogsEventNormalizer.IsDamageEvent(item) && item.Amount > 0)
                .Where(item => FflogsEventNormalizer.ResolveOwnerActor(item.SourceId, fight.Actors)?.Id == interval.TargetId ||
                               item.TargetId == interval.TargetId)
                .ToArray();
            var between = matchingDamage.Where(item => IsBetweenDifferentEnds(item, interval)).ToArray();
            var sameApplyBefore = matchingDamage.Count(item =>
                item.Timestamp == interval.Start && item.AttributionSequence < interval.StartSequence);
            var sameApplyAfter = matchingDamage.Count(item =>
                item.Timestamp == interval.Start && item.AttributionSequence >= interval.StartSequence);
            var sameRemoveBefore = matchingDamage.Count(item =>
                item.Timestamp == interval.ObservedEnd && item.AttributionSequence < interval.EndSequence);
            var sameRemoveAfter = matchingDamage.Count(item =>
                item.Timestamp == interval.ObservedEnd && item.AttributionSequence >= interval.EndSequence);
            var definition = OffensiveBuffRegistry.ByStatusId[interval.StatusId];
            expiry.Add(new StatusExpiryAuditRow(
                fight.Seed.ReportCode,
                fight.Fight.Id,
                interval.StatusId,
                definition.ActionName,
                source?.Name ?? $"Actor {interval.SourceId}",
                owner?.Name ?? $"Actor {interval.TargetId}",
                interval.Start,
                interval.NominalEnd,
                interval.ObservedEnd,
                interval.EndSequence,
                interval.ExplicitRemove,
                interval.RefreshOrOverwrite,
                interval.ClearedByDeath,
                between.Length,
                between.Sum(static item => item.Amount),
                sameApplyBefore,
                sameApplyAfter,
                sameRemoveBefore,
                sameRemoveAfter,
                interval.ObservedEnd > interval.NominalEnd
                    ? "observed state includes post-nominal packets"
                    : interval.ObservedEnd < interval.NominalEnd
                        ? "explicit transition removes pre-nominal packets"
                        : "nominal and observed endpoints agree"));

            if (interval.StatusId == 1822)
            {
                var currentOnly = matchingDamage.Where(item =>
                    IsActive(item, interval.Start, interval.StartSequence, interval.NominalEnd, long.MinValue) &&
                    !IsActive(item, interval.Start, interval.StartSequence, interval.ObservedEnd, interval.EndSequence))
                    .ToArray();
                var observedOnly = matchingDamage.Where(item =>
                    !IsActive(item, interval.Start, interval.StartSequence, interval.NominalEnd, long.MinValue) &&
                    IsActive(item, interval.Start, interval.StartSequence, interval.ObservedEnd, interval.EndSequence))
                    .ToArray();
                technical.Add(new TechnicalEligibilityAuditRow(
                    fight.Seed.ReportCode,
                    fight.Fight.Id,
                    interval.SourceId,
                    interval.TargetId,
                    interval.Start,
                    interval.NominalEnd,
                    interval.ObservedEnd,
                    currentOnly.Length,
                    currentOnly.Sum(static item => item.Amount),
                    observedOnly.Length,
                    observedOnly.Sum(static item => item.Amount),
                    sameApplyBefore,
                    sameApplyAfter,
                    sameRemoveBefore,
                    sameRemoveAfter,
                    "unavailable per event; provider/recipient/fight taken[] is the aggregate reference"));
            }
        }
    }

    private static IReadOnlyList<ObservedStatusInterval> BuildObservedIntervals(
        NormalizedAttributionFight fight)
    {
        var result = new List<ObservedStatusInterval>();
        var active = new Dictionary<(long StatusId, int SourceId, int TargetId), ObservedStatusInterval>();
        foreach (var item in DactRdpsReplay.OrderEventsForAttribution(fight.Events))
        {
            if (string.Equals(item.Type, "death", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var deathKey in active.Keys.Where(key => key.TargetId == item.TargetId).ToArray())
                {
                    active[deathKey].Close(item.Timestamp, item.AttributionSequence, death: true);
                    active.Remove(deathKey);
                }
                continue;
            }
            var key = (item.AbilityId, item.SourceId, item.TargetId);
            if (FflogsEventNormalizer.IsStatusApply(item.Type))
            {
                if (active.Remove(key, out var prior))
                {
                    prior.Close(item.Timestamp, item.AttributionSequence, refresh: true);
                }
                var duration = item.DurationMilliseconds > 0
                    ? item.DurationMilliseconds
                    : fight.Fight.EndTime - item.Timestamp;
                var interval = new ObservedStatusInterval(
                    item.AbilityId,
                    item.SourceId,
                    item.TargetId,
                    item.Timestamp,
                    item.AttributionSequence,
                    Math.Min(fight.Fight.EndTime, item.Timestamp + Math.Max(1, duration)));
                active.Add(key, interval);
                result.Add(interval);
            }
            else if (FflogsEventNormalizer.IsStatusRemove(item.Type) &&
                     item.Type is not "removebuffstack" and not "removedebuffstack" &&
                     active.Remove(key, out var removed))
            {
                removed.Close(item.Timestamp, item.AttributionSequence, explicitRemove: true);
            }
        }
        foreach (var interval in active.Values)
        {
            interval.Close(interval.NominalEnd, long.MinValue);
        }
        return result;
    }

    private static bool IsBetweenDifferentEnds(
        NormalizedFflogsEvent item,
        ObservedStatusInterval interval)
        => interval.ObservedEnd > interval.NominalEnd
            ? item.Timestamp >= interval.NominalEnd &&
              IsActive(item, interval.Start, interval.StartSequence, interval.ObservedEnd, interval.EndSequence)
            : item.Timestamp >= interval.ObservedEnd && item.Timestamp < interval.NominalEnd;

    private static bool IsActive(
        NormalizedFflogsEvent item,
        double start,
        long startSequence,
        double end,
        long endSequence)
    {
        var afterStart = item.Timestamp > start ||
                         item.Timestamp == start && item.AttributionSequence >= startSequence;
        var beforeEnd = item.Timestamp < end ||
                        item.Timestamp == end && item.AttributionSequence < endSequence;
        return afterStart && beforeEnd;
    }

    private readonly record struct ConstraintKey(int RecipientId, int ProviderId, long StatusId);

    private sealed class ConstraintAccumulator
    {
        private readonly string sourceCache;
        private readonly HashSet<string> rateComposition = new(StringComparer.Ordinal);
        private readonly HashSet<string> overlapGroups = new(StringComparer.Ordinal);
        private double current;
        private double nominalSharedLog;
        private double observedFirst;
        private double observedRateFirst;
        private double observedShapley;
        private double observedLog;
        private int eventCount;
        private int directCount;
        private int periodicCount;
        private int rateCount;
        private int stateDifferenceCount;
        private int guaranteedCriticalCount;
        private int guaranteedDirectCount;
        private int guaranteedCombinedCount;
        private long eligibleDamage;
        private long rateDamage;

        public ConstraintAccumulator(
            FflogsActor recipient,
            MatrixBuffExposureEntry provider,
            string sourceCache)
        {
            Recipient = recipient;
            Provider = provider;
            this.sourceCache = sourceCache;
        }

        public FflogsActor Recipient { get; }
        public MatrixBuffExposureEntry Provider { get; }

        public void Observe(
            NormalizedFflogsEvent item,
            double currentContribution,
            double nominalSharedLogContribution,
            double observedFirstContribution,
            double observedRateFirstContribution,
            double observedShapleyContribution,
            double observedLogContribution,
            MatrixEventAttributionState state,
            ProbeGuaranteedDimensions dimensions,
            IEnumerable<string> groups,
            bool stateDifferent)
        {
            current += currentContribution;
            nominalSharedLog += nominalSharedLogContribution;
            observedFirst += observedFirstContribution;
            observedRateFirst += observedRateFirstContribution;
            observedShapley += observedShapleyContribution;
            observedLog += observedLogContribution;
            eventCount++;
            eligibleDamage += item.Amount;
            if (item.IsPeriodic) periodicCount++; else directCount++;
            if (state.RateBuffs.Any(static buff => !buff.IsSelfSourced))
            {
                rateCount++;
                rateDamage += item.Amount;
            }
            if (stateDifferent) stateDifferenceCount++;
            if (dimensions == ProbeGuaranteedDimensions.Critical) guaranteedCriticalCount++;
            if (dimensions == ProbeGuaranteedDimensions.DirectHit) guaranteedDirectCount++;
            if (dimensions == (ProbeGuaranteedDimensions.Critical | ProbeGuaranteedDimensions.DirectHit))
                guaranteedCombinedCount++;
            foreach (var buff in state.RateBuffs.Where(static buff => !buff.IsSelfSourced))
                rateComposition.Add(FormatBuff(buff));
            foreach (var group in groups) overlapGroups.Add(group);
        }

        public PercentageOrderingConstraintRow Build(NormalizedAttributionFight fight, double reference)
            => new(
                fight.Seed.ReportCode,
                fight.Fight.Id,
                fight.Fight.EncounterId,
                fight.Fight.Name,
                fight.PartyComposition,
                ResolveProviderType(Provider.Definition),
                Provider.SourceActorId,
                Provider.SourceActor,
                Provider.SourceJob,
                Recipient.Id,
                Recipient.Name,
                ToJobAbbreviation(Recipient.Job),
                Provider.Definition.StatusId,
                Provider.Definition.ActionName,
                reference,
                current,
                nominalSharedLog,
                observedFirst,
                observedRateFirst,
                observedShapley,
                observedLog,
                current - reference,
                nominalSharedLog - reference,
                observedFirst - reference,
                observedRateFirst - reference,
                observedShapley - reference,
                observedLog - reference,
                eventCount,
                directCount,
                periodicCount,
                rateCount,
                stateDifferenceCount,
                guaranteedCriticalCount,
                guaranteedDirectCount,
                guaranteedCombinedCount,
                eligibleDamage,
                rateDamage,
                string.Join(" + ", rateComposition.Order()),
                string.Join(" | ", overlapGroups.Order()),
                sourceCache);
    }

    private sealed class ObservedStatusInterval(
        long statusId,
        int sourceId,
        int targetId,
        double start,
        long startSequence,
        double nominalEnd)
    {
        public long StatusId { get; } = statusId;
        public int SourceId { get; } = sourceId;
        public int TargetId { get; } = targetId;
        public double Start { get; } = start;
        public long StartSequence { get; } = startSequence;
        public double NominalEnd { get; } = nominalEnd;
        public double ObservedEnd { get; private set; } = nominalEnd;
        public long EndSequence { get; private set; } = long.MinValue;
        public bool ExplicitRemove { get; private set; }
        public bool RefreshOrOverwrite { get; private set; }
        public bool ClearedByDeath { get; private set; }

        public void Close(
            double timestamp,
            long sequence,
            bool explicitRemove = false,
            bool refresh = false,
            bool death = false)
        {
            ObservedEnd = timestamp;
            EndSequence = sequence;
            ExplicitRemove = explicitRemove;
            RefreshOrOverwrite = refresh;
            ClearedByDeath = death;
        }
    }

    private readonly record struct OrderingTotals(
        double PercentageFirstPercentage,
        double RateFirstPercentage,
        double SharedShapleyPercentage,
        double SharedLogPercentage,
        double RateOnRaw,
        double SharedShapleyRate,
        double SharedLogRate);

    private sealed record MatchedEventInput(
        string Report,
        int FightId,
        string Encounter,
        string Actor,
        string Job,
        long ActionId,
        string ActionName,
        long Damage,
        string PercentageComposition,
        string RateClass,
        double PercentageContribution);

    private sealed class RecipientCandidateTotals
    {
        public double PercentageFirst { get; private set; }
        public double RateFirst { get; private set; }
        public double SharedShapley { get; private set; }
        public double SharedLog { get; private set; }
        public double RateAfterPercentage { get; private set; }
        public double RateOnRaw { get; private set; }
        public double SharedShapleyRate { get; private set; }
        public double SharedLogRate { get; private set; }

        public void Observe(OrderingTotals ordering, double rateAfterPercentage)
        {
            PercentageFirst += ordering.PercentageFirstPercentage;
            RateFirst += ordering.RateFirstPercentage;
            SharedShapley += ordering.SharedShapleyPercentage;
            SharedLog += ordering.SharedLogPercentage;
            RateAfterPercentage += rateAfterPercentage;
            RateOnRaw += ordering.RateOnRaw;
            SharedShapleyRate += ordering.SharedShapleyRate;
            SharedLogRate += ordering.SharedLogRate;
        }
    }
}

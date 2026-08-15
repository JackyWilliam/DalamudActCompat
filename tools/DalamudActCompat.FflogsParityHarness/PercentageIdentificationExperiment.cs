using DalamudActCompat.ActRuntime;

namespace DalamudActCompat.FflogsParityHarness;

internal static class PercentageIdentificationExperiment
{
    private const double ExactTolerance = 0.05;

    public static PercentageIdentificationReport Run(
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
        var constraints = new List<PercentageIdentificationConstraintRow>();
        var windowAccumulators = new Dictionary<WindowMetricKey, WindowMetricAccumulator>();

        for (var index = 0; index < samples.Length; index++)
        {
            var (sample, source) = samples[index];
            var fight = FflogsEventNormalizer.NormalizeAttribution(sample);
            AnalyzeFight(fight, source, constraints);
            AnalyzeStatusWindows(fight, windowAccumulators);
            if ((index + 1) % 10 == 0 || index + 1 == samples.Length)
            {
                Console.WriteLine($"Percentage identification analyzed {index + 1}/{samples.Length} fights.");
            }
        }

        var rate = constraints.Where(static item => item.RateOverlapEventCount > 0).ToArray();
        var core = rate.Where(static item => item.IsCleanDirectNormal).ToArray();
        var features = BuildResidualFeatures(rate, core);
        var validation = BuildOwnershipValidation(rate, core);
        var (identifiability, discriminators) = BuildIdentifiability(rate, core);
        var matched = BuildMatchedControls(constraints);
        var windows = windowAccumulators
            .Select(static pair => pair.Value.Build(pair.Key))
            .OrderBy(static item => item.Scope)
            .ThenBy(static item => item.Value)
            .ThenBy(static item => item.Strategy)
            .ToArray();
        var statusContributions = BuildStatusContributionMetrics(constraints, rate, core);
        var oracleShared = CalculateStatistics(
            rate.Select(static item => item.OracleSharedBaseLog - item.FflogsPercentageReference));
        var causalShared = CalculateStatistics(
            rate.Select(static item => item.CausalGraceSharedBaseLog - item.FflogsPercentageReference));
        var coreShared = CalculateStatistics(
            core.Select(static item => item.OracleSharedBaseLog - item.FflogsPercentageReference));
        var shapleyLogId = identifiability.Single(item =>
            item.Dataset == "RateOverlap1738" &&
            (item.CandidateA == PercentageIdentificationCandidates.OracleSharedShapley &&
             item.CandidateB == PercentageIdentificationCandidates.OracleSharedBaseLog ||
             item.CandidateB == PercentageIdentificationCandidates.OracleSharedShapley &&
             item.CandidateA == PercentageIdentificationCandidates.OracleSharedBaseLog));

        return new PercentageIdentificationReport(
            DateTimeOffset.UtcNow,
            samples.Length,
            rate.Length,
            core.Length,
            BuildInteractionDecompositionDocumentation(),
            BuildOwnershipAllocationDocumentation(),
            BuildStateMachineDocumentation(),
            BuildFallbackStrategyDocumentation(),
            constraints,
            features,
            validation,
            identifiability,
            discriminators,
            matched,
            windows,
            statusContributions,
            [
                $"The strict direct-normal core contains {core.Length} constraints. Every row is direct, non-periodic, normal-hit-only, packet-ordered, fixed-magnitude, owner-resolved, and free of death/reset boundary exposure.",
                $"Oracle SharedBaseLog remains MAE={oracleShared.MeanAbsoluteResidual:F3} on {rate.Length} rate-overlap constraints and MAE={coreShared.MeanAbsoluteResidual:F3} on the clean core.",
                $"SharedBaseLog versus SharedShapley has {shapleyLogId.DistinguishableConstraintCount}/{shapleyLogId.ConstraintCount} aggregate predictions separated beyond display tolerance; they are observationally distinguishable even though their global MAEs are close.",
                $"The live-causal individual grace strategy has SharedBaseLog MAE={causalShared.MeanAbsoluteResidual:F3}; it never branches on whether a future remove eventually arrives and hard-expires after {AttributionTimeline.CausalRemoveGraceMilliseconds:F0} ms.",
                "SharedShapley3 is the only added ownership candidate. It changes only the percentage×Crit×DH interaction from a two-block 1/2 share to a three-dimension 1/3 share; it is parameter-free and conservative.",
            ],
            [
                "FFLogs percentage truth remains provider/recipient/fight aggregate. Event-local and action-local component truth is unavailable, so matched event controls cannot identify ownership by themselves.",
                "The causal replay precomputes endpoints for efficiency, but each endpoint is the minimum of an already-arrived transition and a fixed horizon. No event before a transition is classified using knowledge that the transition will arrive later.",
                "The clean-core metadata checks validate the cache and authoritative registry inputs; they do not manufacture actual Crit/DH base rates, which remain production HitBaseline observations.",
                "Association slopes and binned residuals are diagnostics only. They are never fed back into an ownership candidate.",
            ]);
    }

    internal static IReadOnlyList<PercentageIdentificationConstraintRow> AnalyzeSamples(
        IReadOnlyList<(CachedFightSample Sample, string Source)> samples)
    {
        var constraints = new List<PercentageIdentificationConstraintRow>();
        for (var index = 0; index < samples.Count; index++)
        {
            var (sample, source) = samples[index];
            AnalyzeFight(FflogsEventNormalizer.NormalizeAttribution(sample), source, constraints);
            if ((index + 1) % 5 == 0 || index + 1 == samples.Count)
            {
                Console.WriteLine($"Ownership replay analyzed {index + 1}/{samples.Count} targeted fights.");
            }
        }
        return constraints;
    }

    internal static PercentageInteractionDecomposition DecomposeForTest(
        double damage,
        double percentageMultiplier,
        double criticalRateContribution,
        double directRateContribution)
        => Decompose(
            damage,
            percentageMultiplier,
            criticalRateContribution,
            directRateContribution);

    private static void AnalyzeFight(
        NormalizedAttributionFight fight,
        string sourceCache,
        ICollection<PercentageIdentificationConstraintRow> results)
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
        var accumulators = new Dictionary<ConstraintKey, ConstraintAccumulator>();
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
            var dimensions = timeline.ResolveGuaranteed(item, owner).Dimensions;
            var nominal = AnalyzeState(
                item,
                timeline.Resolve(item, owner, AttributionTimelineSemantics.PacketOrdered),
                baseline,
                dimensions);
            var oracle = AnalyzeState(
                item,
                timeline.Resolve(item, owner, AttributionTimelineSemantics.ObservedEventState),
                baseline,
                dimensions);
            var causal = AnalyzeState(
                item,
                timeline.Resolve(item, owner, AttributionTimelineSemantics.CausalRemoveGrace),
                baseline,
                dimensions);
            var cohort = AnalyzeState(
                item,
                timeline.Resolve(item, owner, AttributionTimelineSemantics.CausalCohortRemoveGrace),
                baseline,
                dimensions);
            var providers = nominal.PercentageBuffs
                .Concat(oracle.PercentageBuffs)
                .Concat(causal.PercentageBuffs)
                .Concat(cohort.PercentageBuffs)
                .DistinctBy(static buff => (buff.SourceActorId, buff.Definition.StatusId))
                .ToArray();

            foreach (var provider in providers)
            {
                var key = new ConstraintKey(owner.Id, provider.SourceActorId, provider.Definition.StatusId);
                if (!accumulators.TryGetValue(key, out var accumulator))
                {
                    accumulator = new ConstraintAccumulator(owner, provider, sourceCache);
                    accumulators.Add(key, accumulator);
                }
                accumulator.Observe(
                    fight,
                    item,
                    source,
                    nominal,
                    oracle,
                    causal,
                    cohort,
                    provider,
                    dimensions);
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

        foreach (var accumulator in accumulators.Values)
        {
            if (!fight.DamageTableActors.TryGetValue(accumulator.Recipient.Id, out var tableActor))
            {
                continue;
            }
            var reference = tableActor.Taken
                .Where(item => item.AbilityId == accumulator.Provider.Definition.StatusId)
                .Sum(static item => item.Amount);
            if (reference <= 0)
            {
                continue;
            }
            var rateReference = tableActor.Taken
                .Where(static item => OffensiveBuffRegistry.ByStatusId.TryGetValue(
                    item.AbilityId, out var definition) &&
                    definition.Dimension != OffensiveBuffDimension.PercentageDamage)
                .Sum(static item => item.Amount);
            results.Add(accumulator.Build(fight, reference, rateReference));
        }
    }

    private static StateAnalysis AnalyzeState(
        NormalizedFflogsEvent item,
        MatrixEventAttributionState state,
        HitBaselineSnapshot baseline,
        ProbeGuaranteedDimensions dimensions)
    {
        var percentage = state.Buffs
            .Where(static buff => !buff.IsSelfSourced &&
                                  buff.Definition.Dimension == OffensiveBuffDimension.PercentageDamage &&
                                  buff.DamageMultiplier > 1)
            .ToArray();
        var criticalBuffs = ResolveRateBuffs(state, critical: true);
        var directBuffs = ResolveRateBuffs(state, critical: false);
        var rate = CalculateRateParts(item, state, baseline, dimensions);
        var multiplier = percentage.Aggregate(
            1d,
            static (current, buff) => current * buff.DamageMultiplier);
        return new StateAnalysis(
            state,
            percentage,
            criticalBuffs,
            directBuffs,
            multiplier,
            Decompose(item.Amount, multiplier, rate.Critical, rate.Direct));
    }

    private static (double Critical, double Direct) CalculateRateParts(
        NormalizedFflogsEvent item,
        MatrixEventAttributionState state,
        HitBaselineSnapshot baseline,
        ProbeGuaranteedDimensions dimensions)
    {
        var critical = 0d;
        var direct = 0d;
        foreach (var provider in state.RateBuffs.Where(static buff => !buff.IsSelfSourced))
        {
            var parts = AttributionContributionMath.CalculateRateContributionParts(
                GuaranteedHitCandidateMath.CurrentProduction,
                item,
                state,
                provider,
                baseline.CriticalChance,
                baseline.DirectHitChance,
                dimensions,
                productionInputs: false);
            critical += parts.Critical;
            direct += parts.Direct;
        }
        return (critical, direct);
    }

    private static PercentageInteractionDecomposition Decompose(
        double damage,
        double percentageMultiplier,
        double criticalRateContribution,
        double directRateContribution)
    {
        if (damage <= 0)
        {
            return default;
        }
        percentageMultiplier = Math.Max(1, percentageMultiplier);
        var afterPercentage = damage / percentageMultiplier;
        var requestedRate = Math.Max(0, criticalRateContribution + directRateContribution);
        var boundedRate = Math.Min(afterPercentage, requestedRate);
        var rateScale = requestedRate > 0 ? boundedRate / requestedRate : 0;
        var critical = Math.Max(0, criticalRateContribution) * rateScale;
        var direct = Math.Max(0, directRateContribution) * rateScale;
        var baseDamage = Math.Max(0, afterPercentage - critical - direct);

        // Choose effective Crit and DH multipliers whose ordinary two-player
        // Shapley split exactly reproduces the existing Crit/DH contributions.
        // This makes every pair/triple interaction explicit without changing rate math.
        var discriminantBase = (4 * baseDamage) + (2 * (critical + direct));
        var discriminant = Math.Max(
            0,
            discriminantBase * discriminantBase - (16 * critical * direct));
        var criticalDirect = critical > 0 && direct > 0
            ? (8 * critical * direct) / (discriminantBase + Math.Sqrt(discriminant))
            : 0;
        var criticalMain = Math.Max(0, critical - criticalDirect / 2);
        var directMain = Math.Max(0, direct - criticalDirect / 2);
        var percentageGain = percentageMultiplier - 1;
        var percentageMain = baseDamage * percentageGain;
        var percentageCritical = criticalMain * percentageGain;
        var percentageDirect = directMain * percentageGain;
        var percentageCriticalDirect = criticalDirect * percentageGain;
        var percentageFirst = percentageMain + percentageCritical + percentageDirect +
                              percentageCriticalDirect;
        var rateFirst = percentageMain;
        var sharedShapley2 = percentageMain +
                             (percentageCritical + percentageDirect +
                              percentageCriticalDirect) / 2;
        var sharedShapley3 = percentageMain +
                             (percentageCritical + percentageDirect) / 2 +
                             percentageCriticalDirect / 3;
        var totalRemoval = damage - baseDamage;
        var criticalMultiplier = baseDamage > 0
            ? 1 + criticalMain / baseDamage
            : 1;
        var directMultiplier = baseDamage > 0
            ? 1 + directMain / baseDamage
            : 1;
        var logTotal = Math.Log(percentageMultiplier) + Math.Log(criticalMultiplier) +
                       Math.Log(directMultiplier);
        var sharedBaseLog = logTotal > 0
            ? totalRemoval * Math.Log(percentageMultiplier) / logTotal
            : percentageFirst;
        return new PercentageInteractionDecomposition(
            baseDamage,
            percentageMain,
            criticalMain,
            directMain,
            percentageCritical,
            percentageDirect,
            criticalDirect,
            percentageCriticalDirect,
            percentageFirst,
            rateFirst,
            sharedShapley2,
            sharedBaseLog,
            sharedShapley3,
            critical,
            direct);
    }

    private static MatrixBuffExposureEntry[] ResolveRateBuffs(
        MatrixEventAttributionState state,
        bool critical)
        => state.RateBuffs
            .Where(static buff => !buff.IsSelfSourced)
            .Where(buff => critical
                ? buff.Definition.CriticalRateIncrease > 0
                : buff.Definition.DirectHitRateIncrease > 0)
            .ToArray();

    private static MatrixBuffExposureEntry? FindProvider(
        StateAnalysis state,
        MatrixBuffExposureEntry provider)
        => state.PercentageBuffs.FirstOrDefault(buff =>
            buff.SourceActorId == provider.SourceActorId &&
            buff.Definition.StatusId == provider.Definition.StatusId);

    private static double ProviderContribution(
        StateAnalysis state,
        MatrixBuffExposureEntry provider,
        Func<PercentageInteractionDecomposition, double> selector)
    {
        var resolved = FindProvider(state, provider);
        if (resolved is null || state.PercentageMultiplier <= 1)
        {
            return 0;
        }
        var share = Math.Log(resolved.DamageMultiplier) / Math.Log(state.PercentageMultiplier);
        return selector(state.Decomposition) * share;
    }

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

    private static bool HasTechnicalMetadataMismatch(
        NormalizedAttributionFight fight,
        MatrixBuffExposureEntry provider)
        => provider.Definition.StatusId == 1822 &&
           !fight.Events.Any(item => item.SourceId == provider.SourceActorId &&
                                     item.AbilityId is 0x81C1 or 0x81C2 &&
                                     Math.Abs(item.Timestamp - provider.WindowStart) <= 2000);

    private static IReadOnlyList<ResidualFeatureAnalysisRow> BuildResidualFeatures(
        IReadOnlyList<PercentageIdentificationConstraintRow> rate,
        IReadOnlyList<PercentageIdentificationConstraintRow> core)
    {
        var result = new List<ResidualFeatureAnalysisRow>();
        foreach (var (dataset, rows) in new[]
                 {
                     ("RateOverlap1738", rate),
                     ("DirectNormalCore262", core),
                 })
        {
            AddNumericFeatures(result, dataset, rows);
            AddCategoricalFeatures(result, dataset, rows);
        }
        return result;
    }

    private static void AddNumericFeatures(
        ICollection<ResidualFeatureAnalysisRow> destination,
        string dataset,
        IReadOnlyList<PercentageIdentificationConstraintRow> rows)
    {
        var features = new (string Name, Func<PercentageIdentificationConstraintRow, double> Get)[]
        {
            ("percentage multiplier total", static item => item.DamageWeightedPercentageMultiplier),
            ("number of percentage providers", static item => item.DamageWeightedPercentageProviderCount),
            ("Crit-rate total", static item => item.DamageWeightedCriticalRateTotal),
            ("DH-rate total", static item => item.DamageWeightedDirectRateTotal),
            ("number of Crit providers", static item => item.DamageWeightedCriticalProviderCount),
            ("number of DH providers", static item => item.DamageWeightedDirectProviderCount),
            ("percentage x Crit interaction", static item => item.PercentageCriticalInteraction),
            ("percentage x DH interaction", static item => item.PercentageDirectInteraction),
            ("Crit x DH interaction", static item => item.CriticalDirectInteraction),
            ("percentage x Crit x DH interaction", static item => item.PercentageCriticalDirectInteraction),
            ("raw damage", static item => item.RawDamage),
            ("buff window age", static item => item.MeanBuffWindowAgeMilliseconds),
            ("distance to apply", static item => item.MeanDistanceToApplyMilliseconds),
            ("distance to remove/expiry", static item => item.MeanDistanceToRemoveMilliseconds),
            ("same-timestamp status activity", static item => item.SameTimestampStatusActivityCount),
        };
        foreach (var feature in features)
        {
            var values = rows.Select(item => (
                    Feature: feature.Get(item),
                    Residual: item.OracleSharedBaseLog - item.FflogsPercentageReference))
                .Where(static item => double.IsFinite(item.Feature) && double.IsFinite(item.Residual))
                .ToArray();
            if (values.Length == 0)
            {
                continue;
            }
            var pearson = Correlation(values.Select(static item => item.Feature),
                values.Select(static item => item.Residual));
            var spearman = Spearman(values);
            var sumSquares = values.Sum(static item => item.Feature * item.Feature);
            var slope = sumSquares > 0
                ? values.Sum(static item => item.Feature * item.Residual) / sumSquares
                : 0;
            var residualSse = values.Sum(static item => item.Residual * item.Residual);
            var fittedSse = values.Sum(item =>
            {
                var miss = item.Residual - slope * item.Feature;
                return miss * miss;
            });
            destination.Add(new ResidualFeatureAnalysisRow(
                dataset,
                PercentageIdentificationCandidates.OracleSharedBaseLog,
                feature.Name,
                "association",
                "All",
                values.Length,
                values.Min(static item => item.Feature),
                values.Max(static item => item.Feature),
                values.Average(static item => item.Feature),
                pearson,
                spearman,
                slope,
                residualSse > 0 ? 1 - fittedSse / residualSse : 0,
                CalculateStatistics(values.Select(static item => item.Residual))));

            var ordered = values.OrderBy(static item => item.Feature).ToArray();
            for (var quartile = 0; quartile < 4; quartile++)
            {
                var start = quartile * ordered.Length / 4;
                var end = (quartile + 1) * ordered.Length / 4;
                var bin = ordered[start..end];
                if (bin.Length == 0)
                {
                    continue;
                }
                destination.Add(new ResidualFeatureAnalysisRow(
                    dataset,
                    PercentageIdentificationCandidates.OracleSharedBaseLog,
                    feature.Name,
                    "quartile",
                    $"Q{quartile + 1}",
                    bin.Length,
                    bin.Min(static item => item.Feature),
                    bin.Max(static item => item.Feature),
                    bin.Average(static item => item.Feature),
                    pearson,
                    spearman,
                    slope,
                    residualSse > 0 ? 1 - fittedSse / residualSse : 0,
                    CalculateStatistics(bin.Select(static item => item.Residual))));
            }
        }
    }

    private static void AddCategoricalFeatures(
        ICollection<ResidualFeatureAnalysisRow> destination,
        string dataset,
        IReadOnlyList<PercentageIdentificationConstraintRow> rows)
    {
        var features = new (string Name, Func<PercentageIdentificationConstraintRow, string> Get)[]
        {
            ("actor", static item => item.RecipientActor),
            ("provider", static item => item.ProviderActor),
            ("provider job", static item => item.ProviderJob),
            ("recipient job", static item => item.RecipientJob),
            ("encounter", static item => item.Encounter),
            ("fight", static item => $"{item.Report}:{item.FightId}"),
            ("action family", static item => item.DominantActionFamily),
            ("rate dimension", static item => item.RateDimension),
        };
        foreach (var feature in features)
        {
            foreach (var group in rows.GroupBy(feature.Get).OrderByDescending(static group => group.Count()))
            {
                var residuals = group.Select(static item =>
                    item.OracleSharedBaseLog - item.FflogsPercentageReference).ToArray();
                destination.Add(new ResidualFeatureAnalysisRow(
                    dataset,
                    PercentageIdentificationCandidates.OracleSharedBaseLog,
                    feature.Name,
                    "category",
                    group.Key,
                    residuals.Length,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    0,
                    CalculateStatistics(residuals)));
            }
        }
    }

    private static IReadOnlyList<OwnershipValidationRow> BuildOwnershipValidation(
        IReadOnlyList<PercentageIdentificationConstraintRow> rate,
        IReadOnlyList<PercentageIdentificationConstraintRow> core)
    {
        var result = new List<OwnershipValidationRow>();
        foreach (var (dataset, rows) in new[] { ("RateOverlap1738", rate), ("DirectNormalCore262", core) })
        {
            AddValidationGroups(result, dataset, "ProviderJob", rows.GroupBy(static item => item.ProviderJob));
            AddValidationGroups(result, dataset, "Actor", rows.GroupBy(static item => item.RecipientActor));
            AddValidationGroups(result, dataset, "Encounter", rows.GroupBy(static item => item.Encounter));
            AddValidationGroups(result, dataset, "RecipientJob", rows.GroupBy(static item => item.RecipientJob));
            AddValidationGroups(result, dataset, "RateDimension", BuildRateDimensionGroups(rows));
        }
        return result;
    }

    private static IEnumerable<IGrouping<string, PercentageIdentificationConstraintRow>>
        BuildRateDimensionGroups(IReadOnlyList<PercentageIdentificationConstraintRow> rows)
    {
        var expanded = rows.SelectMany(item => ResolveRateDimensionGroups(item)
            .Select(value => (Value: value, Item: item)));
        return expanded.GroupBy(static item => item.Value, static item => item.Item);
    }

    private static IReadOnlyList<string> ResolveRateDimensionGroups(
        PercentageIdentificationConstraintRow item)
    {
        var result = new List<string> { item.RateDimension };
        if (item.MaximumCriticalProviderCount > 1) result.Add("multiple Crit");
        if (item.MaximumDirectProviderCount > 1) result.Add("multiple DH");
        if (item.HasSeparateCriticalDirectProviders) result.Add("Crit + DH separate providers");
        return result.Distinct().ToArray();
    }

    private static void AddValidationGroups(
        ICollection<OwnershipValidationRow> destination,
        string dataset,
        string dimension,
        IEnumerable<IGrouping<string, PercentageIdentificationConstraintRow>> groups)
    {
        foreach (var group in groups)
        {
            foreach (var candidate in PercentageIdentificationCandidates.SharedCandidates)
            {
                destination.Add(new OwnershipValidationRow(
                    dataset,
                    dimension,
                    group.Key,
                    candidate,
                    CalculateStatistics(group.Select(item =>
                        item.ResolvePrediction(candidate) - item.FflogsPercentageReference))));
            }
        }
    }

    private static (IReadOnlyList<OwnershipIdentifiabilityRow> Identifiability,
        IReadOnlyList<OwnershipDiscriminatorRow> Discriminators) BuildIdentifiability(
        IReadOnlyList<PercentageIdentificationConstraintRow> rate,
        IReadOnlyList<PercentageIdentificationConstraintRow> core)
    {
        var identification = new List<OwnershipIdentifiabilityRow>();
        var discriminators = new List<OwnershipDiscriminatorRow>();
        foreach (var (dataset, rows) in new[] { ("RateOverlap1738", rate), ("DirectNormalCore262", core) })
        {
            for (var left = 0; left < PercentageIdentificationCandidates.OwnershipCandidates.Count; left++)
            {
                for (var right = left + 1;
                     right < PercentageIdentificationCandidates.OwnershipCandidates.Count;
                     right++)
                {
                    var candidateA = PercentageIdentificationCandidates.OwnershipCandidates[left];
                    var candidateB = PercentageIdentificationCandidates.OwnershipCandidates[right];
                    var differences = rows.Select(item => new
                    {
                        Row = item,
                        A = item.ResolvePrediction(candidateA),
                        B = item.ResolvePrediction(candidateB),
                    }).ToArray();
                    var distinguishable = differences.Count(item => Math.Abs(item.A - item.B) > ExactTolerance);
                    identification.Add(new OwnershipIdentifiabilityRow(
                        dataset,
                        candidateA,
                        candidateB,
                        rows.Count,
                        distinguishable,
                        differences.Average(item => Math.Abs(item.A - item.B)),
                        differences.Max(item => Math.Abs(item.A - item.B)),
                        distinguishable == 0,
                        distinguishable == 0
                            ? "Non-identifiable with current observations"
                            : "Observationally distinguishable with provider-level aggregate"));
                    foreach (var item in differences
                                 .OrderByDescending(item => Math.Abs(item.A - item.B))
                                 .Take(12))
                    {
                        var residualA = Math.Abs(item.A - item.Row.FflogsPercentageReference);
                        var residualB = Math.Abs(item.B - item.Row.FflogsPercentageReference);
                        discriminators.Add(new OwnershipDiscriminatorRow(
                            dataset,
                            candidateA,
                            candidateB,
                            item.Row.Report,
                            item.Row.FightId,
                            item.Row.Encounter,
                            item.Row.ProviderActor,
                            item.Row.ProviderJob,
                            item.Row.RecipientActor,
                            item.Row.RecipientJob,
                            item.Row.BuffName,
                            item.Row.RateDimension,
                            item.Row.FflogsPercentageReference,
                            item.A,
                            item.B,
                            item.A - item.B,
                            residualA,
                            residualB,
                            Math.Abs(residualA - residualB) <= ExactTolerance
                                ? "Tie at display tolerance"
                                : residualA < residualB ? candidateA : candidateB));
                    }
                }
            }
        }
        return (identification, discriminators);
    }

    private static IReadOnlyList<MatchedInteractionControlRow> BuildMatchedControls(
        IReadOnlyList<PercentageIdentificationConstraintRow> constraints)
    {
        var result = new List<MatchedInteractionControlRow>();
        foreach (var group in constraints.GroupBy(static item => new
        {
            item.Report,
            item.RecipientActorId,
            item.ProviderActorId,
            item.EncounterId,
            item.BuffStatusId,
            item.DominantActionFamily,
        }))
        {
            var rows = group.OrderBy(static item => item.FightId).ToArray();
            for (var left = 0; left < rows.Length; left++)
            {
                for (var right = left + 1; right < rows.Length; right++)
                {
                    if (rows[left].RateDimension == rows[right].RateDimension)
                    {
                        continue;
                    }
                    var residualA = rows[left].OracleSharedBaseLog -
                                    rows[left].FflogsPercentageReference;
                    var residualB = rows[right].OracleSharedBaseLog -
                                    rows[right].FflogsPercentageReference;
                    var interactionA = rows[left].PercentageCriticalInteraction +
                                       rows[left].PercentageDirectInteraction +
                                       rows[left].PercentageCriticalDirectInteraction;
                    var interactionB = rows[right].PercentageCriticalInteraction +
                                       rows[right].PercentageDirectInteraction +
                                       rows[right].PercentageCriticalDirectInteraction;
                    result.Add(new MatchedInteractionControlRow(
                        "A: same report actor IDs/provider/encounter/buff/dominant action family",
                        group.Key.Report,
                        group.Key.RecipientActorId,
                        rows[left].RecipientActor,
                        rows[left].RecipientJob,
                        group.Key.ProviderActorId,
                        rows[left].ProviderActor,
                        rows[left].ProviderJob,
                        rows[left].Encounter,
                        rows[left].BuffName,
                        group.Key.DominantActionFamily,
                        rows[left].RateDimension,
                        rows[right].RateDimension,
                        rows[left].FightId,
                        rows[right].FightId,
                        residualA,
                        residualB,
                        residualB - residualA,
                        interactionB - interactionA,
                        "provider/recipient/fight taken[] aggregate available"));
                }
            }
        }
        return result.OrderByDescending(static item => Math.Abs(item.InteractionShift)).ToArray();
    }

    private static void AnalyzeStatusWindows(
        NormalizedAttributionFight fight,
        IDictionary<WindowMetricKey, WindowMetricAccumulator> accumulators)
    {
        var timeline = new AttributionTimeline(fight);
        var damage = fight.Events
            .Where(static item => FflogsEventNormalizer.IsDamageEvent(item) && item.Amount > 0)
            .ToArray();
        foreach (var interval in timeline.StatusLifecycles.Where(static item =>
                     OffensiveBuffRegistry.ByStatusId.TryGetValue(item.StatusId, out var definition) &&
                     definition.Dimension == OffensiveBuffDimension.PercentageDamage &&
                     definition.DamageMultiplier is not null))
        {
            var definition = OffensiveBuffRegistry.ByStatusId[interval.StatusId];
            var matchingDamage = damage.Where(item =>
                FflogsEventNormalizer.ResolveOwnerActor(item.SourceId, fight.Actors)?.Id == interval.TargetActorId ||
                item.TargetId == interval.TargetActorId).ToArray();
            foreach (var strategy in StatusStrategies)
            {
                var endpoint = ResolveStrategyEnd(interval, strategy);
                var endpointOrder = CompareEndpoints(
                    endpoint.End,
                    endpoint.Sequence,
                    interval.OracleEnd,
                    interval.OracleEndSequence);
                var included = matchingDamage.Where(item =>
                    IsActive(item, interval.Start, interval.StartSequence, endpoint.End, endpoint.Sequence) &&
                    !IsActive(item, interval.Start, interval.StartSequence,
                        interval.OracleEnd, interval.OracleEndSequence)).ToArray();
                var excluded = matchingDamage.Where(item =>
                    !IsActive(item, interval.Start, interval.StartSequence, endpoint.End, endpoint.Sequence) &&
                    IsActive(item, interval.Start, interval.StartSequence,
                        interval.OracleEnd, interval.OracleEndSequence)).ToArray();
                var fallbackMismatch = endpoint.UsedFallback
                    ? included.Concat(excluded).Distinct().ToArray()
                    : [];
                foreach (var scope in new[]
                         {
                             (Scope: "All", Value: "All"),
                             (Scope: "Buff", Value: definition.ActionName),
                         })
                {
                    var key = new WindowMetricKey(scope.Scope, scope.Value, strategy);
                    if (!accumulators.TryGetValue(key, out var accumulator))
                    {
                        accumulator = new WindowMetricAccumulator();
                        accumulators.Add(key, accumulator);
                    }
                    accumulator.Observe(
                        endpointOrder,
                        included,
                        excluded,
                        fallbackMismatch,
                        Math.Max(0, endpoint.End - interval.OracleEnd));
                }
            }
        }
    }

    private static IReadOnlyList<StatusContributionMetricRow> BuildStatusContributionMetrics(
        IReadOnlyList<PercentageIdentificationConstraintRow> all,
        IReadOnlyList<PercentageIdentificationConstraintRow> rate,
        IReadOnlyList<PercentageIdentificationConstraintRow> core)
    {
        var result = new List<StatusContributionMetricRow>();
        var noRate = all.Where(static item => item.RateOverlapEventCount == 0).ToArray();
        foreach (var (dataset, rows) in new[]
                 {
                     ("All2217", all),
                     ("RateOverlap1738", rate),
                     ("DirectNormalCore262", core),
                     ("NoRateControls", noRate),
                 })
        {
            AddStatusContribution(result, dataset, rows,
                "Current ownership", "nominal packet state",
                PercentageIdentificationCandidates.NominalPercentageFirst,
                PercentageIdentificationCandidates.OraclePercentageFirst);
            AddStatusContribution(result, dataset, rows,
                "Current ownership", "oracle explicit packet state",
                PercentageIdentificationCandidates.OraclePercentageFirst,
                PercentageIdentificationCandidates.OraclePercentageFirst);
            AddStatusContribution(result, dataset, rows,
                "Current ownership", "causal individual 2s fallback",
                PercentageIdentificationCandidates.CausalGracePercentageFirst,
                PercentageIdentificationCandidates.OraclePercentageFirst);
            AddStatusContribution(result, dataset, rows,
                "Current ownership", "causal cohort-evidence 2s fallback",
                PercentageIdentificationCandidates.CausalCohortPercentageFirst,
                PercentageIdentificationCandidates.OraclePercentageFirst);
            AddStatusContribution(result, dataset, rows,
                "SharedBaseLog diagnostic", "nominal packet state",
                PercentageIdentificationCandidates.NominalSharedBaseLog,
                PercentageIdentificationCandidates.OracleSharedBaseLog);
            AddStatusContribution(result, dataset, rows,
                "SharedBaseLog diagnostic", "oracle explicit packet state",
                PercentageIdentificationCandidates.OracleSharedBaseLog,
                PercentageIdentificationCandidates.OracleSharedBaseLog);
            AddStatusContribution(result, dataset, rows,
                "SharedBaseLog diagnostic", "causal individual 2s fallback",
                PercentageIdentificationCandidates.CausalGraceSharedBaseLog,
                PercentageIdentificationCandidates.OracleSharedBaseLog);
            AddStatusContribution(result, dataset, rows,
                "SharedBaseLog diagnostic", "causal cohort-evidence 2s fallback",
                PercentageIdentificationCandidates.CausalCohortSharedBaseLog,
                PercentageIdentificationCandidates.OracleSharedBaseLog);
        }
        return result;
    }

    private static void AddStatusContribution(
        ICollection<StatusContributionMetricRow> destination,
        string dataset,
        IReadOnlyList<PercentageIdentificationConstraintRow> rows,
        string ownership,
        string state,
        string candidate,
        string oracleCandidate)
    {
        destination.Add(new StatusContributionMetricRow(
            dataset,
            ownership,
            state,
            candidate,
            CalculateStatistics(rows.Select(item =>
                item.ResolvePrediction(candidate) - item.FflogsPercentageReference)),
            rows.Count == 0
                ? 0
                : rows.Average(item => Math.Abs(
                    item.ResolvePrediction(candidate) - item.ResolvePrediction(oracleCandidate)))));
    }

    private static readonly string[] StatusStrategies =
    [
        "NominalImmediate",
        "OracleExplicitPacket",
        "CausalIndividualGrace2s",
        "CausalCohortEvidenceGrace2s",
    ];

    private static (double End, long Sequence, bool UsedFallback) ResolveStrategyEnd(
        AttributionStatusLifecycle interval,
        string strategy)
        => strategy switch
        {
            "NominalImmediate" =>
                (interval.NominalCausalEnd, interval.NominalCausalEndSequence, false),
            "OracleExplicitPacket" =>
                (interval.OracleEnd, interval.OracleEndSequence, false),
            "CausalIndividualGrace2s" =>
                (interval.CausalGraceEnd, interval.CausalGraceEndSequence,
                    interval.EndReason == AttributionStatusEndReason.MissingRemoveNominalFallback ||
                    interval.OracleEnd > interval.NominalEnd +
                    AttributionTimeline.CausalRemoveGraceMilliseconds),
            "CausalCohortEvidenceGrace2s" =>
                (interval.CausalCohortEnd, interval.CausalCohortEndSequence,
                    interval.CausalCohortEnd == interval.NominalEnd +
                    AttributionTimeline.CausalRemoveGraceMilliseconds),
            _ => throw new ArgumentOutOfRangeException(nameof(strategy), strategy, null),
        };

    private static int CompareEndpoints(double left, long leftSequence, double right, long rightSequence)
    {
        var time = left.CompareTo(right);
        return time != 0 ? time : leftSequence.CompareTo(rightSequence);
    }

    private static bool IsActive(
        NormalizedFflogsEvent item,
        double start,
        long startSequence,
        double end,
        long endSequence)
        => (item.Timestamp > start ||
            item.Timestamp == start && item.AttributionSequence >= startSequence) &&
           (item.Timestamp < end ||
            item.Timestamp == end && item.AttributionSequence < endSequence);

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

    private static double Correlation(IEnumerable<double> left, IEnumerable<double> right)
    {
        var x = left.ToArray();
        var y = right.ToArray();
        if (x.Length == 0 || x.Length != y.Length)
        {
            return 0;
        }
        var meanX = x.Average();
        var meanY = y.Average();
        var covariance = 0d;
        var varianceX = 0d;
        var varianceY = 0d;
        for (var index = 0; index < x.Length; index++)
        {
            var dx = x[index] - meanX;
            var dy = y[index] - meanY;
            covariance += dx * dy;
            varianceX += dx * dx;
            varianceY += dy * dy;
        }
        return varianceX > 0 && varianceY > 0
            ? covariance / Math.Sqrt(varianceX * varianceY)
            : 0;
    }

    private static double Spearman(IReadOnlyList<(double Feature, double Residual)> values)
    {
        var featureRanks = ResolveRanks(values.Select(static item => item.Feature).ToArray());
        var residualRanks = ResolveRanks(values.Select(static item => item.Residual).ToArray());
        return Correlation(featureRanks, residualRanks);
    }

    private static double[] ResolveRanks(IReadOnlyList<double> values)
    {
        var ordered = values.Select((value, index) => (value, index)).OrderBy(static item => item.value)
            .ToArray();
        var ranks = new double[values.Count];
        for (var start = 0; start < ordered.Length;)
        {
            var end = start + 1;
            while (end < ordered.Length && ordered[end].value == ordered[start].value) end++;
            var rank = (start + 1 + end) / 2d;
            for (var index = start; index < end; index++) ranks[ordered[index].index] = rank;
            start = end;
        }
        return ranks;
    }

    private static IReadOnlyList<string> BuildInteractionDecompositionDocumentation()
        =>
        [
            "Let N = B*M*C*D, with effective C/D chosen so the two-player Shapley split of Crit×DH exactly reproduces the fixed current-equation Crit and DH contributions on N/M.",
            "Base = B.",
            "Percentage-only = B*(M-1). Crit-only = B*(C-1). DH-only = B*(D-1).",
            "Percentage×Crit = B*(M-1)*(C-1). Percentage×DH = B*(M-1)*(D-1).",
            "Crit×DH = B*(C-1)*(D-1). Percentage×Crit×DH = B*(M-1)*(C-1)*(D-1).",
            "The eight terms sum exactly to N; no fitted coefficient or provider-specific correction is introduced.",
        ];

    private static IReadOnlyList<string> BuildOwnershipAllocationDocumentation()
        =>
        [
            "Current PercentageFirst: percentage owns P main + P×C + P×D + P×C×D; Crit/DH own their main effects and split C×D 1/2 each.",
            "RateFirst: percentage owns P main only; Crit owns C main + 1/2 C×D + P×C + 1/2 P×C×D; DH is symmetric.",
            "SharedShapley2: percentage owns P main + 1/2(P×C + P×D + P×C×D); the remaining interaction half goes to the combined rate block and is split across Crit/DH by the current rate decomposition.",
            "SharedShapley3: pair interactions split 1/2 between their two dimensions and P×C×D splits 1/3 to percentage, Crit, and DH.",
            "SharedBaseLog: all non-base terms form one conserved removal pool; percentage/Crit/DH receive global shares ln(M)/ln(MCD), ln(C)/ln(MCD), and ln(D)/ln(MCD).",
        ];

    private static IReadOnlyList<string> BuildStateMachineDocumentation()
        =>
        [
            "Inactive --apply--> ActiveObserved.",
            "ActiveObserved --refresh/overwrite--> ActiveRefreshed; close the old interval at packet sequence and open the replacement.",
            "ActiveObserved/ActiveRefreshed --nominal endpoint reached--> NominalExpiryReached --> PendingRemoveGrace.",
            "PendingRemoveGrace --explicit remove--> RemovedObserved.",
            "PendingRemoveGrace --refresh/overwrite--> ActiveRefreshed.",
            "PendingRemoveGrace --2s protocol horizon--> ExpiredFallback.",
            "Any active/pending state --recipient death, encounter reset, actor reset--> Reset.",
            "Damage uses half-open [apply sequence, remove/fallback sequence) eligibility; same-timestamp decisions never sort by timestamp alone.",
        ];

    private static IReadOnlyList<string> BuildFallbackStrategyDocumentation()
        =>
        [
            "NominalImmediate: causal and zero-latency, but expires before delayed remove fan-out and is the current accuracy baseline.",
            "CausalIndividualGrace2s: keep each observed application pending until its own remove/refresh/death or the existing two-second action/status protocol horizon. Maximum stale exposure and UI delay are 2s.",
            "CausalCohortEvidenceGrace2s: same hard bound, but the first post-nominal sibling removal for the same source/status/apply packet closes pending cohort members. It lowers missing-remove stale risk at the cost of possible early expiry during staggered fan-out.",
            "OracleExplicitPacket: diagnostic ceiling only. It chooses nominal fallback only after seeing that no later remove exists and is therefore forbidden in live production.",
        ];

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

    private readonly record struct ConstraintKey(int RecipientId, int ProviderId, long StatusId);

    private sealed record StateAnalysis(
        MatrixEventAttributionState State,
        IReadOnlyList<MatrixBuffExposureEntry> PercentageBuffs,
        IReadOnlyList<MatrixBuffExposureEntry> CriticalBuffs,
        IReadOnlyList<MatrixBuffExposureEntry> DirectBuffs,
        double PercentageMultiplier,
        PercentageInteractionDecomposition Decomposition);

    private sealed class ConstraintAccumulator(
        FflogsActor recipient,
        MatrixBuffExposureEntry provider,
        string sourceCache)
    {
        private readonly HashSet<string> percentageComposition = new(StringComparer.Ordinal);
        private readonly HashSet<string> criticalComposition = new(StringComparer.Ordinal);
        private readonly HashSet<string> directComposition = new(StringComparer.Ordinal);
        private readonly HashSet<string> stateSources = new(StringComparer.Ordinal);
        private readonly Dictionary<(long ActionId, string ActionName), long> actionDamage = [];
        private double nominalPercentageFirst;
        private double nominalSharedLog;
        private double oraclePercentageFirst;
        private double oracleRateFirst;
        private double oracleSharedShapley;
        private double oracleSharedLog;
        private double oracleSharedShapley3;
        private double causalPercentageFirst;
        private double causalRateFirst;
        private double causalSharedShapley;
        private double causalSharedLog;
        private double causalSharedShapley3;
        private double cohortPercentageFirst;
        private double cohortSharedLog;
        private int eventCount;
        private int directCount;
        private int periodicCount;
        private int rateCount;
        private int guaranteedCriticalCount;
        private int guaranteedDirectCount;
        private int guaranteedCombinedCount;
        private int petCount;
        private int ambiguousPetCount;
        private int deathBoundaryCount;
        private int unknownMagnitudeCount;
        private int metadataMismatchCount;
        private long rawDamage;
        private long rateDamage;
        private double weightedMultiplier;
        private double weightedPercentageProviders;
        private double weightedCriticalRate;
        private double weightedDirectRate;
        private double weightedCriticalProviders;
        private double weightedDirectProviders;
        private int maximumCriticalProviders;
        private int maximumDirectProviders;
        private bool separateProviders;
        private double percentageMain;
        private double criticalMain;
        private double directMain;
        private double percentageCritical;
        private double percentageDirect;
        private double criticalDirect;
        private double percentageCriticalDirect;
        private double timingWeight;
        private double weightedAge;
        private double weightedDistanceToRemove;
        private int sameTimestamp;
        private long minSequence = long.MaxValue;
        private long maxSequence = long.MinValue;

        public FflogsActor Recipient { get; } = recipient;
        public MatrixBuffExposureEntry Provider { get; } = provider;

        public void Observe(
            NormalizedAttributionFight fight,
            NormalizedFflogsEvent item,
            FflogsActor source,
            StateAnalysis nominal,
            StateAnalysis oracle,
            StateAnalysis causal,
            StateAnalysis cohort,
            MatrixBuffExposureEntry expectedProvider,
            ProbeGuaranteedDimensions dimensions)
        {
            nominalPercentageFirst += ProviderContribution(
                nominal, expectedProvider, static value => value.PercentageFirst);
            nominalSharedLog += ProviderContribution(
                nominal, expectedProvider, static value => value.SharedBaseLog);
            oraclePercentageFirst += ProviderContribution(
                oracle, expectedProvider, static value => value.PercentageFirst);
            oracleRateFirst += ProviderContribution(
                oracle, expectedProvider, static value => value.RateFirst);
            oracleSharedShapley += ProviderContribution(
                oracle, expectedProvider, static value => value.SharedShapley2);
            oracleSharedLog += ProviderContribution(
                oracle, expectedProvider, static value => value.SharedBaseLog);
            oracleSharedShapley3 += ProviderContribution(
                oracle, expectedProvider, static value => value.SharedShapley3);
            causalPercentageFirst += ProviderContribution(
                causal, expectedProvider, static value => value.PercentageFirst);
            causalRateFirst += ProviderContribution(
                causal, expectedProvider, static value => value.RateFirst);
            causalSharedShapley += ProviderContribution(
                causal, expectedProvider, static value => value.SharedShapley2);
            causalSharedLog += ProviderContribution(
                causal, expectedProvider, static value => value.SharedBaseLog);
            causalSharedShapley3 += ProviderContribution(
                causal, expectedProvider, static value => value.SharedShapley3);
            cohortPercentageFirst += ProviderContribution(
                cohort, expectedProvider, static value => value.PercentageFirst);
            cohortSharedLog += ProviderContribution(
                cohort, expectedProvider, static value => value.SharedBaseLog);

            var oracleProvider = FindProvider(oracle, expectedProvider);
            if (oracleProvider is null)
            {
                return;
            }
            eventCount++;
            if (item.IsPeriodic) periodicCount++; else directCount++;
            rawDamage += item.Amount;
            minSequence = Math.Min(minSequence, item.AttributionSequence);
            maxSequence = Math.Max(maxSequence, item.AttributionSequence);
            if (source.PetOwnerId is not null)
            {
                petCount++;
                if (source.PetOwnerId != Recipient.Id) ambiguousPetCount++;
            }
            if (dimensions == ProbeGuaranteedDimensions.Critical) guaranteedCriticalCount++;
            if (dimensions == ProbeGuaranteedDimensions.DirectHit) guaranteedDirectCount++;
            if (dimensions == (ProbeGuaranteedDimensions.Critical | ProbeGuaranteedDimensions.DirectHit))
                guaranteedCombinedCount++;
            if (oracle.State.Buffs.Any(static buff =>
                    buff.StatusEndReason == AttributionStatusEndReason.RecipientDeath))
                deathBoundaryCount++;
            if (!double.IsFinite(oracleProvider.DamageMultiplier) || oracleProvider.DamageMultiplier <= 1)
                unknownMagnitudeCount++;
            if (HasTechnicalMetadataMismatch(fight, oracleProvider)) metadataMismatchCount++;

            var criticalBuffs = oracle.CriticalBuffs;
            var directBuffs = oracle.DirectBuffs;
            if (criticalBuffs.Count > 0 || directBuffs.Count > 0)
            {
                rateCount++;
                rateDamage += item.Amount;
                actionDamage[(item.AbilityId, item.AbilityName)] =
                    actionDamage.GetValueOrDefault((item.AbilityId, item.AbilityName)) + item.Amount;
            }
            maximumCriticalProviders = Math.Max(maximumCriticalProviders,
                criticalBuffs.Select(static buff => buff.SourceActorId).Distinct().Count());
            maximumDirectProviders = Math.Max(maximumDirectProviders,
                directBuffs.Select(static buff => buff.SourceActorId).Distinct().Count());
            separateProviders |= criticalBuffs.Count > 0 && directBuffs.Count > 0 &&
                                 !criticalBuffs.Select(static buff => buff.SourceActorId)
                                     .Intersect(directBuffs.Select(static buff => buff.SourceActorId)).Any();
            weightedMultiplier += oracle.PercentageMultiplier * item.Amount;
            weightedPercentageProviders += oracle.PercentageBuffs.Count * item.Amount;
            weightedCriticalRate += oracle.State.ExternalCriticalRateIncrease * item.Amount;
            weightedDirectRate += oracle.State.ExternalDirectRateIncrease * item.Amount;
            weightedCriticalProviders += criticalBuffs.Count * item.Amount;
            weightedDirectProviders += directBuffs.Count * item.Amount;
            var share = Math.Log(oracleProvider.DamageMultiplier) /
                        Math.Log(oracle.PercentageMultiplier);
            percentageMain += oracle.Decomposition.PercentageMain * share;
            criticalMain += oracle.Decomposition.CriticalMain * share;
            directMain += oracle.Decomposition.DirectMain * share;
            percentageCritical += oracle.Decomposition.PercentageCritical * share;
            percentageDirect += oracle.Decomposition.PercentageDirect * share;
            criticalDirect += oracle.Decomposition.CriticalDirect * share;
            percentageCriticalDirect += oracle.Decomposition.PercentageCriticalDirect * share;
            var age = Math.Max(0, item.Timestamp - oracleProvider.WindowStart);
            var distance = Math.Max(0, oracleProvider.WindowEnd - item.Timestamp);
            timingWeight += item.Amount;
            weightedAge += age * item.Amount;
            weightedDistanceToRemove += distance * item.Amount;
            if (item.Timestamp == oracleProvider.WindowStart || item.Timestamp == oracleProvider.WindowEnd)
                sameTimestamp++;
            foreach (var buff in oracle.PercentageBuffs) percentageComposition.Add(FormatBuff(buff));
            foreach (var buff in criticalBuffs) criticalComposition.Add(FormatBuff(buff));
            foreach (var buff in directBuffs) directComposition.Add(FormatBuff(buff));
            stateSources.Add(oracleProvider.StatusStateSource);
        }

        public PercentageIdentificationConstraintRow Build(
            NormalizedAttributionFight fight,
            double reference,
            double rateReference)
        {
            var exclusions = new List<string>();
            if (rateCount == 0) exclusions.Add("no rate overlap");
            if (periodicCount > 0) exclusions.Add("periodic/DoT or simulated tick");
            if (guaranteedCriticalCount > 0) exclusions.Add("known guaranteed Crit");
            if (guaranteedDirectCount > 0) exclusions.Add("known guaranteed DH");
            if (guaranteedCombinedCount > 0) exclusions.Add("known guaranteed CDH");
            if (deathBoundaryCount > 0) exclusions.Add("death/reset boundary");
            if (ambiguousPetCount > 0) exclusions.Add("pet-owner ambiguity");
            if (unknownMagnitudeCount > 0) exclusions.Add("unknown percentage magnitude");
            if (metadataMismatchCount > 0) exclusions.Add("metadata mismatch");
            var rateDimension = (maximumCriticalProviders > 0, maximumDirectProviders > 0) switch
            {
                (true, false) => "Crit-only",
                (false, true) => "DH-only",
                (true, true) => "Crit+DH",
                _ => "No-rate",
            };
            var dominantAction = actionDamage.Count == 0
                ? (ActionId: 0L, ActionName: "None")
                : actionDamage.OrderByDescending(static pair => pair.Value).First().Key;
            return new PercentageIdentificationConstraintRow(
                fight.Seed.ReportCode,
                fight.Fight.Id,
                fight.Fight.EncounterId,
                fight.Fight.Name,
                fight.PartyComposition,
                Provider.SourceActorId,
                Provider.SourceActor,
                ToJobAbbreviation(Provider.SourceJob),
                Recipient.Id,
                Recipient.Name,
                ToJobAbbreviation(Recipient.Job),
                Provider.Definition.StatusId,
                Provider.Definition.ActionName,
                reference,
                rateReference,
                nominalPercentageFirst,
                nominalSharedLog,
                oraclePercentageFirst,
                oracleRateFirst,
                oracleSharedShapley,
                oracleSharedLog,
                oracleSharedShapley3,
                causalPercentageFirst,
                causalRateFirst,
                causalSharedShapley,
                causalSharedLog,
                causalSharedShapley3,
                cohortPercentageFirst,
                cohortSharedLog,
                eventCount,
                directCount,
                periodicCount,
                rateCount,
                guaranteedCriticalCount,
                guaranteedDirectCount,
                guaranteedCombinedCount,
                petCount,
                ambiguousPetCount,
                deathBoundaryCount,
                unknownMagnitudeCount,
                metadataMismatchCount,
                rawDamage,
                rawDamage,
                rateDamage,
                rawDamage > 0 ? weightedMultiplier / rawDamage : 1,
                rawDamage > 0 ? weightedPercentageProviders / rawDamage : 0,
                rawDamage > 0 ? weightedCriticalRate / rawDamage : 0,
                rawDamage > 0 ? weightedDirectRate / rawDamage : 0,
                rawDamage > 0 ? weightedCriticalProviders / rawDamage : 0,
                rawDamage > 0 ? weightedDirectProviders / rawDamage : 0,
                maximumCriticalProviders,
                maximumDirectProviders,
                separateProviders,
                percentageMain,
                criticalMain,
                directMain,
                percentageCritical,
                percentageDirect,
                criticalDirect,
                percentageCriticalDirect,
                timingWeight > 0 ? weightedAge / timingWeight : 0,
                timingWeight > 0 ? weightedAge / timingWeight : 0,
                timingWeight > 0 ? weightedDistanceToRemove / timingWeight : 0,
                sameTimestamp,
                minSequence == long.MaxValue ? 0 : minSequence,
                maxSequence == long.MinValue ? 0 : maxSequence,
                string.Join(" + ", percentageComposition.Order()),
                string.Join(" + ", criticalComposition.Order()),
                string.Join(" + ", directComposition.Order()),
                rateDimension,
                $"{dominantAction.ActionId}:{dominantAction.ActionName}",
                actionDamage.Count,
                string.Join(" + ", stateSources.Order()),
                exclusions.Count == 0,
                string.Join(" | ", exclusions),
                sourceCache);
        }

        private static string FormatBuff(MatrixBuffExposureEntry buff)
            => $"{ToJobAbbreviation(buff.SourceJob)}:{buff.Definition.ActionName}:{buff.Definition.Magnitude}";
    }

    private readonly record struct WindowMetricKey(string Scope, string Value, string Strategy);

    private sealed class WindowMetricAccumulator
    {
        private int intervals;
        private int exact;
        private int early;
        private int late;
        private int includedCount;
        private long includedDamage;
        private int excludedCount;
        private long excludedDamage;
        private int fallbackCount;
        private long fallbackDamage;
        private double maximumLateness;

        public void Observe(
            int endpointOrder,
            IReadOnlyList<NormalizedFflogsEvent> included,
            IReadOnlyList<NormalizedFflogsEvent> excluded,
            IReadOnlyList<NormalizedFflogsEvent> fallback,
            double lateness)
        {
            intervals++;
            if (endpointOrder < 0) early++;
            else if (endpointOrder > 0) late++;
            else exact++;
            includedCount += included.Count;
            includedDamage += included.Sum(static item => item.Amount);
            excludedCount += excluded.Count;
            excludedDamage += excluded.Sum(static item => item.Amount);
            fallbackCount += fallback.Count;
            fallbackDamage += fallback.Sum(static item => item.Amount);
            maximumLateness = Math.Max(maximumLateness, lateness);
        }

        public StatusWindowMetricRow Build(WindowMetricKey key)
            => new(
                key.Scope,
                key.Value,
                key.Strategy,
                intervals,
                exact,
                early,
                late,
                includedCount,
                includedDamage,
                excludedCount,
                excludedDamage,
                fallbackCount,
                fallbackDamage,
                maximumLateness,
                key.Strategy == "OracleExplicitPacket"
                    ? "oracle/non-causal missing-remove branch"
                    : "causal",
                key.Strategy.Contains("2s", StringComparison.Ordinal)
                    ? "2,000 ms hard bound"
                    : key.Strategy == "NominalImmediate" ? "0 ms" : "unbounded diagnostic");
    }
}

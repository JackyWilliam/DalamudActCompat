namespace DalamudActCompat.FflogsParityHarness;

internal static class PercentageAttributionAudit
{
    public static PercentageAuditReport Run(
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
        var constraints = new List<PercentageConstraintAuditRow>();
        var windows = new List<PercentageWindowAuditRow>();
        var unmatched = new List<string>();
        var fights = new List<NormalizedAttributionFight>(samples.Length);
        for (var index = 0; index < samples.Length; index++)
        {
            var (sample, source) = samples[index];
            var fight = FflogsEventNormalizer.NormalizeAttribution(sample);
            fights.Add(fight);
            AnalyzeFight(fight, source, constraints, windows, unmatched);
            if ((index + 1) % 10 == 0 || index + 1 == samples.Length)
            {
                Console.WriteLine($"Percentage audit analyzed {index + 1}/{samples.Length} fights.");
            }
        }

        var reference = BuildReferenceAudit(fights, constraints);
        var statistics = BuildStatistics(constraints);
        var beforeFix = CalculateStatistics(
            constraints.Select(static item => item.CurrentProductionBeforeFixDelta));
        var current = CalculateStatistics(constraints.Select(static item => item.CurrentProductionDelta));
        var legacy = CalculateStatistics(constraints.Select(static item => item.LegacyMetadataDelta));
        var authoritative = CalculateStatistics(constraints.Select(static item => item.AuthoritativeDelta));
        var allActive = CalculateStatistics(constraints.Select(static item => item.AllActiveDenominatorDelta));
        var selfStripped = CalculateStatistics(constraints.Select(static item => item.SelfStrippedBasisDelta));
        var calculatedMultiplier = CalculateStatistics(
            constraints.Select(static item => item.CalculatedMultiplierDelta));
        var packetOrdered = CalculateStatistics(
            constraints.Select(static item => item.PacketOrderedDelta));
        var explicitRemoval = CalculateStatistics(
            constraints.Select(static item => item.ExplicitRemovalDelta));
        var observedEventState = CalculateStatistics(
            constraints.Select(static item => item.ObservedEventStateDelta));
        var hitTimePercentage = CalculateStatistics(
            constraints.Select(static item => item.HitTimePercentageDelta));
        var single = constraints.Where(static item => item.EntireConstraintSinglePercentage).ToArray();
        var overlap = constraints.Where(static item => !item.EntireConstraintSinglePercentage).ToArray();
        var roleConditional = constraints.Where(static item => item.RoleConditionalMagnitude).ToArray();
        var cleanSingleDirect = constraints.Where(static item =>
            item.EntireConstraintSinglePercentage &&
            item.RateOverlapEventCount == 0 &&
            item.GuaranteedEventCount == 0 &&
            item.PeriodicEventCount == 0 &&
            item.PetEventCount == 0).ToArray();
        var cleanOverlapDirect = constraints.Where(static item =>
            !item.EntireConstraintSinglePercentage &&
            item.RateOverlapEventCount == 0 &&
            item.GuaranteedEventCount == 0 &&
            item.PeriodicEventCount == 0 &&
            item.PetEventCount == 0).ToArray();
        var noRateDirect = constraints.Where(static item =>
            item.RateOverlapEventCount == 0 && item.PeriodicEventCount == 0).ToArray();
        var noRatePeriodic = constraints.Where(static item =>
            item.RateOverlapEventCount == 0 && item.PeriodicEventCount > 0).ToArray();
        var rateOverlap = constraints.Where(static item => item.RateOverlapEventCount > 0).ToArray();
        var cleanSingleObserved = CalculateStatistics(
            cleanSingleDirect.Select(static item => item.ObservedEventStateDelta));
        var cleanOverlapObserved = CalculateStatistics(
            cleanOverlapDirect.Select(static item => item.ObservedEventStateDelta));
        var noRateDirectObserved = CalculateStatistics(
            noRateDirect.Select(static item => item.ObservedEventStateDelta));
        var noRatePeriodicObserved = CalculateStatistics(
            noRatePeriodic.Select(static item => item.ObservedEventStateDelta));
        var rateOverlapObserved = CalculateStatistics(
            rateOverlap.Select(static item => item.ObservedEventStateDelta));
        var findings = new List<string>
        {
            $"Reference direction audit: {reference.Count(static item => item.Exact)}/{reference.Count} provider given totals equal the sum of recipient taken totals within 0.05 damage.",
            $"Whole-constraint single-percentage controls: N={single.Length}, authoritative MAE={CalculateStatistics(single.Select(static item => item.AuthoritativeDelta)).MeanAbsoluteResidual:F3}, max={CalculateStatistics(single.Select(static item => item.AuthoritativeDelta)).MaximumAbsoluteResidual:F3}.",
            $"Constraints containing percentage overlap: N={overlap.Length}, authoritative MAE={CalculateStatistics(overlap.Select(static item => item.AuthoritativeDelta)).MeanAbsoluteResidual:F3}, max={CalculateStatistics(overlap.Select(static item => item.AuthoritativeDelta)).MaximumAbsoluteResidual:F3}.",
            $"AST role-conditional card constraints: N={roleConditional.Length}; changing only official 6%/3% target-role metadata moves aggregate MAE from {CalculateStatistics(roleConditional.Select(static item => item.LegacyMetadataDelta)).MeanAbsoluteResidual:F3} to {CalculateStatistics(roleConditional.Select(static item => item.AuthoritativeDelta)).MeanAbsoluteResidual:F3}.",
            $"Clean single-buff direct controls under packet-ordered explicit removal: N={cleanSingleObserved.ConstraintCount}, exact={cleanSingleObserved.ZeroCount}, MAE={cleanSingleObserved.MeanAbsoluteResidual:F6}.",
            $"Clean multi-buff direct controls under packet-ordered explicit removal: N={cleanOverlapObserved.ConstraintCount}, exact={cleanOverlapObserved.ZeroCount}, MAE={cleanOverlapObserved.MeanAbsoluteResidual:F3}.",
            $"No-rate direct constraints under observed event state: N={noRateDirectObserved.ConstraintCount}, exact={noRateDirectObserved.ZeroCount}, MAE={noRateDirectObserved.MeanAbsoluteResidual:F3}; no-rate periodic N={noRatePeriodicObserved.ConstraintCount}, MAE={noRatePeriodicObserved.MeanAbsoluteResidual:F3}.",
            $"Rate-overlap constraints remain structurally positive after the same event-state correction: N={rateOverlapObserved.ConstraintCount}, mean={rateOverlapObserved.MeanResidual:+0.000;-0.000;0.000}, MAE={rateOverlapObserved.MeanAbsoluteResidual:F3}.",
        };
        return new PercentageAuditReport(
            DateTimeOffset.UtcNow,
            samples.Length,
            constraints.Count,
            windows.Count,
            reference.Count,
            reference.Count(static item => item.Exact),
            unmatched.Order(StringComparer.Ordinal).ToArray(),
            beforeFix,
            current,
            legacy,
            authoritative,
            allActive,
            selfStripped,
            calculatedMultiplier,
            packetOrdered,
            explicitRemoval,
            observedEventState,
            hitTimePercentage,
            reference,
            constraints,
            windows,
            statistics,
            constraints.OrderByDescending(static item => Math.Abs(item.AuthoritativeDelta)).Take(10).ToArray(),
            constraints.OrderBy(static item => Math.Abs(item.AuthoritativeDelta)).Take(5).ToArray(),
            findings,
            [
                "FFLogs DamageDone recipient taken[] is the aggregate reference. FFLogs does not expose percentage contribution per event or per window, so window rows deliberately mark that field unavailable.",
                "CurrentProduction is a forward replay using production-covered statuses, official AST target-role multipliers, Mage's Ballad, and packet-correlated ordering; no FFLogs result is fed into event calculations.",
                "ProductionBeforeFix is a historical diagnostic that excludes Mage's Ballad and retains the fixed 6% AST assumption while using the corrected packet order. It exists only to provide an apples-to-apples metadata delta.",
                "LegacyPublishedMath uses every fixed registry status but retains the old fixed 6% AST card assumption. AuthoritativeMetadata uses the official target-role 6%/3% rule.",
                "Radiant Finale is excluded because cached status events do not encode its Coda-dependent rank.",
                "Eligible damage uses effective FFLogs event amount. Direct and periodic timing follow the same hit-time / application-snapshot rules as the existing attribution timeline.",
                "Explicit removal fixes the clean direct controls but worsens the mixed all-constraint aggregate, so it remains a counterfactual rather than an unproven production expiry rewrite.",
                "FFLogs calculateddamage multiplier is a diagnostic field, not a documented list of contributing statuses; it is never treated as the public percentage multiplier M.",
            ]);
    }

    private static void AnalyzeFight(
        NormalizedAttributionFight fight,
        string sourceCache,
        ICollection<PercentageConstraintAuditRow> results,
        ICollection<PercentageWindowAuditRow> windows,
        ICollection<string> unmatched)
    {
        var timeline = new AttributionTimeline(fight);
        var partyIds = fight.Party.Select(static actor => actor.Id).ToHashSet();
        var aoeActions = fight.Events
            .Where(static item => FflogsEventNormalizer.IsDamageEvent(item) && item.Amount > 0)
            .GroupBy(static item => (item.Timestamp, item.SourceId, item.AbilityId))
            .Where(static group => group.Select(item => item.TargetId).Distinct().Count() > 1)
            .Select(static group => group.Key)
            .ToHashSet();
        var accumulators = new Dictionary<ConstraintKey, ConstraintAccumulator>();
        foreach (var item in fight.Events.Where(static item =>
                     FflogsEventNormalizer.IsDamageEvent(item) && item.Amount > 0))
        {
            var owner = FflogsEventNormalizer.ResolveOwnerActor(item.SourceId, fight.Actors);
            if (owner is null || !partyIds.Contains(owner.Id) || partyIds.Contains(item.TargetId))
            {
                continue;
            }
            var state = timeline.Resolve(item, owner);
            var packetState = timeline.Resolve(item, owner, AttributionTimelineSemantics.PacketOrdered);
            var removalState = timeline.Resolve(item, owner, AttributionTimelineSemantics.ExplicitRemoval);
            var observedState = timeline.Resolve(item, owner, AttributionTimelineSemantics.ObservedEventState);
            var hitTimeState = timeline.ResolveAtHitTime(
                item, owner, AttributionTimelineSemantics.ObservedEventState);
            var externalPercentage = state.Buffs
                .Where(static buff => !buff.IsSelfSourced &&
                                      buff.Definition.Dimension == OffensiveBuffDimension.PercentageDamage &&
                                      buff.Definition.DamageMultiplier is not null)
                .ToArray();
            var selfPercentage = state.Buffs
                .Where(static buff => buff.IsSelfSourced &&
                                      buff.Definition.Dimension == OffensiveBuffDimension.PercentageDamage &&
                                      buff.DamageMultiplier > 1)
                .ToArray();
            var providerBuffs = new[] { state, packetState, removalState, observedState, hitTimeState }
                .SelectMany(static candidate => candidate.Buffs)
                .Where(static buff => !buff.IsSelfSourced &&
                                      buff.Definition.Dimension == OffensiveBuffDimension.PercentageDamage &&
                                      buff.Definition.DamageMultiplier is not null)
                .DistinctBy(static buff => (buff.SourceActorId, buff.Definition.StatusId))
                .ToArray();
            if (providerBuffs.Length == 0)
            {
                continue;
            }
            var guaranteed = timeline.ResolveGuaranteed(item, owner).Dimensions != ProbeGuaranteedDimensions.None;
            var rateOverlap = state.RateBuffs.Any(static buff => !buff.IsSelfSourced);
            var pet = item.SourceId != owner.Id;
            var aoe = aoeActions.Contains((item.Timestamp, item.SourceId, item.AbilityId));
            foreach (var providerBuff in providerBuffs)
            {
                var key = new ConstraintKey(owner.Id, providerBuff.SourceActorId, providerBuff.Definition.StatusId);
                if (!accumulators.TryGetValue(key, out var accumulator))
                {
                    accumulator = new ConstraintAccumulator(owner, providerBuff, sourceCache);
                    accumulators.Add(key, accumulator);
                }
                var currentProvider = FindProvider(state, providerBuff);
                if (currentProvider is not null)
                {
                    accumulator.Observe(
                        item,
                        state,
                        currentProvider,
                        externalPercentage,
                        selfPercentage,
                        rateOverlap,
                        guaranteed,
                        pet,
                        aoe);
                }
                accumulator.ObserveTimelineCandidates(
                    item,
                    packetState,
                    FindProvider(packetState, providerBuff),
                    removalState,
                    FindProvider(removalState, providerBuff),
                    observedState,
                    FindProvider(observedState, providerBuff),
                    hitTimeState,
                    FindProvider(hitTimeState, providerBuff));
            }
        }

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
            results.Add(accumulator.Build(fight, reference));
            foreach (var window in accumulator.BuildWindows(fight))
            {
                windows.Add(window);
            }
        }

        foreach (var tableActor in fight.DamageTableActors.Values)
        {
            foreach (var taken in tableActor.Taken.Where(item =>
                         OffensiveBuffRegistry.ByStatusId.TryGetValue(item.AbilityId, out var definition) &&
                         definition.Dimension == OffensiveBuffDimension.PercentageDamage &&
                         definition.DamageMultiplier is not null &&
                         item.Amount > 0))
            {
                if (!accumulators.Keys.Any(key =>
                        key.RecipientActorId == tableActor.ActorId && key.StatusId == taken.AbilityId))
                {
                    unmatched.Add(
                        $"{fight.Seed.ReportCode}:{fight.Fight.Id} recipient={tableActor.ActorName} " +
                        $"buff={taken.AbilityName}/{taken.AbilityId} reference={taken.Amount:F3}");
                }
            }
        }
    }

    private static MatrixBuffExposureEntry? FindProvider(
        MatrixEventAttributionState state,
        MatrixBuffExposureEntry provider)
        => state.Buffs.FirstOrDefault(buff =>
            !buff.IsSelfSourced &&
            buff.SourceActorId == provider.SourceActorId &&
            buff.Definition.StatusId == provider.Definition.StatusId);

    private static IReadOnlyList<PercentageReferenceAuditRow> BuildReferenceAudit(
        IReadOnlyList<NormalizedAttributionFight> fights,
        IReadOnlyList<PercentageConstraintAuditRow> constraints)
    {
        var fightMap = fights.ToDictionary(static item => (item.Seed.ReportCode, item.Fight.Id));
        return constraints
            .GroupBy(static item => (item.Report, item.FightId, item.ProviderActorId,
                item.ProviderActor, item.ProviderJob, item.BuffStatusId, item.BuffName))
            .Select(group =>
            {
                var fight = fightMap[(group.Key.Report, group.Key.FightId)];
                var given = fight.DamageTableActors.GetValueOrDefault(group.Key.ProviderActorId)?.Given
                    .Where(item => item.AbilityId == group.Key.BuffStatusId)
                    .Sum(static item => item.Amount) ?? 0;
                var taken = group.Sum(static item => item.FflogsContribution);
                var delta = taken - given;
                return new PercentageReferenceAuditRow(
                    group.Key.Report,
                    group.Key.FightId,
                    group.Key.ProviderActorId,
                    group.Key.ProviderActor,
                    group.Key.ProviderJob,
                    group.Key.BuffStatusId,
                    group.Key.BuffName,
                    given,
                    taken,
                    delta,
                    group.Select(static item => item.RecipientActorId).Distinct().Count(),
                    Math.Abs(delta) <= 0.05);
            })
            .OrderBy(static item => item.Report, StringComparer.Ordinal)
            .ThenBy(static item => item.FightId)
            .ThenBy(static item => item.ProviderActorId)
            .ThenBy(static item => item.BuffStatusId)
            .ToArray();
    }

    private static IReadOnlyList<PercentageProviderAuditStatistics> BuildStatistics(
        IReadOnlyList<PercentageConstraintAuditRow> constraints)
    {
        var groups = new List<(string Dimension, string Value, PercentageConstraintAuditRow[] Rows)>();
        groups.AddRange(constraints.GroupBy(static item => item.BuffName)
            .Select(group => ("provider", group.Key, group.ToArray())));
        groups.AddRange(constraints.GroupBy(static item => item.ProviderType)
            .Select(group => ("providerType", group.Key, group.ToArray())));
        groups.AddRange(constraints.GroupBy(static item => item.EntireConstraintSinglePercentage ? "single" : "overlap")
            .Select(group => ("percentageOverlap", group.Key, group.ToArray())));
        return groups
            .Select(group => new PercentageProviderAuditStatistics(
                group.Dimension,
                group.Value,
                CalculateStatistics(group.Rows.Select(static item => item.CurrentProductionDelta)),
                CalculateStatistics(group.Rows.Select(static item => item.LegacyMetadataDelta)),
                CalculateStatistics(group.Rows.Select(static item => item.AuthoritativeDelta))))
            .OrderBy(static item => item.Dimension, StringComparer.Ordinal)
            .ThenBy(static item => item.Value, StringComparer.Ordinal)
            .ToArray();
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
            : (ordered[(ordered.Length / 2) - 1] + ordered[ordered.Length / 2]) / 2;
        return new MatrixResidualStatistics(
            values.Length,
            values.Average(),
            median,
            values.Average(static value => Math.Abs(value)),
            Math.Sqrt(values.Average(static value => value * value)),
            values.Max(static value => Math.Abs(value)),
            values.Count(static value => value < -0.05),
            values.Count(static value => Math.Abs(value) <= 0.05),
            values.Count(static value => value > 0.05));
    }

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

    private readonly record struct ConstraintKey(
        int RecipientActorId,
        int ProviderActorId,
        long StatusId);

    private readonly record struct WindowKey(double Start, double End);

    private sealed class ConstraintAccumulator
    {
        private readonly string sourceCache;
        private readonly HashSet<string> percentageComposition = new(StringComparer.Ordinal);
        private readonly HashSet<string> selfPercentageComposition = new(StringComparer.Ordinal);
        private readonly Dictionary<WindowKey, WindowAccumulator> windows = [];
        private double authoritativeMultiplierWeighted;
        private double legacyMultiplierWeighted;

        public ConstraintAccumulator(
            FflogsActor recipient,
            MatrixBuffExposureEntry providerBuff,
            string sourceCache)
        {
            Recipient = recipient;
            ProviderBuff = providerBuff;
            this.sourceCache = sourceCache;
        }

        public FflogsActor Recipient { get; }

        public MatrixBuffExposureEntry ProviderBuff { get; }

        private int EventCount { get; set; }
        private long EligibleDamage { get; set; }
        private int SingleEventCount { get; set; }
        private long SingleDamage { get; set; }
        private int OverlapEventCount { get; set; }
        private long OverlapDamage { get; set; }
        private int RateOverlapEventCount { get; set; }
        private int SelfPercentageEventCount { get; set; }
        private int CalculatedMultiplierEventCount { get; set; }
        private int GuaranteedEventCount { get; set; }
        private int DirectEventCount { get; set; }
        private int PeriodicEventCount { get; set; }
        private int PetEventCount { get; set; }
        private int AoeEventCount { get; set; }
        private double CurrentProductionBeforeFix { get; set; }
        private double CurrentProduction { get; set; }
        private double LegacyPublished { get; set; }
        private double Authoritative { get; set; }
        private double AllActiveDenominator { get; set; }
        private double SelfStrippedBasis { get; set; }
        private double CalculatedMultiplier { get; set; }
        private double PacketOrdered { get; set; }
        private double ExplicitRemoval { get; set; }
        private double ObservedEventState { get; set; }
        private double HitTimePercentage { get; set; }

        public void Observe(
            NormalizedFflogsEvent item,
            MatrixEventAttributionState state,
            MatrixBuffExposureEntry providerBuff,
            IReadOnlyList<MatrixBuffExposureEntry> externalPercentage,
            IReadOnlyList<MatrixBuffExposureEntry> selfPercentage,
            bool rateOverlap,
            bool guaranteed,
            bool pet,
            bool aoe)
        {
            EventCount++;
            EligibleDamage += item.Amount;
            authoritativeMultiplierWeighted += providerBuff.DamageMultiplier * item.Amount;
            legacyMultiplierWeighted += providerBuff.LegacyDamageMultiplier * item.Amount;
            if (externalPercentage.Count == 1)
            {
                SingleEventCount++;
                SingleDamage += item.Amount;
            }
            else
            {
                OverlapEventCount++;
                OverlapDamage += item.Amount;
            }
            if (rateOverlap)
            {
                RateOverlapEventCount++;
            }
            if (selfPercentage.Count > 0)
            {
                SelfPercentageEventCount++;
                foreach (var buff in selfPercentage)
                {
                    selfPercentageComposition.Add(
                        $"{buff.SourceJob}:{buff.Definition.ActionName}:{buff.DamageMultiplier:P0}");
                }
            }
            if (guaranteed)
            {
                GuaranteedEventCount++;
            }
            if (item.IsPeriodic)
            {
                PeriodicEventCount++;
            }
            else
            {
                DirectEventCount++;
            }
            if (pet)
            {
                PetEventCount++;
            }
            if (aoe)
            {
                AoeEventCount++;
            }
            CurrentProductionBeforeFix += AttributionContributionMath.CalculatePercentageContribution(
                item, state, providerBuff, PercentageCalculationMode.ProductionBeforeFix);
            CurrentProduction += AttributionContributionMath.CalculatePercentageContribution(
                item, state, providerBuff, PercentageCalculationMode.CurrentProduction);
            LegacyPublished += AttributionContributionMath.CalculatePercentageContribution(
                item, state, providerBuff, PercentageCalculationMode.PublishedMathLegacyMetadata);
            Authoritative += AttributionContributionMath.CalculatePercentageContribution(
                item, state, providerBuff, PercentageCalculationMode.AuthoritativeMetadata);
            AllActiveDenominator += AttributionContributionMath.CalculatePercentageContribution(
                item, state, providerBuff, PercentageCalculationMode.AuthoritativeAllActiveDenominator);
            SelfStrippedBasis += AttributionContributionMath.CalculatePercentageContribution(
                item, state, providerBuff, PercentageCalculationMode.AuthoritativeSelfStrippedBasis);
            if (item.Multiplier > 1)
            {
                CalculatedMultiplierEventCount++;
                CalculatedMultiplier += CalculateFromFflogsMultiplier(item, providerBuff);
            }
            foreach (var buff in externalPercentage)
            {
                percentageComposition.Add(
                    $"{buff.SourceJob}:{buff.Definition.ActionName}:{buff.DamageMultiplier:P0}");
            }
            var windowKey = new WindowKey(providerBuff.WindowStart, providerBuff.WindowEnd);
            if (!windows.TryGetValue(windowKey, out var window))
            {
                window = new WindowAccumulator(providerBuff);
                windows.Add(windowKey, window);
            }
            window.Observe(item, state, externalPercentage, pet, aoe);
        }

        public void ObserveTimelineCandidates(
            NormalizedFflogsEvent item,
            MatrixEventAttributionState packetState,
            MatrixBuffExposureEntry? packetProvider,
            MatrixEventAttributionState removalState,
            MatrixBuffExposureEntry? removalProvider,
            MatrixEventAttributionState observedState,
            MatrixBuffExposureEntry? observedProvider,
            MatrixEventAttributionState hitTimeState,
            MatrixBuffExposureEntry? hitTimeProvider)
        {
            // Each counterfactual is a forward replay from event state. A provider
            // absent under that timeline contributes zero; aggregate FFLogs totals
            // are never used to decide event eligibility.
            if (packetProvider is not null)
            {
                PacketOrdered += AttributionContributionMath.CalculatePercentageContribution(
                    item, packetState, packetProvider, PercentageCalculationMode.AuthoritativeMetadata);
            }
            if (removalProvider is not null)
            {
                ExplicitRemoval += AttributionContributionMath.CalculatePercentageContribution(
                    item, removalState, removalProvider, PercentageCalculationMode.AuthoritativeMetadata);
            }
            if (observedProvider is not null)
            {
                ObservedEventState += AttributionContributionMath.CalculatePercentageContribution(
                    item, observedState, observedProvider, PercentageCalculationMode.AuthoritativeMetadata);
            }
            if (hitTimeProvider is not null)
            {
                HitTimePercentage += AttributionContributionMath.CalculatePercentageContribution(
                    item, hitTimeState, hitTimeProvider, PercentageCalculationMode.AuthoritativeMetadata);
            }
        }

        public PercentageConstraintAuditRow Build(NormalizedAttributionFight fight, double reference)
            => new(
                fight.Seed.ReportCode,
                fight.Fight.Id,
                fight.Fight.EncounterId,
                fight.Fight.Name,
                fight.PartyComposition,
                ResolveProviderType(ProviderBuff.Definition),
                ProviderBuff.SourceActorId,
                ProviderBuff.SourceActor,
                ProviderBuff.SourceJob,
                Recipient.Id,
                Recipient.Name,
                ToJobAbbreviation(Recipient.Job),
                ProviderBuff.Definition.StatusId,
                ProviderBuff.Definition.ActionName,
                ProviderBuff.Definition.Magnitude,
                EligibleDamage > 0 ? authoritativeMultiplierWeighted / EligibleDamage : 1,
                EligibleDamage > 0 ? legacyMultiplierWeighted / EligibleDamage : 1,
                reference,
                CurrentProductionBeforeFix,
                CurrentProduction,
                LegacyPublished,
                Authoritative,
                AllActiveDenominator,
                SelfStrippedBasis,
                CalculatedMultiplier,
                PacketOrdered,
                ExplicitRemoval,
                ObservedEventState,
                HitTimePercentage,
                CurrentProductionBeforeFix - reference,
                CurrentProduction - reference,
                LegacyPublished - reference,
                Authoritative - reference,
                AllActiveDenominator - reference,
                SelfStrippedBasis - reference,
                CalculatedMultiplier - reference,
                PacketOrdered - reference,
                ExplicitRemoval - reference,
                ObservedEventState - reference,
                HitTimePercentage - reference,
                EventCount,
                EligibleDamage,
                SingleEventCount,
                SingleDamage,
                OverlapEventCount,
                OverlapDamage,
                RateOverlapEventCount,
                SelfPercentageEventCount,
                CalculatedMultiplierEventCount,
                GuaranteedEventCount,
                DirectEventCount,
                PeriodicEventCount,
                PetEventCount,
                AoeEventCount,
                windows.Count,
                OverlapEventCount == 0,
                ProviderBuff.Definition.StatusId is 3887 or 3889,
                string.Join(" + ", percentageComposition.Order(StringComparer.Ordinal)),
                string.Join(" + ", selfPercentageComposition.Order(StringComparer.Ordinal)),
                sourceCache,
                fight.NormalizationWarnings);

        public IEnumerable<PercentageWindowAuditRow> BuildWindows(NormalizedAttributionFight fight)
            => windows.OrderBy(static pair => pair.Key.Start).Select(pair => pair.Value.Build(
                fight,
                Recipient,
                ResolveProviderType(ProviderBuff.Definition)));
    }

    private sealed class WindowAccumulator
    {
        private readonly MatrixBuffExposureEntry providerBuff;
        private readonly HashSet<string> composition = new(StringComparer.Ordinal);
        private double multiplierWeighted;
        private long eligibleDamage;
        private int eventCount;
        private int singleEventCount;
        private long singleDamage;
        private int overlapEventCount;
        private long overlapDamage;
        private int directEventCount;
        private int periodicEventCount;
        private int petEventCount;
        private int aoeEventCount;
        private double legacyContribution;
        private double authoritativeContribution;
        private double allActiveDenominatorContribution;
        private double selfStrippedBasisContribution;
        private double calculatedMultiplierContribution;

        public WindowAccumulator(MatrixBuffExposureEntry providerBuff)
            => this.providerBuff = providerBuff;

        public void Observe(
            NormalizedFflogsEvent item,
            MatrixEventAttributionState state,
            IReadOnlyList<MatrixBuffExposureEntry> externalPercentage,
            bool pet,
            bool aoe)
        {
            eventCount++;
            eligibleDamage += item.Amount;
            multiplierWeighted += providerBuff.DamageMultiplier * item.Amount;
            if (externalPercentage.Count == 1)
            {
                singleEventCount++;
                singleDamage += item.Amount;
            }
            else
            {
                overlapEventCount++;
                overlapDamage += item.Amount;
            }
            if (item.IsPeriodic)
            {
                periodicEventCount++;
            }
            else
            {
                directEventCount++;
            }
            if (pet)
            {
                petEventCount++;
            }
            if (aoe)
            {
                aoeEventCount++;
            }
            legacyContribution += AttributionContributionMath.CalculatePercentageContribution(
                item, new MatrixEventAttributionState(externalPercentage, 1, 0, 0, 0, 0, "audit"),
                providerBuff, PercentageCalculationMode.PublishedMathLegacyMetadata);
            authoritativeContribution += AttributionContributionMath.CalculatePercentageContribution(
                item, state, providerBuff, PercentageCalculationMode.AuthoritativeMetadata);
            allActiveDenominatorContribution += AttributionContributionMath.CalculatePercentageContribution(
                item, state, providerBuff, PercentageCalculationMode.AuthoritativeAllActiveDenominator);
            selfStrippedBasisContribution += AttributionContributionMath.CalculatePercentageContribution(
                item, state, providerBuff, PercentageCalculationMode.AuthoritativeSelfStrippedBasis);
            calculatedMultiplierContribution += CalculateFromFflogsMultiplier(item, providerBuff);
            foreach (var buff in externalPercentage)
            {
                composition.Add($"{buff.SourceJob}:{buff.Definition.ActionName}:{buff.DamageMultiplier:P0}");
            }
        }

        public PercentageWindowAuditRow Build(
            NormalizedAttributionFight fight,
            FflogsActor recipient,
            string providerType)
            => new(
                fight.Seed.ReportCode,
                fight.Fight.Id,
                fight.Fight.Name,
                providerType,
                providerBuff.SourceActorId,
                providerBuff.SourceActor,
                providerBuff.SourceJob,
                recipient.Id,
                recipient.Name,
                ToJobAbbreviation(recipient.Job),
                providerBuff.Definition.StatusId,
                providerBuff.Definition.ActionName,
                providerBuff.WindowStart,
                providerBuff.WindowEnd,
                eligibleDamage > 0 ? multiplierWeighted / eligibleDamage : 1,
                eventCount,
                eligibleDamage,
                singleEventCount,
                singleDamage,
                overlapEventCount,
                overlapDamage,
                directEventCount,
                periodicEventCount,
                petEventCount,
                aoeEventCount,
                legacyContribution,
                authoritativeContribution,
                allActiveDenominatorContribution,
                selfStrippedBasisContribution,
                calculatedMultiplierContribution,
                string.Join(" + ", composition.Order(StringComparer.Ordinal)),
                "unavailable: FFLogs public API exposes only fight-level recipient taken[]");
    }

    private static string ToJobAbbreviation(string job)
        => job.Trim().ToUpperInvariant() switch
        {
            "PALADIN" => "PLD", "WARRIOR" => "WAR", "DARKKNIGHT" => "DRK", "GUNBREAKER" => "GNB",
            "MONK" => "MNK", "DRAGOON" => "DRG", "NINJA" => "NIN", "SAMURAI" => "SAM",
            "REAPER" => "RPR", "VIPER" => "VPR", "BARD" => "BRD", "MACHINIST" => "MCH",
            "DANCER" => "DNC", "BLACKMAGE" => "BLM", "SUMMONER" => "SMN", "REDMAGE" => "RDM",
            "PICTOMANCER" => "PCT", "WHITEMAGE" => "WHM", "SCHOLAR" => "SCH",
            "ASTROLOGIAN" => "AST", "SAGE" => "SGE", var value => value,
        };

    private static double CalculateFromFflogsMultiplier(
        NormalizedFflogsEvent item,
        MatrixBuffExposureEntry providerBuff)
    {
        var totalMultiplier = item.Multiplier;
        var providerMultiplier = providerBuff.DamageMultiplier;
        if (totalMultiplier <= 1 || providerMultiplier <= 1)
        {
            return 0;
        }
        var lostDamage = item.Amount - (item.Amount / totalMultiplier);
        return lostDamage * Math.Log(providerMultiplier) / Math.Log(totalMultiplier);
    }
}

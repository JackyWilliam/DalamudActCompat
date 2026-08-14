using System.Text.Json;
using DalamudActCompat.ActRuntime;

namespace DalamudActCompat.FflogsParityHarness;

internal static class DevilmentPerActionProbe
{
    private const string UnavailablePerAction =
        "unavailable_api: FFLogs events/table expose only fight-level buff totals";
    private const string UnavailableBaseline =
        "unavailable_api: cached public API data does not expose the player's actual Cu/Du";

    public static DevilmentProbeReport Run(
        FflogsSampleCollector collector,
        CacheManifest manifest,
        string outputDirectory)
    {
        var firstRound = ReadFirstRoundReport(outputDirectory);
        var selections = SelectSamples(firstRound.Samples);
        var stableGuarantees = ProductionGuaranteedMetadata.ReadStableActions();
        var samples = new List<DevilmentSampleProbeResult>();
        var actions = new List<DevilmentActionProbeResult>();
        var windows = new List<DevilmentWindowProbeResult>();
        var sensitivity = new List<DevilmentSensitivityProbeResult>();

        foreach (var selection in selections)
        {
            var seed = manifest.Seeds.Single(seed =>
                seed.ReportCode == selection.Report && seed.FightId == selection.FightId);
            var fight = FflogsEventNormalizer.Normalize(collector.ReadCachedSample(seed));
            var replay = ReplaySelectedFight(fight, selection, stableGuarantees);
            samples.Add(replay.Sample);
            actions.AddRange(replay.Actions);
            windows.AddRange(replay.Windows);
            sensitivity.AddRange(replay.Sensitivity);
            Console.WriteLine(
                $"Devilment probe replayed {samples.Count}/{selections.Count}: " +
                $"{selection.PartnerJob} {selection.Report} fight {selection.FightId}.");
        }

        actions = PromoteUnknownCoverageCandidates(actions);
        var coverage = BuildCoverage(actions, stableGuarantees);
        var categories = BuildCategories(actions);
        var topActions = BuildTopActions(actions);
        return new DevilmentProbeReport(
            DateTimeOffset.UtcNow,
            selections.Count,
            actions.Count,
            selections,
            BuildFirstRoundPartnerComparison(firstRound.Samples),
            samples,
            actions,
            categories,
            coverage,
            windows,
            sensitivity,
            topActions,
            [
                "No FFLogs request is made in --devilment-probe mode; all inputs come from the existing 100-sample raw cache.",
                "FFLogs public API events do not expose per-action or per-window rDPS allocation. Those FFLogs fields remain null and are never back-solved from the fight total.",
                "The production contribution is measured as the Dancer's before/after Critical and DirectHit attribution counters around each effective-damage event.",
                "Analytical calculations are diagnostics only. Baseline output and final DACT rDPS continue to come from the unmodified production RaidDpsEstimator.",
                "The SAM scenario tests the published regular-hit path on production-known guaranteed actions; the DRG scenario tests production's guaranteed path on Life Surge packet-state actions. Neither scenario is labeled FFLogs truth.",
                "FFLogs public rDPS documentation specifies regular and simulated-DoT math but does not publish a guaranteed-hit attribution branch.",
            ]);
    }

    private static IReadOnlyList<PartnerParityComparison> BuildFirstRoundPartnerComparison(
        IReadOnlyList<ParitySampleResult> samples)
        => new[] { "SAM", "DRG" }
            .Select(job => samples.Where(sample => sample.DancePartnerJob == job).ToArray())
            .Where(static values => values.Length > 0)
            .Select(values => new PartnerParityComparison(
                values[0].DancePartnerJob,
                values.Length,
                values.Average(static sample => sample.DeltaRdps),
                values.Average(static sample => sample.ExternalBuffContributionReceivedDelta),
                values.Average(static sample => sample.OwnBuffContributionGivenDelta),
                values.Average(static sample => sample.TechnicalAndStandardContributionDelta),
                values.Average(static sample => sample.DevilmentContributionDelta)))
            .ToArray();

    private static ParityReport ReadFirstRoundReport(string outputDirectory)
    {
        var path = Path.Combine(outputDirectory, "parity-report.json");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                "The first-round parity-report.json is required for deterministic sample selection.",
                path);
        }
        return JsonSerializer.Deserialize<ParityReport>(
                   File.ReadAllText(path),
                   new JsonSerializerOptions(JsonSerializerDefaults.Web)
                   {
                       PropertyNameCaseInsensitive = true,
                   })
               ?? throw new InvalidDataException($"First-round report '{path}' is empty.");
    }

    private static IReadOnlyList<DevilmentProbeSampleSelection> SelectSamples(
        IReadOnlyList<ParitySampleResult> samples)
    {
        var result = new List<DevilmentProbeSampleSelection>();
        var selected = new HashSet<(string Report, int FightId)>();
        var sam = samples
            .Where(static sample => sample.DancePartnerJob == "SAM")
            .OrderBy(static sample => sample.DeltaRdps)
            .ToArray();
        AddDistinct(result, selected, sam.Take(3), "SAM largest negative final rDPS delta");
        if (sam.Length > 0)
        {
            var middle = sam.Length / 2;
            AddDistinct(
                result,
                selected,
                sam.Skip(Math.Max(0, middle - 1)).Take(2),
                "SAM median-band final rDPS delta");
            AddDistinct(
                result,
                selected,
                sam.OrderBy(static sample => Math.Abs(sample.DeltaRdps)).Take(1),
                "SAM closest-to-FFLogs sample");
        }

        var drg = samples.Where(static sample => sample.DancePartnerJob == "DRG").ToArray();
        AddDistinct(
            result,
            selected,
            drg.OrderBy(static sample => Math.Abs(sample.DeltaRdps)).Take(3),
            "DRG closest-to-zero final rDPS delta");
        var orderedDrg = drg.OrderBy(static sample => sample.DeltaRdps).ToArray();
        foreach (var quantile in new[] { 0.25, 0.75 })
        {
            if (orderedDrg.Length == 0)
            {
                continue;
            }
            var index = (int)Math.Round((orderedDrg.Length - 1) * quantile);
            AddDistinct(
                result,
                selected,
                [orderedDrg[index]],
                $"DRG representative delta quantile {quantile:P0}");
        }
        return result;
    }

    private static void AddDistinct(
        ICollection<DevilmentProbeSampleSelection> destination,
        ISet<(string Report, int FightId)> selected,
        IEnumerable<ParitySampleResult> samples,
        string reason)
    {
        foreach (var sample in samples)
        {
            if (!selected.Add((sample.Report, sample.FightId)))
            {
                continue;
            }
            destination.Add(new DevilmentProbeSampleSelection(
                sample.Report,
                sample.FightId,
                sample.DancePartnerJob,
                reason,
                sample.DeltaRdps));
        }
    }

    private static SelectedFightReplay ReplaySelectedFight(
        NormalizedFight fight,
        DevilmentProbeSampleSelection selection,
        IReadOnlyDictionary<long, ProbeGuaranteedDimensions> stableGuarantees)
    {
        var parity = DactRdpsReplay.Replay(fight);
        var timeline = new FightAttributionTimeline(fight);
        var partner = FflogsEventNormalizer.ResolveDancePartnerActor(
                          fight.Events,
                          fight.Actors,
                          fight.Dancer.Id)
                      ?? throw new InvalidDataException("Selected fight has no resolvable Dance Partner actor.");
        var targetIds = timeline.DevilmentTargetIds;
        var estimator = new RaidDpsEstimator();
        estimator.Reset();
        var encounterStart = DactRdpsReplay.ToTimestamp(fight.ReportStartTime, fight.Fight.StartTime);
        foreach (var actor in fight.Actors.Values)
        {
            estimator.ObserveNetworkLine(encounterStart, DactRdpsReplay.BuildActorLine(actor));
        }
        estimator.StartEncounter(encounterStart);

        var partyIds = fight.Party.Select(static actor => actor.Id).ToHashSet();
        var contexts = new List<ActionProbeContext>();
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
                estimator.ObserveStatusLine(
                    timestamp,
                    DactRdpsReplay.BuildStatusLine(item, fight, remove: false));
                continue;
            }
            if (FflogsEventNormalizer.IsStatusRemove(item.Type))
            {
                estimator.ObserveStatusLine(
                    timestamp,
                    DactRdpsReplay.BuildStatusLine(item, fight, remove: true));
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
            var criticalContribution = estimator.ResolveContributedDamage(
                                           fight.Dancer.Name,
                                           RaidDpsEstimator.AttributionKind.Critical) -
                                       beforeCritical;
            var directContribution = estimator.ResolveContributedDamage(
                                         fight.Dancer.Name,
                                         RaidDpsEstimator.AttributionKind.DirectHit) -
                                     beforeDirect;
            var productionContribution = criticalContribution + directContribution;
            var state = timeline.Resolve(item, owner);
            if (!targetIds.Contains(owner.Id) ||
                (!state.DevilmentActiveAtHit &&
                 state.DevilmentCriticalIncrease <= 0 &&
                 state.DevilmentDirectIncrease <= 0 &&
                 Math.Abs(productionContribution) < 0.000_001))
            {
                continue;
            }

            var currentDimensions = stableGuarantees.GetValueOrDefault(item.AbilityId);
            var lifeSurge = timeline.TryResolveLifeSurgeAction(item, out var contextualDimensions);
            var expectedDimensions = currentDimensions != ProbeGuaranteedDimensions.None
                ? currentDimensions
                : lifeSurge ? contextualDimensions : ProbeGuaranteedDimensions.None;
            var category = ResolveCategory(currentDimensions, lifeSurge);
            var guaranteeSource = currentDimensions != ProbeGuaranteedDimensions.None
                ? "dact_production_stable_action_metadata"
                : lifeSurge
                    ? "inferred_from_fflogs_life_surge_apply_remove_boundary"
                    : "dact_metadata_no_guarantee";
            var metadata = DescribeDimensions(currentDimensions);
            var analytical = DevilmentContributionMath.Calculate(
                item,
                state,
                baseline.CriticalChance,
                baseline.DirectHitChance,
                currentDimensions);
            var regular = DevilmentContributionMath.Calculate(
                item,
                state,
                baseline.CriticalChance,
                baseline.DirectHitChance,
                ProbeGuaranteedDimensions.None);
            var guaranteed = DevilmentContributionMath.Calculate(
                item,
                state,
                baseline.CriticalChance,
                baseline.DirectHitChance,
                expectedDimensions);
            var analyticalTotal = analytical.Critical + analytical.Direct;
            var regularTotal = regular.Critical + regular.Direct;
            var guaranteedTotal = guaranteed.Critical + guaranteed.Direct;
            var scenarioCorrection = selection.PartnerJob == "SAM" &&
                                     currentDimensions != ProbeGuaranteedDimensions.None
                ? regularTotal - analyticalTotal
                : selection.PartnerJob == "DRG" && lifeSurge
                    ? guaranteedTotal - analyticalTotal
                    : 0;
            var beforeCritRate = Math.Clamp(
                baseline.CriticalChance +
                state.CriticalIncrease -
                state.DevilmentCriticalIncrease,
                0,
                1);
            var beforeDirectRate = Math.Clamp(
                baseline.DirectHitChance +
                state.DirectIncrease -
                state.DevilmentDirectIncrease,
                0,
                1);
            var record = new DevilmentActionProbeResult
            {
                Report = selection.Report,
                FightId = selection.FightId,
                SamplePartnerJob = selection.PartnerJob,
                PartnerJob = ToJobAbbreviation(owner.Job),
                Timestamp = item.Timestamp,
                SourceActorId = source.Id,
                SourceActor = source.Name,
                OwnerActorId = owner.Id,
                OwnerActor = owner.Name,
                ActionId = item.AbilityId,
                ActionName = item.AbilityName,
                RawDamage = item.Amount,
                EffectiveDamage = item.Amount,
                IsPeriodic = item.IsPeriodic,
                IsCrit = item.Critical,
                IsDirectHit = item.DirectHit,
                GuaranteedCrit = expectedDimensions == ProbeGuaranteedDimensions.None
                    ? false
                    : (expectedDimensions & ProbeGuaranteedDimensions.Critical) != 0,
                GuaranteedDirectHit = expectedDimensions == ProbeGuaranteedDimensions.None
                    ? false
                    : (expectedDimensions & ProbeGuaranteedDimensions.DirectHit) != 0,
                GuaranteedCritDh = expectedDimensions == ProbeGuaranteedDimensions.None
                    ? false
                    : expectedDimensions ==
                      (ProbeGuaranteedDimensions.Critical | ProbeGuaranteedDimensions.DirectHit),
                GuaranteeCategory = category,
                GuaranteeSource = guaranteeSource,
                CurrentDactMetadata = metadata,
                DevilmentActive = state.DevilmentActiveAtHit,
                DevilmentAttributionTiming = state.AttributionTiming,
                DevilmentWindow = state.DevilmentWindow,
                DactInferredCu = baseline.CriticalChance,
                DactInferredDu = baseline.DirectHitChance,
                CritRateBeforeBuff = beforeCritRate,
                CritRateAfterBuff = Math.Clamp(
                    beforeCritRate + state.DevilmentCriticalIncrease,
                    0,
                    1),
                DhRateBeforeBuff = beforeDirectRate,
                DhRateAfterBuff = Math.Clamp(
                    beforeDirectRate + state.DevilmentDirectIncrease,
                    0,
                    1),
                ReferenceCu = null,
                ReferenceDu = null,
                FflogsDevilmentContribution = null,
                FflogsContributionAvailability = UnavailablePerAction,
                DactCriticalContribution = criticalContribution,
                DactDirectHitContribution = directContribution,
                DactDevilmentContribution = productionContribution,
                DeltaContribution = null,
                AnalyticalProductionContribution = analyticalTotal,
                AnalyticalCalibrationResidual = productionContribution - analyticalTotal,
                PublishedRegularPathContribution = regularTotal,
                ProductionGuaranteedPathContribution = guaranteedTotal,
                ScenarioContributionCorrection = scenarioCorrection,
            };
            contexts.Add(new ActionProbeContext(
                record,
                item,
                state,
                currentDimensions,
                baseline.CriticalChance,
                baseline.DirectHitChance));
        }

        estimator.FinishEncounter();
        var actionRecords = contexts.Select(static context => context.Record).ToArray();
        var actionContribution = actionRecords.Sum(static action => action.DactDevilmentContribution);
        var analyticalResidual = actionRecords.Sum(static action => action.AnalyticalCalibrationResidual);
        var scenarioCorrectionTotal = actionRecords.Sum(static action => action.ScenarioContributionCorrection);
        var finalBaseline = estimator.ResolveHitBaseline(partner.Name);
        var components = BuildComponents(parity);
        var sampleResult = new DevilmentSampleProbeResult(
            selection,
            fight.Dancer.Name,
            fight.Fight.Name,
            parity.Duration,
            fight.PartyComposition,
            partner.Id,
            partner.Name,
            parity.FflogsRdps,
            parity.DactRdps,
            parity.DeltaRdps,
            parity.FflogsDevilmentContribution,
            parity.DevilmentContribution,
            parity.DevilmentContributionDelta,
            finalBaseline.CriticalChance,
            finalBaseline.DirectHitChance,
            null,
            null,
            UnavailableBaseline,
            actionContribution - parity.DevilmentContribution,
            analyticalResidual,
            scenarioCorrectionTotal,
            parity.DevilmentContribution + scenarioCorrectionTotal -
            parity.FflogsDevilmentContribution,
            components);
        return new SelectedFightReplay(
            sampleResult,
            actionRecords,
            BuildWindows(fight, selection, timeline, actionRecords),
            BuildSensitivity(parity, selection, contexts));
    }

    private static IReadOnlyList<ComponentParityProbeResult> BuildComponents(ParitySampleResult parity)
        =>
        [
            Component(parity, "Raw Damage", parity.FflogsDamageTableAmount, parity.RawDamage),
            Component(
                parity,
                "External Received",
                parity.FflogsExternalBuffContributionReceived,
                parity.ExternalBuffContributionReceived),
            Component(
                parity,
                "Own Given",
                parity.FflogsOwnBuffContributionGiven,
                parity.OwnBuffContributionGiven),
            new ComponentParityProbeResult(
                parity.Report,
                parity.FightId,
                parity.DancePartnerJob,
                "Technical",
                parity.FflogsTechnicalFinishContribution,
                null,
                null,
                "dact_unavailable: production aggregates Dancer percentage contribution by source"),
            new ComponentParityProbeResult(
                parity.Report,
                parity.FightId,
                parity.DancePartnerJob,
                "Standard",
                parity.FflogsStandardFinishContribution,
                null,
                null,
                "dact_unavailable: production aggregates Dancer percentage contribution by source"),
            Component(
                parity,
                "Technical + Standard",
                parity.FflogsTechnicalAndStandardContribution,
                parity.TechnicalAndStandardContribution),
            Component(
                parity,
                "Devilment",
                parity.FflogsDevilmentContribution,
                parity.DevilmentContribution),
            new ComponentParityProbeResult(
                parity.Report,
                parity.FightId,
                parity.DancePartnerJob,
                "Crit contribution",
                null,
                parity.CritContributionGiven,
                null,
                "fflogs_unavailable: public table does not split Devilment Crit vs DH"),
            new ComponentParityProbeResult(
                parity.Report,
                parity.FightId,
                parity.DancePartnerJob,
                "Direct Hit contribution",
                null,
                parity.DirectHitContributionGiven,
                null,
                "fflogs_unavailable: public table does not split Devilment Crit vs DH"),
            Component(
                parity,
                "Crit + Direct Hit contribution",
                parity.FflogsDevilmentContribution,
                parity.CritDirectHitContributionGiven),
            Component(parity, "Final rDPS", parity.FflogsRdps, parity.DactRdps),
        ];

    private static ComponentParityProbeResult Component(
        ParitySampleResult parity,
        string component,
        double fflogs,
        double dact)
        => new(
            parity.Report,
            parity.FightId,
            parity.DancePartnerJob,
            component,
            fflogs,
            dact,
            dact - fflogs,
            "available");

    private static IReadOnlyList<DevilmentWindowProbeResult> BuildWindows(
        NormalizedFight fight,
        DevilmentProbeSampleSelection selection,
        FightAttributionTimeline timeline,
        IReadOnlyList<DevilmentActionProbeResult> actions)
        => timeline.DevilmentWindows
            .Select(window =>
            {
                var values = actions.Where(action =>
                    action.OwnerActorId == window.TargetId &&
                    action.DevilmentWindow == window.Window).ToArray();
                return new DevilmentWindowProbeResult(
                    selection.Report,
                    selection.FightId,
                    selection.PartnerJob,
                    window.Window ?? 0,
                    window.Start,
                    window.End,
                    fight.Actors.GetValueOrDefault(window.TargetId)?.Name ?? $"Actor {window.TargetId}",
                    values.Length,
                    values.Sum(static action => action.RawDamage),
                    null,
                    UnavailablePerAction,
                    values.Sum(static action => action.DactDevilmentContribution),
                    null,
                    values.Sum(static action => action.ScenarioContributionCorrection));
            })
            .ToArray();

    private static IReadOnlyList<DevilmentSensitivityProbeResult> BuildSensitivity(
        ParitySampleResult parity,
        DevilmentProbeSampleSelection selection,
        IReadOnlyList<ActionProbeContext> contexts)
    {
        var result = new List<DevilmentSensitivityProbeResult>();
        foreach (var input in new[] { "Cu", "Du" })
        {
            foreach (var shift in new[] { -0.01, -0.005, 0.005, 0.01 })
            {
                var change = 0d;
                foreach (var context in contexts)
                {
                    var shiftedCu = input == "Cu"
                        ? Math.Clamp(context.Cu + shift, 0.05, 0.50)
                        : context.Cu;
                    var shiftedDu = input == "Du"
                        ? Math.Clamp(context.Du + shift, 0.05, 0.50)
                        : context.Du;
                    var baseline = DevilmentContributionMath.Calculate(
                        context.Event,
                        context.State,
                        context.Cu,
                        context.Du,
                        context.CurrentDimensions);
                    var shifted = DevilmentContributionMath.Calculate(
                        context.Event,
                        context.State,
                        shiftedCu,
                        shiftedDu,
                        context.CurrentDimensions);
                    change += shifted.Critical + shifted.Direct - baseline.Critical - baseline.Direct;
                }
                result.Add(new DevilmentSensitivityProbeResult(
                    selection.Report,
                    selection.FightId,
                    selection.PartnerJob,
                    input,
                    shift * 100,
                    parity.DevilmentContribution,
                    parity.DevilmentContribution + change,
                    change,
                    parity.DeltaRdps,
                    parity.DeltaRdps + change / parity.Duration));
            }
        }
        return result;
    }

    private static List<DevilmentActionProbeResult> PromoteUnknownCoverageCandidates(
        IReadOnlyList<DevilmentActionProbeResult> actions)
    {
        var candidates = actions
            .Where(static action => action.GuaranteeCategory == "A")
            .GroupBy(static action => (action.SamplePartnerJob, action.PartnerJob, action.ActionId))
            .Where(group => group.Count() >= 8 &&
                            (group.All(static action => action.IsCrit) ||
                             group.All(static action => action.IsDirectHit)))
            .Select(static group => group.Key)
            .ToHashSet();
        return actions.Select(action => candidates.Contains(
                    (action.SamplePartnerJob, action.PartnerJob, action.ActionId))
                ? action with
                {
                    GuaranteedCrit = null,
                    GuaranteedDirectHit = null,
                    GuaranteedCritDh = null,
                    GuaranteeCategory = "F",
                    GuaranteeSource = "unknown_metadata: observed all hits in selected Devilment windows",
                }
                : action)
            .ToList();
    }

    private static IReadOnlyList<DevilmentCategoryProbeResult> BuildCategories(
        IReadOnlyList<DevilmentActionProbeResult> actions)
    {
        var groups = actions
            .GroupBy(static action => (action.SamplePartnerJob, action.GuaranteeCategory))
            .ToDictionary(static group => group.Key, static group => group.ToArray());
        var result = new List<DevilmentCategoryProbeResult>();
        foreach (var job in actions.Select(static action => action.SamplePartnerJob).Distinct().Order())
        {
            foreach (var category in new[] { "A", "B", "C", "D", "E", "F" })
            {
                var values = groups.GetValueOrDefault((job, category)) ?? [];
                var rawDamage = values.Sum(static action => action.RawDamage);
                var correction = values.Sum(static action => action.ScenarioContributionCorrection);
                result.Add(new DevilmentCategoryProbeResult(
                    job,
                    category,
                    values.Length,
                    rawDamage,
                    null,
                    UnavailablePerAction,
                    values.Sum(static action => action.DactDevilmentContribution),
                    null,
                    null,
                    null,
                    correction,
                    values.Length > 0 ? correction / values.Length : 0,
                    rawDamage > 0 ? correction / rawDamage * 100_000 : 0));
            }
        }
        return result;
    }

    private static IReadOnlyList<DevilmentActionCoverageResult> BuildCoverage(
        IReadOnlyList<DevilmentActionProbeResult> actions,
        IReadOnlyDictionary<long, ProbeGuaranteedDimensions> stableGuarantees)
        => actions
            .GroupBy(static action => (action.PartnerJob, action.ActionId, action.ActionName))
            .Select(group =>
            {
                var stable = stableGuarantees.GetValueOrDefault(group.Key.ActionId);
                var contextual = group.Any(static action => action.GuaranteeCategory == "E");
                var unknown = group.Any(static action => action.GuaranteeCategory == "F");
                var evidenceEvents = contextual
                    ? group.Where(static action => action.GuaranteeCategory == "E").ToArray()
                    : group.ToArray();
                var expected = stable != ProbeGuaranteedDimensions.None
                    ? $"intrinsic {DescribeDimensions(stable)}"
                    : contextual
                        ? "guaranteed Critical only while Life Surge is consumed"
                        : unknown
                            ? "unknown; selected-window observations are deterministic but metadata proof is absent"
                            : "ordinary random Crit/DH";
                var covered = stable != ProbeGuaranteedDimensions.None
                    ? "YES"
                    : contextual ? "NO" : unknown ? "UNKNOWN" : "YES";
                var mismatch = contextual ? "YES" : unknown ? "UNKNOWN" : "NO";
                var evidence = stable != ProbeGuaranteedDimensions.None
                    ? "production metadata + official FFXIV job action description"
                    : contextual
                        ? "official Life Surge description + cached apply/remove/action state"
                        : unknown
                            ? "selected cached observations only"
                            : "selected cached observations; no guarantee marker";
                return new DevilmentActionCoverageResult(
                    group.Key.ActionId,
                    group.Key.ActionName,
                    group.Key.PartnerJob,
                    expected,
                    stable != ProbeGuaranteedDimensions.None
                        ? DescribeDimensions(stable)
                        : "none",
                    $"{evidenceEvents.Count(static action => action.IsCrit)}/{evidenceEvents.Length} Crit; " +
                    $"{evidenceEvents.Count(static action => action.IsDirectHit)}/{evidenceEvents.Length} DH",
                    evidenceEvents.Length,
                    evidenceEvents.Count(static action => action.IsCrit),
                    evidenceEvents.Count(static action => action.IsDirectHit),
                    evidenceEvents.Sum(static action => action.RawDamage),
                    covered,
                    mismatch,
                    evidence);
            })
            .OrderByDescending(static result => result.RawDamage)
            .ToArray();

    private static IReadOnlyList<DevilmentTopActionProbeResult> BuildTopActions(
        IReadOnlyList<DevilmentActionProbeResult> actions)
        => actions
            .GroupBy(static action =>
                (action.PartnerJob, action.ActionId, action.ActionName, action.GuaranteeCategory))
            .Select(group => new DevilmentTopActionProbeResult(
                group.Key.ActionId,
                group.Key.ActionName,
                group.Key.PartnerJob,
                group.Key.GuaranteeCategory,
                group.Count(),
                group.Sum(static action => action.RawDamage),
                group.Sum(static action => action.DactDevilmentContribution),
                group.Sum(static action => action.ScenarioContributionCorrection),
                "Not a FFLogs per-action delta; ranked by |diagnostic scenario correction| because the API total cannot be back-solved"))
            .OrderByDescending(static action => Math.Abs(action.ScenarioContributionCorrection))
            .ThenByDescending(static action => action.RawDamage)
            .Take(20)
            .ToArray();

    private static string ResolveCategory(ProbeGuaranteedDimensions dimensions, bool contextual)
    {
        if (contextual)
        {
            return "E";
        }
        return dimensions switch
        {
            ProbeGuaranteedDimensions.Critical => "B",
            ProbeGuaranteedDimensions.DirectHit => "C",
            ProbeGuaranteedDimensions.Critical | ProbeGuaranteedDimensions.DirectHit => "D",
            _ => "A",
        };
    }

    private static string DescribeDimensions(ProbeGuaranteedDimensions dimensions)
        => dimensions switch
        {
            ProbeGuaranteedDimensions.Critical => "guaranteed Critical",
            ProbeGuaranteedDimensions.DirectHit => "guaranteed Direct Hit",
            ProbeGuaranteedDimensions.Critical | ProbeGuaranteedDimensions.DirectHit =>
                "guaranteed Critical + Direct Hit",
            _ => "none",
        };

    private static string ToJobAbbreviation(string job)
        => job switch
        {
            "Samurai" => "SAM",
            "Dragoon" => "DRG",
            _ => job,
        };

    private sealed record ActionProbeContext(
        DevilmentActionProbeResult Record,
        NormalizedFflogsEvent Event,
        ProbeAttributionState State,
        ProbeGuaranteedDimensions CurrentDimensions,
        double Cu,
        double Du);

    private sealed record SelectedFightReplay(
        DevilmentSampleProbeResult Sample,
        IReadOnlyList<DevilmentActionProbeResult> Actions,
        IReadOnlyList<DevilmentWindowProbeResult> Windows,
        IReadOnlyList<DevilmentSensitivityProbeResult> Sensitivity);
}

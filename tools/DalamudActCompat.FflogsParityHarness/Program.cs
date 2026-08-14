using System.Text.Json;

namespace DalamudActCompat.FflogsParityHarness;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var options = HarnessOptions.Parse(args);
            if (options.ShowHelp)
            {
                Console.WriteLine(HarnessOptions.Usage);
                return 0;
            }
            if (options.SelfTest)
            {
                RunSelfTest();
                Console.WriteLine("Harness self-test passed.");
                return 0;
            }

            using var cancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };

            await using FflogsApiClient? api = options.ReplayOnly
                ? null
                : new FflogsApiClient(options.LoadCredentials(), options.RefreshCache);
            var collector = new FflogsSampleCollector(
                api,
                options.CacheDirectory,
                options.SampleCount);
            if (options.MatchedOwnershipDhAudit)
            {
                var cacheParent = Directory.GetParent(options.CacheDirectory)?.FullName ??
                                  options.CacheDirectory;
                var dhAuditReport = DhOwnershipExhaustionAudit.Run(
                    Path.Combine(cacheParent, "matched-ownership-cache"),
                    options.CacheDirectory);
                var dhAuditPaths = await DhOwnershipExhaustionAudit.WriteAsync(
                    options.OutputDirectory,
                    dhAuditReport,
                    cancellation.Token);
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    dhAuditReport.OwnershipStatus,
                    dhAuditReport.ExistingApiResponses,
                    dhAuditReport.UniqueMetadataResponses,
                    dhAuditReport.ReconstructedPreflightRows,
                    dhAuditReport.StrictDhOnlyCandidateFights,
                    dhAuditReport.ValidMatchedPairs,
                    dhAuditReport.NewNetworkRequests,
                    dhAuditReport.FullEventsDownloaded,
                    dhAuditReport.ActorFlipStatus,
                    dhAuditPaths.JsonPath,
                    dhAuditPaths.MarkdownPath,
                }, new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }
            if (options.MatchedOwnershipMining || options.MatchedOwnershipReplay)
            {
                var cacheParent = Directory.GetParent(options.CacheDirectory)?.FullName ??
                                  options.CacheDirectory;
                var matchedCollector = new FflogsMatchedOwnershipCollector(
                    api,
                    Path.Combine(cacheParent, "matched-ownership-cache"),
                    options.CacheDirectory,
                    Path.Combine(cacheParent, "matrix-cache"));
                var matchedManifest = options.MatchedOwnershipMining
                    ? await matchedCollector.CollectAsync(cancellation.Token)
                    : matchedCollector.ReadManifest();
                var matchedReport = MatchedOwnershipExperiment.Run(matchedManifest);
                var matchedPaths = await MatchedOwnershipReportWriter.WriteAsync(
                    options.OutputDirectory,
                    matchedReport,
                    cancellation.Token);
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    matchedReport.OwnershipStatus,
                    matchedReport.Mining.ApiCandidatesScanned,
                    MatchedActors = matchedReport.Actors.Count,
                    Fights = matchedReport.Fights.Count,
                    GradeA = matchedReport.GradeAGroupCount,
                    GradeB = matchedReport.GradeBGroupCount,
                    matchedReport.HasEnoughDhOnlyEvidence,
                    matchedReport.HasEnoughCriticalDirectEvidence,
                    matchedReport.SharedBaseLogVsShapley3Identifiable,
                    matchedPaths.JsonPath,
                    matchedPaths.PairCsvPath,
                    matchedPaths.MarkdownPath,
                }, new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }
            if (options.PercentageOrderingOnly)
            {
                var existingManifest = collector.ReadManifest();
                var matrixCache = Path.Combine(
                    Directory.GetParent(options.CacheDirectory)?.FullName ?? options.CacheDirectory,
                    "matrix-cache");
                var matrixCollector = new FflogsTargetedMatrixCollector(api, matrixCache);
                var targetedSamples = matrixCollector.ReadSamples(matrixCollector.ReadManifestOrNull());
                var orderingReport = PercentageOrderingExperiment.Run(
                    collector,
                    existingManifest,
                    targetedSamples);
                var orderingPaths = await PercentageOrderingReportWriter.WriteAsync(
                    options.OutputDirectory,
                    orderingReport,
                    cancellation.Token);
                var identificationReport = PercentageIdentificationExperiment.Run(
                    collector,
                    existingManifest,
                    targetedSamples);
                var identificationPaths = await PercentageIdentificationReportWriter.WriteAsync(
                    options.OutputDirectory,
                    identificationReport,
                    cancellation.Token);
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    orderingReport.FightCount,
                    orderingReport.ConstraintCount,
                    orderingReport.RateOverlapConstraintCount,
                    orderingReport.RateOverlapEventCount,
                    orderingReport.ProductionPercentageCalibration,
                    identificationReport.CleanDirectNormalConstraintCount,
                    orderingPaths.JsonPath,
                    orderingPaths.MarkdownPath,
                    IdentificationJsonPath = identificationPaths.JsonPath,
                    IdentificationMarkdownPath = identificationPaths.MarkdownPath,
                }, new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }
            if (options.PercentageAudit)
            {
                var existingManifest = collector.ReadManifest();
                var matrixCache = Path.Combine(
                    Directory.GetParent(options.CacheDirectory)?.FullName ?? options.CacheDirectory,
                    "matrix-cache");
                var matrixCollector = new FflogsTargetedMatrixCollector(api, matrixCache);
                var targetedManifest = matrixCollector.ReadManifestOrNull();
                var targetedSamples = matrixCollector.ReadSamples(targetedManifest);
                var percentageReport = PercentageAttributionAudit.Run(
                    collector,
                    existingManifest,
                    targetedSamples);
                var percentagePaths = await PercentageAuditReportWriter.WriteAsync(
                    options.OutputDirectory,
                    percentageReport,
                    cancellation.Token);
                var orderingReport = PercentageOrderingExperiment.Run(
                    collector,
                    existingManifest,
                    targetedSamples);
                var orderingPaths = await PercentageOrderingReportWriter.WriteAsync(
                    options.OutputDirectory,
                    orderingReport,
                    cancellation.Token);
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    percentageReport.FightCount,
                    percentageReport.ConstraintCount,
                    percentageReport.ReferenceExactCount,
                    percentageReport.ReferenceAuditCount,
                    percentageReport.CurrentProductionBeforeFix,
                    percentageReport.CurrentProductionAfterFix,
                    percentageReport.AuthoritativeMetadata,
                    PercentageJsonPath = percentagePaths.JsonPath,
                    PercentageMarkdownPath = percentagePaths.MarkdownPath,
                    orderingReport.RateOverlapConstraintCount,
                    orderingReport.RateOverlapEventCount,
                    orderingReport.ProductionPercentageCalibration,
                    OrderingJsonPath = orderingPaths.JsonPath,
                    OrderingMarkdownPath = orderingPaths.MarkdownPath,
                }, new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }
            if (options.AttributionMatrix)
            {
                var existingManifest = collector.ReadManifest();
                var matrixCache = Path.Combine(
                    Directory.GetParent(options.CacheDirectory)?.FullName ?? options.CacheDirectory,
                    "matrix-cache");
                var matrixCollector = new FflogsTargetedMatrixCollector(api, matrixCache);
                var targetedManifest = options.TargetedMatrixMining
                    ? await matrixCollector.CollectAsync(cancellation.Token)
                    : matrixCollector.ReadManifestOrNull();
                var targetedSamples = matrixCollector.ReadSamples(targetedManifest);
                var matrixReport = CrossProviderAttributionMatrixExperiment.Run(
                    collector,
                    existingManifest,
                    targetedSamples,
                    targetedManifest?.NewlyMinedFightCount ?? 0);
                var matrixPaths = await AttributionMatrixReportWriter.WriteAsync(
                    options.OutputDirectory,
                    matrixReport,
                    cancellation.Token);
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    matrixReport.EquationStatus,
                    matrixReport.ExistingCachedFightCount,
                    matrixReport.TargetedCachedFightCount,
                    matrixReport.NewlyMinedFightCount,
                    MatrixCellsCovered = matrixReport.Matrix.Count(static cell => cell.ConstraintCount > 0),
                    MatchedGroups = matrixReport.MatchedGroups.Count,
                    matrixPaths.JsonPath,
                    matrixPaths.MarkdownPath,
                }, new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }

            CacheManifest manifest;
            if (options.ReplayOnly)
            {
                manifest = collector.ReadManifest();
                Console.WriteLine($"Loaded {manifest.Seeds.Count} cached DNC samples.");
            }
            else
            {
                manifest = await collector.CollectAsync(cancellation.Token);
                Console.WriteLine(
                    $"Collection complete: {manifest.Seeds.Count} valid samples, {manifest.Failures.Count} skipped.");
            }

            if (options.CollectOnly)
            {
                Console.WriteLine($"Raw response manifest: {collector.ManifestPath}");
                return 0;
            }

            if (options.DevilmentProbe)
            {
                var probeReport = DevilmentPerActionProbe.Run(
                    collector,
                    manifest,
                    options.OutputDirectory);
                var probePaths = await DevilmentProbeReportWriter.WriteAsync(
                    options.OutputDirectory,
                    probeReport,
                    cancellation.Token);
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    probeReport.SelectedSampleCount,
                    probeReport.ActionCount,
                    probePaths.JsonPath,
                    probePaths.ActionsCsvPath,
                    probePaths.MarkdownPath,
                }, new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }

            if (options.GuaranteedHitExperiment)
            {
                var experiment = GuaranteedHitAttributionExperiment.Run(collector, manifest);
                var experimentPaths = await GuaranteedHitExperimentReportWriter.WriteAsync(
                    options.OutputDirectory,
                    experiment,
                    cancellation.Token);
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    experiment.EquationStatus,
                    experiment.BestCandidate,
                    experiment.SelectedSamFightCount,
                    experiment.CurrentProductionCalibration,
                    experimentPaths.JsonPath,
                    experimentPaths.MarkdownPath,
                }, new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }

            var results = new List<ParitySampleResult>(manifest.Seeds.Count);
            foreach (var seed in manifest.Seeds)
            {
                var normalized = FflogsEventNormalizer.Normalize(collector.ReadCachedSample(seed));
                results.Add(DactRdpsReplay.Replay(normalized));
                if (results.Count % 10 == 0 || results.Count == manifest.Seeds.Count)
                {
                    Console.WriteLine($"Replayed {results.Count}/{manifest.Seeds.Count} samples.");
                }
            }

            var report = ParityReportWriter.Build(results);
            var paths = await ParityReportWriter.WriteAsync(
                options.OutputDirectory,
                report,
                cancellation.Token);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                report.Overall,
                paths.JsonPath,
                paths.CsvPath,
                paths.MarkdownPath,
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Harness cancelled.");
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void RunSelfTest()
    {
        var statistics = ParityStatisticsCalculator.Calculate(
            [-2, 0.02, 12, -30],
            [-2, 0, 12, -30]);
        if (statistics.SampleCount != 4 ||
            statistics.ExactMatchCount != 1 ||
            statistics.Within1 != 1 ||
            statistics.Within10 != 2 ||
            statistics.Within50 != 4 ||
            Math.Abs(statistics.MedianDelta - -0.99) > 0.000_001)
        {
            throw new InvalidOperationException("Statistics thresholds or median calculation regressed.");
        }
        if (DactRdpsReplay.RoundForDisplay(10.05) != 10.1 ||
            DactRdpsReplay.RoundForDisplay(-10.05) != -10.1)
        {
            throw new InvalidOperationException("One-decimal parity rounding regressed.");
        }

        var calculated = new NormalizedFflogsEvent(
            0, 0, 1000, 1000, "calculateddamage", 1, 2, 100, 100, "Test", 123,
            0, 0, 0, 0, 0, true, false, false, null, null, 77, false, 1.1);
        var damage = calculated with
        {
            Sequence = 1,
            Timestamp = 1500,
            DamageTimestamp = 1500,
            Type = "damage",
        };
        var periodic = damage with
        {
            Sequence = 2,
            Timestamp = 2000,
            DamageTimestamp = 2000,
            IsPeriodic = true,
            PacketId = null,
        };
        var correlated = FflogsEventNormalizer.CorrelateCalculatedDamage(
            [calculated, damage, periodic]);
        if (correlated.Count != 2 ||
            correlated[0].Timestamp != calculated.Timestamp ||
            correlated[0].DamageTimestamp != damage.DamageTimestamp ||
            correlated[0].AttributionSequence != calculated.AttributionSequence ||
            !correlated[0].MatchedCalculatedDamage ||
            Math.Abs(correlated[0].Multiplier - 1.1) > 0.000_001 ||
            correlated[1].MatchedCalculatedDamage)
        {
            throw new InvalidOperationException("damage/calculateddamage packet correlation regressed.");
        }
        if (FflogsEventNormalizer.NormalizeAbilityId("applybuff", 1_001_822) != 0x71E ||
            FflogsEventNormalizer.NormalizeAbilityId("damage", 16_196) != 0x81C2)
        {
            throw new InvalidOperationException("FFLogs status/action identity mapping regressed.");
        }

        var guaranteed = ProductionGuaranteedMetadata.ReadStableActions();
        if (guaranteed.GetValueOrDefault(0x9066) != ProbeGuaranteedDimensions.Critical ||
            guaranteed.GetValueOrDefault(0x64C0) !=
            (ProbeGuaranteedDimensions.Critical | ProbeGuaranteedDimensions.DirectHit))
        {
            throw new InvalidOperationException("Production guaranteed-action metadata probe regressed.");
        }
        if (!ProductionGuaranteedMetadata.ReadReassembleWeaponskills().Contains(0x4072))
        {
            throw new InvalidOperationException("Production Reassemble weaponskill metadata probe regressed.");
        }
        if (!HarnessOptions.Parse(["--devilment-probe"]).ReplayOnly)
        {
            throw new InvalidOperationException("Devilment probe must remain cache-only.");
        }
        if (!HarnessOptions.Parse(["--guaranteed-hit-experiment"]).ReplayOnly)
        {
            throw new InvalidOperationException("Guaranteed-hit experiment must remain cache-only.");
        }
        if (!HarnessOptions.Parse(["--attribution-matrix"]).ReplayOnly)
        {
            throw new InvalidOperationException("Attribution matrix replay must remain cache-only.");
        }
        var orderingOptions = HarnessOptions.Parse(["--percentage-ordering-only"]);
        if (!orderingOptions.PercentageOrderingOnly || !orderingOptions.ReplayOnly)
        {
            throw new InvalidOperationException("Percentage ordering probe must remain cache-only.");
        }
        var ordering = PercentageOrderingExperiment.CalculateOrderingTotalsForTest(
            120_000,
            1.10,
            8_000);
        if (!(ordering.RateFirst < ordering.SharedLog &&
              ordering.SharedLog < ordering.PercentageFirst) ||
            Math.Abs(ordering.CurrentTotal - ordering.RateFirstTotal) > 0.000_001 ||
            Math.Abs(ordering.CurrentTotal - ordering.SharedShapleyTotal) > 0.000_001 ||
            Math.Abs(ordering.CurrentTotal - ordering.SharedLogTotal) > 0.000_001)
        {
            throw new InvalidOperationException("Percentage/rate ordering conservation regressed.");
        }
        var noPercentageOrdering = PercentageOrderingExperiment.CalculateOrderingTotalsForTest(
            120_000,
            1,
            8_000);
        if (Math.Abs(noPercentageOrdering.CurrentTotal - 8_000) > 0.000_001 ||
            Math.Abs(noPercentageOrdering.RateFirstTotal - 8_000) > 0.000_001 ||
            Math.Abs(noPercentageOrdering.SharedLogTotal - 8_000) > 0.000_001)
        {
            throw new InvalidOperationException("No-percentage rate conservation regressed.");
        }
        var decomposition = PercentageIdentificationExperiment.DecomposeForTest(
            120_000,
            1.10,
            6_000,
            2_000);
        var reconstructed = decomposition.BaseDamage + decomposition.PercentageMain +
                            decomposition.CriticalMain + decomposition.DirectMain +
                            decomposition.PercentageCritical + decomposition.PercentageDirect +
                            decomposition.CriticalDirect + decomposition.PercentageCriticalDirect;
        if (Math.Abs(reconstructed - 120_000) > 0.000_001 ||
            Math.Abs(decomposition.PercentageFirst - ordering.PercentageFirst) > 0.000_001 ||
            !(decomposition.RateFirst < decomposition.SharedShapley3 &&
              decomposition.SharedShapley3 < decomposition.PercentageFirst))
        {
            throw new InvalidOperationException("Three-dimension interaction decomposition regressed.");
        }
        var statusEstimator = new DalamudActCompat.ActRuntime.RaidDpsEstimator();
        var statusStart = DateTimeOffset.Parse("2026-08-14T00:00:00Z");
        statusEstimator.StartEncounter(statusStart);
        const string apply =
            "26|0|A8F|Searing Light|20|10000001|Provider|10000002|Recipient|";
        const string remove =
            "30|0|A8F|Searing Light|0|10000001|Provider|10000002|Recipient|";
        statusEstimator.ObserveStatusLine(statusStart, apply);
        statusEstimator.ObserveDamage(
            statusStart.AddSeconds(20.5), "Recipient", "Target", 10_000, false, false);
        var insideGrace = statusEstimator.ResolveReceivedDamage(
            "Recipient", DalamudActCompat.ActRuntime.RaidDpsEstimator.AttributionKind.Percentage);
        statusEstimator.ObserveStatusLine(statusStart.AddSeconds(20.75), remove);
        statusEstimator.ObserveDamage(
            statusStart.AddSeconds(21), "Recipient", "Target", 10_000, false, false);
        var afterRemove = statusEstimator.ResolveReceivedDamage(
            "Recipient", DalamudActCompat.ActRuntime.RaidDpsEstimator.AttributionKind.Percentage);
        statusEstimator.ObserveStatusLine(statusStart.AddSeconds(40), apply);
        statusEstimator.ObserveDamage(
            statusStart.AddSeconds(62.001), "Recipient", "Target", 10_000, false, false);
        var afterFallback = statusEstimator.ResolveReceivedDamage(
            "Recipient", DalamudActCompat.ActRuntime.RaidDpsEstimator.AttributionKind.Percentage);
        if (insideGrace <= 0 || Math.Abs(afterRemove - insideGrace) > 0.000_001 ||
            Math.Abs(afterFallback - insideGrace) > 0.000_001)
        {
            throw new InvalidOperationException("Bounded status remove fallback regressed.");
        }
        var targetedOptions = HarnessOptions.Parse(["--mine-targeted-matrix"]);
        if (!targetedOptions.AttributionMatrix ||
            !targetedOptions.TargetedMatrixMining ||
            targetedOptions.ReplayOnly)
        {
            throw new InvalidOperationException("Targeted matrix mining option semantics regressed.");
        }
        var matchedMiningOptions = HarnessOptions.Parse(["--matched-ownership-mining"]);
        var matchedReplayOptions = HarnessOptions.Parse(["--matched-ownership-replay"]);
        var matchedDhOptions = HarnessOptions.Parse(["--matched-ownership-dh-audit"]);
        if (!matchedMiningOptions.MatchedOwnershipMining || matchedMiningOptions.ReplayOnly ||
            !matchedReplayOptions.MatchedOwnershipReplay || !matchedReplayOptions.ReplayOnly ||
            !matchedDhOptions.MatchedOwnershipDhAudit || !matchedDhOptions.ReplayOnly)
        {
            throw new InvalidOperationException("Matched ownership option semantics regressed.");
        }
        if (OffensiveBuffRegistry.All.Any(static item => item.ProviderJob == "SAM") ||
            !OffensiveBuffRegistry.All.Any(static item =>
                item.ProviderJob == "BRD" &&
                item.Dimension == OffensiveBuffDimension.DirectHitRate) ||
            GuaranteedHitRegistry.HasGuaranteedDirectOnly ||
            !GuaranteedHitRegistry.All.Any(static item =>
                item.Job == "PCT" &&
                item.Dimensions == (ProbeGuaranteedDimensions.Critical |
                                    ProbeGuaranteedDimensions.DirectHit)))
        {
            throw new InvalidOperationException("Authoritative provider/recipient registry semantics regressed.");
        }

        var experimentEvent = damage with { Amount = 100_000, Critical = true, DirectHit = false };
        var experimentState = new ProbeAttributionState(
            1,
            0.30,
            0.20,
            0.20,
            0.20,
            true,
            1,
            "hit_time",
            [0x721, 0x312]);
        var productionProbe = DevilmentContributionMath.Calculate(
            experimentEvent,
            experimentState,
            0.25,
            0.25,
            ProbeGuaranteedDimensions.Critical);
        var offlineCandidate = GuaranteedHitCandidateMath.Calculate(
            GuaranteedHitCandidateMath.CurrentProduction,
            new GuaranteedHitCandidateInput(
                100_000,
                true,
                false,
                0.25,
                0.25,
                0.30,
                0.20,
                0.20,
                0.20,
                ProbeGuaranteedDimensions.Critical));
        if (Math.Abs(productionProbe.Critical + productionProbe.Direct -
                     offlineCandidate.Critical - offlineCandidate.Direct) > 0.000_001)
        {
            throw new InvalidOperationException("Guaranteed-hit CurrentProduction candidate calibration regressed.");
        }
        foreach (var definition in GuaranteedHitCandidateMath.Definitions)
        {
            var value = GuaranteedHitCandidateMath.Calculate(
                definition.Name,
                new GuaranteedHitCandidateInput(
                    100_000,
                    true,
                    true,
                    0.25,
                    0.25,
                    0.30,
                    0.40,
                    0.20,
                    0.20,
                    ProbeGuaranteedDimensions.Critical | ProbeGuaranteedDimensions.DirectHit));
            if (!double.IsFinite(value.Critical) || value.Critical < 0 ||
                !double.IsFinite(value.Direct) || value.Direct < 0)
            {
                throw new InvalidOperationException($"Guaranteed-hit candidate '{definition.Name}' is invalid.");
            }
        }

        var selfRateInput = new GuaranteedHitCandidateInput(
            100_000,
            true,
            false,
            0.25,
            0.25,
            0.30,
            0.20,
            0.20,
            0.20,
            ProbeGuaranteedDimensions.Critical,
            SelfCriticalRateIncrease: 0.10);
        var observedExternal = GuaranteedHitCandidateMath.Calculate(
            GuaranteedHitCandidateMath.ObservedExternalProvidersOnly,
            selfRateInput);
        var observedSelfScaling = GuaranteedHitCandidateMath.Calculate(
            GuaranteedHitCandidateMath.ObservedSelfScalingExternalDenominator,
            selfRateInput);
        var unscaledExcluded = GuaranteedHitCandidateMath.Calculate(
            GuaranteedHitCandidateMath.UnscaledExcludeSelfEverywhere,
            selfRateInput);
        var unscaledExternal = GuaranteedHitCandidateMath.Calculate(
            GuaranteedHitCandidateMath.UnscaledExternalProvidersOnly,
            selfRateInput);
        if (observedExternal != observedSelfScaling || unscaledExcluded != unscaledExternal)
        {
            // These duplicate policies are intentionally emitted so the report proves
            // where the declared denominator axes collapse mathematically.
            throw new InvalidOperationException("Guaranteed-hit denominator equivalence regressed.");
        }
        const double externalCriticalGameRatio = (1.60 + 0.30 * 0.60) / 1.60;
        const double allCriticalGameRatio = (1.60 + 0.40 * 0.60) / 1.60;
        var expectedWithoutSelf = GuaranteedHitCandidateMath.Calculate(
            GuaranteedHitCandidateMath.ObservedExternalProvidersOnly,
            selfRateInput with
            {
                DamageAfterPercentageRemoval =
                    selfRateInput.DamageAfterPercentageRemoval *
                    externalCriticalGameRatio / allCriticalGameRatio,
            });
        var observedWithoutSelf = GuaranteedHitCandidateMath.Calculate(
            GuaranteedHitCandidateMath.ObservedExcludeSelfEverywhere,
            selfRateInput);
        if (Math.Abs(observedWithoutSelf.Critical - expectedWithoutSelf.Critical) > 1e-9 ||
            Math.Abs(observedWithoutSelf.Direct - expectedWithoutSelf.Direct) > 1e-9)
        {
            throw new InvalidOperationException("Guaranteed-hit self-scaling removal regressed.");
        }
        var conditionalWithOverlap = GuaranteedHitCandidateMath.Calculate(
            GuaranteedHitCandidateMath.OtherExternalOverlapObservedElseUnscaled,
            selfRateInput);
        var observed = GuaranteedHitCandidateMath.Calculate(
            GuaranteedHitCandidateMath.ObservedHitRegular,
            selfRateInput);
        if (conditionalWithOverlap != observed)
        {
            throw new InvalidOperationException("Guaranteed-hit overlap boundary selection regressed.");
        }
    }
}

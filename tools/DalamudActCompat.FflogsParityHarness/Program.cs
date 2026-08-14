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
            0, 1000, 1000, "calculateddamage", 1, 2, 100, 100, "Test", 123,
            0, 0, 0, 0, 0, true, false, false, null, null, 77, false);
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
            !correlated[0].MatchedCalculatedDamage ||
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
    }
}

using System.Globalization;
using System.Text;
using System.Text.Json;
using DalamudActCompat.ActRuntime;

namespace DalamudActCompat.FflogsParityHarness;

internal static class ProductionCandidateEvaluation
{
    private static readonly HashSet<long> CriticalRateStatuses = [0x312, 0x4C5, 0x721];
    private static readonly HashSet<long> DirectRateStatuses = [0x08D, 0x721];

    public static ProductionCandidateReport Run(
        FflogsSampleCollector collector,
        CacheManifest manifest)
    {
        var samples = new List<ProductionCandidateSample>(manifest.Seeds.Count);
        foreach (var seed in manifest.Seeds)
        {
            var fight = FflogsEventNormalizer.Normalize(collector.ReadCachedSample(seed));
            var current = DactRdpsReplay.Replay(fight, RaidDpsOwnershipModel.PercentageFirst);
            var candidate = DactRdpsReplay.Replay(fight, RaidDpsOwnershipModel.SharedBaseLog);
            samples.Add(new ProductionCandidateSample(
                current.Report,
                current.FightId,
                current.Actor,
                current.Encounter,
                fight.Dancer.Job,
                current.DancePartnerJob,
                ResolveProviderDimension(fight),
                current.FflogsRdps,
                current.DactRdps,
                candidate.DactRdps,
                current.DeltaRdps,
                candidate.DeltaRdps,
                candidate.DactRdps - current.DactRdps,
                current.RawDamage == current.FflogsDamageTableAmount,
                current.MatchedCalculatedDamageCount,
                Math.Max(0, current.DamageEventCount - current.PeriodicDamageEventCount),
                current.NormalizationWarnings.Count));
        }

        var generatedAt = DateTimeOffset.UtcNow;
        return new ProductionCandidateReport(
            generatedAt,
            RaidDpsModelInfo.OwnershipModel,
            RaidDpsModelInfo.StatusEngine,
            Calculate(samples.Select(static item => item.CurrentDelta).ToArray()),
            Calculate(samples.Select(static item => item.CandidateDelta).ToArray()),
            BuildGroups(samples),
            samples
                .OrderByDescending(static item =>
                    Math.Abs(item.CandidateDelta) - Math.Abs(item.CurrentDelta))
                .Take(20)
                .ToArray(),
            samples);
    }

    public static async Task<ProductionCandidatePaths> WriteAsync(
        string outputDirectory,
        string? qualitySnapshotPath,
        ProductionCandidateReport report,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputDirectory);
        var jsonPath = Path.Combine(outputDirectory, "production-candidate-evaluation.json");
        var csvPath = Path.Combine(outputDirectory, "production-candidate-evaluation.csv");
        var markdownPath = Path.Combine(outputDirectory, "production-candidate-evaluation.md");
        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        };
        await File.WriteAllTextAsync(
            jsonPath,
            JsonSerializer.Serialize(report, jsonOptions),
            cancellationToken);
        await File.WriteAllTextAsync(csvPath, BuildCsv(report.Samples), cancellationToken);
        await File.WriteAllTextAsync(markdownPath, BuildMarkdown(report), cancellationToken);

        if (!string.IsNullOrWhiteSpace(qualitySnapshotPath))
        {
            var snapshotDirectory = Path.GetDirectoryName(qualitySnapshotPath);
            if (!string.IsNullOrWhiteSpace(snapshotDirectory))
            {
                Directory.CreateDirectory(snapshotDirectory);
            }

            var rawExactCount = report.Samples.Count(static item => item.RawDamageExact);
            var directMatched = report.Samples.Sum(static item => item.DirectPacketMatched);
            var directExpected = report.Samples.Sum(static item => item.DirectPacketExpected);
            var snapshot = new ProductionQualitySnapshot(
                report.GeneratedAt,
                report.GeneratedAt,
                report.ModelIdentifier,
                report.StatusEngine,
                report.Candidate.SampleCount,
                report.Candidate.MeanDelta,
                report.Candidate.MedianDelta,
                report.Candidate.MeanAbsoluteDelta,
                report.Candidate.P90AbsoluteDelta,
                report.Candidate.P95AbsoluteDelta,
                report.Candidate.MaxAbsoluteDelta,
                rawExactCount,
                report.Samples.Count,
                directMatched,
                directExpected,
                report.Samples.Sum(static item => item.NormalizationWarningCount),
                "artifacts/fflogs-parity-harness/reports/production-candidate-evaluation.md");
            await File.WriteAllTextAsync(
                qualitySnapshotPath,
                JsonSerializer.Serialize(snapshot, jsonOptions),
                cancellationToken);
        }

        return new ProductionCandidatePaths(jsonPath, csvPath, markdownPath, qualitySnapshotPath);
    }

    private static string ResolveProviderDimension(NormalizedFight fight)
    {
        var appliedStatuses = fight.Events
            .Where(static item => FflogsEventNormalizer.IsStatusApply(item.Type))
            .Select(static item => item.AbilityId)
            .ToArray();
        var hasCritical = appliedStatuses.Any(CriticalRateStatuses.Contains);
        var hasDirect = appliedStatuses.Any(DirectRateStatuses.Contains);
        return (hasCritical, hasDirect) switch
        {
            (true, true) => "Crit+DH",
            (true, false) => "Crit-only",
            (false, true) => "DH-only",
            _ => "No rate",
        };
    }

    private static IReadOnlyList<ProductionCandidateGroup> BuildGroups(
        IReadOnlyList<ProductionCandidateSample> samples)
    {
        var groups = new List<ProductionCandidateGroup>();
        AddGroups(groups, samples, "Job", static item => item.Job);
        AddGroups(groups, samples, "Actor", static item => item.Actor);
        AddGroups(groups, samples, "Encounter", static item => item.Encounter);
        AddGroups(groups, samples, "Recipient job", static item => item.RecipientJob);
        AddGroups(groups, samples, "Provider dimension", static item => item.ProviderDimension);
        return groups;
    }

    private static void AddGroups(
        ICollection<ProductionCandidateGroup> destination,
        IReadOnlyList<ProductionCandidateSample> samples,
        string dimension,
        Func<ProductionCandidateSample, string> selector)
    {
        foreach (var group in samples.GroupBy(selector, StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(static group => group.Count())
                     .ThenBy(static group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            destination.Add(new ProductionCandidateGroup(
                dimension,
                string.IsNullOrWhiteSpace(group.Key) ? "Unknown" : group.Key,
                Calculate(group.Select(static item => item.CurrentDelta).ToArray()),
                Calculate(group.Select(static item => item.CandidateDelta).ToArray())));
        }
    }

    private static ProductionCandidateStatistics Calculate(IReadOnlyList<double> deltas)
    {
        if (deltas.Count == 0)
        {
            return new ProductionCandidateStatistics(0, 0, 0, 0, 0, 0, 0);
        }

        var ordered = deltas.Order().ToArray();
        var absolute = deltas.Select(Math.Abs).Order().ToArray();
        return new ProductionCandidateStatistics(
            deltas.Count,
            deltas.Average(),
            Percentile(ordered, 0.50),
            absolute.Average(),
            Percentile(absolute, 0.90),
            Percentile(absolute, 0.95),
            absolute[^1]);
    }

    private static double Percentile(IReadOnlyList<double> ordered, double percentile)
    {
        var position = (ordered.Count - 1) * percentile;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        return lower == upper
            ? ordered[lower]
            : ordered[lower] + ((ordered[upper] - ordered[lower]) * (position - lower));
    }

    private static string BuildCsv(IEnumerable<ProductionCandidateSample> samples)
    {
        var builder = new StringBuilder();
        builder.AppendLine(
            "report,fightId,actor,encounter,job,recipientJob,providerDimension,fflogsRdps," +
            "currentRdps,candidateRdps,currentDelta,candidateDelta,candidateMinusCurrent");
        foreach (var item in samples)
        {
            builder.AppendLine(string.Join(',', new object[]
            {
                item.Report, item.FightId, item.Actor, item.Encounter, item.Job,
                item.RecipientJob, item.ProviderDimension, item.FflogsRdps,
                item.CurrentRdps, item.CandidateRdps, item.CurrentDelta,
                item.CandidateDelta, item.CandidateMinusCurrent,
            }.Select(Csv)));
        }
        return builder.ToString();
    }

    private static string BuildMarkdown(ProductionCandidateReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Production rDPS candidate evaluation");
        builder.AppendLine();
        builder.AppendLine($"Generated: {report.GeneratedAt:O}");
        builder.AppendLine();
        builder.AppendLine(
            "SharedBaseLog is a DACT estimator model selected from the retained diagnostics; " +
            "this report does not identify it as an official FFLogs equation.");
        builder.AppendLine();
        builder.AppendLine("| Model | N | Mean Δ | Median Δ | MAE | P90 |Δ| | P95 |Δ| | Max |Δ| |");
        builder.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|");
        AppendStatistics(builder, "Current PercentageFirst", report.Current);
        AppendStatistics(builder, report.ModelIdentifier, report.Candidate);
        builder.AppendLine();
        builder.AppendLine("## Group regression check");
        builder.AppendLine();
        builder.AppendLine("| Dimension | Value | N | Current MAE | Candidate MAE | Δ MAE | Current max | Candidate max |");
        builder.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|");
        foreach (var group in report.Groups)
        {
            builder.AppendLine(
                $"| {Escape(group.Dimension)} | {Escape(group.Value)} | {group.Current.SampleCount} | " +
                $"{group.Current.MeanAbsoluteDelta:F3} | {group.Candidate.MeanAbsoluteDelta:F3} | " +
                $"{group.Candidate.MeanAbsoluteDelta - group.Current.MeanAbsoluteDelta:F3} | " +
                $"{group.Current.MaxAbsoluteDelta:F3} | {group.Candidate.MaxAbsoluteDelta:F3} |");
        }
        builder.AppendLine();
        builder.AppendLine("## Largest per-fight regressions");
        builder.AppendLine();
        builder.AppendLine("| Report | Fight | Actor | Encounter | Dimension | Current Δ | Candidate Δ | Candidate - current rDPS |");
        builder.AppendLine("|---|---:|---|---|---|---:|---:|---:|");
        foreach (var item in report.LargestRegressions)
        {
            builder.AppendLine(
                $"| {item.Report} | {item.FightId} | {Escape(item.Actor)} | {Escape(item.Encounter)} | " +
                $"{item.ProviderDimension} | {item.CurrentDelta:F3} | {item.CandidateDelta:F3} | " +
                $"{item.CandidateMinusCurrent:F3} |");
        }
        return builder.ToString();
    }

    private static void AppendStatistics(
        StringBuilder builder,
        string model,
        ProductionCandidateStatistics statistics)
        => builder.AppendLine(
            $"| {model} | {statistics.SampleCount} | {statistics.MeanDelta:F3} | " +
            $"{statistics.MedianDelta:F3} | {statistics.MeanAbsoluteDelta:F3} | " +
            $"{statistics.P90AbsoluteDelta:F3} | {statistics.P95AbsoluteDelta:F3} | " +
            $"{statistics.MaxAbsoluteDelta:F3} |");

    private static string Csv(object value)
    {
        var text = value is IFormattable formattable
            ? formattable.ToString(null, CultureInfo.InvariantCulture)
            : value.ToString() ?? string.Empty;
        return text.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? $"\"{text.Replace("\"", "\"\"")}\""
            : text;
    }

    private static string Escape(string value) => value.Replace("|", "\\|");
}

internal sealed record ProductionCandidateReport(
    DateTimeOffset GeneratedAt,
    string ModelIdentifier,
    string StatusEngine,
    ProductionCandidateStatistics Current,
    ProductionCandidateStatistics Candidate,
    IReadOnlyList<ProductionCandidateGroup> Groups,
    IReadOnlyList<ProductionCandidateSample> LargestRegressions,
    IReadOnlyList<ProductionCandidateSample> Samples);

internal sealed record ProductionCandidateStatistics(
    int SampleCount,
    double MeanDelta,
    double MedianDelta,
    double MeanAbsoluteDelta,
    double P90AbsoluteDelta,
    double P95AbsoluteDelta,
    double MaxAbsoluteDelta);

internal sealed record ProductionCandidateGroup(
    string Dimension,
    string Value,
    ProductionCandidateStatistics Current,
    ProductionCandidateStatistics Candidate);

internal sealed record ProductionCandidateSample(
    string Report,
    int FightId,
    string Actor,
    string Encounter,
    string Job,
    string RecipientJob,
    string ProviderDimension,
    double FflogsRdps,
    double CurrentRdps,
    double CandidateRdps,
    double CurrentDelta,
    double CandidateDelta,
    double CandidateMinusCurrent,
    bool RawDamageExact,
    int DirectPacketMatched,
    int DirectPacketExpected,
    int NormalizationWarningCount);

internal sealed record ProductionQualitySnapshot(
    DateTimeOffset GeneratedAt,
    DateTimeOffset LastRun,
    string ModelIdentifier,
    string StatusEngine,
    int Samples,
    double MeanDelta,
    double MedianDelta,
    double Mae,
    double P90AbsoluteDelta,
    double P95AbsoluteDelta,
    double MaxAbsoluteDelta,
    int RawParityExactCount,
    int RawParitySampleCount,
    int DirectPacketMatched,
    int DirectPacketExpected,
    int NormalizationWarnings,
    string LatestReport);

internal sealed record ProductionCandidatePaths(
    string JsonPath,
    string CsvPath,
    string MarkdownPath,
    string? QualitySnapshotPath);

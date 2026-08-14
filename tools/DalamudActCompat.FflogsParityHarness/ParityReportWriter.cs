using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DalamudActCompat.FflogsParityHarness;

internal static class ParityStatisticsCalculator
{
    public static ParityStatistics Calculate(IReadOnlyList<ParitySampleResult> samples)
        => Calculate(
            samples.Select(static sample => sample.DeltaRdps).ToArray(),
            samples.Select(static sample => sample.DisplayDeltaRdps).ToArray());

    internal static ParityStatistics Calculate(
        IReadOnlyList<double> rawDeltas,
        IReadOnlyList<double> displayDeltas)
    {
        if (rawDeltas.Count == 0 || rawDeltas.Count != displayDeltas.Count)
        {
            return new ParityStatistics(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        }

        var ordered = rawDeltas.Order().ToArray();
        var middle = ordered.Length / 2;
        var median = ordered.Length % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2
            : ordered[middle];
        return new ParityStatistics(
            rawDeltas.Count,
            displayDeltas.Count(static delta => Math.Abs(delta) < 0.000_001),
            displayDeltas.Count(static delta => Math.Abs(delta) <= 1),
            displayDeltas.Count(static delta => Math.Abs(delta) <= 10),
            displayDeltas.Count(static delta => Math.Abs(delta) <= 50),
            displayDeltas.Count(static delta => Math.Abs(delta) <= 100),
            displayDeltas.Count(static delta => Math.Abs(delta) <= 500),
            rawDeltas.Average(),
            median,
            rawDeltas.Average(static delta => Math.Abs(delta)),
            rawDeltas.Max(static delta => Math.Abs(delta)),
            rawDeltas.Min(),
            rawDeltas.Max());
    }
}

/// <summary>
/// Produces machine-readable and review-friendly outputs from the same immutable result set.
/// Threshold counts use one-decimal display deltas; aggregate moments retain full precision.
/// </summary>
internal static class ParityReportWriter
{
    public static ParityReport Build(IReadOnlyList<ParitySampleResult> samples)
    {
        var ordered = samples
            .OrderBy(static sample => sample.EncounterId)
            .ThenBy(static sample => sample.Report, StringComparer.Ordinal)
            .ThenBy(static sample => sample.FightId)
            .ToArray();
        return new ParityReport(
            DateTimeOffset.UtcNow,
            "Public CN Dancer ranking discovery, AAC Heavyweight, difficulty 101, partition 9; DamageDone table metrics are parity ground truth",
            "FFLogs and DACT rounded independently to one decimal; exact match means displayed Δ = 0.0",
            "Replay and FFLogs rates use DamageDone table totalTime; table damageDowntime, ReportFight.combatTime, and wall duration remain diagnostics.",
            ParityStatisticsCalculator.Calculate(ordered),
            BuildDeltaDistribution(ordered),
            BuildContributionDeltas(ordered),
            BuildGroups(ordered),
            ordered.OrderByDescending(static sample => Math.Abs(sample.DeltaRdps)).Take(20).ToArray(),
            ordered.OrderBy(static sample => Math.Abs(sample.DeltaRdps)).Take(20).ToArray(),
            ordered,
            [
                "The production RaidDpsEstimator receives only normalized events and duration; FFLogs final metrics are read after replay for comparison.",
                "characterRankings values are discovery metadata, not the parity reference. DamageDone totalRDPS/totalRDPSTaken/totalRDPSGiven are the reference totals.",
                "FFLogs DamageDone table totalTime is the shared rate denominator; targetability-derived duration parity remains a separate packet-state question.",
                "Production currently aggregates Dancer percentage contribution by source, so Technical and Standard individual contribution fields remain null while their combined value is reported.",
                "damage.amount is the effective-damage numerator; packet-matched calculateddamage supplies the earlier action timestamp used by DACT attribution.",
            ]);
    }

    public static async Task<ReportPaths> WriteAsync(
        string outputDirectory,
        ParityReport report,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputDirectory);
        var jsonPath = Path.Combine(outputDirectory, "parity-report.json");
        var csvPath = Path.Combine(outputDirectory, "parity-samples.csv");
        var markdownPath = Path.Combine(outputDirectory, "parity-summary.md");
        await File.WriteAllTextAsync(
            jsonPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true,
            }),
            cancellationToken);
        await File.WriteAllTextAsync(csvPath, BuildCsv(report.Samples), cancellationToken);
        await File.WriteAllTextAsync(markdownPath, BuildMarkdown(report), cancellationToken);
        return new ReportPaths(jsonPath, csvPath, markdownPath);
    }

    private static IReadOnlyList<ParityGroupStatistics> BuildGroups(
        IReadOnlyList<ParitySampleResult> samples)
    {
        var groups = new List<ParityGroupStatistics>();
        AddBooleanGroup(groups, samples, "Technical Finish", static sample => sample.TechnicalFinishPresent);
        AddBooleanGroup(groups, samples, "Standard Finish", static sample => sample.StandardFinishPresent);
        AddBooleanGroup(groups, samples, "Devilment", static sample => sample.DevilmentPresent);
        AddBooleanGroup(groups, samples, "Multiple raid-buff overlap", static sample => sample.MultiRaidBuffOverlap);
        AddStringGroup(groups, samples, "Dance Partner job", static sample => sample.DancePartnerJob);
        AddStringGroup(groups, samples, "Party composition", static sample => sample.PartyComposition);
        AddStringGroup(groups, samples, "Encounter", static sample => sample.Encounter);
        AddStringGroup(groups, samples, "Fight duration", static sample => sample.Duration switch
        {
            < 300 => "<300s",
            < 420 => "300-419s",
            < 540 => "420-539s",
            _ => ">=540s",
        });
        AddStringGroup(groups, samples, "Downtime", static sample => sample.Downtime switch
        {
            <= 0.1 => "none",
            < 5 => "0-4.9s",
            < 20 => "5-19.9s",
            _ => ">=20s",
        });
        AddStringGroup(groups, samples, "Death / resurrection", static sample =>
            (sample.DeathCount > 0, sample.ResurrectionCount > 0) switch
            {
                (false, false) => "none",
                (true, false) => "death only",
                (false, true) => "resurrection only",
                _ => "death and resurrection",
            });
        AddBooleanGroup(groups, samples, "Pet job present", static sample => sample.HasPetJob);
        AddStringGroup(groups, samples, "Pet jobs", static sample =>
            string.IsNullOrWhiteSpace(sample.PetJobs) ? "none" : sample.PetJobs);
        AddBooleanGroup(groups, samples, "DoT job present", static sample => sample.HasDotJob);
        AddStringGroup(groups, samples, "DoT jobs", static sample =>
            string.IsNullOrWhiteSpace(sample.DotJobs) ? "none" : sample.DotJobs);
        return groups;
    }

    private static IReadOnlyList<ParityContributionDeltaStatistics> BuildContributionDeltas(
        IReadOnlyList<ParitySampleResult> samples)
        =>
        [
            CalculateContributionDelta(
                "External buffs received (DACT - FFLogs)",
                samples.Select(static sample => sample.ExternalBuffContributionReceivedDelta)),
            CalculateContributionDelta(
                "Own buffs given (DACT - FFLogs)",
                samples.Select(static sample => sample.OwnBuffContributionGivenDelta)),
            CalculateContributionDelta(
                "Technical + Standard (DACT - FFLogs)",
                samples.Select(static sample => sample.TechnicalAndStandardContributionDelta)),
            CalculateContributionDelta(
                "Devilment (DACT - FFLogs)",
                samples.Select(static sample => sample.DevilmentContributionDelta)),
        ];

    private static ParityDeltaDistribution BuildDeltaDistribution(
        IReadOnlyList<ParitySampleResult> samples)
    {
        var ordered = samples.Select(static sample => sample.DeltaRdps).Order().ToArray();
        return new ParityDeltaDistribution(
            ordered.Count(static delta => delta < 0),
            ordered.Count(static delta => delta == 0),
            ordered.Count(static delta => delta > 0),
            Percentile(ordered, 0.10),
            Percentile(ordered, 0.25),
            Percentile(ordered, 0.50),
            Percentile(ordered, 0.75),
            Percentile(ordered, 0.90));
    }

    private static double Percentile(IReadOnlyList<double> ordered, double percentile)
    {
        if (ordered.Count == 0)
        {
            return 0;
        }

        var position = (ordered.Count - 1) * percentile;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        return lower == upper
            ? ordered[lower]
            : ordered[lower] + ((ordered[upper] - ordered[lower]) * (position - lower));
    }

    private static ParityContributionDeltaStatistics CalculateContributionDelta(
        string component,
        IEnumerable<double> values)
    {
        var ordered = values.Order().ToArray();
        if (ordered.Length == 0)
        {
            return new ParityContributionDeltaStatistics(component, 0, 0, 0, 0, 0, 0);
        }

        var middle = ordered.Length / 2;
        var median = ordered.Length % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2
            : ordered[middle];
        return new ParityContributionDeltaStatistics(
            component,
            ordered.Average(),
            median,
            ordered.Average(static value => Math.Abs(value)),
            ordered.Max(static value => Math.Abs(value)),
            ordered[0],
            ordered[^1]);
    }

    private static void AddBooleanGroup(
        ICollection<ParityGroupStatistics> destination,
        IReadOnlyList<ParitySampleResult> samples,
        string dimension,
        Func<ParitySampleResult, bool> selector)
        => AddStringGroup(destination, samples, dimension, sample => selector(sample) ? "present" : "absent");

    private static void AddStringGroup(
        ICollection<ParityGroupStatistics> destination,
        IReadOnlyList<ParitySampleResult> samples,
        string dimension,
        Func<ParitySampleResult, string> selector)
    {
        foreach (var group in samples.GroupBy(selector, StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(static group => group.Count())
                     .ThenBy(static group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            destination.Add(new ParityGroupStatistics(
                dimension,
                string.IsNullOrWhiteSpace(group.Key) ? "unknown" : group.Key,
                ParityStatisticsCalculator.Calculate(group.ToArray())));
        }
    }

    private static string BuildCsv(IReadOnlyList<ParitySampleResult> samples)
    {
        var builder = new StringBuilder();
        builder.AppendLine(
            "report,fightId,actorId,actor,encounter,encounterId,duration,partyComposition," +
            "fflogsDps,fflogsRdps,fflogsAdps,fflogsNdps,rankingPdps,rankingRdps,rankingAdps,rankingNdps," +
            "dactDps,dactRdps,deltaRdps," +
            "displayDeltaRdps,deltaPercent,rawDamage,fflogsDamageTableAmount,damageNormalizationDelta," +
            "externalBuffContributionReceived,ownBuffContributionGiven," +
            "fflogsExternalBuffContributionReceived,fflogsOwnBuffContributionGiven," +
            "fflogsGivenBreakdown,fflogsTakenBreakdown," +
            "externalBuffContributionReceivedDelta,ownBuffContributionGivenDelta,technicalFinishContribution," +
            "standardFinishContribution,technicalAndStandardContribution,devilmentContribution," +
            "fflogsTechnicalFinishContribution,fflogsStandardFinishContribution," +
            "fflogsTechnicalAndStandardContribution,fflogsDevilmentContribution," +
            "technicalAndStandardContributionDelta,devilmentContributionDelta," +
            "critContributionReceived,directHitContributionReceived,critDirectHitContributionReceived," +
            "critContributionGiven,directHitContributionGiven,critDirectHitContributionGiven," +
            "technicalFinishPresent,standardFinishPresent,devilmentPresent,multiRaidBuffOverlap," +
            "maximumRaidBuffOverlap,dancePartnerJob,wallDuration,downtime,deathCount,resurrectionCount," +
            "hasPetJob,petJobs,hasDotJob,dotJobs,damageEventCount,statusEventCount," +
            "matchedCalculatedDamageCount,unmatchedCalculatedDamageCount,unmatchedDirectDamageCount," +
            "periodicDamageEventCount,normalizationWarnings");
        foreach (var sample in samples)
        {
            var values = new object?[]
            {
                sample.Report, sample.FightId, sample.ActorId, sample.Actor, sample.Encounter,
                sample.EncounterId, sample.Duration, sample.PartyComposition, sample.FflogsDps,
                sample.FflogsRdps, sample.FflogsAdps, sample.FflogsNdps, sample.RankingPdps,
                sample.RankingRdps, sample.RankingAdps, sample.RankingNdps, sample.DactDps,
                sample.DactRdps, sample.DeltaRdps, sample.DisplayDeltaRdps, sample.DeltaPercent,
                sample.RawDamage, sample.FflogsDamageTableAmount, sample.DamageNormalizationDelta,
                sample.ExternalBuffContributionReceived, sample.OwnBuffContributionGiven,
                sample.FflogsExternalBuffContributionReceived, sample.FflogsOwnBuffContributionGiven,
                FormatContributions(sample.FflogsGivenBreakdown),
                FormatContributions(sample.FflogsTakenBreakdown),
                sample.ExternalBuffContributionReceivedDelta, sample.OwnBuffContributionGivenDelta,
                sample.TechnicalFinishContribution, sample.StandardFinishContribution,
                sample.TechnicalAndStandardContribution, sample.DevilmentContribution,
                sample.FflogsTechnicalFinishContribution, sample.FflogsStandardFinishContribution,
                sample.FflogsTechnicalAndStandardContribution, sample.FflogsDevilmentContribution,
                sample.TechnicalAndStandardContributionDelta, sample.DevilmentContributionDelta,
                sample.CritContributionReceived, sample.DirectHitContributionReceived,
                sample.CritDirectHitContributionReceived, sample.CritContributionGiven,
                sample.DirectHitContributionGiven, sample.CritDirectHitContributionGiven,
                sample.TechnicalFinishPresent, sample.StandardFinishPresent, sample.DevilmentPresent,
                sample.MultiRaidBuffOverlap, sample.MaximumRaidBuffOverlap, sample.DancePartnerJob,
                sample.WallDuration, sample.Downtime, sample.DeathCount, sample.ResurrectionCount,
                sample.HasPetJob, sample.PetJobs, sample.HasDotJob, sample.DotJobs,
                sample.DamageEventCount, sample.StatusEventCount,
                sample.MatchedCalculatedDamageCount, sample.UnmatchedCalculatedDamageCount,
                sample.UnmatchedDirectDamageCount, sample.PeriodicDamageEventCount,
                string.Join(" | ", sample.NormalizationWarnings),
            };
            builder.AppendLine(string.Join(',', values.Select(Csv)));
        }

        return builder.ToString();
    }

    private static string BuildMarkdown(ParityReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# FFLogs rDPS Parity Harness");
        builder.AppendLine();
        builder.AppendLine($"Generated: {report.GeneratedAt:O}");
        builder.AppendLine();
        builder.AppendLine($"Scope: {report.Scope}");
        builder.AppendLine();
        builder.AppendLine($"Duration boundary: {report.DurationBoundary}");
        builder.AppendLine();
        AppendStatistics(builder, "Overall", report.Overall);
        builder.AppendLine("## Delta distribution");
        builder.AppendLine();
        builder.AppendLine(
            $"- Negative / zero / positive: {report.DeltaDistribution.NegativeCount} / " +
            $"{report.DeltaDistribution.ZeroCount} / {report.DeltaDistribution.PositiveCount}");
        builder.AppendLine(
            $"- P10 / P25 / P50 / P75 / P90: {report.DeltaDistribution.P10:F3} / " +
            $"{report.DeltaDistribution.P25:F3} / {report.DeltaDistribution.P50:F3} / " +
            $"{report.DeltaDistribution.P75:F3} / {report.DeltaDistribution.P90:F3}");
        builder.AppendLine();
        builder.AppendLine("## Contribution delta patterns");
        builder.AppendLine();
        builder.AppendLine("Damage contribution totals; every delta is DACT minus FFLogs.");
        builder.AppendLine();
        builder.AppendLine("| Component | Mean Δ | Median Δ | Mean abs(Δ) | Max abs(Δ) |");
        builder.AppendLine("|---|---:|---:|---:|---:|");
        foreach (var component in report.ContributionDeltas)
        {
            builder.AppendLine(
                $"| {EscapeMarkdown(component.Component)} | {component.MeanDeltaDamage:F1} | " +
                $"{component.MedianDeltaDamage:F1} | {component.MeanAbsoluteDeltaDamage:F1} | " +
                $"{component.MaximumAbsoluteDeltaDamage:F1} |");
        }
        builder.AppendLine();
        builder.AppendLine("## Group patterns");
        builder.AppendLine();
        builder.AppendLine("| Dimension | Value | N | Exact | Mean Δ | Median Δ | Mean abs(Δ) | Max abs(Δ) |");
        builder.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var group in report.Groups)
        {
            var stats = group.Statistics;
            builder.AppendLine(
                $"| {EscapeMarkdown(group.Dimension)} | {EscapeMarkdown(group.Value)} | {stats.SampleCount} | " +
                $"{stats.ExactMatchCount} | {stats.MeanDelta:F3} | {stats.MedianDelta:F3} | " +
                $"{stats.MeanAbsoluteDelta:F3} | {stats.MaxAbsoluteDelta:F3} |");
        }

        AppendTop(builder, "Top 20 largest errors", report.TopLargestErrors);
        AppendTop(builder, "Top 20 closest", report.TopClosest);
        builder.AppendLine("## Known boundaries");
        builder.AppendLine();
        foreach (var boundary in report.KnownBoundaries)
        {
            builder.AppendLine($"- {boundary}");
        }

        return builder.ToString();
    }

    private static void AppendStatistics(StringBuilder builder, string title, ParityStatistics stats)
    {
        builder.AppendLine($"## {title}");
        builder.AppendLine();
        builder.AppendLine($"- Samples: {stats.SampleCount}");
        builder.AppendLine($"- Exact displayed matches: {stats.ExactMatchCount}");
        builder.AppendLine($"- Within ±1 / ±10 / ±50 / ±100 / ±500: {stats.Within1} / {stats.Within10} / {stats.Within50} / {stats.Within100} / {stats.Within500}");
        builder.AppendLine($"- Mean Δ: {stats.MeanDelta:F6}");
        builder.AppendLine($"- Median Δ: {stats.MedianDelta:F6}");
        builder.AppendLine($"- Mean |Δ|: {stats.MeanAbsoluteDelta:F6}");
        builder.AppendLine($"- Max |Δ|: {stats.MaxAbsoluteDelta:F6}");
        builder.AppendLine();
    }

    private static void AppendTop(
        StringBuilder builder,
        string title,
        IReadOnlyList<ParitySampleResult> samples)
    {
        builder.AppendLine($"## {title}");
        builder.AppendLine();
        builder.AppendLine("| Report | Fight | Actor | Encounter | FFLogs rDPS | DACT rDPS | Δ | Δ% | received Δ | given Δ |");
        builder.AppendLine("|---|---:|---|---|---:|---:|---:|---:|---:|---:|");
        foreach (var sample in samples)
        {
            builder.AppendLine(
                $"| {sample.Report} | {sample.FightId} | {EscapeMarkdown(sample.Actor)} | " +
                $"{EscapeMarkdown(sample.Encounter)} | {sample.FflogsRdps:F3} | {sample.DactRdps:F3} | " +
                $"{sample.DeltaRdps:F3} | {sample.DeltaPercent:F4}% | " +
                $"{sample.ExternalBuffContributionReceivedDelta:F0} | {sample.OwnBuffContributionGivenDelta:F0} |");
        }

        builder.AppendLine();
    }

    private static string Csv(object? value)
    {
        var text = value switch
        {
            null => string.Empty,
            double number => number.ToString("R", CultureInfo.InvariantCulture),
            float number => number.ToString("R", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };
        return text.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? $"\"{text.Replace("\"", "\"\"")}\""
            : text;
    }

    private static string FormatContributions(IEnumerable<FflogsContribution> contributions)
        => string.Join(
            " | ",
            contributions.Select(static item =>
                $"{item.AbilityName}({item.AbilityId}):{item.Amount.ToString("R", CultureInfo.InvariantCulture)}"));

    private static string EscapeMarkdown(string value) => value.Replace("|", "\\|");
}

internal sealed record ReportPaths(string JsonPath, string CsvPath, string MarkdownPath);

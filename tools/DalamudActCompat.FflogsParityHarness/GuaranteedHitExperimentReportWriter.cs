using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DalamudActCompat.FflogsParityHarness;

internal static class GuaranteedHitExperimentReportWriter
{
    public static async Task<GuaranteedHitExperimentReportPaths> WriteAsync(
        string outputDirectory,
        GuaranteedHitAttributionExperimentReport report,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputDirectory);
        var paths = new GuaranteedHitExperimentReportPaths(
            Path.Combine(outputDirectory, "guaranteed-hit-experiment-report.json"),
            Path.Combine(outputDirectory, "guaranteed-hit-fight-candidates.csv"),
            Path.Combine(outputDirectory, "guaranteed-hit-candidate-summary.csv"),
            Path.Combine(outputDirectory, "guaranteed-hit-cohort-validation.csv"),
            Path.Combine(outputDirectory, "guaranteed-hit-action-family-validation.csv"),
            Path.Combine(outputDirectory, "guaranteed-hit-buff-condition-validation.csv"),
            Path.Combine(outputDirectory, "guaranteed-hit-full-replay-counterfactual.csv"),
            Path.Combine(outputDirectory, "guaranteed-hit-experiment-summary.md"));
        await File.WriteAllTextAsync(
            paths.JsonPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true,
            }),
            cancellationToken);
        await File.WriteAllTextAsync(paths.FightCandidatesCsvPath, BuildFightCsv(report), cancellationToken);
        await File.WriteAllTextAsync(paths.CandidateSummaryCsvPath, BuildCandidateCsv(report), cancellationToken);
        await File.WriteAllTextAsync(paths.CohortValidationCsvPath, BuildCohortCsv(report), cancellationToken);
        await File.WriteAllTextAsync(paths.ActionFamilyCsvPath, BuildActionFamilyCsv(report), cancellationToken);
        await File.WriteAllTextAsync(paths.BuffConditionCsvPath, BuildBuffConditionCsv(report), cancellationToken);
        await File.WriteAllTextAsync(paths.FullReplayCsvPath, BuildFullReplayCsv(report), cancellationToken);
        await File.WriteAllTextAsync(paths.MarkdownPath, BuildMarkdown(report), cancellationToken);
        return paths;
    }

    private static string BuildFightCsv(GuaranteedHitAttributionExperimentReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine(
            "report,fightId,actor,encounter,encounterId,partnerJob,duration,partyComposition," +
            "fflogsDevilmentTotal,productionDevilmentTotal,productionResidual,candidate," +
            "candidateDevilmentTotal,candidateResidual,candidateFinalRdpsDelta,guaranteedRawDamage," +
            "guaranteedEventCount,guaranteedDamageShare,criticalChanceProxy,directChanceProxy," +
            "rateBuffOverlapFraction,buffConditions");
        foreach (var item in report.FightResults)
        {
            AppendCsv(builder,
                item.Report, item.FightId, item.Actor, item.Encounter, item.EncounterId,
                item.PartnerJob, item.Duration, item.PartyComposition, item.FflogsDevilmentTotal,
                item.ProductionDevilmentTotal, item.ProductionResidual, item.Candidate,
                item.CandidateDevilmentTotal, item.CandidateResidual, item.CandidateFinalRdpsDelta,
                item.GuaranteedRawDamage, item.GuaranteedEventCount, item.GuaranteedDamageShare,
                item.CriticalChanceProxy, item.DirectChanceProxy, item.RateBuffOverlapFraction,
                item.BuffConditions);
        }
        return builder.ToString();
    }

    private static string BuildCandidateCsv(GuaranteedHitAttributionExperimentReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine(
            "candidate,family,equation,n,meanResidual,medianResidual,mae,rmse,maxAbsoluteResidual," +
            "negativeCount,zeroCount,positiveCount,residualVsDuration,residualVsGuaranteedDamage," +
            "residualVsCriticalProxy,residualVsDirectProxy,residualVsRateBuffOverlap,systematicBias,verdict");
        var definitions = report.Candidates.ToDictionary(static item => item.Name, StringComparer.Ordinal);
        foreach (var item in report.Rankings)
        {
            var stats = item.Statistics;
            AppendCsv(builder,
                item.Candidate, item.Family, definitions[item.Candidate].Equation,
                stats.FightCount, stats.MeanResidual, stats.MedianResidual,
                stats.MeanAbsoluteResidual, stats.RootMeanSquareResidual,
                stats.MaximumAbsoluteResidual, stats.NegativeCount, stats.ZeroCount,
                stats.PositiveCount, stats.ResidualVsDurationCorrelation,
                stats.ResidualVsGuaranteedDamageCorrelation,
                stats.ResidualVsCriticalProxyCorrelation, stats.ResidualVsDirectProxyCorrelation,
                stats.ResidualVsRateBuffOverlapCorrelation, item.SystematicBias, item.Verdict);
        }
        return builder.ToString();
    }

    private static string BuildActionFamilyCsv(GuaranteedHitAttributionExperimentReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine(
            "candidate,actionFamily,actionIds,observedFightCount,rawDamage,heavyFightCount,heavyN,heavyMean," +
            "heavyMedian,heavyMae,heavyMaxAbs,remainingN,remainingMean,remainingMedian,remainingMae," +
            "remainingMaxAbs,residualVsFamilyDamage");
        foreach (var item in report.ActionFamilyValidation)
        {
            AppendCsv(builder,
                item.Candidate, item.ActionFamily, string.Join(';', item.ActionIds),
                item.ObservedFightCount, item.RawDamage, item.HeavyFightCount,
                item.HeavyGroup.FightCount, item.HeavyGroup.MeanResidual,
                item.HeavyGroup.MedianResidual, item.HeavyGroup.MeanAbsoluteResidual,
                item.HeavyGroup.MaximumAbsoluteResidual, item.RemainingGroup.FightCount,
                item.RemainingGroup.MeanResidual, item.RemainingGroup.MedianResidual,
                item.RemainingGroup.MeanAbsoluteResidual, item.RemainingGroup.MaximumAbsoluteResidual,
                item.ResidualVsFamilyDamageCorrelation);
        }
        return builder.ToString();
    }

    private static string BuildCohortCsv(GuaranteedHitAttributionExperimentReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine(
            "candidate,cohort,n,mean,median,mae,rmse,maxAbs,negative,zero,positive," +
            "residualVsDuration,residualVsGuaranteedDamage,residualVsCriticalProxy," +
            "residualVsDirectProxy,residualVsRateBuffOverlap");
        foreach (var item in report.CohortValidation)
        {
            var stats = item.Statistics;
            AppendCsv(builder,
                item.Candidate, item.Cohort, stats.FightCount, stats.MeanResidual,
                stats.MedianResidual, stats.MeanAbsoluteResidual, stats.RootMeanSquareResidual,
                stats.MaximumAbsoluteResidual, stats.NegativeCount, stats.ZeroCount,
                stats.PositiveCount, stats.ResidualVsDurationCorrelation,
                stats.ResidualVsGuaranteedDamageCorrelation,
                stats.ResidualVsCriticalProxyCorrelation, stats.ResidualVsDirectProxyCorrelation,
                stats.ResidualVsRateBuffOverlapCorrelation);
        }
        return builder.ToString();
    }

    private static string BuildBuffConditionCsv(GuaranteedHitAttributionExperimentReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("candidate,condition,n,mean,median,mae,rmse,maxAbs,negative,zero,positive");
        foreach (var item in report.BuffConditionValidation)
        {
            var stats = item.Statistics;
            AppendCsv(builder,
                item.Candidate, item.Condition, stats.FightCount, stats.MeanResidual,
                stats.MedianResidual, stats.MeanAbsoluteResidual, stats.RootMeanSquareResidual,
                stats.MaximumAbsoluteResidual, stats.NegativeCount, stats.ZeroCount, stats.PositiveCount);
        }
        return builder.ToString();
    }

    private static string BuildFullReplayCsv(GuaranteedHitAttributionExperimentReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine(
            "partnerJob,sampleCount,currentMeanDelta,currentMedianDelta,currentMae,currentMaxAbs," +
            "candidateMeanDelta,candidateMedianDelta,candidateMae,candidateMaxAbs");
        foreach (var item in report.FullReplay)
        {
            AppendCsv(builder,
                item.PartnerJob, item.SampleCount, item.CurrentMeanDelta, item.CurrentMedianDelta,
                item.CurrentMeanAbsoluteDelta, item.CurrentMaxAbsoluteDelta, item.CandidateMeanDelta,
                item.CandidateMedianDelta, item.CandidateMeanAbsoluteDelta, item.CandidateMaxAbsoluteDelta);
        }
        return builder.ToString();
    }

    private static string BuildMarkdown(GuaranteedHitAttributionExperimentReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# FFLogs Guaranteed-Hit Attribution Experiment");
        builder.AppendLine();
        builder.AppendLine($"- Equation status: **{report.EquationStatus}**");
        builder.AppendLine($"- Reason: {report.EquationStatusReason}");
        builder.AppendLine($"- Cache: {report.CachedSampleCount} fights; eligible SAM: {report.EligibleSamFightCount}; selected SAM: {report.SelectedSamFightCount}");
        builder.AppendLine($"- Selected diversity: {report.UniqueDancerCount} Dancer actors, {report.EncounterCount} encounters");
        builder.AppendLine($"- Best aggregate candidate: `{report.BestCandidate}`");
        builder.AppendLine();
        builder.AppendLine("## CurrentProduction calibration");
        builder.AppendLine();
        builder.AppendLine(
            $"{report.CurrentProductionCalibration.EventCount} guaranteed events; " +
            $"max per-event residual `{report.CurrentProductionCalibration.MaximumAbsoluteEventResidual:R}`, " +
            $"max per-fight residual `{report.CurrentProductionCalibration.MaximumAbsoluteFightResidual:R}`, " +
            $"PASS = `{report.CurrentProductionCalibration.Passed}`.");
        builder.AppendLine();
        builder.AppendLine("## Candidate equations");
        builder.AppendLine();
        builder.AppendLine("| Candidate | Family | Equation | Conditions |");
        builder.AppendLine("|---|---|---|---|");
        foreach (var item in report.Candidates)
        {
            builder.AppendLine($"| `{item.Name}` | {item.Family} | {EscapeMarkdown(item.Equation)} | {EscapeMarkdown(item.ApplicableConditions)} |");
        }
        builder.AppendLine();
        builder.AppendLine("## Selected-SAM aggregate ranking");
        builder.AppendLine();
        builder.AppendLine("| Candidate | N | Mean | Median | MAE | RMSE | Max abs | Sign | Corr guaranteed damage | Verdict |");
        builder.AppendLine("|---|---:|---:|---:|---:|---:|---:|---|---:|---|");
        foreach (var item in report.Rankings)
        {
            var stats = item.Statistics;
            builder.AppendLine(
                $"| `{item.Candidate}` | {stats.FightCount} | {stats.MeanResidual:F1} | " +
                $"{stats.MedianResidual:F1} | {stats.MeanAbsoluteResidual:F1} | " +
                $"{stats.RootMeanSquareResidual:F1} | {stats.MaximumAbsoluteResidual:F1} | " +
                $"{stats.NegativeCount}/{stats.ZeroCount}/{stats.PositiveCount} | " +
                $"{stats.ResidualVsGuaranteedDamageCorrelation:F3} | {item.Verdict} |");
        }
        builder.AppendLine();
        builder.AppendLine("Residual units in candidate tables are damage contribution, not rDPS. Residual = candidate predicted Devilment given.total - FFLogs Devilment given.total.");
        builder.AppendLine();
        builder.AppendLine("## Selected vs lower-guaranteed-damage holdout (best candidate)");
        builder.AppendLine();
        builder.AppendLine("| Cohort | N | Mean | Median | MAE | Max abs | Corr guaranteed damage |");
        builder.AppendLine("|---|---:|---:|---:|---:|---:|---:|");
        foreach (var item in report.CohortValidation.Where(item => item.Candidate == report.BestCandidate))
        {
            var stats = item.Statistics;
            builder.AppendLine(
                $"| {item.Cohort} | {stats.FightCount} | {stats.MeanResidual:F1} | " +
                $"{stats.MedianResidual:F1} | {stats.MeanAbsoluteResidual:F1} | " +
                $"{stats.MaximumAbsoluteResidual:F1} | {stats.ResidualVsGuaranteedDamageCorrelation:F3} |");
        }
        builder.AppendLine();
        builder.AppendLine("## Leave-one-action-family structural check (best candidate)");
        builder.AppendLine();
        builder.AppendLine("No candidate is trained or fitted. The top guaranteed-damage quartile for each family is treated as the held-out heavy cohort; the other fights are the comparison cohort.");
        builder.AppendLine();
        builder.AppendLine("| Family | IDs | Observed N | Heavy N | Raw damage | Heavy MAE | Remaining MAE | Corr residual/family damage |");
        builder.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|");
        foreach (var item in report.ActionFamilyValidation.Where(item => item.Candidate == report.BestCandidate))
        {
            builder.AppendLine(
                $"| {item.ActionFamily} | {string.Join(", ", item.ActionIds)} | {item.ObservedFightCount} | " +
                $"{item.HeavyFightCount} | {item.RawDamage} | {item.HeavyGroup.MeanAbsoluteResidual:F1} | " +
                $"{item.RemainingGroup.MeanAbsoluteResidual:F1} | {item.ResidualVsFamilyDamageCorrelation:F3} |");
        }
        builder.AppendLine();
        builder.AppendLine("## Buff-condition check (best candidate)");
        builder.AppendLine();
        builder.AppendLine("| Condition | N | Mean | Median | MAE | Max abs | Sign N/0/P |");
        builder.AppendLine("|---|---:|---:|---:|---:|---:|---|");
        foreach (var item in report.BuffConditionValidation.Where(item => item.Candidate == report.BestCandidate))
        {
            var stats = item.Statistics;
            builder.AppendLine(
                $"| {item.Condition} | {stats.FightCount} | {stats.MeanResidual:F1} | " +
                $"{stats.MedianResidual:F1} | {stats.MeanAbsoluteResidual:F1} | " +
                $"{stats.MaximumAbsoluteResidual:F1} | {stats.NegativeCount}/{stats.ZeroCount}/{stats.PositiveCount} |");
        }
        builder.AppendLine();
        builder.AppendLine("## Dimension evidence");
        builder.AppendLine();
        builder.AppendLine("| Dimension | Fights | Events | Raw damage | Verdict |");
        builder.AppendLine("|---|---:|---:|---:|---|");
        foreach (var item in report.DimensionEvidence)
        {
            builder.AppendLine($"| {item.Dimension} | {item.FightCount} | {item.EventCount} | {item.RawDamage} | {item.EvidenceVerdict} |");
        }
        builder.AppendLine();
        builder.AppendLine($"## 100-fight counterfactual: `{report.BestCandidate}`");
        builder.AppendLine();
        builder.AppendLine("| Partner | N | Current mean/median/MAE/max | Candidate mean/median/MAE/max |");
        builder.AppendLine("|---|---:|---|---|");
        foreach (var item in report.FullReplay)
        {
            builder.AppendLine(
                $"| {item.PartnerJob} | {item.SampleCount} | {item.CurrentMeanDelta:F3} / " +
                $"{item.CurrentMedianDelta:F3} / {item.CurrentMeanAbsoluteDelta:F3} / " +
                $"{item.CurrentMaxAbsoluteDelta:F3} | {item.CandidateMeanDelta:F3} / " +
                $"{item.CandidateMedianDelta:F3} / {item.CandidateMeanAbsoluteDelta:F3} / " +
                $"{item.CandidateMaxAbsoluteDelta:F3} |");
        }
        builder.AppendLine();
        builder.AppendLine("## Remaining unknowns");
        builder.AppendLine();
        foreach (var item in report.RemainingUnknowns)
        {
            builder.AppendLine($"- {item}");
        }
        builder.AppendLine();
        builder.AppendLine("## Evidence boundaries");
        builder.AppendLine();
        foreach (var item in report.EvidenceBoundaries)
        {
            builder.AppendLine($"- {item}");
        }
        return builder.ToString();
    }

    private static void AppendCsv(StringBuilder builder, params object?[] values)
        => builder.AppendLine(string.Join(',', values.Select(FormatCsv)));

    private static string FormatCsv(object? value)
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

    private static string EscapeMarkdown(string value)
        => value.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
}

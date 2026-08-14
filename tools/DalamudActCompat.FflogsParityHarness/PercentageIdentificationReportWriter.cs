using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DalamudActCompat.FflogsParityHarness;

internal static class PercentageIdentificationReportWriter
{
    public static async Task<PercentageIdentificationReportPaths> WriteAsync(
        string outputDirectory,
        PercentageIdentificationReport report,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputDirectory);
        var paths = new PercentageIdentificationReportPaths(
            Path.Combine(outputDirectory, "percentage-identification-report.json"),
            Path.Combine(outputDirectory, "percentage-identification-summary.md"),
            Path.Combine(outputDirectory, "percentage-direct-normal-core.csv"),
            Path.Combine(outputDirectory, "percentage-residual-feature-analysis.csv"),
            Path.Combine(outputDirectory, "percentage-cross-provider-validation.csv"),
            Path.Combine(outputDirectory, "percentage-ownership-identifiability.csv"),
            Path.Combine(outputDirectory, "percentage-ownership-discriminators.csv"),
            Path.Combine(outputDirectory, "percentage-matched-interaction-controls.csv"),
            Path.Combine(outputDirectory, "percentage-status-window-metrics.csv"),
            Path.Combine(outputDirectory, "percentage-status-contribution-metrics.csv"));
        await File.WriteAllTextAsync(
            paths.JsonPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);
        await File.WriteAllTextAsync(paths.MarkdownPath, BuildMarkdown(report), cancellationToken);
        await File.WriteAllTextAsync(paths.CoreCsvPath, BuildCoreCsv(report), cancellationToken);
        await File.WriteAllTextAsync(
            paths.ResidualFeaturesCsvPath, BuildResidualFeaturesCsv(report), cancellationToken);
        await File.WriteAllTextAsync(
            paths.OwnershipValidationCsvPath, BuildValidationCsv(report), cancellationToken);
        await File.WriteAllTextAsync(
            paths.IdentifiabilityCsvPath, BuildIdentifiabilityCsv(report), cancellationToken);
        await File.WriteAllTextAsync(
            paths.DiscriminatorsCsvPath, BuildDiscriminatorsCsv(report), cancellationToken);
        await File.WriteAllTextAsync(
            paths.MatchedControlsCsvPath, BuildMatchedCsv(report), cancellationToken);
        await File.WriteAllTextAsync(
            paths.WindowMetricsCsvPath, BuildWindowsCsv(report), cancellationToken);
        await File.WriteAllTextAsync(
            paths.StatusContributionCsvPath, BuildStatusContributionsCsv(report), cancellationToken);
        return paths;
    }

    private static string BuildMarkdown(PercentageIdentificationReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# FFLogs percentage interaction identification and causal status audit");
        builder.AppendLine();
        builder.AppendLine($"Generated: `{report.GeneratedAt:O}`");
        builder.AppendLine();
        builder.AppendLine($"Fights `{report.FightCount}`; rate-overlap constraints " +
                           $"`{report.RateOverlapConstraintCount}`; strict direct-normal core " +
                           $"`{report.CleanDirectNormalConstraintCount}`.");
        builder.AppendLine();
        builder.AppendLine("## Interaction decomposition");
        builder.AppendLine();
        foreach (var line in report.InteractionDecomposition) builder.AppendLine($"- {line}");
        builder.AppendLine();
        builder.AppendLine("| Term | PercentageFirst | RateFirst | SharedShapley2 | SharedShapley3 | SharedBaseLog |");
        builder.AppendLine("|---|---|---|---|---|---|");
        builder.AppendLine("| percentage main | Percentage | Percentage | Percentage | Percentage | global log pool |");
        builder.AppendLine("| Crit main | Crit | Crit | Crit | Crit | global log pool |");
        builder.AppendLine("| DH main | DH | DH | DH | DH | global log pool |");
        builder.AppendLine("| percentage×Crit | Percentage | Crit | 1/2 Percentage + 1/2 Crit | 1/2 each | global log pool |");
        builder.AppendLine("| percentage×DH | Percentage | DH | 1/2 Percentage + 1/2 DH | 1/2 each | global log pool |");
        builder.AppendLine("| Crit×DH | 1/2 Crit + 1/2 DH | 1/2 Crit + 1/2 DH | 1/2 Crit + 1/2 DH | 1/2 Crit + 1/2 DH | global log pool |");
        builder.AppendLine("| percentage×Crit×DH | Percentage | 1/2 Crit + 1/2 DH | 1/2 Percentage + 1/4 Crit + 1/4 DH | 1/3 each | global log pool |");
        builder.AppendLine();
        foreach (var line in report.OwnershipAllocation) builder.AppendLine($"- {line}");

        builder.AppendLine();
        builder.AppendLine("## Ownership candidate results");
        builder.AppendLine();
        builder.AppendLine("| Dataset | Candidate | N | Mean | Median | MAE | RMSE | Max | - / 0 / + |");
        builder.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var dataset in new[] { "RateOverlap1738", "DirectNormalCore262" })
        {
            foreach (var candidate in PercentageIdentificationCandidates.OwnershipCandidates)
            {
                var rows = report.Constraints.Where(item =>
                    dataset == "RateOverlap1738"
                        ? item.RateOverlapEventCount > 0
                        : item.IsCleanDirectNormal);
                var stats = CalculateStatistics(rows.Select(item =>
                    item.ResolvePrediction(candidate) - item.FflogsPercentageReference));
                AppendStatsRow(builder, dataset, candidate, stats);
            }
        }

        builder.AppendLine();
        builder.AppendLine("## SharedBaseLog residual localization");
        builder.AppendLine();
        builder.AppendLine("The table includes association diagnostics plus quartile/category rows in the CSV. " +
                           "No slope is reused as a candidate coefficient.");
        builder.AppendLine();
        builder.AppendLine("| Dataset | Feature | Pearson | Spearman | origin R² | Q1 mean / MAE | Q4 mean / MAE |");
        builder.AppendLine("|---|---|---:|---:|---:|---:|---:|");
        foreach (var association in report.ResidualFeatures
                     .Where(static item => item.Analysis == "association")
                     .OrderBy(static item => item.Dataset)
                     .ThenByDescending(static item => Math.Abs(item.Pearson)))
        {
            var q1 = report.ResidualFeatures.First(item =>
                item.Dataset == association.Dataset && item.Feature == association.Feature &&
                item.Analysis == "quartile" && item.Group == "Q1");
            var q4 = report.ResidualFeatures.First(item =>
                item.Dataset == association.Dataset && item.Feature == association.Feature &&
                item.Analysis == "quartile" && item.Group == "Q4");
            builder.AppendLine($"| {association.Dataset} | {Escape(association.Feature)} | " +
                               $"{association.Pearson:+0.000;-0.000;0.000} | " +
                               $"{association.Spearman:+0.000;-0.000;0.000} | " +
                               $"{association.OriginSlopeExplainedFraction:+0.000;-0.000;0.000} | " +
                               $"{q1.Residuals.MeanResidual:+0.0;-0.0;0.0} / {q1.Residuals.MeanAbsoluteResidual:F1} | " +
                               $"{q4.Residuals.MeanResidual:+0.0;-0.0;0.0} / {q4.Residuals.MeanAbsoluteResidual:F1} |");
        }

        builder.AppendLine();
        builder.AppendLine("## Identifiability");
        builder.AppendLine();
        builder.AppendLine("| Dataset | Candidate A | Candidate B | distinguishable / N | mean | max | conclusion |");
        builder.AppendLine("|---|---|---|---:|---:|---:|---|");
        foreach (var row in report.Identifiability)
        {
            builder.AppendLine($"| {row.Dataset} | {row.CandidateA} | {row.CandidateB} | " +
                               $"{row.DistinguishableConstraintCount} / {row.ConstraintCount} | " +
                               $"{row.MeanAbsolutePredictionDifference:F3} | " +
                               $"{row.MaximumAbsolutePredictionDifference:F3} | {row.Conclusion} |");
        }

        builder.AppendLine();
        builder.AppendLine("## Cross-provider and rate-dimension validation");
        builder.AppendLine();
        builder.AppendLine("| Dataset | Dimension | Value | Candidate | N | Mean | Median | MAE | RMSE | Max | - / 0 / + |");
        builder.AppendLine("|---|---|---|---|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var row in report.OwnershipValidation.Where(static item =>
                     item.Dimension is "ProviderJob" or "RateDimension"))
        {
            AppendValidationRow(builder, row);
        }

        builder.AppendLine();
        builder.AppendLine("## Matched interaction controls");
        builder.AppendLine();
        builder.AppendLine($"Strict aggregate matched pairs: `{report.MatchedControls.Count}`. " +
                           "The CSV retains every same-report actor/provider/encounter/buff/action-family pair; " +
                           "event-local matches without an FFLogs component reference are not promoted to evidence.");

        builder.AppendLine();
        builder.AppendLine("## Causal status lifecycle");
        builder.AppendLine();
        foreach (var line in report.StateMachine) builder.AppendLine($"- {line}");
        builder.AppendLine();
        foreach (var line in report.FallbackStrategies) builder.AppendLine($"- {line}");
        builder.AppendLine();
        builder.AppendLine("| Dataset | Ownership | State | N | Mean | MAE | RMSE | Max | prediction gap from oracle |");
        builder.AppendLine("|---|---|---|---:|---:|---:|---:|---:|---:|");
        foreach (var row in report.StatusContributionMetrics.Where(static item =>
                     item.Dataset is "RateOverlap1738" or "DirectNormalCore262" or "NoRateControls"))
        {
            var stats = row.Statistics;
            builder.AppendLine($"| {row.Dataset} | {row.Ownership} | {row.StateStrategy} | " +
                               $"{stats.ConstraintCount} | {stats.MeanResidual:+0.000;-0.000;0.000} | " +
                               $"{stats.MeanAbsoluteResidual:F3} | {stats.RootMeanSquareResidual:F3} | " +
                               $"{stats.MaximumAbsoluteResidual:F3} | " +
                               $"{row.MeanAbsolutePredictionGapFromOracle:F3} |");
        }
        builder.AppendLine();
        builder.AppendLine("| Strategy | intervals | exact | early | late | incorrect included damage | incorrect excluded damage | fallback mismatch damage | max lateness |");
        builder.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var row in report.WindowMetrics.Where(static item => item.Scope == "All"))
        {
            builder.AppendLine($"| {row.Strategy} | {row.IntervalCount} | {row.ExactEndpointCount} | " +
                               $"{row.EarlyExpiryCount} | {row.LateExpiryCount} | " +
                               $"{row.DamageIncorrectlyIncluded} | {row.DamageIncorrectlyExcluded} | " +
                               $"{row.FallbackOnlyMismatchDamage} | {row.MaximumLatenessMilliseconds:F0} ms |");
        }

        builder.AppendLine();
        builder.AppendLine("## Findings");
        builder.AppendLine();
        foreach (var finding in report.Findings) builder.AppendLine($"- {finding}");
        builder.AppendLine();
        builder.AppendLine("## Evidence boundaries");
        builder.AppendLine();
        foreach (var boundary in report.EvidenceBoundaries) builder.AppendLine($"- {boundary}");
        return builder.ToString();
    }

    private static string BuildCoreCsv(PercentageIdentificationReport report)
    {
        var builder = new StringBuilder();
        AppendCsv(builder,
            "report", "fightId", "encounterId", "encounter", "providerActorId", "providerActor",
            "providerJob", "recipientActorId", "recipientActor", "recipientJob", "buffStatusId", "buffName",
            "rawDamage", "effectiveDamage", "fflogsPercentageReference", "fflogsRecipientRateReference",
            "nominalPercentageFirst", "nominalSharedBaseLog", "oraclePercentageFirst", "oracleRateFirst",
            "oracleSharedShapley", "oracleSharedBaseLog", "oracleSharedShapley3",
            "causalGracePercentageFirst", "causalGraceSharedBaseLog", "causalCohortPercentageFirst",
            "causalCohortSharedBaseLog", "eventCount", "directEventCount", "periodicEventCount",
            "rateOverlapEventCount", "guaranteedCrit", "guaranteedDh", "guaranteedCdh", "petEvents",
            "ambiguousPetEvents", "deathResetBoundaryEvents", "unknownMagnitudeEvents", "metadataMismatchEvents",
            "percentageMultiplier", "percentageProviderCount", "critRateTotal", "dhRateTotal",
            "critProviderCount", "dhProviderCount", "maxCritProviders", "maxDhProviders",
            "separateCritDhProviders", "percentageMain", "criticalMain", "directMain",
            "percentageCritInteraction", "percentageDhInteraction", "critDhInteraction",
            "percentageCritDhInteraction", "buffWindowAgeMs", "distanceToApplyMs", "distanceToRemoveMs",
            "sameTimestampStatusActivity", "minimumPacketSequence", "maximumPacketSequence",
            "percentageComposition", "criticalComposition", "directComposition", "rateDimension",
            "dominantActionFamily", "actionFamilyCount", "statusStateSources", "isCleanDirectNormal",
            "eligibilityExclusions", "sourceCache");
        foreach (var item in report.Constraints.Where(static item => item.IsCleanDirectNormal))
        {
            AppendCsv(builder,
                item.Report, item.FightId, item.EncounterId, item.Encounter, item.ProviderActorId,
                item.ProviderActor, item.ProviderJob, item.RecipientActorId, item.RecipientActor,
                item.RecipientJob, item.BuffStatusId, item.BuffName, item.RawDamage, item.EffectiveDamage,
                item.FflogsPercentageReference, item.FflogsRecipientRateReference, item.NominalPercentageFirst,
                item.NominalSharedBaseLog, item.OraclePercentageFirst, item.OracleRateFirst,
                item.OracleSharedShapley, item.OracleSharedBaseLog, item.OracleSharedShapley3,
                item.CausalGracePercentageFirst, item.CausalGraceSharedBaseLog,
                item.CausalCohortPercentageFirst, item.CausalCohortSharedBaseLog, item.EventCount,
                item.DirectEventCount, item.PeriodicEventCount, item.RateOverlapEventCount,
                item.GuaranteedCriticalEventCount, item.GuaranteedDirectHitEventCount,
                item.GuaranteedCriticalDirectHitEventCount, item.PetEventCount, item.AmbiguousPetEventCount,
                item.DeathResetBoundaryEventCount, item.UnknownMagnitudeEventCount,
                item.MetadataMismatchEventCount, item.DamageWeightedPercentageMultiplier,
                item.DamageWeightedPercentageProviderCount, item.DamageWeightedCriticalRateTotal,
                item.DamageWeightedDirectRateTotal, item.DamageWeightedCriticalProviderCount,
                item.DamageWeightedDirectProviderCount, item.MaximumCriticalProviderCount,
                item.MaximumDirectProviderCount, item.HasSeparateCriticalDirectProviders,
                item.PercentageMainInteraction, item.CriticalMainInteraction, item.DirectMainInteraction,
                item.PercentageCriticalInteraction, item.PercentageDirectInteraction,
                item.CriticalDirectInteraction, item.PercentageCriticalDirectInteraction,
                item.MeanBuffWindowAgeMilliseconds, item.MeanDistanceToApplyMilliseconds,
                item.MeanDistanceToRemoveMilliseconds, item.SameTimestampStatusActivityCount,
                item.MinimumPacketSequence, item.MaximumPacketSequence, item.PercentageComposition,
                item.CriticalComposition, item.DirectComposition, item.RateDimension,
                item.DominantActionFamily, item.ActionFamilyCount, item.StatusStateSources,
                item.IsCleanDirectNormal, item.EligibilityExclusions, item.SourceCache);
        }
        return builder.ToString();
    }

    private static string BuildResidualFeaturesCsv(PercentageIdentificationReport report)
    {
        var builder = new StringBuilder();
        AppendCsv(builder, "dataset", "candidate", "feature", "analysis", "group", "n", "featureMin",
            "featureMax", "featureMean", "pearson", "spearman", "zeroInterceptSlope",
            "originSlopeExplainedFraction", "mean", "median", "mae", "rmse", "maxAbs",
            "negative", "exact", "positive");
        foreach (var item in report.ResidualFeatures)
        {
            AppendCsv(builder, item.Dataset, item.Candidate, item.Feature, item.Analysis, item.Group,
                item.N, item.FeatureMinimum, item.FeatureMaximum, item.FeatureMean, item.Pearson,
                item.Spearman, item.ZeroInterceptSlope, item.OriginSlopeExplainedFraction,
                item.Residuals.MeanResidual, item.Residuals.MedianResidual,
                item.Residuals.MeanAbsoluteResidual, item.Residuals.RootMeanSquareResidual,
                item.Residuals.MaximumAbsoluteResidual, item.Residuals.NegativeCount,
                item.Residuals.ZeroCount, item.Residuals.PositiveCount);
        }
        return builder.ToString();
    }

    private static string BuildValidationCsv(PercentageIdentificationReport report)
    {
        var builder = new StringBuilder();
        AppendCsv(builder, "dataset", "dimension", "value", "candidate", "n", "mean", "median",
            "mae", "rmse", "maxAbs", "negative", "exact", "positive");
        foreach (var item in report.OwnershipValidation)
        {
            AppendStatsCsv(builder, item.Dataset, item.Dimension, item.Value, item.Candidate, item.Statistics);
        }
        return builder.ToString();
    }

    private static string BuildIdentifiabilityCsv(PercentageIdentificationReport report)
    {
        var builder = new StringBuilder();
        AppendCsv(builder, "dataset", "candidateA", "candidateB", "n", "distinguishable",
            "meanAbsolutePredictionDifference", "maximumAbsolutePredictionDifference",
            "observationallyEquivalent", "conclusion");
        foreach (var item in report.Identifiability)
        {
            AppendCsv(builder, item.Dataset, item.CandidateA, item.CandidateB, item.ConstraintCount,
                item.DistinguishableConstraintCount, item.MeanAbsolutePredictionDifference,
                item.MaximumAbsolutePredictionDifference, item.ObservationallyEquivalent, item.Conclusion);
        }
        return builder.ToString();
    }

    private static string BuildDiscriminatorsCsv(PercentageIdentificationReport report)
    {
        var builder = new StringBuilder();
        AppendCsv(builder, "dataset", "candidateA", "candidateB", "report", "fightId", "encounter",
            "provider", "providerJob", "recipient", "recipientJob", "buff", "rateDimension",
            "fflogsReference", "predictionA", "predictionB", "predictionDifference",
            "absoluteResidualA", "absoluteResidualB", "referenceSupports");
        foreach (var item in report.Discriminators)
        {
            AppendCsv(builder, item.Dataset, item.CandidateA, item.CandidateB, item.Report, item.FightId,
                item.Encounter, item.Provider, item.ProviderJob, item.Recipient, item.RecipientJob,
                item.Buff, item.RateDimension, item.FflogsReference, item.PredictionA, item.PredictionB,
                item.PredictionDifference, item.AbsoluteResidualA, item.AbsoluteResidualB,
                item.ReferenceSupports);
        }
        return builder.ToString();
    }

    private static string BuildMatchedCsv(PercentageIdentificationReport report)
    {
        var builder = new StringBuilder();
        AppendCsv(builder, "quality", "report", "recipientActorId", "recipient", "recipientJob",
            "providerActorId", "provider", "providerJob", "encounter", "buff", "actionFamily",
            "controlA", "controlB", "fightA", "fightB", "residualA", "residualB", "residualShift",
            "interactionShift", "referenceAvailability");
        foreach (var item in report.MatchedControls)
        {
            AppendCsv(builder, item.Quality, item.Report, item.RecipientActorId, item.Recipient,
                item.RecipientJob, item.ProviderActorId, item.Provider, item.ProviderJob, item.Encounter,
                item.Buff, item.ActionFamily, item.ControlA, item.ControlB, item.FightA, item.FightB,
                item.ResidualA, item.ResidualB, item.ResidualShift, item.InteractionShift,
                item.ReferenceAvailability);
        }
        return builder.ToString();
    }

    private static string BuildWindowsCsv(PercentageIdentificationReport report)
    {
        var builder = new StringBuilder();
        AppendCsv(builder, "scope", "value", "strategy", "intervalCount", "exactEndpointCount",
            "earlyExpiryCount", "lateExpiryCount", "damageIncorrectlyIncludedCount",
            "damageIncorrectlyIncluded", "damageIncorrectlyExcludedCount", "damageIncorrectlyExcluded",
            "fallbackOnlyMismatchCount", "fallbackOnlyMismatchDamage", "maximumLatenessMs",
            "causality", "bound");
        foreach (var item in report.WindowMetrics)
        {
            AppendCsv(builder, item.Scope, item.Value, item.Strategy, item.IntervalCount,
                item.ExactEndpointCount, item.EarlyExpiryCount, item.LateExpiryCount,
                item.DamageIncorrectlyIncludedCount, item.DamageIncorrectlyIncluded,
                item.DamageIncorrectlyExcludedCount, item.DamageIncorrectlyExcluded,
                item.FallbackOnlyMismatchCount, item.FallbackOnlyMismatchDamage,
                item.MaximumLatenessMilliseconds, item.Causality, item.Bound);
        }
        return builder.ToString();
    }

    private static string BuildStatusContributionsCsv(PercentageIdentificationReport report)
    {
        var builder = new StringBuilder();
        AppendCsv(builder, "dataset", "ownership", "stateStrategy", "candidate", "n", "mean", "median",
            "mae", "rmse", "maxAbs", "negative", "exact", "positive", "meanAbsPredictionGapFromOracle");
        foreach (var item in report.StatusContributionMetrics)
        {
            var stats = item.Statistics;
            AppendCsv(builder, item.Dataset, item.Ownership, item.StateStrategy, item.Candidate,
                stats.ConstraintCount, stats.MeanResidual, stats.MedianResidual,
                stats.MeanAbsoluteResidual, stats.RootMeanSquareResidual,
                stats.MaximumAbsoluteResidual, stats.NegativeCount, stats.ZeroCount,
                stats.PositiveCount, item.MeanAbsolutePredictionGapFromOracle);
        }
        return builder.ToString();
    }

    private static void AppendStatsRow(
        StringBuilder builder,
        string dataset,
        string candidate,
        MatrixResidualStatistics stats)
        => builder.AppendLine($"| {dataset} | {candidate} | {stats.ConstraintCount} | " +
                              $"{stats.MeanResidual:+0.000;-0.000;0.000} | " +
                              $"{stats.MedianResidual:+0.000;-0.000;0.000} | " +
                              $"{stats.MeanAbsoluteResidual:F3} | {stats.RootMeanSquareResidual:F3} | " +
                              $"{stats.MaximumAbsoluteResidual:F3} | " +
                              $"{stats.NegativeCount}/{stats.ZeroCount}/{stats.PositiveCount} |");

    private static void AppendValidationRow(StringBuilder builder, OwnershipValidationRow row)
    {
        var stats = row.Statistics;
        builder.AppendLine($"| {row.Dataset} | {row.Dimension} | {Escape(row.Value)} | {row.Candidate} | " +
                           $"{stats.ConstraintCount} | {stats.MeanResidual:+0.000;-0.000;0.000} | " +
                           $"{stats.MedianResidual:+0.000;-0.000;0.000} | " +
                           $"{stats.MeanAbsoluteResidual:F3} | {stats.RootMeanSquareResidual:F3} | " +
                           $"{stats.MaximumAbsoluteResidual:F3} | " +
                           $"{stats.NegativeCount}/{stats.ZeroCount}/{stats.PositiveCount} |");
    }

    private static void AppendStatsCsv(
        StringBuilder builder,
        string dataset,
        string dimension,
        string value,
        string candidate,
        MatrixResidualStatistics stats)
        => AppendCsv(builder, dataset, dimension, value, candidate, stats.ConstraintCount,
            stats.MeanResidual, stats.MedianResidual, stats.MeanAbsoluteResidual,
            stats.RootMeanSquareResidual, stats.MaximumAbsoluteResidual, stats.NegativeCount,
            stats.ZeroCount, stats.PositiveCount);

    private static MatrixResidualStatistics CalculateStatistics(IEnumerable<double> source)
    {
        var values = source.ToArray();
        if (values.Length == 0) return new MatrixResidualStatistics(0, 0, 0, 0, 0, 0, 0, 0, 0);
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
            values.Count(static value => value < -0.05),
            values.Count(static value => Math.Abs(value) <= 0.05),
            values.Count(static value => value > 0.05));
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

    private static string Escape(string value) => value.Replace("|", "\\|");
}

using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DalamudActCompat.FflogsParityHarness;

internal static class PercentageOrderingReportWriter
{
    public static async Task<PercentageOrderingReportPaths> WriteAsync(
        string outputDirectory,
        PercentageOrderingReport report,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputDirectory);
        var paths = new PercentageOrderingReportPaths(
            Path.Combine(outputDirectory, "percentage-ordering-report.json"),
            Path.Combine(outputDirectory, "percentage-ordering-summary.md"),
            Path.Combine(outputDirectory, "percentage-ordering-constraints.csv"),
            Path.Combine(outputDirectory, "percentage-rate-overlap-events.csv"),
            Path.Combine(outputDirectory, "percentage-ordering-statistics.csv"),
            Path.Combine(outputDirectory, "percentage-ordering-matched-controls.csv"),
            Path.Combine(outputDirectory, "percentage-expiry-audit.csv"),
            Path.Combine(outputDirectory, "percentage-technical-eligibility.csv"),
            Path.Combine(outputDirectory, "percentage-rate-conservation.csv"));
        await File.WriteAllTextAsync(
            paths.JsonPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);
        await File.WriteAllTextAsync(paths.MarkdownPath, BuildMarkdown(report), cancellationToken);
        await File.WriteAllTextAsync(paths.ConstraintsCsvPath, BuildConstraintsCsv(report), cancellationToken);
        await File.WriteAllTextAsync(paths.EventsCsvPath, BuildEventsCsv(report), cancellationToken);
        await File.WriteAllTextAsync(paths.StatisticsCsvPath, BuildStatisticsCsv(report), cancellationToken);
        await File.WriteAllTextAsync(paths.MatchedControlsCsvPath, BuildMatchedCsv(report), cancellationToken);
        await File.WriteAllTextAsync(paths.ExpiryCsvPath, BuildExpiryCsv(report), cancellationToken);
        await File.WriteAllTextAsync(paths.TechnicalCsvPath, BuildTechnicalCsv(report), cancellationToken);
        await File.WriteAllTextAsync(paths.ConservationCsvPath, BuildConservationCsv(report), cancellationToken);
        return paths;
    }

    private static string BuildMarkdown(PercentageOrderingReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# FFLogs percentage / rate component ordering audit");
        builder.AppendLine();
        builder.AppendLine($"Generated: `{report.GeneratedAt:O}`");
        builder.AppendLine();
        builder.AppendLine("Guaranteed Crit/DH/CDH equation identification remains paused. Rate math is held fixed as an input to this ordering diagnostic.");
        builder.AppendLine();
        builder.AppendLine("## Production pipeline and damage bases");
        builder.AppendLine();
        foreach (var step in report.ProductionPipeline) builder.AppendLine($"- {step}");
        builder.AppendLine();
        builder.AppendLine("## Candidate equations");
        builder.AppendLine();
        builder.AppendLine("| Candidate | Equation |");
        builder.AppendLine("|---|---|");
        foreach (var candidate in report.Candidates)
            builder.AppendLine($"| {Escape(candidate.Name)} | {Escape(candidate.Equation)} |");
        builder.AppendLine();
        builder.AppendLine("## Offline state clone versus measured production counters");
        builder.AppendLine();
        builder.AppendLine("| Component | N | Mean | MAE | Max abs | - / 0 / + |");
        builder.AppendLine("|---|---:|---:|---:|---:|---:|");
        AppendStats(builder, "Percentage state clone", report.ProductionPercentageCalibration);
        AppendStats(builder, "Critical state clone", report.ProductionCriticalCalibration);
        AppendStats(builder, "Direct-hit state clone", report.ProductionDirectHitCalibration);
        builder.AppendLine();
        builder.AppendLine("## Residual by scope");
        builder.AppendLine();
        builder.AppendLine("| Scope | Value | Candidate | N | Mean | Median | MAE | RMSE | Max abs | - / 0 / + |");
        builder.AppendLine("|---|---|---|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var item in report.Statistics)
        {
            var stats = item.Statistics;
            builder.AppendLine($"| {Escape(item.Scope)} | {Escape(item.Value)} | {Escape(item.Candidate)} | " +
                $"{stats.ConstraintCount} | {stats.MeanResidual:+0.000;-0.000;0.000} | " +
                $"{stats.MedianResidual:+0.000;-0.000;0.000} | {stats.MeanAbsoluteResidual:F3} | " +
                $"{stats.RootMeanSquareResidual:F3} | {stats.MaximumAbsoluteResidual:F3} | " +
                $"{stats.NegativeCount}/{stats.ZeroCount}/{stats.PositiveCount} |");
        }
        builder.AppendLine();
        builder.AppendLine("## Provider: no-rate versus rate overlap");
        builder.AppendLine();
        builder.AppendLine("| Provider | Buff | No-rate N / MAE | Rate N / MAE | Rate mean | Rate max |");
        builder.AppendLine("|---|---|---:|---:|---:|---:|");
        foreach (var item in report.ProviderRateComparison)
            builder.AppendLine($"| {Escape(item.Provider)} | {Escape(item.Buff)} | " +
                $"{item.NoRateCount} / {item.NoRate.MeanAbsoluteResidual:F3} | " +
                $"{item.RateOverlapCount} / {item.RateOverlap.MeanAbsoluteResidual:F3} | " +
                $"{item.RateOverlap.MeanResidual:+0.000;-0.000;0.000} | " +
                $"{item.RateOverlap.MaximumAbsoluteResidual:F3} |");
        builder.AppendLine();
        builder.AppendLine("## Expiry and Technical state");
        builder.AppendLine();
        builder.AppendLine($"Percentage status intervals: **{report.ExpiryAudit.Count}**; post/pre nominal intervals with affected damage: **{report.ExpiryAudit.Count(static item => item.DamageBetweenNominalAndObserved > 0)}**.");
        builder.AppendLine($"Technical intervals: **{report.TechnicalAudit.Count}**; observed-only damage: **{report.TechnicalAudit.Sum(static item => item.DamageObservedOnly)}**; current-only damage: **{report.TechnicalAudit.Sum(static item => item.DamageCurrentOnly)}**.");
        builder.AppendLine();
        builder.AppendLine("## Percentage + rate conservation");
        builder.AppendLine();
        var productionCombined = CalculateStatistics(report.Conservation.Select(static item => item.ProductionCombinedDelta));
        var observedCombined = CalculateStatistics(report.Conservation.Select(static item => item.ObservedPercentageFirstCombinedDelta));
        builder.AppendLine($"Production measured combined component: N={productionCombined.ConstraintCount}, mean={productionCombined.MeanResidual:+0.000;-0.000;0.000}, MAE={productionCombined.MeanAbsoluteResidual:F3}, max={productionCombined.MaximumAbsoluteResidual:F3}.");
        builder.AppendLine($"Observed-state current-equation combined component: N={observedCombined.ConstraintCount}, mean={observedCombined.MeanResidual:+0.000;-0.000;0.000}, MAE={observedCombined.MeanAbsoluteResidual:F3}, max={observedCombined.MaximumAbsoluteResidual:F3}.");
        builder.AppendLine("All four ordering variants conserve the same percentage+rate total per event; they differ only in which component owns the multiplicative interaction.");
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

    private static void AppendStats(StringBuilder builder, string component, MatrixResidualStatistics stats)
        => builder.AppendLine($"| {component} | {stats.ConstraintCount} | " +
            $"{stats.MeanResidual:+0.000000;-0.000000;0.000000} | {stats.MeanAbsoluteResidual:F9} | " +
            $"{stats.MaximumAbsoluteResidual:G9} | {stats.NegativeCount}/{stats.ZeroCount}/{stats.PositiveCount} |");

    private static string BuildConstraintsCsv(PercentageOrderingReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("report,fightId,encounterId,encounter,partyComposition,providerType,providerActorId,providerActor,providerJob,recipientActorId,recipientActor,recipientJob,buffStatusId,buffName,fflogsContribution,currentProductionContribution,nominalSharedLogContribution,observedPercentageFirstContribution,observedRateFirstContribution,observedSharedShapleyContribution,observedSharedLogContribution,currentProductionDelta,nominalSharedLogDelta,observedPercentageFirstDelta,observedRateFirstDelta,observedSharedShapleyDelta,observedSharedLogDelta,eventCount,directEvents,periodicEvents,rateOverlapEvents,stateDifferenceEvents,guaranteedCritEvents,guaranteedDhEvents,guaranteedCdhEvents,eligibleDamage,rateOverlapDamage,rateComposition,overlapGroups,sourceCache");
        foreach (var item in report.Constraints)
            AppendCsv(builder, item.Report, item.FightId, item.EncounterId, item.Encounter,
                item.PartyComposition, item.ProviderType, item.ProviderActorId, item.ProviderActor,
                item.ProviderJob, item.RecipientActorId, item.RecipientActor, item.RecipientJob,
                item.BuffStatusId, item.BuffName, item.FflogsContribution,
                item.CurrentProductionContribution, item.NominalSharedLogContribution,
                item.ObservedPercentageFirstContribution,
                item.ObservedRateFirstContribution, item.ObservedSharedShapleyContribution,
                item.ObservedSharedLogContribution, item.CurrentProductionDelta,
                item.NominalSharedLogDelta,
                item.ObservedPercentageFirstDelta, item.ObservedRateFirstDelta,
                item.ObservedSharedShapleyDelta, item.ObservedSharedLogDelta, item.EventCount,
                item.DirectEventCount, item.PeriodicEventCount, item.RateOverlapEventCount,
                item.StateDifferenceEventCount,
                item.GuaranteedCriticalEventCount, item.GuaranteedDirectHitEventCount,
                item.GuaranteedCriticalDirectHitEventCount, item.EligibleDamage,
                item.RateOverlapDamage, item.RateComposition, item.OverlapGroups, item.SourceCache);
        return builder.ToString();
    }

    private static string BuildEventsCsv(PercentageOrderingReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("report,fightId,encounter,timestamp,attributionSequence,damageActorId,damageActor,damageActorJob,damageSourceId,damageSource,targetActorId,actionId,actionName,rawDamage,effectiveDamage,isCrit,isDirectHit,isGuaranteedCrit,isGuaranteedDh,isGuaranteedCdh,isPeriodic,activePercentageBuffs,activeCritRateBuffs,activeDhRateBuffs,combinedPercentageMultiplier,productionPercentageContribution,productionCritContribution,productionDhContribution,productionGuaranteedContribution,offlineProductionPercentageContribution,offlineProductionCritContribution,offlineProductionDhContribution,percentageCalibrationResidual,critCalibrationResidual,dhCalibrationResidual,damageBasisUsedByPercentage,damageBasisUsedByRate,observedRateContributionAfterPercentage,observedRateContributionOnRaw,observedPercentageFirstTotal,observedRateFirstTotal,observedSharedShapleyTotal,observedSharedLogTotal,currentConservationTotal,rateFirstConservationTotal,sharedShapleyConservationTotal,sharedLogConservationTotal,overlapGroups,stateBoundaryNote");
        foreach (var item in report.RateOverlapEvents)
            AppendCsv(builder, item.Report, item.FightId, item.Encounter, item.Timestamp,
                item.AttributionSequence, item.DamageActorId, item.DamageActor, item.DamageActorJob,
                item.DamageSourceId, item.DamageSource, item.TargetActorId, item.ActionId,
                item.ActionName, item.RawDamage, item.EffectiveDamage, item.IsCritical,
                item.IsDirectHit, item.IsGuaranteedCritical, item.IsGuaranteedDirectHit,
                item.IsGuaranteedCriticalDirectHit, item.IsPeriodic, item.ActivePercentageBuffs,
                item.ActiveCriticalRateBuffs, item.ActiveDirectRateBuffs,
                item.CombinedPercentageMultiplier, item.ProductionPercentageContribution,
                item.ProductionCriticalContribution, item.ProductionDirectHitContribution,
                item.ProductionGuaranteedContribution, item.OfflineProductionPercentageContribution,
                item.OfflineProductionCriticalContribution, item.OfflineProductionDirectHitContribution,
                item.PercentageCalibrationResidual, item.CriticalCalibrationResidual,
                item.DirectHitCalibrationResidual, item.DamageBasisUsedByPercentage,
                item.DamageBasisUsedByRate, item.ObservedRateContributionAfterPercentage,
                item.ObservedRateContributionOnRaw, item.ObservedPercentageFirstTotal,
                item.ObservedRateFirstTotal, item.ObservedSharedShapleyTotal,
                item.ObservedSharedLogTotal, item.CurrentConservationTotal,
                item.RateFirstConservationTotal, item.SharedShapleyConservationTotal,
                item.SharedLogConservationTotal, item.OverlapGroups, item.StateBoundaryNote);
        return builder.ToString();
    }

    private static string BuildStatisticsCsv(PercentageOrderingReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("scope,value,candidate,n,mean,median,mae,rmse,maxAbs,negative,exact,positive");
        foreach (var item in report.Statistics)
        {
            var stats = item.Statistics;
            AppendCsv(builder, item.Scope, item.Value, item.Candidate, stats.ConstraintCount,
                stats.MeanResidual, stats.MedianResidual, stats.MeanAbsoluteResidual,
                stats.RootMeanSquareResidual, stats.MaximumAbsoluteResidual,
                stats.NegativeCount, stats.ZeroCount, stats.PositiveCount);
        }
        return builder.ToString();
    }

    private static string BuildMatchedCsv(PercentageOrderingReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("actor,job,report,fightId,encounter,actionId,actionName,percentageComposition,percentageOnlyEvents,critOnlyEvents,dhOnlyEvents,critAndDhEvents,percentageOnlyPerDamage,critOnlyPerDamage,dhOnlyPerDamage,critAndDhPerDamage,quality,referenceAvailability");
        foreach (var item in report.MatchedControls)
            AppendCsv(builder, item.Actor, item.Job, item.Report, item.FightId, item.Encounter,
                item.ActionId, item.ActionName, item.PercentageComposition, item.PercentageOnlyEvents,
                item.CriticalOnlyEvents, item.DirectOnlyEvents, item.CriticalAndDirectEvents,
                item.MeanPercentageOnlyPerDamage, item.MeanCriticalOnlyPerDamage,
                item.MeanDirectOnlyPerDamage, item.MeanCriticalAndDirectPerDamage,
                item.Quality, item.ReferenceAvailability);
        return builder.ToString();
    }

    private static string BuildExpiryCsv(PercentageOrderingReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("report,fightId,statusId,buff,provider,recipient,start,nominalEnd,observedEnd,observedEndSequence,explicitRemove,refreshOrOverwrite,clearedByDeath,damageEventsBetweenEnds,rawDamageBetweenEnds,sameTimestampApplyBeforeStatus,sameTimestampApplyAfterStatus,sameTimestampRemoveBeforeStatus,sameTimestampRemoveAfterStatus,evidence");
        foreach (var item in report.ExpiryAudit)
            AppendCsv(builder, item.Report, item.FightId, item.StatusId, item.Buff, item.Provider,
                item.Recipient, item.Start, item.NominalEnd, item.ObservedEnd,
                item.ObservedEndSequence, item.ExplicitRemove, item.RefreshOrOverwrite,
                item.ClearedByDeath, item.DamageBetweenNominalAndObserved,
                item.RawDamageBetweenNominalAndObserved, item.SameTimestampApplyBeforeStatus,
                item.SameTimestampApplyAfterStatus, item.SameTimestampRemoveBeforeStatus,
                item.SameTimestampRemoveAfterStatus, item.Evidence);
        return builder.ToString();
    }

    private static string BuildTechnicalCsv(PercentageOrderingReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("report,fightId,providerActorId,recipientActorId,applyTimestamp,nominalEnd,observedEnd,eventsCurrentOnly,damageCurrentOnly,eventsObservedOnly,damageObservedOnly,sameTimestampApplyBeforeStatus,sameTimestampApplyAfterStatus,sameTimestampRemoveBeforeStatus,sameTimestampRemoveAfterStatus,aggregateReferenceAvailability");
        foreach (var item in report.TechnicalAudit)
            AppendCsv(builder, item.Report, item.FightId, item.ProviderActorId, item.RecipientActorId,
                item.ApplyTimestamp, item.NominalEnd, item.ObservedEnd, item.EventsCurrentOnly,
                item.DamageCurrentOnly, item.EventsObservedOnly, item.DamageObservedOnly,
                item.SameTimestampApplyBeforeStatus, item.SameTimestampApplyAfterStatus,
                item.SameTimestampRemoveBeforeStatus, item.SameTimestampRemoveAfterStatus,
                item.AggregateReferenceAvailability);
        return builder.ToString();
    }

    private static string BuildConservationCsv(PercentageOrderingReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("report,fightId,encounter,recipientActorId,recipientActor,recipientJob,fflogsPercentageTaken,fflogsRateTaken,fflogsCombinedTaken,productionPercentageReceived,productionCriticalReceived,productionDirectHitReceived,productionCombinedReceived,productionCombinedDelta,observedPercentageFirst,observedRateFirst,observedSharedShapley,observedSharedLog,observedRateCurrentEquation,observedPercentageFirstCombinedDelta,observedRateFirstCombinedDelta,observedSharedShapleyCombinedDelta,observedSharedLogCombinedDelta");
        foreach (var item in report.Conservation)
            AppendCsv(builder, item.Report, item.FightId, item.Encounter, item.RecipientActorId,
                item.RecipientActor, item.RecipientJob, item.FflogsPercentageTaken,
                item.FflogsRateTaken, item.FflogsCombinedTaken,
                item.ProductionPercentageReceived, item.ProductionCriticalReceived,
                item.ProductionDirectHitReceived, item.ProductionCombinedReceived,
                item.ProductionCombinedDelta, item.ObservedPercentageFirst,
                item.ObservedRateFirst, item.ObservedSharedShapley, item.ObservedSharedLog,
                item.ObservedRateCurrentEquation, item.ObservedPercentageFirstCombinedDelta,
                item.ObservedRateFirstCombinedDelta, item.ObservedSharedShapleyCombinedDelta,
                item.ObservedSharedLogCombinedDelta);
        return builder.ToString();
    }

    private static MatrixResidualStatistics CalculateStatistics(IEnumerable<double> source)
    {
        var values = source.ToArray();
        if (values.Length == 0)
            return new MatrixResidualStatistics(0, 0, 0, 0, 0, 0, 0, 0, 0);
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
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };
        return text.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? $"\"{text.Replace("\"", "\"\"")}\""
            : text;
    }

    private static string Escape(string value)
        => value.Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
}

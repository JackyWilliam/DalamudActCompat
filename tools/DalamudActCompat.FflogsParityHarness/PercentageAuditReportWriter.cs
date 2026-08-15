using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DalamudActCompat.FflogsParityHarness;

internal static class PercentageAuditReportWriter
{
    public static async Task<PercentageAuditReportPaths> WriteAsync(
        string outputDirectory,
        PercentageAuditReport report,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputDirectory);
        var paths = new PercentageAuditReportPaths(
            Path.Combine(outputDirectory, "percentage-audit-report.json"),
            Path.Combine(outputDirectory, "percentage-audit-summary.md"),
            Path.Combine(outputDirectory, "percentage-audit-constraints.csv"),
            Path.Combine(outputDirectory, "percentage-audit-windows.csv"),
            Path.Combine(outputDirectory, "percentage-audit-reference.csv"),
            Path.Combine(outputDirectory, "percentage-audit-provider-statistics.csv"));
        await File.WriteAllTextAsync(
            paths.JsonPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);
        await File.WriteAllTextAsync(paths.MarkdownPath, BuildMarkdown(report), cancellationToken);
        await File.WriteAllTextAsync(paths.ConstraintsCsvPath, BuildConstraintsCsv(report), cancellationToken);
        await File.WriteAllTextAsync(paths.WindowsCsvPath, BuildWindowsCsv(report), cancellationToken);
        await File.WriteAllTextAsync(paths.ReferenceCsvPath, BuildReferenceCsv(report), cancellationToken);
        await File.WriteAllTextAsync(
            paths.ProviderStatisticsCsvPath,
            BuildStatisticsCsv(report),
            cancellationToken);
        return paths;
    }

    private static string BuildMarkdown(PercentageAuditReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# FFLogs fixed-percentage attribution audit");
        builder.AppendLine();
        builder.AppendLine($"Generated: `{report.GeneratedAt:O}`");
        builder.AppendLine();
        builder.AppendLine("Guaranteed Crit/DH/CDH equation identification is intentionally paused in this report.");
        builder.AppendLine();
        builder.AppendLine("## Reference extraction");
        builder.AppendLine();
        builder.AppendLine($"Provider `given[]` vs sum of recipient `taken[]`: **{report.ReferenceExactCount}/{report.ReferenceAuditCount} exact within 0.05 damage**.");
        builder.AppendLine($"Unmatched fixed-percentage recipient references: **{report.UnmatchedReferences.Count}**.");
        builder.AppendLine();
        builder.AppendLine("## Overall forward replay");
        builder.AppendLine();
        builder.AppendLine("| Model | N | Mean | Median | MAE | RMSE | Max abs | - / 0 / + |");
        builder.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|");
        AppendStatistics(builder, "Current production before fix", report.CurrentProductionBeforeFix);
        AppendStatistics(builder, "Current production after metadata fix", report.CurrentProductionAfterFix);
        AppendStatistics(builder, "Published math + legacy metadata", report.LegacyPublishedMath);
        AppendStatistics(builder, "Published math + authoritative metadata", report.AuthoritativeMetadata);
        AppendStatistics(builder, "Authoritative + all active denominator", report.AllActiveDenominator);
        AppendStatistics(builder, "Authoritative + self-stripped damage basis", report.SelfStrippedBasis);
        AppendStatistics(builder, "FFLogs calculateddamage multiplier diagnostic", report.CalculatedMultiplier);
        AppendStatistics(builder, "Packet-ordered apply/remove + nominal expiry", report.PacketOrdered);
        AppendStatistics(builder, "Timestamp-only + explicit remove expiry", report.ExplicitRemoval);
        AppendStatistics(builder, "Packet-ordered + explicit remove event state", report.ObservedEventState);
        AppendStatistics(builder, "Observed event state + percentage DoT hit-time", report.HitTimePercentage);
        builder.AppendLine();
        builder.AppendLine("## Provider/type classification");
        builder.AppendLine();
        builder.AppendLine("| Dimension | Value | N | Current MAE | Legacy MAE | Authoritative mean | Authoritative MAE | Max abs |");
        builder.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|");
        foreach (var item in report.Statistics)
        {
            builder.AppendLine(
                $"| {item.Dimension} | {EscapeMarkdown(item.Value)} | {item.AuthoritativeMetadata.ConstraintCount} | " +
                $"{item.CurrentProduction.MeanAbsoluteResidual:F3} | {item.LegacyMetadata.MeanAbsoluteResidual:F3} | " +
                $"{item.AuthoritativeMetadata.MeanResidual:+0.000;-0.000;0.000} | " +
                $"{item.AuthoritativeMetadata.MeanAbsoluteResidual:F3} | {item.AuthoritativeMetadata.MaximumAbsoluteResidual:F3} |");
        }
        builder.AppendLine();
        builder.AppendLine("## Findings");
        builder.AppendLine();
        foreach (var finding in report.Findings)
        {
            builder.AppendLine($"- {finding}");
        }
        builder.AppendLine();
        builder.AppendLine("## Top 10 authoritative residuals");
        builder.AppendLine();
        AppendConstraintTable(builder, report.TopLargestResiduals);
        builder.AppendLine();
        builder.AppendLine("## Five closest controls");
        builder.AppendLine();
        AppendConstraintTable(builder, report.ClosestControls);
        builder.AppendLine();
        builder.AppendLine("## Evidence boundaries");
        builder.AppendLine();
        foreach (var boundary in report.Boundaries)
        {
            builder.AppendLine($"- {boundary}");
        }
        return builder.ToString();
    }

    private static void AppendStatistics(
        StringBuilder builder,
        string name,
        MatrixResidualStatistics statistics)
        => builder.AppendLine(
            $"| {name} | {statistics.ConstraintCount} | {statistics.MeanResidual:+0.000;-0.000;0.000} | " +
            $"{statistics.MedianResidual:+0.000;-0.000;0.000} | {statistics.MeanAbsoluteResidual:F3} | " +
            $"{statistics.RootMeanSquareResidual:F3} | {statistics.MaximumAbsoluteResidual:F3} | " +
            $"{statistics.NegativeCount}/{statistics.ZeroCount}/{statistics.PositiveCount} |");

    private static void AppendConstraintTable(
        StringBuilder builder,
        IEnumerable<PercentageConstraintAuditRow> rows)
    {
        builder.AppendLine("| Report:fight | Provider → recipient | Buff/type | FFLogs | Authoritative | Δ | Events/raw | Single/overlap |");
        builder.AppendLine("|---|---|---|---:|---:|---:|---:|---:|");
        foreach (var item in rows)
        {
            builder.AppendLine(
                $"| `{item.Report}:{item.FightId}` | {EscapeMarkdown(item.ProviderActor)} → {EscapeMarkdown(item.RecipientActor)} ({item.RecipientJob}) | " +
                $"{EscapeMarkdown(item.BuffName)}/{item.ProviderType} | {item.FflogsContribution:F3} | " +
                $"{item.AuthoritativeContribution:F3} | {item.AuthoritativeDelta:+0.000;-0.000;0.000} | " +
                $"{item.EventCount}/{item.EligibleDamage} | {item.SinglePercentageEventCount}/{item.OverlapEventCount} |");
        }
    }

    private static string BuildConstraintsCsv(PercentageAuditReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("report,fightId,encounterId,encounter,partyComposition,providerType,providerActorId,providerActor,providerJob,recipientActorId,recipientActor,recipientJob,buffStatusId,buffName,magnitude,authoritativeMultiplier,legacyMultiplier,fflogsContribution,currentProductionBeforeFixContribution,currentProductionContribution,legacyPublishedMathContribution,authoritativeContribution,allActiveDenominatorContribution,selfStrippedBasisContribution,calculatedMultiplierContribution,packetOrderedContribution,explicitRemovalContribution,observedEventStateContribution,hitTimePercentageContribution,currentProductionBeforeFixDelta,currentProductionDelta,legacyMetadataDelta,authoritativeDelta,allActiveDenominatorDelta,selfStrippedBasisDelta,calculatedMultiplierDelta,packetOrderedDelta,explicitRemovalDelta,observedEventStateDelta,hitTimePercentageDelta,eventCount,eligibleDamage,singlePercentageEvents,singlePercentageDamage,overlapEvents,overlapDamage,rateOverlapEvents,selfPercentageEvents,calculatedMultiplierEvents,guaranteedEvents,directEvents,periodicEvents,petEvents,aoeEvents,windowCount,entireConstraintSinglePercentage,roleConditionalMagnitude,activePercentageComposition,selfPercentageComposition,sourceCache,warnings");
        foreach (var item in report.Constraints)
        {
            AppendCsv(builder,
                item.Report, item.FightId, item.EncounterId, item.Encounter, item.PartyComposition,
                item.ProviderType, item.ProviderActorId, item.ProviderActor, item.ProviderJob,
                item.RecipientActorId, item.RecipientActor, item.RecipientJob, item.BuffStatusId,
                item.BuffName, item.Magnitude, item.AuthoritativeMultiplier, item.LegacyMultiplier,
                item.FflogsContribution, item.CurrentProductionBeforeFixContribution,
                item.CurrentProductionContribution,
                item.LegacyPublishedMathContribution, item.AuthoritativeContribution,
                item.AllActiveDenominatorContribution, item.SelfStrippedBasisContribution,
                item.CalculatedMultiplierContribution,
                item.PacketOrderedContribution, item.ExplicitRemovalContribution,
                item.ObservedEventStateContribution,
                item.HitTimePercentageContribution,
                item.CurrentProductionBeforeFixDelta, item.CurrentProductionDelta,
                item.LegacyMetadataDelta, item.AuthoritativeDelta,
                item.AllActiveDenominatorDelta, item.SelfStrippedBasisDelta,
                item.CalculatedMultiplierDelta,
                item.PacketOrderedDelta, item.ExplicitRemovalDelta,
                item.ObservedEventStateDelta,
                item.HitTimePercentageDelta,
                item.EventCount, item.EligibleDamage, item.SinglePercentageEventCount,
                item.SinglePercentageDamage, item.OverlapEventCount, item.OverlapDamage,
                item.RateOverlapEventCount, item.SelfPercentageEventCount,
                item.CalculatedMultiplierEventCount,
                item.GuaranteedEventCount, item.DirectEventCount,
                item.PeriodicEventCount, item.PetEventCount, item.AoeEventCount, item.WindowCount,
                item.EntireConstraintSinglePercentage, item.RoleConditionalMagnitude,
                item.ActivePercentageComposition, item.SelfPercentageComposition,
                item.SourceCache, string.Join(" | ", item.Warnings));
        }
        return builder.ToString();
    }

    private static string BuildWindowsCsv(PercentageAuditReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("report,fightId,encounter,providerType,providerActorId,providerActor,providerJob,recipientActorId,recipientActor,recipientJob,buffStatusId,buffName,windowStart,windowEnd,authoritativeMultiplier,eventCount,eligibleDamage,singlePercentageEvents,singlePercentageDamage,overlapEvents,overlapDamage,directEvents,periodicEvents,petEvents,aoeEvents,legacyContribution,authoritativeContribution,allActiveDenominatorContribution,selfStrippedBasisContribution,calculatedMultiplierContribution,activePercentageComposition,fflogsWindowReference");
        foreach (var item in report.Windows)
        {
            AppendCsv(builder,
                item.Report, item.FightId, item.Encounter, item.ProviderType, item.ProviderActorId,
                item.ProviderActor, item.ProviderJob, item.RecipientActorId, item.RecipientActor,
                item.RecipientJob, item.BuffStatusId, item.BuffName, item.WindowStart, item.WindowEnd,
                item.AuthoritativeMultiplier, item.EventCount, item.EligibleDamage,
                item.SinglePercentageEventCount, item.SinglePercentageDamage, item.OverlapEventCount,
                item.OverlapDamage, item.DirectEventCount, item.PeriodicEventCount,
                item.PetEventCount, item.AoeEventCount, item.LegacyContribution,
                item.AuthoritativeContribution, item.AllActiveDenominatorContribution,
                item.SelfStrippedBasisContribution, item.CalculatedMultiplierContribution,
                item.ActivePercentageComposition,
                item.FflogsWindowReference);
        }
        return builder.ToString();
    }

    private static string BuildReferenceCsv(PercentageAuditReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("report,fightId,providerActorId,providerActor,providerJob,buffStatusId,buffName,providerGiven,recipientTakenSum,delta,recipientCount,exact");
        foreach (var item in report.ReferenceAudit)
        {
            AppendCsv(builder, item.Report, item.FightId, item.ProviderActorId, item.ProviderActor,
                item.ProviderJob, item.BuffStatusId, item.BuffName, item.ProviderGiven,
                item.RecipientTakenSum, item.Delta, item.RecipientCount, item.Exact);
        }
        return builder.ToString();
    }

    private static string BuildStatisticsCsv(PercentageAuditReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("dimension,value,model,n,mean,median,mae,rmse,maxAbs,negative,exact,positive");
        foreach (var item in report.Statistics)
        {
            AppendStatisticsRow(builder, item.Dimension, item.Value, "CurrentProduction", item.CurrentProduction);
            AppendStatisticsRow(builder, item.Dimension, item.Value, "LegacyPublishedMath", item.LegacyMetadata);
            AppendStatisticsRow(builder, item.Dimension, item.Value, "AuthoritativeMetadata", item.AuthoritativeMetadata);
        }
        return builder.ToString();
    }

    private static void AppendStatisticsRow(
        StringBuilder builder,
        string dimension,
        string value,
        string model,
        MatrixResidualStatistics statistics)
        => AppendCsv(builder, dimension, value, model, statistics.ConstraintCount,
            statistics.MeanResidual, statistics.MedianResidual, statistics.MeanAbsoluteResidual,
            statistics.RootMeanSquareResidual, statistics.MaximumAbsoluteResidual,
            statistics.NegativeCount, statistics.ZeroCount, statistics.PositiveCount);

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

    private static string EscapeMarkdown(string value)
        => value.Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
}

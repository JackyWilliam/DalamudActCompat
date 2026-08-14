using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DalamudActCompat.FflogsParityHarness;

internal static class AttributionMatrixReportWriter
{
    public static async Task<AttributionMatrixReportPaths> WriteAsync(
        string outputDirectory,
        AttributionMatrixReport report,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputDirectory);
        var paths = new AttributionMatrixReportPaths(
            Path.Combine(outputDirectory, "attribution-matrix-report.json"),
            Path.Combine(outputDirectory, "attribution-matrix-summary.md"),
            Path.Combine(outputDirectory, "attribution-matrix.csv"),
            Path.Combine(outputDirectory, "attribution-matrix-constraints.csv"),
            Path.Combine(outputDirectory, "attribution-matrix-matched-pairs.csv"),
            Path.Combine(outputDirectory, "attribution-matrix-candidates.csv"),
            Path.Combine(outputDirectory, "offensive-buff-registry.csv"),
            Path.Combine(outputDirectory, "guaranteed-hit-registry.csv"));
        await File.WriteAllTextAsync(
            paths.JsonPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true,
            }),
            cancellationToken);
        await File.WriteAllTextAsync(paths.MatrixCsvPath, BuildMatrixCsv(report), cancellationToken);
        await File.WriteAllTextAsync(paths.ConstraintsCsvPath, BuildConstraintCsv(report), cancellationToken);
        await File.WriteAllTextAsync(paths.MatchedPairsCsvPath, BuildMatchedCsv(report), cancellationToken);
        await File.WriteAllTextAsync(paths.CandidateCsvPath, BuildCandidateCsv(report), cancellationToken);
        await File.WriteAllTextAsync(paths.OffensiveBuffRegistryCsvPath, BuildBuffRegistryCsv(report), cancellationToken);
        await File.WriteAllTextAsync(paths.GuaranteedHitRegistryCsvPath, BuildGuaranteedRegistryCsv(report), cancellationToken);
        await File.WriteAllTextAsync(paths.MarkdownPath, BuildMarkdown(report), cancellationToken);
        return paths;
    }

    private static string BuildMatrixCsv(AttributionMatrixReport report)
    {
        var builder = new StringBuilder();
        AppendCsv(builder,
            "buffDimension", "recipientHitType", "constraints", "fights", "events", "rawDamage",
            "providers", "recipientJobs", "actors", "encounters", "providerNames", "recipientJobNames",
            "referenceQuality", "candidate", "n", "meanResidual", "medianResidual", "mae", "rmse",
            "maxAbs", "negative", "zero", "positive");
        foreach (var cell in report.Matrix)
        {
            foreach (var (candidate, statistics) in cell.CandidateStatistics)
            {
                AppendCsv(builder,
                    cell.BuffDimension, cell.HitType, cell.ConstraintCount, cell.FightCount,
                    cell.EventCount, cell.RawDamage, cell.ProviderCount, cell.RecipientJobCount,
                    cell.ActorCount, cell.EncounterCount, cell.Providers, cell.RecipientJobs,
                    cell.ReferenceQuality, candidate, statistics.ConstraintCount,
                    statistics.MeanResidual, statistics.MedianResidual,
                    statistics.MeanAbsoluteResidual, statistics.RootMeanSquareResidual,
                    statistics.MaximumAbsoluteResidual, statistics.NegativeCount,
                    statistics.ZeroCount, statistics.PositiveCount);
            }
        }
        return builder.ToString();
    }

    private static string BuildConstraintCsv(AttributionMatrixReport report)
    {
        var candidates = GuaranteedHitCandidateMath.Definitions.Select(static item => item.Name).ToArray();
        var builder = new StringBuilder();
        var header = new List<object?>
        {
            "report", "fightId", "encounterId", "encounter", "partition", "partyComposition",
            "providerActorId", "providerActor", "providerJob", "recipientActorId", "recipientActor",
            "recipientJob", "buffStatusId", "buffName", "buffDimension", "magnitude",
            "fflogsReference", "referenceAvailability", "events", "rawDamage", "normalEvents",
            "normalRaw", "gCritEvents", "gCritRaw", "gDhEvents", "gDhRaw", "gCdhEvents",
            "gCdhRaw", "CuProxy", "DuProxy", "rateComposition", "sourceCache", "warnings",
        };
        header.AddRange(candidates.Select(static item => (object?)$"total:{item}"));
        header.AddRange(candidates.Select(static item => (object?)$"residual:{item}"));
        AppendCsv(builder, header.ToArray());
        foreach (var item in report.Constraints)
        {
            var values = new List<object?>
            {
                item.Report, item.FightId, item.EncounterId, item.Encounter, item.Partition,
                item.PartyComposition, item.ProviderActorId, item.ProviderActor, item.ProviderJob,
                item.RecipientActorId, item.RecipientActor, item.RecipientJob, item.BuffStatusId,
                item.BuffName, item.BuffDimension, item.Magnitude, item.FflogsReference,
                item.ReferenceAvailability, item.EventCount, item.RawDamage, item.NormalEventCount,
                item.NormalRawDamage, item.GuaranteedCriticalEventCount,
                item.GuaranteedCriticalRawDamage, item.GuaranteedDirectEventCount,
                item.GuaranteedDirectRawDamage, item.GuaranteedCriticalDirectEventCount,
                item.GuaranteedCriticalDirectRawDamage, item.CriticalChanceProxy,
                item.DirectChanceProxy, string.Join(" + ", item.ActiveRateComposition),
                item.SourceCache, string.Join(" | ", item.Warnings),
            };
            values.AddRange(candidates.Select(candidate => (object?)item.CandidateTotals[candidate]));
            values.AddRange(candidates.Select(candidate => (object?)item.CandidateResiduals[candidate]));
            AppendCsv(builder, values.ToArray());
        }
        return builder.ToString();
    }

    private static string BuildMatchedCsv(AttributionMatrixReport report)
    {
        var builder = new StringBuilder();
        AppendCsv(builder,
            "actor", "job", "partition", "quality", "qualityReason", "guaranteedHitType",
            "report", "fightId", "encounterId", "encounter", "partyComposition", "rateComposition",
            "CuProxy", "DuProxy", "buffDifference", "candidate", "fightResidual", "groupResidualRange");
        foreach (var group in report.MatchedGroups)
        {
            foreach (var fight in group.Fights)
            {
                foreach (var (candidate, residual) in fight.CandidateResiduals)
                {
                    AppendCsv(builder,
                        group.Actor, group.Job, group.Partition, group.MatchQuality,
                        group.QualityReason, group.GuaranteedHitType, fight.Report, fight.FightId,
                        fight.EncounterId, fight.Encounter, fight.PartyComposition,
                        fight.RateComposition, fight.CriticalChanceProxy, fight.DirectChanceProxy,
                        group.BuffDifference, candidate, residual,
                        group.CandidateResidualRange[candidate]);
                }
            }
        }
        return builder.ToString();
    }

    private static string BuildCandidateCsv(AttributionMatrixReport report)
    {
        var definitions = GuaranteedHitCandidateMath.Definitions.ToDictionary(static item => item.Name);
        var builder = new StringBuilder();
        AppendCsv(builder,
            "candidate", "family", "equation", "scopeDimension", "scopeValue", "n",
            "meanResidual", "medianResidual", "mae", "rmse", "maxAbs", "negative", "zero",
            "positive", "verdict", "verdictReason");
        foreach (var ranking in report.CandidateRankings)
        {
            AppendCandidateRow(builder, ranking, "overall", "all rate constraints", ranking.Overall,
                definitions[ranking.Candidate]);
            foreach (var scope in ranking.Scopes)
            {
                AppendCandidateRow(builder, ranking, scope.ScopeDimension, scope.ScopeValue,
                    scope.Statistics, definitions[ranking.Candidate]);
            }
        }
        return builder.ToString();
    }

    private static void AppendCandidateRow(
        StringBuilder builder,
        AttributionMatrixCandidateRanking ranking,
        string scopeDimension,
        string scopeValue,
        MatrixResidualStatistics statistics,
        GuaranteedHitCandidateDefinition definition)
        => AppendCsv(builder,
            ranking.Candidate, definition.Family, definition.Equation, scopeDimension, scopeValue,
            statistics.ConstraintCount, statistics.MeanResidual, statistics.MedianResidual,
            statistics.MeanAbsoluteResidual, statistics.RootMeanSquareResidual,
            statistics.MaximumAbsoluteResidual, statistics.NegativeCount, statistics.ZeroCount,
            statistics.PositiveCount, ranking.Verdict, ranking.VerdictReason);

    private static string BuildBuffRegistryCsv(AttributionMatrixReport report)
    {
        var builder = new StringBuilder();
        AppendCsv(builder,
            "providerJob", "actionIds", "actionName", "statusId", "dimension", "magnitude", "scope",
            "targeting", "partyWide", "singleTarget", "debuffOnEnemy", "selfAlsoAffected",
            "criticalRate", "directRate", "damageMultiplier", "officialSource", "gameVersion",
            "coveredByProduction", "idProvenance", "analysisNote");
        foreach (var item in report.OffensiveBuffRegistry)
        {
            AppendCsv(builder,
                item.ProviderJob, string.Join(';', item.ActionIds), item.ActionName, item.StatusId,
                item.Dimension, item.Magnitude, item.Scope, item.Targeting, item.PartyWide,
                item.SingleTarget, item.DebuffOnEnemy, item.SelfAlsoAffected,
                item.CriticalRateIncrease, item.DirectHitRateIncrease, item.DamageMultiplier,
                item.OfficialSource, item.GameVersion, item.CoveredByProduction,
                item.IdProvenance, item.AnalysisNote);
        }
        return builder.ToString();
    }

    private static string BuildGuaranteedRegistryCsv(AttributionMatrixReport report)
    {
        var builder = new StringBuilder();
        AppendCsv(builder,
            "job", "actionIds", "actionName", "condition", "conditionStatusId", "gCrit", "gDh",
            "gCdh", "officialSource", "gameVersion", "detectionSupport", "coveredByProduction");
        foreach (var item in report.GuaranteedHitRegistry)
        {
            var gCrit = item.Dimensions == ProbeGuaranteedDimensions.Critical;
            var gDh = item.Dimensions == ProbeGuaranteedDimensions.DirectHit;
            var gCdh = item.Dimensions ==
                       (ProbeGuaranteedDimensions.Critical | ProbeGuaranteedDimensions.DirectHit);
            AppendCsv(builder,
                item.Job, string.Join(';', item.ActionIds), item.ActionName, item.Condition,
                item.ConditionStatusId, gCrit, gDh, gCdh, item.OfficialSource, item.GameVersion,
                item.DetectionSupport, item.CoveredByProduction);
        }
        return builder.ToString();
    }

    private static string BuildMarkdown(AttributionMatrixReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Cross-provider Crit/DH attribution matrix");
        builder.AppendLine();
        builder.AppendLine($"Generated: {report.GeneratedAt:O}");
        builder.AppendLine();
        builder.AppendLine($"Equation status: **{report.EquationStatus}** — {report.EquationStatusReason}");
        builder.AppendLine();
        builder.AppendLine($"Evidence: {report.ExistingCachedFightCount} existing cached fights, " +
                           $"{report.TargetedCachedFightCount} targeted cached fights " +
                           $"({report.NewlyMinedFightCount} newly mined), {report.Constraints.Count} provider→recipient aggregates, " +
                           $"{report.MatchedGroups.Count} matched candidates (A/B identity-verified; C name-only).");
        builder.AppendLine();
        builder.AppendLine("> Provider and recipient are independent identities. SAM is a guaranteed-Crit recipient/damage actor in this experiment; it is not an offensive raid-buff provider.");
        builder.AppendLine();
        builder.AppendLine("## Authoritative-source boundary");
        builder.AppendLine();
        builder.AppendLine("- Current PvE buff and action behavior comes from the official FFXIV Job Guides linked in the registries. Patch 6.2 officially established extra game damage for guaranteed Crit/DH actions under matching rate effects: [official patch notes](https://na.finalfantasyxiv.com/lodestone/topics/detail/6eee1ca8a733856669d901d95d2fa9db46a466e6).");
        builder.AppendLine("- FFLogs publishes ordinary direct-hit and DoT rDPS allocation math: [FFLogs rDPS documentation](https://www.fflogs.com/help/rdps). It does not publish a guaranteed Crit/DH/CDH attribution branch.");
        builder.AppendLine("- The scripting model exposes active Crit/DH status-effect totals, but not per-event rDPS contribution or actual Cu/Du: [CalculatedDamageEvent](https://www.fflogs.com/scripting-api-docs/ff/interfaces/RpgLogs.CalculatedDamageEvent.html). The public GraphQL cache used here likewise contains no per-action contribution field.");
        builder.AppendLine();

        AppendBuffRegistry(builder, report);
        AppendGuaranteedRegistry(builder, report);
        AppendMatrix(builder, report);
        AppendMatched(builder, report);
        AppendCandidateRanking(builder, report);

        builder.AppendLine("## Cross-provider result");
        builder.AppendLine();
        foreach (var item in report.CrossProviderFindings)
        {
            builder.AppendLine($"- {item}");
        }
        builder.AppendLine();
        builder.AppendLine("No provider identity is treated as a fitted parameter. Candidate math receives only rate dimension, magnitude, hit properties, Cu/Du proxy, active-rate composition, and damage.");
        builder.AppendLine();

        builder.AppendLine("## Rejected candidates");
        builder.AppendLine();
        if (report.CandidatesRejected.Count == 0)
        {
            builder.AppendLine("No candidate is rejected from aggregate MAE alone.");
        }
        else
        {
            foreach (var item in report.CandidatesRejected)
            {
                builder.AppendLine($"- {item}");
            }
        }
        builder.AppendLine();

        AppendList(builder, "Remaining unknowns", report.RemainingUnknowns);
        AppendList(builder, "Minimum discriminating data", report.MinimumDataNeeds);
        AppendList(builder, "Evidence boundaries", report.KnownBoundaries);
        return builder.ToString();
    }

    private static void AppendBuffRegistry(StringBuilder builder, AttributionMatrixReport report)
    {
        builder.AppendLine("## A. Offensive Buff Registry");
        builder.AppendLine();
        builder.AppendLine("| Provider | Action / status | Dimension | Magnitude | Scope | Production | Official source |");
        builder.AppendLine("|---|---|---|---:|---|---:|---|");
        foreach (var item in report.OffensiveBuffRegistry)
        {
            builder.AppendLine($"| {item.ProviderJob} | {item.ActionName} / {item.StatusId} | {item.Dimension} | {item.Magnitude} | {item.Scope} | {(item.CoveredByProduction ? "yes" : "no")} | [Job Guide]({item.OfficialSource}) |");
        }
        builder.AppendLine();
        builder.AppendLine("SAM provider status: **NO**. The current official SAM guide exposes no relevant party offensive rate/percentage buff.");
        builder.AppendLine();
    }

    private static void AppendGuaranteedRegistry(StringBuilder builder, AttributionMatrixReport report)
    {
        builder.AppendLine("## B. Guaranteed Hit Registry");
        builder.AppendLine();
        builder.AppendLine("| Job | Action | Condition | G-Crit | G-DH | G-CDH | Detection | Official source |");
        builder.AppendLine("|---|---|---|---:|---:|---:|---|---|");
        foreach (var item in report.GuaranteedHitRegistry)
        {
            var gCrit = item.Dimensions == ProbeGuaranteedDimensions.Critical;
            var gDh = item.Dimensions == ProbeGuaranteedDimensions.DirectHit;
            var gCdh = item.Dimensions ==
                       (ProbeGuaranteedDimensions.Critical | ProbeGuaranteedDimensions.DirectHit);
            builder.AppendLine($"| {item.Job} | {item.ActionName} | {item.Condition} | {(gCrit ? "yes" : "")} | {(gDh ? "yes" : "")} | {(gCdh ? "yes" : "")} | {item.DetectionSupport} | [Job Guide]({item.OfficialSource}) |");
        }
        builder.AppendLine();
    }

    private static void AppendMatrix(StringBuilder builder, AttributionMatrixReport report)
    {
        builder.AppendLine("## C. Attribution Matrix");
        builder.AppendLine();
        builder.AppendLine("| Provider buff | Normal | G-Crit | G-DH | G-CDH |");
        builder.AppendLine("|---|---:|---:|---:|---:|");
        foreach (var dimension in Enum.GetValues<OffensiveBuffDimension>())
        {
            var cells = Enum.GetValues<RecipientHitType>()
                .Select(hit => report.Matrix.Single(item => item.BuffDimension == dimension && item.HitType == hit))
                .Select(static cell => cell.ConstraintCount == 0
                    ? "empty"
                    : $"{cell.FightCount}F/{cell.EventCount}E/{cell.RawDamage:N0} raw")
                .ToArray();
            builder.AppendLine($"| {dimension} | {string.Join(" | ", cells)} |");
        }
        builder.AppendLine();
        builder.AppendLine("Residual statistics in the CSV/JSON are whole provider→recipient aggregate residuals for constraints containing that hit type. FFLogs does not expose a per-hit-type split, so those rows are deliberately not presented as cell-local truth.");
        builder.AppendLine();
    }

    private static void AppendMatched(StringBuilder builder, AttributionMatrixReport report)
    {
        builder.AppendLine("## D. Matched-pair candidates");
        builder.AppendLine();
        builder.AppendLine("| Quality | Actor | Job | Hit type | Fights | Report:fight | Buff difference | Reason |");
        builder.AppendLine("|---|---|---|---|---:|---|---|---|");
        foreach (var item in report.MatchedGroups)
        {
            var fights = string.Join("; ", item.Fights.Select(static fight =>
                $"{fight.Report}:{fight.FightId}"));
            builder.AppendLine($"| {item.MatchQuality} | {item.Actor} | {item.Job} | {item.GuaranteedHitType} | {item.Fights.Count} | {fights} | {item.BuffDifference} | {item.QualityReason} |");
        }
        builder.AppendLine();
        builder.AppendLine("Per-fight identifiers and every candidate residual difference are in `attribution-matrix-matched-pairs.csv`. Only same-report actor-ID matches can receive A/B; cross-report name-only matches are C because the old cache lacks recipient server/world identity.");
        builder.AppendLine();
    }

    private static void AppendCandidateRanking(StringBuilder builder, AttributionMatrixReport report)
    {
        builder.AppendLine("## E. Percentage-damage control");
        builder.AppendLine();
        var control = report.PercentageControl.PublishedMathStatistics;
        builder.AppendLine($"Status: **{report.PercentageControl.Status}** — {report.PercentageControl.Reason}");
        builder.AppendLine();
        builder.AppendLine($"N={control.ConstraintCount}, mean={control.MeanResidual:F1}, " +
                           $"MAE={control.MeanAbsoluteResidual:F1}, RMSE={control.RootMeanSquareResidual:F1}, " +
                           $"max |residual|={control.MaximumAbsoluteResidual:F1} damage.");
        builder.AppendLine();

        builder.AppendLine("## F–H. Cross-provider candidate ranking and equation status");
        builder.AppendLine();
        builder.AppendLine("| Candidate | N | Mean | MAE | RMSE | Max abs | Verdict |");
        builder.AppendLine("|---|---:|---:|---:|---:|---:|---|");
        foreach (var item in report.CandidateRankings)
        {
            var stats = item.Overall;
            builder.AppendLine($"| {item.Candidate} | {stats.ConstraintCount} | {stats.MeanResidual:F1} | {stats.MeanAbsoluteResidual:F1} | {stats.RootMeanSquareResidual:F1} | {stats.MaximumAbsoluteResidual:F1} | {item.Verdict} |");
        }
        builder.AppendLine();
        builder.AppendLine("The candidate CSV additionally reports Crit-only, DH-only, Crit+DH, G-Crit, G-DH, G-CDH, provider-specific, and A/B matched scopes. Overall MAE is not an acceptance criterion.");
        builder.AppendLine();
        builder.AppendLine($"Final equation status: **{report.EquationStatus}**.");
        builder.AppendLine();
    }

    private static void AppendList(StringBuilder builder, string heading, IReadOnlyList<string> values)
    {
        builder.AppendLine($"## {heading}");
        builder.AppendLine();
        foreach (var item in values)
        {
            builder.AppendLine($"- {item}");
        }
        builder.AppendLine();
    }

    private static void AppendCsv(StringBuilder builder, params object?[] values)
        => builder.AppendLine(string.Join(',', values.Select(FormatCsv)));

    private static string FormatCsv(object? value)
    {
        var text = value switch
        {
            null => string.Empty,
            double number => number.ToString("0.###############", CultureInfo.InvariantCulture),
            float number => number.ToString("0.###############", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };
        return text.IndexOfAny([',', '"', '\r', '\n']) < 0
            ? text
            : $"\"{text.Replace("\"", "\"\"")}\"";
    }
}

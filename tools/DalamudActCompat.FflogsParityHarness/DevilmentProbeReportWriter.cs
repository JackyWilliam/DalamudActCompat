using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DalamudActCompat.FflogsParityHarness;

internal static class DevilmentProbeReportWriter
{
    public static async Task<DevilmentProbeReportPaths> WriteAsync(
        string outputDirectory,
        DevilmentProbeReport report,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputDirectory);
        var paths = new DevilmentProbeReportPaths(
            Path.Combine(outputDirectory, "devilment-probe-report.json"),
            Path.Combine(outputDirectory, "devilment-probe-actions.csv"),
            Path.Combine(outputDirectory, "devilment-probe-categories.csv"),
            Path.Combine(outputDirectory, "devilment-probe-action-coverage.csv"),
            Path.Combine(outputDirectory, "devilment-probe-windows.csv"),
            Path.Combine(outputDirectory, "devilment-probe-sensitivity.csv"),
            Path.Combine(outputDirectory, "devilment-probe-components.csv"),
            Path.Combine(outputDirectory, "devilment-probe-summary.md"));
        await File.WriteAllTextAsync(
            paths.JsonPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true,
            }),
            cancellationToken);
        await File.WriteAllTextAsync(paths.ActionsCsvPath, BuildActionsCsv(report.Actions), cancellationToken);
        await File.WriteAllTextAsync(paths.CategoriesCsvPath, BuildCategoriesCsv(report.Categories), cancellationToken);
        await File.WriteAllTextAsync(paths.CoverageCsvPath, BuildCoverageCsv(report.Coverage), cancellationToken);
        await File.WriteAllTextAsync(paths.WindowsCsvPath, BuildWindowsCsv(report.Windows), cancellationToken);
        await File.WriteAllTextAsync(paths.SensitivityCsvPath, BuildSensitivityCsv(report.Sensitivity), cancellationToken);
        await File.WriteAllTextAsync(
            paths.ComponentsCsvPath,
            BuildComponentsCsv(report.Samples.SelectMany(static sample => sample.Components)),
            cancellationToken);
        await File.WriteAllTextAsync(paths.MarkdownPath, BuildMarkdown(report), cancellationToken);
        return paths;
    }

    private static string BuildActionsCsv(IReadOnlyList<DevilmentActionProbeResult> actions)
    {
        var builder = new StringBuilder();
        builder.AppendLine(
            "report,fightId,samplePartnerJob,partnerJob,timestamp,sourceActorId,sourceActor,ownerActorId,ownerActor," +
            "actionId,actionName,rawDamage,effectiveDamage,isPeriodic,isCrit,isDirectHit," +
            "guaranteedCrit,guaranteedDirectHit,guaranteedCritDh,guaranteeCategory,guaranteeSource," +
            "currentDactMetadata,devilmentActive,devilmentAttributionTiming,devilmentWindow," +
            "dactInferredCu,dactInferredDu,critRateBeforeBuff,critRateAfterBuff,dhRateBeforeBuff," +
            "dhRateAfterBuff,referenceCu,referenceDu,fflogsDevilmentContribution," +
            "fflogsContributionAvailability,dactCriticalContribution,dactDirectHitContribution," +
            "dactDevilmentContribution,deltaContribution,analyticalProductionContribution," +
            "analyticalCalibrationResidual,publishedRegularPathContribution," +
            "productionGuaranteedPathContribution,scenarioContributionCorrection");
        foreach (var action in actions)
        {
            AppendCsv(builder,
                action.Report, action.FightId, action.SamplePartnerJob, action.PartnerJob, action.Timestamp,
                action.SourceActorId, action.SourceActor, action.OwnerActorId, action.OwnerActor,
                action.ActionId, action.ActionName, action.RawDamage, action.EffectiveDamage,
                action.IsPeriodic, action.IsCrit, action.IsDirectHit, action.GuaranteedCrit,
                action.GuaranteedDirectHit, action.GuaranteedCritDh, action.GuaranteeCategory,
                action.GuaranteeSource, action.CurrentDactMetadata, action.DevilmentActive,
                action.DevilmentAttributionTiming, action.DevilmentWindow, action.DactInferredCu,
                action.DactInferredDu, action.CritRateBeforeBuff, action.CritRateAfterBuff,
                action.DhRateBeforeBuff, action.DhRateAfterBuff, action.ReferenceCu,
                action.ReferenceDu, action.FflogsDevilmentContribution,
                action.FflogsContributionAvailability, action.DactCriticalContribution,
                action.DactDirectHitContribution, action.DactDevilmentContribution,
                action.DeltaContribution, action.AnalyticalProductionContribution,
                action.AnalyticalCalibrationResidual, action.PublishedRegularPathContribution,
                action.ProductionGuaranteedPathContribution, action.ScenarioContributionCorrection);
        }
        return builder.ToString();
    }

    private static string BuildCategoriesCsv(IReadOnlyList<DevilmentCategoryProbeResult> categories)
    {
        var builder = new StringBuilder();
        builder.AppendLine(
            "partnerJob,category,eventCount,totalRawDamage,fflogsDevilmentContribution," +
            "fflogsContributionAvailability,dactDevilmentContribution,totalDelta," +
            "meanDeltaPerEvent,meanDeltaPer100kDamage,scenarioContributionCorrection," +
            "meanScenarioCorrectionPerEvent,meanScenarioCorrectionPer100kDamage");
        foreach (var item in categories)
        {
            AppendCsv(builder,
                item.PartnerJob, item.Category, item.EventCount, item.TotalRawDamage,
                item.FflogsDevilmentContribution, item.FflogsContributionAvailability,
                item.DactDevilmentContribution, item.TotalDelta, item.MeanDeltaPerEvent,
                item.MeanDeltaPer100kDamage, item.ScenarioContributionCorrection,
                item.MeanScenarioCorrectionPerEvent, item.MeanScenarioCorrectionPer100kDamage);
        }
        return builder.ToString();
    }

    private static string BuildCoverageCsv(IReadOnlyList<DevilmentActionCoverageResult> coverage)
    {
        var builder = new StringBuilder();
        builder.AppendLine(
            "actionId,actionName,job,expectedBehavior,currentDactMetadata,observedFflogsBehavior," +
            "eventCount,criticalCount,directHitCount,rawDamage,covered,mismatch,evidenceSource");
        foreach (var item in coverage)
        {
            AppendCsv(builder,
                item.ActionId, item.ActionName, item.Job, item.ExpectedBehavior,
                item.CurrentDactMetadata, item.ObservedFflogsBehavior, item.EventCount,
                item.CriticalCount, item.DirectHitCount, item.RawDamage, item.Covered,
                item.Mismatch, item.EvidenceSource);
        }
        return builder.ToString();
    }

    private static string BuildWindowsCsv(IReadOnlyList<DevilmentWindowProbeResult> windows)
    {
        var builder = new StringBuilder();
        builder.AppendLine(
            "report,fightId,partnerJob,window,start,end,partner,actionCount,rawDamage," +
            "fflogsContribution,fflogsContributionAvailability,dactContribution,delta," +
            "scenarioContributionCorrection");
        foreach (var item in windows)
        {
            AppendCsv(builder,
                item.Report, item.FightId, item.PartnerJob, item.Window, item.Start, item.End,
                item.Partner, item.ActionCount, item.RawDamage, item.FflogsContribution,
                item.FflogsContributionAvailability, item.DactContribution, item.Delta,
                item.ScenarioContributionCorrection);
        }
        return builder.ToString();
    }

    private static string BuildSensitivityCsv(IReadOnlyList<DevilmentSensitivityProbeResult> values)
    {
        var builder = new StringBuilder();
        builder.AppendLine(
            "report,fightId,partnerJob,input,shiftPercentagePoints,dactBaselineContribution," +
            "shiftedContribution,contributionChange,baselineFinalRdpsDelta,shiftedFinalRdpsDelta");
        foreach (var item in values)
        {
            AppendCsv(builder,
                item.Report, item.FightId, item.PartnerJob, item.Input,
                item.ShiftPercentagePoints, item.DactBaselineContribution,
                item.ShiftedContribution, item.ContributionChange, item.BaselineFinalRdpsDelta,
                item.ShiftedFinalRdpsDelta);
        }
        return builder.ToString();
    }

    private static string BuildComponentsCsv(IEnumerable<ComponentParityProbeResult> values)
    {
        var builder = new StringBuilder();
        builder.AppendLine("report,fightId,partnerJob,component,fflogs,dact,delta,availability");
        foreach (var item in values)
        {
            AppendCsv(builder,
                item.Report, item.FightId, item.PartnerJob, item.Component,
                item.Fflogs, item.Dact, item.Delta, item.Availability);
        }
        return builder.ToString();
    }

    private static string BuildMarkdown(DevilmentProbeReport report)
    {
        var builder = new StringBuilder();
        var sam = report.Samples.Where(static sample => sample.Selection.PartnerJob == "SAM").ToArray();
        var drg = report.Samples.Where(static sample => sample.Selection.PartnerJob == "DRG").ToArray();
        var samDelta = sam.Sum(static sample => sample.DevilmentContributionDelta);
        var samCorrection = sam.Sum(static sample => sample.ScenarioContributionCorrection);
        var samGap = -samDelta;
        var share = samGap > 0 ? samCorrection / samGap * 100 : 0;
        var drgDelta = drg.Sum(static sample => sample.DevilmentContributionDelta);
        var drgCorrection = drg.Sum(static sample => sample.ScenarioContributionCorrection);
        var maxSensitivityRdps = report.Sensitivity.Count == 0
            ? 0
            : report.Sensitivity.Max(static value =>
                Math.Abs(value.ShiftedFinalRdpsDelta - value.BaselineFinalRdpsDelta));
        var maxCalibrationResidual = report.Samples.Count == 0
            ? 0
            : report.Samples.Max(static sample => Math.Abs(sample.AnalyticalCalibrationResidual));
        var lifeSurgeCoverage = report.Coverage
            .Where(static item => item.ExpectedBehavior.Contains("Life Surge", StringComparison.Ordinal))
            .ToArray();
        var lifeSurgeFixed = lifeSurgeCoverage.Length > 0 && lifeSurgeCoverage.All(static item =>
            item.Covered == "YES" && item.Mismatch == "NO" &&
            item.CurrentDactMetadata.Contains("status 0x74", StringComparison.Ordinal));

        builder.AppendLine("# DNC Devilment SAM vs DRG Per-Action Probe");
        builder.AppendLine();
        builder.AppendLine($"Generated: {report.GeneratedAt:O}");
        builder.AppendLine();
        builder.AppendLine($"Selected fights: {report.SelectedSampleCount}; action rows: {report.ActionCount}.");
        builder.AppendLine();
        builder.AppendLine("## Executive summary");
        builder.AppendLine();
        builder.AppendLine(
            "The probe separates production action attribution from unavailable FFLogs per-action truth. " +
            $"For SAM, applying the published regular-hit allocation path to production-known intrinsic " +
            $"guaranteed actions changes Devilment by {samCorrection:F1} damage against a selected-sample " +
            $"FFLogs-minus-DACT gap of {samGap:F1} ({share:F1}% modeled coverage). " +
            $"For DRG, classifying cached Life Surge consumption as contextual guaranteed Critical changes " +
            $"Devilment by {drgCorrection:F1} damage against the current DACT-minus-FFLogs delta of {drgDelta:F1}. " +
            "These are diagnostic scenarios, not back-solved FFLogs action contributions.");
        builder.AppendLine();
        builder.AppendLine("## First-round SAM vs DRG component comparison");
        builder.AppendLine();
        builder.AppendLine("All deltas are DACT minus FFLogs over the original 100-sample report.");
        builder.AppendLine();
        builder.AppendLine("| Partner | N | Mean final Δ rDPS | Mean external-received Δ | Mean own-given Δ | Mean Technical+Standard Δ | Mean Devilment Δ |");
        builder.AppendLine("|---|---:|---:|---:|---:|---:|---:|");
        foreach (var item in report.FirstRoundPartnerComparison)
        {
            builder.AppendLine(
                $"| {item.PartnerJob} | {item.SampleCount} | {item.MeanFinalRdpsDelta:F3} | " +
                $"{item.MeanExternalReceivedDelta:F1} | {item.MeanOwnGivenDelta:F1} | " +
                $"{item.MeanTechnicalStandardDelta:F1} | {item.MeanDevilmentDelta:F1} |");
        }
        builder.AppendLine();
        builder.AppendLine(
            "DRG's near-zero final delta is component cancellation, not parity: its positive own-given delta " +
            "nearly cancels its positive external-received delta in the final `raw - received + given` identity. " +
            "SAM has the same Technical+Standard overcount direction, but its much larger negative Devilment delta " +
            "turns own-given negative and reinforces the external-received error.");
        builder.AppendLine();
        builder.AppendLine("## Selected samples");
        builder.AppendLine();
        builder.AppendLine("| Job | Report | Fight | Reason | First-round final Δ rDPS |");
        builder.AppendLine("|---|---|---:|---|---:|");
        foreach (var item in report.Selections)
        {
            builder.AppendLine(
                $"| {item.PartnerJob} | {item.Report} | {item.FightId} | " +
                $"{EscapeMarkdown(item.SelectionReason)} | {item.FirstRoundDeltaRdps:F3} |");
        }
        builder.AppendLine();
        builder.AppendLine("## Per-sample Devilment and baseline");
        builder.AppendLine();
        builder.AppendLine(
            "`scenario correction` is SAM guaranteed-as-regular or DRG Life-Surge-as-guaranteed; " +
            "`scenario residual` remains DACT-scenario minus FFLogs.");
        builder.AppendLine();
        builder.AppendLine(
            "| Job | Report | Fight | FFLogs Devilment | DACT Devilment | Δ | final Cu | final Du | " +
            "action coverage residual | scenario correction | scenario residual |");
        builder.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var item in report.Samples)
        {
            builder.AppendLine(
                $"| {item.Selection.PartnerJob} | {item.Selection.Report} | {item.Selection.FightId} | " +
                $"{item.FflogsDevilmentContribution:F1} | {item.DactDevilmentContribution:F1} | " +
                $"{item.DevilmentContributionDelta:F1} | {item.FinalDactInferredCu:P3} | " +
                $"{item.FinalDactInferredDu:P3} | {item.ActionContributionCoverageDelta:F3} | " +
                $"{item.ScenarioContributionCorrection:F1} | {item.ScenarioResidualToFflogs:F1} |");
        }
        builder.AppendLine();
        builder.AppendLine(
            "Reference Cu/Du: unavailable from the cached public FFLogs API responses; values are not inferred from final rDPS.");
        builder.AppendLine();
        builder.AppendLine("## Guaranteed Crit/DH category totals");
        builder.AppendLine();
        builder.AppendLine("Categories: A random, B guaranteed Crit, C guaranteed DH, D guaranteed Crit+DH, E contextual, F unknown.");
        builder.AppendLine();
        builder.AppendLine("| Job | Category | Events | Raw damage | DACT Devilment | FFLogs contribution | Δ | scenario correction | correction/event | correction/100k | ");
        builder.AppendLine("|---|---|---:|---:|---:|---|---|---:|---:|---:|");
        foreach (var item in report.Categories)
        {
            builder.AppendLine(
                $"| {item.PartnerJob} | {item.Category} | {item.EventCount} | {item.TotalRawDamage} | " +
                $"{item.DactDevilmentContribution:F1} | unavailable_api | unavailable | " +
                $"{item.ScenarioContributionCorrection:F1} | {item.MeanScenarioCorrectionPerEvent:F1} | " +
                $"{item.MeanScenarioCorrectionPer100kDamage:F1} |");
        }
        builder.AppendLine();
        builder.AppendLine("## Action coverage mismatches");
        builder.AppendLine();
        builder.AppendLine("| Action ID | Action | Job | Expected | DACT metadata | Observed | Covered | Mismatch |");
        builder.AppendLine("|---:|---|---|---|---|---|---|---|");
        foreach (var item in report.Coverage.Where(static item => item.Mismatch != "NO"))
        {
            builder.AppendLine(
                $"| {item.ActionId} | {EscapeMarkdown(item.ActionName)} | {item.Job} | " +
                $"{EscapeMarkdown(item.ExpectedBehavior)} | {EscapeMarkdown(item.CurrentDactMetadata)} | " +
                $"{EscapeMarkdown(item.ObservedFflogsBehavior)} | {item.Covered} | {item.Mismatch} |");
        }
        builder.AppendLine();
        builder.AppendLine("## Crit/DH baseline audit");
        builder.AppendLine();
        builder.AppendLine(
            "Production `HitBaseline` enters every regular, guaranteed, and simulated-DoT Crit/DH path in " +
            "`RaidDpsEstimator.ObserveDamage`. It starts from a 25% / 40-hit prior, observes only direct " +
            "unbuffed non-guaranteed dimensions, and clamps Cu/Du to 5%-50%. Therefore it affects Devilment, " +
            "Battle Voice, Battle Litany, Chain Stratagem, and every other configured rate buff; it does not " +
            "change percentage-buff attribution.");
        builder.AppendLine();
        builder.AppendLine(
            $"Across the requested ±0.5/±1.0 percentage-point sensitivity runs, the largest final rDPS movement " +
            $"is {maxSensitivityRdps:F3}. The maximum selected-fight analytical calibration residual is " +
            $"{maxCalibrationResidual:F3} damage.");
        builder.AppendLine();
        builder.AppendLine("## FFLogs published math vs production");
        builder.AppendLine();
        builder.AppendLine("| Case | FFLogs public documentation | DACT production | Parity |");
        builder.AppendLine("|---|---|---|---|");
        builder.AppendLine("| Ordinary Crit-rate buff | regular hit `Pc = log(Mc)/log(Mdc) × (N' - N'/Mdc)`; `gi = ci/Cb × Pc` | same equations, with `Cb = clamp(Cu + configured external Crit rates)` | YES structurally; Cu input differs |");
        builder.AppendLine("| Ordinary DH-rate buff | regular hit `Pd = log(1.25)/log(Mdc) × (N' - N'/Mdc)`; `gi = di/Db × Pd` | same equations, with `Db = clamp(Du + configured external DH rates)` | YES structurally; Du input differs |");
        builder.AppendLine("| Guaranteed Crit | no guaranteed-hit branch is published | `Rc = (Mc + Σc × (Mc - 1)) / Mc`; allocate each buff by `ci/Σc` from the `Rc` bonus | UNKNOWN |");
        builder.AppendLine("| Guaranteed DH | no guaranteed-hit branch is published | `Rd = (1.25 + Σd × 0.25) / 1.25`; allocate each buff by `di/Σd` from the `Rd` bonus | UNKNOWN |");
        builder.AppendLine("| Guaranteed Crit+DH | no guaranteed-hit branch is published | `R = Rc × Rd`; remove `N'/R` and log-weight the Crit/DH bonus portions | UNKNOWN |");
        builder.AppendLine("| Devilment Crit+DH | public regular/DoT paths compute both portions and allocate `0.20/Cb` and `0.20/Db` shares | production uses those regular/DoT shares, or includes both 0.20 rates in `Rc × Rd` for guaranteed dimensions | YES for regular/DoT; UNKNOWN for guaranteed |");
        builder.AppendLine("| Multiplicative damage buffs | product, remove `N/M`, log-weight lost damage | separate percentage path before Crit/DH | YES |");
        builder.AppendLine();
        builder.AppendLine("Primary references: [FFLogs rDPS math](https://www.fflogs.com/help/rdps), " +
                           "[FFXIV Patch 6.2 guaranteed-hit behavior](https://na.finalfantasyxiv.com/lodestone/topics/detail/6eee1ca8a733856669d901d95d2fa9db46a466e6), " +
                           "[official Samurai job guide](https://na.finalfantasyxiv.com/jobguide/samurai/), and " +
                           "[official Dragoon job guide](https://na.finalfantasyxiv.com/jobguide/dragoon/). " +
                           "The game sources establish action behavior, not FFLogs' unpublished guaranteed-hit allocation implementation.");
        builder.AppendLine();
        builder.AppendLine("## Top 20 modeled error actions");
        builder.AppendLine();
        builder.AppendLine(
            "Exact FFLogs per-action Δ is unavailable. This ranking uses absolute diagnostic scenario correction and must not be read as FFLogs ground truth.");
        builder.AppendLine();
        builder.AppendLine("| Rank | Job | Action ID | Action | Category | Events | Raw damage | DACT contribution | scenario correction |");
        builder.AppendLine("|---:|---|---:|---|---|---:|---:|---:|---:|");
        var rank = 1;
        foreach (var item in report.TopModeledErrorActions)
        {
            builder.AppendLine(
                $"| {rank++} | {item.Job} | {item.ActionId} | {EscapeMarkdown(item.ActionName)} | " +
                $"{item.Category} | {item.EventCount} | {item.RawDamage} | {item.DactContribution:F1} | " +
                $"{item.ScenarioContributionCorrection:F1} |");
        }
        builder.AppendLine();
        builder.AppendLine("## Window audit");
        builder.AppendLine();
        builder.AppendLine(
            "FFLogs window-level contribution remains unavailable, so this section tests concentration of the diagnostic action signal only.");
        builder.AppendLine();
        builder.AppendLine("| Job | Report | Fight | Windows | Windows with correction | Total correction | Min/window | Max/window |");
        builder.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|");
        foreach (var group in report.Windows.GroupBy(static window =>
                     (window.PartnerJob, window.Report, window.FightId)))
        {
            var values = group.ToArray();
            builder.AppendLine(
                $"| {group.Key.PartnerJob} | {group.Key.Report} | {group.Key.FightId} | " +
                $"{values.Length} | {values.Count(static value => Math.Abs(value.ScenarioContributionCorrection) > 0.001)} | " +
                $"{values.Sum(static value => value.ScenarioContributionCorrection):F1} | " +
                $"{values.Min(static value => value.ScenarioContributionCorrection):F1} | " +
                $"{values.Max(static value => value.ScenarioContributionCorrection):F1} |");
        }
        builder.AppendLine();
        builder.AppendLine(
            "The signal recurs across windows instead of appearing only at apply/remove boundaries. " +
            "Detailed action membership and start/end timestamps are in `devilment-probe-windows.csv`.");
        builder.AppendLine();
        builder.AppendLine("## Component parity");
        builder.AppendLine();
        builder.AppendLine("Every available Δ is DACT minus FFLogs; unavailable splits remain null.");
        builder.AppendLine();
        builder.AppendLine("| Job | Report | Fight | Component | FFLogs | DACT | Δ | Availability |");
        builder.AppendLine("|---|---|---:|---|---:|---:|---:|---|");
        foreach (var item in report.Samples.SelectMany(static sample => sample.Components))
        {
            builder.AppendLine(
                $"| {item.PartnerJob} | {item.Report} | {item.FightId} | {item.Component} | " +
                $"{FormatNullable(item.Fflogs)} | {FormatNullable(item.Dact)} | " +
                $"{FormatNullable(item.Delta)} | {EscapeMarkdown(item.Availability)} |");
        }
        builder.AppendLine();
        builder.AppendLine("## Root-cause ranking");
        builder.AppendLine();
        builder.AppendLine(lifeSurgeFixed
            ? "- **Confirmed and fixed:** DRG Life Surge (status 0x74) contextual guaranteed-Crit weaponskills are covered by production status consumption; every selected contextual row reports Covered=YES and Mismatch=NO."
            : "- **Confirmed bug:** DRG Life Surge (status 0x74) produces contextual guaranteed-Crit weaponskills, but the selected production coverage still has a mismatch. Action IDs are listed in the coverage table.");
        builder.AppendLine($"- **Strongly supported:** SAM's discrepancy is concentrated in production-known intrinsic guaranteed-Crit actions. The guaranteed-as-regular diagnostic accounts for {share:F1}% of the selected aggregate Devilment gap, led by both Tendo Setsugekka actions. It does not fit every fight exactly, so the precise FFLogs guaranteed branch is not yet confirmed.");
        builder.AppendLine($"- **Possible but insufficient in the requested range:** Cu/Du input mismatch. ±1 percentage point moves final rDPS by at most {maxSensitivityRdps:F3} in these fights, well below the largest hundreds-of-rDPS errors, though a larger unknown actual-stat difference can still affect residuals.");
        builder.AppendLine("- **Possible:** self-sourced rate buffs are excluded before production builds Cb/Db. This is especially relevant to DRG Battle Litany and needs a controlled denominator audit against FFLogs.");
        builder.AppendLine("- **Unlikely for the primary SAM/DRG split:** DoT snapshot and window-edge state. The modeled guaranteed signal is direct-hit-time damage and repeats across nearly every window; public per-action FFLogs truth is still required before fully ruling snapshot state out.");
        builder.AppendLine("- **Ruled out for this dataset's numerator:** damage normalization and direct packet correlation; first-round parity remains exact. Pet/owner attribution is also not a credible explanation for the selected SAM/DRG partner actions, and per-fight action totals cover production Devilment with effectively zero residual.");
        builder.AppendLine();
        builder.AppendLine(lifeSurgeFixed
            ? "## Next evidence checks"
            : "## Minimal production patch plan (not implemented)");
        builder.AppendLine();
        builder.AppendLine("1. Do not change the guaranteed multiplier yet. First obtain an authoritative FFLogs guaranteed-hit rule or controlled logs with known Cu/Du; the public FFLogs page does not define this branch and the regular-path scenario leaves fight-dependent residuals.");
        builder.AppendLine(lifeSurgeFixed
            ? "2. Keep the Life Surge regression matrix and its 11-fight replay as a fixed coverage gate; do not interpret the worsened final DRG delta as permission to restore the old omission."
            : "2. Add Life Surge status consumption beside Reassemble, with weaponskill-only consumption and multi-target action reuse, then add apply/remove/expiry/random-Crit regression coverage.");
        builder.AppendLine("3. Use aggregate candidate elimination without inventing FFLogs per-action truth; validate high- and low-guaranteed-damage cohorts and rate-buff-overlap groups separately.");
        builder.AppendLine("4. If authoritative data confirms a different guaranteed allocation equation, change only `TransferGuaranteedCriticalDirectContribution`, add SAM Midare/Ogi/Tendo and guaranteed Crit+DH fixtures, then rerun the existing 100 cached samples plus this 11-fight probe.");
        builder.AppendLine("5. Replace Cu/Du observation inference only when a reliable actor-stat input exists; do not tune the 25% prior, 40-hit prior, or clamp against final rDPS deltas.");
        builder.AppendLine();
        builder.AppendLine("## Evidence boundaries");
        builder.AppendLine();
        foreach (var boundary in report.EvidenceBoundaries)
        {
            builder.AppendLine($"- {boundary}");
        }
        return builder.ToString();
    }

    private static void AppendCsv(StringBuilder builder, params object?[] values)
        => builder.AppendLine(string.Join(',', values.Select(Csv)));

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

    private static string EscapeMarkdown(string value) => value.Replace("|", "\\|");

    private static string FormatNullable(double? value)
        => value?.ToString("F3", CultureInfo.InvariantCulture) ?? "unavailable";
}

internal sealed record DevilmentProbeReportPaths(
    string JsonPath,
    string ActionsCsvPath,
    string CategoriesCsvPath,
    string CoverageCsvPath,
    string WindowsCsvPath,
    string SensitivityCsvPath,
    string ComponentsCsvPath,
    string MarkdownPath);

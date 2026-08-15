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
            Path.Combine(outputDirectory, "guaranteed-hit-residual-decomposition.csv"),
            Path.Combine(outputDirectory, "guaranteed-hit-candidate-scope-validation.csv"),
            Path.Combine(outputDirectory, "guaranteed-hit-actor-analysis.csv"),
            Path.Combine(outputDirectory, "guaranteed-hit-cohort-features.csv"),
            Path.Combine(outputDirectory, "guaranteed-hit-cohort-categories.csv"),
            Path.Combine(outputDirectory, "guaranteed-hit-partial-correlations.csv"),
            Path.Combine(outputDirectory, "guaranteed-hit-all-candidate-counterfactual.csv"),
            Path.Combine(outputDirectory, "guaranteed-hit-rate-buff-audit.csv"),
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
        await File.WriteAllTextAsync(paths.ResidualDecompositionCsvPath, BuildResidualDecompositionCsv(report), cancellationToken);
        await File.WriteAllTextAsync(paths.CandidateScopeCsvPath, BuildCandidateScopeCsv(report), cancellationToken);
        await File.WriteAllTextAsync(paths.ActorAnalysisCsvPath, BuildActorAnalysisCsv(report), cancellationToken);
        await File.WriteAllTextAsync(paths.CohortFeatureCsvPath, BuildCohortFeatureCsv(report), cancellationToken);
        await File.WriteAllTextAsync(paths.CohortCategoryCsvPath, BuildCohortCategoryCsv(report), cancellationToken);
        await File.WriteAllTextAsync(paths.PartialCorrelationCsvPath, BuildPartialCorrelationCsv(report), cancellationToken);
        await File.WriteAllTextAsync(paths.AllCandidateCounterfactualCsvPath, BuildAllCandidateCounterfactualCsv(report), cancellationToken);
        await File.WriteAllTextAsync(paths.RateBuffAuditCsvPath, BuildRateBuffAuditCsv(report), cancellationToken);
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
            "residualVsDirectProxy,residualVsRateBuffOverlap,residualVsGuaranteedRatio," +
            "residualVsTendoRatio,residualVsTendoKaeshiRatio,residualVsOgiRatio," +
            "residualVsSelfRate,residualVsExternalCrit,residualVsExternalDh," +
            "actorEtaSquared,encounterEtaSquared");
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
                stats.ResidualVsRateBuffOverlapCorrelation,
                stats.ResidualVsGuaranteedDamageRatioCorrelation,
                stats.ResidualVsTendoDamageRatioCorrelation,
                stats.ResidualVsTendoKaeshiDamageRatioCorrelation,
                stats.ResidualVsOgiDamageRatioCorrelation,
                stats.ResidualVsSelfRateExposureCorrelation,
                stats.ResidualVsExternalCriticalOverlapCorrelation,
                stats.ResidualVsExternalDirectOverlapCorrelation,
                stats.ActorEtaSquared, stats.EncounterEtaSquared);
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

    private static string BuildResidualDecompositionCsv(GuaranteedHitAttributionExperimentReport report)
    {
        var candidates = report.Candidates.Select(static item => item.Name).ToArray();
        var builder = new StringBuilder();
        object?[] fixedHeader =
        [
            "report", "fightId", "actor", "partnerActor", "encounter", "encounterId", "cohort",
            "duration", "partyComposition", "partnerTotalRawDamage", "guaranteedRawDamage",
            "guaranteedTotalRawRatio", "guaranteedDevilmentWindowRatio", "midareDamage",
            "tendoDamage", "tendoKaeshiDamage", "ogiDamage", "kaeshiNamikiriDamage",
            "kaeshiSetsugekkaDamage", "criticalChanceProxy", "directChanceProxy",
            "criticalChanceMinimum", "criticalChanceMaximum", "directChanceMinimum",
            "directChanceMaximum", "criticalRateBuffComposition", "directRateBuffComposition",
            "selfRateExposureFraction", "externalRateExposureFraction",
            "externalCriticalOverlapFraction", "externalDirectOverlapFraction",
            "rawWeightedSelfCriticalRate", "rawWeightedSelfDirectRate",
            "rawWeightedExternalCriticalRate", "rawWeightedExternalDirectRate",
            "fflogsDevilmentTotal", "productionDevilmentTotal", "currentProductionResidual",
            "observedHitRegularResidual", "unscaledObservedHitResidual",
        ];
        AppendCsv(builder, fixedHeader.Concat(candidates.Select(static candidate =>
            (object?)$"residual:{candidate}")).ToArray());
        foreach (var item in report.ResidualDecomposition)
        {
            var fixedValues = new object?[]
            {
                item.Report, item.FightId, item.Actor, item.PartnerActor, item.Encounter,
                item.EncounterId, item.Cohort, item.Duration, item.PartyComposition,
                item.PartnerTotalRawDamage, item.GuaranteedRawDamage, item.GuaranteedTotalRawRatio,
                item.GuaranteedDevilmentWindowRatio, item.MidareDamage, item.TendoDamage,
                item.TendoKaeshiDamage, item.OgiDamage, item.KaeshiNamikiriDamage,
                item.KaeshiSetsugekkaDamage, item.CriticalChanceProxy, item.DirectChanceProxy,
                item.CriticalChanceMinimum, item.CriticalChanceMaximum,
                item.DirectChanceMinimum, item.DirectChanceMaximum,
                item.CriticalRateBuffComposition, item.DirectRateBuffComposition,
                item.SelfRateExposureFraction, item.ExternalRateExposureFraction,
                item.ExternalCriticalOverlapFraction, item.ExternalDirectOverlapFraction,
                item.RawWeightedSelfCriticalRate, item.RawWeightedSelfDirectRate,
                item.RawWeightedExternalCriticalRate, item.RawWeightedExternalDirectRate,
                item.FflogsDevilmentTotal, item.ProductionDevilmentTotal,
                item.CurrentProductionResidual, item.ObservedHitRegularResidual,
                item.UnscaledObservedHitResidual,
            };
            AppendCsv(builder, fixedValues.Concat(candidates.Select(candidate =>
                (object?)item.CandidateResiduals[candidate])).ToArray());
        }
        return builder.ToString();
    }

    private static string BuildCandidateScopeCsv(GuaranteedHitAttributionExperimentReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine(
            "candidate,scope,unit,n,mean,median,mae,rmse,maxAbs,negative,zero,positive," +
            "corrDuration,corrGuaranteedRaw,corrGuaranteedRatio,corrCu,corrDu,corrRateOverlap," +
            "corrTendoRatio,corrTendoKaeshiRatio,corrOgiRatio,corrSelfRate,corrExternalCrit," +
            "corrExternalDh,repeatedActorEtaSquared,encounterEtaSquared");
        foreach (var item in report.CandidateScopeValidation)
        {
            var stats = item.Statistics;
            AppendCsv(builder,
                item.Candidate, item.Scope, item.Unit, stats.FightCount, stats.MeanResidual,
                stats.MedianResidual, stats.MeanAbsoluteResidual, stats.RootMeanSquareResidual,
                stats.MaximumAbsoluteResidual, stats.NegativeCount, stats.ZeroCount,
                stats.PositiveCount, stats.ResidualVsDurationCorrelation,
                stats.ResidualVsGuaranteedDamageCorrelation,
                stats.ResidualVsGuaranteedDamageRatioCorrelation,
                stats.ResidualVsCriticalProxyCorrelation, stats.ResidualVsDirectProxyCorrelation,
                stats.ResidualVsRateBuffOverlapCorrelation,
                stats.ResidualVsTendoDamageRatioCorrelation,
                stats.ResidualVsTendoKaeshiDamageRatioCorrelation,
                stats.ResidualVsOgiDamageRatioCorrelation,
                stats.ResidualVsSelfRateExposureCorrelation,
                stats.ResidualVsExternalCriticalOverlapCorrelation,
                stats.ResidualVsExternalDirectOverlapCorrelation,
                stats.ActorEtaSquared, stats.EncounterEtaSquared);
        }
        return builder.ToString();
    }

    private static string BuildActorAnalysisCsv(GuaranteedHitAttributionExperimentReport report)
    {
        var candidates = report.Candidates.Select(static item => item.Name).ToArray();
        var builder = new StringBuilder();
        object?[] fixedHeader =
        [
            "actor", "n", "encounterCount", "encounters", "currentMean", "observedMean",
            "unscaledMean", "cuMin", "cuMax", "duMin", "duMax", "guaranteedDamageRatio",
            "tendoRatio", "externalCritOverlap", "externalDhOverlap", "selfRateExposure",
            "rateBuffComposition",
        ];
        AppendCsv(builder, fixedHeader.Concat(candidates.Select(static candidate =>
            (object?)$"mean:{candidate}")).ToArray());
        foreach (var item in report.ActorAnalysis)
        {
            object?[] fixedValues =
            [
                item.Actor, item.FightCount, item.EncounterCount, item.Encounters,
                item.CurrentResidualMean, item.ObservedResidualMean, item.UnscaledResidualMean,
                item.CriticalChanceMinimum, item.CriticalChanceMaximum,
                item.DirectChanceMinimum, item.DirectChanceMaximum,
                item.GuaranteedDamageRatioMean, item.TendoRatioMean,
                item.ExternalCriticalOverlapMean, item.ExternalDirectOverlapMean,
                item.SelfRateExposureMean, item.RateBuffComposition,
            ];
            AppendCsv(builder, fixedValues.Concat(candidates.Select(candidate =>
                (object?)item.CandidateResidualMeans[candidate])).ToArray());
        }
        return builder.ToString();
    }

    private static string BuildCohortFeatureCsv(GuaranteedHitAttributionExperimentReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("cohort,feature,n,mean,median,p25,p75,min,max");
        foreach (var item in report.CohortFeatureDistributions)
        {
            AppendCsv(builder, item.Cohort, item.Feature, item.FightCount, item.Mean,
                item.Median, item.FirstQuartile, item.ThirdQuartile, item.Minimum, item.Maximum);
        }
        return builder.ToString();
    }

    private static string BuildCohortCategoryCsv(GuaranteedHitAttributionExperimentReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("cohort,dimension,value,n,fraction");
        foreach (var item in report.CohortCategoryDistributions)
        {
            AppendCsv(
                builder,
                item.Cohort,
                item.Dimension,
                item.Value,
                item.FightCount,
                item.Fraction);
        }
        return builder.ToString();
    }

    private static string BuildPartialCorrelationCsv(GuaranteedHitAttributionExperimentReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine(
            "candidate,scope,variable,n,withinActorN,withinEncounterN,raw," +
            "controlGuaranteedRatio,controlNumericInputs," +
            "withinActor,withinEncounter,fullControls");
        foreach (var item in report.PartialCorrelations)
        {
            AppendCsv(builder, item.Candidate, item.Scope, item.Variable, item.FightCount,
                item.WithinActorObservationCount, item.WithinEncounterObservationCount,
                item.RawCorrelation, item.ControllingGuaranteedDamageRatio,
                item.ControllingNumericInputs, item.WithinActorCorrelation,
                item.WithinEncounterCorrelation, item.FullControlsCorrelation);
        }
        return builder.ToString();
    }

    private static string BuildAllCandidateCounterfactualCsv(GuaranteedHitAttributionExperimentReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("candidate,partnerJob,n,mean,median,mae,rmse,maxAbs,negative,zero,positive");
        foreach (var item in report.AllCandidateCounterfactuals)
        {
            AppendCsv(builder, item.Candidate, item.PartnerJob, item.SampleCount,
                item.MeanDelta, item.MedianDelta, item.MeanAbsoluteDelta,
                item.RootMeanSquareDelta, item.MaximumAbsoluteDelta,
                item.NegativeCount, item.ZeroCount, item.PositiveCount);
        }
        return builder.ToString();
    }

    private static string BuildRateBuffAuditCsv(GuaranteedHitAttributionExperimentReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine(
            "abilityId,buff,criticalRate,directRate,sourceAndTarget,externalTargetProduction," +
            "selfTargetProduction,allowsSelfContribution,otherProviderDenominator,fflogsPublicRule");
        foreach (var item in report.RateBuffDenominatorAudit)
        {
            AppendCsv(builder, $"0x{item.AbilityId:X}", item.Buff, item.CriticalRate,
                item.DirectRate, item.SourceAndTarget, item.ExternalTargetProduction,
                item.SelfTargetProduction, item.AllowsSelfContribution,
                item.OtherProviderDenominator, item.FflogsPublicRule);
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
        builder.AppendLine("## Residual root-cause findings");
        builder.AppendLine();
        foreach (var item in report.ResidualFindings)
        {
            builder.AppendLine($"- {item}");
        }
        builder.AppendLine();
        builder.AppendLine("## Production rate-buff denominator audit");
        builder.AppendLine();
        builder.AppendLine("Production removes `source == damage actor` before it constructs Crit/DH arrays. Therefore self rates do not enter ordinary `Cb/Db`, guaranteed `C/D`, DoT snapshots, or another provider's denominator, and self contribution is rejected again at transfer time.");
        builder.AppendLine();
        builder.AppendLine("| Buff | ID | C/D | External carrier | Self carrier | Other-provider denominator | FFLogs public rule |");
        builder.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var item in report.RateBuffDenominatorAudit)
        {
            builder.AppendLine(
                $"| {item.Buff} | `0x{item.AbilityId:X}` | {item.CriticalRate:P0}/{item.DirectRate:P0} | " +
                $"{EscapeMarkdown(item.ExternalTargetProduction)} | {EscapeMarkdown(item.SelfTargetProduction)} | " +
                $"{EscapeMarkdown(item.OtherProviderDenominator)} | {EscapeMarkdown(item.FflogsPublicRule)} |");
        }
        builder.AppendLine();
        builder.AppendLine("## High 30 vs holdout 31 feature split");
        builder.AppendLine();
        builder.AppendLine("| Feature | High mean | Holdout mean | All-SAM mean |");
        builder.AppendLine("|---|---:|---:|---:|");
        foreach (var feature in report.CohortFeatureDistributions.Select(static item => item.Feature)
                     .Distinct(StringComparer.Ordinal))
        {
            var high = report.CohortFeatureDistributions.Single(item =>
                item.Cohort == "High-information 30" && item.Feature == feature);
            var holdout = report.CohortFeatureDistributions.Single(item =>
                item.Cohort == "Holdout 31" && item.Feature == feature);
            var all = report.CohortFeatureDistributions.Single(item =>
                item.Cohort == "All SAM 61" && item.Feature == feature);
            builder.AppendLine($"| {feature} | {high.Mean:F6} | {holdout.Mean:F6} | {all.Mean:F6} |");
        }
        builder.AppendLine();
        builder.AppendLine("## Candidate re-evaluation by scope");
        builder.AppendLine();
        var focusCandidates = new[]
            {
                GuaranteedHitCandidateMath.CurrentProduction,
                GuaranteedHitCandidateMath.ObservedHitRegular,
                GuaranteedHitCandidateMath.UnscaledObservedHit,
                GuaranteedHitCandidateMath.ObservedAllActiveDenominator,
                GuaranteedHitCandidateMath.ObservedExcludeSelfEverywhere,
                GuaranteedHitCandidateMath.UnscaledAllActiveDenominator,
                GuaranteedHitCandidateMath.UnscaledSelfScalingExternalDenominator,
                GuaranteedHitCandidateMath.OtherExternalOverlapObservedElseUnscaled,
                report.BestCandidate,
            }
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        builder.AppendLine("| Candidate | Scope | N | Mean | Median | MAE | RMSE | Max | Sign N/0/P | Corr gRaw | Corr Tendo ratio | Repeated-actor eta² |");
        builder.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---|---:|---:|---:|");
        foreach (var item in report.CandidateScopeValidation.Where(item =>
                     focusCandidates.Contains(item.Candidate, StringComparer.Ordinal)))
        {
            var stats = item.Statistics;
            builder.AppendLine(
                $"| `{item.Candidate}` | {item.Scope} | {stats.FightCount} | {stats.MeanResidual:F1} | " +
                $"{stats.MedianResidual:F1} | {stats.MeanAbsoluteResidual:F1} | " +
                $"{stats.RootMeanSquareResidual:F1} | {stats.MaximumAbsoluteResidual:F1} | " +
                $"{stats.NegativeCount}/{stats.ZeroCount}/{stats.PositiveCount} | " +
                $"{stats.ResidualVsGuaranteedDamageCorrelation:F3} | " +
                $"{stats.ResidualVsTendoDamageRatioCorrelation:F3} | {stats.ActorEtaSquared:F3} |");
        }
        builder.AppendLine();
        builder.AppendLine("Variants C/D in the Observed family and B/C in the Unscaled family are deliberately retained as duplicate rows: their equality is a result of the declared scopes, not missing output.");
        builder.AppendLine();
        builder.AppendLine("## Tendo partial-correlation audit");
        builder.AppendLine();
        builder.AppendLine("| Candidate | Scope | Variable | N | Actor N | Encounter N | Raw | Control g-ratio | Numeric controls | Within actor | Within encounter | Numeric + encounter controls |");
        builder.AppendLine("|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var item in report.PartialCorrelations.Where(static item =>
                     item.Candidate == GuaranteedHitCandidateMath.ObservedHitRegular &&
                     item.Variable is "Tendo raw damage" or "Tendo damage ratio" or
                         "Tendo Kaeshi raw damage" or "Tendo Kaeshi damage ratio"))
        {
            builder.AppendLine(
                $"| `{item.Candidate}` | {item.Scope} | {item.Variable} | {item.FightCount} | " +
                $"{item.WithinActorObservationCount} | {item.WithinEncounterObservationCount} | " +
                $"{item.RawCorrelation:F3} | " +
                $"{item.ControllingGuaranteedDamageRatio:F3} | {item.ControllingNumericInputs:F3} | " +
                $"{item.WithinActorCorrelation:F3} | {item.WithinEncounterCorrelation:F3} | " +
                $"{item.FullControlsCorrelation:F3} |");
        }
        builder.AppendLine();
        builder.AppendLine("## Actor-level audit");
        builder.AppendLine();
        builder.AppendLine("| Dancer actor | N | Encounters | Current mean | Observed mean | Unscaled mean | Cu range | Du range | gRatio | Tendo ratio | ext-Crit exposure | self exposure |");
        builder.AppendLine("|---|---:|---:|---:|---:|---:|---|---|---:|---:|---:|---:|");
        foreach (var item in report.ActorAnalysis.Where(static item => item.FightCount > 1))
        {
            builder.AppendLine(
                $"| {EscapeMarkdown(item.Actor)} | {item.FightCount} | {item.EncounterCount} | " +
                $"{item.CurrentResidualMean:F1} | {item.ObservedResidualMean:F1} | " +
                $"{item.UnscaledResidualMean:F1} | {item.CriticalChanceMinimum:P1}–{item.CriticalChanceMaximum:P1} | " +
                $"{item.DirectChanceMinimum:P1}–{item.DirectChanceMaximum:P1} | " +
                $"{item.GuaranteedDamageRatioMean:P1} | {item.TendoRatioMean:P1} | " +
                $"{item.ExternalCriticalOverlapMean:P1} | {item.SelfRateExposureMean:P1} |");
        }
        builder.AppendLine();
        builder.AppendLine("## Best-candidate acceptance checks");
        builder.AppendLine();
        builder.AppendLine("| Check | PASS | Evidence |");
        builder.AppendLine("|---|---|---|");
        foreach (var item in report.AcceptanceChecks.Where(item => item.Candidate == report.BestCandidate))
        {
            builder.AppendLine($"| {item.Check} | {(item.Passed ? "YES" : "NO")} | {EscapeMarkdown(item.Evidence)} |");
        }
        builder.AppendLine();
        builder.AppendLine("## All-100 counterfactual comparison");
        builder.AppendLine();
        builder.AppendLine("| Candidate | Group | N | Mean | Median | MAE | RMSE | Max | Sign N/0/P |");
        builder.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---|");
        foreach (var item in report.AllCandidateCounterfactuals.Where(item =>
                     focusCandidates.Contains(item.Candidate, StringComparer.Ordinal) &&
                     item.PartnerJob is "Overall" or "SAM" or "DRG"))
        {
            builder.AppendLine(
                $"| `{item.Candidate}` | {item.PartnerJob} | {item.SampleCount} | " +
                $"{item.MeanDelta:F3} | {item.MedianDelta:F3} | {item.MeanAbsoluteDelta:F3} | " +
                $"{item.RootMeanSquareDelta:F3} | {item.MaximumAbsoluteDelta:F3} | " +
                $"{item.NegativeCount}/{item.ZeroCount}/{item.PositiveCount} |");
        }
        builder.AppendLine();
        builder.AppendLine("## Authoritative-source audit");
        builder.AppendLine();
        builder.AppendLine("- [FFLogs official rDPS math](https://www.fflogs.com/help/rdps) publishes ordinary direct-hit and simulated-DoT Crit/DH allocation, but no guaranteed Crit/DH/CDH branch and no explicit self-source membership rule for `Cb/Db`.");
        builder.AppendLine("- [FFXIV Patch 6.2 Notes](https://na.finalfantasyxiv.com/lodestone/topics/detail/6eee1ca8a733856669d901d95d2fa9db46a466e6) and the [SAM Job Guide](https://na.finalfantasyxiv.com/jobguide/samurai/) confirm guaranteed-hit game damage scales under rate-increase effects; they do not define FFLogs attribution.");
        builder.AppendLine("- The [DNC](https://na.finalfantasyxiv.com/jobguide/dancer/), [DRG](https://na.finalfantasyxiv.com/jobguide/dragoon/), [SCH](https://na.finalfantasyxiv.com/jobguide/scholar/), and [BRD](https://na.finalfantasyxiv.com/jobguide/bard/) guides confirm each buff's source/target behavior, including self carriers.");
        builder.AppendLine("- The [FFLogs API](https://www.fflogs.com/api/docs) and [CalculatedDamageEvent scripting interface](https://www.fflogs.com/scripting-api-docs/ff/interfaces/RpgLogs.CalculatedDamageEvent.html) do not expose documented per-action rDPS allocation or actual Cu/Du in the cached GraphQL event shape.");
        builder.AppendLine("- Public RPGLogs repositories do not contain the server-side FFLogs rDPS engine; the guaranteed branch remains **Not publicly documented**.");
        builder.AppendLine();
        builder.AppendLine("## Minimum discriminating data, if another round is authorized");
        builder.AppendLine();
        builder.AppendLine("Do not expand the random DNC sample. The minimum useful set is 5–10 controlled fights: repeated same SAM actor/gear with Devilment-only versus Devilment+Crit-only overlap; a known actual Cu/Du pair across both conditions; DRG Life Surge with and without own Litany while Devilment is active; and independent guaranteed-DH and guaranteed-CDH cases. These separate overlap-state, actor-stat, self-scaling, and dimension-specific equations.");
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

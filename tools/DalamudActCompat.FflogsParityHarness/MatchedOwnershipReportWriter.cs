using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DalamudActCompat.FflogsParityHarness;

internal static class MatchedOwnershipReportWriter
{
    public static async Task<MatchedOwnershipReportPaths> WriteAsync(
        string outputDirectory,
        MatchedOwnershipReport report,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputDirectory);
        var jsonPath = Path.Combine(outputDirectory, "matched-ownership-report.json");
        var fightCsvPath = Path.Combine(outputDirectory, "matched-ownership-fights.csv");
        var pairCsvPath = Path.Combine(outputDirectory, "matched-ownership-pairs.csv");
        var rankingCsvPath = Path.Combine(outputDirectory, "matched-ownership-rankings.csv");
        var markdownPath = Path.Combine(outputDirectory, "matched-ownership-mining-2026-08-14.md");
        await File.WriteAllTextAsync(
            jsonPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);
        await File.WriteAllTextAsync(fightCsvPath, BuildFightCsv(report), cancellationToken);
        await File.WriteAllTextAsync(pairCsvPath, BuildPairCsv(report), cancellationToken);
        await File.WriteAllTextAsync(rankingCsvPath, BuildRankingCsv(report), cancellationToken);
        await File.WriteAllTextAsync(markdownPath, BuildMarkdown(report), cancellationToken);
        return new MatchedOwnershipReportPaths(
            jsonPath,
            fightCsvPath,
            pairCsvPath,
            rankingCsvPath,
            markdownPath);
    }

    private static string BuildMarkdown(MatchedOwnershipReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# FFLogs same-actor matched ownership mining — 2026-08-14");
        builder.AppendLine();
        builder.AppendLine($"Ownership status: **{report.OwnershipStatus}**");
        builder.AppendLine();
        builder.AppendLine("This run is diagnostic-only. It uses the production two-second causal " +
                           "status lifecycle and does not modify ownership or guaranteed-hit math.");
        builder.AppendLine();
        builder.AppendLine("## A. Mining Summary");
        builder.AppendLine();
        builder.AppendLine("| Metric | Count |");
        builder.AppendLine("|---|---:|");
        builder.AppendLine($"| API candidates scanned | {report.Mining.ApiCandidatesScanned} |");
        builder.AppendLine($"| Ranking pages read | {report.Mining.RankingPagesRead} |");
        builder.AppendLine($"| Metadata report/fight preflights | {report.Mining.MetadataPreflights} |");
        builder.AppendLine($"| Full fights fetched/replayed | {report.Fights.Count} |");
        builder.AppendLine($"| Imported existing ranking cache hits | {report.Mining.ImportedCacheHits} |");
        builder.AppendLine($"| API-client cache hits | {report.Mining.ApiCacheHits} |");
        builder.AppendLine($"| New API requests | {report.Mining.NewApiRequests} |");
        builder.AppendLine($"| Unique cached API responses | {report.Mining.UniqueCachedApiResponses} |");
        builder.AppendLine($"| Matched actors | {report.Actors.Count} |");
        builder.AppendLine($"| Grade A pairs | {report.GradeAGroupCount} |");
        builder.AppendLine($"| Grade B pairs | {report.GradeBGroupCount} |");
        builder.AppendLine($"| Grade C pairs | {report.GradeCGroupCount} |");
        builder.AppendLine();
        builder.AppendLine("A ranking page is a cheap candidate scan. A metadata preflight is read " +
                           "before full events; only groups that add a new rate dimension and form " +
                           "a same-actor contrast proceed to event collection.");
        builder.AppendLine();
        builder.AppendLine("## B. Actor Summary");
        builder.AppendLine();
        builder.AppendLine("| Actor | Job | World | Canonical ID | Partition | Fights | Exposure dimensions | Best grade | Identity | Days span |");
        builder.AppendLine("|---|---|---|---:|---:|---:|---|---|---|---:|");
        foreach (var actor in report.Actors)
        {
            builder.AppendLine($"| {Md(actor.Actor)} | {actor.Job} | {Md(actor.World)} | " +
                               $"{actor.CanonicalId?.ToString(CultureInfo.InvariantCulture) ?? "unavailable"} | " +
                               $"{actor.Partition} | {actor.FightCount} | {Md(actor.ExposureDimensions)} | " +
                               $"{actor.BestMatchGrade} | {Md(actor.IdentitySource)} | " +
                               $"{actor.MaximumDaysBetween:F1} |");
        }
        builder.AppendLine();
        builder.AppendLine(report.Actors.All(static item => item.CanonicalId is not null)
            ? "FFLogs returned `Character.canonicalID` for every selected actor; the report still retains world, region, job, and partition as identity checks."
            : "`canonicalID` is preferred when FFLogs returns a Character object. The fallback identity includes the numeric server ID and is independently checked against `report actor.server`.");
        builder.AppendLine();
        builder.AppendLine("## C. Matched Groups");
        builder.AppendLine();
        builder.AppendLine("Each row holds the percentage buff identity fixed across fights. Residual " +
                           "shift is `(prediction - FFLogs)_B - (prediction - FFLogs)_A`; the smallest " +
                           "absolute shift wins that matched provider-component pair.");
        builder.AppendLine();
        builder.AppendLine("| Grade | Actor | Percentage component | Fight A | Fight B | Exposure A → B | Changed | Days | Current | BaseLog | Shapley | Shapley3 | FFLogs observed shift | Winner | Confidence | Max discriminator |");
        builder.AppendLine("|---|---|---|---|---|---|---|---:|---:|---:|---:|---:|---:|---|---|---|");
        foreach (var pair in report.MatchedGroups)
        {
            builder.AppendLine($"| {pair.Grade} | {Md(pair.Actor)} ({pair.Job}) | " +
                               $"{pair.ProviderJob}:{Md(pair.BuffName)}#{pair.BuffStatusId} | {pair.FightA} | " +
                               $"{pair.FightB} | {Md(pair.ExposureA)} → {Md(pair.ExposureB)} | " +
                               $"{Md(pair.ChangedDimension)} | {pair.DaysBetween:F1} | " +
                               $"{R(pair, MatchedOwnershipCandidates.CurrentProduction):F3} | " +
                               $"{R(pair, MatchedOwnershipCandidates.SharedBaseLog):F3} | " +
                               $"{R(pair, MatchedOwnershipCandidates.SharedShapley):F3} | " +
                               $"{R(pair, MatchedOwnershipCandidates.SharedShapley3):F3} | " +
                               $"{pair.FflogsObservedShift:F3} | {pair.Winner} | {Md(pair.Confidence)} | " +
                               $"{Md(pair.MaximumDiscriminatorPair)} Δ={pair.MaximumCandidateSeparation:F3} |");
        }
        builder.AppendLine();
        builder.AppendLine("Candidate prediction shifts are computed from causal event replay before " +
                           "the FFLogs aggregate is subtracted. The `WhyUseful` selection reason for " +
                           "every fetched fight is retained in the JSON and fight CSV.");
        builder.AppendLine();
        builder.AppendLine("## D. DH Evidence");
        builder.AppendLine();
        builder.AppendLine(report.HasEnoughDhOnlyEvidence ? "**YES**" : "**NO**");
        builder.AppendLine();
        builder.AppendLine($"A/B same-actor component pairs involving DH-only: {report.MatchedGroups.Count(static item => item.Grade is "A" or "B" && (Base(item.ExposureA) == "DH-only" || Base(item.ExposureB) == "DH-only"))}. " +
                           "The dimension-evidence gate requires at least two actors and two " +
                           "normal-direct component pairs; the final ownership gate separately " +
                           "requires A-grade replication.");
        builder.AppendLine();
        builder.AppendLine("## E. Crit+DH Evidence");
        builder.AppendLine();
        builder.AppendLine(report.HasEnoughCriticalDirectEvidence ? "**YES**" : "**NO**");
        builder.AppendLine();
        builder.AppendLine($"A/B same-actor component pairs involving Crit+DH: {report.MatchedGroups.Count(static item => item.Grade is "A" or "B" && (Base(item.ExposureA) == "Crit+DH" || Base(item.ExposureB) == "Crit+DH"))}. " +
                           "Guaranteed-CDH recipients remain downgraded when normal and guaranteed " +
                           "aggregate contributions cannot be separated.");
        builder.AppendLine();
        builder.AppendLine("## F. Candidate Ranking");
        builder.AppendLine();
        builder.AppendLine("| Scope | Rank | Candidate | N | Mean | Median | MAE | RMSE | Max | Sign -/0/+ |");
        builder.AppendLine("|---|---:|---|---:|---:|---:|---:|---:|---:|---|");
        foreach (var item in report.Rankings.OrderBy(static item => ScopeOrder(item.Scope))
                     .ThenBy(static item => item.Rank))
        {
            builder.AppendLine($"| {item.Scope} | {item.Rank} | {item.Candidate} | {item.N} | " +
                               $"{item.Mean:F3} | {item.Median:F3} | {item.MeanAbsolute:F3} | " +
                               $"{item.RootMeanSquare:F3} | {item.MaximumAbsolute:F3} | " +
                               $"{item.NegativeCount}/{item.ZeroCount}/{item.PositiveCount} |");
        }
        builder.AppendLine();
        builder.AppendLine("## G. Ownership Status");
        builder.AppendLine();
        builder.AppendLine($"**{report.OwnershipStatus}**");
        builder.AppendLine();
        builder.AppendLine($"SharedBaseLog vs SharedShapley3 identifiable with mined A/B evidence: " +
                           $"**{(report.SharedBaseLogVsShapley3Identifiable ? "YES" : "NO")}**.");
        builder.AppendLine();
        foreach (var finding in report.Findings)
        {
            builder.AppendLine($"- {finding}");
        }
        builder.AppendLine();
        builder.AppendLine("The status is not promoted from overall MAE. Promotion requires one " +
                           "parameter-free candidate to win A-grade, A+B, normal-direct, Crit-only, " +
                           "DH-only, Crit+DH, multiple-provider, cross-actor, and cross-encounter scopes.");
        builder.AppendLine();
        builder.AppendLine("## H. Next Step");
        builder.AppendLine();
        builder.AppendLine(report.MinimumGap);
        builder.AppendLine();
        builder.AppendLine("## Limitations");
        builder.AppendLine();
        foreach (var limitation in report.Limitations)
        {
            builder.AppendLine($"- {limitation}");
        }
        return builder.ToString();
    }

    private static string BuildFightCsv(MatchedOwnershipReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("identityKey,canonicalId,actor,job,world,region,partition,report,fightId,encounterId,encounter,absoluteStartTime,partyComposition,exposureDimension,rateComposition,percentageComposition,criticalProviders,directProviders,separateProviders,fflogsPercentageReference,currentPrediction,rateFirstPrediction,baseLogPrediction,shapleyPrediction,shapley3Prediction,currentResidual,rateFirstResidual,baseLogResidual,shapleyResidual,shapley3Residual,constraintCount,eventCount,directEventCount,periodicEventCount,guaranteedCritEventCount,guaranteedDhEventCount,guaranteedCdhEventCount,isNormalDirect,combatantInfoEvidence,whyUseful");
        foreach (var item in report.Fights)
        {
            var columns = new List<string>
            {
                Csv(item.IdentityKey),
                item.CanonicalId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                Csv(item.Actor), item.Job, Csv(item.World), item.Region,
                item.Partition.ToString(CultureInfo.InvariantCulture), item.Report,
                item.FightId.ToString(CultureInfo.InvariantCulture),
                item.EncounterId.ToString(CultureInfo.InvariantCulture), Csv(item.Encounter),
                item.AbsoluteStartTime.ToString(CultureInfo.InvariantCulture), Csv(item.PartyComposition),
                Csv(item.ExposureDimension), Csv(item.RateComposition), Csv(item.PercentageComposition),
                item.CriticalProviderCount.ToString(CultureInfo.InvariantCulture),
                item.DirectHitProviderCount.ToString(CultureInfo.InvariantCulture),
                item.SeparateCriticalDirectProviders.ToString(), F(item.FflogsPercentageReference),
            };
            columns.AddRange(MatchedOwnershipCandidates.All.Select(candidate =>
                F(item.CandidatePredictions[candidate])));
            columns.AddRange(MatchedOwnershipCandidates.All.Select(candidate =>
                F(item.CandidateResiduals[candidate])));
            columns.AddRange(
            [
                item.ConstraintCount.ToString(CultureInfo.InvariantCulture),
                item.EventCount.ToString(CultureInfo.InvariantCulture),
                item.DirectEventCount.ToString(CultureInfo.InvariantCulture),
                item.PeriodicEventCount.ToString(CultureInfo.InvariantCulture),
                item.GuaranteedCriticalEventCount.ToString(CultureInfo.InvariantCulture),
                item.GuaranteedDirectHitEventCount.ToString(CultureInfo.InvariantCulture),
                item.GuaranteedCriticalDirectHitEventCount.ToString(CultureInfo.InvariantCulture),
                item.IsNormalDirect.ToString(), Csv(item.CombatantInfoEvidence), Csv(item.WhyUseful),
            ]);
            builder.AppendLine(string.Join(',', columns));
        }
        return builder.ToString();
    }

    private static string BuildPairCsv(MatchedOwnershipReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("identityKey,actor,job,world,partition,providerJob,buffStatusId,buffName,fightA,fightB,encounterA,encounterB,exposureA,exposureB,changedDimension,daysBetween,sameReport,sameEncounter,samePercentageComposition,matchScore,grade,fflogsObservedShift,currentResidualShift,rateFirstResidualShift,baseLogResidualShift,shapleyResidualShift,shapley3ResidualShift,maximumDiscriminatorPair,maximumCandidateSeparation,winner,confidence,isCleanDirectNormal,guaranteedEquationConfounded,multipleProviderExposure");
        foreach (var item in report.MatchedGroups)
        {
            var columns = new List<string>
            {
                Csv(item.IdentityKey), Csv(item.Actor), item.Job, Csv(item.World),
                item.Partition.ToString(CultureInfo.InvariantCulture), item.ProviderJob,
                item.BuffStatusId.ToString(CultureInfo.InvariantCulture), Csv(item.BuffName),
                item.FightA, item.FightB,
                item.EncounterA.ToString(CultureInfo.InvariantCulture),
                item.EncounterB.ToString(CultureInfo.InvariantCulture), Csv(item.ExposureA),
                Csv(item.ExposureB), Csv(item.ChangedDimension), F(item.DaysBetween),
                item.SameReport.ToString(), item.SameEncounter.ToString(),
                item.SamePercentageComposition.ToString(),
                item.MatchScore.ToString(CultureInfo.InvariantCulture), item.Grade,
                F(item.FflogsObservedShift),
            };
            columns.AddRange(MatchedOwnershipCandidates.All.Select(candidate =>
                F(item.CandidateResidualShifts[candidate])));
            columns.AddRange(
            [
                Csv(item.MaximumDiscriminatorPair), F(item.MaximumCandidateSeparation), item.Winner,
                Csv(item.Confidence), item.IsCleanDirectNormal.ToString(),
                item.GuaranteedEquationConfounded.ToString(), item.MultipleProviderExposure.ToString(),
            ]);
            builder.AppendLine(string.Join(',', columns));
        }
        return builder.ToString();
    }

    private static string BuildRankingCsv(MatchedOwnershipReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("scope,rank,candidate,n,mean,median,mae,rmse,max,negative,zero,positive");
        foreach (var item in report.Rankings)
        {
            builder.AppendLine(string.Join(",",
                Csv(item.Scope), item.Rank, item.Candidate, item.N, F(item.Mean), F(item.Median),
                F(item.MeanAbsolute), F(item.RootMeanSquare), F(item.MaximumAbsolute),
                item.NegativeCount, item.ZeroCount, item.PositiveCount));
        }
        return builder.ToString();
    }

    private static double R(MatchedOwnershipComponentPairResult pair, string candidate)
        => pair.CandidateResidualShifts[candidate];

    private static string Base(string value) => value.Split(';', 2)[0];

    private static int ScopeOrder(string value)
        => value switch
        {
            "A-grade matched" => 0,
            "A+B matched" => 1,
            "normal-direct" => 2,
            "Crit-only" => 3,
            "DH-only" => 4,
            "Crit+DH" => 5,
            "multiple provider" => 6,
            _ => 7,
        };

    private static string F(double value) => value.ToString("F6", CultureInfo.InvariantCulture);

    private static string Csv(object? value)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        return $"\"{text.Replace("\"", "\"\"")}\"";
    }

    private static string Md(string value) => value.Replace("|", "\\|");
}

internal sealed record MatchedOwnershipReportPaths(
    string JsonPath,
    string FightCsvPath,
    string PairCsvPath,
    string RankingCsvPath,
    string MarkdownPath);

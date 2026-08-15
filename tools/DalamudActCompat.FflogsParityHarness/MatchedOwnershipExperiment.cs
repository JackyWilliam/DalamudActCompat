using System.Text.Json;

namespace DalamudActCompat.FflogsParityHarness;

internal static class MatchedOwnershipExperiment
{
    private const double DisplayTolerance = 0.05;

    public static MatchedOwnershipReport Run(MatchedOwnershipManifest manifest)
    {
        var samples = manifest.Samples.Select(static item =>
            (Sample: new CachedFightSample(
                item.Preflight.Ranking.Seed,
                item.Preflight.MetadataPath,
                item.EventPaths),
             Source: "matched-ownership-cache")).ToArray();
        var constraints = PercentageIdentificationExperiment.AnalyzeSamples(samples);
        var fights = BuildFightResults(manifest.Samples, constraints);
        var pairs = BuildPairs(fights);
        var components = BuildComponents(manifest.Samples, constraints);
        var matchedGroups = BuildComponentPairs(components);
        var rankings = BuildRankings(matchedGroups);
        var actors = BuildActorSummaries(manifest.Samples, fights, matchedGroups);
        var gradeA = matchedGroups.Count(static item => item.Grade == "A");
        var gradeB = matchedGroups.Count(static item => item.Grade == "B");
        var gradeC = matchedGroups.Count(static item => item.Grade == "C");
        var dhPairs = matchedGroups.Where(static item =>
            item.Grade is "A" or "B" &&
            (BaseDimension(item.ExposureA) == "DH-only" ||
             BaseDimension(item.ExposureB) == "DH-only")).ToArray();
        var criticalDirectPairs = matchedGroups.Where(static item =>
            item.Grade is "A" or "B" &&
            (BaseDimension(item.ExposureA) == "Crit+DH" ||
             BaseDimension(item.ExposureB) == "Crit+DH")).ToArray();
        var enoughDh = dhPairs.Select(static item => item.IdentityKey).Distinct().Count() >= 2 &&
                       dhPairs.Count(static item => item.IsCleanDirectNormal) >= 2;
        var enoughCriticalDirect = criticalDirectPairs.Select(static item => item.IdentityKey)
                                       .Distinct().Count() >= 2 &&
                                   criticalDirectPairs.Count(static item =>
                                       item.IsCleanDirectNormal) >= 2;
        var identifiable = matchedGroups.Where(static item => item.Grade is "A" or "B")
            .Any(pair => Math.Abs(
                pair.CandidatePredictionShifts[MatchedOwnershipCandidates.SharedBaseLog] -
                pair.CandidatePredictionShifts[MatchedOwnershipCandidates.SharedShapley3]) >
                DisplayTolerance);
        var ownershipStatus = ResolveOwnershipStatus(
            matchedGroups,
            rankings,
            enoughDh,
            enoughCriticalDirect);
        var minimumGap = BuildMinimumGap(matchedGroups, enoughDh, enoughCriticalDirect);
        return new MatchedOwnershipReport(
            DateTimeOffset.UtcNow,
            manifest,
            actors,
            fights,
            pairs,
            components,
            matchedGroups,
            rankings,
            gradeA,
            gradeB,
            gradeC,
            enoughDh,
            enoughCriticalDirect,
            identifiable,
            ownershipStatus,
            minimumGap,
            BuildFindings(fights, matchedGroups, rankings, identifiable),
            [
                actors.All(static item => item.CanonicalId is not null)
                    ? "FFLogs Character.canonicalID resolved for every selected actor and is the cross-report identity key. Name, world, region, job, and partition are retained as verification fields."
                    : "Where FFLogs characterData returned no Character object, identity falls back to name + numeric server ID + region + job + partition and is independently verified against report actor.server; it is stronger than name-only but lacks Character.canonicalID.",
                "FFLogs exposes percentage truth at recipient/provider/fight aggregate. Difference-in-differences reduces actor and gear fixed effects, but encounter and rotation differences remain for grade-B pairs.",
                "Guaranteed recipient rows retain the current hidden guaranteed equation unchanged. They are cross-validation only unless the pair is also classified normal-direct.",
                "Combatant-info packets are cached when FFLogs emits them. Missing packets are reported as unavailable; no gear, item level, Cu, or Du value is invented.",
            ]);
    }

    private static IReadOnlyList<MatchedOwnershipFightResult> BuildFightResults(
        IReadOnlyList<MatchedOwnershipSample> samples,
        IReadOnlyList<PercentageIdentificationConstraintRow> constraints)
    {
        var result = new List<MatchedOwnershipFightResult>();
        foreach (var sample in samples)
        {
            var preflight = sample.Preflight;
            var identity = sample.Identity;
            var rows = constraints.Where(item =>
                    item.Report == preflight.Ranking.Seed.ReportCode &&
                    item.FightId == preflight.Ranking.Seed.FightId &&
                    item.RecipientActorId == preflight.RecipientActorId)
                .ToArray();
            if (rows.Length == 0)
            {
                continue;
            }

            // Predictions are derived only from causal packet replay. The aggregate
            // FFLogs reference is read afterwards so it cannot influence selection or math.
            var predictions = new Dictionary<string, double>(StringComparer.Ordinal)
            {
                [MatchedOwnershipCandidates.CurrentProduction] = rows.Sum(static item =>
                    item.CausalGracePercentageFirst),
                [MatchedOwnershipCandidates.RateFirst] = rows.Sum(static item =>
                    item.CausalGraceRateFirst),
                [MatchedOwnershipCandidates.SharedBaseLog] = rows.Sum(static item =>
                    item.CausalGraceSharedBaseLog),
                [MatchedOwnershipCandidates.SharedShapley] = rows.Sum(static item =>
                    item.CausalGraceSharedShapley),
                [MatchedOwnershipCandidates.SharedShapley3] = rows.Sum(static item =>
                    item.CausalGraceSharedShapley3),
            };
            var reference = rows.Sum(static item => item.FflogsPercentageReference);
            var residuals = predictions.ToDictionary(
                static pair => pair.Key,
                pair => pair.Value - reference,
                StringComparer.Ordinal);
            var criticalProviders = rows.Max(static item => item.MaximumCriticalProviderCount);
            var directProviders = rows.Max(static item => item.MaximumDirectProviderCount);
            var exposure = ResolveEventExposure(criticalProviders, directProviders);
            var periodic = rows.Sum(static item => item.PeriodicEventCount);
            var guaranteedCritical = rows.Sum(static item => item.GuaranteedCriticalEventCount);
            var guaranteedDirect = rows.Sum(static item => item.GuaranteedDirectHitEventCount);
            var guaranteedCombined = rows.Sum(static item =>
                item.GuaranteedCriticalDirectHitEventCount);
            result.Add(new MatchedOwnershipFightResult(
                identity.Key,
                identity.CanonicalId,
                identity.CharacterName,
                ToJobAbbreviation(identity.Job),
                identity.ServerName,
                identity.Region,
                identity.Partition,
                preflight.Ranking.Seed.ReportCode,
                preflight.Ranking.Seed.FightId,
                preflight.Ranking.Seed.EncounterId,
                preflight.Ranking.Seed.EncounterName,
                preflight.Ranking.AbsoluteStartTime,
                preflight.PartyComposition,
                exposure,
                FormatComposition(rows.SelectMany(static item =>
                    new[] { item.CriticalComposition, item.DirectComposition })),
                FormatComposition(rows.Select(static item => item.PercentageComposition)),
                criticalProviders,
                directProviders,
                rows.Any(static item => item.HasSeparateCriticalDirectProviders),
                reference,
                predictions,
                residuals,
                rows.Length,
                rows.Sum(static item => item.EventCount),
                rows.Sum(static item => item.DirectEventCount),
                periodic,
                guaranteedCritical,
                guaranteedDirect,
                guaranteedCombined,
                periodic == 0 && guaranteedCritical == 0 && guaranteedDirect == 0 &&
                    guaranteedCombined == 0,
                ResolveCombatantInfo(sample),
                sample.WhyUseful));
        }
        return result;
    }

    private static IReadOnlyList<MatchedOwnershipPairResult> BuildPairs(
        IReadOnlyList<MatchedOwnershipFightResult> fights)
    {
        var result = new List<MatchedOwnershipPairResult>();
        foreach (var actor in fights.GroupBy(static item => item.IdentityKey, StringComparer.Ordinal))
        {
            var ordered = actor.OrderBy(static item => item.AbsoluteStartTime).ToArray();
            for (var left = 0; left < ordered.Length; left++)
            {
                for (var right = left + 1; right < ordered.Length; right++)
                {
                    var fightA = ordered[left];
                    var fightB = ordered[right];
                    if (fightA.ExposureDimension == fightB.ExposureDimension)
                    {
                        continue;
                    }
                    var sameReport = fightA.Report == fightB.Report;
                    var sameEncounter = fightA.EncounterId == fightB.EncounterId;
                    var samePercentage = fightA.PercentageComposition == fightB.PercentageComposition;
                    var days = Math.Abs(fightB.AbsoluteStartTime - fightA.AbsoluteStartTime) /
                               86_400_000d;
                    var score = ScorePair(
                        sameReport,
                        sameEncounter,
                        samePercentage,
                        days,
                        fightA.IsNormalDirect && fightB.IsNormalDirect);
                    var grade = sameEncounter && samePercentage && score >= 85
                        ? "A"
                        : score >= 60
                            ? "B"
                            : "C";
                    var observedShift = fightB.FflogsPercentageReference -
                                        fightA.FflogsPercentageReference;
                    var predictionShifts = MatchedOwnershipCandidates.All.ToDictionary(
                        static candidate => candidate,
                        candidate => fightB.CandidatePredictions[candidate] -
                                     fightA.CandidatePredictions[candidate],
                        StringComparer.Ordinal);
                    var residualShifts = predictionShifts.ToDictionary(
                        static pair => pair.Key,
                        pair => pair.Value - observedShift,
                        StringComparer.Ordinal);
                    var orderedPredictions = predictionShifts.OrderBy(static pair => pair.Value)
                        .ToArray();
                    var discriminator =
                        $"{orderedPredictions[0].Key} vs {orderedPredictions[^1].Key}";
                    var separation = orderedPredictions[^1].Value - orderedPredictions[0].Value;
                    var winner = residualShifts.OrderBy(static pair => Math.Abs(pair.Value))
                        .ThenBy(static pair => pair.Key, StringComparer.Ordinal).First().Key;
                    var normalDirect = fightA.IsNormalDirect && fightB.IsNormalDirect;
                    var guaranteedConfounded = !normalDirect &&
                        (fightA.GuaranteedCriticalEventCount + fightA.GuaranteedDirectHitEventCount +
                         fightA.GuaranteedCriticalDirectHitEventCount +
                         fightB.GuaranteedCriticalEventCount + fightB.GuaranteedDirectHitEventCount +
                         fightB.GuaranteedCriticalDirectHitEventCount > 0);
                    var confidence = grade switch
                    {
                        "A" when normalDirect => "High",
                        "A" => "Medium (guaranteed/periodic aggregate mixing)",
                        "B" when normalDirect => "Medium",
                        "B" => "Low (encounter or guaranteed confounder)",
                        _ => "Auxiliary only",
                    };
                    result.Add(new MatchedOwnershipPairResult(
                        fightA.IdentityKey,
                        fightA.Actor,
                        fightA.Job,
                        fightA.World,
                        fightA.Partition,
                        $"{fightA.Report}:{fightA.FightId}",
                        $"{fightB.Report}:{fightB.FightId}",
                        fightA.EncounterId,
                        fightB.EncounterId,
                        fightA.ExposureDimension,
                        fightB.ExposureDimension,
                        ResolveChangedDimension(fightA.ExposureDimension, fightB.ExposureDimension),
                        days,
                        sameReport,
                        sameEncounter,
                        samePercentage,
                        score,
                        grade,
                        observedShift,
                        predictionShifts,
                        residualShifts,
                        discriminator,
                        separation,
                        winner,
                        confidence,
                        normalDirect,
                        guaranteedConfounded));
                }
            }
        }
        return result.OrderBy(static item => item.Grade)
            .ThenByDescending(static item => item.MaximumCandidateSeparation)
            .ToArray();
    }

    private static IReadOnlyList<MatchedOwnershipComponentResult> BuildComponents(
        IReadOnlyList<MatchedOwnershipSample> samples,
        IReadOnlyList<PercentageIdentificationConstraintRow> constraints)
    {
        var result = new List<MatchedOwnershipComponentResult>();
        foreach (var sample in samples)
        {
            var preflight = sample.Preflight;
            foreach (var row in constraints.Where(item =>
                         item.Report == preflight.Ranking.Seed.ReportCode &&
                         item.FightId == preflight.Ranking.Seed.FightId &&
                         item.RecipientActorId == preflight.RecipientActorId))
            {
                // Each row is one fixed percentage provider/buff aggregate. Keeping
                // this boundary intact avoids mixing unrelated percentage compositions.
                var predictions = new Dictionary<string, double>(StringComparer.Ordinal)
                {
                    [MatchedOwnershipCandidates.CurrentProduction] =
                        row.CausalGracePercentageFirst,
                    [MatchedOwnershipCandidates.RateFirst] = row.CausalGraceRateFirst,
                    [MatchedOwnershipCandidates.SharedBaseLog] = row.CausalGraceSharedBaseLog,
                    [MatchedOwnershipCandidates.SharedShapley] = row.CausalGraceSharedShapley,
                    [MatchedOwnershipCandidates.SharedShapley3] = row.CausalGraceSharedShapley3,
                };
                var residuals = predictions.ToDictionary(
                    static pair => pair.Key,
                    pair => pair.Value - row.FflogsPercentageReference,
                    StringComparer.Ordinal);
                result.Add(new MatchedOwnershipComponentResult(
                    sample.Identity.Key,
                    sample.Identity.CharacterName,
                    ToJobAbbreviation(sample.Identity.Job),
                    sample.Identity.ServerName,
                    sample.Identity.Partition,
                    row.Report,
                    row.FightId,
                    row.EncounterId,
                    row.Encounter,
                    preflight.Ranking.AbsoluteStartTime,
                    row.ProviderJob,
                    row.BuffStatusId,
                    row.BuffName,
                    row.RateDimension,
                    row.PercentageComposition,
                    row.MaximumCriticalProviderCount,
                    row.MaximumDirectProviderCount,
                    row.HasSeparateCriticalDirectProviders,
                    row.FflogsPercentageReference,
                    predictions,
                    residuals,
                    row.EventCount,
                    row.PeriodicEventCount,
                    row.GuaranteedCriticalEventCount + row.GuaranteedDirectHitEventCount +
                    row.GuaranteedCriticalDirectHitEventCount,
                    row.IsCleanDirectNormal));
            }
        }
        return result;
    }

    private static IReadOnlyList<MatchedOwnershipComponentPairResult> BuildComponentPairs(
        IReadOnlyList<MatchedOwnershipComponentResult> components)
    {
        var result = new List<MatchedOwnershipComponentPairResult>();
        foreach (var group in components.GroupBy(static item =>
                     (item.IdentityKey, item.BuffStatusId)))
        {
            var ordered = group.OrderBy(static item => item.AbsoluteStartTime).ToArray();
            for (var left = 0; left < ordered.Length; left++)
            {
                for (var right = left + 1; right < ordered.Length; right++)
                {
                    var componentA = ordered[left];
                    var componentB = ordered[right];
                    if (componentA.RateDimension == componentB.RateDimension)
                    {
                        continue;
                    }
                    var sameReport = componentA.Report == componentB.Report;
                    var sameEncounter = componentA.EncounterId == componentB.EncounterId;
                    var samePercentage = componentA.PercentageComposition ==
                                         componentB.PercentageComposition;
                    var normalDirect = componentA.IsCleanDirectNormal &&
                                       componentB.IsCleanDirectNormal;
                    var days = Math.Abs(componentB.AbsoluteStartTime -
                                        componentA.AbsoluteStartTime) / 86_400_000d;
                    var score = ScorePair(
                        sameReport,
                        sameEncounter,
                        samePercentage,
                        days,
                        normalDirect);
                    var grade = sameEncounter && samePercentage && score >= 85
                        ? "A"
                        : score >= 60 ? "B" : "C";
                    var observedShift = componentB.FflogsReference - componentA.FflogsReference;
                    var predictionShifts = MatchedOwnershipCandidates.All.ToDictionary(
                        static candidate => candidate,
                        candidate => componentB.CandidatePredictions[candidate] -
                                     componentA.CandidatePredictions[candidate],
                        StringComparer.Ordinal);
                    var residualShifts = predictionShifts.ToDictionary(
                        static pair => pair.Key,
                        pair => pair.Value - observedShift,
                        StringComparer.Ordinal);
                    var orderedPredictions = predictionShifts.OrderBy(static pair => pair.Value)
                        .ToArray();
                    var winner = residualShifts.OrderBy(static pair => Math.Abs(pair.Value))
                        .ThenBy(static pair => pair.Key, StringComparer.Ordinal).First().Key;
                    var guaranteedConfounded = componentA.GuaranteedEventCount > 0 ||
                                               componentB.GuaranteedEventCount > 0;
                    var confidence = grade switch
                    {
                        "A" when normalDirect => "High",
                        "A" => "Medium (guaranteed/periodic component)",
                        "B" when normalDirect => "Medium",
                        "B" => "Low (encounter or guaranteed confounder)",
                        _ => "Auxiliary only",
                    };
                    result.Add(new MatchedOwnershipComponentPairResult(
                        componentA.IdentityKey,
                        componentA.Actor,
                        componentA.Job,
                        componentA.World,
                        componentA.Partition,
                        componentA.ProviderJob,
                        componentA.BuffStatusId,
                        componentA.BuffName,
                        $"{componentA.Report}:{componentA.FightId}",
                        $"{componentB.Report}:{componentB.FightId}",
                        componentA.EncounterId,
                        componentB.EncounterId,
                        componentA.RateDimension,
                        componentB.RateDimension,
                        ResolveChangedDimension(
                            componentA.RateDimension,
                            componentB.RateDimension),
                        days,
                        sameReport,
                        sameEncounter,
                        samePercentage,
                        score,
                        grade,
                        observedShift,
                        predictionShifts,
                        residualShifts,
                        $"{orderedPredictions[0].Key} vs {orderedPredictions[^1].Key}",
                        orderedPredictions[^1].Value - orderedPredictions[0].Value,
                        winner,
                        confidence,
                        normalDirect,
                        guaranteedConfounded,
                        componentA.CriticalProviderCount > 1 ||
                        componentA.DirectHitProviderCount > 1 ||
                        componentA.SeparateCriticalDirectProviders ||
                        componentB.CriticalProviderCount > 1 ||
                        componentB.DirectHitProviderCount > 1 ||
                        componentB.SeparateCriticalDirectProviders));
                }
            }
        }
        return result.OrderBy(static item => item.Grade)
            .ThenByDescending(static item => item.MaximumCandidateSeparation)
            .ToArray();
    }

    private static IReadOnlyList<MatchedOwnershipCandidateStatistics> BuildRankings(
        IReadOnlyList<MatchedOwnershipComponentPairResult> pairs)
    {
        var scopes = new Dictionary<string, IReadOnlyList<MatchedOwnershipComponentPairResult>>
        {
            ["A-grade matched"] = pairs.Where(static item => item.Grade == "A").ToArray(),
            ["A+B matched"] = pairs.Where(static item => item.Grade is "A" or "B").ToArray(),
            ["normal-direct"] = pairs.Where(static item =>
                item.Grade is "A" or "B" && item.IsCleanDirectNormal).ToArray(),
            ["Crit-only"] = SelectDimension(pairs, "Crit-only"),
            ["DH-only"] = SelectDimension(pairs, "DH-only"),
            ["Crit+DH"] = SelectDimension(pairs, "Crit+DH"),
            ["multiple provider"] = pairs.Where(static item =>
                item.Grade is "A" or "B" && item.MultipleProviderExposure)
                .ToArray(),
            ["all"] = pairs,
        };
        var result = new List<MatchedOwnershipCandidateStatistics>();
        foreach (var (scope, selected) in scopes)
        {
            var scoped = MatchedOwnershipCandidates.All.Select(candidate =>
                    CalculateStatistics(scope, candidate,
                        selected.Select(item => item.CandidateResidualShifts[candidate]).ToArray()))
                .OrderBy(static item => item.MeanAbsolute)
                .ThenBy(static item => item.RootMeanSquare)
                .ToArray();
            for (var index = 0; index < scoped.Length; index++)
            {
                result.Add(scoped[index] with { Rank = scoped[index].N == 0 ? 0 : index + 1 });
            }
        }
        return result;
    }

    private static IReadOnlyList<MatchedOwnershipActorSummary> BuildActorSummaries(
        IReadOnlyList<MatchedOwnershipSample> samples,
        IReadOnlyList<MatchedOwnershipFightResult> fights,
        IReadOnlyList<MatchedOwnershipComponentPairResult> pairs)
        => fights.GroupBy(static item => item.IdentityKey, StringComparer.Ordinal)
            .Select(group =>
            {
                var first = group.First();
                var identity = samples.First(item => item.Identity.Key == group.Key).Identity;
                var actorPairs = pairs.Where(item => item.IdentityKey == group.Key).ToArray();
                var bestGrade = actorPairs.Any(static item => item.Grade == "A")
                    ? "A"
                    : actorPairs.Any(static item => item.Grade == "B") ? "B" : "C";
                var starts = group.Select(static item => item.AbsoluteStartTime).ToArray();
                return new MatchedOwnershipActorSummary(
                    group.Key,
                    first.CanonicalId,
                    first.Actor,
                    first.Job,
                    first.World,
                    first.Region,
                    first.Partition,
                    group.Count(),
                    string.Join(", ", group.Select(static item => item.ExposureDimension)
                        .Distinct(StringComparer.Ordinal)),
                    bestGrade,
                    identity.ResolutionSource,
                    identity.ReportServerVerified,
                    starts.Length > 1 ? (starts.Max() - starts.Min()) / 86_400_000d : 0);
            })
            .OrderBy(static item => item.Actor, StringComparer.Ordinal)
            .ToArray();

    private static string ResolveOwnershipStatus(
        IReadOnlyList<MatchedOwnershipComponentPairResult> pairs,
        IReadOnlyList<MatchedOwnershipCandidateStatistics> rankings,
        bool enoughDh,
        bool enoughCriticalDirect)
    {
        var requiredScopes = new[]
        {
            "A-grade matched", "A+B matched", "normal-direct", "Crit-only", "DH-only",
            "Crit+DH", "multiple provider",
        };
        var winners = requiredScopes.Select(scope => rankings.FirstOrDefault(item =>
                item.Scope == scope && item.Rank == 1 && item.N > 0)?.Candidate)
            .Where(static item => item is not null)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var actorCount = pairs.Where(static item => item.Grade is "A" or "B")
            .Select(static item => item.IdentityKey).Distinct().Count();
        var encounterCount = pairs.Where(static item => item.Grade is "A" or "B")
            .SelectMany(static item => new[] { item.EncounterA, item.EncounterB }).Distinct().Count();
        return pairs.Count(static item => item.Grade == "A") >= 2 &&
               actorCount >= 3 && encounterCount >= 2 && enoughDh && enoughCriticalDirect &&
               winners.Length == 1
            ? "Strongly Supported"
            : "Not determined";
    }

    private static string BuildMinimumGap(
        IReadOnlyList<MatchedOwnershipComponentPairResult> pairs,
        bool enoughDh,
        bool enoughCriticalDirect)
    {
        if (!enoughDh)
        {
            return "Need 2 same-actor, same-encounter normal-direct percentage+DH-only contrasts " +
                   "across at least 2 actors; at least one should differ from a Crit+DH fight only by " +
                   "the added Crit dimension.";
        }
        if (!enoughCriticalDirect)
        {
            return "Need 2 same-actor, same-encounter normal-direct percentage+DH-only vs " +
                   "percentage+Crit+DH contrasts across at least 2 actors.";
        }
        if (!pairs.Any(static item => item.Grade == "A" &&
                                     item.MaximumCandidateSeparation > DisplayTolerance))
        {
            return "Need one same-encounter A-grade pair whose BaseLog vs Shapley3 predicted " +
                   "difference exceeds the 0.05 aggregate display tolerance.";
        }
        return "Need one additional A-grade actor replication in the rate dimension where the " +
               "current winner flips.";
    }

    private static IReadOnlyList<string> BuildFindings(
        IReadOnlyList<MatchedOwnershipFightResult> fights,
        IReadOnlyList<MatchedOwnershipComponentPairResult> pairs,
        IReadOnlyList<MatchedOwnershipCandidateStatistics> rankings,
        bool identifiable)
    {
        var ab = pairs.Where(static item => item.Grade is "A" or "B").ToArray();
        var winners = ab.GroupBy(static item => item.Winner)
            .OrderByDescending(static group => group.Count())
            .Select(static group => $"{group.Key}={group.Count()}");
        var maximum = ab.OrderByDescending(static item => item.MaximumCandidateSeparation)
            .FirstOrDefault();
        var allWinner = rankings.FirstOrDefault(static item => item.Scope == "all" && item.Rank == 1);
        var normalWinners = pairs.Where(static item =>
                item.Grade is "A" or "B" && item.IsCleanDirectNormal)
            .GroupBy(static item => (item.Actor, item.Winner))
            .OrderBy(static group => group.Key.Actor, StringComparer.Ordinal)
            .Select(static group => $"{group.Key.Actor}:{group.Key.Winner}={group.Count()}")
            .ToArray();
        return
        [
            $"Causal replay produced {fights.Count} fight-level percentage aggregates and {pairs.Count} longitudinal exposure-changing pairs; {ab.Length} are grade A/B.",
            $"A/B pair winners: {string.Join(", ", winners)}. A single overall winner is not accepted when winners flip by actor or dimension.",
            maximum is null
                ? "No A/B discriminator was available."
                : $"Largest A/B candidate separation is {maximum.MaximumCandidateSeparation:F3} damage in {maximum.Actor} {maximum.FightA} vs {maximum.FightB} ({maximum.MaximumDiscriminatorPair}).",
            allWinner is null
                ? "No all-pair ranking is available."
                : $"All-pair MAE winner is {allWinner.Candidate} at {allWinner.MeanAbsolute:F3}; this is diagnostic only.",
            normalWinners.Length == 0
                ? "No normal-direct component pair was available."
                : $"Normal-direct actor winners: {string.Join(", ", normalWinners)}. A winner flip across actors blocks a universal rule even when aggregate MAE favors one candidate.",
            identifiable
                ? "SharedBaseLog and SharedShapley3 are observationally distinguishable in at least one mined A/B aggregate pair."
                : "SharedBaseLog and SharedShapley3 are not identifiable with the mined A/B evidence at 0.05 damage tolerance.",
        ];
    }

    private static MatchedOwnershipCandidateStatistics CalculateStatistics(
        string scope,
        string candidate,
        IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return new MatchedOwnershipCandidateStatistics(
                scope, candidate, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        }
        var ordered = values.Order().ToArray();
        var median = ordered.Length % 2 == 1
            ? ordered[ordered.Length / 2]
            : (ordered[(ordered.Length / 2) - 1] + ordered[ordered.Length / 2]) / 2;
        return new MatchedOwnershipCandidateStatistics(
            scope,
            candidate,
            values.Count,
            values.Average(),
            median,
            values.Average(Math.Abs),
            Math.Sqrt(values.Average(static value => value * value)),
            values.Max(static value => Math.Abs(value)),
            values.Count(static value => value < -DisplayTolerance),
            values.Count(static value => Math.Abs(value) <= DisplayTolerance),
            values.Count(static value => value > DisplayTolerance),
            0);
    }

    private static IReadOnlyList<MatchedOwnershipComponentPairResult> SelectDimension(
        IReadOnlyList<MatchedOwnershipComponentPairResult> pairs,
        string dimension)
        => pairs.Where(item => item.Grade is "A" or "B" &&
                               (BaseDimension(item.ExposureA) == dimension ||
                                BaseDimension(item.ExposureB) == dimension))
            .ToArray();

    private static bool FindFightMultiplicity(string exposure)
        => exposure.Contains("Multiple", StringComparison.Ordinal) ||
           exposure.Contains("separate", StringComparison.OrdinalIgnoreCase);

    private static int ScorePair(
        bool sameReport,
        bool sameEncounter,
        bool samePercentage,
        double days,
        bool normalDirect)
    {
        // The score is diagnostic provenance, not a fitted ownership weight.
        var score = 50; // exact name/server-ID/region/job/partition identity, report-verified
        if (sameEncounter) score += 20;
        if (sameReport) score += 10;
        if (samePercentage) score += 10;
        if (days <= 14) score += 10;
        else if (days <= 45) score += 5;
        if (normalDirect) score += 5;
        return score;
    }

    private static string ResolveEventExposure(int criticalProviders, int directProviders)
    {
        var baseDimension = (criticalProviders > 0, directProviders > 0) switch
        {
            (true, false) => "Crit-only",
            (false, true) => "DH-only",
            (true, true) => "Crit+DH",
            _ => "No-rate",
        };
        var suffixes = new List<string>();
        if (criticalProviders > 1) suffixes.Add("Multiple Crit");
        if (directProviders > 1) suffixes.Add("Multiple DH");
        return suffixes.Count == 0 ? baseDimension : $"{baseDimension}; {string.Join("; ", suffixes)}";
    }

    private static string ResolveChangedDimension(string left, string right)
        => $"{BaseDimension(left)} → {BaseDimension(right)}";

    private static string BaseDimension(string value)
        => value.Split(';', 2)[0];

    private static string FormatComposition(IEnumerable<string> values)
        => string.Join(" | ", values
            .SelectMany(static value => value.Split(" | ", StringSplitOptions.RemoveEmptyEntries))
            .Where(static value => value != "None")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal));

    private static string ResolveCombatantInfo(MatchedOwnershipSample sample)
    {
        var packets = 0;
        var gearPackets = 0;
        foreach (var path in sample.EventPaths)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var events = document.RootElement.GetProperty("data").GetProperty("reportData")
                .GetProperty("report").GetProperty("events").GetProperty("data");
            foreach (var item in events.EnumerateArray().Where(item =>
                         string.Equals(item.GetProperty("type").GetString(), "combatantinfo",
                             StringComparison.OrdinalIgnoreCase) &&
                         item.TryGetProperty("sourceID", out var source) &&
                         source.GetInt32() == sample.Preflight.RecipientActorId))
            {
                packets++;
                if (item.TryGetProperty("gear", out var gear) && gear.ValueKind == JsonValueKind.Array)
                {
                    gearPackets++;
                }
            }
        }
        return packets == 0
            ? "Unavailable from public event stream"
            : $"Cached raw: {packets} combatant-info packet(s), {gearPackets} with gear[]";
    }

    private static string ToJobAbbreviation(string job)
        => job switch
        {
            "Warrior" => "WAR",
            "Samurai" => "SAM",
            "Viper" => "VPR",
            "Machinist" => "MCH",
            "Dancer" => "DNC",
            "Pictomancer" => "PCT",
            _ => job.ToUpperInvariant(),
        };
}

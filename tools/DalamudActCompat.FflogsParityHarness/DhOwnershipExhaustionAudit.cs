using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DalamudActCompat.FflogsParityHarness;

internal static class DhOwnershipExhaustionAudit
{
    private const int Partition = 9;
    private const long WanderersMinuetStatusId = 2216;

    public static DhOwnershipExhaustionReport Run(
        string matchedCacheDirectory,
        string existingDancerCacheDirectory)
    {
        var rankings = ReadRankings(matchedCacheDirectory, existingDancerCacheDirectory);
        var metadataPaths = Directory.GetFiles(
            Path.Combine(matchedCacheDirectory, "samples"),
            "metadata.json",
            SearchOption.AllDirectories);
        var preflights = ReadPreflights(metadataPaths, rankings);
        var nearDh = preflights.Where(static item => item.IsPotentialKnownPercentageDhWindow)
            .ToArray();
        var nearCriticalDirect = preflights.Where(static item =>
                item.IsPotentialKnownPercentageCriticalDirectWindow)
            .ToArray();
        var pairs = BuildNearPairs(nearDh, nearCriticalDirect);
        var historicalPreflights = ReadHistoricalPreflightCount(matchedCacheDirectory);
        var apiResponses = Directory.GetFiles(
                matchedCacheDirectory,
                "*.json",
                SearchOption.AllDirectories)
            .Count(path => !string.Equals(
                Path.GetFileName(path),
                "manifest.json",
                StringComparison.OrdinalIgnoreCase));
        var strictDh = preflights.Count(static item => item.IsStrictFightLevelDhOnly);
        var validDh = preflights.Count(static item => item.PassesFullEventGate);
        return new DhOwnershipExhaustionReport(
            DateTimeOffset.UtcNow,
            apiResponses,
            rankings.RankingPageCount,
            historicalPreflights,
            metadataPaths.Length,
            preflights.Count,
            0,
            0,
            preflights.Count(static item => item.HasDirectHitRate),
            strictDh,
            nearDh.Length,
            nearDh.Count(static item => item.HasUnknownPercentageMagnitude),
            validDh,
            0,
            0,
            0,
            "Still present",
            "Not determined",
            preflights,
            nearDh,
            pairs,
            MatchedOwnershipCandidates.All.Select(static candidate =>
                new DhOwnershipCandidateResult(candidate, 0, null, "Unavailable: no pair passed the metadata gate."))
                .ToArray(),
            "Public partition-9 BRD reports expose both Crit and DH song aggregates at fight level. " +
            "A component-level DH-only possibility remains when a known fixed percentage effect " +
            "overlaps a BRD DH phase without an external Crit provider, but every cached occurrence " +
            "also contains unknown-Coda Radiant Finale. Metadata therefore cannot certify a clean " +
            "fixed-magnitude percentage component before event download.",
            "Need 2 canonical VPR actors, each with 2 same-encounter partition-9 fights using the " +
            "same fixed percentage component (prefer Divination or Dokumori). Fight A must overlap " +
            "Battle Voice/Army's Paeon with no Crit-rate status and no Radiant Finale; Fight B must " +
            "keep that DH exposure and add only Chain Stratagem or Battle Litany. Both components " +
            "must contain normal direct damage only.");
    }

    public static async Task<DhOwnershipReportPaths> WriteAsync(
        string outputDirectory,
        DhOwnershipExhaustionReport report,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputDirectory);
        var jsonPath = Path.Combine(outputDirectory, "dh-ownership-exhaustion-report.json");
        var preflightCsvPath = Path.Combine(outputDirectory, "dh-ownership-preflights.csv");
        var pairCsvPath = Path.Combine(outputDirectory, "dh-ownership-near-pairs.csv");
        var markdownPath = Path.Combine(
            outputDirectory,
            "dh-ownership-targeted-mining-2026-08-14.md");
        await File.WriteAllTextAsync(
            jsonPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);
        await File.WriteAllTextAsync(
            preflightCsvPath,
            BuildPreflightCsv(report.Preflights),
            cancellationToken);
        await File.WriteAllTextAsync(
            pairCsvPath,
            BuildPairCsv(report.NearPairs),
            cancellationToken);
        await File.WriteAllTextAsync(
            markdownPath,
            BuildMarkdown(report),
            cancellationToken);
        return new DhOwnershipReportPaths(jsonPath, preflightCsvPath, pairCsvPath, markdownPath);
    }

    private static RankingIndex ReadRankings(
        string matchedCacheDirectory,
        string existingDancerCacheDirectory)
    {
        var files = Directory.GetFiles(
                Path.Combine(matchedCacheDirectory, "discovery"),
                "*.json")
            .Select(path => (Path: path, Job: JobFromDiscoveryFile(path)))
            .Concat(Directory.GetFiles(
                    Path.Combine(existingDancerCacheDirectory, "discovery"),
                    "*.json")
                .Select(static path => (Path: path, Job: "Dancer")))
            .ToArray();
        var byFight = new Dictionary<string, List<DhRankingActor>>(StringComparer.Ordinal);
        foreach (var (path, job) in files)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var encounter = document.RootElement.GetProperty("data").GetProperty("worldData")
                .GetProperty("encounter");
            var encounterId = encounter.GetProperty("id").GetInt32();
            var encounterName = encounter.GetProperty("name").GetString() ?? string.Empty;
            foreach (var row in encounter.GetProperty("characterRankings")
                         .GetProperty("rankings").EnumerateArray())
            {
                if (!row.TryGetProperty("server", out var server) ||
                    server.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                var report = row.GetProperty("report");
                var reportCode = report.GetProperty("code").GetString() ?? string.Empty;
                var fightId = report.GetProperty("fightID").GetInt32();
                var key = FightKey(reportCode, fightId);
                if (!byFight.TryGetValue(key, out var actors))
                {
                    actors = [];
                    byFight[key] = actors;
                }
                actors.Add(new DhRankingActor(
                    row.GetProperty("name").GetString() ?? string.Empty,
                    job,
                    server.GetProperty("id").GetInt32(),
                    server.GetProperty("name").GetString() ?? string.Empty,
                    server.GetProperty("region").GetString() ?? string.Empty,
                    reportCode,
                    fightId,
                    encounterId,
                    encounterName,
                    row.GetProperty("startTime").GetInt64()));
            }
        }
        return new RankingIndex(files.Length, byFight);
    }

    private static IReadOnlyList<DhOwnershipPreflightRow> ReadPreflights(
        IReadOnlyList<string> metadataPaths,
        RankingIndex rankings)
    {
        var result = new List<DhOwnershipPreflightRow>();
        foreach (var path in metadataPaths)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var report = document.RootElement.GetProperty("data").GetProperty("reportData")
                .GetProperty("report");
            if (report.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            var fight = report.GetProperty("fights").EnumerateArray().Single();
            var reportCode = report.GetProperty("code").GetString() ?? string.Empty;
            var fightId = fight.GetProperty("id").GetInt32();
            if (!rankings.ByFight.TryGetValue(FightKey(reportCode, fightId), out var candidates))
            {
                continue;
            }
            var actors = report.GetProperty("masterData").GetProperty("actors")
                .EnumerateArray().ToArray();
            var entries = report.GetProperty("damageTable").GetProperty("data")
                .GetProperty("entries").EnumerateArray().ToArray();
            foreach (var ranking in candidates)
            {
                // FFLogs can retain multiple master-data IDs for one character across
                // report segments. The damage-table membership identifies the ID active
                // in this fight without weakening the name/job/world identity check.
                var recipients = actors.Where(item =>
                        item.GetProperty("type").GetString() == "Player" &&
                        string.Equals(item.GetProperty("name").GetString(), ranking.Actor,
                            StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(item.GetProperty("subType").GetString(), ranking.Job,
                            StringComparison.OrdinalIgnoreCase) &&
                        item.TryGetProperty("server", out var server) &&
                        string.Equals(server.GetString(), ranking.World,
                            StringComparison.OrdinalIgnoreCase) &&
                        entries.Any(entry => entry.GetProperty("id").GetInt32() ==
                                             item.GetProperty("id").GetInt32()))
                    .ToArray();
                if (recipients.Length != 1)
                {
                    continue;
                }
                var recipient = recipients[0];
                var recipientId = recipient.GetProperty("id").GetInt32();
                var entry = entries.SingleOrDefault(item =>
                    item.GetProperty("id").GetInt32() == recipientId);
                if (entry.ValueKind == JsonValueKind.Undefined)
                {
                    continue;
                }
                var statusIds = entry.TryGetProperty("taken", out var taken)
                    ? taken.EnumerateArray()
                        .Where(static item => item.TryGetProperty("total", out var total) &&
                                              total.GetDouble() > 0)
                        .Select(static item => NormalizeStatusId(
                            item.GetProperty("guid").GetInt64()))
                        .Distinct()
                        .ToArray()
                    : [];
                var definitions = statusIds
                    .Where(OffensiveBuffRegistry.ByStatusId.ContainsKey)
                    .Select(statusId => OffensiveBuffRegistry.ByStatusId[statusId])
                    .ToArray();
                var rates = definitions.Where(static definition =>
                        definition.Dimension != OffensiveBuffDimension.PercentageDamage)
                    .ToArray();
                var percentages = definitions.Where(static definition =>
                        definition.Dimension == OffensiveBuffDimension.PercentageDamage &&
                        definition.StatusId != 1821)
                    .ToArray();
                var critical = rates.Where(static definition =>
                    definition.CriticalRateIncrease > 0).ToArray();
                var direct = rates.Where(static definition =>
                    definition.DirectHitRateIncrease > 0).ToArray();
                var fixedPercentages = percentages.Where(static definition =>
                    definition.DamageMultiplier is > 1).ToArray();
                var hasUnknown = percentages.Any(static definition =>
                    definition.DamageMultiplier is null);
                // Wanderer's Minuet is mutually exclusive with the BRD DH song phase.
                // Other Crit statuses can overlap it and therefore invalidate a
                // metadata-only DH-window hypothesis.
                var hasExternalCritical = critical.Any(static definition =>
                    definition.StatusId != WanderersMinuetStatusId);
                var partyIds = entries.Select(static item => item.GetProperty("id").GetInt32())
                    .ToHashSet();
                var party = actors.Where(item =>
                        item.GetProperty("type").GetString() == "Player" &&
                        partyIds.Contains(item.GetProperty("id").GetInt32()))
                    .Select(item => ToJobAbbreviation(
                        item.GetProperty("subType").GetString() ?? string.Empty))
                    .ToArray();
                var isStrictDh = direct.Length > 0 && critical.Length == 0 &&
                                 fixedPercentages.Length > 0 && !hasUnknown;
                // Fight aggregates cannot expose song-phase overlap. This gate admits
                // any known fixed percentage component whose only aggregate Crit source
                // is Wanderer's Minuet, then rejects unknown percentage magnitudes below.
                var potentialDh = direct.Length > 0 && fixedPercentages.Length > 0 &&
                                  !hasExternalCritical;
                var potentialCriticalDirect = direct.Length > 0 &&
                                              fixedPercentages.Length > 0 &&
                                              hasExternalCritical;
                var passesEvents = isStrictDh;
                var gate = passesEvents
                    ? "PassMetadataGate"
                    : potentialDh && hasUnknown
                        ? "RejectedUnknownMagnitude"
                        : potentialDh
                            ? "NeedsEventConfirmation"
                            : direct.Length > 0 && critical.Length > 0
                                ? "RejectedCritPresentAtFightAggregate"
                                : "NotDhTarget";
                var reason = gate switch
                {
                    "RejectedUnknownMagnitude" =>
                        "Known percentage plus BRD DH exposure could contain a DH-only window, but " +
                        "unknown-Coda Radiant Finale is present in the same FFLogs aggregate.",
                    "NeedsEventConfirmation" =>
                        "Metadata suggests a known-percentage BRD DH-only window and " +
                        "contains no external Crit status.",
                    "RejectedCritPresentAtFightAggregate" =>
                        "FFLogs taken[] contains both Crit-rate and DH-rate effects.",
                    "PassMetadataGate" => "Known fixed percentage with DH rate and no Crit rate.",
                    _ => "No DH-only discriminating exposure.",
                };
                result.Add(new DhOwnershipPreflightRow(
                    $"{ranking.Actor.Trim().ToUpperInvariant()}|{ranking.ServerId}|" +
                    $"{ranking.Region.ToUpperInvariant()}|{ranking.Job.ToUpperInvariant()}|{Partition}",
                    ranking.Actor,
                    ToJobAbbreviation(ranking.Job),
                    ranking.ServerId,
                    ranking.World,
                    ranking.Region,
                    Partition,
                    reportCode,
                    fightId,
                    ranking.EncounterId,
                    ranking.Encounter,
                    ranking.AbsoluteStartTime,
                    string.Join("/", party),
                    FormatDefinitions(fixedPercentages),
                    string.Join("/", fixedPercentages.Select(static item => item.StatusId)
                        .OrderBy(static item => item)),
                    FormatDefinitions(rates),
                    critical.Length > 0,
                    direct.Length > 0,
                    hasExternalCritical,
                    hasUnknown,
                    ResolveDimension(critical.Length, direct.Length),
                    isStrictDh,
                    potentialDh,
                    potentialCriticalDirect,
                    passesEvents,
                    gate,
                    reason,
                    path));
            }
        }
        return result.DistinctBy(static item =>
                $"{item.IdentityKey}:{item.Report}:{item.FightId}")
            .OrderBy(static item => item.Job, StringComparer.Ordinal)
            .ThenBy(static item => item.Actor, StringComparer.Ordinal)
            .ThenBy(static item => item.AbsoluteStartTime)
            .ToArray();
    }

    private static IReadOnlyList<DhOwnershipNearPair> BuildNearPairs(
        IReadOnlyList<DhOwnershipPreflightRow> dhRows,
        IReadOnlyList<DhOwnershipPreflightRow> criticalDirectRows)
    {
        var result = new List<DhOwnershipNearPair>();
        foreach (var dh in dhRows)
        {
            foreach (var criticalDirect in criticalDirectRows.Where(item =>
                         item.IdentityKey == dh.IdentityKey &&
                         (item.Report != dh.Report || item.FightId != dh.FightId)))
            {
                var sameEncounter = dh.EncounterId == criticalDirect.EncounterId;
                var days = Math.Abs(criticalDirect.AbsoluteStartTime - dh.AbsoluteStartTime) /
                           86_400_000d;
                var sharedPercentageIds = ParseStatusIds(dh.FixedPercentageStatusIds)
                    .Intersect(ParseStatusIds(criticalDirect.FixedPercentageStatusIds))
                    .ToArray();
                if (sharedPercentageIds.Length == 0)
                {
                    continue;
                }
                var rejected = dh.HasUnknownPercentageMagnitude ||
                               criticalDirect.HasUnknownPercentageMagnitude;
                result.Add(new DhOwnershipNearPair(
                    dh.IdentityKey,
                    null,
                    dh.Actor,
                    dh.Job,
                    dh.World,
                    dh.Region,
                    dh.Partition,
                    $"{dh.Report}:{dh.FightId}",
                    $"{criticalDirect.Report}:{criticalDirect.FightId}",
                    dh.EncounterId,
                    criticalDirect.EncounterId,
                    sameEncounter,
                    days,
                    FormatDefinitions(sharedPercentageIds.Select(statusId =>
                        OffensiveBuffRegistry.ByStatusId[statusId])),
                    dh.RateComposition,
                    criticalDirect.RateComposition,
                    rejected ? "C" : sameEncounter ? "A-candidate" : "B-candidate",
                    rejected,
                    rejected
                        ? "Not downloaded: at least one side contains unknown-Coda Radiant Finale, so " +
                          "the fixed-magnitude metadata gate fails before candidate prediction."
                        : "Actor already has the same fixed percentage component under DH-only and " +
                          "Crit+DH metadata; this would distinguish SharedBaseLog from " +
                          "SharedShapley3 after causal event replay."));
            }
        }
        return result.OrderBy(static item => item.Job, StringComparer.Ordinal)
            .ThenBy(static item => item.Actor, StringComparer.Ordinal)
            .ThenBy(static item => item.DhCandidateFight, StringComparer.Ordinal)
            .ToArray();
    }

    private static string BuildMarkdown(DhOwnershipExhaustionReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# FFLogs DH-only targeted ownership mining - 2026-08-14");
        builder.AppendLine();
        builder.AppendLine($"Ownership status: **{report.OwnershipStatus}**");
        builder.AppendLine();
        builder.AppendLine("## A. Mining");
        builder.AppendLine();
        builder.AppendLine("| Metric | Count |");
        builder.AppendLine("|---|---:|");
        builder.AppendLine($"| Existing matched-cache API responses inventoried | {report.ExistingApiResponses} |");
        builder.AppendLine($"| Existing ranking pages inspected | {report.ExistingRankingPages} |");
        builder.AppendLine($"| Historical metadata preflight operations | {report.HistoricalMetadataPreflights} |");
        builder.AppendLine($"| Unique metadata responses inspected | {report.UniqueMetadataResponses} |");
        builder.AppendLine($"| Reconstructed target actor/fight rows | {report.ReconstructedPreflightRows} |");
        builder.AppendLine($"| New network requests | {report.NewNetworkRequests} |");
        builder.AppendLine($"| Full events downloaded | {report.FullEventsDownloaded} |");
        builder.AppendLine();
        builder.AppendLine("The 72 ranking pages comprise 60 responses inside the matched cache " +
                           "and 12 reused legacy DNC ranking responses. Ranking and metadata " +
                           "payloads are parsed; identity, report-index, and event payloads that " +
                           "cannot pass the metadata gate are only inventoried.");
        builder.AppendLine();
        builder.AppendLine("## B. DH-only");
        builder.AppendLine();
        builder.AppendLine("| Gate | Count |");
        builder.AppendLine("|---|---:|");
        builder.AppendLine($"| Actor/fight rows with any DH-rate aggregate | {report.AnyDirectHitRateFights} |");
        builder.AppendLine($"| Strict fight-level DH-only candidates | {report.StrictDhOnlyCandidateFights} |");
        builder.AppendLine($"| Known-percentage BRD DH-window near candidates | {report.PotentialDhWindowFights} |");
        builder.AppendLine($"| Near candidates rejected by unknown percentage magnitude | {report.UnknownMagnitudeRejectedFights} |");
        builder.AppendLine($"| Valid fights allowed to event download | {report.ValidDhOnlyFights} |");
        builder.AppendLine($"| A-grade pairs | {report.GradeAPairs} |");
        builder.AppendLine($"| B-grade pairs | {report.GradeBPairs} |");
        builder.AppendLine();
        builder.AppendLine("Fight-level `taken[]` never contains DH without Crit. Component-window " +
                           "near candidates rely on BRD song phases being mutually exclusive; all " +
                           "also contain unknown-Coda Radiant Finale and therefore fail before " +
                           "event download.");
        builder.AppendLine();
        builder.AppendLine("## C. Near Matched Actors (Rejected)");
        builder.AppendLine();
        builder.AppendLine("| Actor | Job | World | DH candidate | Crit+DH comparison | Same encounter | Grade | Reason |");
        builder.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var pair in report.NearPairs)
        {
            builder.AppendLine($"| {Md(pair.Actor)} | {pair.Job} | {Md(pair.World)} | " +
                               $"{pair.DhCandidateFight} | {pair.CriticalDirectComparisonFight} | " +
                               $"{pair.SameEncounter} | {pair.Grade} | {Md(pair.WhyUsefulOrRejected)} |");
        }
        builder.AppendLine();
        builder.AppendLine("No row is a matched actor observation. CanonicalID, combatant-info, " +
                           "gear, item level, level, Crit/DH stats, Cu, and Du are `unavailable` because " +
                           "the candidates failed the metadata gate and no identity/event request was authorized.");
        builder.AppendLine("Rate provider magnitudes are retained in the preflight CSV. Actual overlap " +
                           "intervals and eligible normal-direct damage are `unavailable` because no row " +
                           "was allowed through to event download.");
        builder.AppendLine();
        builder.AppendLine("## D. Candidate Result");
        builder.AppendLine();
        builder.AppendLine("| Candidate | N | Pair residual shift |");
        builder.AppendLine("|---|---:|---|");
        foreach (var candidate in report.CandidateResults)
        {
            builder.AppendLine($"| {candidate.Candidate} | {candidate.N} | unavailable |");
        }
        builder.AppendLine();
        builder.AppendLine("No candidate prediction is emitted without a valid observable pair; " +
                           "overall MAE is not reused as a substitute.");
        builder.AppendLine();
        builder.AppendLine("## E. Actor Flip");
        builder.AppendLine();
        builder.AppendLine($"**{report.ActorFlipStatus}**. No independent DH-only axis was added, " +
                           "so the existing 深眸似海蓝/BaseLog versus 弥生千鹤/Shapley3 flip is unchanged.");
        builder.AppendLine();
        builder.AppendLine("## F. Ownership Status");
        builder.AppendLine();
        builder.AppendLine($"**{report.OwnershipStatus}**");
        builder.AppendLine();
        builder.AppendLine(report.PublicDataLimitation);
        builder.AppendLine();
        builder.AppendLine("## G. Minimum Next Gap");
        builder.AppendLine();
        builder.AppendLine(report.MinimumNextGap);
        return builder.ToString();
    }

    private static string BuildPreflightCsv(IReadOnlyList<DhOwnershipPreflightRow> rows)
    {
        var builder = new StringBuilder();
        builder.AppendLine("identityKey,actor,job,serverId,world,region,partition,report,fightId,encounterId,encounter,absoluteStartTime,partyComposition,fixedPercentageComponents,fixedPercentageStatusIds,rateComposition,hasCriticalRate,hasDirectHitRate,hasExternalCriticalRate,hasUnknownPercentageMagnitude,fightRateDimension,isStrictFightLevelDhOnly,isPotentialKnownPercentageDhWindow,isPotentialKnownPercentageCriticalDirectWindow,passesFullEventGate,gateStatus,rejectionReason,metadataPath");
        foreach (var row in rows)
        {
            builder.AppendLine(string.Join(",", new object?[]
            {
                row.IdentityKey, row.Actor, row.Job, row.ServerId, row.World, row.Region,
                row.Partition, row.Report, row.FightId, row.EncounterId, row.Encounter,
                row.AbsoluteStartTime, row.PartyComposition, row.FixedPercentageComponents,
                row.FixedPercentageStatusIds, row.RateComposition, row.HasCriticalRate,
                row.HasDirectHitRate,
                row.HasExternalCriticalRate, row.HasUnknownPercentageMagnitude,
                row.FightRateDimension, row.IsStrictFightLevelDhOnly,
                row.IsPotentialKnownPercentageDhWindow,
                row.IsPotentialKnownPercentageCriticalDirectWindow, row.PassesFullEventGate,
                row.GateStatus, row.RejectionReason, row.MetadataPath,
            }.Select(Csv)));
        }
        return builder.ToString();
    }

    private static string BuildPairCsv(IReadOnlyList<DhOwnershipNearPair> pairs)
    {
        var builder = new StringBuilder();
        builder.AppendLine("identityKey,canonicalId,actor,job,world,region,partition,dhCandidateFight,criticalDirectComparisonFight,dhEncounterId,criticalDirectEncounterId,sameEncounter,daysBetween,percentageComponent,dhComposition,criticalDirectComposition,grade,rejected,whyUsefulOrRejected");
        foreach (var pair in pairs)
        {
            builder.AppendLine(string.Join(",", new object?[]
            {
                pair.IdentityKey, pair.CanonicalId, pair.Actor, pair.Job, pair.World, pair.Region,
                pair.Partition, pair.DhCandidateFight, pair.CriticalDirectComparisonFight,
                pair.DhEncounterId, pair.CriticalDirectEncounterId, pair.SameEncounter,
                pair.DaysBetween.ToString("F6", CultureInfo.InvariantCulture),
                pair.PercentageComponent, pair.DhComposition, pair.CriticalDirectComposition,
                pair.Grade, pair.Rejected, pair.WhyUsefulOrRejected,
            }.Select(Csv)));
        }
        return builder.ToString();
    }

    private static int ReadHistoricalPreflightCount(string cacheDirectory)
    {
        var path = Path.Combine(cacheDirectory, "manifest.json");
        if (!File.Exists(path)) return 0;
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.TryGetProperty("MetadataPreflights", out var value)
            ? value.GetInt32()
            : 0;
    }

    private static string JobFromDiscoveryFile(string path)
        => Path.GetFileNameWithoutExtension(path).Split('-', 2)[0] switch
        {
            "viper" => "Viper",
            "pictomancer" => "Pictomancer",
            "machinist" => "Machinist",
            "warrior" => "Warrior",
            "samurai" => "Samurai",
            _ => throw new InvalidDataException($"Unknown matched discovery job in '{path}'."),
        };

    private static string ResolveDimension(int criticalDefinitions, int directDefinitions)
        => (criticalDefinitions > 0, directDefinitions > 0) switch
        {
            (true, false) => "Crit-only",
            (false, true) => "DH-only",
            (true, true) => "Crit+DH",
            _ => "No-rate",
        };

    private static string FormatDefinitions(IEnumerable<OffensiveBuffDefinition> definitions)
        => string.Join(" | ", definitions
            .DistinctBy(static item => item.StatusId)
            .OrderBy(static item => item.StatusId)
            .Select(static item =>
                $"{item.ProviderJob}:{item.ActionName}#{item.StatusId}:{item.Magnitude}"));

    private static long NormalizeStatusId(long value)
        => value is >= 1_000_000 and < 2_000_000 ? value - 1_000_000 : value;

    private static IEnumerable<long> ParseStatusIds(string value)
        => value.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(static item => long.Parse(item, CultureInfo.InvariantCulture));

    private static string FightKey(string report, int fightId) => $"{report}:{fightId}";

    private static string ToJobAbbreviation(string job)
        => job switch
        {
            "Paladin" => "PLD",
            "Warrior" => "WAR",
            "DarkKnight" => "DRK",
            "Gunbreaker" => "GNB",
            "WhiteMage" => "WHM",
            "Scholar" => "SCH",
            "Astrologian" => "AST",
            "Sage" => "SGE",
            "Monk" => "MNK",
            "Dragoon" => "DRG",
            "Ninja" => "NIN",
            "Samurai" => "SAM",
            "Reaper" => "RPR",
            "Viper" => "VPR",
            "Bard" => "BRD",
            "Machinist" => "MCH",
            "Dancer" => "DNC",
            "BlackMage" => "BLM",
            "Summoner" => "SMN",
            "RedMage" => "RDM",
            "Pictomancer" => "PCT",
            _ => job.ToUpperInvariant(),
        };

    private static string Csv(object? value)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        return $"\"{text.Replace("\"", "\"\"")}\"";
    }

    private static string Md(string value) => value.Replace("|", "\\|");

    private sealed record RankingIndex(
        int RankingPageCount,
        IReadOnlyDictionary<string, List<DhRankingActor>> ByFight);

    private sealed record DhRankingActor(
        string Actor,
        string Job,
        int ServerId,
        string World,
        string Region,
        string Report,
        int FightId,
        int EncounterId,
        string Encounter,
        long AbsoluteStartTime);
}

internal sealed record DhOwnershipPreflightRow(
    string IdentityKey,
    string Actor,
    string Job,
    int ServerId,
    string World,
    string Region,
    int Partition,
    string Report,
    int FightId,
    int EncounterId,
    string Encounter,
    long AbsoluteStartTime,
    string PartyComposition,
    string FixedPercentageComponents,
    string FixedPercentageStatusIds,
    string RateComposition,
    bool HasCriticalRate,
    bool HasDirectHitRate,
    bool HasExternalCriticalRate,
    bool HasUnknownPercentageMagnitude,
    string FightRateDimension,
    bool IsStrictFightLevelDhOnly,
    bool IsPotentialKnownPercentageDhWindow,
    bool IsPotentialKnownPercentageCriticalDirectWindow,
    bool PassesFullEventGate,
    string GateStatus,
    string RejectionReason,
    string MetadataPath);

internal sealed record DhOwnershipNearPair(
    string IdentityKey,
    int? CanonicalId,
    string Actor,
    string Job,
    string World,
    string Region,
    int Partition,
    string DhCandidateFight,
    string CriticalDirectComparisonFight,
    int DhEncounterId,
    int CriticalDirectEncounterId,
    bool SameEncounter,
    double DaysBetween,
    string PercentageComponent,
    string DhComposition,
    string CriticalDirectComposition,
    string Grade,
    bool Rejected,
    string WhyUsefulOrRejected);

internal sealed record DhOwnershipCandidateResult(
    string Candidate,
    int N,
    double? ResidualShift,
    string Evidence);

internal sealed record DhOwnershipExhaustionReport(
    DateTimeOffset GeneratedAt,
    int ExistingApiResponses,
    int ExistingRankingPages,
    int HistoricalMetadataPreflights,
    int UniqueMetadataResponses,
    int ReconstructedPreflightRows,
    int NewNetworkRequests,
    int FullEventsDownloaded,
    int AnyDirectHitRateFights,
    int StrictDhOnlyCandidateFights,
    int PotentialDhWindowFights,
    int UnknownMagnitudeRejectedFights,
    int ValidDhOnlyFights,
    int ValidMatchedPairs,
    int GradeAPairs,
    int GradeBPairs,
    string ActorFlipStatus,
    string OwnershipStatus,
    IReadOnlyList<DhOwnershipPreflightRow> Preflights,
    IReadOnlyList<DhOwnershipPreflightRow> PotentialDhWindowRows,
    IReadOnlyList<DhOwnershipNearPair> NearPairs,
    IReadOnlyList<DhOwnershipCandidateResult> CandidateResults,
    string PublicDataLimitation,
    string MinimumNextGap);

internal sealed record DhOwnershipReportPaths(
    string JsonPath,
    string PreflightCsvPath,
    string PairCsvPath,
    string MarkdownPath);

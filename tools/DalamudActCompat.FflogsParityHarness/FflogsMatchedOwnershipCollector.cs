using System.Globalization;
using System.Text.Json;

namespace DalamudActCompat.FflogsParityHarness;

internal sealed class FflogsMatchedOwnershipCollector(
    FflogsApiClient? api,
    string cacheDirectory,
    string existingDancerCacheDirectory,
    string existingMatrixCacheDirectory)
{
    private static readonly int[] EncounterIds = [101, 102, 103, 104];
    private static readonly int[] DiscoveryPages = [1, 3, 6];
    private static readonly string[] RecipientJobs =
        ["Viper", "Pictomancer", "Machinist", "Warrior", "Samurai", "Dancer"];
    private const int Difficulty = 101;
    private const int Partition = 9;
    private const string Region = "CN";
    private const int TargetActorCount = 5;
    private const int MaximumPreflightActors = 60;
    private const string EventFilter =
        "type = 'damage' OR type = 'calculateddamage' OR type = 'combatantinfo' OR " +
        "type = 'applybuff' OR type = 'refreshbuff' OR type = 'removebuff' OR " +
        "type = 'applydebuff' OR type = 'refreshdebuff' OR type = 'removedebuff' OR " +
        "type = 'applybuffstack' OR type = 'removebuffstack' OR " +
        "type = 'applydebuffstack' OR type = 'removedebuffstack' OR " +
        "type = 'death' OR type = 'resurrect'";

    private int importedCacheHits;
    private int rankingPagesRead;
    private int metadataPreflights;

    public string ManifestPath => Path.Combine(cacheDirectory, "manifest.json");

    private FflogsApiClient Api => api ?? throw new InvalidOperationException(
        "FFLogs API access is unavailable in matched-ownership replay mode.");

    public async Task<MatchedOwnershipManifest> CollectAsync(CancellationToken cancellationToken)
    {
        var failures = new List<string>();
        var discoveries = new List<MatchedRankingFight>();
        foreach (var job in RecipientJobs)
        {
            foreach (var encounterId in EncounterIds)
            {
                foreach (var page in DiscoveryPages)
                {
                    try
                    {
                        discoveries.AddRange(await ReadRankingPageAsync(
                            job,
                            encounterId,
                            page,
                            cancellationToken));
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"ranking {job}/{encounterId}/{page}: {ex.Message}");
                    }
                }
            }
        }
        discoveries.AddRange(ReadTargetedRecipientSeeds(discoveries));

        var uniqueDiscoveries = discoveries
            .Where(static item => !string.Equals(
                item.Seed.ActorName,
                "Anonymous",
                StringComparison.OrdinalIgnoreCase))
            .DistinctBy(static item =>
                $"{item.Seed.ReportCode}:{item.Seed.FightId}:{item.Seed.ActorName}:{item.ServerId}")
            .ToArray();
        var grouped = BuildCandidateQueue(uniqueDiscoveries);
        var informative = new List<PreflightGroup>();
        foreach (var group in grouped.Take(MaximumPreflightActors))
        {
            var preflights = new List<MatchedFightPreflight>();
            foreach (var fight in group.Fights.Take(6))
            {
                try
                {
                    preflights.Add(await ReadPreflightAsync(fight, cancellationToken));
                }
                catch (Exception ex)
                {
                    failures.Add(
                        $"preflight {fight.Seed.ReportCode}:{fight.Seed.FightId}: {ex.Message}");
                }
            }

            var eligible = SelectUsefulFights(preflights);
            if (eligible.Count < 3 || eligible.Select(static item => item.RateDimension)
                    .Distinct(StringComparer.Ordinal).Count() < 2)
            {
                continue;
            }

            informative.Add(new PreflightGroup(group.Key, eligible, ScoreGroup(eligible)));
            if (informative.Count >= 10 &&
                informative.Count(static item => item.Fights.Any(fight =>
                    fight.RateDimension == "DH-only")) >= 2)
            {
                break;
            }
        }

        var selectedGroups = SelectActorGroups(informative);
        var samples = new List<MatchedOwnershipSample>();
        foreach (var group in selectedGroups)
        {
            var expandedFights = await ExpandSameEncounterControlsAsync(
                group.Fights,
                failures,
                cancellationToken);
            var first = expandedFights[0].Ranking;
            var identity = await ResolveIdentityAsync(first, expandedFights, cancellationToken);
            foreach (var fight in expandedFights)
            {
                try
                {
                    var whyUseful = BuildWhyUseful(fight, group.Fights);
                    var eventPaths = await ReadEventPagesAsync(fight, cancellationToken);
                    samples.Add(new MatchedOwnershipSample(identity, fight, eventPaths, whyUseful));
                    Console.WriteLine(
                        $"Matched ownership sample {samples.Count}: {identity.CharacterName} " +
                        $"{fight.Ranking.Seed.ReportCode}:{fight.Ranking.Seed.FightId} " +
                        $"{fight.RateDimension}");
                }
                catch (Exception ex)
                {
                    failures.Add(
                        $"events {fight.Ranking.Seed.ReportCode}:{fight.Ranking.Seed.FightId}: {ex.Message}");
                }
            }
        }

        var manifest = new MatchedOwnershipManifest(
            DateTimeOffset.UtcNow,
            uniqueDiscoveries.Length,
            rankingPagesRead,
            metadataPreflights,
            importedCacheHits,
            Api.CacheHitCount,
            Api.NetworkRequestCount,
            CountCachedApiResponses(),
            samples,
            failures);
        Directory.CreateDirectory(cacheDirectory);
        await File.WriteAllTextAsync(
            ManifestPath,
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);
        return manifest;
    }

    public MatchedOwnershipManifest ReadManifest()
        => JsonSerializer.Deserialize<MatchedOwnershipManifest>(
               File.ReadAllText(ManifestPath),
               JsonOptions())
           ?? throw new InvalidDataException($"Matched manifest '{ManifestPath}' is empty.");

    public IReadOnlyList<CachedFightSample> ReadSamples(MatchedOwnershipManifest manifest)
        => manifest.Samples.Select(static item => new CachedFightSample(
            item.Preflight.Ranking.Seed,
            item.Preflight.MetadataPath,
            item.EventPaths)).ToArray();

    private IReadOnlyList<MatchedRankingFight> ReadTargetedRecipientSeeds(
        IReadOnlyList<MatchedRankingFight> discoveries)
    {
        var manifestPath = Path.Combine(existingMatrixCacheDirectory, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            return [];
        }
        var manifest = JsonSerializer.Deserialize<TargetedMatrixManifest>(
            File.ReadAllText(manifestPath),
            JsonOptions());
        if (manifest is null)
        {
            return [];
        }
        importedCacheHits++;
        var result = new List<MatchedRankingFight>();
        foreach (var sample in manifest.Samples)
        {
            using var metadata = JsonDocument.Parse(File.ReadAllText(sample.MetadataPath));
            importedCacheHits++;
            var report = metadata.RootElement.GetProperty("data").GetProperty("reportData")
                .GetProperty("report");
            var fight = report.GetProperty("fights").EnumerateArray().Single();
            var reportStart = report.GetProperty("startTime").GetInt64();
            var actors = report.GetProperty("masterData").GetProperty("actors")
                .EnumerateArray().ToArray();
            foreach (var recipientName in sample.GuaranteedCdhRecipients)
            {
                var recipient = actors.FirstOrDefault(item =>
                    item.GetProperty("type").GetString() == "Player" &&
                    string.Equals(item.GetProperty("name").GetString(), recipientName,
                        StringComparison.OrdinalIgnoreCase));
                if (recipient.ValueKind == JsonValueKind.Undefined)
                {
                    continue;
                }
                var job = recipient.GetProperty("subType").GetString() ?? string.Empty;
                var identities = discoveries.Where(item =>
                        string.Equals(item.Seed.ActorName, recipientName,
                            StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(item.Job, job, StringComparison.OrdinalIgnoreCase))
                    .GroupBy(static item => (item.ServerId, item.ServerName, item.Region))
                    .ToArray();
                if (identities.Length != 1)
                {
                    continue;
                }
                var identity = identities[0].First();
                var start = fight.GetProperty("startTime").GetDouble();
                var end = fight.GetProperty("endTime").GetDouble();
                var seed = sample.Seed with
                {
                    ActorName = recipientName,
                    DurationMilliseconds = end - start,
                    FflogsDps = 0,
                    FflogsRdps = 0,
                    FflogsAdps = null,
                    FflogsNdps = null,
                };
                result.Add(identity with
                {
                    Seed = seed,
                    AbsoluteStartTime = reportStart + (long)start,
                    DiscoveryPath = manifestPath,
                });
            }
        }
        return result;
    }

    private async Task<IReadOnlyList<MatchedRankingFight>> ReadRankingPageAsync(
        string job,
        int encounterId,
        int page,
        CancellationToken cancellationToken)
    {
        string path;
        JsonDocument response;
        if (string.Equals(job, "Dancer", StringComparison.Ordinal) &&
            File.Exists(path = Path.Combine(
                existingDancerCacheDirectory,
                "discovery",
                $"encounter-{encounterId}-page-{page:D3}.json")))
        {
            response = JsonDocument.Parse(await File.ReadAllTextAsync(path, cancellationToken));
            importedCacheHits++;
        }
        else
        {
            const string query = """
                query MatchedOwnershipSeeds(
                  $encounterId: Int!, $difficulty: Int!, $page: Int!,
                  $region: String!, $partition: Int!, $specName: String!) {
                  rateLimitData { limitPerHour pointsSpentThisHour pointsResetIn }
                  worldData {
                    encounter(id: $encounterId) {
                      id name
                      characterRankings(
                        metric: rdps, difficulty: $difficulty, specName: $specName,
                        serverRegion: $region, partition: $partition, page: $page)
                    }
                  }
                }
                """;
            path = Path.Combine(
                cacheDirectory,
                "discovery",
                $"{job.ToLowerInvariant()}-encounter-{encounterId}-page-{page:D3}.json");
            response = await Api.QueryCachedAsync(
                path,
                query,
                new
                {
                    encounterId,
                    difficulty = Difficulty,
                    page,
                    region = Region,
                    partition = Partition,
                    specName = job,
                },
                cancellationToken);
        }

        using (response)
        {
            rankingPagesRead++;
            return ParseRankingPage(response.RootElement, job, encounterId, page, path);
        }
    }

    private static IReadOnlyList<MatchedRankingFight> ParseRankingPage(
        JsonElement root,
        string job,
        int encounterId,
        int page,
        string path)
    {
        var encounter = root.GetProperty("data").GetProperty("worldData").GetProperty("encounter");
        var encounterName = encounter.GetProperty("name").GetString() ??
                            encounterId.ToString(CultureInfo.InvariantCulture);
        var result = new List<MatchedRankingFight>();
        var rank = ((page - 1) * 100) + 1;
        foreach (var item in encounter.GetProperty("characterRankings").GetProperty("rankings")
                     .EnumerateArray())
        {
            if (!item.TryGetProperty("server", out var server) ||
                server.ValueKind != JsonValueKind.Object)
            {
                rank++;
                continue;
            }
            var report = item.GetProperty("report");
            var seed = new RankingSeed(
                report.GetProperty("code").GetString() ?? string.Empty,
                report.GetProperty("fightID").GetInt32(),
                encounterId,
                encounterName,
                item.GetProperty("name").GetString() ?? string.Empty,
                item.GetProperty("duration").GetDouble(),
                GetDouble(item, "pDPS") ?? 0,
                GetDouble(item, "rDPS") ?? item.GetProperty("amount").GetDouble(),
                GetDouble(item, "aDPS"),
                GetDouble(item, "nDPS"),
                page,
                rank++);
            result.Add(new MatchedRankingFight(
                seed,
                job,
                server.GetProperty("id").GetInt32(),
                server.GetProperty("name").GetString() ?? string.Empty,
                server.GetProperty("region").GetString() ?? Region,
                item.TryGetProperty("lodestoneID", out var lodestone) ? lodestone.GetInt32() : 0,
                item.GetProperty("startTime").GetInt64(),
                Partition,
                path));
        }
        return result;
    }

    private async Task<MatchedFightPreflight> ReadPreflightAsync(
        MatchedRankingFight ranking,
        CancellationToken cancellationToken)
    {
        const string query = """
            query MatchedFightPreflight($code: String!, $fightIDs: [Int!]) {
              rateLimitData { limitPerHour pointsSpentThisHour pointsResetIn }
              reportData {
                report(code: $code) {
                  code title startTime endTime
                  fights(fightIDs: $fightIDs) {
                    id encounterID name startTime endTime combatTime kill difficulty
                  }
                  masterData {
                    actors { id gameID name type subType petOwner server }
                    abilities { gameID name type }
                  }
                  damageTable: table(dataType: DamageDone, fightIDs: $fightIDs)
                }
              }
            }
            """;
        var path = SamplePath(ranking, "metadata.json");
        using var response = await Api.QueryCachedAsync(
            path,
            query,
            new { code = ranking.Seed.ReportCode, fightIDs = new[] { ranking.Seed.FightId } },
            cancellationToken);
        metadataPreflights++;
        var report = response.RootElement.GetProperty("data").GetProperty("reportData")
            .GetProperty("report");
        if (report.ValueKind == JsonValueKind.Null)
        {
            throw new InvalidDataException("Public report became unavailable.");
        }

        var allPlayers = report.GetProperty("masterData").GetProperty("actors")
            .EnumerateArray()
            .Where(static item => item.GetProperty("type").GetString() == "Player")
            .ToArray();
        var entries = report.GetProperty("damageTable").GetProperty("data")
            .GetProperty("entries").EnumerateArray().ToArray();
        var tableActorIds = entries.Select(static item => item.GetProperty("id").GetInt32())
            .ToHashSet();
        var recipient = allPlayers.SingleOrDefault(item =>
            tableActorIds.Contains(item.GetProperty("id").GetInt32()) &&
            string.Equals(item.GetProperty("name").GetString(), ranking.Seed.ActorName,
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.GetProperty("subType").GetString(), ranking.Job,
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.GetProperty("server").GetString(), ranking.ServerName,
                StringComparison.OrdinalIgnoreCase));
        if (recipient.ValueKind == JsonValueKind.Undefined)
        {
            throw new InvalidDataException("Ranking actor was not found in report masterData.");
        }
        var reportServer = recipient.TryGetProperty("server", out var serverValue)
            ? serverValue.GetString() ?? string.Empty
            : string.Empty;
        var serverVerified = string.Equals(
            reportServer,
            ranking.ServerName,
            StringComparison.OrdinalIgnoreCase);
        if (!serverVerified)
        {
            throw new InvalidDataException(
                $"Report server '{reportServer}' did not verify ranking world '{ranking.ServerName}'.");
        }

        var actorId = recipient.GetProperty("id").GetInt32();
        var partyActorIds = entries.Select(static item => item.GetProperty("id").GetInt32())
            .ToHashSet();
        var players = allPlayers.Where(item =>
            partyActorIds.Contains(item.GetProperty("id").GetInt32())).ToArray();
        var entry = entries.SingleOrDefault(item => item.GetProperty("id").GetInt32() == actorId);
        if (entry.ValueKind == JsonValueKind.Undefined)
        {
            throw new InvalidDataException("Ranking actor has no DamageDone table entry.");
        }

        var taken = entry.TryGetProperty("taken", out var takenElement)
            ? takenElement.EnumerateArray()
                .Where(static item => GetDouble(item, "total") is > 0)
                .Select(item => new
                {
                    StatusId = NormalizeStatusId(item.GetProperty("guid").GetInt64()),
                    Name = item.GetProperty("name").GetString() ?? string.Empty,
                })
                .ToArray()
            : [];
        var rate = taken
            .Where(static item => OffensiveBuffRegistry.ByStatusId.TryGetValue(
                item.StatusId,
                out var definition) &&
                definition.Dimension != OffensiveBuffDimension.PercentageDamage)
            .Select(static item => OffensiveBuffRegistry.ByStatusId[item.StatusId])
            .DistinctBy(static item => item.StatusId)
            .ToArray();
        var percentage = taken
            .Where(static item => OffensiveBuffRegistry.ByStatusId.TryGetValue(
                item.StatusId,
                out var definition) &&
                definition.Dimension == OffensiveBuffDimension.PercentageDamage &&
                definition.StatusId != 1821)
            .Select(static item => OffensiveBuffRegistry.ByStatusId[item.StatusId])
            .DistinctBy(static item => item.StatusId)
            .ToArray();
        var critDefinitions = rate.Where(static item => item.CriticalRateIncrease > 0).ToArray();
        var directDefinitions = rate.Where(static item => item.DirectHitRateIncrease > 0).ToArray();
        var criticalProviders = CountPotentialProviders(players, actorId, critDefinitions);
        var directProviders = CountPotentialProviders(players, actorId, directDefinitions);
        var separate = critDefinitions.Length > 0 && directDefinitions.Length > 0 &&
                       !critDefinitions.Select(static item => item.ProviderJob)
                           .Intersect(directDefinitions.Select(static item => item.ProviderJob),
                               StringComparer.OrdinalIgnoreCase).Any();
        var fight = report.GetProperty("fights").EnumerateArray().Single();
        return new MatchedFightPreflight(
            ranking,
            path,
            actorId,
            string.Join("/", players.Select(item => ToJobAbbreviation(
                item.GetProperty("subType").GetString() ?? string.Empty))),
            ResolveRateDimension(critDefinitions.Length, directDefinitions.Length,
                criticalProviders, directProviders),
            FormatDefinitions(rate),
            FormatDefinitions(percentage),
            criticalProviders,
            directProviders,
            separate,
            percentage.Any(static item => item.DamageMultiplier is > 1),
            percentage.Any(static item => item.DamageMultiplier is null),
            entry.GetProperty("total").GetInt64(),
            fight.GetProperty("startTime").GetDouble(),
            fight.GetProperty("endTime").GetDouble(),
            serverVerified);
    }

    private async Task<IReadOnlyList<MatchedFightPreflight>> ExpandSameEncounterControlsAsync(
        IReadOnlyList<MatchedFightPreflight> source,
        ICollection<string> failures,
        CancellationToken cancellationToken)
    {
        var result = source.ToList();
        foreach (var reportGroup in source.GroupBy(static item =>
                     item.Ranking.Seed.ReportCode,
                     StringComparer.Ordinal))
        {
            IReadOnlyList<MatchedRankingFight> candidates;
            try
            {
                candidates = await ReadSameReportKillCandidatesAsync(
                    reportGroup.First().Ranking,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                failures.Add($"fight index {reportGroup.Key}: {ex.Message}");
                continue;
            }
            foreach (var candidate in candidates
                         .Where(candidate => result.All(item =>
                             item.Ranking.Seed.ReportCode != candidate.Seed.ReportCode ||
                             item.Ranking.Seed.FightId != candidate.Seed.FightId))
                         .Where(candidate => reportGroup.Any(item =>
                             item.Ranking.Seed.EncounterId == candidate.Seed.EncounterId))
                         .Take(4))
            {
                try
                {
                    var preflight = await ReadPreflightAsync(candidate, cancellationToken);
                    if (!preflight.HasFixedPercentageReference ||
                        preflight.HasUnknownPercentageMagnitude)
                    {
                        continue;
                    }
                    var sameEncounter = result.Where(item =>
                        item.Ranking.Seed.EncounterId == preflight.Ranking.Seed.EncounterId).ToArray();
                    var discriminates = sameEncounter.Any(item =>
                        item.RateDimension != preflight.RateDimension &&
                        item.PercentageComposition == preflight.PercentageComposition);
                    if (!discriminates)
                    {
                        continue;
                    }
                    result.Add(preflight);
                    if (result.Count >= 6)
                    {
                        return result.OrderBy(static item => item.Ranking.AbsoluteStartTime).ToArray();
                    }
                }
                catch (Exception ex)
                {
                    // Other kills in a public report can belong to a different roster. They
                    // are rejected at preflight and never proceed to expensive event pages.
                    failures.Add($"same-report preflight {candidate.Seed.ReportCode}:" +
                                 $"{candidate.Seed.FightId}: {ex.Message}");
                }
            }
        }
        return result.OrderBy(static item => item.Ranking.AbsoluteStartTime).ToArray();
    }

    private async Task<IReadOnlyList<MatchedRankingFight>> ReadSameReportKillCandidatesAsync(
        MatchedRankingFight identity,
        CancellationToken cancellationToken)
    {
        const string query = """
            query MatchedReportKillIndex($code: String!) {
              rateLimitData { limitPerHour pointsSpentThisHour pointsResetIn }
              reportData {
                report(code: $code) {
                  startTime
                  fights(killType: Kills) {
                    id encounterID name startTime endTime combatTime kill difficulty
                  }
                }
              }
            }
            """;
        var path = Path.Combine(
            cacheDirectory,
            "report-fight-index",
            $"{identity.Seed.ReportCode}.json");
        using var response = await Api.QueryCachedAsync(
            path,
            query,
            new { code = identity.Seed.ReportCode },
            cancellationToken);
        var report = response.RootElement.GetProperty("data").GetProperty("reportData")
            .GetProperty("report");
        var reportStart = report.GetProperty("startTime").GetInt64();
        return report.GetProperty("fights").EnumerateArray()
            .Where(static item => item.GetProperty("difficulty").GetInt32() == Difficulty)
            .Where(item => EncounterIds.Contains(item.GetProperty("encounterID").GetInt32()))
            .Select(item =>
            {
                var start = item.GetProperty("startTime").GetDouble();
                var end = item.GetProperty("endTime").GetDouble();
                var seed = new RankingSeed(
                    identity.Seed.ReportCode,
                    item.GetProperty("id").GetInt32(),
                    item.GetProperty("encounterID").GetInt32(),
                    item.GetProperty("name").GetString() ?? string.Empty,
                    identity.Seed.ActorName,
                    end - start,
                    0,
                    0,
                    null,
                    null,
                    0,
                    0);
                return identity with
                {
                    Seed = seed,
                    AbsoluteStartTime = reportStart + (long)start,
                    DiscoveryPath = path,
                };
            })
            .ToArray();
    }

    private async Task<MatchedActorIdentity> ResolveIdentityAsync(
        MatchedRankingFight ranking,
        IReadOnlyList<MatchedFightPreflight> fights,
        CancellationToken cancellationToken)
    {
        const string query = """
            query ResolveMatchedCharacter($name: String!, $server: String!, $region: String!) {
              rateLimitData { limitPerHour pointsSpentThisHour pointsResetIn }
              characterData {
                character(name: $name, serverSlug: $server, serverRegion: $region) {
                  id canonicalID lodestoneID name hidden
                  server { id name slug region { name slug } }
                }
              }
            }
            """;
        var identitySlug = SafePath($"{ranking.Seed.ActorName}-{ranking.ServerId}-{ranking.Job}");
        var path = Path.Combine(cacheDirectory, "identity", $"{identitySlug}.json");
        using var response = await Api.QueryCachedAsync(
            path,
            query,
            new { name = ranking.Seed.ActorName, server = ranking.ServerName, region = ranking.Region },
            cancellationToken);
        var character = response.RootElement.GetProperty("data").GetProperty("characterData")
            .GetProperty("character");
        int? canonicalId = character.ValueKind == JsonValueKind.Object
            ? character.GetProperty("canonicalID").GetInt32()
            : null;
        var verified = fights.All(static item => item.ReportServerVerified);
        var key = canonicalId is { } stableId
            ? $"canonical:{stableId}:{ranking.Job}:{ranking.Partition}"
            : $"ranking:{ranking.Seed.ActorName}:{ranking.ServerId}:{ranking.Region}:" +
              $"{ranking.Job}:{ranking.Partition}";
        return new MatchedActorIdentity(
            key,
            canonicalId,
            ranking.Seed.ActorName,
            ranking.ServerId,
            ranking.ServerName,
            ranking.Region,
            ranking.Job,
            ranking.Partition,
            canonicalId is not null
                ? "FFLogs Character.canonicalID"
                : "ranking name + numeric server ID + region, verified against report actor.server",
            verified);
    }

    private async Task<IReadOnlyList<string>> ReadEventPagesAsync(
        MatchedFightPreflight fight,
        CancellationToken cancellationToken)
    {
        const string query = """
            query MatchedFightEvents(
              $code: String!, $fightIDs: [Int!], $start: Float!, $end: Float!, $filter: String) {
              rateLimitData { limitPerHour pointsSpentThisHour pointsResetIn }
              reportData {
                report(code: $code) {
                  events(dataType: All, fightIDs: $fightIDs, startTime: $start, endTime: $end,
                    limit: 10000, filterExpression: $filter) { data nextPageTimestamp }
                }
              }
            }
            """;
        var result = new List<string>();
        var pageStart = fight.FightStart;
        for (var page = 0; page < 100; page++)
        {
            var path = SamplePath(fight.Ranking, $"events-{page:D3}.json");
            using var response = await Api.QueryCachedAsync(
                path,
                query,
                new
                {
                    code = fight.Ranking.Seed.ReportCode,
                    fightIDs = new[] { fight.Ranking.Seed.FightId },
                    start = pageStart,
                    end = fight.FightEnd,
                    filter = EventFilter,
                },
                cancellationToken);
            result.Add(path);
            var events = response.RootElement.GetProperty("data").GetProperty("reportData")
                .GetProperty("report").GetProperty("events");
            if (!events.TryGetProperty("nextPageTimestamp", out var next) ||
                next.ValueKind == JsonValueKind.Null)
            {
                return result;
            }
            var nextStart = next.GetDouble();
            if (nextStart <= pageStart || nextStart > fight.FightEnd)
            {
                throw new InvalidDataException(
                    $"FFLogs returned invalid event page {nextStart} after {pageStart}.");
            }
            pageStart = nextStart;
        }
        throw new InvalidDataException("Matched event pagination exceeded 100 pages.");
    }

    private static IReadOnlyList<CandidateGroup> BuildCandidateQueue(
        IReadOnlyList<MatchedRankingFight> discoveries)
    {
        var byJob = discoveries
            .GroupBy(static item => item.Job, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                group => group.GroupBy(static item => IdentityKey(item), StringComparer.OrdinalIgnoreCase)
                    .Select(static identity => new CandidateGroup(
                        identity.Key,
                        identity.OrderBy(static item => item.AbsoluteStartTime)
                            .DistinctBy(static item =>
                                $"{item.Seed.ReportCode}:{item.Seed.FightId}")
                            .ToArray()))
                    .Where(static group => group.Fights.Count >= 3)
                    .OrderByDescending(static group => group.Fights
                        .Select(item => item.Seed.ReportCode).Distinct().Count())
                    .ThenBy(static group => group.Fights[^1].AbsoluteStartTime -
                                            group.Fights[0].AbsoluteStartTime)
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var result = new List<CandidateGroup>();
        for (var index = 0; ; index++)
        {
            var added = false;
            foreach (var job in RecipientJobs)
            {
                var groups = byJob.GetValueOrDefault(job) ?? [];
                if (index >= groups.Length)
                {
                    continue;
                }
                result.Add(groups[index]);
                added = true;
            }
            if (!added)
            {
                return result;
            }
        }
    }

    private static IReadOnlyList<MatchedFightPreflight> SelectUsefulFights(
        IReadOnlyList<MatchedFightPreflight> source)
    {
        var eligible = source
            .Where(static item => item.ReportServerVerified &&
                                  item.HasFixedPercentageReference &&
                                  !item.HasUnknownPercentageMagnitude)
            .OrderBy(static item => item.Ranking.AbsoluteStartTime)
            .ToArray();
        var result = new List<MatchedFightPreflight>();
        foreach (var dimension in new[] { "DH-only", "Crit+DH", "Crit-only", "No-rate" })
        {
            var match = eligible.FirstOrDefault(item => item.RateDimension == dimension);
            if (match is not null)
            {
                result.Add(match);
            }
        }
        foreach (var extra in eligible
                     .Where(item => result.All(selected => selected.Ranking.Seed.ReportCode !=
                                                        item.Ranking.Seed.ReportCode ||
                                                        selected.Ranking.Seed.FightId !=
                                                        item.Ranking.Seed.FightId))
                     .OrderByDescending(item => result.Any(selected =>
                         selected.PercentageComposition == item.PercentageComposition &&
                         selected.RateDimension != item.RateDimension)))
        {
            if (result.Count >= Math.Min(4, eligible.Length))
            {
                break;
            }
            result.Add(extra);
        }
        return result.OrderBy(static item => item.Ranking.AbsoluteStartTime).ToArray();
    }

    private static IReadOnlyList<PreflightGroup> SelectActorGroups(
        IReadOnlyList<PreflightGroup> source)
    {
        var selected = new List<PreflightGroup>();
        foreach (var group in source.OrderByDescending(static item => item.Score))
        {
            var job = group.Fights[0].Ranking.Job;
            if (selected.Count(item => string.Equals(
                    item.Fights[0].Ranking.Job,
                    job,
                    StringComparison.OrdinalIgnoreCase)) >= 2)
            {
                continue;
            }
            selected.Add(group);
            if (selected.Count == TargetActorCount)
            {
                break;
            }
        }
        return selected;
    }

    private static int ScoreGroup(IReadOnlyList<MatchedFightPreflight> fights)
    {
        var dimensions = fights.Select(static item => item.RateDimension)
            .Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        var score = dimensions.Count * 20;
        if (dimensions.Contains("DH-only")) score += 100;
        if (dimensions.Contains("Crit+DH")) score += 80;
        if (dimensions.Contains("No-rate")) score += 40;
        if (fights.Any(left => fights.Any(right =>
                !ReferenceEquals(left, right) &&
                left.RateDimension != right.RateDimension &&
                left.PercentageComposition == right.PercentageComposition)))
        {
            score += 60;
        }
        if (string.Equals(fights[0].Ranking.Job, "Viper", StringComparison.OrdinalIgnoreCase))
        {
            score += 35;
        }
        return score;
    }

    private static string BuildWhyUseful(
        MatchedFightPreflight fight,
        IReadOnlyList<MatchedFightPreflight> group)
    {
        var partner = group
            .Where(item => item.Ranking.Seed.ReportCode != fight.Ranking.Seed.ReportCode ||
                           item.Ranking.Seed.FightId != fight.Ranking.Seed.FightId)
            .OrderByDescending(item => item.RateDimension != fight.RateDimension)
            .ThenByDescending(item => item.PercentageComposition == fight.PercentageComposition)
            .First();
        var priority = fight.RateDimension is "DH-only" or "Crit+DH" ? "P0" : "P1";
        return $"{priority}: adds {fight.RateDimension} exposure for {fight.Ranking.Seed.ActorName}; " +
               $"forms a longitudinal contrast with {partner.Ranking.Seed.ReportCode}:" +
               $"{partner.Ranking.Seed.FightId} ({partner.RateDimension}). Exact candidate " +
               "separation is computed from causal event replay before reading the aggregate residual.";
    }

    private string SamplePath(MatchedRankingFight ranking, string fileName)
        => Path.Combine(
            cacheDirectory,
            "samples",
            $"{ranking.Seed.ReportCode}-{ranking.Seed.FightId}",
            fileName);

    private int CountCachedApiResponses()
        => new[] { "discovery", "samples", "identity", "report-fight-index" }
            .Select(directory => Path.Combine(cacheDirectory, directory))
            .Where(Directory.Exists)
            .Sum(directory => Directory.GetFiles(
                directory,
                "*.json",
                SearchOption.AllDirectories).Length);

    private static int CountPotentialProviders(
        IReadOnlyList<JsonElement> players,
        int recipientId,
        IReadOnlyList<OffensiveBuffDefinition> definitions)
        => definitions.SelectMany(definition => players.Where(player =>
                player.GetProperty("id").GetInt32() != recipientId &&
                string.Equals(
                    ToJobAbbreviation(player.GetProperty("subType").GetString() ?? string.Empty),
                    definition.ProviderJob,
                    StringComparison.OrdinalIgnoreCase)))
            .Select(static player => player.GetProperty("id").GetInt32())
            .Distinct()
            .Count();

    private static string ResolveRateDimension(
        int criticalDefinitions,
        int directDefinitions,
        int criticalProviders,
        int directProviders)
    {
        var hasCritical = criticalDefinitions > 0 && criticalProviders > 0;
        var hasDirect = directDefinitions > 0 && directProviders > 0;
        return (hasCritical, hasDirect) switch
        {
            (true, false) => "Crit-only",
            (false, true) => "DH-only",
            (true, true) => "Crit+DH",
            _ => "No-rate",
        };
    }

    private static string FormatDefinitions(IReadOnlyList<OffensiveBuffDefinition> definitions)
        => definitions.Count == 0
            ? "None"
            : string.Join(" | ", definitions
                .OrderBy(static item => item.StatusId)
                .Select(static item => $"{item.ProviderJob}:{item.ActionName}#{item.StatusId}"));

    private static long NormalizeStatusId(long value)
        => value is >= 1_000_000 and < 2_000_000 ? value - 1_000_000 : value;

    private static string IdentityKey(MatchedRankingFight item)
        => $"{item.Seed.ActorName.Trim().ToUpperInvariant()}|{item.ServerId}|" +
           $"{item.Region.ToUpperInvariant()}|{item.Job.ToUpperInvariant()}|{item.Partition}";

    private static string SafePath(string value)
        => string.Concat(value.Select(character => Path.GetInvalidFileNameChars().Contains(character)
            ? '_'
            : character));

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

    private static double? GetDouble(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;

    private static JsonSerializerOptions JsonOptions()
        => new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    private sealed record CandidateGroup(string Key, IReadOnlyList<MatchedRankingFight> Fights);

    private sealed record PreflightGroup(
        string Key,
        IReadOnlyList<MatchedFightPreflight> Fights,
        int Score);
}

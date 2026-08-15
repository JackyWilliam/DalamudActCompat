using System.Globalization;
using System.Text.Json;

namespace DalamudActCompat.FflogsParityHarness;

internal sealed class FflogsTargetedMatrixCollector(
    FflogsApiClient? api,
    string cacheDirectory,
    int targetFightCount = 6)
{
    private static readonly int[] EncounterIds = [101, 102, 103, 104];
    private static readonly HashSet<string> GuaranteedCdhJobs =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Warrior", "Machinist", "Pictomancer", "Dancer",
        };
    private const int Difficulty = 101;
    private const int Partition = 9;
    private const string EventFilter =
        "type = 'damage' OR type = 'calculateddamage' OR " +
        "type = 'applybuff' OR type = 'refreshbuff' OR type = 'removebuff' OR " +
        "type = 'applydebuff' OR type = 'refreshdebuff' OR type = 'removedebuff' OR " +
        "type = 'applybuffstack' OR type = 'removebuffstack' OR " +
        "type = 'applydebuffstack' OR type = 'removedebuffstack' OR " +
        "type = 'death' OR type = 'resurrect'";

    public string ManifestPath => Path.Combine(cacheDirectory, "manifest.json");

    private FflogsApiClient Api => api ?? throw new InvalidOperationException(
        "FFLogs API access is unavailable in matrix replay-only mode.");

    public async Task<TargetedMatrixManifest> CollectAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(ManifestPath))
        {
            var cached = ReadManifest();
            if (cached.Samples.Count >= targetFightCount)
            {
                return cached;
            }
        }

        var discoveries = new Dictionary<int, IReadOnlyList<RankingSeed>>();
        foreach (var encounterId in EncounterIds)
        {
            discoveries[encounterId] = await FetchBardRankingPageAsync(
                encounterId,
                page: 1,
                cancellationToken);
        }

        var selected = new List<TargetedMatrixSample>();
        var failures = new List<string>();
        foreach (var seed in Interleave(discoveries).Take(40))
        {
            if (selected.Count >= targetFightCount)
            {
                break;
            }
            try
            {
                var preflight = await FetchMetadataAsync(seed, cancellationToken);
                var recipientNames = ResolveDiscriminatingRecipients(preflight.MetadataPath);
                if (recipientNames.Count == 0)
                {
                    continue;
                }
                var eventPaths = await FetchEventPagesAsync(
                    seed,
                    preflight.FightStart,
                    preflight.FightEnd,
                    cancellationToken);
                selected.Add(new TargetedMatrixSample(
                    seed,
                    preflight.MetadataPath,
                    eventPaths,
                    "BRD DH-only rates → WAR/MCH/PCT/DNC guaranteed-CDH; separates dimension-wise from combined guaranteed attribution",
                    recipientNames));
                Console.WriteLine(
                    $"Targeted matrix sample {selected.Count}/{targetFightCount}: " +
                    $"{seed.ReportCode} fight {seed.FightId}, recipients={string.Join('/', recipientNames)}");
            }
            catch (Exception ex)
            {
                failures.Add($"{seed.ReportCode}:{seed.FightId}: {ex.Message}");
            }
        }

        if (selected.Count < targetFightCount)
        {
            throw new InvalidOperationException(
                $"Only {selected.Count}/{targetFightCount} targeted BRD→G-CDH fights were found. " +
                "The collector deliberately does not fall back to random logs.");
        }

        var manifest = new TargetedMatrixManifest(
            DateTimeOffset.UtcNow,
            targetFightCount,
            selected,
            failures,
            selected.Count);
        Directory.CreateDirectory(cacheDirectory);
        await File.WriteAllTextAsync(
            ManifestPath,
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);
        return manifest;
    }

    public TargetedMatrixManifest? ReadManifestOrNull()
        => File.Exists(ManifestPath) ? ReadManifest() : null;

    public IReadOnlyList<CachedFightSample> ReadSamples(TargetedMatrixManifest? manifest)
        => manifest?.Samples.Select(static item => new CachedFightSample(
            item.Seed,
            item.MetadataPath,
            item.EventPaths)).ToArray() ?? [];

    private TargetedMatrixManifest ReadManifest()
        => JsonSerializer.Deserialize<TargetedMatrixManifest>(
               File.ReadAllText(ManifestPath),
               new JsonSerializerOptions(JsonSerializerDefaults.Web)
               {
                   PropertyNameCaseInsensitive = true,
               })
           ?? throw new InvalidDataException($"Targeted manifest '{ManifestPath}' is empty.");

    private async Task<IReadOnlyList<RankingSeed>> FetchBardRankingPageAsync(
        int encounterId,
        int page,
        CancellationToken cancellationToken)
    {
        const string query = """
            query BardMatrixSeeds(
              $encounterId: Int!, $difficulty: Int!, $page: Int!,
              $region: String!, $partition: Int!) {
              rateLimitData { limitPerHour pointsSpentThisHour pointsResetIn }
              worldData {
                encounter(id: $encounterId) {
                  id name
                  characterRankings(
                    metric: rdps, difficulty: $difficulty, specName: "Bard",
                    serverRegion: $region, partition: $partition, page: $page)
                }
              }
            }
            """;
        var path = Path.Combine(
            cacheDirectory,
            "discovery",
            $"bard-encounter-{encounterId}-page-{page:D3}.json");
        using var response = await Api.QueryCachedAsync(
            path,
            query,
            new { encounterId, difficulty = Difficulty, page, region = "CN", partition = Partition },
            cancellationToken);
        var encounter = response.RootElement.GetProperty("data").GetProperty("worldData")
            .GetProperty("encounter");
        var encounterName = encounter.GetProperty("name").GetString() ?? encounterId.ToString();
        var result = new List<RankingSeed>();
        var rank = ((page - 1) * 100) + 1;
        foreach (var item in encounter.GetProperty("characterRankings").GetProperty("rankings")
                     .EnumerateArray())
        {
            var report = item.GetProperty("report");
            result.Add(new RankingSeed(
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
                rank++));
        }
        return result;
    }

    private async Task<MetadataPreflight> FetchMetadataAsync(
        RankingSeed seed,
        CancellationToken cancellationToken)
    {
        const string query = """
            query MatrixFightMetadata($code: String!, $fightIDs: [Int!]) {
              rateLimitData { limitPerHour pointsSpentThisHour pointsResetIn }
              reportData {
                report(code: $code) {
                  code title startTime endTime
                  fights(fightIDs: $fightIDs) {
                    id encounterID name startTime endTime combatTime kill difficulty
                  }
                  masterData {
                    actors { id gameID name type subType petOwner }
                    abilities { gameID name type }
                  }
                  damageTable: table(dataType: DamageDone, fightIDs: $fightIDs)
                }
              }
            }
            """;
        var path = Path.Combine(
            cacheDirectory,
            "samples",
            $"{seed.ReportCode}-{seed.FightId}",
            "metadata.json");
        using var response = await Api.QueryCachedAsync(
            path,
            query,
            new { code = seed.ReportCode, fightIDs = new[] { seed.FightId } },
            cancellationToken);
        var report = response.RootElement.GetProperty("data").GetProperty("reportData")
            .GetProperty("report");
        if (report.ValueKind == JsonValueKind.Null)
        {
            throw new InvalidDataException("Public report became unavailable.");
        }
        var fight = report.GetProperty("fights").EnumerateArray().Single();
        return new MetadataPreflight(
            path,
            fight.GetProperty("startTime").GetDouble(),
            fight.GetProperty("endTime").GetDouble());
    }

    private static IReadOnlyList<string> ResolveDiscriminatingRecipients(string metadataPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(metadataPath));
        var report = document.RootElement.GetProperty("data").GetProperty("reportData")
            .GetProperty("report");
        var actors = report.GetProperty("masterData").GetProperty("actors")
            .EnumerateArray()
            .Where(static item => item.GetProperty("type").GetString() == "Player")
            .ToDictionary(
                static item => item.GetProperty("id").GetInt32(),
                static item => new
                {
                    Name = item.GetProperty("name").GetString() ?? string.Empty,
                    Job = item.GetProperty("subType").GetString() ?? string.Empty,
                });
        var table = report.GetProperty("damageTable").GetProperty("data");
        return table.GetProperty("entries").EnumerateArray()
            .Where(item => actors.TryGetValue(item.GetProperty("id").GetInt32(), out var actor) &&
                           GuaranteedCdhJobs.Contains(actor.Job))
            .Where(static item => item.TryGetProperty("taken", out var taken) &&
                                  taken.EnumerateArray().Any(buff =>
                                      buff.GetProperty("guid").GetInt64()
                                          is 1_000_141 or 1_002_218))
            .Select(item => actors[item.GetProperty("id").GetInt32()].Name)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<IReadOnlyList<string>> FetchEventPagesAsync(
        RankingSeed seed,
        double fightStart,
        double fightEnd,
        CancellationToken cancellationToken)
    {
        const string query = """
            query MatrixFightEvents(
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
        var directory = Path.Combine(cacheDirectory, "samples", $"{seed.ReportCode}-{seed.FightId}");
        var paths = new List<string>();
        var pageStart = fightStart;
        for (var page = 0; page < 100; page++)
        {
            var path = Path.Combine(directory, $"events-{page:D3}.json");
            using var response = await Api.QueryCachedAsync(
                path,
                query,
                new
                {
                    code = seed.ReportCode,
                    fightIDs = new[] { seed.FightId },
                    start = pageStart,
                    end = fightEnd,
                    filter = EventFilter,
                },
                cancellationToken);
            paths.Add(path);
            var events = response.RootElement.GetProperty("data").GetProperty("reportData")
                .GetProperty("report").GetProperty("events");
            if (!events.TryGetProperty("nextPageTimestamp", out var next) ||
                next.ValueKind == JsonValueKind.Null)
            {
                return paths;
            }
            var nextStart = next.GetDouble();
            if (nextStart <= pageStart || nextStart > fightEnd)
            {
                throw new InvalidDataException(
                    $"FFLogs returned invalid event pagination timestamp {nextStart} after {pageStart}.");
            }
            pageStart = nextStart;
        }
        throw new InvalidDataException("FFLogs matrix event pagination exceeded 100 pages.");
    }

    private static IEnumerable<RankingSeed> Interleave(
        IReadOnlyDictionary<int, IReadOnlyList<RankingSeed>> candidates)
    {
        var maximum = candidates.Values.Max(static values => values.Count);
        for (var index = 0; index < maximum; index++)
        {
            foreach (var encounterId in EncounterIds)
            {
                var values = candidates.GetValueOrDefault(encounterId) ?? [];
                if (index < values.Count)
                {
                    yield return values[index];
                }
            }
        }
    }

    private static double? GetDouble(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;

    private sealed record MetadataPreflight(string MetadataPath, double FightStart, double FightEnd);
}

internal sealed record TargetedMatrixSample(
    RankingSeed Seed,
    string MetadataPath,
    IReadOnlyList<string> EventPaths,
    string Discriminates,
    IReadOnlyList<string> GuaranteedCdhRecipients);

internal sealed record TargetedMatrixManifest(
    DateTimeOffset GeneratedAt,
    int RequestedFightCount,
    IReadOnlyList<TargetedMatrixSample> Samples,
    IReadOnlyList<string> Failures,
    int NewlyMinedFightCount);

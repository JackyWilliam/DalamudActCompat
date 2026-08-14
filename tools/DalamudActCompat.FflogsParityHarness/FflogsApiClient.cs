using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace DalamudActCompat.FflogsParityHarness;

/// <summary>
/// Thin FFLogs v2 client with raw-response caching. Credentials and access tokens are
/// deliberately excluded from every cache file and diagnostic message.
/// </summary>
internal sealed class FflogsApiClient(FflogsCredentials credentials, bool refreshCache) : IAsyncDisposable
{
    private const string TokenEndpoint = "https://www.fflogs.com/oauth/token";
    private const string GraphQlEndpoint = "https://www.fflogs.com/api/v2/client";
    private const int MaximumAttempts = 5;
    private const double MinimumRateLimitReserve = 75;
    private readonly HttpClient httpClient = new() { Timeout = TimeSpan.FromSeconds(45) };
    private readonly Random jitter = new();
    private string accessToken = string.Empty;
    private DateTimeOffset tokenExpiresAt;
    private double rateLimitRemaining = double.PositiveInfinity;
    private int rateLimitResetSeconds;

    public int CacheHitCount { get; private set; }

    public int NetworkRequestCount { get; private set; }

    public async Task<JsonDocument> QueryCachedAsync(
        string cachePath,
        string query,
        object variables,
        CancellationToken cancellationToken)
    {
        if (!refreshCache && File.Exists(cachePath))
        {
            CacheHitCount++;
            return JsonDocument.Parse(await File.ReadAllTextAsync(cachePath, cancellationToken));
        }

        await RespectRateLimitAsync(cancellationToken);
        NetworkRequestCount++;
        Exception? lastFailure = null;
        for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            try
            {
                var body = await SendQueryAsync(query, variables, cancellationToken);
                using var validation = JsonDocument.Parse(body);
                ThrowIfGraphQlError(validation.RootElement);
                UpdateRateLimit(validation.RootElement);
                await WriteAtomicallyAsync(cachePath, body, cancellationToken);
                return JsonDocument.Parse(body);
            }
            catch (Exception ex) when (IsRetryable(ex, cancellationToken) && attempt < MaximumAttempts)
            {
                lastFailure = ex;
                var exponentialDelay = TimeSpan.FromMilliseconds(
                    Math.Min(15_000, 400 * Math.Pow(2, attempt - 1)) + jitter.Next(50, 350));
                var delay = ex is FflogsHttpException { RetryAfter: { } retryAfter }
                    ? retryAfter > exponentialDelay ? retryAfter : exponentialDelay
                    : exponentialDelay;
                Console.WriteLine(
                    $"FFLogs request retry {attempt}/{MaximumAttempts - 1} in {delay.TotalSeconds:F1}s: {SafeMessage(ex)}");
                await Task.Delay(delay, cancellationToken);
            }
        }

        throw new InvalidOperationException(
            $"FFLogs request failed after {MaximumAttempts} attempts: {SafeMessage(lastFailure)}",
            lastFailure);
    }

    public ValueTask DisposeAsync()
    {
        httpClient.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<string> SendQueryAsync(
        string query,
        object variables,
        CancellationToken cancellationToken)
    {
        var token = await GetAccessTokenAsync(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, GraphQlEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { query, variables }),
            Encoding.UTF8,
            "application/json");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta;
            throw new FflogsHttpException(response.StatusCode, retryAfter);
        }

        return body;
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(accessToken) &&
            tokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
        {
            return accessToken;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint);
        var credentialBytes = Encoding.UTF8.GetBytes($"{credentials.ClientId}:{credentials.ClientSecret}");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(credentialBytes));
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
        });
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"FFLogs authentication failed with HTTP {(int)response.StatusCode}; check the configured client.");
        }

        using var tokenDocument = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken));
        accessToken = tokenDocument.RootElement.GetProperty("access_token").GetString()
            ?? throw new InvalidDataException("FFLogs token response did not contain access_token.");
        var expiresIn = tokenDocument.RootElement.TryGetProperty("expires_in", out var expires)
            ? expires.GetInt32()
            : 3600;
        tokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
        return accessToken;
    }

    private async Task RespectRateLimitAsync(CancellationToken cancellationToken)
    {
        if (rateLimitRemaining >= MinimumRateLimitReserve || rateLimitResetSeconds <= 0)
        {
            return;
        }

        var delay = TimeSpan.FromSeconds(Math.Clamp(rateLimitResetSeconds + 2, 2, 3602));
        Console.WriteLine(
            $"FFLogs rate-limit reserve reached; waiting {delay.TotalMinutes:F1} minutes for reset.");
        await Task.Delay(delay, cancellationToken);
        rateLimitRemaining = double.PositiveInfinity;
        rateLimitResetSeconds = 0;
    }

    private void UpdateRateLimit(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("rateLimitData", out var rateLimit) ||
            rateLimit.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var limit = rateLimit.GetProperty("limitPerHour").GetDouble();
        var spent = rateLimit.GetProperty("pointsSpentThisHour").GetDouble();
        rateLimitRemaining = Math.Max(0, limit - spent);
        rateLimitResetSeconds = rateLimit.GetProperty("pointsResetIn").GetInt32();
    }

    private static void ThrowIfGraphQlError(JsonElement root)
    {
        if (!root.TryGetProperty("errors", out var errors) || errors.GetArrayLength() == 0)
        {
            return;
        }

        var messages = errors.EnumerateArray()
            .Select(error => error.TryGetProperty("message", out var message)
                ? message.GetString()
                : null)
            .Where(static message => !string.IsNullOrWhiteSpace(message));
        throw new InvalidDataException($"FFLogs GraphQL error: {string.Join(" | ", messages)}");
    }

    private static bool IsRetryable(Exception exception, CancellationToken cancellationToken)
        => !cancellationToken.IsCancellationRequested && exception switch
        {
            HttpRequestException => true,
            TaskCanceledException => true,
            FflogsHttpException http => http.StatusCode == HttpStatusCode.TooManyRequests ||
                                        (int)http.StatusCode >= 500,
            _ => false,
        };

    private static string SafeMessage(Exception? exception)
        => exception switch
        {
            null => "unknown failure",
            FflogsHttpException http => $"HTTP {(int)http.StatusCode}",
            _ => exception.Message,
        };

    private static async Task WriteAtomicallyAsync(
        string destination,
        string contents,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = $"{destination}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, contents, cancellationToken);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private sealed class FflogsHttpException(HttpStatusCode statusCode, TimeSpan? retryAfter)
        : Exception($"FFLogs returned HTTP {(int)statusCode}.")
    {
        public HttpStatusCode StatusCode { get; } = statusCode;

        public TimeSpan? RetryAfter { get; } = retryAfter;
    }
}

internal sealed class FflogsSampleCollector(
    FflogsApiClient? api,
    string cacheDirectory,
    int requestedSamples)
{
    private static readonly int[] EncounterIds = [101, 102, 103, 104];
    private static readonly int[] DiscoveryPages = [1, 3, 6];
    private const int Difficulty = 101;
    private const int Partition = 9;
    private const string Region = "CN";
    private const string Metric = "rdps";
    private const string EventFilter =
        // calculateddamage carries the action-packet timestamp that DACT retains
        // when a later EffectResult confirms the effective damage amount.
        "type = 'damage' OR type = 'calculateddamage' OR " +
        "type = 'applybuff' OR type = 'refreshbuff' OR type = 'removebuff' OR " +
        "type = 'applydebuff' OR type = 'refreshdebuff' OR type = 'removedebuff' OR " +
        "type = 'applybuffstack' OR type = 'removebuffstack' OR " +
        "type = 'applydebuffstack' OR type = 'removedebuffstack' OR " +
        "type = 'death' OR type = 'resurrect'";

    public string ManifestPath => Path.Combine(cacheDirectory, "manifest.json");

    private FflogsApiClient Api => api ?? throw new InvalidOperationException(
        "FFLogs API access is unavailable in replay-only mode.");

    public async Task<CacheManifest> CollectAsync(CancellationToken cancellationToken)
    {
        var candidatesByEncounter = new Dictionary<int, IReadOnlyList<RankingSeed>>();
        foreach (var encounterId in EncounterIds)
        {
            var candidates = new List<RankingSeed>();
            foreach (var page in DiscoveryPages)
            {
                candidates.AddRange(await FetchRankingPageAsync(encounterId, page, cancellationToken));
            }

            candidatesByEncounter[encounterId] = candidates
                .DistinctBy(static seed => $"{seed.ReportCode}:{seed.FightId}:{seed.ActorName}")
                .ToArray();
        }

        var orderedCandidates = InterleaveCandidates(candidatesByEncounter, requestedSamples * 2);
        var valid = new List<RankingSeed>(requestedSamples);
        var failures = new List<string>();
        foreach (var seed in orderedCandidates)
        {
            if (valid.Count >= requestedSamples)
            {
                break;
            }

            try
            {
                await FetchSampleAsync(seed, cancellationToken);
                var normalized = FflogsEventNormalizer.Normalize(ReadCachedSample(seed));
                _ = DactRdpsReplay.Replay(normalized);
                valid.Add(seed);
                Console.WriteLine(
                    $"Cached valid DNC sample {valid.Count}/{requestedSamples}: " +
                    $"{seed.EncounterName} {seed.ReportCode} fight {seed.FightId}");
            }
            catch (Exception ex)
            {
                var failure = $"{seed.ReportCode}:{seed.FightId}: {ex.Message}";
                failures.Add(failure);
                Console.WriteLine($"Skipped sample: {failure}");
            }
        }

        if (valid.Count < requestedSamples)
        {
            throw new InvalidOperationException(
                $"Only {valid.Count} valid samples were collected from {orderedCandidates.Count} candidates.");
        }

        var manifest = new CacheManifest(DateTimeOffset.UtcNow, requestedSamples, valid, failures);
        Directory.CreateDirectory(cacheDirectory);
        await File.WriteAllTextAsync(
            ManifestPath,
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);
        return manifest;
    }

    public CacheManifest ReadManifest()
        => JsonSerializer.Deserialize<CacheManifest>(File.ReadAllText(ManifestPath), JsonOptions())
           ?? throw new InvalidDataException($"Cache manifest '{ManifestPath}' is empty.");

    public CachedFightSample ReadCachedSample(RankingSeed seed)
    {
        var sampleDirectory = SampleDirectory(seed);
        var eventFiles = Directory.GetFiles(sampleDirectory, "events-*.json")
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (eventFiles.Length == 0)
        {
            throw new FileNotFoundException($"No cached events found for {seed.ReportCode}:{seed.FightId}.");
        }

        return new CachedFightSample(
            seed,
            Path.Combine(sampleDirectory, "metadata.json"),
            eventFiles);
    }

    private async Task<IReadOnlyList<RankingSeed>> FetchRankingPageAsync(
        int encounterId,
        int page,
        CancellationToken cancellationToken)
    {
        const string query = """
            query DancerRankingSeeds(
              $encounterId: Int!,
              $difficulty: Int!,
              $page: Int!,
              $region: String!,
              $partition: Int!,
              $metric: CharacterRankingMetricType!) {
              rateLimitData { limitPerHour pointsSpentThisHour pointsResetIn }
              worldData {
                encounter(id: $encounterId) {
                  id
                  name
                  characterRankings(
                    metric: $metric,
                    difficulty: $difficulty,
                    specName: "Dancer",
                    serverRegion: $region,
                    partition: $partition,
                    page: $page)
                }
              }
            }
            """;
        var cachePath = Path.Combine(
            cacheDirectory,
            "discovery",
            $"encounter-{encounterId}-page-{page:D3}.json");
        using var response = await Api.QueryCachedAsync(
            cachePath,
            query,
            new
            {
                encounterId,
                difficulty = Difficulty,
                page,
                region = Region,
                partition = Partition,
                metric = Metric,
            },
            cancellationToken);
        var encounter = response.RootElement
            .GetProperty("data")
            .GetProperty("worldData")
            .GetProperty("encounter");
        var encounterName = encounter.GetProperty("name").GetString() ?? encounterId.ToString(CultureInfo.InvariantCulture);
        var rankings = encounter.GetProperty("characterRankings").GetProperty("rankings");
        var result = new List<RankingSeed>();
        var rank = ((page - 1) * 100) + 1;
        foreach (var item in rankings.EnumerateArray())
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

        return result.Where(static seed =>
                !string.IsNullOrWhiteSpace(seed.ReportCode) &&
                !string.IsNullOrWhiteSpace(seed.ActorName) &&
                seed.FflogsRdps > 0 &&
                seed.DurationMilliseconds > 0)
            .ToArray();
    }

    private async Task FetchSampleAsync(RankingSeed seed, CancellationToken cancellationToken)
    {
        const string metadataQuery = """
            query DancerFightMetadata($code: String!, $fightIDs: [Int!]) {
              rateLimitData { limitPerHour pointsSpentThisHour pointsResetIn }
              reportData {
                report(code: $code) {
                  code
                  title
                  startTime
                  endTime
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
        var sampleDirectory = SampleDirectory(seed);
        var metadataPath = Path.Combine(sampleDirectory, "metadata.json");
        using var metadata = await Api.QueryCachedAsync(
            metadataPath,
            metadataQuery,
            new { code = seed.ReportCode, fightIDs = new[] { seed.FightId } },
            cancellationToken);
        var report = metadata.RootElement.GetProperty("data").GetProperty("reportData").GetProperty("report");
        if (report.ValueKind == JsonValueKind.Null)
        {
            throw new InvalidDataException("Public report became unavailable.");
        }

        var fight = report.GetProperty("fights").EnumerateArray().Single();
        var fightStart = fight.GetProperty("startTime").GetDouble();
        var fightEnd = fight.GetProperty("endTime").GetDouble();
        await FetchEventPagesAsync(seed, sampleDirectory, fightStart, fightEnd, cancellationToken);
    }

    private async Task FetchEventPagesAsync(
        RankingSeed seed,
        string sampleDirectory,
        double fightStart,
        double fightEnd,
        CancellationToken cancellationToken)
    {
        const string eventsQuery = """
            query DancerFightEvents(
              $code: String!,
              $fightIDs: [Int!],
              $start: Float!,
              $end: Float!,
              $filter: String) {
              rateLimitData { limitPerHour pointsSpentThisHour pointsResetIn }
              reportData {
                report(code: $code) {
                  events(
                    dataType: All,
                    fightIDs: $fightIDs,
                    startTime: $start,
                    endTime: $end,
                    limit: 10000,
                    filterExpression: $filter) {
                    data
                    nextPageTimestamp
                  }
                }
              }
            }
            """;
        var pageStart = fightStart;
        for (var page = 0; page < 100; page++)
        {
            var path = Path.Combine(sampleDirectory, $"events-{page:D3}.json");
            using var response = await Api.QueryCachedAsync(
                path,
                eventsQuery,
                new
                {
                    code = seed.ReportCode,
                    fightIDs = new[] { seed.FightId },
                    start = pageStart,
                    end = fightEnd,
                    filter = EventFilter,
                },
                cancellationToken);
            var events = response.RootElement
                .GetProperty("data")
                .GetProperty("reportData")
                .GetProperty("report")
                .GetProperty("events");
            if (!events.TryGetProperty("nextPageTimestamp", out var next) ||
                next.ValueKind == JsonValueKind.Null)
            {
                DeleteStaleEventPages(sampleDirectory, page);
                return;
            }

            var nextStart = next.GetDouble();
            if (nextStart <= pageStart || nextStart > fightEnd)
            {
                throw new InvalidDataException(
                    $"FFLogs returned invalid event pagination timestamp {nextStart} after {pageStart}.");
            }

            pageStart = nextStart;
        }

        throw new InvalidDataException("FFLogs event pagination exceeded the 100-page safety limit.");
    }

    private static void DeleteStaleEventPages(string sampleDirectory, int lastPage)
    {
        // A refreshed query can produce fewer pages than an older cached filter. Leaving
        // the old tail would replay duplicate or out-of-scope events on the next run.
        foreach (var path in Directory.GetFiles(sampleDirectory, "events-*.json"))
        {
            var suffix = Path.GetFileNameWithoutExtension(path).AsSpan("events-".Length);
            if (int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out var page) &&
                page > lastPage)
            {
                File.Delete(path);
            }
        }
    }

    private string SampleDirectory(RankingSeed seed)
        => Path.Combine(cacheDirectory, "samples", $"{seed.ReportCode}-{seed.FightId}");

    private static IReadOnlyList<RankingSeed> InterleaveCandidates(
        IReadOnlyDictionary<int, IReadOnlyList<RankingSeed>> candidates,
        int maximum)
    {
        var result = new List<RankingSeed>();
        var offsets = EncounterIds.ToDictionary(static id => id, static _ => 0);
        while (result.Count < maximum)
        {
            var added = false;
            foreach (var encounterId in EncounterIds)
            {
                var values = candidates.GetValueOrDefault(encounterId) ?? [];
                var offset = offsets[encounterId];
                if (offset >= values.Count)
                {
                    continue;
                }

                result.Add(values[offset]);
                offsets[encounterId] = offset + 1;
                added = true;
                if (result.Count == maximum)
                {
                    break;
                }
            }

            if (!added)
            {
                break;
            }
        }

        return result;
    }

    private static double? GetDouble(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;

    private static JsonSerializerOptions JsonOptions()
        => new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };
}

internal sealed record CachedFightSample(
    RankingSeed Seed,
    string MetadataPath,
    IReadOnlyList<string> EventPaths);

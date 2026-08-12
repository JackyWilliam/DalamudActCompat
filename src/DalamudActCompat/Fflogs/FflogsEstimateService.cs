using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Numerics;
using System.Text;
using System.Text.Json;
using DalamudActCompat.Core.Models;
using DalamudActCompat.Infrastructure.Logging;

namespace DalamudActCompat.Fflogs;

public enum FflogsEstimateState
{
    Disabled,
    NeedsCredentials,
    Idle,
    Loading,
    Ready,
    InactiveContent,
    Error,
}

public sealed record FflogsEstimateStatus(FflogsEstimateState State, string Message);

public sealed record FflogsActiveEncounter(
    uint TerritoryId,
    int Phase,
    int EncounterId,
    string EncounterName,
    int Difficulty);

public sealed record FflogsEstimate(double Percentile, Vector4 Color, string EncounterName)
{
    public int Score => Math.Clamp((int)Math.Round(Percentile), 0, 100);
}

public sealed class FflogsEstimateService : IAsyncDisposable
{
    private const string TokenEndpoint = "https://www.fflogs.com/oauth/token";
    private const string GraphQlEndpoint = "https://www.fflogs.com/api/v2/client";
    private const int PageSize = 100;
    private const int MaximumPage = 4096;
    internal const int CurrentCurveFormatVersion = 1;
    private static readonly double[] PercentilePoints =
    [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9,
        10, 11, 12, 13, 14, 15, 16, 17, 18, 19,
        20, 21, 22, 23, 24, 25,
        30, 35, 40, 45, 50, 55, 60, 65, 70, 75, 80, 85, 90,
        95, 96, 97, 98, 99, 100,
    ];

    private readonly Func<FflogsSettings> getSettings;
    private readonly string cachePath;
    private readonly PluginLogger logger;
    private readonly HttpClient httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(20),
    };
    private readonly SemaphoreSlim apiGate = new(1, 1);
    private readonly SemaphoreSlim cacheWriteGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<string, byte> loading = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FflogsCurveCacheEntry> curves = new(StringComparer.OrdinalIgnoreCase);
    private readonly object cacheLock = new();
    private readonly object encounterContextLock = new();
    private readonly object statusLock = new();
    private readonly object tokenLock = new();
    private readonly object backgroundTaskLock = new();
    private readonly HashSet<Task> backgroundTasks = [];
    private IReadOnlyList<FflogsEncounterCatalogEntry> encounters = [];
    private DateTimeOffset catalogFetchedAt;
    private FflogsEstimateStatus status = new(FflogsEstimateState.Disabled, "FFLogs estimation is disabled.");
    private string accessToken = string.Empty;
    private DateTimeOffset accessTokenExpiresAt;
    private string tokenClientId = string.Empty;
    private string tokenClientSecret = string.Empty;
    private uint currentTerritoryId;
    private string currentZoneName = string.Empty;
    private int currentPhase = 1;
    private Task? disposeTask;
    private bool shutdownStarted;
    private int resourcesDisposed;

    public FflogsEstimateService(
        Func<FflogsSettings> getSettings,
        string cachePath,
        PluginLogger logger)
    {
        this.getSettings = getSettings;
        this.cachePath = cachePath;
        this.logger = logger;
        LoadCache();
        CanUseApi(GetSettingsSnapshot());
    }

    public FflogsEstimateStatus Status
    {
        get
        {
            lock (statusLock)
            {
                return status;
            }
        }
    }

    internal static IReadOnlyList<double> CurveSamplePercentiles => PercentilePoints;

    public FflogsActiveEncounter? ActiveEncounter
    {
        get
        {
            var context = GetEncounterContext();
            return CurrentFflogsEncounterTable.TryResolve(
                context.TerritoryId,
                context.Phase,
                out var encounter)
                ? new FflogsActiveEncounter(
                    encounter.TerritoryId,
                    encounter.Phase,
                    encounter.EncounterId,
                    encounter.EncounterName,
                    encounter.Difficulty)
                : null;
        }
    }

    public FflogsEstimate? GetEstimate(Encounter encounter)
    {
        encounter = encounter.FflogsRankingEncounter ?? encounter;
        var localPlayer = encounter.Combatants.FirstOrDefault(static combatant => combatant.IsLocalPlayer);
        return localPlayer is null
            ? null
            : GetEstimateCore(encounter, localPlayer);
    }

    public FflogsEstimate? GetEstimate(
        Encounter encounter,
        string combatantId,
        string combatantName)
    {
        encounter = encounter.FflogsRankingEncounter ?? encounter;
        var combatant = ResolveRankingCombatant(encounter, combatantId, combatantName);
        return combatant is null
            ? null
            : GetEstimateCore(encounter, combatant);
    }

    public Encounter CaptureAvailableEstimates(Encounter encounter)
    {
        var settings = GetSettingsSnapshot();
        if (!HasApiAccess(settings))
        {
            return encounter;
        }

        var rankingEncounter = encounter.FflogsRankingEncounter ?? encounter;
        if (rankingEncounter.EffectiveDuration.TotalSeconds < 15)
        {
            return encounter;
        }

        var changed = false;
        var missingSpecs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var combatants = encounter.Combatants
            .Select(combatant =>
            {
                var rankingCombatant = ResolveRankingCombatant(
                    rankingEncounter,
                    combatant.Id,
                    combatant.Name);
                if (rankingCombatant is null)
                {
                    return combatant;
                }

                var estimate = GetPersistedEstimate(rankingCombatant) ??
                               TryGetCachedEstimate(
                                   rankingEncounter,
                                   rankingCombatant,
                                   settings,
                                   updateStatus: false);
                if (estimate is null)
                {
                    var specName = ToFflogsSpecName(rankingCombatant.Job);
                    if (!string.IsNullOrWhiteSpace(specName))
                    {
                        missingSpecs.Add(specName);
                    }
                    return combatant;
                }

                changed = true;
                return combatant with
                {
                    FflogsPercentile = estimate.Percentile,
                    FflogsEncounterName = estimate.EncounterName,
                };
            })
            .ToArray();

        // Active snapshots warm every party job in the background. A finished
        // encounter only persists already available estimates and must not start
        // new network work during shutdown/finalization.
        if (rankingEncounter.IsActive)
        {
            foreach (var specName in missingSpecs)
            {
                QueueCurveLoad(specName);
            }
        }

        return changed
            ? encounter with { Combatants = combatants }
            : encounter;
    }

    private FflogsEstimate? GetEstimateCore(Encounter encounter, Combatant combatant)
    {
        if (!encounter.IsActive)
        {
            return GetPersistedEstimate(combatant);
        }

        var settings = GetSettingsSnapshot();
        if (!CanUseApi(settings))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(combatant.Job) ||
            encounter.EffectiveDuration.TotalSeconds < 15)
        {
            return null;
        }

        var estimate = TryGetCachedEstimate(
            encounter,
            combatant,
            settings,
            updateStatus: true);
        if (estimate is not null)
        {
            return estimate;
        }

        QueueCurveLoad(ToFflogsSpecName(combatant.Job));
        return null;
    }

    internal static FflogsEstimate? GetPersistedEstimate(Combatant combatant)
    {
        if (combatant.FflogsPercentile is not { } percentile ||
            !double.IsFinite(percentile) ||
            percentile < 0 ||
            percentile > 100 ||
            string.IsNullOrWhiteSpace(combatant.FflogsEncounterName))
        {
            return null;
        }

        return new FflogsEstimate(
            percentile,
            ColorForPercentile(percentile),
            combatant.FflogsEncounterName);
    }

    private FflogsEstimate? TryGetCachedEstimate(
        Encounter encounter,
        Combatant combatant,
        FflogsSettings settings,
        bool updateStatus)
    {
        if (string.IsNullOrWhiteSpace(combatant.Job) ||
            encounter.EffectiveDuration.TotalSeconds < 15)
        {
            return null;
        }

        var activeEncounter = ActiveEncounter;
        if (activeEncounter is null)
        {
            return null;
        }

        var specName = ToFflogsSpecName(combatant.Job);
        var key = CurveKey(activeEncounter.EncounterId, activeEncounter.Difficulty, specName);
        FflogsCurveCacheEntry? curve;
        lock (cacheLock)
        {
            curves.TryGetValue(key, out curve);
        }

        if (curve is null || IsExpired(curve.FetchedAt, settings.CacheHours))
        {
            return null;
        }

        var encounterDps = combatant.Rdps > 0
            ? combatant.Rdps
            : combatant.EncDps > 0
                ? combatant.EncDps
                : combatant.TotalDamage / Math.Max(1, encounter.EffectiveDuration.TotalSeconds);
        var percentile = EstimatePercentile(curve.Points, encounterDps);
        if (updateStatus)
        {
            SetStatus(
                FflogsEstimateState.Ready,
                $"FFLogs estimate ready: {curve.EncounterName} / {specName}.");
        }
        return new FflogsEstimate(
            percentile,
            ColorForPercentile(percentile),
            curve.EncounterName);
    }

    public void RequestRefresh(Encounter? encounter)
    {
        var settings = GetSettingsSnapshot();
        if (!CanUseApi(settings))
        {
            return;
        }

        if (encounter is null)
        {
            QueueCatalogRefresh();
            return;
        }

        encounter = encounter.FflogsRankingEncounter ?? encounter;

        var activeEncounter = ActiveEncounter;
        var specNames = ResolveFflogsSpecs(encounter);
        if (specNames.Count == 0)
        {
            QueueCatalogRefresh();
            return;
        }

        if (activeEncounter is not null)
        {
            lock (cacheLock)
            {
                foreach (var specName in specNames)
                {
                    curves.Remove(CurveKey(
                        activeEncounter.EncounterId,
                        activeEncounter.Difficulty,
                        specName));
                }
            }
        }

        foreach (var specName in specNames)
        {
            QueueCurveLoad(specName);
        }
    }

    internal static Combatant? ResolveRankingCombatant(
        Encounter encounter,
        string combatantId,
        string combatantName)
    {
        if (!string.IsNullOrWhiteSpace(combatantId))
        {
            var byId = encounter.Combatants.FirstOrDefault(combatant =>
                string.Equals(combatant.Id, combatantId, StringComparison.OrdinalIgnoreCase));
            if (byId is not null)
            {
                return byId;
            }
        }

        return string.IsNullOrWhiteSpace(combatantName)
            ? null
            : encounter.Combatants.FirstOrDefault(combatant =>
                string.Equals(combatant.Name, combatantName, StringComparison.OrdinalIgnoreCase));
    }

    internal static IReadOnlyList<string> ResolveFflogsSpecs(Encounter encounter)
        => encounter.Combatants
            .Where(static combatant => !string.IsNullOrWhiteSpace(combatant.Job))
            .Select(static combatant => ToFflogsSpecName(combatant.Job))
            .Where(static specName => !string.IsNullOrWhiteSpace(specName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public void NotifyCredentialsChanged()
    {
        lock (tokenLock)
        {
            accessToken = string.Empty;
            accessTokenExpiresAt = default;
            tokenClientId = string.Empty;
            tokenClientSecret = string.Empty;
        }

        var settings = GetSettingsSnapshot();
        if (settings.Enabled &&
            !string.IsNullOrWhiteSpace(settings.ClientId) &&
            !string.IsNullOrWhiteSpace(settings.ClientSecret))
        {
            if (ActiveEncounter is null)
            {
                SetInactiveContentStatus();
            }
            else
            {
                SetStatus(FflogsEstimateState.Idle, "FFLogs credentials are ready to be tested.");
            }
        }
        else
        {
            CanUseApi(settings);
        }
    }

    public void NotifyTerritoryChanged(uint territoryId, string zoneName)
    {
        lock (encounterContextLock)
        {
            currentTerritoryId = territoryId;
            currentZoneName = zoneName?.Trim() ?? string.Empty;
            currentPhase = 1;
        }

        var settings = GetSettingsSnapshot();
        if (!CurrentFflogsEncounterTable.TryResolve(territoryId, 1, out var encounter))
        {
            if (settings.Enabled &&
                !string.IsNullOrWhiteSpace(settings.ClientId) &&
                !string.IsNullOrWhiteSpace(settings.ClientSecret))
            {
                SetStatus(
                    FflogsEstimateState.InactiveContent,
                    $"Territory {territoryId} is not part of the current FFLogs ranking tier.");
            }
            else
            {
                CanUseApi(settings);
            }
            return;
        }

        if (CanUseApi(settings))
        {
            SetStatus(
                FflogsEstimateState.Idle,
                $"Automatically matched {encounter.EncounterName} (FFLogs encounter {encounter.EncounterId}).");
            QueueCatalogLoad(forceRefresh: false);
        }
    }

    public void ObserveLogLine(string actLine)
    {
        CurrentFflogsEncounter? encounter = null;
        lock (encounterContextLock)
        {
            var observedPhase = CurrentFflogsEncounterTable.ObservePhase(
                currentTerritoryId,
                currentPhase,
                actLine);
            if (observedPhase == currentPhase)
            {
                return;
            }

            currentPhase = observedPhase;
            if (CurrentFflogsEncounterTable.TryResolve(
                    currentTerritoryId,
                    currentPhase,
                    out var resolvedEncounter))
            {
                encounter = resolvedEncounter;
            }
        }

        if (encounter is not null && CanUseApi(GetSettingsSnapshot()))
        {
            SetStatus(
                FflogsEstimateState.Idle,
                $"Automatically switched to {encounter.EncounterName} (FFLogs encounter {encounter.EncounterId}).");
        }
    }

    private bool CanUseApi(FflogsSettings settings)
    {
        if (!settings.Enabled)
        {
            SetStatus(FflogsEstimateState.Disabled, "FFLogs estimation is disabled.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(settings.ClientId) || string.IsNullOrWhiteSpace(settings.ClientSecret))
        {
            SetStatus(FflogsEstimateState.NeedsCredentials, "FFLogs API credentials are required.");
            return false;
        }

        if (Status.State is FflogsEstimateState.Disabled or FflogsEstimateState.NeedsCredentials)
        {
            SetStatus(FflogsEstimateState.Idle, "FFLogs credentials are ready to be tested.");
        }
        return true;
    }

    private static bool HasApiAccess(FflogsSettings settings)
        => settings.Enabled &&
           !string.IsNullOrWhiteSpace(settings.ClientId) &&
           !string.IsNullOrWhiteSpace(settings.ClientSecret);

    public static Vector4 ColorForPercentile(double percentile)
    {
        var score = Math.Clamp((int)Math.Round(percentile), 0, 100);
        if (score >= 100) return Rgb(229, 204, 128);
        if (score >= 99) return Rgb(226, 104, 168);
        if (score >= 95) return Rgb(255, 128, 0);
        if (score >= 75) return Rgb(163, 53, 238);
        if (score >= 50) return Rgb(0, 112, 255);
        if (score >= 25) return Rgb(30, 255, 0);
        return Rgb(102, 102, 102);
    }

    internal static double EstimatePercentile(IReadOnlyList<FflogsCurvePoint> points, double amount)
    {
        if (points.Count == 0 || amount <= 0)
        {
            return 0;
        }

        var ordered = points.OrderBy(static point => point.Amount).ToArray();
        if (amount <= ordered[0].Amount)
        {
            return ordered[0].Percentile;
        }
        if (amount >= ordered[^1].Amount)
        {
            return ordered[^1].Percentile;
        }

        for (var index = 1; index < ordered.Length; index++)
        {
            var upper = ordered[index];
            if (amount > upper.Amount)
            {
                continue;
            }

            var lower = ordered[index - 1];
            if (Math.Abs(upper.Amount - lower.Amount) < 0.001)
            {
                return upper.Percentile;
            }

            var ratio = (amount - lower.Amount) / (upper.Amount - lower.Amount);
            return Math.Clamp(
                lower.Percentile + ((upper.Percentile - lower.Percentile) * ratio),
                0,
                100);
        }

        return 100;
    }

    private void QueueCurveLoad(string specName)
    {
        if (string.IsNullOrWhiteSpace(specName))
        {
            return;
        }

        var activeEncounter = ActiveEncounter;
        if (activeEncounter is null)
        {
            SetInactiveContentStatus();
            return;
        }

        var encounterId = activeEncounter.EncounterId;
        var difficulty = activeEncounter.Difficulty;
        var loadKey = $"{encounterId}|{difficulty}|{specName}";
        if (!loading.TryAdd(loadKey, 0))
        {
            return;
        }

        SetStatus(FflogsEstimateState.Loading, "Loading FFLogs public ranking samples…");
        if (!TryStartBackgroundTask(async cancellationToken =>
        {
            try
            {
                await EnsureCatalogAsync(cancellationToken).ConfigureAwait(false);
                var curve = await BuildCurveAsync(
                    encounterId,
                    difficulty,
                    specName,
                    cancellationToken).ConfigureAwait(false);
                lock (cacheLock)
                {
                    curves[CurveKey(encounterId, difficulty, specName)] = curve;
                }
                await SaveCacheAsync(cancellationToken).ConfigureAwait(false);
                var currentEncounter = ActiveEncounter;
                if (currentEncounter?.EncounterId == encounterId &&
                    currentEncounter.Difficulty == difficulty)
                {
                    SetStatus(
                        FflogsEstimateState.Ready,
                        $"FFLogs estimate ready: {curve.EncounterName} / {specName}.");
                }
                else if (currentEncounter is null)
                {
                    SetInactiveContentStatus();
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                logger.Error(ex, "FFLogs public-ranking estimate refresh failed.");
                var currentEncounter = ActiveEncounter;
                if (currentEncounter?.EncounterId == encounterId &&
                    currentEncounter.Difficulty == difficulty)
                {
                    SetStatus(FflogsEstimateState.Error, ex.Message);
                }
                else if (currentEncounter is null)
                {
                    SetInactiveContentStatus();
                }
            }
            finally
            {
                loading.TryRemove(loadKey, out _);
            }
        }))
        {
            loading.TryRemove(loadKey, out _);
        }
    }

    private void QueueCatalogRefresh()
        => QueueCatalogLoad(forceRefresh: true);

    private void QueueCatalogLoad(bool forceRefresh)
    {
        if (!loading.TryAdd("catalog", 0))
        {
            return;
        }

        SetStatus(FflogsEstimateState.Loading, "Refreshing FFLogs encounter catalog…");
        if (!TryStartBackgroundTask(async cancellationToken =>
        {
            try
            {
                if (forceRefresh)
                {
                    lock (cacheLock)
                    {
                        catalogFetchedAt = default;
                    }
                }
                await EnsureCatalogAsync(cancellationToken).ConfigureAwait(false);
                var activeEncounter = ActiveEncounter;
                if (activeEncounter is null)
                {
                    SetInactiveContentStatus();
                }
                else
                {
                    SetStatus(
                        FflogsEstimateState.Ready,
                        $"Automatically matched {activeEncounter.EncounterName} " +
                        $"(FFLogs encounter {activeEncounter.EncounterId}).");
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                logger.Error(ex, "FFLogs encounter catalog refresh failed.");
                SetStatus(FflogsEstimateState.Error, ex.Message);
            }
            finally
            {
                loading.TryRemove("catalog", out _);
            }
        }))
        {
            loading.TryRemove("catalog", out _);
        }
    }

    private async Task EnsureCatalogAsync(CancellationToken cancellationToken)
    {
        lock (cacheLock)
        {
            if (encounters.Count > 0 && !IsExpired(catalogFetchedAt, 24))
            {
                return;
            }
        }

        const string query = """
            query EncounterCatalog($zoneId: Int!) {
              worldData {
                zone(id: $zoneId) {
                  id
                  name
                  frozen
                  encounters { id name }
                }
              }
            }
            """;
        using var document = await QueryAsync(
            query,
            new { zoneId = CurrentFflogsEncounterTable.ZoneId },
            cancellationToken).ConfigureAwait(false);
        var result = new List<FflogsEncounterCatalogEntry>();
        var zone = document.RootElement.GetProperty("data").GetProperty("worldData").GetProperty("zone");
        if (zone.ValueKind == JsonValueKind.Null)
        {
            throw new InvalidOperationException(
                $"The current FFLogs zone {CurrentFflogsEncounterTable.ZoneId} was not found.");
        }

        var frozen = zone.TryGetProperty("frozen", out var frozenValue) && frozenValue.ValueKind == JsonValueKind.True;
        if (frozen)
        {
            throw new InvalidOperationException(
                $"FFLogs zone {CurrentFflogsEncounterTable.ZoneId} is frozen; update the current duty table before loading rankings.");
        }

        var zoneName = zone.GetProperty("name").GetString() ?? CurrentFflogsEncounterTable.ZoneName;
        foreach (var encounter in zone.GetProperty("encounters").EnumerateArray())
        {
            var encounterId = encounter.GetProperty("id").GetInt32();
            if (!CurrentFflogsEncounterTable.IsSupportedEncounter(encounterId))
            {
                continue;
            }

            result.Add(new FflogsEncounterCatalogEntry(
                encounterId,
                encounter.GetProperty("name").GetString() ?? string.Empty,
                zoneName,
                frozen));
        }

        if (result.Count == 0)
        {
            throw new InvalidOperationException("FFLogs returned no encounters for the current ranking tier.");
        }

        lock (cacheLock)
        {
            encounters = result;
            catalogFetchedAt = DateTimeOffset.UtcNow;
        }
        await SaveCacheAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<FflogsCurveCacheEntry> BuildCurveAsync(
        int encounterId,
        int difficulty,
        string specName,
        CancellationToken cancellationToken)
    {
        if (!CurrentFflogsEncounterTable.IsSupportedRanking(encounterId, difficulty))
        {
            throw new InvalidOperationException(
                $"FFLogs encounter {encounterId} difficulty {difficulty} is outside the current ranking tier.");
        }

        lock (cacheLock)
        {
            if (!encounters.Any(entry => entry.Id == encounterId && !entry.Frozen))
            {
                throw new InvalidOperationException(
                    $"FFLogs encounter {encounterId} is not in the active ranking catalog.");
            }
        }

        var pages = new Dictionary<int, FflogsRankingPage>();
        async Task<FflogsRankingPage> GetPage(int page)
        {
            if (pages.TryGetValue(page, out var cached))
            {
                return cached;
            }

            var fetched = await FetchRankingPageAsync(
                encounterId,
                difficulty,
                specName,
                page,
                cancellationToken).ConfigureAwait(false);
            pages[page] = fetched;
            return fetched;
        }

        var first = await GetPage(1).ConfigureAwait(false);
        if (first.Amounts.Count == 0)
        {
            throw new InvalidOperationException("FFLogs returned no ranking samples for this encounter and job.");
        }

        var lastPage = 1;
        if (first.HasMorePages)
        {
            var low = 1;
            var high = 2;
            while (high <= MaximumPage && (await GetPage(high).ConfigureAwait(false)).HasMorePages)
            {
                low = high;
                high *= 2;
            }

            if (high > MaximumPage)
            {
                throw new InvalidOperationException("FFLogs ranking list exceeded the safe pagination limit.");
            }

            while (low + 1 < high)
            {
                var middle = low + ((high - low) / 2);
                if ((await GetPage(middle).ConfigureAwait(false)).HasMorePages)
                {
                    low = middle;
                }
                else
                {
                    high = middle;
                }
            }
            lastPage = high;
        }

        var last = await GetPage(lastPage).ConfigureAwait(false);
        while (lastPage > 1 && last.Amounts.Count == 0)
        {
            lastPage--;
            last = await GetPage(lastPage).ConfigureAwait(false);
        }
        var total = ((lastPage - 1) * PageSize) + last.Amounts.Count;
        if (total <= 0)
        {
            throw new InvalidOperationException("FFLogs returned an empty ranking sample set.");
        }

        var points = new List<FflogsCurvePoint>();
        foreach (var percentile in PercentilePoints)
        {
            var rank = percentile >= 100
                ? 1
                : percentile <= 0
                    ? total
                    : Math.Max(1, (int)Math.Ceiling(total * ((100 - percentile) / 100)));
            var pageNumber = ((rank - 1) / PageSize) + 1;
            var offset = (rank - 1) % PageSize;
            var page = await GetPage(pageNumber).ConfigureAwait(false);
            if (page.Amounts.Count == 0)
            {
                continue;
            }
            points.Add(new FflogsCurvePoint(percentile, page.Amounts[Math.Min(offset, page.Amounts.Count - 1)]));
        }

        return new FflogsCurveCacheEntry(
            encounterId,
            first.EncounterName,
            specName,
            DateTimeOffset.UtcNow,
            points,
            difficulty,
            CurrentFflogsEncounterTable.RankingRegion,
            CurrentFflogsEncounterTable.RankingPartition,
            CurrentFflogsEncounterTable.RankingMetric,
            CurrentCurveFormatVersion);
    }

    private async Task<FflogsRankingPage> FetchRankingPageAsync(
        int encounterId,
        int difficulty,
        string specName,
        int page,
        CancellationToken cancellationToken)
    {
        const string query = """
            query RankingPage(
              $encounterId: Int!,
              $difficulty: Int!,
              $specName: String!,
              $page: Int!,
              $serverRegion: String!,
              $partition: Int!,
              $metric: CharacterRankingMetricType!) {
              worldData {
                encounter(id: $encounterId) {
                  name
                  characterRankings(
                    metric: $metric,
                    difficulty: $difficulty,
                    specName: $specName,
                    serverRegion: $serverRegion,
                    partition: $partition,
                    page: $page)
                }
              }
            }
            """;
        using var document = await QueryAsync(
            query,
            new
            {
                encounterId,
                difficulty,
                specName,
                page,
                serverRegion = CurrentFflogsEncounterTable.RankingRegion,
                partition = CurrentFflogsEncounterTable.RankingPartition,
                metric = CurrentFflogsEncounterTable.RankingMetric,
            },
            cancellationToken).ConfigureAwait(false);
        var encounter = document.RootElement.GetProperty("data").GetProperty("worldData").GetProperty("encounter");
        if (encounter.ValueKind == JsonValueKind.Null)
        {
            throw new InvalidOperationException($"FFLogs encounter {encounterId} was not found.");
        }

        var rankings = encounter.GetProperty("characterRankings");
        var amounts = rankings.GetProperty("rankings")
            .EnumerateArray()
            .Select(item => item.GetProperty("amount").GetDouble())
            .ToArray();
        return new FflogsRankingPage(
            encounter.GetProperty("name").GetString() ?? encounterId.ToString(),
            rankings.TryGetProperty("hasMorePages", out var hasMore) && hasMore.ValueKind == JsonValueKind.True,
            amounts);
    }

    private async Task<JsonDocument> QueryAsync(string query, object variables, CancellationToken cancellationToken)
    {
        await apiGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var token = await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Post, GraphQlEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = new StringContent(
                JsonSerializer.Serialize(new { query, variables }),
                Encoding.UTF8,
                "application/json");
            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"FFLogs API returned HTTP {(int)response.StatusCode}.");
            }

            var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("errors", out var errors))
            {
                var message = "Unknown GraphQL error.";
                foreach (var error in errors.EnumerateArray())
                {
                    if (error.ValueKind == JsonValueKind.Object &&
                        error.TryGetProperty("message", out var errorMessage) &&
                        !string.IsNullOrWhiteSpace(errorMessage.GetString()))
                    {
                        message = errorMessage.GetString()!;
                        break;
                    }
                }
                document.Dispose();
                throw new InvalidOperationException($"FFLogs API error: {message}");
            }

            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
            return document;
        }
        finally
        {
            apiGate.Release();
        }
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        var settings = GetSettingsSnapshot();
        lock (tokenLock)
        {
            if (!string.IsNullOrWhiteSpace(accessToken) &&
                accessTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1) &&
                string.Equals(tokenClientId, settings.ClientId, StringComparison.Ordinal) &&
                string.Equals(tokenClientSecret, settings.ClientSecret, StringComparison.Ordinal))
            {
                return accessToken;
            }
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint);
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.ClientId}:{settings.ClientSecret}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
        });
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("FFLogs API authentication failed. Check the client ID and secret.");
        }

        using var tokenDocument = JsonDocument.Parse(body);
        var newAccessToken = tokenDocument.RootElement.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("FFLogs did not return an access token.");
        var expiresIn = tokenDocument.RootElement.TryGetProperty("expires_in", out var expires)
            ? expires.GetInt32()
            : 3600;
        lock (tokenLock)
        {
            accessToken = newAccessToken;
            accessTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
            tokenClientId = settings.ClientId;
            tokenClientSecret = settings.ClientSecret;
        }
        return newAccessToken;
    }

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(cachePath))
            {
                return;
            }

            var cache = JsonSerializer.Deserialize<FflogsCacheDocument>(File.ReadAllText(cachePath));
            if (cache is null)
            {
                return;
            }

            encounters = (cache.Encounters ?? [])
                .Where(static encounter =>
                    CurrentFflogsEncounterTable.IsSupportedEncounter(encounter.Id) &&
                    !encounter.Frozen)
                .ToArray();
            catalogFetchedAt = encounters.Count > 0 ? cache.CatalogFetchedAt : default;
            foreach (var curve in cache.Curves ?? [])
            {
                if (CurrentFflogsEncounterTable.IsSupportedRanking(curve.EncounterId, curve.Difficulty) &&
                    curve.FormatVersion == CurrentCurveFormatVersion &&
                    string.Equals(
                        curve.Region,
                        CurrentFflogsEncounterTable.RankingRegion,
                        StringComparison.OrdinalIgnoreCase) &&
                    curve.Partition == CurrentFflogsEncounterTable.RankingPartition &&
                    string.Equals(
                        curve.Metric,
                        CurrentFflogsEncounterTable.RankingMetric,
                        StringComparison.OrdinalIgnoreCase))
                {
                    curves[CurveKey(curve.EncounterId, curve.Difficulty, curve.SpecName)] = curve;
                }
            }
        }
        catch (Exception ex)
        {
            logger.Warning($"FFLogs estimate cache could not be loaded: {ex.Message}");
        }
    }

    internal async Task SaveCacheAsync(CancellationToken cancellationToken)
    {
        var acquired = false;
        var temporaryPath = CreateCacheTemporaryPath(cachePath);
        try
        {
            await cacheWriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;
            FflogsCurveCacheEntry[] curveSnapshot;
            FflogsEncounterCatalogEntry[] encounterSnapshot;
            DateTimeOffset fetchedAtSnapshot;
            lock (cacheLock)
            {
                curveSnapshot = curves.Values.ToArray();
                encounterSnapshot = encounters.ToArray();
                fetchedAtSnapshot = catalogFetchedAt;
            }
            var document = new FflogsCacheDocument(fetchedAtSnapshot, encounterSnapshot, curveSnapshot);
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            await File.WriteAllTextAsync(
                    temporaryPath,
                    JsonSerializer.Serialize(document),
                    cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporaryPath, cachePath, overwrite: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.Warning($"FFLogs estimate cache could not be saved: {ex.Message}");
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception ex)
            {
                logger.Warning($"FFLogs temporary cache file could not be removed: {ex.Message}");
            }

            if (acquired)
            {
                cacheWriteGate.Release();
            }
        }
    }

    internal static string CreateCacheTemporaryPath(string destinationPath)
        => $"{destinationPath}.{Guid.NewGuid():N}.tmp";

    private FflogsSettings GetSettingsSnapshot() => getSettings().Snapshot();

    private (uint TerritoryId, string ZoneName, int Phase) GetEncounterContext()
    {
        lock (encounterContextLock)
        {
            return (currentTerritoryId, currentZoneName, currentPhase);
        }
    }

    private void SetInactiveContentStatus()
    {
        var context = GetEncounterContext();
        var location = !string.IsNullOrWhiteSpace(context.ZoneName)
            ? context.ZoneName
            : $"territory {context.TerritoryId}";
        SetStatus(
            FflogsEstimateState.InactiveContent,
            $"{location} is not part of the current FFLogs ranking tier.");
    }

    private bool TryStartBackgroundTask(Func<CancellationToken, Task> work)
    {
        Task task;
        lock (backgroundTaskLock)
        {
            if (shutdownStarted)
            {
                return false;
            }

            task = Task.Run(() => work(lifetime.Token), CancellationToken.None);
            backgroundTasks.Add(task);
        }

        _ = task.ContinueWith(
            completedTask =>
            {
                if (completedTask.IsFaulted)
                {
                    logger.Error(
                        completedTask.Exception?.GetBaseException()
                        ?? new InvalidOperationException("Unknown FFLogs background-task failure."),
                        "FFLogs background task failed outside its worker error boundary.");
                }

                lock (backgroundTaskLock)
                {
                    backgroundTasks.Remove(completedTask);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return true;
    }

    private void SetStatus(FflogsEstimateState stateValue, string message)
    {
        lock (statusLock)
        {
            status = new FflogsEstimateStatus(stateValue, message);
        }
    }

    private static bool IsExpired(DateTimeOffset fetchedAt, int hours)
        => fetchedAt == default || fetchedAt.AddHours(Math.Clamp(hours, 1, 168)) <= DateTimeOffset.UtcNow;

    private static string CurveKey(int encounterId, int difficulty, string specName)
        => $"{encounterId}:{difficulty}:{specName}";

    private static string ToFflogsSpecName(string job) => job.Trim().ToUpperInvariant() switch
    {
        "PLD" => "Paladin",
        "WAR" => "Warrior",
        "DRK" => "DarkKnight",
        "GNB" => "Gunbreaker",
        "WHM" => "WhiteMage",
        "SCH" => "Scholar",
        "AST" => "Astrologian",
        "SGE" => "Sage",
        "MNK" => "Monk",
        "DRG" => "Dragoon",
        "NIN" => "Ninja",
        "SAM" => "Samurai",
        "RPR" => "Reaper",
        "VPR" => "Viper",
        "BRD" => "Bard",
        "MCH" => "Machinist",
        "DNC" => "Dancer",
        "BLM" => "BlackMage",
        "SMN" => "Summoner",
        "RDM" => "RedMage",
        "PCT" => "Pictomancer",
        "BLU" => "BlueMage",
        var value => value,
    };

    private static Vector4 Rgb(byte red, byte green, byte blue)
        => new(red / 255f, green / 255f, blue / 255f, 1);

    public void BeginShutdown()
    {
        var shouldCancel = false;
        lock (backgroundTaskLock)
        {
            if (!shutdownStarted)
            {
                shutdownStarted = true;
                shouldCancel = true;
            }
        }

        if (shouldCancel)
        {
            lifetime.Cancel();
        }
    }

    public ValueTask DisposeAsync()
    {
        BeginShutdown();
        lock (backgroundTaskLock)
        {
            disposeTask ??= DisposeCoreAsync(backgroundTasks.ToArray());
            return new ValueTask(disposeTask);
        }
    }

    private async Task DisposeCoreAsync(Task[] tasks)
    {
        try
        {
            // Dispose owns these tasks; a bounded wait would only move their continuations
            // past the plugin assembly-load-context lifetime.
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.Error(ex, "FFLogs background shutdown failed.");
        }

        DisposeResources();
    }

    private void DisposeResources()
    {
        if (Interlocked.Exchange(ref resourcesDisposed, 1) != 0)
        {
            return;
        }

        httpClient.Dispose();
        apiGate.Dispose();
        cacheWriteGate.Dispose();
        lifetime.Dispose();
    }
}

public sealed record FflogsCurvePoint(double Percentile, double Amount);

public sealed record FflogsCurveCacheEntry(
    int EncounterId,
    string EncounterName,
    string SpecName,
    DateTimeOffset FetchedAt,
    IReadOnlyList<FflogsCurvePoint> Points,
    int Difficulty = 0,
    string Region = "",
    int Partition = 0,
    string Metric = "",
    int FormatVersion = 0);

public sealed record FflogsEncounterCatalogEntry(int Id, string Name, string ZoneName, bool Frozen);

public sealed record FflogsCacheDocument(
    DateTimeOffset CatalogFetchedAt,
    IReadOnlyList<FflogsEncounterCatalogEntry> Encounters,
    IReadOnlyList<FflogsCurveCacheEntry> Curves);

internal sealed record FflogsRankingPage(string EncounterName, bool HasMorePages, IReadOnlyList<double> Amounts);

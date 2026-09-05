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
    RequestsPaused,
    RetryWaiting,
}

public sealed record FflogsEstimateStatus(FflogsEstimateState State, string Message)
{
    public FflogsFailureKind? FailureKind { get; init; }
    public DateTimeOffset? RetryAt { get; init; }
}

public sealed record FflogsActiveEncounter(
    uint TerritoryId,
    int Phase,
    int EncounterId,
    string EncounterName,
    int Difficulty);

public sealed record FflogsEstimate(
    double Percentile,
    Vector4 Color,
    string EncounterName,
    DateTimeOffset DataUpdatedAt,
    string Metric,
    bool IsStale)
{
    public int Score => Math.Clamp((int)Math.Round(Percentile), 0, 100);
}

public sealed record FflogsReferenceSnapshot(
    string Region,
    int? Partition,
    string Metric,
    DateTimeOffset? LatestDataUpdatedAt,
    int CurveCount);

public sealed class FflogsEstimateService : IAsyncDisposable
{
    private const string TokenEndpoint = "https://www.fflogs.com/oauth/token";
    private const string GraphQlEndpoint = "https://www.fflogs.com/api/v2/client";
    private const int PageSize = 100;
    private const int MaximumPage = 4096;
    internal const int CurrentCurveFormatVersion = 2;
    private static readonly double[] PercentilePoints =
    [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9,
        10, 11, 12, 13, 14, 15, 16, 17, 18, 19,
        20, 21, 22, 23, 24, 25,
        30, 35, 40, 45, 50, 55, 60, 65, 70, 75, 80, 85, 90,
        95, 96, 97, 98, 99, 100,
    ];

    private readonly Func<FflogsSettings> getSettings;
    private readonly Func<bool> useChineseRankings;
    private readonly string cachePath;
    private readonly PluginLogger logger;
    private readonly HttpClient httpClient;
    private readonly TimeProvider timeProvider;
    private readonly FflogsRequestGuard requestGuard;
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
        PluginLogger logger,
        Func<bool>? useChineseRankings = null)
        : this(getSettings, cachePath, logger,
            new HttpClient { Timeout = TimeSpan.FromSeconds(20) }, TimeProvider.System, useChineseRankings)
    {
    }

    // Inject transport/time only for deterministic offline failure/recovery tests.
    internal FflogsEstimateService(
        Func<FflogsSettings> getSettings,
        string cachePath,
        PluginLogger logger,
        HttpClient httpClient,
        TimeProvider timeProvider,
        Func<bool>? useChineseRankings = null)
    {
        this.getSettings = getSettings;
        this.cachePath = cachePath;
        this.logger = logger;
        this.httpClient = httpClient;
        this.timeProvider = timeProvider;
        requestGuard = new FflogsRequestGuard(timeProvider);
        this.useChineseRankings = useChineseRankings ?? (static () => true);
        LoadCache();
        CanUseApi(GetSettingsSnapshot());
    }

    public FflogsEstimateStatus Status
    {
        get
        {
            var settings = GetSettingsSnapshot();
            requestGuard.Synchronize(settings);
            if (HasApiAccess(settings) && requestGuard.Failure is { } failure)
                return failure;
            lock (statusLock)
            {
                return status;
            }
        }
    }

    public FflogsReferenceSnapshot ReferenceSnapshot
    {
        get
        {
            lock (cacheLock)
            {
                var scope = GetRankingScope();
                var matchingCurves = curves.Values
                    .Where(curve => CurveMatchesScope(curve, scope))
                    .ToArray();
                return new FflogsReferenceSnapshot(
                    scope.DisplayRegion,
                    scope.Partition,
                    CurrentFflogsEncounterTable.RankingMetric.ToUpperInvariant(),
                    matchingCurves.Length == 0
                        ? null
                        : matchingCurves.Max(static curve => curve.FetchedAt),
                    matchingCurves.Length);
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
        if (!settings.Enabled)
        {
            return encounter;
        }
        var canRefresh = HasApiAccess(settings);

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

                if (estimate.IsStale)
                {
                    missingSpecs.Add(ToFflogsSpecName(rankingCombatant.Job));
                }

                changed = true;
                return combatant with
                {
                    FflogsPercentile = estimate.Percentile,
                    FflogsEncounterName = estimate.EncounterName,
                    FflogsDataUpdatedAt = estimate.DataUpdatedAt,
                    FflogsMetric = estimate.Metric,
                    FflogsDataStale = estimate.IsStale,
                };
            })
            .ToArray();

        // Active snapshots warm every party job in the background. A finished
        // encounter only persists already available estimates and must not start
        // new network work during shutdown/finalization.
        if (rankingEncounter.IsActive && canRefresh)
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
        if (!settings.Enabled)
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
            if (estimate.IsStale && HasApiAccess(settings))
            {
                QueueCurveLoad(ToFflogsSpecName(combatant.Job));
            }
            return estimate;
        }

        if (CanUseApi(settings))
        {
            QueueCurveLoad(ToFflogsSpecName(combatant.Job));
        }
        return null;
    }

    internal static FflogsEstimate? GetPersistedEstimate(Combatant combatant)
    {
        if (combatant.FflogsPercentile is not { } percentile ||
            !double.IsFinite(percentile) ||
            percentile < 0 ||
            percentile > 100 ||
            string.IsNullOrWhiteSpace(combatant.FflogsEncounterName) ||
            combatant.FflogsDataUpdatedAt is not { } updatedAt ||
            updatedAt == default ||
            !string.Equals(
                combatant.FflogsMetric,
                CurrentFflogsEncounterTable.RankingMetric,
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new FflogsEstimate(
            percentile,
            ColorForPercentile(percentile),
            combatant.FflogsEncounterName,
            updatedAt,
            CurrentFflogsEncounterTable.RankingMetric.ToUpperInvariant(),
            combatant.FflogsDataStale);
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
        var key = CurveKey(
            GetRankingScope(),
            activeEncounter.EncounterId,
            activeEncounter.Difficulty,
            specName);
        FflogsCurveCacheEntry? curve;
        lock (cacheLock)
        {
            curves.TryGetValue(key, out curve);
        }

        if (curve is null)
        {
            return null;
        }

        var encounterDps = combatant.Dps > 0
            ? combatant.Dps
            : combatant.TotalDamage / Math.Max(1, encounter.EffectiveDuration.TotalSeconds);
        var percentile = EstimatePercentile(curve.Points, encounterDps);
        var isStale = IsExpired(curve.FetchedAt, settings.CacheHours);
        if (updateStatus)
        {
            SetStatus(
                FflogsEstimateState.Ready,
                $"FFLogs estimate ready: {curve.EncounterName} / {specName}.");
        }
        return new FflogsEstimate(
            percentile,
            ColorForPercentile(percentile),
            curve.EncounterName,
            curve.FetchedAt,
            curve.Metric.ToUpperInvariant(),
            isStale);
    }

    public void RequestRefresh(Encounter? encounter)
    {
        requestGuard.RequestManualRetry();
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

        var specNames = ResolveFflogsSpecs(encounter);
        if (specNames.Count == 0)
        {
            QueueCatalogRefresh();
            return;
        }

        // Keep the old curves visible until a complete replacement has been fetched.
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
        requestGuard.Synchronize(GetSettingsSnapshot());
        ClearAccessToken();

        var settings = GetSettingsSnapshot();
        if (CanUseApi(settings))
        {
            if (ActiveEncounter is null)
                SetInactiveContentStatus();
            else
                SetStatus(FflogsEstimateState.Idle, "FFLogs credentials are ready to be tested.");
        }
    }

    private void ClearAccessToken()
    {
        lock (tokenLock)
        {
            accessToken = string.Empty;
            accessTokenExpiresAt = default;
            tokenClientId = string.Empty;
            tokenClientSecret = string.Empty;
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
        var generation = requestGuard.Synchronize(settings);
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
        return requestGuard.CanRequest(generation);
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
        var settings = GetSettingsSnapshot();
        if (string.IsNullOrWhiteSpace(specName) || !CanUseApi(settings))
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
        var generation = requestGuard.Synchronize(settings);
        var difficulty = activeEncounter.Difficulty;
        var scope = GetRankingScope();
        var loadKey = $"{generation}:{CurveKey(scope, encounterId, difficulty, specName)}";
        if (!loading.TryAdd(loadKey, 0))
        {
            return;
        }

        SetStatus(FflogsEstimateState.Loading, "Loading FFLogs public ranking samples…");
        if (!TryStartBackgroundTask(async cancellationToken =>
        {
            try
            {
                await EnsureCatalogAsync(generation, cancellationToken).ConfigureAwait(false);
                var curve = await BuildCurveAsync(
                    scope,
                    encounterId,
                    difficulty,
                    specName,
                    generation,
                    cancellationToken).ConfigureAwait(false);
                requestGuard.Check(generation);
                lock (cacheLock)
                {
                    curves[CurveKey(scope, encounterId, difficulty, specName)] = curve;
                }
                await SaveCacheAsync(cancellationToken).ConfigureAwait(false);
                RecordRequestSuccess(generation);
                var currentEncounter = ActiveEncounter;
                if (scope == GetRankingScope() &&
                    currentEncounter?.EncounterId == encounterId &&
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
            catch (FflogsRequestSuppressedException)
            {
                // A sibling request already reported this failure, or credentials changed.
            }
            catch (Exception ex)
            {
                RecordRequestFailure(generation, FflogsRequestException.FromException(ex, "ranking data"));
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
        var settings = GetSettingsSnapshot();
        if (!CanUseApi(settings))
            return;
        var generation = requestGuard.Synchronize(settings);
        var loadKey = $"catalog:{generation}";
        if (!loading.TryAdd(loadKey, 0))
        {
            return;
        }

        SetStatus(FflogsEstimateState.Loading, "Refreshing FFLogs encounter catalog…");
        if (!TryStartBackgroundTask(async cancellationToken =>
        {
            try
            {
                if (await EnsureCatalogAsync(generation, cancellationToken, forceRefresh).ConfigureAwait(false))
                    RecordRequestSuccess(generation);
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
            catch (FflogsRequestSuppressedException)
            {
            }
            catch (Exception ex)
            {
                RecordRequestFailure(generation, FflogsRequestException.FromException(ex, "encounter catalog"));
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

    private async Task<bool> EnsureCatalogAsync(long generation, CancellationToken cancellationToken, bool forceRefresh = false)
    {
        requestGuard.Check(generation);
        lock (cacheLock)
        {
            if (!forceRefresh && encounters.Count > 0 && !IsExpired(catalogFetchedAt, 24))
            {
                return false;
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
            generation,
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

        requestGuard.Check(generation);
        lock (cacheLock)
        {
            encounters = result;
            catalogFetchedAt = DateTimeOffset.UtcNow;
        }
        await SaveCacheAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<FflogsCurveCacheEntry> BuildCurveAsync(
        FflogsRankingScope scope,
        int encounterId,
        int difficulty,
        string specName,
        long generation,
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
                scope,
                encounterId,
                difficulty,
                specName,
                page,
                generation,
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
            scope.CacheRegion,
            scope.Partition,
            CurrentFflogsEncounterTable.RankingMetric,
            CurrentCurveFormatVersion);
    }

    private async Task<FflogsRankingPage> FetchRankingPageAsync(
        FflogsRankingScope scope,
        int encounterId,
        int difficulty,
        string specName,
        int page,
        long generation,
        CancellationToken cancellationToken)
    {
        const string query = """
            query RankingPage(
              $encounterId: Int!,
              $difficulty: Int!,
              $specName: String!,
              $page: Int!,
              $serverRegion: String,
              $partition: Int,
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
                serverRegion = scope.ServerRegion,
                partition = scope.Partition,
                metric = CurrentFflogsEncounterTable.RankingMetric,
            },
            generation,
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

    private async Task<JsonDocument> QueryAsync(string query, object variables, long generation, CancellationToken cancellationToken)
    {
        await apiGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var stage = "token endpoint";
        try
        {
            // Check again after acquiring the gate: other jobs may have been queued
            // before the first failure, disabling the feature, or a credential edit.
            var settings = GetSettingsSnapshot();
            requestGuard.Synchronize(settings);
            requestGuard.Check(generation);
            if (!HasApiAccess(settings))
                throw new FflogsRequestSuppressedException();
            var token = await GetAccessTokenAsync(settings, generation, cancellationToken).ConfigureAwait(false);
            requestGuard.Synchronize(GetSettingsSnapshot());
            requestGuard.Check(generation);
            stage = "ranking API";
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
                // A revoked/expired bearer token is retried with a fresh token after
                // backoff, not misreported as an invalid Client Secret.
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    ClearAccessToken();
                throw FflogsRequestException.FromResponse(response, body, false, timeProvider.GetUtcNow());
            }

            var document = JsonDocument.Parse(body);
            try
            {
                if (document.RootElement.TryGetProperty("errors", out var errors) &&
                    errors.ValueKind != JsonValueKind.Null &&
                    !(errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() == 0))
                {
                    throw new FflogsRequestException(FflogsFailureKind.InvalidResponse,
                        "FFLogs ranking API: HTTP 200 with GraphQL errors.");
                }
                requestGuard.Check(generation);
                await Task.Delay(150, cancellationToken).ConfigureAwait(false);
                return document;
            }
            catch
            {
                document.Dispose();
                throw;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (FflogsRequestSuppressedException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Publish the failure before releasing apiGate. Worker catch blocks must
            // not log it again or let queued requests bypass the new cooldown.
            RecordRequestFailure(generation, FflogsRequestException.FromException(ex, stage));
            throw new FflogsRequestSuppressedException();
        }
        finally
        {
            apiGate.Release();
        }
    }

    private async Task<string> GetAccessTokenAsync(FflogsSettings settings, long generation, CancellationToken cancellationToken)
    {
        lock (tokenLock)
        {
            if (!string.IsNullOrWhiteSpace(accessToken) &&
                accessTokenExpiresAt > timeProvider.GetUtcNow().AddMinutes(1) &&
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
            throw FflogsRequestException.FromResponse(response, body, true, timeProvider.GetUtcNow());
        }

        using var tokenDocument = JsonDocument.Parse(body);
        var newAccessToken = tokenDocument.RootElement.GetProperty("access_token").GetString();
        if (string.IsNullOrWhiteSpace(newAccessToken))
            throw new FflogsRequestException(FflogsFailureKind.InvalidResponse, "FFLogs token endpoint: missing access token.");
        var expiresIn = tokenDocument.RootElement.TryGetProperty("expires_in", out var expires)
            ? expires.GetInt32()
            : 3600;
        lock (tokenLock)
        {
            requestGuard.Synchronize(GetSettingsSnapshot());
            requestGuard.Check(generation);
            accessToken = newAccessToken;
            accessTokenExpiresAt = timeProvider.GetUtcNow().AddSeconds(Math.Clamp(expiresIn, 0, 86400));
            tokenClientId = settings.ClientId;
            tokenClientSecret = settings.ClientSecret;
        }
        return newAccessToken;
    }

    private void RecordRequestFailure(long generation, FflogsRequestException failure)
    {
        // Synchronizing also discards a late failure from credentials replaced while
        // the HTTP request was in flight (including configuration imports).
        requestGuard.Synchronize(GetSettingsSnapshot());
        if (requestGuard.RecordFailure(generation, failure))
            logger.Warning($"{failure.Message} Automatic FFLogs requests are " +
                (requestGuard.Failure?.State == FflogsEstimateState.RequestsPaused
                    ? "paused; update credentials or use Test and refresh."
                    : $"backing off until {requestGuard.Failure?.RetryAt:O}."));
    }

    private void RecordRequestSuccess(long generation)
    {
        requestGuard.Synchronize(GetSettingsSnapshot());
        requestGuard.Check(generation);
        if (requestGuard.RecordSuccess(generation))
            logger.Information("FFLogs requests recovered; ranking data refreshed successfully.");
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
                    CurrentFflogsEncounterTable.TryGetRankingScope(
                        curve.Region,
                        curve.Partition,
                        out var scope) &&
                    string.Equals(
                        curve.Metric,
                        CurrentFflogsEncounterTable.RankingMetric,
                        StringComparison.OrdinalIgnoreCase))
                {
                    curves[CurveKey(scope, curve.EncounterId, curve.Difficulty, curve.SpecName)] = curve;
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

    private FflogsRankingScope GetRankingScope()
        => CurrentFflogsEncounterTable.GetRankingScope(useChineseRankings());

    private static bool CurveMatchesScope(
        FflogsCurveCacheEntry curve,
        FflogsRankingScope scope)
        => string.Equals(curve.Region, scope.CacheRegion, StringComparison.OrdinalIgnoreCase) &&
           curve.Partition == scope.Partition;

    private static string CurveKey(
        FflogsRankingScope scope,
        int encounterId,
        int difficulty,
        string specName)
        // The key repeats the persisted validation fields so an in-memory cache can
        // never cross a region, partition, or metric boundary after a mode switch.
        => $"{scope.CacheRegion}:" +
           $"{scope.Partition?.ToString() ?? "latest"}:" +
           $"{CurrentFflogsEncounterTable.RankingMetric}:" +
           $"{encounterId}:{difficulty}:{specName}";

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
    int? Partition = 0,
    string Metric = "",
    int FormatVersion = 0);

public sealed record FflogsEncounterCatalogEntry(int Id, string Name, string ZoneName, bool Frozen);

public sealed record FflogsCacheDocument(
    DateTimeOffset CatalogFetchedAt,
    IReadOnlyList<FflogsEncounterCatalogEntry> Encounters,
    IReadOnlyList<FflogsCurveCacheEntry> Curves);

internal sealed record FflogsRankingPage(string EncounterName, bool HasMorePages, IReadOnlyList<double> Amounts);

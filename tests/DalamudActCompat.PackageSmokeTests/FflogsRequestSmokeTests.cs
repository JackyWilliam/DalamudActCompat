using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using Dalamud.Plugin.Services;
using DalamudActCompat.Core.Models;
using DalamudActCompat.Fflogs;
using DalamudActCompat.Infrastructure.Logging;

internal static class FflogsRequestSmokeTests
{
    private const string SecretSentinel = "synthetic-secret-never-log";
    private const string TokenSentinel = "synthetic-token-never-log";

    public static async Task RunAsync(string testRoot)
    {
        ValidateClassificationAndBackoff();
        await ValidateQueuedAuthenticationFailureAsync(testRoot);
        await ValidateTemporaryFailuresAsync(testRoot);
        await ValidateCredentialRaceAsync(testRoot, disable: false);
        await ValidateCredentialRaceAsync(testRoot, disable: true);
        await ValidateCredentialRaceAsync(testRoot, disable: true, oldSuccess: true);
        await ValidateCacheAndBearerRecoveryAsync(testRoot);
        await ValidateRankingPageFailureAsync(testRoot);
        await ValidateShutdownAsync(testRoot);
        Console.WriteLine("FFLogs request protection smoke tests passed (offline HTTP + virtual time).");
    }

    private static void ValidateClassificationAndBackoff()
    {
        var clock = new TestClock();
        foreach (var (code, body, token, expected) in new[]
                 {
                     (400, "{\"error\":\"invalid_client\"}", true, FflogsFailureKind.CredentialsRejected),
                     (401, "not JSON", true, FflogsFailureKind.CredentialsRejected),
                     (403, "<html>proxy denial</html>", true, FflogsFailureKind.AccessDenied),
                     (429, "{\"error\":\"invalid_client\"}", true, FflogsFailureKind.RateLimited),
                     (503, "{\"error\":\"invalid_client\"}", true, FflogsFailureKind.ServerError),
                     (400, "{}", true, FflogsFailureKind.InvalidResponse),
                     (408, "{}", true, FflogsFailureKind.Timeout),
                     (401, "{}", false, FflogsFailureKind.InvalidResponse),
                 })
        {
            using var response = Response(code, body);
            var failure = FflogsRequestException.FromResponse(response, body, token, clock.GetUtcNow());
            Check(failure.Kind == expected && failure.Message.Contains($"HTTP {code}"), "HTTP/OAuth classification regressed.");
        }
        using var unsafeResponse = Response(400, "{}");
        var unsafeBody = JsonSerializer.Serialize(new { error = SecretSentinel, error_description = TokenSentinel });
        var safeFailure = FflogsRequestException.FromResponse(unsafeResponse, unsafeBody, true, clock.GetUtcNow());
        Check(!safeFailure.ToString().Contains(SecretSentinel) && !safeFailure.ToString().Contains(TokenSentinel),
            "An arbitrary OAuth response leaked into diagnostics.");

        var settings = Settings();
        var guard = new FflogsRequestGuard(clock);
        foreach (var seconds in new[] { 30, 60, 120, 240, 300, 300 })
        {
            var generation = guard.Synchronize(settings);
            Check(guard.CanRequest(generation), "The retry did not become eligible.");
            Check(guard.RecordFailure(generation, new(FflogsFailureKind.Timeout, "safe timeout")), "Failure was lost.");
            var current = guard.Synchronize(settings);
            Check(!guard.CanRequest(generation), "Queued work survived an attempt failure.");
            Check(guard.Failure?.RetryAt == clock.GetUtcNow().AddSeconds(seconds), "Exponential backoff/cap is wrong.");
            guard.RequestManualRetry();
            Check(!guard.CanRequest(current), "Manual refresh bypassed transient backoff.");
            clock.Advance(TimeSpan.FromSeconds(seconds - 1));
            Check(!guard.CanRequest(current), "Retry happened before its deadline.");
            clock.Advance(TimeSpan.FromSeconds(1));
        }
        var recoveredGeneration = guard.Synchronize(settings);
        Check(guard.RecordSuccess(recoveredGeneration) && guard.Failure is null, "Recovery failed to clear backoff.");
        guard.RecordFailure(recoveredGeneration, new(FflogsFailureKind.Timeout, "timeout after recovery"));
        Check(guard.Failure?.RetryAt == clock.GetUtcNow().AddSeconds(30), "Success did not reset the failure count.");

        var limitGuard = new FflogsRequestGuard(clock);
        using var limited = Response(429, "{}");
        limited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(20));
        var limit = FflogsRequestException.FromResponse(limited, "{}", true, clock.GetUtcNow());
        limitGuard.RecordFailure(limitGuard.Synchronize(settings), limit);
        Check(limitGuard.Failure?.RetryAt == clock.GetUtcNow().AddMinutes(20), "Retry-After delta was capped/ignored.");
        settings = settings.Snapshot();
        settings.ClientSecret = "replacement";
        var limitedGeneration = limitGuard.Synchronize(settings);
        limitGuard.RequestManualRetry();
        Check(!limitGuard.CanRequest(limitedGeneration), "Editing credentials bypassed Retry-After.");
        limited.Headers.RetryAfter = new RetryConditionHeaderValue(clock.GetUtcNow().AddMinutes(40));
        Check(FflogsRequestException.FromResponse(limited, "{}", false, clock.GetUtcNow()).RetryAt == clock.GetUtcNow().AddMinutes(40),
            "Retry-After HTTP date was not honored.");
        limited.Headers.RetryAfter = new RetryConditionHeaderValue(clock.GetUtcNow().AddSeconds(-1));
        Check(FflogsRequestException.FromResponse(limited, "{}", false, clock.GetUtcNow()).RetryAt is null,
            "A past Retry-After date was treated as a future deadline.");

        var pausedGuard = new FflogsRequestGuard(clock);
        var oldGeneration = pausedGuard.Synchronize(settings);
        pausedGuard.RecordFailure(oldGeneration, new(FflogsFailureKind.CredentialsRejected, "HTTP 401"));
        clock.Advance(TimeSpan.FromDays(2));
        Check(!pausedGuard.CanRequest(pausedGuard.Synchronize(settings)), "Invalid credentials automatically retried.");
        pausedGuard.RequestManualRetry();
        Check(pausedGuard.CanRequest(pausedGuard.Synchronize(settings)), "Explicit retry failed to release the auth pause.");
        Check(!pausedGuard.RecordFailure(oldGeneration, new(FflogsFailureKind.Timeout, "old")) &&
              !pausedGuard.RecordSuccess(oldGeneration), "A stale attempt changed a new attempt's state.");
    }

    private static async Task ValidateQueuedAuthenticationFailureAsync(string testRoot)
    {
        await using var fixture = await Fixture.CreateAsync(testRoot);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Handler.Respond = async (_, cancellationToken) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return Response(401, JsonSerializer.Serialize(new { error = "invalid_client", error_description = SecretSentinel }));
        };
        fixture.Enable();
        fixture.Service.NotifyTerritoryChanged(1327, "test duty");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var encounter = PartyEncounter();
        fixture.Service.CaptureAvailableEstimates(encounter);
        fixture.Service.RequestRefresh(encounter);
        release.TrySetResult();
        await DrainAsync(fixture.Service);
        Check(fixture.Handler.Total == 1 && fixture.Log.WarningCount == 1,
            "Queued party/catalog requests repeated a failed authentication request or logged it more than once.");
        Check(fixture.Service.Status is { State: FflogsEstimateState.RequestsPaused, FailureKind: FflogsFailureKind.CredentialsRejected },
            "Credentials rejection was not exposed as a pause.");
        for (var i = 0; i < 200; i++)
        {
            fixture.Service.CaptureAvailableEstimates(encounter);
            fixture.Service.GetEstimate(encounter, "0", "Player 0");
        }
        fixture.Clock.Advance(TimeSpan.FromDays(1));
        fixture.Service.CaptureAvailableEstimates(encounter);
        await DrainAsync(fixture.Service);
        Check(fixture.Handler.Total == 1, "Battle snapshots bypassed the authentication pause.");
        fixture.Handler.Respond = SuccessfulResponseAsync;
        fixture.Service.RequestRefresh(null);
        await DrainAsync(fixture.Service);
        Check(fixture.Handler.Total == 3 && fixture.Service.Status.State == FflogsEstimateState.Ready && fixture.Log.RecoveryCount == 1,
            "Manual retry did not recover with one token + catalog request and a recovery diagnostic.");
        fixture.AssertSafeDiagnostics();
    }

    private static async Task ValidateTemporaryFailuresAsync(string testRoot)
    {
        foreach (var scenario in new[] { "timeout", "network", "server", "limit", "graphql", "malformed", "empty-token" })
        {
            await using var fixture = await Fixture.CreateAsync(testRoot);
            fixture.Handler.Respond = async (request, cancellationToken) =>
            {
                if (scenario == "timeout") throw new TaskCanceledException(SecretSentinel);
                if (scenario == "network") throw new HttpRequestException(SecretSentinel);
                if (scenario == "server") return Response(503, SecretSentinel);
                if (scenario == "limit")
                {
                    var limited = Response(429, SecretSentinel);
                    limited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(10));
                    return limited;
                }
                if (scenario == "empty-token") return Response(200, "{\"access_token\":\"\"}");
                if (IsToken(request)) return await SuccessfulResponseAsync(request, cancellationToken);
                return Response(200, scenario == "graphql"
                    ? JsonSerializer.Serialize(new { errors = new[] { new { message = SecretSentinel } } })
                    : "not-json-" + TokenSentinel);
            };
            fixture.Enable();
            fixture.Service.RequestRefresh(null);
            await DrainAsync(fixture.Service);
            var expectedKind = scenario switch
            {
                "timeout" => FflogsFailureKind.Timeout,
                "network" => FflogsFailureKind.Network,
                "server" => FflogsFailureKind.ServerError,
                "limit" => FflogsFailureKind.RateLimited,
                _ => FflogsFailureKind.InvalidResponse,
            };
            Check(fixture.Service.Status is { State: FflogsEstimateState.RetryWaiting } status && status.FailureKind == expectedKind,
                $"{scenario}: missing classified retry state.");
            var count = fixture.Handler.Total;
            for (var i = 0; i < 50; i++) fixture.Service.RequestRefresh(null);
            await DrainAsync(fixture.Service);
            Check(fixture.Handler.Total == count && fixture.Log.WarningCount == 1, $"{scenario}: cooldown was bypassed.");
            fixture.Clock.Advance(TimeSpan.FromSeconds(scenario == "limit" ? 600 : 30));
            fixture.Handler.Respond = SuccessfulResponseAsync;
            fixture.Service.RequestRefresh(null);
            await DrainAsync(fixture.Service);
            Check(fixture.Service.Status.FailureKind is null && fixture.Log.RecoveryCount == 1,
                $"{scenario}: successful retry did not clear the failure state.");
            fixture.AssertSafeDiagnostics();
        }
    }

    private static async Task ValidateCredentialRaceAsync(string testRoot, bool disable, bool oldSuccess = false)
    {
        await using var fixture = await Fixture.CreateAsync(testRoot);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Handler.Respond = async (request, cancellationToken) =>
        {
            if (fixture.Handler.Total == 1)
            {
                started.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
                return oldSuccess ? await SuccessfulResponseAsync(request, cancellationToken)
                    : Response(401, "{\"error\":\"invalid_client\"}");
            }
            return await SuccessfulResponseAsync(request, cancellationToken);
        };
        fixture.Enable();
        fixture.Service.RequestRefresh(null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Settings = fixture.Settings.Snapshot();
        fixture.Settings.Enabled = !disable;
        fixture.Settings.ClientSecret = "replacement-synthetic-secret";
        // The successful-response case also simulates an imported configuration
        // that replaces settings without going through the credential-edit UI.
        if (!oldSuccess) fixture.Service.NotifyCredentialsChanged();
        fixture.Service.RequestRefresh(null);
        release.TrySetResult();
        await DrainAsync(fixture.Service);
        Check(fixture.Log.WarningCount == 0 && fixture.Service.Status.FailureKind is null,
            "An old credential response overwrote the new configuration's state.");
        Check(fixture.Handler.Total == (disable ? 1 : 3),
            "A disabled request was sent, or new credentials were blocked behind an old loading key.");
        Check(!disable || fixture.Service.Status.State == FflogsEstimateState.Disabled, "Disabling did not preserve disabled state.");
    }

    private static async Task ValidateRankingPageFailureAsync(string testRoot)
    {
        await using var fixture = await Fixture.CreateAsync(testRoot, seedCache: true);
        fixture.Service.NotifyTerritoryChanged(1327, "test duty");
        fixture.Enable();
        var encounter = PartyEncounter() with { Combatants = [PartyEncounter().Combatants[0]] };
        var before = await File.ReadAllTextAsync(fixture.CachePath);
        fixture.Handler.Respond = async (request, cancellationToken) =>
        {
            if (IsToken(request)) return await SuccessfulResponseAsync(request, cancellationToken);
            using var query = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var page = query.RootElement.GetProperty("variables").GetProperty("page").GetInt32();
            if (page == 1)
                return Response(200, JsonSerializer.Serialize(new
                {
                    data = new { worldData = new { encounter = new { name = "Lindwurm", characterRankings = new
                    {
                        hasMorePages = true,
                        rankings = Enumerable.Range(0, 100).Select(i => new { amount = 10_000 - i * 50 }).ToArray(),
                    } } } },
                }));
            var response = Response(429, TokenSentinel);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(10));
            return response;
        };
        fixture.Service.RequestRefresh(encounter);
        await DrainAsync(fixture.Service);
        Check(fixture.Handler.Total == 3 && fixture.Service.Status.FailureKind == FflogsFailureKind.RateLimited &&
              await File.ReadAllTextAsync(fixture.CachePath) == before,
            "Failure on a later ranking page did not retain the old complete curve.");
        fixture.Service.CaptureAvailableEstimates(PartyEncounter());
        fixture.Service.RequestRefresh(encounter);
        fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        fixture.Service.CaptureAvailableEstimates(PartyEncounter());
        await DrainAsync(fixture.Service);
        Check(fixture.Handler.Total == 3, "Ranking-page Retry-After was not shared by other jobs/manual refresh.");
        fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        fixture.Handler.Respond = SuccessfulResponseAsync;
        // A missing job must recover automatically from subsequent battle snapshots.
        fixture.Service.CaptureAvailableEstimates(PartyEncounter());
        await DrainAsync(fixture.Service);
        Check(fixture.Service.Status.State == FflogsEstimateState.Ready && fixture.Log.RecoveryCount == 1 &&
              fixture.Service.ReferenceSnapshot.CurveCount == 8,
            "Automatic battle-driven recovery did not resume all party jobs after the server deadline.");
    }

    private static async Task ValidateCacheAndBearerRecoveryAsync(string testRoot)
    {
        await using var fixture = await Fixture.CreateAsync(testRoot, seedCache: true);
        fixture.Service.NotifyTerritoryChanged(1327, "test duty");
        fixture.Enable();
        var encounter = PartyEncounter() with { Combatants = [PartyEncounter().Combatants[0]] };
        var before = await File.ReadAllTextAsync(fixture.CachePath);
        fixture.Handler.Respond = (request, cancellationToken) => IsToken(request)
            ? SuccessfulResponseAsync(request, cancellationToken)
            : Task.FromResult(Response(401, SecretSentinel));
        fixture.Service.RequestRefresh(encounter);
        await DrainAsync(fixture.Service);
        Check(fixture.Service.Status.State == FflogsEstimateState.RetryWaiting && fixture.Service.ReferenceSnapshot.CurveCount == 1,
            "A rejected bearer token paused credentials permanently or removed the old curve.");
        Check(fixture.Service.GetEstimate(encounter, "0", "Player 0")?.Score == 50 &&
              await File.ReadAllTextAsync(fixture.CachePath) == before,
            "Refreshing unsuccessfully destroyed the visible/persisted estimate cache.");
        Check(fixture.Service.Status.State == FflogsEstimateState.RetryWaiting,
            "Displaying a cached estimate hid the outstanding failure.");

        fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        fixture.Service.NotifyTerritoryChanged(1327, "test duty");
        await DrainAsync(fixture.Service);
        Check(fixture.Log.RecoveryCount == 0 && fixture.Service.Status.FailureKind is not null,
            "Reading an existing catalog was incorrectly reported as a network recovery.");
        fixture.Handler.Respond = SuccessfulResponseAsync;
        fixture.Service.RequestRefresh(encounter);
        await DrainAsync(fixture.Service);
        Check(fixture.Handler.TokenRequests == 2 && fixture.Service.Status.State == FflogsEstimateState.Ready &&
              fixture.Log.RecoveryCount == 1 && await File.ReadAllTextAsync(fixture.CachePath) != before,
            "A rejected cached bearer token was reused, or a successful curve failed to replace the cache.");
        fixture.AssertSafeDiagnostics();
    }

    private static async Task ValidateShutdownAsync(string testRoot)
    {
        await using var fixture = await Fixture.CreateAsync(testRoot);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Handler.Respond = async (_, cancellationToken) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable");
        };
        fixture.Enable();
        fixture.Service.NotifyTerritoryChanged(1327, "test duty");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Service.CaptureAvailableEstimates(PartyEncounter());
        await fixture.Service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Check(fixture.Handler.Total == 1 && fixture.Log.WarningCount == 0 && fixture.Log.ErrorCount == 0,
            "Shutdown cancellation became a retry failure or sent queued requests.");
    }

    private static FflogsSettings Settings() => new() { Enabled = true, ClientId = "synthetic-client", ClientSecret = SecretSentinel };

    private static Encounter PartyEncounter()
    {
        var jobs = new[] { "PLD", "WAR", "WHM", "SGE", "DRG", "NIN", "BRD", "BLM" };
        return new Encounter(Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(-30), null, "test duty", "Lindwurm",
            jobs.Select((job, i) => new Combatant(i.ToString(), $"Player {i}", job, i == 0, 60_000, 0, 0, Dps: 2_000)).ToArray(),
            [], [], [], [], []) { TerritoryId = 1327, CombatDuration = TimeSpan.FromSeconds(30) };
    }

    private static bool IsToken(HttpRequestMessage request) => request.RequestUri?.AbsolutePath == "/oauth/token";
    private static HttpResponseMessage Response(int status, string body) => new((HttpStatusCode)status) { Content = new StringContent(body) };

    private static async Task<HttpResponseMessage> SuccessfulResponseAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (IsToken(request)) return Response(200, JsonSerializer.Serialize(new { access_token = TokenSentinel, expires_in = 3600 }));
        var query = await request.Content!.ReadAsStringAsync(cancellationToken);
        return query.Contains("EncounterCatalog", StringComparison.Ordinal)
            ? Response(200, """{"data":{"worldData":{"zone":{"id":73,"name":"test tier","frozen":false,"encounters":[{"id":104,"name":"Lindwurm"}]}}}}""")
            : Response(200, """{"data":{"worldData":{"encounter":{"name":"Lindwurm","characterRankings":{"hasMorePages":false,"rankings":[{"amount":4000},{"amount":3000},{"amount":2000},{"amount":1000}]}}}}}""");
    }

    private static async Task DrainAsync(FflogsEstimateService service)
    {
        // Observe the real tracked workers instead of sleeping and assuming the queue drained.
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var sync = typeof(FflogsEstimateService).GetField("backgroundTaskLock", flags)!.GetValue(service)!;
        var tasks = (HashSet<Task>)typeof(FflogsEstimateService).GetField("backgroundTasks", flags)!.GetValue(service)!;
        Task[] pending;
        lock (sync) pending = tasks.ToArray();
        await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("FFLogs request protection: " + message);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public FflogsSettings Settings { get; set; } = FflogsRequestSmokeTests.Settings();
        public TestClock Clock { get; } = new();
        public TestHandler Handler { get; } = new();
        public FflogsRequestLogProxy Log { get; }
        public FflogsEstimateService Service { get; }
        public string CachePath { get; }

        private Fixture(string cachePath)
        {
            CachePath = cachePath;
            Settings.Enabled = false;
            var log = DispatchProxy.Create<IPluginLog, FflogsRequestLogProxy>();
            Log = (FflogsRequestLogProxy)(object)log;
            Service = new FflogsEstimateService(() => Settings, cachePath, new PluginLogger(log), new HttpClient(Handler), Clock);
        }

        public static async Task<Fixture> CreateAsync(string testRoot, bool seedCache = false)
        {
            var cachePath = Path.Combine(testRoot, "fflogs-request-tests", Guid.NewGuid().ToString("N"), "cache.json");
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            if (seedCache)
                await File.WriteAllTextAsync(cachePath, JsonSerializer.Serialize(new FflogsCacheDocument(
                    DateTimeOffset.UtcNow,
                    [new FflogsEncounterCatalogEntry(104, "Lindwurm", "test tier", false)],
                    [new FflogsCurveCacheEntry(104, "Lindwurm", "Paladin", DateTimeOffset.UtcNow,
                        [new FflogsCurvePoint(0, 1_000), new FflogsCurvePoint(100, 3_000)],
                        101, "CN", 9, "dps", FflogsEstimateService.CurrentCurveFormatVersion)])));
            return new Fixture(cachePath);
        }

        public void Enable()
        {
            Settings = Settings.Snapshot();
            Settings.Enabled = true;
            Service.NotifyCredentialsChanged();
        }

        public void AssertSafeDiagnostics()
        {
            var diagnostics = string.Join("\n", Log.Messages) + Service.Status.Message;
            Check(!diagnostics.Contains(SecretSentinel) && !diagnostics.Contains(TokenSentinel) && Log.ErrorCount == 0,
                "Secrets/raw exception bodies leaked or safe request failures were logged again as errors.");
        }

        public ValueTask DisposeAsync() => Service.DisposeAsync();
    }

    private sealed class TestClock : TimeProvider
    {
        private long ticks = DateTimeOffset.UtcNow.Ticks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        public void Advance(TimeSpan duration) => Interlocked.Add(ref ticks, duration.Ticks);
    }

    private sealed class TestHandler : HttpMessageHandler
    {
        private int total;
        private int tokenRequests;
        public int Total => Volatile.Read(ref total);
        public int TokenRequests => Volatile.Read(ref tokenRequests);
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } = SuccessfulResponseAsync;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref total);
            if (IsToken(request)) Interlocked.Increment(ref tokenRequests);
            return Respond(request, cancellationToken);
        }
    }
}

public class FflogsRequestLogProxy : DispatchProxy
{
    public ConcurrentQueue<string> Messages { get; } = new();
    public int WarningCount => Messages.Count(message => message.StartsWith("Warning:", StringComparison.Ordinal));
    public int ErrorCount => Messages.Count(message => message.StartsWith("Error:", StringComparison.Ordinal));
    public int RecoveryCount => Messages.Count(message => message.Contains("requests recovered", StringComparison.Ordinal));

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name is "Warning" or "Information" or "Error")
            Messages.Enqueue(targetMethod.Name + ":" + string.Join(" ", args?.Select(value => value?.ToString()) ?? []));
        var type = targetMethod?.ReturnType;
        return type is null || type == typeof(void) ? null : type.IsValueType ? Activator.CreateInstance(type) : null;
    }
}

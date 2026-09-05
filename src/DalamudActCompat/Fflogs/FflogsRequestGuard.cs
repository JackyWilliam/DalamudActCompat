using System.Net;
using System.Text.Json;

namespace DalamudActCompat.Fflogs;

public enum FflogsFailureKind
{
    CredentialsRejected,
    AccessDenied,
    RateLimited,
    ServerError,
    Timeout,
    Network,
    InvalidResponse,
}

// Only locally constructed diagnostics may cross into the UI/log. Response bodies,
// OAuth descriptions and nested transport exceptions can contain sensitive values.
internal sealed class FflogsRequestException(
    FflogsFailureKind kind,
    string message,
    DateTimeOffset? retryAt = null) : Exception(message)
{
    public FflogsFailureKind Kind { get; } = kind;
    public DateTimeOffset? RetryAt { get; } = retryAt;

    internal static FflogsRequestException FromResponse(
        HttpResponseMessage response, string body, bool tokenEndpoint, DateTimeOffset now)
    {
        var code = response.StatusCode;
        var endpoint = tokenEndpoint ? "token endpoint" : "ranking API";
        var kind = code switch
        {
            HttpStatusCode.TooManyRequests => FflogsFailureKind.RateLimited,
            HttpStatusCode.RequestTimeout => FflogsFailureKind.Timeout,
            >= HttpStatusCode.InternalServerError => FflogsFailureKind.ServerError,
            HttpStatusCode.Unauthorized when tokenEndpoint => FflogsFailureKind.CredentialsRejected,
            HttpStatusCode.Forbidden => FflogsFailureKind.AccessDenied,
            _ => FflogsFailureKind.InvalidResponse,
        };
        var oauthCode = tokenEndpoint ? ReadSafeOAuthCode(body) : null;
        if (code is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden &&
            oauthCode is "invalid_client" or "unauthorized_client")
        {
            kind = FflogsFailureKind.CredentialsRejected;
        }

        DateTimeOffset? retryAt = response.Headers.RetryAfter?.Date;
        if (response.Headers.RetryAfter?.Delta is { } delta)
        {
            retryAt = now + (delta < DateTimeOffset.MaxValue - now ? delta : DateTimeOffset.MaxValue - now);
        }
        return new FflogsRequestException(
            kind,
            $"FFLogs {endpoint}: HTTP {(int)code}" + (oauthCode is null ? "." : $" ({oauthCode})."),
            retryAt > now ? retryAt : null);
    }

    internal static FflogsRequestException FromException(Exception exception, string stage)
        => exception as FflogsRequestException ?? exception switch
        {
            OperationCanceledException => new(FflogsFailureKind.Timeout, $"FFLogs {stage}: request timed out."),
            HttpRequestException http => new(FflogsFailureKind.Network, $"FFLogs {stage}: network failure ({http.HttpRequestError})."),
            _ => new(FflogsFailureKind.InvalidResponse, $"FFLogs {stage}: unusable response or ranking data."),
        };

    private static string? ReadSafeOAuthCode(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("error", out var error) &&
                error.ValueKind == JsonValueKind.String)
            {
                // An allowlist (not truncation/redaction) prevents reflected credentials
                // or arbitrary upstream HTML/text from becoming a diagnostic.
                return error.GetString() switch
                {
                    "invalid_client" => "invalid_client",
                    "unauthorized_client" => "unauthorized_client",
                    "invalid_request" => "invalid_request",
                    "invalid_grant" => "invalid_grant",
                    "unsupported_grant_type" => "unsupported_grant_type",
                    "invalid_scope" => "invalid_scope",
                    _ => null,
                };
            }
        }
        catch (JsonException)
        {
            // Proxies may return HTML instead of OAuth JSON; the HTTP code is sufficient.
        }
        return null;
    }
}

internal sealed class FflogsRequestSuppressedException : Exception { }

internal sealed class FflogsRequestGuard(TimeProvider timeProvider)
{
    private readonly object sync = new();
    private FflogsSettings? credentials;
    private long generation;
    private int consecutiveFailures;
    private bool hadFailure;
    private FflogsEstimateStatus? failure;
    private DateTimeOffset? serverRetryAt;

    public long Synchronize(FflogsSettings settings)
    {
        lock (sync)
        {
            if (credentials is null || credentials.Enabled != settings.Enabled ||
                credentials.ClientId != settings.ClientId || credentials.ClientSecret != settings.ClientSecret)
            {
                credentials = settings;
                generation++;
                consecutiveFailures = 0;
                // Editing credentials or toggling the feature must not bypass Retry-After.
                if (serverRetryAt is null || serverRetryAt <= timeProvider.GetUtcNow())
                    failure = null;
                else if (failure is not null)
                    failure = failure with { State = FflogsEstimateState.RetryWaiting, RetryAt = serverRetryAt };
            }
            return generation;
        }
    }

    public FflogsEstimateStatus? Failure { get { lock (sync) return failure; } }

    public bool CanRequest(long expectedGeneration)
    {
        lock (sync)
        {
            return expectedGeneration == generation &&
                   (failure is null || (failure.State != FflogsEstimateState.RequestsPaused &&
                                        failure.RetryAt <= timeProvider.GetUtcNow()));
        }
    }

    public void Check(long expectedGeneration)
    {
        if (!CanRequest(expectedGeneration))
            throw new FflogsRequestSuppressedException();
    }

    public bool RecordFailure(long expectedGeneration, FflogsRequestException exception)
    {
        lock (sync)
        {
            if (expectedGeneration != generation)
                return false;

            // Invalidate every queued/page request from this attempt before apiGate is
            // released. They must not wake up and repeat the same failing token request.
            generation++;
            hadFailure = true;
            consecutiveFailures = Math.Min(consecutiveFailures + 1, 5);
            var delay = TimeSpan.FromSeconds(Math.Min(30 * (1 << (consecutiveFailures - 1)), 300));
            var retryAt = timeProvider.GetUtcNow() + delay;
            serverRetryAt = exception.RetryAt;
            if (serverRetryAt > retryAt)
                retryAt = serverRetryAt.Value;
            var paused = exception.Kind is FflogsFailureKind.CredentialsRejected or FflogsFailureKind.AccessDenied;
            failure = new FflogsEstimateStatus(
                paused ? FflogsEstimateState.RequestsPaused : FflogsEstimateState.RetryWaiting,
                exception.Message)
            {
                FailureKind = exception.Kind,
                RetryAt = paused ? serverRetryAt : retryAt,
            };
            return true;
        }
    }

    public void RequestManualRetry()
    {
        lock (sync)
        {
            // The button releases an authentication pause, not a network/rate-limit
            // cooldown. Repeated clicks cannot circumvent the shared retry budget.
            if (failure?.State == FflogsEstimateState.RequestsPaused &&
                (serverRetryAt is null || serverRetryAt <= timeProvider.GetUtcNow()))
            {
                failure = null;
                generation++;
                consecutiveFailures = 0;
            }
        }
    }

    public bool RecordSuccess(long expectedGeneration)
    {
        lock (sync)
        {
            if (expectedGeneration != generation)
                return false;
            var recovered = hadFailure;
            hadFailure = false;
            failure = null;
            serverRetryAt = null;
            consecutiveFailures = 0;
            return recovered;
        }
    }
}

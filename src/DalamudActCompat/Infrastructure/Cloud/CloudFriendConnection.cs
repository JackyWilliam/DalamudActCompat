using System.Net;

namespace DalamudActCompat.Infrastructure.Cloud;

internal static class CloudFriendConnection
{
    internal static async Task RunAsync(
        CloudApiClient api, string token, CancellationToken cancellationToken,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<CloudPresenceHeartbeat?>? presence = null, Action<CloudFriendPresence>? received = null)
    {
        delay ??= Task.Delay;
        // A restarted monitor gets its own lease even if the login token is reused.
        // Cleanup of an old connection can therefore never disconnect its successor.
        var clientId = Guid.NewGuid();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var interval = TimeSpan.FromSeconds(25);
                var activity = presence?.Invoke();
                try
                {
                    using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    request.CancelAfter(TimeSpan.FromSeconds(5));
                    var result = await api.SetFriendPresenceAsync(token, clientId, true, request.Token, activity).ConfigureAwait(false);
                    received?.Invoke(result);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                catch (CloudApiException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized || ex.ToBanNotice() is not null)
                {
                    throw;
                }
                catch (CloudApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                {
                    // Older deployments do not have friends yet; keep login usable
                    // and discover the capability later without a reconnect storm.
                    interval = TimeSpan.FromMinutes(5);
                }
                catch
                {
                    // Failed heartbeats expire naturally at the server. They are
                    // never evidence that a still-valid account should be logged out.
                }
                if (presence is null || interval > TimeSpan.FromSeconds(25)) await delay(interval, cancellationToken).ConfigureAwait(false);
                else
                {
                    // Inspect only an immutable frame-produced value. Network
                    // traffic stays at 25 seconds unless the current duty/settings changed.
                    for (var second = 0; second < 25; second++)
                    {
                        await delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                        if (presence() != activity) break;
                    }
                }
            }
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await api.SetFriendPresenceAsync(token, clientId, false, cleanup.Token).ConfigureAwait(false); }
            catch { /* Revoked tokens and lost networks are covered by lease expiry. */ }
        }
    }
}

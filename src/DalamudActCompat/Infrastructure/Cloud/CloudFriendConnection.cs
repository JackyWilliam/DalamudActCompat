using System.Net;

namespace DalamudActCompat.Infrastructure.Cloud;

internal static class CloudFriendConnection
{
    internal static async Task RunAsync(
        CloudApiClient api, string token, CancellationToken cancellationToken,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
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
                try
                {
                    using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    request.CancelAfter(TimeSpan.FromSeconds(5));
                    await api.SetFriendPresenceAsync(token, clientId, true, request.Token).ConfigureAwait(false);
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
                await delay(interval, cancellationToken).ConfigureAwait(false);
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

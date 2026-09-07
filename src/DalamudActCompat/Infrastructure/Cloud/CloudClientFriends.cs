namespace DalamudActCompat.Infrastructure.Cloud;

internal sealed partial class CloudClientService
{
    public CloudFriendsSession FriendsSession
    {
        get
        {
            lock (stateLock)
                return new(friendsSessionGeneration, snapshot.IsSignedIn && credentials is not null && activeBan is null, credentials?.Username);
        }
    }

    public Task<CloudFriendList> ListFriendsAsync(CancellationToken cancellationToken, CloudFriendsSession? expectedSession = null)
        => WithFriendSessionAsync(apiClient.ListFriendsAsync, cancellationToken, expectedSession);

    public Task<CloudFriendLookup> LookupFriendAsync(string username, CancellationToken cancellationToken, CloudFriendsSession? expectedSession = null)
        => WithFriendSessionAsync((token, ct) => apiClient.LookupFriendAsync(token, username, ct), cancellationToken, expectedSession);

    public Task<CloudFriendRelation> RequestFriendAsync(string username, CancellationToken cancellationToken, CloudFriendsSession? expectedSession = null)
        => WithFriendSessionAsync((token, ct) => apiClient.RequestFriendAsync(token, username, ct), cancellationToken, expectedSession);

    public Task<CloudFriendRelation> AcceptFriendAsync(string requestId, CancellationToken cancellationToken, CloudFriendsSession? expectedSession = null)
        => WithFriendSessionAsync((token, ct) => apiClient.AcceptFriendAsync(token, requestId, ct), cancellationToken, expectedSession);

    public Task<CloudFriendRelation> DeclineFriendAsync(string requestId, CancellationToken cancellationToken, CloudFriendsSession? expectedSession = null)
        => WithFriendSessionAsync((token, ct) => apiClient.DeclineFriendAsync(token, requestId, ct), cancellationToken, expectedSession);

    public Task<CloudFriendRemoval> RemoveFriendAsync(string relationId, CancellationToken cancellationToken, CloudFriendsSession? expectedSession = null)
        => WithFriendSessionAsync((token, ct) => apiClient.RemoveFriendAsync(token, relationId, ct), cancellationToken, expectedSession);

    public Task<CloudChatSync> SyncChatAsync(CancellationToken cancellationToken, CloudFriendsSession? expectedSession = null)
        => WithFriendSessionAsync(apiClient.SyncChatAsync, cancellationToken, expectedSession);

    public Task<CloudChatConversation> GetChatAsync(string conversationId, CancellationToken cancellationToken, CloudFriendsSession? expectedSession = null)
        => WithFriendSessionAsync((token, ct) => apiClient.GetChatAsync(token, conversationId, ct), cancellationToken, expectedSession);

    public Task<CloudChatSendResult> SendChatAsync(string conversationId, CloudChatSendRequest message, CancellationToken cancellationToken, CloudFriendsSession? expectedSession = null)
        => WithFriendSessionAsync((token, ct) => apiClient.SendChatAsync(token, conversationId, message, ct), cancellationToken, expectedSession);

    public Task<CloudChatConversation> AcknowledgeChatAsync(string conversationId, IReadOnlyList<long> messageIds, CancellationToken cancellationToken, CloudFriendsSession? expectedSession = null)
        => WithFriendSessionAsync((token, ct) => apiClient.AcknowledgeChatAsync(token, conversationId, messageIds, ct), cancellationToken, expectedSession);

    private async Task<T> WithFriendSessionAsync<T>(Func<string, CancellationToken, Task<T>> operation, CancellationToken cancellationToken, CloudFriendsSession? expectedSession = null)
    {
        CloudStoredCredentials current;
        lock (stateLock)
        {
            if (!snapshot.IsSignedIn || credentials is null)
                throw new InvalidOperationException("请先登录云账号。");
            // Capture credentials under the same lock as the expected UI generation;
            // a switch between controller validation and dispatch cannot use a new account.
            if (expectedSession is { } expected && expected != FriendsSession)
                throw new OperationCanceledException("好友账号会话已切换。");
            current = credentials;
        }
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, monitorShutdown.Token);
        try
        {
            var result = await operation(current.Token, lifetime.Token).ConfigureAwait(false);
            lifetime.Token.ThrowIfCancellationRequested();
            // Do not expose a previous account's conversation after an account switch.
            if (!IsCurrentSession(current)) throw new OperationCanceledException("登录账号已切换。");
            return result;
        }
        catch (CloudApiException ex)
        {
            HandleFriendSessionFailure(current, ex);
            throw;
        }
    }

    private async Task RunFriendConnectionAsync(CloudStoredCredentials current, CancellationToken cancellationToken)
    {
        try { await CloudFriendConnection.RunAsync(apiClient, current.Token, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (CloudApiException ex) { HandleFriendSessionFailure(current, ex); }
    }

    private void HandleFriendSessionFailure(CloudStoredCredentials current, CloudApiException error)
    {
        if (error.ToBanNotice() is { } ban && ShouldApplyBan(current, ban)) ApplyBan(ban);
        else if (error.StatusCode == System.Net.HttpStatusCode.Unauthorized && IsCurrentSession(current))
            InvalidateSessionPreservingRecoveryKey("登录已失效，请重新登录。", isError: true);
    }
}

namespace DalamudActCompat.Infrastructure.Cloud;

internal readonly record struct CloudFriendsSession(long Generation, bool IsSignedIn, string? Username);

// The UI controller receives only an opaque generation, never a login token.
internal interface ICloudFriendsSession
{
    CloudFriendsSession FriendsSession { get; }
    void SuppressFriendDuty(CloudFriendsSession expectedSession) { }
    Task<CloudPresenceSettings> UpdateFriendPresenceSettingsAsync(CloudPresenceSettings settings, CancellationToken cancellationToken, CloudFriendsSession? expectedSession = null)
        => Task.FromException<CloudPresenceSettings>(new NotSupportedException("当前好友服务不支持编辑状态。"));
    Task<CloudFriendList> ListFriendsAsync(CancellationToken cancellationToken, CloudFriendsSession? expectedSession = null);
    Task<CloudFriendLookup> LookupFriendAsync(string username, CancellationToken cancellationToken, CloudFriendsSession? expectedSession = null);
    Task<CloudFriendRelation> RequestFriendAsync(string username, CancellationToken cancellationToken, CloudFriendsSession? expectedSession = null);
    Task<CloudFriendRelation> AcceptFriendAsync(string id, CancellationToken cancellationToken, CloudFriendsSession? expectedSession = null);
    Task<CloudFriendRelation> DeclineFriendAsync(string id, CancellationToken cancellationToken, CloudFriendsSession? expectedSession = null);
    Task<CloudFriendRemoval> RemoveFriendAsync(string id, CancellationToken cancellationToken, CloudFriendsSession? expectedSession = null);
    Task<CloudChatSync> SyncChatAsync(CancellationToken cancellationToken, CloudFriendsSession? expectedSession = null);
    Task<CloudChatConversation> GetChatAsync(string id, CancellationToken cancellationToken, CloudFriendsSession? expectedSession = null);
    Task<CloudChatSendResult> SendChatAsync(string id, CloudChatSendRequest message, CancellationToken cancellationToken, CloudFriendsSession? expectedSession = null);
    Task<CloudChatConversation> AcknowledgeChatAsync(string id, IReadOnlyList<long> ids, CancellationToken cancellationToken, CloudFriendsSession? expectedSession = null);
}

using System.Text.Json.Serialization;

namespace DalamudActCompat.Infrastructure.Cloud;

internal static class CloudChatPolicy
{
    public const string Notice = "请勿利用本功能从事洗钱、刷单、诈骗等违规违法活动。涉嫌违规的消息将由管理员核查处理。";
    public const string InviteNext = "invite_next";
    public const string WhenFinished = "when_finished";
}

internal sealed record CloudFriendRelation(
    string Id, string State, string Direction, CloudApiUser User,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string? ConversationId,
    bool Online = false, string Status = "offline", string StatusText = "", CloudDutyActivity? Duty = null);

internal sealed record CloudFriendList(
    IReadOnlyList<CloudFriendRelation> Friends, int OnlineCount,
    IReadOnlyList<CloudFriendRelation> Requests, string PolicyNotice, CloudApiUser? User = null, CloudPresenceSettings? PresenceSettings = null);

internal sealed record CloudPresenceSettings(string Status, string Text, bool ShareDuty, long Revision)
{
    public static CloudPresenceSettings Default { get; } = new("online", "", false, 0);
}
internal sealed record CloudDutyActivity(uint Id, string Name);
internal sealed record CloudPresenceHeartbeat(long ProfileRevision, CloudDutyActivity? Duty);

internal sealed record CloudFriendLookup(CloudApiUser? User, string Relationship);
internal sealed record CloudFriendRemoval(string Status);
internal sealed record CloudFriendPresence(
    bool Online, int OnlineConnectionCount, int HeartbeatIntervalSeconds, DateTimeOffset? ExpiresAt, CloudPresenceSettings? Settings = null);
internal sealed record CloudChatSender(string Kind, string? UserId, string Name, bool IsAdmin = false, int SponsorTier = 0)
{
    // Only the authenticated server assigns this discriminator; names are not identity.
    public bool IsOfficial => Kind == "official";
}

internal sealed record CloudChatMessage(
    long Id, string ConversationId, CloudChatSender Sender, string RecipientId,
    long ClientSequence, Guid OperationId, string Text, string? QuickMessageId,
    string State, DateTimeOffset CreatedAt, DateTimeOffset? DeliveredAt);

internal sealed record CloudChatSummary(
    string Id, string Kind, long Revision, CloudApiUser Peer,
    int HistoryCount, int PendingCount, long LatestMessageId);

internal sealed record CloudChatConversation(
    string Id, string Kind, long Revision, CloudApiUser Peer,
    int HistoryCount, int PendingCount, long LatestMessageId,
    IReadOnlyList<CloudChatMessage> History, IReadOnlyList<CloudChatMessage> Pending,
    long? NextSendSequence, string PolicyNotice, IReadOnlyList<long>? RetiredIds = null);

internal sealed record CloudQuickMessage(string Id, string Text);
internal sealed record CloudChatSync(
    IReadOnlyList<CloudChatSummary> Conversations, IReadOnlyList<CloudChatMessage> Deliveries,
    string PolicyNotice, IReadOnlyList<CloudQuickMessage> QuickMessages);
internal sealed record CloudChatSendResult(CloudChatMessage Message, bool Duplicate, long NextSendSequence);

// Keep this immutable object for retries after an unknown network outcome. Never
// allocate a new sequence/operation automatically: the original may have committed.
internal sealed record CloudChatSendRequest(
    long Sequence, Guid OperationId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Text = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? QuickMessageId = null);

internal sealed partial class CloudApiClient
{
    public Task<CloudFriendList> ListFriendsAsync(string token, CancellationToken cancellationToken)
        => SendJsonAsync<CloudFriendList>(HttpMethod.Get, "api/v1/friends", null, token, cancellationToken);

    public Task<CloudFriendLookup> LookupFriendAsync(string token, string username, CancellationToken cancellationToken)
        => SendJsonAsync<CloudFriendLookup>(HttpMethod.Post, "api/v1/friends/lookup", new { username }, token, cancellationToken);

    public Task<CloudFriendRelation> RequestFriendAsync(string token, string username, CancellationToken cancellationToken)
        => SendJsonAsync<CloudFriendRelation>(HttpMethod.Post, "api/v1/friends/requests", new { username }, token, cancellationToken);

    public Task<CloudFriendRelation> AcceptFriendAsync(string token, string requestId, CancellationToken cancellationToken)
        => SendJsonAsync<CloudFriendRelation>(HttpMethod.Post, $"api/v1/friends/requests/{Uri.EscapeDataString(requestId)}/accept", new { }, token, cancellationToken);

    public Task<CloudFriendRelation> DeclineFriendAsync(string token, string requestId, CancellationToken cancellationToken)
        => SendJsonAsync<CloudFriendRelation>(HttpMethod.Post, $"api/v1/friends/requests/{Uri.EscapeDataString(requestId)}/decline", new { }, token, cancellationToken);

    public Task<CloudFriendRemoval> RemoveFriendAsync(string token, string relationId, CancellationToken cancellationToken)
        => SendJsonAsync<CloudFriendRemoval>(HttpMethod.Delete, $"api/v1/friends/{Uri.EscapeDataString(relationId)}", new { }, token, cancellationToken);

    public Task<CloudPresenceSettings> UpdateFriendPresenceSettingsAsync(string token, CloudPresenceSettings settings, CancellationToken cancellationToken)
        => SendJsonAsync<CloudPresenceSettings>(HttpMethod.Put, "api/v1/friends/presence/settings", settings, token, cancellationToken);

    public Task<CloudFriendPresence> SetFriendPresenceAsync(string token, Guid clientId, bool online, CancellationToken cancellationToken, CloudPresenceHeartbeat? activity = null)
        => SendJsonAsync<CloudFriendPresence>(online ? HttpMethod.Put : HttpMethod.Delete,
            "api/v1/friends/presence", activity is null ? new { clientId } : (object)new { clientId, activity.ProfileRevision, activity.Duty }, token, cancellationToken);

    public Task<CloudChatSync> SyncChatAsync(string token, CancellationToken cancellationToken)
        => SendJsonAsync<CloudChatSync>(HttpMethod.Get, "api/v1/chat/sync", null, token, cancellationToken);

    public Task<CloudChatConversation> GetChatAsync(string token, string conversationId, CancellationToken cancellationToken)
        => SendJsonAsync<CloudChatConversation>(HttpMethod.Get, ChatPath(conversationId), null, token, cancellationToken);

    public Task<CloudChatSendResult> SendChatAsync(string token, string conversationId, CloudChatSendRequest message, CancellationToken cancellationToken)
        => SendJsonAsync<CloudChatSendResult>(HttpMethod.Post, $"{ChatPath(conversationId)}/messages", message, token, cancellationToken);

    // Call only after a consumer has received the snapshot; heartbeat and sync do
    // not silently acknowledge messages that no chat UI or pet has received yet.
    public Task<CloudChatConversation> AcknowledgeChatAsync(string token, string conversationId, IReadOnlyList<long> messageIds, CancellationToken cancellationToken)
        => SendJsonAsync<CloudChatConversation>(HttpMethod.Post, $"{ChatPath(conversationId)}/ack", new { messageIds }, token, cancellationToken);

    private static string ChatPath(string id) => $"api/v1/chat/conversations/{Uri.EscapeDataString(id)}";
}

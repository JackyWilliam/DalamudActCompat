using DalamudActCompat.Infrastructure.Cloud;

namespace DalamudActCompat.UI;

internal sealed class FriendIncomingNotification(CloudChatMessage message, long shownAt)
{
    internal CloudChatMessage Message { get; } = message;
    internal long ShownAt { get; } = shownAt;
    internal long ExpiresAt { get; set; } = shownAt + 6000;
    internal float? Y { get; set; }
    internal Guid? ReplyOperation { get; set; }
    internal float Opacity(long now) => Math.Clamp((ExpiresAt - now) / 800f, 0, 1);
    internal float SlideProgress(long now) => 1 - MathF.Pow(1 - Math.Clamp((now - ShownAt) / 220f, 0, 1), 3);
}

// Only three visible entries plus one watermark per current conversation live in
// memory. Retained server snapshots remain the only source of message bodies.
internal sealed class FriendsIncomingNotifications
{
    private readonly Dictionary<string, long> observed = new(StringComparer.Ordinal);
    private readonly List<FriendIncomingNotification> visible = [];
    private CloudFriendsSession session;
    internal IReadOnlyList<FriendIncomingNotification> Visible => visible;
    internal void ClearVisible() => visible.Clear();

    internal bool Update(FriendsChatSnapshot state, bool enabled, long now)
    {
        if (session != state.Session)
        {
            session = state.Session; observed.Clear(); visible.Clear();
        }
        visible.RemoveAll(n => now >= n.ExpiresAt || !state.Conversations.ContainsKey(n.Message.ConversationId));
        if (!enabled || !state.Session.IsSignedIn) visible.Clear();
        if (!state.Session.IsSignedIn || !state.InitialSyncComplete) return false;
        foreach (var id in observed.Keys.Where(id => !state.Conversations.ContainsKey(id)).ToArray()) observed.Remove(id);
        var incoming = new List<CloudChatMessage>();
        foreach (var (id, view) in state.Conversations)
        {
            // Conversations are refreshed separately, so a global moving watermark
            // would drop a lower-ID arrival loaded after another chat's higher ID.
            var through = observed.GetValueOrDefault(id, state.NotificationBaseline);
            foreach (var message in view.Chat.History.Concat(view.Chat.Pending).DistinctBy(m => m.Id))
            {
                if (message.Id > through && message.RecipientId == state.Friends?.User?.Id &&
                    message.Sender.UserId != state.Friends?.User?.Id) incoming.Add(message);
                observed[id] = Math.Max(observed.GetValueOrDefault(id, through), message.Id);
            }
            observed.TryAdd(id, through);
        }
        // Advance watermarks while disabled, too: re-enabling must not replay a
        // backlog. Newest is always at the top, regardless of dictionary order.
        if (enabled)
            foreach (var message in incoming.OrderBy(m => m.Id).TakeLast(3)) visible.Insert(0, new(message, now));
        visible.Sort((left, right) => right.Message.Id.CompareTo(left.Message.Id));
        if (visible.Count > 3) visible.RemoveRange(3, visible.Count - 3);
        return incoming.Count > 0;
    }

    internal static IReadOnlyList<string> Replies(CloudChatMessage message) => message.Sender.IsOfficial ? [] : message.QuickMessageId switch
    {
        CloudChatPolicy.InviteNext => ["好，下把叫你", "这次不方便"],
        CloudChatPolicy.WhenFinished => ["快结束了", "还要一会儿"],
        _ => [],
    };
}

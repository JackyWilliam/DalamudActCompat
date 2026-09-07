using DalamudActCompat.Infrastructure.Cloud;

internal static partial class FriendsUiSmokeTests
{
    public static async Task RunUnreadAsync()
    {
        await DeliveryAndReadAsync();
        await UnreadRequestsAndOwnAsync();
        Console.WriteLine("Friends unread: unique received counts, partial read/cold reload/retention, own sends and pending requests passed.");
    }

    private static async Task UnreadRequestsAndOwnAsync()
    {
        var api = new Fake(); api.AddIncoming(3);
        // An in-flight ACK can transiently put the same received ID in both lists.
        api.Chat = api.Chat with { History = [api.Chat.Pending[0] with { State = "history" }] };
        var requestId = Guid.NewGuid().ToString();
        api.Requests.Add(new(requestId, "pending", "incoming", api.Peer, default, default, null));
        api.Requests.Add(new(Guid.NewGuid().ToString(), "pending", "outgoing", api.Peer, default, default, null));
        using var controller = new FriendsChatController(api, new MemoryDisk(), TimeSpan.FromHours(1));
        controller.AttachConsumer(); await Until(() => controller.Snapshot.InitialSyncComplete, "unread count ready");
        Check(controller.Snapshot.Conversations[api.Id].UnreadCount == 3 && controller.Snapshot.HasIncomingRequests, "Duplicate delivery IDs inflated unread count.");
        controller.Send(api.Id, "my reply is not unread");
        await Until(() => !controller.Snapshot.Busy && api.Attempts.Count == 1, "own message count");
        Check(controller.Snapshot.Conversations[api.Id].UnreadCount == 3, "Own reply erased or increased unread received count.");
        controller.MarkRead(api.Id, long.MaxValue);
        await Until(() => !controller.Snapshot.HasUnreadMessages, "read all messages");
        Check(controller.Snapshot.HasIncomingRequests && controller.Snapshot.HasUnread, "Reading a chat silently handled its friend request.");
        controller.Accept(requestId);
        await Until(() => !controller.Snapshot.Busy && !controller.Snapshot.HasIncomingRequests, "request accepted");
        Check(!controller.Snapshot.HasUnread && controller.Snapshot.Friends!.Requests.Count == 1, "Outgoing request kept the incoming notification red.");
        api.SwitchAccount();
        Check(!controller.Snapshot.HasUnreadMessages && !controller.Snapshot.HasIncomingRequests, "Account switch leaked unread indicators.");
    }
}

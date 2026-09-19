using System.Collections.Immutable;
using System.Net;
using System.Threading.Channels;

namespace DalamudActCompat.Infrastructure.Cloud;

internal sealed record FriendConversationView(CloudChatConversation Chat, CloudChatSendRequest? PendingSend, string SendStatus, bool Unread,
    Guid? LastPreparedOperation = null)
{
    public int UnreadCount { get; init; }
}
internal sealed record FriendsChatSnapshot(
    CloudFriendsSession Session, string State, string Status, bool Busy, CloudFriendList? Friends,
    CloudFriendLookup? Lookup, ImmutableDictionary<string, FriendConversationView> Conversations)
{
    public bool InitialSyncComplete { get; init; }
    public long NotificationBaseline { get; init; }
    public static FriendsChatSnapshot Empty(CloudFriendsSession session) => new(session,
        session.IsSignedIn ? "loading" : "signed-out", session.IsSignedIn ? "正在连接好友服务…" : "登录后可使用好友功能。",
        false, null, null, ImmutableDictionary<string, FriendConversationView>.Empty);
    public bool HasUnreadMessages => Conversations.Values.Any(c => c.Unread);
    public bool HasIncomingRequests => Friends?.Requests.Any(r => r.Direction == "incoming") == true;
    public bool HasUnread => HasUnreadMessages || HasIncomingRequests;
    public long LatestUnreadId => Conversations.Values.Where(c => c.Unread)
        .SelectMany(c => c.Chat.History.Concat(c.Chat.Pending)).Where(m => m.RecipientId == Friends?.User?.Id).Select(m => m.Id).DefaultIfEmpty().Max();
}

// One worker owns networking and local state. Render callbacks only enqueue small
// commands and read immutable snapshots; they never await IO or call ImGui off-thread.
internal sealed class FriendsChatController : IDisposable
{
    private enum Kind { Refresh, Lookup, Request, Accept, Decline, Remove, Send, Retry, Discard, Read, Presence }
    private sealed record Command(CloudFriendsSession Session, Kind Kind, string Id = "", string Text = "", string? Quick = null, long ReadId = 0,
        Guid Operation = default, CloudPresenceSettings? Presence = null);
    private readonly ICloudFriendsSession api;
    private readonly IFriendsLocalStateStore disk;
    private readonly Channel<Command> commands = Channel.CreateBounded<Command>(32);
    private readonly CancellationTokenSource shutdown = new();
    private readonly Task worker;
    private readonly TimeSpan pollInterval;
    private readonly Dictionary<string, long> queuedReads = new(StringComparer.Ordinal);
    private FriendsChatSnapshot snapshot;
    private CloudFriendsSession session;
    private FriendsLocalState local = FriendsLocalState.Empty();
    private string? userId;
    private int busy;
    private int consumerAttached;
    private DateTimeOffset nextPoll = DateTimeOffset.MinValue;

    public FriendsChatController(ICloudFriendsSession api, IFriendsLocalStateStore disk, TimeSpan? pollInterval = null)
    {
        this.api = api; this.disk = disk; this.pollInterval = pollInterval ?? TimeSpan.FromSeconds(5);
        session = api.FriendsSession; snapshot = FriendsChatSnapshot.Empty(session);
        worker = Task.Run(RunAsync);
    }
    public FriendsChatSnapshot Snapshot
    {
        get
        {
            var current = api.FriendsSession;
            var view = Volatile.Read(ref snapshot);
            return view.Session == current ? view with { Busy = Volatile.Read(ref busy) > 0 } : FriendsChatSnapshot.Empty(current);
        }
    }
    public void AttachConsumer() { Volatile.Write(ref consumerAttached, 1); Refresh(); }
    public void DetachConsumer() => Volatile.Write(ref consumerAttached, 0);
    public bool Refresh() => Enqueue(new(api.FriendsSession, Kind.Refresh));
    public bool Lookup(string username) => Enqueue(new(api.FriendsSession, Kind.Lookup, Text: username.Trim()));
    public bool Request(string username) => Enqueue(new(api.FriendsSession, Kind.Request, Text: username));
    public bool Accept(string id) => Enqueue(new(api.FriendsSession, Kind.Accept, id));
    public bool Decline(string id) => Enqueue(new(api.FriendsSession, Kind.Decline, id));
    public bool Remove(string id) => Enqueue(new(api.FriendsSession, Kind.Remove, id));
    public bool UpdatePresence(CloudPresenceSettings settings)
    {
        var current = api.FriendsSession;
        // Stop collecting/uploading immediately while an off/invisible save is in
        // flight. Suppress before publishing the command: a fast successful HTTP
        // response must not race with a late suppression that never gets cleared.
        return Enqueue(new(current, Kind.Presence, Presence: settings),
            !settings.ShareDuty || settings.Status == "invisible" ? () => api.SuppressFriendDuty(current) : null);
    }
    public Guid? Send(string id, string text, string? quick = null, CloudFriendsSession? expectedSession = null)
    {
        var current = api.FriendsSession;
        if (expectedSession is { } expected && expected != current) return null;
        var operation = Guid.NewGuid();
        return Enqueue(new(current, Kind.Send, id, text, quick, Operation: operation)) ? operation : null;
    }
    public bool Retry(string id, CloudFriendsSession? expectedSession = null, Guid? expectedOperation = null)
    {
        var current = api.FriendsSession;
        return (expectedSession is null || expectedSession == current) && Enqueue(new(current, Kind.Retry, id, Operation: expectedOperation ?? default));
    }
    public bool Discard(string id) => Enqueue(new(api.FriendsSession, Kind.Discard, id));
    public void MarkRead(string id, long through)
    {
        var state = Snapshot;
        if (through < 1 || state.State != "ready" || state.Conversations.GetValueOrDefault(id)?.Unread != true) return;
        lock (queuedReads)
        {
            if (queuedReads.GetValueOrDefault(id) >= through) return;
            if (Enqueue(new(api.FriendsSession, Kind.Read, id, ReadId: through))) queuedReads[id] = through;
        }
    }
    private bool Enqueue(Command command, Action? beforePublish = null)
    {
        if (shutdown.IsCancellationRequested || !command.Session.IsSignedIn) return false;
        var mutation = command.Kind is not Kind.Read and not Kind.Refresh;
        if (mutation && Interlocked.CompareExchange(ref busy, 1, 0) != 0) return false;
        beforePublish?.Invoke();
        if (commands.Writer.TryWrite(command)) return true;
        if (mutation) Volatile.Write(ref busy, 0);
        return false;
    }
    private bool Current => !shutdown.IsCancellationRequested && api.FriendsSession == session && session.IsSignedIn;
    private void EnsureCurrent()
    {
        if (!Current) throw new OperationCanceledException("好友账号会话已切换。");
    }
    private void Publish(FriendsChatSnapshot value)
    {
        if (api.FriendsSession == session) Volatile.Write(ref snapshot, value);
    }
    private async Task<T> Call<T>(Func<CancellationToken, Task<T>> call)
    {
        EnsureCurrent();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var result = await call(timeout.Token).ConfigureAwait(false);
        EnsureCurrent();
        return result;
    }
    private async Task RunAsync()
    {
        try
        {
            while (!shutdown.IsCancellationRequested)
            {
                var current = api.FriendsSession;
                if (current != session)
                {
                    session = current; userId = null; local = FriendsLocalState.Empty();
                    lock (queuedReads) queuedReads.Clear();
                    Publish(FriendsChatSnapshot.Empty(session)); nextPoll = DateTimeOffset.MinValue;
                }
                while (commands.Reader.TryRead(out var command))
                {
                    try { if (Current && command.Session == session) await ExecuteAsync(command).ConfigureAwait(false); }
                    catch (Exception error) { Report(error); }
                    finally
                    {
                        if (command.Kind is not Kind.Read and not Kind.Refresh) Volatile.Write(ref busy, 0);
                        if (command.Kind == Kind.Read) lock (queuedReads) queuedReads.Remove(command.Id);
                    }
                }
                if (Current && DateTimeOffset.UtcNow >= nextPoll)
                {
                    nextPoll = DateTimeOffset.UtcNow + pollInterval;
                    try { await PollAsync().ConfigureAwait(false); }
                    catch (Exception error) { Report(error); }
                }
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
                wait.CancelAfter(TimeSpan.FromMilliseconds(250));
                try { await commands.Reader.WaitToReadAsync(wait.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!shutdown.IsCancellationRequested) { }
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
    }
    private void Report(Exception error)
    {
        if (!Current) return;
        var unavailable = error is CloudApiException { StatusCode: HttpStatusCode.NotFound } && snapshot.Friends is null;
        nextPoll = DateTimeOffset.UtcNow + (unavailable ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(15));
        var status = unavailable ? "服务器暂未开放好友功能，账号与云同步仍可正常使用。" :
            error is HttpRequestException or OperationCanceledException ? "好友连接暂时中断，将自动重连；原账号与云同步不受影响。" : error.Message;
        Publish(snapshot with { State = unavailable ? "unsupported" : "error", Status = status });
    }
    private async Task PollAsync()
    {
        var friends = await Call(ct => api.ListFriendsAsync(ct, session)).ConfigureAwait(false);
        if (friends.User is null) throw new InvalidDataException("好友服务版本较旧，暂时无法显示聊天身份。");
        if (userId is null)
        {
            var loaded = await disk.LoadAsync(friends.User.Id, shutdown.Token).ConfigureAwait(false);
            EnsureCurrent(); local = loaded; userId = friends.User.Id;
        }
        if (userId != friends.User.Id) throw new InvalidDataException("好友账号身份发生变化，请重新登录。");
        Publish(snapshot with { Friends = friends, State = "ready", Status = "好友服务已连接。" });
        if (Volatile.Read(ref consumerAttached) == 0) return;
        var sync = await Call(ct => api.SyncChatAsync(ct, session)).ConfigureAwait(false);
        var ids = sync.Conversations.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var updated = snapshot.Conversations.RemoveRange(snapshot.Conversations.Keys.Where(id => !ids.Contains(id)));
        var removed = false;
        foreach (var id in local.Outbox.Keys.Where(id => !ids.Contains(id)).ToArray()) { local.Outbox.Remove(id); removed = true; }
        foreach (var id in local.ReadThrough.Keys.Where(id => !ids.Contains(id)).ToArray()) { local.ReadThrough.Remove(id); removed = true; }
        if (removed) await SaveAsync().ConfigureAwait(false);
        Publish(snapshot with { Conversations = updated });
        foreach (var summary in sync.Conversations)
        {
            if (!snapshot.Conversations.TryGetValue(summary.Id, out var view) || view.Chat.Revision != summary.Revision || local.Outbox.ContainsKey(summary.Id) || NeedsReadSync(view.Chat))
                await ReceiveAsync(await Call(ct => api.GetChatAsync(summary.Id, ct, session)).ConfigureAwait(false)).ConfigureAwait(false);
            if (snapshot.Conversations.TryGetValue(summary.Id, out view))
            {
                var incoming = view.Chat.Pending.Where(m => m.RecipientId == userId).Select(m => m.Id).ToArray();
                if (incoming.Length > 0 && Volatile.Read(ref consumerAttached) != 0)
                {
                    // ReceiveAsync has already published the bounded UI model. ACK
                    // records delivery, not reading; unread metadata survives restart.
                    var delivered = await Call(ct => api.AcknowledgeChatAsync(summary.Id, incoming, ct, session)).ConfigureAwait(false);
                    await ReceiveAsync(delivered).ConfigureAwait(false);
                }
            }
        }
        // The friend list becomes ready before chat bootstrap finishes. Publish
        // this barrier only after all retained history is loaded, so old/unread
        // history and its ACKs cannot masquerade as fresh overlay notifications.
        if (!snapshot.InitialSyncComplete)
            Publish(snapshot with { InitialSyncComplete = true, NotificationBaseline = snapshot.Conversations.Values
                .SelectMany(c => c.Chat.History.Concat(c.Chat.Pending)).Select(m => m.Id).DefaultIfEmpty().Max() });
    }
    private async Task ReceiveAsync(CloudChatConversation chat)
    {
        EnsureCurrent();
        var status = snapshot.Conversations.GetValueOrDefault(chat.Id)?.SendStatus ?? "";
        if (local.Outbox.TryGetValue(chat.Id, out var request))
        {
            var found = chat.History.Concat(chat.Pending).Any(m => m.OperationId == request.OperationId && m.ClientSequence == request.Sequence && m.Sender.UserId == userId);
            if (found || chat.NextSendSequence > request.Sequence && !chat.History.Concat(chat.Pending).Any(m => m.ClientSequence == request.Sequence && m.Sender.UserId == userId))
            {
                local.Outbox.Remove(chat.Id);
                try { await SaveAsync().ConfigureAwait(false); }
                catch { local.Outbox[chat.Id] = request; throw; }
                status = found ? "发送已确认。" : "原发送序号已过期，消息不再保留；未重新发送。";
            }
            else status = "发送结果待确认，请重试原消息。";
        }
        SetConversation(chat, status);
        if (NeedsReadSync(chat))
        {
            var synced = await Call(ct => api.MarkChatReadAsync(chat.Id,
                Math.Min(local.ReadThrough.GetValueOrDefault(chat.Id), chat.LatestMessageId), ct, session)).ConfigureAwait(false);
            SetConversation(synced, status);
        }
    }
    // Missing fields identify older servers. Existing local cursors migrate on
    // reconnect, and a failed upload retries without making read messages red again.
    private bool NeedsReadSync(CloudChatConversation chat) => chat.ReadThrough is { } remote &&
        Math.Min(local.ReadThrough.GetValueOrDefault(chat.Id), chat.LatestMessageId) > remote;
    private void SetConversation(CloudChatConversation chat, string status)
    {
        EnsureCurrent();
        // Snapshots replace retained content; no accumulating local transcript exists.
        var bounded = chat with { History = chat.History.OrderBy(m => m.Id).TakeLast(20).ToArray(), Pending = chat.Pending.OrderBy(m => m.Id).TakeLast(3).ToArray() };
        // ACK promotes pending messages into history; count unique received IDs
        // beyond the existing read watermark, never deliveries or our own sends.
        var unreadCount = bounded.History.Concat(bounded.Pending)
            .Where(m => m.RecipientId == userId && m.Sender.UserId != userId && m.Id > Math.Max(local.ReadThrough.GetValueOrDefault(chat.Id), chat.ReadThrough ?? 0))
            .Select(m => m.Id).Distinct().Count();
        var value = new FriendConversationView(bounded, local.Outbox.GetValueOrDefault(chat.Id), status, unreadCount > 0,
            snapshot.Conversations.GetValueOrDefault(chat.Id)?.LastPreparedOperation) { UnreadCount = unreadCount };
        Publish(snapshot with { Conversations = snapshot.Conversations.SetItem(chat.Id, value) });
    }
    private async Task SaveAsync()
    {
        EnsureCurrent();
        if (userId is null) throw new InvalidOperationException("好友服务尚未就绪。");
        await disk.SaveAsync(userId, local, shutdown.Token).ConfigureAwait(false);
        EnsureCurrent();
    }
    private async Task ExecuteAsync(Command command)
    {
        if (command.Kind == Kind.Refresh) { nextPoll = DateTimeOffset.MinValue; return; }
        if (userId is null) { await PollAsync().ConfigureAwait(false); EnsureCurrent(); }
        switch (command.Kind)
        {
            case Kind.Presence:
                var saved = await Call(ct => api.UpdateFriendPresenceSettingsAsync(command.Presence!, ct, session)).ConfigureAwait(false);
                Publish(snapshot with { Friends = snapshot.Friends! with { PresenceSettings = saved }, Status = "状态已保存。" }); break;
            case Kind.Lookup:
                var lookup = await Call(ct => api.LookupFriendAsync(command.Text, ct, session)).ConfigureAwait(false);
                Publish(snapshot with { Lookup = lookup, Status = lookup.User is null ? "未找到可添加的账号。" : "已找到账号。" }); break;
            case Kind.Request: await Call(ct => api.RequestFriendAsync(command.Text, ct, session)).ConfigureAwait(false); Publish(snapshot with { Lookup = null, Status = "好友申请已处理。" }); break;
            case Kind.Accept: await Call(ct => api.AcceptFriendAsync(command.Id, ct, session)).ConfigureAwait(false); break;
            case Kind.Decline: await Call(ct => api.DeclineFriendAsync(command.Id, ct, session)).ConfigureAwait(false); break;
            case Kind.Remove: await Call(ct => api.RemoveFriendAsync(command.Id, ct, session)).ConfigureAwait(false); break;
            case Kind.Send: case Kind.Retry: await SendAsync(command).ConfigureAwait(false); return;
            case Kind.Discard:
                if (local.Outbox.Remove(command.Id, out var abandoned))
                {
                    try { await SaveAsync().ConfigureAwait(false); }
                    catch { local.Outbox[command.Id] = abandoned; throw; }
                    if (snapshot.Conversations.TryGetValue(command.Id, out var discarded)) SetConversation(discarded.Chat, "已放弃待确认发送；原消息可能已送达，请先核对记录。");
                }
                break;
            case Kind.Read:
                if (snapshot.Conversations.TryGetValue(command.Id, out var read))
                {
                    var maximum = read.Chat.History.Concat(read.Chat.Pending).Where(m => m.RecipientId == userId).Select(m => m.Id).DefaultIfEmpty().Max();
                    var previous = local.ReadThrough.GetValueOrDefault(command.Id);
                    var through = Math.Min(command.ReadId, maximum);
                    if (through <= previous) return;
                    local.ReadThrough[command.Id] = through;
                    try { await SaveAsync().ConfigureAwait(false); }
                    catch { local.ReadThrough[command.Id] = previous; throw; }
                    await ReceiveAsync(read.Chat).ConfigureAwait(false);
                }
                lock (queuedReads) queuedReads.Remove(command.Id);
                return;
        }
        nextPoll = DateTimeOffset.MinValue;
    }
    private async Task SendAsync(Command command)
    {
        if (!snapshot.Conversations.TryGetValue(command.Id, out var view) || view.Chat.Kind == "official")
            throw new InvalidOperationException("该会话暂时不能发送消息。");
        if (command.Kind == Kind.Send)
        {
            if (local.Outbox.ContainsKey(command.Id)) throw new InvalidOperationException("请先处理上一条待确认消息。");
            var current = await Call(ct => api.GetChatAsync(command.Id, ct, session)).ConfigureAwait(false);
            if (current.NextSendSequence is not { } sequence) throw new InvalidOperationException("该会话为只读通知。");
            if (command.Quick is null && (string.IsNullOrWhiteSpace(command.Text) || command.Text.EnumerateRunes().Count() > 2000))
                throw new InvalidOperationException("消息需为1～2000个字符。");
            if (command.Quick is not null and not CloudChatPolicy.InviteNext and not CloudChatPolicy.WhenFinished)
                throw new InvalidOperationException("快捷消息无效。");
            local.Outbox[command.Id] = new(sequence, command.Operation, command.Quick is null ? command.Text : null, command.Quick);
            SetConversation(current, "正在保存并发送…");
            view = snapshot.Conversations[command.Id] with { LastPreparedOperation = command.Operation };
            Publish(snapshot with { Conversations = snapshot.Conversations.SetItem(command.Id, view) });
        }
        if (!local.Outbox.TryGetValue(command.Id, out var pending)) return;
        // A short-lived bubble must never retry a different operation prepared by
        // the full chat window after the bubble's snapshot was read.
        if (command.Kind == Kind.Retry && command.Operation != default && pending.OperationId != command.Operation) return;
        var attempted = false;
        var confirmed = false;
        try
        {
            await SaveAsync().ConfigureAwait(false);
            attempted = true;
            var sent = await Call(ct => api.SendChatAsync(command.Id, pending, ct, session)).ConfigureAwait(false);
            confirmed = true;
            local.Outbox.Remove(command.Id);
            var messages = view.Chat.History.Concat(view.Chat.Pending).Where(m => m.Id != sent.Message.Id).Append(sent.Message).ToArray();
            SetConversation(view.Chat with { History = messages.Where(m => m.State == "history").ToArray(), Pending = messages.Where(m => m.State == "pending").ToArray(),
                NextSendSequence = sent.NextSendSequence, Revision = -1 }, sent.Message.State == "pending" ? "已发送，待对方上线接收。" : "已发送。");
            await SaveAsync().ConfigureAwait(false);
            nextPoll = DateTimeOffset.MinValue;
        }
        catch (Exception error)
        {
            if (!Current) throw;
            var retired = error is CloudApiException { Code: "sequence_retired" };
            if (retired)
            {
                local.Outbox.Remove(command.Id);
                try { await SaveAsync().ConfigureAwait(false); }
                catch { local.Outbox[command.Id] = pending; throw; }
            }
            SetConversation(snapshot.Conversations[command.Id].Chat, confirmed ? "发送已确认，但本机状态保存失败；将重新核对记录。" : retired ? "原消息已超出保留范围，未重新发送。" :
                error is CloudApiException apiError ? $"{apiError.Message} 重试会保留原消息；需要改写时先放弃此发送。" :
                !attempted ? "本机保存失败，未开始本次发送，请重试。" : "发送结果尚未确认，请重试原消息。");
            nextPoll = DateTimeOffset.UtcNow.AddSeconds(15);
        }
    }
    public void Dispose()
    {
        DetachConsumer(); shutdown.Cancel(); commands.Writer.TryComplete();
        try { worker.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        shutdown.Dispose();
    }
}

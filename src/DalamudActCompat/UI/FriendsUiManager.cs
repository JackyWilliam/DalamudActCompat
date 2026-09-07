using System.Numerics;
using Dalamud.Bindings.ImGui;
using DalamudActCompat.Infrastructure.Cloud;

namespace DalamudActCompat.UI;

internal sealed class FriendsUiManager : IDisposable
{
    private sealed class ChatWindow
    {
        public bool Open = true;
        public bool Focus = true;
        public bool New = true;
        public bool AtBottom = true;
        public long LastMessage;
        public string Draft = "";
        public Guid? SubmittedOperation;
        public Vector2 Position;
        public Vector2 Size = new(400, 460);
    }
    private static readonly Vector4 Navy = new(.045f, .064f, .09f, .98f);
    private static readonly Vector4 Gold = new(.86f, .73f, .44f, 1);
    private static readonly Vector4 Blue = new(.42f, .78f, .96f, 1);
    private readonly FriendsChatController controller;
    private readonly Action openMain;
    private readonly Dictionary<string, ChatWindow> windows = new(StringComparer.Ordinal);
    private CloudFriendsSession session;
    private Vector2 anchor, anchorSize;
    private bool drawerOpen;
    private string search = "";
    private string? removeId, removeName;
    private long notifiedMessage;
    private int notifiedRequests;
    private long toastUntil;

    public FriendsUiManager(FriendsChatController controller, Action openMain)
    {
        this.controller = controller; this.openMain = openMain;
        // This live component owns the bounded message model even while its windows
        // are closed. Only its lifetime enables delivery acknowledgements.
        controller.AttachConsumer();
    }
    public FriendsChatSnapshot Snapshot => controller.Snapshot;
    public bool AnyOpen => drawerOpen || windows.Values.Any(w => w.Open);
    public void SetAnchor(Vector2 position, Vector2 size) { anchor = position; anchorSize = size; }
    public void ToggleDrawer() { drawerOpen = !drawerOpen; if (drawerOpen) controller.Refresh(); }
    public void Hide() { drawerOpen = false; windows.Clear(); search = ""; toastUntil = 0; }
    public void Draw(bool mainVisible, bool inCombat)
    {
        var state = Snapshot;
        if (session != state.Session)
        {
            Hide(); session = state.Session; notifiedMessage = 0; notifiedRequests = 0; removeId = null;
        }
        if (!state.Session.IsSignedIn) return;
        if (state.Friends is { } friends)
        {
            var active = friends.Friends.Select(f => f.ConversationId).Concat(state.Conversations.Keys).ToHashSet();
            foreach (var id in windows.Keys.Where(id => !active.Contains(id)).ToArray()) windows.Remove(id);
        }
        var requests = state.Friends?.Requests.Count(r => r.Direction == "incoming") ?? 0;
        if (state.LatestUnreadId > notifiedMessage || requests > notifiedRequests)
            toastUntil = Environment.TickCount64 + (inCombat ? 4500 : 6500);
        notifiedMessage = Math.Max(notifiedMessage, state.LatestUnreadId); notifiedRequests = requests;
        PushTheme();
        try
        {
            if (drawerOpen && mainVisible) DrawDrawer(state);
            foreach (var pair in windows.ToArray())
                if (pair.Value.Open) DrawChat(pair.Key, pair.Value, state);
            if (state.HasUnread && Environment.TickCount64 < toastUntil) DrawToast(state, inCombat);
        }
        finally { PopTheme(); }
    }
    private void OpenChat(string id)
    {
        if (!windows.TryGetValue(id, out var window)) windows[id] = window = new();
        window.Open = true; window.Focus = true;
    }
    private void DrawDrawer(FriendsChatSnapshot state)
    {
        var viewport = ImGui.GetMainViewport();
        var scale = Math.Max(.75f, ImGui.GetFontSize() / 17f);
        var layout = FriendsWindowLayout.Drawer(anchor, anchorSize, viewport.WorkPos, viewport.WorkSize, scale);
        ImGui.SetNextWindowPos(layout.Position, ImGuiCond.Always);
        ImGui.SetNextWindowSize(layout.Size, ImGuiCond.Always);
        if (ImGui.Begin("好友###DACTFriendsDrawer", ref drawerOpen,
            ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing))
        {
            ImGui.TextColored(Blue, $"{state.Friends?.OnlineCount ?? 0} 位好友在线");
            ImGui.SameLine();
            if (ImGui.SmallButton("刷新")) controller.Refresh();
            ImGui.TextWrapped(state.Status);
            ImGui.Separator();
            ImGui.TextUnformatted("添加好友");
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##friend-username", "输入完整账号名", ref search, 32);
            ImGui.BeginDisabled(state.Busy || string.IsNullOrWhiteSpace(search));
            if (ImGui.Button("查找账号", new Vector2(-1, 0))) controller.Lookup(search);
            ImGui.EndDisabled();
            if (state.Lookup?.User is { } found)
            {
                ImGui.TextWrapped(found.Username);
                ImGui.BeginDisabled(state.Busy || state.Lookup.Relationship is "friend" or "outgoing");
                if (ImGui.Button(state.Lookup.Relationship == "incoming" ? "同意互加" : state.Lookup.Relationship == "friend" ? "已经是好友" : state.Lookup.Relationship == "outgoing" ? "申请已发出" : "发送好友申请")) controller.Request(found.Username);
                ImGui.EndDisabled();
            }
            ImGui.Separator();
            foreach (var official in state.Conversations.Values.Where(c => c.Chat.Kind == "official"))
                if (ImGui.Selectable($"DACT 官方通知{(official.Unread ? "  ● 未读" : "")}##official-{official.Chat.Id}")) OpenChat(official.Chat.Id);
            ImGui.TextUnformatted("好友列表");
            if (state.Friends?.Friends.Count == 0) ImGui.TextDisabled("还没有好友，先添加一个账号吧。");
            foreach (var friend in (state.Friends?.Friends ?? []).OrderByDescending(f => f.Online).ThenBy(f => f.User.Username))
            {
                var unread = friend.ConversationId is { } id && state.Conversations.GetValueOrDefault(id)?.Unread == true;
                ImGui.PushID(friend.Id);
                ImGui.TextColored(friend.Online ? new Vector4(.4f, .85f, .58f, 1) : new Vector4(.55f, .6f, .66f, 1), friend.Online ? "●" : "○");
                ImGui.SameLine();
                if (ImGui.Selectable($"{friend.User.Username}{(unread ? "  ● 未读" : "")}") && friend.ConversationId is { } conversation) OpenChat(conversation);
                if (ImGui.BeginPopupContextItem("friend-options"))
                {
                    if (ImGui.MenuItem("解除好友关系…")) { removeId = friend.Id; removeName = friend.User.Username; }
                    ImGui.EndPopup();
                }
                ImGui.PopID();
            }
            ImGui.Separator();
            ImGui.TextUnformatted("好友申请");
            foreach (var request in state.Friends?.Requests ?? [])
            {
                ImGui.PushID(request.Id);
                ImGui.TextWrapped($"{request.User.Username} · {(request.Direction == "incoming" ? "希望添加你" : "等待对方确认")}");
                if (request.Direction == "incoming")
                {
                    ImGui.BeginDisabled(state.Busy);
                    if (ImGui.SmallButton("接受")) controller.Accept(request.Id);
                    ImGui.SameLine(); if (ImGui.SmallButton("拒绝")) controller.Decline(request.Id);
                    ImGui.EndDisabled();
                }
                ImGui.PopID();
            }
            if (removeId is not null) ImGui.OpenPopup("解除好友确认");
            if (ImGui.BeginPopup("解除好友确认"))
            {
                ImGui.TextWrapped($"解除与 {removeName} 的好友关系后，双方会话和保留消息将被删除。");
                ImGui.BeginDisabled(state.Busy);
                if (ImGui.Button("确认解除")) { controller.Remove(removeId!); removeId = null; ImGui.CloseCurrentPopup(); }
                ImGui.EndDisabled(); ImGui.SameLine();
                if (ImGui.Button("取消")) { removeId = null; ImGui.CloseCurrentPopup(); }
                ImGui.EndPopup();
            }
            ImGui.Separator(); ImGui.TextWrapped(CloudChatPolicy.Notice);
        }
        ImGui.End();
    }
    private void DrawChat(string id, ChatWindow window, FriendsChatSnapshot state)
    {
        var view = state.Conversations.GetValueOrDefault(id);
        // A queued command may fail before it can prepare a durable send. Keep the
        // editor text until that exact operation has entered the controlled outbox.
        if (window.SubmittedOperation is { } submitted && view?.LastPreparedOperation == submitted)
        { window.Draft = ""; window.SubmittedOperation = null; }
        var official = view?.Chat.Kind == "official";
        var title = official ? "DACT 官方通知" : view?.Chat.Peer.Username ?? "好友聊天";
        var viewport = ImGui.GetMainViewport(); var scale = Math.Max(.75f, ImGui.GetFontSize() / 17f);
        var maximum = viewport.WorkSize - new Vector2(16);
        ImGui.SetNextWindowSizeConstraints(Vector2.Min(new Vector2(320, 340) * scale, maximum), maximum);
        if (window.New)
        {
            window.Size = Vector2.Min(new Vector2(420, 510) * scale, maximum);
            window.Position = FriendsWindowLayout.Clamp(anchor + new Vector2(44, 58), window.Size, viewport.WorkPos, viewport.WorkSize);
            ImGui.SetNextWindowPos(window.Position, ImGuiCond.Always); ImGui.SetNextWindowSize(window.Size, ImGuiCond.Always); window.New = false;
        }
        else
        {
            var clamped = FriendsWindowLayout.Clamp(window.Position, window.Size, viewport.WorkPos, viewport.WorkSize);
            if (clamped != window.Position) ImGui.SetNextWindowPos(clamped, ImGuiCond.Always);
        }
        // Focus is requested only by a user's click, never by arrival/polling.
        if (window.Focus) { ImGui.SetNextWindowFocus(); window.Focus = false; }
        if (ImGui.Begin($"{title}###DACTFriendChat-{id}", ref window.Open, ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing))
        {
            window.Position = ImGui.GetWindowPos(); window.Size = ImGui.GetWindowSize();
            var focused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
            if (view is null) { ImGui.TextWrapped("会话正在同步，或好友关系已解除。"); }
            else
            {
                ImGui.TextColored(official ? Gold : Blue, official ? "DACT 官方 · 只读通知" : title);
                if (state.State != "ready") ImGui.TextWrapped(state.Status);
                var contentWidth = ImGui.GetContentRegionAvail().X;
                var noticeHeight = ImGui.CalcTextSize(CloudChatPolicy.Notice, false, contentWidth).Y + 18 * scale;
                var statusHeight = string.IsNullOrEmpty(view.SendStatus) ? 0 : ImGui.CalcTextSize(view.SendStatus, false, contentWidth).Y;
                var footer = noticeHeight + (official ? 38 * scale : (view.PendingSend is null ? 82 : 175) * scale + statusHeight);
                var historyHeight = Math.Max(90 * scale, ImGui.GetContentRegionAvail().Y - footer);
                if (ImGui.BeginChild("messages", new Vector2(-1, historyHeight), false))
                {
                    var messages = view.Chat.History.Concat(view.Chat.Pending).OrderBy(m => m.Id).ToArray();
                    foreach (var message in messages) DrawBubble(message, message.Sender.UserId == state.Friends?.User?.Id);
                    if (messages.Length == 0) ImGui.TextDisabled("暂无消息。");
                    var last = messages.LastOrDefault()?.Id ?? 0;
                    if (window.AtBottom && last != window.LastMessage) ImGui.SetScrollHereY(1);
                    window.LastMessage = last;
                    window.AtBottom = ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 4;
                    if (focused && window.AtBottom)
                    {
                        var incoming = messages.Where(m => m.RecipientId == state.Friends?.User?.Id).Select(m => m.Id).DefaultIfEmpty().Max();
                        controller.MarkRead(id, incoming);
                    }
                }
                ImGui.EndChild();
                if (official) ImGui.TextWrapped("这里接收 DACT 官方通知，不支持回复。");
                else
                {
                    if (view.PendingSend is { } pending)
                    {
                        var preview = pending.Text ?? (pending.QuickMessageId == CloudChatPolicy.InviteNext ? "下把邀我" : "你什么时候结束");
                        ImGui.TextWrapped($"待确认：{(preview.Length > 60 ? preview[..60] + "…" : preview)}");
                        if (preview.Length > 60 && ImGui.IsItemHovered())
                        {
                            ImGui.BeginTooltip(); ImGui.PushTextWrapPos(ImGui.GetFontSize() * 28);
                            ImGui.TextUnformatted(preview); ImGui.PopTextWrapPos(); ImGui.EndTooltip();
                        }
                        ImGui.BeginDisabled(state.Busy);
                        if (ImGui.Button("重试原消息")) controller.Retry(id);
                        ImGui.SameLine(); if (ImGui.Button("放弃此发送…")) ImGui.OpenPopup("discard-pending");
                        ImGui.EndDisabled();
                        if (ImGui.BeginPopup("discard-pending"))
                        {
                            ImGui.TextWrapped("原消息可能已送达。放弃后请先查看会话记录，再决定是否重新输入发送。");
                            if (ImGui.Button("确认放弃")) { controller.Discard(id); ImGui.CloseCurrentPopup(); }
                            ImGui.EndPopup();
                        }
                    }
                    ImGui.BeginDisabled(state.Busy || view.PendingSend is not null);
                    ImGui.SetNextItemWidth(-1);
                    var enter = ImGui.InputTextWithHint("##chat-draft", "输入消息，按回车发送", ref window.Draft, 4000, ImGuiInputTextFlags.EnterReturnsTrue);
                    if (enter && !string.IsNullOrWhiteSpace(window.Draft) && controller.Send(id, window.Draft) is { } enterOperation)
                        window.SubmittedOperation = enterOperation;
                    if (ImGui.Button("发送") && !string.IsNullOrWhiteSpace(window.Draft) && controller.Send(id, window.Draft) is { } buttonOperation)
                        window.SubmittedOperation = buttonOperation;
                    ImGui.SameLine(); if (ImGui.Button("下把邀我")) controller.Send(id, "", CloudChatPolicy.InviteNext);
                    ImGui.SameLine(); if (ImGui.Button("你什么时候结束")) controller.Send(id, "", CloudChatPolicy.WhenFinished);
                    ImGui.EndDisabled();
                    if (!string.IsNullOrEmpty(view.SendStatus)) ImGui.TextWrapped(view.SendStatus);
                }
                ImGui.Separator(); ImGui.TextWrapped(CloudChatPolicy.Notice);
            }
        }
        ImGui.End();
    }
    private static void DrawBubble(CloudChatMessage message, bool own)
    {
        var available = ImGui.GetContentRegionAvail().X;
        var width = Math.Max(100, available * .85f);
        var bodyHeight = ImGui.CalcTextSize(message.Text, false, width - 20).Y;
        var start = ImGui.GetCursorScreenPos() + new Vector2(own ? Math.Max(0, available - width) : 0, 0);
        var height = bodyHeight + ImGui.GetTextLineHeightWithSpacing() * 2 + 18;
        var background = own ? new Vector4(.09f, .23f, .31f, 1) : new Vector4(.09f, .115f, .15f, 1);
        ImGui.GetWindowDrawList().AddRectFilled(start, start + new Vector2(width, height), ImGui.GetColorU32(background), 8);
        if (message.Sender.IsOfficial) ImGui.GetWindowDrawList().AddRect(start, start + new Vector2(width, height), ImGui.GetColorU32(Gold), 8);
        var original = ImGui.GetCursorPos();
        ImGui.SetCursorScreenPos(start + new Vector2(10, 7));
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width - 20);
        ImGui.TextColored(message.Sender.IsOfficial ? Gold : Blue, message.Sender.IsOfficial ? "DACT 官方" : message.Sender.Name);
        ImGui.TextUnformatted(message.Text);
        ImGui.TextDisabled($"{message.CreatedAt.ToLocalTime():MM-dd HH:mm}{(message.State == "pending" ? " · 待上线接收" : "")}");
        ImGui.PopTextWrapPos();
        ImGui.SetCursorPos(original); ImGui.Dummy(new Vector2(available, height + 8));
    }
    private void DrawToast(FriendsChatSnapshot state, bool inCombat)
    {
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.WorkPos + new Vector2(viewport.WorkSize.X - 18, 60), ImGuiCond.Always, new Vector2(1, 0));
        ImGui.SetNextWindowBgAlpha(inCombat ? .7f : .92f);
        if (ImGui.Begin("##DACTFriendNotification", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoDecoration |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav))
        {
            var official = state.Conversations.Values.Any(c => c.Unread && c.Chat.Kind == "official");
            if (ImGui.SmallButton(official ? "● DACT 官方通知 · 点击查看" : "● 好友消息 / 申请 · 点击查看"))
            { openMain(); drawerOpen = true; toastUntil = 0; }
        }
        ImGui.End();
    }
    private static void PushTheme()
    {
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Navy); ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(.45f, .39f, .25f, .8f));
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(.1f, .23f, .31f, 1)); ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(.16f, .34f, .43f, 1));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 9); ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 5);
    }
    private static void PopTheme() { ImGui.PopStyleVar(2); ImGui.PopStyleColor(4); }
    public void Dispose() { controller.DetachConsumer(); Hide(); }
}

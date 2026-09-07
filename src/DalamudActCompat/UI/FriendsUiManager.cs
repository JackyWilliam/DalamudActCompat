using System.Numerics;
using Dalamud.Bindings.ImGui;
using DalamudActCompat.Infrastructure.Cloud;

namespace DalamudActCompat.UI;

internal sealed partial class FriendsUiManager : IDisposable
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
    private uint anchorWindowId;
    private bool drawerOpen;
    private float drawerProgress, anchorAlpha = 1;
    private CloudPresenceSettings? editingSettings, submittedSettings;
    private bool settingsDirty, shareSubmission, presenceChangedElsewhere, privacySaveUnconfirmed;
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
    public bool AnyOpen => drawerOpen || drawerProgress > 0 || windows.Values.Any(w => w.Open);
    public void SetAnchor(Vector2 position, Vector2 size, float alpha = 1, uint windowId = 0)
    { anchor = position; anchorSize = size; anchorAlpha = alpha; anchorWindowId = windowId; }
    public void ToggleDrawer() { drawerOpen = !drawerOpen; if (drawerOpen) controller.Refresh(); }
    public void Hide() { drawerOpen = false; drawerProgress = 0; windows.Clear(); search = ""; toastUntil = 0; editingSettings = submittedSettings = null; settingsDirty = presenceChangedElsewhere = privacySaveUnconfirmed = false; }
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
            drawerProgress = Math.Clamp(drawerProgress + (drawerOpen ? 1 : -1) * ImGui.GetIO().DeltaTime / .16f, 0, 1);
            if (drawerProgress > 0 && mainVisible) DrawDrawer(state);
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
        var scale = Math.Max(.75f, ImGui.GetFontSize() / 17f);
        var padding = Math.Max(3, 10 * scale);
        var vertical = 8 * scale;
        var width = Math.Min(available, Math.Max(100 * scale, available * .85f));
        var textWidth = Math.Max(1, width - padding * 2);
        var author = message.Sender.IsOfficial ? "DACT 官方" : message.Sender.Name;
        var timestamp = $"{message.CreatedAt.ToLocalTime():MM-dd HH:mm}{(message.State == "pending" ? " · 待上线接收" : "")}";
        var authorHeight = ImGui.CalcTextSize(author, false, textWidth).Y;
        var bodyHeight = ImGui.CalcTextSize(message.Text, false, textWidth).Y;
        var timeHeight = ImGui.CalcTextSize(timestamp, false, textWidth).Y;
        var start = ImGui.GetCursorScreenPos() + new Vector2(own ? Math.Max(0, available - width) : 0, 0);
        var gap = 5 * scale;
        var height = authorHeight + bodyHeight + timeHeight + vertical * 2 + gap * 2;
        var background = own ? new Vector4(.09f, .23f, .31f, 1) : new Vector4(.09f, .115f, .15f, 1);
        ImGui.GetWindowDrawList().AddRectFilled(start, start + new Vector2(width, height), ImGui.GetColorU32(background), 8);
        if (message.Sender.IsOfficial) ImGui.GetWindowDrawList().AddRect(start, start + new Vector2(width, height), ImGui.GetColorU32(Gold), 8);
        // ImGui resets the next item's X to the window indent after Text(). Draw
        // all three blocks from explicit padded origins so every wrapped line stays inside.
        var list = ImGui.GetWindowDrawList(); var font = ImGui.GetFont(); var fontSize = ImGui.GetFontSize();
        var origin = start + new Vector2(padding, vertical);
        list.AddText(font, fontSize, origin, ImGui.GetColorU32(message.Sender.IsOfficial ? Gold : Blue), author, textWidth);
        origin.Y += authorHeight + gap;
        list.AddText(font, fontSize, origin, ImGui.GetColorU32(Vector4.One), message.Text, textWidth);
        origin.Y += bodyHeight + gap;
        list.AddText(font, fontSize, origin, ImGui.GetColorU32(new Vector4(.62f, .69f, .75f, 1)), timestamp, textWidth);
        ImGui.Dummy(new Vector2(available, height + 8 * scale));
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

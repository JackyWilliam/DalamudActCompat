using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using DalamudActCompat.Infrastructure.Cloud;
using DalamudActCompat.Plugin;

namespace DalamudActCompat.UI;

internal sealed partial class FriendsUiManager : IDisposable
{
    private sealed class ChatWindow
    {
        public bool Open = true;
        public bool Focus = true;
        public bool New = true;
        public bool AtBottom = true;
        public bool ScrollToLatest = true;
        public readonly WindowDragController Drag = new();
        public long LastMessage;
        public string Draft = "";
        public Guid? SubmittedOperation;
        public Vector2 Position;
        public Vector2 Size = new(400, 460);
    }
    private static Vector4 Navy => DactTheme.Tone(ControlCenterWindow.Navy, DactTheme.Palette.Surface);
    private static Vector4 Gold => DactTheme.Tone(ControlCenterWindow.Gold, DactTheme.Palette.Gold);
    private static Vector4 Blue => DactTheme.Tone(ControlCenterWindow.IceBlue, DactTheme.Palette.Accent);
    private readonly FriendsChatController controller;
    private readonly Action openMain;
    private readonly PluginConfiguration configuration;
    private readonly UiText uiText;
    private readonly Func<long> clock;
    private readonly Action? playNotificationSound, stopNotificationSound;
    private readonly FriendsIncomingNotifications incoming = new();
    private readonly ISharedImmediateTexture? administratorIcon;
    private readonly ISharedImmediateTexture? sponsorIcon;
    private uint lastNonNotificationFocus;
    private long lastSoundAt = long.MinValue;
    private bool notificationSoundWasEnabled = true;
    private readonly Dictionary<string, ChatWindow> windows = new(StringComparer.Ordinal);
    private CloudFriendsSession session;
    private Vector2 anchor, anchorSize;
    private uint anchorWindowId;
    private bool drawerOpen;
    private int friendSection;
    private float drawerProgress, anchorAlpha = 1;
    private CloudPresenceSettings? editingSettings, submittedSettings;
    private bool settingsDirty, shareSubmission, presenceChangedElsewhere, privacySaveUnconfirmed;
    private string search = "";
    private string? removeId, removeName;
    private string? remarkRelationId;
    private string remarkDraft = "", remarkError = "";
    private bool openRemarkEditor;
    private Guid? submittedRemark;
    private long remarkRevision;
    private int notifiedRequests;
    private long toastUntil;

    public FriendsUiManager(FriendsChatController controller, Action openMain, PluginConfiguration? configuration = null,
        Func<long>? clock = null, Action? playNotificationSound = null, Action? stopNotificationSound = null,
        ISharedImmediateTexture? administratorIcon = null, ISharedImmediateTexture? sponsorIcon = null)
    {
        this.controller = controller; this.openMain = openMain;
        this.administratorIcon = administratorIcon;
        this.sponsorIcon = sponsorIcon;
        this.configuration = configuration ?? new(); this.clock = clock ?? (() => Environment.TickCount64);
        uiText = new UiText(this.configuration);
        this.playNotificationSound = playNotificationSound; this.stopNotificationSound = stopNotificationSound;
        // Recreating a view against an already-live controller is not a new arrival.
        incoming.Update(controller.Snapshot, false, this.clock());
        // This live component owns the bounded message model even while its windows
        // are closed. Only its lifetime enables delivery acknowledgements.
        controller.AttachConsumer();
    }
    public FriendsChatSnapshot Snapshot => controller.Snapshot;
    public void DrawAccountName(CloudClientSnapshot account)
        => AccountIdentityBadge.Text(administratorIcon, account.Username ?? string.Empty,
            account.IsSignedIn && account.Administrator?.IsAdmin == true, DactTheme.Palette.Text,
            sponsorIcon, account.IsSignedIn ? account.Sponsor?.Tier ?? 0 : 0, text: uiText);
    public bool AnyOpen => drawerOpen || drawerProgress > 0 || windows.Values.Any(w => w.Open);
    public void SetAnchor(Vector2 position, Vector2 size, float alpha = 1, uint windowId = 0)
    { anchor = position; anchorSize = size; anchorAlpha = alpha; anchorWindowId = windowId; }
    public void ToggleDrawer() { drawerOpen = !drawerOpen; if (drawerOpen) controller.Refresh(); }
    public void Hide() { drawerOpen = false; drawerProgress = 0; friendSection = 0; windows.Clear(); search = ""; toastUntil = 0; editingSettings = submittedSettings = null; settingsDirty = presenceChangedElsewhere = privacySaveUnconfirmed = false; remarkRelationId = null; remarkDraft = remarkError = ""; submittedRemark = null; openRemarkEditor = false; incoming.ClearVisible(); stopNotificationSound?.Invoke(); }
    public void Draw(bool mainVisible, bool inCombat)
    {
        var state = Snapshot;
        if (session != state.Session)
        {
            Hide(); session = state.Session; notifiedRequests = 0; removeId = null; lastNonNotificationFocus = 0; lastSoundAt = long.MinValue;
        }
        var now = clock();
        var arrived = incoming.Update(state, configuration.FriendNotificationsEnabled, now);
        if (notificationSoundWasEnabled && !configuration.FriendNotificationSoundEnabled) stopNotificationSound?.Invoke();
        notificationSoundWasEnabled = configuration.FriendNotificationSoundEnabled;
        if (arrived && configuration.FriendNotificationSoundEnabled && (lastSoundAt == long.MinValue || now - lastSoundAt >= 1200))
        { playNotificationSound?.Invoke(); lastSoundAt = now; }
        if (!state.Session.IsSignedIn) return;
        if (state.Friends is { } friends)
        {
            var active = friends.Friends.Select(f => f.ConversationId).Concat(state.Conversations.Keys).ToHashSet();
            foreach (var id in windows.Keys.Where(id => !active.Contains(id)).ToArray()) windows.Remove(id);
        }
        var requests = state.Friends?.Requests.Count(r => r.Direction == "incoming") ?? 0;
        if (requests > notifiedRequests) toastUntil = now + (inCombat ? 4500 : 6500);
        notifiedRequests = requests;
        PushTheme();
        try
        {
            drawerProgress = Math.Clamp(drawerProgress + (drawerOpen ? 1 : -1) * ImGui.GetIO().DeltaTime / .16f, 0, 1);
            if (drawerProgress > 0 && mainVisible) DrawDrawer(state);
            foreach (var pair in windows.ToArray())
                if (pair.Value.Open) DrawChat(pair.Key, pair.Value, state);
            if (requests > 0 && now < toastUntil) DrawToast(state, inCombat);
            DrawIncomingNotifications(state, now);
        }
        finally { PopTheme(); }
    }
    private void OpenChat(string id)
    {
        if (!windows.TryGetValue(id, out var window)) windows[id] = window = new();
        window.Open = true; window.Focus = true; window.ScrollToLatest = true;
    }
    private void DrawChat(string id, ChatWindow window, FriendsChatSnapshot state)
    {
        var view = state.Conversations.GetValueOrDefault(id);
        // A queued command may fail before it can prepare a durable send. Keep the
        // editor text until that exact operation has entered the controlled outbox.
        if (window.SubmittedOperation is { } submitted && view?.LastPreparedOperation == submitted)
        { window.Draft = ""; window.SubmittedOperation = null; }
        var official = view?.Chat.Kind == "official";
        var title = official ? uiText.Get("DACT 官方通知", "DACT announcements") : view is null ? uiText.Get("好友聊天", "Friend chat") : state.FriendDisplayName(view.Chat.Peer.Id, view.Chat.Peer.Username);
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
        window.Drag.PrepareNextWindow();
        var flags = DactTheme.WindowFlags(
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoMove);
        var gameFrame = (flags & ImGuiWindowFlags.NoBackground) != 0;
        // The sprite has transparent outer pixels. Suppress the original fill
        // and outline only once textures are ready, or they show as a second rim.
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, gameFrame ? 0 : ImGui.GetStyle().WindowBorderSize);
        var expanded = ImGui.Begin($"{title}###DACTFriendChat-{id}", ref window.Open, flags);
        ImGui.PopStyleVar();
        if (expanded)
        {
            window.Position = ImGui.GetWindowPos(); window.Size = ImGui.GetWindowSize();
            if (gameFrame)
            {
                // Fill inside the same eight-pixel strips used by PopupFrame,
                // leaving its outer transparency and rounded corners intact.
                var inset = new Vector2(8 * scale);
                // The opaque fill belongs to the game sprite. Glass must sample
                // the actual scene here, before chat content is drawn.
                if (!DactTheme.Palette.Glass && !DactTheme.Palette.Rain)
                    ImGui.GetWindowDrawList().AddRectFilled(window.Position + inset,
                        window.Position + window.Size - inset, ImGui.GetColorU32(Navy));
                DactTheme.DrawGamePopupFrame();
            }
            DrawChatHeader(window, title, official, view?.Chat.Peer, scale);
            var focused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
            if (view is null) { ImGui.TextWrapped(uiText.Get("会话正在同步，或好友关系已解除。", "This conversation is syncing, or the friendship has ended.")); }
            else
            {
                if (state.State != "ready") ImGui.TextWrapped(uiText.SystemMessage(state.Status));
                var contentWidth = ImGui.GetContentRegionAvail().X;
                var sendStatus = uiText.SystemMessage(view.SendStatus);
                var statusHeight = string.IsNullOrEmpty(sendStatus) ? 0 : ImGui.CalcTextSize(sendStatus, false, contentWidth).Y;
                var sendLabel = uiText.Get("发送", "Send");
                var inviteLabel = uiText.Get("下把邀我", "Invite me next time");
                var finishLabel = uiText.Get("你什么时候结束", "When will you finish?");
                var retryLabel = uiText.Get("重试原消息", "Retry original message");
                var discardLabel = uiText.Get("放弃此发送…", "Discard this send…");
                // Measure wrapped action rows before history takes its space, so
                // longer translations keep the input and retry controls reachable.
                var extraFooter = ExtraButtonRowHeight(contentWidth, sendLabel, inviteLabel, finishLabel) +
                    (view.PendingSend is null ? 0 : ExtraButtonRowHeight(contentWidth, retryLabel, discardLabel));
                // The usage notice lives once in the friend drawer. Reclaim its
                // former footer space here while keeping official/read-only identity.
                var footer = official ? 38 * scale : (view.PendingSend is null ? 82 : 175) * scale + statusHeight + extraFooter;
                var historyHeight = Math.Max(90 * scale, ImGui.GetContentRegionAvail().Y - footer);
                if (ImGui.BeginChild("messages", new Vector2(-1, historyHeight), false))
                {
                    var messages = view.Chat.History.Concat(view.Chat.Pending).OrderBy(m => m.Id).ToArray();
                    foreach (var message in messages)
                        DrawBubble(message, message.Sender.UserId == state.Friends?.User?.Id, administratorIcon, sponsorIcon,
                            message.Sender.IsOfficial ? null : state.FriendDisplayName(message.Sender.UserId, message.Sender.Name), uiText);
                    if (messages.Length == 0) ImGui.TextDisabled(uiText.Get("暂无消息。", "No messages yet."));
                    var last = messages.LastOrDefault()?.Id ?? 0;
                    // Explicitly entering from a notification also reopens an old
                    // scrolled view. Read only once its latest content is visible.
                    if (window.ScrollToLatest || window.AtBottom && last != window.LastMessage) ImGui.SetScrollHereY(1);
                    window.ScrollToLatest = false;
                    window.LastMessage = last;
                    window.AtBottom = ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 4;
                    if (focused && window.AtBottom)
                    {
                        var incoming = messages.Where(m => m.RecipientId == state.Friends?.User?.Id).Select(m => m.Id).DefaultIfEmpty().Max();
                        controller.MarkRead(id, incoming);
                    }
                }
                ImGui.EndChild();
                if (official) ImGui.TextWrapped(uiText.Get("这里接收 DACT 官方通知，不支持回复。", "DACT announcements appear here. Replies are disabled."));
                else
                {
                    if (view.PendingSend is { } pending)
                    {
                        var preview = pending.Text ?? (pending.QuickMessageId == CloudChatPolicy.InviteNext ? uiText.Get("下把邀我", "Invite me next time") : uiText.Get("你什么时候结束", "When will you finish?"));
                        ImGui.TextWrapped(uiText.Format($"待确认：{(preview.Length > 60 ? preview[..60] + "…" : preview)}", $"Unconfirmed: {(preview.Length > 60 ? preview[..60] + "…" : preview)}"));
                        if (preview.Length > 60 && ImGui.IsItemHovered())
                        {
                            ImGui.BeginTooltip(); ImGui.PushTextWrapPos(ImGui.GetFontSize() * 28);
                            ImGui.TextUnformatted(preview); ImGui.PopTextWrapPos(); ImGui.EndTooltip();
                        }
                        ImGui.BeginDisabled(state.Busy);
                        var retryRowRight = ImGui.GetCursorScreenPos().X + contentWidth;
                        if (DactTheme.Button(retryLabel)) controller.Retry(id);
                        ContinueButtonRow(discardLabel, retryRowRight);
                        if (DactTheme.Button(discardLabel)) ImGui.OpenPopup("discard-pending");
                        ImGui.EndDisabled();
                        if (ImGui.BeginPopup("discard-pending"))
                        {
                            ImGui.TextWrapped(uiText.Get("原消息可能已送达。放弃后请先查看会话记录，再决定是否重新输入发送。", "The original message may have arrived. After discarding, check the conversation before composing another message."));
                            if (DactTheme.Button(uiText.Get("确认放弃", "Confirm discard"))) { controller.Discard(id); ImGui.CloseCurrentPopup(); }
                            ImGui.EndPopup();
                        }
                    }
                    ImGui.BeginDisabled(state.Busy || view.PendingSend is not null);
                    ImGui.SetNextItemWidth(-1);
                    var enter = ImGui.InputTextWithHint("##chat-draft", uiText.Get("输入消息，按回车发送", "Type a message; press Enter to send"), ref window.Draft, 4000, ImGuiInputTextFlags.EnterReturnsTrue);
                    if (enter && !string.IsNullOrWhiteSpace(window.Draft) && controller.Send(id, window.Draft) is { } enterOperation)
                        window.SubmittedOperation = enterOperation;
                    var buttonRowRight = ImGui.GetCursorScreenPos().X + contentWidth;
                    if (DactTheme.Button(sendLabel) && !string.IsNullOrWhiteSpace(window.Draft) && controller.Send(id, window.Draft) is { } buttonOperation)
                        window.SubmittedOperation = buttonOperation;
                    ContinueButtonRow(inviteLabel, buttonRowRight);
                    if (DactTheme.Button(inviteLabel)) controller.Send(id, "", CloudChatPolicy.InviteNext);
                    ContinueButtonRow(finishLabel, buttonRowRight);
                    if (DactTheme.Button(finishLabel)) controller.Send(id, "", CloudChatPolicy.WhenFinished);
                    ImGui.EndDisabled();
                    if (!string.IsNullOrEmpty(sendStatus)) ImGui.TextWrapped(sendStatus);
                }
            }
        }
        ImGui.End();
    }

    private void DrawChatHeader(ChatWindow window, string title, bool official, CloudApiUser? peer, float scale)
    {
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var height = ImGui.GetFontSize() + 14 * scale;
        var dragWidth = Math.Max(1, width - height - ImGui.GetStyle().ItemSpacing.X);
        // A dedicated handle keeps clicking messages/input from dragging the
        // window, while the custom close control cannot start a drag.
        ImGui.InvisibleButton("##chat-header-drag", new Vector2(dragWidth, height));
        window.Drag.HandleItem();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(title);
        AccountIdentityBadge.DrawName(administratorIcon, title, !official && peer?.IsAdmin == true,
            start + new Vector2(4 * scale, 7 * scale), dragWidth - 8 * scale, official ? Gold : Blue,
            sponsorIcon, official ? 0 : peer?.SponsorTier ?? 0, text: uiText);
        ImGui.SameLine();
        var close = ImGui.GetCursorScreenPos();
        if (ImGui.InvisibleButton("##chat-close", new Vector2(height))) { window.Open = false; window.Drag.Cancel(); }
        var list = ImGui.GetWindowDrawList();
        if (ImGui.IsItemHovered())
        {
            list.AddRectFilled(close, close + new Vector2(height), ImGui.GetColorU32(DactTheme.Palette.Surface), 4 * scale);
            ImGui.SetTooltip(uiText.Get("关闭聊天", "Close chat"));
        }
        var inset = height * .34f;
        var color = ImGui.GetColorU32(DactTheme.Palette.Text);
        list.AddLine(close + new Vector2(inset), close + new Vector2(height - inset), color, 1.5f * scale);
        list.AddLine(close + new Vector2(height - inset, inset), close + new Vector2(inset, height - inset), color, 1.5f * scale);
        list.AddLine(start + new Vector2(0, height), start + new Vector2(width, height), ImGui.GetColorU32(Blue), scale);
    }
    private static float ExtraButtonRowHeight(float available, params ReadOnlySpan<string> labels)
    {
        var used = 0f; var rows = 0;
        foreach (var label in labels)
        {
            var width = ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2;
            if (used > 0 && used + ImGui.GetStyle().ItemSpacing.X + width > available) { rows++; used = 0; }
            used += (used > 0 ? ImGui.GetStyle().ItemSpacing.X : 0) + width;
        }
        return rows * ImGui.GetFrameHeightWithSpacing();
    }

    private static void ContinueButtonRow(string nextLabel, float right)
    {
        var width = ImGui.CalcTextSize(nextLabel).X + ImGui.GetStyle().FramePadding.X * 2;
        if (ImGui.GetItemRectMax().X + ImGui.GetStyle().ItemSpacing.X + width <= right) ImGui.SameLine();
    }

    private static void DrawBubble(CloudChatMessage message, bool own, ISharedImmediateTexture? administratorIcon = null,
        ISharedImmediateTexture? sponsorIcon = null, string? displayName = null, UiText? uiText = null)
    {
        uiText ??= new UiText(new PluginConfiguration());
        var available = ImGui.GetContentRegionAvail().X;
        var scale = Math.Max(.75f, ImGui.GetFontSize() / 17f);
        var padding = Math.Max(3, 10 * scale);
        var vertical = 8 * scale;
        var width = Math.Min(available, Math.Max(100 * scale, available * .85f));
        var textWidth = Math.Max(1, width - padding * 2);
        var author = message.Sender.IsOfficial ? uiText.Get("DACT 官方", "DACT team") : displayName ?? message.Sender.Name;
        var timestamp = $"{message.CreatedAt.ToLocalTime():MM-dd HH:mm}{(message.State == "pending" ? uiText.Get(" · 待上线接收", " · Waiting for recipient") : "")}";
        var hasIdentity = !message.Sender.IsOfficial && (message.Sender.IsAdmin || message.Sender.SponsorTier > 0);
        var authorHeight = hasIdentity ? ImGui.GetTextLineHeight() : ImGui.CalcTextSize(author, false, textWidth).Y;
        var bodyHeight = ImGui.CalcTextSize(uiText.MessageBody(message), false, textWidth).Y;
        var timeHeight = ImGui.CalcTextSize(timestamp, false, textWidth).Y;
        var start = ImGui.GetCursorScreenPos() + new Vector2(own ? Math.Max(0, available - width) : 0, 0);
        var gap = 5 * scale;
        var height = authorHeight + bodyHeight + timeHeight + vertical * 2 + gap * 2;
        var background = DactTheme.Tone(own ? new Vector4(.09f, .23f, .31f, 1) : new Vector4(.09f, .115f, .15f, 1), own ? DactTheme.Palette.Hover : DactTheme.Palette.Raised);
        ImGui.GetWindowDrawList().AddRectFilled(start, start + new Vector2(width, height), ImGui.GetColorU32(background), 8);
        if (message.Sender.IsOfficial) ImGui.GetWindowDrawList().AddRect(start, start + new Vector2(width, height), ImGui.GetColorU32(Gold), 8);
        // ImGui resets the next item's X to the window indent after Text(). Draw
        // all three blocks from explicit padded origins so every wrapped line stays inside.
        var list = ImGui.GetWindowDrawList(); var font = ImGui.GetFont(); var fontSize = ImGui.GetFontSize();
        var origin = start + new Vector2(padding, vertical);
        if (hasIdentity)
            AccountIdentityBadge.DrawName(administratorIcon, author, message.Sender.IsAdmin, origin, textWidth, Blue,
                sponsorIcon, message.Sender.SponsorTier, text: uiText);
        else list.AddText(font, fontSize, origin, ImGui.GetColorU32(message.Sender.IsOfficial ? Gold : Blue), author, textWidth);
        origin.Y += authorHeight + gap;
        list.AddText(font, fontSize, origin, ImGui.GetColorU32(DactTheme.Palette.Text), uiText.MessageBody(message), textWidth);
        origin.Y += bodyHeight + gap;
        list.AddText(font, fontSize, origin, ImGui.GetColorU32(DactTheme.Palette.Muted), timestamp, textWidth);
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
            if (DactTheme.SmallButton(uiText.Get("● 新好友申请 · 点击查看", "● New friend request · View")))
            { openMain(); drawerOpen = true; toastUntil = 0; }
        }
        ImGui.End();
    }
    private static void PushTheme()
    {
        // Share the owner's exact input/button/spacing theme rather than grow a
        // second palette that drifts from cloud sync and the rest of DACT.
        ControlCenterWindow.PushTheme();
        DactTheme.PushStyleColor(ImGuiCol.WindowBg, Navy); DactTheme.PushStyleColor(ImGuiCol.PopupBg, Navy);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 9); ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 9);
    }
    private static void PopTheme() { ImGui.PopStyleVar(2); ImGui.PopStyleColor(2); ControlCenterWindow.PopTheme(); }
    public void Dispose() { controller.DetachConsumer(); Hide(); }
}

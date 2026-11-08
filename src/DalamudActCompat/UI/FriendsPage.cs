using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using DalamudActCompat.Infrastructure.Cloud;
using DalamudActCompat.UI.Kit;

namespace DalamudActCompat.UI;

// 好友 as a control-centre page (tools/ui-redesign/screens.js friends, Figma 08 好友弹层与状态):
// the friend list on the left (my status, 好友 / 申请 / 添加) and the open conversation on the
// right. It replaces the side drawer; a conversation can still float as its own window
// (「弹出为独立窗口」), which DrawChat keeps drawing outside the control centre.
internal sealed partial class FriendsUiManager
{
    private const string StatusPopover = "##friends-status";
    private const string RemarkPopover = "##friends-remark";
    private const string MoreMenu = "##friends-more";
    private const string RemoveDialog = "##friends-remove";
    private const string DiscardDialog = "##friends-discard";

    private string? pageConversation;
    private readonly Dictionary<string, ChatWindow> pageViews = new(StringComparer.Ordinal);
    private bool controlCentreFocused;

    // Opened from the page header's 「来信提醒设置」.
    public Action? OpenNotificationSettings { get; set; }

    // Selecting a conversation (a notification's 「全部展开」, a list row) shows it on the page.
    public void ShowConversation(string id)
    {
        pageConversation = id;
        friendSection = 0;
        if (!pageViews.TryGetValue(id, out var view)) pageViews[id] = view = new();
        view.ScrollToLatest = true;
        openMain();
    }

    public void ShowRequests()
    {
        friendSection = 1;
        openMain();
    }

    public void DrawPage()
    {
        var state = Snapshot;
        var s = DactUi.S;
        controlCentreFocused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
        PushPageTheme();
        try
        {
            var online = state.State == "ready" ? state.Friends?.OnlineCount ?? 0 : 0;
            var settingsLabel = uiText.Get("来信提醒设置", "Message alert settings");
            DactUi.PageHeader(uiText.Get("好友", "Friends"),
                uiText.Format($"{online} 人在线 · 消息与申请会同步到你的 DACT 账号。", $"{online} online · Messages and requests sync to your DACT account."),
                DactUi.ButtonWidth(settingsLabel, icon: "gear"),
                () =>
                {
                    if (DactUi.Button(settingsLabel + "##friends-alerts", icon: "gear")) OpenNotificationSettings?.Invoke();
                });

            var wide = ImGui.GetContentRegionAvail().X >= 600 * s;
            var height = Math.Max(360 * s, ImGui.GetContentRegionAvail().Y);
            if (DactUi.BeginPanel("friends-list", new Vector2(wide ? 270 * s : -1, wide ? height : 300 * s), 12, 10))
                DrawFriendColumn(state);
            DactUi.EndPanel();
            if (wide) ImGui.SameLine(0, 14 * s);
            if (DactUi.BeginPanel("friends-conversation", new Vector2(-1, height), 14, 12))
                DrawPageConversation(state);
            DactUi.EndPanel();
            DrawRemoveDialog(state);
        }
        finally { PopPageTheme(); }
    }

    // Child windows inherit the control centre's colours; only the scrollbars are slimmed here.
    private static void PushPageTheme()
    {
        var s = DactUi.S;
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize, 8 * s);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarBg, Vector4.Zero);
    }

    private static void PopPageTheme()
    {
        ImGui.PopStyleColor();
        ImGui.PopStyleVar();
    }

    // ---- Left column

    private void DrawFriendColumn(FriendsChatSnapshot state)
    {
        var s = DactUi.S;
        DrawMe(state);
        var sections = new[] { uiText.Get("好友", "Friends"), uiText.Get("申请", "Requests"), uiText.Get("添加", "Add") };
        var origin = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        DactUi.Segmented("friends-sections", sections, ref friendSection, width);
        if (state.HasIncomingRequests)
        {
            // Incoming requests mark their segment with the unread red dot.
            var segment = (width - 6 * s - 4 * s) / 3;
            var center = origin.X + 3 * s + segment + 2 * s + segment * .5f;
            var dot = new Vector2(center + DactUi.TextWidth(sections[1]) * .5f + 7 * s, origin.Y + 11 * s);
            ImGui.GetWindowDrawList().AddCircleFilled(dot, 3.5f * s, DactUi.U32(new Vector4(.85f, .33f, .31f, 1)), 12);
        }

        // The list scrolls on its own; the usage notice stays pinned under it.
        var notice = uiText.Get(CloudChatPolicy.Notice,
            "Do not use messaging for money laundering, fake transactions, fraud, or other prohibited activities. Administrators may review suspected violations.");
        float noticeHeight;
        using (DactFonts.Push(DactFonts.Small))
            noticeHeight = ImGui.CalcTextSize(string.Join("\n", DactUi.WrapLines(notice, width))).Y;
        var listHeight = Math.Max(80 * s, ImGui.GetContentRegionAvail().Y - noticeHeight - 10 * s);
        // The panel already pads the column; the scrolling list adds none of its own.
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0, 2) * s);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Vector4.Zero);
        if (ImGui.BeginChild($"friends-section-{friendSection}", new Vector2(-1, listHeight), false))
        {
            if (state.State != "ready" && friendSection == 0)
                DactUi.Wrapped(uiText.SystemMessage(state.Status), DactTheme.Palette.Muted, DactFonts.Small);
            switch (friendSection)
            {
                case 1: DrawRequestsSection(state); break;
                case 2: DrawAddSection(state); break;
                default: DrawFriendList(state); break;
            }
        }
        ImGui.EndChild();
        ImGui.PopStyleColor();
        ImGui.PopStyleVar(2);
        DactUi.Hint(notice);
    }

    private void DrawMe(FriendsChatSnapshot state)
    {
        var s = DactUi.S;
        var palette = DactTheme.Palette;
        var current = state.Friends?.PresenceSettings;
        var status = current?.Status ?? "offline";
        var user = state.Friends?.User;
        var name = user?.Username ?? uiText.Get("我的账号", "My account");
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var height = 38 * s;
        ImGui.InvisibleButton("##friends-me", new Vector2(width - 34 * s, height));
        var rowHovered = ImGui.IsItemHovered();
        var clicked = ImGui.IsItemClicked();
        var draw = ImGui.GetWindowDrawList();
        var avatarCenter = start + new Vector2(17 * s, height * .5f);
        draw.AddCircleFilled(avatarCenter, 17 * s, DactUi.U32(palette.Hover), 32);
        var initial = name.Length > 0 ? name[..1].ToUpperInvariant() : "?";
        DactUi.DrawText(draw, new Vector2(avatarCenter.X - DactUi.TextWidth(initial) * .5f, start.Y), height, initial, palette.Gold);
        var textX = start.X + 44 * s;
        AccountIdentityBadge.DrawName(administratorIcon, name, user?.IsAdmin == true, new Vector2(textX, start.Y + 1 * s),
            width - 44 * s - 34 * s, palette.Text, sponsorIcon, user?.SponsorTier ?? 0, uiText);
        var detail = current is null ? uiText.Get("正在读取状态…", "Loading status…")
            : StatusName(status) + (string.IsNullOrEmpty(current.Text) ? "" : " · " + current.Text);
        draw.AddCircleFilled(new Vector2(textX + 3 * s, start.Y + 28 * s), 3 * s, DactUi.U32(StatusColor(status)), 12);
        string line;
        using (DactFonts.Push(DactFonts.Small)) line = DactUi.Fit(detail, width - 44 * s - 34 * s - 12 * s);
        DactUi.DrawText(draw, new Vector2(textX + 12 * s, start.Y + 20 * s), 16 * s, line, palette.Muted, DactFonts.Small);
        ImGui.SameLine(0, 6 * s);
        ImGui.SetCursorScreenPos(new Vector2(start.X + width - 28 * s, start.Y + 5 * s));
        var edit = DactUi.IconButton("##friends-edit-status", "edit", uiText.Get("修改我的状态", "Edit my status"));
        if (clicked || edit)
        {
            editingSettings = null;
            settingsDirty = false;
            DactUi.OpenPopover(StatusPopover, new Vector2(start.X, start.Y + height + 6 * s));
        }
        if (rowHovered) DactUi.Tooltip(uiText.Get("修改我的状态", "Edit my status"));
        ImGui.SetCursorScreenPos(new Vector2(start.X, start.Y + height));
        ImGui.Dummy(Vector2.Zero);

        if (!DactUi.BeginPopover(StatusPopover, 292)) return;
        DrawStatusEditor(state);
        if (ImGui.IsKeyPressed(ImGuiKey.Escape)) ImGui.CloseCurrentPopup();
        DactUi.EndPopover();
    }

    // 修改状态 (Figma 好友 · 修改我的状态): four states, a short status line, and whether friends
    // see the current duty. The draft logic is the drawer's, which kept edits from other
    // devices and duty-sharing changes from overwriting each other.
    private void DrawStatusEditor(FriendsChatSnapshot state)
    {
        var s = DactUi.S;
        var palette = DactTheme.Palette;
        var current = state.Friends?.PresenceSettings;
        DactUi.Text(uiText.Get("修改状态", "Edit status"), palette.Gold);
        if (current is null)
        {
            DactUi.Hint(uiText.Get("正在读取我的状态…", "Loading my status…"));
            return;
        }
        if (submittedSettings is { } sent && !state.Busy)
        {
            if (current.Status == sent.Status && current.Text == sent.Text.Trim() && current.ShareDuty == sent.ShareDuty)
            {
                editingSettings = shareSubmission && settingsDirty && editingSettings is { } draft
                    ? draft with { Revision = current.Revision, ShareDuty = current.ShareDuty } : current;
                if (!shareSubmission) settingsDirty = false;
                privacySaveUnconfirmed = presenceChangedElsewhere = false;
            }
            else if (!sent.ShareDuty || sent.Status == "invisible") privacySaveUnconfirmed = true;
            submittedSettings = null;
        }
        if (editingSettings is null || !settingsDirty) editingSettings = current;
        if (settingsDirty && editingSettings.Revision != current.Revision)
        {
            // Keep the unsent text, but never let saving it silently restore an older
            // duty-sharing choice from another device.
            editingSettings = editingSettings with { Revision = current.Revision, ShareDuty = current.ShareDuty };
            presenceChangedElsewhere = true;
        }

        ImGui.BeginDisabled(state.Busy);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0, 2) * s);
        foreach (var value in StatusValues)
        {
            var start = ImGui.GetCursorScreenPos();
            var width = ImGui.GetContentRegionAvail().X;
            var selected = editingSettings.Status == value;
            if (ImGui.InvisibleButton("##status-" + value, new Vector2(width, 28 * s)))
            {
                editingSettings = editingSettings with { Status = value };
                settingsDirty = true;
            }
            var hovered = ImGui.IsItemHovered();
            var draw = ImGui.GetWindowDrawList();
            var fill = DactMotion.Color(ImGuiP.GetItemID(), palette.Hover with { W = selected ? 1 : hovered ? .5f : 0 }, DactMotion.Fast);
            if (fill.W > 0) draw.AddRectFilled(start, start + new Vector2(width, 28 * s), DactUi.U32(fill), 6 * s);
            draw.AddCircleFilled(start + new Vector2(14 * s, 14 * s), 3.5f * s, DactUi.U32(StatusColor(value)), 12);
            DactUi.DrawText(draw, start + new Vector2(26 * s, 0), 28 * s, StatusName(value), selected ? palette.Text : palette.Muted);
        }
        ImGui.PopStyleVar();

        var text = editingSettings.Text;
        if (DactUi.Input("##my-status-text", ref text, uiText.Get("加一句话，例如：今晚刷坐骑", "Add a status, e.g. mount farming tonight"), 320))
        {
            editingSettings = editingSettings with { Text = text };
            settingsDirty = true;
        }
        var length = text.EnumerateRunes().Count();
        if (length > 80) DactUi.Wrapped(uiText.Format($"状态文字最多 80 字（当前 {length} 字）", $"Status limit: 80 characters ({length} entered)"), palette.Gold, DactFonts.Small);
        else DactUi.Hint(uiText.Get("最多 80 字。", "Up to 80 characters."));
        var saveLabel = uiText.Get("保存状态", "Save status");
        var desired = editingSettings with { ShareDuty = current.ShareDuty };
        if (DactUi.Button(saveLabel + "##status-save", ButtonKind.Primary, disabled: !settingsDirty || length > 80) && controller.UpdatePresence(desired))
        {
            submittedSettings = desired;
            shareSubmission = false;
        }
        if (settingsDirty)
        {
            ImGui.SameLine(0, 8 * s);
            if (DactUi.Button(uiText.Get("还原", "Revert") + "##status-revert", ButtonKind.Ghost))
            {
                editingSettings = current;
                settingsDirty = presenceChangedElsewhere = false;
            }
        }
        var share = current.ShareDuty;
        if (DactUi.ToggleRow(uiText.Get("向好友显示当前副本", "Share current duty with friends"), ref share,
                uiText.Get("默认关闭。只分享当前副本名称；关闭或隐身会清除当前副本，多开副本不一致时不显示，不保留活动历史。",
                    "Off by default. Shares only your current duty name. Disabling this or going invisible clears it; conflicting duties across clients are hidden and no history is kept.")))
        {
            var next = current with { ShareDuty = share };
            if (controller.UpdatePresence(next))
            {
                submittedSettings = next;
                shareSubmission = true;
            }
        }
        ImGui.EndDisabled();
        if (presenceChangedElsewhere) DactUi.Wrapped(uiText.Get("其他设备已更新状态，请确认草稿后再保存。", "Another device updated your status. Review your draft before saving."), palette.Gold, DactFonts.Small);
        if (privacySaveUnconfirmed) DactUi.Wrapped(uiText.Get("关闭操作尚未确认；本机已暂停副本分享，请重试保存。", "The change is unconfirmed. Duty sharing is paused on this PC; please save again."), palette.Gold, DactFonts.Small);
        if (state.Busy && submittedSettings is not null) DactUi.Hint(uiText.Get("正在保存…", "Saving…"));
        else if (state.State == "error") DactUi.Wrapped(uiText.SystemMessage(state.Status), palette.Gold, DactFonts.Small);
        else if (!settingsDirty)
            DactUi.Hint(uiText.Format($"已保存：{StatusName(current.Status)}{(string.IsNullOrEmpty(current.Text) ? "" : " · " + current.Text)}",
                $"Saved: {StatusName(current.Status)}{(string.IsNullOrEmpty(current.Text) ? "" : " · " + current.Text)}"));
    }

    private void DrawFriendList(FriendsChatSnapshot state)
    {
        var palette = DactTheme.Palette;
        foreach (var official in state.Conversations.Values.Where(c => c.Chat.Kind == "official"))
        {
            var preview = FriendsMessagePreview.LastMessage(official.Chat, state.Friends?.User?.Id, uiText);
            if (DrawFriendItem("official-" + official.Chat.Id, official.Chat.Id == pageConversation, palette.Gold,
                    uiText.Get("DACT 官方通知", "DACT announcements"), null, preview.Length > 0 ? preview : uiText.Get("官方通知 · 只读", "Announcements · Read-only"),
                    null, official.UnreadCount, true))
                SelectConversation(official.Chat.Id);
        }
        if (state.Friends?.Friends.Count == 0)
            DactUi.Wrapped(uiText.Get("还没有好友，去“添加”找一个账号吧。", "No friends yet. Find an account on the Add tab."), palette.Muted, DactFonts.Small);
        foreach (var friend in (state.Friends?.Friends ?? []).OrderByDescending(f => f.Online).ThenBy(f => state.FriendDisplayName(f.User.Id, f.User.Username)))
        {
            ImGui.PushID(friend.Id);
            var visible = state.State == "ready" && friend.Online;
            var status = visible ? friend.Status == "offline" ? "online" : friend.Status : "offline";
            var statusText = StatusName(status) + (visible && !string.IsNullOrEmpty(friend.StatusText) ? " · " + friend.StatusText : "");
            var duty = visible ? friend.Duty?.Name : null;
            var view = friend.ConversationId is { } id ? state.Conversations.GetValueOrDefault(id) : null;
            var preview = view is null ? "" : FriendsMessagePreview.LastMessage(view.Chat, state.Friends?.User?.Id, uiText);
            // The second line shows the current duty when shared, otherwise the latest message.
            var second = duty is not null ? uiText.Get("正在进行：", "In duty: ") + duty : preview.Length > 0 ? preview : null;
            var name = state.FriendDisplayName(friend.User.Id, friend.User.Username);
            if (DrawFriendItem("friend", friend.ConversationId is { } selectedId && selectedId == pageConversation, StatusColor(status),
                    name, friend.User, statusText, second, view?.UnreadCount ?? 0, false, duty is not null) &&
                friend.ConversationId is { } chat)
                SelectConversation(chat);
            if (ImGui.IsItemHovered())
                DactUi.Tooltip(name + "\n" + statusText + (duty is null ? "" : uiText.Get("\n正在进行：", "\nIn duty: ") + duty) +
                                 uiText.Get("\n右键设置备注或管理好友", "\nRight-click to edit a note or manage this friend"));
            if (ImGui.BeginPopupContextItem("friend-options"))
            {
                if (ImGui.MenuItem(state.Remarks.ContainsKey(friend.User.Id) ? uiText.Get("修改备注…", "Edit note…") : uiText.Get("设置备注…", "Set note…")))
                    BeginRemark(friend, state, ImGui.GetMousePos());
                if (ImGui.MenuItem(uiText.Get("解除好友关系…", "Remove friend…")))
                {
                    removeId = friend.Id;
                    removeName = name;
                    // Opened by DrawRemoveDialog, in the page scope where the dialog is drawn.
                    openRemoveDialog = true;
                }
                ImGui.EndPopup();
            }
            ImGui.PopID();
        }
    }

    private void SelectConversation(string id)
    {
        pageConversation = id;
        if (!pageViews.TryGetValue(id, out var view)) pageViews[id] = view = new();
        view.ScrollToLatest = true;
    }

    // dsl friendItem: status dot, name (identity markers), a muted line and an optional second
    // line (accent for a shared duty), with the unread count on the right.
    private bool DrawFriendItem(string id, bool selected, Vector4 dot, string name, CloudApiUser? user, string line1, string? line2,
        int unread, bool official, bool accentSecond = false)
    {
        var s = DactUi.S;
        var palette = DactTheme.Palette;
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var height = (9 + 18 + 2 + 16 + (line2 is null ? 0 : 2 + 16) + 9) * s;
        var clicked = ImGui.InvisibleButton("##" + id, new Vector2(width, height));
        var hovered = ImGui.IsItemHovered();
        var draw = ImGui.GetWindowDrawList();
        var fill = DactMotion.Color(ImGuiP.GetItemID(), palette.Hover with { W = selected ? 1 : hovered ? .5f : 0 }, DactMotion.Fast);
        if (fill.W > 0) draw.AddRectFilled(start, start + new Vector2(width, height), DactUi.U32(fill), 8 * s);
        draw.AddCircleFilled(start + new Vector2(14 * s, 18 * s), 4 * s, DactUi.U32(dot), 12);
        var textX = start.X + 28 * s;
        var right = start.X + width - 10 * s - (unread > 0 ? 28 * s : 0);
        if (official) DactUi.DrawText(draw, new Vector2(textX, start.Y + 9 * s), 18 * s, DactUi.Fit(name, right - textX), palette.Gold);
        else AccountIdentityBadge.DrawName(administratorIcon, name, user?.IsAdmin == true, new Vector2(textX, start.Y + 10 * s), right - textX,
            palette.Text, sponsorIcon, user?.SponsorTier ?? 0, uiText);
        string Fit(string value) { using (DactFonts.Push(DactFonts.Small)) return DactUi.Fit(value, right - textX); }
        DactUi.DrawText(draw, new Vector2(textX, start.Y + 29 * s), 16 * s, Fit(line1), palette.Muted, DactFonts.Small);
        if (line2 is not null)
            DactUi.DrawText(draw, new Vector2(textX, start.Y + 47 * s), 16 * s, Fit(line2), accentSecond ? palette.Accent : palette.Muted, DactFonts.Small);
        if (unread > 0) DactUi.Badge(draw, new Vector2(start.X + width - 10 * s, start.Y + 18 * s), unread);
        return clicked;
    }

    private void DrawRequestsSection(FriendsChatSnapshot state)
    {
        var s = DactUi.S;
        var palette = DactTheme.Palette;
        if (state.Friends?.Requests.Count is null or 0)
        {
            DactUi.Wrapped(uiText.Get("暂时没有好友申请。", "No friend requests."), palette.Muted, DactFonts.Small);
            return;
        }
        foreach (var request in state.Friends.Requests)
        {
            ImGui.PushID(request.Id);
            var incoming = request.Direction == "incoming";
            var start = ImGui.GetCursorScreenPos();
            var width = ImGui.GetContentRegionAvail().X;
            var height = (9 + 18 + 2 + 16 + (incoming ? 8 + 30 : 0) + 9) * s;
            var draw = ImGui.GetWindowDrawList();
            if (incoming) draw.AddRectFilled(start, start + new Vector2(width, height), DactUi.U32(palette.Hover), 8 * s);
            AccountIdentityBadge.DrawName(administratorIcon, request.User.Username, request.User.IsAdmin, start + new Vector2(10 * s, 10 * s),
                width - 20 * s, palette.Text, sponsorIcon, request.User.SponsorTier, uiText);
            DactUi.DrawText(draw, start + new Vector2(10 * s, 29 * s), 16 * s,
                incoming ? uiText.Get("希望添加你", "Wants to add you") : uiText.Get("等待对方确认", "Awaiting acceptance"), palette.Muted, DactFonts.Small);
            if (incoming)
            {
                ImGui.SetCursorScreenPos(start + new Vector2(10 * s, 53 * s));
                if (DactUi.Button(uiText.Get("接受", "Accept") + "##accept", ButtonKind.Primary, disabled: state.Busy)) controller.Accept(request.Id);
                ImGui.SameLine(0, 8 * s);
                if (DactUi.Button(uiText.Get("拒绝", "Decline") + "##decline", ButtonKind.Ghost, disabled: state.Busy)) controller.Decline(request.Id);
            }
            ImGui.SetCursorScreenPos(start + new Vector2(0, height + 4 * s));
            ImGui.Dummy(Vector2.Zero);
            ImGui.PopID();
        }
    }

    private void DrawAddSection(FriendsChatSnapshot state)
    {
        var s = DactUi.S;
        var palette = DactTheme.Palette;
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8, 10) * s);
        DactUi.Hint(uiText.Get("通过完整的 DACT 账号名查找", "Search by the full DACT account name"));
        DactUi.Input("##friend-username", ref search, uiText.Get("输入完整账号名", "Enter the full account name"), 32);
        if (DactUi.Button(uiText.Get("查找账号", "Find account") + "##lookup", width: ImGui.GetContentRegionAvail().X,
                disabled: state.Busy || string.IsNullOrWhiteSpace(search)))
            controller.Lookup(search);
        if (state.Lookup?.User is { } found)
        {
            var start = ImGui.GetCursorScreenPos();
            var width = ImGui.GetContentRegionAvail().X;
            var height = (10 + 18 + 10 + 30 + 10) * s;
            ImGui.GetWindowDrawList().AddRectFilled(start, start + new Vector2(width, height), DactUi.U32(DactUi.Field), 8 * s);
            AccountIdentityBadge.DrawName(administratorIcon, found.Username, found.IsAdmin, start + new Vector2(10 * s, 10 * s), width - 20 * s,
                palette.Text, sponsorIcon, found.SponsorTier, uiText);
            ImGui.SetCursorScreenPos(start + new Vector2(10 * s, 38 * s));
            var relationship = state.Lookup.Relationship;
            var label = relationship switch
            {
                "incoming" => uiText.Get("同意互加", "Accept request"),
                "friend" => uiText.Get("已经是好友", "Already friends"),
                "outgoing" => uiText.Get("申请已发出", "Request sent"),
                _ => uiText.Get("发送好友申请", "Send friend request"),
            };
            if (DactUi.Button(label + "##request", ButtonKind.Primary, width: width - 20 * s,
                    disabled: state.Busy || relationship is "friend" or "outgoing"))
                controller.Request(found.Username);
            ImGui.SetCursorScreenPos(start + new Vector2(0, height));
            ImGui.Dummy(Vector2.Zero);
        }
        if (state.State == "ready" && !string.IsNullOrEmpty(state.Status)) DactUi.Hint(uiText.SystemMessage(state.Status));
        ImGui.PopStyleVar();
    }

    // ---- Right column

    private void DrawPageConversation(FriendsChatSnapshot state)
    {
        var palette = DactTheme.Palette;
        if (pageConversation is null || !state.Conversations.ContainsKey(pageConversation))
        {
            // First visit: the newest unread conversation, then announcements, then the first friend.
            pageConversation = state.Conversations.Values.Where(c => c.Unread).OrderByDescending(c => c.Chat.Kind != "official")
                                   .Select(c => c.Chat.Id).FirstOrDefault()
                               ?? state.Conversations.Values.FirstOrDefault(c => c.Chat.Kind == "official")?.Chat.Id
                               ?? state.Friends?.Friends.Select(f => f.ConversationId).FirstOrDefault(id => id is not null);
        }
        if (pageConversation is null || state.Conversations.GetValueOrDefault(pageConversation) is not { } view)
        {
            DactUi.Wrapped(uiText.Get("选择左侧的好友或官方通知开始聊天。", "Pick a friend or the announcements on the left to start chatting."), palette.Muted);
            return;
        }
        if (!pageViews.TryGetValue(pageConversation, out var ui)) pageViews[pageConversation] = ui = new();
        DrawConversation(pageConversation, view, ui, state, page: true);
    }

    // The conversation shared by the page and the floating window: header, messages, then the
    // pending-send box, quick messages and the input. The quick-message and input rows are
    // pinned to the bottom: whatever comes and goes above them (回到底部, 待确认, an error
    // line) takes its room from the message list, so sending never moves or dims the input.
    private void DrawConversation(string id, FriendConversationView view, ChatWindow ui, FriendsChatSnapshot state, bool page)
    {
        var s = DactUi.S;
        var palette = DactTheme.Palette;
        SettleSubmission(id, view, ui, state);
        var official = view.Chat.Kind == "official";
        var friend = state.Friends?.Friends.FirstOrDefault(f => f.ConversationId == id);
        var title = official ? uiText.Get("DACT 官方通知", "DACT announcements") : state.FriendDisplayName(view.Chat.Peer.Id, view.Chat.Peer.Username);
        DrawConversationHeader(id, title, official, view, friend, state, page);

        if (state.State != "ready") DactUi.Wrapped(uiText.SystemMessage(state.Status), palette.Muted, DactFonts.Small);
        var width = ImGui.GetContentRegionAvail().X;
        var spacing = ImGui.GetStyle().ItemSpacing.Y;
        var top = ImGui.GetCursorScreenPos().Y;
        var bottom = top + ImGui.GetContentRegionAvail().Y;
        // Progress and success show on the bubble itself (发送中… / · 已发送); only a status that
        // needs the player is written out, above the input.
        var sendStatus = IsQuietSendStatus(view.SendStatus) ? string.Empty : uiText.SystemMessage(view.SendStatus);
        // While the request is in flight the message shows as a 发送中 bubble; the yellow
        // 待确认 box is only for a send that failed or was interrupted (e.g. by a restart).
        var sending = state.Busy && view.PendingSend is not null;
        var unconfirmed = !sending && view.PendingSend is not null;
        var showLatest = !ui.AtBottom && view.UnreadCount > 0;
        var statusHeight = 0f;
        if (sendStatus.Length > 0)
            using (DactFonts.Push(DactFonts.Small)) statusHeight = DactUi.WrapLines(sendStatus, width).Count * 16 * s + spacing;
        var composer = official ? 30 * s : ui.ComposerHeight > 0 ? ui.ComposerHeight : 72 * s;
        var above = (showLatest ? 30 * s + spacing : 0) + (unconfirmed ? 80 * s + spacing : 0) + statusHeight;
        var historyHeight = Math.Max(90 * s, bottom - top - composer - above - spacing);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Vector4.Zero);
        if (ImGui.BeginChild("messages", new Vector2(-1, historyHeight), false))
            DrawMessages(id, view, ui, state, page);
        ImGui.EndChild();
        ImGui.PopStyleColor();

        if (showLatest && DactUi.Button(uiText.Get("有新消息 · 回到底部", "New messages · Jump to latest") + "##latest", ButtonKind.Ghost, width: width))
            ui.ScrollToLatest = true;
        if (official)
        {
            DactUi.Hint(uiText.Get("这里接收 DACT 官方通知，不支持回复。", "DACT announcements appear here. Replies are disabled."));
            return;
        }
        if (unconfirmed && view.PendingSend is { } pending) DrawPendingSend(id, pending, state);
        if (sendStatus.Length > 0) DactUi.Wrapped(sendStatus, palette.Muted, DactFonts.Small);

        var composerTop = Math.Max(ImGui.GetCursorScreenPos().Y, bottom - composer);
        ImGui.SetCursorScreenPos(new Vector2(ImGui.GetCursorScreenPos().X, composerTop));
        var quickLabel = uiText.Get("快捷消息", "Quick messages");
        var invite = uiText.Get("下把邀我", "Invite me next time");
        var finish = uiText.Get("你什么时候结束", "When will you finish?");
        // Only a send waiting on the player (待确认) holds the composer back. One in flight
        // leaves it as it is; another Enter meanwhile keeps its text until the first is done.
        var blocked = unconfirmed;
        // dsl row 快捷消息: the label sits centred on the 30 px buttons beside it.
        var row = ImGui.GetCursorScreenPos();
        if (page)
        {
            DactUi.DrawText(ImGui.GetWindowDrawList(), row, 30 * s, quickLabel, DactUi.HintColor, DactFonts.Small);
            ImGui.SetCursorScreenPos(row + new Vector2(DactUi.TextWidth(quickLabel, DactFonts.Small) + 10 * s, 0));
        }
        if (DactUi.Button(invite + "##quick-invite", disabled: blocked)) Submit(id, ui, "", CloudChatPolicy.InviteNext);
        DactUi.SameLineIfFits(DactUi.ButtonWidth(finish));
        if (DactUi.Button(finish + "##quick-finish", disabled: blocked)) Submit(id, ui, "", CloudChatPolicy.WhenFinished);

        var sendLabel = uiText.Get("发送", "Send");
        var sendWidth = DactUi.ButtonWidth(sendLabel, ButtonKind.Primary, "send");
        if (ui.RefocusDraft)
        {
            ImGui.SetKeyboardFocusHere();
            ui.RefocusDraft = false;
        }
        // Enter as the field reports it (only while typing): ImGui's own Enter state can be stuck
        // down after a send (see DactUi.Input), and polling it re-focused the field in a loop.
        var enter = DactUi.Input("##chat-draft", ref ui.Draft, uiText.Get("输入消息，按回车发送", "Type a message; press Enter to send"), 4000,
            width: ImGui.GetContentRegionAvail().X - sendWidth - 8 * s, submitOnEnter: true);
        ImGui.SameLine(0, 8 * s);
        var send = DactUi.Button(sendLabel + "##send", ButtonKind.Primary, "send", disabled: blocked);
        if ((enter || send) && !blocked && !string.IsNullOrWhiteSpace(ui.Draft)) Submit(id, ui, ui.Draft, null, fromDraft: true);
        // Enter ends ImGui's editing and a click on 发送 takes the focus; either way the field
        // keeps the keyboard so the next message can be typed straight away.
        if (enter || send) ui.RefocusDraft = true;
        ui.ComposerHeight = ImGui.GetItemRectMax().Y - composerTop;
    }

    // The editor empties and the bubble shows the moment a message is submitted.
    private bool Submit(string id, ChatWindow ui, string text, string? quick, bool fromDraft = false)
    {
        if (controller.Send(id, text, quick) is not { } operation) return false;
        ui.SubmittedOperation = operation;
        ui.SubmittedText = text;
        ui.SubmittedQuick = quick;
        ui.SubmittedFromDraft = fromDraft;
        if (fromDraft) ui.Draft = "";
        ui.ScrollToLatest = true;
        return true;
    }

    // A submission is settled once its operation reaches the durable outbox (the outbox then
    // owns the text). A command can still fail before that (offline, account switched); its
    // text then goes back into the editor instead of being lost.
    private void SettleSubmission(string id, FriendConversationView view, ChatWindow ui, FriendsChatSnapshot state)
    {
        if (ui.SubmittedOperation is not { } submitted) return;
        if (view.LastPreparedOperation != submitted)
        {
            if (state.Busy) return;
            // The snapshot is read before Busy; read it again so an outbox entry published just
            // before the command finished is not taken for a failure.
            if (controller.Snapshot.Conversations.GetValueOrDefault(id)?.LastPreparedOperation != submitted &&
                ui.SubmittedFromDraft && !string.IsNullOrEmpty(ui.SubmittedText))
                ui.Draft = ui.Draft.Length == 0 ? ui.SubmittedText : ui.SubmittedText + " " + ui.Draft;
        }
        ui.SubmittedOperation = null;
        ui.SubmittedText = ui.SubmittedQuick = null;
        ui.SubmittedFromDraft = false;
    }

    private void DrawConversationHeader(string id, string title, bool official, FriendConversationView view, CloudFriendRelation? friend,
        FriendsChatSnapshot state, bool page)
    {
        var s = DactUi.S;
        var palette = DactTheme.Palette;
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var buttons = official ? 0 : page ? 3 : 2;
        var buttonsWidth = buttons * 28 * s + Math.Max(0, buttons - 1) * 6 * s;
        var textWidth = width - (buttons > 0 ? buttonsWidth + 10 * s : 0);
        var visible = friend is not null && state.State == "ready" && friend.Online;
        var status = friend is null ? null : visible ? friend.Status == "offline" ? "online" : friend.Status : "offline";
        var detail = official ? uiText.Get("官方通知 · 只读", "Announcements · Read-only")
            : status is null ? string.Empty
            : StatusName(status) + (visible && !string.IsNullOrEmpty(friend!.StatusText) ? " · " + friend.StatusText : "") +
              (visible && friend!.Duty?.Name is { } duty ? uiText.Get(" · 正在进行：", " · In duty: ") + duty : "");
        if (!page && windows.TryGetValue(id, out var dragged))
        {
            ImGui.InvisibleButton("##chat-header-drag", new Vector2(Math.Max(1, textWidth), 40 * s));
            dragged.Drag.HandleItem();
            ImGui.SetCursorScreenPos(start);
        }
        using (DactFonts.Push(DactFonts.H2))
        {
            if (official) DactUi.DrawText(ImGui.GetWindowDrawList(), start, 20 * s, DactUi.Fit(title, textWidth), palette.Gold, DactFonts.H2);
            else AccountIdentityBadge.DrawName(administratorIcon, title, view.Chat.Peer.IsAdmin, start + new Vector2(0, 1 * s), textWidth,
                palette.Text, sponsorIcon, view.Chat.Peer.SponsorTier, uiText);
        }
        var lines = DactUi.WrapLines(detail, textWidth);
        float detailHeight;
        using (DactFonts.Push(DactFonts.Small)) detailHeight = lines.Count == 0 ? 0 : ImGui.CalcTextSize(string.Join("\n", lines)).Y;
        ImGui.SetCursorScreenPos(start + new Vector2(0, 22 * s));
        if (detail.Length > 0) DactUi.Wrapped(detail, DactUi.HintColor, DactFonts.Small, textWidth);
        var headerHeight = Math.Max(30 * s, 22 * s + detailHeight);

        if (buttons > 0)
        {
            ImGui.SetCursorScreenPos(new Vector2(start.X + width - buttonsWidth, start.Y + (Math.Min(headerHeight, 44 * s) - 28 * s) * .5f));
            if (friend is not null)
            {
                var remarkLabel = state.Remarks.ContainsKey(friend.User.Id) ? uiText.Get("修改备注", "Edit note") : uiText.Get("设置备注", "Set note");
                if (DactUi.IconButton("##chat-remark", "edit", remarkLabel, palette.Hover))
                    BeginRemark(friend, state, new Vector2(ImGui.GetItemRectMax().X, ImGui.GetItemRectMax().Y + 6 * s), alignRight: true);
                ImGui.SameLine(0, 6 * s);
            }
            if (page)
            {
                if (DactUi.IconButton("##chat-popout", "external", uiText.Get("弹出为独立窗口", "Pop out into its own window"), palette.Hover)) PopOut(id);
                ImGui.SameLine(0, 6 * s);
                if (DactUi.IconButton("##chat-more", "more", uiText.Get("更多", "More"), palette.Hover)) ImGui.OpenPopup(MoreMenu);
                if (DactUi.BeginMenuPopup(MoreMenu, 180 * s))
                {
                    if (friend is not null && DactUi.MenuRow(uiText.Get("解除好友关系…", "Remove friend…"), danger: true))
                    {
                        removeId = friend.Id;
                        removeName = title;
                        ImGui.CloseCurrentPopup();
                        openRemoveDialog = true;
                    }
                    ImGui.EndPopup();
                }
            }
            else
            {
                if (DactUi.IconButton("##chat-dock", "close", uiText.Get("关闭聊天", "Close chat"), palette.Hover) && windows.TryGetValue(id, out var floating))
                {
                    floating.Open = false;
                    floating.Drag.Cancel();
                }
            }
        }
        if (remarkRelationId is not null) DrawRemarkPopover(state);
        ImGui.SetCursorScreenPos(start + new Vector2(0, headerHeight + 10 * s));
        DactUi.Divider();
    }

    private bool openRemoveDialog;

    private void PopOut(string id)
    {
        if (!windows.TryGetValue(id, out var window)) windows[id] = window = new();
        window.Open = true;
        window.Focus = true;
        window.ScrollToLatest = true;
    }

    private void DrawMessages(string id, FriendConversationView view, ChatWindow ui, FriendsChatSnapshot state, bool page)
    {
        var s = DactUi.S;
        var history = ImGuiP.GetCurrentWindow();
        var scroll = ImGui.GetScrollY();
        var bottomBeforeDraw = scroll >= ImGui.GetScrollMaxY() - 4;
        var wheel = ImGui.IsWindowHovered() && ImGui.GetIO().MouseWheel != 0;
        var scrollbar = ImGui.GetCurrentContext().ActiveId == ImGuiP.GetWindowScrollbarID(history, ImGuiAxis.Y);
        // User scrolling takes precedence over a pending follow. A shorter retained history
        // can clamp Y too, without user input.
        if (wheel || scrollbar || !ui.ScrollToLatest && scroll < ui.LastScroll - 1 && ImGui.GetScrollMaxY() >= ui.LastScroll)
        {
            ui.ScrollToLatest = false;
            ui.AtBottom = bottomBeforeDraw;
        }
        var me = state.Friends?.User?.Id;
        var messages = view.Chat.History.Concat(view.Chat.Pending).OrderBy(m => m.Id).ToArray();
        if (messages.Any(m => m.Id > ui.LastMessage && m.Sender.UserId == me)) ui.ScrollToLatest = true;
        // Reply options belong to the latest incoming quick question that has no answer yet.
        var lastIncoming = messages.LastOrDefault(m => m.Sender.UserId != me);
        var unanswered = lastIncoming is not null && !messages.Any(m => m.Id > lastIncoming.Id && m.Sender.UserId == me) ? lastIncoming : null;
        var outgoing = InFlightMessage(id, view, ui, state, me, messages);
        // Only what arrives while the conversation is on screen slides in. Opening it, the
        // first sync and a history that loads in one go show settled.
        var now = ImGui.GetTime();
        var frame = ImGui.GetFrameCount();
        var settled = frame - ui.LastDrawFrame > 2 || !state.InitialSyncComplete || ui.Appeared.Count == 0 && messages.Length > 1;
        ui.LastDrawFrame = frame;
        if (settled || ui.Appeared.Count > 64) ui.Appeared.Clear();
        float Appear(CloudChatMessage message)
        {
            var key = message.OperationId != Guid.Empty ? "op:" + message.OperationId : "id:" + message.Id;
            if (!ui.Appeared.TryGetValue(key, out var at)) ui.Appeared[key] = at = settled ? double.NegativeInfinity : now;
            return double.IsNegativeInfinity(at) ? 1 : DactMotion.Progress(at, MessageSeconds);
        }
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8, 10) * s);
        // · 已发送 marks my latest message while nothing has been said after it.
        var sentMarker = outgoing is null && messages.LastOrDefault() is { } latest && latest.Sender.UserId == me && latest.State != "pending" ? latest : null;
        foreach (var message in messages)
        {
            var own = message.Sender.UserId == me;
            var replies = !own && message == unanswered && view.PendingSend is null && outgoing is null && !state.Busy
                ? FriendsIncomingNotifications.Replies(message, uiText) : [];
            var reply = DrawMessage(message, own, message.Sender.IsOfficial ? null : state.FriendDisplayName(message.Sender.UserId, message.Sender.Name), replies,
                message == sentMarker ? uiText.Get("已发送", "Sent") : null, Appear(message));
            if (reply is not null) Submit(id, ui, reply, null);
        }
        if (outgoing is not null) DrawMessage(outgoing, true, null, [], uiText.Get("发送中…", "Sending…"), Appear(outgoing));
        ImGui.PopStyleVar();
        if (messages.Length == 0) DactUi.Wrapped(uiText.Get("暂无消息。", "No messages yet."), DactTheme.Palette.Muted, DactFonts.Small);
        var last = messages.LastOrDefault()?.Id ?? 0;
        // Scroll targets and content extents settle on later frames. Keep following through
        // wrapping and resize changes, and mark read only once the tail is actually visible.
        var tailVisible = ImGui.GetItemRectMax().Y <= history.InnerRect.Max.Y - history.WindowPadding.Y + 4;
        if (ui.ScrollToLatest || ui.AtBottom)
        {
            ui.ScrollToLatest = !tailVisible;
            if (ui.ScrollToLatest) ImGui.SetScrollHereY(1);
            ui.AtBottom = true;
        }
        else ui.AtBottom = bottomBeforeDraw && tailVisible;
        ui.LastMessage = last;
        ui.LastScroll = scroll;
        var focused = page ? controlCentreFocused : ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
        if (focused && ui.AtBottom && tailVisible)
        {
            var incoming = messages.Where(m => m.RecipientId == me).Select(m => m.Id).DefaultIfEmpty().Max();
            controller.MarkRead(id, incoming);
        }
    }

    private const float MessageSeconds = .3f;

    // My message while its send is in flight: what was just submitted, then the outbox entry
    // once prepared. It shares the operation with the confirmed message that replaces it, so the
    // bubble stays put and only its 发送中… becomes 已发送.
    private static CloudChatMessage? InFlightMessage(string id, FriendConversationView view, ChatWindow ui, FriendsChatSnapshot state, string? me,
        CloudChatMessage[] messages)
    {
        if (!state.Busy) return null;
        var request = view.PendingSend ?? (ui.SubmittedOperation is { } operation
            ? new CloudChatSendRequest(0, operation, ui.SubmittedQuick is null ? ui.SubmittedText : null, ui.SubmittedQuick)
            : null);
        if (request is null || messages.Any(m => m.OperationId == request.OperationId)) return null;
        return new CloudChatMessage(long.MaxValue, id, new CloudChatSender("user", me, string.Empty), string.Empty, request.Sequence,
            request.OperationId, request.Text ?? string.Empty, request.QuickMessageId, "sending", DateTimeOffset.Now, null);
    }

    // Moves and fades everything drawn since firstVertex: fill, text, identity badges, chips.
    private static void MoveAndFade(ImDrawListPtr draw, int firstVertex, Vector2 offset, float alpha)
    {
        for (var i = firstVertex; i < draw.VtxBuffer.Size; i++)
        {
            var vertex = draw.VtxBuffer[i];
            vertex.Pos += offset;
            vertex.Col = (vertex.Col & 0x00FFFFFF) | (uint)((vertex.Col >> 24) * alpha) << 24;
            draw.VtxBuffer[i] = vertex;
        }
    }

    // dsl bubble: 300 px wide, mine on the right in the primary tint, theirs in Hover with the
    // sender's name and identity markers; a quick question carries its reply chips.
    // Returns the reply the user clicked, if any. appear (0..1) is the arrival: the row opens up
    // while the bubble fades in, mine rising from the input and theirs sliding in from the left.
    private string? DrawMessage(CloudChatMessage message, bool own, string? displayName, IReadOnlyList<string> replies, string? status = null,
        float appear = 1)
    {
        var s = DactUi.S;
        var palette = DactTheme.Palette;
        var available = ImGui.GetContentRegionAvail().X;
        var width = Math.Min(available, Math.Max(160 * s, Math.Min(300 * s, available * .8f)));
        var textWidth = width - 24 * s;
        var official = message.Sender.IsOfficial;
        var author = official ? uiText.Get("DACT 官方", "DACT team") : displayName ?? message.Sender.Name;
        var body = uiText.MessageBody(message);
        var time = $"{message.CreatedAt.ToLocalTime():MM-dd HH:mm}{(message.State == "pending" ? uiText.Get(" · 待上线接收", " · Waiting for recipient") : status is null ? "" : " · " + status)}";
        var bodyLines = DactUi.WrapLines(body, textWidth);
        var bodyHeight = Math.Max(1, bodyLines.Count) * 18 * s;
        var authorHeight = own ? 0 : 16 * s + 4 * s;
        var replyHeight = replies.Count > 0 ? 4 * s + 30 * s : 0;
        var height = 8 * s + authorHeight + bodyHeight + 4 * s + 14 * s + replyHeight + 8 * s;
        // The gap above an arriving row opens with it (not for the first row of the list).
        if (appear < 1 && ImGui.GetCursorPosY() > ImGui.GetCursorStartPos().Y + 1)
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() - (1 - appear) * ImGui.GetStyle().ItemSpacing.Y);
        var start = ImGui.GetCursorScreenPos() + new Vector2(own ? available - width : 0, 0);
        var draw = ImGui.GetWindowDrawList();
        var firstVertex = draw.VtxBuffer.Size;
        draw.AddRectFilled(start, start + new Vector2(width, height), DactUi.U32(own ? DactUi.Primary : palette.Hover), 10 * s);
        if (official) draw.AddRect(start, start + new Vector2(width, height), DactUi.U32(palette.Gold with { W = .6f }), 10 * s, ImDrawFlags.None, Math.Max(1, s));
        var y = start.Y + 8 * s;
        if (!own)
        {
            using (DactFonts.Push(DactFonts.Small))
            {
                if (official) DactUi.DrawText(draw, new Vector2(start.X + 12 * s, y), 16 * s, author, palette.Gold, DactFonts.Small);
                else AccountIdentityBadge.DrawName(administratorIcon, author, message.Sender.IsAdmin, new Vector2(start.X + 12 * s, y), textWidth,
                    palette.Accent, sponsorIcon, message.Sender.SponsorTier, uiText);
            }
            y += authorHeight;
        }
        foreach (var line in bodyLines)
        {
            DactUi.DrawText(draw, new Vector2(start.X + 12 * s, y), 18 * s, line, palette.Text);
            y += 18 * s;
        }
        if (bodyLines.Count == 0) y += 18 * s;
        y += 4 * s;
        DactUi.DrawText(draw, new Vector2(start.X + 12 * s, y), 14 * s, time, palette.Muted, DactFonts.Tiny);
        y += 14 * s;
        string? clicked = null;
        if (replies.Count > 0)
        {
            var x = start.X + 12 * s;
            y += 4 * s;
            foreach (var reply in replies)
            {
                var chipWidth = DactUi.TextWidth(reply) + 28 * s;
                ImGui.SetCursorScreenPos(new Vector2(x, y));
                if (ImGui.InvisibleButton("##reply-" + message.Id + reply, new Vector2(chipWidth, 30 * s))) clicked = reply;
                var hovered = ImGui.IsItemHovered();
                draw.AddRectFilled(new Vector2(x, y), new Vector2(x + chipWidth, y + 30 * s), DactUi.U32(hovered ? palette.Hover : palette.Raised), 6 * s);
                draw.AddRect(new Vector2(x, y), new Vector2(x + chipWidth, y + 30 * s), DactUi.U32(palette.Accent with { W = .45f }), 6 * s, ImDrawFlags.None, Math.Max(1, s));
                DactUi.DrawText(draw, new Vector2(x + 14 * s, y), 30 * s, reply, palette.Accent);
                x += chipWidth + 6 * s;
            }
        }
        if (appear < 1)
        {
            var rest = 1 - appear;
            MoveAndFade(draw, firstVertex, own ? new Vector2(0, rest * 18 * s) : new Vector2(-rest * 18 * s, rest * 6 * s), appear);
        }
        ImGui.SetCursorScreenPos(new Vector2(ImGui.GetWindowPos().X + ImGui.GetCursorStartPos().X, start.Y));
        ImGui.Dummy(new Vector2(available, height * appear));
        return clicked;
    }

    // Statuses the bubbles already show (发送中… / · 已发送) are not written under the chat.
    private static bool IsQuietSendStatus(string status)
        => status is "" or "正在保存并发送…" or "已发送" or "已发送，待对方上线接收" or "发送已确认。";

    // 发送待确认 (Figma): the unconfirmed text in a gold box with retry and discard; discarding
    // asks first in a centred dialog because the original may already have arrived.
    private void DrawPendingSend(string id, CloudChatSendRequest pending, FriendsChatSnapshot state)
    {
        var s = DactUi.S;
        var palette = DactTheme.Palette;
        var preview = pending.Text ?? (pending.QuickMessageId == CloudChatPolicy.InviteNext ? uiText.Get("下把邀我", "Invite me next time") : uiText.Get("你什么时候结束", "When will you finish?"));
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var height = 80 * s;
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(start, start + new Vector2(width, height), DactUi.U32(DactUi.GoldSoft), 8 * s);
        draw.AddRect(start, start + new Vector2(width, height), DactUi.U32(palette.Gold with { W = .55f }), 8 * s, ImDrawFlags.None, Math.Max(1, s));
        var line = uiText.Format($"待确认：{preview}", $"Unconfirmed: {preview}");
        DactUi.DrawText(draw, start + new Vector2(12 * s, 8 * s), 18 * s, DactUi.Fit(line, width - 24 * s), palette.Gold);
        if (ImGui.IsMouseHoveringRect(start, start + new Vector2(width, 30 * s)) && preview.Length > 30) DactUi.Tooltip(preview);
        ImGui.SetCursorScreenPos(start + new Vector2(12 * s, 38 * s));
        if (DactUi.Button(uiText.Get("重试原消息", "Retry original message") + "##retry", disabled: state.Busy)) controller.Retry(id);
        ImGui.SameLine(0, 8 * s);
        if (DactUi.Button(uiText.Get("放弃此发送…", "Discard this send…") + "##discard", ButtonKind.Ghost, disabled: state.Busy))
        {
            discardId = id;
            DactUi.OpenDialog(DiscardDialog);
        }
        ImGui.SetCursorScreenPos(start + new Vector2(0, height));
        ImGui.Dummy(Vector2.Zero);
        if (DactUi.BeginDialog(DiscardDialog))
        {
            DactUi.Text(uiText.Get("放弃此发送", "Discard this send"), size: DactFonts.H2);
            DactUi.Wrapped(uiText.Get("原消息可能已送达。放弃后请先查看会话记录，再决定是否重新输入发送。",
                "The original message may have arrived. After discarding, check the conversation before composing another message."), palette.Muted);
            if (DialogButtons(uiText.Get("确认放弃", "Confirm discard"), "discard") is { } confirm)
            {
                if (confirm && discardId is { } target) controller.Discard(target);
                DactUi.CloseDialog(DiscardDialog);
            }
            DactUi.EndDialog(DiscardDialog);
        }
    }

    private string? discardId;

    // Dialog footer: 取消 then the danger action, right-aligned. Returns true for the action,
    // false for cancel, null when nothing was clicked.
    private bool? DialogButtons(string action, string id)
    {
        var s = DactUi.S;
        var cancel = uiText.Get("取消", "Cancel");
        var total = DactUi.ButtonWidth(cancel) + 8 * s + DactUi.ButtonWidth(action, ButtonKind.Danger);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, ImGui.GetContentRegionAvail().X - total));
        bool? result = null;
        if (DactUi.Button(cancel + "##cancel-" + id)) result = false;
        ImGui.SameLine(0, 8 * s);
        if (DactUi.Button(action + "##confirm-" + id, ButtonKind.Danger)) result = true;
        return result;
    }

    private void DrawRemoveDialog(FriendsChatSnapshot state)
    {
        if (openRemoveDialog)
        {
            DactUi.OpenDialog(RemoveDialog);
            openRemoveDialog = false;
        }
        if (!DactUi.BeginDialog(RemoveDialog)) return;
        DactUi.Text(uiText.Get("解除好友关系", "Remove friend"), size: DactFonts.H2);
        DactUi.Wrapped(uiText.Format($"解除与 {removeName} 的好友关系后，双方会话和保留消息将被删除。",
            $"Removing {removeName} will delete the conversation and retained messages for both people."), DactTheme.Palette.Muted);
        if (DialogButtons(uiText.Get("确认解除", "Confirm removal"), "remove") is { } confirm)
        {
            if (confirm && removeId is { } target && !state.Busy) controller.Remove(target);
            removeId = null;
            DactUi.CloseDialog(RemoveDialog);
        }
        DactUi.EndDialog(RemoveDialog);
    }

    // ---- 好友备注 (anchored under the conversation's edit button)

    private void BeginRemark(CloudFriendRelation friend, FriendsChatSnapshot state, Vector2 anchor, bool alignRight = false)
    {
        remarkRelationId = friend.Id;
        remarkDraft = state.Remarks.GetValueOrDefault(friend.User.Id, "");
        remarkError = "";
        submittedRemark = null;
        remarkRevision = friend.Remark?.Revision ?? 0;
        openRemarkEditor = true;
        DactUi.OpenPopover(RemarkPopover, anchor, alignRight);
    }

    private void DrawRemarkPopover(FriendsChatSnapshot state)
    {
        var palette = DactTheme.Palette;
        var focus = openRemarkEditor;
        openRemarkEditor = false;
        if (!DactUi.BeginPopover(RemarkPopover, 340))
        {
            if (!focus) remarkRelationId = null;
            return;
        }
        var friend = state.Friends?.Friends.FirstOrDefault(f => f.Id == remarkRelationId);
        if (friend is null)
        {
            remarkRelationId = null;
            ImGui.CloseCurrentPopup();
            DactUi.EndPopover();
            return;
        }
        if (submittedRemark is { } operation)
        {
            if (state.LastSavedRemark == operation)
            {
                submittedRemark = null;
                remarkRelationId = null;
                ImGui.CloseCurrentPopup();
                DactUi.EndPopover();
                return;
            }
            if (!state.Busy)
            {
                remarkError = state.RemarkStatus;
                submittedRemark = null;
            }
        }
        DactUi.Text(uiText.Get("好友备注", "Friend note"), palette.Gold);
        DactUi.Hint(uiText.Get("账号：", "Account: ") + friend.User.Username);
        if (friend.Remark is null) DactUi.Hint(uiText.Get("服务器尚未支持备注云同步。", "This server does not yet support synced friend notes."));
        ImGui.BeginDisabled(state.Busy || friend.Remark is null);
        if (focus) ImGui.SetKeyboardFocusHere();
        var enter = DactUi.Input("##friend-remark", ref remarkDraft, uiText.Get("留空可清除备注", "Leave empty to clear the note"), 256,
            submitOnEnter: true);
        var normalized = remarkDraft.Trim();
        var valid = FriendRemark.IsValid(normalized);
        DactUi.Hint(uiText.Format($"{normalized.EnumerateRunes().Count()}/{FriendRemark.MaximumLength} 字 · 不能包含换行或控制字符；留空可清除备注。",
            $"{normalized.EnumerateRunes().Count()}/{FriendRemark.MaximumLength} characters · No line breaks or control characters; leave empty to clear."));
        if (!valid) DactUi.Wrapped(uiText.Get("备注最多 40 字，不能包含换行或控制字符。", "Use up to 40 characters, without line breaks or control characters."), palette.Gold, DactFonts.Small);
        if (remarkError.Length > 0) DactUi.Wrapped(uiText.SystemMessage(remarkError), palette.Gold, DactFonts.Small);
        var s = DactUi.S;
        if ((DactUi.Button(uiText.Get("保存备注", "Save note") + "##remark-save", ButtonKind.Primary, disabled: !valid) || enter) && valid) Submit(normalized);
        ImGui.SameLine(0, 8 * s);
        ImGui.EndDisabled();
        if (DactUi.Button(uiText.Get("取消", "Cancel") + "##remark-cancel") || ImGui.IsKeyPressed(ImGuiKey.Escape))
        {
            remarkRelationId = null;
            submittedRemark = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine(0, 8 * s);
        ImGui.BeginDisabled(state.Busy || friend.Remark is null);
        if (DactUi.Button(uiText.Get("清除备注", "Clear note") + "##remark-clear", ButtonKind.Ghost)) Submit("");
        ImGui.EndDisabled();
        DactUi.EndPopover();

        void Submit(string value)
        {
            // Bind the edit to the snapshot account. A logout between drawing and clicking must
            // never write a different account's private remark.
            submittedRemark = controller.SetRemark(friend.Id, value, state.Session, remarkRevision);
            remarkError = submittedRemark is null ? "正在处理其他操作，请稍后再试。" : "";
        }
    }
}

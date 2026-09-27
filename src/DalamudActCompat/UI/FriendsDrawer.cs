using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using DalamudActCompat.Infrastructure.Cloud;

namespace DalamudActCompat.UI;

internal sealed partial class FriendsUiManager
{
    private static readonly string[] StatusValues = ["online", "away", "busy", "invisible"];
    private string StatusName(string value) => value switch { "away" => uiText.Get("离开", "Away"), "busy" => uiText.Get("忙碌", "Busy"), "invisible" => uiText.Get("隐身", "Invisible"), "online" => uiText.Get("在线", "Online"), _ => uiText.Get("离线", "Offline") };
    private static Vector4 StatusColor(string value) => value switch
    {
        "online" => new(.38f, .82f, .60f, 1), "away" => new(.88f, .73f, .38f, 1),
        "busy" => new(.94f, .45f, .46f, 1), _ => new(.5f, .57f, .64f, 1),
    };

    private void DrawDrawer(FriendsChatSnapshot state)
    {
        var viewport = ImGui.GetMainViewport();
        var scale = Math.Max(.75f, ImGui.GetFontSize() / 17f);
        var layout = FriendsWindowLayout.Drawer(anchor, anchorSize, viewport.WorkPos, viewport.WorkSize, scale);
        var progress = 1 - MathF.Pow(1 - drawerProgress, 3);
        var outside = layout.Position.X >= anchor.X + anchorSize.X - 2;
        var visibleWidth = Math.Max(1, layout.Size.X * progress);
        var position = layout.Position + new Vector2(outside ? 0 : layout.Size.X - visibleWidth, 0);
        ImGui.SetNextWindowPos(position, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new(visibleWidth, layout.Size.Y), ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, Vector2.One);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0);
        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * anchorAlpha);
        var flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoBackground;
        if (drawerProgress < 1) flags |= ImGuiWindowFlags.NoInputs;
        if (ImGui.Begin("##DACTFriendsDrawer", flags))
        {
            PlaceDrawerAboveOwner();
            var list = ImGui.GetWindowDrawList();
            var end = position + new Vector2(visibleWidth, layout.Size.Y);
            var corners = outside ? ImDrawFlags.RoundCornersRight : ImDrawFlags.RoundCornersLeft;
            var gameFrame = DactTheme.DrawGameWindow();
            // The game sprite has transparent corners and a lower shadow margin.
            // A second filled rectangle makes the drawer visibly taller than its owner.
            if (!gameFrame)
            {
                list.AddRectFilled(position, end, ImGui.GetColorU32(Navy), 10, corners);
                list.AddRect(position, end, ImGui.GetColorU32(new Vector4(.34f, .29f, .18f, .85f)), 10, corners);
            }
            DrawDrawerCollapse(position, visibleWidth, layout.Size.Y, scale, outside);
            // Slide the complete content behind the owner's right divider. The
            // outer window is only its visible clip, not a growing form layout.
            var contentPosition = outside ? layout.Position - new Vector2(layout.Size.X - visibleWidth, 0) : position;
            ImGui.SetCursorScreenPos(contentPosition + new Vector2(14 * scale, 12 * scale));
            // Child backgrounds also move behind the seam, so constrain their
            // inherited clip by one pixel to keep the owner's divider visible.
            ImGui.PushClipRect(position + new Vector2(1, 0), end - new Vector2(1, 0), true);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(2 * scale, 0));
            // Let the textured owner supply the paper and bottom rim instead of
            // covering its last pixels with a solid child background.
            var contentFlags = gameFrame ? ImGuiWindowFlags.NoBackground : ImGuiWindowFlags.None;
            if (ImGui.BeginChild("drawer-content", new(layout.Size.X - 42 * scale, layout.Size.Y - (gameFrame ? 28 : 24) * scale), false, contentFlags))
            {
                var start = ImGui.GetCursorScreenPos();
                FriendsGlyph.Draw(ImGui.GetWindowDrawList(), start, 23 * scale, ImGui.GetColorU32(Blue));
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 31 * scale);
                DactTheme.TextColored(Blue, uiText.Get("好友", "Friends")); ImGui.SameLine();
                ImGui.TextDisabled(uiText.Get($"{(state.State == "ready" ? state.Friends?.OnlineCount ?? 0 : 0)} 人在线", $"{(state.State == "ready" ? state.Friends?.OnlineCount ?? 0 : 0)} online"));
                ImGui.Spacing(); ImGui.Separator(); ImGui.Spacing();
                DrawOwnPresenceMenu(state, scale);
                ImGui.Spacing(); ImGui.Separator(); ImGui.Spacing();
                if (state.State != "ready") ImGui.TextWrapped(uiText.SystemMessage(state.Status));
                friendSection = BrandedWindowChrome.DrawNavigationRail("friend-sections", [uiText.Get("好友", "Friends"), uiText.Get("申请", "Requests"), uiText.Get("添加", "Add")], friendSection, 30 * scale,
                    notificationIndex: state.HasIncomingRequests ? 1 : -1);
                // A one-column table gives every section the same content box,
                // including full-width inputs/selectables; indentation alone leaves
                // their right edge flush against the navigation container.
                var contentPadding = Math.Max(3, 3 * scale);
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + contentPadding);
                ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(contentPadding));
                if (ImGui.BeginTable("friend-section-content", 1, ImGuiTableFlags.NoSavedSettings))
                {
                    ImGui.TableNextRow(); ImGui.TableSetColumnIndex(0);
                    ImGui.PushTextWrapPos(0);
                    switch (friendSection)
                    {
                        case 0: DrawFriendRows(state, scale); break;
                        case 1: DrawFriendRequests(state); break;
                        case 2: DrawAddFriend(state); break;
                    }
                    ImGui.PopTextWrapPos(); ImGui.EndTable();
                }
                ImGui.PopStyleVar();
                DrawRemoveConfirmation(state);
                // The drawer intentionally has no vertical padding; editing popups
                // need their own inset so text and controls clear the themed frame.
                ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(16 * scale));
                ImGui.PushStyleVar(ImGuiStyleVar.PopupBorderSize, 1);
                DactTheme.PushStyleColor(ImGuiCol.Border, Gold);
                DrawRemarkEditor(state, scale);
                ImGui.PopStyleColor(); ImGui.PopStyleVar(2);
                ImGui.Spacing(); ImGui.Separator();
                ImGui.TextDisabled(uiText.Get("消息使用须知", "Messaging guidelines")); ImGui.TextWrapped(uiText.Get(CloudChatPolicy.Notice, "Do not use messaging for money laundering, fake transactions, fraud, or other prohibited activities. Administrators may review suspected violations."));
            }
            ImGui.EndChild(); ImGui.PopStyleVar(); ImGui.PopClipRect();
            // Preserve the main window's original right edge throughout both
            // animation directions instead of painting the shared seam away.
            if (!gameFrame)
            {
                var seam = outside ? position.X : end.X - 1;
                list.AddRectFilled(new(seam, position.Y), new(seam + 1, end.Y), ImGui.GetColorU32(ImGuiCol.Border));
            }
        }
        ImGui.End(); ImGui.PopStyleVar(4);
    }

    private void DrawDrawerCollapse(Vector2 position, float width, float height, float scale, bool outside)
    {
        var size = new Vector2(24, 52) * scale;
        var start = position + new Vector2(width - size.X, Math.Max(0, (height - size.Y) / 2));
        ImGui.SetCursorScreenPos(start);
        if (ImGui.InvisibleButton("collapse-friends", size)) drawerOpen = false;
        var hovered = ImGui.IsItemHovered();
        var list = ImGui.GetWindowDrawList();
        list.AddRectFilled(start, start + size, ImGui.GetColorU32(DactTheme.Tone(hovered ? new Vector4(.11f, .25f, .32f, .9f) : new Vector4(.07f, .12f, .16f, .85f), hovered ? DactTheme.Palette.Hover : DactTheme.Palette.Raised)), 6 * scale);
        var center = start + size / 2;
        var direction = outside ? 1 : -1;
        list.AddLine(center + new Vector2(2 * direction, -5) * scale, center + new Vector2(-3 * direction, 0) * scale, ImGui.GetColorU32(Blue), 1.6f * scale);
        list.AddLine(center + new Vector2(-3 * direction, 0) * scale, center + new Vector2(2 * direction, 5) * scale, ImGui.GetColorU32(Blue), 1.6f * scale);
        if (hovered) ImGui.SetTooltip(uiText.Get("收起好友列表", "Collapse friends"));
    }

    private void DrawOwnPresenceMenu(FriendsChatSnapshot state, float scale)
    {
        var current = state.Friends?.PresenceSettings;
        var status = current?.Status ?? "offline";
        var start = ImGui.GetCursorScreenPos(); var width = ImGui.GetContentRegionAvail().X;
        if (ImGui.InvisibleButton("my-presence-menu", new(width, 53 * scale))) ImGui.OpenPopup("my-presence-settings");
        var hovered = ImGui.IsItemHovered(); var list = ImGui.GetWindowDrawList();
        if (hovered) list.AddRectFilled(start - new Vector2(3 * scale), start + new Vector2(width, 51 * scale), ImGui.GetColorU32(DactTheme.Tone(new Vector4(.08f, .15f, .20f, 1), DactTheme.Palette.Hover)), 6);
        list.AddCircleFilled(start + new Vector2(7, 13) * scale, 4.5f * scale, ImGui.GetColorU32(StatusColor(status)));
        list.PushClipRect(start + new Vector2(21 * scale, 0), start + new Vector2(width, 53 * scale), true);
        AccountIdentityBadge.DrawName(administratorIcon, state.Friends?.User?.Username ?? uiText.Get("我的账号", "My account"),
            state.Friends?.User?.IsAdmin == true, start + new Vector2(22, 2) * scale, width - 26 * scale, DactTheme.Palette.Text,
            sponsorIcon, state.Friends?.User?.SponsorTier ?? 0, text: uiText);
        var detail = current is null ? uiText.Get("正在读取状态…", "Loading status…") : StatusName(status) + (string.IsNullOrEmpty(current.Text) ? "" : " · " + current.Text);
        list.AddText(start + new Vector2(22, 26) * scale, ImGui.GetColorU32(DactTheme.Tone(new Vector4(.60f, .68f, .75f, 1), DactTheme.Palette.Muted)), detail);
        list.PopClipRect();
        if (hovered) ImGui.SetTooltip(uiText.Get("修改我的状态", "Edit my status"));
        // Keep the list compact. Draft text and privacy controls appear only
        // while explicitly editing this account's presence, not in the default list.
        var maximum = ImGui.GetMainViewport().WorkSize - new Vector2(16);
        var popupWidth = Math.Min(292 * scale, maximum.X);
        // Fix only width. Let ImGui fit the actual content height rather than
        // repeatedly forcing a zero-height resize; retain scrolling at real overflow.
        ImGui.SetNextWindowSizeConstraints(new(popupWidth, 0), new(popupWidth, maximum.Y));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(16 * scale));
        ImGui.PushStyleVar(ImGuiStyleVar.PopupBorderSize, 1);
        DactTheme.PushStyleColor(ImGuiCol.Border, Gold);
        DactTheme.PreparePopupPosition("my-presence-settings");
        if (ImGui.BeginPopup("my-presence-settings", ImGuiWindowFlags.AlwaysAutoResize |
            (DactTheme.Palette.Rain ? ImGuiWindowFlags.NoBackground : ImGuiWindowFlags.None)))
        {
            DactTheme.DrawGamePopupFrame();
            DrawPresenceEditor(state, scale);
            // Dalamud can leave keyboard navigation disabled while the game owns
            // movement input; an explicitly opened status popup still dismisses on Esc.
            if (ImGui.IsKeyPressed(ImGuiKey.Escape)) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
        ImGui.PopStyleColor(); ImGui.PopStyleVar(2);
    }

    private void PlaceDrawerAboveOwner()
    {
        var drawer = ImGuiP.GetCurrentWindow();
        var ordered = ImGui.GetCurrentContext().Windows;
        for (var i = 0; i < ordered.Size; i++)
        {
            if (ordered[i].ID != anchorWindowId) continue;
            // Keep the drawer just above its owner, not above independent chat
            // windows or other plugins that the user subsequently brought forward.
            for (var next = i + 1; next < ordered.Size; next++)
            {
                if (ordered[next].RootWindow.ID == anchorWindowId || ordered[next].RootWindow.ID == drawer.ID) continue;
                ImGuiP.BringWindowToDisplayBehind(drawer, ordered[next]); return;
            }
            ImGuiP.BringWindowToDisplayFront(drawer); return;
        }
    }

    private void DrawPresenceEditor(FriendsChatSnapshot state, float scale)
    {
        var current = state.Friends?.PresenceSettings;
        if (current is null) { ImGui.TextDisabled(uiText.Get("正在读取我的状态…", "Loading my status…")); return; }
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
            // Preserve the unsent text, but never let saving it silently restore an
            // older duty-sharing choice from another device or the independent toggle.
            editingSettings = editingSettings with { Revision = current.Revision, ShareDuty = current.ShareDuty };
            presenceChangedElsewhere = true;
        }
        ImGui.TextUnformatted(uiText.Get("修改状态", "Edit status"));
        ImGui.BeginDisabled(state.Busy);
        for (var index = 0; index < StatusValues.Length; index++)
        {
            var start = ImGui.GetCursorScreenPos();
            if (ImGui.Selectable("    " + StatusName(StatusValues[index]) + "##status-" + index, editingSettings.Status == StatusValues[index], ImGuiSelectableFlags.DontClosePopups, new(0, 25 * scale)))
            { editingSettings = editingSettings with { Status = StatusValues[index] }; settingsDirty = true; }
            ImGui.GetWindowDrawList().AddCircleFilled(start + new Vector2(7, 10) * scale, 4 * scale, ImGui.GetColorU32(StatusColor(StatusValues[index])));
        }
        ImGui.Spacing();
        var text = editingSettings.Text;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##my-status-text", uiText.Get("加一句话，例如：今晚刷坐骑", "Add a status, e.g. mount farming tonight"), ref text, 320))
        { editingSettings = editingSettings with { Text = text }; settingsDirty = true; }
        var textLength = text.EnumerateRunes().Count();
        if (textLength > 80) DactTheme.TextColored(Gold, uiText.Get($"状态文字最多 80 字（当前 {textLength} 字）", $"Status limit: 80 characters ({textLength} entered)"));
        ImGui.BeginDisabled(!settingsDirty || textLength > 80);
        var desired = editingSettings with { ShareDuty = current.ShareDuty };
        if (DactTheme.Button(uiText.Get("保存状态", "Save status")) && controller.UpdatePresence(desired))
        { submittedSettings = desired; shareSubmission = false; }
        ImGui.EndDisabled();
        if (settingsDirty)
        {
            ImGui.SameLine(); if (DactTheme.SmallButton(uiText.Get("还原", "Revert"))) { editingSettings = current; settingsDirty = presenceChangedElsewhere = false; }
        }
        var share = current.ShareDuty;
        if (DactTheme.Checkbox(uiText.Get("向好友显示当前副本", "Share current duty with friends"), ref share))
        {
            var next = current with { ShareDuty = share };
            if (controller.UpdatePresence(next)) { submittedSettings = next; shareSubmission = true; }
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(uiText.Get("默认关闭。仅分享实际副本名称；关闭或隐身会清除当前副本。多开副本不一致时不显示，不保留活动历史。", "Off by default. Shares only your current duty name. Disabling this or going invisible clears it. Conflicting duties across clients are hidden; no activity history is kept."));
        ImGui.EndDisabled();
        if (presenceChangedElsewhere) ImGui.TextWrapped(uiText.Get("其他设备已更新状态，请确认草稿后再保存。", "Another device updated your status. Review your draft before saving."));
        if (privacySaveUnconfirmed) ImGui.TextWrapped(uiText.Get("关闭操作尚未确认；本机已暂停副本分享，请重试保存。", "The change is unconfirmed. Duty sharing is paused on this PC; please save again."));
        if (state.Busy && submittedSettings is not null) ImGui.TextDisabled(uiText.Get("正在保存…", "Saving…"));
        else if (state.State == "error") ImGui.TextWrapped(uiText.SystemMessage(state.Status));
        else if (!settingsDirty)
        {
            DactTheme.PushStyleColor(ImGuiCol.Text, new Vector4(.60f, .68f, .75f, 1));
            ImGui.TextWrapped(uiText.Get($"已保存：{StatusName(current.Status)}{(string.IsNullOrEmpty(current.Text) ? "" : " · " + current.Text)}", $"Saved: {StatusName(current.Status)}{(string.IsNullOrEmpty(current.Text) ? "" : " · " + current.Text)}"));
            ImGui.PopStyleColor();
        }
    }

    private void DrawFriendRows(FriendsChatSnapshot state, float scale)
    {
        foreach (var official in state.Conversations.Values.Where(c => c.Chat.Kind == "official"))
        {
            var start = ImGui.GetCursorScreenPos(); var width = ImGui.GetContentRegionAvail().X;
            var message = FriendsMessagePreview.LastMessage(official.Chat, state.Friends?.User?.Id, uiText);
            var hasMessage = message.Length > 0;
            var textRight = width - UnreadBadgeWidth(official.UnreadCount, scale);
            if (ImGui.Selectable("##official-" + official.Chat.Id, false, ImGuiSelectableFlags.None, new(width, (hasMessage ? 74 : 52) * scale))) OpenChat(official.Chat.Id);
            var list = ImGui.GetWindowDrawList();
            list.AddCircleFilled(start + new Vector2(6, 12) * scale, 3.5f * scale, ImGui.GetColorU32(Gold));
            list.AddText(start + new Vector2(19, 1) * scale, ImGui.GetColorU32(Gold), uiText.Get("DACT 官方通知", "DACT announcements"));
            var preview = FriendsMessagePreview.Ellipsize(message, Math.Max(0, textRight - 29 * scale), s => ImGui.CalcTextSize(s).X);
            if (hasMessage) list.AddText(start + new Vector2(19, 23) * scale, ImGui.GetColorU32(DactTheme.Tone(new Vector4(.75f, .80f, .85f, 1), DactTheme.Palette.Muted)), preview);
            list.AddText(start + new Vector2(19, hasMessage ? 45 : 23) * scale, ImGui.GetColorU32(DactTheme.Tone(new Vector4(.57f, .65f, .73f, 1), DactTheme.Palette.Muted)), uiText.Get("官方通知 · 只读", "Announcements · Read-only"));
            DrawUnreadBadge(start, width, official.UnreadCount, scale);
        }
        if (state.Friends?.Friends.Count == 0) ImGui.TextDisabled(uiText.Get("还没有好友，去“添加”找一个账号吧。", "No friends yet. Find an account on the Add tab."));
        foreach (var friend in (state.Friends?.Friends ?? []).OrderByDescending(f => f.Online).ThenBy(f => state.FriendDisplayName(f.User.Id, f.User.Username)))
        {
            ImGui.PushID(friend.Id);
            var visible = state.State == "ready" && friend.Online;
            var status = visible ? friend.Status == "offline" ? "online" : friend.Status : "offline";
            var statusText = StatusName(status) + (visible && !string.IsNullOrEmpty(friend.StatusText) ? " · " + friend.StatusText : "");
            var duty = visible ? friend.Duty?.Name : null;
            var view = friend.ConversationId is { } id ? state.Conversations.GetValueOrDefault(id) : null;
            var preview = view is null ? "" : FriendsMessagePreview.LastMessage(view.Chat, state.Friends?.User?.Id, uiText);
            var hasMessage = preview.Length > 0;
            var unreadCount = view?.UnreadCount ?? 0;
            // Empty or not-yet-synced chats keep the friend visible, with no
            // synthetic preview text or blank message row.
            var statusY = hasMessage ? 45 : 23;
            var rowHeight = (statusY + (duty is null ? 29 : 51)) * scale;
            var start = ImGui.GetCursorScreenPos(); var width = ImGui.GetContentRegionAvail().X;
            var textRight = width - UnreadBadgeWidth(unreadCount, scale);
            if (ImGui.Selectable("##friend-row", false, ImGuiSelectableFlags.None, new(width, rowHeight)) && friend.ConversationId is { } chat) OpenChat(chat);
            var hovered = ImGui.IsItemHovered();
            if (ImGui.BeginPopupContextItem("friend-options"))
            {
                if (ImGui.MenuItem(state.Remarks.ContainsKey(friend.User.Id) ? uiText.Get("修改备注…", "Edit note…") : uiText.Get("设置备注…", "Set note…")))
                {
                    remarkRelationId = friend.Id; remarkDraft = state.Remarks.GetValueOrDefault(friend.User.Id, "");
                    remarkError = ""; submittedRemark = null; openRemarkEditor = true;
                    remarkRevision = friend.Remark?.Revision ?? 0;
                }
                if (ImGui.MenuItem(uiText.Get("解除好友关系…", "Remove friend…"))) { removeId = friend.Id; removeName = state.FriendDisplayName(friend.User.Id, friend.User.Username); }
                ImGui.EndPopup();
            }
            var list = ImGui.GetWindowDrawList();
            string Fit(string value) => FriendsMessagePreview.Ellipsize(value, Math.Max(0, textRight - 29 * scale), s => ImGui.CalcTextSize(s).X);
            list.AddCircleFilled(start + new Vector2(6, 12) * scale, 3.5f * scale, ImGui.GetColorU32(StatusColor(status)));
            list.PushClipRect(start + new Vector2(18 * scale, 0), start + new Vector2(textRight - 10 * scale, rowHeight), true);
            AccountIdentityBadge.DrawName(administratorIcon, state.FriendDisplayName(friend.User.Id, friend.User.Username), friend.User.IsAdmin,
                start + new Vector2(19, 1) * scale, Math.Max(1, textRight - 29 * scale), DactTheme.Palette.Text,
                sponsorIcon, friend.User.SponsorTier, text: uiText);
            if (hasMessage) list.AddText(start + new Vector2(19, 23) * scale, ImGui.GetColorU32(DactTheme.Tone(new Vector4(.75f, .80f, .85f, 1), DactTheme.Palette.Muted)), Fit(preview));
            list.AddText(start + new Vector2(19, statusY) * scale, ImGui.GetColorU32(DactTheme.Tone(new Vector4(.57f, .65f, .73f, 1), DactTheme.Palette.Muted)), Fit(statusText));
            if (duty is not null) list.AddText(start + new Vector2(19, statusY + 22) * scale, ImGui.GetColorU32(Blue), Fit(uiText.Get("正在进行：", "In duty: ") + duty));
            list.PopClipRect();
            DrawUnreadBadge(start, width, unreadCount, scale);
            if (hovered) ImGui.SetTooltip(state.FriendDisplayName(friend.User.Id, friend.User.Username) + "\n" + statusText + (duty is null ? "" : uiText.Get("\n正在进行：", "\nIn duty: ") + duty) + uiText.Get("\n右键设置备注或管理好友", "\nRight-click to edit a note or manage this friend"));
            ImGui.PopID();
        }
    }

    private static float UnreadBadgeWidth(int count, float scale) => count <= 0 ? 0 : ImGui.CalcTextSize(count.ToString()).X + 16 * scale;

    private static void DrawUnreadBadge(Vector2 start, float width, int count, float scale)
    {
        if (count <= 0) return;
        var value = count.ToString(); var textSize = ImGui.CalcTextSize(value); var padding = Math.Max(3, 3 * scale);
        var size = textSize + new Vector2(padding * 2);
        var origin = start + new Vector2(width - size.X - 3 * scale, 0);
        var list = ImGui.GetWindowDrawList();
        list.AddRectFilled(origin, origin + size, ImGui.GetColorU32(new Vector4(.72f, .22f, .25f, 1)), size.Y / 2);
        list.AddText(origin + new Vector2(padding), ImGui.GetColorU32(Vector4.One), value);
    }

    private void DrawAddFriend(FriendsChatSnapshot state)
    {
        ImGui.TextDisabled(uiText.Get("通过完整的 DACT 账号名查找", "Search by the full DACT account name"));
        ImGui.SetNextItemWidth(-1); ImGui.InputTextWithHint("##friend-username", uiText.Get("输入完整账号名", "Enter the full account name"), ref search, 32);
        ImGui.BeginDisabled(state.Busy || string.IsNullOrWhiteSpace(search));
        if (DactTheme.Button(uiText.Get("查找账号", "Find account"), new(-1, 0))) controller.Lookup(search);
        ImGui.EndDisabled();
        if (state.Lookup?.User is { } found)
        {
            AccountIdentityBadge.Text(administratorIcon, found.Username, found.IsAdmin, DactTheme.Palette.Text, sponsorIcon, found.SponsorTier, text: uiText);
            ImGui.BeginDisabled(state.Busy || state.Lookup.Relationship is "friend" or "outgoing");
            if (DactTheme.Button(state.Lookup.Relationship == "incoming" ? uiText.Get("同意互加", "Accept request") : state.Lookup.Relationship == "friend" ? uiText.Get("已经是好友", "Already friends") : state.Lookup.Relationship == "outgoing" ? uiText.Get("申请已发出", "Request sent") : uiText.Get("发送好友申请", "Send friend request"))) controller.Request(found.Username);
            ImGui.EndDisabled();
        }
        if (state.State == "ready") ImGui.TextWrapped(uiText.SystemMessage(state.Status));
    }

    private void DrawFriendRequests(FriendsChatSnapshot state)
    {
        if (state.Friends?.Requests.Count == 0) ImGui.TextDisabled(uiText.Get("暂时没有好友申请。", "No friend requests."));
        foreach (var request in state.Friends?.Requests ?? [])
        {
            ImGui.PushID(request.Id);
            AccountIdentityBadge.Text(administratorIcon, request.User.Username, request.User.IsAdmin, DactTheme.Palette.Text, sponsorIcon, request.User.SponsorTier, text: uiText);
            ImGui.TextDisabled(request.Direction == "incoming" ? uiText.Get("希望添加你", "Wants to add you") : uiText.Get("等待对方确认", "Awaiting acceptance"));
            if (request.Direction == "incoming")
            {
                ImGui.BeginDisabled(state.Busy);
                if (DactTheme.SmallButton(uiText.Get("接受", "Accept"))) controller.Accept(request.Id);
                ImGui.SameLine(); if (DactTheme.SmallButton(uiText.Get("拒绝", "Decline"))) controller.Decline(request.Id);
                ImGui.EndDisabled();
            }
            ImGui.PopID();
        }
    }

    private void DrawRemoveConfirmation(FriendsChatSnapshot state)
    {
        if (removeId is not null) ImGui.OpenPopup("remove-friend-confirmation");
        if (!ImGui.BeginPopup("remove-friend-confirmation")) return;
        ImGui.TextWrapped(uiText.Get($"解除与 {removeName} 的好友关系后，双方会话和保留消息将被删除。", $"Removing {removeName} will delete the conversation and retained messages for both people."));
        ImGui.BeginDisabled(state.Busy);
        if (DactTheme.Button(uiText.Get("确认解除", "Confirm removal"))) { controller.Remove(removeId!); removeId = null; ImGui.CloseCurrentPopup(); }
        ImGui.EndDisabled(); ImGui.SameLine();
        if (DactTheme.Button(uiText.Get("取消", "Cancel"))) { removeId = null; ImGui.CloseCurrentPopup(); }
        ImGui.EndPopup();
    }
}

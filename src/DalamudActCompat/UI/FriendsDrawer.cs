using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using DalamudActCompat.Infrastructure.Cloud;

namespace DalamudActCompat.UI;

internal sealed partial class FriendsUiManager
{
    private static readonly string[] StatusLabels = ["在线", "离开", "忙碌", "隐身"];
    private static readonly string[] StatusValues = ["online", "away", "busy", "invisible"];
    private static string StatusName(string value) => value switch { "away" => "离开", "busy" => "忙碌", "invisible" => "隐身", "online" => "在线", _ => "离线" };
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
                DactTheme.TextColored(Blue, "好友"); ImGui.SameLine();
                ImGui.TextDisabled($"{(state.State == "ready" ? state.Friends?.OnlineCount ?? 0 : 0)} 人在线");
                ImGui.Spacing(); ImGui.Separator(); ImGui.Spacing();
                DrawOwnPresenceMenu(state, scale);
                ImGui.Spacing(); ImGui.Separator(); ImGui.Spacing();
                if (state.State != "ready") ImGui.TextWrapped(state.Status);
                friendSection = BrandedWindowChrome.DrawNavigationRail("friend-sections", ["好友", "申请", "添加"], friendSection, 30 * scale,
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
                ImGui.TextDisabled("消息使用须知"); ImGui.TextWrapped(CloudChatPolicy.Notice);
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
        if (hovered) ImGui.SetTooltip("收起好友列表");
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
        AccountIdentityBadge.DrawName(administratorIcon, state.Friends?.User?.Username ?? "我的账号",
            state.Friends?.User?.IsAdmin == true, start + new Vector2(22, 2) * scale, width - 26 * scale, DactTheme.Palette.Text,
            sponsorIcon, state.Friends?.User?.SponsorTier ?? 0);
        var detail = current is null ? "正在读取状态…" : StatusName(status) + (string.IsNullOrEmpty(current.Text) ? "" : " · " + current.Text);
        list.AddText(start + new Vector2(22, 26) * scale, ImGui.GetColorU32(DactTheme.Tone(new Vector4(.60f, .68f, .75f, 1), DactTheme.Palette.Muted)), detail);
        list.PopClipRect();
        if (hovered) ImGui.SetTooltip("修改我的状态");
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
        if (ImGui.BeginPopup("my-presence-settings", ImGuiWindowFlags.AlwaysAutoResize))
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
        if (current is null) { ImGui.TextDisabled("正在读取我的状态…"); return; }
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
        ImGui.TextUnformatted("修改状态");
        ImGui.BeginDisabled(state.Busy);
        for (var index = 0; index < StatusValues.Length; index++)
        {
            var start = ImGui.GetCursorScreenPos();
            if (ImGui.Selectable("    " + StatusLabels[index] + "##status-" + index, editingSettings.Status == StatusValues[index], ImGuiSelectableFlags.DontClosePopups, new(0, 25 * scale)))
            { editingSettings = editingSettings with { Status = StatusValues[index] }; settingsDirty = true; }
            ImGui.GetWindowDrawList().AddCircleFilled(start + new Vector2(7, 10) * scale, 4 * scale, ImGui.GetColorU32(StatusColor(StatusValues[index])));
        }
        ImGui.Spacing();
        var text = editingSettings.Text;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##my-status-text", "加一句话，例如：今晚刷坐骑", ref text, 320))
        { editingSettings = editingSettings with { Text = text }; settingsDirty = true; }
        var textLength = text.EnumerateRunes().Count();
        if (textLength > 80) DactTheme.TextColored(Gold, $"状态文字最多 80 字（当前 {textLength} 字）");
        ImGui.BeginDisabled(!settingsDirty || textLength > 80);
        var desired = editingSettings with { ShareDuty = current.ShareDuty };
        if (DactTheme.Button("保存状态") && controller.UpdatePresence(desired))
        { submittedSettings = desired; shareSubmission = false; }
        ImGui.EndDisabled();
        if (settingsDirty)
        {
            ImGui.SameLine(); if (DactTheme.SmallButton("还原")) { editingSettings = current; settingsDirty = presenceChangedElsewhere = false; }
        }
        var share = current.ShareDuty;
        if (DactTheme.Checkbox("向好友显示当前副本", ref share))
        {
            var next = current with { ShareDuty = share };
            if (controller.UpdatePresence(next)) { submittedSettings = next; shareSubmission = true; }
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("默认关闭。仅分享实际副本名称；关闭或隐身会清除当前副本。多开副本不一致时不显示，不保留活动历史。");
        ImGui.EndDisabled();
        if (presenceChangedElsewhere) ImGui.TextWrapped("其他设备已更新状态，请确认草稿后再保存。");
        if (privacySaveUnconfirmed) ImGui.TextWrapped("关闭操作尚未确认；本机已暂停副本分享，请重试保存。");
        if (state.Busy && submittedSettings is not null) ImGui.TextDisabled("正在保存…");
        else if (state.State == "error") ImGui.TextWrapped(state.Status);
        else if (!settingsDirty)
        {
            DactTheme.PushStyleColor(ImGuiCol.Text, new Vector4(.60f, .68f, .75f, 1));
            ImGui.TextWrapped($"已保存：{StatusName(current.Status)}{(string.IsNullOrEmpty(current.Text) ? "" : " · " + current.Text)}");
            ImGui.PopStyleColor();
        }
    }

    private void DrawFriendRows(FriendsChatSnapshot state, float scale)
    {
        foreach (var official in state.Conversations.Values.Where(c => c.Chat.Kind == "official"))
        {
            var start = ImGui.GetCursorScreenPos(); var width = ImGui.GetContentRegionAvail().X;
            var message = FriendsMessagePreview.LastMessage(official.Chat, state.Friends?.User?.Id);
            var hasMessage = message.Length > 0;
            var textRight = width - UnreadBadgeWidth(official.UnreadCount, scale);
            if (ImGui.Selectable("##official-" + official.Chat.Id, false, ImGuiSelectableFlags.None, new(width, (hasMessage ? 74 : 52) * scale))) OpenChat(official.Chat.Id);
            var list = ImGui.GetWindowDrawList();
            list.AddCircleFilled(start + new Vector2(6, 12) * scale, 3.5f * scale, ImGui.GetColorU32(Gold));
            list.AddText(start + new Vector2(19, 1) * scale, ImGui.GetColorU32(Gold), "DACT 官方通知");
            var preview = FriendsMessagePreview.Ellipsize(message, Math.Max(0, textRight - 29 * scale), s => ImGui.CalcTextSize(s).X);
            if (hasMessage) list.AddText(start + new Vector2(19, 23) * scale, ImGui.GetColorU32(DactTheme.Tone(new Vector4(.75f, .80f, .85f, 1), DactTheme.Palette.Muted)), preview);
            list.AddText(start + new Vector2(19, hasMessage ? 45 : 23) * scale, ImGui.GetColorU32(DactTheme.Tone(new Vector4(.57f, .65f, .73f, 1), DactTheme.Palette.Muted)), "官方通知 · 只读");
            DrawUnreadBadge(start, width, official.UnreadCount, scale);
        }
        if (state.Friends?.Friends.Count == 0) ImGui.TextDisabled("还没有好友，去“添加”找一个账号吧。");
        foreach (var friend in (state.Friends?.Friends ?? []).OrderByDescending(f => f.Online).ThenBy(f => state.FriendDisplayName(f.User.Id, f.User.Username)))
        {
            ImGui.PushID(friend.Id);
            var visible = state.State == "ready" && friend.Online;
            var status = visible ? friend.Status == "offline" ? "online" : friend.Status : "offline";
            var statusText = StatusName(status) + (visible && !string.IsNullOrEmpty(friend.StatusText) ? " · " + friend.StatusText : "");
            var duty = visible ? friend.Duty?.Name : null;
            var view = friend.ConversationId is { } id ? state.Conversations.GetValueOrDefault(id) : null;
            var preview = view is null ? "" : FriendsMessagePreview.LastMessage(view.Chat, state.Friends?.User?.Id);
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
                if (ImGui.MenuItem(state.Remarks.ContainsKey(friend.User.Id) ? "修改备注…" : "设置备注…"))
                {
                    remarkRelationId = friend.Id; remarkDraft = state.Remarks.GetValueOrDefault(friend.User.Id, "");
                    remarkError = ""; submittedRemark = null; openRemarkEditor = true;
                    remarkRevision = friend.Remark?.Revision ?? 0;
                }
                if (ImGui.MenuItem("解除好友关系…")) { removeId = friend.Id; removeName = state.FriendDisplayName(friend.User.Id, friend.User.Username); }
                ImGui.EndPopup();
            }
            var list = ImGui.GetWindowDrawList();
            string Fit(string value) => FriendsMessagePreview.Ellipsize(value, Math.Max(0, textRight - 29 * scale), s => ImGui.CalcTextSize(s).X);
            list.AddCircleFilled(start + new Vector2(6, 12) * scale, 3.5f * scale, ImGui.GetColorU32(StatusColor(status)));
            list.PushClipRect(start + new Vector2(18 * scale, 0), start + new Vector2(textRight - 10 * scale, rowHeight), true);
            AccountIdentityBadge.DrawName(administratorIcon, state.FriendDisplayName(friend.User.Id, friend.User.Username), friend.User.IsAdmin,
                start + new Vector2(19, 1) * scale, Math.Max(1, textRight - 29 * scale), DactTheme.Palette.Text,
                sponsorIcon, friend.User.SponsorTier);
            if (hasMessage) list.AddText(start + new Vector2(19, 23) * scale, ImGui.GetColorU32(DactTheme.Tone(new Vector4(.75f, .80f, .85f, 1), DactTheme.Palette.Muted)), Fit(preview));
            list.AddText(start + new Vector2(19, statusY) * scale, ImGui.GetColorU32(DactTheme.Tone(new Vector4(.57f, .65f, .73f, 1), DactTheme.Palette.Muted)), Fit(statusText));
            if (duty is not null) list.AddText(start + new Vector2(19, statusY + 22) * scale, ImGui.GetColorU32(Blue), Fit("正在进行：" + duty));
            list.PopClipRect();
            DrawUnreadBadge(start, width, unreadCount, scale);
            if (hovered) ImGui.SetTooltip(state.FriendDisplayName(friend.User.Id, friend.User.Username) + "\n" + statusText + (duty is null ? "" : "\n正在进行：" + duty) + "\n右键设置备注或管理好友");
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
        ImGui.TextDisabled("通过完整的 DACT 账号名查找");
        ImGui.SetNextItemWidth(-1); ImGui.InputTextWithHint("##friend-username", "输入完整账号名", ref search, 32);
        ImGui.BeginDisabled(state.Busy || string.IsNullOrWhiteSpace(search));
        if (DactTheme.Button("查找账号", new(-1, 0))) controller.Lookup(search);
        ImGui.EndDisabled();
        if (state.Lookup?.User is { } found)
        {
            AccountIdentityBadge.Text(administratorIcon, found.Username, found.IsAdmin, DactTheme.Palette.Text, sponsorIcon, found.SponsorTier);
            ImGui.BeginDisabled(state.Busy || state.Lookup.Relationship is "friend" or "outgoing");
            if (DactTheme.Button(state.Lookup.Relationship == "incoming" ? "同意互加" : state.Lookup.Relationship == "friend" ? "已经是好友" : state.Lookup.Relationship == "outgoing" ? "申请已发出" : "发送好友申请")) controller.Request(found.Username);
            ImGui.EndDisabled();
        }
        if (state.State == "ready") ImGui.TextWrapped(state.Status);
    }

    private void DrawFriendRequests(FriendsChatSnapshot state)
    {
        if (state.Friends?.Requests.Count == 0) ImGui.TextDisabled("暂时没有好友申请。");
        foreach (var request in state.Friends?.Requests ?? [])
        {
            ImGui.PushID(request.Id);
            AccountIdentityBadge.Text(administratorIcon, request.User.Username, request.User.IsAdmin, DactTheme.Palette.Text, sponsorIcon, request.User.SponsorTier);
            ImGui.TextDisabled(request.Direction == "incoming" ? "希望添加你" : "等待对方确认");
            if (request.Direction == "incoming")
            {
                ImGui.BeginDisabled(state.Busy);
                if (DactTheme.SmallButton("接受")) controller.Accept(request.Id);
                ImGui.SameLine(); if (DactTheme.SmallButton("拒绝")) controller.Decline(request.Id);
                ImGui.EndDisabled();
            }
            ImGui.PopID();
        }
    }

    private void DrawRemoveConfirmation(FriendsChatSnapshot state)
    {
        if (removeId is not null) ImGui.OpenPopup("解除好友确认");
        if (!ImGui.BeginPopup("解除好友确认")) return;
        ImGui.TextWrapped($"解除与 {removeName} 的好友关系后，双方会话和保留消息将被删除。");
        ImGui.BeginDisabled(state.Busy);
        if (DactTheme.Button("确认解除")) { controller.Remove(removeId!); removeId = null; ImGui.CloseCurrentPopup(); }
        ImGui.EndDisabled(); ImGui.SameLine();
        if (DactTheme.Button("取消")) { removeId = null; ImGui.CloseCurrentPopup(); }
        ImGui.EndPopup();
    }
}

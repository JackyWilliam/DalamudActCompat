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
            list.AddRectFilled(position, end, ImGui.GetColorU32(Navy), 10, corners);
            list.AddRect(position, end, ImGui.GetColorU32(new Vector4(.34f, .29f, .18f, .85f)), 10, corners);
            DrawDrawerCollapse(position, visibleWidth, layout.Size.Y, scale, outside);
            // Slide the complete content behind the owner's right divider. The
            // outer window is only its visible clip, not a growing form layout.
            var contentPosition = outside ? layout.Position - new Vector2(layout.Size.X - visibleWidth, 0) : position;
            ImGui.SetCursorScreenPos(contentPosition + new Vector2(14 * scale, 12 * scale));
            // Child backgrounds also move behind the seam, so constrain their
            // inherited clip by one pixel to keep the owner's divider visible.
            ImGui.PushClipRect(position + new Vector2(1, 0), end - new Vector2(1, 0), true);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(2 * scale, 0));
            if (ImGui.BeginChild("drawer-content", new(layout.Size.X - 42 * scale, layout.Size.Y - 24 * scale), false))
            {
                var start = ImGui.GetCursorScreenPos();
                FriendsGlyph.Draw(ImGui.GetWindowDrawList(), start, 23 * scale, ImGui.GetColorU32(Blue));
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 31 * scale);
                ImGui.TextColored(Blue, "好友"); ImGui.SameLine();
                ImGui.TextDisabled($"{(state.State == "ready" ? state.Friends?.OnlineCount ?? 0 : 0)} 人在线");
                ImGui.Spacing(); ImGui.Separator(); ImGui.Spacing();
                DrawOwnPresenceMenu(state, scale);
                ImGui.Spacing(); ImGui.Separator(); ImGui.Spacing();
                if (state.State != "ready") ImGui.TextWrapped(state.Status);
                var count = state.Friends?.Requests.Count(r => r.Direction == "incoming") ?? 0;
                friendSection = BrandedWindowChrome.DrawNavigationRail("friend-sections", ["好友", count > 0 ? $"申请 ({count})" : "申请", "添加"], friendSection, 30 * scale);
                switch (friendSection)
                {
                    case 0: DrawFriendRows(state, scale); break;
                    case 1: DrawFriendRequests(state); break;
                    case 2: DrawAddFriend(state); break;
                }
                DrawRemoveConfirmation(state);
                ImGui.Spacing(); ImGui.Separator();
                ImGui.TextDisabled("消息使用须知"); ImGui.TextWrapped(CloudChatPolicy.Notice);
            }
            ImGui.EndChild(); ImGui.PopStyleVar(); ImGui.PopClipRect();
            // Preserve the main window's original right edge throughout both
            // animation directions instead of painting the shared seam away.
            var seam = outside ? position.X : end.X - 1;
            list.AddRectFilled(new(seam, position.Y), new(seam + 1, end.Y), ImGui.GetColorU32(ImGuiCol.Border));
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
        list.AddRectFilled(start, start + size, ImGui.GetColorU32(hovered ? new Vector4(.11f, .25f, .32f, .9f) : new Vector4(.07f, .12f, .16f, .85f)), 6 * scale);
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
        if (hovered) list.AddRectFilled(start - new Vector2(3 * scale), start + new Vector2(width, 51 * scale), ImGui.GetColorU32(new Vector4(.08f, .15f, .20f, 1)), 6);
        list.AddCircleFilled(start + new Vector2(7, 13) * scale, 4.5f * scale, ImGui.GetColorU32(StatusColor(status)));
        list.PushClipRect(start + new Vector2(21 * scale, 0), start + new Vector2(width, 53 * scale), true);
        list.AddText(start + new Vector2(22, 2) * scale, ImGui.GetColorU32(Vector4.One), state.Friends?.User?.Username ?? "我的账号");
        var detail = current is null ? "正在读取状态…" : StatusName(status) + (string.IsNullOrEmpty(current.Text) ? "" : " · " + current.Text);
        list.AddText(start + new Vector2(22, 26) * scale, ImGui.GetColorU32(new Vector4(.60f, .68f, .75f, 1)), detail);
        list.PopClipRect();
        if (hovered) ImGui.SetTooltip("修改我的状态");
        // Keep the list compact. Draft text and privacy controls appear only
        // while explicitly editing this account's presence, not in the default list.
        ImGui.SetNextWindowSize(new(292 * scale, 0), ImGuiCond.Always);
        ImGui.SetNextWindowSizeConstraints(new(240 * scale, 0), ImGui.GetMainViewport().WorkSize - new Vector2(16));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(12 * scale));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1);
        ImGui.PushStyleColor(ImGuiCol.Border, Gold);
        if (ImGui.BeginPopup("my-presence-settings"))
        {
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
        if (textLength > 80) ImGui.TextColored(Gold, $"状态文字最多 80 字（当前 {textLength} 字）");
        ImGui.BeginDisabled(!settingsDirty || textLength > 80);
        var desired = editingSettings with { ShareDuty = current.ShareDuty };
        if (ImGui.Button("保存状态") && controller.UpdatePresence(desired))
        { submittedSettings = desired; shareSubmission = false; }
        ImGui.EndDisabled();
        if (settingsDirty)
        {
            ImGui.SameLine(); if (ImGui.SmallButton("还原")) { editingSettings = current; settingsDirty = presenceChangedElsewhere = false; }
        }
        var share = current.ShareDuty;
        if (ImGui.Checkbox("向好友显示当前副本", ref share))
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
        else if (!settingsDirty) ImGui.TextDisabled($"已保存：{StatusName(current.Status)}{(string.IsNullOrEmpty(current.Text) ? "" : " · " + current.Text)}");
    }

    private void DrawFriendRows(FriendsChatSnapshot state, float scale)
    {
        foreach (var official in state.Conversations.Values.Where(c => c.Chat.Kind == "official"))
            if (ImGui.Selectable($"DACT 官方通知{(official.Unread ? "  · 未读" : "")}##official-{official.Chat.Id}")) OpenChat(official.Chat.Id);
        if (state.Friends?.Friends.Count == 0) ImGui.TextDisabled("还没有好友，去“添加”找一个账号吧。");
        foreach (var friend in (state.Friends?.Friends ?? []).OrderByDescending(f => f.Online).ThenBy(f => f.User.Username))
        {
            ImGui.PushID(friend.Id);
            var visible = state.State == "ready" && friend.Online;
            var status = visible ? friend.Status == "offline" ? "online" : friend.Status : "offline";
            var statusText = StatusName(status) + (visible && !string.IsNullOrEmpty(friend.StatusText) ? " · " + friend.StatusText : "");
            var duty = visible ? friend.Duty?.Name : null;
            var unread = friend.ConversationId is { } id && state.Conversations.GetValueOrDefault(id)?.Unread == true;
            var rowHeight = (duty is null ? 53 : 74) * scale;
            var start = ImGui.GetCursorScreenPos(); var width = ImGui.GetContentRegionAvail().X;
            if (ImGui.Selectable("##friend-row", false, ImGuiSelectableFlags.None, new(width, rowHeight)) && friend.ConversationId is { } chat) OpenChat(chat);
            var hovered = ImGui.IsItemHovered();
            if (ImGui.BeginPopupContextItem("friend-options"))
            {
                if (ImGui.MenuItem("解除好友关系…")) { removeId = friend.Id; removeName = friend.User.Username; }
                ImGui.EndPopup();
            }
            var list = ImGui.GetWindowDrawList();
            list.AddCircleFilled(start + new Vector2(6, 12) * scale, 3.5f * scale, ImGui.GetColorU32(StatusColor(status)));
            list.PushClipRect(start + new Vector2(18 * scale, 0), start + new Vector2(width - 10 * scale, rowHeight), true);
            list.AddText(start + new Vector2(19, 1) * scale, ImGui.GetColorU32(Vector4.One), friend.User.Username);
            list.AddText(start + new Vector2(19, 23) * scale, ImGui.GetColorU32(new Vector4(.57f, .65f, .73f, 1)), statusText);
            if (duty is not null) list.AddText(start + new Vector2(19, 45) * scale, ImGui.GetColorU32(Blue), "正在进行：" + duty);
            list.PopClipRect();
            if (unread) list.AddCircleFilled(start + new Vector2(width - 4 * scale, 12 * scale), 3 * scale, ImGui.GetColorU32(new Vector4(1, .35f, .35f, 1)));
            if (hovered) ImGui.SetTooltip(friend.User.Username + "\n" + statusText + (duty is null ? "" : "\n正在进行：" + duty));
            ImGui.PopID();
        }
    }

    private void DrawAddFriend(FriendsChatSnapshot state)
    {
        ImGui.TextDisabled("通过完整的 DACT 账号名查找");
        ImGui.SetNextItemWidth(-1); ImGui.InputTextWithHint("##friend-username", "输入完整账号名", ref search, 32);
        ImGui.BeginDisabled(state.Busy || string.IsNullOrWhiteSpace(search));
        if (ImGui.Button("查找账号", new(-1, 0))) controller.Lookup(search);
        ImGui.EndDisabled();
        if (state.Lookup?.User is { } found)
        {
            ImGui.TextWrapped(found.Username);
            ImGui.BeginDisabled(state.Busy || state.Lookup.Relationship is "friend" or "outgoing");
            if (ImGui.Button(state.Lookup.Relationship == "incoming" ? "同意互加" : state.Lookup.Relationship == "friend" ? "已经是好友" : state.Lookup.Relationship == "outgoing" ? "申请已发出" : "发送好友申请")) controller.Request(found.Username);
            ImGui.EndDisabled();
        }
        if (state.State == "ready") ImGui.TextWrapped(state.Status);
    }

    private void DrawFriendRequests(FriendsChatSnapshot state)
    {
        if (state.Friends?.Requests.Count == 0) ImGui.TextDisabled("暂时没有好友申请。");
        foreach (var request in state.Friends?.Requests ?? [])
        {
            ImGui.PushID(request.Id); ImGui.TextWrapped($"{request.User.Username} · {(request.Direction == "incoming" ? "希望添加你" : "等待对方确认")}");
            if (request.Direction == "incoming")
            {
                ImGui.BeginDisabled(state.Busy);
                if (ImGui.SmallButton("接受")) controller.Accept(request.Id);
                ImGui.SameLine(); if (ImGui.SmallButton("拒绝")) controller.Decline(request.Id);
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
        if (ImGui.Button("确认解除")) { controller.Remove(removeId!); removeId = null; ImGui.CloseCurrentPopup(); }
        ImGui.EndDisabled(); ImGui.SameLine();
        if (ImGui.Button("取消")) { removeId = null; ImGui.CloseCurrentPopup(); }
        ImGui.EndPopup();
    }
}

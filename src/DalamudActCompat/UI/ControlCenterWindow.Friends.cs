using DalamudActCompat.UI.Kit;

namespace DalamudActCompat.UI;

// 好友 and its 来信提醒 sub-page (features.js notify). A feature's own settings live under the
// feature with 「返回 XX」 at the top, not in the general 设置 page (user decision).
public sealed partial class ControlCenterWindow
{
    private bool friendNotificationsPage;

    public void OpenFriends()
    {
        selectedPage = Page.Friends;
        friendNotificationsPage = false;
        ShowAnimated();
    }

    private bool DrawFriendsPage()
    {
        if (Friends is null) return false;
        if (friendNotificationsPage) return DrawFriendNotificationsPage();
        Friends.OpenNotificationSettings = () => friendNotificationsPage = true;
        Friends.DrawPage();
        return false;
    }

    private bool DrawFriendNotificationsPage()
    {
        var changed = false;
        var backLabel = text.Get("返回好友", "Back to Friends");
        DactUi.PageHeader(text.Get("来信提醒", "Message alerts"),
            text.Get("好友 / 来信提醒 · 收到好友消息时怎么提醒你。", "Friends / Message alerts · How you are told about friend messages."),
            DactUi.ButtonWidth(backLabel),
            () =>
            {
                if (DactUi.Button(backLabel + "##friends-back")) friendNotificationsPage = false;
            });
        if (!DactUi.BeginGrid("friend-alerts-grid", 14)) return false;
        using (DactUi.Gap(14))
        {
            DactUi.BeginCard("friend-bubbles", text.Get("来信气泡", "Incoming message bubbles"));
            var enabled = configuration.FriendNotificationsEnabled;
            if (DactUi.ToggleRow(text.Get("显示来信气泡", "Show incoming message bubbles"), ref enabled,
                    text.Get("好友发来消息时，在屏幕边上弹出一个小气泡。", "A small bubble appears at the screen edge when a friend messages you.")))
            {
                configuration.FriendNotificationsEnabled = enabled;
                changed = true;
            }
            DactUi.BeginField(text.Get("气泡位置", "Bubble position"));
            var side = configuration.FriendNotificationsOnRight ? 1 : 0;
            if (DactUi.Select("##friend-bubble-side", [text.Get("左侧", "Left"), text.Get("右侧", "Right")], ref side, disabled: !enabled))
            {
                configuration.FriendNotificationsOnRight = side == 1;
                changed = true;
            }
            DactUi.EndField();
            var transparency = Math.Clamp(configuration.FriendNotificationBackgroundTransparency, 0, 100);
            DactUi.BeginField(text.Get("气泡背景透明度", "Bubble background transparency"));
            ImGuiDisabled(!enabled, () =>
            {
                if (DactUi.SliderInt("##friend-bubble-alpha", ref transparency, 0, 100, $"{transparency}%"))
                {
                    configuration.FriendNotificationBackgroundTransparency = transparency;
                    changed = true;
                }
            });
            DactUi.EndField(text.Get("0% 为不透明，100% 为全透明；文字和按钮保持清晰。", "0% is opaque; 100% is transparent. Text and buttons stay clear."));
            DactUi.EndCard();
        }
        DactUi.NextColumn();
        using (DactUi.Gap(14))
        {
            DactUi.BeginCard("friend-sound", text.Get("提示音", "Notification sound"));
            var sound = configuration.FriendNotificationSoundEnabled;
            if (DactUi.ToggleRow(text.Get("播放新消息提示音", "Play a sound for new messages"), ref sound))
            {
                configuration.FriendNotificationSoundEnabled = sound;
                changed = true;
            }
            DactUi.BeginField(text.Get("提示音", "Notification sound"));
            var selected = Math.Clamp(configuration.FriendNotificationSound, 0, 3);
            var previewLabel = text.Get("试听", "Preview");
            var previewWidth = DactUi.ButtonWidth(previewLabel);
            if (DactUi.Select("##friend-sound", [text.Get("提示音 1", "Sound 1"), text.Get("提示音 2", "Sound 2"), text.Get("提示音 3", "Sound 3"), text.Get("提示音 4", "Sound 4")],
                    ref selected, Dalamud.Bindings.ImGui.ImGui.GetContentRegionAvail().X - previewWidth - 8 * DactUi.S, !sound))
            {
                configuration.FriendNotificationSound = selected;
                changed = true;
            }
            Dalamud.Bindings.ImGui.ImGui.SameLine(0, 8 * DactUi.S);
            if (DactUi.Button(previewLabel + "##friend-sound-preview")) PreviewFriendNotificationSound?.Invoke(selected);
            DactUi.EndField(text.Get("关闭只停止提示，好友消息和未读数照常接收。", "Turning alerts off keeps messages and unread counts working."));
            DactUi.EndCard();
        }
        DactUi.EndGrid();
        return changed;
    }

    private static void ImGuiDisabled(bool disabled, Action draw)
    {
        Dalamud.Bindings.ImGui.ImGui.BeginDisabled(disabled);
        draw();
        Dalamud.Bindings.ImGui.ImGui.EndDisabled();
    }
}

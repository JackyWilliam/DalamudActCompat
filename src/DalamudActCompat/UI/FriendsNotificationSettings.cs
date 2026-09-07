using Dalamud.Bindings.ImGui;
using DalamudActCompat.Plugin;

namespace DalamudActCompat.UI;

internal static class FriendsNotificationSettings
{
    internal static bool Draw(PluginConfiguration configuration, UiText text, Action<int>? previewSound = null)
    {
        ImGui.Spacing(); ImGui.Separator(); ImGui.Spacing();
        ImGui.TextColored(ControlCenterWindow.Gold, text.Get("来信气泡", "Incoming message bubbles"));
        var enabled = configuration.FriendNotificationsEnabled;
        var changed = ImGui.Checkbox(text.Get("显示来信气泡", "Show incoming message bubbles"), ref enabled);
        configuration.FriendNotificationsEnabled = enabled;
        ImGui.BeginDisabled(!enabled);
        var side = configuration.FriendNotificationsOnRight ? 1 : 0;
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 9);
        if (ImGui.Combo(text.Get("来信气泡位置", "Bubble position"), ref side, text.IsChinese ? ["左侧", "右侧"] : ["Left", "Right"], 2))
        { configuration.FriendNotificationsOnRight = side == 1; changed = true; }
        var transparency = Math.Clamp(configuration.FriendNotificationBackgroundTransparency, 0, 100);
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 12);
        if (ImGui.SliderInt(text.Get("气泡背景透明度", "Bubble background transparency"), ref transparency, 0, 100, "%d%%"))
        { configuration.FriendNotificationBackgroundTransparency = transparency; changed = true; }
        ImGui.TextWrapped(text.Get("0% 为不透明，100% 为全透明；文字和按钮保持清晰。", "0% is opaque; 100% is transparent. Text and buttons stay clear."));
        ImGui.EndDisabled();
        var sound = configuration.FriendNotificationSoundEnabled;
        if (ImGui.Checkbox(text.Get("播放新消息提示音（独立于气泡）", "Play message sound (independent of bubbles)"), ref sound))
        { configuration.FriendNotificationSoundEnabled = sound; changed = true; }
        var selected = Math.Clamp(configuration.FriendNotificationSound, 0, 3);
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 9);
        if (ImGui.Combo(text.Get("提示音", "Notification sound"), ref selected,
            text.IsChinese ? ["提示音 1", "提示音 2", "提示音 3", "提示音 4"] : ["Sound 1", "Sound 2", "Sound 3", "Sound 4"], 4))
        { configuration.FriendNotificationSound = selected; changed = true; }
        ImGui.SameLine();
        if (ImGui.Button(text.Get("试听", "Preview"))) previewSound?.Invoke(selected);
        ImGui.TextWrapped(text.Get("关闭仅停止气泡提示，好友消息和未读仍正常接收。", "Turning bubbles off keeps messages and unread indicators working."));
        return changed;
    }
}

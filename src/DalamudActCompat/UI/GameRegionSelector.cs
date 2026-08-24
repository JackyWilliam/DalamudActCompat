using Dalamud.Bindings.ImGui;
using DalamudActCompat.Plugin;
using DalamudActCompat.Protocol;

namespace DalamudActCompat.UI;

internal static class GameRegionSelector
{
    public static void Draw(
        UiText text,
        GameRegionSelection selection,
        Action<GameRegionMode> setMode)
    {
        if (ImGui.BeginCombo(
                text.Get("游戏区域", "Game region"),
                FormatMode(text, selection.Mode, selection.EffectiveRegion)))
        {
            foreach (var mode in Enum.GetValues<GameRegionMode>())
            {
                if (ImGui.Selectable(
                        FormatMode(text, mode, ResolvePreviewRegion(mode, selection)),
                        mode == selection.Mode))
                {
                    setMode(mode);
                }

                if (mode == selection.Mode)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }
            ImGui.EndCombo();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(text.Get(
                "自动模式按当前 Dalamud 客户端识别；手动切换会立即刷新解析器和正在运行的扩展 Host。",
                "Auto uses the current Dalamud client. A manual change immediately refreshes the parser and active extension Hosts."));
        }

        // Region chooses packet/opcode tables, while language continues to follow the client
        // so a manual override never changes localized action names or log parsing.
        ImGui.TextDisabled(selection.IsManualOverride
            ? text.Get(
                $"当前：{FormatRegion(text, selection.EffectiveRegion)}（手动） · 自动检测：{FormatRegion(text, selection.DetectedRegion)} · 语言：{FormatLanguage(text, selection.ClientLanguage)}",
                $"Current: {FormatRegion(text, selection.EffectiveRegion)} (manual) · Detected: {FormatRegion(text, selection.DetectedRegion)} · Language: {FormatLanguage(text, selection.ClientLanguage)}")
            : text.Get(
                $"已自动检测：{FormatRegion(text, selection.EffectiveRegion)} · 语言：{FormatLanguage(text, selection.ClientLanguage)}",
                $"Detected: {FormatRegion(text, selection.EffectiveRegion)} · Language: {FormatLanguage(text, selection.ClientLanguage)}"));
    }

    private static HostGameRegion ResolvePreviewRegion(
        GameRegionMode mode,
        GameRegionSelection selection)
        => mode switch
        {
            GameRegionMode.Chinese => HostGameRegion.Chinese,
            GameRegionMode.Global => HostGameRegion.Global,
            _ => selection.DetectedRegion,
        };

    private static string FormatMode(
        UiText text,
        GameRegionMode mode,
        HostGameRegion region)
        => mode switch
        {
            GameRegionMode.Chinese => text.Get("国服（手动）", "China (manual)"),
            GameRegionMode.Global => text.Get("国际服（手动）", "Global (manual)"),
            _ => text.Get(
                $"自动检测（{FormatRegion(text, region)}）",
                $"Auto ({FormatRegion(text, region)})"),
        };

    private static string FormatRegion(UiText text, HostGameRegion region)
        => region == HostGameRegion.Chinese
            ? text.Get("国服", "China")
            : text.Get("国际服", "Global");

    private static string FormatLanguage(UiText text, HostClientLanguage language)
        => language switch
        {
            HostClientLanguage.Chinese => text.Get("简体中文", "Simplified Chinese"),
            HostClientLanguage.Japanese => text.Get("日语", "Japanese"),
            HostClientLanguage.German => text.Get("德语", "German"),
            HostClientLanguage.French => text.Get("法语", "French"),
            HostClientLanguage.Korean => text.Get("韩语", "Korean"),
            _ => text.Get("英语", "English"),
        };
}

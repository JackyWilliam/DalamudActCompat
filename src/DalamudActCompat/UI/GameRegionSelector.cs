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
                "自动模式按当前 Dalamud 所在的 XIVLauncherCN / XIVLauncher 目录识别，不使用客户端语言；手动切换会立即刷新解析器和正在运行的扩展 Host。",
                "Auto uses the XIVLauncherCN / XIVLauncher directory that owns the current Dalamud installation, not the client language. A manual change immediately refreshes the parser and active extension Hosts."));
        }

        // Region chooses packet/opcode tables, while language continues to follow the client
        // so a manual override never changes localized action names or log parsing.
        ImGui.TextDisabled(FormatDetectionStatus(text, selection));
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

    private static string FormatDetectionStatus(UiText text, GameRegionSelection selection)
    {
        var language = FormatLanguage(text, selection.ClientLanguage);
        if (!selection.HasDetectedLauncher)
        {
            return selection.IsManualOverride
                ? text.Get(
                    $"当前：{FormatRegion(text, selection.EffectiveRegion)}（手动） · 未识别启动器目录 · 语言：{language}",
                    $"Current: {FormatRegion(text, selection.EffectiveRegion)} (manual) · Launcher directory not recognized · Language: {language}")
                : text.Get(
                    $"未识别启动器目录，暂按国际服处理 · 可手动选择 · 语言：{language}",
                    $"Launcher directory not recognized; using Global · Manual selection is available · Language: {language}");
        }

        return selection.IsManualOverride
            ? text.Get(
                $"当前：{FormatRegion(text, selection.EffectiveRegion)}（手动） · 自动检测：{FormatRegion(text, selection.DetectedRegion)}（{selection.DetectedLauncherName}） · 语言：{language}",
                $"Current: {FormatRegion(text, selection.EffectiveRegion)} (manual) · Detected: {FormatRegion(text, selection.DetectedRegion)} ({selection.DetectedLauncherName}) · Language: {language}")
            : text.Get(
                $"已自动检测：{FormatRegion(text, selection.EffectiveRegion)}（{selection.DetectedLauncherName}） · 语言：{language}",
                $"Detected: {FormatRegion(text, selection.EffectiveRegion)} ({selection.DetectedLauncherName}) · Language: {language}");
    }
}

using Dalamud.Bindings.ImGui;
using DalamudActCompat.ActRuntime;
using DalamudActCompat.Plugin;

namespace DalamudActCompat.UI;

internal static class EncounterResetSelector
{
    internal static bool Draw(UiText text, PluginConfiguration configuration)
    {
        var changed = false;
        var mode = configuration.EncounterResetMode;
        ImGui.SetNextItemWidth(260);
        if (DactTheme.BeginCombo(text.Get("统计重置方式", "Statistics reset"), Label(text, mode)))
        {
            foreach (var option in Enum.GetValues<EncounterResetMode>())
                if (ImGui.Selectable(Label(text, option), mode == option) && mode != option)
                { configuration.EncounterResetMode = mode = option; changed = true; }
            ImGui.EndCombo();
        }
        if (mode == EncounterResetMode.AfterCombat)
        {
            ImGui.SetNextItemWidth(110);
            var seconds = configuration.EncounterResetSeconds;
            if (ImGui.InputInt(text.Get("秒后重置", "seconds before reset"), ref seconds))
            { configuration.EncounterResetSeconds = Math.Clamp(seconds, 0, 3600); changed = true; }
            ImGui.TextWrapped(text.Get("脱战后等待指定秒数；期间重新进入战斗则取消。结算并重置统计，下一场从零开始。0 为立即重置。",
                "Resets statistics after the selected time out of combat; re-entering combat cancels the countdown. The next fight starts from zero. 0 resets immediately."));
        }
        else ImGui.TextWrapped(text.Get("保持原来的统计逻辑。", "Keeps the existing statistics behavior."));
        return changed;
    }

    private static string Label(UiText text, EncounterResetMode mode) => mode == EncounterResetMode.AfterCombat
        ? text.Get("脱战指定秒数后重置", "Reset after combat") : text.Get("DACT默认", "DACT default");
}

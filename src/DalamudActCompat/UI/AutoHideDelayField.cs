using Dalamud.Bindings.ImGui;
using DalamudActCompat.ActRuntime;
using DalamudActCompat.UI.Kit;

namespace DalamudActCompat.UI;

// Shown under 「脱战隐藏」 for HTML overlays and combat meters: how long the window stays up after
// combat ends (CombatAutoHidePolicy). 0 keeps the original hide-at-once behaviour.
internal static class AutoHideDelayField
{
    public static bool Draw(UiText text, string id, ref int seconds)
    {
        var value = seconds;
        var changed = DactUi.NumberInput(id, ref value, 0, CombatAutoHidePolicy.MaximumDelaySeconds);
        ImGui.SameLine(0, 8 * DactUi.S);
        ImGui.AlignTextToFramePadding();
        DactUi.Text(text.Get("秒后隐藏（0 为脱战立即隐藏）", "seconds before hiding (0 hides as combat ends)"), DactTheme.Palette.Muted);
        if (ImGui.IsItemHovered())
            DactUi.Tooltip(text.Get("脱战后继续显示指定秒数再隐藏，方便看完上一场；期间重新进入战斗则一直显示。",
                "Stays up for the chosen seconds after combat ends so the last pull can be read; re-entering combat keeps it shown."));
        if (changed) seconds = value;
        return changed;
    }
}

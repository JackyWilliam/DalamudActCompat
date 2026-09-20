using Dalamud.Bindings.ImGui;
using DalamudActCompat.Meter;
using DalamudActCompat.Plugin;

namespace DalamudActCompat.UI;

internal static class MeterDisplayScopeSelector
{
    public static bool Draw(UiText text, PluginConfiguration configuration)
    {
        var settings = configuration.Meter;
        var changed = false;
        if (DactTheme.BeginCombo(text.Get("显示范围", "Display scope"), Label(text, settings.DisplayScope)))
        {
            foreach (var option in Enum.GetValues<MeterDisplayScope>())
            {
                var selected = option == settings.DisplayScope;
                if (ImGui.Selectable(Label(text, option), selected) && !selected)
                {
                    settings.DisplayScope = option;
                    changed = true;
                }
                if (selected) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
        ImGui.TextWrapped(settings.DisplayScope == MeterDisplayScope.ParserScope
            ? text.Get("按游戏解析范围显示已记录的玩家；自动范围跟随实际组队。8/24人、队伍选择和仅自己筛选暂不限制人数，切回后恢复原设置。",
                "Shows recorded players in the parse scope; Auto follows your actual group. The 8/24-player, party and self-only display limits resume when you switch back.")
            : text.Get("沿用各榜单的8/24人、队伍选择和仅自己等显示设置。",
                "Uses each meter's 8/24-player, party and self-only display settings."));
        return changed;
    }

    private static string Label(UiText text, MeterDisplayScope value)
        => value == MeterDisplayScope.ParserScope
            ? text.Get("跟随解析范围", "Follow parse scope")
            : text.Get("按榜单设置", "Use meter settings");
}

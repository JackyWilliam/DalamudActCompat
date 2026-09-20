using Dalamud.Bindings.ImGui;
using DalamudActCompat.ActRuntime;
using DalamudActCompat.Plugin;

namespace DalamudActCompat.UI;

internal static class ParserScopeSelector
{
    private static readonly ParserScope[] Options =
        [ParserScope.Auto, ParserScope.All, ParserScope.Self, ParserScope.Party, ParserScope.Alliance];

    public static bool Draw(UiText text, PluginConfiguration configuration)
    {
        var changed = false;
        if (DactTheme.BeginCombo(text.Get("游戏解析范围", "Parse scope"), Format(text, configuration.ParserScope)))
        {
            foreach (var scope in Options)
            {
                var selected = scope == configuration.ParserScope;
                if (ImGui.Selectable(Format(text, scope), selected) && !selected)
                {
                    configuration.ParserScope = scope;
                    changed = true;
                }
                if (selected) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(text.Get(
                "自动按实际组队切换：单人→仅自己，小队→小队成员，24人团队→团队成员；不受统计窗口8/24人布局影响。切换后用于后续统计，已记录的数据保留；不影响原始日志和触发器。",
                "Auto follows your actual group: solo → Self, party → Party, alliance → Alliance. The meter's 8/24-player layout does not change parsing. Changes apply to subsequent statistics; existing data, raw logs and triggers are retained."));
        return changed;
    }

    private static string Format(UiText text, ParserScope scope) => scope switch
    {
        ParserScope.Auto => text.Get("自动（按实际组队）", "Auto (current group)"),
        ParserScope.Self => text.Get("仅自己", "Self"),
        ParserScope.Party => text.Get("小队成员", "Party"),
        ParserScope.Alliance => text.Get("团队成员", "Alliance"),
        _ => text.Get("所有人", "Everyone"),
    };
}

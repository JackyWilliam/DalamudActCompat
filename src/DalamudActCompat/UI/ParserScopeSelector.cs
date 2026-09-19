using Dalamud.Bindings.ImGui;
using DalamudActCompat.ActRuntime;
using DalamudActCompat.Plugin;

namespace DalamudActCompat.UI;

internal static class ParserScopeSelector
{
    public static bool Draw(UiText text, PluginConfiguration configuration)
    {
        var changed = false;
        if (DactTheme.BeginCombo(text.Get("游戏解析范围", "Parse scope"), Format(text, configuration.ParserScope)))
        {
            foreach (var scope in Enum.GetValues<ParserScope>())
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
                "小队成员：自己所在的小队；团队成员：包括其他小队。切换后立即用于后续统计，已记录的数据保留；不影响原始日志和触发器。",
                "Party: your own party. Alliance: includes the other parties. Changes apply to subsequent statistics; existing data is retained. Raw logs and triggers are unaffected."));
        return changed;
    }

    private static string Format(UiText text, ParserScope scope) => scope switch
    {
        ParserScope.Self => text.Get("仅自己", "Self"),
        ParserScope.Party => text.Get("小队成员", "Party"),
        ParserScope.Alliance => text.Get("团队成员", "Alliance"),
        _ => text.Get("所有人", "Everyone"),
    };
}

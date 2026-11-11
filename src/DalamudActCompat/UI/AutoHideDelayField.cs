using System.Numerics;
using Dalamud.Bindings.ImGui;
using DalamudActCompat.ActRuntime;
using DalamudActCompat.UI.Kit;

namespace DalamudActCompat.UI;

// When 「脱战隐藏」 hides a window (CombatAutoHidePolicy): at once, or after a short delay so the
// last pull can still be read. A few presets in a select instead of a free number field (user
// found the bare number box under the switch ugly, 2026-11-10). It eases in under its switch.
internal static class AutoHideDelayField
{
    private static readonly int[] Presets = [0, 10, 30, 60, 120];

    // Measured row heights: a long translation wraps the label onto several lines, and the
    // opening animation must reveal the whole row, not one line of it.
    private static readonly Dictionary<uint, float> RowHeights = new();

    // The overlay page: its own row under the 「脱战隐藏」 switch, select lined up with the switches.
    public static bool DrawRow(UiText text, string id, bool visible, ref int seconds)
    {
        var s = DactUi.S;
        var key = ImGui.GetID(id + "##reveal");
        var reveal = DactMotion.Float(key, visible ? 1 : 0, DactMotion.Normal);
        if (reveal < .01f) return false;
        var (items, values, index) = Options(text, seconds);
        var opening = reveal < 1;
        var width = ImGui.GetContentRegionAvail().X;
        var height = (RowHeights.TryGetValue(key, out var measured) ? measured : 30 * s) * reveal;
        if (opening)
        {
            // Grow from the switch above: the row and the gap before it open together.
            var gap = ImGui.GetStyle().ItemSpacing.Y;
            ImGui.SetCursorScreenPos(ImGui.GetCursorScreenPos() - new Vector2(0, gap * (1 - reveal)));
        }

        var top = ImGui.GetCursorScreenPos();
        if (opening)
        {
            ImGui.PushClipRect(top, top + new Vector2(width, height + 1), true);
            ImGui.PushStyleVar(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * reveal);
        }

        var changed = DactUi.SelectRow(text.Get("隐藏时机", "Hide timing"), id, items, ref index, 120, muted: true) && visible;
        var hovered = ImGui.IsItemHovered();
        RowHeights[key] = ImGui.GetItemRectSize().Y;
        if (opening)
        {
            ImGui.PopStyleVar();
            ImGui.PopClipRect();
            ImGui.SetCursorScreenPos(top);
            ImGui.Dummy(new Vector2(width, Math.Max(1, height)));
        }

        if (hovered && !ImGui.IsPopupOpen("##select-popup-" + id)) DactUi.Tooltip(Explanation(text));
        if (changed) seconds = values[index];
        return changed;
    }

    // The meter editor: a pill select right after the 「脱战隐藏」 chip, on the same line.
    public static bool DrawChip(UiText text, string id, bool visible, ref int seconds)
    {
        var s = DactUi.S;
        var reveal = DactMotion.Float(ImGui.GetID(id + "##reveal"), visible ? 1 : 0, DactMotion.Normal);
        if (reveal < .01f) return false;
        var (items, values, index) = Options(text, seconds);
        var width = items.Max(item => DactUi.TextWidth(item)) + 44 * s;
        ImGui.SameLine(0, 8 * s * reveal);
        var start = ImGui.GetCursorScreenPos();
        // Slide open from the chip before it, so the chips after it move instead of jumping.
        var shown = width * reveal;
        ImGui.PushClipRect(start, start + new Vector2(shown, 28 * s), true);
        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * reveal);
        var changed = DactUi.Select(id, items, ref index, width, pill: true) && visible;
        var hovered = ImGui.IsItemHovered();
        ImGui.PopStyleVar();
        ImGui.PopClipRect();
        if (reveal < 1)
        {
            ImGui.SetCursorScreenPos(start);
            ImGui.Dummy(new Vector2(Math.Max(1, shown), 28 * s));
        }

        if (hovered && !ImGui.IsPopupOpen("##select-popup-" + id)) DactUi.Tooltip(Explanation(text));
        if (changed) seconds = values[index];
        return changed;
    }

    private static string Explanation(UiText text)
        => text.Get("脱战后什么时候隐藏。选「30 秒后」等，脱战后会再显示一会儿，方便看完上一场；期间重新进入战斗则一直显示。",
            "When to hide after combat. With After 30 s and the like, the window stays up for a while so the last pull can be read; re-entering combat keeps it shown.");

    // The presets, plus a value saved some other way so the select never hides what is set.
    private static (string[] Items, int[] Values, int Index) Options(UiText text, int seconds)
    {
        var current = CombatAutoHidePolicy.ClampDelay(seconds);
        var values = Presets.Contains(current) ? Presets : Presets.Append(current).Order().ToArray();
        return (values.Select(value => Label(text, value)).ToArray(), values, Array.IndexOf(values, current));
    }

    private static string Label(UiText text, int seconds)
        => seconds == 0 ? text.Get("立即", "Immediately")
            : seconds % 60 == 0 ? text.Format($"{seconds / 60} 分钟后", $"After {seconds / 60} min")
            : text.Format($"{seconds} 秒后", $"After {seconds} s");
}

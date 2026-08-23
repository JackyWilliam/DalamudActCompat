using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using DalamudActCompat.Plugin;
using DalamudActCompat.UI;
using System.Numerics;

namespace DalamudActCompat.Meter;

public sealed class MeterStyleEditorWindow : Window
{
    private enum DragMode
    {
        None,
        Move,
        Resize,
    }

    private static readonly Vector4 CanvasBackground = new(0.035f, 0.048f, 0.068f, 1);
    private static readonly Vector4 GridColor = new(0.30f, 0.36f, 0.43f, 0.35f);
    private static readonly Vector4 Accent = new(0.42f, 0.78f, 0.96f, 1);
    private readonly PluginConfiguration configuration;
    private readonly UiText text;
    private readonly Action saveConfiguration;
    private string? selectedSlotId;
    private DragMode dragMode;
    private Vector2 dragOffsetCells;
    private bool dragChanged;

    public MeterStyleEditorWindow(
        PluginConfiguration configuration,
        UiText text,
        Action saveConfiguration)
        : base("统计样式编辑器###DalamudActCompatMeterStyleEditor")
    {
        this.configuration = configuration;
        this.text = text;
        this.saveConfiguration = saveConfiguration;
        Size = new Vector2(980, 660);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(820, 560),
            MaximumSize = new Vector2(float.MaxValue),
        };
    }

    public void Open()
    {
        EnsureSelectedStyle();
        IsOpen = true;
    }

    public override void Draw()
    {
        WindowName = text.Get(
            "统计样式编辑器###DalamudActCompatMeterStyleEditor",
            "Meter Style Editor###DalamudActCompatMeterStyleEditor");
        var style = EnsureSelectedStyle();
        var changed = false;

        if (ImGui.BeginChild("style-list", new Vector2(220, -1), true))
        {
            ImGui.TextColored(Accent, text.Get("自定义样式", "Custom styles"));
            foreach (var candidate in configuration.Meter.CustomStyles.ToArray())
            {
                if (ImGui.Selectable(
                        $"{candidate.Name}##{candidate.Id}",
                        string.Equals(candidate.Id, style.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    configuration.Meter.Preset = MeterPreset.Custom;
                    configuration.Meter.SelectedCustomStyleId = candidate.Id;
                    selectedSlotId = null;
                    style = candidate;
                    changed = true;
                }
            }

            ImGui.Spacing();
            if (ImGui.Button(text.Get("新建横版样式", "New horizontal style"), new Vector2(-1, 0)))
            {
                style = CreateStyle(text.Get("自定义横版", "Custom horizontal"));
                changed = true;
            }
            if (ImGui.Button(text.Get("复制当前样式", "Duplicate style"), new Vector2(-1, 0)))
            {
                var clone = style.Clone(style.Name + text.Get(" 副本", " copy"));
                configuration.Meter.CustomStyles.Add(clone);
                SelectStyle(clone);
                style = clone;
                changed = true;
            }
            ImGui.BeginDisabled(configuration.Meter.CustomStyles.Count <= 1);
            if (ImGui.Button(text.Get("删除当前样式", "Delete style"), new Vector2(-1, 0)))
            {
                configuration.Meter.CustomStyles.Remove(style);
                style = configuration.Meter.CustomStyles[0];
                SelectStyle(style);
                selectedSlotId = null;
                changed = true;
            }
            ImGui.EndDisabled();
        }
        ImGui.EndChild();
        ImGui.SameLine();

        if (ImGui.BeginChild("style-editor", new Vector2(-1, -1), false))
        {
            var name = style.Name;
            ImGui.SetNextItemWidth(260);
            if (ImGui.InputText(text.Get("样式名称", "Style name"), ref name, 64))
            {
                style.Name = string.IsNullOrWhiteSpace(name) ? style.Name : name;
                changed = true;
            }
            ImGui.SameLine();
            ImGui.TextDisabled(text.Get(
                "内置预设只读；此处编辑的是横版副本",
                "Built-in presets are read-only; this edits a horizontal copy."));

            var cardOpacity = style.CardOpacity;
            if (SliderFloat(text.Get("卡片透明度", "Card opacity"), ref cardOpacity, 0, 1))
            {
                style.CardOpacity = cardOpacity;
                changed = true;
            }
            var backgroundOpacity = style.BackgroundOpacity;
            if (SliderFloat(text.Get("窗口背景", "Window background"), ref backgroundOpacity, 0, 1))
            {
                style.BackgroundOpacity = backgroundOpacity;
                changed = true;
            }
            ImGui.SameLine();
            var cardSpacing = style.CardSpacing;
            if (SliderFloat(text.Get("卡片间距", "Card spacing"), ref cardSpacing, 0, 24))
            {
                style.CardSpacing = cardSpacing;
                changed = true;
            }
            var cardRounding = style.CardRounding;
            if (SliderFloat(text.Get("圆角", "Rounding"), ref cardRounding, 0, 18))
            {
                style.CardRounding = cardRounding;
                changed = true;
            }
            ImGui.SameLine();
            var styleScale = style.FontScale;
            if (SliderFloat(text.Get("样式缩放", "Style scale"), ref styleScale, 0.65f, 2))
            {
                style.FontScale = styleScale;
                changed = true;
            }
            var textColor = style.TextColor;
            if (ImGui.ColorEdit4(text.Get("文字颜色", "Text color"), ref textColor))
            {
                style.TextColor = textColor;
                changed = true;
            }

            ImGui.TextDisabled(text.Get(
                "拖动槽位改变位置，拖动右下角手柄改变大小；全部自动吸附到 24×6 网格。",
                "Drag a slot to move it; drag its bottom-right handle to resize. Everything snaps to a 24×6 grid."));
            changed |= DrawCanvas(style);
            changed |= DrawSelectedSlotControls(style);
        }
        ImGui.EndChild();

        if (changed)
        {
            style.Normalize();
            saveConfiguration();
        }
    }

    private bool DrawCanvas(MeterCustomStyle style)
    {
        var available = ImGui.GetContentRegionAvail();
        var canvasSize = new Vector2(Math.Max(560, available.X), Math.Clamp(available.Y * 0.52f, 230, 330));
        var origin = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("slot-canvas", canvasSize);
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(origin, origin + canvasSize, ImGui.GetColorU32(CanvasBackground), 4);
        var cell = new Vector2(canvasSize.X / 24f, canvasSize.Y / 6f);
        for (var column = 1; column < 24; column++)
        {
            var x = origin.X + (column * cell.X);
            drawList.AddLine(new Vector2(x, origin.Y), new Vector2(x, origin.Y + canvasSize.Y), ImGui.GetColorU32(GridColor));
        }
        for (var row = 1; row < 6; row++)
        {
            var y = origin.Y + (row * cell.Y);
            drawList.AddLine(new Vector2(origin.X, y), new Vector2(origin.X + canvasSize.X, y), ImGui.GetColorU32(GridColor));
        }

        var visibleSlots = style.Slots.Where(static slot => slot.Visible).ToArray();
        var mouse = ImGui.GetMousePos();
        if (dragMode == DragMode.None && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            var hit = visibleSlots.Reverse().FirstOrDefault(slot => IsInside(mouse, SlotRect(slot, origin, cell)));
            if (hit is not null)
            {
                selectedSlotId = hit.Id;
                var rect = SlotRect(hit, origin, cell);
                var handle = new Vector2(rect.Max.X - 12, rect.Max.Y - 12);
                dragMode = mouse.X >= handle.X && mouse.Y >= handle.Y
                    ? DragMode.Resize
                    : DragMode.Move;
                dragOffsetCells = new Vector2(
                    (mouse.X - rect.Min.X) / cell.X,
                    (mouse.Y - rect.Min.Y) / cell.Y);
            }
        }

        foreach (var slot in visibleSlots)
        {
            var rect = SlotRect(slot, origin, cell);
            var selected = string.Equals(slot.Id, selectedSlotId, StringComparison.OrdinalIgnoreCase);
            drawList.AddRectFilled(
                rect.Min + new Vector2(2),
                rect.Max - new Vector2(2),
                ImGui.GetColorU32(selected
                    ? new Vector4(Accent.X, Accent.Y, Accent.Z, 0.48f)
                    : new Vector4(0.18f, 0.30f, 0.42f, 0.72f)),
                4);
            drawList.AddRect(
                rect.Min + new Vector2(2),
                rect.Max - new Vector2(2),
                ImGui.GetColorU32(selected ? Accent : new Vector4(0.65f, 0.72f, 0.80f, 0.65f)),
                4);
            var label = MetricLabel(slot.Metric);
            drawList.AddText(rect.Min + new Vector2(6, 5), ImGui.GetColorU32(Vector4.One), label);
            if (selected)
            {
                drawList.AddTriangleFilled(
                    rect.Max - new Vector2(12, 2),
                    rect.Max - new Vector2(2, 12),
                    rect.Max - new Vector2(2),
                    ImGui.GetColorU32(Accent));
            }
        }

        var selectedSlot = style.Slots.FirstOrDefault(slot =>
            string.Equals(slot.Id, selectedSlotId, StringComparison.OrdinalIgnoreCase));
        if (dragMode != DragMode.None && selectedSlot is not null && ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            if (dragMode == DragMode.Move)
            {
                selectedSlot.Column = Math.Clamp(
                    (int)MathF.Round(((mouse.X - origin.X) / cell.X) - dragOffsetCells.X),
                    0,
                    24 - selectedSlot.ColumnSpan);
                selectedSlot.Row = Math.Clamp(
                    (int)MathF.Round(((mouse.Y - origin.Y) / cell.Y) - dragOffsetCells.Y),
                    0,
                    6 - selectedSlot.RowSpan);
            }
            else
            {
                selectedSlot.ColumnSpan = Math.Clamp(
                    (int)MathF.Round((mouse.X - origin.X) / cell.X) - selectedSlot.Column,
                    1,
                    24 - selectedSlot.Column);
                selectedSlot.RowSpan = Math.Clamp(
                    (int)MathF.Round((mouse.Y - origin.Y) / cell.Y) - selectedSlot.Row,
                    1,
                    6 - selectedSlot.Row);
            }
            dragChanged = true;
        }
        if (dragMode != DragMode.None && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
        {
            dragMode = DragMode.None;
            var changed = dragChanged;
            dragChanged = false;
            return changed;
        }

        return false;
    }

    private bool DrawSelectedSlotControls(MeterCustomStyle style)
    {
        var changed = false;
        ImGui.Spacing();
        if (ImGui.Button(text.Get("添加数据槽", "Add data slot")))
        {
            var used = style.Slots.Select(static slot => slot.Metric).ToHashSet();
            var metric = Enum.GetValues<MeterSlotMetric>().FirstOrDefault(candidate => !used.Contains(candidate));
            var addedSlot = new MeterSlotDefinition(metric, 0, 0, 6, 2, MeterSlotAlignment.Left);
            style.Slots.Add(addedSlot);
            selectedSlotId = addedSlot.Id;
            changed = true;
        }

        var slot = style.Slots.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, selectedSlotId, StringComparison.OrdinalIgnoreCase));
        if (slot is null)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(text.Get("选择一个槽位后可编辑数据和尺寸", "Select a slot to edit its data and size"));
            return changed;
        }

        ImGui.SameLine();
        if (ImGui.Button(text.Get("删除槽位", "Delete slot")))
        {
            style.Slots.Remove(slot);
            selectedSlotId = null;
            return true;
        }
        var visible = slot.Visible;
        ImGui.SameLine();
        if (ImGui.Checkbox(text.Get("显示", "Visible"), ref visible))
        {
            slot.Visible = visible;
            changed = true;
        }

        if (ImGui.BeginCombo(text.Get("数据", "Data"), MetricLabel(slot.Metric)))
        {
            foreach (var metric in Enum.GetValues<MeterSlotMetric>())
            {
                if (ImGui.Selectable(MetricLabel(metric), metric == slot.Metric))
                {
                    slot.Metric = metric;
                    changed = true;
                }
            }
            ImGui.EndCombo();
        }
        var alignment = slot.Alignment;
        if (ImGui.BeginCombo(text.Get("对齐", "Alignment"), alignment.ToString()))
        {
            foreach (var candidate in Enum.GetValues<MeterSlotAlignment>())
            {
                if (ImGui.Selectable(candidate.ToString(), candidate == alignment))
                {
                    slot.Alignment = candidate;
                    changed = true;
                }
            }
            ImGui.EndCombo();
        }
        var column = slot.Column;
        if (SliderInt(text.Get("横向位置", "Column"), ref column, 0, 23))
        {
            slot.Column = column;
            changed = true;
        }
        var row = slot.Row;
        if (SliderInt(text.Get("纵向位置", "Row"), ref row, 0, 5))
        {
            slot.Row = row;
            changed = true;
        }
        var columnSpan = slot.ColumnSpan;
        if (SliderInt(text.Get("宽度", "Width"), ref columnSpan, 1, 24 - slot.Column))
        {
            slot.ColumnSpan = columnSpan;
            changed = true;
        }
        var rowSpan = slot.RowSpan;
        if (SliderInt(text.Get("高度", "Height"), ref rowSpan, 1, 6 - slot.Row))
        {
            slot.RowSpan = rowSpan;
            changed = true;
        }
        return changed;
    }

    private MeterCustomStyle EnsureSelectedStyle()
    {
        var selected = configuration.Meter.GetSelectedCustomStyle();
        if (selected is not null)
        {
            return selected;
        }

        return CreateStyle(text.Get("自定义横版", "Custom horizontal"));
    }

    private MeterCustomStyle CreateStyle(string name)
    {
        var style = new MeterCustomStyle { Name = name };
        style.Normalize();
        configuration.Meter.CustomStyles.Add(style);
        SelectStyle(style);
        saveConfiguration();
        return style;
    }

    private void SelectStyle(MeterCustomStyle style)
    {
        configuration.Meter.Preset = MeterPreset.Custom;
        configuration.Meter.SelectedCustomStyleId = style.Id;
    }

    private static (Vector2 Min, Vector2 Max) SlotRect(
        MeterSlotDefinition slot,
        Vector2 origin,
        Vector2 cell)
        => (
            origin + new Vector2(slot.Column * cell.X, slot.Row * cell.Y),
            origin + new Vector2(
                (slot.Column + slot.ColumnSpan) * cell.X,
                (slot.Row + slot.RowSpan) * cell.Y));

    private static bool IsInside(Vector2 point, (Vector2 Min, Vector2 Max) rect)
        => point.X >= rect.Min.X && point.X <= rect.Max.X &&
           point.Y >= rect.Min.Y && point.Y <= rect.Max.Y;

    private static bool SliderFloat(string label, ref float value, float minimum, float maximum)
    {
        ImGui.SetNextItemWidth(170);
        return ImGui.SliderFloat(label, ref value, minimum, maximum, "%.2f");
    }

    private static bool SliderInt(string label, ref int value, int minimum, int maximum)
    {
        ImGui.SetNextItemWidth(170);
        return ImGui.SliderInt(label, ref value, minimum, maximum);
    }

    private string MetricLabel(MeterSlotMetric metric)
        => metric switch
        {
            MeterSlotMetric.Rank => text.Get("排名", "Rank"),
            MeterSlotMetric.Job => text.Get("职业图标", "Job icon"),
            MeterSlotMetric.PlayerName => text.Get("玩家名", "Player name"),
            MeterSlotMetric.Dps => "DPS",
            MeterSlotMetric.Hps => "HPS",
            MeterSlotMetric.DamagePercent => text.Get("伤害占比", "Damage %"),
            MeterSlotMetric.TotalDamage => text.Get("总伤害", "Total damage"),
            MeterSlotMetric.HighestDamageAction => text.Get("最高技能名", "Highest action"),
            MeterSlotMetric.HighestDamage => text.Get("最高伤害", "Highest hit"),
            MeterSlotMetric.Deaths => text.Get("死亡", "Deaths"),
            MeterSlotMetric.CriticalHitPercent => text.Get("暴击率", "CRIT %"),
            MeterSlotMetric.DirectHitPercent => text.Get("直击率", "DH %"),
            MeterSlotMetric.CriticalDirectHitPercent => text.Get("直暴率", "CDH %"),
            _ => metric.ToString(),
        };
}

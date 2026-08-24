using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using DalamudActCompat.Plugin;
using DalamudActCompat.UI;
using System.Numerics;
using System.Reflection;

namespace DalamudActCompat.Meter;

public sealed class MeterStyleEditorWindow : Window
{
    private static readonly Vector4 Navy = new(0.035f, 0.048f, 0.068f, 1);
    private static readonly Vector4 NavyRaised = new(0.070f, 0.095f, 0.125f, 1);
    private static readonly Vector4 NavyHover = new(0.105f, 0.145f, 0.185f, 1);
    private static readonly Vector4 Gold = new(0.78f, 0.66f, 0.36f, 1);
    private static readonly Vector4 IceBlue = new(0.42f, 0.78f, 0.96f, 1);
    private readonly PluginConfiguration configuration;
    private readonly ISharedImmediateTexture logoTexture;
    private readonly UiText text;
    private readonly Action saveConfiguration;
    private MeterWindowKind selectedKind;
    private string? selectedSlotId;

    public MeterStyleEditorWindow(
        PluginConfiguration configuration,
        ISharedImmediateTexture logoTexture,
        UiText text,
        Action saveConfiguration)
        : base("战斗统计布局编辑器###DalamudActCompatMeterStyleEditor")
    {
        this.configuration = configuration;
        this.logoTexture = logoTexture;
        this.text = text;
        this.saveConfiguration = saveConfiguration;
        Size = new Vector2(1040, 690);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(880, 590),
            MaximumSize = new Vector2(float.MaxValue),
        };
    }

    public void Open()
    {
        EnsureSelectedSlot(CurrentProfile);
        IsOpen = true;
    }

    public override void PreDraw()
    {
        Flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse;
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Navy);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, NavyRaised);
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(Gold.X, Gold.Y, Gold.Z, 0.72f));
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.12f, 0.17f, 0.24f, 1));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, NavyHover);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.18f, 0.25f, 0.34f, 1));
        ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(0.055f, 0.075f, 0.10f, 1));
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, NavyHover);
        ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(0.14f, 0.34f, 0.46f, 0.30f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 10);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 8);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8, 7));
    }

    public override void PostDraw()
    {
        ImGui.PopStyleVar(4);
        ImGui.PopStyleColor(9);
    }

    public override void Draw()
    {
        WindowName = text.Get(
            "战斗统计布局编辑器###DalamudActCompatMeterStyleEditor",
            "Combat Meter Layout Editor###DalamudActCompatMeterStyleEditor");
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "--";
        if (BrandedWindowChrome.Draw(
                logoTexture,
                text.Get("布局编辑器", "Layout editor"),
                KindLabel(selectedKind),
                IceBlue,
                $"v{version}",
                "meter-style-editor"))
        {
            IsOpen = false;
            return;
        }

        var selectedIndex = BrandedWindowChrome.DrawNavigationRail(
            "meter-editor-kind",
            [
                text.Get("经典榜", "Classic"),
                text.Get("透明横版", "Transparent horizontal"),
                text.Get("职能分栏", "Role split"),
            ],
            (int)selectedKind);
        if (selectedIndex != (int)selectedKind)
        {
            selectedKind = (MeterWindowKind)selectedIndex;
            selectedSlotId = null;
            EnsureSelectedSlot(CurrentProfile);
        }
        ImGui.Dummy(new Vector2(1, 6));

        var changed = DrawWindowControls(CurrentProfile);
        ImGui.Dummy(new Vector2(1, 5));
        var available = ImGui.GetContentRegionAvail();
        const float leftWidth = 255;
        const float rightWidth = 270;
        if (ImGui.BeginChild("meter-editor-slots", new Vector2(leftWidth, available.Y), true))
        {
            changed |= DrawSlotList(CurrentProfile);
        }
        ImGui.EndChild();
        ImGui.SameLine();
        if (ImGui.BeginChild(
                "meter-editor-preview",
                new Vector2(Math.Max(250, available.X - leftWidth - rightWidth - 16), available.Y),
                true))
        {
            changed |= DrawPreview(CurrentProfile);
        }
        ImGui.EndChild();
        ImGui.SameLine();
        if (ImGui.BeginChild("meter-editor-properties", new Vector2(rightWidth, available.Y), true))
        {
            changed |= DrawSlotProperties(CurrentProfile);
        }
        ImGui.EndChild();

        if (changed)
        {
            CurrentProfile.Normalize(DefaultSlots(selectedKind));
            SynchronizeClassicSettings();
            saveConfiguration();
        }
    }

    private MeterWindowProfile CurrentProfile => selectedKind switch
    {
        MeterWindowKind.Horizontal => configuration.Meter.HorizontalWindow,
        MeterWindowKind.RoleSplit => configuration.Meter.RoleSplitWindow,
        _ => configuration.Meter.ClassicWindow,
    };

    private bool DrawWindowControls(MeterWindowProfile profile)
    {
        var changed = false;
        var enabled = profile.IsEnabled;
        if (ImGui.Checkbox(text.Get("启用这个独立窗口", "Enable this independent window"), ref enabled))
        {
            profile.IsEnabled = enabled;
            changed = true;
        }
        ImGui.SameLine();
        var locked = profile.IsLocked;
        if (ImGui.Checkbox(text.Get("锁定", "Lock"), ref locked))
        {
            profile.IsLocked = locked;
            changed = true;
        }
        ImGui.SameLine();
        var clickThrough = profile.ClickThroughWhenLocked;
        ImGui.BeginDisabled(!profile.IsLocked);
        if (ImGui.Checkbox(text.Get("锁定时鼠标穿透", "Click-through when locked"), ref clickThrough))
        {
            profile.ClickThroughWhenLocked = clickThrough;
            changed = true;
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        var autoHide = profile.AutoHideOutOfCombat;
        if (ImGui.Checkbox(text.Get("脱战隐藏", "Hide out of combat"), ref autoHide))
        {
            profile.AutoHideOutOfCombat = autoHide;
            changed = true;
        }
        return changed;
    }

    private bool DrawSlotList(MeterWindowProfile profile)
    {
        var changed = false;
        ImGui.TextColored(Gold, text.Get("槽位", "Complication slots"));
        ImGui.TextDisabled(text.Get(
            "点击槽位后选择内容；系统自动排布，不会重叠。",
            "Select a slot and choose its content. Layout never overlaps."));
        ImGui.Separator();
        for (var index = 0; index < profile.Slots.Count; index++)
        {
            var slot = profile.Slots[index];
            var visible = slot.Visible;
            if (ImGui.Checkbox($"##slot-visible-{slot.Id}", ref visible))
            {
                slot.Visible = visible;
                changed = true;
            }
            ImGui.SameLine();
            if (ImGui.Selectable(
                    $"{index + 1:00}  {MeterSlotPresentation.Label(slot.Metric, text)}##slot-{slot.Id}",
                    string.Equals(selectedSlotId, slot.Id, StringComparison.OrdinalIgnoreCase)))
            {
                selectedSlotId = slot.Id;
            }
        }

        ImGui.Dummy(new Vector2(1, 4));
        if (ImGui.Button(text.Get("＋ 添加槽位", "+ Add slot"), new Vector2(-1, 0)))
        {
            var slot = new MeterSlotDefinition(
                FirstUnusedMetric(profile),
                0,
                0,
                4,
                2,
                MeterSlotAlignment.Left);
            profile.Slots.Add(slot);
            selectedSlotId = slot.Id;
            changed = true;
        }
        if (ImGui.Button(text.Get("恢复此模板默认槽位", "Restore template slots"), new Vector2(-1, 0)))
        {
            profile.Slots = DefaultSlots(selectedKind).Select(static slot => slot.Clone()).ToList();
            selectedSlotId = profile.Slots.FirstOrDefault()?.Id;
            changed = true;
        }
        return changed;
    }

    private bool DrawPreview(MeterWindowProfile profile)
    {
        var changed = false;
        ImGui.TextColored(Gold, text.Get("自动布局预览", "Automatic layout preview"));
        ImGui.TextDisabled(KindDescription(selectedKind));
        ImGui.Separator();
        var slots = profile.Slots.Where(static slot => slot.Visible).ToArray();
        if (slots.Length == 0)
        {
            ImGui.TextDisabled(text.Get("当前没有启用的槽位。", "No enabled slots."));
            return false;
        }

        var available = ImGui.GetContentRegionAvail();
        var columns = selectedKind == MeterWindowKind.Horizontal ? 2 : 3;
        var spacing = new Vector2(8);
        var cellWidth = Math.Max(62, (available.X - ((columns - 1) * spacing.X)) / columns);
        var cellHeight = selectedKind == MeterWindowKind.Classic ? 56 : 72;
        var origin = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();
        for (var index = 0; index < slots.Length; index++)
        {
            var slot = slots[index];
            var column = index % columns;
            var row = index / columns;
            var start = origin + new Vector2(column * (cellWidth + spacing.X), row * (cellHeight + spacing.Y));
            ImGui.SetCursorScreenPos(start);
            ImGui.InvisibleButton($"slot-preview-{slot.Id}", new Vector2(cellWidth, cellHeight));
            if (ImGui.IsItemClicked())
            {
                selectedSlotId = slot.Id;
            }
            var selected = string.Equals(selectedSlotId, slot.Id, StringComparison.OrdinalIgnoreCase);
            drawList.AddRectFilled(
                start,
                start + new Vector2(cellWidth, cellHeight),
                ImGui.GetColorU32(selected ? new Vector4(0.14f, 0.34f, 0.46f, 0.72f) : Navy),
                Math.Min(18, cellHeight * 0.28f));
            drawList.AddRect(
                start,
                start + new Vector2(cellWidth, cellHeight),
                ImGui.GetColorU32(selected ? IceBlue : new Vector4(Gold.X, Gold.Y, Gold.Z, 0.55f)),
                Math.Min(18, cellHeight * 0.28f));
            var label = MeterSlotPresentation.Label(slot.Metric, text);
            var rendered = MeterSlotPresentation.TrimToWidth(label, cellWidth - 14);
            var labelSize = ImGui.CalcTextSize(rendered);
            drawList.AddText(
                start + ((new Vector2(cellWidth, cellHeight) - labelSize) * 0.5f),
                ImGui.GetColorU32(selected ? IceBlue : Vector4.One),
                rendered);
        }

        var rows = (slots.Length + columns - 1) / columns;
        ImGui.SetCursorScreenPos(origin + new Vector2(0, rows * (cellHeight + spacing.Y)));
        return changed;
    }

    private bool DrawSlotProperties(MeterWindowProfile profile)
    {
        var changed = false;
        ImGui.TextColored(Gold, text.Get("窗口与槽位", "Window and slot"));
        var showHeader = profile.ShowHeader;
        if (selectedKind != MeterWindowKind.Horizontal &&
            ImGui.Checkbox(text.Get("显示标题区", "Show header"), ref showHeader))
        {
            profile.ShowHeader = showHeader;
            changed = true;
        }
        var fontScale = profile.FontScale;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.SliderFloat(text.Get("字号", "Text scale"), ref fontScale, 0.65f, 2, "%.2f"))
        {
            profile.FontScale = fontScale;
            changed = true;
        }
        if (selectedKind == MeterWindowKind.Horizontal)
        {
            var itemWidth = profile.ItemWidth;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.SliderFloat(text.Get("模块宽度", "Module width"), ref itemWidth, 140, 420, "%.0f"))
            {
                profile.ItemWidth = itemWidth;
                changed = true;
            }
        }
        ImGui.Separator();

        var slot = profile.Slots.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, selectedSlotId, StringComparison.OrdinalIgnoreCase));
        if (slot is null)
        {
            ImGui.TextDisabled(text.Get("请先选择一个槽位。", "Select a slot first."));
            return changed;
        }

        ImGui.TextColored(IceBlue, text.Get("槽位内容", "Slot content"));
        var preview = MeterSlotPresentation.Label(slot.Metric, text);
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##slot-metric", preview))
        {
            foreach (var metric in Enum.GetValues<MeterSlotMetric>())
            {
                if (ImGui.Selectable(
                        $"{MeterSlotPresentation.Label(metric, text)}##metric-{metric}",
                        metric == slot.Metric))
                {
                    slot.Metric = metric;
                    changed = true;
                }
            }
            ImGui.EndCombo();
        }
        var visible = slot.Visible;
        if (ImGui.Checkbox(text.Get("使用这个槽位", "Use this slot"), ref visible))
        {
            slot.Visible = visible;
            changed = true;
        }
        ImGui.Dummy(new Vector2(1, 8));
        var index = profile.Slots.IndexOf(slot);
        ImGui.BeginDisabled(index <= 0);
        if (ImGui.Button(text.Get("↑ 前移", "↑ Move up"), new Vector2(-1, 0)))
        {
            (profile.Slots[index - 1], profile.Slots[index]) =
                (profile.Slots[index], profile.Slots[index - 1]);
            changed = true;
        }
        ImGui.EndDisabled();
        ImGui.BeginDisabled(index < 0 || index >= profile.Slots.Count - 1);
        if (ImGui.Button(text.Get("↓ 后移", "↓ Move down"), new Vector2(-1, 0)))
        {
            (profile.Slots[index + 1], profile.Slots[index]) =
                (profile.Slots[index], profile.Slots[index + 1]);
            changed = true;
        }
        ImGui.EndDisabled();
        if (ImGui.Button(text.Get("删除槽位", "Delete slot"), new Vector2(-1, 0)))
        {
            profile.Slots.Remove(slot);
            selectedSlotId = profile.Slots.Count == 0
                ? null
                : profile.Slots[Math.Min(index, profile.Slots.Count - 1)].Id;
            changed = true;
        }

        ImGui.Dummy(new Vector2(1, 8));
        ImGui.TextDisabled(text.Get(
            "最高伤害技能会自动截短；把鼠标移到统计项上可查看完整技能名和数值。",
            "Max-hit skills are truncated; hover the meter for full details."));
        return changed;
    }

    private void SynchronizeClassicSettings()
    {
        var meter = configuration.Meter;
        var profile = meter.ClassicWindow;
        meter.IsLocked = profile.IsLocked;
        meter.ClickThroughWhenLocked = profile.ClickThroughWhenLocked;
        meter.AutoHideOutOfCombat = profile.AutoHideOutOfCombat;
        meter.ShowHeader = profile.ShowHeader;
        meter.FontScale = profile.FontScale;
        meter.SortMode = profile.SortMode;
        bool Has(MeterSlotMetric metric) => profile.Slots.Any(slot => slot.Visible && slot.Metric == metric);
        meter.ShowRank = Has(MeterSlotMetric.Rank);
        meter.ShowJob = Has(MeterSlotMetric.Job);
        meter.ShowPlayerName = Has(MeterSlotMetric.PlayerName);
        meter.ShowDps = Has(MeterSlotMetric.Dps);
        meter.ShowRdps = Has(MeterSlotMetric.Rdps);
        meter.ShowHps = Has(MeterSlotMetric.Hps);
        meter.ShowDamagePercent = Has(MeterSlotMetric.DamagePercent);
        meter.ShowTotalDamage = Has(MeterSlotMetric.TotalDamage);
        meter.ShowTotalHealing = Has(MeterSlotMetric.TotalHealing);
        meter.ShowHighestDamage = Has(MeterSlotMetric.HighestDamageAction) || Has(MeterSlotMetric.HighestDamage);
        meter.ShowDeaths = Has(MeterSlotMetric.Deaths);
        meter.ShowCriticalHitRate = Has(MeterSlotMetric.CriticalHitPercent);
        meter.ShowDirectHitRate = Has(MeterSlotMetric.DirectHitPercent);
        meter.ShowCriticalDirectHitRate = Has(MeterSlotMetric.CriticalDirectHitPercent);
    }

    private void EnsureSelectedSlot(MeterWindowProfile profile)
    {
        if (!profile.Slots.Any(slot => string.Equals(
                slot.Id,
                selectedSlotId,
                StringComparison.OrdinalIgnoreCase)))
        {
            selectedSlotId = profile.Slots.FirstOrDefault()?.Id;
        }
    }

    private MeterSlotMetric FirstUnusedMetric(MeterWindowProfile profile)
        => Enum.GetValues<MeterSlotMetric>()
            .FirstOrDefault(metric => profile.Slots.All(slot => slot.Metric != metric));

    private static IReadOnlyList<MeterSlotDefinition> DefaultSlots(MeterWindowKind kind)
        => kind switch
        {
            MeterWindowKind.Horizontal => MeterSlotDefaults.CreateHorizontal(),
            MeterWindowKind.RoleSplit => MeterSlotDefaults.CreateRoleSplit(),
            _ => MeterSlotDefaults.CreateClassic(),
        };

    private string KindLabel(MeterWindowKind kind)
        => kind switch
        {
            MeterWindowKind.Horizontal => text.Get("透明横版", "Transparent horizontal"),
            MeterWindowKind.RoleSplit => text.Get("职能分栏", "Role split"),
            _ => text.Get("经典榜", "Classic"),
        };

    private string KindDescription(MeterWindowKind kind)
        => kind switch
        {
            MeterWindowKind.Horizontal => text.Get(
                "横向滑动；DPS/HPS 从高到低排列；运行时完全透明。",
                "Horizontal carousel; DPS/HPS descending; fully transparent at runtime."),
            MeterWindowKind.RoleSplit => text.Get(
                "D/T 与治疗分区展示，保持普通列表结构。",
                "Separate D/T and healer sections with a regular list structure."),
            _ => text.Get(
                "经典纵向榜单；槽位会映射为可见列。",
                "Classic vertical ranking; slots map to visible columns."),
        };
}

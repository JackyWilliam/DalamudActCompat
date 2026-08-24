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
    private readonly JobIconTextureSet jobIcons;
    private readonly UiText text;
    private readonly Action saveConfiguration;
    private MeterWindowKind selectedKind;
    private string? selectedSlotId;

    public MeterStyleEditorWindow(
        PluginConfiguration configuration,
        ISharedImmediateTexture logoTexture,
        JobIconTextureSet jobIcons,
        UiText text,
        Action saveConfiguration)
        : base("战斗统计布局编辑器###DalamudActCompatMeterStyleEditor")
    {
        this.configuration = configuration;
        this.logoTexture = logoTexture;
        this.jobIcons = jobIcons;
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
        SetEditingProfile(null);
        selectedKind = configuration.Meter.ActiveWindowKind;
        SetEditingProfile(selectedKind);
        EnsureSelectedSlot(CurrentProfile);
        IsOpen = true;
    }

    public override void OnClose()
    {
        SetEditingProfile(null);
        saveConfiguration();
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
            SetEditingProfile(null);
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
            SetEditingProfile(null);
            selectedKind = (MeterWindowKind)selectedIndex;
            SetEditingProfile(selectedKind);
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
        if (selectedKind == MeterWindowKind.Classic)
        {
            var compact = configuration.Meter.CompactMode;
            ImGui.SameLine();
            if (ImGui.Checkbox(text.Get("只显示自己", "Show self only"), ref compact))
            {
                configuration.Meter.CompactMode = compact;
                changed = true;
            }
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
            var canEnable = CanUseMetric(slot.Metric);
            ImGui.BeginDisabled(!canEnable);
            if (ImGui.Checkbox($"##slot-visible-{slot.Id}", ref visible))
            {
                slot.Visible = visible;
                changed = true;
            }
            ImGui.EndDisabled();
            ImGui.SameLine();
            if (ImGui.Selectable(
                    $"{index + 1:00}  {MeterSlotPresentation.Label(slot.Metric, text)}##slot-{slot.Id}",
                    string.Equals(selectedSlotId, slot.Id, StringComparison.OrdinalIgnoreCase)))
            {
                selectedSlotId = slot.Id;
            }
            if (!canEnable && slot.Metric == MeterSlotMetric.Fflogs && ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(text.Get(
                    "请先在设置页启用 FFLogs 在线估算。",
                    "Enable FFLogs online estimates in Settings first."));
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
        ImGui.TextColored(Gold, text.Get("真实页面预览", "Live page preview"));
        ImGui.TextDisabled(KindDescription(selectedKind));
        ImGui.Separator();
        var slots = profile.Slots.Where(slot => slot.Visible && CanUseMetric(slot.Metric)).ToArray();
        if (slots.Length == 0)
        {
            ImGui.TextDisabled(text.Get("当前没有启用的槽位。", "No enabled slots."));
            return false;
        }

        var origin = ImGui.GetCursorScreenPos();
        var width = Math.Max(220, ImGui.GetContentRegionAvail().X);
        var usedHeight = selectedKind switch
        {
            MeterWindowKind.Horizontal => DrawHorizontalPreview(slots, origin, width),
            MeterWindowKind.RoleSplit => DrawRoleSplitPreview(slots, origin, width),
            _ => DrawClassicPreview(slots, origin, width),
        };

        ImGui.SetCursorScreenPos(origin + new Vector2(0, usedHeight + 8));
        return false;
    }

    private float DrawClassicPreview(
        IReadOnlyList<MeterSlotDefinition> slots,
        Vector2 origin,
        float width)
    {
        const float gap = 7;
        const float headerHeight = 36;
        var playerSlots = slots.Where(static slot =>
            slot.Metric is not MeterSlotMetric.TotalDamage and not MeterSlotMetric.TotalHealing).ToArray();
        var metricCount = playerSlots.Count(static slot =>
            slot.Metric is not MeterSlotMetric.Rank and not MeterSlotMetric.PlayerIdentity);
        var tileHeight = Math.Max(70, 38 + (metricCount * 21));
        var tileWidth = Math.Max(104, (width - gap) / 2);
        var panelHeight = headerHeight + (tileHeight * 2) + gap +
                          (slots.Any(IsTeamSummarySlot) ? 34 : 0) + 10;
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(origin, origin + new Vector2(width, panelHeight), ImGui.GetColorU32(Navy), 6);
        drawList.AddRect(origin, origin + new Vector2(width, panelHeight), ImGui.GetColorU32(new Vector4(Gold.X, Gold.Y, Gold.Z, 0.38f)), 6);
        drawList.AddText(origin + new Vector2(9, 9), ImGui.GetColorU32(Vector4.One), text.Get("DPS 榜 · 本队 8 人", "DPS · Party 8"));
        DrawPreviewHeaderIcons(origin + new Vector2(width - 54, 7), drawList);

        var first = origin + new Vector2(0, headerHeight);
        DrawClassicPreviewTile(playerSlots, first, new Vector2(tileWidth, tileHeight), "01", "自己", "PLD", true);
        DrawClassicPreviewTile(playerSlots, first + new Vector2(tileWidth + gap, 0), new Vector2(tileWidth, tileHeight), "02", "队友 A", "WAR", false);
        DrawClassicPreviewTile(playerSlots, first + new Vector2(0, tileHeight + gap), new Vector2(tileWidth, tileHeight), "03", "队友 B", "WHM", false);

        if (slots.Any(IsTeamSummarySlot))
        {
            DrawPreviewSummary(
                slots.Where(IsTeamSummarySlot).ToArray(),
                origin + new Vector2(6, panelHeight - 38),
                new Vector2(width - 12, 30),
                "classic-summary");
        }
        return panelHeight;
    }

    private void DrawClassicPreviewTile(
        IReadOnlyList<MeterSlotDefinition> slots,
        Vector2 start,
        Vector2 size,
        string rank,
        string playerName,
        string job,
        bool selectable)
    {
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(start, start + size, ImGui.GetColorU32(NavyRaised), 5);
        drawList.AddRectFilled(start, start + new Vector2(size.X * 0.76f, size.Y), ImGui.GetColorU32(new Vector4(0.30f, 0.55f, 0.80f, 0.13f)), 5);
        var y = start.Y + 5;
        var rankSlot = slots.FirstOrDefault(static slot => slot.Metric == MeterSlotMetric.Rank);
        var identitySlot = slots.FirstOrDefault(static slot => slot.Metric == MeterSlotMetric.PlayerIdentity);
        var cursorX = start.X + 5;
        if (rankSlot is not null)
        {
            var rankSize = new Vector2(25, 26);
            if (selectable)
            {
                DrawPreviewSlot(rankSlot, new Vector2(cursorX, y), rankSize, "classic-rank", rank, false);
            }
            else
            {
                drawList.AddText(new Vector2(cursorX + 7, y + 5), ImGui.GetColorU32(IceBlue), rank);
            }
            cursorX += rankSize.X + 3;
        }
        if (identitySlot is not null)
        {
            var identitySize = new Vector2(Math.Max(36, start.X + size.X - cursorX - 5), 26);
            if (selectable)
            {
                DrawPreviewSlot(identitySlot, new Vector2(cursorX, y), identitySize, "classic-identity", playerName, true, job);
            }
            else
            {
                DrawPreviewIdentity(new Vector2(cursorX + 3, y + 3), identitySize.X - 6, playerName, job, drawList);
            }
        }

        y += 31;
        foreach (var slot in slots.Where(static slot =>
                     slot.Metric is not MeterSlotMetric.Rank and not MeterSlotMetric.PlayerIdentity))
        {
            if (selectable)
            {
                DrawPreviewSlot(slot, new Vector2(start.X + 5, y), new Vector2(size.X - 10, 18), $"classic-{slot.Id}", SampleValue(slot.Metric), false);
            }
            else
            {
                DrawPreviewMetricText(slot.Metric, new Vector2(start.X + 8, y + 2), size.X - 16, SampleValue(slot.Metric), drawList);
            }
            y += 21;
        }
    }

    private float DrawHorizontalPreview(
        IReadOnlyList<MeterSlotDefinition> slots,
        Vector2 origin,
        float width)
    {
        const float gap = 8;
        const float headerHeight = 38;
        var playerSlots = slots.Where(static slot =>
            slot.Metric is not MeterSlotMetric.TotalDamage and not MeterSlotMetric.TotalHealing).ToArray();
        var metricCount = playerSlots.Count(static slot =>
            slot.Metric is not MeterSlotMetric.Rank and not MeterSlotMetric.PlayerIdentity);
        var moduleHeight = Math.Max(78, 38 + (metricCount * 20));
        var moduleWidth = Math.Max(92, (width - (gap * 2)) / 3);
        var totalHeight = headerHeight + moduleHeight + (slots.Any(IsTeamSummarySlot) ? 34 : 0);
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddText(origin + new Vector2(2, 7), ImGui.GetColorU32(IceBlue), "DPS榜    A   B   C");
        drawList.AddText(origin + new Vector2(width - 47, 7), ImGui.GetColorU32(new Vector4(0.72f, 0.75f, 0.80f, 1)), "8 MAX");
        drawList.AddLine(origin + new Vector2(0, 30), origin + new Vector2(width, 30), ImGui.GetColorU32(new Vector4(IceBlue.X, IceBlue.Y, IceBlue.Z, 0.42f)));

        var modulesStart = origin + new Vector2(0, headerHeight);
        DrawHorizontalPreviewModule(playerSlots, modulesStart, new Vector2(moduleWidth, moduleHeight), "PLD", "自己", true);
        DrawHorizontalPreviewModule(playerSlots, modulesStart + new Vector2(moduleWidth + gap, 0), new Vector2(moduleWidth, moduleHeight), "SAM", "队友 A", false);
        DrawHorizontalPreviewModule(playerSlots, modulesStart + new Vector2((moduleWidth + gap) * 2, 0), new Vector2(moduleWidth, moduleHeight), "WHM", "队友 B", false);

        if (slots.Any(IsTeamSummarySlot))
        {
            DrawPreviewSummary(
                slots.Where(IsTeamSummarySlot).ToArray(),
                origin + new Vector2(0, totalHeight - 30),
                new Vector2(width, 28),
                "horizontal-summary");
        }
        return totalHeight;
    }

    private void DrawHorizontalPreviewModule(
        IReadOnlyList<MeterSlotDefinition> slots,
        Vector2 start,
        Vector2 size,
        string job,
        string playerName,
        bool selectable)
    {
        var drawList = ImGui.GetWindowDrawList();
        // Horizontal runtime has no card background; only the progress accent remains visible.
        drawList.AddLine(start + new Vector2(0, size.Y - 2), start + new Vector2(size.X * 0.82f, size.Y - 2), ImGui.GetColorU32(new Vector4(IceBlue.X, IceBlue.Y, IceBlue.Z, 0.60f)), 3);
        var y = start.Y + 3;
        foreach (var slot in slots)
        {
            var height = slot.Metric == MeterSlotMetric.PlayerIdentity ? 28 : 18;
            if (selectable)
            {
                DrawPreviewSlot(slot, new Vector2(start.X, y), new Vector2(size.X, height), $"horizontal-{slot.Id}", slot.Metric == MeterSlotMetric.PlayerIdentity ? playerName : SampleValue(slot.Metric), slot.Metric == MeterSlotMetric.PlayerIdentity, job);
            }
            else if (slot.Metric == MeterSlotMetric.PlayerIdentity)
            {
                DrawPreviewIdentity(new Vector2(start.X + 2, y + 2), size.X - 4, playerName, job, drawList);
            }
            else
            {
                DrawPreviewMetricText(slot.Metric, new Vector2(start.X + 3, y + 1), size.X - 6, SampleValue(slot.Metric), drawList);
            }
            y += height + 2;
        }
    }

    private float DrawRoleSplitPreview(
        IReadOnlyList<MeterSlotDefinition> slots,
        Vector2 origin,
        float width)
    {
        const float gap = 8;
        var panelWidth = Math.Max(104, (width - gap) / 2);
        var metrics = slots.Where(static slot =>
            slot.Metric is not MeterSlotMetric.TotalDamage and not MeterSlotMetric.TotalHealing).ToArray();
        var panelHeight = Math.Max(145, 49 + (metrics.Length * 20) + 32);
        DrawRolePreviewPanel(metrics, slots, origin, new Vector2(panelWidth, panelHeight), false, "role-damage");
        DrawRolePreviewPanel(metrics, slots, origin + new Vector2(panelWidth + gap, 0), new Vector2(panelWidth, panelHeight), true, "role-healer");
        return panelHeight;
    }

    private void DrawRolePreviewPanel(
        IReadOnlyList<MeterSlotDefinition> playerSlots,
        IReadOnlyList<MeterSlotDefinition> allSlots,
        Vector2 start,
        Vector2 size,
        bool healer,
        string instanceId)
    {
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(start, start + size, ImGui.GetColorU32(Navy), 6);
        drawList.AddRect(start, start + size, ImGui.GetColorU32(healer ? new Vector4(0.32f, 0.78f, 0.48f, 0.72f) : new Vector4(Gold.X, Gold.Y, Gold.Z, 0.62f)), 6);
        var title = healer ? "H  HPS榜" : "D/T  DPS榜";
        drawList.AddText(start + new Vector2(8, 8), ImGui.GetColorU32(healer ? new Vector4(0.42f, 0.91f, 0.60f, 1) : Gold), title);
        drawList.AddText(start + new Vector2(size.X - 19, 8), ImGui.GetColorU32(Vector4.One), "⌃");
        drawList.AddLine(start + new Vector2(5, 34), start + new Vector2(size.X - 5, 34), ImGui.GetColorU32(new Vector4(1, 1, 1, 0.20f)));
        var y = start.Y + 40;
        foreach (var previewSlot in ResolveRolePreviewSlots(playerSlots, healer))
        {
            var slot = previewSlot.Slot;
            var metric = previewSlot.Metric;
            var height = metric == MeterSlotMetric.PlayerIdentity ? 27 : 18;
            DrawPreviewSlot(slot, new Vector2(start.X + 5, y), new Vector2(size.X - 10, height), $"{instanceId}-{slot.Id}", metric == MeterSlotMetric.PlayerIdentity ? (healer ? "治疗 A" : "自己") : SampleValue(metric), metric == MeterSlotMetric.PlayerIdentity, healer ? "WHM" : "PLD", metric);
            y += height + 2;
        }

        var summaryMetric = healer ? MeterSlotMetric.TotalHealing : MeterSlotMetric.TotalDamage;
        var summary = allSlots.FirstOrDefault(slot => slot.Metric == summaryMetric);
        if (summary is not null)
        {
            DrawPreviewSummary([summary], start + new Vector2(5, size.Y - 29), new Vector2(size.X - 10, 24), instanceId + "-summary");
        }
    }

    private void DrawPreviewSlot(
        MeterSlotDefinition slot,
        Vector2 start,
        Vector2 size,
        string instanceId,
        string value,
        bool identity,
        string job = "PLD",
        MeterSlotMetric? displayMetric = null)
    {
        ImGui.SetCursorScreenPos(start);
        ImGui.InvisibleButton($"preview-slot-{instanceId}", size);
        if (ImGui.IsItemClicked())
        {
            selectedSlotId = slot.Id;
        }
        var selected = string.Equals(selectedSlotId, slot.Id, StringComparison.OrdinalIgnoreCase);
        var drawList = ImGui.GetWindowDrawList();
        if (selected || ImGui.IsItemHovered())
        {
            drawList.AddRectFilled(start, start + size, ImGui.GetColorU32(new Vector4(Gold.X, Gold.Y, Gold.Z, selected ? 0.16f : 0.08f)), 3);
        }
        drawList.AddRect(start, start + size, ImGui.GetColorU32(new Vector4(Gold.X, Gold.Y, Gold.Z, selected ? 1 : 0.70f)), 3);
        if (selected)
        {
            drawList.AddRect(start + new Vector2(1), start + size - new Vector2(1), ImGui.GetColorU32(Gold), 2);
        }

        if (identity)
        {
            DrawPreviewIdentity(start + new Vector2(4, 2), size.X - 8, value, job, drawList);
            return;
        }
        DrawPreviewMetricText(displayMetric ?? slot.Metric, start + new Vector2(4, 2), size.X - 8, value, drawList);
    }

    private void DrawPreviewIdentity(
        Vector2 start,
        float width,
        string playerName,
        string job,
        ImDrawListPtr drawList)
    {
        const float iconSize = 20;
        var texture = jobIcons.Get(configuration.Meter.JobDisplayStyle, job);
        if (texture is not null)
        {
            drawList.AddImage(texture.GetWrapOrEmpty().Handle, start, start + new Vector2(iconSize));
        }
        else
        {
            drawList.AddText(start + new Vector2(1, 2), ImGui.GetColorU32(IceBlue), JobDisplayFormatter.FormatText(job, configuration.Meter.JobDisplayStyle));
        }
        var rendered = MeterSlotPresentation.TrimToWidth(playerName, Math.Max(12, width - iconSize - 5));
        drawList.AddText(start + new Vector2(iconSize + 5, 2), ImGui.GetColorU32(Vector4.One), rendered);
    }

    private void DrawPreviewMetricText(
        MeterSlotMetric metric,
        Vector2 start,
        float width,
        string value,
        ImDrawListPtr drawList)
    {
        var label = MeterSlotPresentation.TrimToWidth(MeterSlotPresentation.Label(metric, text), Math.Max(16, width * 0.48f));
        drawList.AddText(start, ImGui.GetColorU32(new Vector4(0.70f, 0.74f, 0.80f, 1)), label);
        var rendered = MeterSlotPresentation.TrimToWidth(value, Math.Max(16, width * 0.48f));
        var valueSize = ImGui.CalcTextSize(rendered);
        var color = metric == MeterSlotMetric.Hps
            ? new Vector4(0.42f, 0.91f, 0.60f, 1)
            : metric is MeterSlotMetric.Dps or MeterSlotMetric.Rdps
                ? IceBlue
                : Vector4.One;
        drawList.AddText(new Vector2(start.X + width - valueSize.X, start.Y), ImGui.GetColorU32(color), rendered);
    }

    private void DrawPreviewSummary(
        IReadOnlyList<MeterSlotDefinition> summaries,
        Vector2 start,
        Vector2 size,
        string instanceId)
    {
        if (summaries.Count == 0)
        {
            return;
        }
        var cellWidth = size.X / summaries.Count;
        for (var index = 0; index < summaries.Count; index++)
        {
            var slot = summaries[index];
            DrawPreviewSlot(slot, start + new Vector2(index * cellWidth, 0), new Vector2(cellWidth, size.Y), $"{instanceId}-{slot.Id}", SampleValue(slot.Metric), false);
        }
    }

    private static bool IsTeamSummarySlot(MeterSlotDefinition slot)
        => slot.Metric is MeterSlotMetric.TotalDamage or MeterSlotMetric.TotalHealing;

    private static IReadOnlyList<PreviewMetric> ResolveRolePreviewSlots(
        IReadOnlyList<MeterSlotDefinition> slots,
        bool healer)
    {
        if (!healer)
        {
            return slots.Where(static slot => slot.Metric != MeterSlotMetric.Hps)
                .Select(static slot => new PreviewMetric(slot, slot.Metric))
                .ToArray();
        }

        var leadingDamage = slots.FirstOrDefault(static slot =>
            slot.Metric is MeterSlotMetric.Dps or MeterSlotMetric.Rdps);
        return slots.Where(slot => leadingDamage is null || slot.Metric != MeterSlotMetric.Hps)
            .Select(slot => new PreviewMetric(
                slot,
                ReferenceEquals(slot, leadingDamage) ? MeterSlotMetric.Hps : slot.Metric))
            .ToArray();
    }

    private static string SampleValue(MeterSlotMetric metric) => metric switch
    {
        MeterSlotMetric.Rank => "1",
        MeterSlotMetric.Fflogs => "92",
        MeterSlotMetric.Dps => "12,846",
        MeterSlotMetric.Rdps => "13,102",
        MeterSlotMetric.Hps => "8,640",
        MeterSlotMetric.DamagePercent => "24.8%",
        MeterSlotMetric.TotalDamage => "8.42M",
        MeterSlotMetric.TotalHealing => "3.16M",
        MeterSlotMetric.HighestDamageAction or MeterSlotMetric.HighestDamage => "爆发击 128K",
        MeterSlotMetric.Deaths => "0",
        MeterSlotMetric.CriticalHitPercent => "31.2%",
        MeterSlotMetric.DirectHitPercent => "22.6%",
        MeterSlotMetric.CriticalDirectHitPercent => "11.8%",
        _ => "--",
    };

    private static void DrawPreviewHeaderIcons(Vector2 start, ImDrawListPtr drawList)
    {
        var color = ImGui.GetColorU32(new Vector4(0.80f, 0.83f, 0.88f, 1));
        drawList.AddTriangleFilled(start + new Vector2(3, 11), start + new Vector2(9, 4), start + new Vector2(8, 11), color);
        drawList.AddTriangleFilled(start + new Vector2(9, 11), start + new Vector2(15, 4), start + new Vector2(14, 11), color);
        drawList.AddLine(start + new Vector2(27, 6), start + new Vector2(32, 11), color, 2);
        drawList.AddLine(start + new Vector2(32, 11), start + new Vector2(37, 6), color, 2);
    }

    private sealed record PreviewMetric(MeterSlotDefinition Slot, MeterSlotMetric Metric);

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
        if (selectedKind is MeterWindowKind.Classic or MeterWindowKind.Horizontal)
        {
            var itemWidth = profile.ItemWidth;
            ImGui.SetNextItemWidth(-1);
            var widthLabel = selectedKind == MeterWindowKind.Classic
                ? text.Get("玩家方块宽度", "Player tile width")
                : text.Get("模块宽度", "Module width");
            if (ImGui.SliderFloat(widthLabel, ref itemWidth, 140, 420, "%.0f"))
            {
                profile.ItemWidth = itemWidth;
                changed = true;
            }
        }
        if (selectedKind == MeterWindowKind.Classic)
        {
            var backgroundOpacity = configuration.Meter.BackgroundOpacity;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.SliderFloat(
                    text.Get("背景透明度", "Background opacity"),
                    ref backgroundOpacity,
                    0,
                    1,
                    "%.2f"))
            {
                configuration.Meter.BackgroundOpacity = backgroundOpacity;
                changed = true;
            }
        }
        var localPlayerColor = configuration.Meter.LocalPlayerColor;
        if (ImGui.ColorEdit4(text.Get("自己的强调色", "Local player accent"), ref localPlayerColor))
        {
            configuration.Meter.LocalPlayerColor = localPlayerColor;
            changed = true;
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
            foreach (var metric in MeterSlotDefaults.EditableMetrics.Where(CanUseMetric))
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
        if (slot.Metric == MeterSlotMetric.PlayerIdentity)
        {
            var jobStyle = configuration.Meter.JobDisplayStyle;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.BeginCombo(
                    text.Get("职业显示方式", "Job display"),
                    JobDisplayFormatter.Label(jobStyle, text)))
            {
                foreach (var style in Enum.GetValues<JobDisplayStyle>())
                {
                    if (ImGui.Selectable(
                            JobDisplayFormatter.Label(style, text),
                            style == jobStyle))
                    {
                        configuration.Meter.JobDisplayStyle = style;
                        changed = true;
                    }
                }
                ImGui.EndCombo();
            }
        }
        var visible = slot.Visible;
        var canUseSelectedSlot = CanUseMetric(slot.Metric);
        ImGui.BeginDisabled(!canUseSelectedSlot);
        if (ImGui.Checkbox(text.Get("使用这个槽位", "Use this slot"), ref visible))
        {
            slot.Visible = visible;
            changed = true;
        }
        ImGui.EndDisabled();
        if (!canUseSelectedSlot && slot.Metric == MeterSlotMetric.Fflogs)
        {
            ImGui.TextWrapped(text.Get(
                "先在设置页启用 FFLogs 在线估算，才能打开这个列。原有配置会保留。",
                "Enable FFLogs online estimates in Settings before showing this column. Its saved configuration is preserved."));
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
            "最高伤害会自动截短；把鼠标移到统计项上可查看完整技能名和数值。",
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
        if (meter.ActiveWindowKind == MeterWindowKind.Classic)
        {
            meter.SortMode = profile.SortMode;
        }
        bool Has(MeterSlotMetric metric) => profile.Slots.Any(slot => slot.Visible && slot.Metric == metric);
        meter.ShowRank = Has(MeterSlotMetric.Rank);
        var showPlayerIdentity = Has(MeterSlotMetric.PlayerIdentity);
        meter.ShowJob = showPlayerIdentity;
        meter.ShowPlayerName = showPlayerIdentity;
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
        meter.ShowFflogs = Has(MeterSlotMetric.Fflogs);
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
        => MeterSlotDefaults.EditableMetrics.Where(CanUseMetric)
            .FirstOrDefault(metric => profile.Slots.All(slot => slot.Metric != metric));

    private bool CanUseMetric(MeterSlotMetric metric)
        => metric != MeterSlotMetric.Fflogs ||
           (selectedKind == MeterWindowKind.Classic && configuration.Fflogs.Enabled);

    private void SetEditingProfile(MeterWindowKind? kind)
    {
        configuration.Meter.ClassicWindow.IsEditing = kind == MeterWindowKind.Classic;
        configuration.Meter.HorizontalWindow.IsEditing = kind == MeterWindowKind.Horizontal;
        configuration.Meter.RoleSplitWindow.IsEditing = kind == MeterWindowKind.RoleSplit;
    }

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
                "D/T 与治疗是两个独立窗口，黄色框内均可点击调整。",
                "D/T and healer are separate windows; click any yellow-framed item to adjust it."),
            _ => text.Get(
                "玩家方块从左到右排列；总伤害与总治疗固定在队伍汇总区。",
                "Player tiles flow left to right; team damage and healing stay in the summary area."),
        };

}

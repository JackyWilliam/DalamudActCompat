using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using DalamudActCompat.UI;
using DalamudActCompat.UI.Kit;

namespace DalamudActCompat.Meter;

// 战斗统计 · 自定义布局 in the 1.0 layout (screens.js meterEditor boards): a 48 px header (title,
// feedback, help, close), template segments with the enabled state, the four window switches as
// chips, three panels (槽位 / 页面预览 / 属性) and a bottom bar with 取消 and 保存. The preview
// itself is the live meter drawing and stays as it is.
public sealed partial class MeterStyleEditorWindow
{
    // Set by the plugin: feedback and help open in the control center.
    internal Action? OpenFeedback { get; set; }
    internal Action? OpenHelp { get; set; }
    internal Action? OpenMeterSettings { get; set; }

    private const string ExitDialog = "##meter-editor-exit";
    private int propertiesTab;
    private bool stylePushed;

    private readonly WindowFade fade = new();

    public override void OnOpen() => fade.Opened();

    public override void PreDraw()
    {
        headerDrag.PrepareNextWindow();
        Flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 10);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        // Indigo of the DACT logo, like the control center; skins substitute their own border.
        DactTheme.PushStyleColor(ImGuiCol.Border, new Vector4(.208f, .251f, .420f, 1));
        ImGui.PushStyleColor(ImGuiCol.ResizeGrip, Vector4.Zero);
        stylePushed = true;
        Flags = DactTheme.WindowFlags(Flags);
        // Pushed last so it nests inside this window's other style pushes (WindowFade).
        if (!fade.Push()) IsOpen = false;
    }

    public override void PostDraw()
    {
        fade.Pop();
        if (!stylePushed) return;
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(3);
        stylePushed = false;
    }

    public override void Draw()
    {
        // Preview actors are synthetic UI content, not user combat logs. Refresh
        // them when language changes without touching the edited meter settings.
        if (previewLanguage != text.Language)
        {
            previewLanguage = text.Language;
            previewEncounter = CreatePreviewEncounter(text);
            previewRows = CreatePreviewRows(previewEncounter);
        }
        WindowName = text.Get(
            "战斗统计布局编辑器###DalamudActCompatMeterStyleEditor",
            "Combat Meter Layout Editor###DalamudActCompatMeterStyleEditor");

        ControlCenterWindow.PushTheme();
        try
        {
            DactUi.BeginFrame();
            DactTheme.DrawGameWindow();
            var s = DactUi.S;
            var min = ImGui.GetWindowPos();
            var size = ImGui.GetWindowSize();
            DactUi.HostMin = min;
            DactUi.HostMax = min + size;
            var line = Math.Max(1, s);
            var draw = ImGui.GetWindowDrawList();
            DrawEditorHeader(min, size.X, 48 * s);
            draw.AddRectFilled(new Vector2(min.X, min.Y + 48 * s), new Vector2(min.X + size.X, min.Y + 48 * s + line), DactUi.U32(DactUi.DividerColor));

            var bodyTop = min.Y + 48 * s + line;
            var footerHeight = 58 * s;
            ImGui.SetCursorScreenPos(new Vector2(min.X, bodyTop));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(18, 14) * s);
            ImGui.PushStyleColor(ImGuiCol.ChildBg, Vector4.Zero);
            var changed = false;
            if (ImGui.BeginChild("meter-editor-body", new Vector2(-1, min.Y + size.Y - bodyTop - footerHeight), false,
                    ImGuiWindowFlags.AlwaysUseWindowPadding | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
            {
                ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8, 12) * s);
                changed |= DrawTemplateRow();
                changed |= DrawWindowChips(CurrentProfile);
                changed |= DrawPanels();
                ImGui.PopStyleVar();
            }
            ImGui.EndChild();
            ImGui.PopStyleColor();
            ImGui.PopStyleVar();
            if (DrawFooter(min, size, footerHeight)) return;

            // The exit question dims this window only, under the dialog.
            var scrim = DactUi.DialogOpacity(ExitDialog);
            if (scrim > 0)
            {
                ImGui.SetCursorPos(Vector2.Zero);
                ImGui.BeginChild("##meter-editor-scrim", size, false,
                    ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoSavedSettings);
                ImGui.GetWindowDrawList().AddRectFilled(min, min + size, DactUi.U32(new Vector4(.016f, .024f, .035f, .62f * scrim)), 10 * s);
                ImGui.EndChild();
            }
            if (DrawExitConfirmation()) return;

            if (changed)
            {
                NormalizeCurrentSlots();
                SynchronizeClassicSettings();
            }
        }
        finally
        {
            ControlCenterWindow.PopTheme();
        }
    }

    // ---- Header

    private void DrawEditorHeader(Vector2 min, float width, float height)
    {
        var s = DactUi.S;
        var draw = ImGui.GetWindowDrawList();
        var palette = DactTheme.Palette;
        var logo = new Vector2(min.X + 14 * s, min.Y + (height - 26 * s) * .5f);
        draw.AddImageRounded(logoTexture.GetWrapOrEmpty().Handle, logo, logo + new Vector2(26 * s), Vector2.Zero, Vector2.One,
            ImGui.GetColorU32(Vector4.One), 6 * s);
        var title = text.Get("战斗统计 · 自定义布局", "Combat Meter · Custom layout");
        var x = logo.X + 36 * s;
        DactUi.DrawText(draw, new Vector2(x, min.Y), height, title, palette.Text);
        x += DactUi.TextWidth(title) + 10 * s;
        var subtitle = text.Get("改动实时预览", "Live preview");
        DactUi.DrawText(draw, new Vector2(x, min.Y), height, subtitle, DactUi.HintColor, DactFonts.Small);
        var dragLeft = x + DactUi.TextWidth(subtitle, DactFonts.Small) + 8 * s;

        // Right: feedback, help, close (10 px apart, like the control center).
        var right = min.X + width - 10 * s - 28 * s;
        var buttonTop = min.Y + (height - 28 * s) * .5f;
        ImGui.SetCursorScreenPos(new Vector2(right, buttonTop));
        if (DactUi.IconButton("##meter-editor-close", "close", text.Get("关闭", "Close"))) RequestExitConfirmation();
        right -= 38 * s;
        ImGui.SetCursorScreenPos(new Vector2(right, buttonTop));
        if (DactUi.IconButton("##meter-editor-help", "help", text.Get("帮助", "Help"))) OpenHelp?.Invoke();
        right -= 38 * s;
        ImGui.SetCursorScreenPos(new Vector2(right, buttonTop));
        if (DactUi.IconButton("##meter-editor-feedback", "feedback", text.Get("反馈问题或建议", "Send feedback"))) OpenFeedback?.Invoke();

        // No search field here (user decision): the rest of the header is the drag handle.
        HeaderDrag(dragLeft, right - 8 * s, min.Y, height, "##meter-editor-drag");
        headerDrag.ObserveHandle(ImGui.GetFrameCount(), ImGui.GetMousePos(), ImGui.GetWindowPos(), headerDragClicked, headerDragActive);
        headerDragClicked = headerDragActive = false;
    }

    private bool headerDragClicked;
    private bool headerDragActive;

    private void HeaderDrag(float left, float right, float top, float height, string id)
    {
        if (right <= left) return;
        ImGui.SetCursorScreenPos(new Vector2(left, top));
        ImGui.InvisibleButton(id, new Vector2(right - left, height));
        headerDragClicked |= ImGui.IsItemClicked(ImGuiMouseButton.Left);
        headerDragActive |= ImGui.IsItemActive();
    }

    // ---- Template row and window switches

    private bool DrawTemplateRow()
    {
        var s = DactUi.S;
        var changed = false;
        var kind = (int)selectedKind;
        if (DactUi.Segmented("meter-editor-kind", [text.Get("经典榜", "Classic"), text.Get("横版", "Horizontal"), text.Get("职能分栏", "Role split")], ref kind))
        {
            SetEditingProfile(null);
            selectedKind = (MeterWindowKind)kind;
            SetEditingProfile(selectedKind);
            selectedSlotId = null;
            EnsureSelectedSlot(CurrentSlots);
            ResetPreviewInteraction();
        }
        var rowTop = ImGui.GetItemRectMin().Y;
        ImGui.SameLine(0, 12 * s);
        var profile = CurrentProfile;
        if (profile.IsEnabled)
        {
            ImGui.SetCursorScreenPos(new Vector2(ImGui.GetCursorScreenPos().X, rowTop + 5 * s));
            DactUi.Pill(text.Get("当前模板已启用", "This template is enabled"), PillTone.Ok);
        }
        else
        {
            ImGui.SetCursorScreenPos(new Vector2(ImGui.GetCursorScreenPos().X, rowTop + 1 * s));
            if (DactUi.Button(text.Get("启用此模板", "Enable this template") + "##meter-editor-enable", ButtonKind.Primary))
            {
                configuration.Meter.ActivateWindow(selectedKind);
                changed = true;
            }
        }
        ImGui.SameLine(0, 12 * s);
        ImGui.SetCursorScreenPos(new Vector2(ImGui.GetCursorScreenPos().X, rowTop));
        DactUi.DrawText(ImGui.GetWindowDrawList(), ImGui.GetCursorScreenPos(), 32 * s,
            text.Get("启用后会自动停用另外两个模板。", "Enabling this template disables the other two."), DactUi.HintColor, DactFonts.Small);
        ImGui.Dummy(new Vector2(1, 32 * s));
        return changed;
    }

    private bool DrawWindowChips(MeterWindowProfile profile)
    {
        var s = DactUi.S;
        var changed = false;
        var locked = profile.IsLocked;
        if (Chip("lock", text.Get("锁定", "Lock"), ref locked))
        {
            profile.IsLocked = locked;
            changed = true;
        }
        ImGui.SameLine(0, 8 * s);
        var clickThrough = profile.ClickThroughWhenLocked;
        if (Chip("click-through", text.Get("锁定时鼠标穿透", "Click-through when locked"), ref clickThrough, !profile.IsLocked))
        {
            profile.ClickThroughWhenLocked = clickThrough;
            changed = true;
        }
        ImGui.SameLine(0, 8 * s);
        var autoHide = profile.AutoHideOutOfCombat;
        if (Chip("auto-hide", text.Get("脱战隐藏", "Hide out of combat"), ref autoHide))
        {
            profile.AutoHideOutOfCombat = autoHide;
            changed = true;
        }
        if (selectedKind == MeterWindowKind.Classic)
        {
            ImGui.SameLine(0, 8 * s);
            var compact = configuration.Meter.CompactMode;
            if (Chip("self-only", text.Get("只显示自己", "Show self only"), ref compact))
            {
                configuration.Meter.CompactMode = compact;
                changed = true;
            }
            if (ImGui.IsItemHovered())
                DactUi.Tooltip(text.Get("只影响实际统计窗口；页面预览始终展示完整示例队伍。",
                    "Applies to the live meter only; the preview always shows the full sample party."));
        }
        return changed;
    }

    // A switch with its label in a rounded chip; the whole chip toggles. The switch is the shared
    // DactUi one (skin variants, hover glow), and the chip's own hover eases like other controls.
    private static bool Chip(string id, string label, ref bool value, bool disabled = false)
    {
        var s = DactUi.S;
        var width = 8 * s + 34 * s + 8 * s + DactUi.TextWidth(label) + 12 * s;
        var min = ImGui.GetCursorScreenPos();
        if (disabled) ImGui.BeginDisabled();
        var clicked = ImGui.InvisibleButton("##chip-" + id, new Vector2(width, 28 * s));
        if (disabled) ImGui.EndDisabled();
        if (clicked && !disabled) value = !value;
        var key = ImGuiP.GetItemID();
        var hover = DactMotion.Float(ImGui.GetID("##chip-hover-" + id), ImGui.IsItemHovered() && !disabled ? 1 : 0, DactMotion.Fast);
        var on = DactMotion.Float(key, value ? 1 : 0, DactMotion.Normal);
        var draw = ImGui.GetWindowDrawList();
        var palette = DactTheme.Palette;
        var alpha = disabled ? .45f : 1;
        var fill = Vector4.Lerp(DactUi.Field, Vector4.Lerp(DactUi.Field, palette.Hover, .6f), hover);
        var border = Vector4.Lerp(DactUi.FieldBorder, palette.Accent with { W = .45f }, hover * .6f);
        draw.AddRectFilled(min, min + new Vector2(width, 28 * s), DactUi.U32(fill with { W = fill.W * alpha }), 14 * s);
        draw.AddRect(min, min + new Vector2(width, 28 * s), DactUi.U32(border with { W = border.W * alpha }), 14 * s, ImDrawFlags.None, Math.Max(1, s));
        DactUi.DrawToggle(draw, min + new Vector2(8 * s, 4 * s), on, hover, disabled);
        DactUi.DrawText(draw, min + new Vector2(50 * s, 0), 28 * s, label, palette.Text with { W = alpha });
        return clicked && !disabled;
    }

    // ---- The three panels

    private bool DrawPanels()
    {
        var s = DactUi.S;
        var changed = false;
        var available = ImGui.GetContentRegionAvail();
        var gap = 14 * s;
        var left = 282 * s;
        var right = 298 * s;
        var height = Math.Max(240 * s, available.Y);
        var middle = Math.Max(240 * s, available.X - left - right - gap * 2);

        if (DactUi.BeginPanel("meter-editor-slots", new Vector2(left, height), 14, 10)) changed |= DrawSlotPanel(CurrentSlots);
        DactUi.EndPanel();
        ImGui.SameLine(0, gap);
        if (DactUi.BeginPanel("meter-editor-preview", new Vector2(middle, height), 14, 10)) changed |= DrawPreviewPanel(CurrentProfile);
        DactUi.EndPanel();
        ImGui.SameLine(0, gap);
        if (DactUi.BeginPanel("meter-editor-properties", new Vector2(right, height), 14, 12)) changed |= DrawPropertiesPanel(CurrentProfile, CurrentSlots);
        DactUi.EndPanel();
        return changed;
    }

    private bool DrawSlotPanel(List<MeterSlotDefinition> slots)
    {
        var s = DactUi.S;
        var changed = false;
        DactUi.Text(text.Get("槽位", "Slots"), DactTheme.Palette.Gold);
        if (selectedKind == MeterWindowKind.Classic && configuration.Meter.ClassicAllianceView)
        {
            DactUi.Hint(text.Get(
                "24 人本使用固定紧凑条，只显示职业 / 名字和当前 DPS/HPS，不提供槽位调整。切回 8 人本可编辑经典表格列。",
                "The 24-player compact row is fixed to job/name and the current DPS/HPS value. Switch to 8-player mode to edit classic table columns."));
            return false;
        }
        DactUi.Hint(text.Get(
            "勾选要显示的槽位；点中一行（或预览里的那一列）后在右栏编辑。拖动可改顺序。",
            "Check the slots to show; select a row (or that column in the preview) to edit it on the right. Drag to reorder."));
        if (selectedKind == MeterWindowKind.RoleSplit)
        {
            var group = (int)selectedRoleSplitGroup;
            if (DactUi.Segmented("meter-editor-role-group", [text.Get("D / T 榜槽位", "D / T columns"), text.Get("H 榜槽位", "H columns")], ref group,
                    ImGui.GetContentRegionAvail().X))
                SelectRoleSplitGroup((RoleSplitGroup)group);
        }

        // The list scrolls on its own; 添加槽位 and 恢复默认槽位 stay at the panel bottom.
        var listHeight = Math.Max(60 * s, ImGui.GetContentRegionAvail().Y - 30 * s - ImGui.GetStyle().ItemSpacing.Y);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Vector4.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        if (ImGui.BeginChild("meter-editor-slot-list", new Vector2(-1, listHeight), false))
        {
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0, 0));
            var width = ImGui.GetContentRegionAvail().X;
            for (var index = 0; index < slots.Count; index++) changed |= DrawSlotRow(slots, index, width);
            ImGui.PopStyleVar();
        }
        ImGui.EndChild();
        ImGui.PopStyleVar();
        ImGui.PopStyleColor();

        if (DactUi.Button(text.Get("添加槽位", "Add slot") + "##meter-editor-add", icon: "plus"))
        {
            var slot = new MeterSlotDefinition(FirstUnusedMetric(slots), 0, 0, 4, 2, MeterSlotAlignment.Left);
            slots.Add(slot);
            selectedSlotId = slot.Id;
            propertiesTab = 1;
            changed = true;
        }
        var restore = text.Get("恢复默认槽位", "Restore default slots");
        ImGui.SameLine(ImGui.GetContentRegionMax().X - DactUi.ButtonWidth(restore, ButtonKind.Ghost));
        if (DactUi.Button(restore + "##meter-editor-restore", ButtonKind.Ghost))
        {
            ReplaceCurrentSlots(DefaultSlots(selectedKind).Select(static slot => slot.Clone()).ToList());
            selectedSlotId = CurrentSlots.FirstOrDefault()?.Id;
            changed = true;
        }
        return changed;
    }

    private bool DrawSlotRow(List<MeterSlotDefinition> slots, int index, float width)
    {
        var s = DactUi.S;
        var palette = DactTheme.Palette;
        var slot = slots[index];
        var changed = false;
        var rowHeight = 30 * s;
        var min = ImGui.GetCursorScreenPos();
        var selected = string.Equals(selectedSlotId, slot.Id, StringComparison.OrdinalIgnoreCase);
        var canEnable = CanUseMetric(slot.Metric);
        var draw = ImGui.GetWindowDrawList();

        // The row selects (and drags to reorder); the checkbox on its left shows or hides the slot.
        ImGui.SetCursorScreenPos(min + new Vector2(32 * s, 0));
        if (ImGui.InvisibleButton($"##slot-row-{slot.Id}", new Vector2(width - 32 * s, rowHeight)))
        {
            selectedSlotId = slot.Id;
            propertiesTab = 1;
        }
        var rowHovered = ImGui.IsItemHovered();
        if (ImGui.IsItemActive() && !rowHovered && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            var target = index + (ImGui.GetMouseDragDelta(ImGuiMouseButton.Left).Y < 0 ? -1 : 1);
            if (target >= 0 && target < slots.Count)
            {
                (slots[index], slots[target]) = (slots[target], slots[index]);
                ImGui.ResetMouseDragDelta(ImGuiMouseButton.Left);
                changed = true;
            }
        }
        var hover = DactMotion.Float(ImGuiP.GetItemID(), rowHovered || selected ? 1 : 0, DactMotion.Fast);
        if (hover > 0) draw.AddRectFilled(min, min + new Vector2(width, rowHeight), DactUi.U32(palette.Hover with { W = (selected ? 1 : .55f) * hover }), 6 * s);
        if (selected) draw.AddRect(min, min + new Vector2(width, rowHeight), DactUi.U32(palette.Accent with { W = .8f }), 6 * s, ImDrawFlags.None, Math.Max(1, s));

        ImGui.SetCursorScreenPos(min + new Vector2(8 * s, 3 * s));
        var visible = slot.Visible;
        if (DactUi.Checkbox($"##slot-visible-{slot.Id}", ref visible, disabled: !canEnable))
        {
            slot.Visible = visible;
            changed = true;
        }
        var alpha = canEnable ? 1 : .45f;
        DactUi.DrawText(draw, min + new Vector2(36 * s, 0), rowHeight, (index + 1).ToString("00", CultureInfo.InvariantCulture), palette.Muted with { W = alpha }, DactFonts.Small);
        var label = MeterSlotPresentation.Label(slot.Metric, text);
        // A slot can carry a tag: 未启用 (FFLogs off) or 排序 (this template's DPS ranking).
        var tag = !canEnable && slot.Metric == MeterSlotMetric.Fflogs ? (text.Get("未启用", "Off"), PillTone.Off)
            : MeterSlotPresentation.TryGetDpsMetric(slot.Metric, out var dps) && CurrentProfile.DpsSortMetric == dps && slot.Visible
                ? (text.Get("排序", "Ranking"), PillTone.Warn)
                : ((string, PillTone)?)null;
        var tagWidth = tag is { } shown ? DactUi.PillWidth(shown.Item1, dot: false) + 6 * s : 0;
        DactUi.DrawText(draw, min + new Vector2(60 * s, 0), rowHeight, DactUi.Fit(label, width - 66 * s - tagWidth), palette.Text with { W = alpha });
        if (tag is { } pill) DactUi.DrawPill(draw, new Vector2(min.X + width - tagWidth, min.Y + 4 * s), pill.Item1, pill.Item2, dot: false);
        if (!canEnable && slot.Metric == MeterSlotMetric.Fflogs && rowHovered)
            DactUi.Tooltip(text.Get("请先在战斗统计页启用 FFLogs 在线估算。", "Enable FFLogs online estimates on Combat Meter first."));
        ImGui.SetCursorScreenPos(min + new Vector2(0, rowHeight));
        ImGui.Dummy(new Vector2(width, 0));
        return changed;
    }

    private bool DrawPreviewPanel(MeterWindowProfile profile)
    {
        DactUi.Text(text.Get("页面预览", "Page preview"), DactTheme.Palette.Gold);
        DactUi.Hint(KindDescription(selectedKind) + text.Get("点栏目选择，拖动交换顺序。", " Click a column to select it; drag to swap."));
        var height = Math.Max(1, ImGui.GetContentRegionAvail().Y);
        return DrawPreview(profile, height);
    }

    // ---- 属性: 窗口外观 / 所选槽位

    private bool DrawPropertiesPanel(MeterWindowProfile profile, List<MeterSlotDefinition> slots)
    {
        var changed = false;
        DactUi.Text(text.Get("属性", "Properties"), DactTheme.Palette.Gold);
        DactUi.Segmented("meter-editor-properties-tab", [text.Get("窗口外观", "Window"), text.Get("所选槽位", "Selected slot")], ref propertiesTab,
            ImGui.GetContentRegionAvail().X);
        if (propertiesTab == 0) changed |= DrawWindowAppearance(profile);
        else changed |= DrawSelectedSlot(profile, slots);
        return changed;
    }

    private bool DrawWindowAppearance(MeterWindowProfile profile)
    {
        var s = DactUi.S;
        var changed = false;
        if (selectedKind != MeterWindowKind.Horizontal)
        {
            var showHeader = profile.ShowHeader;
            if (DactUi.ToggleRow(text.Get("显示标题区", "Show header"), ref showHeader))
            {
                profile.ShowHeader = showHeader;
                changed = true;
            }
        }
        var trackWidth = ImGui.GetContentRegionAvail().X - 64 * s;
        var fontScale = profile.FontScale;
        if (Field(text.Get("字号", "Text scale"), text.Get("只调整当前模板；表头、内容和行高会一起缩放。", "Scales this template's headers, values, and row height together."),
                () => DactUi.SliderFloat("##profile-font-scale", ref fontScale, .65f, 2, "0.00", trackWidth)))
        {
            profile.FontScale = fontScale;
            changed = true;
        }
        if (selectedKind == MeterWindowKind.Horizontal)
        {
            var itemWidth = (int)MathF.Round(profile.ItemWidth);
            if (Field(text.Get("模块宽度", "Module width"), text.Get("调整横向滑动序列中每名玩家占用的宽度。", "Sets each player's width in the horizontal carousel."),
                    () => DactUi.SliderInt("##horizontal-module-width", ref itemWidth, 140, 420, $"{itemWidth} px", trackWidth)))
            {
                profile.ItemWidth = itemWidth;
                changed = true;
            }
        }

        var background = profile.BackgroundColor ?? MeterBackground.DefaultColor;
        if (Field(text.Get("背景颜色", "Background color"), null, () => ColorField("background", ref background)))
        {
            profile.BackgroundColor = background;
            // A color picked for a transparent panel should be visible.
            if (profile.BackgroundOpacity == 0) profile.BackgroundOpacity = 0.85f;
            changed = true;
        }
        if (DactUi.Button(text.Get("恢复默认背景", "Reset background") + "##reset-background"))
        {
            profile.BackgroundColor = null;
            profile.BackgroundOpacity = selectedKind == MeterWindowKind.Horizontal ? 0 : 0.85f;
            changed = true;
        }
        var transparent = text.Get("底板全透明", "Transparent panel");
        DactUi.SameLineIfFits(DactUi.ButtonWidth(transparent));
        if (DactUi.Button(transparent + "##transparent-background"))
        {
            profile.BackgroundOpacity = 0;
            changed = true;
        }

        var opacity = (int)MathF.Round(profile.BackgroundOpacity * 100);
        if (Field(text.Get("底板不透明度", "Panel opacity"),
                text.Get("只调整窗口和行底色，不影响彩条。各统计模板分别保存；0% 为全透明，100% 为不透明。",
                    "Changes the window and row backgrounds without affecting data bars. Saved per meter template; 0% is transparent and 100% is opaque."),
                () => DactUi.SliderInt("##profile-background-opacity", ref opacity, 0, 100, $"{opacity}%", trackWidth)))
        {
            profile.BackgroundOpacity = opacity / 100f;
            changed = true;
        }
        if (selectedKind != MeterWindowKind.Horizontal)
        {
            var bars = (int)MathF.Round(MeterBackground.DataBarOpacity(profile) * 100);
            if (Field(text.Get("彩色数据条不透明度", "Data bar opacity"),
                    text.Get("独立调整彩条浓度，不影响底板和文字；0% 隐藏彩条。自己的强调色仍可单独调整。",
                        "Adjusts data bar strength independently of the panel and text; 0% hides the bars. The local player accent remains adjustable."),
                    () => DactUi.SliderInt("##profile-data-bar-opacity", ref bars, 0, 100, $"{bars}%", trackWidth)))
            {
                profile.DataBarOpacity = bars / 100f;
                changed = true;
            }
        }
        var accent = configuration.Meter.LocalPlayerColor;
        var accentRgb = new Vector3(accent.X, accent.Y, accent.Z);
        var accentAlpha = accent.W;
        if (Field(text.Get("自己的强调色", "Local player accent"), null, () => ColorField("accent", ref accentRgb, true, ref accentAlpha)))
        {
            configuration.Meter.LocalPlayerColor = new Vector4(accentRgb, accentAlpha);
            changed = true;
        }
        if (selectedKind != MeterWindowKind.Horizontal)
        {
            string[] directions = [CollapseDirectionLabel(MeterCollapseDirection.Upward), CollapseDirectionLabel(MeterCollapseDirection.Downward)];
            var direction = profile.CollapseDirection == MeterCollapseDirection.Downward ? 1 : 0;
            if (Field(text.Get("收起方向", "Collapse direction"),
                    text.Get("向上收起时固定顶部；向下收起时固定底部，展开时向上恢复。", "Upward keeps the top edge fixed. Downward keeps the bottom edge fixed and expands upward."),
                    () => DactUi.Select("##profile-collapse-direction", directions, ref direction)))
            {
                profile.CollapseDirection = direction == 1 ? MeterCollapseDirection.Downward : MeterCollapseDirection.Upward;
                changed = true;
            }
        }
        return changed;
    }

    // Label above its control; the explanation shows when hovering the label.
    private static bool Field(string label, string? hint, Func<bool> control)
    {
        DactUi.BeginField(label);
        if (hint is not null && ImGui.IsItemHovered()) DactUi.Tooltip(hint);
        var changed = control();
        DactUi.EndField();
        return changed;
    }

    private bool ColorField(string id, ref Vector3 color)
    {
        var alpha = 1f;
        return ColorField(id, ref color, false, ref alpha);
    }

    // A swatch that opens a picker, next to the value as editable "R, G, B[, A]" (0–255).
    private bool ColorField(string id, ref Vector3 color, bool withAlpha, ref float alpha)
    {
        var s = DactUi.S;
        var changed = false;
        var swatch = ImGui.GetCursorScreenPos();
        var popover = $"##color-{id}";
        if (ImGui.InvisibleButton($"##swatch-{id}", new Vector2(30 * s))) DactUi.OpenPopover(popover, swatch + new Vector2(0, 34 * s));
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(swatch, swatch + new Vector2(30 * s), DactUi.U32(new Vector4(color, 1)), 6 * s);
        draw.AddRect(swatch, swatch + new Vector2(30 * s), DactUi.U32(DactUi.FieldBorder), 6 * s, ImDrawFlags.None, Math.Max(1, s));
        ImGui.SameLine(0, 8 * s);
        static int Byte(float value) => (int)MathF.Round(Math.Clamp(value, 0, 1) * 255);
        var current = withAlpha
            ? $"{Byte(color.X)}, {Byte(color.Y)}, {Byte(color.Z)}, {Byte(alpha)}"
            : $"{Byte(color.X)}, {Byte(color.Y)}, {Byte(color.Z)}";
        var typed = current;
        if (DactUi.Input($"color-text-{id}", ref typed, withAlpha ? "R, G, B, A" : "R, G, B", 32) && typed != current)
        {
            var parts = typed.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(part => int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? Math.Clamp(value, 0, 255) : -1)
                .ToArray();
            // Applied only once the text is a complete colour, so typing never flashes black.
            if (parts.Length == (withAlpha ? 4 : 3) && parts.All(part => part >= 0))
            {
                color = new Vector3(parts[0], parts[1], parts[2]) / 255f;
                if (withAlpha) alpha = parts[3] / 255f;
                changed = true;
            }
        }
        if (DactUi.BeginPopover(popover, 240))
        {
            var picked = new Vector4(color, alpha);
            var flags = ImGuiColorEditFlags.NoSidePreview | ImGuiColorEditFlags.NoSmallPreview |
                        (withAlpha ? ImGuiColorEditFlags.AlphaBar : ImGuiColorEditFlags.NoAlpha);
            ImGui.SetNextItemWidth(216 * s);
            if (withAlpha ? ImGui.ColorPicker4($"##picker-{id}", ref picked, flags) : PickRgb(id, ref picked, flags))
            {
                color = new Vector3(picked.X, picked.Y, picked.Z);
                if (withAlpha) alpha = picked.W;
                changed = true;
            }
            DactUi.EndPopover();
        }
        return changed;
    }

    private static bool PickRgb(string id, ref Vector4 color, ImGuiColorEditFlags flags)
    {
        var rgb = new Vector3(color.X, color.Y, color.Z);
        if (!ImGui.ColorPicker3($"##picker-{id}", ref rgb, flags)) return false;
        color = new Vector4(rgb, color.W);
        return true;
    }

    private bool DrawSelectedSlot(MeterWindowProfile profile, List<MeterSlotDefinition> slots)
    {
        var s = DactUi.S;
        var changed = false;
        if (selectedKind == MeterWindowKind.Classic && configuration.Meter.ClassicAllianceView)
        {
            DactUi.Text(text.Get("24 人本固定布局", "Fixed 24-player layout"), DactTheme.Palette.Accent);
            DactUi.Hint(text.Get(
                "固定显示职业 / 名字和当前 DPS/HPS；没有可调整槽位。预览标题栏的 8/24 切回 8 人本后可继续编辑经典表格。",
                "Shows only job/name and the current DPS/HPS value. Switch the preview's 8/24 menu back to 8-player mode to edit classic table slots."));
            return false;
        }
        var slot = slots.FirstOrDefault(candidate => string.Equals(candidate.Id, selectedSlotId, StringComparison.OrdinalIgnoreCase));
        if (slot is null)
        {
            DactUi.Hint(text.Get("请先在左侧选择一个槽位。", "Select a slot on the left first."));
            return false;
        }

        var metrics = MeterSlotDefaults.EditableMetrics.Where(metric => CanUseMetric(metric) || metric == slot.Metric).ToArray();
        var labels = metrics.Select(metric => MeterSlotPresentation.Label(metric, text)).ToArray();
        var selectedMetric = Array.IndexOf(metrics, slot.Metric);
        if (Field(text.Get("槽位内容", "Slot content"), null, () => DactUi.Select("##slot-metric", labels, ref selectedMetric)))
        {
            slot.Metric = metrics[selectedMetric];
            changed = true;
        }
        var canUse = CanUseMetric(slot.Metric);
        var visible = slot.Visible;
        if (DactUi.ToggleRow(text.Get("使用这个槽位", "Use this slot"), ref visible, disabled: !canUse))
        {
            slot.Visible = visible;
            changed = true;
        }

        // 槽位选项: what this kind of slot can change, in one inset box.
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var draw = ImGui.GetWindowDrawList();
        draw.ChannelsSplit(2);
        draw.ChannelsSetCurrent(1);
        ImGui.SetCursorScreenPos(start + new Vector2(12 * s));
        ImGui.BeginGroup();
        using (DactUi.Gap(8))
        {
            var inner = width - 24 * s;
            DactUi.Text(text.Get("槽位选项", "Slot options"), DactTheme.Palette.Gold, DactFonts.Small);
            changed |= DrawSlotOptions(profile, slots, slot, canUse, inner);
        }
        ImGui.EndGroup();
        var bottom = ImGui.GetItemRectMax().Y + 12 * s;
        draw.ChannelsSetCurrent(0);
        draw.AddRectFilled(start, new Vector2(start.X + width, bottom), DactUi.U32(DactUi.Field), 8 * s);
        draw.AddRect(start, new Vector2(start.X + width, bottom), DactUi.U32(DactUi.FieldBorder), 8 * s, ImDrawFlags.None, Math.Max(1, s));
        draw.ChannelsMerge();
        ImGui.SetCursorScreenPos(new Vector2(start.X, bottom));
        ImGui.Dummy(new Vector2(width, 0));

        // 前移 / 后移 / 删除, sharing the row.
        var index = slots.IndexOf(slot);
        var buttonWidth = (width - 16 * s) / 3;
        if (DactUi.Button(text.Get("前移", "Move up") + "##slot-up", width: buttonWidth, disabled: index <= 0))
        {
            (slots[index - 1], slots[index]) = (slots[index], slots[index - 1]);
            changed = true;
        }
        ImGui.SameLine(0, 8 * s);
        if (DactUi.Button(text.Get("后移", "Move down") + "##slot-down", width: buttonWidth, disabled: index < 0 || index >= slots.Count - 1))
        {
            (slots[index + 1], slots[index]) = (slots[index], slots[index + 1]);
            changed = true;
        }
        ImGui.SameLine(0, 8 * s);
        if (DactUi.Button(text.Get("删除", "Delete") + "##slot-delete", ButtonKind.Danger, width: buttonWidth))
        {
            slots.Remove(slot);
            selectedSlotId = slots.Count == 0 ? null : slots[Math.Min(index, slots.Count - 1)].Id;
            changed = true;
        }
        return changed;
    }

    private bool DrawSlotOptions(MeterWindowProfile profile, List<MeterSlotDefinition> slots, MeterSlotDefinition slot, bool canUse, float width)
    {
        var s = DactUi.S;
        var changed = false;
        if (!canUse && slot.Metric == MeterSlotMetric.Fflogs)
        {
            DactUi.Wrapped(text.Get("先在战斗统计页启用 FFLogs 在线估算，才能打开这个列。原有配置会保留。",
                "Enable FFLogs online estimates on Combat Meter before showing this column. Its saved configuration is preserved."), DactUi.HintColor, DactFonts.Small, width);
            if (OpenMeterSettings is { } open && DactUi.Button(text.Get("去战斗统计页启用", "Enable on Combat Meter") + "##slot-fflogs-open", width: width)) open();
            return false;
        }
        if (slot.Metric == MeterSlotMetric.PlayerIdentity)
        {
            DactUi.Text(text.Get("职业显示方式", "Job display"));
            changed |= DrawJobStyleTiles(width);
            DactUi.Wrapped(text.Get("所有模板共用这一个设置；图标为游戏职业图标。", "Shared by every template; icons are the game's job icons."), DactUi.HintColor, DactFonts.Small, width);
            return changed;
        }
        if (MeterSlotPresentation.TryGetDpsMetric(slot.Metric, out var dpsMetric))
        {
            var ranking = profile.DpsSortMetric == dpsMetric;
            if (DactUi.Button(text.Get(ranking ? "当前 DPS 排序依据" : "设为 DPS 排序依据", ranking ? "Current DPS ranking metric" : "Use for DPS ranking") + "##slot-ranking",
                    icon: ranking ? "check" : null, width: width, disabled: !slot.Visible || ranking))
            {
                // Ranking belongs to the window profile, so each template can rank by its own DPS.
                profile.DpsSortMetric = dpsMetric;
                changed = true;
            }
            DactUi.Wrapped(slot.Visible
                    ? text.Get("排序决定这个模板按哪种 DPS 排名，不会改变显示的列。", "Ranking decides which DPS this template sorts by; the columns stay as they are.")
                    : text.Get("请先启用这个槽位。", "Enable this slot before using it for ranking."),
                DactUi.HintColor, DactFonts.Small, width);
            return changed;
        }
        if (slot.Metric == MeterSlotMetric.HighestDamage ||
            (slot.Metric == MeterSlotMetric.HighestDamageAction &&
             (selectedKind != MeterWindowKind.Horizontal || !slots.Any(candidate => candidate.Visible && candidate.Metric == MeterSlotMetric.HighestDamage))))
        {
            var format = slot.UseCompactHighestDamage ? 0 : 1;
            if (Field(text.Get("伤害数字格式", "Damage number format"), null,
                    () => DactUi.Select("##highest-damage-format", [text.Get("缩写（K / M）", "Compact (K / M)"), text.Get("完整数字", "Full number")], ref format, width)))
            {
                slot.UseCompactHighestDamage = format == 0;
                changed = true;
            }
            DactUi.Wrapped(text.Get("最高伤害会自动截短；把鼠标移到统计项上可查看完整技能名和数值。", "Max-hit values are truncated; hover the meter for full details."),
                DactUi.HintColor, DactFonts.Small, width);
            return changed;
        }
        if (slot.Metric == MeterSlotMetric.TeamDps)
        {
            DactUi.Wrapped(text.Get(
                "在队伍汇总区显示：本场全队总伤害 ÷ 有效战斗时长；切换职能分栏或只显示自己不会缩小统计范围。",
                "Shown in the team summary: total encounter damage / effective combat time. Role and self-only views keep the full team total."), DactUi.HintColor, DactFonts.Small, width);
            return false;
        }
        DactUi.Wrapped(text.Get(
            "普通槽位按列表顺序显示；姓名列会先压缩到两字省略，继续缩小窗口才出现横向滚动，悬停可看完整名称。全队汇总固定在底部。",
            "Regular slots follow list order. Names shrink to a two-character ellipsis before horizontal scrolling appears; hover for the full name. Team totals stay at the bottom."),
            DactUi.HintColor, DactFonts.Small, width);
        return false;
    }

    // Five tiles, three to a row: text styles show the job as it will read, icon styles a mark.
    private bool DrawJobStyleTiles(float width)
    {
        var s = DactUi.S;
        var palette = DactTheme.Palette;
        var changed = false;
        var styles = Enum.GetValues<JobDisplayStyle>();
        var gap = 6 * s;
        var tileWidth = (width - gap * 2) / 3;
        var tileHeight = 46 * s;
        var origin = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        for (var index = 0; index < styles.Length; index++)
        {
            var style = styles[index];
            var min = origin + new Vector2(index % 3 * (tileWidth + gap), index / 3 * (tileHeight + gap));
            ImGui.SetCursorScreenPos(min);
            if (ImGui.InvisibleButton($"##job-style-{style}", new Vector2(tileWidth, tileHeight)) && configuration.Meter.JobDisplayStyle != style)
            {
                configuration.Meter.JobDisplayStyle = style;
                changed = true;
            }
            var active = configuration.Meter.JobDisplayStyle == style;
            var hovered = ImGui.IsItemHovered();
            var fill = DactMotion.Color(ImGuiP.GetItemID(), active ? palette.Hover : hovered ? Vector4.Lerp(palette.Raised, palette.Hover, .5f) : palette.Raised, DactMotion.Fast);
            draw.AddRectFilled(min, min + new Vector2(tileWidth, tileHeight), DactUi.U32(fill), 6 * s);
            draw.AddRect(min, min + new Vector2(tileWidth, tileHeight), DactUi.U32(active ? palette.Accent with { W = .8f } : DactUi.FieldBorder), 6 * s, ImDrawFlags.None, Math.Max(1, s));
            var center = min + new Vector2(tileWidth * .5f, 15 * s);
            switch (style)
            {
                case JobDisplayStyle.Abbreviation or JobDisplayStyle.ChineseAbbreviation:
                    var sample = JobDisplayFormatter.FormatText("PLD", style);
                    DactUi.DrawText(draw, new Vector2(center.X - DactUi.TextWidth(sample, DactFonts.Small) * .5f, min.Y + 5 * s), 20 * s, sample, palette.Text, DactFonts.Small);
                    break;
                case JobDisplayStyle.MinimalIcon:
                    draw.AddCircle(center, 6 * s, DactUi.U32(palette.Accent), 20, 1.5f * s);
                    break;
                default:
                    var box = new Vector2(7 * s);
                    draw.AddRectFilled(center - box, center + box, DactUi.U32(style == JobDisplayStyle.ClassicIcon ? palette.Accent with { W = .55f } : palette.Accent with { W = .85f }),
                        style == JobDisplayStyle.ClassicIcon ? 3 * s : 1.5f * s);
                    break;
            }
            var label = DactUi.Fit(JobDisplayFormatter.Label(style, text), tileWidth - 8 * s);
            DactUi.DrawText(draw, new Vector2(min.X + (tileWidth - DactUi.TextWidth(label, DactFonts.Tiny)) * .5f, min.Y + 25 * s), 18 * s, label, palette.Muted, DactFonts.Tiny);
        }
        var rows = (styles.Length + 2) / 3;
        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(new Vector2(width, rows * tileHeight + (rows - 1) * gap));
        return changed;
    }

    // ---- Bottom bar and exit question

    private bool DrawFooter(Vector2 min, Vector2 size, float height)
    {
        var s = DactUi.S;
        var top = min.Y + size.Y - height;
        var draw = ImGui.GetWindowDrawList();
        DactUi.DrawText(draw, new Vector2(min.X + 18 * s, top), height,
            DactUi.Fit(text.Get("预览随设置实时变化；保存后写入配置，取消恢复打开时的样式。", "The preview follows every change; Save writes it, Cancel restores the opening style."), size.X * .6f),
            DactUi.HintColor, DactFonts.Small);
        var save = text.Get("保存", "Save");
        var cancel = text.Get("取消", "Cancel");
        var saveWidth = DactUi.ButtonWidth(save, ButtonKind.Primary);
        var x = min.X + size.X - 18 * s - saveWidth;
        ImGui.SetCursorScreenPos(new Vector2(x, top + (height - 30 * s) * .5f));
        if (DactUi.Button(save + "##meter-editor-save", ButtonKind.Primary))
        {
            SaveAndClose();
            return true;
        }
        ImGui.SetCursorScreenPos(new Vector2(x - 8 * s - DactUi.ButtonWidth(cancel), top + (height - 30 * s) * .5f));
        if (DactUi.Button(cancel + "##meter-editor-cancel")) RequestExitConfirmation();
        return false;
    }

    private bool DrawExitConfirmation()
    {
        if (exitConfirmationRequested)
        {
            // Both exit affordances share one dialog so neither can discard a live preview
            // without making the save decision explicit.
            DactUi.OpenDialog(ExitDialog);
            exitConfirmationRequested = false;
        }
        if (!DactUi.BeginDialog(ExitDialog, 420, sharedScrim: false)) return false;
        var s = DactUi.S;
        var closed = false;
        DactUi.Text(text.Get("真的要退出吗？", "Do you really want to exit?"), size: DactFonts.H2);
        DactUi.Hint(text.Get("还有没保存的改动。", "There are unsaved changes."));
        if (DactUi.Button(text.Get("保存并退出", "Save and exit") + "##exit-save", ButtonKind.Primary))
        {
            DactUi.CloseDialog(ExitDialog);
            SaveAndClose();
            closed = true;
        }
        ImGui.SameLine(0, 8 * s);
        if (DactUi.Button(text.Get("不保存并退出", "Exit without saving") + "##exit-discard", ButtonKind.Danger))
        {
            DactUi.CloseDialog(ExitDialog);
            CancelAndClose();
            closed = true;
        }
        ImGui.SameLine(0, 8 * s);
        if (DactUi.Button(text.Get("取消", "Cancel") + "##exit-cancel", ButtonKind.Ghost)) DactUi.CloseDialog(ExitDialog);
        DactUi.EndDialog(ExitDialog);
        return closed;
    }
}

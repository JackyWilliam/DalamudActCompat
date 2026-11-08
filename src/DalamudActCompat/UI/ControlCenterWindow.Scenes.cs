using System.Numerics;
using Dalamud.Bindings.ImGui;
using DalamudActCompat.Meter;
using DalamudActCompat.Plugin;
using DalamudActCompat.UI.Kit;

namespace DalamudActCompat.UI;

// 场景方案 (features.js scenes / scenesPick): a card on 设置 and its own sub-page. Each scene picks
// the combat meter and, per overlay, 打开 / 关闭 / 不变 in an anchored popover under its field.
public sealed partial class ControlCenterWindow
{
    private const string SceneOverlayPopover = "##scene-overlays";
    private bool scenesPage;
    private SceneKind sceneEditing;
    // Opens a scene's overlay popover without a click (offline previews of the design).
    private SceneKind? pendingScenePopover;
    // ImGui popups are keyed by the ID stack, so the field (inside the card) only requests the
    // popover and DrawSceneOverlayPopover (outside it) opens and draws it in one scope.
    private Vector2? scenePopoverRequest;
    private float scenePopoverWidth = 340;
    private bool scenePopoverAlignRight;
    private bool scenePopoverOpen;

    internal ScenePresetController? ScenePresets { get; set; }

    private string SceneName(SceneKind kind) => kind switch
    {
        SceneKind.HighEnd => text.Get("高难副本", "High-end duties"),
        SceneKind.Normal => text.Get("普通副本与日随", "Normal duties & roulettes"),
        SceneKind.PvP => "PvP",
        _ => text.Get("野外与主城", "Field & cities"),
    };

    private string SceneSub(SceneKind kind) => kind switch
    {
        SceneKind.HighEnd => text.Get("零式、绝境、极神", "Savage, ultimate, extreme"),
        SceneKind.Normal => text.Get("迷宫、讨伐、团队任务", "Dungeons, trials, alliance raids"),
        SceneKind.PvP => text.Get("水晶冲突、纷争前线等", "Crystalline Conflict, Frontline…"),
        _ => text.Get("不在副本里时", "Outside duties"),
    };

    // The card on 设置 (between 解析器与诊断 and the danger zone).
    private bool DrawScenePresetCard()
    {
        var changed = false;
        var manage = text.Get("管理场景", "Manage scenes");
        DactUi.BeginCard("settings-scenes", null);
        DactUi.CardTitleRow(text.Get("场景方案", "Scene presets"), null, DactUi.ButtonWidth(manage, ButtonKind.Ghost, "gear"), () =>
        {
            if (DactUi.Button(manage + "##scenes-manage", ButtonKind.Ghost, "gear")) scenesPage = true;
        });
        var enabled = configuration.Scenes.Enabled;
        if (DactUi.ToggleRow(text.Get("按内容类型自动切换", "Switch by content type"), ref enabled,
                text.Get("进入高难、日随、野外或 PvP 时，自动换成对应的战斗统计和悬浮窗组合。",
                    "Entering high-end, roulette, field or PvP content switches to the meter and overlays set for it.")))
        {
            configuration.Scenes.Enabled = enabled;
            ScenePresets?.RequestApply();
            changed = true;
        }
        DactUi.EndCard();
        return changed;
    }

    private bool DrawScenesPage()
    {
        var changed = false;
        var back = text.Get("返回设置", "Back to Settings");
        DactUi.PageHeader(text.Get("场景方案", "Scene presets"),
            text.Get("设置 / 场景方案 · 进入不同类型的内容时，自动换成对应的战斗统计和悬浮窗。",
                "Settings / Scene presets · Switch the combat meter and overlays when you enter different content."),
            DactUi.ButtonWidth(back),
            () =>
            {
                if (DactUi.Button(back + "##scenes-back")) scenesPage = false;
            });

        var autoLabel = text.Get("按内容类型自动切换", "Switch by content type");
        var autoWidth = DactUi.InlineToggleWidth(autoLabel);
        DactUi.BeginCard("scenes", null);
        DactUi.CardTitleRow(text.Get("场景", "Scenes"), null, autoWidth, () =>
        {
            var enabled = configuration.Scenes.Enabled;
            if (DactUi.InlineToggle(autoLabel, ref enabled))
            {
                configuration.Scenes.Enabled = enabled;
                ScenePresets?.RequestApply();
                changed = true;
            }
        });
        if (ScenePresets is { } presets && configuration.Scenes.Enabled)
            DactUi.Hint(text.Format($"当前：{SceneName(presets.Current)}（{presets.CurrentName}）。", $"Now: {SceneName(presets.Current)} ({presets.CurrentName})."));
        else
            DactUi.Hint(text.Get("开启后，进入不同内容时会按下面的设置切换。", "Once on, entering content switches to the settings below."));

        foreach (var kind in Enum.GetValues<SceneKind>()) changed |= DrawSceneRow(kind);
        DactUi.Hint(text.Get("悬浮窗一栏：蓝色是进入时打开，灰色是进入时关闭，没列出的保持不变。点击可修改。",
            "Overlays: blue opens on entry, grey closes on entry, anything not listed stays as it is. Click to change."));
        DactUi.Hint(text.Get("PvP 中始终隐藏战斗统计，不提供 PvP 信息优势（见 使用须知）。",
            "The combat meter always stays hidden in PvP, so it gives no information advantage there (see the usage notice)."));
        DactUi.EndCard();
        changed |= DrawSceneOverlayPopover();
        if (changed) ScenePresets?.RequestApply();
        return changed;
    }

    // dsl scene row: name and examples, the meter select, the overlay field, the scene switch.
    private bool DrawSceneRow(SceneKind kind)
    {
        var s = DactUi.S;
        var palette = DactTheme.Palette;
        var scene = configuration.Scenes.Get(kind);
        var changed = false;
        ImGui.PushID((int)kind);
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var height = 68 * s;
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(start, start + new Vector2(width, height), DactUi.U32(DactUi.Field), 8 * s);
        draw.AddRect(start, start + new Vector2(width, height), DactUi.U32(DactUi.FieldBorder), 8 * s, ImDrawFlags.None, Math.Max(1, s));

        // Column widths follow the design (200 / 150 / fill / switch) and shrink on narrow windows.
        var inner = width - 24 * s;
        var nameWidth = Math.Min(200 * s, inner * .3f);
        var meterWidth = Math.Min(150 * s, inner * .24f);
        var toggleWidth = 34 * s;
        var overlayWidth = inner - nameWidth - meterWidth - toggleWidth - 36 * s;
        var x = start.X + 12 * s;
        var top = start.Y + 8 * s;
        DactUi.DrawText(draw, new Vector2(x, start.Y + 15 * s), 18 * s, DactUi.Fit(SceneName(kind), nameWidth), palette.Text);
        string sub;
        using (DactFonts.Push(DactFonts.Small)) sub = DactUi.Fit(SceneSub(kind), nameWidth);
        DactUi.DrawText(draw, new Vector2(x, start.Y + 35 * s), 16 * s, sub, DactUi.HintColor, DactFonts.Small);
        x += nameWidth + 12 * s;

        DactUi.DrawText(draw, new Vector2(x, top), 18 * s, text.Get("战斗统计", "Combat meter"), palette.Muted, DactFonts.Small);
        ImGui.SetCursorScreenPos(new Vector2(x, top + 22 * s));
        if (kind == SceneKind.PvP)
        {
            var locked = 0;
            DactUi.Select("##scene-meter", [text.Get("隐藏（固定）", "Hidden (fixed)")], ref locked, meterWidth, disabled: true);
        }
        else
        {
            string[] values = [SceneMeter.Keep, nameof(MeterWindowKind.Classic), nameof(MeterWindowKind.Horizontal), nameof(MeterWindowKind.RoleSplit), SceneMeter.Hidden];
            string[] labels = [text.Get("不改变", "Keep as is"), MeterKindLabel(MeterWindowKind.Classic), MeterKindLabel(MeterWindowKind.Horizontal),
                MeterKindLabel(MeterWindowKind.RoleSplit), text.Get("隐藏", "Hidden")];
            var index = Math.Max(0, Array.IndexOf(values, scene.Meter));
            if (DactUi.Select("##scene-meter", labels, ref index, meterWidth, disabled: !scene.Enabled))
            {
                scene.Meter = values[index];
                changed = true;
            }
        }
        x += meterWidth + 12 * s;

        DactUi.DrawText(draw, new Vector2(x, top), 18 * s, text.Get("悬浮窗", "Overlays"), palette.Muted, DactFonts.Small);
        DrawSceneOverlayField(kind, scene, new Vector2(x, top + 22 * s), overlayWidth);
        x += overlayWidth + 12 * s;

        ImGui.SetCursorScreenPos(new Vector2(x, start.Y + (height - 20 * s) * .5f));
        var on = scene.Enabled;
        if (DactUi.Toggle("##scene-on", ref on))
        {
            scene.Enabled = on;
            changed = true;
        }
        if (ImGui.IsItemHovered()) DactUi.Tooltip(on ? text.Get("这个场景会自动切换", "This scene switches automatically") : text.Get("这个场景不做任何改动", "This scene changes nothing"));
        ImGui.SetCursorScreenPos(start + new Vector2(0, height));
        ImGui.Dummy(new Vector2(width, 0));
        ImGui.PopID();
        return changed;
    }

    // The field looks like the meter select beside it: blue pills open on entry, grey ones close.
    private void DrawSceneOverlayField(SceneKind kind, SceneSettings scene, Vector2 min, float width)
    {
        var s = DactUi.S;
        var palette = DactTheme.Palette;
        var size = new Vector2(width, 30 * s);
        ImGui.SetCursorScreenPos(min);
        var open = scenePopoverOpen && sceneEditing == kind;
        if (ImGui.InvisibleButton("##scene-overlays-field", size) && scene.Enabled || pendingScenePopover == kind)
        {
            pendingScenePopover = null;
            sceneEditing = kind;
            // The picker is at least as wide as the field and shares an edge with it: the left edge
            // when it fits, the right edge when the field sits near the window's right side.
            scenePopoverWidth = Math.Max(width / s, 340);
            scenePopoverAlignRight = min.X + scenePopoverWidth * s > DactUi.HostMax.X - 8 * s;
            scenePopoverRequest = new Vector2(scenePopoverAlignRight ? min.X + width : min.X, min.Y + 34 * s);
        }
        var hovered = ImGui.IsItemHovered();
        var draw = ImGui.GetWindowDrawList();
        var alpha = scene.Enabled ? 1 : .5f;
        draw.AddRectFilled(min, min + size, DactUi.U32(DactUi.Field with { W = DactUi.Field.W * alpha }), 6 * s);
        var border = DactMotion.Color(ImGuiP.GetItemID(), open || hovered && scene.Enabled ? palette.Accent : DactUi.FieldBorder, DactMotion.Fast);
        draw.AddRect(min, min + size, DactUi.U32(border), 6 * s, ImDrawFlags.None, Math.Max(1, s));
        var names = RegisteredOverlayNames();
        var opens = names.Where(name => scene.Overlays.GetValueOrDefault(name) == SceneOverlayAction.Open).ToArray();
        var closes = names.Where(name => scene.Overlays.GetValueOrDefault(name) == SceneOverlayAction.Close).ToArray();
        var right = min.X + width - 26 * s;
        var x = min.X + 4 * s;
        var shown = 0;
        foreach (var (name, tone) in opens.Select(n => (n, PillTone.Ok)).Concat(closes.Select(n => (n, PillTone.Off))))
        {
            var label = OverlayDisplayName(name);
            var pill = DactUi.PillWidth(label);
            // Pills that no longer fit collapse into "+N", the popover lists them all.
            if (x + pill > right - (shown < opens.Length + closes.Length - 1 ? 30 * s : 0)) break;
            DactUi.DrawPill(draw, new Vector2(x, min.Y + 4 * s), label, tone);
            x += pill + 6 * s;
            shown++;
        }
        var hidden = opens.Length + closes.Length - shown;
        if (hidden > 0) DactUi.DrawText(draw, new Vector2(x, min.Y), 30 * s, $"+{hidden}", palette.Muted, DactFonts.Small);
        if (opens.Length + closes.Length == 0)
            DactUi.DrawText(draw, new Vector2(min.X + 10 * s, min.Y), 30 * s, text.Get("不改动", "No change"), palette.Muted);
        DactIcons.Draw(draw, "chevronDown", new Vector2(min.X + width - 22 * s, min.Y + 8 * s), 14 * s, open ? palette.Accent : palette.Muted);
    }

    private bool DrawSceneOverlayPopover()
    {
        if (scenePopoverRequest is { } anchor)
        {
            DactUi.OpenPopover(SceneOverlayPopover, anchor, scenePopoverAlignRight);
            scenePopoverRequest = null;
        }
        scenePopoverOpen = DactUi.BeginPopover(SceneOverlayPopover, scenePopoverWidth);
        if (!scenePopoverOpen) return false;
        var s = DactUi.S;
        var changed = false;
        var scene = configuration.Scenes.Get(sceneEditing);
        DactUi.Text(text.Format($"进入「{SceneName(sceneEditing)}」时", $"On entering {SceneName(sceneEditing)}"));
        var names = RegisteredOverlayNames();
        if (names.Length == 0) DactUi.Hint(text.Get("还没有悬浮窗。", "No overlays yet."));
        var templates = getOverlayTemplates();
        string[] choices = [text.Get("打开", "Open"), text.Get("关闭", "Close"), text.Get("不变", "Keep")];
        var segmentWidth = choices.Sum(choice => DactUi.TextWidth(choice) + 24 * s) + 4 * s + 6 * s;
        foreach (var name in names)
        {
            ImGui.PushID(name);
            var top = ImGui.GetCursorPos();
            var available = ImGui.GetContentRegionAvail().X;
            var nameWidth = available - segmentWidth - 10 * s;
            var template = templates.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
            var draw = ImGui.GetWindowDrawList();
            var screen = ImGui.GetCursorScreenPos();
            DactUi.DrawText(draw, screen, 18 * s, DactUi.Fit(OverlayDisplayName(name), nameWidth), DactTheme.Palette.Text);
            string kind;
            using (DactFonts.Push(DactFonts.Small)) kind = DactUi.Fit(OverlayKind(name, template), nameWidth);
            DactUi.DrawText(draw, screen + new Vector2(0, 17 * s), 16 * s, kind, DactTheme.Palette.Muted, DactFonts.Small);
            ImGui.SetCursorPos(new Vector2(top.X + available - segmentWidth, top.Y));
            var action = scene.Overlays.GetValueOrDefault(name);
            var active = action switch { SceneOverlayAction.Open => 0, SceneOverlayAction.Close => 1, _ => 2 };
            if (DactUi.Segmented("##scene-action", choices, ref active))
            {
                var next = active switch { 0 => SceneOverlayAction.Open, 1 => SceneOverlayAction.Close, _ => SceneOverlayAction.Unchanged };
                if (next == SceneOverlayAction.Unchanged) scene.Overlays.Remove(name);
                else scene.Overlays[name] = next;
                changed = true;
            }
            ImGui.PopID();
        }
        DactUi.Divider();
        DactUi.Hint(text.Get("选「不变」就保持原样；新加的悬浮窗默认「不变」。", "Keep leaves it as it is; overlays you add later start as Keep."));
        // A ghost link has 6 px of hover padding; pull it out so its text lines up with the hint.
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() - 6 * s);
        if (DactUi.Button(text.Get("管理我的悬浮窗", "Manage my overlays") + "##scene-manage-overlays", ButtonKind.Ghost))
        {
            ImGui.CloseCurrentPopup();
            selectedPage = Page.Overlays;
        }
        if (ImGui.IsKeyPressed(ImGuiKey.Escape)) ImGui.CloseCurrentPopup();
        DactUi.EndPopover();
        return changed;
    }
}

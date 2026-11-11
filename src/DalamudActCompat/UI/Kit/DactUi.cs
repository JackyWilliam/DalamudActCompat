using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace DalamudActCompat.UI.Kit;

internal enum ButtonKind { Primary, Secondary, Ghost, Danger }

internal enum PillTone { Ok, Warn, Off, Error }

// Controls of the 1.0 design (tools/ui-redesign/dsl.js), drawn with ImDrawList so they match
// the Figma components pixel for pixel at the default 17 px font. Layout follows Figma auto
// layout: a container's gap is ImGui's vertical ItemSpacing while its children are drawn.
internal static class DactUi
{
    // Design pixel -> screen pixel. Set by the window shell each frame under the body font.
    public static float S { get; private set; } = 1;

    public static void BeginFrame() => S = Math.Max(.75f, ImGui.GetFontSize() / 17f);

    public static float Px(float value) => value * S;

    private static SkinPalette P => DactTheme.Palette;

    // Derived colours use the formulas of dsl.js, so every skin palette restyles them.
    public static Vector4 DividerColor => P.Border with { W = P.Border.W * .55f };
    public static Vector4 CardBorder => P.Border with { W = P.Border.W * .75f };
    // Ultimate nameplates set fields into a groove with the metal frame as their rim.
    public static Vector4 Field => P.Ultimate is { } u ? u.Groove : P.Surface;
    public static Vector4 FieldBorder => P.Ultimate is { } u ? u.Frame with { W = .55f } : P.Muted with { W = .16f };
    public static Vector4 Primary => Vector4.Lerp(P.Hover, P.Accent, .35f) with { W = 1 };
    public static Vector4 AccentSoft => P.Accent with { W = .14f };
    public static Vector4 GoldSoft => P.Gold with { W = .16f };
    public static Vector4 Off => P.Muted with { W = .12f };
    public static Vector4 HintColor => P.Muted with { W = .82f };
    public static readonly Vector4 Danger = new(.878f, .424f, .424f, 1);
    public static Vector4 DangerSoft => Danger with { W = .14f };
    public static readonly Vector4 DangerFill = new(.290f, .122f, .137f, 1);
    public static readonly Vector4 DangerText = new(.953f, .714f, .714f, 1);
    public static readonly Vector4 BadgeRed = new(.851f, .325f, .310f, 1);

    public static uint U32(Vector4 color) => ImGui.GetColorU32(color);

    // ---- Tooltip

    // DACT's hover help. 艾欧泽亚 draws it as a piece of the game's own window (paper and metal
    // rim) behind the text, like its windows. A rim laid over ImGui's tooltip afterwards sat inside
    // the tooltip's own fill and border and squeezed the window sprite (user: 位置不对). Other
    // skins keep ImGui's tooltip.
    public static void Tooltip(string text)
    {
        if (!P.Light || DactTheme.GameAssets is not { HasWindowTextures: true } game)
        {
            ImGui.SetTooltip(text);
            return;
        }
        var scale = GameSkinAssets.Scale;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(16, 12) * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupBorderSize, 0);
        ImGui.PushStyleColor(ImGuiCol.PopupBg, Vector4.Zero);
        // Replace any tooltip already shown this frame, as ImGui.SetTooltip does; appending would
        // paint this paper over the earlier text.
        ImGuiP.BeginTooltipEx(ImGuiTooltipFlags.OverridePreviousTooltip, ImGuiWindowFlags.None);
        // The sprite ends in a 10px transparent shadow tail; hang it below the tooltip so the
        // bottom rim sits on the tooltip's edge and the padding reads the same on all sides.
        var min = ImGui.GetWindowPos();
        game.Window(min, min + ImGui.GetWindowSize() + new Vector2(0, 10 * scale), scale, clipToContent: false);
        // Long help wraps instead of running across the screen inside the frame.
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 32);
        ImGui.PushStyleColor(ImGuiCol.Text, P.Text);
        ImGui.TextUnformatted(text);
        ImGui.PopStyleColor();
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
        ImGui.PopStyleColor();
        ImGui.PopStyleVar(2);
    }

    // ---- Floating panes

    // 澄光流璃 and 听雨窗 paint every floating pane (dropdowns, menus, popovers, dialogs, the
    // search box) in the control center's own material, the blurred glass or the rain pane. A
    // flat Raised fill in those skins was see-through with nothing blurred behind it.
    public static bool SkinPane => P.Rain || P.Glass && DactTheme.GlassRenderer?.Ready == true;

    // The fill and border to push for a floating pane: clear where the skin paints it.
    public static Vector4 PaneFill(Vector4 fill) => SkinPane ? Vector4.Zero : fill;
    public static Vector4 PaneBorder(Vector4 border) => SkinPane ? Vector4.Zero : border;

    // Paints the skin's pane behind the current window; call right after its Begin. 艾欧泽亚 rims
    // every floating pane with the game's metal frame (user request), like its windows.
    public static void DrawSkinPane()
    {
        if (SkinPane) DactTheme.DrawGameWindow();
        else if (P.Light) DactTheme.GameAssets?.PopupFrame();
    }

    // ---- Spacing

    public readonly struct SpacingScope : IDisposable
    {
        public void Dispose() => ImGui.PopStyleVar();
    }

    // Children drawn inside use gap (design px) between them, like an auto-layout column.
    public static SpacingScope Gap(float gap, float horizontal = 8)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(horizontal, gap) * S);
        return default;
    }

    public static void Space(float height) => ImGui.Dummy(new Vector2(0, Math.Max(0, height * S - ImGui.GetStyle().ItemSpacing.Y)));

    // ---- Text

    // Figma line heights per size (dsl.js LH). ImGui lines are exactly one font size tall, so
    // text gets the difference as leading, split above and below, to keep the layout in step.
    private static float LineHeight(int size) => size switch
    {
        DactFonts.Title => 24, DactFonts.H2 => 20, DactFonts.Small => 16, DactFonts.Tiny => 14, _ => 18,
    } * S;

    private static void Leading(int size, float lines = 1)
    {
        var extra = (LineHeight(size) - ImGui.GetTextLineHeight()) * .5f * lines;
        if (extra > 0) ImGui.SetCursorPosY(ImGui.GetCursorPosY() + extra);
    }

    public static void Text(string value, Vector4? color = null, int size = DactFonts.Body, bool bold = false)
    {
        using var font = DactFonts.Push(size);
        var tint = DactTheme.Foreground(color ?? P.Text);
        Leading(size);
        var start = ImGui.GetCursorScreenPos();
        ImGui.PushStyleColor(ImGuiCol.Text, tint);
        ImGui.TextUnformatted(value);
        ImGui.PopStyleColor();
        // Dalamud's default font has no bold face; a second pass one pixel right thickens it.
        if (bold) ImGui.GetWindowDrawList().AddText(start + new Vector2(MathF.Max(1, S * .8f), 0), U32(tint), value);
        Leading(size);
    }

    // Wraps to width (0: the available width). Breaks between CJK characters as well as at
    // spaces (HelpWindow.WrapLines); ImGui's own wrapping only breaks at spaces.
    public static void Wrapped(string value, Vector4? color = null, int size = DactFonts.Body, float width = 0)
    {
        using var font = DactFonts.Push(size);
        var lines = WrapLines(value, width > 0 ? width : ImGui.GetContentRegionAvail().X);
        ImGui.PushStyleColor(ImGuiCol.Text, DactTheme.Foreground(color ?? P.Text));
        Leading(size);
        ImGui.TextUnformatted(string.Join("\n", lines));
        // A paragraph gets the leading of every line it occupies, all added below its last line.
        Leading(size, lines.Count * 2 - 1);
        ImGui.PopStyleColor();
    }

    // Lines of value that fit width in the current font.
    public static IReadOnlyList<string> WrapLines(string value, float width)
        => HelpWindow.WrapLines(value, Math.Max(1, width), part => ImGui.CalcTextSize(part).X);

    // Shortens value with an ellipsis to width in the current font.
    public static string Fit(string value, float width)
        => FriendsMessagePreview.Ellipsize(value, Math.Max(0, width), part => ImGui.CalcTextSize(part).X);

    public static void Hint(string value) => Wrapped(value, HintColor, DactFonts.Small);

    public static float TextWidth(string value, int size = DactFonts.Body)
    {
        using var font = DactFonts.Push(size);
        return ImGui.CalcTextSize(value).X;
    }

    // Draws text so its line box is vertically centred in [top, top + height].
    public static void DrawText(ImDrawListPtr draw, Vector2 at, float height, string value, Vector4 color, int size = DactFonts.Body)
    {
        using var font = DactFonts.Push(size);
        var line = ImGui.GetTextLineHeight();
        draw.AddText(new Vector2(MathF.Round(at.X), MathF.Round(at.Y + (height - line) * .5f)), U32(DactTheme.Foreground(color)), value);
    }

    public static void Divider()
    {
        var min = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        ImGui.Dummy(new Vector2(width, Math.Max(1, S)));
        ImGui.GetWindowDrawList().AddRectFilled(min, min + new Vector2(width, Math.Max(1, S)), U32(DividerColor));
    }

    // ---- Buttons

    public static float ButtonWidth(string label, ButtonKind kind = ButtonKind.Secondary, string? icon = null)
        => TextWidth(Caption(label)) + (icon is null ? 0 : 20 * S) + (kind == ButtonKind.Ghost ? 12 : 28) * S;

    public static bool Button(string label, ButtonKind kind = ButtonKind.Secondary, string? icon = null, float width = 0, bool disabled = false)
    {
        var caption = Caption(label);
        var height = 30 * S;
        var size = new Vector2(width > 0 ? width : ButtonWidth(label, kind, icon), height);
        // Ghost buttons are text links in every skin (screens: 运行状态, 清除搜索).
        if (UsesSkinButtons() && kind != ButtonKind.Ghost)
        {
            // Textured, glass and nameplate skins keep their own button art.
            if (disabled) ImGui.BeginDisabled();
            DactTheme.PendingButtonIcon = icon;
            var skinned = DactTheme.Button(label, size);
            DactTheme.PendingButtonIcon = null;
            if (disabled) ImGui.EndDisabled();
            return skinned && !disabled;
        }

        if (disabled) ImGui.BeginDisabled();
        var clicked = ImGui.InvisibleButton(label, size);
        if (disabled) ImGui.EndDisabled();
        var min = ImGui.GetItemRectMin();
        var hovered = !disabled && ImGui.IsItemHovered();
        var held = !disabled && ImGui.IsItemActive();
        var (fill, ink) = kind switch
        {
            ButtonKind.Primary => (Primary, P.Text),
            ButtonKind.Danger => (DangerFill, DangerText),
            ButtonKind.Ghost => (P.Accent with { W = 0 }, P.Accent),
            _ => (P.Hover, P.Text),
        };
        var target = kind == ButtonKind.Ghost
            ? AccentSoft with { W = held ? .22f : hovered ? .12f : 0 }
            : Vector4.Lerp(fill, P.Accent, held ? .22f : hovered ? .12f : 0) with { W = fill.W };
        var shown = DactMotion.Color(ImGuiP.GetItemID(), target, DactMotion.Fast);
        var alpha = disabled ? .45f : 1;
        var draw = ImGui.GetWindowDrawList();
        if (shown.W > 0) draw.AddRectFilled(min, min + size, U32(shown with { W = shown.W * alpha }), 6 * S);
        var textWidth = TextWidth(caption);
        var content = textWidth + (icon is null ? 0 : 20 * S);
        var x = min.X + (size.X - content) * .5f;
        var press = held ? S : 0;
        if (icon is not null)
        {
            DactIcons.Draw(draw, icon, new Vector2(x, min.Y + (height - 14 * S) * .5f + press), 14 * S, ink with { W = alpha });
            x += 20 * S;
        }
        DrawText(draw, new Vector2(x, min.Y + press), height, caption, ink with { W = alpha });
        FocusRing(min, min + size, 6);
        return clicked && !disabled;
    }

    private static GameSkinIcon? GameIcon(string icon) => icon switch
    {
        "close" => GameSkinIcon.Close,
        "help" => GameSkinIcon.Help,
        "feedback" => GameSkinIcon.Chat,
        "refresh" => GameSkinIcon.Refresh,
        "search" => GameSkinIcon.Search,
        "users" => GameSkinIcon.Friends,
        _ => null,
    };

    public static bool IconButton(string id, string icon, string? tooltip = null, Vector4? background = null, float size = 28)
    {
        var box = new Vector2(size * S);
        var clicked = ImGui.InvisibleButton(id, box);
        var min = ImGui.GetItemRectMin();
        var hovered = ImGui.IsItemHovered();
        // 艾欧泽亚 draws the game's own round buttons (and the bronze close) when its textures load.
        if (P.Light && DactTheme.GameAssets is { } game && GameIcon(icon) is { } gameIcon &&
            game.Icon(gameIcon, min + new Vector2(1 * S), box.X - 2 * S, ImGui.IsItemActive()))
        {
            if (tooltip is not null && hovered) Tooltip(tooltip);
            FocusRing(min, min + box, 6);
            return clicked;
        }
        var fill = DactMotion.Color(ImGuiP.GetItemID(), background ?? P.Hover with { W = hovered ? 1 : 0 }, DactMotion.Fast);
        var draw = ImGui.GetWindowDrawList();
        if (fill.W > 0) draw.AddRectFilled(min, min + box, U32(fill), 6 * S);
        var ink = DactMotion.Color(ImGuiP.GetItemID() + 1, hovered ? P.Text : P.Muted, DactMotion.Fast);
        DactIcons.Draw(draw, icon, min + (box - new Vector2(16 * S)) * .5f, 16 * S, ink);
        if (tooltip is not null && hovered) Tooltip(tooltip);
        FocusRing(min, min + box, 6);
        return clicked;
    }

    // ---- Toggle (34×20, knob 14)

    public static bool Toggle(string id, ref bool value, bool disabled = false)
    {
        var size = new Vector2(34, 20) * S;
        if (disabled) ImGui.BeginDisabled();
        var clicked = ImGui.InvisibleButton(id, size);
        if (disabled) ImGui.EndDisabled();
        if (clicked && !disabled) value = !value;
        var min = ImGui.GetItemRectMin();
        var on = DactMotion.Float(ImGuiP.GetItemID(), value ? 1 : 0, DactMotion.Normal);
        var hover = DactMotion.Float(ImGui.GetID(id + "##hover"), ImGui.IsItemHovered() && !disabled ? 1 : 0, DactMotion.Fast);
        DrawToggle(ImGui.GetWindowDrawList(), min, on, hover, disabled);
        FocusRing(min, min + size, 10);
        return clicked && !disabled;
    }

    // The 34×20 switch at min, on and hover eased 0..1. Shared with controls that wrap a switch
    // in a bigger target (the style editor's chips), so every switch looks and moves the same.
    public static void DrawToggle(ImDrawListPtr draw, Vector2 min, float on, float hover, bool disabled = false)
    {
        var size = new Vector2(34, 20) * S;
        var alpha = disabled ? .45f : 1;
        var track = Vector4.Lerp(P.Hover, P.Accent, on) with { W = alpha };
        var knob = Vector4.Lerp(P.Muted, P.Surface, on) with { W = alpha };
        if (P.Ultimate is { } plate)
        {
            // Nameplate switch: a groove when off, the skin's lamp colour with its rim when on.
            draw.AddRectFilled(min, min + size, U32(Vector4.Lerp(plate.Groove, plate.Active, on) with { W = alpha }), 10 * S);
            draw.AddRect(min, min + size, U32(Vector4.Lerp(plate.Frame with { W = .8f }, plate.ActiveRim, on) with { W = alpha }), 10 * S, ImDrawFlags.None, Math.Max(1, S));
            knob = Vector4.Lerp(P.Muted, plate.ActiveInk, on) with { W = alpha };
        }
        else if (P.Light)
        {
            // 艾欧泽亚: the game's Light-theme switch, an orange face when on and a white knob.
            // The lower tone fills the whole capsule and the upper tone its top half, both rounded:
            // a square gradient between two end caps left its corners outside the capsule (the
            // 「方框」 under a switched-on switch).
            var top = Vector4.Lerp(new Vector4(.91f, .86f, .78f, alpha), new Vector4(1, .62f, .16f, alpha), on);
            var low = Vector4.Lerp(new Vector4(.84f, .78f, .69f, alpha), new Vector4(.92f, .51f, .07f, alpha), on);
            draw.AddRectFilled(min, min + size, U32(low), 10 * S);
            draw.AddRectFilled(min + new Vector2(1 * S, 1 * S), new Vector2(min.X + size.X - 1 * S, min.Y + size.Y * .55f), U32(top), 9 * S,
                ImDrawFlags.RoundCornersTop);
            draw.AddRect(min, min + size, U32(new Vector4(.45f, .33f, .2f, .55f * alpha)), 10 * S, ImDrawFlags.None, Math.Max(1, S));
            knob = new Vector4(1, 1, 1, alpha);
        }
        else draw.AddRectFilled(min, min + size, U32(track), 10 * S);
        // Hover eases in: the track brightens, the knob grows a little and gains a soft halo.
        if (hover > .01f) draw.AddRectFilled(min, min + size, U32(P.Text with { W = .1f * hover }), 10 * S);
        var travel = size.X - 6 * S - 14 * S;
        var knobCenter = min + new Vector2(3 * S + 7 * S + travel * on, size.Y * .5f);
        if (hover > .01f) draw.AddCircleFilled(knobCenter, (7 + 4 * hover) * S, U32((on > .5f ? P.Accent : P.Text) with { W = .2f * hover * alpha }), 24);
        draw.AddCircleFilled(knobCenter, (7 + hover) * S, U32(knob), 20);
    }

    // dsl checkbox + label: an 18 px box (accent with a check when on) and its label. The whole
    // row is the click target; the check eases in with the fill.
    public static bool Checkbox(string label, ref bool value, int size = DactFonts.Small, bool disabled = false)
    {
        var id = label.Contains("##", StringComparison.Ordinal) ? label : "##check-" + label;
        var shown = label.Split("##", 2)[0];
        var textWidth = TextWidth(shown, size);
        var rowSize = new Vector2(18 * S + (textWidth > 0 ? 8 * S + textWidth : 0), 24 * S);
        if (disabled) ImGui.BeginDisabled();
        var clicked = ImGui.InvisibleButton(id, rowSize);
        if (disabled) ImGui.EndDisabled();
        if (clicked && !disabled) value = !value;
        var min = ImGui.GetItemRectMin();
        var on = DactMotion.Float(ImGuiP.GetItemID(), value ? 1 : 0, DactMotion.Fast);
        var alpha = disabled ? .45f : 1;
        var box = min + new Vector2(0, 3 * S);
        var draw = ImGui.GetWindowDrawList();
        if (P.Ultimate is { } plate && !disabled)
        {
            UltimateSkins.DrawCheckbox(draw, box, 18 * S, value, ImGui.IsItemHovered(), plate, S);
            if (textWidth > 0) DrawText(draw, min + new Vector2(26 * S, 0), 24 * S, shown, P.Text with { W = alpha }, size);
            FocusRing(box, box + new Vector2(18 * S), 4);
            return clicked;
        }
        draw.AddRectFilled(box, box + new Vector2(18 * S), U32(Vector4.Lerp(Field, P.Accent, on) with { W = alpha }), 4 * S);
        if (on < .99f) draw.AddRect(box, box + new Vector2(18 * S), U32(FieldBorder with { W = FieldBorder.W * (1 - on) * alpha }), 4 * S, ImDrawFlags.None, Math.Max(1, S));
        if (on > .01f) DactIcons.Draw(draw, "check", box + new Vector2(3 * S), 12 * S, P.Surface with { W = on * alpha });
        if (textWidth > 0) DrawText(draw, min + new Vector2(26 * S, 0), 24 * S, shown, P.Text with { W = alpha }, size);
        FocusRing(box, box + new Vector2(18 * S), 4);
        return clicked && !disabled;
    }

    // Label on the left, switch on the right, optional hint below (dsl toggleRow).
    public static bool ToggleRow(string label, ref bool value, string? hint = null, bool disabled = false)
    {
        bool changed;
        ImGui.BeginGroup();
        using (Gap(4))
        {
            var start = ImGui.GetCursorScreenPos();
            var width = ImGui.GetContentRegionAvail().X;
            // Long translations wrap beside the switch instead of running under it.
            // Only the visible part of an ImGui label (before ##) is drawn; the whole label stays the ID.
            var labelText = string.Join("\n", WrapLines(label.Split("##", 2)[0], width - 44 * S));
            var labelHeight = ImGui.CalcTextSize(labelText).Y;
            var rowHeight = Math.Max(24 * S, labelHeight + 6 * S);
            // The switch goes first and the row is claimed after it: moving the cursor back down
            // past the row would stretch the group by one gap.
            ImGui.SetCursorScreenPos(start + new Vector2(width - 34 * S, (rowHeight - 20 * S) * .5f));
            changed = Toggle("##toggle-" + label, ref value, disabled);
            ImGui.SetCursorScreenPos(start);
            ImGui.Dummy(new Vector2(width, rowHeight));
            var draw = ImGui.GetWindowDrawList();
            draw.AddText(new Vector2(start.X, MathF.Round(start.Y + (rowHeight - labelHeight) * .5f)),
                U32(DactTheme.Foreground(P.Text with { W = disabled ? .45f : 1 })), labelText);
            if (!string.IsNullOrEmpty(hint)) Hint(hint);
        }
        ImGui.EndGroup();
        return changed;
    }

    // Label on the left, a compact select on the right, lined up with ToggleRow switches. A muted
    // label marks a setting that belongs to the row above it.
    public static bool SelectRow(string label, string id, string[] items, ref int selected, float selectWidth = 120, bool muted = false)
    {
        bool changed;
        ImGui.BeginGroup();
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var fieldWidth = Math.Min(selectWidth * S, width * .6f);
        var labelText = string.Join("\n", WrapLines(label, width - fieldWidth - 10 * S));
        var labelHeight = ImGui.CalcTextSize(labelText).Y;
        var rowHeight = Math.Max(30 * S, labelHeight + 6 * S);
        ImGui.SetCursorScreenPos(start + new Vector2(width - fieldWidth, (rowHeight - 30 * S) * .5f));
        changed = Select(id, items, ref selected, fieldWidth);
        ImGui.SetCursorScreenPos(start);
        ImGui.Dummy(new Vector2(width, rowHeight));
        ImGui.GetWindowDrawList().AddText(new Vector2(start.X, MathF.Round(start.Y + (rowHeight - labelHeight) * .5f)),
            U32(DactTheme.Foreground(muted ? P.Muted : P.Text)), labelText);
        ImGui.EndGroup();
        return changed;
    }

    // ---- Status

    public static void Pill(string label, PillTone tone = PillTone.Ok, bool dot = true, float maxWidth = 0)
    {
        var full = label;
        if (maxWidth <= 0 && extrasLimit > 0) maxWidth = extrasLimit;
        if (maxWidth > 0)
        {
            // A long pill (e.g. translated metadata) ends in an ellipsis; hovering shows it in full.
            using (DactFonts.Push(DactFonts.Small)) label = Fit(label, maxWidth - (dot ? 12 : 0) * S - 18 * S);
        }
        var (fill, ink) = tone switch
        {
            PillTone.Warn => (GoldSoft, P.Gold),
            PillTone.Off => (Off, P.Muted),
            PillTone.Error => (DangerSoft, Danger),
            _ => (AccentSoft, P.Accent),
        };
        var width = TextWidth(label, DactFonts.Small) + (dot ? 12 : 0) * S + 18 * S;
        var size = new Vector2(width, 22 * S);
        var min = ImGui.GetCursorScreenPos();
        ImGui.Dummy(size);
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(min, min + size, U32(fill), 11 * S);
        var x = min.X + 9 * S;
        if (dot)
        {
            draw.AddCircleFilled(new Vector2(x + 3 * S, min.Y + size.Y * .5f), 3 * S, U32(ink), 12);
            x += 12 * S;
        }
        DrawText(draw, new Vector2(x, min.Y), size.Y, label, ink, DactFonts.Small);
        if (label != full && ImGui.IsItemHovered()) Tooltip(full);
    }

    public static float PillWidth(string label, bool dot = true) => TextWidth(label, DactFonts.Small) + (dot ? 12 : 0) * S + 18 * S;

    // The same pill painted straight onto a draw list, for rows that are drawn by hand
    // (selectable list items) and cannot host a layout item.
    public static void DrawPill(ImDrawListPtr draw, Vector2 min, string label, PillTone tone, bool dot = true)
    {
        var (fill, ink) = tone switch
        {
            PillTone.Warn => (GoldSoft, P.Gold),
            PillTone.Off => (Off, P.Muted),
            PillTone.Error => (DangerSoft, Danger),
            _ => (AccentSoft, P.Accent),
        };
        var size = new Vector2(PillWidth(label, dot), 22 * S);
        draw.AddRectFilled(min, min + size, U32(fill), 11 * S);
        var x = min.X + 9 * S;
        if (dot)
        {
            draw.AddCircleFilled(new Vector2(x + 3 * S, min.Y + size.Y * .5f), 3 * S, U32(ink), 12);
            x += 12 * S;
        }
        DrawText(draw, new Vector2(x, min.Y), size.Y, label, ink, DactFonts.Small);
    }

    public static void Badge(ImDrawListPtr draw, Vector2 rightCenter, int count)
    {
        var label = count > 99 ? "99+" : count.ToString();
        var width = Math.Max(18 * S, TextWidth(label, DactFonts.Tiny) + 12 * S);
        var min = new Vector2(rightCenter.X - width, rightCenter.Y - 9 * S);
        draw.AddRectFilled(min, min + new Vector2(width, 18 * S), U32(BadgeRed), 9 * S);
        var textWidth = TextWidth(label, DactFonts.Tiny);
        DrawText(draw, new Vector2(min.X + (width - textWidth) * .5f, min.Y), 18 * S, label, new Vector4(1, 1, 1, 1), DactFonts.Tiny);
    }

    // ---- Status tiles (dsl statTile): equal-height row, icon + small label, value (15), hint.

    internal readonly record struct StatTileData(string Icon, string Label, string Value, Vector4 Color, string Sub, bool Bold = false);

    public static void StatTiles(params StatTileData[] tiles)
    {
        if (tiles.Length == 0) return;
        var gap = 12 * S;
        var origin = ImGui.GetCursorScreenPos();
        var width = (ImGui.GetContentRegionAvail().X - gap * (tiles.Length - 1)) / tiles.Length;
        var inner = width - 24 * S;
        float SubHeight(string sub) { using (DactFonts.Push(DactFonts.Small)) return ImGui.CalcTextSize(string.Join("\n", WrapLines(sub, inner))).Y; }
        // Value and first hint line use the design line heights (20, 16); extra hint lines add their own.
        var height = 24 * S + 16 * S + 12 * S + LineHeight(DactFonts.H2) + Math.Max(LineHeight(DactFonts.Small), tiles.Max(tile => SubHeight(tile.Sub)));
        ImGui.Dummy(new Vector2(ImGui.GetContentRegionAvail().X, height));
        var draw = ImGui.GetWindowDrawList();
        for (var i = 0; i < tiles.Length; i++)
        {
            var tile = tiles[i];
            var min = origin + new Vector2(i * (width + gap), 0);
            draw.AddRectFilled(min, min + new Vector2(width, height), U32(P.Raised), 10 * S);
            draw.AddRect(min, min + new Vector2(width, height), U32(CardBorder), 10 * S, ImDrawFlags.None, Math.Max(1, S));
            var at = min + new Vector2(12 * S, 12 * S);
            DactIcons.Draw(draw, tile.Icon, at + new Vector2(0, 1 * S), 14 * S, P.Muted);
            string label;
            using (DactFonts.Push(DactFonts.Small)) label = Fit(tile.Label, inner - 20 * S);
            DrawText(draw, at + new Vector2(20 * S, 0), 16 * S, label, P.Muted, DactFonts.Small);
            at.Y += 22 * S;
            var truncated = label != tile.Label;
            using (DactFonts.Push(DactFonts.H2))
            {
                // Long translated values end in an ellipsis; hovering the tile shows them in full.
                var value = Fit(tile.Value, inner);
                truncated |= value != tile.Value;
                var color = U32(DactTheme.Foreground(tile.Color));
                draw.AddText(at, color, value);
                if (tile.Bold) draw.AddText(at + new Vector2(MathF.Max(1, S * .8f), 0), color, value);
                at.Y += ImGui.GetTextLineHeight() + 6 * S;
            }
            using (DactFonts.Push(DactFonts.Small))
                draw.AddText(at, U32(DactTheme.Foreground(HintColor)), string.Join("\n", WrapLines(tile.Sub, inner)));
            if (truncated && ImGui.IsMouseHoveringRect(min, min + new Vector2(width, height)) && ImGui.IsWindowHovered())
                Tooltip(tile.Label + "\n" + tile.Value);
        }
    }

    // Starts the next item on the same line when it still fits, like a wrapping auto-layout row.
    public static void SameLineIfFits(float nextWidth, float gap = 8)
    {
        var lastRight = ImGui.GetItemRectMax().X;
        var limit = ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X;
        if (lastRight + gap * S + nextWidth <= limit) ImGui.SameLine(0, gap * S);
    }

    // ---- Containers

    // Card: Raised panel, 1 px border, radius 10, padding 14, gap 12, gold title.
    // This ImGui has no auto-sizing child windows, so a card is sized from the content height
    // measured last frame. Easing toward it also animates cards that grow or shrink.
    private static readonly Dictionary<uint, float> CardHeights = new();
    private static readonly Stack<(uint Key, float Padding, bool Visible, bool Highlighted, bool Titled)> OpenCards = new();

    // 全局搜索 jumps to a card and pulses its border (Enter 跳到该位置并高亮). The page scrolls it
    // into view on the first frame it is drawn.
    private static string? highlightedCard;
    private static double highlightedAt = double.NegativeInfinity;
    private static bool highlightScrollPending;
    private const double HighlightSeconds = 1.8;

    public static void HighlightCard(string id)
    {
        highlightedCard = id;
        highlightedAt = ImGui.GetTime();
        highlightScrollPending = true;
    }

    public static bool BeginCard(string id, string? title, float width = 0, float padding = 14, float gap = 12)
    {
        var key = ImGui.GetID(id);
        var height = CardHeights.TryGetValue(key, out var measured) ? DactMotion.Float(key, measured, DactMotion.Normal) : 0;
        var highlighted = id == highlightedCard && ImGui.GetTime() - highlightedAt < HighlightSeconds;
        if (highlighted && highlightScrollPending)
        {
            ImGui.SetScrollHereY(.15f);
            highlightScrollPending = false;
        }
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 10 * S);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, Math.Max(1, S));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(padding * S));
        ImGui.PushStyleColor(ImGuiCol.ChildBg, P.Raised);
        ImGui.PushStyleColor(ImGuiCol.Border, CardBorder);
        var visible = ImGui.BeginChild(id, new Vector2(width, Math.Max(1, height)), true, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8, gap) * S);
        OpenCards.Push((key, padding, visible, highlighted, title is not null));
        if (title is not null) Text(title, P.Gold);
        return true;
    }

    public static void EndCard()
    {
        var (key, padding, visible, highlighted, titled) = OpenCards.Pop();
        // A card scrolled fully out of view skips its items, so the cursor never advances; measuring
        // then would collapse the card and make the page jump while scrolling. Keep the last height.
        // The cursor sits one item gap below the last child; the card ends one padding below it.
        if (visible || !CardHeights.ContainsKey(key))
            CardHeights[key] = ImGui.GetCursorPosY() - ImGui.GetStyle().ItemSpacing.Y + padding * S;
        ImGui.PopStyleVar();
        ImGui.EndChild();
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(3);
        // Nameplate skins mark each titled card on its left rim at the title line.
        if (titled && P.Ultimate is { } plate)
            UltimateSkins.DrawCardMark(ImGui.GetWindowDrawList(), new Vector2(ImGui.GetItemRectMin().X, ImGui.GetItemRectMin().Y + padding * S + 11 * S), S, plate);
        if (!highlighted) return;
        // Two soft pulses that fade out over HighlightSeconds.
        var t = (float)((ImGui.GetTime() - highlightedAt) / HighlightSeconds);
        var pulse = (1 - t) * (.55f + .45f * MathF.Cos(t * MathF.PI * 4));
        ImGui.GetWindowDrawList().AddRect(ImGui.GetItemRectMin() - new Vector2(2 * S), ImGui.GetItemRectMax() + new Vector2(2 * S),
            U32(P.Accent with { W = Math.Clamp(pulse, 0, 1) }), 12 * S, ImDrawFlags.None, 2 * S);
    }

    // Moves to the right end of the card title line for buttons of the given total width.
    public static void CardTitleActions(float width)
    {
        ImGui.SameLine();
        var y = ImGui.GetCursorPosY();
        ImGui.SetCursorPos(new Vector2(ImGui.GetWindowContentRegionMax().X - width, y - (30 * S - ImGui.GetTextLineHeight()) * .5f));
    }

    // Page title (18), description below, actions on the right. Wide action groups (long
    // translations, narrow windows) go under the description instead of squeezing it.
    public static void PageHeader(string title, string description, float actionsWidth = 0, Action? actions = null)
    {
        var start = ImGui.GetCursorPos();
        var width = ImGui.GetContentRegionAvail().X;
        var stacked = actions is not null && width - actionsWidth - 12 * S < 260 * S;
        ImGui.BeginGroup();
        using (Gap(2))
        {
            var textWidth = width - (actionsWidth > 0 && !stacked ? actionsWidth + 12 * S : 0);
            Text(title, size: DactFonts.Title);
            Wrapped(description, HintColor, DactFonts.Small, textWidth);
        }
        ImGui.EndGroup();
        if (actions is null) return;
        if (stacked)
        {
            ImGui.BeginGroup();
            actions();
            ImGui.EndGroup();
            return;
        }
        var after = ImGui.GetCursorPos();
        var headerHeight = ImGui.GetItemRectSize().Y;
        ImGui.SetCursorPos(new Vector2(start.X + width - actionsWidth, start.Y + (headerHeight - 30 * S) * .5f));
        ImGui.BeginGroup();
        actions();
        ImGui.EndGroup();
        ImGui.SetCursorPos(after);
    }

    // Two equal columns (dsl grid2). Call NextColumn() once between the two halves.
    public static bool BeginGrid(string id, float gap = 14)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(gap * .5f * S, 0));
        var open = ImGui.BeginTable(id, 2, ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.NoPadOuterX);
        ImGui.PopStyleVar();
        if (!open) return false;
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        GridRowTops.Push(ImGui.GetCursorPosY());
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8, gap) * S);
        return true;
    }

    // A table starts every cell on the row's text baseline, which the first column's framed
    // controls (selects, inputs) have pushed down by their frame padding; text at the top of the
    // second cell would sit that much lower. An empty item resets the baseline, then the cursor
    // goes back to the row's top.
    public static void NextColumn()
    {
        ImGui.TableSetColumnIndex(1);
        if (!GridRowTops.TryPeek(out var top)) return;
        ImGui.Dummy(Vector2.Zero);
        ImGui.SetCursorPosY(top);
    }

    public static void EndGrid()
    {
        ImGui.PopStyleVar();
        ImGui.EndTable();
        GridRowTops.TryPop(out _);
    }

    private static readonly Stack<float> GridRowTops = new();

    // Stacked field: label, control, hint, 6 px apart.
    public static void BeginField(string label, string? trailing = null)
    {
        ImGui.BeginGroup();
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8, 6) * S);
        Text(label);
        if (trailing is null) return;
        ImGui.SameLine(ImGui.GetContentRegionMax().X - TextWidth(trailing));
        Text(trailing, P.Muted);
    }

    public static void EndField(string? hint = null)
    {
        if (!string.IsNullOrEmpty(hint)) Hint(hint);
        ImGui.PopStyleVar();
        ImGui.EndGroup();
    }

    // ---- Select (dsl select + dropdownMenu)

    // The field opens our own popup right under it, exactly as wide, fading in and sliding down.
    // ImGui's combo popup forced its own side padding (wider than the field, with a scrollbar)
    // and appeared without motion. Up to 8 rows show without scrolling. ImGui removes a popup
    // the frame it closes (a pick, a click elsewhere, Esc), so the list's last place is kept and
    // a copy that takes no input fades and slides back out there.
    private const int SelectVisibleRows = 8;
    private const float SelectCloseSeconds = .12f;

    private sealed class SelectState
    {
        public double OpenedAt;
        public double ClosingAt = -1;
        public Vector2 Position, Size;
        public float Scroll;
        public bool Below = true;
    }

    private static readonly Dictionary<uint, SelectState> Selects = new();

    // pill: the 28 px rounded shape of a chip, for a choice that sits in a row of chips.
    public static bool Select(string id, string[] items, ref int selected, float width = 0, bool disabled = false, bool pill = false)
    {
        width = width > 0 ? width : ImGui.GetContentRegionAvail().X;
        var height = (pill ? 28 : 30) * S;
        var rounding = (pill ? 14 : 6) * S;
        // The chevron is drawn over the right end, so a long label stops short of it.
        var preview = Fit(selected >= 0 && selected < items.Length ? items[selected] : "", width - 44 * S);
        var key = ImGui.GetID(id);
        var popupId = "##select-popup-" + id;
        var open = ImGui.IsPopupOpen(popupId);

        if (disabled) ImGui.BeginDisabled();
        var clicked = ImGui.InvisibleButton("##select-" + id, new Vector2(width, height));
        if (disabled) ImGui.EndDisabled();
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var hover = DactMotion.Float(key, !disabled && (ImGui.IsItemHovered() || open) ? 1 : 0, DactMotion.Fast);
        var alpha = disabled ? .45f : 1;
        var draw = ImGui.GetWindowDrawList();
        if (P.Light) DrawLightSelect(draw, min, max, preview, hover, open, disabled, pill ? 14 * S : 8 * S);
        else
        {
            var fill = Vector4.Lerp(Field, Vector4.Lerp(Field, P.Hover, .5f), hover);
            var border = open ? P.Accent : Vector4.Lerp(FieldBorder, P.Accent with { W = .45f }, hover * .6f);
            draw.AddRectFilled(min, max, U32(fill with { W = fill.W * alpha }), rounding);
            draw.AddRect(min, max, U32(border with { W = border.W * alpha }), rounding, ImDrawFlags.None, Math.Max(1, S));
            DrawText(draw, min + new Vector2((pill ? 12 : 10) * S, 0), height, preview, P.Text with { W = alpha });
            DactIcons.Draw(draw, open ? "chevronUp" : "chevronDown", new Vector2(max.X - 24 * S, min.Y + (height - 14 * S) * .5f), 14 * S,
                (open ? P.Accent : P.Muted) with { W = alpha });
        }
        FocusRing(min, max, pill ? 14 : P.Light ? 8 : 6);
        var now = ImGui.GetTime();
        Selects.TryGetValue(key, out var state);
        if (clicked && !disabled)
        {
            ImGui.OpenPopup(popupId);
            Selects[key] = state = new SelectState { OpenedAt = now };
        }
        else if (!open && state is not null)
        {
            if (state.ClosingAt < 0) state.ClosingAt = now;
            if (state.Size.X > 0 && now - state.ClosingAt < SelectCloseSeconds && !DactMotion.Disabled)
                DrawClosingSelect(id, state, items, selected, (float)((now - state.ClosingAt) / SelectCloseSeconds));
            else Selects.Remove(key);
            return false;
        }

        var changed = false;
        var rowHeight = 28 * S;
        var gap = 2 * S;
        var padding = 4 * S;
        var rows = Math.Clamp(items.Length, 1, SelectVisibleRows);
        var listHeight = rows * rowHeight + (rows - 1) * gap + padding * 2;
        if (open && state is null) Selects[key] = state = new SelectState { OpenedAt = now };
        var progress = DactMotion.Progress(state?.OpenedAt ?? now, .16f);
        // Below the field; above it when the screen has no room underneath.
        var viewport = ImGui.GetMainViewport();
        var below = max.Y + 4 * S + listHeight <= viewport.Pos.Y + viewport.Size.Y;
        var top = below ? max.Y + 4 * S - (1 - progress) * 6 * S : min.Y - 4 * S - listHeight + (1 - progress) * 6 * S;
        ImGui.SetNextWindowPos(new Vector2(min.X, top), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(width, listHeight), ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, Math.Max(.01f, progress));
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 8 * S);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupBorderSize, Math.Max(1, S));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(padding));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(4 * S, gap));
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize, 6 * S);
        ImGui.PushStyleColor(ImGuiCol.PopupBg, PaneFill(P.Raised));
        ImGui.PushStyleColor(ImGuiCol.Border, PaneBorder(P.Border));
        var flags = ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoSavedSettings |
                    (items.Length > SelectVisibleRows ? ImGuiWindowFlags.None : ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        if (ImGui.BeginPopup(popupId, flags))
        {
            DrawSkinPane();
            if (state is not null)
            {
                state.Position = ImGui.GetWindowPos();
                state.Size = ImGui.GetWindowSize();
                state.Scroll = ImGui.GetScrollY();
                state.Below = below;
            }
            var rowWidth = ImGui.GetContentRegionAvail().X;
            for (var index = 0; index < items.Length; index++)
            {
                if (!MenuItem(items[index] + "##select-item-" + index, index == selected, rowWidth)) continue;
                selected = index;
                changed = true;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(6);
        return changed;
    }

    // 艾欧泽亚 (redesigned, user: the chocolate pill was too ugly, and unreadable when disabled):
    // the game's Light-theme field, a cream inset with a tan rim that warms to orange on hover and
    // while open, dark brown text, and the arrow in a small round chocolate button on the right.
    public static readonly Vector4 LightCream = new(.988f, .953f, .890f, 1);
    public static readonly Vector4 LightCreamHover = new(1, .976f, .937f, 1);
    public static readonly Vector4 LightRim = new(.722f, .588f, .408f, 1);
    public static readonly Vector4 LightGlow = new(.886f, .553f, .204f, 1);

    private static void DrawLightSelect(ImDrawListPtr draw, Vector2 min, Vector2 max, string preview, float hover, bool open, bool disabled, float rounding)
    {
        var height = max.Y - min.Y;
        var alpha = disabled ? .55f : 1;
        var warm = disabled ? 0 : Math.Max(hover, open ? 1 : 0);
        var fill = Vector4.Lerp(LightCream, LightCreamHover, warm);
        draw.AddRectFilled(min, max, U32(fill with { W = disabled ? .7f : 1 }), rounding);
        // A thin shade under the top edge reads as the game's inset field.
        draw.AddRectFilled(min + new Vector2(2 * S, 1 * S), new Vector2(max.X - 2 * S, min.Y + 3 * S), U32(new Vector4(.45f, .32f, .18f, .10f * alpha)), 2 * S);
        draw.AddRect(min, max, U32(Vector4.Lerp(LightRim, LightGlow, warm) with { W = alpha }), rounding, ImDrawFlags.None, Math.Max(1, S) * (1 + warm * .4f));
        DrawText(draw, min + new Vector2(12 * S, 0), height, preview, (disabled ? P.Muted : P.Text) with { W = disabled ? .85f : 1 });
        var center = new Vector2(max.X - 17 * S, min.Y + height * .5f);
        var knob = Vector4.Lerp(new Vector4(.478f, .325f, .184f, 1), new Vector4(.584f, .400f, .220f, 1), warm);
        draw.AddCircleFilled(center, 10 * S, U32(knob with { W = alpha }), 24);
        draw.AddCircle(center + new Vector2(0, -.5f * S), 9.5f * S, U32(new Vector4(1, .93f, .80f, .35f * alpha)), 24, Math.Max(1, S));
        DactIcons.Draw(draw, open ? "chevronUp" : "chevronDown", center - new Vector2(6 * S), 12 * S, new Vector4(1, 1, 1, alpha));
    }

    // The list on its way out, where it last was: fading, sliding back toward the field, and
    // taking no input (the click that closed it has already been handled).
    private static void DrawClosingSelect(string id, SelectState state, string[] items, int selected, float progress)
    {
        var eased = DactMotion.EaseOutCubic(progress);
        ImGui.SetNextWindowPos(state.Position + new Vector2(0, (state.Below ? -6 : 6) * S * eased), ImGuiCond.Always);
        ImGui.SetNextWindowSize(state.Size, ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, Math.Max(.01f, 1 - eased));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 8 * S);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, Math.Max(1, S));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(4 * S));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, PaneFill(P.Raised));
        ImGui.PushStyleColor(ImGuiCol.Border, PaneBorder(P.Border));
        if (ImGui.Begin("##select-closing-" + id, ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoSavedSettings |
                ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoMove))
        {
            ImGuiP.BringWindowToDisplayFront(ImGuiP.GetCurrentWindow());
            DrawSkinPane();
            var draw = ImGui.GetWindowDrawList();
            var origin = ImGui.GetCursorScreenPos() - new Vector2(0, state.Scroll);
            var rowWidth = ImGui.GetContentRegionAvail().X;
            for (var index = 0; index < items.Length; index++)
            {
                var row = origin + new Vector2(0, index * 30 * S);
                if (index == selected) draw.AddRectFilled(row, row + new Vector2(rowWidth, 28 * S), U32(AccentSoft), 6 * S);
                DrawText(draw, row + new Vector2(10 * S, 0), 28 * S, Caption(items[index]), index == selected ? P.Accent : P.Text);
                if (index == selected) DactIcons.Draw(draw, "check", new Vector2(row.X + rowWidth - 24 * S, row.Y + 7 * S), 14 * S, P.Accent);
            }
        }
        ImGui.End();
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(4);
    }

    private static bool MenuItem(string label, bool selected, float width)
    {
        var size = new Vector2(width, 28 * S);
        var clicked = ImGui.InvisibleButton(label, size);
        var min = ImGui.GetItemRectMin();
        var hovered = ImGui.IsItemHovered();
        var draw = ImGui.GetWindowDrawList();
        var fill = DactMotion.Color(ImGuiP.GetItemID(), selected ? AccentSoft : P.Hover with { W = hovered ? 1 : 0 }, DactMotion.Fast);
        if (fill.W > 0) draw.AddRectFilled(min, min + size, U32(fill), 6 * S);
        DrawText(draw, min + new Vector2(10 * S, 0), size.Y, Caption(label), selected ? P.Accent : P.Text);
        if (selected) DactIcons.Draw(draw, "check", new Vector2(min.X + size.X - 24 * S, min.Y + 7 * S), 14 * S, P.Accent);
        if (selected && ImGui.IsWindowAppearing()) ImGui.SetScrollHereY();
        return clicked;
    }

    // ---- Panel: a card-styled child window with a fixed size that scrolls on its own
    // (master/detail lists such as 悬浮窗).

    public static bool BeginPanel(string id, Vector2 size, float padding = 14, float gap = 12)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 10 * S);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, Math.Max(1, S));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(padding * S));
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize, 8 * S);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, P.Raised);
        ImGui.PushStyleColor(ImGuiCol.Border, CardBorder);
        var visible = ImGui.BeginChild(id, size, true);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8, gap) * S);
        return visible;
    }

    public static void EndPanel()
    {
        ImGui.PopStyleVar();
        ImGui.EndChild();
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(4);
    }

    // ---- Tabs (dsl tabs): labels 18 px apart with an accent underline that slides.

    public static bool Tabs(string id, string[] items, ref int active)
    {
        var height = 34 * S;
        var origin = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var draw = ImGui.GetWindowDrawList();
        var lefts = new float[items.Length];
        var widths = items.Select(item => TextWidth(item)).ToArray();
        var x = 0f;
        for (var i = 0; i < items.Length; i++) { lefts[i] = x; x += widths[i] + 18 * S; }
        var index = Math.Clamp(active, 0, items.Length - 1);
        var line = DactMotion.Point(ImGui.GetID(id), new Vector2(lefts[index], widths[index]), DactMotion.Normal);
        draw.AddRectFilled(origin + new Vector2(line.X, height - 2 * S), origin + new Vector2(line.X + line.Y, height), U32(P.Accent), S);
        var changed = false;
        for (var i = 0; i < items.Length; i++)
        {
            ImGui.SetCursorScreenPos(origin + new Vector2(lefts[i], 0));
            if (ImGui.InvisibleButton($"{id}-{i}", new Vector2(widths[i], height)) && i != active) { active = i; changed = true; }
            var ink = DactMotion.Color(ImGuiP.GetItemID(), i == active || ImGui.IsItemHovered() ? P.Text : P.Muted, DactMotion.Fast);
            DrawText(draw, origin + new Vector2(lefts[i], 0), height - 7 * S, items[i], ink);
        }
        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(new Vector2(width, height));
        return changed;
    }

    public static bool SliderFloat(string id, ref float value, float min, float max, string format = "0.00", float trackWidth = 150)
    {
        // Integer steps of 1/100 keep the drag smooth and the shown value exact.
        var scaled = (int)MathF.Round(value * 100);
        if (!SliderInt(id, ref scaled, (int)MathF.Round(min * 100), (int)MathF.Round(max * 100), value.ToString(format), trackWidth)) return false;
        value = scaled / 100f;
        return true;
    }

    // One action row in a popup menu (dsl MenuItem, normal/hover).
    public static bool MenuRow(string label, bool danger = false, float width = 0)
    {
        width = width > 0 ? width : Math.Max(ImGui.GetContentRegionAvail().X, TextWidth(label) + 24 * S);
        var size = new Vector2(width, 28 * S);
        var clicked = ImGui.InvisibleButton("##menu-" + label, size);
        var min = ImGui.GetItemRectMin();
        var fill = DactMotion.Color(ImGuiP.GetItemID(), P.Hover with { W = ImGui.IsItemHovered() ? 1 : 0 }, DactMotion.Fast);
        var draw = ImGui.GetWindowDrawList();
        if (fill.W > 0) draw.AddRectFilled(min, min + size, U32(fill), 6 * S);
        DrawText(draw, min + new Vector2(10 * S, 0), size.Y, label, danger ? Danger : P.Text);
        return clicked;
    }

    // Styles a popup like the design's dropdown menu: Raised, radius 8, 4 px padding.
    // padding: 4 for menus of rows; content popups (permissions) use the card padding instead.
    // Popups fade in over 160 ms from the frame ImGui first reports them open.
    private static readonly Dictionary<uint, double> MenuOpenedAt = new();

    public static bool BeginMenuPopup(string id, float width = 0, float padding = 4)
    {
        var key = ImGui.GetID(id);
        if (!ImGui.IsPopupOpen(id))
        {
            MenuOpenedAt.Remove(key);
            return false;
        }
        if (!MenuOpenedAt.TryGetValue(key, out var openedAt)) MenuOpenedAt[key] = openedAt = ImGui.GetTime();
        if (width > 0) ImGui.SetNextWindowSize(new Vector2(width, 0));
        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, Math.Max(.01f, DactMotion.Progress(openedAt, .16f)));
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, (padding > 8 ? 10 : 8) * S);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupBorderSize, Math.Max(1, S));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(padding * S));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(4, 2) * S);
        ImGui.PushStyleColor(ImGuiCol.PopupBg, PaneFill(P.Raised));
        ImGui.PushStyleColor(ImGuiCol.Border, PaneBorder(P.Border));
        var open = ImGui.BeginPopup(id);
        // Under the fade still pushed, so the pane fades in with the menu.
        if (open) DrawSkinPane();
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(5);
        return open;
    }

    // ---- Segmented control (dsl segmented): inset track, the active segment slides.

    public static bool Segmented(string id, string[] items, ref int active, float width = 0)
    {
        var height = 32 * S;
        var widths = items.Select(item => TextWidth(item) + 24 * S).ToArray();
        var natural = widths.Sum() + 2 * S * (items.Length - 1) + 6 * S;
        var total = width > 0 ? width : natural;
        // With a fixed width the segments share it equally (dsl: w "fill").
        if (width > 0) widths = Enumerable.Repeat((total - 6 * S - 2 * S * (items.Length - 1)) / items.Length, items.Length).ToArray();
        var min = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(min, min + new Vector2(total, height), U32(Field), 8 * S);
        draw.AddRect(min, min + new Vector2(total, height), U32(FieldBorder), 8 * S, ImDrawFlags.None, Math.Max(1, S));
        var lefts = new float[items.Length];
        var x = 3 * S;
        for (var i = 0; i < items.Length; i++) { lefts[i] = x; x += widths[i] + 2 * S; }
        var key = ImGui.GetID(id);
        var slide = DactMotion.Point(key, new Vector2(lefts[Math.Clamp(active, 0, items.Length - 1)], widths[Math.Clamp(active, 0, items.Length - 1)]), DactMotion.Normal);
        draw.AddRectFilled(min + new Vector2(slide.X, 3 * S), min + new Vector2(slide.X + slide.Y, 29 * S), U32(P.Hover), 6 * S);
        var changed = false;
        for (var i = 0; i < items.Length; i++)
        {
            ImGui.SetCursorScreenPos(min + new Vector2(lefts[i], 3 * S));
            if (ImGui.InvisibleButton($"{id}-{i}", new Vector2(widths[i], 26 * S)) && i != active) { active = i; changed = true; }
            var hovered = ImGui.IsItemHovered();
            var ink = DactMotion.Color(ImGuiP.GetItemID(), i == active || hovered ? P.Text : P.Muted, DactMotion.Fast);
            // Equal-width segments shorten long translations; the tooltip shows the full label.
            var label = Fit(items[i], widths[i] - 12 * S);
            if (label != items[i] && hovered) Tooltip(items[i]);
            DrawText(draw, min + new Vector2(lefts[i] + (widths[i] - TextWidth(label)) * .5f, 3 * S), 26 * S, label, ink);
        }
        ImGui.SetCursorScreenPos(min);
        ImGui.Dummy(new Vector2(total, height));
        return changed;
    }

    // ---- Slider (dsl slider): 4 px rail, accent fill, 14 px knob, value text on the right.

    public static bool SliderInt(string id, ref int value, int min, int max, string valueText, float trackWidth = 150)
    {
        var track = new Vector2(trackWidth * S, 30 * S);
        var origin = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton(id, track);
        var active = ImGui.IsItemActive();
        var hovered = ImGui.IsItemHovered();
        var changed = false;
        if (active)
        {
            var t = Math.Clamp((ImGui.GetIO().MousePos.X - origin.X - 7 * S) / (track.X - 14 * S), 0, 1);
            var next = (int)MathF.Round(min + t * (max - min));
            if (next != value) { value = next; changed = true; }
        }
        if (hovered || active) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        var fraction = max > min ? (value - min) / (float)(max - min) : 0;
        var shown = DactMotion.Float(ImGuiP.GetItemID(), fraction, active ? 60 : DactMotion.Fast);
        var draw = ImGui.GetWindowDrawList();
        var railY = origin.Y + track.Y * .5f;
        var knobX = origin.X + 7 * S + shown * (track.X - 14 * S);
        draw.AddRectFilled(new Vector2(origin.X, railY - 2 * S), new Vector2(origin.X + track.X, railY + 2 * S), U32(P.Hover), 2 * S);
        draw.AddRectFilled(new Vector2(origin.X, railY - 2 * S), new Vector2(Math.Max(origin.X + 6 * S, knobX), railY + 2 * S), U32(P.Accent), 2 * S);
        draw.AddCircleFilled(new Vector2(knobX, railY), (active ? 8 : 7) * S, U32(P.Text), 20);
        FocusRing(origin, origin + track, 6);
        ImGui.SameLine(0, 12 * S);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (track.Y - ImGui.GetTextLineHeight()) * .5f);
        ImGui.PushStyleColor(ImGuiCol.Text, DactTheme.Foreground(P.Muted));
        ImGui.TextUnformatted(valueText);
        ImGui.PopStyleColor();
        return changed;
    }

    // ---- Text input (dsl input): inset field, placeholder, optional show/hide for secrets.

    private static readonly HashSet<uint> RevealedSecrets = new();

    // submitOnEnter: returns true only when Enter is pressed while typing in the field (a chat
    // line), not on every edit; polling ImGui's Enter instead could fire for a key it wrongly
    // thought was still held (see Plugin.ReleaseStuckKeys).
    public static bool Input(string id, ref string value, string placeholder, int maxLength = 256, bool secret = false, float width = 0,
        bool readOnly = false, bool submitOnEnter = false)
    {
        width = width > 0 ? width : ImGui.GetContentRegionAvail().X;
        var key = ImGui.GetID(id);
        var revealed = RevealedSecrets.Contains(key);
        var line = ImGui.GetTextLineHeight();
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(10 * S, (30 * S - line) * .5f));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6 * S);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, Math.Max(1, S));
        // The border eases to accent while typing (last frame's focus, as ImGui reports it after
        // the item); easing hides the one frame a chat field spends unfocused between messages.
        var border = DactMotion.Color(key, FocusedInputs.Contains(key) ? P.Accent with { W = .8f } : FieldBorder, DactMotion.Fast);
        var frameMin = ImGui.GetCursorScreenPos();
        if (secret)
        {
            // One framed box: the borderless text input on the left, the show/hide eye inside on the right.
            var draw = ImGui.GetWindowDrawList();
            draw.AddRectFilled(frameMin, frameMin + new Vector2(width, 30 * S), U32(Field), 6 * S);
            draw.AddRect(frameMin, frameMin + new Vector2(width, 30 * S), U32(border), 6 * S, ImDrawFlags.None, Math.Max(1, S));
        }
        ImGui.PushStyleColor(ImGuiCol.FrameBg, secret ? Vector4.Zero : Field);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, secret ? Vector4.Zero : Field);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, secret ? Vector4.Zero : Field);
        ImGui.PushStyleColor(ImGuiCol.TextDisabled, P.Muted);
        ImGui.PushStyleColor(ImGuiCol.Border, secret ? Vector4.Zero : border);
        ImGui.SetNextItemWidth(secret ? width - 30 * S : width);
        var flags = (secret && !revealed ? ImGuiInputTextFlags.Password : ImGuiInputTextFlags.None) |
                    (readOnly ? ImGuiInputTextFlags.ReadOnly : ImGuiInputTextFlags.None) |
                    (submitOnEnter ? ImGuiInputTextFlags.EnterReturnsTrue : ImGuiInputTextFlags.None);
        var changed = ImGui.InputTextWithHint("##" + id, placeholder, ref value, maxLength, flags);
        if (ImGui.IsItemActive()) FocusedInputs.Add(key); else FocusedInputs.Remove(key);
        ImGui.PopStyleColor(5);
        ImGui.PopStyleVar(3);
        if (!secret) return changed;
        // Same line as the input, so the row keeps the field's height (restoring the cursor
        // afterwards would extend the enclosing group by one item gap).
        ImGui.SameLine(0, 0);
        ImGui.SetCursorScreenPos(frameMin + new Vector2(width - 29 * S, 2 * S));
        if (ImGui.InvisibleButton("##reveal-" + id, new Vector2(26 * S)))
        {
            if (revealed) RevealedSecrets.Remove(key); else RevealedSecrets.Add(key);
        }
        var eyeInk = DactMotion.Color(ImGuiP.GetItemID(), ImGui.IsItemHovered() ? P.Text : P.Muted, DactMotion.Fast);
        DactIcons.Draw(ImGui.GetWindowDrawList(), revealed ? "close" : "eye", ImGui.GetItemRectMin() + new Vector2(6 * S), 14 * S, eyeInk);
        return changed;
    }

    private static readonly HashSet<uint> FocusedInputs = new();

    // Integer field in the same inset style (e.g. seconds, limits).
    public static bool NumberInput(string id, ref int value, int min, int max, float width = 110)
    {
        var line = ImGui.GetTextLineHeight();
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(10 * S, (30 * S - line) * .5f));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6 * S);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, Math.Max(1, S));
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Field);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, Field);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, Field);
        ImGui.PushStyleColor(ImGuiCol.Border, FieldBorder);
        ImGui.PushStyleColor(ImGuiCol.Button, P.Hover);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Vector4.Lerp(P.Hover, P.Accent, .15f));
        ImGui.SetNextItemWidth(width * S);
        var next = value;
        var changed = ImGui.InputInt("##" + id, ref next);
        ImGui.PopStyleColor(6);
        ImGui.PopStyleVar(3);
        next = Math.Clamp(next, min, max);
        if (!changed || next == value) return false;
        value = next;
        return true;
    }

    // Room left for a title-line pill; Pill() limits itself to it while CardTitleRow draws extras.
    private static float extrasLimit;

    // Card title line: gold title, optional extras right after it (e.g. a status pill) and
    // actions pinned to the right, all centred on one row. When a long translation would make
    // them collide, the actions move to their own line under the title.
    public static void CardTitleRow(string title, Action? extras = null, float actionsWidth = 0, Action? actions = null)
    {
        var width = ImGui.GetContentRegionAvail().X;
        var reserve = extras is null ? 0 : 90 * S;
        var stacked = actions is not null && TextWidth(title) + reserve + 12 * S + actionsWidth > width;
        var rowHeight = actions is null || stacked ? 22 * S : 30 * S;
        var start = ImGui.GetCursorScreenPos();
        var shownTitle = Fit(title, width - reserve - (actions is null || stacked ? 0 : actionsWidth + 12 * S));
        DrawText(ImGui.GetWindowDrawList(), start, rowHeight, shownTitle, P.Gold);
        if (extras is not null)
        {
            var titleRight = TextWidth(shownTitle) + 8 * S;
            ImGui.SetCursorScreenPos(start + new Vector2(titleRight, (rowHeight - 22 * S) * .5f));
            extrasLimit = Math.Max(40 * S, width - titleRight - (actions is null || stacked ? 0 : actionsWidth + 12 * S));
            extras();
            extrasLimit = 0;
        }
        if (actions is not null)
        {
            var actionsTop = stacked ? start.Y + rowHeight + ImGui.GetStyle().ItemSpacing.Y : start.Y + (rowHeight - 30 * S) * .5f;
            ImGui.SetCursorScreenPos(new Vector2(stacked ? start.X : start.X + width - actionsWidth, actionsTop));
            ImGui.BeginGroup();
            actions();
            ImGui.EndGroup();
            if (stacked)
            {
                // The actions row is a real item, so the next child starts one gap below it.
                ImGui.SetCursorScreenPos(start);
                ImGui.Dummy(new Vector2(width, actionsTop - start.Y + 30 * S));
                return;
            }
        }
        ImGui.SetCursorScreenPos(start);
        ImGui.Dummy(new Vector2(width, rowHeight));
    }

    // Muted label followed by a switch, on a 30 px row (page-header actions).
    public static float InlineToggleWidth(string label) => TextWidth(label) + 8 * S + 34 * S;

    public static bool InlineToggle(string label, ref bool value)
    {
        var start = ImGui.GetCursorScreenPos();
        DrawText(ImGui.GetWindowDrawList(), start, 30 * S, label, P.Muted);
        ImGui.SetCursorScreenPos(start + new Vector2(TextWidth(label) + 8 * S, 5 * S));
        var changed = Toggle("##inline-" + label, ref value);
        ImGui.SetCursorScreenPos(start);
        ImGui.Dummy(new Vector2(InlineToggleWidth(label), 30 * S));
        return changed;
    }

    // ---- Disclosure + steps panel (dsl disclosure / stepsPanel)

    public static bool Disclosure(string label, ref bool open)
    {
        var height = 24 * S;
        var width = TextWidth(label) + 20 * S;
        var clicked = ImGui.InvisibleButton("##disclosure-" + label, new Vector2(width, height));
        if (clicked) open = !open;
        var min = ImGui.GetItemRectMin();
        var draw = ImGui.GetWindowDrawList();
        var turn = DactMotion.Float(ImGuiP.GetItemID(), open ? 1 : 0, DactMotion.Normal);
        var ink = ImGui.IsItemHovered() ? Vector4.Lerp(P.Accent, P.Text, .25f) : P.Accent;
        // The chevron rotates from right to down instead of swapping icons.
        var center = min + new Vector2(7 * S, height * .5f);
        var angle = turn * MathF.PI * .5f;
        Vector2 Rotate(Vector2 p) => center + new Vector2(p.X * MathF.Cos(angle) - p.Y * MathF.Sin(angle), p.X * MathF.Sin(angle) + p.Y * MathF.Cos(angle)) * S;
        draw.PathLineTo(Rotate(new Vector2(-2, -4)));
        draw.PathLineTo(Rotate(new Vector2(2, 0)));
        draw.PathLineTo(Rotate(new Vector2(-2, 4)));
        draw.PathStroke(U32(ink), ImDrawFlags.None, Math.Max(1, 1.5f * S));
        DrawText(draw, min + new Vector2(20 * S, 0), height, label, ink);
        FocusRing(min, min + new Vector2(width, height), 4);
        return clicked;
    }

    public static void StepsPanel(IReadOnlyList<string> steps)
    {
        var width = ImGui.GetContentRegionAvail().X;
        var inner = width - 24 * S - 26 * S;
        var origin = ImGui.GetCursorScreenPos();
        var rows = steps.Select(step => string.Join("\n", WrapLines(step, inner))).ToArray();
        var heights = rows.Select(row => Math.Max(18 * S, ImGui.CalcTextSize(row).Y)).ToArray();
        var height = 20 * S + heights.Sum() + 8 * S * (rows.Length - 1);
        ImGui.Dummy(new Vector2(width, height));
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(origin, origin + new Vector2(width, height), U32(Field), 8 * S);
        draw.AddRect(origin, origin + new Vector2(width, height), U32(FieldBorder), 8 * S, ImDrawFlags.None, Math.Max(1, S));
        var y = origin.Y + 10 * S;
        for (var i = 0; i < rows.Length; i++)
        {
            var badge = new Vector2(origin.X + 12 * S, y);
            draw.AddCircleFilled(badge + new Vector2(9 * S), 9 * S, U32(P.Hover), 20);
            var number = (i + 1).ToString();
            DrawText(draw, badge + new Vector2(9 * S - TextWidth(number, DactFonts.Tiny) * .5f, 0), 18 * S, number, P.Muted, DactFonts.Tiny);
            draw.AddText(new Vector2(origin.X + 38 * S, y + (18 * S - ImGui.GetTextLineHeight()) * .5f), U32(DactTheme.Foreground(P.Text)), rows[i]);
            y += heights[i] + 8 * S;
        }
    }

    // ---- Path field: a read-only inset box that shortens long paths from the left (the folder
    // names at the end matter most); the full path is in the tooltip.

    public static void PathField(string path, float width = 0)
    {
        width = width > 0 ? width : ImGui.GetContentRegionAvail().X;
        var min = ImGui.GetCursorScreenPos();
        var size = new Vector2(width, 30 * S);
        ImGui.Dummy(size);
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(min, min + size, U32(Field), 6 * S);
        draw.AddRect(min, min + size, U32(FieldBorder), 6 * S, ImDrawFlags.None, Math.Max(1, S));
        var room = width - 20 * S;
        var shown = path;
        if (ImGui.CalcTextSize(shown).X > room)
        {
            var tail = path;
            while (tail.Length > 1 && ImGui.CalcTextSize("…" + tail).X > room) tail = tail[1..];
            shown = "…" + tail;
        }
        DrawText(draw, min + new Vector2(10 * S, 0), size.Y, shown, P.Text);
        if (ImGui.IsItemHovered() && shown != path) Tooltip(path);
    }

    // ---- Centred dialogs. Confirmations sit in the middle of the DACT window over a scrim that
    // covers only that window (design 设置 · 恢复出厂确认 and friends). The panel fades and rises in;
    // CloseDialog fades it out before ImGui closes the popup.

    private sealed class DialogState
    {
        public double OpenedAt;
        public double ClosingAt = -1;
    }

    private static readonly Dictionary<string, DialogState> Dialogs = new();
    private static float scrimThisFrame;
    private const float DialogCloseSeconds = .14f;

    // Window the dialogs centre on; the shell sets it every frame.
    public static Vector2 HostMin { get; set; }
    public static Vector2 HostMax { get; set; }

    // Highest dialog opacity drawn this frame; the shell paints the scrim with it.
    public static float ScrimLevel { get; private set; }

    public static void BeginFrameDialogs()
    {
        ScrimLevel = scrimThisFrame;
        scrimThisFrame = 0;
    }

    // ---- Anchored popover (status editor, friend note, scene overlay picker): opens at its
    // anchor (below a button), fades and rises in like a dialog but has no scrim and closes on an
    // outside click. Centred confirmations use the dialog below instead (user rule).
    private static readonly Dictionary<string, (Vector2 Anchor, double OpenedAt, bool AlignRight)> Popovers = new();

    public static void OpenPopover(string id, Vector2 anchor, bool alignRight = false)
    {
        ImGui.OpenPopup(id);
        Popovers[id] = (anchor, ImGui.GetTime(), alignRight);
    }

    public static bool BeginPopover(string id, float width)
    {
        if (!ImGui.IsPopupOpen(id, ImGuiPopupFlags.None))
        {
            Popovers.Remove(id);
            return false;
        }
        var (anchor, openedAt, alignRight) = Popovers.TryGetValue(id, out var placed) ? placed : (ImGui.GetMousePos(), ImGui.GetTime(), false);
        var progress = DactMotion.Progress(openedAt, DactMotion.DialogSeconds);
        var size = width * S;
        var x = alignRight ? anchor.X - size : anchor.X;
        // Stay inside the DACT window so a popover near its edge never hangs off it.
        if (HostMax.X > HostMin.X) x = Math.Clamp(x, HostMin.X + 8 * S, Math.Max(HostMin.X + 8 * S, HostMax.X - size - 8 * S));
        ImGui.SetNextWindowPos(new Vector2(x, anchor.Y + (1 - progress) * 6 * S), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(size, 0));
        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, Math.Max(.01f, progress));
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 10 * S);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupBorderSize, Math.Max(1, S));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(12 * S));
        ImGui.PushStyleColor(ImGuiCol.PopupBg, PaneFill(P.Raised));
        ImGui.PushStyleColor(ImGuiCol.Border, PaneBorder(P.Border));
        var open = ImGui.BeginPopup(id, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings);
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(3);
        if (!open)
        {
            ImGui.PopStyleVar();
            Popovers.Remove(id);
            return false;
        }
        DrawSkinPane();
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8, 10) * S);
        return true;
    }

    public static void EndPopover()
    {
        ImGui.PopStyleVar();
        ImGui.EndPopup();
        ImGui.PopStyleVar();
    }

    public static void OpenDialog(string id)
    {
        ImGui.OpenPopup(id);
        Dialogs[id] = new DialogState { OpenedAt = ImGui.GetTime() };
    }

    public static void CloseDialog(string id)
    {
        if (Dialogs.TryGetValue(id, out var state) && state.ClosingAt < 0) state.ClosingAt = ImGui.GetTime();
    }

    // How far a dialog has faded in (0 closed … 1 open); windows that paint their own scrim use it.
    public static float DialogOpacity(string id)
    {
        if (!Dialogs.TryGetValue(id, out var state)) return 0;
        return state.ClosingAt >= 0
            ? 1 - DactMotion.Progress(state.ClosingAt, DialogCloseSeconds)
            : DactMotion.Progress(state.OpenedAt, DactMotion.DialogSeconds);
    }

    // top >= 0 hangs the dialog that far below the host's top edge (全局搜索) instead of centring it.
    // sharedScrim = false: a window other than the control center paints its own scrim, so the
    // control center behind it is not dimmed as well.
    // dismissible: a click on the scrim (the dimmed DACT window around it) closes it like 取消.
    public static bool BeginDialog(string id, float width = 420, float top = -1, float padding = 20, bool sharedScrim = true,
        bool dismissible = true)
    {
        if (!ImGui.IsPopupOpen(id, ImGuiPopupFlags.None))
        {
            Dialogs.Remove(id);
            return false;
        }
        if (!Dialogs.TryGetValue(id, out var state)) Dialogs[id] = state = new DialogState { OpenedAt = ImGui.GetTime() };
        var progress = state.ClosingAt >= 0
            ? 1 - DactMotion.Progress(state.ClosingAt, DialogCloseSeconds)
            : DactMotion.Progress(state.OpenedAt, DactMotion.DialogSeconds);
        if (sharedScrim) scrimThisFrame = Math.Max(scrimThisFrame, progress);
        var viewport = ImGui.GetMainViewport();
        var center = HostMax.X > HostMin.X ? (HostMin + HostMax) * .5f : viewport.Pos + viewport.Size * .5f;
        if (top >= 0 && HostMax.X > HostMin.X)
            ImGui.SetNextWindowPos(new Vector2(center.X, HostMin.Y + top * S + (1 - progress) * 10 * S), ImGuiCond.Always, new Vector2(.5f, 0));
        else
            ImGui.SetNextWindowPos(center + new Vector2(0, (1 - progress) * 10 * S), ImGuiCond.Always, new Vector2(.5f));
        ImGui.SetNextWindowSize(new Vector2(width * S, 0));
        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, Math.Max(.01f, progress));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 12 * S);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(padding * S));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, Math.Max(1, S));
        ImGui.PushStyleColor(ImGuiCol.PopupBg, PaneFill(P.Raised));
        ImGui.PushStyleColor(ImGuiCol.Border, PaneBorder(P.Border));
        var open = ImGui.BeginPopupModal(id, ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
                                             ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.AlwaysAutoResize);
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(3);
        if (!open)
        {
            ImGui.PopStyleVar();
            return false;
        }
        DrawSkinPane();
        SuppressFullScreenDim();
        if (dismissible && state.ClosingAt < 0 && ScrimClicked(state)) state.ClosingAt = ImGui.GetTime();
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8, 12) * S);
        return true;
    }

    // A press on the scrim: inside the DACT window, on no window the modal lets through (ImGui
    // clears the hovered window for everything the modal blocks; its own dropdowns stay
    // hovered). The click that opened the dialog was hovering the page, so it never counts.
    private static unsafe bool ScrimClicked(DialogState state)
    {
        if (!ImGui.IsMouseClicked(ImGuiMouseButton.Left) || ImGui.GetTime() - state.OpenedAt < .1) return false;
        if (ImGui.GetCurrentContext().HoveredWindow.Handle != null) return false;
        var mouse = ImGui.GetMousePos();
        return HostMax.X > HostMin.X && mouse.X >= HostMin.X && mouse.Y >= HostMin.Y && mouse.X < HostMax.X && mouse.Y < HostMax.Y;
    }

    // ImGui dims the whole game screen behind a modal in Render(), after every plugin's style
    // pushes are popped, so a transparent ModalWindowDimBg pushed around Begin never reaches it.
    // While one of our dialogs is the top modal, the DACT scrim alone dims (only the DACT window);
    // a modal from another plugin on top keeps its own full-screen dim.
    private static unsafe void SuppressFullScreenDim()
    {
        if (ImGuiP.GetTopMostAndVisiblePopupModal().Handle == ImGuiP.GetCurrentWindow().Handle)
            ImGui.GetCurrentContext().DimBgRatio = 0;
    }

    public static void EndDialog(string id)
    {
        ImGui.PopStyleVar();
        if (Dialogs.TryGetValue(id, out var state) && state.ClosingAt >= 0 && DactMotion.Progress(state.ClosingAt, DialogCloseSeconds) >= 1)
        {
            ImGui.CloseCurrentPopup();
            Dialogs.Remove(id);
        }
        ImGui.EndPopup();
        ImGui.PopStyleVar();
    }

    // Covers the current window with the dialog scrim. Called last in the window, inside a child
    // that is submitted after the page so it lies above the page but below the dialog.
    public static void DrawScrim(float rounding = 10)
    {
        if (ScrimLevel <= 0) return;
        var min = ImGui.GetWindowPos();
        var max = min + ImGui.GetWindowSize();
        ImGui.SetCursorPos(Vector2.Zero);
        ImGui.BeginChild("##dact-scrim", ImGui.GetWindowSize(), false,
            ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoSavedSettings);
        ImGui.GetWindowDrawList().AddRectFilled(min, max, U32(new Vector4(.016f, .024f, .035f, .62f * ScrimLevel)), rounding * S);
        ImGui.EndChild();
    }

    // ---- Helpers

    private static string Caption(string label) => label.Split("##", 2)[0];

    private static bool UsesSkinButtons() => P.Light || P.Glass || P.Ultimate is not null;

    // Mouse clicks also give ImGui focus; only keyboard/gamepad navigation shows the ring.
    private static void FocusRing(Vector2 min, Vector2 max, float radius)
    {
        if (ImGui.IsItemFocused() && !ImGui.GetCurrentContext().NavDisableHighlight)
            ImGui.GetWindowDrawList().AddRect(min - new Vector2(2 * S), max + new Vector2(2 * S), U32(P.Accent), (radius + 2) * S);
    }
}

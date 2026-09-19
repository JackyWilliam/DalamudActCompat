using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace DalamudActCompat.UI;

internal sealed record SkinPalette(Vector4 Surface, Vector4 Raised, Vector4 Hover,
    Vector4 Gold, Vector4 Accent, Vector4 Text, Vector4 Muted, Vector4 Border, bool Light = false, bool Glass = false);

internal static class DactTheme
{
    public static unsafe void PreparePopupPosition(string id)
    {
        var context = ImGui.GetCurrentContext();
        var depth = context.BeginPopupStack.Size;
        if (context.OpenPopupStack.Size <= depth) return;
        var popup = context.OpenPopupStack[depth];
        if (popup.PopupId != ImGui.GetID(id) || popup.Window == null) return;

        // Auto-sized popups can grow after a save. Clamp the next fitted size before
        // Begin emits background/scrollbar vertices so the frame and content move together.
        var window = new ImGuiWindowPtr(popup.Window);
        var viewport = ImGui.GetMainViewport();
        var size = ImGuiP.CalcWindowNextAutoFitSize(window);
        var minimum = viewport.WorkPos + new Vector2(8);
        var maximum = Vector2.Max(minimum, viewport.WorkPos + viewport.WorkSize - size - new Vector2(8));
        ImGui.SetNextWindowPos(Vector2.Clamp(window.Pos, minimum, maximum));
    }

    private static Vector4 Rgb(uint rgb) => new(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, 1);
    private static readonly SkinPalette DefaultPalette = new(
        new(.035f, .048f, .068f, 1), new(.070f, .095f, .125f, 1), new(.105f, .145f, .185f, 1),
        new(.78f, .66f, .36f, 1), new(.42f, .78f, .96f, 1), Vector4.One, new(.65f, .70f, .76f, 1), new(.34f, .29f, .18f, 1));

    public static GameSkinAssets? GameAssets { get; set; }
    public static LiquidGlassRenderer? GlassRenderer { get; set; }
    public static string CurrentSkin { get; private set; } = SkinCatalog.Default;
    public static SkinPalette Palette { get; private set; } = DefaultPalette;
    private static readonly ImGuiCol[] FrameSlots = [ImGuiCol.WindowBg, ImGuiCol.ChildBg, ImGuiCol.PopupBg, ImGuiCol.Border, ImGuiCol.Separator,
        ImGuiCol.FrameBg, ImGuiCol.FrameBgHovered, ImGuiCol.FrameBgActive, ImGuiCol.Button,
        ImGuiCol.ButtonHovered, ImGuiCol.ButtonActive, ImGuiCol.Header, ImGuiCol.HeaderHovered,
        ImGuiCol.HeaderActive, ImGuiCol.CheckMark, ImGuiCol.SliderGrab, ImGuiCol.SliderGrabActive,
        ImGuiCol.Text, ImGuiCol.TextDisabled];
    public static SkinPalette For(string id) => id switch
    {
        // Content colors accompany the original Light-theme textures loaded locally.
        // No extracted game textures are distributed in the plugin package.
        SkinCatalog.Eorzea => new(Rgb(0xF4DAB5), Rgb(0xF9E6C8), Rgb(0xE8CAA0), Rgb(0x765534),
            Rgb(0x805228), Rgb(0x493728), Rgb(0x74604C), Rgb(0xA3865A), true),
        SkinCatalog.Jade => new(Rgb(0x0D1C1B), Rgb(0x142B28), Rgb(0x203D34), Rgb(0xC9BA89),
            Rgb(0x74DCC1), Rgb(0xEDF8F1), Rgb(0x9EBFB5), Rgb(0x456957)),
        SkinCatalog.Amethyst => new(Rgb(0x1A1428), Rgb(0x261E37), Rgb(0x392B4F), Rgb(0xCCB8DB),
            Rgb(0xC5A4FF), Rgb(0xF4EFFB), Rgb(0xB9A8C9), Rgb(0x69557C)),
        SkinCatalog.Amber => new(Rgb(0x231913), Rgb(0x35261C), Rgb(0x4B3525), Rgb(0xE7B771),
            Rgb(0xFFC988), Rgb(0xFFF3E6), Rgb(0xC7AD95), Rgb(0x805C39)),
        // Keep the requested fluorescent accent exact; darker pink surfaces
        // preserve text contrast across long settings pages and dialogs.
        SkinCatalog.NeonPink => new(Rgb(0x271023), Rgb(0x3D1232), Rgb(0x602049), Rgb(0xFF91CE),
            Rgb(0xFE1493), Rgb(0xFFF0F8), Rgb(0xDFA6C9), Rgb(0xBD256F)),
        // Dark foregrounds sit on a milky body even when GPU glass is unavailable.
        SkinCatalog.LiquidGlass => new(Rgb(0xF9FBFF) with { W = .78f }, Rgb(0xFFFFFF) with { W = .20f },
            Rgb(0xFFFFFF) with { W = .38f }, Rgb(0x293B4D), Rgb(0x174A6B),
            Rgb(0x14191F), Rgb(0x3D4955), Rgb(0x596D80) with { W = .28f }, Glass: true),
        // Keep the canvas genuinely black; only interactive states lift into gray.
        SkinCatalog.Obsidian => new(Rgb(0x000000), Rgb(0x0C0C0C), Rgb(0x252525), Rgb(0xE1E1E1),
            Rgb(0xFFFFFF), Rgb(0xF5F5F5), Rgb(0xA3A3A3), Rgb(0x383838)),
        _ => DefaultPalette,
    };

    public static void SetCurrent(UiSkinSettings settings, bool signedIn, int sponsorTier)
    {
        var id = SkinCatalog.Resolve(settings, signedIn, sponsorTier);
        if (id == CurrentSkin) return;
        CurrentSkin = id;
        Palette = For(id);
    }

    public static Vector4 Tone(Vector4 original, Vector4 themed)
        => CurrentSkin == SkinCatalog.Default ? original : themed with { W = themed.W * original.W };

    public static Vector4 Foreground(Vector4 color)
        => (Palette.Light || Palette.Glass) && !IsPaletteColor(color) && .2126f * color.X + .7152f * color.Y + .0722f * color.Z > .4f
            ? new Vector4(color.X * .45f, color.Y * .45f, color.Z * .45f, color.W) : color;

    public static void TextColored(Vector4 color, string text) => ImGui.TextColored(Foreground(color), text);

    public static void PushStyleColor(ImGuiCol slot, Vector4 original)
        // Page containers must not stack opaque sheets over the glass window.
        => ImGui.PushStyleColor(slot, Palette.Glass && slot == ImGuiCol.ChildBg && SameRgb(original, Palette.Surface)
            ? Vector4.Zero
            : CurrentSkin == SkinCatalog.Default || original.W == 0 || IsPaletteColor(original) ? original : Color(slot, original));

    private static bool IsPaletteColor(Vector4 color)
        => SameRgb(color, Palette.Surface) || SameRgb(color, Palette.Raised) || SameRgb(color, Palette.Hover) ||
           SameRgb(color, Palette.Gold) || SameRgb(color, Palette.Accent) || SameRgb(color, Palette.Text) || SameRgb(color, Palette.Muted) || SameRgb(color, Palette.Border);

    private static bool SameRgb(Vector4 left, Vector4 right) => left.X == right.X && left.Y == right.Y && left.Z == right.Z;

    private static Vector4 Color(ImGuiCol slot, Vector4 original)
    {
        var color = slot switch
        {
            ImGuiCol.ChildBg when Palette.Glass => Vector4.Zero,
            // Menus need their own contrast scrim above a busy game scene.
            ImGuiCol.PopupBg when Palette.Glass => Palette.Surface with { W = .96f },
            ImGuiCol.WindowBg or ImGuiCol.ChildBg or ImGuiCol.PopupBg => Palette.Surface,
            ImGuiCol.Border or ImGuiCol.Separator => Palette.Border,
            ImGuiCol.FrameBg or ImGuiCol.Button => Palette.Raised,
            ImGuiCol.FrameBgHovered or ImGuiCol.ButtonHovered or ImGuiCol.HeaderHovered => Palette.Hover,
            ImGuiCol.FrameBgActive or ImGuiCol.ButtonActive or ImGuiCol.HeaderActive => Palette.Hover,
            ImGuiCol.CheckMark or ImGuiCol.SliderGrab or ImGuiCol.SliderGrabActive => Palette.Accent,
            ImGuiCol.Header => Palette.Hover,
            ImGuiCol.Text => Palette.Text,
            ImGuiCol.TextDisabled => Palette.Muted,
            _ => original with { W = 1 },
        };
        return color with { W = color.W * original.W };
    }

    public static Scope PushFrame()
    {
        if (CurrentSkin == SkinCatalog.Default) return new Scope(0, 0);
        foreach (var slot in FrameSlots) ImGui.PushStyleColor(slot, Color(slot, Vector4.One));
        if (Palette.Light)
        {
            // ImGui's resize triangle occupies the game sprite's transparent
            // lower margin. Keep its native hit target without painting outside the frame.
            ImGui.PushStyleColor(ImGuiCol.ResizeGrip, Vector4.Zero);
            ImGui.PushStyleColor(ImGuiCol.ResizeGripHovered, Vector4.Zero);
            ImGui.PushStyleColor(ImGuiCol.ResizeGripActive, Vector4.Zero);
        }
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, Palette.Light || Palette.Glass ? 12 : 5);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, Palette.Light ? 1 : 0);
        if (Palette.Glass) ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 14 * Math.Max(.75f, ImGui.GetFontSize() / 17f));
        var extraColors = 0;
        if (Palette.Glass || CurrentSkin == SkinCatalog.Obsidian)
        {
            foreach (var slot in new[] { ImGuiCol.TitleBg, ImGuiCol.TitleBgActive, ImGuiCol.ScrollbarBg })
                ImGui.PushStyleColor(slot, Palette.Surface);
            foreach (var slot in new[] { ImGuiCol.ScrollbarGrab, ImGuiCol.ScrollbarGrabHovered, ImGuiCol.ScrollbarGrabActive,
                         ImGuiCol.ResizeGrip, ImGuiCol.ResizeGripHovered, ImGuiCol.ResizeGripActive })
                ImGui.PushStyleColor(slot, Palette.Muted with { W = .45f });
            extraColors = 9;
        }
        return new Scope(FrameSlots.Length + (Palette.Light ? 3 : 0) + extraColors, Palette.Glass ? 3 : 2);
    }

    public static bool Button(string label, Vector2 size = default) => DrawButton(label, size, false);
    public static bool SmallButton(string label) => DrawButton(label, default, true);
    public static float ButtonHeight => Palette.Light
        ? Math.Max(ImGui.GetFrameHeight(), 28 * ImGui.GetFontSize() / 17f) : ImGui.GetFrameHeight();

    public static bool DrawGameTab(ImDrawListPtr draw, Vector2 min, Vector2 max, bool selected, bool hovered)
    {
        if (GameAssets?.Tab(min, max, selected, hovered) == true) return true;
        draw.AddRectFilled(min, max, ImGui.GetColorU32(selected ? Palette.Hover : Palette.Raised), 3);
        return false;
    }

    public static bool DrawGameWindow()
    {
        if (!Palette.Glass) return Palette.Light && GameAssets?.Window() == true;
        var min = ImGui.GetWindowPos();
        var scale = Math.Max(.75f, ImGui.GetFontSize() / 17f);
        var draw = ImGui.GetWindowDrawList();
        // Expand the content clip only while painting the rim, never for controls.
        draw.PushClipRect(min, min + ImGui.GetWindowSize(), false);
        DrawGlassSurface(draw, min + Vector2.One, min + ImGui.GetWindowSize() - Vector2.One, 14 * scale, Vector4.Zero);
        draw.PopClipRect();
        return true;
    }

    internal static void DrawGlassSurface(ImDrawListPtr draw, Vector2 min, Vector2 max, float radius, Vector4 fill, bool interactive = false)
    {
        if (max.X <= min.X || max.Y <= min.Y) return;
        radius = Math.Clamp(radius, 0, Math.Min(max.X - min.X, max.Y - min.Y) * .5f);
        // Insert the GPU pass before labels and content. Each pane samples exactly
        // what has already been drawn behind it, including lower DACT windows.
        // Root panes need a white body for dark labels. Nested controls already
        // sample that body and only add a small amount to avoid opaque stacking.
        var body = fill.W == 0 || fill.W >= .5f ? .56f : interactive ? .18f : .10f;
        if (GlassRenderer?.Enqueue(draw, min, max, radius, body, interactive) == true) return;
        if (fill.W > 0) draw.AddRectFilled(min, max, ImGui.GetColorU32(fill), radius);
        // Vertex tinting keeps the sheen inside the rounded mesh and adds no textures
        // or framebuffer hooks. This is optical styling, not sampled game refraction.
        var first = draw.VtxBuffer.Size;
        draw.AddRectFilled(min, max, ImGui.GetColorU32(Vector4.One), radius);
        TintGlassVertices(draw, first, min, max, false, interactive);
        first = draw.VtxBuffer.Size;
        draw.AddRect(min + Vector2.One, max - Vector2.One, ImGui.GetColorU32(Vector4.One), Math.Max(0, radius - 1), ImDrawFlags.None, 1.25f);
        TintGlassVertices(draw, first, min, max, true, interactive);
        draw.AddRect(min + new Vector2(3), max - new Vector2(3), ImGui.GetColorU32(new Vector4(.08f, .12f, .18f, .18f)), Math.Max(0, radius - 3));
    }

    private static void TintGlassVertices(ImDrawListPtr draw, int first, Vector2 min, Vector2 max, bool rim, bool interactive)
    {
        var mouse = ImGui.GetIO().MousePos;
        var size = max - min;
        for (var i = first; i < draw.VtxBuffer.Size; i++)
        {
            var vertex = draw.VtxBuffer[i];
            var uv = (vertex.Pos - min) / size;
            var light = Math.Clamp(1 - uv.Y * .8f - uv.X * .2f, 0, 1);
            var glint = interactive ? Math.Max(0, 1 - Vector2.Distance(vertex.Pos, mouse) / 130f) : 0;
            var tint = Vector4.Lerp(new(.60f, .77f, .95f, rim ? .16f : .015f),
                new(1, 1, 1, rim ? .65f : .13f), light);
            tint.W += glint * (rim ? .3f : .08f);
            // Retain the mesh's anti-aliased fringe and the enclosing fade animation.
            tint.W *= (vertex.Col >> 24) / 255f;
            vertex.Col = ImGui.ColorConvertFloat4ToU32(tint);
            draw.VtxBuffer[i] = vertex;
        }
    }

    public static void DrawGamePopupFrame()
    {
        if (Palette.Light) GameAssets?.PopupFrame();
        else if (Palette.Glass) DrawGameWindow();
    }

    public static bool BeginCombo(string label, string preview, ImGuiComboFlags flags = ImGuiComboFlags.None)
    {
        if (Palette.Glass && GlassRenderer?.Ready == true)
        {
            ImGui.PushStyleColor(ImGuiCol.PopupBg, Vector4.Zero);
            ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 14 * Math.Max(.75f, ImGui.GetFontSize() / 17f));
            var glassOpen = ImGui.BeginCombo(label, preview, flags);
            ImGui.PopStyleVar(); ImGui.PopStyleColor();
            if (glassOpen) DrawGamePopupFrame();
            return glassOpen;
        }
        if (!Palette.Light) return ImGui.BeginCombo(label, preview, flags);
        var scale = Math.Max(.75f, ImGui.GetFontSize() / 17f);
        var padding = ImGui.GetStyle().FramePadding;
        // BeginCombo derives popup horizontal padding from FramePadding. Give
        // menu rows room inside the metal rim without changing their hit targets.
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(Math.Max(padding.X, 12 * scale), padding.Y));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(12, 12) * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupBorderSize, 1);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 9 * scale);
        ImGui.PushStyleColor(ImGuiCol.PopupBg, Palette.Surface);
        var open = ImGui.BeginCombo(label, preview, flags);
        ImGui.PopStyleColor(); ImGui.PopStyleVar(4);
        if (open) DrawGamePopupFrame();
        return open;
    }

    public static bool Combo(string label, ref int selected, string[] items, int count)
    {
        if (!Palette.Light && !Palette.Glass) return ImGui.Combo(label, ref selected, items, count);
        var changed = false;
        if (BeginCombo(label, selected >= 0 && selected < count ? items[selected] : ""))
        {
            for (var index = 0; index < count; index++)
            {
                ImGui.PushID(index);
                if (ImGui.Selectable(items[index], selected == index)) { selected = index; changed = true; }
                if (selected == index) ImGui.SetItemDefaultFocus();
                ImGui.PopID();
            }
            ImGui.EndCombo();
        }
        return changed;
    }

    public static ImGuiWindowFlags WindowFlags(ImGuiWindowFlags flags)
        // Keep the normal background until asynchronous textures are available.
        // Once ready, only the original rounded window sprite owns the border.
        => Palette.Glass && GlassRenderer?.Ready == true || Palette.Light && GameAssets?.HasWindowTextures == true
            ? flags | ImGuiWindowFlags.NoBackground : flags & ~ImGuiWindowFlags.NoBackground;

    public static bool IconButton(string id, GameSkinIcon icon, string fallback, Vector2 size)
    {
        if (!Palette.Light || GameAssets is null) return Button(fallback + "##" + id, size);
        var pressed = ImGui.InvisibleButton(id, size);
        var min = ImGui.GetItemRectMin();
        var iconSize = Math.Min(size.X, size.Y);
        if (!GameAssets.Icon(icon, min + (size - new Vector2(iconSize)) * .5f, iconSize, ImGui.IsItemActive()))
            ImGui.GetWindowDrawList().AddText(min, ImGui.GetColorU32(Palette.Text), fallback);
        if (ImGui.IsItemFocused()) ImGui.GetWindowDrawList().AddRect(min, min + size, ImGui.GetColorU32(Palette.Accent), 4);
        return pressed;
    }

    public static bool Checkbox(string label, ref bool value)
    {
        if (!Palette.Light || GameAssets is null) return ImGui.Checkbox(label, ref value);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.CheckMark, Vector4.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0);
        var changed = ImGui.Checkbox(label, ref value);
        ImGui.PopStyleVar(); ImGui.PopStyleColor(4);
        var frame = ImGui.GetFrameHeight();
        var size = Math.Min(frame, 16 * ImGui.GetFontSize() / 17f);
        var min = ImGui.GetItemRectMin() + new Vector2((frame - size) * .5f);
        if (!GameAssets.Checkbox(min, size, value))
        {
            ImGui.GetWindowDrawList().AddRect(min, min + new Vector2(size), ImGui.GetColorU32(Palette.Border));
            if (value) ImGui.GetWindowDrawList().AddRectFilled(min + new Vector2(3), min + new Vector2(size - 3), ImGui.GetColorU32(Palette.Accent));
        }
        return changed;
    }

    private static bool DrawButton(string label, Vector2 size, bool small)
    {
        if (Palette.Glass)
        {
            // Let ImGui own focus, keyboard navigation, hit testing and disabled state;
            // replace only its fill, then paint the label above the glass material.
            var textColor = ImGui.GetStyle().Colors[(int)ImGuiCol.Text];
            ImGui.PushStyleColor(ImGuiCol.Text, Vector4.Zero);
            ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Vector4.Zero);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, Vector4.Zero);
            var clicked = small ? ImGui.SmallButton(label) : ImGui.Button(label, size);
            ImGui.PopStyleColor(4);
            if (!ImGui.IsItemVisible()) return clicked;
            var glassMin = ImGui.GetItemRectMin(); var glassMax = ImGui.GetItemRectMax();
            var glassDraw = ImGui.GetWindowDrawList();
            DrawGlassSurface(glassDraw, glassMin, glassMax, (glassMax.Y - glassMin.Y) * .5f,
                ImGui.IsItemActive() ? Palette.Hover : Palette.Raised, ImGui.IsItemHovered());
            var caption = label.Split("##", 2)[0];
            glassDraw.PushClipRect(glassMin, glassMax, true);
            glassDraw.AddText(CenteredTextPosition(caption, glassMin, glassMax), ImGui.GetColorU32(textColor), caption);
            glassDraw.PopClipRect();
            if (ImGui.IsItemFocused()) glassDraw.AddRect(glassMin, glassMax, ImGui.GetColorU32(Palette.Accent), (glassMax.Y - glassMin.Y) * .5f);
            return clicked;
        }
        if (!Palette.Light) return small ? ImGui.SmallButton(label) : ImGui.Button(label, size);
        ImGui.PushStyleColor(ImGuiCol.Text, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, Vector4.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0);
        // The original 28px sprite includes its shadow. ImGui.SmallButton's
        // line-height box would leave less visible face height than the glyphs.
        if (size.Y == 0) size.Y = ButtonHeight;
        var pressed = ImGui.Button(label, size);
        ImGui.PopStyleVar(); ImGui.PopStyleColor(4);
        if (!ImGui.IsItemVisible()) return pressed;
        var min = ImGui.GetItemRectMin(); var max = ImGui.GetItemRectMax();
        var draw = ImGui.GetWindowDrawList();
        var textured = GameAssets?.Button(min, max, ImGui.IsItemActive(), ImGui.IsItemHovered()) == true;
        if (!textured) draw.AddRectFilled(min, max, ImGui.GetColorU32(Palette.Raised), 5);
        if (ImGui.IsItemFocused()) draw.AddRect(min - Vector2.One, max + Vector2.One, ImGui.GetColorU32(Palette.Accent), 6);
        var display = label.Split("##", 2)[0];
        draw.PushClipRect(min, max, true);
        // ButtonA's opaque face is y=2..22 in its 28px source. Its lower
        // transparent shadow is not part of the surface that centers the label.
        var faceMin = textured ? new Vector2(min.X, min.Y + (max.Y - min.Y) * 2 / 28) : min;
        var faceMax = textured ? new Vector2(max.X, min.Y + (max.Y - min.Y) * 22 / 28) : max;
        var textPos = CenteredTextPosition(display, faceMin, faceMax) + (ImGui.IsItemActive() ? new Vector2(0, 1) : Vector2.Zero);
        if (textured) draw.AddText(textPos + new Vector2(0, 1), ImGui.GetColorU32(new Vector4(.12f, .08f, .04f, .8f)), display);
        // UIColor row 50, used by Character/Social buttons, is white in Light.
        draw.AddText(textPos, ImGui.GetColorU32(textured ? Vector4.One : Palette.Text), display);
        draw.PopClipRect();
        return pressed;
    }

    internal static unsafe Vector2 CenteredTextPosition(string text, Vector2 min, Vector2 max)
    {
        var font = ImGui.GetFont();
        var scale = ImGui.GetFontSize() / font.FontSize;
        var inkMin = new Vector2(float.PositiveInfinity);
        var inkMax = new Vector2(float.NegativeInfinity);
        var pen = Vector2.Zero;
        // CalcTextSize reports a line box, not ink bounds: CJK, Latin capitals
        // and descenders have different bearings in both game and custom fonts.
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value == '\n') { pen = new Vector2(0, pen.Y + ImGui.GetFontSize()); continue; }
            var pointer = font.FindGlyph(rune.Value <= ushort.MaxValue ? (ushort)rune.Value : (ushort)0xFFFD);
            if (pointer == null) continue;
            var glyph = new ImFontGlyphPtr(pointer);
            if (glyph.X1 > glyph.X0 && glyph.Y1 > glyph.Y0)
            {
                inkMin = Vector2.Min(inkMin, pen + new Vector2(glyph.X0, glyph.Y0) * scale);
                inkMax = Vector2.Max(inkMax, pen + new Vector2(glyph.X1, glyph.Y1) * scale);
            }
            pen.X += glyph.AdvanceX * scale;
        }
        if (!float.IsFinite(inkMin.X)) return min;
        var position = (min + max - inkMin - inkMax) * .5f;
        return new(MathF.Round(position.X), MathF.Round(position.Y));
    }

    internal readonly struct Scope(int colors, int variables) : IDisposable
    {
        public void Dispose()
        {
            if (variables > 0) ImGui.PopStyleVar(variables);
            if (colors > 0) ImGui.PopStyleColor(colors);
        }
    }
}

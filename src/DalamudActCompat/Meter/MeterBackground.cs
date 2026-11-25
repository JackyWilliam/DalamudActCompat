using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace DalamudActCompat.Meter;

internal static class MeterBackground
{
    public static readonly Vector3 DefaultColor = new(0.035f, 0.055f, 0.09f);
    internal const float LegacyDataBarOpacity = .20f;
    internal const float DefaultDataBarOpacity = LegacyDataBarOpacity * .85f;

    // Text settings of the meter window being drawn, set by PushText and cleared by PopText.
    // Every meter label goes through AddText or DrawText below, which have no profile to ask.
    private static bool outlineText;
    private static bool customText;

    public static float DataBarOpacity(MeterWindowProfile profile)
        => Channel(profile.DataBarOpacity ?? DefaultDataBarOpacity, DefaultDataBarOpacity);

    public static Vector4 Bar(MeterWindowProfile profile, Vector4 color)
    {
        // Preserve the stronger local-player accent and compact-view shading.
        // The reference bar's alpha is now controlled independently of the panel.
        color.W = Math.Clamp(color.W / LegacyDataBarOpacity * DataBarOpacity(profile), 0, 1);
        return color;
    }

    public static Vector3 Normalize(Vector3 color)
        => new(Channel(color.X, DefaultColor.X), Channel(color.Y, DefaultColor.Y), Channel(color.Z, DefaultColor.Z));

    // A broken channel falls back to white, the automatic colour on the default panel.
    public static Vector3 NormalizeText(Vector3 color)
        => new(Channel(color.X, 1), Channel(color.Y, 1), Channel(color.Z, 1));

    private static float Channel(float value, float fallback)
        => float.IsFinite(value) ? Math.Clamp(value, 0, 1) : fallback;

    // Null preserves each old template's original shade. Alpha has one owner so
    // the color picker cannot accidentally multiply opacity a second time.
    public static Vector4 Color(MeterWindowProfile profile, Vector4 fallback)
        => profile.BackgroundColor is { } color ? new Vector4(Normalize(color), fallback.W) : fallback;

    public static Vector4 Color(MeterWindowProfile profile)
        => Color(profile, new Vector4(DefaultColor, 1));

    public static Vector4 Fill(MeterWindowProfile profile, Vector4? fallback = null)
        => MeterWindow.ApplyBackgroundOpacity(Color(profile, fallback ?? new Vector4(DefaultColor, 1)), profile.BackgroundOpacity);

    public static Vector4 Text(MeterWindowProfile profile)
    {
        if (profile.TextColor is { } picked) return new Vector4(NormalizeText(picked), 1);
        var color = Color(profile);
        return profile.BackgroundOpacity >= .65f && Brightness(color) > .62f
            ? new Vector4(.12f, .10f, .08f, 1) : Vector4.One;
    }

    public static void PushText(MeterWindowProfile profile)
    {
        var text = Text(profile);
        customText = profile.TextColor is not null;
        outlineText = profile.TextOutline;
        ImGui.PushStyleColor(ImGuiCol.Text, text);
        ImGui.PushStyleColor(ImGuiCol.TextDisabled, customText
            ? MutedFrom(text)
            : IsDark(text) ? new Vector4(.32f, .30f, .28f, 1) : new Vector4(.65f, .70f, .76f, 1));
    }

    // Ends what PushText began. The outline and the picked colour belong to one window; left
    // set, they would carry into the next meter window drawn in the same frame.
    public static void PopText()
    {
        ImGui.PopStyleColor(2);
        customText = false;
        outlineText = false;
    }

    public static Vector4 CurrentText => ImGui.GetStyle().Colors[(int)ImGuiCol.Text];

    public static Vector4 DisabledText => ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled];

    // Labels keep their own grey unless the player picked a text colour; then they follow it,
    // or a label would stay grey beside names in the colour chosen to be readable.
    public static Vector4 Muted(Vector4 fallback) => customText ? MutedFrom(CurrentText) : fallback;

    private static Vector4 MutedFrom(Vector4 text)
        => new(Vector3.Lerp(new Vector3(text.X, text.Y, text.Z), new Vector3(.5f), .3f), text.W);

    private static float Brightness(Vector4 color) => .2126f * color.X + .7152f * color.Y + .0722f * color.Z;

    // Brightness rather than the red channel: a picked colour such as green or cyan is bright
    // text although its red is zero. The two automatic colours fall on the same side either way.
    private static bool IsDark(Vector4 color) => Brightness(color) < .5f;

    public static Vector4 Foreground(Vector4 color)
        => IsDark(CurrentText) && Brightness(color) > .45f
            ? new Vector4(color.X * .38f, color.Y * .38f, color.Z * .38f, color.W) : color;

    public static void DrawText(Vector4 color, string text)
    {
        var foreground = Foreground(color);
        if (outlineText) DrawOutline(ImGui.GetWindowDrawList(), ImGui.GetCursorScreenPos(), foreground, text);
        ImGui.TextColored(foreground, text);
    }

    public static void AddText(ImDrawListPtr draw, Vector2 position, uint color, string text)
    {
        // Packed colors already include ImGui's disabled/window alpha. Converting
        // directly back avoids multiplying that alpha a second time.
        var foreground = Foreground(ImGui.ColorConvertU32ToFloat4(color));
        if (outlineText) DrawOutline(draw, position, foreground, text);
        draw.AddText(position, ImGui.ColorConvertFloat4ToU32(foreground), text);
    }

    // Synthetic bold is the text drawn twice a fraction of a pixel apart. Outlined one copy at
    // a time, the second copy's edge would paint over the first and the weight would be lost,
    // so both edges go down before either copy.
    public static void AddBoldText(ImDrawListPtr draw, Vector2 position, uint color, string text, float boldOffset)
    {
        var foreground = Foreground(ImGui.ColorConvertU32ToFloat4(color));
        var packed = ImGui.ColorConvertFloat4ToU32(foreground);
        var second = position + new Vector2(boldOffset, 0);
        if (outlineText)
        {
            DrawOutline(draw, position, foreground, text);
            DrawOutline(draw, second, foreground, text);
        }
        draw.AddText(position, packed, text);
        draw.AddText(second, packed, text);
    }

    // The same text once more on each side, behind the real one. On a transparent panel the
    // meter sits straight on the game image, where no single colour stays readable over both
    // snow and night sky; an edge of the opposite brightness does.
    private static void DrawOutline(ImDrawListPtr draw, Vector2 position, Vector4 foreground, string text)
    {
        if (foreground.W <= 0 || text.Length == 0) return;
        // The edge fades with its text, so a row that is fading in or dimmed leaves no hard
        // shape behind.
        var edge = ImGui.ColorConvertFloat4ToU32(IsDark(foreground)
            ? new Vector4(1, 1, 1, .8f * foreground.W)
            : new Vector4(0, 0, 0, .85f * foreground.W));
        // One pixel at the default size, thicker as the meter's text scale grows.
        var offset = MathF.Max(1, MathF.Round(ImGui.GetFontSize() / 18f));
        draw.AddText(position + new Vector2(-offset, 0), edge, text);
        draw.AddText(position + new Vector2(offset, 0), edge, text);
        draw.AddText(position + new Vector2(0, -offset), edge, text);
        draw.AddText(position + new Vector2(0, offset), edge, text);
    }
}

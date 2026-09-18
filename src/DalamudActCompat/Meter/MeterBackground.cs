using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace DalamudActCompat.Meter;

internal static class MeterBackground
{
    public static readonly Vector3 DefaultColor = new(0.035f, 0.055f, 0.09f);

    public static Vector3 Normalize(Vector3 color)
        => new(Channel(color.X, DefaultColor.X), Channel(color.Y, DefaultColor.Y), Channel(color.Z, DefaultColor.Z));

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
        var color = Color(profile);
        var bright = .2126f * color.X + .7152f * color.Y + .0722f * color.Z;
        return profile.BackgroundOpacity >= .65f && bright > .62f
            ? new Vector4(.12f, .10f, .08f, 1) : Vector4.One;
    }

    public static void PushText(MeterWindowProfile profile)
    {
        var text = Text(profile);
        ImGui.PushStyleColor(ImGuiCol.Text, text);
        ImGui.PushStyleColor(ImGuiCol.TextDisabled, text.X < .5f ? new Vector4(.32f, .30f, .28f, 1) : new Vector4(.65f, .70f, .76f, 1));
    }

    public static Vector4 CurrentText => ImGui.GetStyle().Colors[(int)ImGuiCol.Text];

    public static Vector4 Foreground(Vector4 color)
        => CurrentText.X < .5f && .2126f * color.X + .7152f * color.Y + .0722f * color.Z > .45f
            ? new Vector4(color.X * .38f, color.Y * .38f, color.Z * .38f, color.W) : color;

    public static void DrawText(Vector4 color, string text) => ImGui.TextColored(Foreground(color), text);

    public static void AddText(ImDrawListPtr draw, Vector2 position, uint color, string text)
        // Packed colors already include ImGui's disabled/window alpha. Converting
        // directly back avoids multiplying that alpha a second time.
        => draw.AddText(position, ImGui.ColorConvertFloat4ToU32(Foreground(ImGui.ColorConvertU32ToFloat4(color))), text);
}

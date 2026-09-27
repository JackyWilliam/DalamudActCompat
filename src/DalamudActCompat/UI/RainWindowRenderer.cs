using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace DalamudActCompat.UI;

internal static class RainWindowRenderer
{
    private static readonly SkinPalette RainPalette = DactTheme.For(SkinCatalog.RainyWindow);

    internal static void Draw(ImDrawListPtr draw, Vector2 min, Vector2 max, float scale, double time)
    {
        if (max.X <= min.X || max.Y <= min.Y) return;
        var size = max - min;
        var radius = Math.Min(11 * scale, Math.Min(size.X, size.Y) / 2);
        // Share the existing compositor's lifetime, backdrop capture and state
        // restoration. The callback precedes the pane's text and input widgets.
        if (DactTheme.GlassRenderer?.Enqueue(draw, min, max, radius, 0, false,
                rain: true, time: time, rainScale: scale) != true)
            DrawFallback(draw, min, max, radius, scale, time);
        draw.AddRect(min + Vector2.One, max - Vector2.One,
            ImGui.GetColorU32(RainPalette.Border with { W = .65f }), radius);
    }

    private static void DrawFallback(ImDrawListPtr draw, Vector2 min, Vector2 max, float radius, float scale, double time)
    {
        // Device loss or unavailable effects must still leave a readable pane.
        // Keep a bounded set of convex droplets; no timers or particle state are
        // retained while the skin is hidden or another skin is selected.
        draw.AddRectFilled(min, max, ImGui.GetColorU32(RainPalette.Surface), radius);
        var inset = new Vector2(Math.Min(radius, (max.X - min.X) / 4));
        draw.PushClipRect(min + inset, max - inset, true);
        var size = max - min - inset * 2;
        var count = Math.Clamp((int)(size.X * size.Y / (27 * 27 * scale * scale) * .7f), 12, 200);
        for (var i = 0; i < count; i++)
        {
            var moving = i < Math.Max(1, count / 32);
            var phase = Fraction(time / (30 + Noise(i, 3) * 20) + Noise(i, 4));
            var pos = min + inset + new Vector2(Noise(i, 1) * size.X,
                (moving ? phase : Noise(i, 2)) * size.Y);
            var width = (moving ? 2.4f : .7f + Noise(i, 5) * 1.8f) * scale;
            var height = width * (1.1f + Noise(i, 6));
            Drop(draw, pos, new(width, height), new(.015f, .045f, .065f, .75f));
            Drop(draw, pos + new Vector2(0, height * .2f), new(width * .65f, height * .55f), new(.36f, .49f, .55f, .6f));
            draw.AddCircleFilled(pos + new Vector2(-width * .3f, -height * .42f), Math.Max(.45f, width * .23f),
                ImGui.GetColorU32(new Vector4(.78f, .88f, .91f, .72f)), 5);
        }
        draw.PopClipRect();
    }

    private static void Drop(ImDrawListPtr draw, Vector2 pos, Vector2 radius, Vector4 color)
    {
        for (var point = 0; point < 8; point++)
        {
            var angle = point * MathF.Tau / 8;
            draw.PathLineTo(pos + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius);
        }
        draw.PathFillConvex(ImGui.GetColorU32(color));
    }

    private static float Fraction(double value) => (float)(value - Math.Floor(value));
    private static float Noise(int index, int salt)
    {
        var value = unchecked((uint)(index * 374761393 + salt * 668265263));
        value = (value ^ (value >> 13)) * 1274126177u;
        return (value & 0xffff) / 65535f;
    }
}

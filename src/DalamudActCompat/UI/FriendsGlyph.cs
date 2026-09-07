using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace DalamudActCompat.UI;

internal static class FriendsGlyph
{
    public static void Draw(ImDrawListPtr list, Vector2 origin, float size, uint color)
    {
        // Open shoulders and outlined heads stay legible at 20px; avoid the
        // former filled circles/thick horizontal bars that read as blobs.
        var unit = size / 24;
        var stroke = Math.Max(1.25f, 1.55f * unit);
        Vector2 P(float x, float y) => origin + new Vector2(x, y) * unit;
        list.AddCircle(P(9, 7), 3.2f * unit, color, 16, stroke);
        list.AddBezierCubic(P(2, 21), P(2, 12), P(16, 12), P(16, 21), color, stroke);
        list.PathArcTo(P(17, 8), 2.8f * unit, -1.9f, 1.6f, 12); list.PathStroke(color, ImDrawFlags.None, stroke);
        list.AddBezierCubic(P(18, 14), P(22, 14), P(23, 17), P(23, 21), color, stroke);
    }
}

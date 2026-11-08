using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace DalamudActCompat.UI.Kit;

internal readonly record struct IconStroke(bool Closed, Vector2[] Points);

// Line icons of the 1.0 design (16×16 units, 1.5-unit stroke), drawn from the same paths as
// the Figma components; see DactIconPaths.g.cs.
internal static partial class DactIcons
{
    public static bool Has(string name) => Strokes.ContainsKey(name);

    public static void Draw(ImDrawListPtr draw, string name, Vector2 min, float size, Vector4 color)
    {
        if (!Strokes.TryGetValue(name, out var strokes) || color.W <= 0) return;
        var unit = size / 16f;
        var thickness = Math.Max(1, 1.5f * unit);
        var col = ImGui.GetColorU32(color);
        foreach (var stroke in strokes)
        {
            foreach (var point in stroke.Points) draw.PathLineTo(min + point * unit);
            draw.PathStroke(col, stroke.Closed ? ImDrawFlags.Closed : ImDrawFlags.None, thickness);
            if (stroke.Closed) continue;
            // ImGui strokes have square ends; the design's round caps matter for short marks
            // such as the dot of the help icon, which is a zero-length segment.
            draw.AddCircleFilled(min + stroke.Points[0] * unit, thickness * .5f, col, 8);
            draw.AddCircleFilled(min + stroke.Points[^1] * unit, thickness * .5f, col, 8);
        }
    }

    // Draws at the cursor and advances it like an ImGui item.
    public static void Item(string name, float size, Vector4 color)
    {
        var min = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Vector2(size));
        Draw(ImGui.GetWindowDrawList(), name, min, size, color);
    }
}

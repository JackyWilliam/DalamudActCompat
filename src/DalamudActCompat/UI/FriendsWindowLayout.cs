using System.Numerics;

namespace DalamudActCompat.UI;

internal static class FriendsWindowLayout
{
    public static Vector2 Clamp(Vector2 position, Vector2 size, Vector2 viewportPosition, Vector2 viewportSize)
        => Vector2.Clamp(position, viewportPosition + new Vector2(8),
            Vector2.Max(viewportPosition + new Vector2(8), viewportPosition + viewportSize - size - new Vector2(8)));

    public static (Vector2 Position, Vector2 Size) Drawer(Vector2 anchor, Vector2 anchorSize, Vector2 viewport, Vector2 viewportSize, float scale)
    {
        var top = Math.Clamp(anchor.Y + 48, viewport.Y + 8, viewport.Y + viewportSize.Y - 128);
        var size = Vector2.Min(new Vector2(352 * scale, Math.Max(120, anchor.Y + anchorSize.Y - top - 1)), viewportSize - new Vector2(16));
        var edge = anchor.X + anchorSize.X - 1;
        // The seam touches the main window exactly. When the screen edge leaves
        // no room, reveal inward below its header so the entry stays reachable.
        var outside = edge + size.X <= viewport.X + viewportSize.X - 8;
        var x = outside ? edge : edge - size.X;
        return (Clamp(new Vector2(x, top), size, viewport, viewportSize), size);
    }
}

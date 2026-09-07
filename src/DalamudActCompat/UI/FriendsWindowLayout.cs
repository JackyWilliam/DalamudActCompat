using System.Numerics;

namespace DalamudActCompat.UI;

internal static class FriendsWindowLayout
{
    public static Vector2 Clamp(Vector2 position, Vector2 size, Vector2 viewportPosition, Vector2 viewportSize)
        => Vector2.Clamp(position, viewportPosition + new Vector2(8),
            Vector2.Max(viewportPosition + new Vector2(8), viewportPosition + viewportSize - size - new Vector2(8)));

    public static (Vector2 Position, Vector2 Size) Drawer(Vector2 anchor, Vector2 anchorSize, Vector2 viewport, Vector2 viewportSize, float scale)
    {
        var size = Vector2.Min(new Vector2(340 * scale, Math.Max(350 * scale, anchorSize.Y - 48)), viewportSize - new Vector2(16));
        var right = anchor.X + anchorSize.X + 6;
        // Prefer an attached side panel; at the viewport edge it fits inside the
        // main window instead of becoming unreachable beyond the game surface.
        var x = right + size.X <= viewport.X + viewportSize.X - 8 ? right : anchor.X + anchorSize.X - size.X;
        return (Clamp(new Vector2(x, anchor.Y + 44), size, viewport, viewportSize), size);
    }
}

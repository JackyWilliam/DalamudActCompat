using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace DalamudActCompat.Meter;

internal sealed class MeterHeightAnchor
{
    private Vector2? pendingPosition;

    public void PrepareNextWindow(MeterCollapseDirection direction, bool locating)
    {
        // Begin builds the background and clipping rectangles. Move before Begin,
        // together with the submitted height, so the header and body cannot tear.
        if (!locating && direction == MeterCollapseDirection.Downward && pendingPosition is { } position)
            ImGui.SetNextWindowPos(position, ImGuiCond.Always);
        pendingPosition = null;
    }

    public void SetHeight(float height, MeterCollapseDirection direction)
    {
        var size = ImGui.GetWindowSize();
        if (direction == MeterCollapseDirection.Downward)
        {
            var viewport = ImGui.GetWindowViewport();
            var position = ImGui.GetWindowPos();
            // ImGui truncates window sizes to pixels. Using the same height avoids
            // cumulative bottom-edge drift across animation frames and repeated toggles.
            var nextHeight = MathF.Floor(height);
            position.Y = Math.Clamp(position.Y + size.Y - nextHeight,
                viewport.WorkPos.Y, Math.Max(viewport.WorkPos.Y, viewport.WorkPos.Y + viewport.WorkSize.Y - nextHeight));
            pendingPosition = position;
        }
        ImGui.SetWindowSize(new Vector2(size.X, height), ImGuiCond.Always);
    }
}

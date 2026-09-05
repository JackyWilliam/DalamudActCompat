using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace DalamudActCompat.UI;

internal sealed class WindowDragController
{
    private Vector2? mouseOffset;
    private int lastHandleFrame = -1;

    public void Cancel() => mouseOffset = null;

    public void PrepareNextWindow(bool enabled = true)
    {
        var position = ResolvePosition(
            ImGui.GetFrameCount(), ImGui.GetMousePos(), ImGui.IsMouseDown(ImGuiMouseButton.Left), enabled);
        if (position is { } target && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            // Begin creates the background and clip rectangles. Moving after it leaves
            // already submitted header vertices behind while later content moves ahead.
            ImGui.SetNextWindowPos(target, ImGuiCond.Always);
        }
    }

    public void HandleItem(bool enabled = true, bool allowStart = true)
    {
        ObserveHandle(
            ImGui.GetFrameCount(), ImGui.GetMousePos(), ImGui.GetWindowPos(),
            ImGui.IsItemClicked(ImGuiMouseButton.Left) && allowStart,
            ImGui.IsItemActive() && enabled);
    }

    internal void ObserveHandle(int frame, Vector2 mousePosition, Vector2 windowPosition, bool clicked, bool active)
    {
        if (!active)
        {
            Cancel();
            return;
        }

        if (clicked)
        {
            // Anchor to the original grab point, so fast movement or crossing the drag
            // threshold cannot drop mouse deltas and make the window trail the cursor.
            mouseOffset = mousePosition - windowPosition;
        }

        if (mouseOffset.HasValue)
        {
            lastHandleFrame = frame;
        }
    }

    internal Vector2? ResolvePosition(int frame, Vector2 mousePosition, bool mouseDown, bool enabled)
    {
        // A hidden/closed window must not resume an old drag when it reappears while
        // the button is still held. Each live header owns its own controller instance.
        if (!enabled || !mouseDown || lastHandleFrame != frame - 1)
        {
            Cancel();
        }

        return mouseOffset is { } offset ? mousePosition - offset : null;
    }
}

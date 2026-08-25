using Dalamud.Bindings.ImGui;
using System.Numerics;

namespace DalamudActCompat.Meter;

internal sealed class MeterPreviewInteraction
{
    private static readonly Vector4 Gold = new(0.90f, 0.81f, 0.55f, 1);
    private static readonly Vector4 IceBlue = new(0.42f, 0.78f, 0.96f, 1);
    private readonly MeterWindowProfile profile;
    private readonly Func<string?> getSelectedSlotId;
    private readonly Action<string> selectSlot;
    private string? draggedSlotId;
    private bool changed;

    public MeterPreviewInteraction(
        MeterWindowProfile profile,
        Func<string?> getSelectedSlotId,
        Action<string> selectSlot)
    {
        this.profile = profile;
        this.getSelectedSlotId = getSelectedSlotId;
        this.selectSlot = selectSlot;
    }

    public void Observe(
        MeterSlotDefinition? slot,
        Vector2 start,
        Vector2 end,
        ImDrawListPtr drawList,
        bool highlightSelection = true)
    {
        if (slot is null || end.X <= start.X || end.Y <= start.Y)
        {
            return;
        }

        var hovered = ImGui.IsMouseHoveringRect(start, end);
        var selected = string.Equals(
            getSelectedSlotId(),
            slot.Id,
            StringComparison.OrdinalIgnoreCase);
        if (hovered || (selected && highlightSelection))
        {
            drawList.AddRect(
                start,
                end,
                ImGui.GetColorU32(hovered ? IceBlue : Gold),
                3,
                ImDrawFlags.None,
                hovered ? 1.8f : 1.2f);
        }

        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            draggedSlotId = slot.Id;
            selectSlot(slot.Id);
        }

        if (hovered && ImGui.IsMouseReleased(ImGuiMouseButton.Left) &&
            !string.IsNullOrWhiteSpace(draggedSlotId) &&
            !string.Equals(draggedSlotId, slot.Id, StringComparison.OrdinalIgnoreCase))
        {
            SwapSlots(draggedSlotId, slot.Id);
            selectSlot(draggedSlotId);
            draggedSlotId = null;
        }
    }

    public void EndFrame()
    {
        if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
        {
            draggedSlotId = null;
        }
    }

    public bool ConsumeChanged()
    {
        var result = changed;
        changed = false;
        return result;
    }

    private void SwapSlots(string sourceId, string targetId)
    {
        var sourceIndex = profile.Slots.FindIndex(slot => string.Equals(
            slot.Id,
            sourceId,
            StringComparison.OrdinalIgnoreCase));
        var targetIndex = profile.Slots.FindIndex(slot => string.Equals(
            slot.Id,
            targetId,
            StringComparison.OrdinalIgnoreCase));
        if (sourceIndex < 0 || targetIndex < 0 || sourceIndex == targetIndex)
        {
            return;
        }

        // Swapping keeps every slot definition intact while making preview drag behavior
        // match the editor's existing order-based layout model.
        (profile.Slots[sourceIndex], profile.Slots[targetIndex]) =
            (profile.Slots[targetIndex], profile.Slots[sourceIndex]);
        changed = true;
    }
}

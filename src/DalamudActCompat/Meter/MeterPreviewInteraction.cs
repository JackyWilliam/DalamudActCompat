using Dalamud.Bindings.ImGui;
using System.Numerics;
using DalamudActCompat.UI;
using DalamudActCompat.UI.Kit;

namespace DalamudActCompat.Meter;

internal sealed class MeterPreviewInteraction
{
    private readonly List<MeterSlotDefinition> slots;
    private readonly Func<string?> getSelectedSlotId;
    private readonly Action<string> selectSlot;
    private string? draggedSlotId;
    private bool changed;

    public MeterPreviewInteraction(
        List<MeterSlotDefinition> slots,
        Func<string?> getSelectedSlotId,
        Action<string> selectSlot)
    {
        this.slots = slots;
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
        // 1.0: the theme accent instead of the old gold / ice-blue frames, easing in and out.
        // Hover lights a faint rim; the selection a tinted fill with a solid accent rim.
        var key = $"##preview-slot-{slot.Id}-{(int)start.X}-{(int)start.Y}";
        var hover = DactMotion.Float(ImGui.GetID(key + "-hover"), hovered ? 1 : 0, DactMotion.Fast);
        var pick = DactMotion.Float(ImGui.GetID(key + "-pick"), selected && highlightSelection ? 1 : 0, DactMotion.Normal);
        var s = DactUi.S;
        var palette = DactTheme.Palette;
        var radius = 4 * s;
        if (pick > .01f)
        {
            drawList.AddRectFilled(start, end, ImGui.GetColorU32(palette.Accent with { W = .14f * pick }), radius);
            drawList.AddRect(start, end, ImGui.GetColorU32(palette.Accent with { W = .9f * pick }), radius, ImDrawFlags.None, 1.5f * s);
        }
        if (hover > .01f)
        {
            drawList.AddRect(start, end, ImGui.GetColorU32(palette.Text with { W = .5f * hover * (1 - .6f * pick) }), radius,
                ImDrawFlags.None, 1.2f * s);
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
        var sourceIndex = slots.FindIndex(slot => string.Equals(
            slot.Id,
            sourceId,
            StringComparison.OrdinalIgnoreCase));
        var targetIndex = slots.FindIndex(slot => string.Equals(
            slot.Id,
            targetId,
            StringComparison.OrdinalIgnoreCase));
        if (sourceIndex < 0 || targetIndex < 0 || sourceIndex == targetIndex)
        {
            return;
        }

        // Swapping keeps every slot definition intact while making preview drag behavior
        // match the editor's existing order-based layout model.
        (slots[sourceIndex], slots[targetIndex]) =
            (slots[targetIndex], slots[sourceIndex]);
        changed = true;
    }
}

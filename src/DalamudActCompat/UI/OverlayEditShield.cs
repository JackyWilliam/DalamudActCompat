using Dalamud.Bindings.ImGui;
using System.Numerics;

namespace DalamudActCompat.UI;

internal static class OverlayEditShield
{
    private const string WindowId = "###DalamudActCompatOverlayEditShield";
    private const string InputId = "###DalamudActCompatOverlayEditShieldInput";

    internal static bool IsRequired(
        bool hasVisibleEditingOverlay,
        bool hasVisibleManagementWindow)
        => hasVisibleEditingOverlay && !hasVisibleManagementWindow;

    public static void Draw(
        bool hasVisibleEditingOverlay,
        bool hasVisibleManagementWindow)
    {
        if (!IsRequired(hasVisibleEditingOverlay, hasVisibleManagementWindow))
        {
            return;
        }

        var displaySize = ImGui.GetIO().DisplaySize;
        if (displaySize.X <= 0 || displaySize.Y <= 0)
        {
            return;
        }

        var flags = ImGuiWindowFlags.NoDecoration |
                    ImGuiWindowFlags.NoMove |
                    ImGuiWindowFlags.NoResize |
                    ImGuiWindowFlags.NoSavedSettings |
                    ImGuiWindowFlags.NoBackground |
                    ImGuiWindowFlags.NoScrollWithMouse |
                    ImGuiWindowFlags.NoNav |
                    ImGuiWindowFlags.NoFocusOnAppearing |
                    ImGuiWindowFlags.NoBringToFrontOnFocus;
        ImGui.SetNextWindowPos(Vector2.Zero, ImGuiCond.Always);
        ImGui.SetNextWindowSize(displaySize, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        try
        {
            if (ImGui.Begin(WindowId, flags))
            {
                ImGui.InvisibleButton(InputId, displaySize);
            }

            ImGui.End();
        }
        finally
        {
            ImGui.PopStyleVar();
        }
    }
}

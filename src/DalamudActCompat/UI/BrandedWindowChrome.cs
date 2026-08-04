using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;

namespace DalamudActCompat.UI;

internal static class BrandedWindowChrome
{
    private static readonly Vector4 NavyRaised = new(0.070f, 0.095f, 0.125f, 1);

    public static bool Draw(
        ISharedImmediateTexture logoTexture,
        string sectionLabel,
        string centerLabel,
        Vector4 centerColor,
        string versionLabel,
        string id)
    {
        const float height = 40;
        const float closeWidth = 34;
        const float horizontalPadding = 8;
        const float logoSize = 28;
        var start = ImGui.GetCursorPos();
        var screenStart = ImGui.GetCursorScreenPos();
        var availableWidth = ImGui.GetContentRegionAvail().X;
        var screenEnd = screenStart + new Vector2(availableWidth, height);
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(
            screenStart,
            screenEnd,
            ImGui.GetColorU32(NavyRaised),
            9,
            ImDrawFlags.RoundCornersTop);

        var logoTop = screenStart.Y + ((height - logoSize) * 0.5f);
        var logoLeft = screenStart.X + horizontalPadding;
        drawList.AddImage(
            logoTexture.GetWrapOrEmpty().Handle,
            new Vector2(logoLeft, logoTop),
            new Vector2(logoLeft + logoSize, logoTop + logoSize));

        var textTop = screenStart.Y + ((height - ImGui.GetTextLineHeight()) * 0.5f);
        const string title = "Dalamud ACT Compat";
        var titleLeft = logoLeft + logoSize + 9;
        drawList.AddText(
            new Vector2(titleLeft, textTop),
            ImGui.GetColorU32(Vector4.One),
            title);
        drawList.AddText(
            new Vector2(titleLeft + ImGui.CalcTextSize(title).X + 9, textTop),
            ImGui.GetColorU32(new Vector4(0.68f, 0.72f, 0.77f, 1)),
            sectionLabel);

        var centerSize = ImGui.CalcTextSize(centerLabel);
        drawList.AddText(
            new Vector2(
                screenStart.X + ((availableWidth - centerSize.X) * 0.5f),
                textTop),
            ImGui.GetColorU32(centerColor),
            centerLabel);

        var versionSize = ImGui.CalcTextSize(versionLabel);
        drawList.AddText(
            new Vector2(
                screenStart.X + availableWidth - closeWidth - versionSize.X - 12,
                textTop),
            ImGui.GetColorU32(new Vector4(0.62f, 0.66f, 0.71f, 1)),
            versionLabel);

        ImGui.InvisibleButton(
            $"branded-window-drag-handle##{id}",
            new Vector2(Math.Max(1, availableWidth - closeWidth), height));
        if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            ImGui.SetWindowPos(ImGui.GetWindowPos() + ImGui.GetIO().MouseDelta, ImGuiCond.Always);
        }

        ImGui.SetCursorPos(new Vector2(start.X + availableWidth - closeWidth, start.Y));
        ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.56f, 0.16f, 0.16f, 0.88f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.72f, 0.20f, 0.20f, 1));
        var closeRequested = ImGui.Button($"×##close-{id}", new Vector2(closeWidth, height));
        ImGui.PopStyleColor(3);
        ImGui.SetCursorPos(new Vector2(start.X, start.Y + height + 6));
        return closeRequested;
    }
}

using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;

namespace DalamudActCompat.UI;

internal static class BrandedWindowChrome
{
    private static readonly Vector4 NavyRaised = new(0.070f, 0.095f, 0.125f, 1);
    private static readonly Vector4 NavigationHover = new(0.16f, 0.31f, 0.40f, 0.24f);
    private static readonly Vector4 NavigationSelected = new(0.14f, 0.34f, 0.46f, 0.30f);
    private static readonly Vector4 NavigationText = new(0.74f, 0.79f, 0.84f, 1);
    private static readonly Vector4 NavigationAccent = new(0.42f, 0.78f, 0.96f, 1);
    private static readonly Vector4 GoldCardBorder = new(0.78f, 0.66f, 0.36f, 0.82f);
    private static readonly Vector4 GoldCardBackground = new(0.055f, 0.075f, 0.10f, 0.96f);
    private static readonly Dictionary<string, float> NavigationIndicatorPositions = new(StringComparer.Ordinal);

    public static bool Draw(
        ISharedImmediateTexture logoTexture,
        string sectionLabel,
        string centerLabel,
        Vector4 centerColor,
        string versionLabel,
        string id,
        bool showCloseButton = true)
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

        var trailingWidth = showCloseButton ? closeWidth : 0;
        var versionSize = ImGui.CalcTextSize(versionLabel);
        drawList.AddText(
            new Vector2(
                screenStart.X + availableWidth - trailingWidth - versionSize.X - 12,
                textTop),
            ImGui.GetColorU32(new Vector4(0.62f, 0.66f, 0.71f, 1)),
            versionLabel);

        ImGui.InvisibleButton(
            $"branded-window-drag-handle##{id}",
            new Vector2(Math.Max(1, availableWidth - trailingWidth), height));
        if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            ImGui.SetWindowPos(ImGui.GetWindowPos() + ImGui.GetIO().MouseDelta, ImGuiCond.Always);
        }

        var closeRequested = false;
        if (showCloseButton)
        {
            ImGui.SetCursorPos(new Vector2(start.X + availableWidth - closeWidth, start.Y));
            ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.56f, 0.16f, 0.16f, 0.88f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.72f, 0.20f, 0.20f, 1));
            closeRequested = ImGui.Button($"×##close-{id}", new Vector2(closeWidth, height));
            ImGui.PopStyleColor(3);
        }
        ImGui.SetCursorPos(new Vector2(start.X, start.Y + height + 6));
        return closeRequested;
    }

    public static int DrawNavigationRail(
        string id,
        IReadOnlyList<string> labels,
        int selectedIndex,
        float height = 38)
    {
        if (labels.Count == 0)
        {
            return selectedIndex;
        }

        selectedIndex = Math.Clamp(selectedIndex, 0, labels.Count - 1);
        var localStart = ImGui.GetCursorPos();
        var screenStart = ImGui.GetCursorScreenPos();
        var width = Math.Max(1, ImGui.GetContentRegionAvail().X);
        var segmentWidth = width / labels.Count;
        var screenEnd = screenStart + new Vector2(width, height);
        var drawList = ImGui.GetWindowDrawList();

        drawList.AddRectFilled(
            screenStart,
            screenEnd,
            ImGui.GetColorU32(NavyRaised),
            7);

        if (!NavigationIndicatorPositions.TryGetValue(id, out var indicatorPosition))
        {
            indicatorPosition = selectedIndex;
        }

        indicatorPosition = AdvanceNavigationIndicator(
            indicatorPosition,
            selectedIndex,
            ImGui.GetIO().DeltaTime);
        NavigationIndicatorPositions[id] = indicatorPosition;

        var indicatorMin = new Vector2(screenStart.X + (segmentWidth * indicatorPosition), screenStart.Y);
        var indicatorMax = indicatorMin + new Vector2(segmentWidth, height);
        drawList.AddRectFilled(
            indicatorMin,
            indicatorMax,
            ImGui.GetColorU32(NavigationSelected),
            6);
        drawList.AddRectFilled(
            new Vector2(indicatorMin.X + 8, indicatorMax.Y - 2),
            new Vector2(indicatorMax.X - 8, indicatorMax.Y),
            ImGui.GetColorU32(NavigationAccent),
            1);

        var clickedIndex = selectedIndex;
        for (var index = 0; index < labels.Count; index++)
        {
            var itemMin = new Vector2(screenStart.X + (segmentWidth * index), screenStart.Y);
            ImGui.SetCursorScreenPos(itemMin);
            ImGui.InvisibleButton($"navigation-segment-{index}##{id}", new Vector2(segmentWidth, height));
            if (ImGui.IsItemHovered())
            {
                drawList.AddRectFilled(
                    itemMin,
                    itemMin + new Vector2(segmentWidth, height),
                    ImGui.GetColorU32(NavigationHover),
                    6);
            }

            if (ImGui.IsItemClicked())
            {
                clickedIndex = index;
            }

            var labelSize = ImGui.CalcTextSize(labels[index]);
            drawList.AddText(
                new Vector2(
                    itemMin.X + ((segmentWidth - labelSize.X) * 0.5f),
                    itemMin.Y + ((height - labelSize.Y) * 0.5f)),
                ImGui.GetColorU32(index == selectedIndex ? NavigationAccent : NavigationText),
                labels[index]);
        }

        ImGui.SetCursorPos(new Vector2(localStart.X, localStart.Y + height));
        return clickedIndex;
    }

    internal static float AdvanceNavigationIndicator(float current, float target, float deltaTime)
    {
        if (deltaTime <= 0 || Math.Abs(target - current) < 0.001f)
        {
            return target;
        }

        const float response = 18;
        var progress = 1 - MathF.Exp(-response * deltaTime);
        var next = current + ((target - current) * progress);
        return Math.Abs(target - next) < 0.001f ? target : next;
    }

    public static bool BeginGoldCard(string id, float height)
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, GoldCardBackground);
        ImGui.PushStyleColor(ImGuiCol.Border, GoldCardBorder);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 8);
        return ImGui.BeginChild(id, new Vector2(-1, height), true);
    }

    public static void EndGoldCard()
    {
        ImGui.EndChild();
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(2);
    }
}

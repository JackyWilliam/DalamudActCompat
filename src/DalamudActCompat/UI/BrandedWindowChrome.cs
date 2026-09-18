using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;

namespace DalamudActCompat.UI;

internal static class BrandedWindowChrome
{
    private static Vector4 NavyRaised => DactTheme.Tone(new Vector4(0.070f, 0.095f, 0.125f, 1), DactTheme.Palette.Raised);
    private static Vector4 NavigationHover => DactTheme.Tone(new Vector4(0.16f, 0.31f, 0.40f, 0.24f), DactTheme.Palette.Hover);
    private static Vector4 NavigationSelected => DactTheme.Tone(new Vector4(0.14f, 0.34f, 0.46f, 0.30f), DactTheme.Palette.Hover);
    private static Vector4 NavigationText => DactTheme.Tone(new Vector4(0.74f, 0.79f, 0.84f, 1), DactTheme.Palette.Muted);
    private static Vector4 NavigationAccent => DactTheme.Tone(new Vector4(0.42f, 0.78f, 0.96f, 1), DactTheme.Palette.Accent);
    private static Vector4 GoldCardBorder => DactTheme.Tone(new Vector4(0.78f, 0.66f, 0.36f, 0.82f), DactTheme.Palette.Border);
    private static Vector4 GoldCardBackground => DactTheme.Tone(new Vector4(0.055f, 0.075f, 0.10f, 0.96f), DactTheme.Palette.Raised);
    private static readonly Dictionary<string, float> NavigationIndicatorPositions = new(StringComparer.Ordinal);

    public static bool Draw(
        WindowDragController drag,
        ISharedImmediateTexture logoTexture,
        string sectionLabel,
        string centerLabel,
        Vector4 centerColor,
        string versionLabel,
        string id,
        bool showCloseButton = true,
        Action? helpAction = null,
        string? helpTooltip = null,
        Action? statusAction = null,
        string? statusLabel = null,
        Vector4? statusColor = null,
        string? statusTooltip = null,
        Action? friendsAction = null,
        int onlineFriends = 0,
        bool friendsUnread = false,
        Action? logoAction = null,
        Action? versionAction = null)
    {
        DactTheme.DrawGameWindow();
        const float height = 40;
        const float actionButtonSize = 28;
        const float helpCloseGap = 3;
        const float horizontalPadding = 8;
        const float logoSize = 28;
        var start = ImGui.GetCursorPos();
        var screenStart = ImGui.GetCursorScreenPos();
        var availableWidth = ImGui.GetContentRegionAvail().X;
        var screenEnd = screenStart + new Vector2(availableWidth, height);
        var drawList = ImGui.GetWindowDrawList();
        if (!DactTheme.Palette.Light) drawList.AddRectFilled(
            screenStart,
            screenEnd,
            ImGui.GetColorU32(NavyRaised),
            9,
            ImDrawFlags.RoundCornersTop);

        var logoTop = screenStart.Y + ((height - logoSize) * 0.5f);
        var logoLeft = screenStart.X + horizontalPadding;
        // Clip the texture itself so its square corners do not remain visible
        // over the rounded title chrome; the discovery hit target stays unchanged.
        drawList.AddImageRounded(
            logoTexture.GetWrapOrEmpty().Handle,
            new Vector2(logoLeft, logoTop),
            new Vector2(logoLeft + logoSize, logoTop + logoSize),
            Vector2.Zero, Vector2.One, ImGui.GetColorU32(Vector4.One), 6);

        var textTop = screenStart.Y + ((height - ImGui.GetTextLineHeight()) * 0.5f);
        const string title = "Dalamud ACT Compat";
        var titleLeft = logoLeft + logoSize + 9;
        drawList.AddText(
            new Vector2(titleLeft, textTop),
            ImGui.GetColorU32(DactTheme.Palette.Text),
            title);
        drawList.AddText(
            new Vector2(titleLeft + ImGui.CalcTextSize(title).X + 9, textTop),
            ImGui.GetColorU32(DactTheme.Tone(new Vector4(0.68f, 0.72f, 0.77f, 1), DactTheme.Palette.Muted)),
            sectionLabel);

        var helpWidth = helpAction is null ? 0 : actionButtonSize;
        var friendsWidth = friendsAction is null ? 0 : ImGui.CalcTextSize(Math.Max(0, onlineFriends).ToString()).X + 34;
        var friendsGap = friendsWidth > 0 ? 5 : 0;
        var statusWidth = statusAction is null || string.IsNullOrWhiteSpace(statusLabel)
            ? 0
            : ImGui.CalcTextSize(statusLabel).X + 18;
        const float statusHelpGap = 5;
        var statusTrailingGap = statusWidth > 0 && (helpWidth > 0 || showCloseButton)
            ? statusHelpGap
            : 0;
        // Measure the optional status pill as part of the trailing controls so the
        // version label and draggable region can never cover an interactive button.
        var trailingWidth = (showCloseButton ? actionButtonSize : 0) +
                            helpWidth +
                            (showCloseButton && helpAction is not null ? helpCloseGap : 0) +
                            statusWidth +
                            statusTrailingGap + friendsWidth + friendsGap;
        var versionSize = ImGui.CalcTextSize(versionLabel);
        var centerSize = ImGui.CalcTextSize(centerLabel);
        var centerLeft = screenStart.X + ((availableWidth - centerSize.X) * 0.5f);
        var versionLeft = screenStart.X + availableWidth - trailingWidth - versionSize.X - 12;
        // On narrow/scaled windows preserve clickable controls and omit the optional
        // middle status instead of drawing it over the version or friend button.
        if (centerLeft > titleLeft + ImGui.CalcTextSize(title).X + ImGui.CalcTextSize(sectionLabel).X + 24 &&
            centerLeft + centerSize.X < versionLeft - 8)
            drawList.AddText(new Vector2(centerLeft, textTop), ImGui.GetColorU32(centerColor), centerLabel);
        drawList.AddText(
            new Vector2(
                screenStart.X + availableWidth - trailingWidth - versionSize.X - 12,
                textTop),
            ImGui.GetColorU32(DactTheme.Tone(new Vector4(0.62f, 0.66f, 0.71f, 1), DactTheme.Palette.Muted)),
            versionLabel);

        // Give discoveries their own hit targets so a logo click never starts
        // dragging the window. Other windows keep the existing full-width handle.
        var dragLeft = logoAction is null ? 0 : horizontalPadding + logoSize + 5;
        var dragRight = versionAction is null ? availableWidth - trailingWidth : versionLeft - screenStart.X - 4;
        ImGui.SetCursorPos(start + new Vector2(dragLeft, 0));
        ImGui.InvisibleButton(
            $"branded-window-drag-handle##{id}",
            new Vector2(Math.Max(1, dragRight - dragLeft), height));
        drag.HandleItem();
        if (logoAction is not null)
        {
            ImGui.SetCursorScreenPos(new Vector2(logoLeft, logoTop));
            if (ImGui.InvisibleButton($"logo-discovery##{id}", new Vector2(logoSize))) logoAction();
        }
        if (versionAction is not null)
        {
            ImGui.SetCursorScreenPos(new Vector2(versionLeft, screenStart.Y));
            if (ImGui.InvisibleButton($"version-discovery##{id}", new Vector2(versionSize.X, height))) versionAction();
        }

        var closeRequested = false;
        var actionButtonOffsetY = (height - actionButtonSize) * 0.5f;
        if (statusWidth > 0 && statusAction is not null)
        {
            ImGui.SetCursorPos(new Vector2(
                start.X + availableWidth -
                (showCloseButton ? actionButtonSize : 0) -
                helpWidth -
                (showCloseButton && helpWidth > 0 ? helpCloseGap : 0) -
                statusTrailingGap -
                friendsWidth - friendsGap -
                statusWidth,
                start.Y + actionButtonOffsetY));
            DactTheme.PushStyleColor(ImGuiCol.Button, new Vector4(0.055f, 0.12f, 0.16f, 0.88f));
            DactTheme.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.12f, 0.29f, 0.38f, 0.96f));
            DactTheme.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.16f, 0.39f, 0.50f, 1));
            DactTheme.PushStyleColor(ImGuiCol.Text, statusColor ?? NavigationText);
            if (DactTheme.Button(
                    $"{statusLabel}##status-{id}",
                    new Vector2(statusWidth, actionButtonSize)))
            {
                statusAction();
            }
            ImGui.PopStyleColor(4);
            if (!string.IsNullOrWhiteSpace(statusTooltip) && ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(statusTooltip);
            }
        }
        if (friendsAction is not null)
        {
            ImGui.SetCursorPos(new Vector2(start.X + availableWidth -
                (showCloseButton ? actionButtonSize : 0) - helpWidth -
                (showCloseButton && helpWidth > 0 ? helpCloseGap : 0) - friendsWidth - statusTrailingGap,
                start.Y + actionButtonOffsetY));
            var buttonStart = ImGui.GetCursorScreenPos();
            if (ImGui.InvisibleButton($"friends-{id}", new Vector2(friendsWidth, actionButtonSize))) friendsAction();
            drawList.AddRectFilled(buttonStart, buttonStart + new Vector2(friendsWidth, actionButtonSize),
                ImGui.GetColorU32(ImGui.IsItemHovered() ? NavigationHover : DactTheme.Tone(new Vector4(0.055f, 0.12f, 0.16f, 0.88f), DactTheme.Palette.Raised)), 5);
            FriendsGlyph.Draw(drawList, buttonStart + new Vector2(4, 3), 21, ImGui.GetColorU32(NavigationAccent));
            drawList.AddText(buttonStart + new Vector2(28, (actionButtonSize - ImGui.GetTextLineHeight()) / 2),
                ImGui.GetColorU32(DactTheme.Palette.Text), Math.Max(0, onlineFriends).ToString());
            if (friendsUnread) drawList.AddCircleFilled(buttonStart + new Vector2(friendsWidth - 2, 3), 3.5f, ImGui.GetColorU32(new Vector4(1, .3f, .3f, 1)));
            if (ImGui.IsItemHovered()) ImGui.SetTooltip($"好友 · {Math.Max(0, onlineFriends)} 人在线{(friendsUnread ? " · 有未读消息" : "")}");
        }
        if (helpAction is not null)
        {
            ImGui.SetCursorPos(new Vector2(
                start.X + availableWidth -
                (showCloseButton ? actionButtonSize + helpCloseGap : 0) -
                helpWidth,
                start.Y + actionButtonOffsetY));
            DactTheme.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
            DactTheme.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.14f, 0.34f, 0.46f, 0.82f));
            DactTheme.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.18f, 0.45f, 0.60f, 1));
            if (DactTheme.IconButton($"help-{id}", GameSkinIcon.Help, "?", new Vector2(actionButtonSize, actionButtonSize)))
            {
                helpAction();
            }
            ImGui.PopStyleColor(3);
            if (!string.IsNullOrWhiteSpace(helpTooltip) && ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(helpTooltip);
            }
        }
        if (showCloseButton)
        {
            ImGui.SetCursorPos(new Vector2(
                start.X + availableWidth - actionButtonSize,
                start.Y + actionButtonOffsetY));
            DactTheme.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
            DactTheme.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.56f, 0.16f, 0.16f, 0.88f));
            DactTheme.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.72f, 0.20f, 0.20f, 1));
            closeRequested = DactTheme.IconButton(
                $"close-{id}", GameSkinIcon.Close, "×",
                new Vector2(actionButtonSize, actionButtonSize));
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("关闭窗口 / Close window");
            ImGui.PopStyleColor(3);
        }
        ImGui.SetCursorPos(new Vector2(start.X, start.Y + height + 6));
        return closeRequested;
    }

    public static int DrawNavigationRail(
        string id,
        IReadOnlyList<string> labels,
        int selectedIndex,
        float height = 38,
        int notificationIndex = -1)
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

        if (!DactTheme.Palette.Light) drawList.AddRectFilled(
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
        if (!DactTheme.Palette.Light) drawList.AddRectFilled(
            indicatorMin,
            indicatorMax,
            ImGui.GetColorU32(NavigationSelected),
            6);
        if (!DactTheme.Palette.Light) drawList.AddRectFilled(
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
            var texturedTab = false;
            if (DactTheme.Palette.Light)
            {
                // Preserve a generous hit area while matching the game's compact
                // tab proportions; this also scales with the user's text size.
                var tabHeight = Math.Min(height - 6, ImGui.GetTextLineHeight() + 9);
                var tabTop = (height - tabHeight) * .5f;
                texturedTab = DactTheme.DrawGameTab(drawList, itemMin + new Vector2(1, tabTop),
                    itemMin + new Vector2(segmentWidth - 2, tabTop + tabHeight), index == selectedIndex, ImGui.IsItemHovered());
            }
            else if (ImGui.IsItemHovered())
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
            var labelPosition = texturedTab
                ? DactTheme.CenteredTextPosition(labels[index], itemMin, itemMin + new Vector2(segmentWidth, height - 2))
                : new Vector2(
                    itemMin.X + ((segmentWidth - labelSize.X) * 0.5f),
                    itemMin.Y + ((height - labelSize.Y) * 0.5f));
            drawList.AddText(
                labelPosition,
                ImGui.GetColorU32(texturedTab
                    ? Vector4.One
                    : index == selectedIndex ? NavigationAccent : NavigationText),
                labels[index]);
            if (index == notificationIndex)
            {
                var scale = Math.Max(.75f, ImGui.GetFontSize() / 17f);
                drawList.AddCircleFilled(itemMin + new Vector2(segmentWidth - 6 * scale, 6 * scale), 3 * scale,
                    ImGui.GetColorU32(new Vector4(1, .3f, .3f, 1)));
            }
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

    public static bool BeginGoldCard(
        string id,
        float height,
        bool allowScrolling = true)
    {
        DactTheme.PushStyleColor(ImGuiCol.ChildBg, GoldCardBackground);
        DactTheme.PushStyleColor(ImGuiCol.Border, GoldCardBorder);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 8);
        var flags = allowScrolling
            ? ImGuiWindowFlags.None
            : ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        return ImGui.BeginChild(id, new Vector2(-1, height), true, flags);
    }

    public static void EndGoldCard()
    {
        ImGui.EndChild();
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(2);
    }
}

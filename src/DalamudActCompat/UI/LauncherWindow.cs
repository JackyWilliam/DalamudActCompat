using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using DalamudActCompat.Plugin;
using DalamudActCompat.UI.Kit;
using System.Numerics;

namespace DalamudActCompat.UI;

public sealed class LauncherWindow : Window
{
    private const int MinimumButtonSize = 56;
    private const int MaximumButtonSize = 128;

    private readonly PluginConfiguration configuration;
    private readonly ISharedImmediateTexture buttonTexture;
    private readonly UiText text;
    private readonly Action toggleSettings;
    private readonly Action toggleMeter;
    private readonly Action saveConfiguration;
    private bool isDragging;
    private Vector2 dragOffset;

    public LauncherWindow(
        PluginConfiguration configuration,
        ISharedImmediateTexture buttonTexture,
        UiText text,
        Action toggleSettings,
        Action toggleMeter,
        Action saveConfiguration)
        : base("ACT Launcher###DalamudActCompatLauncher")
    {
        this.configuration = configuration;
        this.buttonTexture = buttonTexture;
        this.text = text;
        this.toggleSettings = toggleSettings;
        this.toggleMeter = toggleMeter;
        this.saveConfiguration = saveConfiguration;
        ShowCloseButton = false;
        RespectCloseHotkey = false;
        Flags = ImGuiWindowFlags.NoDecoration |
                ImGuiWindowFlags.NoMove |
                ImGuiWindowFlags.NoResize |
                ImGuiWindowFlags.NoSavedSettings |
                ImGuiWindowFlags.NoBackground |
                ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse |
                ImGuiWindowFlags.NoNav |
                ImGuiWindowFlags.NoFocusOnAppearing |
                ImGuiWindowFlags.NoBringToFrontOnFocus;
    }

    public override bool DrawConditions()
        => configuration.ShowLauncherButton && !configuration.SimplifiedModeEnabled;

    public override void PreDraw()
    {
        var buttonSize = GetButtonSize();
        var buttonDimensions = new Vector2(buttonSize, buttonSize);
        var viewport = ImGui.GetMainViewport();
        var position = ResolveViewportPosition(
            new Vector2(
                configuration.LauncherPositionX,
                configuration.LauncherPositionY),
            viewport.Pos,
            viewport.Size,
            buttonSize);
        Position = position;
        PositionCondition = ImGuiCond.Always;
        Size = buttonDimensions;
        SizeCondition = ImGuiCond.Always;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0);
    }

    public override void PostDraw()
    {
        ImGui.PopStyleVar(2);
    }

    public override void Draw()
    {
        var buttonSize = GetButtonSize();
        var buttonDimensions = new Vector2(buttonSize, buttonSize);
        // 1.0: just the transparent DACT logo, no tile or border (user decision after testing in
        // game). Hover grows it slightly and lifts it from a soft dim, so it still reads as a button.
        ImGui.InvisibleButton("##dact-launcher", buttonDimensions,
            ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight | ImGuiButtonFlags.MouseButtonMiddle);
        var min = ImGui.GetItemRectMin();
        var draw = ImGui.GetWindowDrawList();
        var hover = Kit.DactMotion.Float(ImGuiP.GetItemID(), ImGui.IsItemHovered() ? 1 : 0, Kit.DactMotion.Fast);
        // The resting size leaves room for the hover growth so the logo never clips the window.
        var half = buttonSize * (.46f + .04f * hover);
        var center = min + buttonDimensions * .5f;
        var tint = .88f + .12f * hover;
        draw.AddImage(buttonTexture.GetWrapOrEmpty().Handle, center - new Vector2(half), center + new Vector2(half),
            Vector2.Zero, Vector2.One, ImGui.GetColorU32(new Vector4(tint, tint, tint, 1)));

        var hovered = ImGui.IsItemHovered();
        var leftClicked = ImGui.IsItemClicked(ImGuiMouseButton.Left);
        var rightClicked = ImGui.IsItemClicked(ImGuiMouseButton.Right);
        var middleClicked = ImGui.IsItemClicked(ImGuiMouseButton.Middle);
        if (hovered)
        {
            DrawHelpTooltip();
        }

        if (leftClicked)
        {
            toggleSettings();
        }

        if (rightClicked)
        {
            toggleMeter();
        }

        if (middleClicked)
        {
            isDragging = true;
            dragOffset = ImGui.GetMousePos() - ImGui.GetWindowPos();
        }

        if (isDragging && ImGui.IsMouseDown(ImGuiMouseButton.Middle))
        {
            var viewport = ImGui.GetMainViewport();
            var relativePosition = ClampPosition(
                ImGui.GetMousePos() - dragOffset - viewport.Pos,
                viewport.Size,
                buttonSize);
            configuration.LauncherPositionX = relativePosition.X;
            configuration.LauncherPositionY = relativePosition.Y;
            ImGui.SetWindowPos(viewport.Pos + relativePosition, ImGuiCond.Always);
        }

        if (isDragging && ImGui.IsMouseReleased(ImGuiMouseButton.Middle))
        {
            isDragging = false;
            saveConfiguration();
        }
    }

    private void DrawHelpTooltip()
    {
        // A blank line stands in for the old separator so every skin, 艾欧泽亚's framed tooltip
        // included, draws this help through the one tooltip path.
        DactUi.Tooltip(text.Get("ACT 快捷按钮", "ACT quick button") + "\n\n" +
            text.Get("左键：打开或关闭设置", "Left click: Open or close settings") + "\n" +
            text.Get("右键：打开或关闭战斗统计", "Right click: Open or close Combat Meter") + "\n" +
            text.Get("按住中键：拖动按钮", "Hold middle mouse: Move button"));
    }

    private float GetButtonSize()
    {
        configuration.LauncherButtonSize = Math.Clamp(
            configuration.LauncherButtonSize,
            MinimumButtonSize,
            MaximumButtonSize);
        return configuration.LauncherButtonSize;
    }

    internal static Vector2 ResolveViewportPosition(
        Vector2 relativePosition,
        Vector2 viewportPosition,
        Vector2 viewportSize,
        float buttonSize)
    {
        // Persist viewport-relative coordinates so moving or resizing the game window does
        // not reinterpret the same saved values as desktop-space coordinates.
        return viewportPosition + ClampPosition(relativePosition, viewportSize, buttonSize);
    }

    private static Vector2 ClampPosition(
        Vector2 position,
        Vector2 viewportSize,
        float buttonSize)
    {
        if (viewportSize.X <= buttonSize || viewportSize.Y <= buttonSize)
        {
            return position;
        }

        var maxX = Math.Max(0, viewportSize.X - buttonSize);
        var maxY = Math.Max(0, viewportSize.Y - buttonSize);
        return new Vector2(
            Math.Clamp(position.X, 0, maxX),
            Math.Clamp(position.Y, 0, maxY));
    }
}

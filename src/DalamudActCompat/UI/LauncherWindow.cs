using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using DalamudActCompat.Plugin;
using System.Numerics;

namespace DalamudActCompat.UI;

public sealed class LauncherWindow : Window
{
    private const float ButtonSize = 56;
    private static readonly Vector2 ButtonDimensions = new(ButtonSize, ButtonSize);
    private static readonly Vector2 TextureUvMin = new(1f / 6f, 0);
    private static readonly Vector2 TextureUvMax = new(5f / 6f, 1);

    private readonly PluginConfiguration configuration;
    private readonly ISharedImmediateTexture buttonTexture;
    private readonly UiText text;
    private readonly Action openSettings;
    private readonly Action toggleMeter;
    private readonly Action saveConfiguration;
    private bool isDragging;
    private Vector2 dragOffset;

    public LauncherWindow(
        PluginConfiguration configuration,
        ISharedImmediateTexture buttonTexture,
        UiText text,
        Action openSettings,
        Action toggleMeter,
        Action saveConfiguration)
        : base("ACT Launcher###DalamudActCompatLauncher")
    {
        this.configuration = configuration;
        this.buttonTexture = buttonTexture;
        this.text = text;
        this.openSettings = openSettings;
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

    public override bool DrawConditions() => configuration.ShowLauncherButton;

    public override void PreDraw()
    {
        var position = ClampPosition(new Vector2(
            configuration.LauncherPositionX,
            configuration.LauncherPositionY));
        configuration.LauncherPositionX = position.X;
        configuration.LauncherPositionY = position.Y;
        Position = position;
        PositionCondition = ImGuiCond.Always;
        Size = ButtonDimensions;
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
        var wrap = buttonTexture.GetWrapOrEmpty();
        ImGui.Image(wrap.Handle, ButtonDimensions, TextureUvMin, TextureUvMax);

        var hovered = ImGui.IsItemHovered();
        var leftClicked = ImGui.IsItemClicked(ImGuiMouseButton.Left);
        var rightClicked = ImGui.IsItemClicked(ImGuiMouseButton.Right);
        var middleClicked = ImGui.IsItemClicked(ImGuiMouseButton.Middle);
        if (hovered)
        {
            var drawList = ImGui.GetWindowDrawList();
            drawList.AddRect(
                ImGui.GetItemRectMin(),
                ImGui.GetItemRectMax(),
                ImGui.GetColorU32(new Vector4(0.78f, 0.66f, 0.36f, 0.9f)),
                14,
                ImDrawFlags.None,
                1.5f);
            DrawHelpTooltip();
        }

        if (leftClicked)
        {
            openSettings();
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
            var position = ClampPosition(ImGui.GetMousePos() - dragOffset);
            configuration.LauncherPositionX = position.X;
            configuration.LauncherPositionY = position.Y;
            ImGui.SetWindowPos(position, ImGuiCond.Always);
        }

        if (isDragging && ImGui.IsMouseReleased(ImGuiMouseButton.Middle))
        {
            isDragging = false;
            saveConfiguration();
        }
    }

    private void DrawHelpTooltip()
    {
        ImGui.BeginTooltip();
        ImGui.TextUnformatted(text.Get("ACT 快捷按钮", "ACT quick button"));
        ImGui.Separator();
        ImGui.TextUnformatted(text.Get("左键：打开设置", "Left click: Open settings"));
        ImGui.TextUnformatted(text.Get("右键：显示或隐藏 Meter", "Right click: Show or hide Meter"));
        ImGui.TextUnformatted(text.Get("按住中键：拖动按钮", "Hold middle mouse: Move button"));
        ImGui.EndTooltip();
    }

    private static Vector2 ClampPosition(Vector2 position)
    {
        var displaySize = ImGui.GetIO().DisplaySize;
        if (displaySize.X <= ButtonSize || displaySize.Y <= ButtonSize)
        {
            return position;
        }

        var maxX = Math.Max(0, displaySize.X - ButtonSize);
        var maxY = Math.Max(0, displaySize.Y - ButtonSize);
        return new Vector2(
            Math.Clamp(position.X, 0, maxX),
            Math.Clamp(position.Y, 0, maxY));
    }
}

using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using DalamudActCompat.Plugin;
using DalamudActCompat.UI.Kit;

namespace DalamudActCompat.UI;

public sealed class SimplifiedHomeWindow : Window
{
    private static Vector4 Navy => DactTheme.Tone(new Vector4(0.035f, 0.048f, 0.068f, 1), DactTheme.Palette.Surface);
    private static readonly string VersionLabel =
        $"v{typeof(SimplifiedHomeWindow).Assembly.GetName().Version?.ToString(4) ?? "0.0.0.0"}";
    private readonly PluginConfiguration configuration;
    private readonly ISharedImmediateTexture logoTexture;
    private readonly UiText text;
    private readonly Action<bool> setMeterVisible;
    private readonly Action exitSimplifiedMode;
    private bool locateOnNextDraw;

    public SimplifiedHomeWindow(
        PluginConfiguration configuration,
        ISharedImmediateTexture logoTexture,
        UiText text,
        Action<bool> setMeterVisible,
        Action exitSimplifiedMode)
        : base("精简主页###DalamudActCompatSimplifiedHome")
    {
        this.configuration = configuration;
        this.logoTexture = logoTexture;
        this.text = text;
        this.setMeterVisible = setMeterVisible;
        this.exitSimplifiedMode = exitSimplifiedMode;
        ShowCloseButton = false;
        RespectCloseHotkey = false;
        Size = new Vector2(360, 300);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(320, 270),
            MaximumSize = new Vector2(460, 380),
        };
    }

    public void LocateOnNextDraw()
    {
        locateOnNextDraw = true;
        IsOpen = true;
    }

    public override void PreDraw()
    {
        WindowName = text.Get(
            "精简主页###DalamudActCompatSimplifiedHome",
            "Simplified Home###DalamudActCompatSimplifiedHome");
        Flags = ImGuiWindowFlags.NoTitleBar |
                ImGuiWindowFlags.NoCollapse |
                ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse;
        if (locateOnNextDraw)
        {
            var viewport = ImGui.GetMainViewport();
            ImGui.SetNextWindowPos(
                viewport.Pos + (viewport.Size * 0.5f),
                ImGuiCond.Always,
                new Vector2(0.5f, 0.5f));
            locateOnNextDraw = false;
        }

        DactTheme.PushStyleColor(ImGuiCol.WindowBg, Navy);
        // Indigo of the DACT logo, like the control center; skins substitute their own border.
        DactTheme.PushStyleColor(ImGuiCol.Border, new Vector4(.208f, .251f, .420f, 1));
        DactTheme.PushStyleColor(ImGuiCol.CheckMark, DactTheme.Palette.Accent);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 10);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(20, 18));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6);
        Flags = DactTheme.WindowFlags(Flags);
    }

    public override void PostDraw()
    {
        ImGui.PopStyleVar(3);
        ImGui.PopStyleColor(3);
    }

    // 精简主页 in the 1.0 controls: logo, version, the meter switch and the way back.
    public override void Draw()
    {
        DactUi.BeginFrame();
        DactTheme.DrawGameWindow();
        var s = DactUi.S;
        DrawCloseButton();
        DrawCenteredLogo();
        DrawCenteredText(VersionLabel, DactTheme.Palette.Muted);
        ImGui.Dummy(new Vector2(1, 6 * s));
        DactUi.Divider();
        ImGui.Dummy(new Vector2(1, 4 * s));

        var meterVisible = configuration.Meter.IsVisible;
        if (DactUi.ToggleRow(text.Get("显示战斗统计悬浮窗", "Show Combat Meter overlay"), ref meterVisible)) setMeterVisible(meterVisible);
        ImGui.Dummy(new Vector2(1, 4 * s));
        if (DactUi.Button(text.Get("退出精简模式", "Exit simplified mode") + "##exit-simplified", ButtonKind.Primary, width: ImGui.GetContentRegionAvail().X))
            exitSimplifiedMode();
    }

    private void DrawCloseButton()
    {
        var s = DactUi.S;
        var start = ImGui.GetCursorPos();
        ImGui.SetCursorPos(new Vector2(ImGui.GetWindowWidth() - 10 * s - 28 * s, 10 * s));
        // Closing the menu must not disable simplified mode; `/actcompat` reopens it.
        if (DactUi.IconButton("##close-simplified-home", "close", text.Get("关闭精简主页", "Close simplified home"))) IsOpen = false;
        ImGui.SetCursorPos(start);
    }

    private void DrawCenteredLogo()
    {
        const float logoSize = 92;
        var availableWidth = ImGui.GetContentRegionAvail().X;
        var startX = ImGui.GetCursorPosX() + Math.Max(0, (availableWidth - logoSize) * 0.5f);
        ImGui.SetCursorPosX(startX);
        var wrap = logoTexture.GetWrapOrEmpty();
        ImGui.Image(wrap.Handle, new Vector2(logoSize));
    }

    private static void DrawCenteredText(string value, Vector4 color)
    {
        var width = ImGui.GetContentRegionAvail().X;
        var textSize = ImGui.CalcTextSize(value);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, (width - textSize.X) * 0.5f));
        DactTheme.TextColored(color, value);
    }
}

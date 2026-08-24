using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using DalamudActCompat.Plugin;

namespace DalamudActCompat.UI;

public sealed class SimplifiedHomeWindow : Window
{
    private static readonly Vector4 Navy = new(0.035f, 0.048f, 0.068f, 1);
    private static readonly Vector4 Gold = new(0.78f, 0.66f, 0.36f, 1);
    private static readonly Vector4 IceBlue = new(0.42f, 0.78f, 0.96f, 1);
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

        ImGui.PushStyleColor(ImGuiCol.WindowBg, Navy);
        ImGui.PushStyleColor(ImGuiCol.Border, Gold);
        ImGui.PushStyleColor(ImGuiCol.CheckMark, IceBlue);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 9);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(22, 20));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 5);
    }

    public override void PostDraw()
    {
        ImGui.PopStyleVar(3);
        ImGui.PopStyleColor(3);
    }

    public override void Draw()
    {
        DrawCenteredLogo();
        DrawCenteredText(VersionLabel, new Vector4(0.66f, 0.70f, 0.75f, 1));
        ImGui.Dummy(new Vector2(1, 18));
        ImGui.Separator();
        ImGui.Dummy(new Vector2(1, 12));

        var meterVisible = configuration.Meter.IsVisible;
        if (ImGui.Checkbox(
                text.Get("显示战斗统计悬浮窗", "Show Combat Meter overlay"),
                ref meterVisible))
        {
            setMeterVisible(meterVisible);
        }

        ImGui.Dummy(new Vector2(1, 8));
        var exitRequested = false;
        if (ImGui.Checkbox(
                text.Get("退出精简模式", "Exit simplified mode"),
                ref exitRequested) &&
            exitRequested)
        {
            exitSimplifiedMode();
        }
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
        ImGui.TextColored(color, value);
    }
}

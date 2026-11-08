using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using DalamudActCompat.UI.Kit;

namespace DalamudActCompat.UI;

// The standalone 全局搜索 box the shortcut opens anywhere in the game, with DACT open or closed
// (user request). It floats near the top of the screen and dims nothing; Esc, a click elsewhere
// (the box loses focus) or the shortcut again close it. A result opens the control center there.
internal sealed class SearchWindow : Window
{
    private const float BoxWidth = 560;
    private readonly ControlCenterWindow controlCenter;
    private double openedAt;
    private bool focusField;
    private bool stylePushed;

    public SearchWindow(ControlCenterWindow controlCenter)
        : base("##DalamudActCompatSearch",
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.AlwaysAutoResize)
    {
        this.controlCenter = controlCenter;
        ShowCloseButton = false;
        // Dalamud's own Esc handling closes it at moments when no text field is typing.
        RespectCloseHotkey = true;
        AllowPinning = false;
        AllowClickthrough = false;
    }

    public void ToggleSearch()
    {
        if (IsOpen)
        {
            IsOpen = false;
            return;
        }
        controlCenter.ResetWindowSearch();
        openedAt = ImGui.GetTime();
        focusField = true;
        IsOpen = true;
        BringToFront();
    }

    public override void PreDraw()
    {
        DactUi.BeginFrame();
        var s = DactUi.S;
        var viewport = ImGui.GetMainViewport();
        var progress = DactMotion.Progress(openedAt, .16f);
        // Fixed width, height from the content; it slides down a few pixels as it fades in. Set
        // through ImGui directly (Dalamud's Position/SizeConstraints are left unset) so it holds
        // wherever the window is drawn.
        ImGui.SetNextWindowSizeConstraints(new Vector2(BoxWidth * s, 0), new Vector2(BoxWidth * s, float.MaxValue));
        ImGui.SetNextWindowPos(new Vector2(viewport.Pos.X + (viewport.Size.X - BoxWidth * s) * .5f,
            viewport.Pos.Y + viewport.Size.Y * .16f - (1 - progress) * 8 * s), ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * Math.Max(.01f, progress));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 12 * s);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10 * s));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, Math.Max(1, s));
        // 澄光流璃 / 听雨窗 paint the control center's own pane in Draw instead of a flat fill.
        ImGui.PushStyleColor(ImGuiCol.WindowBg, DactUi.PaneFill(DactTheme.Palette.Raised));
        ImGui.PushStyleColor(ImGuiCol.Border, DactUi.PaneBorder(DactTheme.Palette.Accent with { W = .55f }));
        stylePushed = true;
        if (focusField) ImGui.SetNextWindowFocus();
    }

    public override void Draw()
    {
        DactUi.DrawSkinPane();
        var close = controlCenter.DrawStandaloneSearch(focusField);
        focusField = false;
        // A click in the game (Dalamud then clears ImGui focus) or in another window closes it.
        if (!close && ImGui.GetTime() - openedAt > .15 && !ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows)) close = true;
        if (close) IsOpen = false;
    }

    public override void PostDraw()
    {
        if (!stylePushed) return;
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(4);
        stylePushed = false;
    }
}

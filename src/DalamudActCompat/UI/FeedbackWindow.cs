using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using DalamudActCompat.UI.Kit;

namespace DalamudActCompat.UI;

// 反馈 in a window of its own. It used to be a modal dialog inside the control center, and while
// any ImGui popup is open ImGui claims the mouse for the whole screen: the game and every other
// window ignored clicks until the dialog was closed (反馈 from RaynorDing, 2026-11-12). As an
// ordinary window it opens over the control center but can be dragged aside, the game and other
// windows keep working, and the screenshot picker opens beside it instead of closing it.
internal sealed class FeedbackWindow : Window
{
    private const float FormWidth = 500;
    private readonly ControlCenterWindow controlCenter;
    private double openedAt;
    private bool placeOverControlCenter;
    private bool stylePushed;

    public FeedbackWindow(ControlCenterWindow controlCenter)
        : base("##DalamudActCompatFeedback",
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.AlwaysAutoResize)
    {
        this.controlCenter = controlCenter;
        ShowCloseButton = false;
        // The form handles Esc itself so a send in progress is not abandoned.
        RespectCloseHotkey = false;
        AllowPinning = false;
        AllowClickthrough = false;
    }

    // Opens the window over the control center, or brings an open one forward where it was left.
    public void Present()
    {
        if (!IsOpen)
        {
            openedAt = ImGui.GetTime();
            placeOverControlCenter = true;
        }
        IsOpen = true;
        BringToFront();
    }

    public override void PreDraw()
    {
        DactUi.BeginFrame();
        var s = DactUi.S;
        var progress = DactMotion.Progress(openedAt, DactMotion.DialogSeconds);
        ImGui.SetNextWindowSizeConstraints(new Vector2(FormWidth * s, 0), new Vector2(FormWidth * s, float.MaxValue));
        if (placeOverControlCenter)
        {
            // Where the dialog used to appear: centred on the control center, or on the screen
            // when it is closed. After that the window stays wherever it is dragged.
            var viewport = ImGui.GetMainViewport();
            var center = controlCenter.IsOpen && controlCenter.ShellBounds is { } shell
                ? (shell.Min + shell.Max) * .5f
                : viewport.Pos + viewport.Size * .5f;
            ImGui.SetNextWindowPos(center, ImGuiCond.Always, new Vector2(.5f));
            placeOverControlCenter = false;
        }
        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * Math.Max(.01f, progress));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 12 * s);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(18 * s));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, Math.Max(1, s));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8, 12) * s);
        // 澄光流璃 / 听雨窗 paint the control center's own pane in Draw instead of a flat fill.
        ImGui.PushStyleColor(ImGuiCol.WindowBg, DactUi.PaneFill(DactTheme.Palette.Raised));
        ImGui.PushStyleColor(ImGuiCol.Border, DactUi.PaneBorder(DactTheme.Palette.Border));
        stylePushed = true;
    }

    public override void Draw()
    {
        DactUi.DrawSkinPane();
        if (controlCenter.DrawFeedbackForm()) IsOpen = false;
    }

    public override void PostDraw()
    {
        if (!stylePushed) return;
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(5);
        stylePushed = false;
    }
}

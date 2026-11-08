using Dalamud.Bindings.ImGui;

namespace DalamudActCompat.UI.Kit;

// Secondary DACT windows (使用帮助, 运行状态, 样式编辑器, 三方扩展说明, 封禁通知) fade in when they open
// and fade out when their own close button is used, like the control center; they used to pop in
// and out. PreDraw pushes the alpha last and PostDraw pops it first, so it nests inside the
// window's other style pushes. Paths that set IsOpen directly (Dalamud, plugin code) still close
// at once.
internal sealed class WindowFade
{
    private const float InSeconds = .18f, OutSeconds = .14f;
    private double openedAt = double.NegativeInfinity;
    private double closingAt = -1;
    private bool pushed;

    internal void Opened()
    {
        openedAt = ImGui.GetTime();
        closingAt = -1;
    }

    internal void RequestClose()
    {
        if (closingAt < 0) closingAt = ImGui.GetTime();
    }

    // Pushes this frame's alpha. False once a requested close has faded out: close the window now.
    internal bool Push()
    {
        var closing = closingAt >= 0;
        var alpha = closing ? 1 - DactMotion.Progress(closingAt, OutSeconds) : DactMotion.Progress(openedAt, InSeconds);
        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * Math.Max(.01f, alpha));
        pushed = true;
        if (!closing || alpha > 0) return true;
        closingAt = -1;
        return false;
    }

    internal void Pop()
    {
        if (!pushed) return;
        ImGui.PopStyleVar();
        pushed = false;
    }
}

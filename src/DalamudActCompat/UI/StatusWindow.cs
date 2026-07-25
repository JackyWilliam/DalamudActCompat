using Dalamud.Interface.Windowing;
using DalamudActCompat.Core.Interfaces;
using DalamudActCompat.Parser;
using Dalamud.Bindings.ImGui;

namespace DalamudActCompat.UI;

public sealed class StatusWindow : Window
{
    private readonly IParserEngine parserEngine;
    private ParserStatus status;
    private readonly UiText text;

    public StatusWindow(IParserEngine parserEngine, UiText text)
        : base("ACT 兼容状态###DalamudActCompatStatus")
    {
        this.parserEngine = parserEngine;
        this.text = text;
        status = parserEngine.Status;
        parserEngine.StatusChanged += OnStatusChanged;
    }

    public override void Draw()
    {
        WindowName = text.Get("ACT 兼容状态###DalamudActCompatStatus", "ACT Compat Status###DalamudActCompatStatus");
        ImGui.TextUnformatted($"{text.Get("状态", "State")}: {status.State}");
        ImGui.TextWrapped(status.Message);
        if (!string.IsNullOrWhiteSpace(status.Detail))
        {
            ImGui.TextWrapped(status.Detail);
        }

        ImGui.TextUnformatted($"{text.Get("更新时间", "Updated")}: {status.UpdatedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}");
    }

    public void Detach() => parserEngine.StatusChanged -= OnStatusChanged;

    private void OnStatusChanged(object? sender, ParserStatus next)
        => status = next;
}

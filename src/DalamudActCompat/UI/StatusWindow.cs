using Dalamud.Interface.Windowing;
using DalamudActCompat.Core.Interfaces;
using DalamudActCompat.Parser;
using Dalamud.Bindings.ImGui;

namespace DalamudActCompat.UI;

public sealed class StatusWindow : Window
{
    private readonly IParserEngine parserEngine;
    private ParserStatus status;

    public StatusWindow(IParserEngine parserEngine)
        : base("ACT Compat Status###DalamudActCompatStatus")
    {
        this.parserEngine = parserEngine;
        status = parserEngine.Status;
        parserEngine.StatusChanged += OnStatusChanged;
    }

    public override void Draw()
    {
        ImGui.TextUnformatted($"State: {status.State}");
        ImGui.TextWrapped(status.Message);
        if (!string.IsNullOrWhiteSpace(status.Detail))
        {
            ImGui.TextWrapped(status.Detail);
        }

        ImGui.TextUnformatted($"Updated: {status.UpdatedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}");
    }

    public void Detach() => parserEngine.StatusChanged -= OnStatusChanged;

    private void OnStatusChanged(object? sender, ParserStatus next)
        => status = next;
}

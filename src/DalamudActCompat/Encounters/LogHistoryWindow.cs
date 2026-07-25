using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using DalamudActCompat.Infrastructure.Storage;
using DalamudActCompat.UI;

namespace DalamudActCompat.Encounters;

public sealed class LogHistoryWindow : Window
{
    private readonly PluginPaths paths;
    private readonly UiText text;
    private string? selectedFile;
    private string preview = string.Empty;

    public LogHistoryWindow(PluginPaths paths, UiText text)
        : base("战斗日志历史###DalamudActCompatLogHistory")
    {
        this.paths = paths;
        this.text = text;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new System.Numerics.Vector2(720, 420),
            MaximumSize = new System.Numerics.Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void Draw()
    {
        WindowName = text.Get("战斗日志历史###DalamudActCompatLogHistory", "Combat log history###DalamudActCompatLogHistory");
        Directory.CreateDirectory(paths.EncounterLogDirectory);
        if (ImGui.Button(text.Get("打开日志文件夹", "Open log folder")))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(paths.EncounterLogDirectory)
            {
                UseShellExecute = true,
            });
        }

        ImGui.Separator();
        var files = Directory.EnumerateFiles(paths.EncounterLogDirectory, "*.json")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToArray();
        if (files.Length == 0)
        {
            ImGui.TextDisabled(text.Get("暂无独立战斗日志。完成一场战斗后会自动生成。", "No encounter logs yet. One is created after each encounter."));
            return;
        }

        if (ImGui.BeginChild("log-list", new System.Numerics.Vector2(250, 0), true))
        {
            foreach (var file in files)
            {
                if (ImGui.Selectable(Path.GetFileName(file), string.Equals(file, selectedFile, StringComparison.OrdinalIgnoreCase)))
                {
                    selectedFile = file;
                    preview = File.ReadAllText(file);
                }
            }
        }
        ImGui.EndChild();
        ImGui.SameLine();
        if (ImGui.BeginChild("log-preview", new System.Numerics.Vector2(0, 0), true))
        {
            if (selectedFile is not null)
            {
                ImGui.TextUnformatted(Path.GetFileName(selectedFile));
                ImGui.Separator();
                ImGui.TextWrapped(preview);
            }
        }
        ImGui.EndChild();
    }
}

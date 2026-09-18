using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace DalamudActCompatRepair;

internal sealed class RepairWindow(Action openInstaller, Action repair) : Window("DACT 修复工具###DACTRepair")
{
    public bool Loaded { get; set; }
    public bool Busy { get; set; }
    public bool WrongVersionPresent { get; set; }
    public string Detection { get; set; } = "正在检测…";
    public string Status { get; set; } = "保留账号、皮肤和触发器配置；旧安装会自动备份。";
    public string Backup { get; set; } = "";

    public override void PreDraw()
    {
        Size ??= new Vector2(640, 365);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new() { MinimumSize = new(500, 330), MaximumSize = new(float.MaxValue) };
    }

    public override void Draw()
    {
        ImGui.TextColored(new Vector4(.38f, .75f, .95f, 1), "DACT 版本号修复");
        ImGui.TextWrapped(Detection);
        ImGui.Separator();
        ImGui.TextWrapped("1. 在卫月插件列表停用 DACT（不要卸载）。");
        ImGui.TextWrapped("2. 返回这里点击修复，完成后完整重启一次游戏。");
        ImGui.TextWrapped("3. 重新启用 DACT；此修复工具之后可以卸载。");
        ImGui.Spacing();
        if (ImGui.Button("打开 DACT 管理")) openInstaller();
        ImGui.SameLine();
        ImGui.BeginDisabled(Busy || Loaded || !WrongVersionPresent);
        if (ImGui.Button(Busy ? "正在修复…" : "备份并修复到 0.4.4.0")) repair();
        ImGui.EndDisabled();
        if (Loaded) ImGui.TextColored(new Vector4(1, .73f, .35f, 1), "DACT 仍在运行，请先停用。修复工具保持启用即可。");
        ImGui.Separator();
        ImGui.TextWrapped(Status);
        if (Backup.Length > 0)
        {
            ImGui.TextWrapped("备份位置：" + Backup);
            if (ImGui.Button("复制备份路径")) ImGui.SetClipboardText(Backup);
        }
    }
}

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using DalamudActCompat.Core.Interfaces;
using DalamudActCompat.Infrastructure.Processes;
using DalamudActCompat.Parser;
using DalamudActCompat.Protocol;

namespace DalamudActCompat.UI;

public sealed class StatusWindow : Window
{
    private readonly IParserEngine parserEngine;
    private readonly UiText text;
    private readonly Func<HostSupervisorSnapshot> getHostSnapshot;
    private readonly Action restartHost;
    private readonly Action stopHost;
    private ParserStatus status;

    public StatusWindow(
        IParserEngine parserEngine,
        UiText text,
        Func<HostSupervisorSnapshot> getHostSnapshot,
        Action restartHost,
        Action stopHost)
        : base("ACT 兼容状态###DalamudActCompatStatus")
    {
        this.parserEngine = parserEngine;
        this.text = text;
        this.getHostSnapshot = getHostSnapshot;
        this.restartHost = restartHost;
        this.stopHost = stopHost;
        status = parserEngine.Status;
        parserEngine.StatusChanged += OnStatusChanged;
    }

    public override void Draw()
    {
        WindowName = text.Get(
            "ACT 兼容状态###DalamudActCompatStatus",
            "ACT Compat Status###DalamudActCompatStatus");
        ImGui.TextUnformatted($"{text.Get("状态", "State")}: {status.State}");
        ImGui.TextWrapped(status.Message);
        if (!string.IsNullOrWhiteSpace(status.Detail))
        {
            ImGui.TextWrapped(status.Detail);
        }

        ImGui.Separator();
        var host = getHostSnapshot();
        ImGui.TextUnformatted(
            $"{text.Get("独立 Host", "Independent Host")}: {host.State} / IPC {host.IpcStatus}");
        ImGui.TextUnformatted(
            $"{text.Get("有界队列", "Bounded queues")}: " +
            $"control={host.ControlQueueLength}/{HostProtocol.ControlQueueCapacity}, " +
            $"data={host.DataQueueLength}/{HostProtocol.DataQueueCapacity}");
        ImGui.TextUnformatted(
            $"{text.Get("已丢弃低优先级事件", "Dropped low-priority events")}: {host.DroppedMessages}");
        ImGui.TextUnformatted(
            $"{text.Get("Host 处理进度", "Host processing progress")}: " +
            $"{host.HostAcknowledgedSequence}/{host.LastWrittenSequence}");
        ImGui.TextUnformatted(
            $"{text.Get("Host 资源", "Host resources")}: " +
            $"{host.HostWorkingSetBytes / (1024d * 1024d):0.0} MiB, " +
            $"{host.HostThreadCount} {text.Get("线程", "threads")}");
        ImGui.TextWrapped(
            $"{text.Get("Host 健康", "Host health")}: {host.HealthState} — {host.HealthDetail}");
        if (ImGui.Button(text.Get("重启独立 Host", "Restart independent Host")))
        {
            restartHost();
        }
        ImGui.SameLine();
        if (ImGui.Button(text.Get("停止独立 Host", "Stop independent Host")))
        {
            stopHost();
        }
        foreach (var plugin in host.PluginHealth)
        {
            ImGui.TextWrapped(
                $"{plugin.PluginId}: {plugin.State}, " +
                $"events={plugin.CompletedEvents}, exceptions={plugin.Exceptions}, " +
                $"slow={plugin.SlowCalls}, last={plugin.LastDurationMilliseconds}ms" +
                (plugin.ActiveCallback is null
                    ? string.Empty
                    : $", active={plugin.ActiveMilliseconds}ms"));
        }
        if (host.Diagnostics.Count > 0)
        {
            ImGui.Separator();
            ImGui.TextUnformatted(
                text.Get("最近 Host 结构化异常", "Recent structured Host diagnostics"));
            foreach (var diagnostic in host.Diagnostics.TakeLast(20).Reverse())
            {
                ImGui.TextWrapped(
                    $"[{diagnostic.PluginId}/{diagnostic.Phase}] " +
                    $"{diagnostic.ExceptionType}: {diagnostic.Message} " +
                    $"source={diagnostic.SourceAssembly}/" +
                    $"{diagnostic.SourceType}.{diagnostic.SourceMethod}, " +
                    $"thread={diagnostic.ThreadId}" +
                    (diagnostic.IsWindowsFormsThread ? " (WinForms)" : string.Empty) +
                    $", x{diagnostic.RepeatCount}");
            }
        }
        if (host.PluginStages.Any(stage => stage.PluginId == "postnamazu"))
        {
            ImGui.Separator();
            ImGui.TextUnformatted(
                text.Get("PostNamazu 分阶段状态", "PostNamazu staged status"));
            foreach (var stage in host.PluginStages.Where(stage =>
                         stage.PluginId == "postnamazu"))
            {
                ImGui.TextWrapped(
                    $"{stage.Stage}: {stage.State} — {stage.Detail}");
            }
        }
        if (host.PluginStages.Any(stage => stage.PluginId == "triggernometry"))
        {
            ImGui.Separator();
            ImGui.TextUnformatted(
                text.Get("Triggernometry 兼容状态", "Triggernometry compatibility status"));
            foreach (var stage in host.PluginStages.Where(stage =>
                         stage.PluginId == "triggernometry"))
            {
                ImGui.TextWrapped(
                    $"{stage.Stage}: {stage.State} — {stage.Detail}");
            }
        }
        ImGui.TextWrapped(text.Get(
            "安全边界：所有已安装的传统 ACT 插件均由独立 Host 加载；游戏进程只保留数据采集、" +
            "状态界面和白名单语义命令桥。Host 仍是普通用户权限的完整信任进程，操作系统级沙箱属于后续加固。",
            "Safety boundary: all installed legacy ACT plugins load in the independent Host. " +
            "Only data collection, status UI, and the whitelisted semantic command bridge remain in-game. " +
            "The Host is still a full-trust standard-user process; OS sandboxing is future hardening."));

        ImGui.TextUnformatted(
            $"{text.Get("更新时间", "Updated")}: {status.UpdatedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}");
    }

    public void Detach() => parserEngine.StatusChanged -= OnStatusChanged;

    private void OnStatusChanged(object? sender, ParserStatus next)
        => status = next;
}

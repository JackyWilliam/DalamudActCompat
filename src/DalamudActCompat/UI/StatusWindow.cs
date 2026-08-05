using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using DalamudActCompat.Core.Interfaces;
using DalamudActCompat.Infrastructure.Processes;
using DalamudActCompat.Parser;
using DalamudActCompat.Protocol;

namespace DalamudActCompat.UI;

public sealed class StatusWindow : Window
{
    private static readonly Vector4 Navy = new(0.035f, 0.048f, 0.068f, 1);
    private static readonly Vector4 NavyRaised = new(0.070f, 0.095f, 0.125f, 1);
    private static readonly Vector4 NavyHover = new(0.105f, 0.145f, 0.185f, 1);
    private static readonly Vector4 Gold = new(0.78f, 0.66f, 0.36f, 1);
    private static readonly Vector4 IceBlue = new(0.42f, 0.78f, 0.96f, 1);

    private readonly IParserEngine parserEngine;
    private readonly UiText text;
    private readonly ISharedImmediateTexture logoTexture;
    private readonly Func<HostSupervisorSnapshot> getHostSnapshot;
    private readonly Action restartHost;
    private readonly Action stopHost;
    private ParserStatus status;
    private bool outerFrameStylePushed;

    public StatusWindow(
        IParserEngine parserEngine,
        UiText text,
        ISharedImmediateTexture logoTexture,
        Func<HostSupervisorSnapshot> getHostSnapshot,
        Action restartHost,
        Action stopHost)
        : base("ACT 兼容状态###DalamudActCompatStatus")
    {
        this.parserEngine = parserEngine;
        this.text = text;
        this.logoTexture = logoTexture;
        this.getHostSnapshot = getHostSnapshot;
        this.restartHost = restartHost;
        this.stopHost = stopHost;
        status = parserEngine.Status;
        parserEngine.StatusChanged += OnStatusChanged;
        Size = new Vector2(900, 680);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(720, 500),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        ShowCloseButton = false;
        RespectCloseHotkey = false;
        Flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse;
    }

    public override void PreDraw()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 10);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1);
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0.34f, 0.29f, 0.18f, 0.85f));
        outerFrameStylePushed = true;
    }

    public override void PostDraw()
    {
        if (!outerFrameStylePushed)
        {
            return;
        }

        ImGui.PopStyleColor();
        ImGui.PopStyleVar(2);
        outerFrameStylePushed = false;
    }

    public override void Draw()
    {
        WindowName = text.Get(
            "ACT 兼容状态###DalamudActCompatStatus",
            "ACT Compat Status###DalamudActCompatStatus");
        PushTheme();
        try
        {
            var stateLabel = $"● {LocalizeParserState(status.State)}";
            var stateColor = status.State == ParserState.Running
                ? IceBlue
                : new Vector4(0.70f, 0.72f, 0.76f, 1);
            if (BrandedWindowChrome.Draw(
                    logoTexture,
                    text.Get("运行状态", "Runtime status"),
                    stateLabel,
                    stateColor,
                    ControlCenterWindow.FormatVersionLabel(
                        typeof(StatusWindow).Assembly.GetName().Version),
                    "runtime-status"))
            {
                IsOpen = false;
            }

            if (ImGui.BeginChild("runtime-status-content", new Vector2(-1, -1), true))
            {
                DrawStatusContent();
            }
            ImGui.EndChild();
        }
        finally
        {
            PopTheme();
        }
    }

    public void Detach() => parserEngine.StatusChanged -= OnStatusChanged;

    private void DrawStatusContent()
    {
        DrawCard("runtime-parser-card", text.Get("解析器", "Parser"),
            string.IsNullOrWhiteSpace(status.Detail) ? 126 : 166,
            () =>
            {
                ImGui.TextColored(IceBlue, LocalizeParserState(status.State));
                ImGui.TextWrapped(status.Message);
                if (!string.IsNullOrWhiteSpace(status.Detail))
                {
                    ImGui.TextDisabled(status.Detail);
                }
                ImGui.TextDisabled(
                    $"{text.Get("更新时间", "Updated")}: {status.UpdatedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}");
            });

        var host = getHostSnapshot();
        DrawCard("runtime-host-card", text.Get("独立 ACT Host", "Independent ACT Host"), 226, () =>
        {
            ImGui.TextColored(
                host.State == HostSupervisorState.Running ? IceBlue : Vector4.One,
                $"{text.Get("进程", "Process")}: {LocalizeValue(host.State)}    " +
                $"IPC: {LocalizeValue(host.IpcStatus)}");
            ImGui.TextUnformatted(
                $"{text.Get("有界队列", "Bounded queues")}: " +
                $"control={host.ControlQueueLength}/{HostProtocol.ControlQueueCapacity}, " +
                $"data={host.DataQueueLength}/{HostProtocol.DataQueueCapacity}");
            ImGui.TextUnformatted(
                $"{text.Get("已丢弃低优先级事件", "Dropped low-priority events")}: {host.DroppedMessages}");
            ImGui.TextUnformatted(
                $"{text.Get("处理进度", "Processing progress")}: " +
                $"{host.HostAcknowledgedSequence}/{host.LastWrittenSequence}");
            ImGui.TextUnformatted(
                $"{text.Get("资源", "Resources")}: " +
                $"{host.HostWorkingSetBytes / (1024d * 1024d):0.0} MiB, " +
                $"{host.HostThreadCount} {text.Get("线程", "threads")}");
            ImGui.TextWrapped(
                $"{text.Get("健康状态", "Health")}: {LocalizeValue(host.HealthState)} — {host.HealthDetail}");
            if (ImGui.Button(text.Get("重启独立 Host", "Restart independent Host")))
            {
                restartHost();
            }
            ImGui.SameLine();
            if (ImGui.Button(text.Get("停止独立 Host", "Stop independent Host")))
            {
                stopHost();
            }
        });

        if (host.PluginHealth.Count > 0)
        {
            var height = Math.Clamp(72 + (host.PluginHealth.Count * 38), 118, 260);
            DrawCard("runtime-plugin-health-card", text.Get("扩展运行健康", "Extension runtime health"), height, () =>
            {
                foreach (var plugin in host.PluginHealth)
                {
                    ImGui.TextColored(IceBlue, plugin.PluginId);
                    ImGui.SameLine();
                    ImGui.TextWrapped(
                        $"{LocalizeValue(plugin.State)}, events={plugin.CompletedEvents}, " +
                        $"exceptions={plugin.Exceptions}, slow={plugin.SlowCalls}, " +
                        $"last={plugin.LastDurationMilliseconds}ms" +
                        (plugin.ActiveCallback is null
                            ? string.Empty
                            : $", active={plugin.ActiveMilliseconds}ms"));
                }
            });
        }

        if (host.Diagnostics.Count > 0)
        {
            DrawCard("runtime-diagnostics-card", text.Get("最近 Host 结构化异常", "Recent structured Host diagnostics"), 240, () =>
            {
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
            });
        }

        DrawStages(host, "postnamazu", text.Get("鲶鱼精邮差分阶段状态", "PostNamazu staged status"));
        DrawStages(host, "triggernometry", text.Get("Triggernometry 兼容状态", "Triggernometry compatibility status"));

        DrawCard("runtime-safety-card", text.Get("安全边界", "Safety boundary"), 138, () =>
        {
            ImGui.TextWrapped(text.Get(
                "所有已安装的传统 ACT 插件均由独立 Host 加载。普通权限使用语义桥；用户明确启用鲶鱼精完整权限后，Host 会把准确的游戏进程交给原版原生运行时，以保留签名扫描、高级模块和全部已注册动作。Host 仍是普通用户权限的完整信任进程。",
                "All installed legacy ACT plugins load in the independent Host. Normal permissions use semantic bridges; after the user explicitly grants full PostNamazu permissions, the Host exposes the exact game process to the original native runtime so signature scanning, advanced modules, and every registered action remain available. The Host remains a full-trust standard-user process."));
        });
    }

    private void DrawStages(HostSupervisorSnapshot host, string pluginId, string title)
    {
        var stages = host.PluginStages
            .Where(stage => string.Equals(stage.PluginId, pluginId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (stages.Length == 0)
        {
            return;
        }

        var height = Math.Clamp(70 + (stages.Length * 42), 116, 260);
        DrawCard($"runtime-stages-{pluginId}", title, height, () =>
        {
            foreach (var stage in stages)
            {
                ImGui.TextColored(IceBlue, stage.Stage);
                ImGui.SameLine();
                ImGui.TextWrapped($"{LocalizeValue(stage.State)} — {stage.Detail}");
            }
        });
    }

    private static void DrawCard(string id, string title, float height, Action drawContent)
    {
        if (BrandedWindowChrome.BeginGoldCard(id, height))
        {
            ImGui.TextColored(Gold, title);
            drawContent();
        }
        BrandedWindowChrome.EndGoldCard();
        ImGui.Spacing();
    }

    private string LocalizeParserState(ParserState state) => state switch
    {
        ParserState.Stopped => text.Get("已停止", "Stopped"),
        ParserState.Initializing => text.Get("初始化中", "Initializing"),
        ParserState.Running => text.Get("运行中", "Running"),
        ParserState.Disabled => text.Get("已禁用", "Disabled"),
        ParserState.MissingDependency => text.Get("缺少依赖", "Missing dependency"),
        ParserState.VersionIncompatible => text.Get("版本不兼容", "Version incompatible"),
        ParserState.Faulted => text.Get("故障", "Faulted"),
        _ => state.ToString(),
    };

    private string LocalizeValue(object? value)
    {
        var raw = value?.ToString() ?? "-";
        if (!text.IsChinese)
        {
            return raw;
        }

        return raw.ToLowerInvariant() switch
        {
            "stopped" => "已停止",
            "starting" => "启动中",
            "running" => "运行中",
            "stopping" => "停止中",
            "faulted" => "故障",
            "circuitopen" or "circuit-open" => "熔断中",
            "connecting" => "连接中",
            "connected" => "已连接",
            "suspect" => "连接可疑",
            "healthy" => "正常",
            "degraded" => "降级",
            "disabled" => "已禁用",
            "ready" => "就绪",
            "initializing" => "初始化中",
            _ => raw,
        };
    }

    private static void PushTheme()
    {
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Navy);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Navy);
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0.34f, 0.29f, 0.18f, 0.85f));
        ImGui.PushStyleColor(ImGuiCol.Separator, new Vector4(0.34f, 0.29f, 0.18f, 0.70f));
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.12f, 0.17f, 0.24f, 1));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, NavyHover);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.18f, 0.25f, 0.34f, 1));
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 8);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 5);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8, 8));
    }

    private static void PopTheme()
    {
        ImGui.PopStyleVar(3);
        ImGui.PopStyleColor(7);
    }

    private void OnStatusChanged(object? sender, ParserStatus next)
        => status = next;
}

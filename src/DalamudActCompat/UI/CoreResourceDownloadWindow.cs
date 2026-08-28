using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Windowing;
using DalamudActCompat.Infrastructure.Resources;
using System.Numerics;

namespace DalamudActCompat.UI;

public sealed class CoreResourceDownloadWindow : Window
{
    private readonly UiText text;
    private readonly Func<ResourcePackOperationStatus> getStatus;
    private readonly Action startDownload;
    private readonly Action cancelDownload;
    private bool locateOnNextDraw;

    public CoreResourceDownloadWindow(
        UiText text,
        Func<ResourcePackOperationStatus> getStatus,
        Action startDownload,
        Action cancelDownload)
        : base(text.Get(
            "核心组件不可用###DalamudActCompatCoreResourceDownload",
            "Core components unavailable###DalamudActCompatCoreResourceDownload"))
    {
        this.text = text;
        this.getStatus = getStatus;
        this.startDownload = startDownload;
        this.cancelDownload = cancelDownload;
        Size = new Vector2(470, 190);
        SizeCondition = ImGuiCond.Always;
        Flags = ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize;
        RespectCloseHotkey = false;
    }

    public void Open()
    {
        IsOpen = true;
        locateOnNextDraw = true;
    }

    public override void PreDraw()
    {
        if (!locateOnNextDraw)
        {
            return;
        }

        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(
            viewport.Pos + (viewport.Size * 0.5f),
            ImGuiCond.Always,
            new Vector2(0.5f));
        locateOnNextDraw = false;
    }

    public override void Draw()
    {
        var status = getStatus();
        ImGui.TextWrapped(text.Get(
            "兼容核心组件尚不可用。DACT 基础解析与界面仍可使用；下载核心组件后可启用传统 ACT 扩展。",
            "Compatibility core components are unavailable. DACT parsing and UI remain usable; download them to enable traditional ACT extensions."));
        ImGui.Spacing();

        if (status.State == ResourcePackOperationState.Downloading)
        {
            ImGui.TextUnformatted(text.Get(
                $"下载中...{status.ProgressPercent}%",
                $"Downloading...{status.ProgressPercent}%"));
            ImGui.ProgressBar(status.ProgressPercent / 100f, new Vector2(-1, 22));
            ImGui.Spacing();
            if (ImGuiComponents.IconButton(FontAwesomeIcon.Times))
            {
                cancelDownload();
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(text.Get("取消下载", "Cancel download"));
            }
            return;
        }

        if (!string.IsNullOrWhiteSpace(status.FailureMessage))
        {
            ImGui.TextDisabled(text.Get(
                "上次下载失败，可检查 HTTPS 网络后重试。",
                "The previous download failed. Check HTTPS connectivity and retry."));
        }
        ImGui.Spacing();
        if (ImGuiComponents.IconButton(FontAwesomeIcon.Download))
        {
            startDownload();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(text.Get("下载核心组件", "Download core components"));
        }
        ImGui.SameLine();
        ImGui.TextUnformatted(text.Get("是否下载核心组件？", "Download core components?"));
    }
}

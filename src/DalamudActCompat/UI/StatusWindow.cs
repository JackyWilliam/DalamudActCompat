using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using DalamudActCompat.Core.Interfaces;
using DalamudActCompat.Infrastructure.Processes;
using DalamudActCompat.Parser;
using DalamudActCompat.Protocol;
using DalamudActCompat.UI.Kit;

namespace DalamudActCompat.UI;

public sealed class StatusWindow : Window
{

    private readonly IParserEngine parserEngine;
    private readonly UiText text;
    private readonly WindowDragController headerDrag = new();
    private readonly ISharedImmediateTexture logoTexture;
    private readonly Func<HostSupervisorSnapshot> getHostSnapshot;
    private readonly Func<HostSupervisorSnapshot> getMatchaHostSnapshot;
    private readonly Func<HostSupervisorSnapshot> getGenericHostSnapshot;
    private readonly Action restartHost;
    private readonly Action stopHost;
    private readonly Action ignoreHostMemoryProtection;
    private readonly Action restartMatchaHost;
    private readonly Action stopMatchaHost;
    private readonly Action restartGenericHost;
    private readonly Action stopGenericHost;
    private ParserStatus status;
    private bool outerFrameStylePushed;

    public StatusWindow(
        IParserEngine parserEngine,
        UiText text,
        ISharedImmediateTexture logoTexture,
        Func<HostSupervisorSnapshot> getHostSnapshot,
        Func<HostSupervisorSnapshot> getMatchaHostSnapshot,
        Func<HostSupervisorSnapshot> getGenericHostSnapshot,
        Action restartHost,
        Action stopHost,
        Action ignoreHostMemoryProtection,
        Action restartMatchaHost,
        Action stopMatchaHost,
        Action restartGenericHost,
        Action stopGenericHost)
        : base("ACT 兼容状态###DalamudActCompatStatus")
    {
        this.parserEngine = parserEngine;
        this.text = text;
        this.logoTexture = logoTexture;
        this.getHostSnapshot = getHostSnapshot;
        this.getMatchaHostSnapshot = getMatchaHostSnapshot;
        this.getGenericHostSnapshot = getGenericHostSnapshot;
        this.restartHost = restartHost;
        this.stopHost = stopHost;
        this.ignoreHostMemoryProtection = ignoreHostMemoryProtection;
        this.restartMatchaHost = restartMatchaHost;
        this.stopMatchaHost = stopMatchaHost;
        this.restartGenericHost = restartGenericHost;
        this.stopGenericHost = stopGenericHost;
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
        Flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
    }

    public void Detach() => parserEngine.StatusChanged -= OnStatusChanged;

    private readonly WindowFade fade = new();

    public override void OnOpen() => fade.Opened();

    public override void PreDraw()
    {
        headerDrag.PrepareNextWindow();
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 10);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        // Indigo of the DACT logo, like the control center; skins substitute their own border.
        DactTheme.PushStyleColor(ImGuiCol.Border, new Vector4(.208f, .251f, .420f, 1));
        ImGui.PushStyleColor(ImGuiCol.ResizeGrip, Vector4.Zero);
        outerFrameStylePushed = true;
        Flags = DactTheme.WindowFlags(Flags);
        // Pushed last so it nests inside this window's other style pushes (WindowFade).
        if (!fade.Push()) IsOpen = false;
    }

    public override void PostDraw()
    {
        fade.Pop();
        if (!outerFrameStylePushed) return;
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(3);
        outerFrameStylePushed = false;
    }

    // 运行状态 in the 1.0 frame: a 48 px header with the parser state, then one card per
    // component; each Host card leads with its process and IPC state and keeps its restart and
    // stop buttons.
    public override void Draw()
    {
        WindowName = text.Get("ACT 兼容状态###DalamudActCompatStatus", "ACT Compat Status###DalamudActCompatStatus");
        ControlCenterWindow.PushTheme();
        try
        {
            DactUi.BeginFrame();
            DactTheme.DrawGameWindow();
            var s = DactUi.S;
            var min = ImGui.GetWindowPos();
            var size = ImGui.GetWindowSize();
            var line = Math.Max(1, s);
            DrawHeader(min, size.X, 48 * s);
            ImGui.GetWindowDrawList().AddRectFilled(new Vector2(min.X, min.Y + 48 * s), new Vector2(min.X + size.X, min.Y + 48 * s + line),
                DactUi.U32(DactUi.DividerColor));
            ImGui.SetCursorScreenPos(new Vector2(min.X, min.Y + 48 * s + line));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(22, 18) * s);
            ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize, 8 * s);
            ImGui.PushStyleColor(ImGuiCol.ChildBg, Vector4.Zero);
            if (ImGui.BeginChild("runtime-status-content", new Vector2(-1, size.Y - 48 * s - line), false, ImGuiWindowFlags.AlwaysUseWindowPadding))
            {
                ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8, 14) * s);
                DrawStatusContent();
                ImGui.PopStyleVar();
            }
            ImGui.EndChild();
            ImGui.PopStyleColor();
            ImGui.PopStyleVar(2);
        }
        finally
        {
            ControlCenterWindow.PopTheme();
        }
    }

    private void DrawHeader(Vector2 min, float width, float height)
    {
        var s = DactUi.S;
        var draw = ImGui.GetWindowDrawList();
        var logo = new Vector2(min.X + 14 * s, min.Y + (height - 26 * s) * .5f);
        draw.AddImageRounded(logoTexture.GetWrapOrEmpty().Handle, logo, logo + new Vector2(26 * s), Vector2.Zero, Vector2.One,
            ImGui.GetColorU32(Vector4.One), 6 * s);
        var title = text.Get("运行状态", "Runtime status");
        var x = logo.X + 36 * s;
        DactUi.DrawText(draw, new Vector2(x, min.Y), height, title, DactTheme.Palette.Text);
        x += DactUi.TextWidth(title) + 10 * s;
        var version = ControlCenterWindow.FormatVersionLabel(typeof(StatusWindow).Assembly.GetName().Version);
        DactUi.DrawText(draw, new Vector2(x, min.Y), height, version, DactUi.HintColor, DactFonts.Small);
        var dragLeft = x + DactUi.TextWidth(version, DactFonts.Small) + 8 * s;

        var right = min.X + width - 10 * s - 28 * s;
        ImGui.SetCursorScreenPos(new Vector2(right, min.Y + (height - 28 * s) * .5f));
        if (DactUi.IconButton("##runtime-close", "close", text.Get("关闭", "Close"))) fade.RequestClose();
        var state = LocalizeParserState(status.State);
        var tone = status.State == ParserState.Running ? PillTone.Ok
            : status.State is ParserState.Faulted or ParserState.MissingDependency or ParserState.VersionIncompatible ? PillTone.Error
            : status.State == ParserState.Initializing ? PillTone.Warn : PillTone.Off;
        right -= 10 * s + DactUi.PillWidth(state);
        DactUi.DrawPill(draw, new Vector2(right, min.Y + (height - 22 * s) * .5f), state, tone);
        if (right - 8 * s > dragLeft)
        {
            ImGui.SetCursorScreenPos(new Vector2(dragLeft, min.Y));
            ImGui.InvisibleButton("##runtime-drag", new Vector2(right - 8 * s - dragLeft, height));
            headerDrag.HandleItem();
        }
    }

    private void DrawStatusContent()
    {
        DactUi.BeginCard("runtime-parser-card", text.Get("解析器", "Parser"));
        StatePills((LocalizeParserState(status.State), status.State == ParserState.Running ? PillTone.Ok : PillTone.Off));
        DactUi.Wrapped(status.Message);
        if (!string.IsNullOrWhiteSpace(status.Detail)) DactUi.Hint(status.Detail);
        DactUi.Hint($"{text.Get("更新时间", "Updated")}: {status.UpdatedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}");
        DactUi.EndCard();

        var s = DactUi.S;
        var host = getHostSnapshot();
        DactUi.BeginCard("runtime-host-card", text.Get("共享 ACT Host", "Shared ACT Host"));
        HostState(host);
        Rows(
            (text.Get("有界队列", "Bounded queues"), $"control={host.ControlQueueLength}/{HostProtocol.ControlQueueCapacity}, data={host.DataQueueLength}/{HostProtocol.DataQueueCapacity}"),
            (text.Get("已丢弃低优先级事件", "Dropped low-priority events"), host.DroppedMessages.ToString()),
            (text.Get("处理进度", "Processing progress"), $"{host.HostAcknowledgedSequence}/{host.LastWrittenSequence}"),
            (text.Get("资源", "Resources"), $"working set={FormatBytes(host.HostWorkingSetBytes)}, private={FormatBytes(host.HostPrivateBytes)}, {host.HostThreadCount} {text.Get("线程", "threads")}"),
            (text.Get("系统可用内存", "System available memory"), FormatBytes(host.AvailablePhysicalMemoryBytes)),
            (text.Get("内存保护", "Memory protection"), $"{LocalizeValue(host.MemoryProtection.State)} — {FormatMemoryProtectionDetail(host.MemoryProtection)}"),
            (text.Get("健康状态", "Health"), $"{LocalizeValue(host.HealthState)}{Detail(host.HealthDetail)}"));
        if (DactUi.Button(text.Get("重启共享 Host", "Restart shared Host") + "##restart-shared", icon: "refresh")) restartHost();
        ImGui.SameLine(0, 8 * s);
        if (DactUi.Button(text.Get("停止共享 Host", "Stop shared Host") + "##stop-shared")) stopHost();
        if (host.MemoryProtection.State is HostMemoryProtectionState.Monitoring or HostMemoryProtectionState.DeferredForCombat or HostMemoryProtectionState.EmergencyCountdown)
        {
            var ignore = text.Get("本次忽略自动内存回收", "Ignore automatic recovery this session");
            DactUi.SameLineIfFits(DactUi.ButtonWidth(ignore, ButtonKind.Ghost));
            if (DactUi.Button(ignore + "##ignore-memory", ButtonKind.Ghost)) ignoreHostMemoryProtection();
        }
        DactUi.EndCard();

        var matchaHost = getMatchaHostSnapshot();
        DactUi.BeginCard("runtime-matcha-host-card", text.Get("抹茶专属 Host", "Matcha dedicated Host"));
        HostState(matchaHost);
        Rows(
            (text.Get("有界队列", "Bounded queues"), $"control={matchaHost.ControlQueueLength}/{HostProtocol.ControlQueueCapacity}, data={matchaHost.DataQueueLength}/{HostProtocol.DataQueueCapacity}, network={matchaHost.MatchaNetworkQueueLength}/{HostProtocol.MatchaNetworkQueueCapacity}"),
            (text.Get("已丢弃抹茶网络事件", "Dropped Matcha network events"), matchaHost.DroppedMatchaNetworkMessages.ToString()),
            (text.Get("处理进度", "Processing progress"), $"{matchaHost.HostAcknowledgedSequence}/{matchaHost.LastWrittenSequence}"),
            (text.Get("资源", "Resources"), $"{matchaHost.HostWorkingSetBytes / (1024d * 1024d):0.0} MiB, {matchaHost.HostThreadCount} {text.Get("线程", "threads")}"),
            (text.Get("健康状态", "Health"), $"{LocalizeValue(matchaHost.HealthState)}{Detail(matchaHost.HealthDetail)}"));
        if (DactUi.Button(text.Get("重启抹茶 Host", "Restart Matcha Host") + "##restart-matcha", icon: "refresh")) restartMatchaHost();
        ImGui.SameLine(0, 8 * s);
        if (DactUi.Button(text.Get("停止抹茶 Host", "Stop Matcha Host") + "##stop-matcha")) stopMatchaHost();
        DactUi.EndCard();

        var genericHost = getGenericHostSnapshot();
        DactUi.BeginCard("runtime-generic-host-card", text.Get("通用第三方 Host", "Generic third-party Host"));
        HostState(genericHost);
        DactUi.Hint(text.Get(
            "仅在至少一个已授权的普通 ACT 插件启用时运行；所有这类插件共用这一个进程。",
            "Runs only while at least one authorized generic ACT plugin is enabled; all such plugins share this process."));
        Rows(
            (text.Get("有界队列", "Bounded queues"), $"control={genericHost.ControlQueueLength}/{HostProtocol.ControlQueueCapacity}, data={genericHost.DataQueueLength}/{HostProtocol.DataQueueCapacity}"),
            (text.Get("资源", "Resources"), $"{genericHost.HostWorkingSetBytes / (1024d * 1024d):0.0} MiB, {genericHost.HostThreadCount} {text.Get("线程", "threads")}"),
            (text.Get("健康状态", "Health"), $"{LocalizeValue(genericHost.HealthState)}{Detail(genericHost.HealthDetail)}"));
        if (DactUi.Button(text.Get("重启通用 Host", "Restart generic Host") + "##restart-generic", icon: "refresh")) restartGenericHost();
        ImGui.SameLine(0, 8 * s);
        if (DactUi.Button(text.Get("停止通用 Host", "Stop generic Host") + "##stop-generic")) stopGenericHost();
        DactUi.EndCard();

        PluginHealth("runtime-plugin-health-card", text.Get("扩展运行健康", "Extension runtime health"), host.PluginHealth, true);
        PluginHealth("runtime-matcha-plugin-health-card", text.Get("抹茶运行健康", "Matcha runtime health"), matchaHost.PluginHealth, true);
        PluginHealth("runtime-generic-plugin-health-card", text.Get("普通插件运行健康", "Generic plugin runtime health"), genericHost.PluginHealth, false);
        Diagnostics("runtime-diagnostics-card", text.Get("最近 Host 结构化异常", "Recent structured Host diagnostics"), host.Diagnostics, true);
        Diagnostics("runtime-matcha-diagnostics-card", text.Get("最近抹茶 Host 异常", "Recent Matcha Host diagnostics"), matchaHost.Diagnostics, true);
        Diagnostics("runtime-generic-diagnostics-card", text.Get("最近通用 Host 异常", "Recent generic Host diagnostics"), genericHost.Diagnostics, false);

        DrawStages(host, "postnamazu", text.Get("鲶鱼精邮差分阶段状态", "PostNamazu staged status"));
        DrawStages(host, "triggernometry", text.Get("Triggernometry 兼容状态", "Triggernometry compatibility status"));
        DrawStages(matchaHost, "matcha", text.Get("抹茶兼容状态", "Matcha compatibility status"));
        foreach (var pluginId in genericHost.PluginStages.Select(static stage => stage.PluginId).Distinct(StringComparer.OrdinalIgnoreCase))
            DrawStages(genericHost, pluginId, $"{pluginId} — {text.Get("通用 Host 状态", "generic Host status")}");

        DactUi.BeginCard("runtime-safety-card", text.Get("安全边界", "Safety boundary"));
        DactUi.Wrapped(text.Get(
            "FoxTTS、鲶鱼精邮差、Triggernometry 与银山雀儿保持在原共享 Host；抹茶独占专属 Host；用户自行安装且未特化的插件共用一个按需启动的通用 Host。三个边界互不连带崩溃，但都是普通用户权限的完整信任桌面进程。",
            "FoxTTS, PostNamazu, Triggernometry, and SilverDasher stay in the original shared Host; Matcha owns its dedicated Host; user-installed non-specialized plugins share one on-demand generic Host. Failures do not cross these three boundaries, but every Host remains a full-trust standard-user desktop process."));
        DactUi.EndCard();
    }

    private static string Detail(string? detail) => string.IsNullOrWhiteSpace(detail) ? string.Empty : $" — {detail}";

    private void HostState(HostSupervisorSnapshot host)
        => StatePills(
            ($"{text.Get("进程", "Process")} {LocalizeValue(host.State)}", host.State == HostSupervisorState.Running ? PillTone.Ok : PillTone.Off),
            ($"IPC {LocalizeValue(host.IpcStatus)}", host.IpcStatus.ToString().Equals("Connected", StringComparison.OrdinalIgnoreCase) ? PillTone.Ok : PillTone.Off));

    private static void StatePills(params (string Label, PillTone Tone)[] pills)
    {
        var s = DactUi.S;
        for (var index = 0; index < pills.Length; index++)
        {
            if (index > 0) ImGui.SameLine(0, 8 * s);
            DactUi.Pill(pills[index].Label, pills[index].Tone);
        }
    }

    // Label column on the left (muted), value on the right; long values wrap in their column.
    private static void Rows(params (string Label, string Value)[] rows)
    {
        var s = DactUi.S;
        var width = ImGui.GetContentRegionAvail().X;
        var labelWidth = Math.Min(width * .35f, Math.Max(112 * s, rows.Max(row => DactUi.TextWidth(row.Label, DactFonts.Small)) + 8 * s));
        ImGui.BeginGroup();
        using (DactUi.Gap(6))
        {
            foreach (var (label, value) in rows)
            {
                var start = ImGui.GetCursorScreenPos();
                ImGui.BeginGroup();
                DactUi.Wrapped(label, DactTheme.Palette.Muted, DactFonts.Small, labelWidth);
                ImGui.EndGroup();
                var bottom = ImGui.GetItemRectMax().Y;
                ImGui.SetCursorScreenPos(start + new Vector2(labelWidth + 12 * s, 0));
                ImGui.BeginGroup();
                DactUi.Wrapped(value, DactTheme.Palette.Text, DactFonts.Small, width - labelWidth - 12 * s);
                ImGui.EndGroup();
                bottom = Math.Max(bottom, ImGui.GetItemRectMax().Y);
                ImGui.SetCursorScreenPos(new Vector2(start.X, bottom));
                ImGui.Dummy(new Vector2(width, 0));
            }
        }
        ImGui.EndGroup();
    }

    private void PluginHealth(string id, string title, IReadOnlyList<HostPluginHealth> plugins, bool showActive)
    {
        if (plugins.Count == 0) return;
        DactUi.BeginCard(id, title);
        Rows(plugins.Select(plugin => (plugin.PluginId,
            $"{LocalizeValue(plugin.State)}, events={plugin.CompletedEvents}, exceptions={plugin.Exceptions}, slow={plugin.SlowCalls}, last={plugin.LastDurationMilliseconds}ms" +
            (showActive && plugin.ActiveCallback is not null ? $", active={plugin.ActiveMilliseconds}ms" : string.Empty))).ToArray());
        DactUi.EndCard();
    }

    private static void Diagnostics(string id, string title, IReadOnlyList<HostDiagnostic> diagnostics, bool detailed)
    {
        if (diagnostics.Count == 0) return;
        DactUi.BeginCard(id, title, gap: 6);
        foreach (var diagnostic in diagnostics.TakeLast(20).Reverse())
            DactUi.Wrapped(detailed
                    ? $"[{diagnostic.PluginId}/{diagnostic.Phase}] {diagnostic.ExceptionType}: {diagnostic.Message} " +
                      $"source={diagnostic.SourceAssembly}/{diagnostic.SourceType}.{diagnostic.SourceMethod}, thread={diagnostic.ThreadId}" +
                      (diagnostic.IsWindowsFormsThread ? " (WinForms)" : string.Empty) + $", x{diagnostic.RepeatCount}"
                    : $"[{diagnostic.PluginId}/{diagnostic.Phase}] {diagnostic.ExceptionType}: {diagnostic.Message}",
                DactTheme.Palette.Text, DactFonts.Small);
        DactUi.EndCard();
    }

    private void DrawStages(HostSupervisorSnapshot host, string pluginId, string title)
    {
        var stages = host.PluginStages.Where(stage => string.Equals(stage.PluginId, pluginId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (stages.Length == 0) return;
        DactUi.BeginCard($"runtime-stages-{pluginId}", title);
        Rows(stages.Select(stage => (stage.Stage, $"{LocalizeValue(stage.State)}{Detail(stage.Detail)}")).ToArray());
        DactUi.EndCard();
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
        return raw.ToLowerInvariant() switch
        {
            "stopped" => text.Get("已停止", "Stopped"),
            "starting" => text.Get("启动中", "Starting"),
            "running" => text.Get("运行中", "Running"),
            "stopping" => text.Get("停止中", "Stopping"),
            "faulted" => text.Get("故障", "Faulted"),
            "circuitopen" or "circuit-open" => text.Get("熔断中", "Circuit open"),
            "connecting" => text.Get("连接中", "Connecting"),
            "connected" => text.Get("已连接", "Connected"),
            "suspect" => text.Get("连接可疑", "Suspect"),
            "healthy" => text.Get("正常", "Healthy"),
            "degraded" => text.Get("降级", "Degraded"),
            "disabled" => text.Get("已禁用", "Disabled"),
            "normal" => text.Get("正常", "Normal"),
            "monitoring" => text.Get("观察中", "Monitoring"),
            "deferredforcombat" => text.Get("等待脱战", "Waiting for combat to end"),
            "emergencycountdown" => text.Get("紧急倒计时", "Emergency countdown"),
            "recycling" => text.Get("平滑重启中", "Restarting gracefully"),
            "ignored" => text.Get("本次已忽略", "Ignored for this session"),
            "ready" => text.Get("就绪", "Ready"),
            "initializing" => text.Get("初始化中", "Initializing"),
            _ => raw,
        };
    }

    private string FormatMemoryProtectionDetail(HostMemoryProtectionSnapshot snapshot)
    {
        var detail = snapshot.State switch
        {
            HostMemoryProtectionState.Normal => text.Get(
                "共享 Host 私有内存低于自动保护阈值。",
                snapshot.Detail),
            HostMemoryProtectionState.Monitoring => text.Get(
                "共享 Host 已达到 3 GiB，需持续 15 秒才会在脱战后自动恢复。",
                snapshot.Detail),
            HostMemoryProtectionState.DeferredForCombat => text.Get(
                "已持续超过 3 GiB，正在等待脱战。",
                snapshot.Detail),
            HostMemoryProtectionState.EmergencyCountdown => text.Get(
                "共享 Host 已达到 4 GiB，或系统可用内存不足 2 GiB。",
                snapshot.Detail),
            HostMemoryProtectionState.Recycling => text.Get(
                "正在排空队列并平滑重启共享 Host。",
                snapshot.Detail),
            HostMemoryProtectionState.Ignored => text.Get(
                "本次 Host 会话已忽略自动内存回收。",
                snapshot.Detail),
            HostMemoryProtectionState.CircuitOpen => text.Get(
                "十分钟内已自动恢复两次，保护已停止 Host 并打开熔断。",
                snapshot.Detail),
            _ => snapshot.Detail,
        };
        if (snapshot.CountdownEndsAt is { } countdownEndsAt)
        {
            var seconds = Math.Max(0, Math.Ceiling((countdownEndsAt - DateTimeOffset.UtcNow).TotalSeconds));
            return text.Format(
                $"{detail} 剩余约 {seconds:0} 秒，可选择本次忽略。",
                $"{detail} About {seconds:0} seconds remain; recovery can be ignored for this session.");
        }

        return detail;
    }

    private static string FormatBytes(long bytes)
        => bytes <= 0
            ? "-"
            : bytes >= 1024L * 1024L * 1024L
                ? $"{bytes / (1024d * 1024d * 1024d):0.00} GiB"
                : $"{bytes / (1024d * 1024d):0.0} MiB";

    private void OnStatusChanged(object? sender, ParserStatus next)
        => status = next;
}

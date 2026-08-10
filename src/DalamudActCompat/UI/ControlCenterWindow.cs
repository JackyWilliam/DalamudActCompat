using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using DalamudActCompat.ActRuntime;
using DalamudActCompat.Compatibility.PluginHost;
using DalamudActCompat.Compatibility.Cactbot;
using DalamudActCompat.Core.Interfaces;
using DalamudActCompat.Core.Models;
using DalamudActCompat.Fflogs;
using DalamudActCompat.Infrastructure.Logging;
using DalamudActCompat.Meter;
using DalamudActCompat.Parser;
using DalamudActCompat.Plugin;
using System.Numerics;

namespace DalamudActCompat.UI;

public sealed class ControlCenterWindow : Window
{
    private enum VisibilityTransition
    {
        Closed,
        Opening,
        Open,
        Closing,
    }

    private enum Page
    {
        Overview,
        Meter,
        Overlays,
        Extensions,
        Diagnostics,
    }

    private static readonly Vector4 Navy = new(0.035f, 0.048f, 0.068f, 1);
    private static readonly Vector4 NavyRaised = new(0.070f, 0.095f, 0.125f, 1);
    private static readonly Vector4 NavyHover = new(0.105f, 0.145f, 0.185f, 1);
    private static readonly Vector4 Gold = new(0.78f, 0.66f, 0.36f, 1);
    private static readonly Vector4 IceBlue = new(0.42f, 0.78f, 0.96f, 1);
    private static readonly string VersionLabel = BuildVersionLabel();
    private const int OpenAnimationMilliseconds = 180;
    private const int CloseAnimationMilliseconds = 160;
    private const int ResetConfirmationMilliseconds = 10_000;
    private const string ResetEncounterPopupId = "重置当前战斗###DalamudActCompatResetEncounter";

    private readonly PluginConfiguration configuration;
    private readonly IParserEngine parserEngine;
    private readonly PluginLogger logger;
    private readonly UiText text;
    private readonly FflogsEstimateService fflogsEstimateService;
    private readonly Func<Encounter?> getCurrentEncounter;
    private readonly ISharedImmediateTexture logoTexture;
    private readonly Action saveConfiguration;
    private readonly Action applyPermissionChanges;
    private readonly Action<bool> setMeterVisible;
    private readonly Action openMeter;
    private readonly Action openHistory;
    private readonly Func<bool> isStatusVisible;
    private readonly Action<bool> setStatusVisible;
    private readonly Action selectPluginPackage;
    private readonly Action openPluginDirectory;
    private readonly Action openBundledPluginNotice;
    private readonly Action checkBundledPluginUpdates;
    private readonly Action openLogDirectory;
    private readonly Func<string> openCombatLogDirectory;
    private readonly Func<string> buildDiagnosticReport;
    private readonly Func<IReadOnlyList<InstalledActPlugin>> discoverPlugins;
    private readonly Action<string> openPluginConfiguration;
    private readonly Func<bool> isCactbotInstalled;
    private readonly Func<CactbotOperationStatus> getCactbotOperationStatus;
    private readonly Action selectCactbotPackage;
    private readonly Action openCactbotOverlay;
    private readonly Action openCactbotSettings;
    private readonly Func<IReadOnlyList<ActOverlayTemplate>> getOverlayTemplates;
    private readonly Func<string, bool> openHtmlOverlay;
    private readonly Action<string> closeHtmlOverlay;
    private readonly Action<string> deleteHtmlOverlay;
    private readonly Action<string> applyOverlayWindowSettings;
    private readonly Func<Task<string>> factoryReset;
    private readonly Action resetCurrentEncounter;
    private Page selectedPage;
    private ParserStatus parserStatus;
    private string? selectedCreatedOverlay;
    private string? selectedUsedCactbotOverlay;
    private string? selectedAvailableCactbotOverlay;
    private string customOverlayName = string.Empty;
    private string customOverlayUrl = string.Empty;
    private string? customOverlayFeedback;
    private bool customOverlayFeedbackIsError;
    private string? diagnosticCopyFeedback;
    private bool diagnosticCopyFeedbackIsError;
    private string? combatLogFolderFeedback;
    private bool combatLogFolderFeedbackIsError;
    private VisibilityTransition visibilityTransition = VisibilityTransition.Closed;
    private long visibilityTransitionStartedAt;
    private bool visibilityStylePushed;
    private long resetEncounterConfirmationExpiresAt;
    private bool confirmFactoryReset;
    private string? factoryResetResult;

    public ControlCenterWindow(
        PluginConfiguration configuration,
        IParserEngine parserEngine,
        PluginLogger logger,
        UiText text,
        FflogsEstimateService fflogsEstimateService,
        Func<Encounter?> getCurrentEncounter,
        ISharedImmediateTexture logoTexture,
        Action saveConfiguration,
        Action applyPermissionChanges,
        Action<bool> setMeterVisible,
        Action openMeter,
        Action openHistory,
        Func<bool> isStatusVisible,
        Action<bool> setStatusVisible,
        Action selectPluginPackage,
        Action openPluginDirectory,
        Action openBundledPluginNotice,
        Action checkBundledPluginUpdates,
        Action openLogDirectory,
        Func<string> openCombatLogDirectory,
        Func<string> buildDiagnosticReport,
        Func<IReadOnlyList<InstalledActPlugin>> discoverPlugins,
        Action<string> openPluginConfiguration,
        Func<bool> isCactbotInstalled,
        Func<CactbotOperationStatus> getCactbotOperationStatus,
        Action selectCactbotPackage,
        Action openCactbotOverlay,
        Action openCactbotSettings,
        Func<IReadOnlyList<ActOverlayTemplate>> getOverlayTemplates,
        Func<string, bool> openHtmlOverlay,
        Action<string> closeHtmlOverlay,
        Action<string> deleteHtmlOverlay,
        Func<Task<string>> factoryReset,
        Action resetCurrentEncounter,
        Action<string> applyOverlayWindowSettings)
        : base("ACT 控制中心###DalamudActCompatControlCenter")
    {
        this.configuration = configuration;
        this.parserEngine = parserEngine;
        this.logger = logger;
        this.text = text;
        this.fflogsEstimateService = fflogsEstimateService;
        this.getCurrentEncounter = getCurrentEncounter;
        this.logoTexture = logoTexture;
        this.saveConfiguration = saveConfiguration;
        this.applyPermissionChanges = applyPermissionChanges;
        this.setMeterVisible = setMeterVisible;
        this.openMeter = openMeter;
        this.openHistory = openHistory;
        this.isStatusVisible = isStatusVisible;
        this.setStatusVisible = setStatusVisible;
        this.selectPluginPackage = selectPluginPackage;
        this.openPluginDirectory = openPluginDirectory;
        this.openBundledPluginNotice = openBundledPluginNotice;
        this.checkBundledPluginUpdates = checkBundledPluginUpdates;
        this.openLogDirectory = openLogDirectory;
        this.openCombatLogDirectory = openCombatLogDirectory;
        this.buildDiagnosticReport = buildDiagnosticReport;
        this.discoverPlugins = discoverPlugins;
        this.openPluginConfiguration = openPluginConfiguration;
        this.isCactbotInstalled = isCactbotInstalled;
        this.getCactbotOperationStatus = getCactbotOperationStatus;
        this.selectCactbotPackage = selectCactbotPackage;
        this.openCactbotOverlay = openCactbotOverlay;
        this.openCactbotSettings = openCactbotSettings;
        this.getOverlayTemplates = getOverlayTemplates;
        this.openHtmlOverlay = openHtmlOverlay;
        this.closeHtmlOverlay = closeHtmlOverlay;
        this.deleteHtmlOverlay = deleteHtmlOverlay;
        this.factoryReset = factoryReset;
        this.resetCurrentEncounter = resetCurrentEncounter;
        this.applyOverlayWindowSettings = applyOverlayWindowSettings;
        parserStatus = parserEngine.Status;
        parserEngine.StatusChanged += OnParserStatusChanged;
        Size = new Vector2(920, 640);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(760, 520),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        ShowCloseButton = false;
        RespectCloseHotkey = false;
        Flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse;
    }

    public void ShowAnimated()
    {
        IsOpen = true;
        visibilityTransition = VisibilityTransition.Opening;
        visibilityTransitionStartedAt = Environment.TickCount64;
    }

    public void HideAnimated()
    {
        if (!IsOpen || visibilityTransition == VisibilityTransition.Closing)
        {
            return;
        }

        saveConfiguration();
        visibilityTransition = VisibilityTransition.Closing;
        visibilityTransitionStartedAt = Environment.TickCount64;
    }

    public void ToggleAnimated()
    {
        if (IsOpen && visibilityTransition != VisibilityTransition.Closing)
        {
            HideAnimated();
        }
        else
        {
            ShowAnimated();
        }
    }

    public override void PreDraw()
    {
        var alpha = visibilityTransition switch
        {
            VisibilityTransition.Opening => EaseInOut(TransitionProgress(OpenAnimationMilliseconds)),
            VisibilityTransition.Closing => 1 - EaseInOut(TransitionProgress(CloseAnimationMilliseconds)),
            VisibilityTransition.Closed => 0,
            _ => 1,
        };
        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, Math.Clamp(alpha, 0.01f, 1));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 10);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1);
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0.34f, 0.29f, 0.18f, 0.85f));
        visibilityStylePushed = true;
    }

    public override void PostDraw()
    {
        if (visibilityStylePushed)
        {
            ImGui.PopStyleColor();
            ImGui.PopStyleVar(3);
            visibilityStylePushed = false;
        }
    }

    public override void Draw()
    {
        if (visibilityTransition == VisibilityTransition.Opening &&
            TransitionProgress(OpenAnimationMilliseconds) >= 1)
        {
            visibilityTransition = VisibilityTransition.Open;
        }
        else if (visibilityTransition == VisibilityTransition.Closing &&
                 TransitionProgress(CloseAnimationMilliseconds) >= 1)
        {
            visibilityTransition = VisibilityTransition.Closed;
            IsOpen = false;
            return;
        }

        WindowName = text.Get(
            "ACT 控制中心###DalamudActCompatControlCenter",
            "ACT Control Center###DalamudActCompatControlCenter");

        PushTheme();
        try
        {
            DrawWindowChrome();
            DrawPageTabs();
            ImGui.Spacing();
            if (ImGui.BeginChild("control-center-page-content", new Vector2(-1, -1), true))
            {
                var permissionsChanged = false;
                var changed = selectedPage switch
                {
                    Page.Overview => DrawOverview(),
                    Page.Meter => DrawMeter(),
                    Page.Overlays => DrawOverlays(),
                    Page.Extensions => DrawExtensions(out permissionsChanged),
                    Page.Diagnostics => DrawDiagnostics(),
                    _ => false,
                };

                if (changed)
                {
                    saveConfiguration();
                }

                if (permissionsChanged)
                {
                    applyPermissionChanges();
                }
            }
            ImGui.EndChild();
        }
        finally
        {
            PopTheme();
        }
    }

    public override void OnClose() => saveConfiguration();

    public void Detach() => parserEngine.StatusChanged -= OnParserStatusChanged;

    private string FormatCactbotStatus()
    {
        var status = getCactbotOperationStatus();
        return status.State switch
        {
            CactbotOperationState.Checking => text.Get("正在检查资源…", "Checking assets…"),
            CactbotOperationState.Installing => text.Get("正在安装资源…", "Installing assets…"),
            CactbotOperationState.Error => text.Get(
                $"安装失败：{status.ErrorMessage}",
                $"Installation failed: {status.ErrorMessage}"),
            _ when isCactbotInstalled() => text.Get("资源已安装", "Assets installed"),
            _ => text.Get("资源未安装", "Assets not installed"),
        };
    }

    internal static float EaseInOut(float progress)
    {
        var value = Math.Clamp(progress, 0, 1);
        return value * value * (3 - (2 * value));
    }

    private float TransitionProgress(int durationMilliseconds)
        => Math.Clamp(
            (Environment.TickCount64 - visibilityTransitionStartedAt) /
            (float)durationMilliseconds,
            0,
            1);

    private void DrawWindowChrome()
    {
        var stateLabel = $"● {LocalizeState(parserStatus.State)}";
        var stateColor = parserStatus.State == ParserState.Running
            ? IceBlue
            : new Vector4(0.70f, 0.72f, 0.76f, 1);
        if (BrandedWindowChrome.Draw(
                logoTexture,
                text.Get("主页", "Home"),
                stateLabel,
                stateColor,
                VersionLabel,
                "control-center"))
        {
            HideAnimated();
        }
    }

    private void DrawPageTabs()
    {
        var tabs = new (Page Page, string Label)[]
        {
            (Page.Overview, text.Get("概览", "Overview")),
            (Page.Meter, text.Get("战斗统计", "Combat Meter")),
            (Page.Overlays, text.Get("悬浮窗", "Overlays")),
            (Page.Extensions, text.Get("扩展", "Extensions")),
            (Page.Diagnostics, text.Get("设置", "Settings")),
        };
        var currentIndex = Array.FindIndex(tabs, tab => tab.Page == selectedPage);
        var nextIndex = BrandedWindowChrome.DrawNavigationRail(
            "control-center-navigation",
            tabs.Select(tab => tab.Label).ToArray(),
            currentIndex);
        if (nextIndex != currentIndex)
        {
            selectedPage = tabs[nextIndex].Page;
        }
    }
    private bool DrawOverview()
    {
        DrawPageHeader(
            text.Get("概览", "Overview"),
            text.Get("常用状态和入口集中在这里。", "Status and common actions in one place."),
            showDivider: false);

        var parserCardHeight = string.IsNullOrWhiteSpace(parserStatus.Detail) ? 108 : 142;
        if (BrandedWindowChrome.BeginGoldCard("overview-parser-card", parserCardHeight))
        {
            ImGui.TextColored(Gold, text.Get("解析器", "Parser"));
            ImGui.TextColored(IceBlue, LocalizeState(parserStatus.State));
            ImGui.TextWrapped(parserStatus.Message);
            if (!string.IsNullOrWhiteSpace(parserStatus.Detail))
            {
                ImGui.TextDisabled(parserStatus.Detail);
            }
        }
        BrandedWindowChrome.EndGoldCard();

        ImGui.Spacing();
        if (BrandedWindowChrome.BeginGoldCard("overview-quick-actions-card", 140))
        {
            ImGui.TextColored(Gold, text.Get("快捷入口", "Quick actions"));
            var meterVisible = configuration.Meter.IsVisible;
            if (meterVisible)
            {
                PushOpenWindowButtonStyle();
            }
            if (ImGui.Button(
                    meterVisible
                        ? text.Get("关闭战斗统计", "Close Combat Meter")
                        : text.Get("打开战斗统计", "Open Combat Meter"),
                    new Vector2(150, 36)))
            {
                setMeterVisible(!meterVisible);
            }
            if (meterVisible)
            {
                ImGui.PopStyleColor(4);
            }
            ImGui.SameLine();
            if (ImGui.Button(text.Get("战斗历史", "Encounter history"), new Vector2(150, 36)))
            {
                openHistory();
            }
            ImGui.SameLine();
            DrawStatusWindowToggleButton(new Vector2(150, 36));

            if (ImGui.Button(
                    text.Get("打开 FFLogs 上传日志", "Open FFLogs upload logs"),
                    new Vector2(230, 36)))
            {
                OpenCombatLogDirectoryForUpload();
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(text.Get(
                    "打开 FFLogs 上传使用的原始 Network 日志目录，并把文件夹路径复制到剪贴板。",
                    "Opens the raw Network log directory used for FFLogs uploads and copies its folder path to the clipboard."));
            }
            if (!string.IsNullOrWhiteSpace(combatLogFolderFeedback))
            {
                ImGui.SameLine();
                ImGui.TextColored(
                    combatLogFolderFeedbackIsError
                        ? new Vector4(0.95f, 0.45f, 0.40f, 1)
                        : IceBlue,
                    combatLogFolderFeedback);
            }
        }
        BrandedWindowChrome.EndGoldCard();

        ImGui.Spacing();
        var changed = false;
        if (BrandedWindowChrome.BeginGoldCard("overview-general-card", 142))
        {
            ImGui.TextColored(Gold, text.Get("基础设置", "General"));
            changed |= Checkbox(
                text.Get("启用解析", "Enable parsing"),
                configuration.EnableParsing,
                value => configuration.EnableParsing = value);
            changed |= Checkbox(
                text.Get("自动启动解析器", "Auto start parser"),
                configuration.AutoStartParser,
                value => configuration.AutoStartParser = value);
            changed |= Checkbox(
                text.Get("显示 ACT 快捷按钮", "Show ACT quick button"),
                configuration.ShowLauncherButton,
                value => configuration.ShowLauncherButton = value);
            ImGui.TextDisabled(text.Get(
                "快捷按钮：左键设置、右键战斗统计、按住中键拖动。",
                "Quick button: left settings, right Combat Meter, hold middle mouse to move."));
        }
        BrandedWindowChrome.EndGoldCard();
        return changed;
    }

    private void OpenCombatLogDirectoryForUpload()
    {
        try
        {
            var directory = openCombatLogDirectory();
            ImGui.SetClipboardText(directory);
            combatLogFolderFeedback = text.Get(
                "已打开，文件夹路径已复制",
                "Opened; folder path copied");
            combatLogFolderFeedbackIsError = false;
            logger.Information("FFLogs upload log directory opened and its path copied to the clipboard.");
        }
        catch (Exception ex)
        {
            combatLogFolderFeedback = text.Get(
                "打开或复制失败",
                "Open or copy failed");
            combatLogFolderFeedbackIsError = true;
            logger.Error(ex, "Failed to open or copy the FFLogs upload log directory.");
        }
    }

    private bool DrawMeter()
    {
        DrawPageHeader(
            text.Get("战斗统计", "Combat Meter"),
            text.Get("仅调整内置战斗统计的显示，不影响 Cactbot 或 HTML 悬浮窗。", "These options affect only the built-in Combat Meter, not Cactbot or HTML overlays."));

        var changed = false;
        var visible = configuration.Meter.IsVisible;
        if (ImGui.Checkbox(text.Get("显示战斗统计", "Show Combat Meter"), ref visible))
        {
            setMeterVisible(visible);
        }
        ImGui.SameLine();
        if (ImGui.Button(text.Get("定位到战斗统计", "Open Combat Meter window")))
        {
            openMeter();
        }

        changed |= Checkbox(text.Get("锁定窗口", "Lock window"), configuration.Meter.IsLocked, value => configuration.Meter.IsLocked = value);
        ImGui.SameLine();
        changed |= Checkbox(text.Get("锁定时鼠标穿透", "Click-through when locked"), configuration.Meter.ClickThroughWhenLocked, value => configuration.Meter.ClickThroughWhenLocked = value);
        changed |= Checkbox(text.Get("脱战自动隐藏", "Auto hide out of combat"), configuration.Meter.AutoHideOutOfCombat, value => configuration.Meter.AutoHideOutOfCombat = value);
        changed |= SliderFloat(text.Get("背景透明度", "Background opacity"), configuration.Meter.BackgroundOpacity, 0.05f, 1, value => configuration.Meter.BackgroundOpacity = value);
        changed |= SliderFloat(text.Get("字体缩放", "Font scale"), configuration.Meter.FontScale, 0.75f, 1.8f, value => configuration.Meter.FontScale = value);

        var refreshInterval = configuration.Meter.RefreshIntervalMs;
        if (ImGui.SliderInt(text.Get("刷新间隔（毫秒）", "Refresh interval (ms)"), ref refreshInterval, 250, 2000))
        {
            configuration.Meter.RefreshIntervalMs = refreshInterval;
            changed = true;
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        if (ImGui.BeginChild("meter-display-controls", new Vector2(-1, 270), true))
        {
            ImGui.TextColored(IceBlue, text.Get("战斗统计显示", "Combat Meter display"));
            ImGui.TextDisabled(text.Get(
                "每名玩家固定一行；排序仅支持 DPS 与 HPS。",
                "Each player uses one row; sorting supports only DPS and HPS."));

            var sortMode = MeterSortModeOptions.Normalize(configuration.Meter.SortMode);
            if (ImGui.BeginCombo(
                    text.Get("排序 / 主要数据", "Sort / primary metric"),
                    sortMode == MeterSortMode.Hps ? "HPS" : "DPS"))
            {
                foreach (var mode in MeterSortModeOptions.Supported)
                {
                    if (ImGui.Selectable(
                            mode == MeterSortMode.Hps ? "HPS" : "DPS",
                            mode == sortMode))
                    {
                        configuration.Meter.SortMode = mode;
                        changed = true;
                    }
                }
                ImGui.EndCombo();
            }

            var dpsMetric = configuration.Meter.DpsMetric;
            if (ImGui.BeginCombo(text.Get("DPS 口径", "DPS metric"), DpsMetricLabel(dpsMetric)))
            {
                foreach (var metric in Enum.GetValues<DpsMetric>())
                {
                    if (ImGui.Selectable(DpsMetricLabel(metric), metric == dpsMetric))
                    {
                        configuration.Meter.DpsMetric = metric;
                        changed = true;
                    }
                }
                ImGui.EndCombo();
            }

            changed |= Checkbox(text.Get("战斗标题", "Encounter header"), configuration.Meter.ShowHeader, value => configuration.Meter.ShowHeader = value);
            ImGui.SameLine();
            changed |= Checkbox(text.Get("职业", "Job"), configuration.Meter.ShowJob, value => configuration.Meter.ShowJob = value);
            changed |= Checkbox(
                text.Get("收起（只显示自己）", "Collapsed (self only)"),
                configuration.Meter.CompactMode,
                value => configuration.Meter.CompactMode = value);
            if (configuration.Meter.ShowJob)
            {
                var jobStyle = configuration.Meter.JobDisplayStyle;
                ImGui.SetNextItemWidth(190);
                if (ImGui.BeginCombo(
                        text.Get("职业显示方式", "Job display"),
                        JobDisplayFormatter.Label(jobStyle, text)))
                {
                    foreach (var style in Enum.GetValues<JobDisplayStyle>())
                    {
                        if (ImGui.Selectable(
                                JobDisplayFormatter.Label(style, text),
                                style == jobStyle))
                        {
                            configuration.Meter.JobDisplayStyle = style;
                            changed = true;
                        }
                    }
                    ImGui.EndCombo();
                }
            }

            var localPlayerColor = configuration.Meter.LocalPlayerColor;
            if (ImGui.ColorEdit4(text.Get("自己的高亮颜色", "Your highlight color"), ref localPlayerColor))
            {
                configuration.Meter.LocalPlayerColor = localPlayerColor;
                changed = true;
            }

            ImGui.TextDisabled(text.Get(
                "每名玩家固定显示当前 DPS/HPS、占比、暴击率、直暴率和死亡数。",
                "Every player always shows current DPS/HPS, percentage, critical rate, critical-direct rate, and deaths."));

            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.38f, 0.10f, 0.12f, 1));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.58f, 0.15f, 0.17f, 1));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.68f, 0.18f, 0.20f, 1));
            if (ImGui.Button(text.Get("重置当前战斗…", "Reset current encounter…")))
            {
                resetEncounterConfirmationExpiresAt =
                    Environment.TickCount64 + ResetConfirmationMilliseconds;
                ImGui.OpenPopup(ResetEncounterPopupId);
            }
            ImGui.PopStyleColor(3);
        }
        ImGui.EndChild();
        DrawResetEncounterConfirmation();

        changed |= DrawPlayerIdentityControls();
        changed |= DrawFflogsSettings();

        return changed;
    }

    private void DrawResetEncounterConfirmation()
    {
        ImGui.SetNextWindowSize(new Vector2(430, 0), ImGuiCond.Appearing);
        if (!ImGui.BeginPopupModal(
                ResetEncounterPopupId,
                ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoResize))
        {
            return;
        }

        var now = Environment.TickCount64;
        if (IsResetConfirmationExpired(resetEncounterConfirmationExpiresAt, now))
        {
            resetEncounterConfirmationExpiresAt = 0;
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }

        var secondsRemaining = Math.Max(
            0,
            (int)Math.Ceiling((resetEncounterConfirmationExpiresAt - now) / 1000.0));
        ImGui.TextWrapped(text.Get(
            "这会清空当前战斗统计，但不会删除已经保存的历史记录。确认后才能执行。",
            "This clears the current encounter but does not delete saved history. It runs only after confirmation."));
        ImGui.TextDisabled(text.Get(
            $"确认窗口将在 {secondsRemaining} 秒后自动关闭。",
            $"This confirmation closes automatically in {secondsRemaining} seconds."));
        ImGui.Spacing();

        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.48f, 0.10f, 0.12f, 1));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.68f, 0.16f, 0.18f, 1));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.78f, 0.20f, 0.22f, 1));
        var confirmed = ImGui.Button(text.Get("确认重置", "Confirm reset"));
        ImGui.PopStyleColor(3);
        ImGui.SameLine();
        var cancelled = ImGui.Button(text.Get("取消", "Cancel"));

        if (confirmed)
        {
            resetCurrentEncounter();
            resetEncounterConfirmationExpiresAt = 0;
            ImGui.CloseCurrentPopup();
        }
        else if (cancelled)
        {
            resetEncounterConfirmationExpiresAt = 0;
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    internal static bool IsResetConfirmationExpired(long expiresAt, long now)
        => expiresAt > 0 && now >= expiresAt;

    private bool DrawOverlays()
    {
        DrawPageHeader(
            text.Get("悬浮窗", "Overlays"),
            text.Get(
                "Cactbot 使用本地页面并集中管理；其他网页悬浮窗仍可单独创建。",
                "Cactbot uses installed local pages and is managed here; other web overlays can still be created separately."));

        var changed = false;
        configuration.OverlayWindows ??= new Dictionary<string, HtmlOverlayWindowSettings>(
            StringComparer.OrdinalIgnoreCase);
        var allTemplates = getOverlayTemplates();
        var cactbotTemplates = allTemplates
            .Where(static template => template.IsCactbot)
            .OrderBy(template => GetCactbotOverlayOrder(template.Name))
            .ThenBy(static template => template.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        ImGui.TextColored(Gold, "Cactbot");
        ImGui.TextDisabled(FormatCactbotStatus());
        if (ImGui.Button(text.Get("Cactbot 设置", "Cactbot settings")))
        {
            openCactbotSettings();
        }
        ImGui.SameLine();
        if (ImGui.Button(text.Get("安装 / 更新", "Install / update")))
        {
            selectCactbotPackage();
        }
        ImGui.TextDisabled(text.Get(
            "提示中的玩家默认显示职业全称；可在 Cactbot 设置的“默认玩家代称”中修改。",
            "Player callouts default to full job names; change this under Default Player Label in Cactbot settings."));
        changed |= DrawCactbotOverlayManager(cactbotTemplates, allTemplates.Count > 0);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextColored(Gold, text.Get("HTML 悬浮窗", "HTML overlays"));
        changed |= DrawCreatedHtmlOverlays();

        ImGui.Spacing();
        changed |= DrawCustomHtmlOverlayCreator();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextColored(IceBlue, text.Get("从模板创建", "Create from template"));
        var templates = allTemplates
            .Where(static template => !template.IsCactbot)
            .ToArray();
        if (templates.Length == 0)
        {
            ImGui.TextDisabled(text.Get("启动解析器后可选择悬浮窗模板。", "Start the parser to select an overlay template."));
        }
        else
        {
            if (!templates.Any(template => string.Equals(template.Name, configuration.SelectedOverlayTemplate, StringComparison.OrdinalIgnoreCase)))
            {
                configuration.SelectedOverlayTemplate = templates[0].Name;
                changed = true;
            }

            if (ImGui.BeginCombo(text.Get("模板", "Template"), configuration.SelectedOverlayTemplate))
            {
                foreach (var template in templates)
                {
                    var selected = string.Equals(template.Name, configuration.SelectedOverlayTemplate, StringComparison.OrdinalIgnoreCase);
                    if (ImGui.Selectable(template.Name, selected))
                    {
                        configuration.SelectedOverlayTemplate = template.Name;
                        selectedCreatedOverlay = template.Name;
                        changed = true;
                    }
                }
                ImGui.EndCombo();
            }

            configuration.OverlayWindows.TryGetValue(
                configuration.SelectedOverlayTemplate,
                out var selectedSettings);
            if (ImGui.Button(selectedSettings?.IsVisible == true
                    ? text.Get("关闭所选 HTML 悬浮窗", "Close selected HTML overlay")
                    : text.Get("打开所选 HTML 悬浮窗", "Open selected HTML overlay")))
            {
                if (selectedSettings?.IsVisible == true)
                {
                    closeHtmlOverlay(configuration.SelectedOverlayTemplate);
                }
                else
                {
                    openHtmlOverlay(configuration.SelectedOverlayTemplate);
                }
            }
            if (selectedSettings is not null)
            {
                changed |= DrawOverlayWindowSettings(configuration.SelectedOverlayTemplate);
            }
        }

        return changed;
    }

    private bool DrawCactbotOverlayManager(
        IReadOnlyList<ActOverlayTemplate> templates,
        bool templateCatalogAvailable)
    {
        var changed = false;
        var templateNames = templates
            .Select(static template => template.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var usedNames = configuration.OverlayWindows
            .Where(pair =>
                SelfHostedActRuntime.IsCactbotOverlayName(pair.Key) &&
                pair.Value.HasBeenOpened)
            .Select(static pair => pair.Key)
            .OrderBy(GetCactbotOverlayOrder)
            .ThenBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        ImGui.Spacing();
        ImGui.TextColored(IceBlue, text.Get(
            "打开过的 Cactbot 悬浮窗",
            "Previously opened Cactbot overlays"));
        if (usedNames.Length == 0)
        {
            ImGui.TextDisabled(text.Get(
                "还没有打开过 Cactbot 悬浮窗。",
                "No Cactbot overlays have been opened yet."));
            selectedUsedCactbotOverlay = null;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(selectedUsedCactbotOverlay) ||
                !usedNames.Contains(selectedUsedCactbotOverlay, StringComparer.OrdinalIgnoreCase))
            {
                selectedUsedCactbotOverlay = usedNames[0];
            }

            foreach (var name in usedNames)
            {
                var settings = configuration.OverlayWindows[name];
                var status = settings.IsVisible
                    ? text.Get("已打开", "Open")
                    : !templateCatalogAvailable
                        ? text.Get("解析器未运行", "Parser stopped")
                        : templateNames.Contains(name)
                            ? text.Get("已关闭", "Closed")
                            : text.Get("本地资源不可用", "Local asset unavailable");
                var selected = string.Equals(
                    name,
                    selectedUsedCactbotOverlay,
                    StringComparison.OrdinalIgnoreCase);
                if (ImGui.Selectable(
                        $"{FormatCactbotOverlayName(name)}  [{status}]##used-cactbot-{name}",
                        selected))
                {
                    selectedUsedCactbotOverlay = name;
                }
            }

            if (!string.IsNullOrWhiteSpace(selectedUsedCactbotOverlay) &&
                configuration.OverlayWindows.TryGetValue(
                    selectedUsedCactbotOverlay,
                    out var selectedSettings))
            {
                var selectedName = selectedUsedCactbotOverlay;
                var localTemplateAvailable = templateNames.Contains(selectedName);
                ImGui.Spacing();
                ImGui.PushID($"used-cactbot-actions-{selectedName}");
                if (localTemplateAvailable)
                {
                    if (ImGui.Button(selectedSettings.IsVisible
                            ? text.Get("关闭", "Close")
                            : text.Get("打开", "Open")))
                    {
                        if (selectedSettings.IsVisible)
                        {
                            closeHtmlOverlay(selectedName);
                        }
                        else
                        {
                            openHtmlOverlay(selectedName);
                        }
                    }
                }
                else
                {
                    ImGui.TextColored(
                        new Vector4(0.95f, 0.55f, 0.35f, 1),
                        templateCatalogAvailable
                            ? text.Get(
                                "当前 Cactbot 包缺少该页面，不会回退到远程地址。",
                                "The current Cactbot package does not contain this page; no online fallback will be used.")
                            : text.Get(
                                "启动解析器后才能打开该悬浮窗。",
                                "Start the parser before opening this overlay."));
                    if (selectedSettings.OpenOnStartup &&
                        ImGui.Button(text.Get("停止自动打开", "Disable startup")))
                    {
                        selectedSettings.OpenOnStartup = false;
                        changed = true;
                    }
                }

                if (localTemplateAvailable)
                {
                    ImGui.SameLine();
                }
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.48f, 0.10f, 0.12f, 1));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.68f, 0.16f, 0.18f, 1));
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.78f, 0.20f, 0.22f, 1));
                var removeSelected = ImGui.Button(text.Get(
                    "移除并重置",
                    "Remove and reset"));
                ImGui.PopStyleColor(3);
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(text.Get(
                        "关闭窗口并从此列表移除，清除保存的位置、大小与显示设置；本地模板不会被删除。",
                        "Close and remove this entry, clearing its saved position, size, and display settings. The local template is not deleted."));
                }

                if (removeSelected)
                {
                    deleteHtmlOverlay(selectedName);
                    selectedUsedCactbotOverlay = null;
                }
                else if (localTemplateAvailable)
                {
                    changed |= DrawOverlayWindowSettings(selectedName);
                }
                ImGui.PopID();
            }
        }

        ImGui.Spacing();
        ImGui.TextColored(IceBlue, text.Get(
            "从本地模板添加",
            "Add from local template"));
        if (!templateCatalogAvailable)
        {
            ImGui.TextDisabled(text.Get(
                "启动解析器后会列出本地 Cactbot 悬浮窗。",
                "Start the parser to list installed local Cactbot overlays."));
        }
        else
        {
            var availableTemplates = templates
                .Where(template =>
                    !configuration.OverlayWindows.TryGetValue(template.Name, out var settings) ||
                    !settings.HasBeenOpened)
                .ToArray();
            if (availableTemplates.Length == 0)
            {
                ImGui.TextDisabled(text.Get(
                    "所有可用模板都已加入上方列表。",
                    "All available templates are already listed above."));
                selectedAvailableCactbotOverlay = null;
            }
            else
            {
                if (string.IsNullOrWhiteSpace(selectedAvailableCactbotOverlay) ||
                    !availableTemplates.Any(template => string.Equals(
                        template.Name,
                        selectedAvailableCactbotOverlay,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    selectedAvailableCactbotOverlay = availableTemplates[0].Name;
                }

                var selectedAvailableName = selectedAvailableCactbotOverlay!;
                if (ImGui.BeginCombo(
                        text.Get("本地模板", "Local template"),
                        FormatCactbotOverlayName(selectedAvailableName)))
                {
                    foreach (var template in availableTemplates)
                    {
                        var selected = string.Equals(
                            template.Name,
                            selectedAvailableCactbotOverlay,
                            StringComparison.OrdinalIgnoreCase);
                        if (ImGui.Selectable(FormatCactbotOverlayName(template.Name), selected))
                        {
                            selectedAvailableCactbotOverlay = template.Name;
                        }
                    }
                    ImGui.EndCombo();
                }

                if (ImGui.Button(text.Get("添加并打开", "Add and open")))
                {
                    configuration.SelectedCactbotOverlay = selectedAvailableName;
                    openCactbotOverlay();
                    selectedUsedCactbotOverlay = selectedAvailableName;
                }
            }
        }

        ImGui.TextDisabled(text.Get(
            "文字提醒和时间轴可以同时打开；它们与旧版组合窗口互斥。其他 Cactbot 窗口可自由多开。",
            "Alerts and timeline can be open together; both conflict with the legacy combined window. Other Cactbot overlays can be opened together freely."));
        return changed;
    }

    private bool DrawCreatedHtmlOverlays()
    {
        var changed = false;
        ImGui.TextColored(IceBlue, text.Get("已创建的悬浮窗", "Created overlays"));
        var createdNames = configuration.OverlayWindows.Keys
            .Where(name => !SelfHostedActRuntime.IsCactbotOverlayName(name))
            .OrderBy(name => name)
            .ToArray();
        if (createdNames.Length == 0)
        {
            ImGui.TextDisabled(text.Get("还没有创建 HTML 悬浮窗。", "No HTML overlays have been created yet."));
        }
        else
        {
            selectedCreatedOverlay ??= createdNames[0];
            foreach (var name in createdNames)
            {
                if (ImGui.Selectable($"{name}##created-overlay-{name}", string.Equals(name, selectedCreatedOverlay, StringComparison.OrdinalIgnoreCase)))
                {
                    selectedCreatedOverlay = name;
                }
            }

            if (!string.IsNullOrWhiteSpace(selectedCreatedOverlay) && configuration.OverlayWindows.ContainsKey(selectedCreatedOverlay))
            {
                ImGui.Spacing();
                var createdSettings = configuration.GetOverlayWindowSettings(
                    selectedCreatedOverlay);
                if (!string.IsNullOrWhiteSpace(createdSettings.SourceUrl))
                {
                    ImGui.TextWrapped(createdSettings.SourceUrl);
                }
                if (ImGui.Button(createdSettings.IsVisible
                        ? text.Get("关闭", "Close")
                        : text.Get("打开", "Open")))
                {
                    if (createdSettings.IsVisible)
                    {
                        closeHtmlOverlay(selectedCreatedOverlay);
                    }
                    else
                    {
                        openHtmlOverlay(selectedCreatedOverlay);
                    }
                }
                ImGui.SameLine();
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.48f, 0.10f, 0.12f, 1));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.68f, 0.16f, 0.18f, 1));
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.78f, 0.20f, 0.22f, 1));
                var deleteSelected = ImGui.Button(text.Get("删除悬浮窗", "Delete overlay"));
                ImGui.PopStyleColor(3);
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(text.Get(
                        "关闭悬浮窗并删除它保存的网址、位置、大小与显示设置。",
                        "Close the overlay and delete its saved URL, position, size, and display settings."));
                }

                if (deleteSelected)
                {
                    var deletedName = selectedCreatedOverlay;
                    deleteHtmlOverlay(deletedName);
                    selectedCreatedOverlay = null;
                }
                else
                {
                    changed |= DrawOverlayWindowSettings(selectedCreatedOverlay);
                }
            }
        }

        return changed;
    }

    private bool DrawCustomHtmlOverlayCreator()
    {
        var changed = false;
        ImGui.TextColored(IceBlue, text.Get("从网址创建", "Create from URL"));
        ImGui.SetNextItemWidth(280);
        ImGui.InputText(
            text.Get("名称###custom-overlay-name", "Name###custom-overlay-name"),
            ref customOverlayName,
            80);
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText(
            text.Get("网址###custom-overlay-url", "URL###custom-overlay-url"),
            ref customOverlayUrl,
            2048);
        ImGui.TextDisabled(text.Get(
            "支持 http、https 与 file 地址；只添加你信任的悬浮窗页面。",
            "Supports http, https, and file URLs. Only add overlay pages you trust."));

        if (ImGui.Button(text.Get("创建并打开", "Create and open")))
        {
            var name = customOverlayName.Trim();
            var templates = getOverlayTemplates();
            if (string.IsNullOrWhiteSpace(name))
            {
                SetCustomOverlayFeedback(text.Get("请输入悬浮窗名称。", "Enter an overlay name."), true);
            }
            else if (SelfHostedActRuntime.IsCactbotOverlayName(name) ||
                     templates.Any(template => string.Equals(
                         template.Name,
                         name,
                         StringComparison.OrdinalIgnoreCase)) ||
                     configuration.OverlayWindows.ContainsKey(name))
            {
                SetCustomOverlayFeedback(text.Get("该名称已被使用。", "That name is already in use."), true);
            }
            else if (!SelfHostedActRuntime.TryNormalizeCustomOverlayUri(
                         customOverlayUrl,
                         out var sourceUri))
            {
                SetCustomOverlayFeedback(
                    text.Get("网址无效；请使用完整的 http、https 或 file 地址。", "Invalid URL. Use a complete http, https, or file URL."),
                    true);
            }
            else
            {
                var settings = configuration.GetOverlayWindowSettings(name);
                settings.SourceUrl = sourceUri.AbsoluteUri;
                settings.SetEditing(true);
                selectedCreatedOverlay = name;
                openHtmlOverlay(name);
                customOverlayName = string.Empty;
                customOverlayUrl = string.Empty;
                SetCustomOverlayFeedback(text.Get("悬浮窗已创建并进入编辑模式。", "Overlay created in edit mode."), false);
                changed = true;
            }
        }

        if (!string.IsNullOrWhiteSpace(customOverlayFeedback))
        {
            ImGui.TextColored(
                customOverlayFeedbackIsError
                    ? new Vector4(0.96f, 0.42f, 0.38f, 1)
                    : new Vector4(0.45f, 0.88f, 0.62f, 1),
                customOverlayFeedback);
        }

        return changed;
    }

    private void SetCustomOverlayFeedback(string message, bool isError)
    {
        customOverlayFeedback = message;
        customOverlayFeedbackIsError = isError;
    }

    private bool DrawExtensions(out bool permissionsChanged)
    {
        DrawPageHeader(
            text.Get("扩展", "Extensions"),
            text.Get("系统插件与第三方 ACT 扩展保持原有运行方式。", "System plugins and third-party ACT extensions keep their existing runtime behavior."));

        var changed = false;
        changed |= Checkbox(
            "FFXIV_ACT_Plugin",
            configuration.EmbeddedPlugins.FfxivActPluginEnabled,
            value => configuration.EmbeddedPlugins.FfxivActPluginEnabled = value);
        changed |= Checkbox(
            "OverlayPlugin",
            configuration.EmbeddedPlugins.OverlayPluginEnabled,
            value => configuration.EmbeddedPlugins.OverlayPluginEnabled = value);
        ImGui.TextDisabled(text.Get("改变插件启用状态后需要重启解析器。", "Restart the parser after changing plugin state."));

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextColored(Gold, text.Get("兼容扩展", "Compatibility extensions"));
        var installedPlugins = discoverPlugins();
        changed |= DrawExtensionEntry(
            installedPlugins,
            "act.foxtts",
            "ACT.FoxTTS",
            text.Get("TTS 语音合成与播报", "TTS speech synthesis and announcements"));
        changed |= DrawExtensionEntry(
            installedPlugins,
            "postnamazu",
            text.Get("鲶鱼精邮差 / PostNamazu", "PostNamazu"),
            text.Get("游戏命令、标点与本地桥接", "Game commands, markers, and local bridge"));
        changed |= DrawExtensionEntry(
            installedPlugins,
            "triggernometry",
            "Triggernometry",
            text.Get("触发器、时间轴、TTS 与绘图", "Triggers, timelines, TTS, and drawing"));
        changed |= DrawExtensionEntry(
            installedPlugins,
            "silverdasher",
            text.Get("银山雀儿 / SilverDasher", "SilverDasher"),
            text.Get("狩猎、临危受命与跨区状态提醒", "Hunts, FATEs, and cross-world status alerts"));

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextColored(Gold, text.Get("扩展管理", "Extension management"));
        if (ImGui.Button(text.Get("安装 DLL / ZIP", "Install DLL / ZIP")))
        {
            selectPluginPackage();
        }
        ImGui.SameLine();
        if (ImGui.Button(text.Get("打开扩展文件夹", "Open extension folder")))
        {
            openPluginDirectory();
        }
        ImGui.SameLine();
        if (ImGui.Button(text.Get("检查更新与来源", "Check updates and sources")))
        {
            checkBundledPluginUpdates();
        }
        var autoCheckUpdates = configuration.AutoCheckBundledPluginUpdates;
        if (ImGui.Checkbox(
                text.Get(
                    "启动时自动检查第三方扩展更新",
                    "Automatically check third-party extension updates on startup"),
                ref autoCheckUpdates))
        {
            configuration.AutoCheckBundledPluginUpdates = autoCheckUpdates;
            changed = true;
        }
        ImGui.TextDisabled(text.Get(
            "关闭后仍可使用上方按钮手动检查；不会影响 Dalamud 自身的插件更新。",
            "When disabled, the button above still checks manually; Dalamud's own plugin updates are unaffected."));

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextColored(Gold, text.Get("ACT 插件权限边界", "ACT plugin permission boundary"));
        ImGui.TextWrapped(text.Get(
            "高风险能力默认关闭，授权按扩展和能力分别保存；权限组保存后会自动重启 Host 一次，使完整功能立即生效。第三方 DLL 的直接系统调用仍由独立 Host 的进程边界承担。",
            "High-risk capabilities are denied by default. Grants are stored per extension and capability; after a permission group is saved, the Host restarts once so the complete feature set takes effect immediately. Direct system calls from third-party DLLs remain behind the independent Host process boundary."));
        permissionsChanged = DrawPluginPermissions(
            "postnamazu",
            text.Get("鲶鱼精邮差 / PostNamazu", "PostNamazu"),
            BundledActPluginCapabilities.PostNamazu);
        permissionsChanged |= DrawPluginPermissions(
            "triggernometry",
            "Triggernometry",
            BundledActPluginCapabilities.Triggernometry);
        permissionsChanged |= DrawPluginPermissions(
            "silverdasher",
            text.Get("银山雀儿 / SilverDasher", "SilverDasher"),
            BundledActPluginCapabilities.SilverDasher);
        changed |= permissionsChanged;
        return changed;
    }

    private bool DrawPluginPermissions(
        string pluginId,
        string displayName,
        IReadOnlyList<ActCapability> capabilities)
    {
        var changed = false;
        if (!ImGui.TreeNode($"{displayName}##control-center-permissions-{pluginId}"))
        {
            return false;
        }

        foreach (var capability in capabilities)
        {
            var allowed = configuration.IsActCapabilityAllowed(pluginId, capability);
            if (!ImGui.Checkbox(
                    $"{ActCapabilityDisplay.Label(capability, text)}##control-center-{pluginId}-{capability}",
                    ref allowed))
            {
                continue;
            }

            configuration.SetActCapability(pluginId, capability, allowed);
            logger.Information(
                $"ACT permission changed: plugin={pluginId}, capability={capability}, allowed={allowed}.");
            changed = true;
        }

        ImGui.TreePop();
        return changed;
    }

    private bool DrawExtensionEntry(
        IReadOnlyList<InstalledActPlugin> installedPlugins,
        string pluginId,
        string displayName,
        string description)
    {
        var installed = installedPlugins.FirstOrDefault(plugin =>
            string.Equals(plugin.Manifest.Id, pluginId, StringComparison.OrdinalIgnoreCase));
        var changed = false;
        ImGui.PushID($"extension-{pluginId}");
        if (installed is null)
        {
            ImGui.TextUnformatted(displayName);
            ImGui.SameLine();
            ImGui.TextDisabled(text.Get("未安装", "Not installed"));
        }
        else
        {
            var enabled = installed.Enabled;
            if (ImGui.Checkbox(displayName, ref enabled))
            {
                if (enabled)
                {
                    configuration.DisabledActPluginIds.Remove(pluginId);
                }
                else
                {
                    configuration.DisabledActPluginIds.Add(pluginId);
                }
                changed = true;
            }

            ImGui.SameLine();
            ImGui.TextColored(
                enabled ? IceBlue : new Vector4(0.66f, 0.69f, 0.74f, 1),
                enabled ? text.Get("已启用", "Enabled") : text.Get("已禁用", "Disabled"));
            ImGui.SameLine();
            if (enabled && ImGui.SmallButton(text.Get("打开配置", "Open configuration")))
            {
                openPluginConfiguration(pluginId);
            }
            ImGui.SameLine();
            ImGui.TextDisabled($"v{installed.Manifest.Version}");
        }

        ImGui.TextDisabled(description);
        ImGui.PopID();
        return changed;
    }

    private bool DrawDiagnostics()
    {
        DrawPageHeader(
            text.Get("设置", "Settings"),
            text.Get(
                "运行状态、语言、快捷按钮与恢复选项。",
                "Runtime status, language, quick-button, and recovery options."));

        ImGui.TextColored(Gold, $"{text.Get("解析器", "Parser")}: {LocalizeState(parserStatus.State)}");
        ImGui.TextWrapped(parserStatus.Message);
        if (!string.IsNullOrWhiteSpace(parserStatus.Detail))
        {
            ImGui.TextWrapped(parserStatus.Detail);
        }
        if (ImGui.Button(text.Get("重启解析器", "Restart parser")))
        {
            RestartParser();
        }
        ImGui.SameLine();
        DrawStatusWindowToggleButton(Vector2.Zero, detailedLabel: true);
        ImGui.SameLine();
        if (ImGui.Button(text.Get("三方扩展声明", "Third-party extension notice")))
        {
            openBundledPluginNotice();
        }

        if (ImGui.Button(text.Get("复制诊断日志", "Copy diagnostic log")))
        {
            CopyDiagnosticReport();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(text.Get(
                "复制本插件相关的运行状态与近期日志；不复制战斗日志和配置文件，并会遮盖用户路径与常见凭据。原始战斗数据问题可使用右侧按钮打开日志文件夹。",
                "Copies recent plugin diagnostics and runtime state. Combat logs and configuration files are excluded, and user paths and common credentials are redacted. For raw combat-data issues, use the button on the right to open the log folder."));
        }
        ImGui.SameLine();
        if (ImGui.Button(text.Get("打开日志文件夹", "Open log folder")))
        {
            openLogDirectory();
        }

        if (!string.IsNullOrWhiteSpace(diagnosticCopyFeedback))
        {
            ImGui.TextColored(
                diagnosticCopyFeedbackIsError
                    ? new Vector4(0.95f, 0.45f, 0.40f, 1)
                    : IceBlue,
                diagnosticCopyFeedback);
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        var changed = false;
        if (ImGui.BeginCombo(text.Get("界面语言", "UI language"), text.IsChinese ? "简体中文" : "English"))
        {
            if (ImGui.Selectable("简体中文", text.IsChinese))
            {
                configuration.UiLanguage = "zh-CN";
                changed = true;
            }
            if (ImGui.Selectable("English", !text.IsChinese))
            {
                configuration.UiLanguage = "en";
                changed = true;
            }
            ImGui.EndCombo();
        }
        changed |= Checkbox(text.Get("调试模式", "Debug mode"), configuration.DebugMode, value => configuration.DebugMode = value);
        changed |= Checkbox(text.Get("显示 ACT 快捷按钮", "Show ACT quick button"), configuration.ShowLauncherButton, value => configuration.ShowLauncherButton = value);
        var launcherButtonSize = configuration.LauncherButtonSize;
        if (ImGui.SliderInt(text.Get("快捷按钮尺寸（像素）", "Quick-button size (pixels)"), ref launcherButtonSize, 56, 128))
        {
            configuration.LauncherButtonSize = launcherButtonSize;
            changed = true;
        }
        if (ImGui.Button(text.Get("重置快捷按钮大小与位置", "Reset quick-button size and position")))
        {
            configuration.LauncherPositionX = 80;
            configuration.LauncherPositionY = 160;
            configuration.LauncherButtonSize = 80;
            configuration.ShowLauncherButton = true;
            changed = true;
        }

        var historyLimit = configuration.HistoryLimit;
        if (ImGui.SliderInt(text.Get("历史记录上限", "History limit"), ref historyLimit, 1, 200))
        {
            configuration.HistoryLimit = historyLimit;
            changed = true;
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextColored(Gold, text.Get("恢复", "Recovery"));
        ImGui.TextWrapped(text.Get(
            "恢复出厂设置会停止 ACT 宿主、备份所有可变数据，并恢复两个系统插件和默认设置。",
            "Factory reset stops the ACT host, backs up all mutable data, and restores the two system plugins and default settings."));
        if (!confirmFactoryReset)
        {
            if (ImGui.Button(text.Get("恢复出厂设置...", "Restore factory settings...")))
            {
                confirmFactoryReset = true;
            }
        }
        else
        {
            ImGui.TextWrapped(text.Get(
                "按确认继续。此前状态仍可从备份目录恢复。",
                "Press confirm to continue. The previous state remains recoverable from the backup directory."));
            if (ImGui.Button(text.Get("确认恢复", "Confirm factory reset")))
            {
                confirmFactoryReset = false;
                _ = RunFactoryResetAsync();
            }

            ImGui.SameLine();
            if (ImGui.Button(text.Get("取消", "Cancel")))
            {
                confirmFactoryReset = false;
            }
        }

        if (!string.IsNullOrWhiteSpace(factoryResetResult))
        {
            ImGui.TextWrapped(
                $"{text.Get("最近一次出厂备份", "Last factory reset backup")}: {factoryResetResult}");
        }
        return changed;
    }

    private void CopyDiagnosticReport()
    {
        try
        {
            var report = buildDiagnosticReport();
            ImGui.SetClipboardText(report);
            diagnosticCopyFeedback = text.Get(
                $"已复制诊断日志（{report.Length:N0} 字符），可直接粘贴到问题反馈。",
                $"Diagnostic log copied ({report.Length:N0} characters); paste it into the issue report.");
            diagnosticCopyFeedbackIsError = false;
            logger.Information("Bounded diagnostic report copied to the clipboard.");
        }
        catch (Exception ex)
        {
            diagnosticCopyFeedback = text.Get(
                "复制失败，请改用“打开日志文件夹”。",
                "Copy failed; use Open log folder instead.");
            diagnosticCopyFeedbackIsError = true;
            logger.Error(ex, "Failed to copy the diagnostic report.");
        }
    }

    private string FormatCactbotOverlayName(string name)
        => name switch
        {
            SelfHostedActRuntime.CactbotAlertsOverlayName =>
                text.Get("文字提醒", "Raidboss alerts"),
            SelfHostedActRuntime.CactbotTimelineOverlayName =>
                text.Get("时间轴", "Raidboss timeline"),
            SelfHostedActRuntime.CactbotOverlayName =>
                text.Get("文字提醒 + 时间轴（旧版组合）", "Alerts + timeline (legacy combined)"),
            _ => name.StartsWith("Cactbot ", StringComparison.OrdinalIgnoreCase)
                ? name["Cactbot ".Length..]
                : name,
        };

    private static int GetCactbotOverlayOrder(string name)
        => name switch
        {
            SelfHostedActRuntime.CactbotAlertsOverlayName => 0,
            SelfHostedActRuntime.CactbotTimelineOverlayName => 1,
            SelfHostedActRuntime.CactbotOverlayName => 2,
            _ => 100,
        };

    private bool DrawOverlayWindowSettings(string name)
    {
        var changed = false;
        var settings = configuration.GetOverlayWindowSettings(name);
        ImGui.PushID(name);
        ImGui.TextDisabled(text.Get("位置、缩放、穿透与锁定", "Position, scale, click-through, and lock"));
        if (ImGui.Button(settings.IsEditing
                ? text.Get("完成并操作网页", "Finish and interact with page")
                : text.Get("编辑位置和大小", "Edit position and size")))
        {
            var beginEditing = !settings.IsEditing;
            if (!beginEditing || settings.IsVisible || openHtmlOverlay(name))
            {
                settings.SetEditing(beginEditing);
                changed = true;
            }
        }
        ImGui.SameLine();
        changed |= Checkbox(text.Get("鼠标穿透", "Click-through"), settings.IsClickThrough, value => settings.IsClickThrough = value);
        ImGui.SameLine();
        changed |= Checkbox(text.Get("锁定", "Locked"), settings.IsLocked, value => settings.IsLocked = value);
        changed |= SliderFloat(text.Get("页面缩放", "Page zoom"), settings.ZoomFactor, 0.5f, 2, value => settings.ZoomFactor = value);
        ImGui.TextDisabled(settings.IsEditing
            ? text.Get(
                "单击可操作网页；按住并拖动可移动，拖动右下角斜纹可缩放。",
                "Click to use the page; hold and drag to move, or drag the striped bottom-right grip to resize.")
            : text.Get(
                "关闭穿透时可操作网页；打开穿透后，鼠标点击会传给游戏。",
                "With click-through off, the page is interactive; when enabled, mouse clicks pass to the game."));
        ImGui.PopID();

        if (changed)
        {
            applyOverlayWindowSettings(name);
        }
        return changed;
    }

    private void RestartParser()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                await parserEngine.RestartAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Parser restart failed.");
            }
        });
    }

    private void DrawPageHeader(string title, string description, bool showDivider = true)
    {
        ImGui.TextColored(Gold, title);
        ImGui.TextDisabled(description);
        ImGui.Spacing();
        if (showDivider)
        {
            ImGui.Separator();
            ImGui.Spacing();
        }
    }

    private void DrawStatusWindowToggleButton(Vector2 size, bool detailedLabel = false)
    {
        var visible = isStatusVisible();
        if (visible)
        {
            PushOpenWindowButtonStyle();
        }

        var label = detailedLabel
            ? visible
                ? text.Get("关闭详细运行状态", "Close detailed runtime status")
                : text.Get("详细运行状态", "Detailed runtime status")
            : visible
                ? text.Get("关闭运行状态", "Close Runtime status")
                : text.Get("运行状态", "Runtime status");
        if (ImGui.Button(label, size))
        {
            setStatusVisible(!visible);
        }

        if (visible)
        {
            ImGui.PopStyleColor(4);
        }
    }

    private static void PushOpenWindowButtonStyle()
    {
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.11f, 0.29f, 0.38f, 1));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.56f, 0.16f, 0.18f, 1));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.68f, 0.18f, 0.20f, 1));
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.94f, 0.98f, 1, 1));
    }

    private static string BuildVersionLabel()
    {
        var version = typeof(ControlCenterWindow).Assembly.GetName().Version;
        return FormatVersionLabel(version);
    }

    internal static string FormatVersionLabel(Version? version)
        => version is null
            ? "v?"
            : $"v{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}.{Math.Max(0, version.Revision)}";

    private static bool Checkbox(string label, bool current, Action<bool> set)
    {
        var value = current;
        if (!ImGui.Checkbox(label, ref value))
        {
            return false;
        }
        set(value);
        return true;
    }

    private static bool SliderFloat(string label, float current, float min, float max, Action<float> set)
    {
        var value = current;
        if (!ImGui.SliderFloat(label, ref value, min, max))
        {
            return false;
        }
        set(value);
        return true;
    }

    private void OnParserStatusChanged(object? sender, ParserStatus status) => parserStatus = status;

    private string LocalizeState(ParserState state) => state switch
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

    private string DpsMetricLabel(DpsMetric metric) => metric switch
    {
        DpsMetric.Rdps => text.Get("rDPS（团队贡献估算）", "rDPS (estimated raid contribution)"),
        DpsMetric.Dps => text.Get("DPS（个人有效动作时长）", "DPS (personal active duration)"),
        DpsMetric.EncDps => text.Get("EncDPS（整场战斗时长）", "EncDPS (encounter duration)"),
        DpsMetric.ExtDps => text.Get("ExtDPS（ACT 兼容字段）", "ExtDPS (ACT compatibility field)"),
        _ => metric.ToString(),
    };

    private bool DrawPlayerIdentityControls()
    {
        var changed = false;
        var identityMode = configuration.Meter.PlayerIdentityMode;
        if (ImGui.BeginCombo(text.Get("玩家 ID 显示", "Player identity"), PlayerIdentityModeLabel(identityMode)))
        {
            foreach (var mode in Enum.GetValues<PlayerIdentityMode>())
            {
                if (ImGui.Selectable(PlayerIdentityModeLabel(mode), mode == identityMode))
                {
                    configuration.Meter.PlayerIdentityMode = mode;
                    changed = true;
                }
            }
            ImGui.EndCombo();
        }

        ImGui.TextDisabled(text.Get(
            "遮盖只影响界面显示，不修改解析数据和保存的战斗日志。",
            "Masking affects display only; parsed data and saved encounter logs are unchanged."));
        if (configuration.Meter.PlayerIdentityMode == PlayerIdentityMode.Anonymous)
        {
            var alias = configuration.Meter.LocalPlayerAlias;
            if (ImGui.InputText(text.Get("自己的代称", "Your alias"), ref alias, 32))
            {
                configuration.Meter.LocalPlayerAlias = alias;
                changed = true;
            }
        }

        return changed;
    }

    private bool DrawFflogsSettings()
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextColored(Gold, text.Get("FFLogs 实时估算", "FFLogs live estimate"));
        ImGui.TextWrapped(text.Get(
            "后台按团队增益归因计算 rDPS，再用 FFLogs 公共排名样本估算百分位；显示为带“~”的颜色与数字，不是官方实时日志分数。",
            "Calculate estimated rDPS from raid-buff attribution in the background, then estimate its percentile from public FFLogs ranking samples. The colored number is prefixed with '~' and is not an official live-log score."));

        var changed = false;
        if (ImGui.BeginTable(
                "fflogs-api-refresh-card",
                1,
                ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableNextColumn();
            ImGui.TextColored(IceBlue, text.Get(
                "API 凭据与数据刷新",
                "API credentials and data refresh"));
            ImGui.TextDisabled(text.Get(
                "负责验证 FFLogs API 凭据并刷新估算数据。",
                "Validates the FFLogs API credentials and refreshes estimate data."));

            var enabled = configuration.Fflogs.Enabled;
            if (ImGui.Checkbox(text.Get("启用 FFLogs 在线估算", "Enable FFLogs online estimate"), ref enabled))
            {
                UpdateFflogsSettings(settings => settings.Enabled = enabled);
                fflogsEstimateService.NotifyCredentialsChanged();
                changed = true;
                if (enabled)
                {
                    fflogsEstimateService.RequestRefresh(getCurrentEncounter());
                }
            }

            if (ImGui.CollapsingHeader(text.Get(
                    "如何创建 FFLogs API Client",
                    "How to create an FFLogs API client")))
            {
                ImGui.PushStyleColor(ImGuiCol.ChildBg, NavyRaised);
                if (ImGui.BeginChild("fflogs-client-guide", new Vector2(-1, 150), true))
                {
                    ImGui.TextWrapped(text.Get(
                        "1. 登录 FFLogs 网站。\n" +
                        "2. 点击 Create Client。\n" +
                        "3. 输入一个名称。\n" +
                        "4. 输入一个网址；如果不知道填什么，请填写 https://example.com。\n" +
                        "5. 点击创建。\n" +
                        "6. 在下方找到 Client ID 和 Client Secret，并分别填入插件对应位置。",
                        "1. Sign in to FFLogs.\n" +
                        "2. Click Create Client.\n" +
                        "3. Enter a name.\n" +
                        "4. Enter a URL; use https://example.com if you do not have one.\n" +
                        "5. Create the client.\n" +
                        "6. Copy the Client ID and Client Secret shown below into the matching plugin fields."));
                }
                ImGui.EndChild();
                ImGui.PopStyleColor();
            }

            var clientId = configuration.Fflogs.ClientId;
            if (ImGui.InputText("Client ID", ref clientId, 128))
            {
                UpdateFflogsSettings(settings => settings.ClientId = clientId.Trim());
                fflogsEstimateService.NotifyCredentialsChanged();
                changed = true;
            }

            var clientSecret = configuration.Fflogs.ClientSecret;
            if (ImGui.InputText("Client Secret", ref clientSecret, 256, ImGuiInputTextFlags.Password))
            {
                UpdateFflogsSettings(settings => settings.ClientSecret = clientSecret.Trim());
                fflogsEstimateService.NotifyCredentialsChanged();
                changed = true;
            }

            ImGui.TextDisabled(text.Get(
                "使用免费的 FFLogs API Client；密钥保存在本机插件配置中，可随时撤销；不会上传玩家 ID。",
                "Uses a free FFLogs API client. The secret is stored locally and can be revoked; player IDs are never uploaded."));
            if (ImGui.Button(text.Get("创建 / 管理 API Client", "Create / manage API client")))
            {
                OpenUrl("https://www.fflogs.com/api/clients/");
            }
            ImGui.SameLine();
            if (ImGui.Button(text.Get("测试并刷新", "Test and refresh")))
            {
                fflogsEstimateService.RequestRefresh(getCurrentEncounter());
            }

            var status = fflogsEstimateService.Status;
            ImGui.TextColored(FflogsStatusColor(status.State), FflogsStatusLabel(status.State));
            var statusDetail = status.State is
                FflogsEstimateState.Error or
                FflogsEstimateState.InactiveContent
                ? status.Message
                : string.Empty;
            var statusDetailHeight = ImGui.GetTextLineHeightWithSpacing() * 2.4f;
            if (ImGui.BeginChild(
                    "fflogs-status-detail",
                    new Vector2(-1, statusDetailHeight),
                    false,
                    ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
            {
                if (!string.IsNullOrWhiteSpace(statusDetail))
                {
                    ImGui.TextWrapped(statusDetail);
                }
            }
            ImGui.EndChild();
            ImGui.EndTable();
        }

        ImGui.Spacing();
        if (ImGui.BeginTable(
                "fflogs-automatic-encounter-card",
                1,
                ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableNextColumn();
            ImGui.TextColored(IceBlue, text.Get("当前副本自动识别", "Automatic duty matching"));
            ImGui.TextDisabled(text.Get(
                "进入副本后按游戏 Territory ID 自动匹配；只会访问当前开放的 FFLogs 榜单。",
                "Matches by the game's Territory ID on duty entry and only accesses the current FFLogs ranking tier."));

            var activeEncounter = fflogsEstimateService.ActiveEncounter;
            if (activeEncounter is not null)
            {
                ImGui.TextDisabled(
                    $"Territory {activeEncounter.TerritoryId}  ·  " +
                    $"{activeEncounter.EncounterName}  ·  " +
                    $"FFLogs #{activeEncounter.EncounterId}  ·  " +
                    $"{(activeEncounter.Difficulty == 101 ? "Savage" : "Normal")}");
                if (activeEncounter.Phase > 1)
                {
                    ImGui.TextColored(
                        IceBlue,
                        text.Get("已自动切换至第二阶段榜单", "Automatically switched to the phase-two ranking"));
                }
            }
            else
            {
                ImGui.TextDisabled(text.Get(
                    "当前不在最新团队副本榜单中，不会加载历史榜单。",
                    "The current territory is outside the latest raid tier; historical rankings will not be loaded."));
            }
            ImGui.TextDisabled(text.Get(
                "榜单换季时只需更新内置副本表，不依赖国服客户端返回英文 Boss 名。",
                "Tier rollovers only require updating the built-in duty table; English boss names are not required from the CN client."));
            ImGui.EndTable();
        }

        return changed;
    }

    private async Task RunFactoryResetAsync()
    {
        try
        {
            factoryResetResult = await factoryReset().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Plugin shutdown owns cancellation and observes the reset task.
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Factory reset failed.");
            factoryResetResult = $"{text.Get("恢复出厂设置失败", "Factory reset failed")}: {ex.Message}";
        }
    }

    private void UpdateFflogsSettings(Action<FflogsSettings> update)
    {
        var next = configuration.Fflogs.Snapshot();
        update(next);
        configuration.Fflogs = next;
    }

    private string FflogsStatusLabel(FflogsEstimateState state) => state switch
    {
        FflogsEstimateState.Disabled => text.Get("状态：未启用", "Status: disabled"),
        FflogsEstimateState.NeedsCredentials => text.Get("状态：需要 Client ID 与 Secret", "Status: client ID and secret required"),
        FflogsEstimateState.Idle => text.Get("状态：等待测试或战斗数据", "Status: waiting for a test or encounter data"),
        FflogsEstimateState.Loading => text.Get("状态：正在连接 FFLogs…", "Status: connecting to FFLogs…"),
        FflogsEstimateState.Ready => text.Get("状态：估算数据已就绪", "Status: estimate data ready"),
        FflogsEstimateState.InactiveContent => text.Get("状态：当前副本没有活跃榜单", "Status: no active ranking for this duty"),
        FflogsEstimateState.Error => text.Get("状态：连接失败", "Status: connection failed"),
        _ => state.ToString(),
    };

    private static Vector4 FflogsStatusColor(FflogsEstimateState state) => state switch
    {
        FflogsEstimateState.Ready => IceBlue,
        FflogsEstimateState.Error => new Vector4(0.93f, 0.38f, 0.36f, 1),
        FflogsEstimateState.InactiveContent => Gold,
        _ => new Vector4(0.70f, 0.72f, 0.76f, 1),
    };

    private void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            logger.Error(ex, $"Could not open URL: {url}");
        }
    }

    private string PlayerIdentityModeLabel(PlayerIdentityMode mode) => mode switch
    {
        PlayerIdentityMode.Job => text.Get("用职业替换 ID", "Replace names with jobs"),
        PlayerIdentityMode.Anonymous => text.Get("匿名编号", "Anonymous numbering"),
        _ => text.Get("显示原始 ID", "Show original names"),
    };

    private static void PushTheme()
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Navy);
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0.34f, 0.29f, 0.18f, 0.85f));
        ImGui.PushStyleColor(ImGuiCol.Separator, new Vector4(0.34f, 0.29f, 0.18f, 0.70f));
        ImGui.PushStyleColor(ImGuiCol.FrameBg, NavyRaised);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, NavyHover);
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.12f, 0.17f, 0.24f, 1));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.18f, 0.25f, 0.34f, 1));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.24f, 0.30f, 0.37f, 1));
        ImGui.PushStyleColor(ImGuiCol.CheckMark, IceBlue);
        ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(0.22f, 0.25f, 0.28f, 1));
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, NavyHover);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 8);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 5);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8, 8));
    }

    private static void PopTheme()
    {
        ImGui.PopStyleVar(3);
        ImGui.PopStyleColor(11);
    }
}

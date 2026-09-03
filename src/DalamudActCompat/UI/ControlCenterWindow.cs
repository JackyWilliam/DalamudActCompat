using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using DalamudActCompat.ActRuntime;
using DalamudActCompat.Compatibility.PluginHost;
using DalamudActCompat.Compatibility.Cactbot;
using DalamudActCompat.Core.Interfaces;
using DalamudActCompat.Core.Models;
using DalamudActCompat.Fflogs;
using DalamudActCompat.Infrastructure.Cloud;
using DalamudActCompat.Infrastructure.Logging;
using DalamudActCompat.Infrastructure.Resources;
using DalamudActCompat.Meter;
using DalamudActCompat.Parser;
using DalamudActCompat.Plugin;
using DalamudActCompat.Quality;
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
        Cloud,
        Diagnostics,
    }

    private enum HtmlOverlayCreatorPage
    {
        Template,
        Url,
    }

    private enum CloudAuthenticationPage
    {
        Login,
        Register,
        ResetPassword,
    }

    private sealed record CombatLogDirectoryFeedback(string Message, bool IsError);

    private static readonly Vector4 Navy = new(0.035f, 0.048f, 0.068f, 1);
    private static readonly Vector4 NavyRaised = new(0.070f, 0.095f, 0.125f, 1);
    private static readonly Vector4 NavyHover = new(0.105f, 0.145f, 0.185f, 1);
    private static readonly Vector4 Gold = new(0.78f, 0.66f, 0.36f, 1);
    private static readonly Vector4 IceBlue = new(0.42f, 0.78f, 0.96f, 1);
    private static readonly Vector4 AuthenticationHeroBackground = new(0.045f, 0.075f, 0.105f, 1);
    private static readonly Vector4 AuthenticationCardBackground = new(0.055f, 0.070f, 0.095f, 1);
    private static readonly Vector4 AuthenticationBorder = new(0.25f, 0.48f, 0.60f, 0.62f);
    private static readonly Vector4 AuthenticationMutedText = new(0.62f, 0.68f, 0.74f, 1);
    private static readonly string VersionLabel = BuildVersionLabel();
    private const int OpenAnimationMilliseconds = 180;
    private const int CloseAnimationMilliseconds = 160;
    private const int ResetConfirmationMilliseconds = 10_000;
    private const string GenericPermissionPopupId =
        "第三方 ACT 插件授权###DalamudActCompatGenericPermission";
    private const string GenericDeletePopupId =
        "删除第三方 ACT 插件###DalamudActCompatGenericDelete";
    private const string PluginInstallFailurePopupId =
        "第三方插件导入失败###DalamudActCompatPluginInstallFailure";
    private const string ResetEncounterPopupId = "重置当前战斗###DalamudActCompatResetEncounter";

    private readonly PluginConfiguration configuration;
    private readonly IParserEngine parserEngine;
    private readonly PluginLogger logger;
    private readonly UiText text;
    private readonly FflogsEstimateService fflogsEstimateService;
    private readonly CombatQualitySnapshot? combatQualitySnapshot;
    private readonly Func<Encounter?> getCurrentEncounter;
    private readonly ISharedImmediateTexture logoTexture;
    private readonly Action openHelp;
    private readonly Action saveConfiguration;
    private readonly Func<int, bool> applyHistoryLimit;
    private readonly Action applyPermissionChanges;
    private readonly Func<GameRegionSelection> getGameRegionSelection;
    private readonly Action<GameRegionMode> setGameRegionMode;
    private readonly Action<bool> setSimplifiedMode;
    private readonly Action<bool> setHideHtmlOverlaysWhenUnfocused;
    private readonly Action<bool> setMeterVisible;
    private readonly Action openMeter;
    private readonly Action openMeterStyleEditor;
    private readonly Action openHistory;
    private readonly Func<bool> isStatusVisible;
    private readonly Action<bool> setStatusVisible;
    private readonly Action selectPluginPackage;
    private readonly Func<ThirdPartyPluginInstallStatus> getPluginInstallStatus;
    private readonly Action approvePendingPlugin;
    private readonly Action denyPendingPlugin;
    private readonly Action<string> requestPluginAuthorization;
    private readonly Action<string> uninstallGenericPlugin;
    private readonly Action dismissPluginInstallFailure;
    private readonly Action openPluginDirectory;
    private readonly Action openBundledPluginNotice;
    private readonly Action checkBundledPluginUpdates;
    private readonly Action openLogDirectory;
    private readonly Func<string> openCombatLogDirectory;
    private readonly Func<string> getCombatLogDirectory;
    private readonly Action<Action<bool, string>> selectCombatLogDirectory;
    private readonly Action<Action<bool, string>> resetCombatLogDirectory;
    private readonly Func<string> buildDiagnosticReport;
    private readonly Func<IReadOnlyList<InstalledActPlugin>> discoverPlugins;
    private readonly Action<string> openPluginConfiguration;
    private readonly Func<ResourcePackOperationStatus> getBundledActResourceStatus;
    private readonly Action startBundledActResourceDownload;
    private readonly Action cancelBundledActResourceDownload;
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
    private readonly CloudUiBridge cloud;
    private Page selectedPage;
    private HtmlOverlayCreatorPage selectedHtmlOverlayCreatorPage;
    private ParserStatus parserStatus;
    private string? selectedCreatedOverlay;
    private string? selectedUsedCactbotOverlay;
    private string? selectedAvailableCactbotOverlay;
    private string customOverlayName = string.Empty;
    private string customOverlayUrl = string.Empty;
    private string? customOverlayFeedback;
    private bool customOverlayFeedbackIsError;
    private string? overlayBeingRenamed;
    private string overlayRenameValue = string.Empty;
    private string? overlayRenameFeedback;
    private string? diagnosticCopyFeedback;
    private bool diagnosticCopyFeedbackIsError;
    private string? combatLogFolderFeedback;
    private bool combatLogFolderFeedbackIsError;
    // Parser restart finishes off the draw thread, so replace one immutable snapshot to keep
    // the result text and its success/error color from being observed out of sync.
    private CombatLogDirectoryFeedback? combatLogDirectoryChangeFeedback;
    private VisibilityTransition visibilityTransition = VisibilityTransition.Closed;
    private long visibilityTransitionStartedAt;
    private bool visibilityStylePushed;
    private long resetEncounterConfirmationExpiresAt;
    private bool confirmFactoryReset;
    private string? factoryResetResult;
    private string? openedGenericPermissionKey;
    private ThirdPartyPluginInstallStatus? openedPluginFailureStatus;
    private bool pluginFailureLogCopied;
    private string? genericPluginToDeleteId;
    private string? genericPluginToDeleteName;
    private bool genericDeletePopupRequested;
    private bool focusOnNextDraw;
    private bool locateOnNextDraw;
    private int? historyLimitDraft;
    private CloudAuthenticationPage cloudAuthenticationPage;
    private string cloudUsername = string.Empty;
    private string cloudPassword = string.Empty;
    private string cloudPasswordConfirmation = string.Empty;
    private string cloudActivationKey = string.Empty;
    private string cloudResetCode = string.Empty;
    private string cloudRecoveryKey = string.Empty;
    private string cloudInviteeContact = string.Empty;
    private bool cloudRememberLogin = true;
    private string? selectedCloudBackupId;
    private string? cloudPreviewRequestedBackupId;
    private bool confirmCloudRestore;
    private bool confirmCloudRollback;

    public ControlCenterWindow(
        PluginConfiguration configuration,
        IParserEngine parserEngine,
        PluginLogger logger,
        UiText text,
        FflogsEstimateService fflogsEstimateService,
        CombatQualitySnapshot? combatQualitySnapshot,
        Func<Encounter?> getCurrentEncounter,
        ISharedImmediateTexture logoTexture,
        Action openHelp,
        Action saveConfiguration,
        Func<int, bool> applyHistoryLimit,
        Action applyPermissionChanges,
        Func<GameRegionSelection> getGameRegionSelection,
        Action<GameRegionMode> setGameRegionMode,
        Action<bool> setSimplifiedMode,
        Action<bool> setHideHtmlOverlaysWhenUnfocused,
        Action<bool> setMeterVisible,
        Action openMeter,
        Action openMeterStyleEditor,
        Action openHistory,
        Func<bool> isStatusVisible,
        Action<bool> setStatusVisible,
        Action selectPluginPackage,
        Func<ThirdPartyPluginInstallStatus> getPluginInstallStatus,
        Action approvePendingPlugin,
        Action denyPendingPlugin,
        Action<string> requestPluginAuthorization,
        Action<string> uninstallGenericPlugin,
        Action dismissPluginInstallFailure,
        Action openPluginDirectory,
        Action openBundledPluginNotice,
        Action checkBundledPluginUpdates,
        Action openLogDirectory,
        Func<string> openCombatLogDirectory,
        Func<string> getCombatLogDirectory,
        Action<Action<bool, string>> selectCombatLogDirectory,
        Action<Action<bool, string>> resetCombatLogDirectory,
        Func<string> buildDiagnosticReport,
        Func<IReadOnlyList<InstalledActPlugin>> discoverPlugins,
        Action<string> openPluginConfiguration,
        Func<ResourcePackOperationStatus> getBundledActResourceStatus,
        Action startBundledActResourceDownload,
        Action cancelBundledActResourceDownload,
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
        Action<string> applyOverlayWindowSettings,
        CloudUiBridge cloud)
        : base("ACT 控制中心###DalamudActCompatControlCenter")
    {
        this.configuration = configuration;
        this.parserEngine = parserEngine;
        this.logger = logger;
        this.text = text;
        this.fflogsEstimateService = fflogsEstimateService;
        this.combatQualitySnapshot = combatQualitySnapshot;
        this.getCurrentEncounter = getCurrentEncounter;
        this.logoTexture = logoTexture;
        this.openHelp = openHelp;
        this.saveConfiguration = saveConfiguration;
        this.applyHistoryLimit = applyHistoryLimit;
        this.applyPermissionChanges = applyPermissionChanges;
        this.getGameRegionSelection = getGameRegionSelection;
        this.setGameRegionMode = setGameRegionMode;
        this.setSimplifiedMode = setSimplifiedMode;
        this.setHideHtmlOverlaysWhenUnfocused = setHideHtmlOverlaysWhenUnfocused;
        this.setMeterVisible = setMeterVisible;
        this.openMeter = openMeter;
        this.openMeterStyleEditor = openMeterStyleEditor;
        this.openHistory = openHistory;
        this.isStatusVisible = isStatusVisible;
        this.setStatusVisible = setStatusVisible;
        this.selectPluginPackage = selectPluginPackage;
        this.getPluginInstallStatus = getPluginInstallStatus;
        this.approvePendingPlugin = approvePendingPlugin;
        this.denyPendingPlugin = denyPendingPlugin;
        this.requestPluginAuthorization = requestPluginAuthorization;
        this.uninstallGenericPlugin = uninstallGenericPlugin;
        this.dismissPluginInstallFailure = dismissPluginInstallFailure;
        this.openPluginDirectory = openPluginDirectory;
        this.openBundledPluginNotice = openBundledPluginNotice;
        this.checkBundledPluginUpdates = checkBundledPluginUpdates;
        this.openLogDirectory = openLogDirectory;
        this.openCombatLogDirectory = openCombatLogDirectory;
        this.getCombatLogDirectory = getCombatLogDirectory;
        this.selectCombatLogDirectory = selectCombatLogDirectory;
        this.resetCombatLogDirectory = resetCombatLogDirectory;
        this.buildDiagnosticReport = buildDiagnosticReport;
        this.discoverPlugins = discoverPlugins;
        this.openPluginConfiguration = openPluginConfiguration;
        this.getBundledActResourceStatus = getBundledActResourceStatus;
        this.startBundledActResourceDownload = startBundledActResourceDownload;
        this.cancelBundledActResourceDownload = cancelBundledActResourceDownload;
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
        this.cloud = cloud;
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
        // External plugin collectors can invoke OpenMainUi while this window is already open
        // behind another window, so an "appearing" focus condition alone is insufficient.
        focusOnNextDraw = true;
        visibilityTransition = VisibilityTransition.Opening;
        visibilityTransitionStartedAt = Environment.TickCount64;
    }

    public void LocateAnimated()
    {
        locateOnNextDraw = true;
        ShowAnimated();
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
            LocateAnimated();
        }
    }

    public void ShowExtensionsPage()
    {
        selectedPage = Page.Extensions;
        ShowAnimated();
    }

    public override void PreDraw()
    {
        if (locateOnNextDraw)
        {
            var viewport = ImGui.GetMainViewport();
            ImGui.SetNextWindowPos(
                viewport.Pos + (viewport.Size * 0.5f),
                ImGuiCond.Always,
                new Vector2(0.5f, 0.5f));
            locateOnNextDraw = false;
        }

        if (focusOnNextDraw)
        {
            ImGui.SetNextWindowFocus();
            focusOnNextDraw = false;
        }

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
            var cloudSnapshot = cloud.GetSnapshot();
            if (!cloudSnapshot.IsSignedIn)
            {
                ImGui.Spacing();
                if (ImGui.BeginChild("account-authentication-gate", new Vector2(-1, -1), false))
                {
                    DrawAuthenticationGate(cloudSnapshot);
                }
                ImGui.EndChild();
                return;
            }

            if (!string.IsNullOrWhiteSpace(cloudSnapshot.RecoveryKeyToSave))
            {
                // Registration reveals the recovery key once, so keep it visible instead
                // of dropping the user onto the previously selected functional page.
                selectedPage = Page.Cloud;
            }
            DrawPageTabs();
            ImGui.Spacing();
            if (ImGui.BeginChild("control-center-page-content", new Vector2(-1, -1), true))
            {
                var hostConfigurationChanged = false;
                var changed = selectedPage switch
                {
                    Page.Overview => DrawOverview(),
                    Page.Meter => DrawMeter(),
                    Page.Overlays => DrawOverlays(),
                    Page.Extensions => DrawExtensions(out hostConfigurationChanged),
                    Page.Cloud => DrawCloud(),
                    Page.Diagnostics => DrawDiagnostics(),
                    _ => false,
                };

                if (changed)
                {
                    saveConfiguration();
                }

                if (hostConfigurationChanged)
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

    public override void OnClose()
    {
        historyLimitDraft = null;
        saveConfiguration();
    }

    public void Detach() => parserEngine.StatusChanged -= OnParserStatusChanged;

    private void DrawHistoryLimitEditor()
    {
        var draft = historyLimitDraft ?? configuration.HistoryLimit;
        if (ImGui.SliderInt(text.Get("历史记录上限", "History limit"), ref draft, 1, 200))
        {
            historyLimitDraft = draft;
        }

        ImGui.TextDisabled(text.Get(
            "战斗历史、战斗 JSON 与 Network 日志分别保留此数量。",
            "Encounter history, encounter JSON, and Network logs each keep this many."));
        if (historyLimitDraft is not { } pending || pending == configuration.HistoryLimit)
        {
            historyLimitDraft = null;
            return;
        }

        // This page shares the same destructive setting as advanced settings, so both
        // entry points require the same explicit confirmation contract.
        if (ImGui.SmallButton(text.Get("确定###history-limit-confirm", "Confirm###history-limit-confirm")) &&
            applyHistoryLimit(pending))
        {
            historyLimitDraft = null;
        }
        ImGui.SameLine();
        if (ImGui.SmallButton(text.Get("取消###history-limit-cancel", "Cancel###history-limit-cancel")))
        {
            historyLimitDraft = null;
        }
    }

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
                "control-center",
                helpAction: openHelp,
                helpTooltip: text.Get("帮助", "Help")))
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
            (Page.Cloud, text.Get("云同步", "Cloud Sync")),
            (Page.Diagnostics, text.Get("设置&账号", "Settings & Account")),
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

        var cardContentWidth = Math.Max(
            1,
            ImGui.GetContentRegionAvail().X - (ImGui.GetStyle().WindowPadding.X * 2));
        var parserMessageHeight = ImGui.CalcTextSize(
            parserStatus.Message,
            false,
            cardContentWidth).Y;
        var parserCardHeight =
            (ImGui.GetStyle().WindowPadding.Y * 2) +
            (ImGui.GetTextLineHeightWithSpacing() *
             (string.IsNullOrWhiteSpace(parserStatus.Detail) ? 2 : 3)) +
            parserMessageHeight +
            ImGui.GetStyle().ItemSpacing.Y;
        if (BrandedWindowChrome.BeginGoldCard(
                "overview-parser-card",
                parserCardHeight,
                allowScrolling: false))
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
        var quickActionsCardHeight =
            (ImGui.GetStyle().WindowPadding.Y * 2) +
            ImGui.GetTextLineHeightWithSpacing() +
            72 +
            (ImGui.GetStyle().ItemSpacing.Y * 3) +
            (string.IsNullOrWhiteSpace(combatLogFolderFeedback)
                ? 0
                : ImGui.GetTextLineHeightWithSpacing());
        if (BrandedWindowChrome.BeginGoldCard(
                "overview-quick-actions-card",
                quickActionsCardHeight,
                allowScrolling: false))
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
        var generalHint = text.Get(
            "快捷按钮：左键设置、右键战斗统计、按住中键拖动。",
            "Quick button: left settings, right Combat Meter, hold middle mouse to move.");
        var regionHint = text.Get(
            "无法读取游戏区域，暂按国际服处理 · 可手动选择 · 语言：简体中文",
            "Game region unavailable; using Global · Manual selection is available · Language: Simplified Chinese");
        var generalCardHeight =
            (ImGui.GetStyle().WindowPadding.Y * 2) +
            ImGui.GetTextLineHeightWithSpacing() +
            (ImGui.GetFrameHeightWithSpacing() * 6) +
            ImGui.CalcTextSize(regionHint, false, cardContentWidth).Y +
            ImGui.CalcTextSize(generalHint, false, cardContentWidth).Y +
            (ImGui.GetStyle().ItemSpacing.Y * 2);
        if (BrandedWindowChrome.BeginGoldCard(
                "overview-general-card",
                generalCardHeight,
                allowScrolling: false))
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
            GameRegionSelector.Draw(
                text,
                getGameRegionSelection(),
                setGameRegionMode);
            var simplifiedMode = configuration.SimplifiedModeEnabled;
            if (ImGui.Checkbox(text.Get("精简模式", "Simplified mode"), ref simplifiedMode))
            {
                setSimplifiedMode(simplifiedMode);
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(text.Get(
                    "只保留解析和战斗统计；网页悬浮窗、扩展插件及其他 DACT 窗口会暂时关闭。可用 /actcompat simple off 退出。",
                    "Keeps only parsing and the combat meter. Web overlays, extensions, and other DACT windows are temporarily closed. Use /actcompat simple off to exit."));
            }
            var hideWhenUnfocused = configuration.HideHtmlOverlaysWhenGameUnfocused;
            if (ImGui.Checkbox(
                    text.Get("游戏失去焦点时隐藏网页悬浮窗", "Hide web overlays when the game is unfocused"),
                    ref hideWhenUnfocused))
            {
                setHideHtmlOverlaysWhenUnfocused(hideWhenUnfocused);
            }
            changed |= Checkbox(
                text.Get("显示 ACT 快捷按钮", "Show ACT quick button"),
                configuration.ShowLauncherButton,
                value => configuration.ShowLauncherButton = value);
            ImGui.TextDisabled(generalHint);
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

        ImGui.TextColored(IceBlue, text.Get("榜单模板", "Meter template"));
        var activeKind = configuration.Meter.ActiveWindowKind;
        ImGui.SetNextItemWidth(260);
        if (ImGui.BeginCombo("##meter-template", MeterKindLabel(activeKind)))
        {
            foreach (var kind in Enum.GetValues<MeterWindowKind>())
            {
                if (ImGui.Selectable(MeterKindLabel(kind), kind == activeKind))
                {
                    configuration.Meter.ActivateWindow(kind);
                    changed = true;
                }
            }
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        if (ImGui.Button(text.Get("自定义", "Customize")))
        {
            openMeterStyleEditor();
        }
        ImGui.TextDisabled(text.Get(
            "一次只显示一个榜单；切换时保留各模板的位置、大小、锁定和槽位配置。",
            "Only one meter is shown at a time; each template keeps its own position, size, lock, and slots."));

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
        DrawResetEncounterConfirmation();

        var refreshInterval = configuration.Meter.RefreshIntervalMs;
        if (ImGui.SliderInt(
                text.Get("刷新间隔（毫秒）", "Refresh interval (ms)"),
                ref refreshInterval,
                250,
                2000))
        {
            configuration.Meter.RefreshIntervalMs = refreshInterval;
            changed = true;
        }
        changed |= DrawPlayerIdentityControls();
        changed |= DrawFflogsSettings();
        return changed;
    }

    private string MeterKindLabel(MeterWindowKind kind) => kind switch
    {
        MeterWindowKind.Horizontal => text.Get("横版", "Horizontal"),
        MeterWindowKind.RoleSplit => text.Get("职能分栏", "Role split"),
        _ => text.Get("经典榜", "Classic"),
    };

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
        var hideWhenUnfocused = configuration.HideHtmlOverlaysWhenGameUnfocused;
        if (ImGui.Checkbox(
                text.Get("游戏失去焦点时隐藏网页悬浮窗", "Hide web overlays when the game is unfocused"),
                ref hideWhenUnfocused))
        {
            setHideHtmlOverlaysWhenUnfocused(hideWhenUnfocused);
        }
        ImGui.TextDisabled(text.Get(
            "仅临时隐藏网页悬浮窗，不改变各悬浮窗保存的开启状态。",
            "Temporarily hides web overlays without changing their saved open state."));
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
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
        ImGui.Separator();
        ImGui.Spacing();
        changed |= DrawHtmlOverlayCreators(allTemplates);

        return changed;
    }

    private bool DrawHtmlOverlayCreators(IReadOnlyList<ActOverlayTemplate> allTemplates)
    {
        var labels = new[]
        {
            text.Get("从模板创建", "Create from template"),
            text.Get("从网址创建", "Create from URL"),
        };
        var selectedIndex = BrandedWindowChrome.DrawNavigationRail(
            "html-overlay-create-tabs",
            labels,
            (int)selectedHtmlOverlayCreatorPage,
            height: 34);
        selectedHtmlOverlayCreatorPage = (HtmlOverlayCreatorPage)selectedIndex;
        ImGui.Spacing();

        return selectedHtmlOverlayCreatorPage switch
        {
            HtmlOverlayCreatorPage.Template => DrawTemplateHtmlOverlayCreator(allTemplates),
            HtmlOverlayCreatorPage.Url => DrawCustomHtmlOverlayCreator(),
            _ => false,
        };
    }

    private bool DrawTemplateHtmlOverlayCreator(IReadOnlyList<ActOverlayTemplate> allTemplates)
    {
        var changed = false;
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
            .OrderBy(
                name => ResolveOverlayDisplayName(
                    name,
                    configuration.OverlayWindows[name]),
                StringComparer.OrdinalIgnoreCase)
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
                var settings = configuration.OverlayWindows[name];
                var displayName = ResolveOverlayDisplayName(name, settings);
                if (ImGui.Selectable(
                        $"{displayName}##created-overlay-{name}",
                        string.Equals(
                            name,
                            selectedCreatedOverlay,
                            StringComparison.OrdinalIgnoreCase)))
                {
                    selectedCreatedOverlay = name;
                    if (!string.Equals(
                            overlayBeingRenamed,
                            name,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        CancelOverlayRename();
                    }
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
                if (ImGui.Button(text.Get("重命名", "Rename")))
                {
                    overlayBeingRenamed = selectedCreatedOverlay;
                    overlayRenameValue = ResolveOverlayDisplayName(
                        selectedCreatedOverlay,
                        createdSettings);
                    overlayRenameFeedback = null;
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
                    CancelOverlayRename();
                }
                else
                {
                    if (string.Equals(
                            overlayBeingRenamed,
                            selectedCreatedOverlay,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        changed |= DrawOverlayRenameEditor(
                            selectedCreatedOverlay,
                            createdSettings);
                    }
                    changed |= DrawOverlayWindowSettings(selectedCreatedOverlay);
                }
            }
        }

        return changed;
    }

    private bool DrawOverlayRenameEditor(
        string overlayKey,
        HtmlOverlayWindowSettings settings)
    {
        var changed = false;
        ImGui.Spacing();
        ImGui.SetNextItemWidth(280);
        ImGui.InputText(
            text.Get("新名称###overlay-rename-value", "New name###overlay-rename-value"),
            ref overlayRenameValue,
            80);
        if (ImGui.Button(text.Get("保存名称", "Save name")))
        {
            var candidate = overlayRenameValue.Trim();
            if (string.IsNullOrWhiteSpace(candidate))
            {
                overlayRenameFeedback = text.Get(
                    "名称不能为空。",
                    "The name cannot be empty.");
            }
            else if (SelfHostedActRuntime.IsCactbotOverlayName(candidate) ||
                     HasOverlayDisplayNameConflict(overlayKey, candidate))
            {
                overlayRenameFeedback = text.Get(
                    "该名称已被使用或属于系统保留名称。",
                    "That name is already in use or reserved by the system.");
            }
            else
            {
                settings.DisplayName = string.Equals(
                    overlayKey,
                    candidate,
                    StringComparison.OrdinalIgnoreCase)
                    ? string.Empty
                    : candidate;
                applyOverlayWindowSettings(overlayKey);
                saveConfiguration();
                CancelOverlayRename();
                changed = true;
            }
        }
        ImGui.SameLine();
        if (ImGui.Button(text.Get("取消重命名", "Cancel rename")))
        {
            CancelOverlayRename();
        }

        if (!string.IsNullOrWhiteSpace(overlayRenameFeedback))
        {
            ImGui.TextColored(
                new Vector4(0.96f, 0.42f, 0.38f, 1),
                overlayRenameFeedback);
        }

        return changed;
    }

    private bool HasOverlayDisplayNameConflict(string overlayKey, string candidate)
    {
        if (getOverlayTemplates().Any(template =>
                !string.Equals(template.Name, overlayKey, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(template.Name, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return configuration.OverlayWindows.Any(pair =>
            !string.Equals(pair.Key, overlayKey, StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(pair.Key, candidate, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(
                 ResolveOverlayDisplayName(pair.Key, pair.Value),
                 candidate,
                 StringComparison.OrdinalIgnoreCase)));
    }

    private void CancelOverlayRename()
    {
        overlayBeingRenamed = null;
        overlayRenameValue = string.Empty;
        overlayRenameFeedback = null;
    }

    internal static string ResolveOverlayDisplayName(
        string overlayKey,
        HtmlOverlayWindowSettings settings)
        => string.IsNullOrWhiteSpace(settings.DisplayName)
            ? overlayKey
            : settings.DisplayName.Trim();

    private bool DrawCustomHtmlOverlayCreator()
    {
        var changed = false;
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
                     configuration.OverlayWindows.ContainsKey(name) ||
                     HasOverlayDisplayNameConflict(name, name))
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

    private bool DrawExtensions(out bool hostConfigurationChanged)
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
        ImGui.TextDisabled(text.Get(
            "改变系统插件状态后需要重启解析器；兼容扩展启停后会自动重启独立 Host。",
            "Restart the parser after changing a system plugin; compatibility-extension changes restart the independent Host automatically."));

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextColored(Gold, text.Get("兼容扩展", "Compatibility extensions"));
        var installedPlugins = discoverPlugins();
        hostConfigurationChanged = false;
        changed |= DrawExtensionEntry(
            installedPlugins,
            "act.foxtts",
            "ACT.FoxTTS",
            text.Get("TTS 语音合成与播报", "TTS speech synthesis and announcements"),
            out var extensionChanged);
        hostConfigurationChanged |= extensionChanged;
        changed |= DrawExtensionEntry(
            installedPlugins,
            "postnamazu",
            text.Get("鲶鱼精邮差 / PostNamazu", "PostNamazu"),
            text.Get("游戏命令、标点与本地桥接", "Game commands, markers, and local bridge"),
            out extensionChanged);
        hostConfigurationChanged |= extensionChanged;
        changed |= DrawExtensionEntry(
            installedPlugins,
            "triggernometry",
            "Triggernometry",
            text.Get("触发器、时间轴、TTS 与绘图", "Triggers, timelines, TTS, and drawing"),
            out extensionChanged);
        hostConfigurationChanged |= extensionChanged;
        changed |= DrawExtensionEntry(
            installedPlugins,
            "silverdasher",
            text.Get("银山雀儿 / SilverDasher", "SilverDasher"),
            text.Get(
                "狩猎、临危受命与跨区状态提醒",
                "Hunts, FATEs, and cross-world status alerts"),
            out extensionChanged);
        hostConfigurationChanged |= extensionChanged;
        changed |= DrawExtensionEntry(
            installedPlugins,
            "matcha",
            text.Get("抹茶 / Cafe.Matcha", "Cafe.Matcha"),
            text.Get(
                "狩猎与临危受命提醒；默认自启动并独占专属 Host",
                "Hunt and FATE alerts; starts by default in its dedicated Host"),
            out extensionChanged);
        hostConfigurationChanged |= extensionChanged;

        var genericPlugins = installedPlugins
            .Where(plugin => !ActPluginPackageInstaller.IsSpecializedPluginId(
                plugin.Manifest.Id))
            .ToArray();
        if (genericPlugins.Length > 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(
                IceBlue,
                text.Get("用户安装的普通 ACT 插件", "User-installed generic ACT plugins"));
            ImGui.TextDisabled(text.Get(
                "这些插件共用一个按需启动的通用 Host，不会为每个插件创建常驻进程。",
                "These plugins share one on-demand generic Host; no persistent process is created per plugin."));
            foreach (var plugin in genericPlugins)
            {
                changed |= DrawGenericExtensionEntry(plugin, out extensionChanged);
                hostConfigurationChanged |= extensionChanged;
            }
        }
        DrawGenericDeleteModal();

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
        DrawPluginInstallStatus();
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
        hostConfigurationChanged |= DrawPluginPermissions(
            "postnamazu",
            text.Get("鲶鱼精邮差 / PostNamazu", "PostNamazu"),
            BundledActPluginCapabilities.PostNamazu);
        hostConfigurationChanged |= DrawPluginPermissions(
            "triggernometry",
            "Triggernometry",
            BundledActPluginCapabilities.Triggernometry);
        hostConfigurationChanged |= DrawPluginPermissions(
            "silverdasher",
            text.Get("银山雀儿 / SilverDasher", "SilverDasher"),
            BundledActPluginCapabilities.SilverDasher);
        hostConfigurationChanged |= DrawPluginPermissions(
            "matcha",
            text.Get("抹茶 / Cafe.Matcha", "Cafe.Matcha"),
            BundledActPluginCapabilities.Matcha);
        changed |= hostConfigurationChanged;
        return changed;
    }

    private void DrawPluginInstallStatus()
    {
        var status = getPluginInstallStatus();
        if (status.State == ThirdPartyPluginInstallState.Idle)
        {
            return;
        }

        ImGui.Spacing();
        ImGui.TextUnformatted(string.IsNullOrWhiteSpace(status.DisplayName)
            ? text.Get("第三方插件", "Third-party plugin")
            : status.DisplayName);
        ImGui.SameLine();
        var (labelZh, labelEn) = status.State switch
        {
            ThirdPartyPluginInstallState.Preflighting => ("正在预检…", "Preflighting…"),
            ThirdPartyPluginInstallState.AwaitingPermission => ("等待权限确认", "Awaiting permission"),
            ThirdPartyPluginInstallState.StartingHost => ("正在启动通用 Host…", "Starting generic Host…"),
            ThirdPartyPluginInstallState.Ready => ("已可用", "Ready"),
            ThirdPartyPluginInstallState.Denied => ("已安装，未授权", "Installed, not authorized"),
            ThirdPartyPluginInstallState.Removing => ("正在删除…", "Removing…"),
            ThirdPartyPluginInstallState.Removed => ("已删除", "Removed"),
            ThirdPartyPluginInstallState.Failed => ("失败", "Failed"),
            _ => ("", ""),
        };
        var statusColor = status.State is ThirdPartyPluginInstallState.Failed
            ? new Vector4(0.95f, 0.38f, 0.38f, 1)
            : status.State is ThirdPartyPluginInstallState.Ready
                ? new Vector4(0.42f, 0.88f, 0.56f, 1)
                : Gold;
        ImGui.TextColored(statusColor, text.Get(labelZh, labelEn));
        if (!string.IsNullOrWhiteSpace(status.Detail))
        {
            ImGui.TextWrapped(status.Detail);
        }

        if (status.State == ThirdPartyPluginInstallState.Failed)
        {
            openedGenericPermissionKey = null;
            if (!ReferenceEquals(openedPluginFailureStatus, status))
            {
                openedPluginFailureStatus = status;
                pluginFailureLogCopied = false;
                ImGui.OpenPopup(PluginInstallFailurePopupId);
            }

            if (ImGui.Button(text.Get("查看失败原因", "View failure details")))
            {
                ImGui.OpenPopup(PluginInstallFailurePopupId);
            }
            DrawPluginInstallFailureModal(status);
            return;
        }

        openedPluginFailureStatus = null;
        pluginFailureLogCopied = false;
        if (status.State != ThirdPartyPluginInstallState.AwaitingPermission)
        {
            openedGenericPermissionKey = null;
            return;
        }

        var permissionKey = $"{status.PluginId}|{status.Version}";
        if (!string.Equals(
                openedGenericPermissionKey,
                permissionKey,
                StringComparison.Ordinal))
        {
            openedGenericPermissionKey = permissionKey;
            ImGui.OpenPopup(GenericPermissionPopupId);
        }

        if (ImGui.Button(text.Get("查看授权", "Review permissions")))
        {
            ImGui.OpenPopup(GenericPermissionPopupId);
        }
        DrawGenericPermissionModal(status);
    }

    private void DrawPluginInstallFailureModal(ThirdPartyPluginInstallStatus status)
    {
        ImGui.SetNextWindowSize(new Vector2(640, 0), ImGuiCond.Appearing);
        if (!ImGui.BeginPopupModal(
                PluginInstallFailurePopupId,
                ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoResize))
        {
            return;
        }

        ImGui.TextColored(
            new Vector4(0.95f, 0.38f, 0.38f, 1),
            text.Get("插件导入失败", "Plugin import failed"));
        ImGui.TextUnformatted(string.IsNullOrWhiteSpace(status.DisplayName)
            ? text.Get("第三方插件", "Third-party plugin")
            : status.DisplayName);
        if (!string.IsNullOrWhiteSpace(status.Version))
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"v{status.Version}");
        }

        ImGui.Separator();
        ImGui.TextWrapped(string.IsNullOrWhiteSpace(status.Detail)
            ? text.Get("导入过程没有返回具体原因。", "The import did not return a specific reason.")
            : status.Detail);
        ImGui.Spacing();
        if (ImGui.Button(
                text.Get("复制日志", "Copy log"),
                new Vector2(140, 34)))
        {
            ImGui.SetClipboardText(BuildPluginInstallFailureLog(status));
            pluginFailureLogCopied = true;
        }
        if (pluginFailureLogCopied)
        {
            ImGui.SameLine();
            ImGui.TextColored(
                new Vector4(0.45f, 0.88f, 0.62f, 1),
                text.Get("已复制", "Copied"));
        }

        ImGui.SameLine();
        if (ImGui.Button(
                text.Get("关闭并清除记录", "Close and clear"),
                new Vector2(180, 34)))
        {
            ImGui.CloseCurrentPopup();
            openedPluginFailureStatus = null;
            pluginFailureLogCopied = false;
            dismissPluginInstallFailure();
        }

        ImGui.TextDisabled(text.Get(
            "关闭只会清除本次失败提示，不会删除原始 DLL / ZIP 或已安装插件。",
            "Closing clears only this failure notice; it does not delete the original DLL / ZIP or installed plugins."));
        ImGui.EndPopup();
    }

    internal static string BuildPluginInstallFailureLog(
        ThirdPartyPluginInstallStatus status)
    {
        var lines = new List<string>
        {
            $"Plugin: {status.DisplayName}",
            $"Plugin ID: {status.PluginId}",
            $"Version: {status.Version}",
            $"Reason: {status.Detail}",
        };
        if (!string.IsNullOrWhiteSpace(status.Diagnostic) &&
            !string.Equals(status.Diagnostic, status.Detail, StringComparison.Ordinal))
        {
            lines.Add("Diagnostic:");
            lines.Add(status.Diagnostic);
        }

        return string.Join(Environment.NewLine, lines);
    }

    private void DrawGenericPermissionModal(ThirdPartyPluginInstallStatus status)
    {
        ImGui.SetNextWindowSize(new Vector2(620, 0), ImGuiCond.Appearing);
        if (!ImGui.BeginPopupModal(
                GenericPermissionPopupId,
                ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoResize))
        {
            return;
        }

        ImGui.TextColored(Gold, text.Get("第三方 ACT 插件授权", "Third-party ACT plugin authorization"));
        ImGui.TextUnformatted(status.DisplayName);
        if (!string.IsNullOrWhiteSpace(status.Version))
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"v{status.Version}");
        }

        ImGui.Separator();
        ImGui.TextWrapped(text.Get(
            "静态预检已经完成。该 DLL 会作为桌面代码运行；以下清单约束兼容接口，但无法拦截插件自身直接调用 Windows API。",
            "Static preflight is complete. This DLL runs as desktop code; the list below governs compatibility APIs but cannot intercept direct Windows API calls made by the plugin."));
        ImGui.TextDisabled(text.Get(
            "预检生成的权限清单：",
            "Permissions generated by preflight:"));
        if (status.Capabilities.Count == 0)
        {
            ImGui.BulletText(text.Get("未检测到额外兼容接口权限", "No additional compatibility API permissions detected"));
        }
        else
        {
            foreach (var capability in status.Capabilities)
            {
                ImGui.BulletText(ActCapabilityDisplay.Label(capability, text));
            }
        }

        ImGui.Spacing();
        if (ImGui.Button(
                text.Get("授权并启用", "Authorize and enable"),
                new Vector2(170, 34)))
        {
            ImGui.CloseCurrentPopup();
            approvePendingPlugin();
        }
        ImGui.SameLine();
        if (ImGui.Button(
                text.Get("暂不授权", "Do not authorize"),
                new Vector2(150, 34)))
        {
            ImGui.CloseCurrentPopup();
            denyPendingPlugin();
        }

        ImGui.EndPopup();
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

    private bool DrawGenericExtensionEntry(
        InstalledActPlugin plugin,
        out bool enabledChanged)
    {
        var pluginId = plugin.Manifest.Id;
        var changed = false;
        enabledChanged = false;
        ImGui.PushID($"generic-extension-{pluginId}");
        var trusted = configuration.TrustedGenericActPluginIds.Contains(pluginId);
        var enabled = plugin.Enabled && trusted;
        if (ImGui.Checkbox(plugin.Manifest.Name, ref enabled))
        {
            if (enabled && !configuration.TrustedGenericActPluginIds.Contains(pluginId))
            {
                // Enabling an untrusted DLL opens the same informed-consent flow as installation.
                requestPluginAuthorization(pluginId);
                enabled = false;
            }
            else
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
                enabledChanged = true;
            }
        }

        ImGui.SameLine();
        ImGui.TextColored(
            enabled ? IceBlue : new Vector4(0.66f, 0.69f, 0.74f, 1),
            enabled
                ? text.Get("已启用", "Enabled")
                : trusted
                    ? text.Get("已禁用", "Disabled")
                    : text.Get("未授权", "Not authorized"));
        ImGui.SameLine();
        DrawInstalledVersion(plugin);
        if (!trusted)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton(text.Get("查看并授权", "Review and authorize")))
            {
                requestPluginAuthorization(pluginId);
            }
        }
        else if (enabled)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton(text.Get("打开配置", "Open configuration")))
            {
                openPluginConfiguration(pluginId);
            }
        }
        ImGui.SameLine();
        if (ImGui.SmallButton(text.Get("删除", "Delete")))
        {
            genericPluginToDeleteId = pluginId;
            genericPluginToDeleteName = plugin.Manifest.Name;
            genericDeletePopupRequested = true;
        }

        var requested = ActPluginPackageInstaller.GetRequestedCapabilities(plugin.Manifest);
        if (trusted && requested.Count > 0)
        {
            enabledChanged |= DrawPluginPermissions(pluginId, plugin.Manifest.Name, requested);
        }

        ImGui.PopID();
        return changed;
    }

    private void DrawGenericDeleteModal()
    {
        if (genericDeletePopupRequested)
        {
            ImGui.OpenPopup(GenericDeletePopupId);
            genericDeletePopupRequested = false;
        }

        ImGui.SetNextWindowSize(new Vector2(520, 0), ImGuiCond.Appearing);
        if (!ImGui.BeginPopupModal(
                GenericDeletePopupId,
                ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoResize))
        {
            return;
        }

        ImGui.TextColored(Gold, text.Get("删除第三方 ACT 插件", "Delete third-party ACT plugin"));
        ImGui.TextWrapped(text.Get(
            $"确定删除 {genericPluginToDeleteName} 吗？运行中的通用 Host 会先安全停止，插件文件将移入备份目录，以便需要时恢复。",
            $"Delete {genericPluginToDeleteName}? The generic Host will stop safely first, and plugin files will be moved to the backup directory for recovery."));
        if (ImGui.Button(text.Get("确认删除", "Delete"), new Vector2(140, 34)))
        {
            var pluginId = genericPluginToDeleteId;
            ImGui.CloseCurrentPopup();
            genericPluginToDeleteId = null;
            genericPluginToDeleteName = null;
            if (!string.IsNullOrWhiteSpace(pluginId))
            {
                uninstallGenericPlugin(pluginId);
            }
        }
        ImGui.SameLine();
        if (ImGui.Button(text.Get("取消", "Cancel"), new Vector2(120, 34)))
        {
            ImGui.CloseCurrentPopup();
            genericPluginToDeleteId = null;
            genericPluginToDeleteName = null;
        }

        ImGui.EndPopup();
    }

    private bool DrawExtensionEntry(
        IReadOnlyList<InstalledActPlugin> installedPlugins,
        string pluginId,
        string displayName,
        string description,
        out bool enabledChanged)
    {
        var installed = installedPlugins.FirstOrDefault(plugin =>
            string.Equals(plugin.Manifest.Id, pluginId, StringComparison.OrdinalIgnoreCase));
        var changed = false;
        enabledChanged = false;
        ImGui.PushID($"extension-{pluginId}");
        var resourceStatus = getBundledActResourceStatus();
        if (resourceStatus.State != ResourcePackOperationState.Ready)
        {
            ImGui.TextUnformatted(displayName);
            ImGui.SameLine();
            if (resourceStatus.State == ResourcePackOperationState.Downloading)
            {
                ImGui.TextColored(
                    IceBlue,
                    text.Get(
                        $"下载中...{resourceStatus.ProgressPercent}%",
                        $"Downloading...{resourceStatus.ProgressPercent}%"));
                ImGui.SameLine();
                if (ImGuiComponents.IconButton(FontAwesomeIcon.Times))
                {
                    cancelBundledActResourceDownload();
                }
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(text.Get("取消下载", "Cancel download"));
                }
            }
            else if (resourceStatus.State == ResourcePackOperationState.Unavailable)
            {
                ImGui.TextDisabled(text.Get("不可用", "Unavailable"));
                ImGui.SameLine();
                if (ImGuiComponents.IconButton(FontAwesomeIcon.Download))
                {
                    startBundledActResourceDownload();
                }
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(text.Get("下载扩展组件", "Download extension components"));
                }
            }
        }
        else if (installed is null)
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
                enabledChanged = true;
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
            DrawInstalledVersion(installed);
        }

        ImGui.TextDisabled(description);
        ImGui.PopID();
        return changed;
    }

    private void DrawInstalledVersion(InstalledActPlugin plugin)
    {
        ImGui.TextDisabled($"v{plugin.DisplayVersion}");
        if (!plugin.HasVersionMismatch || !ImGui.IsItemHovered())
        {
            return;
        }

        // Keep the signed-off manifest visible for diagnosis without presenting it as the loaded DLL version.
        ImGui.SetTooltip(text.Get(
            $"实际 DLL 版本为 {plugin.DisplayVersion}；安装清单记录为 {plugin.Manifest.Version}。",
            $"The DLL version is {plugin.DisplayVersion}; the install manifest records {plugin.Manifest.Version}."));
    }

    private void DrawCombatQuality()
    {
        ImGui.Spacing();
        if (!ImGui.CollapsingHeader("Combat Quality"))
        {
            return;
        }

        void DrawRow(string label, string value)
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextDisabled(label);
            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(value);
        }

        void DrawSection(string title)
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextColored(IceBlue, title);
            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(string.Empty);
        }

        if (!ImGui.BeginTable(
                "combat-quality-table",
                2,
                ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp))
        {
            return;
        }

        var snapshot = combatQualitySnapshot;
        DrawSection("Combat Engine");
        DrawRow("Raw Damage", snapshot is null
            ? "Unavailable"
            : $"{(snapshot.RawParityExactCount == snapshot.RawParitySampleCount ? "Exact" : "Mismatch")} " +
              $"({snapshot.RawParityExactCount}/{snapshot.RawParitySampleCount})");
        DrawRow("Direct Packets", snapshot is null
            ? "Unavailable"
            : $"{(snapshot.DirectPacketMatched == snapshot.DirectPacketExpected ? "Exact" : "Mismatch")} " +
              $"({snapshot.DirectPacketMatched}/{snapshot.DirectPacketExpected})");
        DrawRow("rDPS Model", snapshot?.ModelIdentifier ?? RaidDpsModelInfo.OwnershipModel);
        DrawRow("Status Engine", snapshot?.StatusEngine ?? RaidDpsModelInfo.StatusEngine);

        var reference = fflogsEstimateService.ReferenceSnapshot;
        DrawSection("FFLogs Reference");
        DrawRow("Region", reference.Region);
        DrawRow("Partition", reference.Partition?.ToString() ?? "Latest");
        DrawRow("Parse Metric", reference.Metric);
        DrawRow(
            "Percentile Data",
            reference.LatestDataUpdatedAt is { } updatedAt
                ? updatedAt.ToLocalTime().ToString("yyyy/MM/dd")
                : "--");

        DrawSection("rDPS Validation");
        DrawRow("Samples", snapshot?.Samples.ToString() ?? "--");
        DrawRow("Mean Δ", snapshot is null ? "--" : snapshot.MeanDelta.ToString("F3"));
        DrawRow("Median Δ", snapshot is null ? "--" : snapshot.MedianDelta.ToString("F3"));
        DrawRow("MAE", snapshot is null ? "--" : snapshot.Mae.ToString("F3"));
        DrawRow("P90 |Δ|", snapshot is null ? "--" : snapshot.P90AbsoluteDelta.ToString("F3"));
        DrawRow("P95 |Δ|", snapshot is null ? "--" : snapshot.P95AbsoluteDelta.ToString("F3"));
        DrawRow("Max |Δ|", snapshot is null ? "--" : snapshot.MaxAbsoluteDelta.ToString("F3"));

        DrawSection("Parity Harness");
        DrawRow(
            "Last Run",
            snapshot is null ? "--" : snapshot.LastRun.ToLocalTime().ToString("yyyy/MM/dd HH:mm"));
        DrawRow("Raw Parity", snapshot is null
            ? "Unavailable"
            : $"{snapshot.RawParityExactCount}/{snapshot.RawParitySampleCount}");
        DrawRow("Direct Packet Match", snapshot is null
            ? "Unavailable"
            : $"{snapshot.DirectPacketMatched}/{snapshot.DirectPacketExpected}");
        DrawRow(
            "Normalization Warnings",
            snapshot?.NormalizationWarnings.ToString() ?? "--");
        ImGui.EndTable();
    }

    private bool DrawCloud()
    {
        var snapshot = cloud.GetSnapshot();
        DrawPageHeader(
            text.Get("云同步", "Cloud Sync"),
            text.Get(
                "配置在本机加密后上传；服务器无法读取内容，也不会自动覆盖本机。",
                "Configuration is encrypted locally before upload. The server cannot read it and never overwrites this PC automatically."));
        ImGui.TextDisabled(text.Get(
            "为执行账号安全和机器封禁，客户端只上传不可逆的 SHA-256 设备指纹，不上传原始机器信息。",
            "For account security and device bans, only an irreversible SHA-256 device fingerprint is uploaded; raw machine identifiers stay local."));

        ImGui.TextColored(
            snapshot.StatusIsError
                ? new Vector4(0.96f, 0.42f, 0.38f, 1)
                : new Vector4(0.45f, 0.88f, 0.62f, 1),
            snapshot.StatusMessage);
        if (snapshot.IsBusy)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(text.Get("处理中…", "Working…"));
        }
        ImGui.Spacing();

        DrawSignedInCloud(snapshot);
        return false;
    }

    private void DrawAuthenticationGate(CloudClientSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(cloudUsername) &&
            !string.IsNullOrWhiteSpace(snapshot.Username))
        {
            cloudUsername = snapshot.Username;
        }

        var available = ImGui.GetContentRegionAvail();
        // At the minimum supported window width, two columns leave the credential
        // fields too narrow to scan safely, so the brand panel becomes a compact banner.
        var stackAuthenticationLayout = available.X < 820;
        if (stackAuthenticationLayout)
        {
            if (BeginAuthenticationPanel(
                    "account-authentication-hero",
                    new Vector2(-1, 142),
                    hero: true,
                    allowScrolling: false))
            {
                DrawAuthenticationHero(compact: true);
            }
            EndAuthenticationPanel();
            ImGui.Spacing();
            if (BeginAuthenticationPanel(
                    "account-authentication-card",
                    new Vector2(-1, -1),
                    hero: false,
                    allowScrolling: true))
            {
                DrawCloudAuthentication(snapshot);
            }
            EndAuthenticationPanel();
            return;
        }

        const float panelGap = 14;
        var heroWidth = Math.Clamp(available.X * 0.37f, 292, 342);
        if (BeginAuthenticationPanel(
                "account-authentication-hero",
                new Vector2(heroWidth, -1),
                hero: true,
                allowScrolling: false))
        {
            DrawAuthenticationHero(compact: false);
        }
        EndAuthenticationPanel();
        ImGui.SameLine(0, panelGap);
        if (BeginAuthenticationPanel(
                "account-authentication-card",
                new Vector2(-1, -1),
                hero: false,
                allowScrolling: true))
        {
            DrawCloudAuthentication(snapshot);
        }
        EndAuthenticationPanel();
    }

    private void DrawCloudAuthentication(CloudClientSnapshot snapshot)
    {
        var busy = snapshot.IsBusy;
        ImGui.TextColored(Gold, text.Get("账号入口", "ACCOUNT ACCESS"));
        DrawAuthenticationMutedText(text.Get(
            "验证账号后进入 DACT 控制中心",
            "Verify your account to enter the DACT control center"));
        ImGui.Spacing();

        var authenticationPages = new[]
        {
            text.Get("登录", "Login"),
            text.Get("注册", "Register"),
            text.Get("重置密码", "Reset password"),
        };
        var selectedAuthenticationPage = BrandedWindowChrome.DrawNavigationRail(
            "account-authentication-navigation",
            authenticationPages,
            (int)cloudAuthenticationPage,
            height: 34);
        cloudAuthenticationPage = (CloudAuthenticationPage)selectedAuthenticationPage;
        ImGui.Spacing();

        var (pageTitle, pageDescription) = cloudAuthenticationPage switch
        {
            CloudAuthenticationPage.Register => (
                text.Get("创建 DACT 账号", "Create a DACT account"),
                text.Get("使用一次性激活码完成注册并绑定本机。", "Register with a one-time activation key and bind this PC.")),
            CloudAuthenticationPage.ResetPassword => (
                text.Get("重设账号密码", "Reset account password"),
                text.Get("使用管理员重置码和恢复密钥保护原有云配置。", "Use an administrator reset code and recovery key to protect existing cloud data.")),
            _ => (
                text.Get("欢迎回来", "Welcome back"),
                text.Get("输入账号和密码，继续使用 DACT。", "Enter your username and password to continue.")),
        };
        ImGui.TextColored(IceBlue, pageTitle);
        DrawAuthenticationMutedText(pageDescription);
        ImGui.Spacing();
        DrawAuthenticationStatus(snapshot);
        ImGui.Spacing();

        DrawAuthenticationInput(
            text.Get("用户名", "Username"),
            text.Get("请输入账号名", "Enter your username"),
            "cloud-username",
            ref cloudUsername,
            32);

        switch (cloudAuthenticationPage)
        {
            case CloudAuthenticationPage.Login:
                DrawCloudLoginForm(busy);
                break;
            case CloudAuthenticationPage.Register:
                DrawCloudRegistrationForm(busy);
                break;
            case CloudAuthenticationPage.ResetPassword:
                DrawCloudPasswordResetForm(busy, snapshot.HasSavedRecoveryKey);
                break;
        }
    }

    private void DrawCloudLoginForm(bool busy)
    {
        DrawAuthenticationInput(
            text.Get("密码", "Password"),
            text.Get("请输入密码", "Enter your password"),
            "cloud-password",
            ref cloudPassword,
            128,
            ImGuiInputTextFlags.Password);
        if (ImGui.CollapsingHeader(text.Get(
                "管理员改密后的恢复选项###cloud-login-recovery-options",
                "Recovery after an administrator reset###cloud-login-recovery-options")))
        {
            DrawAuthenticationInput(
                text.Get("恢复密钥", "Recovery key"),
                text.Get("通常无需填写", "Normally not required"),
                "cloud-login-recovery-key",
                ref cloudRecoveryKey,
                96,
                ImGuiInputTextFlags.Password);
            DrawAuthenticationMutedText(text.Get(
                "仅当管理员直接重置过密码，且本机没有保存账号密钥时才需要。",
                "Only required after a direct administrator reset when this PC has no saved account key."));
        }
        DrawAuthenticationPersistenceOption();
        if (DrawAuthenticationPrimaryButton(text.Get("登录 DACT", "Sign in to DACT"), !busy))
        {
            cloud.Login(new CloudLoginRequest(
                cloudUsername,
                cloudPassword,
                cloudRecoveryKey,
                cloudRememberLogin));
            cloudPassword = string.Empty;
            cloudRecoveryKey = string.Empty;
        }
    }

    private void DrawCloudRegistrationForm(bool busy)
    {
        DrawAuthenticationInput(
            text.Get("密码", "Password"),
            text.Get("至少 10 位", "At least 10 characters"),
            "cloud-register-password",
            ref cloudPassword,
            128,
            ImGuiInputTextFlags.Password);
        DrawAuthenticationInput(
            text.Get("确认密码", "Confirm password"),
            text.Get("再次输入密码", "Enter your password again"),
            "cloud-register-confirm",
            ref cloudPasswordConfirmation,
            128,
            ImGuiInputTextFlags.Password);
        DrawAuthenticationInput(
            text.Get("一次性激活码", "One-time activation key"),
            text.Get("输入管理员或好友发放的激活码", "Enter an activation key from an administrator or friend"),
            "cloud-activation-key",
            ref cloudActivationKey,
            96);
        var passwordsMatch = cloudPassword.Length >= 10 &&
                             cloudPassword == cloudPasswordConfirmation;
        DrawAuthenticationPersistenceOption();
        if (DrawAuthenticationPrimaryButton(
                text.Get("注册并绑定本机", "Register and bind this PC"),
                !busy && passwordsMatch))
        {
            cloud.Register(new CloudRegistrationRequest(
                cloudUsername,
                cloudPassword,
                cloudActivationKey,
                cloudRememberLogin));
            ClearCloudSecrets();
        }
        if (!string.IsNullOrEmpty(cloudPasswordConfirmation) && !passwordsMatch)
        {
            ImGui.TextColored(
                new Vector4(0.96f, 0.42f, 0.38f, 1),
                text.Get("两次密码不一致，或密码不足 10 位。", "Passwords differ or contain fewer than 10 characters."));
        }
        DrawAuthenticationMutedText(text.Get(
            "注册必须同时提供用户名、密码和有效的一次性激活码。",
            "Registration requires a username, password, and valid one-time activation key."));
    }

    private void DrawCloudPasswordResetForm(bool busy, bool hasLocalRecoveryKey)
    {
        DrawAuthenticationInput(
            text.Get("管理员密码重置码", "Administrator reset code"),
            text.Get("输入管理员提供的一次性重置码", "Enter the one-time code from an administrator"),
            "cloud-reset-code",
            ref cloudResetCode,
            96);
        DrawAuthenticationInput(
            text.Get("新密码", "New password"),
            text.Get("至少 10 位", "At least 10 characters"),
            "cloud-new-password",
            ref cloudPassword,
            128,
            ImGuiInputTextFlags.Password);
        DrawAuthenticationInput(
            text.Get("确认新密码", "Confirm new password"),
            text.Get("再次输入新密码", "Enter the new password again"),
            "cloud-new-password-confirm",
            ref cloudPasswordConfirmation,
            128,
            ImGuiInputTextFlags.Password);
        DrawAuthenticationInput(
            text.Get("恢复密钥", "Recovery key"),
            hasLocalRecoveryKey
                ? text.Get("本机已保存，可留空", "Saved on this PC; may be left blank")
                : text.Get("输入注册时保存的 dact1_ 密钥", "Enter the dact1_ key saved at registration"),
            "cloud-recovery-key",
            ref cloudRecoveryKey,
            96,
            ImGuiInputTextFlags.Password);
        DrawAuthenticationMutedText(hasLocalRecoveryKey
            ? text.Get(
                "本机已保存账号密钥；恢复密钥可留空。",
                "This PC has the account key, so the recovery key can remain empty.")
            : text.Get(
                "必须输入注册时保存的 dact1_ 恢复密钥，服务器无法代为找回。",
                "Enter the dact1_ recovery key saved during registration; the server cannot recover it."));

        var passwordsMatch = cloudPassword.Length >= 10 &&
                             cloudPassword == cloudPasswordConfirmation;
        DrawAuthenticationPersistenceOption();
        if (DrawAuthenticationPrimaryButton(
                text.Get("确认重置密码", "Reset password"),
                !busy && passwordsMatch &&
                (hasLocalRecoveryKey || !string.IsNullOrWhiteSpace(cloudRecoveryKey))))
        {
            cloud.ResetPassword(new CloudPasswordResetRequest(
                cloudUsername,
                cloudResetCode,
                cloudPassword,
                cloudRecoveryKey,
                cloudRememberLogin));
            ClearCloudSecrets();
        }
    }

    private void DrawAuthenticationHero(bool compact)
    {
        var wrap = logoTexture.GetWrapOrEmpty();
        if (compact)
        {
            ImGui.Image(wrap.Handle, new Vector2(48));
            ImGui.SameLine();
            ImGui.BeginGroup();
            ImGui.TextColored(Gold, "DACT");
            ImGui.TextColored(IceBlue, text.Get("账号与云服务", "ACCOUNT & CLOUD"));
            ImGui.EndGroup();
            ImGui.Spacing();
            DrawAuthenticationMutedText(text.Get(
                "账号验证后启用解析器、悬浮窗与扩展 · Windows 加密自动登录 · 配置本地加密后上传",
                "Account-gated runtime · Windows-protected auto-login · Locally encrypted cloud backups"));
            return;
        }

        const float logoSize = 82;
        var logoOffset = Math.Max(0, (ImGui.GetContentRegionAvail().X - logoSize) * 0.5f);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + logoOffset);
        ImGui.Image(wrap.Handle, new Vector2(logoSize));
        ImGui.Spacing();
        DrawCenteredAuthenticationText("DACT", Gold);
        DrawCenteredAuthenticationText(text.Get("账号与云服务", "ACCOUNT & CLOUD"), IceBlue);
        ImGui.Dummy(new Vector2(0, 16));
        DrawAuthenticationMutedText(text.Get(
            "连接你的 DACT 工作区，在一处管理解析器、悬浮窗、扩展与加密配置。",
            "Connect your DACT workspace and manage parsers, overlays, extensions, and encrypted settings in one place."));
        ImGui.Dummy(new Vector2(0, 18));
        DrawAuthenticationFeature(
            text.Get("账号验证后启用", "Enabled after verification"),
            text.Get("未登录时 DACT 功能保持关闭。", "DACT features stay off while signed out."));
        DrawAuthenticationFeature(
            text.Get("安全自动登录", "Protected auto-login"),
            text.Get("登录状态由 Windows 当前用户加密。", "Windows protects the session for the current user."));
        DrawAuthenticationFeature(
            text.Get("加密云同步", "Encrypted cloud sync"),
            text.Get("配置在本地加密后才会上传。", "Settings are encrypted locally before upload."));
    }

    private static bool BeginAuthenticationPanel(
        string id,
        Vector2 size,
        bool hero,
        bool allowScrolling)
    {
        ImGui.PushStyleColor(
            ImGuiCol.ChildBg,
            hero ? AuthenticationHeroBackground : AuthenticationCardBackground);
        ImGui.PushStyleColor(
            ImGuiCol.Border,
            hero
                ? new Vector4(Gold.X, Gold.Y, Gold.Z, 0.72f)
                : AuthenticationBorder);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 11);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(22, 20));
        var flags = allowScrolling
            ? ImGuiWindowFlags.None
            : ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        return ImGui.BeginChild(id, size, true, flags);
    }

    private static void EndAuthenticationPanel()
    {
        ImGui.EndChild();
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(2);
    }

    private void DrawAuthenticationFeature(string title, string description)
    {
        ImGui.TextColored(IceBlue, "◆");
        ImGui.SameLine();
        ImGui.BeginGroup();
        ImGui.TextColored(new Vector4(0.90f, 0.94f, 0.98f, 1), title);
        DrawAuthenticationMutedText(description);
        ImGui.EndGroup();
        ImGui.Dummy(new Vector2(0, 9));
    }

    private static void DrawCenteredAuthenticationText(string value, Vector4 color)
    {
        var offset = Math.Max(0, (ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(value).X) * 0.5f);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + offset);
        ImGui.TextColored(color, value);
    }

    private static void DrawAuthenticationMutedText(string value)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, AuthenticationMutedText);
        ImGui.TextWrapped(value);
        ImGui.PopStyleColor();
    }

    private void DrawAuthenticationStatus(CloudClientSnapshot snapshot)
    {
        var statusMessage = snapshot.IsBusy
            ? $"{snapshot.StatusMessage}  {text.Get("处理中…", "Working…")}"
            : snapshot.StatusMessage;
        if (string.IsNullOrWhiteSpace(statusMessage))
        {
            return;
        }

        var statusTextColor = snapshot.StatusIsError
            ? new Vector4(1, 0.52f, 0.48f, 1)
            : new Vector4(0.58f, 0.86f, 1, 1);
        var statusBackground = snapshot.StatusIsError
            ? new Vector4(0.30f, 0.075f, 0.075f, 0.70f)
            : new Vector4(0.07f, 0.18f, 0.24f, 0.78f);
        var wrapWidth = Math.Max(1, ImGui.GetContentRegionAvail().X - 42);
        var statusHeight = Math.Max(40, ImGui.CalcTextSize(statusMessage, false, wrapWidth).Y + 16);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, statusBackground);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 6);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10, 8));
        if (ImGui.BeginChild(
                "account-authentication-status",
                new Vector2(-1, statusHeight),
                false,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            ImGui.TextColored(statusTextColor, snapshot.StatusIsError ? "!" : "●");
            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Text, statusTextColor);
            ImGui.TextWrapped(statusMessage);
            ImGui.PopStyleColor();
        }
        ImGui.EndChild();
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor();
    }

    private void DrawAuthenticationInput(
        string label,
        string hint,
        string id,
        ref string value,
        int maxLength,
        ImGuiInputTextFlags flags = ImGuiInputTextFlags.None)
    {
        ImGui.TextDisabled(label);
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint($"##{id}", hint, ref value, maxLength, flags);
    }

    private void DrawAuthenticationPersistenceOption()
    {
        ImGui.Spacing();
        ImGui.Checkbox(text.Get("自动登录", "Sign in automatically"), ref cloudRememberLogin);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(text.Get(
                "登录状态使用 Windows 当前用户加密，并在重启游戏或电脑后恢复。",
                "Windows protects the saved session for this user and restores it after a game or PC restart."));
        }
    }

    private static bool DrawAuthenticationPrimaryButton(string label, bool enabled)
    {
        ImGui.BeginDisabled(!enabled);
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.10f, 0.36f, 0.50f, 1));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.14f, 0.48f, 0.64f, 1));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.18f, 0.56f, 0.72f, 1));
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.94f, 0.98f, 1, 1));
        var clicked = ImGui.Button(label, new Vector2(-1, 38));
        ImGui.PopStyleColor(4);
        ImGui.EndDisabled();
        return enabled && clicked;
    }

    private void DrawSignedInCloud(CloudClientSnapshot snapshot)
    {
        if (!snapshot.IsBusy && ImGui.Button(text.Get("刷新版本", "Refresh versions")))
        {
            cloud.Refresh();
        }

        if (!string.IsNullOrWhiteSpace(snapshot.RecoveryKeyToSave))
        {
            ImGui.Spacing();
            ImGui.TextColored(
                new Vector4(1f, 0.72f, 0.25f, 1),
                text.Get("恢复密钥只显示这一次，请离线保存：", "Save this recovery key offline; it is shown only once:"));
            var visibleRecoveryKey = snapshot.RecoveryKeyToSave;
            ImGui.SetNextItemWidth(-90);
            ImGui.InputText(
                "###cloud-created-recovery-key",
                ref visibleRecoveryKey,
                96,
                ImGuiInputTextFlags.ReadOnly);
            ImGui.SameLine();
            if (ImGui.Button(text.Get("复制", "Copy")))
            {
                ImGui.SetClipboardText(snapshot.RecoveryKeyToSave);
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextColored(Gold, text.Get("好友邀请", "Friend invitations"));
        if (snapshot.Invitations is { } invitations)
        {
            ImGui.TextDisabled(text.Get(
                $"已生成 {invitations.Used}/{invitations.Quota}，剩余 {invitations.Remaining} 个名额。",
                $"Generated {invitations.Used}/{invitations.Quota}; {invitations.Remaining} slots remain."));
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint(
                "###cloud-invitee-contact",
                text.Get("受邀好友的游戏 ID 或 QQ ID", "Invitee game ID or QQ ID"),
                ref cloudInviteeContact,
                81);
            var normalizedInviteeContact = cloudInviteeContact.Trim();
            var inviteeContactValid = normalizedInviteeContact.Length is >= 2 and <= 80;
            ImGui.BeginDisabled(snapshot.IsBusy || invitations.Remaining <= 0 || !inviteeContactValid);
            if (!snapshot.IsBusy && invitations.Remaining > 0 &&
                ImGui.Button(text.Get("生成好友激活码", "Create friend activation key")))
            {
                cloud.CreateInvitation(normalizedInviteeContact);
            }
            ImGui.EndDisabled();
            if (!inviteeContactValid && !string.IsNullOrWhiteSpace(cloudInviteeContact))
            {
                ImGui.TextDisabled(text.Get(
                    "游戏 ID 或 QQ ID 长度需要为 2 到 80 个字符。",
                    "The game ID or QQ ID must contain 2 to 80 characters."));
            }
            if (!string.IsNullOrWhiteSpace(snapshot.InvitationKeyToShare))
            {
                var invitationKey = snapshot.InvitationKeyToShare;
                ImGui.SetNextItemWidth(-90);
                ImGui.InputText(
                    "###cloud-created-invitation-key",
                    ref invitationKey,
                    96,
                    ImGuiInputTextFlags.ReadOnly);
                ImGui.SameLine();
                if (ImGui.Button(text.Get("复制###copy-invitation", "Copy###copy-invitation")))
                {
                    ImGui.SetClipboardText(snapshot.InvitationKeyToShare);
                }
                ImGui.TextDisabled(text.Get(
                    "完整激活码只显示这一次，请现在发给好友。",
                    "The complete key is shown once; share it now."));
            }
            foreach (var invitation in invitations.Invitations)
            {
                ImGui.BulletText(
                    $"{invitation.Name} · {invitation.InviteeContact ?? "—"} · " +
                    $"…{invitation.CodeHint} · {FormatInvitationStatus(invitation.Status)}");
            }
        }
        ImGui.BeginDisabled();
        ImGui.Button(text.Get("爱发电支持（即将开放）", "Buy me a coffee (coming soon)"));
        ImGui.EndDisabled();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextColored(Gold, text.Get("云端版本", "Cloud versions"));
        ImGui.TextDisabled(text.Get(
            "最多保留最近 10 个密文版本。上传会短暂停止解析器与扩展，完成后自动恢复。",
            "The latest 10 encrypted versions are retained. Upload briefly stops parsers and extensions, then restores them."));
        if (!snapshot.IsBusy && ImGui.Button(text.Get("上传当前配置", "Upload current configuration")))
        {
            cloud.Upload();
        }

        if (snapshot.Backups.Count == 0)
        {
            ImGui.TextDisabled(text.Get("还没有云端备份。", "No cloud backups yet."));
        }
        else
        {
            if (snapshot.Backups.All(backup => backup.Id != selectedCloudBackupId))
            {
                selectedCloudBackupId = snapshot.Backups[0].Id;
                cloudPreviewRequestedBackupId = null;
                confirmCloudRestore = false;
            }
            foreach (var backup in snapshot.Backups)
            {
                var selected = backup.Id == selectedCloudBackupId;
                var label = $"{backup.CreatedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}  " +
                            $"{FormatCloudBytes(backup.SizeBytes)}###{backup.Id}";
                if (ImGui.Selectable(label, selected))
                {
                    selectedCloudBackupId = backup.Id;
                    cloudPreviewRequestedBackupId = null;
                    confirmCloudRestore = false;
                }
            }
        }

        var canUseSelection = !snapshot.IsBusy && !string.IsNullOrWhiteSpace(selectedCloudBackupId);
        if (canUseSelection && ImGui.Button(text.Get("预览恢复内容", "Preview restore")))
        {
            cloudPreviewRequestedBackupId = selectedCloudBackupId;
            confirmCloudRestore = false;
            cloud.PreviewRestore(selectedCloudBackupId!);
        }

        if (snapshot.RestorePreview is { } preview &&
            cloudPreviewRequestedBackupId == selectedCloudBackupId)
        {
            ImGui.TextWrapped(text.Get(
                $"将新增 {preview.AddedFiles}、修改 {preview.ChangedFiles}、删除 {preview.RemovedFiles} 个文件；" +
                $"共 {preview.FileCount} 个文件。新电脑的日志目录和 ACT 插件目录会保留。",
                $"Adds {preview.AddedFiles}, changes {preview.ChangedFiles}, and removes {preview.RemovedFiles} files; " +
                $"{preview.FileCount} files total. This PC's log and ACT plugin paths are preserved."));
            foreach (var scope in preview.Scopes)
            {
                ImGui.BulletText(
                    $"{scope.RelativePath}: +{scope.AddedFiles} ~{scope.ChangedFiles} -{scope.RemovedFiles} ={scope.UnchangedFiles}");
            }
            if (!confirmCloudRestore)
            {
                if (!snapshot.IsBusy && ImGui.Button(text.Get("恢复这个版本", "Restore this version")))
                {
                    confirmCloudRestore = true;
                }
            }
            else
            {
                ImGui.TextColored(
                    new Vector4(1f, 0.56f, 0.30f, 1),
                    text.Get("将覆盖白名单内的本机配置，完成后必须重载 DACT。", "Whitelisted local settings will be replaced; DACT must be reloaded afterward."));
                if (!snapshot.IsBusy && ImGui.Button(text.Get("确认覆盖", "Confirm restore")))
                {
                    cloud.Restore(selectedCloudBackupId!);
                    confirmCloudRestore = false;
                }
                ImGui.SameLine();
                if (ImGui.Button(text.Get("取消", "Cancel")))
                {
                    confirmCloudRestore = false;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(snapshot.LastRollbackPath))
        {
            ImGui.Spacing();
            if (!confirmCloudRollback)
            {
                if (!snapshot.IsBusy && ImGui.Button(text.Get("撤销上次恢复", "Undo last restore")))
                {
                    confirmCloudRollback = true;
                }
            }
            else
            {
                if (!snapshot.IsBusy && ImGui.Button(text.Get("确认回滚", "Confirm rollback")))
                {
                    cloud.Rollback();
                    confirmCloudRollback = false;
                }
                ImGui.SameLine();
                if (ImGui.Button(text.Get("取消###cloud-rollback", "Cancel###cloud-rollback")))
                {
                    confirmCloudRollback = false;
                }
            }
        }

    }

    private void ClearCloudSecrets()
    {
        cloudPassword = string.Empty;
        cloudPasswordConfirmation = string.Empty;
        cloudActivationKey = string.Empty;
        cloudResetCode = string.Empty;
        cloudRecoveryKey = string.Empty;
    }

    private static string FormatCloudBytes(long bytes)
        => bytes >= 1024 * 1024
            ? $"{bytes / (1024d * 1024d):0.0} MiB"
            : $"{Math.Max(1, bytes / 1024d):0.0} KiB";

    private string FormatInvitationStatus(string status)
        => status switch
        {
            "available" => text.Get("可用", "Available"),
            "used" => text.Get("已使用", "Used"),
            "revoked" => text.Get("已撤销", "Revoked"),
            "expired" => text.Get("已过期", "Expired"),
            _ => status,
        };

    private void DrawAccountSettings()
    {
        var snapshot = cloud.GetSnapshot();
        ImGui.TextColored(Gold, text.Get("账号", "Account"));
        ImGui.TextUnformatted(snapshot.Username ?? string.Empty);
        if (snapshot.SessionExpiresAt is { } expiresAt)
        {
            ImGui.TextDisabled(text.Get(
                $"登录有效期至 {expiresAt.LocalDateTime:yyyy-MM-dd HH:mm}",
                $"Session expires {expiresAt.LocalDateTime:yyyy-MM-dd HH:mm}"));
        }
        if (!snapshot.IsBusy && ImGui.Button(text.Get("退出登录", "Sign out")))
        {
            cloud.Logout();
            ClearCloudSecrets();
        }

        if (ImGui.CollapsingHeader(text.Get(
                "使用管理员重置码修改密码",
                "Change password with an administrator reset code")))
        {
            cloudUsername = snapshot.Username ?? string.Empty;
            ImGui.Checkbox(
                text.Get("重置后自动登录", "Sign in automatically after reset"),
                ref cloudRememberLogin);
            DrawCloudPasswordResetForm(snapshot.IsBusy, hasLocalRecoveryKey: true);
        }
    }

    private bool DrawDiagnostics()
    {
        DrawPageHeader(
            text.Get("设置&账号", "Settings & Account"),
            text.Get(
                "运行状态、账号、语言、快捷按钮与恢复选项。",
                "Runtime status, account, language, quick-button, and recovery options."));

        DrawAccountSettings();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

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

        DrawCombatLogDirectorySettings();

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
        if (configuration.DebugMode)
        {
            changed |= Checkbox(
                text.Get("记录 FFLogs parity 诊断", "Record FFLogs parity diagnostics"),
                configuration.EnableFflogsParityRecorder,
                value => configuration.EnableFflogsParityRecorder = value);
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(text.Get(
                    "仅在手动启用后记录；普通 Debug 不会自动启动 parity 工具。",
                    "Records only when explicitly enabled; ordinary Debug does not start the parity tool."));
            }
            DrawCombatQuality();
        }
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

        DrawHistoryLimitEditor();

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

    private void DrawCombatLogDirectorySettings()
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextColored(Gold, text.Get("FFLogs 上传日志", "FFLogs upload logs"));
        ImGui.TextDisabled(text.Get("当前路径", "Current path"));
        ImGui.TextWrapped(getCombatLogDirectory());

        if (ImGui.Button(text.Get("更改目录...", "Change directory...")))
        {
            selectCombatLogDirectory(ReportCombatLogDirectoryChange);
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(text.Get(
                "只更改之后写入的原始 Network 日志；旧日志不会移动或删除。解析器正在运行时会自动重启，战斗中切换可能截断当前战斗。",
                "Changes only future raw Network log writes; existing logs are not moved or deleted. A running parser restarts automatically, which may split the current encounter."));
        }
        ImGui.SameLine();
        if (ImGui.Button(text.Get("恢复默认", "Restore default")))
        {
            resetCombatLogDirectory(ReportCombatLogDirectoryChange);
        }

        var feedback = Volatile.Read(ref combatLogDirectoryChangeFeedback);
        if (feedback is not null)
        {
            ImGui.TextColored(
                feedback.IsError
                    ? new Vector4(0.95f, 0.45f, 0.40f, 1)
                    : IceBlue,
                feedback.Message);
        }

        ImGui.Spacing();
    }

    private void ReportCombatLogDirectoryChange(bool success, string message)
        => Volatile.Write(
            ref combatLogDirectoryChangeFeedback,
            new CombatLogDirectoryFeedback(message, !success));

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
        var connectionChanged = false;
        var settings = configuration.GetOverlayWindowSettings(name);
        ImGui.PushID(name);
        if (!string.IsNullOrWhiteSpace(settings.SourceUrl))
        {
            var connectionDetail = string.IsNullOrWhiteSpace(settings.ConnectionStateDetail)
                ? text.Get("打开后自动检测连接", "Connection will be detected when opened")
                : settings.ConnectionStateDetail;
            var connectionColor = settings.ConnectionState switch
            {
                OverlayConnectionState.Connected => new Vector4(0.45f, 0.88f, 0.62f, 1),
                OverlayConnectionState.Failed => new Vector4(0.96f, 0.42f, 0.38f, 1),
                _ => IceBlue,
            };
            ImGui.TextColored(connectionColor, connectionDetail);
            if (ImGui.TreeNode(text.Get("连接高级设置", "Advanced connection settings")))
            {
                var selectedMode = settings.ConnectionMode;
                if (ImGui.BeginCombo(
                        text.Get("连接方式", "Connection mode"),
                        GetOverlayConnectionModeLabel(selectedMode)))
                {
                    foreach (var mode in Enum.GetValues<OverlayConnectionMode>())
                    {
                        var selected = mode == selectedMode;
                        if (ImGui.Selectable(GetOverlayConnectionModeLabel(mode), selected))
                        {
                            settings.ConnectionMode = mode;
                            settings.ResetConnectionDetection();
                            connectionChanged = true;
                            changed = true;
                        }
                    }
                    ImGui.EndCombo();
                }

                ImGui.TextDisabled(text.Get(
                    "默认自动检测；手动模式只用于检测失败时微调。",
                    "Automatic detection is the default. Manual modes are only for troubleshooting."));
                if (ImGui.Button(text.Get("重新检测", "Detect again")))
                {
                    settings.ConnectionMode = OverlayConnectionMode.Auto;
                    settings.ResetConnectionDetection();
                    connectionChanged = true;
                    changed = true;
                }
                ImGui.TreePop();
            }
        }

        ImGui.TextDisabled(text.Get("位置、缩放、穿透与锁定", "Position, scale, click-through, and lock"));
        if (ImGui.Button(settings.IsEditing
                ? text.Get("完成位置编辑", "Finish position editing")
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
        changed |= Checkbox(text.Get("鼠标穿透", "Click-through"), settings.IsClickThrough, settings.SetClickThrough);
        ImGui.SameLine();
        changed |= Checkbox(text.Get("锁定", "Locked"), settings.IsLocked, settings.SetLocked);
        changed |= SliderFloat(text.Get("页面缩放", "Page zoom"), settings.ZoomFactor, 0.5f, 2, value => settings.ZoomFactor = value);
        ImGui.TextDisabled(settings.IsEditing
            ? text.Get(
                "单击可操作网页；按住并拖动可移动，拖动右下角斜纹可缩放。",
                "Click to use the page; hold and drag to move, or drag the striped bottom-right grip to resize.")
            : text.Get(
                "位置编辑时会暂时关闭穿透与锁定；完成后会恢复。",
                "Position editing temporarily disables click-through and locking; finishing restores them."));
        ImGui.PopID();

        if (changed)
        {
            applyOverlayWindowSettings(name);
        }
        if (connectionChanged && settings.IsVisible)
        {
            openHtmlOverlay(name);
        }
        return changed;
    }

    private string GetOverlayConnectionModeLabel(OverlayConnectionMode mode)
        => mode switch
        {
            OverlayConnectionMode.Auto => text.Get("自动检测（推荐）", "Automatic (recommended)"),
            OverlayConnectionMode.OverlayPlugin => text.Get("现代悬浮窗", "Modern overlay"),
            OverlayConnectionMode.ActWebSocket => text.Get("旧版 ACTWS", "Legacy ACTWS"),
            OverlayConnectionMode.Original => text.Get("原样打开（高级）", "Open URL unchanged (advanced)"),
            _ => mode.ToString(),
        };

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
        ImGui.TextColored(Gold, text.Get("FFLogs DPS Parse 预估", "FFLogs DPS Parse estimate"));
        ImGui.TextWrapped(text.Get(
            "根据本场实际 DPS 与当前 FFLogs 同职业、同副本、同分区的 DPS 分布估算 Parse 数字与颜色。缓存缺失时显示“--”，不会猜测百分位。",
            "Estimate the Parse number and color from this encounter's actual DPS and the current FFLogs DPS distribution for the same job, encounter, and partition. Missing cache data is shown as '--'; no percentile is guessed."));
        var reference = fflogsEstimateService.ReferenceSnapshot;
        var referenceDate = reference.LatestDataUpdatedAt is { } updatedAt
            ? updatedAt.ToLocalTime().ToString("yyyy/MM/dd")
            : "--";
        var partitionLabel = reference.Partition?.ToString() ?? text.Get("最新", "Latest");
        ImGui.TextDisabled(text.Get(
            $"区域 {reference.Region} · 分区 {partitionLabel} · 指标 {reference.Metric} · FFLogs 数据更新于：{referenceDate}",
            $"Region {reference.Region} · Partition {partitionLabel} · Metric {reference.Metric} · FFLogs data updated: {referenceDate}"));

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
                "榜单区域跟随上方游戏区域设置；国服使用 CN 分区，国际服使用 FFLogs 最新全球分区。",
                "The ranking population follows the game-region setting above: CN partition for China, latest global partition for Global."));
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

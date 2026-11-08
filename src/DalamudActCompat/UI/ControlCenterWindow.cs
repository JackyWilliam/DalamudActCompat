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
using DalamudActCompat.Infrastructure.Diagnostics;
using DalamudActCompat.Infrastructure.Logging;
using DalamudActCompat.Infrastructure.Processes;
using DalamudActCompat.Infrastructure.Resources;
using DalamudActCompat.Meter;
using DalamudActCompat.Parser;
using DalamudActCompat.Plugin;
using DalamudActCompat.Quality;
using DalamudActCompat.UI.Kit;
using System.Numerics;

namespace DalamudActCompat.UI;

public sealed partial class ControlCenterWindow : Window
{
    internal OverlayHotkeyEditor? HotkeyEditor { get; set; }
    internal Action<int>? PreviewFriendNotificationSound { get; set; }

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
        History,
        Overlays,
        Extensions,
        Friends,
        Ai,
        Cloud,
        Diagnostics,
    }

    private enum CloudAuthenticationPage
    {
        Login,
        Register,
        ResetPassword,
    }

    private sealed record CombatLogDirectoryFeedback(string Message, bool IsError);

    internal static Vector4 Navy => DactTheme.Tone(new Vector4(0.035f, 0.048f, 0.068f, 1), DactTheme.Palette.Surface);
    private static Vector4 NavyRaised => DactTheme.Tone(new Vector4(0.070f, 0.095f, 0.125f, 1), DactTheme.Palette.Raised);
    private static Vector4 NavyHover => DactTheme.Tone(new Vector4(0.105f, 0.145f, 0.185f, 1), DactTheme.Palette.Hover);
    internal static Vector4 Gold => DactTheme.Tone(new Vector4(0.78f, 0.66f, 0.36f, 1), DactTheme.Palette.Gold);
    internal static Vector4 IceBlue => DactTheme.Tone(new Vector4(0.42f, 0.78f, 0.96f, 1), DactTheme.Palette.Accent);
    private static Vector4 AuthenticationHeroBackground => DactTheme.Tone(new Vector4(0.045f, 0.075f, 0.105f, 1), DactTheme.Palette.Surface);
    private static Vector4 AuthenticationCardBackground => DactTheme.Tone(new Vector4(0.055f, 0.070f, 0.095f, 1), DactTheme.Palette.Raised);
    private static Vector4 AuthenticationBorder => DactTheme.Tone(new Vector4(0.25f, 0.48f, 0.60f, 0.62f), DactTheme.Palette.Border);
    private static Vector4 AuthenticationMutedText => DactTheme.Tone(new Vector4(0.62f, 0.68f, 0.74f, 1), DactTheme.Palette.Muted);
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
    private const string CloudQuickPopupId = "云同步状态###DalamudActCompatCloudQuickStatus";
    private const string ActivationQqGroup = "1098561701";
    private const string ActivationDiscordUrl = "https://discord.gg/mZHFwXe7Gk";
    private const string AfdianSupportUrl = "https://ifdian.net/a/raynor";

    private readonly PluginConfiguration configuration;
    private readonly SkinDiscoveries skinDiscoveries = new();
    private readonly SkinSettingsPanel skinSettings = new();
    private bool resetSettingsScroll;
    private string? discoveredSkin;
    private long discoveryNoticeUntil;
    private readonly WindowDragController headerDrag = new();
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
    private readonly Func<DiagnosticReportExport> exportDiagnosticReport;
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
    private ParserStatus parserStatus;
    private string? selectedCreatedOverlay;
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
    private CloudPasswordResetMethod cloudPasswordResetMethod = CloudPasswordResetMethod.AdministratorCode;
    private string? cloudAuthenticationPreviousStatus;
    private string? selectedCloudBackupId;
    private string? cloudPreviewRequestedBackupId;
    private bool confirmCloudRestore;
    private bool confirmCloudRollback;
    private bool cloudQuickPopupRequested;

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
        Func<DiagnosticReportExport> exportDiagnosticReport,
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
        this.exportDiagnosticReport = exportDiagnosticReport;
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
        // Reopened during the close fade: the window never closed as far as Dalamud knows, so it
        // sends neither OnClose nor OnOpen, but the player did close it and open it again.
        if (IsOpen && visibilityTransition == VisibilityTransition.Closing)
        {
            EndSplashOnClose();
            OnOpen();
        }

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

        skinSettings.Close();
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
        headerDrag.PrepareNextWindow(!locateOnNextDraw);
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
        // The 1.0 header, sidebar and page area run to the window edges and pad themselves.
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        // Indigo of the DACT logo (#35406B); other skins substitute their own border.
        DactTheme.PushStyleColor(ImGuiCol.Border, new Vector4(.208f, .251f, .420f, 1));
        // The window still resizes from its corner, but the design has no visible grip.
        ImGui.PushStyleColor(ImGuiCol.ResizeGrip, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.ResizeGripHovered, DactTheme.Palette.Muted with { W = .25f });
        ImGui.PushStyleColor(ImGuiCol.ResizeGripActive, DactTheme.Palette.Muted with { W = .4f });
        visibilityStylePushed = true;
        Flags = DactTheme.WindowFlags(Flags);
    }

    public override void PostDraw()
    {
        if (visibilityStylePushed)
        {
            ImGui.PopStyleColor(4);
            ImGui.PopStyleVar(4);
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
            var cloudSnapshot = cloud.GetSnapshot();
            BeginSplashIfFirstOpen();
            // AI replies keep arriving while another page is open.
            PumpAiResults();
            // Only the control centre carries the plate ornaments; they stay anchored to this
            // window (not the friends drawer) and follow its fade through the style alpha.
            if (DactTheme.Palette.Ultimate is { } ultimate)
                UltimateSkins.DrawOrnaments(ImGui.GetBackgroundDrawList(), ImGui.GetWindowPos(), ImGui.GetWindowPos() + ImGui.GetWindowSize(),
                    ultimate, Math.Max(.75f, ImGui.GetFontSize() / 17f), ImGui.GetTime(), DactTheme.UltimateAssets);
            Friends?.SetAnchor(ImGui.GetWindowPos(), ImGui.GetWindowSize(), ImGui.GetStyle().Alpha, ImGuiP.GetCurrentWindow().ID);
            DactTheme.DrawGameWindow();
            if (!cloudSnapshot.IsSignedIn || cloudSnapshot.CanUseDact) cloudDiscordCodeRequestedFor = null;
            if (!cloudSnapshot.CanUseDact)
            {
                skinSettings.Close();
                DactUi.BeginFrame();
                var headerHeight = 48 * DactUi.S;
                DrawShellHeader(cloudSnapshot, ImGui.GetWindowPos(), ImGui.GetWindowSize().X, headerHeight);
                DrawCloudQuickPopup(cloudSnapshot);
                ImGui.SetCursorPos(new Vector2(0, headerHeight));
                ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(18) * DactUi.S);
                if (ImGui.BeginChild("account-authentication-gate", new Vector2(-1, -1), false,
                        ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.AlwaysUseWindowPadding))
                {
                    if (cloudSnapshot.IsSignedIn) DrawDiscordBindingGate(cloudSnapshot);
                    else DrawAuthenticationGate(cloudSnapshot);
                }
                ImGui.EndChild();
                ImGui.PopStyleVar();
                // Before sign-in the splash lands on the sign-in page's big logo.
                DrawSplashOverlay(false);
                return;
            }

            if (!string.IsNullOrWhiteSpace(cloudSnapshot.RecoveryKeyToSave))
            {
                // Registration reveals the recovery key once, so keep it visible instead
                // of dropping the user onto the previously selected functional page.
                selectedPage = Page.Cloud;
            }
            if (selectedPage != Page.Diagnostics) skinSettings.Close();
            // 好友 needs a signed-in account; its sub-page always reopens on the friend list.
            if (selectedPage == Page.Friends && (Friends is null || !cloudSnapshot.CanUseDact)) selectedPage = Page.Overview;
            if (selectedPage != Page.Friends) friendNotificationsPage = false;
            if (selectedPage != Page.Diagnostics) scenesPage = false;
            DrawShell(cloudSnapshot, () =>
            {
                var hostConfigurationChanged = false;
                var changed = selectedPage switch
                {
                    Page.Overview => DrawOverview(),
                    Page.Meter => DrawMeter(),
                    Page.History => DrawHistoryPage(),
                    Page.Friends => DrawFriendsPage(),
                    Page.Ai => DrawAiPage(cloudSnapshot),
                    Page.Overlays => DrawOverlays(),
                    Page.Extensions => DrawExtensions(out hostConfigurationChanged),
                    Page.Cloud => DrawCloud(),
                    Page.Diagnostics => DrawSettingsPage(),
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
            });
            DrawCloudQuickPopup(cloudSnapshot);
            DrawTour(cloudSnapshot);
            DrawSplashOverlay(true);
        }
        finally
        {
            PopTheme();
        }
    }

    internal static float PageContentHeight()
        // The game frame ends before its transparent shadow tail. Keep scrolling
        // child panels above that rim rather than painting across the bottom edge.
        => DactTheme.Palette.Light
            ? Math.Max(1, ImGui.GetContentRegionAvail().Y - 12 * Math.Max(.75f, ImGui.GetFontSize() / 17f)) : -1;

    public override void OnClose()
    {
        EndSplashOnClose();
        skinSettings.Close();
        historyLimitDraft = null;
        saveConfiguration();
    }

    public void Detach() => parserEngine.StatusChanged -= OnParserStatusChanged;

    private void DrawHistoryLimitEditor()
    {
        var s = DactUi.S;
        var draft = historyLimitDraft ?? configuration.HistoryLimit;
        DactUi.BeginField(text.Get("历史记录上限", "History limit"));
        if (DactUi.SliderInt("history-limit", ref draft, 1, 200, text.Format($"{draft} 场", $"{draft} fights"))) historyLimitDraft = draft;
        // This page shares the same destructive setting as advanced settings, so both
        // entry points require the same explicit confirmation contract.
        if (historyLimitDraft is { } pending && pending != configuration.HistoryLimit)
        {
            if (DactUi.Button(text.Get("确定###history-limit-confirm", "Confirm###history-limit-confirm"), ButtonKind.Primary) &&
                applyHistoryLimit(pending))
                historyLimitDraft = null;
            ImGui.SameLine(0, 8 * s);
            if (DactUi.Button(text.Get("取消###history-limit-cancel", "Cancel###history-limit-cancel"))) historyLimitDraft = null;
        }
        else historyLimitDraft = null;
        DactUi.EndField(text.Get(
            "战斗历史、战斗 JSON 与 Network 日志分别保留此数量。",
            "Encounter history, encounter JSON, and Network logs each keep this many."));
    }

    private string FormatCactbotStatus()
    {
        var status = getCactbotOperationStatus();
        return status.State switch
        {
            CactbotOperationState.Checking => text.Get("正在检查资源…", "Checking assets…"),
            CactbotOperationState.Installing => text.Get("正在安装资源…", "Installing assets…"),
            CactbotOperationState.Error => text.Format(
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

    private void DrawWindowChrome(CloudClientSnapshot cloudSnapshot)
    {
        var stateLabel = $"● {LocalizeState(parserStatus.State)}";
        var stateColor = parserStatus.State == ParserState.Running
            ? IceBlue
            : new Vector4(0.70f, 0.72f, 0.76f, 1);
        var (cloudStatusLabel, cloudStatusColor) = ResolveCloudStatus(cloudSnapshot);
        // The guide documents authenticated runtime controls, so hiding its entry before
        // sign-in avoids presenting a button that the authentication gate cannot open.
        var helpAction = cloudSnapshot.CanUseDact ? openHelp : null;
        if (BrandedWindowChrome.Draw(
                headerDrag,
                logoTexture,
                text.Get("主页", "Home"),
                stateLabel,
                stateColor,
                VersionLabel,
                "control-center",
                helpAction: helpAction,
                helpTooltip: text.Get("帮助", "Help"),
                statusAction: () => cloudQuickPopupRequested = true,
                statusLabel: $"● {cloudStatusLabel}",
                statusColor: cloudStatusColor,
                statusTooltip: text.Get("查看云同步状态", "View cloud sync status"),
                friendsAction: cloudSnapshot.CanUseDact && Friends is { } friends ? friends.ToggleDrawer : null,
                onlineFriends: Friends?.Snapshot is { State: "ready" } friendSnapshot ? friendSnapshot.Friends?.OnlineCount ?? 0 : 0,
                friendsUnread: Friends?.Snapshot.HasUnreadMessages ?? false,
                logoAction: () => DiscoverSkin(skinDiscoveries.ClickLogo(configuration.Appearance, Environment.TickCount64)),
                versionAction: () => DiscoverSkin(skinDiscoveries.ClickVersion(configuration.Appearance, Environment.TickCount64)), text: text,
                centerRunning: parserStatus.State == ParserState.Running))
        {
            HideAnimated();
        }
    }

    internal FriendsUiManager? Friends { get; set; }

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
        DiscoverSkin(skinDiscoveries.VisitPage(configuration.Appearance, (int)selectedPage));
        if (discoveredSkin is { } id && Environment.TickCount64 < discoveryNoticeUntil)
        {
            var skin = SkinCatalog.All.First(item => item.Id == id);
            DactTheme.TextColored(IceBlue, text.Format($"发现隐藏配色「{skin.ChineseName}」！可在设置&账号中使用。", $"Discovered {text.Get(skin.ChineseName, skin.EnglishName)}! Apply it in Settings & Account."));
        }
    }

    private void DiscoverSkin(string? id)
    {
        if (id is null) return;
        discoveredSkin = id;
        discoveryNoticeUntil = Environment.TickCount64 + 8000;
        saveConfiguration();
    }
    // 主页 (screens.js homeContent): status tiles, quick actions, basic settings in two columns.
    // "显示 ACT 快捷按钮" moved to 设置 · 通用 in 1.0.
    private bool DrawOverview()
    {
        var palette = DactTheme.Palette;
        DactUi.PageHeader(
            text.Get("主页", "Home"),
            text.Get("常用状态和入口集中在这里。", "Status and common actions in one place."));

        var running = parserStatus.State == ParserState.Running;
        var plugins = discoverPlugins();
        var enabledPlugins = plugins.Count(plugin => plugin.Enabled);
        var cloudSnapshot = cloud.GetSnapshot();
        var (cloudLabel, cloudColor) = ResolveCloudStatus(cloudSnapshot);
        var friendsSnapshot = Friends?.Snapshot;
        var onlineFriends = friendsSnapshot?.Friends?.OnlineCount ?? 0;
        var unreadMessages = friendsSnapshot?.Conversations.Values.Where(c => c.Unread).Sum(c => Math.Max(1, c.UnreadCount)) ?? 0;
        DactUi.StatTiles(
            new("pulse", text.Get("解析器", "Parser"), LocalizeState(parserStatus.State), running ? palette.Accent : palette.Gold,
                running ? text.Get("两个系统插件已加载", "Both system plugins loaded") : parserStatus.Message),
            new("plug", text.Get("扩展", "Extensions"), text.Format($"{enabledPlugins} / {plugins.Count} 已启用", $"{enabledPlugins} / {plugins.Count} enabled"), palette.Text,
                enabledPlugins == plugins.Count
                    ? text.Get("全部已启用", "All enabled")
                    : text.Format($"{plugins.Count - enabledPlugins} 个已停用", $"{plugins.Count - enabledPlugins} disabled")),
            new("cloud", text.Get("云同步", "Cloud sync"), cloudLabel, cloudColor,
                cloudSnapshot.Backups.Count == 0
                    ? text.Get("自动同步已开启", "Automatic sync on")
                    : text.Format($"云端保留 {cloudSnapshot.Backups.Count} 个版本", $"{cloudSnapshot.Backups.Count} cloud versions kept")),
            new("users", text.Get("好友", "Friends"), text.Format($"{onlineFriends} 人在线", $"{onlineFriends} online"), palette.Text,
                unreadMessages == 0
                    ? text.Get("没有未读消息", "No unread messages")
                    : text.Format($"{unreadMessages} 条未读消息", $"{unreadMessages} unread messages")));

        DactUi.BeginCard("home-quick-actions", text.Get("快捷入口", "Quick actions"));
        var meterVisible = configuration.Meter.IsVisible;
        var meterLabel = meterVisible ? text.Get("关闭战斗统计", "Close Combat Meter") : text.Get("打开战斗统计", "Open Combat Meter");
        if (DactUi.Button(meterLabel + "##home-meter", ButtonKind.Primary, "meter")) setMeterVisible(!meterVisible);
        var historyLabel = text.Get("战斗记录", "Encounter history");
        DactUi.SameLineIfFits(DactUi.ButtonWidth(historyLabel, icon: "history"));
        if (DactUi.Button(historyLabel + "##home-history", icon: "history"))
        {
            selectedPage = Page.History;
            historyTab = 0;
        }
        var statusLabel = isStatusVisible() ? text.Get("关闭运行状态", "Close Runtime status") : text.Get("运行状态", "Runtime status");
        DactUi.SameLineIfFits(DactUi.ButtonWidth(statusLabel, icon: "pulse"));
        if (DactUi.Button(statusLabel + "##home-status", icon: "pulse")) setStatusVisible(!isStatusVisible());
        var logsLabel = text.Get("打开 FFLogs 上传日志", "Open FFLogs upload logs");
        DactUi.SameLineIfFits(DactUi.ButtonWidth(logsLabel, icon: "folder"));
        if (DactUi.Button(logsLabel + "##home-logs", icon: "folder")) OpenCombatLogDirectoryForUpload();
        if (ImGui.IsItemHovered())
            DactUi.Tooltip(text.Get(
                "打开 FFLogs 上传使用的原始 Network 日志目录，并把文件夹路径复制到剪贴板。",
                "Opens the raw Network log directory used for FFLogs uploads and copies its folder path to the clipboard."));
        if (!string.IsNullOrWhiteSpace(combatLogFolderFeedback))
            DactUi.Wrapped(combatLogFolderFeedback, combatLogFolderFeedbackIsError ? DactUi.Danger : palette.Accent, DactFonts.Small);
        DactUi.EndCard();

        var changed = false;
        DactUi.BeginCard("home-general", text.Get("基础设置", "General"));
        if (DactUi.BeginGrid("home-general-grid", 16))
        {
            var parsing = configuration.EnableParsing;
            if (DactUi.ToggleRow(text.Get("启用解析", "Enable parsing"), ref parsing)) { configuration.EnableParsing = parsing; changed = true; }
            GameRegionSelector.DrawField(text, getGameRegionSelection(), setGameRegionMode);
            var simplifiedMode = configuration.SimplifiedModeEnabled;
            if (DactUi.ToggleRow(text.Get("精简模式", "Simplified mode"), ref simplifiedMode)) setSimplifiedMode(simplifiedMode);
            if (ImGui.IsItemHovered())
                DactUi.Tooltip(text.Get(
                    "只保留解析和战斗统计；网页悬浮窗、扩展插件及其他 DACT 窗口会暂时关闭。可用 /actcompat simple off 退出。",
                    "Keeps only parsing and the combat meter. Web overlays, extensions, and other DACT windows are temporarily closed. Use /actcompat simple off to exit."));

            DactUi.NextColumn();
            var autoStart = configuration.AutoStartParser;
            if (DactUi.ToggleRow(text.Get("自动启动解析器", "Auto start parser"), ref autoStart)) { configuration.AutoStartParser = autoStart; changed = true; }
            changed |= ParserScopeSelector.DrawField(text, configuration);
            var hideWhenUnfocused = configuration.HideHtmlOverlaysWhenGameUnfocused;
            if (DactUi.ToggleRow(text.Get("游戏失焦时隐藏网页悬浮窗", "Hide web overlays when the game is unfocused"), ref hideWhenUnfocused))
                setHideHtmlOverlaysWhenUnfocused(hideWhenUnfocused);
            DactUi.EndGrid();
        }
        DactUi.EndCard();
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

    // 战斗统计 (screens.js meterScreen): the 榜单 and FFLogs cards side by side.
    private bool DrawMeter()
    {
        var s = DactUi.S;
        var changed = false;
        var showLabel = text.Get("显示战斗统计", "Show Combat Meter");
        var locateLabel = text.Get("定位到战斗统计", "Open Combat Meter window");
        DactUi.PageHeader(
            text.Get("战斗统计", "Combat Meter"),
            text.Get("仅调整内置战斗统计的显示，不影响 Cactbot 或 HTML 悬浮窗。", "These options affect only the built-in Combat Meter, not Cactbot or HTML overlays."),
            DactUi.InlineToggleWidth(showLabel) + 12 * s + DactUi.ButtonWidth(locateLabel, icon: "target"),
            () =>
            {
                var visible = configuration.Meter.IsVisible;
                if (DactUi.InlineToggle(showLabel, ref visible)) setMeterVisible(visible);
                ImGui.SameLine(0, 12 * s);
                if (DactUi.Button(locateLabel + "##meter-locate", icon: "target")) openMeter();
            });

        if (DactUi.BeginGrid("meter-grid", 14))
        {
            changed |= DrawMeterRankingCard();
            DactUi.NextColumn();
            changed |= DrawFflogsSettings();
            DactUi.EndGrid();
        }
        return changed;
    }

    private bool DrawMeterRankingCard()
    {
        var s = DactUi.S;
        var changed = false;
        DactUi.BeginCard("meter-ranking", null);
        var resetLabel = text.Get("重置当前战斗…", "Reset current encounter…");
        DactUi.CardTitleRow(text.Get("榜单", "Meter"), null, DactUi.ButtonWidth(resetLabel, ButtonKind.Danger, "reset"), () =>
        {
            if (DactUi.Button(resetLabel + "##meter-reset", ButtonKind.Danger, "reset"))
            {
                resetEncounterConfirmationExpiresAt = Environment.TickCount64 + ResetConfirmationMilliseconds;
                DactUi.OpenDialog(ResetEncounterPopupId);
            }
            // The confirmation shares this card's ID scope, where the popup was opened.
            DrawResetEncounterConfirmation();
        });

        // 榜单模板 is a segmented control now (was a dropdown); 自定义… opens the style editor.
        DactUi.BeginField(text.Get("榜单模板", "Meter template"));
        var kinds = Enum.GetValues<MeterWindowKind>();
        var kindIndex = Array.IndexOf(kinds, configuration.Meter.ActiveWindowKind);
        var customizeLabel = text.Get("自定义…", "Customize…");
        var segmentWidth = ImGui.GetContentRegionAvail().X - DactUi.ButtonWidth(customizeLabel) - 8 * s;
        if (DactUi.Segmented("meter-template", kinds.Select(MeterKindLabel).ToArray(), ref kindIndex, segmentWidth) && kindIndex >= 0)
        {
            configuration.Meter.ActivateWindow(kinds[kindIndex]);
            changed = true;
        }
        ImGui.SameLine(0, 8 * s);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + s);
        if (DactUi.Button(customizeLabel + "##meter-customize")) openMeterStyleEditor();
        DactUi.EndField(text.Get(
            "一次只显示一个榜单；切换时保留各模板的位置、大小、锁定和槽位配置。",
            "Only one meter is shown at a time; each template keeps its own position, size, lock, and slots."));

        changed |= MeterDisplayScopeSelector.DrawField(text, configuration);
        changed |= EncounterResetSelector.DrawField(text, configuration);

        DactUi.BeginField(text.Get("刷新间隔", "Refresh interval"));
        var refreshInterval = configuration.Meter.RefreshIntervalMs;
        if (DactUi.SliderInt("meter-refresh-interval", ref refreshInterval, 250, 2000,
                text.Format($"{refreshInterval} 毫秒", $"{refreshInterval} ms")))
        {
            configuration.Meter.RefreshIntervalMs = refreshInterval;
            changed = true;
        }
        DactUi.EndField();

        changed |= DrawPlayerIdentityControls();
        DactUi.EndCard();
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
        if (!DactUi.BeginDialog(ResetEncounterPopupId, 430)) return;
        var now = Environment.TickCount64;
        // Expiry starts the fade-out; a click during the fade no longer counts as a confirmation.
        if (IsResetConfirmationExpired(resetEncounterConfirmationExpiresAt, now))
        {
            resetEncounterConfirmationExpiresAt = 0;
            DactUi.CloseDialog(ResetEncounterPopupId);
        }
        var live = resetEncounterConfirmationExpiresAt > 0;
        var secondsRemaining = live ? Math.Max(0, (int)Math.Ceiling((resetEncounterConfirmationExpiresAt - now) / 1000.0)) : 0;
        DactUi.Text(text.Get("重置当前战斗", "Reset current encounter"), size: DactFonts.H2);
        DactUi.Wrapped(text.Get(
            "这会清空当前战斗统计，但不会删除已经保存的历史记录。确认后才能执行。",
            "This clears the current encounter but does not delete saved history. It runs only after confirmation."), DactTheme.Palette.Text);
        DactUi.Hint(text.Format(
            $"确认窗口将在 {secondsRemaining} 秒后自动关闭。",
            $"This confirmation closes automatically in {secondsRemaining} seconds."));
        if (DialogFooter(text.Get("确认重置", "Confirm reset"), "reset-encounter", ButtonKind.Danger) is { } confirmed && live)
        {
            if (confirmed) resetCurrentEncounter();
            resetEncounterConfirmationExpiresAt = 0;
            DactUi.CloseDialog(ResetEncounterPopupId);
        }
        DactUi.EndDialog(ResetEncounterPopupId);
    }

    // DACT dialog footer: 取消 then the main action, right-aligned. Returns true for the action,
    // false for 取消 and null when neither was clicked this frame.
    private bool? DialogFooter(string action, string id, ButtonKind kind = ButtonKind.Primary, string? cancelLabel = null)
    {
        var s = DactUi.S;
        var cancel = cancelLabel ?? text.Get("取消", "Cancel");
        var total = DactUi.ButtonWidth(cancel) + 8 * s + DactUi.ButtonWidth(action, kind);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, ImGui.GetContentRegionAvail().X - total));
        bool? result = null;
        if (DactUi.Button(cancel + "##dialog-cancel-" + id)) result = false;
        ImGui.SameLine(0, 8 * s);
        if (DactUi.Button(action + "##dialog-confirm-" + id, kind)) result = true;
        return result;
    }

    internal static bool IsResetConfirmationExpired(long expiresAt, long now)
        => expiresAt > 0 && now >= expiresAt;

    private bool DrawOverlayRenameEditor(
        string overlayKey,
        HtmlOverlayWindowSettings settings)
    {
        var changed = false;
        ImGui.Spacing();
        ImGui.TextUnformatted(text.Get("新名称###overlay-rename-value", "New name###overlay-rename-value").Split("##", 2)[0]);
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText(
            "###overlay-rename-value",
            ref overlayRenameValue,
            80);
        if (DactTheme.Button(text.Get("保存名称", "Save name")))
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
        OverlaySameLineIfFits(ImGui.CalcTextSize(text.Get("取消重命名", "Cancel rename")).X + ImGui.GetStyle().FramePadding.X * 2);
        if (DactTheme.Button(text.Get("取消重命名", "Cancel rename")))
        {
            CancelOverlayRename();
        }

        if (!string.IsNullOrWhiteSpace(overlayRenameFeedback))
        {
            DactTheme.TextColored(
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
        // Separate field labels keep the old hidden IDs and stay readable at large fonts.
        DactUi.BeginField(text.Get("名称###custom-overlay-name", "Name###custom-overlay-name").Split("##", 2)[0]);
        DactUi.Input("custom-overlay-name", ref customOverlayName, text.Get("例如：我的计时器", "e.g. My timer"), 80);
        DactUi.EndField();
        DactUi.BeginField(text.Get("网址###custom-overlay-url", "URL###custom-overlay-url").Split("##", 2)[0]);
        DactUi.Input("custom-overlay-url", ref customOverlayUrl, "https://", 2048);
        DactUi.EndField(text.Get(
            "支持 http、https 与 file 地址；只添加你信任的悬浮窗页面。",
            "Supports http, https, and file URLs. Only add overlay pages you trust."));

        if (DactUi.Button(text.Get("创建并打开", "Create and open") + "##custom-overlay-create", ButtonKind.Primary))
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
            DactUi.Wrapped(customOverlayFeedback, customOverlayFeedbackIsError ? DactUi.Danger : DactTheme.Palette.Accent, DactFonts.Small);

        return changed;
    }

    private void SetCustomOverlayFeedback(string message, bool isError)
    {
        customOverlayFeedback = message;
        customOverlayFeedbackIsError = isError;
    }

    // Host snapshots (main, Matcha, generic) for the per-extension run state; set by the plugin.
    internal Func<IReadOnlyList<HostSupervisorSnapshot>>? HostSnapshots { get; set; }

    // 扩展 (screens.js extensionsScreen): system plugins, then every compatibility extension as a
    // row with its run state, permissions and configuration.
    private bool DrawExtensions(out bool hostConfigurationChanged)
    {
        var s = DactUi.S;
        var installLabel = text.Get("安装 DLL / ZIP", "Install DLL / ZIP");
        var folderLabel = text.Get("打开文件夹", "Open folder");
        var updatesLabel = text.Get("检查更新与来源", "Check updates and sources");
        DactUi.PageHeader(
            text.Get("扩展", "Extensions"),
            text.Get("系统插件与第三方 ACT 扩展保持原有运行方式。", "System plugins and third-party ACT extensions keep their existing runtime behavior."),
            DactUi.ButtonWidth(installLabel, ButtonKind.Primary, "upload") + DactUi.ButtonWidth(folderLabel, icon: "folder") +
            DactUi.ButtonWidth(updatesLabel, icon: "refresh") + 16 * s,
            () =>
            {
                if (DactUi.Button(installLabel + "##ext-install", ButtonKind.Primary, "upload")) selectPluginPackage();
                ImGui.SameLine(0, 8 * s);
                if (DactUi.Button(folderLabel + "##ext-folder", icon: "folder")) openPluginDirectory();
                ImGui.SameLine(0, 8 * s);
                if (DactUi.Button(updatesLabel + "##ext-updates", icon: "refresh")) checkBundledPluginUpdates();
            });
        if (getPluginInstallStatus().State != ThirdPartyPluginInstallState.Idle)
        {
            // Install progress sits in its own card; its failure and consent dialogs open from inside it.
            DactUi.BeginCard("ext-install-status", null);
            DrawPluginInstallStatus();
            DactUi.EndCard();
        }

        var changed = false;
        DactUi.BeginCard("ext-system", text.Get("系统插件", "System plugins"));
        var ffxiv = configuration.EmbeddedPlugins.FfxivActPluginEnabled;
        var overlay = configuration.EmbeddedPlugins.OverlayPluginEnabled;
        var start = ImGui.GetCursorScreenPos();
        if (DactUi.Toggle("##ext-ffxiv", ref ffxiv)) { configuration.EmbeddedPlugins.FfxivActPluginEnabled = ffxiv; changed = true; }
        var draw = ImGui.GetWindowDrawList();
        DactUi.DrawText(draw, start + new Vector2(44 * s, -2 * s), 24 * s, "FFXIV_ACT_Plugin", DactTheme.Palette.Text);
        var second = start + new Vector2(44 * s + DactUi.TextWidth("FFXIV_ACT_Plugin") + 24 * s, 0);
        ImGui.SetCursorScreenPos(second);
        if (DactUi.Toggle("##ext-overlay", ref overlay)) { configuration.EmbeddedPlugins.OverlayPluginEnabled = overlay; changed = true; }
        DactUi.DrawText(draw, second + new Vector2(44 * s, -2 * s), 24 * s, "OverlayPlugin", DactTheme.Palette.Text);
        ImGui.SetCursorScreenPos(start);
        ImGui.Dummy(new Vector2(ImGui.GetContentRegionAvail().X, 20 * s));
        DactUi.Hint(text.Get(
            "改变系统插件状态后需要重启解析器；兼容扩展启停后会自动重启独立 Host。",
            "Restart the parser after changing a system plugin; compatibility-extension changes restart the independent Host automatically."));
        DactUi.EndCard();

        var installedPlugins = discoverPlugins();
        hostConfigurationChanged = false;
        DactUi.BeginCard("ext-compat", null, gap: 4);
        var autoLabel = text.Get("启动时自动检查更新", "Check for updates on startup");
        var noticeLabel = text.Get("三方扩展声明", "Third-party extension notice");
        var titleActions = DactUi.TextWidth(autoLabel, DactFonts.Small) + 6 * s + 13 * s + 8 * s + 34 * s + 8 * s +
                           DactUi.ButtonWidth(noticeLabel, ButtonKind.Ghost);
        DactUi.CardTitleRow(text.Get("兼容扩展", "Compatibility extensions"), null, titleActions, () =>
        {
            var at = ImGui.GetCursorScreenPos();
            var titleDraw = ImGui.GetWindowDrawList();
            DactUi.DrawText(titleDraw, at, 30 * s, autoLabel, DactTheme.Palette.Muted, DactFonts.Small);
            var helpAt = at + new Vector2(DactUi.TextWidth(autoLabel, DactFonts.Small) + 6 * s, 8.5f * s);
            ImGui.SetCursorScreenPos(helpAt);
            ImGui.InvisibleButton("##ext-auto-help", new Vector2(13 * s));
            DactIcons.Draw(titleDraw, "help", helpAt, 13 * s, ImGui.IsItemHovered() ? DactTheme.Palette.Text : DactTheme.Palette.Muted);
            if (ImGui.IsItemHovered())
                DactUi.Tooltip(text.Get(
                    "关闭后仍可使用上方按钮手动检查；不会影响 Dalamud 自身的插件更新。",
                    "When disabled, the button above still checks manually; Dalamud's own plugin updates are unaffected."));
            ImGui.SetCursorScreenPos(new Vector2(helpAt.X + 21 * s, at.Y + 5 * s));
            var autoCheckUpdates = configuration.AutoCheckBundledPluginUpdates;
            if (DactUi.Toggle("##ext-auto-check", ref autoCheckUpdates))
            {
                configuration.AutoCheckBundledPluginUpdates = autoCheckUpdates;
                changed = true;
            }
            ImGui.SetCursorScreenPos(new Vector2(helpAt.X + 21 * s + 42 * s, at.Y));
            if (DactUi.Button(noticeLabel + "##ext-notice", ButtonKind.Ghost)) openBundledPluginNotice();
        });

        var first = true;
        void Separator()
        {
            if (!first) DactUi.Divider();
            first = false;
        }
        foreach (var (id, name, description, capabilities) in new (string, string, string, IReadOnlyList<ActCapability>?)[]
                 {
                     ("act.foxtts", "ACT.FoxTTS", text.Get("TTS 语音合成与播报", "TTS speech synthesis and announcements"), null),
                     ("postnamazu", text.Get("鲶鱼精邮差 / PostNamazu", "PostNamazu"), text.Get("游戏命令、标点与本地桥接", "Game commands, markers, and local bridge"), BundledActPluginCapabilities.PostNamazu),
                     ("triggernometry", "Triggernometry", text.Get("触发器、时间轴、TTS 与绘图", "Triggers, timelines, TTS, and drawing"), BundledActPluginCapabilities.Triggernometry),
                     ("silverdasher", text.Get("银山雀儿 / SilverDasher", "SilverDasher"), text.Get("狩猎、临危受命与跨区状态提醒", "Hunts, FATEs, and cross-world status alerts"), BundledActPluginCapabilities.SilverDasher),
                     ("matcha", text.Get("抹茶 / Cafe.Matcha", "Cafe.Matcha"), text.Get("狩猎与临危受命提醒；默认自启动并独占专属 Host", "Hunt and FATE alerts; starts by default in its dedicated Host"), BundledActPluginCapabilities.Matcha),
                 })
        {
            Separator();
            changed |= DrawExtensionEntry(installedPlugins, id, name, description, capabilities, out var extensionChanged);
            hostConfigurationChanged |= extensionChanged;
        }

        if (installedPlugins.Any(plugin => plugin.Manifest.Id == "simulant"))
        {
            Separator();
            changed |= DrawSimulantEntry(installedPlugins, out var simulantChanged);
            hostConfigurationChanged |= simulantChanged;
        }

        var genericPlugins = installedPlugins
            .Where(plugin => !ActPluginPackageInstaller.IsSpecializedPluginId(plugin.Manifest.Id))
            .ToArray();
        foreach (var plugin in genericPlugins)
        {
            Separator();
            changed |= DrawGenericExtensionEntry(plugin, out var genericChanged);
            hostConfigurationChanged |= genericChanged;
        }
        if (genericPlugins.Length > 0)
            DactUi.Hint(text.Get(
                "这些插件共用一个按需启动的通用 Host，不会为每个插件创建常驻进程。",
                "These plugins share one on-demand generic Host; no persistent process is created per plugin."));
        DrawGenericDeleteModal();
        DactUi.EndCard();

        changed |= hostConfigurationChanged;
        return changed;
    }

    // Run state of one extension from the Host that runs it (stages and callback health).
    private (string Label, PillTone Tone, string? Note) ExtensionRunState(string pluginId, bool enabled)
    {
        if (!enabled) return (text.Get("已禁用", "Disabled"), PillTone.Off, null);
        var hosts = HostSnapshots?.Invoke() ?? [];
        bool Same(string other) => string.Equals(other, pluginId, StringComparison.OrdinalIgnoreCase);
        var host = hosts.FirstOrDefault(candidate => candidate.PluginStages?.Any(stage => Same(stage.PluginId)) == true ||
                                                     candidate.PluginHealth?.Any(health => Same(health.PluginId)) == true);
        if (host is null) return (text.Get("已启用", "Enabled"), PillTone.Ok, null);
        var failed = host.PluginStages?.FirstOrDefault(stage => Same(stage.PluginId) && stage.State == "failed");
        var health = host.PluginHealth?.FirstOrDefault(candidate => Same(candidate.PluginId));
        if (host.State is HostSupervisorState.Faulted or HostSupervisorState.CircuitOpen)
            return (text.Get("故障", "Faulted"), PillTone.Error, text.Get("所在 Host 已停止响应；可在运行状态中重启 Host。", "Its Host stopped responding; restart the Host from Runtime status."));
        if (failed is not null) return (text.Get("故障", "Faulted"), PillTone.Error, $"{failed.Stage}: {failed.Detail}");
        if (health?.CircuitOpen == true)
            return (text.Get("故障", "Faulted"), PillTone.Error, text.Get("事件处理多次超时，已暂停；重启 Host 后恢复。", "Event handling timed out repeatedly and is paused; restart the Host to resume."));
        if (host.State == HostSupervisorState.Starting) return (text.Get("初始化中", "Initializing"), PillTone.Warn, null);
        if (health is { State: "degraded" })
            return (text.Get("运行异常", "Degraded"), PillTone.Warn,
                text.Format($"异常 {health.Exceptions} 次 · 超时 {health.SlowCalls} 次", $"{health.Exceptions} exceptions · {health.SlowCalls} timeouts"));
        return (text.Get("已启用", "Enabled"), PillTone.Ok, null);
    }

    // One row (screens.js extensionRow): switch, name + version (+ 自行安装), note, state pill,
    // 权限, 打开配置 or 查看并授权, and delete for user-installed DLLs. Returns the toggled value.
    private bool? DrawExtensionRow(string rowId, string name, InstalledActPlugin? installed, string note, Vector4? noteColor,
        bool enabled, bool canToggle, (string Label, PillTone Tone) state, IReadOnlyList<ActCapability>? capabilities,
        bool needsConsent, Action? openConfig, Action authorize, ref bool permissionsChanged,
        (string Icon, string Tooltip, Action Run)? trailing = null)
    {
        var s = DactUi.S;
        ImGui.PushID(rowId);
        var width = ImGui.GetContentRegionAvail().X;
        var start = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        var permissionLabel = text.Get("权限", "Permissions");
        var actionLabel = needsConsent ? text.Get("查看并授权", "Review and authorize") : text.Get("打开配置", "Open configuration");
        var userManaged = installed is not null && ActPluginPackageInstaller.IsUserManaged(installed.Manifest);
        var pillWidth = DactUi.TextWidth(state.Label, DactFonts.Small) + 30 * s;
        var rightWidth = pillWidth + 12 * s + (capabilities is { Count: > 0 } && installed is not null ? DactUi.ButtonWidth(permissionLabel, icon: "shield") + 8 * s : 0) +
                         (installed is null ? 0 : DactUi.ButtonWidth(actionLabel)) + (userManaged ? 36 * s : 0) + (trailing is null ? 0 : 36 * s);
        var nameWidth = Math.Max(80 * s, width - 46 * s - rightWidth - 12 * s);
        string noteText;
        using (DactFonts.Push(DactFonts.Small)) noteText = string.Join("\n", DactUi.WrapLines(note, nameWidth));
        float noteHeight;
        using (DactFonts.Push(DactFonts.Small)) noteHeight = string.IsNullOrEmpty(note) ? 0 : ImGui.CalcTextSize(noteText).Y;
        var rowHeight = Math.Max(30 * s, 18 * s + (noteHeight > 0 ? 2 * s + noteHeight : 0)) + 16 * s;
        var middle = start.Y + rowHeight * .5f;

        bool? toggled = null;
        ImGui.SetCursorScreenPos(new Vector2(start.X + 4 * s, middle - 10 * s));
        var on = enabled;
        if (DactUi.Toggle("##enabled", ref on, !canToggle)) toggled = on;

        var textLeft = start.X + 50 * s;
        var top = start.Y + 8 * s;
        var title = DactUi.Fit(name, nameWidth);
        DactUi.DrawText(draw, new Vector2(textLeft, top), 18 * s, title, enabled ? DactTheme.Palette.Text : DactTheme.Palette.Muted);
        var x = textLeft + DactUi.TextWidth(title) + 8 * s;
        if (installed is not null)
        {
            var version = $"v{installed.DisplayVersion}";
            DactUi.DrawText(draw, new Vector2(x, top), 18 * s, version, DactTheme.Palette.Muted, DactFonts.Small);
            var versionMin = new Vector2(x, top);
            x += DactUi.TextWidth(version, DactFonts.Small) + 8 * s;
            // Keep the signed-off manifest visible for diagnosis without presenting it as the loaded DLL version.
            if (installed.HasVersionMismatch && ImGui.IsMouseHoveringRect(versionMin, new Vector2(x, top + 18 * s)))
                DactUi.Tooltip(text.Format(
                    $"实际 DLL 版本为 {installed.DisplayVersion}；安装清单记录为 {installed.Manifest.Version}。",
                    $"The DLL version is {installed.DisplayVersion}; the install manifest records {installed.Manifest.Version}."));
        }
        if (userManaged && x + 60 * s < textLeft + nameWidth)
        {
            ImGui.SetCursorScreenPos(new Vector2(x, top - 2 * s));
            DactUi.Pill(text.Get("自行安装", "Self-installed"), PillTone.Off, dot: false);
        }
        if (noteHeight > 0)
            using (DactFonts.Push(DactFonts.Small))
                draw.AddText(new Vector2(textLeft, top + 20 * s), DactUi.U32(DactTheme.Foreground(noteColor ?? DactUi.HintColor)), noteText);

        var right = start.X + width;
        if (trailing is { } extra)
        {
            right -= 28 * s;
            ImGui.SetCursorScreenPos(new Vector2(right, middle - 14 * s));
            if (DactUi.IconButton("##trailing", extra.Icon, extra.Tooltip)) extra.Run();
            right -= 8 * s;
        }
        if (userManaged)
        {
            right -= 28 * s;
            ImGui.SetCursorScreenPos(new Vector2(right, middle - 14 * s));
            if (DactUi.IconButton("##delete", "trash", text.Get("删除插件", "Delete plugin")))
            {
                genericPluginToDeleteId = installed!.Manifest.Id;
                genericPluginToDeleteName = installed.Manifest.Name;
                genericDeletePopupRequested = true;
            }
            right -= 8 * s;
        }
        if (installed is not null)
        {
            right -= DactUi.ButtonWidth(actionLabel);
            ImGui.SetCursorScreenPos(new Vector2(right, middle - 15 * s));
            if (needsConsent ? DactUi.Button(actionLabel + "##authorize") : DactUi.Button(actionLabel + "##configure", disabled: !enabled || openConfig is null))
            {
                if (needsConsent) authorize();
                else openConfig?.Invoke();
            }
            right -= 8 * s;
        }
        if (capabilities is { Count: > 0 } && installed is not null)
        {
            right -= DactUi.ButtonWidth(permissionLabel, icon: "shield");
            ImGui.SetCursorScreenPos(new Vector2(right, middle - 15 * s));
            if (DactUi.Button(permissionLabel + "##permissions", icon: "shield")) ImGui.OpenPopup("permissions");
            permissionsChanged |= DrawPluginPermissionsPopup(installed.Manifest.Id, name, capabilities);
            right -= 8 * s;
        }
        right -= pillWidth + 4 * s;
        ImGui.SetCursorScreenPos(new Vector2(right, middle - 11 * s));
        DactUi.Pill(state.Label, state.Tone);

        ImGui.SetCursorScreenPos(start);
        ImGui.Dummy(new Vector2(width, rowHeight));
        ImGui.PopID();
        return toggled;
    }

    private void DrawPluginInstallStatus()
    {
        var status = getPluginInstallStatus();
        if (status.State == ThirdPartyPluginInstallState.Idle)
        {
            return;
        }

        var displayName = string.IsNullOrWhiteSpace(status.DisplayName)
            ? text.Get("第三方插件", "Third-party plugin")
            : status.DisplayName;
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
        var tone = status.State switch
        {
            ThirdPartyPluginInstallState.Failed => PillTone.Error,
            ThirdPartyPluginInstallState.Ready or ThirdPartyPluginInstallState.Removed => PillTone.Ok,
            _ => PillTone.Warn,
        };
        DactUi.CardTitleRow(displayName, () => DactUi.Pill(text.Get(labelZh, labelEn), tone));
        if (!string.IsNullOrWhiteSpace(status.Detail)) DactUi.Hint(status.Detail);

        if (status.State == ThirdPartyPluginInstallState.Failed)
        {
            openedGenericPermissionKey = null;
            if (!ReferenceEquals(openedPluginFailureStatus, status))
            {
                openedPluginFailureStatus = status;
                pluginFailureLogCopied = false;
                DactUi.OpenDialog(PluginInstallFailurePopupId);
            }

            if (DactTheme.Button(text.Get("查看失败原因", "View failure details")))
            {
                DactUi.OpenDialog(PluginInstallFailurePopupId);
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
            DactUi.OpenDialog(GenericPermissionPopupId);
        }

        if (DactTheme.Button(text.Get("查看授权", "Review permissions")))
        {
            DactUi.OpenDialog(GenericPermissionPopupId);
        }
        DrawGenericPermissionModal(status);
    }

    private void DrawPluginInstallFailureModal(ThirdPartyPluginInstallStatus status)
    {
        if (!DactUi.BeginDialog(PluginInstallFailurePopupId, 560)) return;
        var s = DactUi.S;
        var palette = DactTheme.Palette;
        DactUi.Text(text.Get("插件导入失败", "Plugin import failed"), DactUi.Danger, DactFonts.H2);
        var name = string.IsNullOrWhiteSpace(status.DisplayName) ? text.Get("第三方插件", "Third-party plugin") : status.DisplayName;
        DactUi.Text(string.IsNullOrWhiteSpace(status.Version) ? name : $"{name}  v{status.Version}", palette.Text);
        DactUi.Divider();
        DactUi.Wrapped(string.IsNullOrWhiteSpace(status.Detail)
            ? text.Get("导入过程没有返回具体原因。", "The import did not return a specific reason.")
            : status.Detail, palette.Text);
        DactUi.Hint(text.Get(
            "关闭只会清除本次失败提示，不会删除原始 DLL / ZIP 或已安装插件。",
            "Closing clears only this failure notice; it does not delete the original DLL / ZIP or installed plugins."));
        if (DactUi.Button(text.Get("复制日志", "Copy log") + "##plugin-failure-copy", icon: "copy"))
        {
            ImGui.SetClipboardText(BuildPluginInstallFailureLog(status));
            pluginFailureLogCopied = true;
        }
        if (pluginFailureLogCopied)
        {
            ImGui.SameLine(0, 8 * s);
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 4 * s);
            DactUi.Pill(text.Get("已复制", "Copied"), PillTone.Ok);
        }
        var close = text.Get("关闭并清除记录", "Close and clear");
        ImGui.SameLine(0, 8 * s);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, ImGui.GetContentRegionAvail().X - DactUi.ButtonWidth(close, ButtonKind.Primary)));
        if (DactUi.Button(close + "##plugin-failure-close", ButtonKind.Primary))
        {
            DactUi.CloseDialog(PluginInstallFailurePopupId);
            openedPluginFailureStatus = null;
            pluginFailureLogCopied = false;
            dismissPluginInstallFailure();
        }
        DactUi.EndDialog(PluginInstallFailurePopupId);
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
        if (!DactUi.BeginDialog(GenericPermissionPopupId, 560)) return;
        var palette = DactTheme.Palette;
        DactUi.Text(text.Get("第三方 ACT 插件授权", "Third-party ACT plugin authorization"), palette.Gold, DactFonts.H2);
        DactUi.Text(string.IsNullOrWhiteSpace(status.Version) ? status.DisplayName : $"{status.DisplayName}  v{status.Version}", palette.Text);
        DactUi.Divider();
        DactUi.Wrapped(text.Get(
            "静态预检已经完成。该 DLL 会作为桌面代码运行；以下清单约束兼容接口，但无法拦截插件自身直接调用 Windows API。",
            "Static preflight is complete. This DLL runs as desktop code; the list below governs compatibility APIs but cannot intercept direct Windows API calls made by the plugin."),
            palette.Text);
        DactUi.Hint(text.Get("预检生成的权限清单：", "Permissions generated by preflight:"));
        var lines = status.Capabilities.Count == 0
            ? [text.Get("未检测到额外兼容接口权限", "No additional compatibility API permissions detected")]
            : status.Capabilities.Select(capability => ActCapabilityDisplay.Label(capability, text)).ToArray();
        foreach (var line in lines) DactUi.Wrapped("·  " + line, palette.Text);
        if (DialogFooter(text.Get("授权并启用", "Authorize and enable"), "generic-permission", cancelLabel: text.Get("暂不授权", "Do not authorize")) is { } approved)
        {
            DactUi.CloseDialog(GenericPermissionPopupId);
            if (approved) approvePendingPlugin();
            else denyPendingPlugin();
        }
        DactUi.EndDialog(GenericPermissionPopupId);
    }

    // Capability switches for one extension, opened from its 权限 button. Grants are saved per
    // extension and capability; saving restarts the Host once so they take effect.
    private bool DrawPluginPermissionsPopup(string pluginId, string displayName, IReadOnlyList<ActCapability> capabilities)
    {
        var s = DactUi.S;
        // A content popup: card padding on every side (the menu padding left the switches on the edge).
        if (!DactUi.BeginMenuPopup("permissions", 380 * s, 16)) return false;
        var changed = false;
        using (DactUi.Gap(10))
        {
            DactUi.Text(text.Format($"{displayName} 权限", $"{displayName} permissions"), DactTheme.Palette.Gold);
            DactUi.Hint(text.Get(
                "高风险能力默认关闭，授权按扩展和能力分别保存；权限组保存后会自动重启 Host 一次，使完整功能立即生效。第三方 DLL 的直接系统调用仍由独立 Host 的进程边界承担。",
                "High-risk capabilities are denied by default. Grants are stored per extension and capability; after a permission group is saved, the Host restarts once so the complete feature set takes effect immediately. Direct system calls from third-party DLLs remain behind the independent Host process boundary."));
            foreach (var capability in capabilities)
            {
                var allowed = configuration.IsActCapabilityAllowed(pluginId, capability);
                if (!DactUi.ToggleRow($"{ActCapabilityDisplay.Label(capability, text)}##control-center-{pluginId}-{capability}", ref allowed)) continue;
                configuration.SetActCapability(pluginId, capability, allowed);
                logger.Information($"ACT permission changed: plugin={pluginId}, capability={capability}, allowed={allowed}.");
                changed = true;
            }
        }
        ImGui.EndPopup();
        return changed;
    }

    private bool DrawGenericExtensionEntry(
        InstalledActPlugin plugin,
        out bool enabledChanged)
    {
        var pluginId = plugin.Manifest.Id;
        var changed = false;
        enabledChanged = false;
        var trusted = configuration.TrustedGenericActPluginIds.Contains(pluginId);
        var enabled = plugin.Enabled && trusted;
        var state = trusted ? ExtensionRunState(pluginId, enabled) : (text.Get("未授权", "Not authorized"), PillTone.Warn, (string?)null);
        var requested = ActPluginPackageInstaller.GetRequestedCapabilities(plugin.Manifest);
        var permissionsChanged = false;
        var toggled = DrawExtensionRow($"generic-extension-{pluginId}", plugin.Manifest.Name, plugin,
            state.Item3 ?? text.Get("自行安装的 ACT 扩展；装好后才显示，可单独设置权限", "Self-installed ACT extension; listed once installed, with its own permissions"),
            state.Item3 is null ? null : ExtensionNoteColor(state.Item2),
            enabled, true, (state.Item1, state.Item2), trusted && requested.Count > 0 ? requested : null,
            !trusted, () => openPluginConfiguration(pluginId), () => requestPluginAuthorization(pluginId), ref permissionsChanged);
        enabledChanged |= permissionsChanged;
        if (toggled is { } value)
        {
            if (value && !configuration.TrustedGenericActPluginIds.Contains(pluginId))
            {
                // Enabling an untrusted DLL opens the same informed-consent flow as installation.
                requestPluginAuthorization(pluginId);
            }
            else
            {
                if (value) configuration.DisabledActPluginIds.Remove(pluginId);
                else configuration.DisabledActPluginIds.Add(pluginId);
                changed = true;
                enabledChanged = true;
            }
        }
        return changed;
    }

    private static Vector4 ExtensionNoteColor(PillTone tone) => tone switch
    {
        PillTone.Error => DactUi.Danger,
        PillTone.Warn => DactTheme.Palette.Gold,
        _ => DactUi.HintColor,
    };

    private void DrawPluginDeleteButton(InstalledActPlugin plugin)
    {
        if (!ActPluginPackageInstaller.IsUserManaged(plugin.Manifest)) return;
        ImGui.SameLine();
        if (ImGuiComponents.IconButton(FontAwesomeIcon.Trash))
        {
            genericPluginToDeleteId = plugin.Manifest.Id;
            genericPluginToDeleteName = plugin.Manifest.Name;
            genericDeletePopupRequested = true;
        }
        if (ImGui.IsItemHovered()) DactUi.Tooltip(text.Get("删除插件", "Delete plugin"));
    }

    private bool NeedsManualPluginConsent(InstalledActPlugin plugin)
        => ActPluginPackageInstaller.RequiresManualAuthorization(plugin.Manifest) &&
           !configuration.TrustedGenericActPluginIds.Contains(plugin.Manifest.Id);

    private bool SetInstalledPluginEnabled(InstalledActPlugin plugin, bool enabled)
    {
        if (enabled && NeedsManualPluginConsent(plugin))
        {
            requestPluginAuthorization(plugin.Manifest.Id);
            return false;
        }
        if (enabled) configuration.DisabledActPluginIds.Remove(plugin.Manifest.Id);
        else configuration.DisabledActPluginIds.Add(plugin.Manifest.Id);
        return true;
    }

    private void DrawInstalledPluginAuthorization(InstalledActPlugin plugin)
    {
        if (!NeedsManualPluginConsent(plugin)) return;
        ImGui.SameLine();
        if (DactTheme.SmallButton(text.Get("查看并授权", "Review and authorize")))
            requestPluginAuthorization(plugin.Manifest.Id);
    }

    private void DrawGenericDeleteModal()
    {
        if (genericDeletePopupRequested)
        {
            DactUi.OpenDialog(GenericDeletePopupId);
            genericDeletePopupRequested = false;
        }

        if (!DactUi.BeginDialog(GenericDeletePopupId, 440)) return;
        DactUi.Text(text.Get("删除第三方 ACT 插件", "Delete third-party ACT plugin"), size: DactFonts.H2);
        DactUi.Wrapped(text.Format(
            $"确定删除 {genericPluginToDeleteName} 吗？插件文件会先备份，相关扩展会短暂重启。",
            $"Delete {genericPluginToDeleteName}? Plugin files will be backed up first, and related extensions will briefly restart."),
            DactTheme.Palette.Text);
        if (DialogFooter(text.Get("确认删除", "Delete"), "generic-delete", ButtonKind.Danger) is { } confirmed)
        {
            var pluginId = genericPluginToDeleteId;
            DactUi.CloseDialog(GenericDeletePopupId);
            genericPluginToDeleteId = null;
            genericPluginToDeleteName = null;
            if (confirmed && !string.IsNullOrWhiteSpace(pluginId)) uninstallGenericPlugin(pluginId);
        }
        DactUi.EndDialog(GenericDeletePopupId);
    }

    private bool DrawSimulantEntry(
        IReadOnlyList<InstalledActPlugin> plugins,
        out bool enabledChanged)
    {
        var installed = plugins.FirstOrDefault(plugin => plugin.Manifest.Id == "simulant");
        var changed = false;
        enabledChanged = false;
        // Simulant is user-supplied; expose management only after a manual import.
        if (installed is null) return false;
        var needsConsent = NeedsManualPluginConsent(installed);
        var enabled = installed.Enabled && !needsConsent;
        var missing = new[] { "postnamazu", "triggernometry" }
            .Where(id => !plugins.Any(plugin => plugin.Manifest.Id == id && plugin.Enabled)).ToArray();
        var state = needsConsent ? (text.Get("未授权", "Not authorized"), PillTone.Warn, (string?)null) : ExtensionRunState("simulant", enabled);
        var note = state.Item3 ?? (missing.Length > 0
            ? text.Format($"请先安装并启用：{string.Join(", ", missing)}。", $"Install and enable first: {string.Join(", ", missing)}.")
            : text.Get("本地机制模拟；与 PostNamazu、Triggernometry 一起运行。", "Local mechanic simulation; runs alongside PostNamazu and Triggernometry."));
        var permissionsChanged = false;
        var toggled = DrawExtensionRow("extension-simulant", text.Get("仿生石 / Simulant", "Simulant"), installed, note,
            state.Item3 is not null || missing.Length > 0 ? ExtensionNoteColor(state.Item3 is not null ? state.Item2 : PillTone.Warn) : null,
            enabled, true, (state.Item1, state.Item2), BundledActPluginCapabilities.Simulant, needsConsent,
            () => openPluginConfiguration("simulant"), () => requestPluginAuthorization(installed.Manifest.Id), ref permissionsChanged);
        enabledChanged |= permissionsChanged;
        if (toggled is { } value) changed = enabledChanged = SetInstalledPluginEnabled(installed, value) || enabledChanged;
        if (ImGui.IsItemHovered())
            DactUi.Tooltip(text.Get(
                "初始化需要 Simulant 原生内存权限，以及 PostNamazu 游戏命令和原生内存权限。上游 v0.0.4.2 标注支持 7.55；当前游戏版本需以实际扫描结果和实机验证为准。",
                "Initialization requires Simulant native memory access and PostNamazu game-command/native-memory access. Upstream v0.0.4.2 targets 7.55; other game builds require signature checks and in-game verification."));
        return changed;
    }

    private bool DrawExtensionEntry(
        IReadOnlyList<InstalledActPlugin> installedPlugins,
        string pluginId,
        string displayName,
        string description,
        IReadOnlyList<ActCapability>? capabilities,
        out bool enabledChanged)
    {
        var s = DactUi.S;
        var installed = installedPlugins.FirstOrDefault(plugin =>
            string.Equals(plugin.Manifest.Id, pluginId, StringComparison.OrdinalIgnoreCase));
        var changed = false;
        enabledChanged = false;
        var resourceStatus = getBundledActResourceStatus();
        var permissionsChanged = false;
        if (resourceStatus.State != ResourcePackOperationState.Ready)
        {
            // Extension components are not on disk yet: the row offers the download instead.
            var downloading = resourceStatus.State == ResourcePackOperationState.Downloading;
            var state = downloading
                ? (text.Format($"下载中 {resourceStatus.ProgressPercent}%", $"Downloading {resourceStatus.ProgressPercent}%"), PillTone.Warn)
                : (text.Get("不可用", "Unavailable"), PillTone.Off);
            (string, string, Action)? action = downloading
                ? ("close", text.Get("取消下载", "Cancel download"), cancelBundledActResourceDownload)
                : resourceStatus.State == ResourcePackOperationState.Unavailable
                    ? ("download", text.Get("下载扩展组件", "Download extension components"), startBundledActResourceDownload)
                    : null;
            DrawExtensionRow($"extension-{pluginId}", displayName, null, description, null, false, false, state, null, false, null, () => { },
                ref permissionsChanged, action);
            return false;
        }

        if (installed is null)
        {
            DrawExtensionRow($"extension-{pluginId}", displayName, null, description, null, false, false,
                (text.Get("未安装", "Not installed"), PillTone.Off), null, false, null, () => { }, ref permissionsChanged);
            return false;
        }

        var needsConsent = NeedsManualPluginConsent(installed);
        var enabled = installed.Enabled && !needsConsent;
        var run = needsConsent ? (text.Get("未授权", "Not authorized"), PillTone.Warn, (string?)null) : ExtensionRunState(pluginId, enabled);
        var toggled = DrawExtensionRow($"extension-{pluginId}", displayName, installed, run.Item3 ?? description,
            run.Item3 is null ? null : ExtensionNoteColor(run.Item2), enabled, true, (run.Item1, run.Item2), capabilities,
            needsConsent, () => openPluginConfiguration(pluginId), () => requestPluginAuthorization(installed.Manifest.Id), ref permissionsChanged);
        enabledChanged |= permissionsChanged;
        if (toggled is { } value) changed = enabledChanged = SetInstalledPluginEnabled(installed, value) || enabledChanged;
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
        DactUi.Tooltip(text.Format(
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
            DactTheme.TextColored(IceBlue, title);
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

    // 账号与云同步 (screens.js cloud): 账号 and 同步 on the left, 备份与恢复 and Discord on the
    // right, 邀请好友 across the bottom. 支持 DACT moved to the 设置 page.
    private bool DrawCloud()
    {
        var snapshot = cloud.GetSnapshot();
        DactUi.PageHeader(
            text.Get("账号与云同步", "Account & Cloud"),
            text.Get(
                "自动保护常用配置，管理最近两份备份，并在需要时安全恢复。",
                "Automatically protect settings, manage the latest two backups, and restore safely when needed."));
        if (!string.IsNullOrWhiteSpace(snapshot.RecoveryKeyToSave)) DrawCloudRecoveryKeyCard(snapshot.RecoveryKeyToSave);
        if (DactUi.BeginGrid("cloud-grid", 14))
        {
            using (DactUi.Gap(14))
            {
                DrawCloudAccountCard(snapshot);
                DrawCloudSummaryCard(snapshot);
            }
            DactUi.NextColumn();
            using (DactUi.Gap(14))
            {
                DrawCloudBackupCard(snapshot);
                DrawCloudDiscordRecovery(snapshot);
            }
            DactUi.EndGrid();
        }
        DrawCloudInvitationCard(snapshot);
        return false;
    }

    private bool cloudChangePasswordOpen;

    private void DrawCloudAccountCard(CloudClientSnapshot snapshot)
    {
        var s = DactUi.S;
        DactUi.BeginCard("cloud-account", text.Get("账号", "Account"));
        var start = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddImageRounded(logoTexture.GetWrapOrEmpty().Handle, start, start + new Vector2(40 * s), Vector2.Zero, Vector2.One,
            ImGui.GetColorU32(Vector4.One), 8 * s);
        ImGui.SetCursorScreenPos(start + new Vector2(52 * s, 0));
        ImGui.BeginGroup();
        using (DactUi.Gap(2))
        {
            // The name keeps its server-authorized sponsor/administrator markers.
            using (DactFonts.Push(DactFonts.H2))
            {
                if (Friends is not null) Friends.DrawAccountName(snapshot);
                else ImGui.TextUnformatted(snapshot.Username ?? string.Empty);
            }
            if (snapshot.SessionExpiresAt is { } expiresAt)
                DactUi.Hint(text.Format(
                    $"登录有效期至 {expiresAt.LocalDateTime:yyyy-MM-dd HH:mm}",
                    $"Session expires {expiresAt.LocalDateTime:yyyy-MM-dd HH:mm}"));
        }
        ImGui.EndGroup();
        ImGui.SetCursorScreenPos(new Vector2(start.X, Math.Max(ImGui.GetItemRectMax().Y, start.Y + 40 * s) + 12 * s));
        var changeLabel = text.Get("修改密码", "Change password");
        if (DactUi.Button(changeLabel + "##cloud-change-password", cloudChangePasswordOpen ? ButtonKind.Primary : ButtonKind.Secondary))
            cloudChangePasswordOpen = !cloudChangePasswordOpen;
        ImGui.SameLine(0, 8 * s);
        if (DactUi.Button(text.Get("退出登录", "Sign out") + "##cloud-sign-out", disabled: snapshot.IsBusy))
        {
            cloud.Logout();
            ClearCloudSecrets();
        }
        if (cloudChangePasswordOpen)
        {
            cloudUsername = snapshot.Username ?? string.Empty;
            DrawCloudPasswordResetForm(snapshot.IsBusy, hasLocalRecoveryKey: true);
        }
        DactUi.EndCard();
    }

    private (string Label, Vector4 Color) ResolveCloudStatus(CloudClientSnapshot snapshot)
    {
        if (!snapshot.IsSignedIn)
        {
            return (text.Get("云同步未登录", "Cloud signed out"), new Vector4(0.62f, 0.66f, 0.71f, 1));
        }
        if (snapshot.StatusIsError)
        {
            return (text.Get("云同步异常", "Cloud error"), DactUi.Danger);
        }
        if (snapshot.IsBusy)
        {
            return (text.Get("云同步处理中", "Cloud working"), Gold);
        }
        if (!configuration.AutoCloudSyncEnabled)
        {
            return (text.Get("自动同步已关闭", "Auto-sync off"), new Vector4(0.62f, 0.66f, 0.71f, 1));
        }
        if (snapshot.Backups.Count == 0)
        {
            return (text.Get("等待首次同步", "Awaiting first sync"), Gold);
        }
        return (text.Get("云同步正常", "Cloud sync ready"), DactTheme.Palette.Accent);
    }

    // 顶栏 · 云同步状态 (features.js cloudPopover): an anchored popover under the header chip,
    // right edges aligned. Status, account, backups and the auto-sync switch, then refresh and a
    // way into 账号与云同步. Clicking its title keeps the hidden-skin discovery of the old popup.
    private Vector2 cloudChipAnchor;

    private void DrawCloudQuickPopup(CloudClientSnapshot snapshot)
    {
        if (cloudQuickPopupRequested)
        {
            DactUi.OpenPopover(CloudQuickPopupId, cloudChipAnchor, alignRight: true);
            cloudQuickPopupRequested = false;
        }
        if (!DactUi.BeginPopover(CloudQuickPopupId, 320)) return;
        var s = DactUi.S;
        var palette = DactTheme.Palette;
        var (statusLabel, _) = ResolveCloudStatus(snapshot);
        var top = ImGui.GetCursorPos();
        var width = ImGui.GetContentRegionAvail().X;
        DactUi.Text(text.Get("云同步状态", "Cloud sync status"), palette.Gold);
        if (ImGui.IsItemClicked()) DiscoverSkin(skinDiscoveries.ClickCloudStatus(configuration.Appearance, Environment.TickCount64));
        var pillWidth = Math.Min(DactUi.PillWidth(statusLabel), width * .6f);
        ImGui.SetCursorPos(new Vector2(top.X + width - pillWidth, top.Y - 2 * s));
        DactUi.Pill(statusLabel, CloudStatusTone(snapshot), maxWidth: pillWidth);
        if (!string.IsNullOrWhiteSpace(snapshot.StatusMessage)) DactUi.Hint(text.SystemMessage(snapshot.StatusMessage));

        CloudInfoRow(text.Get("账号", "Account"), () =>
        {
            if (snapshot.CanUseDact && Friends is not null) Friends.DrawAccountName(snapshot);
            else DactUi.Text(snapshot.IsSignedIn ? snapshot.Username ?? "—" : text.Get("尚未登录", "Signed out"));
        });
        var latest = snapshot.Backups.FirstOrDefault();
        CloudInfoRow(text.Get("最近备份", "Latest backup"), () => DactUi.Text(
            latest is null ? text.Get("暂无", "None") : latest.CreatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm"),
            latest is null ? palette.Muted : palette.Text));
        CloudInfoRow(text.Get("云端版本", "Cloud versions"), () => DactUi.Text($"{snapshot.Backups.Count} / 2"));
        DactUi.Divider();

        var autoSync = configuration.AutoCloudSyncEnabled;
        if (DactUi.ToggleRow(text.Get("打开游戏后自动同步配置", "Sync configuration after game launch"), ref autoSync,
                text.Get("每次打开游戏并登录后检查一次；配置没变化就不上传，退出游戏时不会等待网络。",
                    "Checks once after each game launch and sign-in; unchanged settings are not uploaded, and shutdown never waits for the network.")))
        {
            configuration.AutoCloudSyncEnabled = autoSync;
            saveConfiguration();
        }
        if (snapshot.IsSignedIn)
        {
            if (DactUi.Button(text.Get("刷新状态", "Refresh status") + "##cloud-quick-refresh", icon: "refresh", disabled: snapshot.IsBusy)) cloud.Refresh();
            ImGui.SameLine(0, 8 * s);
        }
        if (DactUi.Button(text.Get("打开云同步", "Open Cloud Sync") + "##cloud-quick-open", ButtonKind.Primary))
        {
            selectedPage = Page.Cloud;
            ImGui.CloseCurrentPopup();
        }
        if (ImGui.IsKeyPressed(ImGuiKey.Escape)) ImGui.CloseCurrentPopup();
        DactUi.EndPopover();
    }

    // Label column (72 px, small muted) beside the value, like dsl infoRow.
    private static void CloudInfoRow(string label, Action value)
    {
        var s = DactUi.S;
        var top = ImGui.GetCursorPos();
        DactUi.DrawText(ImGui.GetWindowDrawList(), ImGui.GetCursorScreenPos(), 18 * s, label, DactUi.HintColor, DactFonts.Small);
        ImGui.SetCursorPos(new Vector2(top.X + 80 * s, top.Y));
        ImGui.BeginGroup();
        value();
        ImGui.EndGroup();
    }

    private static void DrawCloudQuickRow(string label, string value)
    {
        ImGui.TextDisabled(label);
        ImGui.SameLine(122);
        ImGui.TextUnformatted(value);
    }

    private PillTone CloudStatusTone(CloudClientSnapshot snapshot)
        => !snapshot.IsSignedIn || !configuration.AutoCloudSyncEnabled ? PillTone.Off
            : snapshot.StatusIsError ? PillTone.Error
            : snapshot.IsBusy || snapshot.Backups.Count == 0 ? PillTone.Warn
            : PillTone.Ok;

    private void DrawCloudSummaryCard(CloudClientSnapshot snapshot)
    {
        var s = DactUi.S;
        var palette = DactTheme.Palette;
        var (statusLabel, _) = ResolveCloudStatus(snapshot);
        DactUi.BeginCard("cloud-summary", null);
        DactUi.CardTitleRow(text.Get("同步", "Sync"), () => DactUi.Pill(statusLabel, CloudStatusTone(snapshot)));

        // Three small figures side by side: label (small, muted) over value.
        var stats = new (string Label, string Value, Vector4 Color)[]
        {
            (text.Get("自动同步", "Auto-sync"), configuration.AutoCloudSyncEnabled ? text.Get("已开启", "On") : text.Get("已关闭", "Off"),
                configuration.AutoCloudSyncEnabled ? palette.Accent : palette.Muted),
            (text.Get("云端版本", "Cloud versions"), $"{snapshot.Backups.Count} / 2", palette.Text),
            (text.Get("最近备份", "Latest backup"), snapshot.Backups.FirstOrDefault() is { } latest
                ? latest.CreatedAt.LocalDateTime.ToString("MM-dd HH:mm") : text.Get("暂无", "None"),
                snapshot.Backups.Count > 0 ? palette.Text : palette.Muted),
        };
        var origin = ImGui.GetCursorScreenPos();
        var column = (ImGui.GetContentRegionAvail().X - 24 * s) / 3;
        var draw = ImGui.GetWindowDrawList();
        for (var i = 0; i < stats.Length; i++)
        {
            var at = origin + new Vector2(i * (column + 12 * s), 0);
            string label, value;
            using (DactFonts.Push(DactFonts.Small)) label = DactUi.Fit(stats[i].Label, column);
            value = DactUi.Fit(stats[i].Value, column);
            DactUi.DrawText(draw, at, 16 * s, label, palette.Muted, DactFonts.Small);
            DactUi.DrawText(draw, at + new Vector2(0, 18 * s), 18 * s, value, stats[i].Color);
        }
        ImGui.Dummy(new Vector2(ImGui.GetContentRegionAvail().X, 36 * s));

        var autoCloudSyncEnabled = configuration.AutoCloudSyncEnabled;
        if (DactUi.ToggleRow(text.Get("打开游戏后自动同步配置", "Sync configuration after game launch"), ref autoCloudSyncEnabled,
                text.Get("每次打开游戏并登录后检查一次；配置没变化就不上传，退出游戏不会被阻塞。",
                    "Checks once after each game launch and sign-in; unchanged settings are not uploaded, and quitting the game is never delayed.")))
        {
            configuration.AutoCloudSyncEnabled = autoCloudSyncEnabled;
            saveConfiguration();
        }

        var uploadLabel = snapshot.IsBusy ? text.Get("处理中…", "Working…") : text.Get("上传当前配置", "Upload current configuration");
        if (DactUi.Button(uploadLabel + "###CloudUploadCurrent", ButtonKind.Primary, "upload", disabled: snapshot.IsBusy)) cloud.Upload();
        if (ImGui.IsItemHovered())
            DactUi.Tooltip(text.Get(
                "手动上传会短暂停止解析器与扩展，完成后自动恢复。",
                "Manual upload briefly stops parsers and extensions, then restores them."));
        var refreshLabel = text.Get("刷新状态", "Refresh status");
        DactUi.SameLineIfFits(DactUi.ButtonWidth(refreshLabel, icon: "refresh"));
        if (DactUi.Button(refreshLabel + "##cloud-refresh", icon: "refresh", disabled: snapshot.IsBusy)) cloud.Refresh();
        // The result of the last action (upload, refresh, restore) stays readable under the buttons.
        var statusMessage = text.SystemMessage(snapshot.StatusMessage);
        if (!string.IsNullOrWhiteSpace(statusMessage))
            DactUi.Wrapped(snapshot.IsBusy ? $"{statusMessage}  {text.Get("处理中…", "Working…")}" : statusMessage,
                snapshot.StatusIsError ? DactUi.Danger : DactUi.HintColor, DactFonts.Small);
        DactUi.EndCard();
    }

    private void DrawCloudRecoveryKeyCard(string recoveryKey)
    {
        var s = DactUi.S;
        DactUi.BeginCard("cloud-recovery-key", null);
        DactUi.Wrapped(text.Get(
            "恢复密钥只显示这一次，请立即离线保存",
            "Save this recovery key offline now; it is shown only once"), DactTheme.Palette.Gold);
        var visibleRecoveryKey = recoveryKey;
        var copyLabel = text.Get("复制", "Copy");
        DactUi.Input("cloud-created-recovery-key", ref visibleRecoveryKey, "", 96,
            width: ImGui.GetContentRegionAvail().X - DactUi.ButtonWidth(copyLabel) - 8 * s, readOnly: true);
        ImGui.SameLine(0, 8 * s);
        if (DactUi.Button(copyLabel + "##cloud-copy-recovery", ButtonKind.Primary)) ImGui.SetClipboardText(recoveryKey);
        DactUi.EndCard();
    }

    private void DrawCloudBackupCard(CloudClientSnapshot snapshot)
    {
        var s = DactUi.S;
        DactUi.BeginCard("cloud-backup", text.Get("备份与恢复", "Backup & restore"));
        DactUi.Hint(text.Get(
            "配置在本机加密后上传，服务器无法读取；最多保留最近 2 个不同内容的版本，恢复前始终先预览。",
            "Settings are encrypted on this PC before upload, so the server cannot read them; up to 2 versions with different content are kept, and every restore is previewed first."));

        DactUi.BeginField(text.Get("云端版本", "Cloud version"));
        if (snapshot.Backups.Count == 0)
        {
            var none = 0;
            DactUi.Select("cloud-backups", [text.Get("暂无备份", "No backups")], ref none, disabled: true);
            // The empty-state explanation lives in the (disabled) select's tooltip, as in the design.
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                DactUi.Tooltip(text.Get(
                    "暂无备份。开启自动同步后，下次打开游戏并登录时会自动创建第一份备份。",
                    "No backup yet. With auto-sync enabled, the first backup is created after the next game launch and sign-in."));
            DactUi.EndField();
        }
        else
        {
            if (snapshot.Backups.All(backup => backup.Id != selectedCloudBackupId))
            {
                selectedCloudBackupId = snapshot.Backups[0].Id;
                cloudPreviewRequestedBackupId = null;
                confirmCloudRestore = false;
            }
            var index = snapshot.Backups.ToList().FindIndex(backup => backup.Id == selectedCloudBackupId);
            var labels = snapshot.Backups
                .Select(backup => $"{backup.CreatedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}  {FormatCloudBytes(backup.SizeBytes)}")
                .ToArray();
            if (DactUi.Select("cloud-backups", labels, ref index) && index >= 0)
            {
                selectedCloudBackupId = snapshot.Backups[index].Id;
                cloudPreviewRequestedBackupId = null;
                confirmCloudRestore = false;
            }
            DactUi.EndField();
        }

        var canUseSelection = !snapshot.IsBusy && !string.IsNullOrWhiteSpace(selectedCloudBackupId) && snapshot.Backups.Count > 0;
        if (DactUi.Button(text.Get("预览恢复内容", "Preview restore") + "##cloud-preview", disabled: !canUseSelection))
        {
            cloudPreviewRequestedBackupId = selectedCloudBackupId;
            confirmCloudRestore = false;
            cloud.PreviewRestore(selectedCloudBackupId!);
        }

        if (snapshot.RestorePreview is { } preview && cloudPreviewRequestedBackupId == selectedCloudBackupId)
        {
            DactUi.Wrapped(text.Format(
                $"将新增 {preview.AddedFiles}、修改 {preview.ChangedFiles}、删除 {preview.RemovedFiles} 个文件；" +
                $"共 {preview.FileCount} 个文件。新电脑的日志目录和 ACT 插件目录会保留。",
                $"Adds {preview.AddedFiles}, changes {preview.ChangedFiles}, and removes {preview.RemovedFiles} files; {preview.FileCount} files total. This PC's log and ACT plugin paths are preserved."),
                size: DactFonts.Small);
            foreach (var scope in preview.Scopes)
                DactUi.Hint($"· {scope.RelativePath}: +{scope.AddedFiles} ~{scope.ChangedFiles} -{scope.RemovedFiles} ={scope.UnchangedFiles}");
            if (!confirmCloudRestore)
            {
                if (DactUi.Button(text.Get("恢复这个版本", "Restore this version") + "##cloud-restore", disabled: snapshot.IsBusy))
                    confirmCloudRestore = true;
            }
            else
            {
                DactUi.Wrapped(text.Get(
                    "将覆盖白名单内的本机配置，完成后必须重载 DACT。",
                    "Whitelisted local settings will be replaced; DACT must be reloaded afterward."), DactUi.Danger, DactFonts.Small);
                if (DactUi.Button(text.Get("确认覆盖", "Confirm restore") + "##cloud-restore-confirm", ButtonKind.Danger, disabled: snapshot.IsBusy))
                {
                    cloud.Restore(selectedCloudBackupId!);
                    confirmCloudRestore = false;
                }
                ImGui.SameLine(0, 8 * s);
                if (DactUi.Button(text.Get("取消", "Cancel") + "##cloud-restore-cancel")) confirmCloudRestore = false;
            }
        }

        if (!string.IsNullOrWhiteSpace(snapshot.LastRollbackPath))
        {
            if (!confirmCloudRollback)
            {
                if (DactUi.Button(text.Get("撤销上次恢复", "Undo last restore") + "##cloud-rollback", disabled: snapshot.IsBusy))
                    confirmCloudRollback = true;
            }
            else
            {
                if (DactUi.Button(text.Get("确认回滚", "Confirm rollback") + "##cloud-rollback-confirm", ButtonKind.Danger, disabled: snapshot.IsBusy))
                {
                    cloud.Rollback();
                    confirmCloudRollback = false;
                }
                ImGui.SameLine(0, 8 * s);
                if (DactUi.Button(text.Get("取消###cloud-rollback", "Cancel###cloud-rollback"))) confirmCloudRollback = false;
            }
        }
        DactUi.EndCard();
    }

    private void DrawCloudInvitationCard(CloudClientSnapshot snapshot)
    {
        var s = DactUi.S;
        DactUi.BeginCard("cloud-invitations", text.Get("邀请好友", "Invite friends"));
        if (snapshot.Invitations is not { } invitations)
        {
            DactUi.Hint(text.Get("默认 3 个邀请名额。", "3 invitation slots by default."));
            DactUi.EndCard();
            return;
        }
        DactUi.Hint((invitations.IsAdmin
            ? text.Format($"管理员 · 无限生成 · 已生成 {invitations.Used} 个", $"Administrator · Unlimited · {invitations.Used} created")
            : text.Format(
                $"已使用 {invitations.QuotaUsed ?? invitations.Used}/{invitations.Quota} · 剩余 {invitations.Remaining}",
                $"{invitations.QuotaUsed ?? invitations.Used}/{invitations.Quota} used · {invitations.Remaining} remaining")) +
            (text.IsChinese ? "。" : ". ") + text.Get(
                "每个激活码只能使用一次。请填写受邀好友的游戏 ID 或 QQ ID，方便后续核对。",
                "Each activation key can be used once. Enter the friend's game ID or QQ ID for later verification."));
        var createLabel = text.Get("生成邀请码", "Create invitation key");
        DactUi.Input("cloud-invitee-contact", ref cloudInviteeContact, text.Get("受邀好友的游戏 ID 或 QQ ID", "Invitee game ID or QQ ID"), 81,
            width: ImGui.GetContentRegionAvail().X - DactUi.ButtonWidth(createLabel) - 8 * s);
        var normalizedInviteeContact = cloudInviteeContact.Trim();
        var inviteeContactValid = normalizedInviteeContact.Length is >= 2 and <= 80;
        ImGui.SameLine(0, 8 * s);
        if (DactUi.Button(createLabel + "##cloud-invite", disabled: snapshot.IsBusy || !invitations.CanGenerate || !inviteeContactValid))
            cloud.CreateInvitation(normalizedInviteeContact);
        if (!inviteeContactValid && !string.IsNullOrWhiteSpace(cloudInviteeContact))
            DactUi.Wrapped(text.Get("游戏 ID 或 QQ ID 长度需要为 2 到 80 个字符。", "The game ID or QQ ID must contain 2 to 80 characters."),
                DactTheme.Palette.Gold, DactFonts.Small);
        if (!string.IsNullOrWhiteSpace(snapshot.InvitationKeyToShare))
        {
            var invitationKey = snapshot.InvitationKeyToShare;
            var copyLabel = text.Get("复制###copy-invitation", "Copy###copy-invitation");
            DactUi.Input("cloud-created-invitation-key", ref invitationKey, "", 96,
                width: ImGui.GetContentRegionAvail().X - DactUi.ButtonWidth(copyLabel) - 8 * s, readOnly: true);
            ImGui.SameLine(0, 8 * s);
            if (DactUi.Button(copyLabel, ButtonKind.Primary)) ImGui.SetClipboardText(snapshot.InvitationKeyToShare);
            DactUi.Hint(text.Get("完整激活码只显示这一次，请现在发给好友。", "The complete key is shown once; share it now."));
        }
        foreach (var invitation in invitations.Invitations)
            DactUi.Hint($"· {invitation.Name} · {invitation.InviteeContact ?? "—"} · …{invitation.CodeHint} · {FormatInvitationStatus(invitation.Status)}");
        DactUi.EndCard();
    }

    // 支持 DACT card (设置 page in 1.0).
    private void DrawSupportCard()
    {
        DactUi.BeginCard("settings-support", text.Get("支持 DACT", "Support DACT"));
        DactUi.Hint(text.Get("用于服务器运行与后续开发。", "Keeps the servers running and funds further development."));
        if (DactUi.Button(text.Get("爱发电支持", "Support on Afdian") + "##support-afdian", icon: "heart"))
        {
            // Opening the support page does not grant benefits; sponsorship
            // remains a permanent account grant verified by the administrator.
            OpenUrl(AfdianSupportUrl);
        }
        if (ImGui.IsItemHovered())
        {
            DactUi.Tooltip(text.Format(
                $"在浏览器中打开爱发电。\n赞助后请联系管理员核对账号并开通等级；{SkinCatalog.EorzeaSponsorTier} 级永久解锁艾欧泽亚皮肤。",
                $"Open Afdian in your browser.\nAfter sponsoring, contact the administrator to verify your account; tier {SkinCatalog.EorzeaSponsorTier} permanently unlocks the Eorzea skin.") +
                "\n" + text.Get(
                    "你的支持将用于账号与加密备份服务器的持续运行，以及 DACT 的维护和后续开发。",
                    "Your support helps keep the account and encrypted-backup server online and funds ongoing DACT maintenance and development.") +
                "\n" + text.Get(
                    "支持者可以联系管理员，申请增加超过默认 3 个的好友邀请名额。",
                    "Supporters may contact the administrator to request more friend-invite slots beyond the default 3.") +
                "\n" + text.Get(
                    "支持不会解除封禁、跳过风控或授予管理员权限。",
                    "Support does not remove bans, bypass risk controls, or grant administrator privileges."));
        }
        DactUi.EndCard();
    }

    private void ClearCloudSecrets()
    {
        cloudShowPassword = false;
        cloudResetPasswordStep = false;
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
        DactTheme.TextColored(Gold, text.Get("账号", "Account"));
        if (Friends is not null) Friends.DrawAccountName(snapshot);
        else ImGui.TextUnformatted(snapshot.Username ?? string.Empty);
        if (snapshot.SessionExpiresAt is { } expiresAt)
        {
            ImGui.TextDisabled(text.Format(
                $"登录有效期至 {expiresAt.LocalDateTime:yyyy-MM-dd HH:mm}",
                $"Session expires {expiresAt.LocalDateTime:yyyy-MM-dd HH:mm}"));
        }
        if (!snapshot.IsBusy && DactTheme.Button(text.Get("退出登录", "Sign out")))
        {
            cloud.Logout();
            ClearCloudSecrets();
        }

        if (ImGui.CollapsingHeader(text.Get(
                "修改密码",
                "Change password")))
        {
            cloudUsername = snapshot.Username ?? string.Empty;
            DrawCloudPasswordResetForm(snapshot.IsBusy, hasLocalRecoveryKey: true);
        }
    }

    private bool DrawSettingsPage()
    {
        if (!skinSettings.IsOpen) return DrawDiagnostics();
        var changed = skinSettings.Draw(configuration.Appearance, cloud.GetSnapshot(), text, cloud.Refresh,
            () => DiscoverSkin(skinDiscoveries.ClickAppearanceTitle(configuration.Appearance, Environment.TickCount64)),
            () => DiscoverSkin(skinDiscoveries.ClickAppearanceBreadcrumb(configuration.Appearance, Environment.TickCount64)),
            () => DiscoverSkin(skinDiscoveries.ClickAppearanceHint(configuration.Appearance, Environment.TickCount64)));
        if (!skinSettings.IsOpen) resetSettingsScroll = true;
        return changed;
    }

    internal bool NeedsGlassPreview => skinSettings.IsOpen &&
        skinSettings.PreviewSkinId is SkinCatalog.LiquidGlass or SkinCatalog.RainyWindow &&
        configuration.Appearance.UnlockedEasterEggs.Contains(skinSettings.PreviewSkinId);

    // Paths for 文件位置; set by the plugin (absent in screenshot tests).
    internal Infrastructure.Storage.PluginPaths? Paths { get; set; }

    private const string FactoryResetDialog = "factory-reset-confirm";
    private bool fileLocationsOpen;

    // 设置 (screens.js settings): two columns of cards, 解析器与诊断, then the danger zone. The
    // account moved to 账号与云同步 and 来信提醒 to its own page under 好友.
    private bool DrawDiagnostics()
    {
        if (scenesPage) return DrawScenesPage();
        var changed = false;
        DactUi.PageHeader(text.Get("设置", "Settings"),
            text.Get("外观、语言、提醒、日志与恢复选项。", "Appearance, language, notifications, logs and recovery options."));
        if (DactUi.BeginGrid("settings-grid", 14))
        {
            using (DactUi.Gap(14))
            {
                skinSettings.DrawCard(configuration.Appearance, cloud.GetSnapshot(), text);
                if (skinSettings.IsOpen)
                {
                    resetSettingsScroll = true;
                    DactUi.EndGrid();
                    return false;
                }
                changed |= DrawGeneralSettingsCard();
            }
            DactUi.NextColumn();
            using (DactUi.Gap(14))
            {
                changed |= DrawDataAndLogsCard();
                DrawSupportCard();
            }
            DactUi.EndGrid();
        }
        DrawParserDiagnosticsCard();
        changed |= DrawScenePresetCard();
        DrawFactoryResetZone();
        return changed;
    }

    private bool DrawGeneralSettingsCard()
    {
        var s = DactUi.S;
        var changed = false;
        DactUi.BeginCard("settings-general", text.Get("通用", "General"));
        changed |= UiLanguageSelector.DrawField(configuration, text);
        changed |= DrawSearchShortcutField();
        changed |= DrawSplashAnimationField();
        var showLauncher = configuration.ShowLauncherButton;
        if (DactUi.ToggleRow(text.Get("显示 ACT 快捷按钮", "Show ACT quick button"), ref showLauncher,
                text.Get("左键打开控制中心、右键显示或隐藏战斗统计、按住中键拖动。", "Left-click opens the control center, right-click shows or hides the Combat Meter, hold the middle button to move it.")))
        {
            configuration.ShowLauncherButton = showLauncher;
            changed = true;
        }
        DactUi.BeginField(text.Get("快捷按钮尺寸", "Quick-button size"));
        var launcherButtonSize = configuration.LauncherButtonSize;
        if (DactUi.SliderInt("launcher-size", ref launcherButtonSize, 56, 128, text.Format($"{launcherButtonSize} 像素", $"{launcherButtonSize} px")))
        {
            configuration.LauncherButtonSize = launcherButtonSize;
            changed = true;
        }
        DactUi.EndField();
        if (DactUi.Button(text.Get("重置快捷按钮大小与位置", "Reset quick-button size and position") + "##launcher-reset", icon: "reset"))
        {
            configuration.LauncherPositionX = 80;
            configuration.LauncherPositionY = 160;
            configuration.LauncherButtonSize = 80;
            configuration.ShowLauncherButton = true;
            changed = true;
        }
        var debug = configuration.DebugMode;
        if (DactUi.ToggleRow(text.Get("调试模式", "Debug mode"), ref debug)) { configuration.DebugMode = debug; changed = true; }
        if (configuration.DebugMode)
        {
            var parity = configuration.EnableFflogsParityRecorder;
            if (DactUi.ToggleRow(text.Get("记录 FFLogs parity 诊断", "Record FFLogs parity diagnostics"), ref parity))
            {
                configuration.EnableFflogsParityRecorder = parity;
                changed = true;
            }
            if (ImGui.IsItemHovered())
                DactUi.Tooltip(text.Get(
                    "仅在手动启用后记录；普通 Debug 不会自动启动 parity 工具。",
                    "Records only when explicitly enabled; ordinary Debug does not start the parity tool."));
            DrawCombatQuality();
        }
        DactUi.EndCard();
        return changed;
    }

    // 开屏动画 (user request): every open, the first open after each game launch (1.0's behaviour
    // and the default), or off. Items follow SplashAnimationMode's order.
    private bool DrawSplashAnimationField()
    {
        string[] modes =
        [
            text.Get("每次启动游戏后首次打开时播放", "On the first open after each game launch"),
            text.Get("每次打开控制中心都播放", "Every time the control center opens"),
            text.Get("关闭", "Off"),
        ];
        DactUi.BeginField(text.Get("开屏动画", "Splash animation"));
        var mode = Math.Clamp((int)configuration.SplashAnimation, 0, modes.Length - 1);
        var changed = DactUi.Select("splash-animation", modes, ref mode) && mode != (int)configuration.SplashAnimation;
        if (changed) configuration.SplashAnimation = (SplashAnimationMode)mode;
        DactUi.EndField(text.Get("播放时点一下即可跳过。", "Click the animation to skip it."));
        return changed;
    }

    // 全局搜索快捷键: a switch (the user wants it possible to turn off), and while it is on a
    // recorder: click, then press the new chord; Esc cancels. A chord needs Ctrl or Alt, so a plain
    // letter never fires while playing.
    private bool recordingSearchShortcut;
    private HashSet<int> recordingHeldKeys = [];
    private string? searchShortcutError;

    private bool DrawSearchShortcutField()
    {
        var s = DactUi.S;
        var changed = false;
        const OverlayHotkeyModifiers defaultModifiers = OverlayHotkeyModifiers.Control | OverlayHotkeyModifiers.Shift;
        var enabled = configuration.SearchHotkeyActive();
        if (DactUi.ToggleRow(text.Get("全局搜索快捷键", "Search shortcut"), ref enabled, text.Get(
                "在游戏里任何时候都能按，不用先打开 DACT，只弹出搜索框；再按一次、按 Esc 或点别处关闭。",
                "Works anywhere in the game without opening DACT and shows only the search box; press it again, Esc or click elsewhere to close.")))
        {
            configuration.SearchHotkeyEnabled = enabled;
            // A chord switched off by an earlier build (key 0) comes back as the default.
            if (enabled && configuration.SearchHotkeyKey == 0)
            {
                configuration.SearchHotkeyKey = 0x4B;
                configuration.SearchHotkeyModifiers = defaultModifiers;
            }
            recordingSearchShortcut = false;
            searchShortcutError = null;
            changed = true;
        }
        if (!configuration.SearchHotkeyActive()) return changed;
        ImGui.BeginGroup();
        var label = recordingSearchShortcut
            ? text.Get("请按下新的组合键…（Esc 取消）", "Press the new shortcut… (Esc cancels)")
            : ShortcutLabel;
        if (DactUi.Button(label + "##search-shortcut-record", recordingSearchShortcut ? ButtonKind.Primary : ButtonKind.Secondary, "edit"))
        {
            recordingSearchShortcut = !recordingSearchShortcut;
            recordingHeldKeys = HeldKeyboardKeys();
            searchShortcutError = null;
        }
        if (!recordingSearchShortcut && (configuration.SearchHotkeyKey != 0x4B || configuration.SearchHotkeyModifiers != defaultModifiers))
        {
            ImGui.SameLine(0, 8 * s);
            if (DactUi.Button(text.Get("恢复默认", "Reset to default") + "##search-shortcut-default", ButtonKind.Ghost))
            {
                configuration.SearchHotkeyKey = 0x4B;
                configuration.SearchHotkeyModifiers = defaultModifiers;
                changed = true;
            }
        }
        if (recordingSearchShortcut)
        {
            // Only a key pressed after recording began counts; the click's own keys are ignored.
            var held = HeldKeyboardKeys();
            foreach (var key in held.Except(recordingHeldKeys).Where(key => key is not (0x10 or 0x11 or 0x12 or 0x5B or 0x5C)))
            {
                var modifiers = (SearchShortcut ?? new SearchHotkey(configuration)).CurrentModifiers();
                if (key == 0x1B) recordingSearchShortcut = false;
                else if ((modifiers & (OverlayHotkeyModifiers.Control | OverlayHotkeyModifiers.Alt)) == 0)
                    searchShortcutError = text.Get("组合键需要包含 Ctrl 或 Alt，以免打字或操作角色时误触。", "The shortcut needs Ctrl or Alt so typing or playing never triggers it.");
                else
                {
                    configuration.SearchHotkeyKey = key;
                    configuration.SearchHotkeyModifiers = modifiers;
                    recordingSearchShortcut = false;
                    searchShortcutError = null;
                    changed = true;
                }
                break;
            }
            recordingHeldKeys = held;
            if (SearchShortcut is { } hotkey) hotkey.SuspendedThroughFrame = ImGui.GetFrameCount() + 1;
        }
        if (searchShortcutError is { } error) DactUi.Wrapped(error, DactUi.Danger, DactFonts.Small);
        ImGui.EndGroup();
        return changed;
    }

    private static HashSet<int> HeldKeyboardKeys()
    {
        var held = new HashSet<int>();
        for (var key = 8; key < 255; key++)
            if (HtmlOverlayHotkey.IsKeyboardKey(key) && SearchHotkey.IsKeyDown(key)) held.Add(key);
        return held;
    }

    private bool DrawDataAndLogsCard()
    {
        var s = DactUi.S;
        DactUi.BeginCard("settings-data", text.Get("数据与日志", "Data & logs"));
        DrawHistoryLimitEditor();
        DrawCombatLogDirectorySettings();

        if (DactUi.Button(text.Get("复制诊断日志", "Copy diagnostic log") + "##copy-diagnostics")) CopyDiagnosticReport();
        if (ImGui.IsItemHovered())
            DactUi.Tooltip(text.Get(
                "把本插件相关的运行状态与近期日志打包成 .txt 文件并复制，在 QQ、Discord 等聊天框里按 Ctrl+V 即可作为文件发送；不含战斗日志和配置文件，并会遮盖用户路径与常见凭据。原始战斗数据问题可使用右侧按钮打开日志文件夹。",
                "Packs recent plugin diagnostics and runtime state into a .txt file and copies the file, so Ctrl+V in QQ, Discord or another chat sends it as an attachment. Combat logs and configuration files are excluded, and user paths and common credentials are redacted. For raw combat-data issues, use the button on the right to open the log folder."));
        var openLabel = text.Get("打开日志文件夹", "Open log folder");
        DactUi.SameLineIfFits(DactUi.ButtonWidth(openLabel, icon: "folder"));
        if (DactUi.Button(openLabel + "##open-logs", icon: "folder")) openLogDirectory();
        if (!string.IsNullOrWhiteSpace(diagnosticCopyFeedback))
            DactUi.Wrapped(diagnosticCopyFeedback, diagnosticCopyFeedbackIsError ? DactUi.Danger : DactTheme.Palette.Accent, DactFonts.Small);

        DactUi.Disclosure(text.Get("文件位置（配置、调试日志、战斗日志）", "File locations (config, debug logs, combat logs)"), ref fileLocationsOpen);
        if (fileLocationsOpen)
        {
            var locations = new List<(string Label, string Path)>();
            if (Paths is { } paths)
            {
                locations.Add((text.Get("配置", "Config"), paths.ConfigDirectory));
                locations.Add((text.Get("调试日志", "Debug logs"), paths.LogDirectory));
            }
            locations.Add((text.Get("战斗日志", "Combat logs"), getCombatLogDirectory()));
            foreach (var (label, location) in locations)
            {
                DactUi.Text(label, DactTheme.Palette.Muted, DactFonts.Small);
                DactUi.PathField(location);
            }
        }
        DactUi.EndCard();
        return false;
    }

    private void DrawParserDiagnosticsCard()
    {
        var s = DactUi.S;
        DactUi.BeginCard("settings-parser", null);
        var running = parserStatus.State == ParserState.Running;
        DactUi.Text(text.Get("解析器与诊断", "Parser & diagnostics"), DactTheme.Palette.Gold);
        // State pill with the parser's own message beside it, as in the design.
        DactUi.Pill(text.Format($"解析器{LocalizeState(parserStatus.State)}", $"Parser {LocalizeState(parserStatus.State).ToLowerInvariant()}"),
            running ? PillTone.Ok : parserStatus.State == ParserState.Faulted ? PillTone.Error : PillTone.Warn);
        ImGui.SameLine(0, 10 * s);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (22 * s - ImGui.GetTextLineHeight()) * .5f);
        DactUi.Wrapped(parserStatus.Message, DactUi.HintColor, DactFonts.Small, ImGui.GetContentRegionAvail().X);
        if (!string.IsNullOrWhiteSpace(parserStatus.Detail)) DactUi.Hint(parserStatus.Detail);
        if (DactUi.Button(text.Get("重启解析器", "Restart parser") + "##restart-parser", icon: "reset")) RestartParser();
        var visible = isStatusVisible();
        var statusLabel = visible ? text.Get("关闭详细运行状态", "Close detailed runtime status") : text.Get("详细运行状态", "Detailed runtime status");
        DactUi.SameLineIfFits(DactUi.ButtonWidth(statusLabel, icon: "pulse"));
        if (DactUi.Button(statusLabel + "##detailed-status", visible ? ButtonKind.Primary : ButtonKind.Secondary, "pulse")) setStatusVisible(!visible);
        var noticeLabel = text.Get("三方扩展声明", "Third-party extension notice");
        DactUi.SameLineIfFits(DactUi.ButtonWidth(noticeLabel));
        if (DactUi.Button(noticeLabel + "##settings-notice")) openBundledPluginNotice();
        DrawMyFeedbackRow();
        DactUi.EndCard();
    }

    private void DrawFactoryResetZone()
    {
        var s = DactUi.S;
        var width = ImGui.GetContentRegionAvail().X;
        var start = ImGui.GetCursorScreenPos();
        var buttonLabel = text.Get("恢复出厂设置...", "Restore factory settings...");
        var textWidth = width - DactUi.ButtonWidth(buttonLabel, ButtonKind.Danger) - 40 * s;
        // Danger zone (dsl: dangerSoft panel with a red edge); the button opens a centred confirmation.
        ImGui.SetCursorScreenPos(start + new Vector2(14 * s));
        ImGui.BeginGroup();
        using (DactUi.Gap(2))
        {
            DactUi.Text(text.Get("恢复出厂设置", "Factory reset"), DactUi.Danger);
            DactUi.Wrapped(text.Get(
                "恢复出厂设置会停止 ACT 宿主、备份所有可变数据，并恢复两个系统插件和默认设置。",
                "Factory reset stops the ACT host, backs up all mutable data, and restores the two system plugins and default settings."),
                DactUi.HintColor, DactFonts.Small, textWidth);
            if (!string.IsNullOrWhiteSpace(factoryResetResult))
                DactUi.Wrapped($"{text.Get("最近一次出厂备份", "Last factory reset backup")}: {factoryResetResult}", DactUi.HintColor, DactFonts.Small, textWidth);
        }
        ImGui.EndGroup();
        var height = ImGui.GetItemRectSize().Y + 28 * s;
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(start, start + new Vector2(width, height), DactUi.U32(DactUi.DangerSoft), 10 * s);
        draw.AddRect(start, start + new Vector2(width, height), DactUi.U32(DactUi.Danger with { W = .35f }), 10 * s, ImDrawFlags.None, Math.Max(1, s));
        ImGui.SetCursorScreenPos(new Vector2(start.X + width - 14 * s - DactUi.ButtonWidth(buttonLabel, ButtonKind.Danger), start.Y + (height - 30 * s) * .5f));
        if (DactUi.Button(buttonLabel + "##factory-reset", ButtonKind.Danger)) DactUi.OpenDialog(FactoryResetDialog);
        DrawFactoryResetDialog();
        ImGui.SetCursorScreenPos(start);
        ImGui.Dummy(new Vector2(width, height));
    }

    private void DrawFactoryResetDialog()
    {
        var s = DactUi.S;
        if (!DactUi.BeginDialog(FactoryResetDialog)) return;
        DactUi.Text(text.Get("恢复出厂设置", "Factory reset"), DactUi.Danger, DactFonts.H2);
        DactUi.Wrapped(text.Get(
            "恢复出厂设置会停止 ACT 宿主、备份所有可变数据，并恢复两个系统插件和默认设置。",
            "Factory reset stops the ACT host, backs up all mutable data, and restores the two system plugins and default settings."));
        DactUi.Hint(text.Get(
            "按确认继续。此前状态仍可从备份目录恢复。",
            "Press confirm to continue. The previous state remains recoverable from the backup directory."));
        if (!string.IsNullOrWhiteSpace(factoryResetResult))
            DactUi.Hint($"{text.Get("最近一次出厂备份", "Last factory reset backup")}: {factoryResetResult}");
        if (DactUi.Button(text.Get("取消", "Cancel") + "##factory-cancel")) DactUi.CloseDialog(FactoryResetDialog);
        ImGui.SameLine(0, 8 * s);
        if (DactUi.Button(text.Get("确认恢复", "Confirm factory reset") + "##factory-confirm", ButtonKind.Danger))
        {
            DactUi.CloseDialog(FactoryResetDialog);
            _ = RunFactoryResetAsync();
        }
        DactUi.EndDialog(FactoryResetDialog);
    }

    private void DrawCombatLogDirectorySettings()
    {
        var s = DactUi.S;
        DactUi.BeginField(text.Get("FFLogs 上传日志目录", "FFLogs upload log folder"));
        DactUi.PathField(getCombatLogDirectory());
        DactUi.EndField();
        if (DactUi.Button(text.Get("更改目录...", "Change directory...") + "##log-dir-change")) selectCombatLogDirectory(ReportCombatLogDirectoryChange);
        if (ImGui.IsItemHovered())
            DactUi.Tooltip(text.Get(
                "只更改之后写入的原始 Network 日志；旧日志不会移动或删除。解析器正在运行时会自动重启，战斗中切换可能截断当前战斗。",
                "Changes only future raw Network log writes; existing logs are not moved or deleted. A running parser restarts automatically, which may split the current encounter."));
        // Long translations (Standard wiederherstellen) take the next line instead of overflowing the card.
        DactUi.SameLineIfFits(DactUi.ButtonWidth(text.Get("恢复默认", "Restore default")));
        if (DactUi.Button(text.Get("恢复默认", "Restore default") + "##log-dir-reset")) resetCombatLogDirectory(ReportCombatLogDirectoryChange);
        var feedback = Volatile.Read(ref combatLogDirectoryChangeFeedback);
        if (feedback is not null)
            DactUi.Wrapped(feedback.Message, feedback.IsError ? DactUi.Danger : DactTheme.Palette.Accent, DactFonts.Small);
    }

    private void ReportCombatLogDirectoryChange(bool success, string message)
        => Volatile.Write(
            ref combatLogDirectoryChangeFeedback,
            new CombatLogDirectoryFeedback(message, !success));

    private void CopyDiagnosticReport()
    {
        try
        {
            var export = exportDiagnosticReport();
            var fileName = Path.GetFileName(export.FilePath);
            if (FileClipboard.TrySetFiles([export.FilePath]))
            {
                diagnosticCopyFeedback = text.Format(
                    $"已复制诊断日志文件 {fileName}，在聊天框或反馈页按 Ctrl+V 即可作为文件发送。",
                    $"Diagnostic log file {fileName} copied; press Ctrl+V in a chat or the issue report to send it as a file.");
                logger.Information("Bounded diagnostic report saved and copied to the clipboard as a file.");
            }
            else
            {
                // Some setups refuse file clipboard data; the text still gets the report across.
                ImGui.SetClipboardText(export.Report);
                diagnosticCopyFeedback = text.Format(
                    $"无法以文件形式复制，已改为复制文字；文件 {fileName} 保存在日志文件夹的 {DiagnosticReportFile.ExportDirectoryName} 里。",
                    $"Could not copy as a file, so the text was copied instead; {fileName} is saved in the log folder under {DiagnosticReportFile.ExportDirectoryName}.");
                logger.Warning("Clipboard refused the diagnostic report file; copied its text instead.");
            }

            diagnosticCopyFeedbackIsError = false;
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
        DactTheme.TextColored(Gold, title);
        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        ImGui.TextWrapped(description);
        ImGui.PopStyleColor();
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
        if (DactTheme.Button(label, size))
        {
            setStatusVisible(!visible);
        }

        if (visible)
        {
            PopOpenWindowButtonStyle();
        }
    }

    private static void PushOpenWindowButtonStyle()
    {
        // Keep an already-open window distinguishable from a normal action in
        // solid skins; closing on hover still uses the existing red feedback.
        if (DactTheme.UsesSolidControls)
            ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Lerp(DactTheme.Palette.Hover, DactTheme.Palette.Accent, .24f));
        else DactTheme.PushStyleColor(ImGuiCol.Button, new Vector4(0.11f, 0.29f, 0.38f, 1));
        DactTheme.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.56f, 0.16f, 0.18f, 1));
        DactTheme.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.68f, 0.18f, 0.20f, 1));
        DactTheme.PushStyleColor(ImGuiCol.Text, new Vector4(0.94f, 0.98f, 1, 1));
        // Ultimate skins draw open-window buttons with their lit state, not a recoloured fill.
        DactTheme.OpenButtonDepth++;
    }

    private static void PopOpenWindowButtonStyle()
    {
        DactTheme.OpenButtonDepth--;
        ImGui.PopStyleColor(4);
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
        if (!DactTheme.Checkbox(label, ref value))
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
        var modes = Enum.GetValues<PlayerIdentityMode>();
        DactUi.BeginField(text.Get("玩家 ID 显示", "Player identity"));
        var index = Array.IndexOf(modes, configuration.Meter.PlayerIdentityMode);
        if (DactUi.Select("player-identity", modes.Select(PlayerIdentityModeLabel).ToArray(), ref index) &&
            index >= 0 && modes[index] != configuration.Meter.PlayerIdentityMode)
        {
            configuration.Meter.PlayerIdentityMode = modes[index];
            changed = true;
        }
        DactUi.EndField(text.Get(
            "遮盖只影响界面显示，不修改解析数据和保存的战斗日志。",
            "Masking affects display only; parsed data and saved encounter logs are unchanged."));
        if (configuration.Meter.PlayerIdentityMode != PlayerIdentityMode.Anonymous) return changed;

        DactUi.BeginField(text.Get("自己的代称", "Your alias"));
        var alias = configuration.Meter.LocalPlayerAlias;
        if (DactUi.Input("local-player-alias", ref alias, text.Get("自己的代称", "Your alias"), 32))
        {
            configuration.Meter.LocalPlayerAlias = alias;
            changed = true;
        }
        DactUi.EndField();
        return changed;
    }

    private bool fflogsGuideOpen;

    // FFLogs Parse 预估 card: status pill in the title, switch, guide, credentials, duty matching.
    private bool DrawFflogsSettings()
    {
        var s = DactUi.S;
        var changed = false;
        var status = fflogsEstimateService.Status;
        DactUi.BeginCard("meter-fflogs", null);
        DactUi.CardTitleRow(text.Get("FFLogs Parse 预估", "FFLogs Parse estimate"),
            () => DactUi.Pill(FflogsStatusShort(status.State), FflogsStatusTone(status.State)));
        DactUi.Hint(text.Get(
            "用本场实际 DPS 对照 FFLogs 同职业、同副本、同分区的分布估算 Parse；缓存缺失时显示“--”。",
            "Estimates the Parse from this fight's actual DPS against the FFLogs distribution for the same job, duty and partition; shows \"--\" when no cache is available."));

        var reference = fflogsEstimateService.ReferenceSnapshot;
        var referenceDate = reference.LatestDataUpdatedAt is { } updatedAt ? updatedAt.ToLocalTime().ToString("yyyy/MM/dd") : "--";
        var partitionLabel = reference.Partition?.ToString() ?? text.Get("最新", "Latest");
        DactUi.Pill(text.Format(
                $"区域 {reference.Region} · 分区 {partitionLabel} · 指标 {reference.Metric}",
                $"Region {reference.Region} · Partition {partitionLabel} · Metric {reference.Metric}"),
            PillTone.Off, dot: false, maxWidth: ImGui.GetContentRegionAvail().X);
        // The data date moved into the pill's tooltip (the 1.0 card keeps one metadata line).
        if (ImGui.IsItemHovered())
            DactUi.Tooltip(text.Format($"FFLogs 数据更新于：{referenceDate}", $"FFLogs data updated: {referenceDate}"));

        var enabled = configuration.Fflogs.Enabled;
        if (DactUi.ToggleRow(text.Get("启用 FFLogs 在线估算", "Enable FFLogs online estimate"), ref enabled))
        {
            UpdateFflogsSettings(settings => settings.Enabled = enabled);
            fflogsEstimateService.NotifyCredentialsChanged();
            changed = true;
            if (enabled) fflogsEstimateService.RequestRefresh(getCurrentEncounter());
        }

        // Same order as before: the guide sits right above the fields it explains.
        DactUi.Disclosure(text.Get("如何创建 FFLogs API Client", "How to create an FFLogs API client"), ref fflogsGuideOpen);
        if (fflogsGuideOpen)
        {
            var steps = text.Get(
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
                    "6. Copy the Client ID and Client Secret shown below into the matching plugin fields.")
                .Split('\n')
                // The panel numbers the steps itself.
                .Select(step => System.Text.RegularExpressions.Regex.Replace(step, @"^\s*\d+\.\s*", ""))
                .ToArray();
            DactUi.StepsPanel(steps);
        }

        DactUi.BeginField("Client ID");
        var clientId = configuration.Fflogs.ClientId;
        if (DactUi.Input("fflogs-client-id", ref clientId, text.Get("粘贴 Client ID", "Paste the Client ID"), 128))
        {
            UpdateFflogsSettings(settings => settings.ClientId = clientId.Trim());
            fflogsEstimateService.NotifyCredentialsChanged();
            changed = true;
        }
        DactUi.EndField();
        DactUi.BeginField("Client Secret");
        var clientSecret = configuration.Fflogs.ClientSecret;
        if (DactUi.Input("fflogs-client-secret", ref clientSecret, text.Get("粘贴 Client Secret", "Paste the Client Secret"), 256, secret: true))
        {
            UpdateFflogsSettings(settings => settings.ClientSecret = clientSecret.Trim());
            fflogsEstimateService.NotifyCredentialsChanged();
            changed = true;
        }
        DactUi.EndField();

        if (DactUi.Button(text.Get("测试并刷新", "Test and refresh") + "##fflogs-refresh", ButtonKind.Primary))
            fflogsEstimateService.RequestRefresh(getCurrentEncounter());
        var manageLabel = text.Get("创建 / 管理 API Client", "Create / manage API client");
        DactUi.SameLineIfFits(DactUi.ButtonWidth(manageLabel, icon: "external"));
        if (DactUi.Button(manageLabel + "##fflogs-manage", icon: "external")) OpenUrl("https://www.fflogs.com/api/clients/");
        DactUi.Hint(text.Get(
            "密钥只保存在本机插件配置中，可随时撤销；不会上传玩家 ID。",
            "The secret is stored only in the local plugin settings and can be revoked anytime; player IDs are never uploaded."));
        // The title pill shows the state; the full sentence and details appear only when there is a problem.
        var statusDetail = FflogsStatusDetail(status);
        if (!string.IsNullOrWhiteSpace(statusDetail))
        {
            DactUi.Wrapped(FflogsStatusLabel(status.State), FflogsStatusColor(status.State), DactFonts.Small);
            DactUi.Hint(statusDetail.Trim());
        }

        DactUi.Divider();
        // Duty matching: a small target icon beside its title and explanation.
        var iconAt = ImGui.GetCursorScreenPos();
        DactIcons.Draw(ImGui.GetWindowDrawList(), "target", iconAt + new Vector2(0, 2 * s), 14 * s, DactTheme.Palette.Muted);
        ImGui.Indent(22 * s);
        using (DactUi.Gap(2))
        {
            DactUi.Text(text.Get("当前副本自动识别", "Automatic duty matching"), DactTheme.Palette.Muted, DactFonts.Small);
            if (ImGui.IsItemHovered())
                DactUi.Tooltip(text.Get(
                    "进入副本后按游戏 Territory ID 自动匹配；只会访问当前开放的 FFLogs 榜单。",
                    "Matches by the game's Territory ID on duty entry and only accesses the current FFLogs ranking tier.") + "\n" +
                    text.Get("国服使用 CN 分区，国际服使用 FFLogs 最新全球分区。", "China uses the CN partition; Global uses the latest FFLogs global partition."));
            var activeEncounter = fflogsEstimateService.ActiveEncounter;
            var where = activeEncounter is null
                ? text.Get("当前不在最新团队副本榜单中，不会加载历史榜单。",
                    "The current territory is outside the latest raid tier; historical rankings will not be loaded.")
                : $"Territory {activeEncounter.TerritoryId}  ·  {activeEncounter.EncounterName}  ·  " +
                  $"FFLogs #{activeEncounter.EncounterId}  ·  {(activeEncounter.Difficulty == 101 ? "Savage" : "Normal")}";
            var region = text.Get("榜单区域跟随主页的游戏区域。", "The ranking region follows the game region on Home.");
            DactUi.Hint(text.IsChinese ? where + region : where + " " + region);
            if (activeEncounter?.Phase > 1)
                DactUi.Wrapped(text.Get("已自动切换至第二阶段榜单", "Automatically switched to the phase-two ranking"), DactTheme.Palette.Accent, DactFonts.Small);
        }
        ImGui.Unindent(22 * s);
        DactUi.EndCard();
        return changed;
    }

    // Short state for the card's title pill; the full sentence and details stay below the buttons.
    private string FflogsStatusShort(FflogsEstimateState state) => state switch
    {
        FflogsEstimateState.Disabled => text.Get("未启用", "Off"),
        FflogsEstimateState.NeedsCredentials => text.Get("需要凭据", "Needs credentials"),
        FflogsEstimateState.Idle => text.Get("等待数据", "Waiting"),
        FflogsEstimateState.Loading => text.Get("连接中", "Connecting"),
        FflogsEstimateState.Ready => text.Get("已就绪", "Ready"),
        FflogsEstimateState.InactiveContent => text.Get("无活跃榜单", "No active ranking"),
        FflogsEstimateState.Error => text.Get("连接失败", "Failed"),
        FflogsEstimateState.RequestsPaused => text.Get("已暂停", "Paused"),
        FflogsEstimateState.RetryWaiting => text.Get("等待重试", "Retrying"),
        _ => state.ToString(),
    };

    private static PillTone FflogsStatusTone(FflogsEstimateState state) => state switch
    {
        FflogsEstimateState.Ready => PillTone.Ok,
        FflogsEstimateState.Error or FflogsEstimateState.RequestsPaused => PillTone.Error,
        FflogsEstimateState.InactiveContent or FflogsEstimateState.RetryWaiting or FflogsEstimateState.NeedsCredentials => PillTone.Warn,
        _ => PillTone.Off,
    };

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
        FflogsEstimateState.RequestsPaused => text.Get("状态：自动请求已暂停", "Status: automatic requests paused"),
        FflogsEstimateState.RetryWaiting => text.Get("状态：等待重试", "Status: waiting to retry"),
        _ => state.ToString(),
    };

    private string FflogsStatusDetail(FflogsEstimateStatus status)
    {
        var reason = status.FailureKind switch
        {
            FflogsFailureKind.CredentialsRejected => text.Get("API 凭据被拒绝，请检查 Client ID / Secret。", "API credentials rejected; check Client ID / Secret."),
            FflogsFailureKind.AccessDenied => text.Get("服务拒绝访问，请检查 API Client 权限或网络限制。", "Access denied; check API client permissions or network restrictions."),
            FflogsFailureKind.RateLimited => text.Get("请求受到限流。", "Requests are rate limited."),
            FflogsFailureKind.ServerError => text.Get("FFLogs 服务暂时异常。", "FFLogs is temporarily unavailable."),
            FflogsFailureKind.Timeout => text.Get("连接 FFLogs 超时。", "The FFLogs request timed out."),
            FflogsFailureKind.Network => text.Get("无法通过网络连接 FFLogs。", "Could not connect to FFLogs over the network."),
            FflogsFailureKind.InvalidResponse => text.Get("FFLogs 未返回可用的响应或榜单数据。", "FFLogs did not return usable response/ranking data."),
            _ => string.Empty,
        };
        if (status.FailureKind is null)
            return status.State is FflogsEstimateState.Error or FflogsEstimateState.InactiveContent
                ? status.Message : string.Empty;

        var retry = status.RetryAt is { } retryAt
            ? text.Format($"下次请求不早于 {retryAt.ToLocalTime():MM-dd HH:mm:ss}。", $"Next request no earlier than {retryAt.ToLocalTime():MM-dd HH:mm:ss}.")
            : string.Empty;
        var action = status.State == FflogsEstimateState.RequestsPaused
            ? text.Get("修正配置后或点击“测试并刷新”重试。", "Correct the settings or click Test and refresh to retry.")
            : text.Get("后续需要数据时自动重试；手动刷新不会跳过等待。", "Retries automatically when data is needed; manual refresh does not bypass the wait.");
        return $"{reason}\n{status.Message}\n{retry}{action}\n" +
               text.Get("已有估算缓存仍保留。", "Existing estimate cache is retained.");
    }

    private static Vector4 FflogsStatusColor(FflogsEstimateState state) => state switch
    {
        FflogsEstimateState.Ready => IceBlue,
        FflogsEstimateState.Error or FflogsEstimateState.RequestsPaused => new Vector4(0.93f, 0.38f, 0.36f, 1),
        FflogsEstimateState.InactiveContent or FflogsEstimateState.RetryWaiting => Gold,
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

    internal static void PushTheme()
    {
        DactTheme.PushStyleColor(ImGuiCol.ChildBg, Navy);
        DactTheme.PushStyleColor(ImGuiCol.Border, new Vector4(0.34f, 0.29f, 0.18f, 0.85f));
        DactTheme.PushStyleColor(ImGuiCol.Separator, new Vector4(0.34f, 0.29f, 0.18f, 0.70f));
        DactTheme.PushStyleColor(ImGuiCol.FrameBg, NavyRaised);
        DactTheme.PushStyleColor(ImGuiCol.FrameBgHovered, NavyHover);
        DactTheme.PushStyleColor(ImGuiCol.Button, new Vector4(0.12f, 0.17f, 0.24f, 1));
        DactTheme.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.18f, 0.25f, 0.34f, 1));
        DactTheme.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.24f, 0.30f, 0.37f, 1));
        DactTheme.PushStyleColor(ImGuiCol.CheckMark, IceBlue);
        DactTheme.PushStyleColor(ImGuiCol.Header, new Vector4(0.22f, 0.25f, 0.28f, 1));
        DactTheme.PushStyleColor(ImGuiCol.HeaderHovered, NavyHover);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 8);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 5);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8, 8));
    }

    internal static void PopTheme()
    {
        ImGui.PopStyleVar(3);
        ImGui.PopStyleColor(11);
    }
}

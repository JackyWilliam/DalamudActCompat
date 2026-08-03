using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using DalamudActCompat.ActRuntime;
using DalamudActCompat.Compatibility.PluginHost;
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

    private readonly PluginConfiguration configuration;
    private readonly IParserEngine parserEngine;
    private readonly PluginLogger logger;
    private readonly UiText text;
    private readonly FflogsEstimateService fflogsEstimateService;
    private readonly Func<Encounter?> getCurrentEncounter;
    private readonly ISharedImmediateTexture logoTexture;
    private readonly Action saveConfiguration;
    private readonly Action<bool> setMeterVisible;
    private readonly Action openMeter;
    private readonly Action openHistory;
    private readonly Action openStatus;
    private readonly Action openAdvancedSettings;
    private readonly Func<IReadOnlyList<InstalledActPlugin>> discoverPlugins;
    private readonly Action<string> openPluginConfiguration;
    private readonly Func<bool> isCactbotInstalled;
    private readonly Action selectCactbotPackage;
    private readonly Action openCactbotOverlay;
    private readonly Action openCactbotSettings;
    private readonly Func<IReadOnlyList<ActOverlayTemplate>> getOverlayTemplates;
    private readonly Action<string> openHtmlOverlay;
    private readonly Action<string> applyOverlayWindowSettings;
    private Page selectedPage;
    private ParserStatus parserStatus;
    private string? selectedCreatedOverlay;
    private string? fflogsEncounterInputKey;
    private int fflogsEncounterIdInput;

    public ControlCenterWindow(
        PluginConfiguration configuration,
        IParserEngine parserEngine,
        PluginLogger logger,
        UiText text,
        FflogsEstimateService fflogsEstimateService,
        Func<Encounter?> getCurrentEncounter,
        ISharedImmediateTexture logoTexture,
        Action saveConfiguration,
        Action<bool> setMeterVisible,
        Action openMeter,
        Action openHistory,
        Action openStatus,
        Action openAdvancedSettings,
        Func<IReadOnlyList<InstalledActPlugin>> discoverPlugins,
        Action<string> openPluginConfiguration,
        Func<bool> isCactbotInstalled,
        Action selectCactbotPackage,
        Action openCactbotOverlay,
        Action openCactbotSettings,
        Func<IReadOnlyList<ActOverlayTemplate>> getOverlayTemplates,
        Action<string> openHtmlOverlay,
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
        this.setMeterVisible = setMeterVisible;
        this.openMeter = openMeter;
        this.openHistory = openHistory;
        this.openStatus = openStatus;
        this.openAdvancedSettings = openAdvancedSettings;
        this.discoverPlugins = discoverPlugins;
        this.openPluginConfiguration = openPluginConfiguration;
        this.isCactbotInstalled = isCactbotInstalled;
        this.selectCactbotPackage = selectCactbotPackage;
        this.openCactbotOverlay = openCactbotOverlay;
        this.openCactbotSettings = openCactbotSettings;
        this.getOverlayTemplates = getOverlayTemplates;
        this.openHtmlOverlay = openHtmlOverlay;
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
    }

    public override void Draw()
    {
        WindowName = text.Get(
            "ACT 控制中心###DalamudActCompatControlCenter",
            "ACT Control Center###DalamudActCompatControlCenter");

        PushTheme();
        try
        {
            var sidebarWidth = Math.Clamp(ImGui.GetContentRegionAvail().X * 0.20f, 158, 188);
            if (ImGui.BeginChild("control-center-sidebar", new Vector2(sidebarWidth, -1), true))
            {
                DrawSidebar();
            }
            ImGui.EndChild();

            ImGui.SameLine();
            if (ImGui.BeginChild("control-center-content", new Vector2(-1, -1), true))
            {
                DrawPageTabs();
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();

                var changed = selectedPage switch
                {
                    Page.Overview => DrawOverview(),
                    Page.Meter => DrawMeter(),
                    Page.Overlays => DrawOverlays(),
                    Page.Extensions => DrawExtensions(),
                    Page.Diagnostics => DrawDiagnostics(),
                    _ => false,
                };

                if (changed)
                {
                    saveConfiguration();
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

    private void DrawSidebar()
    {
        var wrap = logoTexture.GetWrapOrEmpty();
        var logoSize = Math.Clamp(ImGui.GetContentRegionAvail().X - 44, 92, 132);
        ImGui.SetCursorPosX((ImGui.GetWindowWidth() - logoSize) * 0.5f);
        ImGui.Image(wrap.Handle, new Vector2(logoSize, logoSize));
        ImGui.Spacing();

        CenteredText("Dalamud ACT Compat");
        ImGui.TextDisabled(text.Get("游戏内 ACT 工作台", "In-game ACT workspace"));
        ImGui.Spacing();

        if (ImGui.BeginChild("parser-state-card", new Vector2(-1, 76), true))
        {
            ImGui.TextDisabled(text.Get("解析状态", "Parser status"));
            var stateColor = parserStatus.State == ParserState.Running
                ? IceBlue
                : new Vector4(0.70f, 0.72f, 0.76f, 1);
            ImGui.TextColored(stateColor, $"● {LocalizeState(parserStatus.State)}");
            if (!string.IsNullOrWhiteSpace(parserStatus.Detail))
            {
                ImGui.TextDisabled(TrimText(parserStatus.Detail, 26));
            }
        }
        ImGui.EndChild();

        var footerY = ImGui.GetWindowHeight() - ImGui.GetTextLineHeightWithSpacing() - 12;
        if (ImGui.GetCursorPosY() < footerY)
        {
            ImGui.SetCursorPosY(footerY);
        }
        ImGui.SetCursorPosX(Math.Max(8, (ImGui.GetWindowWidth() - ImGui.CalcTextSize(VersionLabel).X) * 0.5f));
        ImGui.TextDisabled(VersionLabel);

    }

    private void DrawPageTabs()
    {
        var tabs = new (Page Page, string Label)[]
        {
            (Page.Overview, text.Get("概览", "Overview")),
            (Page.Meter, text.Get("战斗统计", "Combat Meter")),
            (Page.Overlays, text.Get("悬浮窗", "Overlays")),
            (Page.Extensions, text.Get("扩展", "Extensions")),
            (Page.Diagnostics, text.Get("诊断", "Settings")),
        };
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var tabWidth = Math.Max(86, (ImGui.GetContentRegionAvail().X - (spacing * (tabs.Length - 1))) / tabs.Length);
        for (var index = 0; index < tabs.Length; index++)
        {
            if (index > 0)
            {
                ImGui.SameLine();
            }

            var tab = tabs[index];
            var selected = selectedPage == tab.Page;
            ImGui.PushStyleColor(
                ImGuiCol.Button,
                selected ? new Vector4(0.11f, 0.25f, 0.34f, 1) : new Vector4(0.055f, 0.075f, 0.10f, 1));
            ImGui.PushStyleColor(ImGuiCol.Text, selected ? IceBlue : new Vector4(0.78f, 0.82f, 0.87f, 1));
            if (ImGui.Button($"{tab.Label}##top-nav-{tab.Page}", new Vector2(tabWidth, 38)))
            {
                selectedPage = tab.Page;
            }
            ImGui.PopStyleColor(2);
        }
    }
    private bool DrawOverview()
    {
        DrawPageHeader(
            text.Get("概览", "Overview"),
            text.Get("常用状态和入口集中在这里。", "Status and common actions in one place."));

        ImGui.TextColored(Gold, text.Get("解析器", "Parser"));
        ImGui.TextUnformatted(LocalizeState(parserStatus.State));
        ImGui.TextWrapped(parserStatus.Message);
        if (!string.IsNullOrWhiteSpace(parserStatus.Detail))
        {
            ImGui.TextDisabled(parserStatus.Detail);
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextColored(Gold, text.Get("快捷入口", "Quick actions"));
        if (ImGui.Button(text.Get("打开战斗统计", "Open Combat Meter"), new Vector2(150, 36)))
        {
            openMeter();
        }
        ImGui.SameLine();
        if (ImGui.Button(text.Get("战斗历史", "Encounter history"), new Vector2(150, 36)))
        {
            openHistory();
        }
        ImGui.SameLine();
        if (ImGui.Button(text.Get("运行状态", "Runtime status"), new Vector2(150, 36)))
        {
            openStatus();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextColored(Gold, text.Get("基础设置", "General"));
        var changed = false;
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
        return changed;
    }

    private bool DrawMeter()
    {
        DrawPageHeader(
            text.Get("战斗统计", "Combat Meter"),
            text.Get("仅调整内置战斗统计的显示，不影响 Cactbot 或 HTML Overlay。", "These options affect only the built-in Combat Meter, not Cactbot or HTML overlays."));

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

        changed |= DrawPlayerIdentityControls();
        changed |= DrawFflogsSettings();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextColored(Gold, text.Get("显示列", "Columns"));
        changed |= Checkbox(text.Get("战斗标题", "Encounter header"), configuration.Meter.ShowHeader, value => configuration.Meter.ShowHeader = value);
        ImGui.SameLine();
        changed |= Checkbox(text.Get("职业", "Job"), configuration.Meter.ShowJob, value => configuration.Meter.ShowJob = value);
        ImGui.SameLine();
        changed |= Checkbox("DPS", configuration.Meter.ShowDps, value => configuration.Meter.ShowDps = value);
        changed |= Checkbox(text.Get("伤害", "Damage"), configuration.Meter.ShowDamage, value => configuration.Meter.ShowDamage = value);
        ImGui.SameLine();
        changed |= Checkbox(text.Get("伤害占比", "Damage percent"), configuration.Meter.ShowDamagePercent, value => configuration.Meter.ShowDamagePercent = value);
        ImGui.SameLine();
        changed |= Checkbox("HPS", configuration.Meter.ShowHps, value => configuration.Meter.ShowHps = value);
        changed |= Checkbox(text.Get("治疗量", "Healing"), configuration.Meter.ShowHealing, value => configuration.Meter.ShowHealing = value);
        ImGui.SameLine();
        changed |= Checkbox(text.Get("死亡", "Deaths"), configuration.Meter.ShowDeaths, value => configuration.Meter.ShowDeaths = value);

        var localPlayerColor = configuration.Meter.LocalPlayerColor;
        if (ImGui.ColorEdit4(text.Get("本地玩家颜色", "Local player color"), ref localPlayerColor))
        {
            configuration.Meter.LocalPlayerColor = localPlayerColor;
            changed = true;
        }

        return changed;
    }

    private bool DrawOverlays()
    {
        DrawPageHeader(
            text.Get("悬浮窗", "Overlays"),
            text.Get("Cactbot 与 HTML Overlay 保持原来的 WebView2 窗口，这里只负责管理。", "Cactbot and HTML overlays keep their existing WebView2 windows; this page only manages them."));

        var changed = false;
        ImGui.TextColored(Gold, "Cactbot Raidboss");
        ImGui.TextDisabled(isCactbotInstalled()
            ? text.Get("资源已安装", "Assets installed")
            : text.Get("资源未安装", "Assets not installed"));
        if (ImGui.Button(text.Get("打开 Cactbot", "Open Cactbot")))
        {
            openCactbotOverlay();
        }
        ImGui.SameLine();
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
        changed |= DrawOverlayWindowSettings(SelfHostedActRuntime.CactbotOverlayName);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextColored(Gold, "HTML Overlay");
        var templates = getOverlayTemplates();
        if (templates.Count == 0)
        {
            ImGui.TextDisabled(text.Get("启动解析器后可选择 Overlay 模板。", "Start the parser to select an overlay template."));
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

            if (ImGui.Button(text.Get("打开所选 HTML Overlay", "Open selected HTML overlay")))
            {
                openHtmlOverlay(configuration.SelectedOverlayTemplate);
            }
            changed |= DrawOverlayWindowSettings(configuration.SelectedOverlayTemplate);
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextColored(Gold, text.Get("已创建的悬浮窗", "Created overlays"));
        configuration.OverlayWindows ??= new Dictionary<string, HtmlOverlayWindowSettings>(StringComparer.OrdinalIgnoreCase);
        var createdNames = configuration.OverlayWindows.Keys
            .Where(name => !string.Equals(name, SelfHostedActRuntime.CactbotOverlayName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name)
            .ToArray();
        if (createdNames.Length == 0)
        {
            ImGui.TextDisabled(text.Get("还没有创建 HTML Overlay。", "No HTML overlays have been created yet."));
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
                if (ImGui.Button(text.Get("重新打开", "Reopen")))
                {
                    openHtmlOverlay(selectedCreatedOverlay);
                }
                changed |= DrawOverlayWindowSettings(selectedCreatedOverlay);
            }
        }

        return changed;
    }

    private bool DrawExtensions()
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

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextWrapped(text.Get(
            "安装、更新来源和权限白名单等低频选项仍放在完整设置页。",
            "Low-frequency installation, update-source, and permission options remain in the full settings page."));
        if (ImGui.Button(text.Get("打开完整设置", "Open full settings")))
        {
            openAdvancedSettings();
        }
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
            text.Get("诊断与设置", "Diagnostics & Settings"),
            text.Get("运行状态、语言和快捷按钮。", "Runtime status, language, and quick-button options."));

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
        if (ImGui.Button(text.Get("详细运行状态", "Detailed runtime status")))
        {
            openStatus();
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
        if (ImGui.Button(text.Get("打开完整设置与日志路径", "Open full settings and log paths")))
        {
            openAdvancedSettings();
        }
        return changed;
    }

    private bool DrawOverlayWindowSettings(string name)
    {
        var changed = false;
        var settings = configuration.GetOverlayWindowSettings(name);
        ImGui.PushID(name);
        ImGui.TextDisabled(text.Get("位置、缩放、穿透与锁定", "Position, scale, click-through, and lock"));
        if (ImGui.Button(settings.IsEditing
                ? text.Get("完成位置编辑", "Finish position editing")
                : text.Get("编辑位置和大小", "Edit position and size")))
        {
            settings.SetEditing(!settings.IsEditing);
            changed = true;
        }
        ImGui.SameLine();
        changed |= Checkbox(text.Get("鼠标穿透", "Click-through"), settings.IsClickThrough, value => settings.IsClickThrough = value);
        ImGui.SameLine();
        changed |= Checkbox(text.Get("锁定", "Locked"), settings.IsLocked, value => settings.IsLocked = value);
        changed |= SliderFloat(text.Get("页面缩放", "Page zoom"), settings.ZoomFactor, 0.5f, 2, value => settings.ZoomFactor = value);
        ImGui.TextDisabled(settings.IsEditing
            ? text.Get("现在可以拖动窗口，并从右下角调整大小。", "You can now drag the window and resize it from the bottom-right.")
            : text.Get("位置编辑时会暂时关闭穿透与锁定。", "Position editing temporarily disables click-through and locking."));
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

    private void DrawPageHeader(string title, string description)
    {
        ImGui.TextColored(Gold, title);
        ImGui.TextDisabled(description);
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
    }

    private static void CenteredText(string value)
    {
        ImGui.SetCursorPosX(Math.Max(8, (ImGui.GetWindowWidth() - ImGui.CalcTextSize(value).X) * 0.5f));
        ImGui.TextUnformatted(value);
    }

    private static string TrimText(string value, int maximumLength)
        => value.Length <= maximumLength ? value : $"{value[..Math.Max(1, maximumLength - 1)]}…";

    private static string BuildVersionLabel()
    {
        var version = typeof(ControlCenterWindow).Assembly.GetName().Version;
        return version is null ? "v?" : $"v{version.Major}.{version.Minor}.{version.Build}";
    }

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
            "使用 FFLogs 公共排名样本估算自己的当前 EncDPS 百分位；显示为带“~”的颜色与数字，不是官方实时日志分数。",
            "Estimate your current EncDPS percentile from public FFLogs ranking samples. The colored number is prefixed with '~' and is not an official live-log score."));

        var changed = false;
        var enabled = configuration.Fflogs.Enabled;
        if (ImGui.Checkbox(text.Get("启用 FFLogs 在线估算", "Enable FFLogs online estimate"), ref enabled))
        {
            configuration.Fflogs.Enabled = enabled;
            fflogsEstimateService.NotifyCredentialsChanged();
            changed = true;
            if (enabled)
            {
                fflogsEstimateService.RequestRefresh(getCurrentEncounter());
            }
        }

        var clientId = configuration.Fflogs.ClientId;
        if (ImGui.InputText("Client ID", ref clientId, 128))
        {
            configuration.Fflogs.ClientId = clientId.Trim();
            fflogsEstimateService.NotifyCredentialsChanged();
            changed = true;
        }

        var clientSecret = configuration.Fflogs.ClientSecret;
        if (ImGui.InputText("Client Secret", ref clientSecret, 256, ImGuiInputTextFlags.Password))
        {
            configuration.Fflogs.ClientSecret = clientSecret.Trim();
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
        if (status.State is FflogsEstimateState.Error or FflogsEstimateState.EncounterNotMatched)
        {
            ImGui.TextWrapped(status.Message);
        }

        var encounter = getCurrentEncounter();
        if (encounter is not null && !string.IsNullOrWhiteSpace(encounter.EnemyName))
        {
            configuration.Fflogs.EncounterMappings ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (!string.Equals(fflogsEncounterInputKey, encounter.EnemyName, StringComparison.Ordinal))
            {
                fflogsEncounterInputKey = encounter.EnemyName;
                fflogsEncounterIdInput = configuration.Fflogs.EncounterMappings.TryGetValue(encounter.EnemyName, out var mappedId)
                    ? mappedId
                    : 0;
            }

            ImGui.Spacing();
            ImGui.TextDisabled($"{text.Get("当前首领", "Current boss")}: {encounter.EnemyName}");
            if (ImGui.InputInt(text.Get("FFLogs Encounter ID", "FFLogs encounter ID"), ref fflogsEncounterIdInput))
            {
                fflogsEncounterIdInput = Math.Max(0, fflogsEncounterIdInput);
            }
            if (ImGui.Button(text.Get("绑定当前战斗", "Bind current encounter")) && fflogsEncounterIdInput > 0)
            {
                configuration.Fflogs.EncounterMappings[encounter.EnemyName] = fflogsEncounterIdInput;
                fflogsEstimateService.RequestRefresh(encounter);
                changed = true;
            }
            ImGui.SameLine();
            if (ImGui.Button(text.Get("清除绑定", "Clear binding")) &&
                configuration.Fflogs.EncounterMappings.Remove(encounter.EnemyName))
            {
                fflogsEncounterIdInput = 0;
                changed = true;
            }
            ImGui.TextDisabled(text.Get(
                "自动匹配失败时，可从 FFLogs 对应首领页面 URL 中填写 Encounter ID。",
                "If automatic matching fails, enter the encounter ID from the matching FFLogs boss page URL."));
        }

        return changed;
    }

    private string FflogsStatusLabel(FflogsEstimateState state) => state switch
    {
        FflogsEstimateState.Disabled => text.Get("状态：未启用", "Status: disabled"),
        FflogsEstimateState.NeedsCredentials => text.Get("状态：需要 Client ID 与 Secret", "Status: client ID and secret required"),
        FflogsEstimateState.Idle => text.Get("状态：等待测试或战斗数据", "Status: waiting for a test or encounter data"),
        FflogsEstimateState.Loading => text.Get("状态：正在连接 FFLogs…", "Status: connecting to FFLogs…"),
        FflogsEstimateState.Ready => text.Get("状态：估算数据已就绪", "Status: estimate data ready"),
        FflogsEstimateState.EncounterNotMatched => text.Get("状态：未匹配到当前战斗", "Status: current encounter not matched"),
        FflogsEstimateState.Error => text.Get("状态：连接失败", "Status: connection failed"),
        _ => state.ToString(),
    };

    private static Vector4 FflogsStatusColor(FflogsEstimateState state) => state switch
    {
        FflogsEstimateState.Ready => IceBlue,
        FflogsEstimateState.Error => new Vector4(0.93f, 0.38f, 0.36f, 1),
        FflogsEstimateState.EncounterNotMatched => Gold,
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

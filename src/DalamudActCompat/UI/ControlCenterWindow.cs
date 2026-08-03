using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using DalamudActCompat.ActRuntime;
using DalamudActCompat.Core.Interfaces;
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

    private static readonly Vector4 Navy = new(0.045f, 0.065f, 0.105f, 1);
    private static readonly Vector4 NavyRaised = new(0.075f, 0.10f, 0.15f, 1);
    private static readonly Vector4 NavyHover = new(0.11f, 0.16f, 0.23f, 1);
    private static readonly Vector4 Gold = new(0.78f, 0.66f, 0.36f, 1);
    private static readonly Vector4 IceBlue = new(0.38f, 0.72f, 0.90f, 1);

    private readonly PluginConfiguration configuration;
    private readonly IParserEngine parserEngine;
    private readonly PluginLogger logger;
    private readonly UiText text;
    private readonly ISharedImmediateTexture logoTexture;
    private readonly Action saveConfiguration;
    private readonly Action<bool> setMeterVisible;
    private readonly Action openMeter;
    private readonly Action openHistory;
    private readonly Action openStatus;
    private readonly Action openAdvancedSettings;
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

    public ControlCenterWindow(
        PluginConfiguration configuration,
        IParserEngine parserEngine,
        PluginLogger logger,
        UiText text,
        ISharedImmediateTexture logoTexture,
        Action saveConfiguration,
        Action<bool> setMeterVisible,
        Action openMeter,
        Action openHistory,
        Action openStatus,
        Action openAdvancedSettings,
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
        this.logoTexture = logoTexture;
        this.saveConfiguration = saveConfiguration;
        this.setMeterVisible = setMeterVisible;
        this.openMeter = openMeter;
        this.openHistory = openHistory;
        this.openStatus = openStatus;
        this.openAdvancedSettings = openAdvancedSettings;
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
            var sidebarWidth = Math.Clamp(ImGui.GetContentRegionAvail().X * 0.23f, 170, 210);
            if (ImGui.BeginChild("control-center-sidebar", new Vector2(sidebarWidth, -1), true))
            {
                DrawSidebar();
            }
            ImGui.EndChild();

            ImGui.SameLine();
            if (ImGui.BeginChild("control-center-content", new Vector2(-1, -1), true))
            {
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
        var stateColor = parserStatus.State == ParserState.Running ? IceBlue : new Vector4(0.70f, 0.72f, 0.76f, 1);
        var stateLabel = $"● {LocalizeState(parserStatus.State)}";
        ImGui.SetCursorPosX(Math.Max(8, (ImGui.GetWindowWidth() - ImGui.CalcTextSize(stateLabel).X) * 0.5f));
        ImGui.TextColored(stateColor, stateLabel);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        NavButton(Page.Overview, text.Get("概览", "Overview"));
        NavButton(Page.Meter, "Meter");
        NavButton(Page.Overlays, text.Get("悬浮窗", "Overlays"));
        NavButton(Page.Extensions, text.Get("扩展", "Extensions"));
        NavButton(Page.Diagnostics, text.Get("诊断与设置", "Diagnostics & Settings"));

        var footerY = ImGui.GetWindowHeight() - ImGui.GetTextLineHeightWithSpacing() * 2.2f;
        if (footerY > ImGui.GetCursorPosY())
        {
            ImGui.SetCursorPosY(footerY);
        }
        ImGui.Separator();
        ImGui.TextDisabled(text.Get("简洁、原生、可实现", "Simple, native, practical"));
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
        if (ImGui.Button(text.Get("打开 Meter", "Open Meter"), new Vector2(150, 36)))
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
            "快捷按钮：左键设置、右键 Meter、按住中键拖动。",
            "Quick button: left settings, right Meter, hold middle mouse to move."));
        return changed;
    }

    private bool DrawMeter()
    {
        DrawPageHeader(
            "Meter",
            text.Get("仅调整原生 Meter 的显示，不影响 Cactbot 或 HTML Overlay。", "These options affect only the native Meter, not Cactbot or HTML overlays."));

        var changed = false;
        var visible = configuration.Meter.IsVisible;
        if (ImGui.Checkbox(text.Get("显示 Meter", "Show Meter"), ref visible))
        {
            setMeterVisible(visible);
        }
        ImGui.SameLine();
        if (ImGui.Button(text.Get("定位到 Meter", "Open Meter window")))
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
        ImGui.TextWrapped(text.Get(
            "第三方 DLL 的安装、更新来源、权限白名单等低频选项仍放在完整设置页，避免主界面过于复杂。",
            "Low-frequency options such as third-party DLL installation, update sources, and permission grants remain in the full settings page."));
        if (ImGui.Button(text.Get("打开完整设置", "Open full settings")))
        {
            openAdvancedSettings();
        }
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
        if (ImGui.Button(text.Get("重置快捷按钮位置", "Reset quick-button position")))
        {
            configuration.LauncherPositionX = 80;
            configuration.LauncherPositionY = 160;
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

    private void NavButton(Page page, string label)
    {
        var selected = selectedPage == page;
        ImGui.PushStyleColor(ImGuiCol.Button, selected ? new Vector4(0.26f, 0.23f, 0.14f, 1) : Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.Text, selected ? new Vector4(0.95f, 0.86f, 0.60f, 1) : new Vector4(0.84f, 0.87f, 0.91f, 1));
        if (ImGui.Button($"{label}##nav-{page}", new Vector2(-1, 34)))
        {
            selectedPage = page;
        }
        ImGui.PopStyleColor(2);
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

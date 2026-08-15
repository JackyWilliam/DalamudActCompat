using Dalamud.Interface.Windowing;
using DalamudActCompat.Core.Interfaces;
using DalamudActCompat.ActRuntime;
using DalamudActCompat.Infrastructure.Logging;
using DalamudActCompat.Infrastructure.Storage;
using DalamudActCompat.Meter;
using DalamudActCompat.Parser;
using DalamudActCompat.Plugin;
using DalamudActCompat.Compatibility.PluginHost;
using DalamudActCompat.Compatibility.Cactbot;
using Dalamud.Bindings.ImGui;

namespace DalamudActCompat.UI;

public sealed class SettingsWindow : Window
{
    private readonly PluginConfiguration configuration;
    private readonly IParserEngine parserEngine;
    private readonly PluginPaths paths;
    private readonly PluginLogger logger;
    private readonly Action saveConfiguration;
    private readonly Action applyPermissionChanges;
    private readonly Func<Task<string>> factoryReset;
    private readonly Func<IReadOnlyList<InstalledActPlugin>> discoverPlugins;
    private readonly Action selectPluginPackage;
    private readonly Action openPluginDirectory;
    private readonly UiText text;
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
    private readonly Action<string> openPluginConfiguration;
    private readonly Action openBundledPluginNotice;
    private ParserStatus parserStatus;
    private bool confirmFactoryReset;
    private string? resetResult;
    private string? selectedUsedCactbotOverlay;
    private string? selectedAvailableCactbotOverlay;

    public SettingsWindow(
        PluginConfiguration configuration,
        IParserEngine parserEngine,
        PluginPaths paths,
        PluginLogger logger,
        Action saveConfiguration,
        Action applyPermissionChanges,
        Func<Task<string>> factoryReset,
        Func<IReadOnlyList<InstalledActPlugin>> discoverPlugins,
        Action selectPluginPackage,
        Action openPluginDirectory,
        UiText text,
        Func<bool> isCactbotInstalled,
        Func<CactbotOperationStatus> getCactbotOperationStatus,
        Action selectCactbotPackage,
        Action openCactbotOverlay,
        Action openCactbotSettings,
        Func<IReadOnlyList<ActOverlayTemplate>> getOverlayTemplates,
        Func<string, bool> openHtmlOverlay,
        Action<string> closeHtmlOverlay,
        Action<string> deleteHtmlOverlay,
        Action<string> applyOverlayWindowSettings,
        Action<string> openPluginConfiguration,
        Action openBundledPluginNotice)
        : base("ACT 兼容设置###DalamudActCompatSettings")
    {
        this.configuration = configuration;
        this.parserEngine = parserEngine;
        this.paths = paths;
        this.logger = logger;
        this.saveConfiguration = saveConfiguration;
        this.applyPermissionChanges = applyPermissionChanges;
        this.factoryReset = factoryReset;
        this.discoverPlugins = discoverPlugins;
        this.selectPluginPackage = selectPluginPackage;
        this.openPluginDirectory = openPluginDirectory;
        this.text = text;
        this.isCactbotInstalled = isCactbotInstalled;
        this.getCactbotOperationStatus = getCactbotOperationStatus;
        this.selectCactbotPackage = selectCactbotPackage;
        this.openCactbotOverlay = openCactbotOverlay;
        this.openCactbotSettings = openCactbotSettings;
        this.getOverlayTemplates = getOverlayTemplates;
        this.openHtmlOverlay = openHtmlOverlay;
        this.closeHtmlOverlay = closeHtmlOverlay;
        this.deleteHtmlOverlay = deleteHtmlOverlay;
        this.applyOverlayWindowSettings = applyOverlayWindowSettings;
        this.openPluginConfiguration = openPluginConfiguration;
        this.openBundledPluginNotice = openBundledPluginNotice;
        parserStatus = parserEngine.Status;
        parserEngine.StatusChanged += OnParserStatusChanged;
    }

    public override void Draw()
    {
        var changed = false;
        var hostConfigurationChanged = false;
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
        WindowName = text.Get("ACT 兼容设置###DalamudActCompatSettings", "ACT Compat Settings###DalamudActCompatSettings");
        changed |= Checkbox(text.Get("启用解析", "Enable parsing"), configuration.EnableParsing, value => configuration.EnableParsing = value);
        changed |= Checkbox(text.Get("自动启动解析器", "Auto start parser"), configuration.AutoStartParser, value => configuration.AutoStartParser = value);
        changed |= Checkbox(text.Get("调试模式", "Debug mode"), configuration.DebugMode, value => configuration.DebugMode = value);
        changed |= Checkbox(
            text.Get("系统插件：FFXIV_ACT_Plugin", "System plugin: FFXIV_ACT_Plugin"),
            configuration.EmbeddedPlugins.FfxivActPluginEnabled,
            value => configuration.EmbeddedPlugins.FfxivActPluginEnabled = value);
        changed |= Checkbox(
            text.Get("系统插件：OverlayPlugin", "System plugin: OverlayPlugin"),
            configuration.EmbeddedPlugins.OverlayPluginEnabled,
            value => configuration.EmbeddedPlugins.OverlayPluginEnabled = value);

        ImGui.TextUnformatted(text.Get("已安装的 ACT 扩展", "Installed ACT plugins"));
        var installedPlugins = discoverPlugins();
        if (installedPlugins.Count == 0)
        {
            ImGui.TextDisabled(text.Get("没有安装可选 ACT 扩展。", "No optional ACT plugins installed."));
        }

        foreach (var plugin in installedPlugins)
        {
            var isGeneric = !ActPluginPackageInstaller.IsSpecializedPluginId(
                plugin.Manifest.Id);
            var enabled = plugin.Enabled &&
                          (!isGeneric || configuration.TrustedGenericActPluginIds.Contains(
                              plugin.Manifest.Id));
            if (ImGui.Checkbox(
                    $"{plugin.Manifest.Name} {plugin.Manifest.Version}###{plugin.Manifest.Id}",
                    ref enabled))
            {
                var genericNeedsConsent = enabled && isGeneric &&
                    !configuration.TrustedGenericActPluginIds.Contains(plugin.Manifest.Id);
                if (genericNeedsConsent)
                {
                    // The compact legacy settings page must not bypass the main consent card.
                    logger.Warning(
                        $"Generic ACT plugin '{plugin.Manifest.Id}' must be authorized from the main Extensions page before it can be enabled.");
                }
                else if (enabled)
                {
                    configuration.DisabledActPluginIds.Remove(plugin.Manifest.Id);
                }
                else
                {
                    configuration.DisabledActPluginIds.Add(plugin.Manifest.Id);
                }

                if (!genericNeedsConsent)
                {
                    changed = true;
                    hostConfigurationChanged = true;
                }
            }

            ImGui.SameLine();
            if (ImGui.SmallButton($"{text.Get("打开配置", "Open configuration")}###open-config-{plugin.Manifest.Id}"))
            {
                openPluginConfiguration(plugin.Manifest.Id);
            }
        }

        if (ImGui.Button(text.Get("安装 ACT 扩展 DLL 或 ZIP...", "Install ACT plugin DLL or ZIP...")))
        {
            selectPluginPackage();
        }

        ImGui.SameLine();
        if (ImGui.Button(text.Get("打开 ACT 扩展文件夹", "Open ACT plugin folder")))
        {
            openPluginDirectory();
        }

        if (ImGui.Button(text.Get(
                "检查 DLL 更新并查看作者与来源网址",
                "Check DLL updates and view authors/source URLs")))
        {
            openBundledPluginNotice();
        }

        var autoCheckUpdates = configuration.AutoCheckBundledPluginUpdates;
        if (ImGui.Checkbox(
                text.Get(
                    "启动时自动检查第三方扩展更新",
                    "Automatically check third-party extension updates on startup"),
                ref autoCheckUpdates))
        {
            configuration.AutoCheckBundledPluginUpdates = autoCheckUpdates;
            saveConfiguration();
        }

        ImGui.TextDisabled(text.Get(
            "安装扩展后请重启解析器；启停兼容扩展会自动重启独立 Host。",
            "Restart the parser after installing an extension; enabling or disabling a compatibility extension restarts the independent Host automatically."));
        ImGui.Separator();
        ImGui.TextUnformatted("Cactbot（OverlayPlugin addon）");
        ImGui.TextDisabled(FormatCactbotStatus());
        if (ImGui.Button(text.Get("安装/更新 Cactbot...", "Install/update Cactbot...")))
        {
            selectCactbotPackage();
        }
        ImGui.SameLine();
        if (ImGui.Button(text.Get("Cactbot 设置", "Cactbot settings")))
        {
            openCactbotSettings();
        }
        ImGui.SameLine();
        if (ImGui.SmallButton(text.Get("打开官方项目页###Cactbot", "Open official project###Cactbot")))
        {
            OpenUrl("https://github.com/OverlayPlugin/cactbot");
        }

        var allTemplates = getOverlayTemplates();
        var cactbotTemplates = allTemplates
            .Where(static template => template.IsCactbot)
            .OrderBy(template => GetCactbotOverlayOrder(template.Name))
            .ThenBy(static template => template.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        changed |= DrawCactbotOverlayManager(cactbotTemplates, allTemplates.Count > 0);

        ImGui.TextUnformatted(text.Get("HTML 悬浮窗", "HTML overlays"));
        var templates = allTemplates
            .Where(static template => !template.IsCactbot)
            .ToArray();
        if (templates.Length == 0)
        {
            ImGui.TextDisabled(text.Get(
                "OverlayPlugin 尚未运行；启动或重启解析器后可选择模板。",
                "OverlayPlugin is not running; start or restart the parser to select a template."));
        }
        else
        {
            if (!templates.Any(template =>
                    string.Equals(
                        template.Name,
                        configuration.SelectedOverlayTemplate,
                        StringComparison.OrdinalIgnoreCase)))
            {
                configuration.SelectedOverlayTemplate = templates[0].Name;
                changed = true;
            }

            if (ImGui.BeginCombo(
                    text.Get("悬浮窗模板", "Overlay template"),
                    configuration.SelectedOverlayTemplate))
            {
                foreach (var template in templates)
                {
                    var selected = string.Equals(
                        template.Name,
                        configuration.SelectedOverlayTemplate,
                        StringComparison.OrdinalIgnoreCase);
                    if (ImGui.Selectable(template.Name, selected))
                    {
                        configuration.SelectedOverlayTemplate = template.Name;
                        changed = true;
                    }
                }

                ImGui.EndCombo();
            }

            var selectedSettings = configuration.GetOverlayWindowSettings(
                configuration.SelectedOverlayTemplate);
            if (ImGui.Button(selectedSettings.IsVisible
                    ? text.Get("关闭所选 HTML 悬浮窗", "Close selected HTML overlay")
                    : text.Get("打开所选 HTML 悬浮窗", "Open selected HTML overlay")))
            {
                if (selectedSettings.IsVisible)
                {
                    closeHtmlOverlay(configuration.SelectedOverlayTemplate);
                }
                else
                {
                    openHtmlOverlay(configuration.SelectedOverlayTemplate);
                }
            }

        }

        ImGui.TextUnformatted(text.Get("可选 ACT 扩展（与 Cactbot 本体分开）", "Optional ACT extensions (separate from Cactbot)"));
        DrawCompatibilityTarget("CactbotSelf / MoreLogLine", text.Get("国服额外日志扩展；实验性兼容。", "CN extra-log extension; experimental compatibility."), "https://github.com/tssailzz8/cacbotSelf");
        DrawCompatibilityTarget("PostNamazu", text.Get("鲶鱼精邮差；完整保留 7 个动作模块、15 个命令别名、HTTP、Triggernometry 与 OverlayPlugin 集成。启用完整权限后，原版进程附加、签名扫描和原生调用也会恢复。", "PostNamazu retains all 7 action modules, 15 command aliases, HTTP, Triggernometry, and OverlayPlugin integration. With full permissions enabled, the original process attachment, signature scanning, and native calls are also restored."), "https://github.com/Natsukage/PostNamazu");
        DrawCompatibilityTarget("ACT.FoxTTS", text.Get("中文 TTS；安装/基础加载，音频后端需实测。", "Chinese TTS; install/basic load, audio backends require testing."), "https://github.com/Noisyfox/ACT.FoxTTS");
        DrawCompatibilityTarget("Triggernometry 中文维护版", text.Get("支持 DLL 与汉化 XML、全部 29 种动作，以及日志/网络/区域/战斗/TTS/实体接口；其内置 BridgeNamazu 高级模块在鲶鱼精完整权限下使用原版原生运行时。", "Supports the DLL and translation XML, all 29 action types, and log/network/zone/combat/TTS/entity APIs. Its built-in advanced BridgeNamazu modules use the original native runtime when PostNamazu has full permissions."), "https://github.com/MnFeN/Triggernometry");
        ImGui.BulletText(text.Get("银山雀儿 / SilverDasher（共享 Host 最后加载）", "SilverDasher (loaded last in the shared Host)"));
        ImGui.SameLine();
        ImGui.TextDisabled(text.Get(
            "使用独立事件队列与专属内存权限上下文。",
            "Uses an independent event queue and dedicated memory-permission context."));
        ImGui.BulletText(text.Get("抹茶 / Cafe.Matcha（最后启动）", "Cafe.Matcha (started last)"));
        ImGui.SameLine();
        ImGui.TextDisabled(text.Get(
            "默认自启动，并单独运行在第二个 Host；不会进入现有四个扩展的共享进程。",
            "Starts by default in a second dedicated Host and never enters the process shared by the existing four extensions."));

        ImGui.Separator();
        ImGui.TextUnformatted(text.Get("ACT 插件权限边界", "ACT plugin permission boundary"));
        ImGui.TextWrapped(text.Get(
            "高风险能力默认关闭。权限按插件与动作类别保存；权限组保存后会自动重启 Host 一次，使完整功能立即生效；" +
            "完整限制第三方 DLL 的直接系统调用仍需独立受限进程。",
            "High-risk capabilities are denied by default. Grants are stored per plugin and category " +
            "and the Host restarts once after a permission group is saved so the complete feature set takes effect immediately; " +
            "fully constraining direct OS calls still requires a restricted process."));
        var permissionsChanged = DrawPluginPermissions(
            "postnamazu",
            BundledActPluginCapabilities.PostNamazu);
        permissionsChanged |= DrawPluginPermissions(
            "triggernometry",
            BundledActPluginCapabilities.Triggernometry);
        permissionsChanged |= DrawPluginPermissions(
            "silverdasher",
            BundledActPluginCapabilities.SilverDasher);
        permissionsChanged |= DrawPluginPermissions(
            "matcha",
            BundledActPluginCapabilities.Matcha);
        changed |= permissionsChanged;

        ImGui.Separator();
        ImGui.TextUnformatted($"{text.Get("解析器", "Parser")}: {LocalizeState(parserStatus.State)}");
        ImGui.TextWrapped(parserStatus.Message);
        if (!string.IsNullOrWhiteSpace(parserStatus.Detail))
        {
            ImGui.TextWrapped(parserStatus.Detail);
        }

        if (ImGui.Button(text.Get("重启解析器", "Restart parser")))
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

        ImGui.Separator();
        var historyLimit = configuration.HistoryLimit;
        if (ImGui.SliderInt(text.Get("历史记录上限", "History limit"), ref historyLimit, 1, 200))
        {
            configuration.HistoryLimit = historyLimit;
            changed = true;
        }

        changed |= Checkbox(text.Get("显示战斗统计", "Combat Meter visible"), configuration.Meter.IsVisible, value => configuration.Meter.IsVisible = value);
        changed |= Checkbox(text.Get("锁定窗口", "Window locked"), configuration.Meter.IsLocked, value => configuration.Meter.IsLocked = value);
        changed |= Checkbox(text.Get("锁定时鼠标穿透", "Click-through when locked"), configuration.Meter.ClickThroughWhenLocked, value => configuration.Meter.ClickThroughWhenLocked = value);
        changed |= Checkbox(text.Get("脱战自动隐藏", "Auto hide"), configuration.Meter.AutoHideOutOfCombat, value => configuration.Meter.AutoHideOutOfCombat = value);
        changed |= SliderFloat(text.Get("背景透明度", "Background opacity"), configuration.Meter.BackgroundOpacity, 0.05f, 1.0f, value => configuration.Meter.BackgroundOpacity = value);
        changed |= SliderFloat(text.Get("字体缩放", "Font scale"), configuration.Meter.FontScale, 0.75f, 1.8f, value => configuration.Meter.FontScale = value);
        var refreshInterval = configuration.Meter.RefreshIntervalMs;
        if (ImGui.SliderInt(text.Get("DPS 刷新间隔（毫秒）", "DPS refresh interval (ms)"), ref refreshInterval, 250, 2000))
        {
            configuration.Meter.RefreshIntervalMs = refreshInterval;
            changed = true;
        }
        var dpsMetric = configuration.Meter.DpsMetric;
        if (ImGui.BeginCombo(text.Get("DPS 计算口径", "DPS metric"), DpsMetricLabel(dpsMetric)))
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
        ImGui.TextUnformatted(text.Get("战斗统计显示列", "Combat Meter columns"));
        changed |= Checkbox(text.Get("战斗标题", "Encounter header"), configuration.Meter.ShowHeader, value => configuration.Meter.ShowHeader = value);
        changed |= Checkbox(
            text.Get("收起（只显示自己）", "Collapsed (self only)"),
            configuration.Meter.CompactMode,
            value => configuration.Meter.CompactMode = value);
        changed |= Checkbox(text.Get("职业", "Job"), configuration.Meter.ShowJob, value => configuration.Meter.ShowJob = value);
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
        ImGui.TextDisabled(text.Get(
            "每名玩家固定一行，显示当前 DPS/HPS、伤害占比、暴击率、直暴率和死亡数。",
            "Each player uses one row showing current DPS/HPS, damage percentage, critical rate, critical-direct rate, and deaths."));
        var localPlayerColor = configuration.Meter.LocalPlayerColor;
        if (ImGui.ColorEdit4(text.Get("本地玩家颜色", "Local player color"), ref localPlayerColor))
        {
            configuration.Meter.LocalPlayerColor = localPlayerColor;
            changed = true;
        }

        ImGui.Separator();
        ImGui.TextUnformatted($"{text.Get("配置", "Config")}: {paths.ConfigDirectory}");
        ImGui.TextUnformatted($"{text.Get("调试日志", "Debug logs")}: {paths.LogDirectory}");
        ImGui.TextUnformatted($"{text.Get("战斗日志", "Combat logs")}: {paths.CombatLogDirectory}");
        if (ImGui.Button(text.Get("打开日志文件夹", "Open log directory")))
        {
            OpenDirectory(paths.LogDirectory);
        }

        ImGui.Separator();
        ImGui.TextWrapped(text.Get("恢复出厂设置会停止 ACT 宿主、备份所有可变数据，并恢复两个系统插件和默认设置。", "Factory reset stops the ACT host, backs up all mutable data, and restores the two system plugins and default settings."));
        if (!confirmFactoryReset)
        {
            if (ImGui.Button(text.Get("恢复出厂设置...", "Restore factory settings...")))
            {
                confirmFactoryReset = true;
            }
        }
        else
        {
            ImGui.TextWrapped(text.Get("按确认继续。此前状态仍可从备份目录恢复。", "Press confirm to continue. The previous state remains recoverable from the backup directory."));
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

        if (!string.IsNullOrWhiteSpace(resetResult))
        {
            ImGui.TextWrapped($"{text.Get("最近一次出厂备份", "Last factory reset backup")}: {resetResult}");
        }

        if (changed)
        {
            saveConfiguration();
        }

        if (permissionsChanged || hostConfigurationChanged)
        {
            applyPermissionChanges();
        }
    }

    public override void OnClose()
    {
        saveConfiguration();
    }

    public void Detach() => parserEngine.StatusChanged -= OnParserStatusChanged;

    private string FormatCactbotStatus()
    {
        var status = getCactbotOperationStatus();
        return status.State switch
        {
            CactbotOperationState.Checking => text.Get(
                "正在检查内置 Cactbot 资源…",
                "Checking bundled Cactbot assets…"),
            CactbotOperationState.Installing => text.Get(
                "正在安装 Cactbot 资源…",
                "Installing Cactbot assets…"),
            CactbotOperationState.Error => text.Get(
                $"Cactbot 安装失败：{status.ErrorMessage}",
                $"Cactbot installation failed: {status.ErrorMessage}"),
            _ when isCactbotInstalled() => text.Get(
                "资源已安装；OverlayPlugin 事件源可用。",
                "Assets installed; OverlayPlugin event source is available."),
            _ => text.Get(
                "未安装。请选择 OverlayPlugin/cactbot 官方 Release ZIP。",
                "Not installed. Select the official OverlayPlugin/cactbot Release ZIP."),
        };
    }

    private bool DrawPluginPermissions(
        string pluginId,
        IReadOnlyList<ActCapability> capabilities)
    {
        var changed = false;
        var displayName = pluginId.ToLowerInvariant() switch
        {
            "postnamazu" => text.Get("鲶鱼精邮差 / PostNamazu", "PostNamazu"),
            "silverdasher" => text.Get("银山雀儿 / SilverDasher", "SilverDasher"),
            "matcha" => text.Get("抹茶 / Cafe.Matcha", "Cafe.Matcha"),
            _ => pluginId,
        };
        if (!ImGui.TreeNode($"{displayName}##permissions-{pluginId}"))
        {
            return false;
        }

        foreach (var capability in capabilities)
        {
            var allowed = configuration.IsActCapabilityAllowed(pluginId, capability);
            if (ImGui.Checkbox(
                    $"{ActCapabilityDisplay.Label(capability, text)}##{pluginId}-{capability}",
                    ref allowed))
            {
                configuration.SetActCapability(pluginId, capability, allowed);
                logger.Information(
                    $"ACT permission changed: plugin={pluginId}, capability={capability}, allowed={allowed}.");
                changed = true;
            }
        }

        ImGui.TreePop();
        return changed;
    }

    private void OnParserStatusChanged(object? sender, ParserStatus status)
        => parserStatus = status;

    private void OpenDirectory(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(directory)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Failed to open log directory.");
        }
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

    private bool DrawCactbotOverlayManager(
        IReadOnlyList<ActOverlayTemplate> templates,
        bool templateCatalogAvailable)
    {
        var changed = false;
        configuration.OverlayWindows ??= new Dictionary<string, HtmlOverlayWindowSettings>(
            StringComparer.OrdinalIgnoreCase);
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

        ImGui.TextUnformatted(text.Get(
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
                if (ImGui.Selectable(
                        $"{FormatCactbotOverlayName(name)}  [{status}]##advanced-used-cactbot-{name}",
                        string.Equals(
                            name,
                            selectedUsedCactbotOverlay,
                            StringComparison.OrdinalIgnoreCase)))
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
                ImGui.PushID($"advanced-used-cactbot-actions-{selectedName}");
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
                            _ = openHtmlOverlay(selectedName);
                        }
                    }
                }
                else
                {
                    ImGui.TextDisabled(templateCatalogAvailable
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
                if (ImGui.Button(text.Get("移除并重置", "Remove and reset")))
                {
                    deleteHtmlOverlay(selectedName);
                    selectedUsedCactbotOverlay = null;
                }
                else if (localTemplateAvailable)
                {
                    var windowChanged = DrawHtmlOverlaySettings(selectedName);
                    changed |= windowChanged;
                    if (windowChanged)
                    {
                        applyOverlayWindowSettings(selectedName);
                    }
                }
                ImGui.PopID();
            }
        }

        ImGui.Spacing();
        ImGui.TextUnformatted(text.Get(
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

    private bool DrawHtmlOverlaySettings(string name)
    {
        var changed = false;
        var settings = configuration.GetOverlayWindowSettings(name);
        ImGui.TextDisabled($"{text.Get("窗口设置", "Window settings")}: {name}");
        var editing = settings.IsEditing;
        if (ImGui.Button(
                editing
                    ? $"{text.Get("完成编辑悬浮窗", "Finish editing overlay")}###{name}-edit-mode"
                    : $"{text.Get("编辑位置和大小", "Edit position and size")}###{name}-edit-mode"))
        {
            var beginEditing = !editing;
            if (!beginEditing || settings.IsVisible || openHtmlOverlay(name))
            {
                settings.SetEditing(beginEditing);
                changed = true;
            }
        }

        ImGui.SameLine();
        ImGui.TextDisabled(editing
            ? text.Get(
                "单击可操作网页按钮；按住并拖动可移动，拖动右下角斜纹可缩放。编辑期间游戏鼠标操作会暂时关闭。",
                "Click page controls normally; hold and drag to move, or drag the striped bottom-right grip to resize. Game mouse input is temporarily blocked while editing.")
            : text.Get(
                "编辑会暂时关闭穿透并解除位置锁定；完成后恢复穿透与锁定。",
                "Editing temporarily disables click-through and unlocks the layout; finishing restores both."));
        changed |= Checkbox(
            $"{text.Get("鼠标穿透", "Click-through")}###{name}-click-through",
            settings.IsClickThrough,
            settings.SetClickThrough);
        ImGui.SameLine();
        changed |= Checkbox(
            $"{text.Get("锁定位置和大小", "Lock position and size")}###{name}-locked",
            settings.IsLocked,
            settings.SetLocked);
        var zoomFactor = settings.ZoomFactor;
        if (ImGui.SliderFloat(
                $"{text.Get("页面缩放", "Page zoom")}###{name}-zoom",
                ref zoomFactor,
                0.5f,
                2.0f))
        {
            settings.ZoomFactor = zoomFactor;
            changed = true;
        }

        ImGui.TextDisabled(text.Get(
            "悬浮窗始终无边框透明置顶；需要操作网页时关闭穿透，需要操作游戏时打开穿透。",
            "Overlays stay borderless, transparent, and topmost. Turn click-through off for the page, or on to pass mouse input to the game."));
        return changed;
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

    private void DrawCompatibilityTarget(string name, string note, string url)
    {
        ImGui.BulletText(name);
        ImGui.SameLine();
        ImGui.TextDisabled(note);
        ImGui.TextWrapped($"{text.Get("网址", "URL")}: {url}");
    }

    private async Task RunFactoryResetAsync()
    {
        try
        {
            resetResult = await factoryReset().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Plugin shutdown owns cancellation and observes the reset task.
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Factory reset failed.");
            resetResult = $"Factory reset failed: {ex.Message}";
        }
    }

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
            logger.Error(ex, $"Failed to open {url}.");
        }
    }

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

    private string PlayerIdentityModeLabel(PlayerIdentityMode mode) => mode switch
    {
        PlayerIdentityMode.Job => text.Get("用职业替换 ID", "Replace names with jobs"),
        PlayerIdentityMode.Anonymous => text.Get("匿名编号", "Anonymous numbering"),
        _ => text.Get("显示原始 ID", "Show original names"),
    };
}

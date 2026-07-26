using Dalamud.Interface.Windowing;
using DalamudActCompat.Core.Interfaces;
using DalamudActCompat.ActRuntime;
using DalamudActCompat.Infrastructure.Logging;
using DalamudActCompat.Infrastructure.Storage;
using DalamudActCompat.Meter;
using DalamudActCompat.Parser;
using DalamudActCompat.Plugin;
using DalamudActCompat.Compatibility.PluginHost;
using Dalamud.Bindings.ImGui;

namespace DalamudActCompat.UI;

public sealed class SettingsWindow : Window
{
    private readonly PluginConfiguration configuration;
    private readonly IParserEngine parserEngine;
    private readonly PluginPaths paths;
    private readonly PluginLogger logger;
    private readonly Action saveConfiguration;
    private readonly Func<Task<string>> factoryReset;
    private readonly Func<IReadOnlyList<InstalledActPlugin>> discoverPlugins;
    private readonly Action selectPluginPackage;
    private readonly Action openPluginDirectory;
    private readonly UiText text;
    private readonly Func<bool> isCactbotInstalled;
    private readonly Action selectCactbotPackage;
    private readonly Action openCactbotOverlay;
    private readonly Action openCactbotSettings;
    private readonly Func<IReadOnlyList<ActOverlayTemplate>> getOverlayTemplates;
    private readonly Action<string> openHtmlOverlay;
    private readonly Action<string> openPluginConfiguration;
    private ParserStatus parserStatus;
    private bool confirmFactoryReset;
    private string? resetResult;

    public SettingsWindow(
        PluginConfiguration configuration,
        IParserEngine parserEngine,
        PluginPaths paths,
        PluginLogger logger,
        Action saveConfiguration,
        Func<Task<string>> factoryReset,
        Func<IReadOnlyList<InstalledActPlugin>> discoverPlugins,
        Action selectPluginPackage,
        Action openPluginDirectory,
        UiText text,
        Func<bool> isCactbotInstalled,
        Action selectCactbotPackage,
        Action openCactbotOverlay,
        Action openCactbotSettings,
        Func<IReadOnlyList<ActOverlayTemplate>> getOverlayTemplates,
        Action<string> openHtmlOverlay,
        Action<string> openPluginConfiguration)
        : base("ACT 兼容设置###DalamudActCompatSettings")
    {
        this.configuration = configuration;
        this.parserEngine = parserEngine;
        this.paths = paths;
        this.logger = logger;
        this.saveConfiguration = saveConfiguration;
        this.factoryReset = factoryReset;
        this.discoverPlugins = discoverPlugins;
        this.selectPluginPackage = selectPluginPackage;
        this.openPluginDirectory = openPluginDirectory;
        this.text = text;
        this.isCactbotInstalled = isCactbotInstalled;
        this.selectCactbotPackage = selectCactbotPackage;
        this.openCactbotOverlay = openCactbotOverlay;
        this.openCactbotSettings = openCactbotSettings;
        this.getOverlayTemplates = getOverlayTemplates;
        this.openHtmlOverlay = openHtmlOverlay;
        this.openPluginConfiguration = openPluginConfiguration;
        parserStatus = parserEngine.Status;
        parserEngine.StatusChanged += OnParserStatusChanged;
    }

    public override void Draw()
    {
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
            var enabled = plugin.Enabled;
            if (ImGui.Checkbox(
                    $"{plugin.Manifest.Name} {plugin.Manifest.Version}###{plugin.Manifest.Id}",
                    ref enabled))
            {
                if (enabled)
                {
                    configuration.DisabledActPluginIds.Remove(plugin.Manifest.Id);
                }
                else
                {
                    configuration.DisabledActPluginIds.Add(plugin.Manifest.Id);
                }

                changed = true;
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

        ImGui.TextDisabled(text.Get("安装或启停扩展后请重启解析器。", "Restart the parser after installing or changing plugins."));
        ImGui.Separator();
        ImGui.TextUnformatted("Cactbot（OverlayPlugin addon）");
        ImGui.TextDisabled(isCactbotInstalled()
            ? text.Get("资源已安装；OverlayPlugin 事件源可用。", "Assets installed; OverlayPlugin event source is available.")
            : text.Get("未安装。请选择 OverlayPlugin/cactbot 官方 Release ZIP。", "Not installed. Select the official OverlayPlugin/cactbot Release ZIP."));
        if (ImGui.Button(text.Get("安装/更新 Cactbot...", "Install/update Cactbot...")))
        {
            selectCactbotPackage();
        }
        ImGui.SameLine();
        if (ImGui.Button(text.Get("打开 Cactbot 窗口", "Open Cactbot window")))
        {
            openCactbotOverlay();
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

        ImGui.TextUnformatted(text.Get("OverlayPlugin HTML 悬浮窗", "OverlayPlugin HTML overlays"));
        var templates = getOverlayTemplates();
        if (templates.Count == 0)
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

            if (ImGui.Button(text.Get("打开所选 HTML 悬浮窗", "Open selected HTML overlay")))
            {
                openHtmlOverlay(configuration.SelectedOverlayTemplate);
            }
        }

        ImGui.TextUnformatted(text.Get("可选 ACT 扩展（与 Cactbot 本体分开）", "Optional ACT extensions (separate from Cactbot)"));
        DrawCompatibilityTarget("CactbotSelf / MoreLogLine", text.Get("国服额外日志扩展；实验性兼容。", "CN extra-log extension; experimental compatibility."), "https://github.com/tssailzz8/cacbotSelf");
        DrawCompatibilityTarget("PostNamazu", text.Get("鲶鱼精邮差；使用 Dalamud 同进程原生写入桥，保留 Triggernometry/HTTP/OverlayPlugin 入口。", "Uses the Dalamud in-process native write bridge while preserving Triggernometry, HTTP, and OverlayPlugin entry points."), "https://github.com/Natsukage/PostNamazu");
        DrawCompatibilityTarget("ACT.FoxTTS", text.Get("中文 TTS；安装/基础加载，音频后端需实测。", "Chinese TTS; install/basic load, audio backends require testing."), "https://github.com/Noisyfox/ACT.FoxTTS");
        DrawCompatibilityTarget("Triggernometry 中文维护版", text.Get("支持 DLL 与汉化 XML；日志/战斗生命周期/TTS API 已接入。", "DLL and translation XML supported; log/combat/TTS APIs wired."), "https://github.com/MnFeN/Triggernometry");

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

        changed |= Checkbox(text.Get("显示悬浮窗", "Meter visible"), configuration.Meter.IsVisible, value => configuration.Meter.IsVisible = value);
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
        ImGui.TextUnformatted(text.Get("悬浮窗列", "Meter columns"));
        changed |= Checkbox(text.Get("战斗标题", "Encounter header"), configuration.Meter.ShowHeader, value => configuration.Meter.ShowHeader = value);
        changed |= Checkbox(text.Get("职业", "Job"), configuration.Meter.ShowJob, value => configuration.Meter.ShowJob = value);
        changed |= Checkbox("DPS", configuration.Meter.ShowDps, value => configuration.Meter.ShowDps = value);
        changed |= Checkbox(text.Get("伤害", "Damage"), configuration.Meter.ShowDamage, value => configuration.Meter.ShowDamage = value);
        changed |= Checkbox(text.Get("伤害占比", "Damage percent"), configuration.Meter.ShowDamagePercent, value => configuration.Meter.ShowDamagePercent = value);
        changed |= Checkbox("HPS", configuration.Meter.ShowHps, value => configuration.Meter.ShowHps = value);
        changed |= Checkbox(text.Get("治疗量", "Healing"), configuration.Meter.ShowHealing, value => configuration.Meter.ShowHealing = value);
        changed |= Checkbox(text.Get("死亡", "Deaths"), configuration.Meter.ShowDeaths, value => configuration.Meter.ShowDeaths = value);
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
                _ = Task.Run(async () =>
                {
                    try
                    {
                        resetResult = await factoryReset().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        logger.Error(ex, "Factory reset failed.");
                        resetResult = $"Factory reset failed: {ex.Message}";
                    }
                });
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
    }

    public override void OnClose()
    {
        saveConfiguration();
    }

    public void Detach() => parserEngine.StatusChanged -= OnParserStatusChanged;

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
        ImGui.SameLine();
        if (ImGui.SmallButton($"{text.Get("打开项目页", "Open project page")}###{name}"))
        {
            OpenUrl(url);
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
        DpsMetric.Dps => text.Get("DPS（个人有效动作时长）", "DPS (personal active duration)"),
        DpsMetric.EncDps => text.Get("EncDPS（整场战斗时长）", "EncDPS (encounter duration)"),
        DpsMetric.ExtDps => text.Get("ExtDPS（ACT 兼容字段）", "ExtDPS (ACT compatibility field)"),
        _ => metric.ToString(),
    };
}

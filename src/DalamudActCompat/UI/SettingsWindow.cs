using Dalamud.Interface.Windowing;
using DalamudActCompat.Core.Interfaces;
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
        Func<IReadOnlyList<InstalledActPlugin>> discoverPlugins)
        : base("ACT Compat Settings###DalamudActCompatSettings")
    {
        this.configuration = configuration;
        this.parserEngine = parserEngine;
        this.paths = paths;
        this.logger = logger;
        this.saveConfiguration = saveConfiguration;
        this.factoryReset = factoryReset;
        this.discoverPlugins = discoverPlugins;
        parserStatus = parserEngine.Status;
        parserEngine.StatusChanged += OnParserStatusChanged;
    }

    public override void Draw()
    {
        var changed = false;
        changed |= Checkbox("Enable parsing", configuration.EnableParsing, value => configuration.EnableParsing = value);
        changed |= Checkbox("Auto start parser", configuration.AutoStartParser, value => configuration.AutoStartParser = value);
        changed |= Checkbox("Debug mode", configuration.DebugMode, value => configuration.DebugMode = value);
        changed |= Checkbox(
            "System plugin: FFXIV_ACT_Plugin",
            configuration.EmbeddedPlugins.FfxivActPluginEnabled,
            value => configuration.EmbeddedPlugins.FfxivActPluginEnabled = value);
        changed |= Checkbox(
            "System plugin: OverlayPlugin",
            configuration.EmbeddedPlugins.OverlayPluginEnabled,
            value => configuration.EmbeddedPlugins.OverlayPluginEnabled = value);

        ImGui.TextUnformatted("Installed ACT plugins");
        var installedPlugins = discoverPlugins();
        if (installedPlugins.Count == 0)
        {
            ImGui.TextDisabled("No optional ACT plugins installed.");
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
        }

        ImGui.TextDisabled("Install: /actcompat install \"C:\\path\\plugin.zip\". Restart the ACT host after changes.");

        ImGui.Separator();
        ImGui.TextUnformatted($"Parser: {parserStatus.State}");
        ImGui.TextWrapped(parserStatus.Message);
        if (!string.IsNullOrWhiteSpace(parserStatus.Detail))
        {
            ImGui.TextWrapped(parserStatus.Detail);
        }

        if (ImGui.Button("Restart parser"))
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
        if (ImGui.SliderInt("History limit", ref historyLimit, 1, 200))
        {
            configuration.HistoryLimit = historyLimit;
            changed = true;
        }

        changed |= Checkbox("Meter visible", configuration.Meter.IsVisible, value => configuration.Meter.IsVisible = value);
        changed |= Checkbox("Window locked", configuration.Meter.IsLocked, value => configuration.Meter.IsLocked = value);
        changed |= Checkbox("Click-through when locked", configuration.Meter.ClickThroughWhenLocked, value => configuration.Meter.ClickThroughWhenLocked = value);
        changed |= Checkbox("Auto hide", configuration.Meter.AutoHideOutOfCombat, value => configuration.Meter.AutoHideOutOfCombat = value);
        changed |= SliderFloat("Background opacity", configuration.Meter.BackgroundOpacity, 0.05f, 1.0f, value => configuration.Meter.BackgroundOpacity = value);
        changed |= SliderFloat("Font scale", configuration.Meter.FontScale, 0.75f, 1.8f, value => configuration.Meter.FontScale = value);

        ImGui.Separator();
        ImGui.TextUnformatted($"Config: {paths.ConfigDirectory}");
        ImGui.TextUnformatted($"Debug logs: {paths.LogDirectory}");
        ImGui.TextUnformatted($"Combat logs: {paths.CombatLogDirectory}");
        if (ImGui.Button("Open log directory"))
        {
            OpenDirectory(paths.LogDirectory);
        }

        ImGui.Separator();
        ImGui.TextWrapped("Factory reset stops the ACT host, backs up all mutable data, and restores the two system plugins and default settings.");
        if (!confirmFactoryReset)
        {
            if (ImGui.Button("Restore factory settings..."))
            {
                confirmFactoryReset = true;
            }
        }
        else
        {
            ImGui.TextWrapped("Press confirm to continue. The previous state remains recoverable from the backup directory.");
            if (ImGui.Button("Confirm factory reset"))
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
            if (ImGui.Button("Cancel"))
            {
                confirmFactoryReset = false;
            }
        }

        if (!string.IsNullOrWhiteSpace(resetResult))
        {
            ImGui.TextWrapped($"Last factory reset backup: {resetResult}");
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
}

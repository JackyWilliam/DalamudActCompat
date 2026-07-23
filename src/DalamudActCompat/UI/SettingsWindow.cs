using Dalamud.Interface.Windowing;
using DalamudActCompat.Core.Interfaces;
using DalamudActCompat.Infrastructure.Logging;
using DalamudActCompat.Infrastructure.Storage;
using DalamudActCompat.Meter;
using DalamudActCompat.Parser;
using DalamudActCompat.Plugin;
using ImGuiNET;

namespace DalamudActCompat.UI;

public sealed class SettingsWindow : Window
{
    private readonly PluginConfiguration configuration;
    private readonly IParserEngine parserEngine;
    private readonly PluginPaths paths;
    private readonly PluginLogger logger;
    private readonly Action saveConfiguration;
    private ParserStatus parserStatus;

    public SettingsWindow(
        PluginConfiguration configuration,
        IParserEngine parserEngine,
        PluginPaths paths,
        PluginLogger logger,
        Action saveConfiguration)
        : base("ACT Compat Settings###DalamudActCompatSettings")
    {
        this.configuration = configuration;
        this.parserEngine = parserEngine;
        this.paths = paths;
        this.logger = logger;
        this.saveConfiguration = saveConfiguration;
        parserStatus = parserEngine.Status;
        parserEngine.StatusChanged += OnParserStatusChanged;
    }

    public override void Draw()
    {
        var changed = false;
        changed |= Checkbox("Enable parsing", configuration.EnableParsing, value => configuration.EnableParsing = value);
        changed |= Checkbox("Auto start parser", configuration.AutoStartParser, value => configuration.AutoStartParser = value);
        changed |= Checkbox("Debug mode", configuration.DebugMode, value => configuration.DebugMode = value);

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

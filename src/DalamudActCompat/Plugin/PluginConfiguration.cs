using Dalamud.Configuration;
using DalamudActCompat.Meter;
using DalamudActCompat.Compatibility.PluginHost;

namespace DalamudActCompat.Plugin;

public sealed class PluginConfiguration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public bool EnableParsing { get; set; }

    public bool AutoStartParser { get; set; }

    public bool DebugMode { get; set; }

    public int HistoryLimit { get; set; } = 20;

    public string LogDirectory { get; set; } = string.Empty;

    public MeterSettings Meter { get; set; } = new();

    public EmbeddedPluginSettings EmbeddedPlugins { get; set; } = new();

    public HashSet<string> DisabledActPluginIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public void ResetToDefaults(string defaultLogDirectory)
    {
        EnableParsing = false;
        AutoStartParser = false;
        DebugMode = false;
        HistoryLimit = 20;
        LogDirectory = defaultLogDirectory;
        Meter = new MeterSettings();
        EmbeddedPlugins = new EmbeddedPluginSettings();
        DisabledActPluginIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }
}

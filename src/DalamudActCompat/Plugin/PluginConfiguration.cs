using Dalamud.Configuration;
using DalamudActCompat.Meter;

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
}

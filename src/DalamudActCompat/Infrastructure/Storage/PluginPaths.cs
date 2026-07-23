using Dalamud.Plugin;

namespace DalamudActCompat.Infrastructure.Storage;

public sealed class PluginPaths
{
    public PluginPaths(IDalamudPluginInterface pluginInterface)
    {
        ConfigDirectory = pluginInterface.ConfigDirectory.FullName;
        HistoryFile = Path.Combine(ConfigDirectory, "encounters.json");
        LogDirectory = Path.Combine(ConfigDirectory, "logs");
        CombatLogDirectory = Path.Combine(LogDirectory, "ffxiv");
        HostDirectory = Path.Combine(ConfigDirectory, "host");
    }

    public string ConfigDirectory { get; }

    public string HistoryFile { get; }

    public string LogDirectory { get; }

    public string CombatLogDirectory { get; }

    public string HostDirectory { get; }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(CombatLogDirectory);
        Directory.CreateDirectory(HostDirectory);
    }
}

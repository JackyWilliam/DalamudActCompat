using Dalamud.Plugin;

namespace DalamudActCompat.Infrastructure.Storage;

public sealed class PluginPaths
{
    public PluginPaths(IDalamudPluginInterface pluginInterface)
        : this(pluginInterface.ConfigDirectory.FullName)
    {
    }

    public PluginPaths(string configDirectory)
    {
        ConfigDirectory = Path.GetFullPath(configDirectory);
        HistoryFile = Path.Combine(ConfigDirectory, "encounters.json");
        LogDirectory = Path.Combine(ConfigDirectory, "logs");
        CombatLogDirectory = Path.Combine(LogDirectory, "ffxiv");
        HostDirectory = Path.Combine(ConfigDirectory, "host");
        ActPluginDirectory = Path.Combine(ConfigDirectory, "act-plugins");
        PluginStagingDirectory = Path.Combine(ConfigDirectory, ".plugin-staging");
        PluginBackupDirectory = Path.Combine(ConfigDirectory, "plugin-backups");
    }

    public string ConfigDirectory { get; }

    public string HistoryFile { get; }

    public string LogDirectory { get; }

    public string CombatLogDirectory { get; }

    public string HostDirectory { get; }

    public string ActPluginDirectory { get; }

    public string PluginStagingDirectory { get; }

    public string PluginBackupDirectory { get; }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(CombatLogDirectory);
        Directory.CreateDirectory(HostDirectory);
        Directory.CreateDirectory(ActPluginDirectory);
        Directory.CreateDirectory(PluginStagingDirectory);
        Directory.CreateDirectory(PluginBackupDirectory);
    }
}

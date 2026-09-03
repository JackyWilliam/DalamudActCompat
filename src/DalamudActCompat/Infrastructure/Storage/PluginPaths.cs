using Dalamud.Plugin;

namespace DalamudActCompat.Infrastructure.Storage;

public sealed class PluginPaths
{
    public PluginPaths(IDalamudPluginInterface pluginInterface, string? actPluginDirectory = null)
        : this(pluginInterface.ConfigDirectory.FullName, actPluginDirectory)
    {
    }

    public PluginPaths(string configDirectory, string? actPluginDirectory = null)
    {
        ConfigDirectory = Path.GetFullPath(configDirectory);
        HistoryFile = Path.Combine(ConfigDirectory, "encounters.json");
        LogDirectory = Path.Combine(ConfigDirectory, "logs");
        CombatLogDirectory = Path.Combine(LogDirectory, "ffxiv");
        EncounterLogDirectory = Path.Combine(LogDirectory, "encounters");
        FflogsCacheFile = Path.Combine(ConfigDirectory, "fflogs-estimates.json");
        HostDirectory = Path.Combine(ConfigDirectory, "host");
        ActPluginDirectory = string.IsNullOrWhiteSpace(actPluginDirectory)
            ? Path.Combine(ConfigDirectory, "act-plugins")
            : Path.GetFullPath(actPluginDirectory);
        CactbotDirectory = Path.Combine(ConfigDirectory, "cactbot");
        PluginStagingDirectory = Path.Combine(ConfigDirectory, ".plugin-staging");
        PluginBackupDirectory = Path.Combine(ConfigDirectory, "plugin-backups");
        BundledPluginUpdateCacheDirectory = Path.Combine(
            ConfigDirectory,
            "bundled-plugin-updates");
        ResourcePackCacheDirectory = Path.Combine(ConfigDirectory, "resource-packs");
        CloudCredentialFile = Path.Combine(ConfigDirectory, "cloud-account.dat");
        CloudDeviceFile = Path.Combine(ConfigDirectory, "cloud-device.dat");
        CloudBanFile = Path.Combine(ConfigDirectory, "cloud-ban.dat");
        CloudRollbackDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DalamudActCompat",
            "CloudRollbacks");
    }

    public string ConfigDirectory { get; }

    public string HistoryFile { get; }

    public string LogDirectory { get; }

    public string CombatLogDirectory { get; }

    public string EncounterLogDirectory { get; }

    public string FflogsCacheFile { get; }

    public string HostDirectory { get; }

    public string ActPluginDirectory { get; private set; }

    public string CactbotDirectory { get; }

    public string PluginStagingDirectory { get; }

    public string PluginBackupDirectory { get; }

    public string BundledPluginUpdateCacheDirectory { get; }

    public string ResourcePackCacheDirectory { get; }

    public string CloudCredentialFile { get; }

    public string CloudDeviceFile { get; }

    public string CloudBanFile { get; }

    public string CloudRollbackDirectory { get; }

    public void SetActPluginDirectory(string directory)
        => ActPluginDirectory = Path.GetFullPath(directory);

    public void EnsureCreated()
    {
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(CombatLogDirectory);
        Directory.CreateDirectory(EncounterLogDirectory);
        Directory.CreateDirectory(HostDirectory);
        Directory.CreateDirectory(ActPluginDirectory);
        Directory.CreateDirectory(CactbotDirectory);
        Directory.CreateDirectory(PluginStagingDirectory);
        Directory.CreateDirectory(PluginBackupDirectory);
        Directory.CreateDirectory(BundledPluginUpdateCacheDirectory);
        Directory.CreateDirectory(ResourcePackCacheDirectory);
        Directory.CreateDirectory(CloudRollbackDirectory);
    }
}

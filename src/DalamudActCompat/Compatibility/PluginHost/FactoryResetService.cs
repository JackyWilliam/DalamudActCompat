using DalamudActCompat.Core.Interfaces;
using DalamudActCompat.Infrastructure.Logging;
using DalamudActCompat.Infrastructure.Storage;
using DalamudActCompat.Plugin;

namespace DalamudActCompat.Compatibility.PluginHost;

public sealed class FactoryResetService
{
    private const string BackupDirectoryName = "factory-reset-backups";
    private readonly IParserEngine parserEngine;
    private readonly PluginPaths paths;
    private readonly PluginConfiguration configuration;
    private readonly PluginLogger logger;
    private readonly Action saveConfiguration;

    public FactoryResetService(
        IParserEngine parserEngine,
        PluginPaths paths,
        PluginConfiguration configuration,
        PluginLogger logger,
        Action saveConfiguration)
    {
        this.parserEngine = parserEngine;
        this.paths = paths;
        this.configuration = configuration;
        this.logger = logger;
        this.saveConfiguration = saveConfiguration;
    }

    public async Task<string> ResetAsync(CancellationToken cancellationToken)
    {
        await parserEngine.StopAsync(cancellationToken).ConfigureAwait(false);
        paths.EnsureCreated();

        var backupRoot = Path.Combine(paths.ConfigDirectory, BackupDirectoryName);
        var backupDirectory = Path.Combine(
            backupRoot,
            DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(backupDirectory);

        foreach (var entry in Directory.EnumerateFileSystemEntries(paths.ConfigDirectory))
        {
            if (Path.GetFileName(entry).Equals(BackupDirectoryName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var destination = Path.Combine(backupDirectory, Path.GetFileName(entry));
            if (Directory.Exists(entry))
            {
                Directory.Move(entry, destination);
            }
            else
            {
                File.Move(entry, destination);
            }
        }

        configuration.ResetToDefaults(paths.CombatLogDirectory);
        paths.EnsureCreated();
        saveConfiguration();
        logger.Information($"Factory settings restored. Backup: {backupDirectory}");
        return backupDirectory;
    }
}

using System.Reflection;
using DalamudActCompat.Infrastructure.Logging;

namespace DalamudActCompat.Infrastructure.Processes;

public sealed class CompatibilityHostAssets
{
    private const string ResourcePrefix = "DalamudActCompat.HostAssets.";

    private readonly string targetDirectory;
    private readonly PluginLogger logger;
    private readonly Assembly assembly;

    public CompatibilityHostAssets(string targetDirectory, PluginLogger logger)
    {
        this.targetDirectory = targetDirectory;
        this.logger = logger;
        assembly = typeof(CompatibilityHostAssets).Assembly;
    }

    public void EnsureExtracted()
    {
        var resources = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .ToArray();
        if (resources.Length == 0)
        {
            logger.Warning("Compatibility host embedded assets were not found.");
            return;
        }

        Directory.CreateDirectory(targetDirectory);
        foreach (var resourceName in resources)
        {
            var fileName = resourceName[ResourcePrefix.Length..];
            var destination = Path.Combine(targetDirectory, fileName);
            using var source = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Embedded host resource could not be opened: {resourceName}");

            if (File.Exists(destination) && new FileInfo(destination).Length == source.Length)
            {
                continue;
            }

            var temporary = destination + ".tmp";
            try
            {
                using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    source.CopyTo(output);
                    output.Flush(flushToDisk: true);
                }

                File.Move(temporary, destination, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }

        logger.Information($"Compatibility host assets are available under {targetDirectory}.");
    }
}

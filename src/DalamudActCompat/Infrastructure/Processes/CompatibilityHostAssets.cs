using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using DalamudActCompat.Infrastructure.Logging;

namespace DalamudActCompat.Infrastructure.Processes;

public sealed class CompatibilityHostAssets
{
    private const string ResourcePrefix = "DalamudActCompat.HostAssets.";
    private static readonly object ExtractionLock = new();

    private readonly string targetDirectory;
    private readonly PluginLogger logger;
    private readonly Assembly assembly;

    public CompatibilityHostAssets(string targetRootDirectory, PluginLogger logger)
    {
        this.logger = logger;
        assembly = typeof(CompatibilityHostAssets).Assembly;
        targetDirectory = Path.Combine(
            Path.GetFullPath(targetRootDirectory),
            GetAssetSetDirectoryName(assembly));
    }

    public string TargetDirectory => targetDirectory;

    public void EnsureExtracted()
    {
        // All Host roles use the same embedded files. Serializing extraction prevents
        // their startup paths from racing over the same temporary files.
        lock (ExtractionLock)
        {
            EnsureExtractedLocked();
        }
    }

    private void EnsureExtractedLocked()
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
            if (File.Exists(destination) && ResourceMatchesFile(resourceName, destination))
            {
                continue;
            }

            var temporary = destination + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
            try
            {
                using var source = assembly.GetManifestResourceStream(resourceName)
                    ?? throw new InvalidOperationException(
                        $"Embedded host resource could not be opened: {resourceName}");
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

    private static string GetAssetSetDirectoryName(Assembly sourceAssembly)
    {
        var identity = sourceAssembly
                           .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                           ?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(identity))
        {
            identity = sourceAssembly.GetName().Version?.ToString() ?? "unknown";
        }

        var safeIdentity = new string(identity.Select(character =>
                char.IsLetterOrDigit(character) || character is '.' or '-' or '_' or '+'
                    ? character
                    : '_')
            .ToArray());
        if (safeIdentity.Length <= 96)
        {
            return safeIdentity;
        }

        // A bounded hash keeps an unusual informational version from exceeding
        // Windows path limits while retaining a stable directory per build.
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    private bool ResourceMatchesFile(string resourceName, string path)
    {
        using var resource = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded host resource could not be opened: {resourceName}");
        var file = new FileInfo(path);
        if (file.Length != resource.Length)
        {
            return false;
        }

        using var existing = File.OpenRead(path);
        return SHA256.HashData(resource).AsSpan()
            .SequenceEqual(SHA256.HashData(existing));
    }
}

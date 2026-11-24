using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using DalamudActCompat.Infrastructure.Logging;

namespace DalamudActCompat.Infrastructure.Processes;

public sealed class CompatibilityHostAssets
{
    private const string ResourcePrefix = "DalamudActCompat.HostAssets.";
    private static readonly object ExtractionLock = new();
    // Read-only runtime assemblies already shipped in the Core plugin package.
    // Excluding them from host-*.zip avoids downloading ~9 MB of duplicate binaries
    // (primarily FFXIV_ACT_Plugin.Resource.dll) while copying them locally on demand.
    private static readonly string[] SharedPluginAssemblies =
    [
        "Advanced Combat Tracker.dll",
        "DalamudActCompat.Protocol.dll",
        "FFXIV_ACT_Plugin.dll",
        "FFXIV_ACT_Plugin.Common.dll",
        "FFXIV_ACT_Plugin.Config.dll",
        "FFXIV_ACT_Plugin.Logfile.dll",
        "FFXIV_ACT_Plugin.Memory.dll",
        "FFXIV_ACT_Plugin.Network.dll",
        "FFXIV_ACT_Plugin.Parse.dll",
        "FFXIV_ACT_Plugin.Resource.dll",
    ];

    private readonly string targetDirectory;
    private readonly PluginLogger logger;
    private readonly Assembly assembly;
    private string? packagedHostDirectory;

    public CompatibilityHostAssets(
        string targetRootDirectory,
        PluginLogger logger,
        string? packagedHostDirectory = null)
    {
        this.logger = logger;
        this.packagedHostDirectory = string.IsNullOrWhiteSpace(packagedHostDirectory)
            ? null
            : Path.GetFullPath(packagedHostDirectory);
        assembly = typeof(CompatibilityHostAssets).Assembly;
        targetDirectory = Path.Combine(
            Path.GetFullPath(targetRootDirectory),
            GetAssetSetDirectoryName(assembly));
    }

    public string TargetDirectory => targetDirectory;

    public void SetPackagedHostDirectory(string directory)
    {
        var normalized = Path.GetFullPath(directory);
        if (!Directory.Exists(normalized))
        {
            throw new DirectoryNotFoundException(
                $"Compatibility host resource directory does not exist: {normalized}");
        }

        lock (ExtractionLock)
        {
            // Resource downloads complete after plugin construction. Updating the source under
            // the extraction lock keeps a concurrent Host start from observing a partial switch.
            packagedHostDirectory = normalized;
        }
    }

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
        var packagedDirectory = packagedHostDirectory;
        if (packagedDirectory is not null && Directory.Exists(packagedDirectory))
        {
            EnsurePackagedFilesCopied(packagedDirectory);
            return;
        }

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

    private void EnsurePackagedFilesCopied(string packagedDirectory)
    {
        Directory.CreateDirectory(targetDirectory);
        var copiedFromPack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in Directory.GetFiles(packagedDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(source);
            copiedFromPack.Add(fileName);
            CopyFileAtomicallyIfChanged(source, Path.Combine(targetDirectory, fileName));
        }

        var pluginDirectory = Path.GetDirectoryName(assembly.Location);
        if (!string.IsNullOrWhiteSpace(pluginDirectory) && Directory.Exists(pluginDirectory))
        {
            foreach (var fileName in SharedPluginAssemblies)
            {
                if (copiedFromPack.Contains(fileName))
                {
                    continue;
                }

                var sharedSource = Path.Combine(pluginDirectory, fileName);
                if (File.Exists(sharedSource))
                {
                    CopyFileAtomicallyIfChanged(sharedSource, Path.Combine(targetDirectory, fileName));
                }
            }
        }

        logger.Information($"Compatibility host resource pack is available under {targetDirectory}.");
    }

    private static void CopyFileAtomicallyIfChanged(string source, string destination)
    {
        if (File.Exists(destination) && FilesMatch(source, destination))
        {
            return;
        }

        var temporary = destination + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.Copy(source, temporary, overwrite: false);
            using (var output = new FileStream(temporary, FileMode.Open, FileAccess.Write, FileShare.None))
            {
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

    private static bool FilesMatch(string left, string right)
    {
        var leftInfo = new FileInfo(left);
        var rightInfo = new FileInfo(right);
        if (leftInfo.Length != rightInfo.Length)
        {
            return false;
        }
        using var leftStream = leftInfo.OpenRead();
        using var rightStream = rightInfo.OpenRead();
        return SHA256.HashData(leftStream).AsSpan().SequenceEqual(SHA256.HashData(rightStream));
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

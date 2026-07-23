using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using DalamudActCompat.Infrastructure.Storage;

namespace DalamudActCompat.Compatibility.PluginHost;

public sealed partial class ActPluginPackageInstaller
{
    private const int MaximumEntryCount = 2048;
    private const long MaximumExpandedBytes = 256L * 1024 * 1024;
    private readonly PluginPaths paths;
    private readonly SemaphoreSlim installLock = new(1, 1);

    public ActPluginPackageInstaller(PluginPaths paths)
    {
        this.paths = paths;
    }

    public async Task<InstalledActPlugin> InstallAsync(
        string packagePath,
        CancellationToken cancellationToken)
    {
        var fullPackagePath = Path.GetFullPath(packagePath);
        if (!File.Exists(fullPackagePath))
        {
            throw new FileNotFoundException("ACT plugin package was not found.", fullPackagePath);
        }

        await installLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? stagingDirectory = null;
        string? installDirectory = null;
        string? backupDirectory = null;
        try
        {
            paths.EnsureCreated();
            stagingDirectory = Path.Combine(
                paths.PluginStagingDirectory,
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stagingDirectory);
            using var archive = ZipFile.OpenRead(fullPackagePath);
            ExtractSafely(archive, stagingDirectory);
            var manifest = await ReadManifestAsync(stagingDirectory, cancellationToken).ConfigureAwait(false);
            ValidateManifest(manifest, stagingDirectory);

            installDirectory = Path.Combine(paths.ActPluginDirectory, manifest.Id);
            if (Directory.Exists(installDirectory))
            {
                backupDirectory = Path.Combine(
                    paths.PluginBackupDirectory,
                    $"{manifest.Id}-{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}");
                Directory.CreateDirectory(paths.PluginBackupDirectory);
                Directory.Move(installDirectory, backupDirectory);
            }

            Directory.Move(stagingDirectory, installDirectory);
            return new InstalledActPlugin(manifest, installDirectory, true);
        }
        catch
        {
            if (stagingDirectory is not null && Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, true);
            }

            if (installDirectory is not null &&
                backupDirectory is not null &&
                !Directory.Exists(installDirectory) &&
                Directory.Exists(backupDirectory))
            {
                Directory.Move(backupDirectory, installDirectory);
            }

            throw;
        }
        finally
        {
            installLock.Release();
        }
    }

    public IReadOnlyList<InstalledActPlugin> Discover(ISet<string> disabledPluginIds)
    {
        if (!Directory.Exists(paths.ActPluginDirectory))
        {
            return [];
        }

        var plugins = new List<InstalledActPlugin>();
        foreach (var directory in Directory.EnumerateDirectories(paths.ActPluginDirectory))
        {
            try
            {
                var manifest = ReadManifest(directory);
                ValidateManifest(manifest, directory);
                plugins.Add(new InstalledActPlugin(
                    manifest,
                    directory,
                    !disabledPluginIds.Contains(manifest.Id)));
            }
            catch
            {
                // Invalid packages stay on disk for diagnosis but are not load candidates.
            }
        }

        return plugins.OrderBy(plugin => plugin.Manifest.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void ExtractSafely(ZipArchive archive, string stagingDirectory)
    {
        if (archive.Entries.Count > MaximumEntryCount)
        {
            throw new InvalidDataException($"Plugin package contains more than {MaximumEntryCount} entries.");
        }

        var expandedBytes = archive.Entries.Sum(entry => entry.Length);
        if (expandedBytes > MaximumExpandedBytes)
        {
            throw new InvalidDataException($"Expanded plugin package exceeds {MaximumExpandedBytes} bytes.");
        }

        var root = Path.GetFullPath(stagingDirectory) + Path.DirectorySeparatorChar;
        foreach (var entry in archive.Entries)
        {
            var destination = Path.GetFullPath(Path.Combine(stagingDirectory, entry.FullName));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Package entry escapes its install directory: {entry.FullName}");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: false);
        }
    }

    private static async Task<ActPluginManifest> ReadManifestAsync(
        string directory,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(Path.Combine(directory, ActPluginManifest.FileName));
        return await JsonSerializer.DeserializeAsync<ActPluginManifest>(
                   stream,
                   cancellationToken: cancellationToken).ConfigureAwait(false)
               ?? throw new InvalidDataException("Plugin manifest is empty.");
    }

    private static ActPluginManifest ReadManifest(string directory)
    {
        using var stream = File.OpenRead(Path.Combine(directory, ActPluginManifest.FileName));
        return JsonSerializer.Deserialize<ActPluginManifest>(stream)
               ?? throw new InvalidDataException("Plugin manifest is empty.");
    }

    private static void ValidateManifest(ActPluginManifest manifest, string directory)
    {
        if (!PluginIdPattern().IsMatch(manifest.Id))
        {
            throw new InvalidDataException("Plugin id must contain only lowercase letters, digits, dots, dashes, or underscores.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Name) ||
            string.IsNullOrWhiteSpace(manifest.Version) ||
            string.IsNullOrWhiteSpace(manifest.EntryAssembly) ||
            string.IsNullOrWhiteSpace(manifest.EntryType))
        {
            throw new InvalidDataException("Plugin manifest is missing required fields.");
        }

        if (manifest.HostApiVersion != 1)
        {
            throw new InvalidDataException($"Unsupported host API version: {manifest.HostApiVersion}");
        }

        var root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        var entryAssembly = Path.GetFullPath(Path.Combine(directory, manifest.EntryAssembly));
        if (!entryAssembly.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(entryAssembly))
        {
            throw new InvalidDataException("Plugin entry assembly is missing or outside the package.");
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{1,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex PluginIdPattern();
}

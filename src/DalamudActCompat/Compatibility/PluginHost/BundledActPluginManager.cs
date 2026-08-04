using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using DalamudActCompat.Plugin;

namespace DalamudActCompat.Compatibility.PluginHost;

public sealed class BundledActPluginManager
{
    public const string DirectoryName = "BundledActPlugins";
    public const string LockFileName = "bundled-plugins.lock.json";

    private readonly ActPluginPackageInstaller installer;
    private readonly PluginConfiguration configuration;
    private readonly string hostVersion;
    private readonly IReadOnlyList<BundledActPluginDescriptor> bundledPlugins;
    private readonly Dictionary<string, BundledActPluginDescriptor> onlineUpdates =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object updateLock = new();

    public BundledActPluginManager(
        string pluginAssemblyDirectory,
        string hostVersion,
        ActPluginPackageInstaller installer,
        PluginConfiguration configuration)
    {
        this.installer = installer;
        this.configuration = configuration;
        this.hostVersion = hostVersion;
        bundledPlugins = LoadAndValidate(
            Path.Combine(pluginAssemblyDirectory, DirectoryName));
    }

    public IReadOnlyList<BundledActPluginDescriptor> Plugins => bundledPlugins;

    public IReadOnlyList<BundledActPluginDescriptor> GetDisclosures()
    {
        var installed = installer
            .Discover(new HashSet<string>(StringComparer.OrdinalIgnoreCase))
            .ToArray();
        lock (updateLock)
        {
            return bundledPlugins
                .Select(plugin => GetEffectivePlugin(plugin, installed))
                .ToArray();
        }
    }

    public int ApplyOnlineUpdates(
        IReadOnlyList<BundledActPluginDescriptor> candidates)
    {
        var applied = 0;
        lock (updateLock)
        {
            configuration.BundledPluginUpdateRecords ??=
                new Dictionary<string, BundledActPluginUpdateRecord>(
                    StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in candidates)
            {
                var bundled = bundledPlugins.FirstOrDefault(plugin =>
                    string.Equals(
                        plugin.Id,
                        candidate.Id,
                        StringComparison.OrdinalIgnoreCase));
                if (bundled is null || !candidate.IsOnlineUpdate)
                {
                    throw new InvalidDataException(
                        $"Online update does not match a bundled plugin: {candidate.Id}.");
                }

                ValidateAssembly(candidate);
                var baselineVersion = bundled.Version;
                var baselineSha = bundled.Sha256;
                if (TryGetCurrentUpdateRecord(bundled, out var record))
                {
                    baselineVersion = record.Version;
                    baselineSha = record.Sha256;
                }

                var comparison = BundledActPluginUpdateChecker.CompareVersions(
                    candidate.Version,
                    baselineVersion);
                if (comparison < 0 ||
                    comparison == 0 &&
                    string.Equals(
                        candidate.Sha256,
                        baselineSha,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                onlineUpdates[candidate.Id] = candidate;
                applied++;
            }
        }

        return applied;
    }

    public IReadOnlyList<BundledActPluginDescriptor> GetPendingDisclosures()
    {
        var installed = installer
            .Discover(new HashSet<string>(StringComparer.OrdinalIgnoreCase))
            .ToArray();
        lock (updateLock)
        {
            return bundledPlugins
                .Select(plugin => GetEffectivePlugin(plugin, installed))
                .Where(plugin =>
                    !IsAcknowledged(plugin) ||
                    !installed.Any(current =>
                        string.Equals(
                            current.Manifest.Id,
                            plugin.Id,
                            StringComparison.OrdinalIgnoreCase) &&
                        IsCurrentPackage(current, plugin)))
                .ToArray();
        }
    }

    public bool IsAllowedToLoad(InstalledActPlugin installed)
    {
        var bundled = bundledPlugins.FirstOrDefault(plugin =>
            string.Equals(
                plugin.Id,
                installed.Manifest.Id,
                StringComparison.OrdinalIgnoreCase));
        if (bundled is null)
        {
            return true;
        }

        lock (updateLock)
        {
            var effective = GetEffectivePlugin(bundled, [installed]);
            return IsAcknowledged(effective) &&
                   IsCurrentPackage(installed, effective);
        }
    }

    public async Task InstallAndAcknowledgeAsync(
        IReadOnlyList<BundledActPluginDescriptor> selected,
        CancellationToken cancellationToken)
    {
        var acknowledged = new List<BundledActPluginDescriptor>(selected.Count);
        foreach (var plugin in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = installer
                .Discover(new HashSet<string>(StringComparer.OrdinalIgnoreCase))
                .FirstOrDefault(installed =>
                    string.Equals(
                        installed.Manifest.Id,
                        plugin.Id,
                        StringComparison.OrdinalIgnoreCase) &&
                    IsCurrentPackage(installed, plugin));
            if (current is null)
            {
                if (string.IsNullOrWhiteSpace(plugin.AssemblyPath) ||
                    !File.Exists(plugin.AssemblyPath))
                {
                    throw new FileNotFoundException(
                        $"The approved DLL update cache is missing for {plugin.Id}.",
                        plugin.AssemblyPath);
                }

                var result = await installer
                    .InstallAsync(plugin.AssemblyPath, cancellationToken)
                    .ConfigureAwait(false);
                if (!string.Equals(
                        result.Manifest.Id,
                        plugin.Id,
                        StringComparison.OrdinalIgnoreCase) ||
                    !IsCurrentPackage(result, plugin))
                {
                    throw new InvalidDataException(
                        $"Bundled plugin installation did not match its disclosure: {plugin.Id}.");
                }
            }

            acknowledged.Add(plugin);
        }

        lock (updateLock)
        {
            configuration.BundledPluginDisclosureKeys ??=
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            configuration.BundledPluginUpdateRecords ??=
                new Dictionary<string, BundledActPluginUpdateRecord>(
                    StringComparer.OrdinalIgnoreCase);
            foreach (var plugin in acknowledged)
            {
                configuration.BundledPluginDisclosureKeys[plugin.Id] =
                    GetDisclosureKey(plugin);
                configuration.DisabledActPluginIds.Remove(plugin.Id);
                if (plugin.IsOnlineUpdate)
                {
                    configuration.BundledPluginUpdateRecords[plugin.Id] =
                        new BundledActPluginUpdateRecord
                        {
                            HostVersionWhenAccepted = hostVersion,
                            Version = plugin.Version,
                            DownloadUrl = plugin.DownloadUrl,
                            SourceUrl = plugin.SourceUrl,
                            Sha256 = plugin.Sha256,
                        };
                }
                else
                {
                    configuration.BundledPluginUpdateRecords.Remove(plugin.Id);
                }
            }
        }
    }

    private BundledActPluginDescriptor GetEffectivePlugin(
        BundledActPluginDescriptor bundled,
        IReadOnlyList<InstalledActPlugin> installed)
    {
        if (onlineUpdates.TryGetValue(bundled.Id, out var online))
        {
            return online;
        }

        if (!TryGetCurrentUpdateRecord(bundled, out var record) ||
            !installed.Any(plugin =>
                string.Equals(
                    plugin.Manifest.Id,
                    bundled.Id,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    plugin.Manifest.Version,
                    record.Version,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    plugin.Manifest.SourceSha256,
                    record.Sha256,
                    StringComparison.OrdinalIgnoreCase)))
        {
            return bundled;
        }

        return new BundledActPluginDescriptor
        {
            Id = bundled.Id,
            Name = bundled.Name,
            Version = record.Version,
            Author = bundled.Author,
            Maintainer = bundled.Maintainer,
            Copyright = bundled.Copyright,
            ProjectUrl = bundled.ProjectUrl,
            DownloadUrl = record.DownloadUrl,
            SourceUrl = record.SourceUrl,
            License = bundled.License,
            LicenseFile = bundled.LicenseFile,
            RelativeAssembly = bundled.RelativeAssembly,
            Sha256 = record.Sha256,
            IsOnlineUpdate = true,
        };
    }

    private bool TryGetCurrentUpdateRecord(
        BundledActPluginDescriptor bundled,
        out BundledActPluginUpdateRecord record)
    {
        configuration.BundledPluginUpdateRecords ??=
            new Dictionary<string, BundledActPluginUpdateRecord>(
                StringComparer.OrdinalIgnoreCase);
        if (!configuration.BundledPluginUpdateRecords.TryGetValue(
                bundled.Id,
                out record!) ||
            string.IsNullOrWhiteSpace(record.Version) ||
            string.IsNullOrWhiteSpace(record.DownloadUrl) ||
            string.IsNullOrWhiteSpace(record.SourceUrl) ||
            record.Sha256.Length != 64)
        {
            record = null!;
            return false;
        }

        var comparison = BundledActPluginUpdateChecker.CompareVersions(
            record.Version,
            bundled.Version);
        if (comparison < 0 ||
            comparison == 0 &&
            !string.Equals(
                record.Sha256,
                bundled.Sha256,
                StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(
                record.HostVersionWhenAccepted,
                hostVersion,
                StringComparison.Ordinal))
        {
            record = null!;
            return false;
        }

        return true;
    }

    private bool IsAcknowledged(BundledActPluginDescriptor plugin)
    {
        configuration.BundledPluginDisclosureKeys ??=
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        return configuration.BundledPluginDisclosureKeys.TryGetValue(
                   plugin.Id,
                   out var accepted) &&
               string.Equals(
                   accepted,
                   GetDisclosureKey(plugin),
                   StringComparison.Ordinal);
    }

    private string GetDisclosureKey(BundledActPluginDescriptor plugin)
        => $"{hostVersion}|{plugin.Id}|{plugin.Version}|{plugin.Sha256}";

    private static bool IsCurrentPackage(
        InstalledActPlugin installed,
        BundledActPluginDescriptor bundled)
        => string.Equals(
               installed.Manifest.Version,
               bundled.Version,
               StringComparison.OrdinalIgnoreCase) &&
           string.Equals(
               installed.Manifest.SourceSha256,
               bundled.Sha256,
               StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<BundledActPluginDescriptor> LoadAndValidate(
        string bundleDirectory)
    {
        var root = Path.GetFullPath(bundleDirectory);
        var lockPath = Path.Combine(root, LockFileName);
        using var stream = File.OpenRead(lockPath);
        var manifest = JsonSerializer.Deserialize<BundledActPluginLock>(
                           stream,
                           new JsonSerializerOptions
                           {
                               PropertyNameCaseInsensitive = true,
                           })
                       ?? throw new InvalidDataException(
                           "Bundled plugin lock is empty.");
        if (manifest.SchemaVersion != 1 || manifest.Plugins.Count == 0)
        {
            throw new InvalidDataException(
                $"Unsupported bundled plugin lock schema: {manifest.SchemaVersion}.");
        }

        var rootPrefix = root + Path.DirectorySeparatorChar;
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var plugin in manifest.Plugins)
        {
            if (!seenIds.Add(plugin.Id) ||
                string.IsNullOrWhiteSpace(plugin.Name) ||
                string.IsNullOrWhiteSpace(plugin.Version) ||
                string.IsNullOrWhiteSpace(plugin.Author) ||
                string.IsNullOrWhiteSpace(plugin.Maintainer) ||
                string.IsNullOrWhiteSpace(plugin.ProjectUrl) ||
                string.IsNullOrWhiteSpace(plugin.DownloadUrl) ||
                string.IsNullOrWhiteSpace(plugin.SourceUrl) ||
                string.IsNullOrWhiteSpace(plugin.License) ||
                string.IsNullOrWhiteSpace(plugin.RelativeAssembly) ||
                plugin.Sha256.Length != 64)
            {
                throw new InvalidDataException(
                    "Bundled plugin lock contains an invalid entry.");
            }

            var assemblyPath = Path.GetFullPath(
                Path.Combine(root, plugin.RelativeAssembly));
            if (!assemblyPath.StartsWith(
                    rootPrefix,
                    StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(assemblyPath))
            {
                throw new InvalidDataException(
                    $"Bundled plugin assembly is missing or outside its package: {plugin.Id}.");
            }

            plugin.AssemblyPath = assemblyPath;
            ValidateAssembly(plugin);
        }

        return manifest.Plugins;
    }

    private static void ValidateAssembly(
        BundledActPluginDescriptor plugin)
    {
        using var assemblyStream = File.OpenRead(plugin.AssemblyPath);
        var actualSha = Convert
            .ToHexString(SHA256.HashData(assemblyStream))
            .ToLowerInvariant();
        var actualVersion = FileVersionInfo
            .GetVersionInfo(plugin.AssemblyPath)
            .FileVersion;
        if (!string.Equals(
                actualSha,
                plugin.Sha256,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                actualVersion,
                plugin.Version,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Bundled plugin version or hash does not match its disclosure: {plugin.Id}.");
        }
    }
}

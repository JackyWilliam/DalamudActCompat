using System.Diagnostics;
using System.IO.Compression;
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
    private IReadOnlyList<BundledActPluginDescriptor> bundledPlugins = [];
    private readonly Dictionary<string, BundledActPluginDescriptor> onlineUpdates =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object updateLock = new();

    public BundledActPluginManager(
        string pluginAssemblyDirectory,
        string hostVersion,
        ActPluginPackageInstaller installer,
        PluginConfiguration configuration,
        bool directoryIsBundleRoot = false)
        : this(hostVersion, installer, configuration)
    {
        LoadBundle(directoryIsBundleRoot
            ? pluginAssemblyDirectory
            : Path.Combine(pluginAssemblyDirectory, DirectoryName));
    }

    public BundledActPluginManager(
        string hostVersion,
        ActPluginPackageInstaller installer,
        PluginConfiguration configuration)
    {
        this.installer = installer;
        this.configuration = configuration;
        this.hostVersion = hostVersion;
    }

    public IReadOnlyList<BundledActPluginDescriptor> Plugins
    {
        get
        {
            lock (updateLock)
            {
                return bundledPlugins.ToArray();
            }
        }
    }

    public void LoadBundle(string bundleDirectory)
    {
        var loaded = LoadAndValidate(bundleDirectory);
        lock (updateLock)
        {
            // Online candidates are tied to the disclosure baseline from one bundle version.
            // Replacing the pack must not carry those candidates into the new baseline.
            bundledPlugins = loaded;
            onlineUpdates.Clear();
        }
    }

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
                if (bundled is null ||
                    bundled.DisableOnlineUpdates ||
                    !candidate.IsOnlineUpdate)
                {
                    throw new InvalidDataException(
                        $"Online update does not match a bundled plugin: {candidate.Id}.");
                }

                ValidateArtifact(candidate);
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
        lock (updateLock)
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
                var installSource = string.IsNullOrWhiteSpace(plugin.PackagePath)
                    ? plugin.AssemblyPath
                    : plugin.PackagePath;
                if (string.IsNullOrWhiteSpace(installSource) ||
                    !File.Exists(installSource))
                {
                    throw new FileNotFoundException(
                        $"The approved plugin artifact is missing for {plugin.Id}.",
                        installSource);
                }

                var result = await installer
                    .InstallAsync(installSource, cancellationToken)
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
                if (plugin.EnableAfterInstall)
                {
                    configuration.DisabledActPluginIds.Remove(plugin.Id);
                }
                if (plugin.IsOnlineUpdate)
                {
                    configuration.BundledPluginUpdateRecords[plugin.Id] =
                        new BundledActPluginUpdateRecord
                        {
                            HostVersionWhenAccepted = hostVersion,
                            BundledSha256WhenAccepted = bundledPlugins
                                .First(bundled => string.Equals(
                                    bundled.Id,
                                    plugin.Id,
                                    StringComparison.OrdinalIgnoreCase))
                                .Sha256,
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
        if (bundled.DisableOnlineUpdates)
        {
            return bundled;
        }

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
            DisableOnlineUpdates = bundled.DisableOnlineUpdates,
            EnableAfterInstall = bundled.EnableAfterInstall,
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
        if (comparison < 0)
        {
            record = null!;
            return false;
        }

        if (string.IsNullOrWhiteSpace(record.BundledSha256WhenAccepted))
        {
            // Older records only named the Host release. Migrate them when the same Host
            // created the record or the exact downloaded artifact was explicitly accepted.
            if (!string.Equals(
                    record.HostVersionWhenAccepted,
                    hostVersion,
                    StringComparison.Ordinal) &&
                !IsUpdateRecordAcknowledged(bundled.Id, record))
            {
                record = null!;
                return false;
            }

            record.BundledSha256WhenAccepted = bundled.Sha256;
        }
        else if (!string.Equals(
                     record.BundledSha256WhenAccepted,
                     bundled.Sha256,
                     StringComparison.OrdinalIgnoreCase))
        {
            // A changed bundle is a new trust baseline even when its display version was
            // accidentally left unchanged, so do not let an older online record mask it.
            record = null!;
            return false;
        }

        return true;
    }

    private bool IsUpdateRecordAcknowledged(
        string pluginId,
        BundledActPluginUpdateRecord record)
    {
        configuration.BundledPluginDisclosureKeys ??=
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!configuration.BundledPluginDisclosureKeys.TryGetValue(
                pluginId,
                out var accepted))
        {
            return false;
        }

        var stable = $"{pluginId}|{record.Version}|{record.Sha256}|";
        return string.Equals(accepted, stable, StringComparison.Ordinal) ||
               accepted.EndsWith($"|{stable}", StringComparison.Ordinal);
    }

    private bool IsAcknowledged(BundledActPluginDescriptor plugin)
    {
        configuration.BundledPluginDisclosureKeys ??=
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!configuration.BundledPluginDisclosureKeys.TryGetValue(
                plugin.Id,
                out var accepted))
        {
            return false;
        }

        var current = GetDisclosureKey(plugin);
        if (string.Equals(accepted, current, StringComparison.Ordinal))
        {
            return true;
        }

        // Releases before 0.3.10.2 prefixed the stable artifact identity with the Host
        // version. Accept and migrate that exact legacy suffix so an unchanged DLL does
        // not ask for the same disclosure after every DalamudActCompat update.
        if (!accepted.EndsWith($"|{current}", StringComparison.Ordinal))
        {
            return false;
        }

        configuration.BundledPluginDisclosureKeys[plugin.Id] = current;
        return true;
    }

    private static string GetDisclosureKey(BundledActPluginDescriptor plugin)
        => $"{plugin.Id}|{plugin.Version}|{plugin.Sha256}|{plugin.PackageSha256}";

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

            var hasPackage = !string.IsNullOrWhiteSpace(plugin.RelativePackage) ||
                             !string.IsNullOrWhiteSpace(plugin.PackageSha256);
            if (hasPackage)
            {
                if (string.IsNullOrWhiteSpace(plugin.RelativePackage) ||
                    plugin.PackageSha256.Length != 64)
                {
                    throw new InvalidDataException(
                        $"Bundled plugin package metadata is incomplete: {plugin.Id}.");
                }

                var packagePath = Path.GetFullPath(
                    Path.Combine(root, plugin.RelativePackage));
                if (!packagePath.StartsWith(
                        rootPrefix,
                        StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(
                        Path.GetExtension(packagePath),
                        ".zip",
                        StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(packagePath))
                {
                    throw new InvalidDataException(
                        $"Bundled plugin package is missing or outside its bundle: {plugin.Id}.");
                }

                plugin.PackagePath = packagePath;
            }
            else
            {
                var assemblyPath = Path.GetFullPath(
                    Path.Combine(root, plugin.RelativeAssembly));
                if (!assemblyPath.StartsWith(
                        rootPrefix,
                        StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(assemblyPath))
                {
                    throw new InvalidDataException(
                        $"Bundled plugin assembly is missing or outside its bundle: {plugin.Id}.");
                }

                plugin.AssemblyPath = assemblyPath;
            }

            ValidateArtifact(plugin);
        }

        return manifest.Plugins;
    }

    private static void ValidateArtifact(
        BundledActPluginDescriptor plugin)
    {
        if (!string.IsNullOrWhiteSpace(plugin.PackagePath))
        {
            using var packageStream = File.OpenRead(plugin.PackagePath);
            var actualPackageSha = Convert
                .ToHexString(SHA256.HashData(packageStream))
                .ToLowerInvariant();
            if (!string.Equals(
                    actualPackageSha,
                    plugin.PackageSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Bundled plugin package hash does not match its disclosure: {plugin.Id}.");
            }

            packageStream.Position = 0;
            using var archive = new ZipArchive(
                packageStream,
                ZipArchiveMode.Read,
                leaveOpen: true);
            var expectedEntry = plugin.RelativeAssembly.Replace('\\', '/');
            var entries = archive.Entries
                .Where(entry => string.Equals(
                    entry.FullName,
                    expectedEntry,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (entries.Length != 1)
            {
                throw new InvalidDataException(
                    $"Bundled plugin package does not contain exactly one disclosed entry assembly: {plugin.Id}.");
            }

            using var packageAssemblyStream = entries[0].Open();
            var actualAssemblySha = Convert
                .ToHexString(SHA256.HashData(packageAssemblyStream))
                .ToLowerInvariant();
            if (!string.Equals(
                    actualAssemblySha,
                    plugin.Sha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Bundled plugin package entry hash does not match its disclosure: {plugin.Id}.");
            }

            return;
        }

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

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
    private readonly IReadOnlyList<BundledActPluginDescriptor> plugins;

    public BundledActPluginManager(
        string pluginAssemblyDirectory,
        string hostVersion,
        ActPluginPackageInstaller installer,
        PluginConfiguration configuration)
    {
        this.installer = installer;
        this.configuration = configuration;
        this.hostVersion = hostVersion;
        plugins = LoadAndValidate(Path.Combine(pluginAssemblyDirectory, DirectoryName));
    }

    public IReadOnlyList<BundledActPluginDescriptor> Plugins => plugins;

    public IReadOnlyList<BundledActPluginDescriptor> GetPendingDisclosures()
    {
        var installed = installer
            .Discover(new HashSet<string>(StringComparer.OrdinalIgnoreCase))
            .ToArray();
        return plugins
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

    public bool IsAllowedToLoad(InstalledActPlugin installed)
    {
        var bundled = plugins.FirstOrDefault(
            plugin => string.Equals(plugin.Id, installed.Manifest.Id, StringComparison.OrdinalIgnoreCase));
        return bundled is null || IsAcknowledged(bundled) && IsCurrentPackage(installed, bundled);
    }

    public async Task InstallAndAcknowledgeAsync(
        IReadOnlyList<BundledActPluginDescriptor> selected,
        CancellationToken cancellationToken)
    {
        var installed = new List<BundledActPluginDescriptor>(selected.Count);
        foreach (var plugin in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await installer
                .InstallAsync(plugin.AssemblyPath, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(result.Manifest.Id, plugin.Id, StringComparison.OrdinalIgnoreCase) ||
                !IsCurrentPackage(result, plugin))
            {
                throw new InvalidDataException(
                    $"Bundled plugin installation did not match its lock entry: {plugin.Id}.");
            }

            installed.Add(plugin);
        }

        configuration.BundledPluginDisclosureKeys ??=
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var plugin in installed)
        {
            configuration.BundledPluginDisclosureKeys[plugin.Id] = GetDisclosureKey(plugin);
            configuration.DisabledActPluginIds.Remove(plugin.Id);
        }
    }

    private bool IsAcknowledged(BundledActPluginDescriptor plugin)
    {
        configuration.BundledPluginDisclosureKeys ??=
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        return configuration.BundledPluginDisclosureKeys.TryGetValue(plugin.Id, out var accepted) &&
               string.Equals(accepted, GetDisclosureKey(plugin), StringComparison.Ordinal);
    }

    private string GetDisclosureKey(BundledActPluginDescriptor plugin)
        => $"{hostVersion}|{plugin.Id}|{plugin.Version}|{plugin.Sha256}";

    private static bool IsCurrentPackage(
        InstalledActPlugin installed,
        BundledActPluginDescriptor bundled)
        => string.Equals(installed.Manifest.Version, bundled.Version, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(installed.Manifest.SourceSha256, bundled.Sha256, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<BundledActPluginDescriptor> LoadAndValidate(string bundleDirectory)
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
                       ?? throw new InvalidDataException("Bundled plugin lock is empty.");
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
                throw new InvalidDataException("Bundled plugin lock contains an invalid entry.");
            }

            var assemblyPath = Path.GetFullPath(Path.Combine(root, plugin.RelativeAssembly));
            if (!assemblyPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(assemblyPath))
            {
                throw new InvalidDataException(
                    $"Bundled plugin assembly is missing or outside its package: {plugin.Id}.");
            }

            using var assemblyStream = File.OpenRead(assemblyPath);
            var actualSha = Convert.ToHexString(SHA256.HashData(assemblyStream)).ToLowerInvariant();
            var actualVersion = FileVersionInfo.GetVersionInfo(assemblyPath).FileVersion;
            if (!string.Equals(actualSha, plugin.Sha256, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(actualVersion, plugin.Version, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Bundled plugin version or hash does not match its lock entry: {plugin.Id}.");
            }

            plugin.AssemblyPath = assemblyPath;
        }

        return manifest.Plugins;
    }
}

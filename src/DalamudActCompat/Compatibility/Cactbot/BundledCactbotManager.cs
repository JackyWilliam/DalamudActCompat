using System.Security.Cryptography;
using System.Text.Json;

namespace DalamudActCompat.Compatibility.Cactbot;

public sealed class BundledCactbotManager
{
    public const string DirectoryName = "BundledCactbot";
    public const string LockFileName = "bundled-cactbot.lock.json";

    private readonly CactbotPackageInstaller installer;
    private readonly BundledCactbotDescriptor package;

    public BundledCactbotManager(
        string pluginAssemblyDirectory,
        CactbotPackageInstaller installer,
        bool directoryIsBundleRoot = false)
    {
        this.installer = installer;
        package = LoadAndValidate(directoryIsBundleRoot
            ? pluginAssemblyDirectory
            : Path.Combine(pluginAssemblyDirectory, DirectoryName));
    }

    public string BundledVersion => package.Version;

    public async Task<bool> EnsureCurrentAsync(
        CancellationToken cancellationToken,
        Action? installationStarting = null)
    {
        if (installer.IsInstalled &&
            installer.InstalledVersion is { } installedVersion &&
            installedVersion >= ParseVersion(package.Version))
        {
            return false;
        }

        installationStarting?.Invoke();
        await installer
            .InstallAsync(package.ArchivePath, cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    private static BundledCactbotDescriptor LoadAndValidate(string bundleDirectory)
    {
        var root = Path.GetFullPath(bundleDirectory);
        var lockPath = Path.Combine(root, LockFileName);
        using var stream = File.OpenRead(lockPath);
        var descriptor = JsonSerializer.Deserialize<BundledCactbotDescriptor>(
                             stream,
                             new JsonSerializerOptions
                             {
                                 PropertyNameCaseInsensitive = true,
                             })
                         ?? throw new InvalidDataException(
                             "Bundled Cactbot lock file is empty.");
        if (descriptor.SchemaVersion != 1 ||
            string.IsNullOrWhiteSpace(descriptor.Version) ||
            string.IsNullOrWhiteSpace(descriptor.ProjectUrl) ||
            string.IsNullOrWhiteSpace(descriptor.DownloadUrl) ||
            string.IsNullOrWhiteSpace(descriptor.RelativeArchive) ||
            string.IsNullOrWhiteSpace(descriptor.Sha256) ||
            descriptor.Sha256.Length != 64)
        {
            throw new InvalidDataException(
                "Bundled Cactbot lock file contains invalid metadata.");
        }

        _ = ParseVersion(descriptor.Version);
        var archivePath = Path.GetFullPath(
            Path.Combine(root, descriptor.RelativeArchive));
        var rootPrefix = root + Path.DirectorySeparatorChar;
        if (!archivePath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(archivePath))
        {
            throw new InvalidDataException(
                "Bundled Cactbot archive is missing or outside its package.");
        }

        using var archiveStream = File.OpenRead(archivePath);
        var actualSha256 = Convert
            .ToHexString(SHA256.HashData(archiveStream))
            .ToLowerInvariant();
        if (!string.Equals(
                actualSha256,
                descriptor.Sha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Bundled Cactbot archive does not match its locked SHA-256.");
        }

        descriptor.ArchivePath = archivePath;
        return descriptor;
    }

    private static Version ParseVersion(string version)
        => Version.TryParse(version, out var parsed)
            ? parsed
            : throw new InvalidDataException(
                $"Bundled Cactbot version is invalid: {version}.");

    private sealed class BundledCactbotDescriptor
    {
        public int SchemaVersion { get; init; }

        public string Version { get; init; } = string.Empty;

        public string ProjectUrl { get; init; } = string.Empty;

        public string DownloadUrl { get; init; } = string.Empty;

        public string RelativeArchive { get; init; } = string.Empty;

        public string Sha256 { get; init; } = string.Empty;

        public string ArchivePath { get; set; } = string.Empty;
    }
}

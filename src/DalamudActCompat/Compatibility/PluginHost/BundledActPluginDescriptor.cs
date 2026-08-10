using System.Text.Json.Serialization;

namespace DalamudActCompat.Compatibility.PluginHost;

public sealed class BundledActPluginLock
{
    public int SchemaVersion { get; set; }

    public List<BundledActPluginDescriptor> Plugins { get; set; } = [];
}

public sealed class BundledActPluginDescriptor
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public string Author { get; set; } = string.Empty;

    public string Maintainer { get; set; } = string.Empty;

    public string Copyright { get; set; } = string.Empty;

    public string ProjectUrl { get; set; } = string.Empty;

    public string DownloadUrl { get; set; } = string.Empty;

    public string SourceUrl { get; set; } = string.Empty;

    public string License { get; set; } = string.Empty;

    public string LicenseFile { get; set; } = string.Empty;

    public string RelativeAssembly { get; set; } = string.Empty;

    public string Sha256 { get; set; } = string.Empty;

    public string RelativePackage { get; set; } = string.Empty;

    public string PackageSha256 { get; set; } = string.Empty;

    public bool DisableOnlineUpdates { get; set; }

    public bool EnableAfterInstall { get; set; } = true;

    [JsonIgnore]
    public string AssemblyPath { get; internal set; } = string.Empty;

    [JsonIgnore]
    public string PackagePath { get; internal set; } = string.Empty;

    [JsonIgnore]
    public bool IsOnlineUpdate { get; internal set; }
}

public sealed class BundledActPluginUpdateRecord
{
    public string HostVersionWhenAccepted { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public string DownloadUrl { get; set; } = string.Empty;

    public string SourceUrl { get; set; } = string.Empty;

    public string Sha256 { get; set; } = string.Empty;
}

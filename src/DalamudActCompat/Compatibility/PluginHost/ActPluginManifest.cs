namespace DalamudActCompat.Compatibility.PluginHost;

public sealed class ActPluginManifest
{
    public const string FileName = "actcompat.plugin.json";

    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public string SourceSha256 { get; set; } = string.Empty;

    public string EntryAssembly { get; set; } = string.Empty;

    public string EntryType { get; set; } = string.Empty;

    public string[] RequestedCapabilities { get; set; } = [];

    public int HostApiVersion { get; set; } = 1;
}

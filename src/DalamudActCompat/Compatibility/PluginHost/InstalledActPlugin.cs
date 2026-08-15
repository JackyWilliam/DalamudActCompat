namespace DalamudActCompat.Compatibility.PluginHost;

public sealed record InstalledActPlugin(
    ActPluginManifest Manifest,
    string InstallDirectory,
    bool Enabled,
    string? DetectedVersion = null)
{
    public string DisplayVersion => string.IsNullOrWhiteSpace(DetectedVersion)
        ? Manifest.Version
        : DetectedVersion;

    public bool HasVersionMismatch =>
        !string.IsNullOrWhiteSpace(DetectedVersion) &&
        !string.Equals(
            DetectedVersion,
            Manifest.Version,
            StringComparison.OrdinalIgnoreCase);
}

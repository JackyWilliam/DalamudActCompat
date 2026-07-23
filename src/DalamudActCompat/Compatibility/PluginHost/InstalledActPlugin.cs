namespace DalamudActCompat.Compatibility.PluginHost;

public sealed record InstalledActPlugin(
    ActPluginManifest Manifest,
    string InstallDirectory,
    bool Enabled);

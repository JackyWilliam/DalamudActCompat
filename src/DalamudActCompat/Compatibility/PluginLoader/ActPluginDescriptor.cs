using DalamudActCompat.Compatibility.ActApi;

namespace DalamudActCompat.Compatibility.PluginLoader;

public sealed record ActPluginDescriptor(
    string Name,
    string AssemblyPath,
    ActCompatibilityLevel CompatibilityLevel,
    string Notes);

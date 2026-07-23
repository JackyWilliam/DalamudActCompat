namespace DalamudActCompat.ActRuntime;

public sealed record RuntimePluginSpec(
    string Id,
    string InstallDirectory,
    string EntryAssembly,
    string EntryType);

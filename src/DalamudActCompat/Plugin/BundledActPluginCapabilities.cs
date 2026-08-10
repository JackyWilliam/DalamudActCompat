using DalamudActCompat.ActRuntime;

namespace DalamudActCompat.Plugin;

internal static class BundledActPluginCapabilities
{
    public static IReadOnlyList<ActCapability> FoxTts { get; } =
        [ActCapability.TextToSpeech];

    public static IReadOnlyList<ActCapability> PostNamazu { get; } =
    [
        ActCapability.Clipboard,
        ActCapability.NetworkRequest,
        ActCapability.WriteFiles,
        ActCapability.GameCommand,
        ActCapability.NativeGameMemory,
    ];

    public static IReadOnlyList<ActCapability> Triggernometry { get; } =
    [
        ActCapability.TextToSpeech,
        ActCapability.Clipboard,
        ActCapability.NetworkRequest,
        ActCapability.LaunchExternalProcess,
        ActCapability.WriteFiles,
        ActCapability.HighRiskScript,
    ];

    public static IReadOnlyList<ActCapability> SilverDasher { get; } =
    [
        ActCapability.ReadCombatLogs,
        ActCapability.ReadLocalConfiguration,
        ActCapability.TextToSpeech,
        ActCapability.NetworkRequest,
        ActCapability.WriteFiles,
        ActCapability.NativeGameMemory,
    ];

    public static IReadOnlyList<(string PluginId, IReadOnlyList<ActCapability> Capabilities)> All { get; } =
    [
        ("act.foxtts", FoxTts),
        ("postnamazu", PostNamazu),
        ("triggernometry", Triggernometry),
    ];

    public static IReadOnlyList<(string PluginId, IReadOnlyList<ActCapability> Capabilities)> FullPermissionConfirmation { get; } =
    [
        .. All,
        ("silverdasher", SilverDasher),
    ];
}

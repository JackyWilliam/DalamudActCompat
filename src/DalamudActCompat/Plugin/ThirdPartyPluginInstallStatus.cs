using DalamudActCompat.ActRuntime;

namespace DalamudActCompat.Plugin;

public enum ThirdPartyPluginInstallState
{
    Idle,
    Preflighting,
    AwaitingPermission,
    StartingHost,
    Ready,
    Denied,
    Removing,
    Removed,
    Failed,
}

public sealed record ThirdPartyPluginInstallStatus(
    ThirdPartyPluginInstallState State,
    string DisplayName = "",
    string PluginId = "",
    string Version = "",
    IReadOnlyList<ActCapability>? RequestedCapabilities = null,
    string Detail = "",
    string Diagnostic = "")
{
    public IReadOnlyList<ActCapability> Capabilities => RequestedCapabilities ?? [];
}

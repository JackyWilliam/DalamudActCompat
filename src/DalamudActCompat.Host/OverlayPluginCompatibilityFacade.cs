using Advanced_Combat_Tracker;

namespace DalamudActCompat.Host;

internal sealed class OverlayPluginCompatibilityFacade : IActPluginV1
{
    public OverlayPluginCompatibilityContainer Container { get; } = new();

    public void InitPlugin(TabPage pluginScreenSpace, Label pluginStatusText)
    {
        pluginScreenSpace.Text = "OverlayPlugin";
        pluginStatusText.Text =
            "Game-side OverlayPlugin event dispatcher bridged through bounded IPC.";
    }

    public void DeInitPlugin()
    {
    }
}

internal sealed class OverlayPluginCompatibilityContainer
{
    public T Resolve<T>()
        => throw new NotSupportedException(
            $"OverlayPlugin service {typeof(T).FullName} is game-side; use the compatibility broker.");
}

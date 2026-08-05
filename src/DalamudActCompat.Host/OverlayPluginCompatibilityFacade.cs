using Advanced_Combat_Tracker;

// Triggernometry's widely distributed self-check compares pluginObj.ToString()
// with the literal "OverlayPlugin". Keep this compatibility type in the global
// namespace so the check sees the same identity as the legacy ACT plugin.
internal sealed class OverlayPlugin : IActPluginV1
{
    public CompatibilityContainer Container { get; } = new();

    public void InitPlugin(TabPage pluginScreenSpace, Label pluginStatusText)
    {
        pluginScreenSpace.Text = "OverlayPlugin";
        pluginStatusText.Text =
            "Game-side OverlayPlugin event dispatcher bridged through bounded IPC.";
    }

    public void DeInitPlugin()
    {
    }

    internal sealed class CompatibilityContainer
    {
        public T Resolve<T>()
            => throw new NotSupportedException(
                $"OverlayPlugin service {typeof(T).FullName} is game-side; use the compatibility broker.");
    }
}

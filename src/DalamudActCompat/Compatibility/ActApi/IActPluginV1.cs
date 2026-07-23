namespace DalamudActCompat.Compatibility.ActApi;

/// <summary>
/// ACT plugin entrypoint shape. This is a contract marker for the out-of-process
/// compatibility host; it is not a claim that arbitrary ACT plugins are safe.
/// </summary>
public interface IActPluginV1
{
    void InitPlugin(object pluginScreenSpace, object pluginStatusText);

    void DeInitPlugin();
}

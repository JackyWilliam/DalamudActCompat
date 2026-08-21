using Newtonsoft.Json.Linq;
using RainbowMage.OverlayPlugin;

namespace DalamudActCompat.ActRuntime;

internal sealed class PostNamazuEventSource : EventSourceBase
{
    public const string EventSourceName = "\u9CB6\u9C7C\u7CBE\u90AE\u5DEE";

    private Action<string, string>? action;

    public PostNamazuEventSource(TinyIoCContainer container)
        : base(container)
    {
        Name = EventSourceName;
        RegisterEventHandler("PostNamazu", HandleAction);
    }

    public void SetAction(Action<string, string>? nextAction)
        => Volatile.Write(ref action, nextAction);

    public override void LoadConfig(IPluginConfig config)
    {
    }

    public override void SaveConfig(IPluginConfig config)
    {
    }

    protected override void Update()
    {
    }

    private JToken HandleAction(JObject message)
    {
        var currentAction = Volatile.Read(ref action)
            ?? throw new InvalidOperationException("No active PostNamazu plugin instance is available.");
        currentAction(
            message["c"]?.Value<string>() ?? "null",
            message["p"]?.Value<string>() ?? string.Empty);
        // OverlayPlugin handlers accept only objects or a C# null response. Return an object so
        // both in-process callers and WebSocket callers receive the same successful contract.
        return new JObject();
    }
}

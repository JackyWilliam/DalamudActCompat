using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace DalamudActCompat.Plugin;

internal sealed class PluginServices
{
    public PluginServices(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IPluginLog log,
        IClientState clientState,
        IDataManager dataManager,
        IChatGui chatGui,
        IFramework framework,
        ICondition condition,
        IGameInteropProvider gameInteropProvider,
        INotificationManager notificationManager)
    {
        PluginInterface = pluginInterface;
        CommandManager = commandManager;
        Log = log;
        ClientState = clientState;
        DataManager = dataManager;
        ChatGui = chatGui;
        Framework = framework;
        Condition = condition;
        GameInteropProvider = gameInteropProvider;
        NotificationManager = notificationManager;
    }

    public IDalamudPluginInterface PluginInterface { get; }

    public ICommandManager CommandManager { get; }

    public IPluginLog Log { get; }

    public IClientState ClientState { get; }

    public IDataManager DataManager { get; }

    public IChatGui ChatGui { get; }

    public IFramework Framework { get; }

    public ICondition Condition { get; }

    public IGameInteropProvider GameInteropProvider { get; }

    public INotificationManager NotificationManager { get; }
}

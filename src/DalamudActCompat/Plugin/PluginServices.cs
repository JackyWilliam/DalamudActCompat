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
        IDataManager dataManager)
    {
        PluginInterface = pluginInterface;
        CommandManager = commandManager;
        Log = log;
        ClientState = clientState;
        DataManager = dataManager;
    }

    public IDalamudPluginInterface PluginInterface { get; }

    public ICommandManager CommandManager { get; }

    public IPluginLog Log { get; }

    public IClientState ClientState { get; }

    public IDataManager DataManager { get; }
}

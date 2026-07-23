using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using DalamudActCompat.Core.Interfaces;
using DalamudActCompat.Core.Models;
using DalamudActCompat.Core.State;
using DalamudActCompat.Encounters;
using DalamudActCompat.Infrastructure.Ipc;
using DalamudActCompat.Infrastructure.Logging;
using DalamudActCompat.Infrastructure.Storage;
using DalamudActCompat.Meter;
using DalamudActCompat.Overlay;
using DalamudActCompat.Parser;
using DalamudActCompat.UI;

namespace DalamudActCompat.Plugin;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/actcompat";

    private readonly PluginServices services;
    private readonly WindowSystem windowSystem = new("DalamudActCompat");
    private readonly PluginConfiguration configuration;
    private readonly PluginPaths paths;
    private readonly PluginLogger logger;
    private readonly EncounterStateStore stateStore;
    private readonly IParserEngine parserEngine;
    private readonly EncounterService encounterService;
    private readonly PluginLifecycle lifecycle;
    private readonly MeterWindow meterWindow;
    private readonly EncounterWindow encounterWindow;
    private readonly SettingsWindow settingsWindow;
    private readonly StatusWindow statusWindow;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IPluginLog log,
        IClientState clientState,
        IDataManager dataManager)
    {
        services = new PluginServices(pluginInterface, commandManager, log, clientState, dataManager);
        configuration = pluginInterface.GetPluginConfig() as PluginConfiguration ?? new PluginConfiguration();
        logger = new PluginLogger(log);
        paths = new PluginPaths(pluginInterface);
        if (string.IsNullOrWhiteSpace(configuration.LogDirectory))
        {
            configuration.LogDirectory = paths.CombatLogDirectory;
        }

        stateStore = new EncounterStateStore();
        var jsonStore = new JsonFileStore();
        var repository = new EncounterRepository(jsonStore, paths);
        encounterService = new EncounterService(repository, stateStore, configuration, logger);
        var ipcClient = new HostIpcClient(stateStore, logger);
        parserEngine = new ParserEngine(new IinactAdapter(ipcClient, logger));
        var meterService = new MeterService(stateStore, configuration.Meter);

        _ = new OverlayManager(new OverlayEventBus());

        meterWindow = new MeterWindow(meterService, stateStore, configuration);
        encounterWindow = new EncounterWindow(stateStore);
        settingsWindow = new SettingsWindow(configuration, parserEngine, paths, logger, SaveConfiguration);
        statusWindow = new StatusWindow(parserEngine);
        windowSystem.AddWindow(meterWindow);
        windowSystem.AddWindow(encounterWindow);
        windowSystem.AddWindow(settingsWindow);
        windowSystem.AddWindow(statusWindow);

        pluginInterface.UiBuilder.Draw += Draw;
        pluginInterface.UiBuilder.OpenConfigUi += OpenConfigUi;
        pluginInterface.UiBuilder.OpenMainUi += OpenMainUi;
        commandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Dalamud ACT compatibility platform windows. Args: meter, history, status, settings.",
        });

        lifecycle = new PluginLifecycle(parserEngine, encounterService, paths, configuration, logger);
        lifecycle.Start();
    }

    public string Name => "Dalamud ACT Compat";

    public void Dispose()
    {
        services.CommandManager.RemoveHandler(CommandName);
        services.PluginInterface.UiBuilder.Draw -= Draw;
        services.PluginInterface.UiBuilder.OpenConfigUi -= OpenConfigUi;
        services.PluginInterface.UiBuilder.OpenMainUi -= OpenMainUi;
        settingsWindow.Detach();
        statusWindow.Detach();
        windowSystem.RemoveAllWindows();

        SaveConfiguration();
        lifecycle.DisposeAsync().AsTask().GetAwaiter().GetResult();
        encounterService.DisposeAsync().AsTask().GetAwaiter().GetResult();
        parserEngine.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private void Draw() => windowSystem.Draw();

    private void OpenConfigUi() => settingsWindow.IsOpen = true;

    private void OpenMainUi()
        => meterWindow.IsOpen = true;

    private void OnCommand(string command, string arguments)
    {
        switch (arguments.Trim().ToLowerInvariant())
        {
            case "history":
                encounterWindow.IsOpen = true;
                break;
            case "status":
                statusWindow.IsOpen = true;
                break;
            case "settings":
                settingsWindow.IsOpen = true;
                break;
            case "sample":
                LoadSampleEncounter();
                meterWindow.IsOpen = true;
                break;
            case "clear":
                stateStore.ResetCurrent();
                break;
            case "meter":
            case "":
                meterWindow.IsOpen = true;
                break;
            default:
                settingsWindow.IsOpen = true;
                break;
        }
    }

    private void LoadSampleEncounter()
    {
        var snapshot = stateStore.GetSnapshot();
        stateStore.Replace(SampleEncounterFactory.Create(DateTimeOffset.UtcNow), snapshot.Recent);
        logger.Information("Loaded sample encounter snapshot.");
    }

    private void SaveConfiguration()
    {
        try
        {
            services.PluginInterface.SavePluginConfig(configuration);
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Failed to save plugin configuration.");
        }
    }
}

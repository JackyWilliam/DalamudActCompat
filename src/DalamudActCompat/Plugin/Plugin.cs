using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using DalamudActCompat.Core.Interfaces;
using DalamudActCompat.ActRuntime;
using DalamudActCompat.Compatibility.PluginHost;
using DalamudActCompat.Compatibility.Cactbot;
using DalamudActCompat.Core.Models;
using DalamudActCompat.Core.State;
using DalamudActCompat.Encounters;
using DalamudActCompat.Infrastructure.Ipc;
using DalamudActCompat.Infrastructure.Logging;
using DalamudActCompat.Infrastructure.Processes;
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
    private readonly SelfHostedActRuntime actRuntime;
    private readonly EncounterService encounterService;
    private readonly PluginLifecycle lifecycle;
    private readonly MeterWindow meterWindow;
    private readonly EncounterWindow encounterWindow;
    private readonly LogHistoryWindow logHistoryWindow;
    private readonly SettingsWindow settingsWindow;
    private readonly StatusWindow statusWindow;
    private readonly FactoryResetService factoryResetService;
    private readonly ActPluginPackageInstaller packageInstaller;
    private readonly CactbotPackageInstaller cactbotInstaller;
    private readonly FileDialogManager fileDialogManager = new();

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IPluginLog log,
        IClientState clientState,
        IPlayerState playerState,
        IDataManager dataManager,
        IChatGui chatGui,
        IFramework framework,
        ICondition condition,
        IGameInteropProvider gameInteropProvider,
        INotificationManager notificationManager,
        IPartyList partyList)
    {
        services = new PluginServices(
            pluginInterface,
            commandManager,
            log,
            clientState,
            dataManager,
            chatGui,
            framework,
            condition,
            gameInteropProvider,
            notificationManager);
        var localDeathWhilePartyContinues = () =>
            condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Unconscious] &&
            condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BoundByDuty] &&
            partyList.Count > 1 &&
            partyList.Any(member => member.CurrentHP > 0);
        configuration = pluginInterface.GetPluginConfig() as PluginConfiguration ?? new PluginConfiguration();
        logger = new PluginLogger(log);
        paths = new PluginPaths(pluginInterface, configuration.ActPluginDirectory);
        if (string.IsNullOrWhiteSpace(configuration.LogDirectory))
        {
            configuration.LogDirectory = paths.CombatLogDirectory;
        }

        stateStore = new EncounterStateStore();
        var jsonStore = new JsonFileStore();
        var repository = new EncounterRepository(jsonStore, paths);
        encounterService = new EncounterService(repository, stateStore, configuration, logger, paths);
        actRuntime = new SelfHostedActRuntime(
            pluginInterface,
            log,
            dataManager,
            () => playerState.CharacterName,
            () => playerState.ClassJob.RowId,
            chatGui,
            framework,
            condition,
            gameInteropProvider,
            notificationManager,
            localDeathWhilePartyContinues);
        parserEngine = new ParserEngine(new IinactAdapter(
            actRuntime,
            logger,
            stateStore,
            encounterService,
            paths.CombatLogDirectory,
            () => configuration.EmbeddedPlugins.FfxivActPluginEnabled,
            () => configuration.EmbeddedPlugins.OverlayPluginEnabled,
            DiscoverRuntimePlugins));
        var meterService = new MeterService(stateStore, configuration.Meter);

        _ = new OverlayManager(new OverlayEventBus());

        var text = new UiText(configuration);
        meterWindow = new MeterWindow(meterService, stateStore, configuration, text);
        meterWindow.IsOpen = configuration.Meter.IsVisible;
        logHistoryWindow = new LogHistoryWindow(paths, text);
        encounterWindow = new EncounterWindow(stateStore, text, () => logHistoryWindow.IsOpen = true);
        factoryResetService = new FactoryResetService(
            parserEngine,
            paths,
            configuration,
            logger,
            SaveConfiguration);
        packageInstaller = new ActPluginPackageInstaller(paths);
        cactbotInstaller = new CactbotPackageInstaller(paths);
        settingsWindow = new SettingsWindow(
            configuration,
            parserEngine,
            paths,
            logger,
            SaveConfiguration,
            FactoryResetAsync,
            () => packageInstaller.Discover(configuration.DisabledActPluginIds),
            SelectPluginPackage,
            OpenPluginDirectory,
            text,
            () => cactbotInstaller.IsInstalled,
            SelectCactbotPackage,
            OpenActPluginConfiguration);
        statusWindow = new StatusWindow(parserEngine, text);
        windowSystem.AddWindow(meterWindow);
        windowSystem.AddWindow(encounterWindow);
        windowSystem.AddWindow(logHistoryWindow);
        windowSystem.AddWindow(settingsWindow);
        windowSystem.AddWindow(statusWindow);

        pluginInterface.UiBuilder.Draw += Draw;
        pluginInterface.UiBuilder.OpenConfigUi += OpenConfigUi;
        pluginInterface.UiBuilder.OpenMainUi += OpenMainUi;
        commandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open ACT Compat UI. Args: meter, history, status, settings, sample, clear, host, stop, install <dll-or-zip>, factory-reset.",
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

    private void Draw()
    {
        windowSystem.Draw();
        fileDialogManager.Draw();
    }

    private void OpenConfigUi() => settingsWindow.IsOpen = true;

    private void OpenMainUi()
        => meterWindow.IsOpen = true;

    private void OnCommand(string command, string arguments)
    {
        var trimmedArguments = arguments.Trim();
        var separator = trimmedArguments.IndexOf(' ');
        var verb = (separator < 0 ? trimmedArguments : trimmedArguments[..separator]).ToLowerInvariant();
        var remainder = separator < 0 ? string.Empty : trimmedArguments[(separator + 1)..].Trim();
        switch (verb)
        {
            case "history":
                encounterWindow.IsOpen = true;
                break;
            case "logs":
                logHistoryWindow.IsOpen = true;
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
            case "host":
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                        await parserEngine.RestartAsync(timeout.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        logger.Error(ex, "Host bridge command failed.");
                    }
                });
                statusWindow.IsOpen = true;
                break;
            case "stop":
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        await parserEngine.StopAsync(timeout.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        logger.Error(ex, "Host bridge stop command failed.");
                    }
                });
                break;
            case "clear":
                stateStore.ResetCurrent();
                break;
            case "factory-reset":
                settingsWindow.IsOpen = true;
                break;
            case "install":
                InstallActPlugin(remainder);
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

    private async Task<string> FactoryResetAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        return await factoryResetService.ResetAsync(timeout.Token).ConfigureAwait(false);
    }

    private void InstallActPlugin(string packagePath)
    {
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            logger.Warning("Usage: /actcompat install <path-to-plugin.dll-or-zip>");
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var installed = await packageInstaller.InstallAsync(
                    packagePath.Trim('"'),
                    timeout.Token).ConfigureAwait(false);
                configuration.DisabledActPluginIds.Remove(installed.Manifest.Id);
                SaveConfiguration();
                logger.Information(
                    $"Installed ACT plugin {installed.Manifest.Name} {installed.Manifest.Version}. Restart the ACT host to load it.");
            }
            catch (Exception ex)
            {
                logger.Error(ex, "ACT plugin package installation failed.");
            }
        });
    }

    private void SelectPluginPackage()
    {
        if (!Directory.Exists(paths.ActPluginDirectory))
        {
            ChoosePluginDirectory(SelectPluginPackage);
            return;
        }

        fileDialogManager.OpenFileDialog(
            "Select ACT plugin package",
            "ACT plugin{.dll,.zip}",
            (success, selectedPath) =>
            {
                if (success && !string.IsNullOrWhiteSpace(selectedPath))
                {
                    InstallActPlugin(selectedPath);
                }
            });
    }

    private void SelectCactbotPackage()
    {
        fileDialogManager.OpenFileDialog(
            "选择 OverlayPlugin/cactbot 官方 Release ZIP",
            "Cactbot Release{.zip}",
            async (success, selectedPath) =>
            {
                if (!success || string.IsNullOrWhiteSpace(selectedPath))
                {
                    return;
                }

                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                    await cactbotInstaller.InstallAsync(selectedPath, timeout.Token).ConfigureAwait(false);
                    services.NotificationManager.AddNotification(new()
                    {
                        Title = "ACT 兼容",
                        Content = "Cactbot 资源已安装。重启解析器以加载 OverlayPlugin 事件源。",
                    });
                }
                catch (Exception ex)
                {
                    logger.Error(ex, "Cactbot installation failed.");
                }
            });
    }

    private void OpenPluginDirectory()
    {
        if (!Directory.Exists(paths.ActPluginDirectory))
        {
            ChoosePluginDirectory(OpenPluginDirectory);
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(paths.ActPluginDirectory)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Failed to open the ACT plugin directory.");
        }
    }

    private void OpenActPluginConfiguration(string pluginId)
    {
        if (!actRuntime.OpenCustomPluginConfiguration(pluginId))
        {
            logger.Warning($"ACT plugin '{pluginId}' is not running. Restart the parser and try again.");
        }
    }

    private void ChoosePluginDirectory(Action continueWith)
    {
        fileDialogManager.OpenFolderDialog(
            "Choose where the ACT plugin folder should be created",
            (success, directory) =>
            {
                if (!success || string.IsNullOrWhiteSpace(directory))
                {
                    return;
                }

                paths.SetActPluginDirectory(directory);
                configuration.ActPluginDirectory = paths.ActPluginDirectory;
                SaveConfiguration();
                Directory.CreateDirectory(paths.ActPluginDirectory);
                continueWith();
            });
    }

    private IReadOnlyList<RuntimePluginSpec> DiscoverRuntimePlugins()
        => packageInstaller
            .Discover(configuration.DisabledActPluginIds)
            .Where(plugin => plugin.Enabled)
            .Select(plugin => new RuntimePluginSpec(
                plugin.Manifest.Id,
                plugin.InstallDirectory,
                plugin.Manifest.EntryAssembly,
                plugin.Manifest.EntryType))
            .ToArray();
}

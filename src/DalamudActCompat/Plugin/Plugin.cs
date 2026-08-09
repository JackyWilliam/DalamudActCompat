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
using DalamudActCompat.Fflogs;
using DalamudActCompat.Infrastructure.Diagnostics;
using DalamudActCompat.Infrastructure.Ipc;
using DalamudActCompat.Infrastructure.Logging;
using DalamudActCompat.Infrastructure.Processes;
using DalamudActCompat.Infrastructure.Storage;
using DalamudActCompat.Meter;
using DalamudActCompat.Overlay;
using DalamudActCompat.Parser;
using DalamudActCompat.Protocol;
using DalamudActCompat.UI;
using System.Threading.Channels;

namespace DalamudActCompat.Plugin;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/actcompat";

    private readonly PluginServices services;
    private readonly WindowSystem windowSystem = new("DalamudActCompat");
    private readonly PluginConfiguration configuration;
    private readonly PluginPaths paths;
    private readonly PluginLogger logger;
    private readonly UiText text;
    private readonly EncounterStateStore stateStore;
    private readonly IParserEngine parserEngine;
    private readonly SelfHostedActRuntime actRuntime;
    private readonly EncounterService encounterService;
    private readonly PluginLifecycle lifecycle;
    private readonly MeterWindow meterWindow;
    private readonly FflogsEstimateService fflogsEstimateService;
    private readonly ZoneNameLocalizer zoneNameLocalizer;
    private readonly EncounterWindow encounterWindow;
    private readonly ControlCenterWindow settingsWindow;
    private readonly SettingsWindow advancedSettingsWindow;
    private readonly StatusWindow statusWindow;
    private readonly LauncherWindow launcherWindow;
    private readonly ThirdPartyPluginNoticeWindow thirdPartyPluginNoticeWindow;
    private readonly FactoryResetService factoryResetService;
    private readonly FactoryResetOperationCoordinator factoryResetOperations;
    private readonly ActPluginPackageInstaller packageInstaller;
    private readonly BundledActPluginManager bundledPluginManager;
    private readonly BundledActPluginUpdateChecker bundledPluginUpdateChecker;
    private readonly CactbotPackageInstaller cactbotInstaller;
    private readonly FileDialogManager fileDialogManager = new();
    private readonly CancellationTokenSource bundledUpdateCancellation = new();
    private readonly SemaphoreSlim bundledUpdateCheckLock = new(1, 1);
    private readonly CancellationTokenSource cactbotOperationCancellation = new();
    private readonly object cactbotTaskLock = new();
    private readonly object cactbotStatusLock = new();
    private readonly SemaphoreSlim cactbotFileOperationGate = new(1, 1);
    private readonly HashSet<Task> cactbotTasks = [];
    private readonly ActHostSupervisor hostSupervisor;
    private readonly PictoActOverlayService pictoActOverlay;
    private readonly IObjectTable objectTable;
    private readonly IPartyList partyList;
    private readonly IPlayerState playerState;
    private readonly Channel<HostCommandInvocation> hostCommandQueue =
        Channel.CreateBounded<HostCommandInvocation>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    private readonly CancellationTokenSource hostCommandCancellation = new();
    private readonly Task hostCommandWorker;
    private DateTimeOffset nextHostEntitySnapshotAt;
    private DateTimeOffset nextHostEntitySnapshotFailureLogAt;
    private CactbotOperationStatus cactbotOperationStatus = new(CactbotOperationState.Idle);
    private Task? cactbotShutdownTask;
    private bool cactbotShutdownStarted;
    private int cactbotCancellationDisposed;

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
        ISigScanner sigScanner,
        IGameGui gameGui,
        INotificationManager notificationManager,
        IPartyList partyList,
        IObjectTable objectTable,
        ITextureProvider textureProvider)
    {
        this.objectTable = objectTable;
        this.partyList = partyList;
        this.playerState = playerState;
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
        pictoActOverlay = new PictoActOverlayService(gameGui);
        var localDeathWhilePartyContinues = () =>
            condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Unconscious] &&
            condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BoundByDuty] &&
            partyList.Count > 1 &&
            partyList.Any(member => member.CurrentHP > 0);
        configuration = pluginInterface.GetPluginConfig() as PluginConfiguration ?? new PluginConfiguration();
        var configurationMigrated = configuration.ApplyMigrations();
        configuration.Meter.SortMode = MeterSortModeOptions.Normalize(
            configuration.Meter.SortMode);
        logger = new PluginLogger(log);
        paths = new PluginPaths(pluginInterface, configuration.ActPluginDirectory);
        if (string.IsNullOrWhiteSpace(configuration.LogDirectory))
        {
            configuration.LogDirectory = paths.CombatLogDirectory;
        }
        if (configurationMigrated)
        {
            pluginInterface.SavePluginConfig(configuration);
        }

        packageInstaller = new ActPluginPackageInstaller(paths);
        bundledPluginManager = new BundledActPluginManager(
            pluginInterface.AssemblyLocation.Directory!.FullName,
            typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "unknown",
            packageInstaller,
            configuration);
        bundledPluginUpdateChecker = new BundledActPluginUpdateChecker(
            paths.BundledPluginUpdateCacheDirectory);
        stateStore = new EncounterStateStore();
        paths.EnsureCreated();
        var hostIpcClient = new HostIpcClient(
            stateStore,
            logger,
            BuildHostPermissionSnapshot);
        hostSupervisor = new ActHostSupervisor(
            paths.HostDirectory,
            paths.ActPluginDirectory,
            paths.ConfigDirectory,
            hostIpcClient,
            logger);
        hostSupervisor.CommandRequested += OnHostCommandRequested;
        hostCommandWorker = Task.Run(
            () => RunHostCommandBrokerAsync(hostCommandCancellation.Token),
            CancellationToken.None);
        var jsonStore = new JsonFileStore();
        var repository = new EncounterRepository(jsonStore, paths);
        encounterService = new EncounterService(repository, stateStore, configuration, logger, paths);
        zoneNameLocalizer = new ZoneNameLocalizer(dataManager, log);
        fflogsEstimateService = new FflogsEstimateService(
            () => configuration.Fflogs,
            paths.FflogsCacheFile,
            logger);
        fflogsEstimateService.NotifyTerritoryChanged(
            clientState.TerritoryType,
            zoneNameLocalizer.Localize(clientState.TerritoryType, string.Empty));
        actRuntime = new SelfHostedActRuntime(
            pluginInterface,
            log,
            dataManager,
            () => playerState.CharacterName,
            () => BuildPlayerIdentities(
                playerState,
                partyList,
                condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Unconscious]),
            chatGui,
            framework,
            condition,
            gameInteropProvider,
            sigScanner,
            notificationManager,
            name => objectTable
                .FirstOrDefault(gameObject =>
                    gameObject.EntityId != 0 &&
                    string.Equals(
                        gameObject.Name.TextValue,
                        name,
                        StringComparison.OrdinalIgnoreCase))
                ?.EntityId,
            localDeathWhilePartyContinues,
            configuration.GetOverlayWindowSettings,
            configuration.GetOverlayWindowSettingsSnapshot,
            () => configuration.DebugMode,
            configuration.IsActCapabilityAllowed);
        actRuntime.ConfigureExternalPluginBridges(
            text => hostSupervisor.RequestTts(text, "game-side-act"),
            (action, payload) => hostSupervisor.InvokePluginAction(
                "postnamazu",
                "overlay",
                new Dictionary<string, string>
                {
                    ["command"] = action,
                    ["payload"] = payload,
                }));
        actRuntime.RawLogLineReceived += OnRawLogLineForHost;
        actRuntime.ZoneChanged += OnZoneChangedForHost;
        actRuntime.EncounterChanged += OnEncounterChangedForHost;
        framework.Update += OnFrameworkUpdateForHost;
        parserEngine = new ParserEngine(new IinactAdapter(
            actRuntime,
            logger,
            stateStore,
            encounterService,
            paths.CombatLogDirectory,
            framework,
            () => clientState.TerritoryType,
            () => condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BoundByDuty],
            () => configuration.EmbeddedPlugins.FfxivActPluginEnabled,
            () => configuration.EmbeddedPlugins.OverlayPluginEnabled,
            DiscoverRuntimePlugins,
            fflogsEstimateService.CaptureAvailableEstimates));
        var meterService = new MeterService(stateStore, configuration.Meter);

        _ = new OverlayManager(new OverlayEventBus());

        text = new UiText(configuration);
        var assetDirectory = Path.Combine(
            pluginInterface.AssemblyLocation.Directory!.FullName,
            "Assets");
        var logoTexture = textureProvider.GetFromFile(
            Path.Combine(assetDirectory, "act-logo.jpg"));
        var launcherTexture = textureProvider.GetFromFile(
            Path.Combine(assetDirectory, "act-button.png"));
        var jobIcons = new JobIconTextureSet(
            textureProvider,
            Path.Combine(assetDirectory, "JobIcons"));
        var runningStatusIcon = textureProvider.GetFromFile(
            Path.Combine(assetDirectory, "StatusIcons", "CombatRunning.png"));
        var transitionStatusIcon = textureProvider.GetFromFile(
            Path.Combine(assetDirectory, "StatusIcons", "CombatTransition.png"));
        var endedStatusIcon = textureProvider.GetFromFile(
            Path.Combine(assetDirectory, "StatusIcons", "CombatEnded.png"));
        meterWindow = new MeterWindow(
            meterService,
            fflogsEstimateService,
            configuration,
            text,
            jobIcons,
            runningStatusIcon,
            transitionStatusIcon,
            endedStatusIcon,
            zoneNameLocalizer.Localize,
            SaveConfiguration);
        meterWindow.IsOpen = configuration.Meter.IsVisible;
        encounterWindow = new EncounterWindow(
            stateStore,
            paths,
            configuration,
            text,
            jobIcons,
            logoTexture,
            zoneNameLocalizer.Localize,
            SaveConfiguration);
        factoryResetService = new FactoryResetService(
            parserEngine,
            paths,
            configuration,
            logger,
            SaveConfigurationForFactoryReset);
        factoryResetOperations = new FactoryResetOperationCoordinator(FactoryResetCoreAsync);
        cactbotInstaller = new CactbotPackageInstaller(
            paths,
            logger,
            warning => TryRunCactbotCompletion(
                CancellationToken.None,
                () => logger.Warning(warning)));
        thirdPartyPluginNoticeWindow = new ThirdPartyPluginNoticeWindow(
            bundledPluginManager.GetDisclosures,
            bundledPluginManager.GetPendingDisclosures,
            InstallBundledPluginsAsync,
            ConfigureBundledPluginPermissions,
            ShouldOfferFoxTtsPro,
            CompleteBundledPluginSetup,
            logger,
            text,
            logoTexture);
        advancedSettingsWindow = new SettingsWindow(
            configuration,
            parserEngine,
            paths,
            logger,
            SaveConfiguration,
            () => ApplyActPermissionChanges(),
            StartFactoryReset,
            () => packageInstaller.Discover(configuration.DisabledActPluginIds),
            SelectPluginPackage,
            OpenPluginDirectory,
            text,
            () => cactbotInstaller.IsInstalled,
            GetCactbotOperationStatus,
            SelectCactbotPackage,
            OpenCactbotOverlay,
            OpenCactbotSettings,
            () => actRuntime.OverlayTemplates,
            OpenHtmlOverlay,
            CloseHtmlOverlay,
            name => _ = actRuntime.ApplyOverlayWindowSettings(name),
            OpenActPluginConfiguration,
            () => StartBundledPluginUpdateCheck(openWindow: true));
        statusWindow = new StatusWindow(
            parserEngine,
            text,
            logoTexture,
            () => hostSupervisor.Snapshot,
            RestartHostFromUi,
            StopHostFromUi);
        settingsWindow = new ControlCenterWindow(
            configuration,
            parserEngine,
            logger,
            text,
            fflogsEstimateService,
            () => stateStore.GetSnapshot().Current,
            logoTexture,
            SaveConfiguration,
            () => ApplyActPermissionChanges(),
            SetMeterVisible,
            () => SetMeterVisible(true),
            encounterWindow.OpenRecent,
            () => statusWindow.IsOpen,
            value => statusWindow.IsOpen = value,
            SelectPluginPackage,
            OpenPluginDirectory,
            thirdPartyPluginNoticeWindow.OpenManualDisclosure,
            () => StartBundledPluginUpdateCheck(openWindow: true),
            OpenLogDirectory,
            OpenCombatLogDirectory,
            BuildDiagnosticReport,
            () => packageInstaller.Discover(configuration.DisabledActPluginIds),
            OpenActPluginConfiguration,
            () => cactbotInstaller.IsInstalled,
            GetCactbotOperationStatus,
            SelectCactbotPackage,
            OpenCactbotOverlay,
            OpenCactbotSettings,
            () => actRuntime.OverlayTemplates,
            OpenHtmlOverlay,
            CloseHtmlOverlay,
            DeleteHtmlOverlay,
            StartFactoryReset,
            stateStore.ResetCurrent,
            name => _ = actRuntime.ApplyOverlayWindowSettings(name));
        launcherWindow = new LauncherWindow(
            configuration,
            launcherTexture,
            text,
            settingsWindow.ToggleAnimated,
            ToggleMeter,
            SaveConfiguration)
        {
            IsOpen = true,
        };
        windowSystem.AddWindow(meterWindow);
        windowSystem.AddWindow(encounterWindow);
        windowSystem.AddWindow(settingsWindow);
        windowSystem.AddWindow(advancedSettingsWindow);
        windowSystem.AddWindow(statusWindow);
        windowSystem.AddWindow(thirdPartyPluginNoticeWindow);
        windowSystem.AddWindow(launcherWindow);
        thirdPartyPluginNoticeWindow.OpenRequiredAfterPluginUpdateWhenPending();

        pluginInterface.UiBuilder.Draw += Draw;
        pluginInterface.UiBuilder.OpenConfigUi += OpenConfigUi;
        pluginInterface.UiBuilder.OpenMainUi += OpenMainUi;
        commandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open ACT Compat UI. Args: meter, cactbot, overlay [template], history, status, settings, sample, clear, host, stop, install <dll-or-zip>, factory-reset.",
        });

        lifecycle = new PluginLifecycle(parserEngine, encounterService, paths, configuration, logger);
        lifecycle.Start();
        StartBundledCactbotInitialization(pluginInterface.AssemblyLocation.Directory!.FullName);
        _ = Task.Run(StartIndependentHostAsync);
        if (configuration.AutoCheckBundledPluginUpdates)
        {
            StartBundledPluginUpdateCheck(openWindow: false);
        }
    }

    public string Name => "Dalamud ACT Compat";

    public void Dispose()
    {
        bundledUpdateCancellation.Cancel();
        BeginCactbotShutdown();
        fflogsEstimateService.BeginShutdown();
        var factoryResetCompleted = factoryResetOperations
            .WaitForShutdownAsync(TimeSpan.FromSeconds(5))
            .GetAwaiter()
            .GetResult();
        bundledPluginUpdateChecker.Dispose();
        services.CommandManager.RemoveHandler(CommandName);
        services.PluginInterface.UiBuilder.Draw -= Draw;
        services.PluginInterface.UiBuilder.OpenConfigUi -= OpenConfigUi;
        services.PluginInterface.UiBuilder.OpenMainUi -= OpenMainUi;
        settingsWindow.Detach();
        advancedSettingsWindow.Detach();
        statusWindow.Detach();
        windowSystem.RemoveAllWindows();
        actRuntime.RawLogLineReceived -= OnRawLogLineForHost;
        actRuntime.ZoneChanged -= OnZoneChangedForHost;
        actRuntime.EncounterChanged -= OnEncounterChangedForHost;
        services.Framework.Update -= OnFrameworkUpdateForHost;
        hostSupervisor.CommandRequested -= OnHostCommandRequested;

        if (factoryResetCompleted)
        {
            SaveConfiguration();
        }
        else
        {
            logger.Warning(
                "Factory reset did not stop within five seconds. Dalamud configuration saving and component disposal are deferred to avoid racing the active reset.");
        }
        var shutdown = Task.Run(() => DisposeComponentsAsync(factoryResetCompleted));
        var completed = ReferenceEquals(
            Task.WhenAny(shutdown, Task.Delay(TimeSpan.FromMilliseconds(250)))
                .GetAwaiter()
                .GetResult(),
            shutdown);
        if (!completed)
        {
            logger.Warning(
                "ACT compatibility shutdown exceeded 250 ms. Dalamud/FFXIV was released; " +
                "remaining in-process plugin cleanup will continue only on the background task.");
            _ = shutdown.ContinueWith(
                task => logger.Error(
                    task.Exception?.GetBaseException()
                    ?? new InvalidOperationException("Unknown shutdown failure."),
                    "ACT compatibility background shutdown failed."),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        else if (shutdown.IsFaulted)
        {
            logger.Error(
                shutdown.Exception?.GetBaseException() ?? new InvalidOperationException("Unknown shutdown failure."),
                "ACT compatibility background shutdown failed.");
        }
    }

    private void Draw()
    {
        pictoActOverlay.Draw();
        OverlayEditShield.Draw(actRuntime.HasVisibleEditingOverlay);
        windowSystem.Draw();
        fileDialogManager.Draw();
    }

    private void OpenConfigUi() => settingsWindow.ShowAnimated();

    private void OpenMainUi()
        => settingsWindow.ShowAnimated();

    private void ToggleMeter()
    {
        var shouldShow = !configuration.Meter.IsVisible || !meterWindow.IsOpen;
        SetMeterVisible(shouldShow);
    }

    private void SetMeterVisible(bool visible)
    {
        configuration.Meter.IsVisible = visible;
        meterWindow.IsOpen = visible;
        SaveConfiguration();
    }

    private void OnCommand(string command, string arguments)
    {
        var trimmedArguments = arguments.Trim();
        var separator = trimmedArguments.IndexOf(' ');
        var verb = (separator < 0 ? trimmedArguments : trimmedArguments[..separator]).ToLowerInvariant();
        var remainder = separator < 0 ? string.Empty : trimmedArguments[(separator + 1)..].Trim();
        switch (verb)
        {
            case "history":
                encounterWindow.OpenRecent();
                break;
            case "logs":
                encounterWindow.OpenLogFiles();
                break;
            case "status":
                statusWindow.IsOpen = true;
                break;
            case "settings":
                settingsWindow.ShowAnimated();
                break;
            case "cactbot":
                if (!actRuntime.ShowCactbotOverlay())
                {
                    logger.Warning(
                        "Cactbot overlay is not running. Install Cactbot, enable OverlayPlugin, and restart the parser.");
                }
                break;
            case "overlay":
                OpenHtmlOverlay(string.IsNullOrWhiteSpace(remainder)
                    ? configuration.SelectedOverlayTemplate
                    : remainder);
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
                        await hostSupervisor.StartAsync(timeout.Token).ConfigureAwait(false);
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
                        await hostSupervisor.StopAsync(timeout.Token).ConfigureAwait(false);
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
                settingsWindow.ShowAnimated();
                break;
            case "install":
                InstallActPlugin(remainder);
                break;
            case "meter":
            case "":
                meterWindow.IsOpen = true;
                break;
            default:
                settingsWindow.ShowAnimated();
                break;
        }
    }

    private void LoadSampleEncounter()
    {
        stateStore.UpdateCurrent(SampleEncounterFactory.Create(DateTimeOffset.UtcNow));
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

    private void StartBundledCactbotInitialization(string pluginAssemblyDirectory)
    {
        _ = StartCactbotOperation(
            cancellationToken => EnsureBundledCactbotAsync(pluginAssemblyDirectory, cancellationToken));
    }

    private async Task EnsureBundledCactbotAsync(
        string pluginAssemblyDirectory,
        CancellationToken cancellationToken)
    {
        TrySetCactbotOperationStatus(CactbotOperationState.Checking);
        var gateAcquired = false;
        try
        {
            await cactbotFileOperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateAcquired = true;
            var bundledCactbotManager = new BundledCactbotManager(
                pluginAssemblyDirectory,
                cactbotInstaller);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            var installed = await bundledCactbotManager
                .EnsureCurrentAsync(
                    timeout.Token,
                    () => TrySetCactbotOperationStatus(CactbotOperationState.Installing))
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!TrySetCactbotOperationStatus(CactbotOperationState.Ready))
            {
                return;
            }
            if (installed)
            {
                TryRunCactbotCompletion(
                    cancellationToken,
                    () => logger.Information(
                        $"Installed bundled Cactbot {bundledCactbotManager.BundledVersion} into this user's plugin configuration."));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!CactbotOperationLifecycle.CanPublishCompletion(
                    IsCactbotShutdownStarted(),
                    cancellationToken))
            {
                return;
            }
            TryRunCactbotCompletion(
                cancellationToken,
                () =>
                {
                    SetCactbotOperationStatus(CactbotOperationState.Error, ex.Message);
                    logger.Error(ex, "Bundled Cactbot installation failed.");
                });
        }
        finally
        {
            if (gateAcquired)
            {
                cactbotFileOperationGate.Release();
            }
        }
    }

    private void SaveConfigurationForFactoryReset()
    {
        if (factoryResetOperations.IsShutdownStarted)
        {
            throw new OperationCanceledException(
                "Plugin shutdown started before the factory-reset configuration save.",
                factoryResetOperations.ShutdownToken);
        }

        services.PluginInterface.SavePluginConfig(configuration);
    }

    private Task? StartCactbotOperation(Func<CancellationToken, Task> operation)
    {
        Task task;
        lock (cactbotTaskLock)
        {
            if (cactbotShutdownStarted)
            {
                return null;
            }

            task = Task.Run(
                () => operation(cactbotOperationCancellation.Token),
                CancellationToken.None);
            cactbotTasks.Add(task);
        }

        _ = task.ContinueWith(
            completedTask =>
            {
                var failure = completedTask.Exception?.GetBaseException();
                if (failure is not null)
                {
                    TryRunCactbotCompletion(
                        CancellationToken.None,
                        () => logger.Error(
                            failure,
                            "Cactbot background operation failed outside its worker error boundary."));
                }

                lock (cactbotTaskLock)
                {
                    cactbotTasks.Remove(completedTask);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return task;
    }

    private CactbotOperationStatus GetCactbotOperationStatus()
    {
        lock (cactbotStatusLock)
        {
            return cactbotOperationStatus;
        }
    }

    private void SetCactbotOperationStatus(CactbotOperationState state, string? errorMessage = null)
    {
        lock (cactbotStatusLock)
        {
            cactbotOperationStatus = new CactbotOperationStatus(state, errorMessage);
        }
    }

    private bool TrySetCactbotOperationStatus(
        CactbotOperationState state,
        string? errorMessage = null)
        => TryRunCactbotCompletion(
            CancellationToken.None,
            () => SetCactbotOperationStatus(state, errorMessage));

    private bool TryRunCactbotCompletion(
        CancellationToken cancellationToken,
        Action action)
    {
        lock (cactbotTaskLock)
        {
            if (!CactbotOperationLifecycle.CanPublishCompletion(
                    cactbotShutdownStarted,
                    cancellationToken))
            {
                return false;
            }

            action();
            return true;
        }
    }

    private Task PublishCactbotInstalledNotificationAsync(CancellationToken cancellationToken)
    {
        lock (cactbotTaskLock)
        {
            return CactbotOperationLifecycle.PublishIfActiveAsync(
                cactbotShutdownStarted,
                cancellationToken,
                () => services.Framework.RunOnFrameworkThread(() =>
                {
                    TryRunCactbotCompletion(
                        cancellationToken,
                        () => services.NotificationManager.AddNotification(new()
                        {
                            Title = "ACT 兼容",
                            Content = "Cactbot 资源已安装。重启解析器以加载 OverlayPlugin 事件源。",
                        }));
                }));
        }
    }

    private bool IsCactbotShutdownStarted()
    {
        lock (cactbotTaskLock)
        {
            return cactbotShutdownStarted;
        }
    }

    private void BeginCactbotShutdown()
    {
        var shouldCancel = false;
        lock (cactbotTaskLock)
        {
            if (!cactbotShutdownStarted)
            {
                cactbotShutdownStarted = true;
                shouldCancel = true;
            }
        }

        if (shouldCancel)
        {
            cactbotOperationCancellation.Cancel();
        }
    }

    private Task ShutdownCactbotOperationsAsync()
    {
        BeginCactbotShutdown();
        lock (cactbotTaskLock)
        {
            cactbotShutdownTask ??= ShutdownCactbotOperationsCoreAsync(cactbotTasks.ToArray());
            return cactbotShutdownTask;
        }
    }

    private async Task ShutdownCactbotOperationsCoreAsync(Task[] tasks)
    {
        var completion = Task.WhenAll(tasks);
        try
        {
            await completion.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            logger.Warning(
                "Cactbot background operations did not stop within five seconds; cancellation resources will be released after they exit.");
            _ = completion.ContinueWith(
                completedTask =>
                {
                    _ = completedTask.Exception;
                    DisposeCactbotCancellation();
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return;
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Cactbot background shutdown failed.");
        }

        DisposeCactbotCancellation();
    }

    private void DisposeCactbotCancellation()
    {
        if (Interlocked.Exchange(ref cactbotCancellationDisposed, 1) == 0)
        {
            cactbotOperationCancellation.Dispose();
        }
    }

    private Task<string> StartFactoryReset()
        => factoryResetOperations.Start();

    private async Task<string> FactoryResetCoreAsync(CancellationToken shutdownToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await cactbotFileOperationGate.WaitAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            return await factoryResetService
                .ResetAsync(timeout.Token, shutdownToken)
                .ConfigureAwait(false);
        }
        finally
        {
            cactbotFileOperationGate.Release();
        }
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

    private async Task InstallBundledPluginsAsync(
        IReadOnlyList<BundledActPluginDescriptor> plugins)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            bundledUpdateCancellation.Token);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        var hostWasRunning = hostSupervisor.Snapshot.State == HostSupervisorState.Running;
        var parserWasRunning = parserEngine.Status.State == ParserState.Running;
        await BundledPluginInstallCoordinator.ExecuteAsync(
                hostWasRunning,
                parserWasRunning,
                hostSupervisor.StopAsync,
                parserEngine.StopAsync,
                async cancellationToken =>
                {
                    await bundledPluginManager
                        .InstallAndAcknowledgeAsync(plugins, cancellationToken)
                        .ConfigureAwait(false);
                    SaveConfiguration();
                },
                hostSupervisor.StartAsync,
                parserEngine.StartAsync,
                () => !bundledUpdateCancellation.IsCancellationRequested,
                timeout.Token,
                TimeSpan.FromSeconds(20))
            .ConfigureAwait(false);

        services.NotificationManager.AddNotification(new()
        {
            Title = "ACT 兼容",
            Content = "第三方 DLL 已按告知版本安装/更新；作者、版本和来源告知已记录。",
        });
    }

    private void StartBundledPluginUpdateCheck(bool openWindow)
    {
        _ = Task.Run(async () =>
        {
            var cancellationToken = bundledUpdateCancellation.Token;
            if (openWindow)
            {
                await services.Framework
                    .RunOnFrameworkThread(
                        () =>
                        {
                            thirdPartyPluginNoticeWindow.BeginUpdateCheck(userInitiated: true);
                            services.NotificationManager.AddNotification(new()
                            {
                                Title = text.Get("扩展更新", "Extension updates"),
                                Content = text.Get(
                                    "正在检查三项 DLL 的作者上游版本。",
                                    "Checking the author sources for all three DLLs."),
                            });
                        })
                    .ConfigureAwait(false);
            }

            if (!await bundledUpdateCheckLock
                    .WaitAsync(0)
                    .ConfigureAwait(false))
            {
                if (openWindow)
                {
                    await services.Framework
                        .RunOnFrameworkThread(
                            () => services.NotificationManager.AddNotification(new()
                            {
                                Title = text.Get("扩展更新", "Extension updates"),
                                Content = text.Get(
                                    "更新检查已经在进行中。",
                                    "An update check is already in progress."),
                            }))
                        .ConfigureAwait(false);
                }
                return;
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!openWindow)
                {
                    await services.Framework
                        .RunOnFrameworkThread(
                            () => thirdPartyPluginNoticeWindow.BeginUpdateCheck(userInitiated: false))
                        .ConfigureAwait(false);
                }
                var check = await bundledPluginUpdateChecker
                    .CheckAsync(
                        bundledPluginManager.Plugins,
                        cancellationToken)
                    .ConfigureAwait(false);
                bundledPluginManager.ApplyOnlineUpdates(
                    check.Updates);
                var pendingDisclosures = bundledPluginManager
                    .GetPendingDisclosures()
                    .ToArray();
                var pendingOnline = pendingDisclosures
                    .Where(plugin => plugin.IsOnlineUpdate)
                    .ToArray();
                var message = BuildBundledPluginUpdateMessage(
                    check,
                    pendingOnline.Length);
                await services.Framework
                    .RunOnFrameworkThread(
                        () =>
                        {
                            thirdPartyPluginNoticeWindow.CompleteUpdateCheck(
                                message,
                                ThirdPartyPluginNoticeWindow.ShouldOpenUpdateResult(
                                    pendingDisclosures.Length,
                                    failed: false,
                                    userInitiated: openWindow),
                                userInitiated: openWindow);
                            if (openWindow)
                            {
                                services.NotificationManager.AddNotification(new()
                                {
                                    Title = text.Get("扩展更新", "Extension updates"),
                                    Content = message,
                                });
                            }
                        })
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Plugin shutdown cancels the online check.
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                // Disposing the HTTP client can surface a non-cancellation exception.
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Bundled ACT plugin online update check failed.");
                await services.Framework
                    .RunOnFrameworkThread(
                        () =>
                        {
                            var message = $"DLL 在线更新检查失败；仍可使用安装包内版本：{ex.GetBaseException().Message}";
                            var pendingCount = bundledPluginManager
                                .GetPendingDisclosures()
                                .Count;
                            thirdPartyPluginNoticeWindow.CompleteUpdateCheck(
                                message,
                                ThirdPartyPluginNoticeWindow.ShouldOpenUpdateResult(
                                    pendingCount,
                                    failed: true,
                                    userInitiated: openWindow),
                                userInitiated: openWindow);
                            if (openWindow)
                            {
                                services.NotificationManager.AddNotification(new()
                                {
                                    Title = text.Get("扩展更新失败", "Extension update check failed"),
                                    Content = message,
                                });
                            }
                        })
                    .ConfigureAwait(false);
            }
            finally
            {
                bundledUpdateCheckLock.Release();
            }
        });
    }

    private static string BuildBundledPluginUpdateMessage(
        BundledActPluginUpdateCheckResult check,
        int pendingOnlineCount)
    {
        var summary = pendingOnlineCount > 0
            ? $"发现 {pendingOnlineCount} 项作者上游 DLL 更新；确认前这些 DLL 不会重新加载。"
            : "DLL 作者上游检查完成，当前没有新的在线更新。";
        if (check.Failures.Count == 0)
        {
            return summary;
        }

        return $"{summary} {check.Failures.Count} 项来源检查失败：" +
               string.Join("；", check.Failures);
    }

    private void SelectPluginPackage()
    {
        if (!Directory.Exists(paths.ActPluginDirectory))
        {
            ChoosePluginDirectory(SelectPluginPackage);
            return;
        }

        fileDialogManager.OpenFileDialog(
            text.Get("选择 ACT 扩展 DLL 或 ZIP", "Select ACT plugin DLL or ZIP"),
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

                var operation = StartCactbotOperation(
                    cancellationToken => InstallSelectedCactbotAsync(selectedPath, cancellationToken));
                if (operation is not null)
                {
                    await operation.ConfigureAwait(false);
                }
            });
    }

    private async Task InstallSelectedCactbotAsync(
        string selectedPath,
        CancellationToken cancellationToken)
    {
        TrySetCactbotOperationStatus(CactbotOperationState.Installing);
        var gateAcquired = false;
        try
        {
            await cactbotFileOperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateAcquired = true;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            await cactbotInstaller.InstallAsync(selectedPath, timeout.Token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!TrySetCactbotOperationStatus(CactbotOperationState.Ready))
            {
                return;
            }

            await PublishCactbotInstalledNotificationAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!CactbotOperationLifecycle.CanPublishCompletion(
                    IsCactbotShutdownStarted(),
                    cancellationToken))
            {
                return;
            }
            TryRunCactbotCompletion(
                cancellationToken,
                () =>
                {
                    SetCactbotOperationStatus(CactbotOperationState.Error, ex.Message);
                    logger.Error(ex, "Cactbot installation failed.");
                });
        }
        finally
        {
            if (gateAcquired)
            {
                cactbotFileOperationGate.Release();
            }
        }
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

    private void OpenLogDirectory()
    {
        try
        {
            Directory.CreateDirectory(paths.LogDirectory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(paths.LogDirectory)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Failed to open the log directory.");
        }
    }

    private string OpenCombatLogDirectory()
    {
        Directory.CreateDirectory(paths.CombatLogDirectory);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(paths.CombatLogDirectory)
        {
            UseShellExecute = true,
        });
        return paths.CombatLogDirectory;
    }

    private string BuildDiagnosticReport()
    {
        IReadOnlyList<InstalledActPlugin> installedPlugins;
        string? discoveryError = null;
        try
        {
            installedPlugins = packageInstaller.Discover(configuration.DisabledActPluginIds);
        }
        catch (Exception ex)
        {
            installedPlugins = [];
            discoveryError = ex.GetType().Name;
        }

        return DiagnosticReportBuilder.Build(
            paths,
            new DiagnosticReportSnapshot(
                typeof(Plugin).Assembly.GetName().Version?.ToString(4) ?? "unknown",
                typeof(IDalamudPluginInterface).Assembly.GetName().Version?.ToString() ?? "unknown",
                parserEngine.Status,
                hostSupervisor.Snapshot,
                configuration.EnableParsing,
                configuration.DebugMode,
                configuration.Fflogs.Enabled,
                configuration.Meter.SortMode,
                configuration.Meter.CompactMode,
                installedPlugins,
                discoveryError));
    }

    private void ConfigureBundledPluginPermissions(bool enableFullFunctionality)
    {
        foreach (var (pluginId, capabilities) in BundledActPluginCapabilities.All)
        {
            foreach (var capability in capabilities)
            {
                configuration.SetActCapability(
                    pluginId,
                    capability,
                    ShouldEnableBundledCapability(enableFullFunctionality, capability));
            }
        }
        SaveConfiguration();
        logger.Information(enableFullFunctionality
            ? "All declared capabilities were enabled for bundled ACT plugins by user choice."
            : "Bundled ACT plugin permissions were reset to safe defaults by user choice.");
    }

    internal static bool ShouldEnableBundledCapability(
        bool enableFullFunctionality,
        ActCapability capability)
        => enableFullFunctionality ||
           PluginConfiguration.IsActCapabilityAllowedByDefault(capability);

    private bool ShouldOfferFoxTtsPro()
    {
        if (configuration.SuppressFoxTtsProPrompt)
        {
            return false;
        }

        try
        {
            return ShouldOfferFoxTtsPro(
                suppressPrompt: false,
                isPro: FoxTtsConfigurationDefaults.IsPro(paths.ConfigDirectory));
        }
        catch (Exception ex)
        {
            logger.Warning(
                $"Failed to inspect the FoxTTS engine selection; offering Cafe TTS Pro: {ex.GetBaseException().Message}");
            return true;
        }
    }

    internal static bool ShouldOfferFoxTtsPro(bool suppressPrompt, bool isPro)
        => !suppressPrompt && !isPro;

    private void CompleteBundledPluginSetup(FoxTtsProChoice choice)
    {
        if (choice == FoxTtsProChoice.NeverRemind)
        {
            configuration.SuppressFoxTtsProPrompt = true;
            SaveConfiguration();
            logger.Information("Future FoxTTS Cafe TTS Pro prompts were disabled by user choice.");
        }

        ApplyActPermissionChanges(choice == FoxTtsProChoice.EnablePro);
    }

    private void OpenActPluginConfiguration(string pluginId)
    {
        if (hostSupervisor.OpenPluginUi(pluginId))
        {
            return;
        }

        logger.Warning($"ACT plugin '{pluginId}' is not running in the independent Host.");
        services.NotificationManager.AddNotification(new()
        {
            Title = "ACT 兼容",
            Content = $"扩展 {pluginId} 未在独立 Host 中成功加载，请重启 Host 并查看状态日志。",
        });
    }

    private void OpenCactbotOverlay()
    {
        if (!actRuntime.ShowCactbotOverlay())
        {
            logger.Warning(
                "Cactbot overlay is not running. Install Cactbot, enable OverlayPlugin, and restart the parser.");
        }
    }

    private void OpenCactbotSettings()
    {
        if (!actRuntime.ShowCactbotSettings())
        {
            logger.Warning(
                "Cactbot settings are not available. Install Cactbot, enable OverlayPlugin, and restart the parser.");
        }
    }

    private void OpenHtmlOverlay(string name)
    {
        try
        {
            if (actRuntime.ShowHtmlOverlay(name))
            {
                configuration.GetOverlayWindowSettings(name).OpenOnStartup = true;
                if (actRuntime.OverlayTemplates.Any(template => string.Equals(
                        template.Name,
                        name,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    configuration.SelectedOverlayTemplate = name;
                }
                SaveConfiguration();
                return;
            }

            logger.Warning(
                $"HTML overlay '{name}' is unavailable. Enable OverlayPlugin, restart the parser, and select a listed template or saved custom URL.");
        }
        catch (Exception ex)
        {
            logger.Error(ex, $"HTML overlay '{name}' could not be opened.");
        }
    }

    private void CloseHtmlOverlay(string name)
    {
        if (!actRuntime.HideHtmlOverlay(name))
        {
            logger.Warning($"HTML overlay '{name}' is not available to close.");
            return;
        }

        configuration.GetOverlayWindowSettings(name).OpenOnStartup = false;
        SaveConfiguration();
    }

    private void DeleteHtmlOverlay(string name)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            string.Equals(
                name,
                SelfHostedActRuntime.CactbotOverlayName,
                StringComparison.OrdinalIgnoreCase))
        {
            logger.Warning("The Cactbot overlay is managed separately and cannot be deleted.");
            return;
        }

        _ = actRuntime.DeleteHtmlOverlay(name);
        configuration.OverlayWindows?.Remove(name);
        SaveConfiguration();
        logger.Information($"Deleted HTML overlay '{name}' and its saved window layout.");
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

    private async Task StartIndependentHostAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await hostSupervisor.StartAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.Error(
                ex,
                "Independent ACT Host did not start. In-process parsing and overlays remain available, " +
                "but traditional ACT plugins stay unloaded because hard crash isolation is not active.");
        }
    }

    private void RestartHostFromUi()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                await hostSupervisor.StopAsync(timeout.Token).ConfigureAwait(false);
                await hostSupervisor.StartAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "ACT Host UI restart failed.");
            }
        });
    }

    private void ApplyActPermissionChanges(bool switchFoxTtsToPro = false)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                if (switchFoxTtsToPro)
                {
                    await hostSupervisor.StopAsync(timeout.Token).ConfigureAwait(false);
                    try
                    {
                        FoxTtsConfigurationDefaults.SetPro(paths.ConfigDirectory);
                    }
                    finally
                    {
                        await hostSupervisor.StartAsync(timeout.Token).ConfigureAwait(false);
                    }

                    logger.Information(
                        "ACT Host stopped before FoxTTS was switched to Cafe TTS Pro, then started again.");
                    await services.Framework.RunOnFrameworkThread(() =>
                        services.NotificationManager.AddNotification(new()
                        {
                            Title = "ACT 兼容",
                            Content = "FoxTTS 已切换为 Cafe TTS Pro，兼容 Host 已重新启动。",
                        })).ConfigureAwait(false);
                    return;
                }

                if (await hostSupervisor.RestartAsync(timeout.Token).ConfigureAwait(false))
                {
                    logger.Information(
                        "ACT Host restarted once after the permission group was saved.");
                }
            }
            catch (Exception ex)
            {
                logger.Error(
                    ex,
                    switchFoxTtsToPro
                        ? "Failed to switch FoxTTS to Cafe TTS Pro and restart the ACT Host."
                        : "ACT Host permission refresh failed.");
                if (switchFoxTtsToPro)
                {
                    await services.Framework.RunOnFrameworkThread(() =>
                        services.NotificationManager.AddNotification(new()
                        {
                            Title = "ACT 兼容",
                            Content = $"FoxTTS 切换 Cafe TTS Pro 失败：{ex.GetBaseException().Message}",
                        })).ConfigureAwait(false);
                }
            }
        });
    }

    private void StopHostFromUi()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await hostSupervisor.StopAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "ACT Host UI stop failed.");
            }
        });
    }

    private HostPermissionSnapshot BuildHostPermissionSnapshot()
    {
        var capabilities = Enum.GetValues<ActCapability>();
        string[] pluginIds = ["triggernometry", "postnamazu"];
        var allowed = pluginIds.ToDictionary(
            pluginId => pluginId,
            pluginId => (IReadOnlyList<string>)capabilities
                .Where(capability =>
                    configuration.IsActCapabilityAllowed(pluginId, capability))
                .Select(capability => capability.ToString())
                .ToArray(),
            StringComparer.OrdinalIgnoreCase);
        return new HostPermissionSnapshot(
            allowed,
            packageInstaller
                .Discover(configuration.DisabledActPluginIds)
                .Where(plugin =>
                    plugin.Enabled &&
                    bundledPluginManager.IsAllowedToLoad(plugin))
                .Select(plugin => plugin.Manifest.Id)
                .ToArray());
    }

    private async Task DisposeComponentsAsync(bool factoryResetCompleted)
    {
        hostCommandQueue.Writer.TryComplete();
        await hostCommandCancellation.CancelAsync().ConfigureAwait(false);
        try
        {
            await hostCommandWorker.WaitAsync(TimeSpan.FromMilliseconds(500))
                .ConfigureAwait(false);
            hostCommandCancellation.Dispose();
        }
        catch (TimeoutException)
        {
            logger.Warning(
                "ACT Host command broker did not stop within 500 ms; it remains off the game thread.");
        }

        if (!factoryResetCompleted)
        {
            var delayedDisposal = DisposeComponentsAfterFactoryResetAsync();
            _ = delayedDisposal.ContinueWith(
                task => logger.Error(
                    task.Exception?.GetBaseException()
                    ?? new InvalidOperationException("Unknown delayed shutdown failure."),
                    "ACT compatibility delayed shutdown failed after factory reset."),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return;
        }

        await DisposeRemainingComponentsAsync().ConfigureAwait(false);
    }

    private async Task DisposeComponentsAfterFactoryResetAsync()
    {
        await factoryResetOperations.WaitForCompletionAsync().ConfigureAwait(false);
        await DisposeRemainingComponentsAsync().ConfigureAwait(false);
    }

    private async Task DisposeRemainingComponentsAsync()
    {
        await ShutdownCactbotOperationsAsync().ConfigureAwait(false);
        await fflogsEstimateService.DisposeAsync().ConfigureAwait(false);
        await lifecycle.DisposeAsync().ConfigureAwait(false);
        await parserEngine.DisposeAsync().ConfigureAwait(false);
        await encounterService.DisposeAsync().ConfigureAwait(false);
        await hostSupervisor.DisposeAsync().ConfigureAwait(false);
    }

    private void OnRawLogLineForHost(
        DateTimeOffset timestamp,
        string rawLine,
        string actLine,
        bool isImport)
    {
        if (!isImport)
        {
            fflogsEstimateService.ObserveLogLine(actLine);
        }
        hostSupervisor.PublishLog(timestamp, rawLine, actLine, isImport);
    }

    private void OnZoneChangedForHost(uint territoryId, string zoneName)
    {
        fflogsEstimateService.NotifyTerritoryChanged(
            territoryId,
            zoneNameLocalizer.Localize(territoryId, zoneName));
        hostSupervisor.PublishZone(territoryId, zoneName);
    }

    private void OnEncounterChangedForHost(ActEncounterSnapshot _, bool finished)
        => hostSupervisor.PublishEncounter(finished);

    private void OnFrameworkUpdateForHost(IFramework _)
    {
        var now = DateTimeOffset.UtcNow;
        if (now < nextHostEntitySnapshotAt)
        {
            return;
        }

        nextHostEntitySnapshotAt = now.AddMilliseconds(500);
        try
        {
            hostSupervisor.PublishFfxivEntities(FfxivEntitySnapshotBuilder.Build(
                objectTable,
                partyList,
                services.ClientState,
                playerState,
                now));
        }
        catch (Exception ex)
        {
            if (now >= nextHostEntitySnapshotFailureLogAt)
            {
                nextHostEntitySnapshotFailureLogAt = now.AddSeconds(10);
                logger.Error(ex, "Game-side FFXIV entity snapshot failed.");
            }
        }
    }

    private void OnHostCommandRequested(object? sender, HostCommandInvocation invocation)
    {
        if (hostCommandQueue.Writer.TryWrite(invocation))
        {
            return;
        }

        logger.Warning(
            $"ACT Host command broker queue is full; rejecting " +
            $"plugin={invocation.Request.PluginId} action={invocation.Request.Command}.");
        hostSupervisor.ReplyCommand(
            invocation.CorrelationId,
            false,
            "busy",
            "The bounded game-side command broker queue is full.");
    }

    private async Task RunHostCommandBrokerAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var invocation in hostCommandQueue.Reader.ReadAllAsync(cancellationToken)
                               .ConfigureAwait(false))
            {
                await HandleHostCommandAsync(invocation, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task HandleHostCommandAsync(
        HostCommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            string? resultDetail = null;
            switch (invocation.Request.Command)
            {
                case "tts":
                    if (!string.Equals(
                            invocation.Request.PluginId,
                            "triggernometry",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        throw new UnauthorizedAccessException(
                            "Only Triggernometry may request the TTS broker capability.");
                    }

                    if (!configuration.IsActCapabilityAllowed(
                            "triggernometry",
                            ActCapability.TextToSpeech))
                    {
                        throw new UnauthorizedAccessException(
                            "Triggernometry TTS capability is denied.");
                    }

                    if (!invocation.Request.Arguments.TryGetValue("text", out var speech) ||
                        speech.Length is 0 or > 2000)
                    {
                        throw new InvalidDataException("TTS payload is invalid.");
                    }

                    // Authorization remains game-side, while the actual FoxTTS callback
                    // stays in the disposable external Host. A blocked/native TTS provider
                    // can therefore be recovered by killing the Host without freezing FFXIV.
                    break;
                case "triggernometry.overlay":
                    if (!string.Equals(
                            invocation.Request.PluginId,
                            "triggernometry",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        throw new UnauthorizedAccessException(
                            "Only Triggernometry may request the OverlayPlugin handler broker.");
                    }

                    if (!configuration.IsActCapabilityAllowed(
                            "triggernometry",
                            ActCapability.HighRiskScript))
                    {
                        throw new UnauthorizedAccessException(
                            "Triggernometry OverlayPlugin handler capability is denied.");
                    }

                    if (!invocation.Request.Arguments.TryGetValue("payload", out var overlayPayload))
                    {
                        throw new InvalidDataException(
                            "Triggernometry OverlayPlugin payload is missing.");
                    }

                    resultDetail = await actRuntime
                        .CallOverlayHandlerAsync(overlayPayload, timeout.Token)
                        .ConfigureAwait(false);
                    break;
                case "postnamazu.chat":
                    if (!string.Equals(
                            invocation.Request.PluginId,
                            "postnamazu",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        throw new UnauthorizedAccessException(
                            "Only PostNamazu may request the game-command broker capability.");
                    }

                    if (!configuration.IsActCapabilityAllowed(
                            "postnamazu",
                            ActCapability.GameCommand))
                    {
                        throw new UnauthorizedAccessException(
                            "PostNamazu game-command capability is denied.");
                    }

                    if (!invocation.Request.Arguments.TryGetValue("text", out var command))
                    {
                        throw new InvalidDataException("PostNamazu command payload is missing.");
                    }

                    ValidatePostNamazuCommand(command);
                    await NativePostNamazuBridge
                        .SendCommandAsync(command, timeout.Token)
                        .ConfigureAwait(false);
                    break;
                case "postnamazu.mark":
                case "postnamazu.place":
                case "postnamazu.pictoact":
                case "postnamazu.preset":
                case "postnamazu.sendkey":
                    if (!string.Equals(
                            invocation.Request.PluginId,
                            "postnamazu",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        throw new UnauthorizedAccessException(
                            "Only PostNamazu may request the marking broker capability.");
                    }

                    if (!configuration.IsActCapabilityAllowed(
                            "postnamazu",
                            ActCapability.GameCommand))
                    {
                        throw new UnauthorizedAccessException(
                            "PostNamazu game-command capability is denied.");
                    }

                    if (!invocation.Request.Arguments.TryGetValue("payload", out var payload))
                    {
                        throw new InvalidDataException("PostNamazu marking payload is missing.");
                    }

                    if (invocation.Request.Command == "postnamazu.mark")
                    {
                        await NativePostNamazuBridge
                            .SendMarkAsync(payload, timeout.Token)
                            .ConfigureAwait(false);
                    }
                    else if (invocation.Request.Command == "postnamazu.place")
                    {
                        await NativePostNamazuBridge
                            .SendWaymarksAsync(payload, timeout.Token)
                            .ConfigureAwait(false);
                    }
                    else if (invocation.Request.Command == "postnamazu.pictoact")
                    {
                        pictoActOverlay.Apply(payload);
                    }
                    else if (invocation.Request.Command == "postnamazu.preset")
                    {
                        await NativePostNamazuBridge
                            .SendPresetAsync(payload, timeout.Token)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        await NativePostNamazuBridge
                            .SendKeyAsync(payload, timeout.Token)
                            .ConfigureAwait(false);
                    }

                    break;
                default:
                    throw new UnauthorizedAccessException(
                        $"Unknown Host command '{invocation.Request.Command}' is denied.");
            }

            logger.Information(
                $"ACT Host command broker allowed plugin={invocation.Request.PluginId} " +
                $"action={invocation.Request.Command}.");

            hostSupervisor.ReplyCommand(
                invocation.CorrelationId,
                true,
                "completed",
                resultDetail);
        }
        catch (Exception ex)
        {
            logger.Error(ex, $"ACT Host command '{invocation.Request.Command}' was rejected.");
            hostSupervisor.ReplyCommand(
                invocation.CorrelationId,
                false,
                "denied",
                ex.Message);
        }
    }

    private static void ValidatePostNamazuCommand(string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        if (!command.StartsWith('/'))
        {
            throw new InvalidDataException(
                "PostNamazu commands must begin with '/'.");
        }
    }

    private IReadOnlyList<RuntimePluginSpec> DiscoverRuntimePlugins()
        => [];

    private static IReadOnlyList<ActPlayerIdentity> BuildPlayerIdentities(
        IPlayerState playerState,
        IPartyList partyList,
        bool localPlayerDead)
    {
        var identities = new Dictionary<string, ActPlayerIdentity>(StringComparer.OrdinalIgnoreCase);
        if (playerState.IsLoaded && !string.IsNullOrWhiteSpace(playerState.CharacterName))
        {
            var identity = new ActPlayerIdentity(
                playerState.CharacterName,
                playerState.HomeWorld.ValueNullable?.Name.ToString() ?? string.Empty,
                playerState.ClassJob.ValueNullable?.Abbreviation.ToString() ?? string.Empty,
                true,
                localPlayerDead)
            {
                EntityId = playerState.EntityId,
                ContentId = playerState.ContentId,
                WorldId = playerState.HomeWorld.RowId,
                JobId = unchecked((byte)playerState.ClassJob.RowId),
                Level = unchecked((byte)playerState.EffectiveLevel),
                CurrentHp = localPlayerDead ? 0u : 1u,
                MaxHp = 1,
            };
            identities[identity.DisplayName] = identity;
        }

        foreach (var member in partyList)
        {
            var name = member.Name.TextValue;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var identity = new ActPlayerIdentity(
                name,
                member.World.ValueNullable?.Name.ToString() ?? string.Empty,
                member.ClassJob.ValueNullable?.Abbreviation.ToString() ?? string.Empty,
                (member.ContentId != 0 && member.ContentId == playerState.ContentId) ||
                (member.EntityId != 0 && member.EntityId == playerState.EntityId),
                member.MaxHP > 0 && member.CurrentHP == 0)
            {
                EntityId = member.EntityId,
                ContentId = member.ContentId,
                WorldId = member.World.RowId,
                JobId = unchecked((byte)member.ClassJob.RowId),
                Level = member.Level,
                CurrentHp = member.CurrentHP,
                MaxHp = member.MaxHP,
                CurrentMp = member.CurrentMP,
                MaxMp = member.MaxMP,
                TerritoryId = unchecked((ushort)member.Territory.RowId),
                PositionX = member.Position.X,
                PositionY = member.Position.Y,
                PositionZ = member.Position.Z,
            };
            identities[identity.DisplayName] = identity;
        }

        return identities.Values.ToArray();
    }
}

using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.ImGuiNotification;
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
    private readonly HelpWindow helpWindow;
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
    private readonly ActHostSupervisor matchaHostSupervisor;
    private readonly ActHostSupervisor genericHostSupervisor;
    private readonly SemaphoreSlim hostTopologyLock = new(1, 1);
    private readonly object permissionSnapshotLock = new();
    private readonly object pluginInstallStatusLock = new();
    private string? activeHostPermissionFingerprint;
    private string? activeMatchaPermissionFingerprint;
    private string? activeGenericPermissionFingerprint;
    private ThirdPartyPluginInstallStatus pluginInstallStatus = new(
        ThirdPartyPluginInstallState.Idle);
    private int silverDasherEventsEnabled;
    private int matchaEventsEnabled;
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
        var disabledUntrustedPlugin = false;
        foreach (var installed in packageInstaller.Discover(configuration.DisabledActPluginIds))
        {
            if (!ActPluginPackageInstaller.IsSpecializedPluginId(installed.Manifest.Id) &&
                !configuration.TrustedGenericActPluginIds.Contains(installed.Manifest.Id))
            {
                disabledUntrustedPlugin |= configuration.DisabledActPluginIds.Add(
                    installed.Manifest.Id);
            }
        }
        if (disabledUntrustedPlugin)
        {
            // Old arbitrary manifests predate full-trust consent and must re-enter through the new review card.
            pluginInterface.SavePluginConfig(configuration);
        }

        var hostIpcClient = new HostIpcClient(
            stateStore,
            logger,
            BuildHostPermissionSnapshot);
        hostSupervisor = new ActHostSupervisor(
            paths.HostDirectory,
            paths.ActPluginDirectory,
            paths.ConfigDirectory,
            hostIpcClient,
            logger,
            () => Volatile.Read(ref silverDasherEventsEnabled) == 1);
        hostSupervisor.CommandRequested += OnHostCommandRequested;
        hostSupervisor.SilverDasherNotificationRequested += OnSilverDasherNotificationRequested;
        var matchaIpcClient = new HostIpcClient(
            stateStore,
            logger,
            BuildMatchaHostPermissionSnapshot);
        matchaHostSupervisor = new ActHostSupervisor(
            paths.HostDirectory,
            paths.ActPluginDirectory,
            paths.ConfigDirectory,
            matchaIpcClient,
            logger,
            matchaEventsEnabled: () => Volatile.Read(ref matchaEventsEnabled) == 1);
        matchaHostSupervisor.MatchaNotificationRequested += OnMatchaNotificationRequested;
        matchaHostSupervisor.MatchaLogLineRequested += OnMatchaLogLineRequested;
        matchaHostSupervisor.MatchaTtsRequested += OnMatchaTtsRequested;
        var genericIpcClient = new HostIpcClient(
            stateStore,
            logger,
            BuildGenericHostPermissionSnapshot);
        genericHostSupervisor = new ActHostSupervisor(
            paths.HostDirectory,
            paths.ActPluginDirectory,
            paths.ConfigDirectory,
            genericIpcClient,
            logger);
        genericHostSupervisor.MatchaTtsRequested += OnGenericTtsRequested;
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
            () => _ = framework.RunOnFrameworkThread(SaveConfiguration),
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
        actRuntime.NetworkReceived += OnNetworkReceivedForHost;
        actRuntime.NetworkSent += OnNetworkSentForMatchaHost;
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
        var helpTexture = textureProvider.GetFromFile(
            Path.Combine(assetDirectory, "HelpIcon.png"));
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
            DeleteHtmlOverlay,
            name => _ = actRuntime.ApplyOverlayWindowSettings(name),
            OpenActPluginConfiguration,
            () => StartBundledPluginUpdateCheck(openWindow: true));
        statusWindow = new StatusWindow(
            parserEngine,
            text,
            logoTexture,
            () => hostSupervisor.Snapshot,
            () => matchaHostSupervisor.Snapshot,
            () => genericHostSupervisor.Snapshot,
            RestartHostFromUi,
            StopHostFromUi,
            RestartMatchaHostFromUi,
            StopMatchaHostFromUi,
            RestartGenericHostFromUi,
            StopGenericHostFromUi);
        helpWindow = new HelpWindow(
            text,
            logoTexture,
            logger,
            () => statusWindow.IsOpen = true,
            OpenLogDirectory,
            thirdPartyPluginNoticeWindow.OpenManualDisclosure);
        settingsWindow = new ControlCenterWindow(
            configuration,
            parserEngine,
            logger,
            text,
            fflogsEstimateService,
            () => stateStore.GetSnapshot().Current,
            logoTexture,
            helpTexture,
            () => helpWindow.IsOpen = true,
            SaveConfiguration,
            () => ApplyActPermissionChanges(),
            SetMeterVisible,
            () => SetMeterVisible(true),
            encounterWindow.OpenRecent,
            () => statusWindow.IsOpen,
            value => statusWindow.IsOpen = value,
            SelectPluginPackage,
            GetPluginInstallStatus,
            ApprovePendingGenericPlugin,
            DenyPendingGenericPlugin,
            RequestGenericPluginAuthorization,
            UninstallGenericActPlugin,
            DismissPluginInstallFailure,
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
        windowSystem.AddWindow(helpWindow);
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
        actRuntime.NetworkReceived -= OnNetworkReceivedForHost;
        actRuntime.NetworkSent -= OnNetworkSentForMatchaHost;
        actRuntime.EncounterChanged -= OnEncounterChangedForHost;
        services.Framework.Update -= OnFrameworkUpdateForHost;
        hostSupervisor.CommandRequested -= OnHostCommandRequested;
        hostSupervisor.SilverDasherNotificationRequested -= OnSilverDasherNotificationRequested;
        matchaHostSupervisor.MatchaNotificationRequested -= OnMatchaNotificationRequested;
        matchaHostSupervisor.MatchaLogLineRequested -= OnMatchaLogLineRequested;
        matchaHostSupervisor.MatchaTtsRequested -= OnMatchaTtsRequested;
        genericHostSupervisor.MatchaTtsRequested -= OnGenericTtsRequested;

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
                OpenCactbotOverlay();
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
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        await cactbotFileOperationGate.WaitAsync(timeout.Token).ConfigureAwait(false);
        await hostTopologyLock.WaitAsync(timeout.Token).ConfigureAwait(false);
        var hostWasRunning = hostSupervisor.Snapshot.State == HostSupervisorState.Running;
        var matchaWasRunning =
            matchaHostSupervisor.Snapshot.State == HostSupervisorState.Running;
        var genericWasRunning =
            genericHostSupervisor.Snapshot.State == HostSupervisorState.Running;
        try
        {
            try
            {
                Volatile.Write(ref matchaEventsEnabled, 0);
                actRuntime.SetNetworkSentCaptureEnabled(false);
                if (matchaWasRunning)
                {
                    await matchaHostSupervisor.StopAsync(timeout.Token).ConfigureAwait(false);
                }
                if (genericWasRunning)
                {
                    await genericHostSupervisor.StopAsync(timeout.Token).ConfigureAwait(false);
                }
                if (hostWasRunning)
                {
                    await hostSupervisor.StopAsync(timeout.Token).ConfigureAwait(false);
                }

                return await factoryResetService
                    .ResetAsync(timeout.Token, shutdownToken)
                    .ConfigureAwait(false);
            }
            catch (Exception resetFailure)
            {
                var restoreFailures = new List<Exception>();
                if (hostWasRunning && !shutdownToken.IsCancellationRequested)
                {
                    try
                    {
                        await hostSupervisor.StartAsync(timeout.Token).ConfigureAwait(false);
                        await hostSupervisor.WaitForPluginStartupAsync(timeout.Token)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        restoreFailures.Add(ex);
                    }
                }
                if (matchaWasRunning &&
                    hostSupervisor.Snapshot.State == HostSupervisorState.Running &&
                    !shutdownToken.IsCancellationRequested)
                {
                    try
                    {
                        await StartMatchaAfterSharedHostAsync(
                                timeout.Token,
                                throwOnFailure: true)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        restoreFailures.Add(ex);
                    }
                }
                if (genericWasRunning && !shutdownToken.IsCancellationRequested)
                {
                    try
                    {
                        await StartGenericHostAsync(timeout.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        restoreFailures.Add(ex);
                    }
                }

                if (restoreFailures.Count > 0)
                {
                    throw new AggregateException(
                        "Factory reset failed and one or more Host processes could not be restored.",
                        [resetFailure, .. restoreFailures]);
                }

                throw;
            }
        }
        finally
        {
            hostTopologyLock.Release();
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

        var normalizedPackagePath = packagePath.Trim('"');
        SetPluginInstallStatus(new ThirdPartyPluginInstallStatus(
            ThirdPartyPluginInstallState.Preflighting,
            Path.GetFileNameWithoutExtension(normalizedPackagePath),
            Detail: "正在进行安全静态预检；此阶段不会执行 DLL。"));
        _ = Task.Run(async () =>
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var installed = await packageInstaller.InstallAsync(
                    normalizedPackagePath,
                    timeout.Token).ConfigureAwait(false);
                if (!ActPluginPackageInstaller.IsSpecializedPluginId(installed.Manifest.Id))
                {
                    // A changed DLL needs fresh consent even when an older version was trusted.
                    configuration.DisabledActPluginIds.Add(installed.Manifest.Id);
                    configuration.TrustedGenericActPluginIds.Remove(installed.Manifest.Id);
                    configuration.ActPluginPermissions.Remove(installed.Manifest.Id);
                    SaveConfiguration();
                    SetPluginInstallStatus(CreatePermissionPrompt(installed));
                    ApplyActPermissionChanges();
                    logger.Information(
                        $"Static preflight completed for generic ACT plugin {installed.Manifest.Name} {installed.Manifest.Version}; waiting for user permission.");
                    return;
                }

                configuration.DisabledActPluginIds.Remove(installed.Manifest.Id);
                SaveConfiguration();
                SetPluginInstallStatus(new ThirdPartyPluginInstallStatus(
                    ThirdPartyPluginInstallState.Ready,
                    installed.Manifest.Name,
                    installed.Manifest.Id,
                    installed.Manifest.Version,
                    Detail: "预检与安装完成；该插件继续使用现有特化 Host 和权限设置。"));
                logger.Information(
                    $"Installed ACT plugin {installed.Manifest.Name} {installed.Manifest.Version}; its assigned Host will refresh.");
                ApplyActPermissionChanges();
            }
            catch (Exception ex)
            {
                SetPluginInstallStatus(new ThirdPartyPluginInstallStatus(
                    ThirdPartyPluginInstallState.Failed,
                    Path.GetFileNameWithoutExtension(normalizedPackagePath),
                    Detail: ex.GetBaseException().Message,
                    Diagnostic: ex.ToString()));
                logger.Error(ex, "ACT plugin package installation failed.");
            }
        });
    }

    private ThirdPartyPluginInstallStatus GetPluginInstallStatus()
    {
        lock (pluginInstallStatusLock)
        {
            return pluginInstallStatus;
        }
    }

    private void SetPluginInstallStatus(ThirdPartyPluginInstallStatus status)
    {
        lock (pluginInstallStatusLock)
        {
            pluginInstallStatus = status;
        }

        QueuePluginInstallFeedback(status);
    }

    private void DismissPluginInstallFailure()
    {
        lock (pluginInstallStatusLock)
        {
            if (pluginInstallStatus.State == ThirdPartyPluginInstallState.Failed)
            {
                // Dismissing removes only transient UI feedback; installed files and the
                // package selected by the user are outside this in-memory status object.
                pluginInstallStatus = new ThirdPartyPluginInstallStatus(
                    ThirdPartyPluginInstallState.Idle);
            }
        }
    }

    private void QueuePluginInstallFeedback(ThirdPartyPluginInstallStatus status)
    {
        var content = status.State switch
        {
            ThirdPartyPluginInstallState.AwaitingPermission =>
                $"{status.DisplayName} 预检完成，请查看权限并决定是否启用。",
            ThirdPartyPluginInstallState.Ready =>
                $"{status.DisplayName} 已通过运行时预检并启用。",
            ThirdPartyPluginInstallState.Removed =>
                $"{status.DisplayName} 已删除，原文件保存在备份目录。",
            ThirdPartyPluginInstallState.Failed =>
                $"{status.DisplayName} 操作失败：{status.Detail}",
            _ => null,
        };
        if (content is null)
        {
            return;
        }

        try
        {
            _ = services.Framework.RunOnFrameworkThread(() =>
            {
                if (status.State is ThirdPartyPluginInstallState.AwaitingPermission or
                    ThirdPartyPluginInstallState.Failed)
                {
                    // Authorization and import failures both require a user decision, so place
                    // their modal in front instead of leaving feedback below a scrollable page.
                    settingsWindow.ShowExtensionsPage();
                }

                services.NotificationManager.AddNotification(new Notification
                {
                    Title = "第三方 ACT 插件",
                    Content = content,
                    Type = status.State == ThirdPartyPluginInstallState.Failed
                        ? NotificationType.Error
                        : NotificationType.Info,
                });
            });
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Could not display third-party ACT plugin install feedback.");
        }
    }

    private static ThirdPartyPluginInstallStatus CreatePermissionPrompt(
        InstalledActPlugin installed)
        => new(
            ThirdPartyPluginInstallState.AwaitingPermission,
            installed.Manifest.Name,
            installed.Manifest.Id,
            installed.Manifest.Version,
            ActPluginPackageInstaller.GetRequestedCapabilities(installed.Manifest),
            "静态预检已通过。允许后，该 DLL 将在所有普通第三方插件共用的通用 Host 中作为桌面代码运行。");

    private void RequestGenericPluginAuthorization(string pluginId)
    {
        var installed = packageInstaller
            .Discover(configuration.DisabledActPluginIds)
            .FirstOrDefault(plugin => string.Equals(
                plugin.Manifest.Id,
                pluginId,
                StringComparison.OrdinalIgnoreCase));
        if (installed is null || ActPluginPackageInstaller.IsSpecializedPluginId(pluginId))
        {
            return;
        }

        SetPluginInstallStatus(CreatePermissionPrompt(installed));
    }

    private void DenyPendingGenericPlugin()
    {
        var pending = GetPluginInstallStatus();
        if (pending.State != ThirdPartyPluginInstallState.AwaitingPermission ||
            string.IsNullOrWhiteSpace(pending.PluginId))
        {
            return;
        }

        configuration.DisabledActPluginIds.Add(pending.PluginId);
        configuration.TrustedGenericActPluginIds.Remove(pending.PluginId);
        configuration.ActPluginPermissions.Remove(pending.PluginId);
        SaveConfiguration();
        SetPluginInstallStatus(pending with
        {
            State = ThirdPartyPluginInstallState.Denied,
            Detail = "已安装但未授权，因此保持禁用；可稍后在扩展列表中重新授权。",
        });
        ApplyActPermissionChanges();
    }

    private void ApprovePendingGenericPlugin()
    {
        var pending = GetPluginInstallStatus();
        if (pending.State != ThirdPartyPluginInstallState.AwaitingPermission ||
            string.IsNullOrWhiteSpace(pending.PluginId))
        {
            return;
        }

        SetPluginInstallStatus(pending with
        {
            State = ThirdPartyPluginInstallState.StartingHost,
            Detail = "权限已确认，正在启动共享通用 Host 并执行运行时加载检查。",
        });
        _ = Task.Run(async () =>
        {
            try
            {
                // Consent applies to this preflight's exact capability list, not grants
                // retained from an older DLL or manifest with the same plugin id.
                configuration.ActPluginPermissions.Remove(pending.PluginId);
                foreach (var capability in pending.Capabilities)
                {
                    configuration.SetActCapability(pending.PluginId, capability, true);
                }

                configuration.TrustedGenericActPluginIds.Add(pending.PluginId);
                configuration.DisabledActPluginIds.Remove(pending.PluginId);
                SaveConfiguration();

                await hostTopologyLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
                    if (genericHostSupervisor.Snapshot.State != HostSupervisorState.Stopped)
                    {
                        await genericHostSupervisor.StopAsync(timeout.Token).ConfigureAwait(false);
                    }

                    await genericHostSupervisor.StartAsync(timeout.Token).ConfigureAwait(false);
                    await genericHostSupervisor.WaitForPluginStartupAsync(timeout.Token)
                        .ConfigureAwait(false);
                    var initStage = await genericHostSupervisor.WaitForPluginStageAsync(
                            pending.PluginId,
                            "InitPlugin",
                            timeout.Token)
                        .ConfigureAwait(false);
                    if (!string.Equals(
                            initStage.State,
                            "success",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            initStage.Detail ?? "通用 Host 未报告插件初始化成功。");
                    }
                }
                finally
                {
                    hostTopologyLock.Release();
                }

                SetPluginInstallStatus(pending with
                {
                    State = ThirdPartyPluginInstallState.Ready,
                    Detail = "运行时预检通过，插件已在共享通用 Host 中启用。",
                });
            }
            catch (Exception ex)
            {
                configuration.DisabledActPluginIds.Add(pending.PluginId);
                SaveConfiguration();
                var cleanupDetail = string.Empty;
                try
                {
                    await hostTopologyLock.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                        if (genericHostSupervisor.Snapshot.State != HostSupervisorState.Stopped)
                        {
                            await genericHostSupervisor.StopAsync(timeout.Token).ConfigureAwait(false);
                        }

                        // A failed InitPlugin can leave handlers behind, so only a fresh
                        // process may continue serving the other trusted generic plugins.
                        await StartGenericHostAsync(timeout.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        hostTopologyLock.Release();
                    }
                }
                catch (Exception cleanupFailure)
                {
                    cleanupDetail = $"；通用 Host 清理也失败：{cleanupFailure.GetBaseException().Message}";
                    logger.Error(
                        cleanupFailure,
                        "Generic ACT Host cleanup failed after a plugin preflight error.");
                }

                SetPluginInstallStatus(pending with
                {
                    State = ThirdPartyPluginInstallState.Failed,
                    Detail = $"运行时预检失败，插件已重新禁用：{ex.GetBaseException().Message}{cleanupDetail}",
                    Diagnostic = ex.ToString(),
                });
                logger.Error(ex, $"Generic ACT plugin runtime preflight failed: {pending.PluginId}.");
            }
        });
    }

    private void UninstallGenericActPlugin(string pluginId)
    {
        var installed = packageInstaller
            .Discover(configuration.DisabledActPluginIds)
            .FirstOrDefault(plugin => string.Equals(
                plugin.Manifest.Id,
                pluginId,
                StringComparison.OrdinalIgnoreCase));
        if (installed is null ||
            ActPluginPackageInstaller.IsSpecializedPluginId(installed.Manifest.Id))
        {
            return;
        }

        SetPluginInstallStatus(new ThirdPartyPluginInstallStatus(
            ThirdPartyPluginInstallState.Removing,
            installed.Manifest.Name,
            installed.Manifest.Id,
            installed.Manifest.Version,
            Detail: "正在安全停止通用 Host 并删除插件。"));
        _ = Task.Run(async () =>
        {
            try
            {
                string? backupDirectory;
                await hostTopologyLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    if (genericHostSupervisor.Snapshot.State != HostSupervisorState.Stopped)
                    {
                        await genericHostSupervisor.StopAsync(timeout.Token).ConfigureAwait(false);
                    }

                    backupDirectory = await packageInstaller.UninstallAsync(
                            installed.Manifest.Id,
                            timeout.Token)
                        .ConfigureAwait(false);
                    configuration.DisabledActPluginIds.Remove(installed.Manifest.Id);
                    configuration.TrustedGenericActPluginIds.Remove(installed.Manifest.Id);
                    configuration.ActPluginPermissions.Remove(installed.Manifest.Id);
                    SaveConfiguration();
                    await StartGenericHostAsync(timeout.Token).ConfigureAwait(false);
                }
                finally
                {
                    hostTopologyLock.Release();
                }

                SetPluginInstallStatus(new ThirdPartyPluginInstallStatus(
                    ThirdPartyPluginInstallState.Removed,
                    installed.Manifest.Name,
                    installed.Manifest.Id,
                    installed.Manifest.Version,
                    Detail: backupDirectory is null
                        ? "插件目录已不存在，相关授权记录已清理。"
                        : "插件已从扩展列表移除，原文件保存在插件备份目录。"));
                logger.Information(
                    $"Uninstalled generic ACT plugin {installed.Manifest.Id}; backup={backupDirectory ?? "none"}.");
            }
            catch (Exception ex)
            {
                SetPluginInstallStatus(new ThirdPartyPluginInstallStatus(
                    ThirdPartyPluginInstallState.Failed,
                    installed.Manifest.Name,
                    installed.Manifest.Id,
                    installed.Manifest.Version,
                    Detail: $"删除失败：{ex.GetBaseException().Message}",
                    Diagnostic: ex.ToString()));
                logger.Error(ex, $"Could not uninstall generic ACT plugin {installed.Manifest.Id}.");
            }
        });
    }

    private async Task InstallBundledPluginsAsync(
        IReadOnlyList<BundledActPluginDescriptor> plugins)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            bundledUpdateCancellation.Token);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        await hostTopologyLock.WaitAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            var hostWasRunning = hostSupervisor.Snapshot.State == HostSupervisorState.Running;
            var parserWasRunning = parserEngine.Status.State == ParserState.Running;
            var matchaWasRunning =
                matchaHostSupervisor.Snapshot.State == HostSupervisorState.Running;
            try
            {
                if (matchaWasRunning)
                {
                    Volatile.Write(ref matchaEventsEnabled, 0);
                    actRuntime.SetNetworkSentCaptureEnabled(false);
                    await matchaHostSupervisor.StopAsync(timeout.Token).ConfigureAwait(false);
                }

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
            }
            catch (Exception installFailure)
            {
                try
                {
                    if (matchaWasRunning &&
                        hostSupervisor.Snapshot.State == HostSupervisorState.Running)
                    {
                        await hostSupervisor.WaitForPluginStartupAsync(timeout.Token)
                            .ConfigureAwait(false);
                        await StartMatchaAfterSharedHostAsync(
                                timeout.Token,
                                throwOnFailure: true)
                            .ConfigureAwait(false);
                    }
                }
                catch (Exception restoreFailure)
                {
                    throw new AggregateException(
                        "Bundled plugin installation failed and the Matcha Host could not be restored.",
                        installFailure,
                        restoreFailure);
                }

                throw;
            }

            if (hostWasRunning &&
                hostSupervisor.Snapshot.State == HostSupervisorState.Running)
            {
                await hostSupervisor.WaitForPluginStartupAsync(timeout.Token).ConfigureAwait(false);
                await StartMatchaAfterSharedHostAsync(timeout.Token).ConfigureAwait(false);
            }

            services.NotificationManager.AddNotification(new()
            {
                Title = "ACT 兼容",
                Content = "第三方 DLL 已按告知版本安装/更新；作者、版本和来源告知已记录。",
            });
        }
        finally
        {
            hostTopologyLock.Release();
        }
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
                                    "正在检查三项已注册在线来源的 DLL。",
                                    "Checking the three DLLs with registered online sources."),
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
        foreach (var (pluginId, capabilities) in BundledActPluginCapabilities.FullPermissionConfirmation)
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
            ? "All declared capabilities were enabled for bundled ACT plugins, including SilverDasher and Matcha, by user choice."
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
        var target = string.Equals(pluginId, "matcha", StringComparison.OrdinalIgnoreCase)
            ? matchaHostSupervisor
            : ActPluginPackageInstaller.IsSpecializedPluginId(pluginId)
                ? hostSupervisor
                : genericHostSupervisor;
        if (target.OpenPluginUi(pluginId))
        {
            return;
        }

        logger.Warning($"ACT plugin '{pluginId}' is not running in its assigned Host.");
        services.NotificationManager.AddNotification(new()
        {
            Title = "ACT 兼容",
            Content = $"扩展 {pluginId} 未在分配的 Host 中成功加载，请重启对应 Host 并查看状态日志。",
        });
    }

    private void OpenCactbotOverlay()
        => _ = OpenHtmlOverlay(configuration.SelectedCactbotOverlay);

    private void OpenCactbotSettings()
    {
        if (!actRuntime.ShowCactbotSettings())
        {
            logger.Warning(
                "Cactbot settings are not available. Install Cactbot, enable OverlayPlugin, and restart the parser.");
        }
    }

    private bool OpenHtmlOverlay(string name)
    {
        name = SelfHostedActRuntime.NormalizeCactbotOverlayName(name);
        try
        {
            if (actRuntime.ShowHtmlOverlay(name))
            {
                configuration.RegisterOverlayWindow(name).OpenOnStartup = true;
                var template = actRuntime.OverlayTemplates.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
                if (template?.IsCactbot == true)
                {
                    configuration.SelectedCactbotOverlay = name;
                }
                else if (template is not null)
                {
                    configuration.SelectedOverlayTemplate = name;
                }
                SaveConfiguration();
                return true;
            }

            logger.Warning(
                $"HTML overlay '{name}' is unavailable. Enable OverlayPlugin, restart the parser, and select a listed template or saved custom URL.");
            return false;
        }
        catch (Exception ex)
        {
            logger.Error(ex, $"HTML overlay '{name}' could not be opened.");
            return false;
        }
    }

    private void CloseHtmlOverlay(string name)
    {
        name = SelfHostedActRuntime.NormalizeCactbotOverlayName(name);
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
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        if (SelfHostedActRuntime.IsCactbotOverlayName(name))
        {
            if (!actRuntime.ResetCactbotOverlayWindow(name))
            {
                logger.Warning($"Cactbot overlay '{name}' could not be reset.");
                return;
            }

            SaveConfiguration();
            logger.Information(
                $"Removed Cactbot overlay '{name}' from the opened list and reset its saved layout.");
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
        await hostTopologyLock.WaitAsync().ConfigureAwait(false);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            await hostSupervisor.StartAsync(timeout.Token).ConfigureAwait(false);
            await hostSupervisor.WaitForPluginStartupAsync(timeout.Token).ConfigureAwait(false);
            await StartMatchaAfterSharedHostAsync(timeout.Token).ConfigureAwait(false);
            await StartGenericHostAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.Error(
                ex,
                "Independent ACT Host did not start. In-process parsing and overlays remain available, " +
                "but traditional ACT plugins stay unloaded because hard crash isolation is not active.");
        }
        finally
        {
            hostTopologyLock.Release();
        }
    }

    private async Task StartMatchaAfterSharedHostAsync(
        CancellationToken cancellationToken,
        bool throwOnFailure = false)
    {
        if (!IsMatchaConfiguredToRun())
        {
            Volatile.Write(ref matchaEventsEnabled, 0);
            actRuntime.SetNetworkSentCaptureEnabled(false);
            if (matchaHostSupervisor.Snapshot.State != HostSupervisorState.Stopped)
            {
                await matchaHostSupervisor.StopAsync(cancellationToken).ConfigureAwait(false);
            }
            return;
        }

        try
        {
            Volatile.Write(ref matchaEventsEnabled, 1);
            actRuntime.SetNetworkSentCaptureEnabled(true);
            await matchaHostSupervisor.StartAsync(cancellationToken).ConfigureAwait(false);
            await matchaHostSupervisor.WaitForPluginStartupAsync(cancellationToken).ConfigureAwait(false);
            logger.Information(
                "Matcha dedicated Host started after the shared ACT Host completed plugin initialization.");
        }
        catch (Exception ex)
        {
            Volatile.Write(ref matchaEventsEnabled, 0);
            actRuntime.SetNetworkSentCaptureEnabled(false);
            logger.Error(
                ex,
                "Matcha dedicated Host did not start. The shared ACT Host and all existing extensions remain untouched.");
            if (throwOnFailure)
            {
                throw;
            }
        }
    }

    private async Task StartGenericHostAsync(CancellationToken cancellationToken)
    {
        if (!IsGenericHostConfiguredToRun())
        {
            if (genericHostSupervisor.Snapshot.State != HostSupervisorState.Stopped)
            {
                await genericHostSupervisor.StopAsync(cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        await genericHostSupervisor.StartAsync(cancellationToken).ConfigureAwait(false);
        await genericHostSupervisor.WaitForPluginStartupAsync(cancellationToken).ConfigureAwait(false);
        logger.Information(
            "Generic ACT Host is running for user-installed non-specialized plugins.");
    }

    private void RestartHostFromUi()
    {
        _ = Task.Run(async () =>
        {
            await hostTopologyLock.WaitAsync().ConfigureAwait(false);
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
            finally
            {
                hostTopologyLock.Release();
            }
        });
    }

    private void RestartMatchaHostFromUi()
    {
        _ = Task.Run(async () =>
        {
            await hostTopologyLock.WaitAsync().ConfigureAwait(false);
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                Volatile.Write(ref matchaEventsEnabled, 0);
                actRuntime.SetNetworkSentCaptureEnabled(false);
                await matchaHostSupervisor.StopAsync(timeout.Token).ConfigureAwait(false);
                if (hostSupervisor.Snapshot.State != HostSupervisorState.Running)
                {
                    logger.Warning(
                        "Matcha Host was not restarted because the shared ACT Host is not running.");
                    return;
                }

                await hostSupervisor.WaitForPluginStartupAsync(timeout.Token).ConfigureAwait(false);
                await StartMatchaAfterSharedHostAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Volatile.Write(ref matchaEventsEnabled, 0);
                actRuntime.SetNetworkSentCaptureEnabled(false);
                logger.Error(ex, "Matcha Host UI restart failed; the shared ACT Host was not restarted.");
            }
            finally
            {
                hostTopologyLock.Release();
            }
        });
    }

    private void RestartGenericHostFromUi()
    {
        _ = Task.Run(async () =>
        {
            await hostTopologyLock.WaitAsync().ConfigureAwait(false);
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                if (genericHostSupervisor.Snapshot.State != HostSupervisorState.Stopped)
                {
                    await genericHostSupervisor.StopAsync(timeout.Token).ConfigureAwait(false);
                }

                await StartGenericHostAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Generic ACT Host UI restart failed.");
            }
            finally
            {
                hostTopologyLock.Release();
            }
        });
    }

    private void ApplyActPermissionChanges(bool switchFoxTtsToPro = false)
    {
        _ = Task.Run(async () =>
        {
            await hostTopologyLock.WaitAsync().ConfigureAwait(false);
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

                var desiredHost = CreateHostPermissionSnapshot();
                var desiredMatcha = CreateMatchaHostPermissionSnapshot();
                var desiredGeneric = CreateGenericHostPermissionSnapshot();
                string? activeHost;
                string? activeMatcha;
                string? activeGeneric;
                lock (permissionSnapshotLock)
                {
                    activeHost = activeHostPermissionFingerprint;
                    activeMatcha = activeMatchaPermissionFingerprint;
                    activeGeneric = activeGenericPermissionFingerprint;
                }

                if (!string.Equals(
                        activeHost,
                        BuildPermissionFingerprint(desiredHost),
                        StringComparison.Ordinal) &&
                    await hostSupervisor.RestartAsync(timeout.Token).ConfigureAwait(false))
                {
                    logger.Information("Shared ACT Host restarted after its configuration changed.");
                }

                var genericChanged = !string.Equals(
                    activeGeneric,
                    BuildPermissionFingerprint(desiredGeneric),
                    StringComparison.Ordinal);
                if (!IsGenericHostConfiguredToRun())
                {
                    if (genericHostSupervisor.Snapshot.State != HostSupervisorState.Stopped)
                    {
                        await genericHostSupervisor.StopAsync(timeout.Token).ConfigureAwait(false);
                    }
                }
                else if (genericHostSupervisor.Snapshot.State == HostSupervisorState.Running &&
                         genericChanged)
                {
                    await genericHostSupervisor.StopAsync(timeout.Token).ConfigureAwait(false);
                    await StartGenericHostAsync(timeout.Token).ConfigureAwait(false);
                }
                else if (genericHostSupervisor.Snapshot.State != HostSupervisorState.Running)
                {
                    await StartGenericHostAsync(timeout.Token).ConfigureAwait(false);
                }

                if (!IsMatchaConfiguredToRun())
                {
                    Volatile.Write(ref matchaEventsEnabled, 0);
                    actRuntime.SetNetworkSentCaptureEnabled(false);
                    if (matchaHostSupervisor.Snapshot.State != HostSupervisorState.Stopped)
                    {
                        await matchaHostSupervisor.StopAsync(timeout.Token).ConfigureAwait(false);
                    }
                    logger.Information(
                        "Matcha was disabled without restarting the shared ACT Host.");
                    return;
                }

                if (hostSupervisor.Snapshot.State != HostSupervisorState.Running)
                {
                    logger.Warning(
                        "Matcha configuration was saved, but its Host remains stopped because the shared ACT Host is not running.");
                    return;
                }

                await hostSupervisor.WaitForPluginStartupAsync(timeout.Token).ConfigureAwait(false);
                var matchaChanged = !string.Equals(
                    activeMatcha,
                    BuildPermissionFingerprint(desiredMatcha),
                    StringComparison.Ordinal);
                if (matchaHostSupervisor.Snapshot.State == HostSupervisorState.Running && matchaChanged)
                {
                    Volatile.Write(ref matchaEventsEnabled, 0);
                    actRuntime.SetNetworkSentCaptureEnabled(false);
                    await matchaHostSupervisor.StopAsync(timeout.Token).ConfigureAwait(false);
                }

                if (matchaHostSupervisor.Snapshot.State != HostSupervisorState.Running)
                {
                    await StartMatchaAfterSharedHostAsync(timeout.Token).ConfigureAwait(false);
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
            finally
            {
                hostTopologyLock.Release();
            }
        });
    }

    private void StopHostFromUi()
    {
        _ = Task.Run(async () =>
        {
            await hostTopologyLock.WaitAsync().ConfigureAwait(false);
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await hostSupervisor.StopAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "ACT Host UI stop failed.");
            }
            finally
            {
                hostTopologyLock.Release();
            }
        });
    }

    private void StopMatchaHostFromUi()
    {
        _ = Task.Run(async () =>
        {
            await hostTopologyLock.WaitAsync().ConfigureAwait(false);
            try
            {
                Volatile.Write(ref matchaEventsEnabled, 0);
                actRuntime.SetNetworkSentCaptureEnabled(false);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await matchaHostSupervisor.StopAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Matcha Host UI stop failed.");
            }
            finally
            {
                hostTopologyLock.Release();
            }
        });
    }

    private void StopGenericHostFromUi()
    {
        _ = Task.Run(async () =>
        {
            await hostTopologyLock.WaitAsync().ConfigureAwait(false);
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await genericHostSupervisor.StopAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Generic ACT Host UI stop failed.");
            }
            finally
            {
                hostTopologyLock.Release();
            }
        });
    }

    private HostPermissionSnapshot BuildHostPermissionSnapshot()
    {
        var snapshot = CreateHostPermissionSnapshot();
        Volatile.Write(
            ref silverDasherEventsEnabled,
            snapshot.AllowedPluginIds.Contains(
                "silverdasher",
                StringComparer.OrdinalIgnoreCase) ? 1 : 0);
        lock (permissionSnapshotLock)
        {
            activeHostPermissionFingerprint = BuildPermissionFingerprint(snapshot);
        }
        return snapshot;
    }

    private HostPermissionSnapshot CreateHostPermissionSnapshot()
    {
        var capabilities = Enum.GetValues<ActCapability>();
        string[] pluginIds = ["triggernometry", "postnamazu", "silverdasher"];
        var allowedPluginIds = packageInstaller
            .Discover(configuration.DisabledActPluginIds)
            .Where(plugin =>
                plugin.Enabled &&
                bundledPluginManager.IsAllowedToLoad(plugin) &&
                ActPluginPackageInstaller.IsSpecializedPluginId(plugin.Manifest.Id) &&
                !string.Equals(plugin.Manifest.Id, "matcha", StringComparison.OrdinalIgnoreCase))
            .Select(plugin => plugin.Manifest.Id)
            .ToArray();
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
            allowedPluginIds);
    }

    private HostPermissionSnapshot BuildMatchaHostPermissionSnapshot()
    {
        var snapshot = CreateMatchaHostPermissionSnapshot();
        Volatile.Write(
            ref matchaEventsEnabled,
            snapshot.AllowedPluginIds.Contains(
                "matcha",
                StringComparer.OrdinalIgnoreCase) ? 1 : 0);
        lock (permissionSnapshotLock)
        {
            activeMatchaPermissionFingerprint = BuildPermissionFingerprint(snapshot);
        }
        return snapshot;
    }

    private HostPermissionSnapshot CreateMatchaHostPermissionSnapshot()
    {
        var allowedPluginIds = packageInstaller
            .Discover(configuration.DisabledActPluginIds)
            .Where(plugin =>
                plugin.Enabled &&
                bundledPluginManager.IsAllowedToLoad(plugin) &&
                string.Equals(plugin.Manifest.Id, "matcha", StringComparison.OrdinalIgnoreCase))
            .Select(plugin => plugin.Manifest.Id)
            .ToArray();
        var allowed = new Dictionary<string, IReadOnlyList<string>>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["matcha"] = Enum.GetValues<ActCapability>()
                .Where(capability =>
                    configuration.IsActCapabilityAllowed("matcha", capability))
                .Select(capability => capability.ToString())
                .ToArray(),
        };
        return new HostPermissionSnapshot(allowed, allowedPluginIds);
    }

    private HostPermissionSnapshot BuildGenericHostPermissionSnapshot()
    {
        var snapshot = CreateGenericHostPermissionSnapshot();
        lock (permissionSnapshotLock)
        {
            activeGenericPermissionFingerprint = BuildPermissionFingerprint(snapshot);
        }

        return snapshot;
    }

    private HostPermissionSnapshot CreateGenericHostPermissionSnapshot()
    {
        var plugins = packageInstaller
            .Discover(configuration.DisabledActPluginIds)
            .Where(plugin =>
                plugin.Enabled &&
                bundledPluginManager.IsAllowedToLoad(plugin) &&
                !ActPluginPackageInstaller.IsSpecializedPluginId(plugin.Manifest.Id) &&
                configuration.TrustedGenericActPluginIds.Contains(plugin.Manifest.Id))
            .ToArray();
        var allowed = plugins.ToDictionary(
            plugin => plugin.Manifest.Id,
            plugin => (IReadOnlyList<string>)ActPluginPackageInstaller
                .GetRequestedCapabilities(plugin.Manifest)
                .Where(capability => configuration.IsActCapabilityAllowed(
                    plugin.Manifest.Id,
                    capability))
                .Select(static capability => capability.ToString())
                .ToArray(),
            StringComparer.OrdinalIgnoreCase);
        return new HostPermissionSnapshot(
            allowed,
            plugins.Select(plugin => plugin.Manifest.Id).ToArray());
    }

    private static string BuildPermissionFingerprint(HostPermissionSnapshot snapshot)
    {
        var plugins = string.Join(",", snapshot.AllowedPluginIds
            .OrderBy(static id => id, StringComparer.OrdinalIgnoreCase));
        var capabilities = string.Join(
            ";",
            snapshot.AllowedCapabilities
                .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair =>
                    $"{pair.Key}:{string.Join(',', pair.Value.OrderBy(static value => value, StringComparer.OrdinalIgnoreCase))}"));
        return $"{plugins}|{capabilities}";
    }

    private bool IsMatchaConfiguredToRun()
        => packageInstaller
            .Discover(configuration.DisabledActPluginIds)
            .Any(plugin =>
                plugin.Enabled &&
                bundledPluginManager.IsAllowedToLoad(plugin) &&
                string.Equals(plugin.Manifest.Id, "matcha", StringComparison.OrdinalIgnoreCase));

    private bool IsGenericHostConfiguredToRun()
        => CreateGenericHostPermissionSnapshot().AllowedPluginIds.Count > 0;

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
        await hostTopologyLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await lifecycle.DisposeAsync().ConfigureAwait(false);
            await parserEngine.DisposeAsync().ConfigureAwait(false);
            await encounterService.DisposeAsync().ConfigureAwait(false);
            Volatile.Write(ref matchaEventsEnabled, 0);
            actRuntime.SetNetworkSentCaptureEnabled(false);
            await matchaHostSupervisor.DisposeAsync().ConfigureAwait(false);
            await genericHostSupervisor.DisposeAsync().ConfigureAwait(false);
            await hostSupervisor.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            hostTopologyLock.Release();
        }
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
        genericHostSupervisor.PublishLog(timestamp, rawLine, actLine, isImport);
    }

    private void OnZoneChangedForHost(uint territoryId, string zoneName)
    {
        fflogsEstimateService.NotifyTerritoryChanged(
            territoryId,
            zoneNameLocalizer.Localize(territoryId, zoneName));
        hostSupervisor.PublishZone(territoryId, zoneName);
        genericHostSupervisor.PublishZone(territoryId, zoneName);
    }

    private void OnNetworkReceivedForHost(string connection, long epoch, byte[] message)
    {
        _ = hostSupervisor.PublishSilverDasherNetwork(connection, epoch, message);
        if (Volatile.Read(ref matchaEventsEnabled) == 1)
        {
            _ = matchaHostSupervisor.PublishMatchaNetworkReceived(
                connection,
                epoch,
                message);
        }
    }

    private void OnNetworkSentForMatchaHost(string connection, long epoch, byte[] message)
    {
        if (Volatile.Read(ref matchaEventsEnabled) == 1)
        {
            _ = matchaHostSupervisor.PublishMatchaNetworkSent(connection, epoch, message);
        }
    }

    private void OnMatchaLogLineRequested(object? sender, HostMatchaLogLine logLine)
    {
        try
        {
            _ = services.Framework.RunOnFrameworkThread(() =>
            {
                if (!actRuntime.InjectExternalPluginLogLine(logLine.Line))
                {
                    logger.Warning(
                        "Matcha produced an overlay log line while the game-side ACT parser was stopped.");
                }
            });
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Could not relay a Matcha log line to OverlayPlugin.");
        }
    }

    private void OnMatchaTtsRequested(object? sender, HostTtsRequest request)
    {
        if (!hostSupervisor.RequestTts(request.Text, "matcha"))
        {
            logger.Warning(
                "Matcha TTS request was not accepted by the shared ACT Host; other extensions remain unaffected.");
        }
    }

    private void OnGenericTtsRequested(object? sender, HostTtsRequest request)
    {
        if (!hostSupervisor.RequestTts(request.Text, request.Source))
        {
            logger.Warning(
                "A generic ACT plugin requested TTS, but the shared Host has no available TTS provider.");
        }
    }

    private void OnMatchaNotificationRequested(
        object? sender,
        HostMatchaNotification notification)
    {
        try
        {
            _ = services.Framework.RunOnFrameworkThread(() =>
            {
                try
                {
                    services.NotificationManager.AddNotification(new()
                    {
                        Title = "抹茶 / Cafe.Matcha",
                        Content = notification.Message,
                    });
                }
                catch (Exception exception)
                {
                    logger.Error(
                        exception,
                        "Could not display the Matcha fallback notification.");
                }
            });
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Could not display the Matcha fallback notification.");
        }
    }

    private void OnEncounterChangedForHost(ActEncounterSnapshot _, bool finished)
    {
        hostSupervisor.PublishEncounter(finished);
        genericHostSupervisor.PublishEncounter(finished);
    }

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
            var snapshot = FfxivEntitySnapshotBuilder.Build(
                objectTable,
                partyList,
                services.ClientState,
                playerState,
                now);
            hostSupervisor.PublishFfxivEntities(snapshot);
            genericHostSupervisor.PublishFfxivEntities(snapshot);
            if (Volatile.Read(ref matchaEventsEnabled) == 1)
            {
                matchaHostSupervisor.PublishFfxivEntities(snapshot);
            }
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

    private void OnSilverDasherNotificationRequested(
        object? sender,
        HostSilverDasherNotification notification)
    {
        try
        {
            _ = services.Framework.RunOnFrameworkThread(() =>
            {
                try
                {
                    services.NotificationManager.AddNotification(new Notification
                    {
                        Title = "银山雀儿 / SilverDasher",
                        Content = string.IsNullOrWhiteSpace(notification.Detail)
                            ? notification.Message
                            : $"{notification.Message}\n{notification.Detail}",
                        Type = NotificationType.Info,
                    });
                }
                catch (Exception exception)
                {
                    logger.Error(
                        exception,
                        "Could not display the SilverDasher game-side notification.");
                }
            });
        }
        catch (Exception exception)
        {
            logger.Error(exception, "Could not display the SilverDasher game-side notification.");
        }
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

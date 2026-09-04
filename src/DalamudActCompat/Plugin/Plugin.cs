using Dalamud.Game.Command;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Interface.Textures;
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
using DalamudActCompat.Infrastructure.Cloud;
using DalamudActCompat.Infrastructure.Ipc;
using DalamudActCompat.Infrastructure.Logging;
using DalamudActCompat.Infrastructure.Processes;
using DalamudActCompat.Infrastructure.Resources;
using DalamudActCompat.Infrastructure.Storage;
using DalamudActCompat.Meter;
using DalamudActCompat.Overlay;
using DalamudActCompat.Parser;
using DalamudActCompat.Quality;
using DalamudActCompat.Protocol;
using DalamudActCompat.UI;
using System.Threading.Channels;
using System.Numerics;
using NativeGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using NativeFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;

namespace DalamudActCompat.Plugin;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/actcompat";
    private const uint MatchaWorldChangedIconId = 61835;
    private const uint MatchaDutyEnteredIconId = 61832;
    private static readonly TimeSpan CloudAutoSyncRetryDelay = TimeSpan.FromMinutes(10);

    internal static bool IsDutyPartyWiped(
        bool boundByDuty,
        bool localPlayerUnconscious,
        bool anyPartyMemberAlive)
        => boundByDuty && localPlayerUnconscious && !anyPartyMemberAlive;

    private readonly PluginServices services;
    private readonly string clientLanguageName;
    private readonly byte? nativeClientLanguageCode;
    private readonly WindowSystem windowSystem = new("DalamudActCompat");
    private readonly PluginConfiguration configuration;
    private readonly PluginPaths paths;
    private readonly PluginLogger logger;
    private readonly ResourcePackManager resourcePackManager;
    private readonly UiText text;
    private readonly EncounterStateStore stateStore;
    private readonly IParserEngine parserEngine;
    private readonly IinactAdapter parserAdapter;
    private readonly SelfHostedActRuntime actRuntime;
    private readonly EncounterService encounterService;
    private readonly PluginLifecycle lifecycle;
    private readonly MeterWindow meterWindow;
    private readonly HorizontalMeterWindow horizontalMeterWindow;
    private readonly RoleSplitMeterWindow roleSplitDamageWindow;
    private readonly RoleSplitMeterWindow roleSplitHealerWindow;
    private readonly MeterStyleEditorWindow meterStyleEditorWindow;
    private readonly SimplifiedHomeWindow simplifiedHomeWindow;
    private readonly FflogsEstimateService fflogsEstimateService;
    private readonly ZoneNameLocalizer zoneNameLocalizer;
    private readonly EncounterModeStateProvider encounterModeStateProvider;
    private readonly EncounterWindow encounterWindow;
    private readonly ControlCenterWindow settingsWindow;
    private readonly HelpWindow helpWindow;
    private readonly SettingsWindow advancedSettingsWindow;
    private readonly StatusWindow statusWindow;
    private readonly LauncherWindow launcherWindow;
    private readonly CoreResourceDownloadWindow coreResourceDownloadWindow;
    private readonly ThirdPartyPluginNoticeWindow thirdPartyPluginNoticeWindow;
    private readonly CloudBanNoticeWindow cloudBanNoticeWindow;
    private readonly FactoryResetService factoryResetService;
    private readonly FactoryResetOperationCoordinator factoryResetOperations;
    private readonly CloudClientService cloudClient;
    private readonly ActPluginPackageInstaller packageInstaller;
    private readonly BundledActPluginManager bundledPluginManager;
    private readonly BundledActPluginUpdateChecker bundledPluginUpdateChecker;
    private readonly CactbotPackageInstaller cactbotInstaller;
    private readonly FileDialogManager fileDialogManager = new();
    private readonly CancellationTokenSource bundledUpdateCancellation = new();
    private readonly SemaphoreSlim bundledUpdateCheckLock = new(1, 1);
    private readonly CancellationTokenSource cactbotOperationCancellation = new();
    private readonly CancellationTokenSource cloudOperationCancellation = new();
    private readonly object cactbotTaskLock = new();
    private readonly object cactbotStatusLock = new();
    private readonly SemaphoreSlim cactbotFileOperationGate = new(1, 1);
    private readonly HashSet<Task> cactbotTasks = [];
    private readonly ActHostSupervisor hostSupervisor;
    private readonly ActHostSupervisor matchaHostSupervisor;
    private readonly ActHostSupervisor genericHostSupervisor;
    private readonly ISharedImmediateTexture matchaWorldChangedIcon;
    private readonly ISharedImmediateTexture matchaDutyEnteredIcon;
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
    private readonly TriggernometryNativeBridgeService triggernometryNativeBridge;
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
    private readonly CancellationTokenSource independentHostStartupCancellation = new();
    private Task independentHostStartupTask = Task.CompletedTask;
    private Task bundledActPluginInitializationTask = Task.CompletedTask;
    private bool bundledActResourcesAvailableAtStartup;
    private bool hostResourcesAvailableAtStartup;
    private readonly object resourcePackOperationLock = new();
    private CancellationTokenSource? bundledActResourceAttemptCancellation;
    private CancellationTokenSource? hostResourceAttemptCancellation;
    private ResourcePackOperationStatus bundledActResourceStatus =
        ResourcePackOperationStatus.Unavailable();
    private ResourcePackOperationStatus hostResourceStatus =
        ResourcePackOperationStatus.Unavailable();
    private readonly object backgroundOperationLock = new();
    private readonly List<Task> backgroundOperations = [];
    private bool backgroundOperationShutdownStarted;
    private DateTimeOffset nextHostEntitySnapshotAt;
    private DateTimeOffset nextHostEntityDeltaAt;
    private DateTimeOffset nextHostEntitySnapshotFailureLogAt;
    private HostFfxivEntitySnapshot? hostEntitySnapshotBaseline;
    private IReadOnlyList<ActPlayerIdentity> playerIdentitySnapshot = [];
    private ActPlayerPose? localPlayerPoseSnapshot;
    private HostPostNamazuHeading? pendingPostNamazuHeading;
    private CactbotOperationStatus cactbotOperationStatus = new(CactbotOperationState.Idle);
    private Task? cactbotShutdownTask;
    private bool cactbotShutdownStarted;
    private int cactbotCancellationDisposed;
    private DateTimeOffset nextForegroundCheckAt;
    private bool htmlOverlaySuppressionApplied;
    private SimplifiedWindowSnapshot? simplifiedWindowSnapshot;
    private readonly object cloudBanLock = new();
    private CloudBanNotice? enforcedCloudBan;
    private Task cloudBanEnforcementTask = Task.CompletedTask;
    private TaskCompletionSource<bool>? cloudBanParserStopCompletion;
    private int cloudBanParserStopPending;
    private int cloudAccessBlocked;
    private int cloudBanLifted;
    private int pluginInitialized;
    private int pluginDisposing;
    private readonly object cloudRuntimeCancellationLock = new();
    private readonly SemaphoreSlim cloudRuntimeTransitionGate = new(1, 1);
    private CancellationTokenSource? cloudRuntimeCancellation;
    // Cached authentication is restored asynchronously. Treat the initial signed-out
    // snapshot as already observed so normal game startup does not look like a logout.
    private int observedCloudAuthenticationState;
    private int cloudRuntimeAuthorized;
    private bool cloudRuntimeInitialized;
    private Task cloudRuntimeTransitionTask = Task.CompletedTask;
    private int bundledAutoUpdateCheckStarted;
    private long cloudAutoSyncDueUtcTicks;
    private int cloudAutoSyncRunning;
    private int cloudStartupSyncRequested;

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
        // Client language is fixed for this plugin lifetime. Capturing the primitive name
        // keeps parser/FFLogs worker callbacks from reading a Dalamud service off-thread.
        clientLanguageName = dataManager.Language.ToString();
        nativeClientLanguageCode = ReadNativeClientLanguageCode(log);
        pictoActOverlay = new PictoActOverlayService(
            gameGui,
            sigScanner,
            gameInteropProvider,
            log,
            objectTable);
        triggernometryNativeBridge = new TriggernometryNativeBridgeService(
            sigScanner,
            gameInteropProvider,
            objectTable,
            log);
        var localDeathWhilePartyContinues = () =>
            condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Unconscious] &&
            condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BoundByDuty] &&
            partyList.Count > 1 &&
            partyList.Any(member => member.CurrentHP > 0);
        var dutyPartyWiped = () => IsDutyPartyWiped(
            condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BoundByDuty],
            condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Unconscious],
            partyList.Any(member => member.CurrentHP > 0));
        encounterModeStateProvider = new EncounterModeStateProvider(
            dataManager,
            clientState,
            condition,
            dutyPartyWiped,
            log);
        configuration = pluginInterface.GetPluginConfig() as PluginConfiguration ?? new PluginConfiguration();
        var configurationChanged = configuration.ApplyMigrations();
        configuration.Meter.SortMode = MeterSortModeOptions.Normalize(
            configuration.Meter.SortMode);
        logger = new PluginLogger(log);
        paths = new PluginPaths(pluginInterface, configuration.ActPluginDirectory);
        paths.EnsureCreated();
        cloudClient = new CloudClientService(paths);
        enforcedCloudBan = cloudClient.ActiveBan;
        var configuredLogDirectory = string.IsNullOrWhiteSpace(configuration.LogDirectory)
            ? paths.CombatLogDirectory
            : configuration.LogDirectory;
        try
        {
            configuredLogDirectory = NormalizeCombatLogDirectory(configuredLogDirectory);
        }
        catch (Exception ex) when (IsCombatLogDirectoryException(ex))
        {
            logger.Warning(
                $"Configured FFLogs upload log directory is invalid; using the default directory. {ex.Message}");
            configuredLogDirectory = paths.CombatLogDirectory;
        }
        if (!string.Equals(configuration.LogDirectory, configuredLogDirectory, StringComparison.Ordinal))
        {
            configuration.LogDirectory = configuredLogDirectory;
            configurationChanged = true;
        }
        if (configurationChanged)
        {
            pluginInterface.SavePluginConfig(configuration);
        }

        var pluginAssemblyDirectory = pluginInterface.AssemblyLocation.Directory!.FullName;
        resourcePackManager = new ResourcePackManager(
            pluginAssemblyDirectory,
            paths.ResourcePackCacheDirectory,
            logger);
        var hasBundledActPluginResources = resourcePackManager.TryResolveAvailableDirectory(
            "act-plugins",
            BundledActPluginManager.DirectoryName,
            out var bundledActPluginDirectory);
        var hasPackagedHostResources = resourcePackManager.TryResolveAvailableDirectory(
            "host",
            "host",
            out var packagedHostDirectory);
        hostResourcesAvailableAtStartup = hasPackagedHostResources;
        bundledActResourceStatus = hasBundledActPluginResources
            ? ResourcePackOperationStatus.Ready()
            : ResourcePackOperationStatus.Downloading();
        hostResourceStatus = hasPackagedHostResources
            ? ResourcePackOperationStatus.Ready()
            : ResourcePackOperationStatus.Downloading();

        packageInstaller = new ActPluginPackageInstaller(paths);
        bundledPluginManager = new BundledActPluginManager(
            typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "unknown",
            packageInstaller,
            configuration);
        if (hasBundledActPluginResources)
        {
            try
            {
                bundledPluginManager.LoadBundle(bundledActPluginDirectory);
            }
            catch (Exception ex)
            {
                // A broken optional bundle must not turn a usable parser/UI into a failed plugin load.
                logger.Error(ex, "Cached bundled ACT plugin resources are invalid; extensions remain unavailable while the pack is repaired in the background.");
                hasBundledActPluginResources = false;
            }
        }
        bundledActResourcesAvailableAtStartup = hasBundledActPluginResources;
        bundledPluginUpdateChecker = new BundledActPluginUpdateChecker(
            paths.BundledPluginUpdateCacheDirectory);
        stateStore = new EncounterStateStore();
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
            BuildHostPermissionSnapshot,
            GetHostGameContext);
        hostSupervisor = new ActHostSupervisor(
            paths.HostDirectory,
            paths.ActPluginDirectory,
            paths.ConfigDirectory,
            hostIpcClient,
            logger,
            () => Volatile.Read(ref silverDasherEventsEnabled) == 1,
            enableMemoryProtection: true,
            packagedHostDirectory: hasPackagedHostResources ? packagedHostDirectory : null);
        hostSupervisor.CommandRequested += OnHostCommandRequested;
        hostSupervisor.PostNamazuHeadingRequested += OnPostNamazuHeadingRequested;
        hostSupervisor.SilverDasherNotificationRequested += OnSilverDasherNotificationRequested;
        var matchaIpcClient = new HostIpcClient(
            stateStore,
            logger,
            BuildMatchaHostPermissionSnapshot,
            GetHostGameContext);
        matchaHostSupervisor = new ActHostSupervisor(
            paths.HostDirectory,
            paths.ActPluginDirectory,
            paths.ConfigDirectory,
            matchaIpcClient,
            logger,
            matchaEventsEnabled: () => Volatile.Read(ref matchaEventsEnabled) == 1,
            packagedHostDirectory: hasPackagedHostResources ? packagedHostDirectory : null);
        matchaHostSupervisor.MatchaNotificationRequested += OnMatchaNotificationRequested;
        matchaHostSupervisor.MatchaLogLineRequested += OnMatchaLogLineRequested;
        matchaHostSupervisor.MatchaTtsRequested += OnMatchaTtsRequested;
        var genericIpcClient = new HostIpcClient(
            stateStore,
            logger,
            BuildGenericHostPermissionSnapshot,
            GetHostGameContext);
        genericHostSupervisor = new ActHostSupervisor(
            paths.HostDirectory,
            paths.ActPluginDirectory,
            paths.ConfigDirectory,
            genericIpcClient,
            logger,
            packagedHostDirectory: hasPackagedHostResources ? packagedHostDirectory : null);
        genericHostSupervisor.MatchaTtsRequested += OnGenericTtsRequested;
        hostCommandWorker = Task.Run(
            () => RunHostCommandBrokerAsync(hostCommandCancellation.Token),
            CancellationToken.None);
        var jsonStore = new JsonFileStore();
        var repository = new EncounterRepository(jsonStore, paths);
        encounterService = new EncounterService(
            repository,
            stateStore,
            configuration,
            logger,
            paths,
            ResolveCombatLogDirectory);
        zoneNameLocalizer = new ZoneNameLocalizer(dataManager, log);
        fflogsEstimateService = new FflogsEstimateService(
            () => configuration.Fflogs,
            paths.FflogsCacheFile,
            logger,
            () => ResolveGameRegionSelection().EffectiveRegion == HostGameRegion.Chinese);
        actRuntime = new SelfHostedActRuntime(
            pluginInterface,
            log,
            dataManager,
            () => ResolveGameRegionSelection().EffectiveRegion == HostGameRegion.Chinese,
            // ACT publishes encounters from worker threads, so resolve the local name from the
            // immutable identity snapshot instead of reading IPlayerState in that callback.
            () => Volatile.Read(ref playerIdentitySnapshot)
                .FirstOrDefault(static identity => identity.IsLocalPlayer)?.Name ?? string.Empty,
            () => Volatile.Read(ref playerIdentitySnapshot),
            () => Volatile.Read(ref localPlayerPoseSnapshot),
            chatGui,
            framework,
            condition,
            encounterModeStateProvider.Read,
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
            () => configuration.EnableFflogsParityRecorder,
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
        parserAdapter = new IinactAdapter(
            actRuntime,
            logger,
            stateStore,
            encounterService,
            ResolveCombatLogDirectory,
            framework,
            encounterModeStateProvider.Read,
            () => configuration.EmbeddedPlugins.FfxivActPluginEnabled,
            () => configuration.EmbeddedPlugins.OverlayPluginEnabled,
            DiscoverRuntimePlugins,
            fflogsEstimateService.CaptureAvailableEstimates);
        parserEngine = new ParserEngine(parserAdapter);
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
        var combatQualitySnapshot = CombatQualitySnapshot.Load(
            Path.Combine(assetDirectory, "Quality", "combat-quality.json"),
            logger);
        var runningStatusIcon = textureProvider.GetFromFile(
            Path.Combine(assetDirectory, "StatusIcons", "CombatRunning.png"));
        var transitionStatusIcon = textureProvider.GetFromFile(
            Path.Combine(assetDirectory, "StatusIcons", "CombatTransition.png"));
        var endedStatusIcon = textureProvider.GetFromFile(
            Path.Combine(assetDirectory, "StatusIcons", "CombatEnded.png"));
        // These game icon IDs are the canonical source of the two user-selected images,
        // avoiding duplicate extracted assets while retaining Dalamud's texture lifecycle.
        matchaWorldChangedIcon = textureProvider.GetFromGameIcon(
            new GameIconLookup(MatchaWorldChangedIconId));
        matchaDutyEnteredIcon = textureProvider.GetFromGameIcon(
            new GameIconLookup(MatchaDutyEnteredIconId));
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
        horizontalMeterWindow = new HorizontalMeterWindow(
            meterService,
            configuration,
            text,
            jobIcons,
            SaveConfiguration)
        {
            IsOpen = configuration.Meter.IsVisible,
        };
        roleSplitDamageWindow = new RoleSplitMeterWindow(
            meterService,
            configuration,
            text,
            meterWindow,
            SaveConfiguration,
            RoleSplitGroup.DamageTank)
        {
            IsOpen = configuration.Meter.IsVisible,
        };
        roleSplitHealerWindow = new RoleSplitMeterWindow(
            meterService,
            configuration,
            text,
            meterWindow,
            SaveConfiguration,
            RoleSplitGroup.Healer)
        {
            IsOpen = configuration.Meter.IsVisible,
        };
        meterStyleEditorWindow = new MeterStyleEditorWindow(
            configuration,
            logoTexture,
            meterWindow,
            horizontalMeterWindow,
            roleSplitDamageWindow,
            roleSplitHealerWindow,
            text,
            SaveConfiguration);
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
            ShouldPromptForBundledPluginPermissions,
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
            ApplyHistoryLimit,
            () => ApplyActPermissionChanges(),
            ResolveGameRegionSelection,
            SetGameRegionMode,
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
            hostSupervisor.IgnoreMemoryProtectionForCurrentSession,
            RestartMatchaHostFromUi,
            StopMatchaHostFromUi,
            RestartGenericHostFromUi,
            StopGenericHostFromUi);
        hostSupervisor.MemoryProtectionChanged += OnHostMemoryProtectionChanged;
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
            combatQualitySnapshot,
            () => stateStore.GetSnapshot().Current,
            logoTexture,
            () => helpWindow.IsOpen = true,
            SaveConfiguration,
            ApplyHistoryLimit,
            () => ApplyActPermissionChanges(),
            ResolveGameRegionSelection,
            SetGameRegionMode,
            SetSimplifiedMode,
            SetHideHtmlOverlaysWhenUnfocused,
            SetMeterVisible,
            OpenMeter,
            meterStyleEditorWindow.Open,
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
            ResolveCombatLogDirectory,
            SelectCombatLogDirectory,
            ResetCombatLogDirectory,
            BuildDiagnosticReport,
            () => packageInstaller.Discover(configuration.DisabledActPluginIds),
            OpenActPluginConfiguration,
            GetCompatibilityExtensionResourceStatus,
            StartCompatibilityExtensionResourceDownload,
            CancelCompatibilityExtensionResourceDownload,
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
            parserEngine.ResetCurrentEncounter,
            name => _ = actRuntime.ApplyOverlayWindowSettings(name),
            new CloudUiBridge(
                () => cloudClient.Snapshot,
                request => StartCloudOperation(token => cloudClient.RegisterAsync(
                    request.Username,
                    request.Password,
                    request.ActivationKey,
                    request.RememberLogin,
                    token)),
                request => StartCloudOperation(token => cloudClient.LoginAsync(
                    request.Username,
                    request.Password,
                    request.RecoveryKey,
                    request.RememberLogin,
                    token)),
                request => StartCloudOperation(token => cloudClient.ResetPasswordAsync(
                    request.Username,
                    request.ResetCode,
                    request.NewPassword,
                    request.RecoveryKey,
                    request.RememberLogin,
                    request.Method,
                    token)),
                () => StartCloudOperation(cloudClient.LogoutAsync),
                () => StartCloudOperation(cloudClient.RefreshBackupsAsync),
                inviteeContact => StartCloudOperation(
                    token => cloudClient.CreateInvitationAsync(inviteeContact, token)),
                StartCloudUpload,
                backupId => StartCloudOperation(
                    token => cloudClient.PreviewRestoreAsync(backupId, token)),
                StartCloudRestore,
                StartCloudRollback));
        coreResourceDownloadWindow = new CoreResourceDownloadWindow(
            text,
            GetHostResourceStatus,
            StartHostResourceDownload,
            CancelHostResourceDownload);
        cloudBanNoticeWindow = new CloudBanNoticeWindow(text, logoTexture);
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
        simplifiedHomeWindow = new SimplifiedHomeWindow(
            configuration,
            logoTexture,
            text,
            SetMeterVisible,
            () => SetSimplifiedMode(false));
        windowSystem.AddWindow(meterWindow);
        windowSystem.AddWindow(horizontalMeterWindow);
        windowSystem.AddWindow(roleSplitDamageWindow);
        windowSystem.AddWindow(roleSplitHealerWindow);
        windowSystem.AddWindow(meterStyleEditorWindow);
        windowSystem.AddWindow(simplifiedHomeWindow);
        windowSystem.AddWindow(encounterWindow);
        windowSystem.AddWindow(settingsWindow);
        windowSystem.AddWindow(helpWindow);
        windowSystem.AddWindow(advancedSettingsWindow);
        windowSystem.AddWindow(statusWindow);
        windowSystem.AddWindow(thirdPartyPluginNoticeWindow);
        windowSystem.AddWindow(coreResourceDownloadWindow);
        windowSystem.AddWindow(cloudBanNoticeWindow);
        windowSystem.AddWindow(launcherWindow);
        // Authentication still gates every functional window, but a cold game start must
        // stay silent until the user explicitly opens DACT or a real logout is observed.
        RestrictWindowsToAuthenticationGate(openGate: false);

        pluginInterface.UiBuilder.Draw += Draw;
        pluginInterface.UiBuilder.OpenConfigUi += OpenConfigUi;
        pluginInterface.UiBuilder.OpenMainUi += OpenMainUi;
        commandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Control Center. Args: on, off, meter, simple [on|off], cactbot, overlay [template], history, logs, status, sample, clear, host, stop, install <dll-or-zip>, factory-reset.",
        });

        lifecycle = new PluginLifecycle(
            parserEngine,
            encounterService,
            paths,
            configuration,
            logger,
            canStartParser: IsDactAccessAllowed);
        cloudClient.BanReceived += OnCloudBanReceived;
        cloudClient.BanLifted += OnCloudBanLifted;
        // Framework updates were subscribed before parser consumers to preserve frame
        // ordering. They may run during construction, so expose UI state only now.
        Volatile.Write(ref pluginInitialized, 1);
        if (enforcedCloudBan is { } startupBan)
        {
            OnCloudBanReceived(startupBan);
            // The marker monitor starts with the cloud client and can confirm an unban
            // while this comparatively large constructor is still wiring its subscribers.
            if (cloudClient.ActiveBan is null)
            {
                OnCloudBanLifted(startupBan);
            }
        }
        else
        {
            StartInitialResourcePreparation();
            StartCloudOperation(cloudClient.InitializeAsync);
        }
    }

    public string Name => "Dalamud ACT Compat";

    public void Dispose()
    {
        Interlocked.Exchange(ref pluginDisposing, 1);
        AbandonPendingCloudBanParserStop();
        bundledUpdateCancellation.Cancel();
        cloudOperationCancellation.Cancel();
        BeginCactbotShutdown();
        BeginBackgroundOperationShutdown();
        fflogsEstimateService.BeginShutdown();
        factoryResetOperations.BeginShutdown();
        lifecycle.BeginShutdown();
        CancelCloudRuntimeSession();
        independentHostStartupCancellation.Cancel();
        bundledPluginUpdateChecker.Dispose();

        // CanUnloadAsync invokes Dispose away from the framework thread. Marshal only the
        // short Dalamud/UI detach phase back before awaiting background component shutdown.
        try
        {
            services.Framework
                .RunOnFrameworkThread(DetachDalamudResources)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex)
        {
            // Critical parser/Host cleanup must still run if Dalamud is already tearing
            // down its framework services during game exit.
            logger.Error(ex, "Dalamud UI/event detach failed during shutdown.");
        }

        // Returning early would let Dalamud tear down the load context around live hooks/tasks.
        DisposeComponentsAsync().GetAwaiter().GetResult();
    }

    private void DetachDalamudResources()
    {
        services.CommandManager.RemoveHandler(CommandName);
        services.PluginInterface.UiBuilder.Draw -= Draw;
        services.PluginInterface.UiBuilder.OpenConfigUi -= OpenConfigUi;
        services.PluginInterface.UiBuilder.OpenMainUi -= OpenMainUi;
        cloudClient.BanReceived -= OnCloudBanReceived;
        cloudClient.BanLifted -= OnCloudBanLifted;
        triggernometryNativeBridge.Dispose();
        pictoActOverlay.Dispose();
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
        hostSupervisor.PostNamazuHeadingRequested -= OnPostNamazuHeadingRequested;
        hostSupervisor.SilverDasherNotificationRequested -= OnSilverDasherNotificationRequested;
        hostSupervisor.MemoryProtectionChanged -= OnHostMemoryProtectionChanged;
        matchaHostSupervisor.MatchaNotificationRequested -= OnMatchaNotificationRequested;
        matchaHostSupervisor.MatchaLogLineRequested -= OnMatchaLogLineRequested;
        matchaHostSupervisor.MatchaTtsRequested -= OnMatchaTtsRequested;
        genericHostSupervisor.MatchaTtsRequested -= OnGenericTtsRequested;
    }

    private void Draw()
    {
        triggernometryNativeBridge.Update(DateTimeOffset.UtcNow);
        if (Volatile.Read(ref cloudAccessBlocked) != 0)
        {
            windowSystem.Draw();
            return;
        }
        if (!cloudClient.Snapshot.IsSignedIn)
        {
            // Authentication can be revoked by a network callback between Framework frames.
            // Close every functional window before this same UI frame is rendered.
            RestrictWindowsToAuthenticationGate(openGate: false);
            windowSystem.Draw();
            return;
        }
        if (!configuration.SimplifiedModeEnabled)
        {
            pictoActOverlay.Draw();
        }
        // The full-screen shield prevents clicks leaking into the game while an HTML overlay
        // is being positioned, but it must not sit above the controls used to finish editing.
        var hasVisibleManagementWindow = settingsWindow.IsOpen ||
                                         advancedSettingsWindow.IsOpen ||
                                         statusWindow.IsOpen ||
                                         helpWindow.IsOpen ||
                                         launcherWindow.IsOpen ||
                                         thirdPartyPluginNoticeWindow.IsOpen ||
                                         coreResourceDownloadWindow.IsOpen ||
                                         cloudBanNoticeWindow.IsOpen ||
                                         encounterWindow.IsOpen ||
                                         simplifiedHomeWindow.IsOpen ||
                                         meterStyleEditorWindow.IsOpen;
        OverlayEditShield.Draw(
            actRuntime.HasVisibleEditingOverlay,
            hasVisibleManagementWindow);
        windowSystem.Draw();
        fileDialogManager.Draw();
    }

    private bool IsDactAccessAllowed()
        => Volatile.Read(ref cloudAccessBlocked) == 0 &&
           Volatile.Read(ref cloudRuntimeAuthorized) != 0;

    private void SynchronizeCloudAuthenticationState()
    {
        var authenticated = cloudClient.Snapshot is { IsSignedIn: true, ActiveBan: null } &&
                            Volatile.Read(ref cloudAccessBlocked) == 0;
        var nextState = authenticated ? 1 : 0;
        if (Interlocked.Exchange(ref observedCloudAuthenticationState, nextState) == nextState)
        {
            return;
        }

        Volatile.Write(ref cloudRuntimeAuthorized, nextState);
        if (authenticated)
        {
            ScheduleCloudStartupSync(DateTimeOffset.UtcNow);
            RestoreWindowsAfterAuthenticationGate();
        }
        else
        {
            CancelCloudRuntimeSession();
            RestrictWindowsToAuthenticationGate(openGate: true);
        }
        cloudRuntimeTransitionTask =
            StartBackgroundOperation(ReconcileCloudRuntimeAsync) ?? Task.CompletedTask;
    }

    private async Task ReconcileCloudRuntimeAsync()
    {
        await cloudRuntimeTransitionGate.WaitAsync(independentHostStartupCancellation.Token)
            .ConfigureAwait(false);
        try
        {
            if (!IsDactAccessAllowed())
            {
                await StopDactComponentsAsync("Cloud authentication gate").ConfigureAwait(false);
                return;
            }

            await StartAuthenticatedDactRuntimeAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (independentHostStartupCancellation.IsCancellationRequested)
        {
            // Plugin shutdown owns the final component stop and disposal sequence.
        }
        finally
        {
            cloudRuntimeTransitionGate.Release();
        }
    }

    private async Task StartAuthenticatedDactRuntimeAsync()
    {
        var sessionToken = BeginCloudRuntimeSession();
        var firstStart = !cloudRuntimeInitialized;
        if (firstStart)
        {
            cloudRuntimeInitialized = true;
            lifecycle.Start();
            StartBundledCactbotInitialization();
        }

        await lifecycle.WaitForStartupAsync(sessionToken).ConfigureAwait(false);
        if (!firstStart && configuration.EnableParsing && configuration.AutoStartParser)
        {
            await parserEngine.StartAsync(sessionToken).ConfigureAwait(false);
        }
        await Task.WhenAll(
                bundledActPluginInitializationTask.WaitAsync(sessionToken),
                independentHostStartupTask.WaitAsync(sessionToken))
            .ConfigureAwait(false);
        if (GetHostResourceStatus().State == ResourcePackOperationState.Ready)
        {
            await StartIndependentHostAsync(sessionToken).ConfigureAwait(false);
        }
    }

    private void StartInitialResourcePreparation()
    {
        // Resource acquisition is intentionally outside the account gate. These tasks only
        // prepare verified files; authenticated runtime startup is owned separately above.
        var bundledAttempt = CancellationTokenSource.CreateLinkedTokenSource(
            bundledUpdateCancellation.Token);
        bundledActResourceAttemptCancellation = bundledAttempt;
        bundledActPluginInitializationTask = Task.Run(
            () => InitializeBundledActPluginResourcesAsync(
                bundledActResourcesAvailableAtStartup,
                bundledAttempt,
                bundledAttempt.Token),
            CancellationToken.None);

        var hostAttempt = CancellationTokenSource.CreateLinkedTokenSource(
            independentHostStartupCancellation.Token);
        hostResourceAttemptCancellation = hostAttempt;
        independentHostStartupTask = Task.Run(
            () => InitializeHostResourcesAsync(
                hostResourcesAvailableAtStartup,
                hostAttempt,
                hostAttempt.Token),
            CancellationToken.None);
    }

    private CancellationToken BeginCloudRuntimeSession()
    {
        lock (cloudRuntimeCancellationLock)
        {
            cloudRuntimeCancellation?.Dispose();
            cloudRuntimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                independentHostStartupCancellation.Token);
            return cloudRuntimeCancellation.Token;
        }
    }

    private void CancelCloudRuntimeSession()
    {
        lock (cloudRuntimeCancellationLock)
        {
            cloudRuntimeCancellation?.Cancel();
        }
    }

    private void RestrictWindowsToAuthenticationGate(bool openGate)
    {
        triggernometryNativeBridge.Clear();
        pictoActOverlay.Clear();
        meterWindow.IsOpen = false;
        horizontalMeterWindow.IsOpen = false;
        roleSplitDamageWindow.IsOpen = false;
        roleSplitHealerWindow.IsOpen = false;
        advancedSettingsWindow.IsOpen = false;
        statusWindow.IsOpen = false;
        helpWindow.IsOpen = false;
        launcherWindow.IsOpen = false;
        thirdPartyPluginNoticeWindow.IsOpen = false;
        encounterWindow.IsOpen = false;
        meterStyleEditorWindow.IsOpen = false;
        simplifiedHomeWindow.IsOpen = false;
        if (openGate)
        {
            settingsWindow.LocateAnimated();
        }
    }

    private void RestoreWindowsAfterAuthenticationGate()
    {
        fflogsEstimateService.NotifyTerritoryChanged(
            services.ClientState.TerritoryType,
            zoneNameLocalizer.Localize(services.ClientState.TerritoryType, string.Empty));
        meterWindow.IsOpen = configuration.Meter.IsVisible;
        horizontalMeterWindow.IsOpen = configuration.Meter.IsVisible;
        roleSplitDamageWindow.IsOpen = configuration.Meter.IsVisible;
        roleSplitHealerWindow.IsOpen = configuration.Meter.IsVisible;
        launcherWindow.IsOpen = true;
        thirdPartyPluginNoticeWindow.OpenRequiredAfterPluginUpdateWhenPending();
        TryStartAutomaticBundledPluginUpdateCheck();
        if (configuration.SimplifiedModeEnabled)
        {
            ApplySimplifiedWindowVisibility();
        }
        UpdateHtmlOverlaySuppression(DateTimeOffset.UtcNow, force: true);
    }

    private void TryStartAutomaticBundledPluginUpdateCheck()
    {
        if (configuration.AutoCheckBundledPluginUpdates &&
            GetBundledActResourceStatus().State == ResourcePackOperationState.Ready &&
            Interlocked.Exchange(ref bundledAutoUpdateCheckStarted, 1) == 0)
        {
            StartBundledPluginUpdateCheck(openWindow: false);
        }
    }

    private void OnCloudBanReceived(CloudBanNotice notice)
    {
        lock (cloudBanLock)
        {
            enforcedCloudBan = notice;
            Volatile.Write(ref cloudBanLifted, 0);
            if (Interlocked.Exchange(ref cloudAccessBlocked, 1) == 0)
            {
                // Every startup/restart source is made permanently inert for this plugin
                // lifetime before the asynchronous process stops begin.
                Volatile.Write(ref cloudRuntimeAuthorized, 0);
                Volatile.Write(ref observedCloudAuthenticationState, 0);
                CancelCloudRuntimeSession();
                bundledUpdateCancellation.Cancel();
                cloudOperationCancellation.Cancel();
                independentHostStartupCancellation.Cancel();
                BeginCactbotShutdown();
                BeginBackgroundOperationShutdown();
                fflogsEstimateService.BeginShutdown();
                factoryResetOperations.BeginShutdown();
                lifecycle.BeginShutdown();
                hostCommandCancellation.Cancel();
                Volatile.Write(ref silverDasherEventsEnabled, 0);
                Volatile.Write(ref matchaEventsEnabled, 0);
                actRuntime.SetNetworkSentCaptureEnabled(false);
                cloudBanEnforcementTask = Task.Run(StopDactForCloudBanAsync, CancellationToken.None);
            }
        }

        _ = services.Framework.RunOnFrameworkThread(() =>
        {
            triggernometryNativeBridge.Clear();
            pictoActOverlay.Clear();
            meterWindow.IsOpen = false;
            horizontalMeterWindow.IsOpen = false;
            roleSplitDamageWindow.IsOpen = false;
            roleSplitHealerWindow.IsOpen = false;
            settingsWindow.IsOpen = false;
            advancedSettingsWindow.IsOpen = false;
            statusWindow.IsOpen = false;
            helpWindow.IsOpen = false;
            launcherWindow.IsOpen = false;
            thirdPartyPluginNoticeWindow.IsOpen = false;
            encounterWindow.IsOpen = false;
            meterStyleEditorWindow.IsOpen = false;
            simplifiedHomeWindow.IsOpen = false;
            coreResourceDownloadWindow.IsOpen = false;
            cloudBanNoticeWindow.Show(notice, lifted: false);
            services.NotificationManager.AddNotification(new()
            {
                Title = string.Equals(notice.BanType, "device", StringComparison.Ordinal)
                    ? "DACT 账号与关联机器已禁用"
                    : "DACT 已被禁用",
                Content = BuildCloudBanSummary(notice),
                Type = NotificationType.Error,
            });
        });
    }

    private void OnCloudBanLifted(CloudBanNotice? liftedNotice)
    {
        CloudBanNotice? notice = liftedNotice;
        var requiresRestart = Volatile.Read(ref cloudAccessBlocked) != 0;
        lock (cloudBanLock)
        {
            notice ??= enforcedCloudBan;
            if (Interlocked.Exchange(ref cloudBanLifted, 1) != 0)
            {
                return;
            }
        }

        _ = services.Framework.RunOnFrameworkThread(() =>
        {
            cloudBanNoticeWindow.Show(
                notice,
                lifted: true,
                requiresRestart: requiresRestart);
            services.NotificationManager.AddNotification(new()
            {
                Title = "DACT 封禁已解除",
                Content = requiresRestart
                    ? "请重启游戏或重载 DACT，然后重新登录以恢复功能。"
                    : "服务器已确认解封，本次登录已经生效。",
            });
        });
    }

    private async Task StopDactForCloudBanAsync()
    {
        var initialParserStop = RequestCloudBanParserStopAsync();
        await StopDactHostsAsync("Cloud ban").ConfigureAwait(false);
        await initialParserStop.ConfigureAwait(false);

        try
        {
            // Cancellation is cooperative. Join every known startup and mutation owner,
            // then stop once more so a task already past its cancellation check cannot
            // resurrect a parser or Host after the first enforcement pass.
            await Task.WhenAll(
                    ShutdownBackgroundOperationsAsync(),
                    ShutdownCactbotOperationsAsync(),
                    factoryResetOperations.WaitForCompletionAsync(),
                    lifecycle.WaitForStartupAsync(CancellationToken.None),
                    bundledActPluginInitializationTask,
                    independentHostStartupTask)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.Error(ex.GetBaseException(), "Cloud ban could not join every in-flight DACT operation cleanly.");
        }

        await RequestCloudBanParserStopAsync().ConfigureAwait(false);
        await StopDactHostsAsync("Cloud ban").ConfigureAwait(false);
    }

    private async Task StopDactComponentsAsync(string reason)
    {
        Volatile.Write(ref silverDasherEventsEnabled, 0);
        Volatile.Write(ref matchaEventsEnabled, 0);
        actRuntime.SetNetworkSentCaptureEnabled(false);
        await StopDactComponentAsync(reason, "parser", parserEngine.StopAsync)
            .ConfigureAwait(false);

        await StopDactHostsAsync(reason).ConfigureAwait(false);
    }

    private async Task StopDactHostsAsync(string reason)
    {
        var lockAcquired = false;
        try
        {
            using var lockTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await hostTopologyLock.WaitAsync(lockTimeout.Token).ConfigureAwait(false);
            lockAcquired = true;
            await StopDactComponentAsync(reason, "generic Host", genericHostSupervisor.StopAsync)
                .ConfigureAwait(false);
            await StopDactComponentAsync(reason, "Matcha Host", matchaHostSupervisor.StopAsync)
                .ConfigureAwait(false);
            await StopDactComponentAsync(reason, "shared Host", hostSupervisor.StopAsync)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.Error(ex, $"{reason} could not acquire the ACT Host topology lock.");
        }
        finally
        {
            if (lockAcquired)
            {
                hostTopologyLock.Release();
            }
        }
    }

    private Task RequestCloudBanParserStopAsync()
    {
        lock (cloudBanLock)
        {
            if (Volatile.Read(ref pluginDisposing) != 0)
            {
                return Task.CompletedTask;
            }

            var completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            cloudBanParserStopCompletion = completion;
            Volatile.Write(ref cloudBanParserStopPending, 1);
            return completion.Task;
        }
    }

    private void TryStopParserForCloudBanOnFrameworkThread()
    {
        if (Volatile.Read(ref cloudBanParserStopPending) == 0)
        {
            return;
        }

        TaskCompletionSource<bool>? completion;
        lock (cloudBanLock)
        {
            completion = cloudBanParserStopCompletion;
        }
        if (completion is null)
        {
            return;
        }

        try
        {
            if (!parserAdapter.TryStopForAccessRevocation())
            {
                return;
            }

            Interlocked.Exchange(ref cloudBanParserStopPending, 0);
            completion.TrySetResult(true);
            logger.Information("Cloud ban stopped the in-process parser on the framework thread.");
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref cloudBanParserStopPending, 0);
            completion.TrySetResult(false);
            logger.Error(ex, "Cloud ban could not stop the in-process parser safely.");
        }
    }

    private void AbandonPendingCloudBanParserStop()
    {
        TaskCompletionSource<bool>? completion;
        lock (cloudBanLock)
        {
            Interlocked.Exchange(ref cloudBanParserStopPending, 0);
            completion = cloudBanParserStopCompletion;
            cloudBanParserStopCompletion = null;
        }

        // Normal plugin disposal already owns parser teardown. Releasing this waiter avoids
        // deadlocking unload while the framework update callback is being detached.
        completion?.TrySetResult(false);
    }

    private async Task StopDactComponentAsync(
        string reason,
        string component,
        Func<CancellationToken, Task> stop)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await stop(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Continue through every component so one broken extension cannot keep the
            // parser or another managed Host alive after access has been revoked.
            logger.Error(ex, $"{reason} could not stop {component} cleanly.");
        }
    }

    private static string BuildCloudBanSummary(CloudBanNotice notice)
        => string.IsNullOrWhiteSpace(notice.BanReason)
            ? $"封禁时间：{notice.BannedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}"
            : $"封禁时间：{notice.BannedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}；原因：{notice.BanReason}";

    private void OpenConfigUi()
    {
        if (Volatile.Read(ref cloudAccessBlocked) != 0)
        {
            return;
        }
        if (!cloudClient.Snapshot.IsSignedIn)
        {
            settingsWindow.LocateAnimated();
            return;
        }
        if (GetHostResourceStatus().State == ResourcePackOperationState.Unavailable)
        {
            coreResourceDownloadWindow.Open();
        }

        if (configuration.SimplifiedModeEnabled)
        {
            simplifiedHomeWindow.LocateOnNextDraw();
            return;
        }

        settingsWindow.LocateAnimated();
    }

    private void OpenMainUi()
        => OpenConfigUi();

    private void OpenMeter()
    {
        switch (configuration.Meter.ActiveWindowKind)
        {
            case MeterWindowKind.Horizontal:
                horizontalMeterWindow.LocateOnNextDraw();
                break;
            case MeterWindowKind.RoleSplit:
                roleSplitDamageWindow.LocateOnNextDraw();
                roleSplitHealerWindow.LocateOnNextDraw();
                break;
            default:
                meterWindow.LocateOnNextDraw();
                break;
        }
        SetMeterVisible(true);
    }

    private void ToggleMeter()
    {
        var visible = !configuration.Meter.IsVisible;
        // The launcher shortcut is a visibility toggle, so reopening must preserve ImGui's
        // saved position; recentering is reserved for the explicit window-recovery entry point.
        SetMeterVisible(visible);
    }

    private void SetMeterVisible(bool visible)
    {
        configuration.Meter.IsVisible = visible;
        meterWindow.IsOpen = visible;
        horizontalMeterWindow.IsOpen = visible;
        roleSplitDamageWindow.IsOpen = visible;
        roleSplitHealerWindow.IsOpen = visible;
        SaveConfiguration();
    }

    private void SetHideHtmlOverlaysWhenUnfocused(bool enabled)
    {
        configuration.HideHtmlOverlaysWhenGameUnfocused = enabled;
        UpdateHtmlOverlaySuppression(DateTimeOffset.UtcNow, force: true);
        SaveConfiguration();
    }

    private void SetSimplifiedMode(bool enabled)
    {
        if (configuration.SimplifiedModeEnabled == enabled)
        {
            return;
        }

        configuration.SimplifiedModeEnabled = enabled;
        if (enabled)
        {
            ApplySimplifiedWindowVisibility();
            triggernometryNativeBridge.Clear();
            pictoActOverlay.Clear();
            Volatile.Write(ref silverDasherEventsEnabled, 0);
            Volatile.Write(ref matchaEventsEnabled, 0);
            actRuntime.SetNetworkSentCaptureEnabled(false);
            StartBackgroundOperation(StopAllManagedHostsForSimplifiedModeAsync);
        }
        else
        {
            RestoreWindowsAfterSimplifiedMode();
            StartBackgroundOperation(() => StartIndependentHostAsync(CancellationToken.None));
        }

        UpdateHtmlOverlaySuppression(DateTimeOffset.UtcNow, force: true);
        SaveConfiguration();
    }

    private void ApplySimplifiedWindowVisibility()
    {
        simplifiedWindowSnapshot ??= new SimplifiedWindowSnapshot(
            settingsWindow.IsOpen,
            advancedSettingsWindow.IsOpen,
            statusWindow.IsOpen,
            helpWindow.IsOpen,
            launcherWindow.IsOpen,
            thirdPartyPluginNoticeWindow.IsOpen,
            encounterWindow.IsOpen,
            meterStyleEditorWindow.IsOpen);
        settingsWindow.IsOpen = false;
        advancedSettingsWindow.IsOpen = false;
        statusWindow.IsOpen = false;
        helpWindow.IsOpen = false;
        launcherWindow.IsOpen = false;
        thirdPartyPluginNoticeWindow.IsOpen = false;
        encounterWindow.IsOpen = false;
        meterStyleEditorWindow.IsOpen = false;
        simplifiedHomeWindow.IsOpen = true;
        meterWindow.IsOpen = configuration.Meter.IsVisible;
        horizontalMeterWindow.IsOpen = configuration.Meter.IsVisible;
        roleSplitDamageWindow.IsOpen = configuration.Meter.IsVisible;
        roleSplitHealerWindow.IsOpen = configuration.Meter.IsVisible;
    }

    private void RestoreWindowsAfterSimplifiedMode()
    {
        simplifiedHomeWindow.IsOpen = false;
        if (simplifiedWindowSnapshot is not { } snapshot)
        {
            launcherWindow.IsOpen = true;
            return;
        }

        settingsWindow.IsOpen = snapshot.Settings;
        advancedSettingsWindow.IsOpen = snapshot.AdvancedSettings;
        statusWindow.IsOpen = snapshot.Status;
        helpWindow.IsOpen = snapshot.Help;
        launcherWindow.IsOpen = snapshot.Launcher;
        thirdPartyPluginNoticeWindow.IsOpen = snapshot.ThirdPartyNotice;
        encounterWindow.IsOpen = snapshot.EncounterHistory;
        meterStyleEditorWindow.IsOpen = snapshot.MeterStyleEditor;
        simplifiedWindowSnapshot = null;
    }

    private async Task StopAllManagedHostsForSimplifiedModeAsync()
    {
        await hostTopologyLock.WaitAsync().ConfigureAwait(false);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await genericHostSupervisor.StopAsync(timeout.Token).ConfigureAwait(false);
            await matchaHostSupervisor.StopAsync(timeout.Token).ConfigureAwait(false);
            await hostSupervisor.StopAsync(timeout.Token).ConfigureAwait(false);
            logger.Information("Simplified mode stopped every DACT-managed ACT plugin host.");
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Simplified mode could not stop every managed ACT plugin host cleanly.");
        }
        finally
        {
            hostTopologyLock.Release();
        }
    }

    private void OnCommand(string command, string arguments)
    {
        if (Volatile.Read(ref cloudAccessBlocked) != 0)
        {
            logger.Warning("DACT command ignored because cloud access is banned.");
            return;
        }
        if (!cloudClient.Snapshot.IsSignedIn)
        {
            settingsWindow.LocateAnimated();
            logger.Warning("DACT command ignored until cloud authentication succeeds.");
            return;
        }
        var trimmedArguments = arguments.Trim();
        var separator = trimmedArguments.IndexOf(' ');
        var verb = (separator < 0 ? trimmedArguments : trimmedArguments[..separator]).ToLowerInvariant();
        var remainder = separator < 0 ? string.Empty : trimmedArguments[(separator + 1)..].Trim();
        if (configuration.SimplifiedModeEnabled &&
            verb is not ("" or "on" or "simple" or "meter" or "clear"))
        {
            // Simplified mode is an operational boundary, not only a visual filter. Keeping
            // non-meter commands inert prevents hidden tools from changing their desired state
            // and unexpectedly reopening when the mode is disabled.
            logger.Warning("Only meter, clear, and simple commands are available in simplified mode.");
            return;
        }
        switch (verb)
        {
            case "on":
                OpenConfigUi();
                break;
            case "":
                if (configuration.SimplifiedModeEnabled)
                {
                    simplifiedHomeWindow.LocateOnNextDraw();
                }
                else
                {
                    settingsWindow.ToggleAnimated();
                }
                break;
            case "off":
                settingsWindow.HideAnimated();
                break;
            case "history":
                encounterWindow.OpenRecent();
                break;
            case "logs":
                encounterWindow.OpenLogFiles();
                break;
            case "status":
                statusWindow.IsOpen = true;
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
                OpenMeter();
                break;
            case "host":
                if (configuration.SimplifiedModeEnabled)
                {
                    logger.Warning("ACT plugin hosts stay stopped while simplified mode is enabled.");
                    break;
                }
                StartBackgroundOperation(async () =>
                {
                    try
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                        if (!IsDactAccessAllowed())
                        {
                            return;
                        }
                        await hostSupervisor.StartAsync(timeout.Token).ConfigureAwait(false);
                        if (!IsDactAccessAllowed())
                        {
                            return;
                        }
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
                StartBackgroundOperation(async () =>
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
                parserEngine.ResetCurrentEncounter();
                break;
            case "factory-reset":
                settingsWindow.LocateAnimated();
                break;
            case "install":
                InstallActPlugin(remainder);
                break;
            case "meter":
                OpenMeter();
                break;
            case "simple":
                SetSimplifiedMode(remainder.ToLowerInvariant() switch
                {
                    "on" => true,
                    "off" => false,
                    _ => !configuration.SimplifiedModeEnabled,
                });
                break;
            default:
                // Unknown macros should show the authoritative command list instead of
                // silently acting as `on`; this also makes the removed `settings` alias inert.
                helpWindow.OpenCommands();
                break;
        }
    }

    private void LoadSampleEncounter()
    {
        stateStore.UpdateCurrent(SampleEncounterFactory.Create(DateTimeOffset.UtcNow));
        logger.Information("Loaded sample encounter snapshot.");
    }

    private void SaveConfiguration()
        => _ = TrySaveConfiguration();

    private bool TrySaveConfiguration()
    {
        try
        {
            services.PluginInterface.SavePluginConfig(configuration);
            return true;
        }
        catch (Exception ex)
        {
            if (Volatile.Read(ref pluginDisposing) == 0)
            {
                cloudClient.ReportExternalFailure(new IOException(
                    $"无法保存当前 DACT 主配置；自动云同步已暂停。请修复写盘问题后重新保存：{ex.GetBaseException().Message}"));
            }
            logger.Error(ex, "Failed to save plugin configuration.");
            return false;
        }
    }

    private bool ApplyHistoryLimit(int requestedLimit)
    {
        var normalizedLimit = Math.Clamp(requestedLimit, 1, 200);
        if (normalizedLimit == configuration.HistoryLimit)
        {
            return true;
        }

        var previousLimit = configuration.HistoryLimit;
        configuration.HistoryLimit = normalizedLimit;
        if (!TrySaveConfiguration())
        {
            configuration.HistoryLimit = previousLimit;
            return false;
        }

        encounterService.QueueRetentionCleanup();
        logger.Information(
            $"Retention limit changed from {previousLimit} to {normalizedLimit}; " +
            "encounter history, encounter snapshots, and Network logs will each keep that many files.");
        return true;
    }

    private ResourcePackOperationStatus GetBundledActResourceStatus()
        => Volatile.Read(ref bundledActResourceStatus);

    private ResourcePackOperationStatus GetHostResourceStatus()
        => Volatile.Read(ref hostResourceStatus);

    private ResourcePackOperationStatus GetCompatibilityExtensionResourceStatus()
    {
        var bundled = GetBundledActResourceStatus();
        return bundled.State == ResourcePackOperationState.Ready
            ? GetHostResourceStatus()
            : bundled;
    }

    private void StartCompatibilityExtensionResourceDownload()
    {
        if (GetBundledActResourceStatus().State != ResourcePackOperationState.Ready)
        {
            StartBundledActResourceDownload();
            return;
        }

        coreResourceDownloadWindow.Open();
    }

    private void CancelCompatibilityExtensionResourceDownload()
    {
        if (GetBundledActResourceStatus().State == ResourcePackOperationState.Downloading)
        {
            CancelBundledActResourceDownload();
        }
        else
        {
            CancelHostResourceDownload();
        }
    }

    private void StartBundledActResourceDownload()
    {
        CancellationTokenSource attempt;
        lock (resourcePackOperationLock)
        {
            if (bundledActResourceStatus.State == ResourcePackOperationState.Downloading)
            {
                return;
            }

            attempt = CancellationTokenSource.CreateLinkedTokenSource(
                bundledUpdateCancellation.Token);
            bundledActResourceAttemptCancellation = attempt;
            Volatile.Write(
                ref bundledActResourceStatus,
                ResourcePackOperationStatus.Downloading());
        }

        StartBackgroundOperation(() => InitializeBundledActPluginResourcesAsync(
            cachedResourcesAvailable: false,
            attempt,
            attempt.Token));
    }

    private void CancelBundledActResourceDownload()
    {
        lock (resourcePackOperationLock)
        {
            bundledActResourceAttemptCancellation?.Cancel();
        }
    }

    private void StartHostResourceDownload()
    {
        CancellationTokenSource attempt;
        lock (resourcePackOperationLock)
        {
            if (hostResourceStatus.State == ResourcePackOperationState.Downloading)
            {
                return;
            }

            attempt = CancellationTokenSource.CreateLinkedTokenSource(
                independentHostStartupCancellation.Token);
            hostResourceAttemptCancellation = attempt;
            Volatile.Write(ref hostResourceStatus, ResourcePackOperationStatus.Downloading());
        }

        StartBackgroundOperation(() => InitializeHostResourcesAsync(
            cachedResourcesAvailable: false,
            attempt,
            attempt.Token));
    }

    private void CancelHostResourceDownload()
    {
        lock (resourcePackOperationLock)
        {
            hostResourceAttemptCancellation?.Cancel();
        }
    }

    private async Task InitializeBundledActPluginResourcesAsync(
        bool cachedResourcesAvailable,
        CancellationTokenSource attempt,
        CancellationToken cancellationToken)
    {
        var progress = cachedResourcesAvailable
            ? null
            : new Progress<ResourcePackDownloadProgress>(value =>
                UpdateBundledActResourceProgress(attempt, value.Percent));
        try
        {
            var directory = await ResolveResourcePackWithTimeoutAsync(
                    "act-plugins",
                    BundledActPluginManager.DirectoryName,
                    cancellationToken,
                    progress)
                .ConfigureAwait(false);
            bundledPluginManager.LoadBundle(directory);
            UpdateBundledActResourceStatus(attempt, ResourcePackOperationStatus.Ready());
            logger.Information(
                $"Bundled ACT plugin resource pack is available under {directory}.");
            await services.Framework.RunOnFrameworkThread(
                    () =>
                    {
                        if (IsDactAccessAllowed())
                        {
                            thirdPartyPluginNoticeWindow.OpenRequiredAfterPluginUpdateWhenPending();
                        }
                    })
                .ConfigureAwait(false);
            if (IsDactAccessAllowed())
            {
                TryStartAutomaticBundledPluginUpdateCheck();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!bundledUpdateCancellation.IsCancellationRequested && !cachedResourcesAvailable)
            {
                UpdateBundledActResourceStatus(
                    attempt,
                    ResourcePackOperationStatus.Unavailable());
            }
        }
        catch (Exception ex)
        {
            if (!cachedResourcesAvailable)
            {
                UpdateBundledActResourceStatus(
                    attempt,
                    ResourcePackOperationStatus.Unavailable(ex.GetBaseException().Message));
            }
            logger.Warning(
                "Bundled ACT plugin resources are unavailable; DACT remains loaded and " +
                $"only bundled extensions are disabled: {ex.GetBaseException().Message}");
            await TryNotifyResourcePackFailureAsync(
                    "ACT 扩展资源下载失败；DACT 基础解析和界面仍可使用，可稍后重启重试。",
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            CompleteResourcePackAttempt(attempt, bundled: true);
        }
    }

    private async Task InitializeHostResourcesAsync(
        bool cachedResourcesAvailable,
        CancellationTokenSource attempt,
        CancellationToken cancellationToken)
    {
        try
        {
            var progress = cachedResourcesAvailable
                ? null
                : new Progress<ResourcePackDownloadProgress>(value =>
                    UpdateHostResourceProgress(attempt, value.Percent));
            var directory = await ResolveResourcePackWithTimeoutAsync(
                    "host",
                    "host",
                    cancellationToken,
                    progress)
                .ConfigureAwait(false);
            hostSupervisor.SetPackagedHostDirectory(directory);
            matchaHostSupervisor.SetPackagedHostDirectory(directory);
            genericHostSupervisor.SetPackagedHostDirectory(directory);
            UpdateHostResourceStatus(attempt, ResourcePackOperationStatus.Ready());
            await services.Framework.RunOnFrameworkThread(
                    () => coreResourceDownloadWindow.IsOpen = false)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!independentHostStartupCancellation.IsCancellationRequested &&
                !cachedResourcesAvailable)
            {
                UpdateHostResourceStatus(attempt, ResourcePackOperationStatus.Unavailable());
            }
        }
        catch (Exception ex)
        {
            if (!cachedResourcesAvailable)
            {
                UpdateHostResourceStatus(
                    attempt,
                    ResourcePackOperationStatus.Unavailable(ex.GetBaseException().Message));
                await TryOpenCoreResourceDownloadWindowAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            logger.Warning(
                "Compatibility Host resources are unavailable; DACT remains loaded and " +
                $"traditional ACT extensions stay disabled: {ex.GetBaseException().Message}");
            await TryNotifyResourcePackFailureAsync(
                    "兼容 Host 资源下载失败；DACT 基础解析和界面仍可使用，传统 ACT 扩展暂不可用。",
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            CompleteResourcePackAttempt(attempt, bundled: false);
        }
    }

    private async Task<string> ResolveResourcePackWithTimeoutAsync(
        string packId,
        string localDirectoryName,
        CancellationToken cancellationToken,
        IProgress<ResourcePackDownloadProgress>? progress = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        try
        {
            return await resourcePackManager.ResolveDirectoryAsync(
                    packId,
                    localDirectoryName,
                    timeout.Token,
                    progress)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            timeout.IsCancellationRequested &&
            !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Resource pack '{packId}' did not become available within three minutes.");
        }
    }

    private void UpdateBundledActResourceProgress(
        CancellationTokenSource attempt,
        int percent)
    {
        lock (resourcePackOperationLock)
        {
            if (!ReferenceEquals(bundledActResourceAttemptCancellation, attempt) ||
                bundledActResourceStatus.State != ResourcePackOperationState.Downloading)
            {
                return;
            }

            Volatile.Write(
                ref bundledActResourceStatus,
                ResourcePackOperationStatus.Downloading(percent));
        }
    }

    private void UpdateHostResourceProgress(CancellationTokenSource attempt, int percent)
    {
        lock (resourcePackOperationLock)
        {
            if (!ReferenceEquals(hostResourceAttemptCancellation, attempt) ||
                hostResourceStatus.State != ResourcePackOperationState.Downloading)
            {
                return;
            }

            Volatile.Write(ref hostResourceStatus, ResourcePackOperationStatus.Downloading(percent));
        }
    }

    private void UpdateBundledActResourceStatus(
        CancellationTokenSource attempt,
        ResourcePackOperationStatus status)
    {
        lock (resourcePackOperationLock)
        {
            if (ReferenceEquals(bundledActResourceAttemptCancellation, attempt))
            {
                Volatile.Write(ref bundledActResourceStatus, status);
            }
        }
    }

    private void UpdateHostResourceStatus(
        CancellationTokenSource attempt,
        ResourcePackOperationStatus status)
    {
        lock (resourcePackOperationLock)
        {
            if (ReferenceEquals(hostResourceAttemptCancellation, attempt))
            {
                Volatile.Write(ref hostResourceStatus, status);
            }
        }
    }

    private void CompleteResourcePackAttempt(CancellationTokenSource attempt, bool bundled)
    {
        lock (resourcePackOperationLock)
        {
            if (bundled && ReferenceEquals(bundledActResourceAttemptCancellation, attempt))
            {
                bundledActResourceAttemptCancellation = null;
            }
            else if (!bundled && ReferenceEquals(hostResourceAttemptCancellation, attempt))
            {
                hostResourceAttemptCancellation = null;
            }
        }
        attempt.Dispose();
    }

    private async Task TryOpenCoreResourceDownloadWindowAsync(
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await services.Framework.RunOnFrameworkThread(coreResourceDownloadWindow.Open)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.Warning($"Could not open the core-resource recovery dialog: {ex.Message}");
        }
    }

    private async Task TryNotifyResourcePackFailureAsync(
        string content,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await services.Framework.RunOnFrameworkThread(() =>
                services.NotificationManager.AddNotification(new Notification
                {
                    Title = "ACT 兼容资源不可用",
                    Content = content,
                    Type = NotificationType.Warning,
                })).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // Notification failure is diagnostic-only and must not turn degradation into a crash.
            logger.Warning($"Could not show the resource-pack failure notification: {ex.Message}");
        }
    }

    private void StartBundledCactbotInitialization()
    {
        _ = StartCactbotOperation(
            EnsureBundledCactbotAsync);
    }

    private async Task EnsureBundledCactbotAsync(CancellationToken cancellationToken)
    {
        TrySetCactbotOperationStatus(CactbotOperationState.Checking);
        var gateAcquired = false;
        try
        {
            await cactbotFileOperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateAcquired = true;
            var bundledCactbotDirectory = await resourcePackManager.ResolveDirectoryAsync(
                    "cactbot",
                    BundledCactbotManager.DirectoryName,
                    cancellationToken)
                .ConfigureAwait(false);
            var bundledCactbotManager = new BundledCactbotManager(
                bundledCactbotDirectory,
                cactbotInstaller,
                directoryIsBundleRoot: true);
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

    private Task? StartBackgroundOperation(Func<Task> operation)
    {
        lock (backgroundOperationLock)
        {
            if (backgroundOperationShutdownStarted)
            {
                return null;
            }

            // Retain every task until unload. Removing completed tasks via a continuation
            // would itself create unowned plugin code that could race the ALC teardown.
            var task = Task.Run(async () =>
            {
                // A queued task may not begin executing until after a live ban has closed
                // the public UI; re-check at execution time as well as at registration time.
                if (Volatile.Read(ref cloudAccessBlocked) != 0)
                {
                    return;
                }
                await operation().ConfigureAwait(false);
            }, CancellationToken.None);
            backgroundOperations.Add(task);
            return task;
        }
    }

    private void BeginBackgroundOperationShutdown()
    {
        lock (backgroundOperationLock)
        {
            backgroundOperationShutdownStarted = true;
        }
    }

    private async Task ShutdownBackgroundOperationsAsync()
    {
        Task[] tasks;
        lock (backgroundOperationLock)
        {
            backgroundOperationShutdownStarted = true;
            tasks = backgroundOperations.ToArray();
        }

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.Error(ex.GetBaseException(), "A tracked plugin operation failed during shutdown.");
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
        try
        {
            // Cancellation is cooperative, so unloading must join every tracked operation.
            // Abandoning one here would keep plugin code alive after its ALC is released.
            await Task.WhenAll(tasks).ConfigureAwait(false);
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

    private void StartCloudOperation(Func<CancellationToken, Task> operation)
    {
        StartBackgroundOperation(async () =>
        {
            try
            {
                await operation(cloudOperationCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cloudOperationCancellation.IsCancellationRequested)
            {
                // Plugin unload cancels network work before disposing the HTTP client.
            }
            catch (Exception ex)
            {
                cloudClient.ReportExternalFailure(ex);
                logger.Error(ex, "Cloud account operation failed.");
            }
        });
    }

    private void StartCloudUpload()
        => StartCloudOperation(token => RunCloudFileOperationAsync(
            async operationToken =>
            {
                var uploaded = await cloudClient.UploadAsync(
                        paths.ConfigDirectory,
                        operationToken)
                    .ConfigureAwait(false);
                if (uploaded)
                {
                    Interlocked.Exchange(ref cloudAutoSyncDueUtcTicks, 0);
                }
                return uploaded;
            },
            keepStoppedAfterSuccess: false,
            token));

    private void ScheduleCloudStartupSync(DateTimeOffset now)
    {
        if (Volatile.Read(ref pluginDisposing) != 0 ||
            Interlocked.Exchange(ref cloudStartupSyncRequested, 1) != 0)
        {
            return;
        }

        // One content-deduplicated check per game process captures the previous session's
        // final files without delaying shutdown or reacting to every file-system event.
        Interlocked.Exchange(
            ref cloudAutoSyncDueUtcTicks,
            now.UtcDateTime.Ticks);
    }

    private void TryStartCloudAutoSync(DateTimeOffset now)
    {
        if (!configuration.AutoCloudSyncEnabled)
        {
            return;
        }

        var nowTicks = now.UtcDateTime.Ticks;
        var dueTicks = Volatile.Read(ref cloudAutoSyncDueUtcTicks);
        if (dueTicks == 0 || dueTicks > nowTicks)
        {
            return;
        }
        if (cloudClient.Snapshot.IsBusy ||
            services.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat] ||
            !cloudRuntimeTransitionTask.IsCompletedSuccessfully ||
            Interlocked.CompareExchange(ref cloudAutoSyncRunning, 1, 0) != 0)
        {
            return;
        }

        // Refuse to upload an older on-disk main configuration. A failed save is surfaced
        // in the cloud page and retried later, while DACT keeps its current runtime state.
        if (!TrySaveConfiguration())
        {
            Interlocked.Exchange(
                ref cloudAutoSyncDueUtcTicks,
                now.Add(CloudAutoSyncRetryDelay).UtcDateTime.Ticks);
            Interlocked.Exchange(ref cloudAutoSyncRunning, 0);
            return;
        }
        Interlocked.Exchange(ref cloudAutoSyncDueUtcTicks, 0);
        // The startup check is content-deduplicated. A manual upload remains available when
        // the user needs a fresh snapshot during the same game session.
        StartCloudOperation(async token =>
        {
            try
            {
                await cloudClient.AutoUploadIfChangedAsync(paths.ConfigDirectory, token)
                    .ConfigureAwait(false);
                if (cloudClient.Snapshot is { IsSignedIn: true, StatusIsError: true })
                {
                    Interlocked.Exchange(
                        ref cloudAutoSyncDueUtcTicks,
                        DateTimeOffset.UtcNow.Add(CloudAutoSyncRetryDelay).UtcDateTime.Ticks);
                }
            }
            finally
            {
                Interlocked.Exchange(ref cloudAutoSyncRunning, 0);
            }
        });
    }

    private void StartCloudRestore(string backupId)
        => StartCloudOperation(token => RunCloudFileOperationAsync(
            operationToken => cloudClient.RestoreAsync(backupId, operationToken),
            keepStoppedAfterSuccess: true,
            token));

    private void StartCloudRollback()
        => StartCloudOperation(token => RunCloudFileOperationAsync(
            cloudClient.RollbackAsync,
            keepStoppedAfterSuccess: true,
            token));

    private async Task RunCloudFileOperationAsync(
        Func<CancellationToken, Task<bool>> operation,
        bool keepStoppedAfterSuccess,
        CancellationToken shutdownToken)
    {
        if (!IsDactAccessAllowed())
        {
            throw new InvalidOperationException("请先登录 DACT 账号。");
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        await cloudRuntimeTransitionTask.WaitAsync(timeout.Token).ConfigureAwait(false);
        // Finish all one-shot startup writers before taking the file-operation locks;
        // otherwise an early restore could be overwritten after this method releases them.
        await lifecycle.WaitForStartupAsync(timeout.Token).ConfigureAwait(false);
        await bundledActPluginInitializationTask.WaitAsync(timeout.Token).ConfigureAwait(false);
        await independentHostStartupTask.WaitAsync(timeout.Token).ConfigureAwait(false);
        var cactbotGateAcquired = false;
        var hostTopologyAcquired = false;
        try
        {
            await cactbotFileOperationGate.WaitAsync(timeout.Token).ConfigureAwait(false);
            cactbotGateAcquired = true;
            await hostTopologyLock.WaitAsync(timeout.Token).ConfigureAwait(false);
            hostTopologyAcquired = true;
            var parserWasRunning = parserEngine.Status.State == ParserState.Running;
            var sharedWasRunning = hostSupervisor.Snapshot.State == HostSupervisorState.Running;
            var matchaWasRunning = matchaHostSupervisor.Snapshot.State == HostSupervisorState.Running;
            var genericWasRunning = genericHostSupervisor.Snapshot.State == HostSupervisorState.Running;
            var completed = false;
            var runtimeQuiesceStarted = false;
            var runtimeRecoveryAttempted = false;
            try
            {
                // A consistent account snapshot requires every parser and ACT extension writer
                // to release its files before export or transactional replacement starts.
                if (!TrySaveConfiguration())
                {
                    throw new IOException("无法保存当前 DACT 主配置，云端操作已取消。");
                }
                runtimeQuiesceStarted = true;
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
                if (sharedWasRunning)
                {
                    await hostSupervisor.StopAsync(timeout.Token).ConfigureAwait(false);
                }
                if (parserWasRunning)
                {
                    await parserEngine.StopAsync(timeout.Token).ConfigureAwait(false);
                }
                if (!TrySaveConfiguration())
                {
                    throw new IOException("停止运行组件后仍无法保存 DACT 主配置，云端操作已取消。");
                }

                completed = await operation(timeout.Token).ConfigureAwait(false);
                if (keepStoppedAfterSuccess && completed)
                {
                    try
                    {
                        ApplyRestoredConfigurationToMemory();
                    }
                    catch (Exception applyFailure)
                    {
                        // Plugin unload always saves the live configuration object. If the
                        // restored JSON cannot replace that object, roll the files back now
                        // so unload cannot overwrite a partially accepted cloud restore.
                        using var rollbackTimeout =
                            CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
                        rollbackTimeout.CancelAfter(TimeSpan.FromMinutes(1));
                        var rolledBack = await cloudClient.RollbackAsync(rollbackTimeout.Token)
                            .ConfigureAwait(false);
                        if (!rolledBack)
                        {
                            throw new AggregateException(
                                "Cloud files were restored but could not be loaded or rolled back.",
                                applyFailure);
                        }
                        ApplyRestoredConfigurationToMemory();
                        completed = false;
                        throw new InvalidDataException(
                            "Restored configuration could not be loaded; the original files were recovered.",
                            applyFailure);
                    }
                }
                if (!keepStoppedAfterSuccess || !completed)
                {
                    runtimeRecoveryAttempted = true;
                    await RestoreCloudOperationRuntimeWithFreshDeadlineAsync(
                            parserWasRunning,
                            sharedWasRunning,
                            matchaWasRunning,
                            genericWasRunning,
                            shutdownToken)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception operationFailure)
            {
                if (!completed &&
                    runtimeQuiesceStarted &&
                    !runtimeRecoveryAttempted &&
                    !shutdownToken.IsCancellationRequested)
                {
                    try
                    {
                        await RestoreCloudOperationRuntimeWithFreshDeadlineAsync(
                                parserWasRunning,
                                sharedWasRunning,
                                matchaWasRunning,
                                genericWasRunning,
                                shutdownToken)
                            .ConfigureAwait(false);
                    }
                    catch (Exception recoveryFailure)
                    {
                        throw new AggregateException(
                            "Cloud file operation failed and the previous runtime could not be restored.",
                            operationFailure,
                            recoveryFailure);
                    }
                }
                throw;
            }
        }
        finally
        {
            if (hostTopologyAcquired)
            {
                hostTopologyLock.Release();
            }
            if (cactbotGateAcquired)
            {
                cactbotFileOperationGate.Release();
            }
        }
    }

    private async Task RestoreCloudOperationRuntimeWithFreshDeadlineAsync(
        bool parserWasRunning,
        bool sharedWasRunning,
        bool matchaWasRunning,
        bool genericWasRunning,
        CancellationToken shutdownToken)
    {
        using var recoveryTimeout =
            CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
        // File/network work owns its own deadline. Recovery needs a fresh window so an
        // operation timeout cannot prevent the components it stopped from restarting.
        recoveryTimeout.CancelAfter(TimeSpan.FromMinutes(1));
        await RestoreCloudOperationRuntimeAsync(
                parserWasRunning,
                sharedWasRunning,
                matchaWasRunning,
                genericWasRunning,
                recoveryTimeout.Token)
            .ConfigureAwait(false);
    }

    private async Task RestoreCloudOperationRuntimeAsync(
        bool parserWasRunning,
        bool sharedWasRunning,
        bool matchaWasRunning,
        bool genericWasRunning,
        CancellationToken cancellationToken)
    {
        if (!IsDactAccessAllowed())
        {
            // A ban can arrive while a cloud snapshot owns the topology lock. Its
            // finally path must never restart components after enforcement began.
            return;
        }
        if (sharedWasRunning)
        {
            if (!IsDactAccessAllowed())
            {
                return;
            }
            await hostSupervisor.StartAsync(cancellationToken).ConfigureAwait(false);
            await hostSupervisor.WaitForPluginStartupAsync(cancellationToken).ConfigureAwait(false);
        }
        if (parserWasRunning)
        {
            if (!IsDactAccessAllowed())
            {
                return;
            }
            await parserEngine.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        if (matchaWasRunning && sharedWasRunning)
        {
            await StartMatchaAfterSharedHostAsync(cancellationToken, throwOnFailure: true)
                .ConfigureAwait(false);
        }
        if (genericWasRunning)
        {
            await StartGenericHostAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void ApplyRestoredConfigurationToMemory()
    {
        var configurationRoot = Path.GetDirectoryName(paths.ConfigDirectory)
                                ?? throw new InvalidOperationException(
                                    "DACT configuration directory has no parent directory.");
        var configurationPath = Path.Combine(configurationRoot, "DalamudActCompat.json");
        var restored = Newtonsoft.Json.JsonConvert.DeserializeObject<PluginConfiguration>(
                           File.ReadAllText(configurationPath))
                       ?? throw new InvalidDataException(
                           "Restored DACT configuration JSON is empty.");
        restored.ApplyMigrations();
        configuration.RestoreFrom(restored);
    }

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
                if (hostWasRunning && !shutdownToken.IsCancellationRequested &&
                    IsDactAccessAllowed())
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
                    !shutdownToken.IsCancellationRequested &&
                    IsDactAccessAllowed())
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
        StartBackgroundOperation(async () =>
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
        StartBackgroundOperation(async () =>
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

                    await StartGenericHostAsync(timeout.Token).ConfigureAwait(false);
                    if (!IsDactAccessAllowed())
                    {
                        return;
                    }
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
        StartBackgroundOperation(async () =>
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

    private async Task<BundledPluginInstallOutcome> InstallBundledPluginsAsync(
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
            var installCommitted = false;
            Exception? runtimeRecoveryFailure = null;
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
                            // The packages and disclosure keys are durable at this point. A later
                            // Host fault is a runtime recovery problem, not an installation rollback.
                            installCommitted = true;
                        },
                        hostSupervisor.StartAsync,
                        parserEngine.StartAsync,
                        () => !bundledUpdateCancellation.IsCancellationRequested &&
                              IsDactAccessAllowed(),
                        timeout.Token,
                        TimeSpan.FromSeconds(20))
                    .ConfigureAwait(false);
            }
            catch (Exception recoveryFailure) when (installCommitted)
            {
                runtimeRecoveryFailure = recoveryFailure;
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

            if (runtimeRecoveryFailure is null &&
                hostWasRunning &&
                hostSupervisor.Snapshot.State == HostSupervisorState.Running)
            {
                try
                {
                    await hostSupervisor.WaitForPluginStartupAsync(timeout.Token).ConfigureAwait(false);
                    await StartMatchaAfterSharedHostAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (Exception recoveryFailure)
                {
                    runtimeRecoveryFailure = recoveryFailure;
                }
            }

            if (runtimeRecoveryFailure is not null)
            {
                logger.Warning(
                    $"Bundled ACT plugins were installed and acknowledged, but runtime recovery is pending: {runtimeRecoveryFailure.GetBaseException().Message}");
                TryNotifyBundledPluginInstall(
                    "第三方 DLL 和来源声明已保存；兼容 Host 未完全恢复，将由监督器继续重试。");
                return BundledPluginInstallOutcome.RuntimeRecoveryPending(runtimeRecoveryFailure);
            }

            TryNotifyBundledPluginInstall(
                "第三方 DLL 已按告知版本安装/更新；作者、版本和来源告知已记录。");
            return BundledPluginInstallOutcome.Ready;
        }
        finally
        {
            hostTopologyLock.Release();
        }
    }

    private void TryNotifyBundledPluginInstall(string content)
    {
        try
        {
            services.NotificationManager.AddNotification(new()
            {
                Title = "ACT 兼容",
                Content = content,
            });
        }
        catch (Exception ex)
        {
            // Notification delivery must not reinterpret a durable installation as failed.
            logger.Warning($"Could not show the bundled ACT plugin completion notification: {ex.Message}");
        }
    }

    private void StartBundledPluginUpdateCheck(bool openWindow)
    {
        StartBackgroundOperation(async () =>
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
        var directory = ResolveCombatLogDirectory();
        Directory.CreateDirectory(directory);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(directory)
        {
            UseShellExecute = true,
        });
        return directory;
    }

    private string ResolveCombatLogDirectory()
        => string.IsNullOrWhiteSpace(configuration.LogDirectory)
            ? paths.CombatLogDirectory
            : configuration.LogDirectory;

    private void SelectCombatLogDirectory(Action<bool, string> reportResult)
    {
        fileDialogManager.OpenFolderDialog(
            text.Get("选择 FFLogs 上传日志目录", "Choose FFLogs upload log directory"),
            (success, directory) =>
            {
                if (success && !string.IsNullOrWhiteSpace(directory))
                {
                    ApplyCombatLogDirectory(directory, reportResult);
                }
            });
    }

    private void ResetCombatLogDirectory(Action<bool, string> reportResult)
        => ApplyCombatLogDirectory(paths.CombatLogDirectory, reportResult);

    private void ApplyCombatLogDirectory(string requestedDirectory, Action<bool, string> reportResult)
    {
        if (!TryPrepareCombatLogDirectory(requestedDirectory, out var normalizedDirectory, out var error))
        {
            reportResult(
                false,
                $"{text.Get("目录不可用：", "Directory unavailable: ")}{error}");
            return;
        }

        var previousDirectory = ResolveCombatLogDirectory();
        var pathChanged = !string.Equals(
            previousDirectory,
            normalizedDirectory,
            StringComparison.OrdinalIgnoreCase);
        var parserWasFaulted = parserEngine.Status.State == ParserState.Faulted;
        if (!pathChanged && !parserWasFaulted)
        {
            reportResult(true, text.Get("当前已经使用这个目录。", "This directory is already in use."));
            return;
        }

        if (pathChanged)
        {
            configuration.LogDirectory = normalizedDirectory;
            if (!TrySaveConfiguration())
            {
                configuration.LogDirectory = previousDirectory;
                reportResult(
                    false,
                    text.Get("保存目录失败，仍使用原目录。", "Could not save the directory; the previous directory is still in use."));
                return;
            }

            // Existing logs belong to the user and may still be needed for an upload, so a
            // directory switch changes only future writes and never migrates or removes files.
            logger.Information(
                $"FFLogs upload log directory changed from '{previousDirectory}' to '{normalizedDirectory}'. " +
                "Existing log files were left in place.");
        }

        var parserWasActive = parserEngine.Status.State is
            ParserState.Running or
            ParserState.Initializing or
            ParserState.Faulted;
        if (!parserWasActive)
        {
            reportResult(
                true,
                text.Get(
                    "目录已保存，将在下次启动解析器时生效。",
                    "Directory saved; it will take effect the next time the parser starts."));
            return;
        }

        reportResult(
            true,
            text.Get("目录已保存，正在重启解析器。", "Directory saved; restarting the parser."));
        StartBackgroundOperation(async () =>
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                if (!IsDactAccessAllowed())
                {
                    return;
                }
                await parserEngine.RestartAsync(timeout.Token).ConfigureAwait(false);
                var status = parserEngine.Status;
                reportResult(
                    status.State == ParserState.Running,
                    status.State == ParserState.Running
                        ? text.Get("目录已更改，解析器已重启。", "Directory changed and the parser restarted.")
                        : text.Get(
                            "目录已保存，但解析器没有恢复；请查看上方状态。",
                            "Directory saved, but the parser did not recover; check the status above."));
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Parser restart after changing the FFLogs upload log directory failed.");
                reportResult(
                    false,
                    text.Get(
                        "目录已保存，但解析器重启失败。",
                        "Directory saved, but the parser restart failed."));
            }
        });
    }

    private static bool TryPrepareCombatLogDirectory(
        string directory,
        out string normalizedDirectory,
        out string error)
    {
        normalizedDirectory = string.Empty;
        error = string.Empty;
        try
        {
            normalizedDirectory = NormalizeCombatLogDirectory(directory);
            Directory.CreateDirectory(normalizedDirectory);
            var probePath = Path.Combine(
                normalizedDirectory,
                $".dact-write-probe-{Guid.NewGuid():N}.tmp");
            // Creating a delete-on-close probe verifies the exact permission needed by ACT
            // without risking an existing user file or leaving test data in the log folder.
            using var probe = new FileStream(
                probePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);
            probe.WriteByte(0);
            return true;
        }
        catch (Exception ex) when (IsCombatLogDirectoryException(ex))
        {
            error = ex.Message;
            return false;
        }
    }

    private static string NormalizeCombatLogDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory.Trim()));
    }

    private static bool IsCombatLogDirectoryException(Exception exception)
        => exception is ArgumentException or
            NotSupportedException or
            IOException or
            UnauthorizedAccessException or
            System.Security.SecurityException;

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

    private bool ShouldPromptForBundledPluginPermissions()
        => BundledActPluginCapabilities.FullPermissionConfirmation.Any(entry =>
            entry.Capabilities.Any(capability =>
                !configuration.HasExplicitActCapabilityDecision(
                    entry.PluginId,
                    capability)));

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

    private async Task StartIndependentHostAsync(CancellationToken cancellationToken)
    {
        if (configuration.SimplifiedModeEnabled || !IsDactAccessAllowed())
        {
            logger.Information("ACT plugin Host startup skipped because DACT is not allowed to start services.");
            return;
        }

        var lockAcquired = false;
        try
        {
            await hostTopologyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            lockAcquired = true;
            if (configuration.SimplifiedModeEnabled || !IsDactAccessAllowed())
            {
                return;
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            await hostSupervisor.StartAsync(timeout.Token).ConfigureAwait(false);
            await hostSupervisor.WaitForPluginStartupAsync(timeout.Token).ConfigureAwait(false);
            await StartMatchaAfterSharedHostAsync(timeout.Token).ConfigureAwait(false);
            await StartGenericHostAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.Information("Independent ACT Host startup was cancelled during plugin shutdown.");
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
            if (lockAcquired)
            {
                hostTopologyLock.Release();
            }
        }
    }

    private async Task StartMatchaAfterSharedHostAsync(
        CancellationToken cancellationToken,
        bool throwOnFailure = false)
    {
        if (!IsDactAccessAllowed())
        {
            Volatile.Write(ref matchaEventsEnabled, 0);
            actRuntime.SetNetworkSentCaptureEnabled(false);
            return;
        }
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
            if (!IsDactAccessAllowed())
            {
                Volatile.Write(ref matchaEventsEnabled, 0);
                actRuntime.SetNetworkSentCaptureEnabled(false);
                return;
            }
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
        if (!IsDactAccessAllowed())
        {
            return;
        }
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
        StartBackgroundOperation(async () =>
        {
            await hostTopologyLock.WaitAsync().ConfigureAwait(false);
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                await hostSupervisor.StopAsync(timeout.Token).ConfigureAwait(false);
                if (!IsDactAccessAllowed())
                {
                    return;
                }
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
        StartBackgroundOperation(async () =>
        {
            await hostTopologyLock.WaitAsync().ConfigureAwait(false);
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                Volatile.Write(ref matchaEventsEnabled, 0);
                actRuntime.SetNetworkSentCaptureEnabled(false);
                await matchaHostSupervisor.StopAsync(timeout.Token).ConfigureAwait(false);
                if (!IsDactAccessAllowed())
                {
                    return;
                }
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
        StartBackgroundOperation(async () =>
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
        StartBackgroundOperation(async () =>
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
                        if (IsDactAccessAllowed())
                        {
                            await hostSupervisor.StartAsync(timeout.Token).ConfigureAwait(false);
                        }
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

                if (IsDactAccessAllowed() &&
                    !string.Equals(
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
        StartBackgroundOperation(async () =>
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
        StartBackgroundOperation(async () =>
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
        StartBackgroundOperation(async () =>
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

    private async Task DisposeComponentsAsync()
    {
        hostCommandQueue.Writer.TryComplete();
        await hostCommandCancellation.CancelAsync().ConfigureAwait(false);
        try
        {
            await hostCommandWorker.ConfigureAwait(false);
            hostCommandCancellation.Dispose();
        }
        catch (Exception ex)
        {
            hostCommandCancellation.Dispose();
            logger.Error(ex, "ACT Host command broker failed during shutdown.");
        }

        Task banTask;
        lock (cloudBanLock)
        {
            banTask = cloudBanEnforcementTask;
        }
        await banTask.ConfigureAwait(false);
        await ShutdownBackgroundOperationsAsync().ConfigureAwait(false);
        cloudClient.Dispose();
        cloudOperationCancellation.Dispose();

        // A reset owns configuration files until its rollback/commit finishes. Waiting here
        // keeps those operations inside the plugin lifetime instead of abandoning them.
        await factoryResetOperations.WaitForCompletionAsync().ConfigureAwait(false);
        SaveConfiguration();
        await DisposeRemainingComponentsAsync().ConfigureAwait(false);
    }

    private async Task DisposeRemainingComponentsAsync()
    {
        await ShutdownCactbotOperationsAsync().ConfigureAwait(false);
        await bundledActPluginInitializationTask.ConfigureAwait(false);
        await independentHostStartupTask.ConfigureAwait(false);
        independentHostStartupCancellation.Dispose();
        await hostTopologyLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await lifecycle.DisposeAsync().ConfigureAwait(false);
            await parserEngine.DisposeAsync().ConfigureAwait(false);
            await encounterService.DisposeAsync().ConfigureAwait(false);
            await fflogsEstimateService.DisposeAsync().ConfigureAwait(false);
            Volatile.Write(ref matchaEventsEnabled, 0);
            actRuntime.SetNetworkSentCaptureEnabled(false);
            await matchaHostSupervisor.DisposeAsync().ConfigureAwait(false);
            await genericHostSupervisor.DisposeAsync().ConfigureAwait(false);
            await hostSupervisor.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            resourcePackManager.Dispose();
            hostTopologyLock.Release();
            lock (cloudRuntimeCancellationLock)
            {
                cloudRuntimeCancellation?.Dispose();
                cloudRuntimeCancellation = null;
            }
            cloudRuntimeTransitionGate.Dispose();
        }
    }

    private void OnRawLogLineForHost(
        DateTimeOffset timestamp,
        string rawLine,
        string actLine,
        bool isImport)
    {
        if (!IsDactAccessAllowed())
        {
            return;
        }
        if (!isImport)
        {
            fflogsEstimateService.ObserveLogLine(actLine);
        }
        hostSupervisor.PublishLog(timestamp, rawLine, actLine, isImport);
        genericHostSupervisor.PublishLog(timestamp, rawLine, actLine, isImport);
    }

    private void OnZoneChangedForHost(uint territoryId, string zoneName)
    {
        if (!IsDactAccessAllowed())
        {
            return;
        }
        // Trigger repositories are territory-scoped; delayed or indefinite PictoACT VFX from
        // the old territory can never be valid after a zone transition.
        triggernometryNativeBridge.Clear();
        pictoActOverlay.Clear();
        var localizedZoneName = zoneNameLocalizer.Localize(territoryId, zoneName);
        fflogsEstimateService.NotifyTerritoryChanged(
            territoryId,
            localizedZoneName);
        // ACT exposes the client-language zone name. Trigger packs commonly compare _zone
        // with that localized value, even when the network parser reports an English name.
        hostSupervisor.PublishZone(territoryId, localizedZoneName);
        genericHostSupervisor.PublishZone(territoryId, localizedZoneName);
    }

    private void OnNetworkReceivedForHost(string connection, long epoch, byte[] message)
    {
        if (!IsDactAccessAllowed())
        {
            return;
        }
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
        if (!IsDactAccessAllowed())
        {
            return;
        }
        if (Volatile.Read(ref matchaEventsEnabled) == 1)
        {
            _ = matchaHostSupervisor.PublishMatchaNetworkSent(connection, epoch, message);
        }
    }

    private void OnMatchaLogLineRequested(object? sender, HostMatchaLogLine logLine)
    {
        if (!IsDactAccessAllowed())
        {
            return;
        }
        try
        {
            _ = services.Framework.RunOnFrameworkThread(() =>
            {
                if (!IsDactAccessAllowed())
                {
                    return;
                }
                if (!actRuntime.InjectExternalPluginLogLine(logLine.Line))
                {
                    logger.Warning(
                        "Matcha produced a malformed overlay log line, or the game-side ACT parser was stopped.");
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
        if (!IsDactAccessAllowed())
        {
            return;
        }
        if (!hostSupervisor.RequestTts(request.Text, "matcha"))
        {
            logger.Warning(
                "Matcha TTS request was not accepted by the shared ACT Host; other extensions remain unaffected.");
        }
    }

    private void OnGenericTtsRequested(object? sender, HostTtsRequest request)
    {
        if (!IsDactAccessAllowed())
        {
            return;
        }
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
        if (!IsDactAccessAllowed())
        {
            return;
        }
        try
        {
            _ = services.Framework.RunOnFrameworkThread(() =>
            {
                try
                {
                    if (!IsDactAccessAllowed())
                    {
                        return;
                    }
                    var gameNotification = new Notification
                    {
                        Title = "抹茶 / Cafe.Matcha",
                        Content = notification.Message,
                    };
                    gameNotification.IconTexture = notification.Kind switch
                    {
                        HostMatchaNotificationKind.WorldChanged => matchaWorldChangedIcon,
                        HostMatchaNotificationKind.DutyEntered => matchaDutyEnteredIcon,
                        _ => null,
                    };
                    services.NotificationManager.AddNotification(gameNotification);
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
        if (!IsDactAccessAllowed())
        {
            return;
        }
        hostSupervisor.PublishEncounter(finished);
        genericHostSupervisor.PublishEncounter(finished);
    }

    private void OnFrameworkUpdateForHost(IFramework _)
    {
        if (Volatile.Read(ref pluginInitialized) == 0)
        {
            // The subscription order is intentional, but a construction-time frame must
            // not touch windows that have not yet been assigned.
            return;
        }

        SynchronizeCloudAuthenticationState();
        TryStopParserForCloudBanOnFrameworkThread();
        if (!IsDactAccessAllowed())
        {
            return;
        }
        // This handler is registered before both parser consumers, so they observe one
        // coherent mode snapshot for the entire Framework frame.
        encounterModeStateProvider.Update();
        var now = DateTimeOffset.UtcNow;
        TryStartCloudAutoSync(now);
        UpdateHtmlOverlaySuppression(now);
        ApplyPendingPostNamazuHeading(now);
        // The shared Host carries timing-sensitive legacy actions, so ordinary pressure waits
        // for combat to end instead of interrupting Triggernometry or PostNamazu mid-pull.
        hostSupervisor.SetMemoryProtectionCombatState(
            services.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat]);
        try
        {
            var localPlayer = objectTable.LocalPlayer;
            Volatile.Write(
                ref localPlayerPoseSnapshot,
                localPlayer is null || localPlayer.EntityId == 0
                    ? null
                    : ActPlayerPose.FromDalamud(
                        localPlayer.EntityId,
                        localPlayer.Position.X,
                        localPlayer.Position.Y,
                        localPlayer.Position.Z,
                        localPlayer.Rotation));
        }
        catch (Exception ex)
        {
            Volatile.Write(ref localPlayerPoseSnapshot, null);
            if (now >= nextHostEntitySnapshotFailureLogAt)
            {
                nextHostEntitySnapshotFailureLogAt = now.AddSeconds(10);
                logger.Error(ex, "Game-side local player pose snapshot failed.");
            }
        }

        var fullSnapshotDue = hostEntitySnapshotBaseline is null || now >= nextHostEntitySnapshotAt;
        if (!fullSnapshotDue && now < nextHostEntityDeltaAt)
        {
            return;
        }

        nextHostEntityDeltaAt = now.AddMilliseconds(30);
        if (fullSnapshotDue)
        {
            nextHostEntitySnapshotAt = now.AddMilliseconds(500);
        }

        try
        {
            var snapshot = FfxivEntitySnapshotBuilder.Build(
                objectTable,
                partyList,
                services.ClientState,
                playerState,
                now);

            var baseline = hostEntitySnapshotBaseline;
            if (baseline is null ||
                baseline.TerritoryId != snapshot.TerritoryId ||
                baseline.CurrentPlayerId != snapshot.CurrentPlayerId)
            {
                fullSnapshotDue = true;
                nextHostEntitySnapshotAt = now.AddMilliseconds(500);
            }

            if (fullSnapshotDue)
            {
                // Parser/overlay startup can run on a worker thread, while Dalamud's object table
                // is framework-thread-only. The full fallback also refreshes identity consumers.
                var identities = BuildPlayerIdentities(
                    playerState,
                    partyList,
                    objectTable,
                    services.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Unconscious]);
                Volatile.Write(ref playerIdentitySnapshot, identities);
                hostEntitySnapshotBaseline = snapshot;
                hostSupervisor.PublishFfxivEntities(snapshot);
                genericHostSupervisor.PublishFfxivEntities(snapshot);
                if (Volatile.Read(ref matchaEventsEnabled) == 1)
                {
                    matchaHostSupervisor.PublishFfxivEntities(snapshot);
                }

                return;
            }

            var delta = FfxivEntitySnapshotBuilder.BuildDelta(baseline!, snapshot);
            if (delta.Upserts.Count == 0 && delta.RemovedIds.Count == 0)
            {
                return;
            }

            hostSupervisor.PublishFfxivEntityDelta(delta);
            genericHostSupervisor.PublishFfxivEntityDelta(delta);
            if (Volatile.Read(ref matchaEventsEnabled) == 1)
            {
                matchaHostSupervisor.PublishFfxivEntityDelta(delta);
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

    private void OnHostMemoryProtectionChanged(
        object? sender,
        HostMemoryProtectionEventArgs args)
    {
        if (!IsDactAccessAllowed())
        {
            return;
        }
        if (args.Snapshot.State is HostMemoryProtectionState.Normal or
            HostMemoryProtectionState.Disabled)
        {
            return;
        }

        try
        {
            _ = services.Framework.RunOnFrameworkThread(() =>
            {
                if (!IsDactAccessAllowed())
                {
                    return;
                }
                var snapshot = args.Snapshot;
                if (snapshot.State != HostMemoryProtectionState.Ignored)
                {
                    statusWindow.IsOpen = true;
                }

                var content = snapshot.State switch
                {
                    HostMemoryProtectionState.Monitoring =>
                        $"共享 ACT Host 私有内存已到 {snapshot.PrivateBytes / (1024d * 1024d * 1024d):0.00} GiB，正在确认是否持续增长；不会立即结束进程。",
                    HostMemoryProtectionState.DeferredForCombat =>
                        "共享 ACT Host 已持续超过 3 GiB。当前仍在战斗，自动回收会等到脱战。",
                    HostMemoryProtectionState.EmergencyCountdown =>
                        "共享 ACT Host 内存已进入紧急区间，10 秒后将尝试平滑重启；可在状态窗口选择本次忽略。",
                    HostMemoryProtectionState.Recycling =>
                        "正在平滑重启共享 ACT Host。Triggernometry、鲶鱼精邮差与 FoxTTS 会短暂停止，未执行的延时动作或发送队列可能丢失。",
                    HostMemoryProtectionState.Ignored =>
                        "本次 Host 会话已忽略自动内存回收；请留意系统剩余内存，手动重启 Host 后保护会恢复。",
                    HostMemoryProtectionState.CircuitOpen =>
                        "十分钟内已自动恢复两次，本次已停止共享 ACT Host 且不再自动重启。请查看状态与日志后手动启动。",
                    _ => snapshot.Detail,
                };
                services.NotificationManager.AddNotification(new Notification
                {
                    Title = "共享 ACT Host 内存保护",
                    Content = content,
                    Type = snapshot.State == HostMemoryProtectionState.CircuitOpen
                        ? NotificationType.Error
                        : NotificationType.Warning,
                });
            });
        }
        catch (Exception exception)
        {
            // A UI notification failure must not interfere with the protection state machine.
            logger.Error(exception, "Could not display the shared Host memory protection status.");
        }
    }

    private void OnPostNamazuHeadingRequested(
        object? sender,
        HostPostNamazuHeading heading)
    {
        if (IsDactAccessAllowed())
        {
            Interlocked.Exchange(ref pendingPostNamazuHeading, heading);
        }
    }

    private unsafe void ApplyPendingPostNamazuHeading(DateTimeOffset now)
    {
        var heading = Interlocked.Exchange(ref pendingPostNamazuHeading, null);
        if (heading is null ||
            now - heading.Timestamp > TimeSpan.FromSeconds(1) ||
            !float.IsFinite(heading.Heading) ||
            !configuration.IsActCapabilityAllowed(
                "postnamazu",
                ActCapability.NativeGameMemory))
        {
            return;
        }

        var localPlayer = objectTable.LocalPlayer;
        if (localPlayer is null ||
            localPlayer.Address == 0 ||
            localPlayer.Address != (nint)heading.Address)
        {
            return;
        }

        // The isolated Host supplies the address for legacy callback compatibility, but only the
        // live local-player pointer is writable. This prevents the bridge from becoming a general
        // cross-process memory-write primitive while preserving U6b's short heading updates.
        ((NativeGameObject*)localPlayer.Address)->Rotation =
            MathF.IEEERemainder(heading.Heading, MathF.Tau);
    }

    private void OnHostCommandRequested(object? sender, HostCommandInvocation invocation)
    {
        if (!IsDactAccessAllowed())
        {
            hostSupervisor.ReplyCommand(
                invocation.CorrelationId,
                false,
                "banned",
                "DACT access is banned.");
            return;
        }
        if (configuration.SimplifiedModeEnabled)
        {
            hostSupervisor.ReplyCommand(
                invocation.CorrelationId,
                false,
                "disabled",
                "ACT plugin commands are disabled in simplified mode.");
            return;
        }

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
        if (!IsDactAccessAllowed())
        {
            return;
        }
        try
        {
            _ = services.Framework.RunOnFrameworkThread(() =>
            {
                try
                {
                    if (!IsDactAccessAllowed())
                    {
                        return;
                    }
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
            if (!IsDactAccessAllowed())
            {
                hostSupervisor.ReplyCommand(
                    invocation.CorrelationId,
                    false,
                    "banned",
                    "DACT access is banned.");
                return;
            }
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
                case "postnamazu.hint":
                case "postnamazu.warn":
                case "postnamazu.lockon":
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
                        throw new InvalidDataException("PostNamazu semantic payload is missing.");
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
                        if (!configuration.IsActCapabilityAllowed(
                                "postnamazu",
                                ActCapability.NativeGameMemory))
                        {
                            throw new UnauthorizedAccessException(
                                "PostNamazu native game-memory capability is denied.");
                        }

                        // The Host broker runs in the background, while PictoACT parsing may
                        // resolve game objects and native VFX creation always touches game state.
                        await services.Framework
                            .RunOnFrameworkThread(() =>
                            {
                                // A timed-out callback can remain queued for a later frame;
                                // reject it before it can touch an overlay being disposed.
                                timeout.Token.ThrowIfCancellationRequested();
                                pictoActOverlay.Apply(payload);
                            })
                            .WaitAsync(timeout.Token)
                            .ConfigureAwait(false);
                    }
                    else if (invocation.Request.Command is
                             "postnamazu.hint" or
                             "postnamazu.warn" or
                             "postnamazu.lockon")
                    {
                        if (!configuration.IsActCapabilityAllowed(
                                "postnamazu",
                                ActCapability.NativeGameMemory))
                        {
                            throw new UnauthorizedAccessException(
                                "PostNamazu native game-memory capability is denied.");
                        }

                        await services.Framework
                            .RunOnFrameworkThread(() =>
                            {
                                // A request may time out while waiting for the next game frame.
                                // Never execute its native pointer after the Host has abandoned it.
                                timeout.Token.ThrowIfCancellationRequested();
                                if (invocation.Request.Command == "postnamazu.lockon")
                                {
                                    triggernometryNativeBridge.CreateLockOn(payload);
                                }
                                else
                                {
                                    triggernometryNativeBridge.ShowGimmickHint(
                                        payload,
                                        invocation.Request.Command == "postnamazu.hint");
                                }
                            })
                            .WaitAsync(timeout.Token)
                            .ConfigureAwait(false);
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
        IObjectTable objectTable,
        bool localPlayerDead)
    {
        var identities = new Dictionary<string, ActPlayerIdentity>(StringComparer.OrdinalIgnoreCase);
        var rotations = new Dictionary<uint, float>();
        foreach (var gameObject in objectTable)
        {
            if (gameObject.EntityId != 0)
            {
                rotations[gameObject.EntityId] = gameObject.Rotation;
            }
        }

        var localGameObject = objectTable.LocalPlayer;
        if (playerState.IsLoaded && !string.IsNullOrWhiteSpace(playerState.CharacterName))
        {
            var localPosition = ActCoordinateMapper.FromDalamud(
                localGameObject?.Position.X ?? 0,
                localGameObject?.Position.Y ?? 0,
                localGameObject?.Position.Z ?? 0);
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
                PositionX = localPosition.X,
                PositionY = localPosition.Y,
                PositionZ = localPosition.Z,
                Rotation = localGameObject?.Rotation ?? 0,
            };
            identities[identity.DisplayName] = identity;
        }

        foreach (var (member, partyGroup) in EnumeratePartyMembers(partyList))
        {
            var name = member.Name.TextValue;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var position = ActCoordinateMapper.FromDalamud(
                member.Position.X,
                member.Position.Y,
                member.Position.Z);
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
                PositionX = position.X,
                PositionY = position.Y,
                PositionZ = position.Z,
                Rotation = rotations.GetValueOrDefault(member.EntityId),
                PartyGroup = partyGroup,
            };
            identities[identity.DisplayName] = identity;
        }

        return identities.Values.ToArray();
    }

    private static IEnumerable<(Dalamud.Game.ClientState.Party.IPartyMember Member, int PartyGroup)>
        EnumeratePartyMembers(IPartyList partyList)
    {
        var capacity = partyList.IsAlliance ? 24 : 8;
        for (var index = 0; index < capacity; index++)
        {
            // Dalamud's alliance indexer is the only authoritative 24-player roster. Keeping
            // its natural blocks of eight also gives the meter stable A/B/C grouping metadata.
            var member = partyList[index];
            if (member is not null)
            {
                yield return (member, partyList.IsAlliance ? (index / 8) + 1 : 0);
            }
        }
    }

    private GameRegionSelection ResolveGameRegionSelection()
        => GameRegionResolver.Resolve(
            configuration.GameRegionMode,
            clientLanguageName,
            nativeClientLanguageCode);

    private HostGameContext GetHostGameContext()
        => ResolveGameRegionSelection().ToHostContext();

    private void SetGameRegionMode(GameRegionMode mode)
    {
        if (configuration.GameRegionMode == mode)
        {
            return;
        }

        configuration.GameRegionMode = mode;
        SaveConfiguration();
        var selection = ResolveGameRegionSelection();
        logger.Information(
            $"Game region mode changed: mode={selection.Mode}, detected={selection.DetectedRegion}, " +
            $"effective={selection.EffectiveRegion}, nativeLanguageCode={selection.NativeClientLanguageCode?.ToString() ?? "unknown"}, " +
            $"language={selection.ClientLanguage}.");
        StartBackgroundOperation(() => ApplyGameRegionChangeAsync(selection));
    }

    private async Task ApplyGameRegionChangeAsync(GameRegionSelection requestedSelection)
    {
        if (!IsDactAccessAllowed())
        {
            return;
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var parserWasActive = parserEngine.Status.State is
            ParserState.Running or
            ParserState.Initializing or
            ParserState.Faulted;
        if (parserWasActive)
        {
            if (!IsDactAccessAllowed())
            {
                return;
            }
            await parserEngine.RestartAsync(timeout.Token).ConfigureAwait(false);
        }

        await hostTopologyLock.WaitAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            if (!IsDactAccessAllowed())
            {
                return;
            }
            var sharedWasRunning = hostSupervisor.Snapshot.State == HostSupervisorState.Running;
            var matchaWasRunning = matchaHostSupervisor.Snapshot.State == HostSupervisorState.Running;
            var genericWasRunning = genericHostSupervisor.Snapshot.State == HostSupervisorState.Running;

            // Host startup consumes region context during its handshake, so each active process
            // must reconnect; changing the in-memory repository under loaded ACT plugins is unsafe.
            if (genericWasRunning)
            {
                await genericHostSupervisor.StopAsync(timeout.Token).ConfigureAwait(false);
            }
            if (matchaWasRunning)
            {
                Volatile.Write(ref matchaEventsEnabled, 0);
                actRuntime.SetNetworkSentCaptureEnabled(false);
                await matchaHostSupervisor.StopAsync(timeout.Token).ConfigureAwait(false);
            }
            if (sharedWasRunning)
            {
                await hostSupervisor.StopAsync(timeout.Token).ConfigureAwait(false);
                if (!IsDactAccessAllowed())
                {
                    return;
                }
                await hostSupervisor.StartAsync(timeout.Token).ConfigureAwait(false);
                await hostSupervisor.WaitForPluginStartupAsync(timeout.Token).ConfigureAwait(false);
            }
            if (matchaWasRunning && sharedWasRunning)
            {
                await StartMatchaAfterSharedHostAsync(timeout.Token, throwOnFailure: true)
                    .ConfigureAwait(false);
            }
            if (genericWasRunning)
            {
                await StartGenericHostAsync(timeout.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            hostTopologyLock.Release();
        }

        var appliedSelection = ResolveGameRegionSelection();
        await services.Framework.RunOnFrameworkThread(() =>
            services.NotificationManager.AddNotification(new()
            {
                Title = text.Get("游戏区域已切换", "Game region changed"),
                Content = text.Get(
                    $"已切换为 {FormatGameRegion(appliedSelection.EffectiveRegion)}；解析器和正在运行的扩展 Host 已刷新。",
                    $"Switched to {FormatGameRegion(appliedSelection.EffectiveRegion)}; the parser and active extension Hosts were refreshed."),
            })).ConfigureAwait(false);

        if (requestedSelection.Mode != configuration.GameRegionMode)
        {
            logger.Information(
                "A newer game-region selection superseded this refresh; the latest selection will own the final restart.");
        }
    }

    private string FormatGameRegion(HostGameRegion region)
        => region == HostGameRegion.Chinese
            ? text.Get("国服", "China")
            : text.Get("国际服", "Global");

    private static unsafe byte? ReadNativeClientLanguageCode(IPluginLog log)
    {
        try
        {
            var framework = NativeFramework.Instance();
            return framework == null ? null : framework->ClientLanguage;
        }
        catch (Exception exception)
        {
            // A missing native address must not prevent startup; Auto visibly falls back
            // to Global and the manual selector remains available.
            log.Warning(exception, "The native FFXIV client region could not be read.");
            return null;
        }
    }

    private void UpdateHtmlOverlaySuppression(DateTimeOffset now, bool force = false)
    {
        if (!force && now < nextForegroundCheckAt)
        {
            return;
        }

        nextForegroundCheckAt = now.AddMilliseconds(100);
        var focusSuppressed = configuration.HideHtmlOverlaysWhenGameUnfocused &&
                              !GameForegroundDetector.IsCurrentProcessForeground();
        var shouldSuppress = configuration.SimplifiedModeEnabled || focusSuppressed;
        if (!force && shouldSuppress == htmlOverlaySuppressionApplied)
        {
            return;
        }

        htmlOverlaySuppressionApplied = shouldSuppress;
        actRuntime.SetHtmlOverlaysSuppressed(shouldSuppress);
    }

    private sealed record SimplifiedWindowSnapshot(
        bool Settings,
        bool AdvancedSettings,
        bool Status,
        bool Help,
        bool Launcher,
        bool ThirdPartyNotice,
        bool EncounterHistory,
        bool MeterStyleEditor);
}

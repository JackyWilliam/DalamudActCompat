using Advanced_Combat_Tracker;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIV_ACT_Plugin.Logfile;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;
using DalamudActCompat.ActRuntime.Parity;

namespace DalamudActCompat.ActRuntime;

public sealed class SelfHostedActRuntime : IDisposable
{
    private static readonly TimeSpan CustomOverlayConnectionTimeout = TimeSpan.FromSeconds(8);
    public const string CactbotOverlayName = "Cactbot Raidboss";
    public const string CactbotAlertsOverlayName = "Cactbot Raidboss Alerts only";
    public const string CactbotTimelineOverlayName = "Cactbot Raidboss Timeline only";
    public const string CactbotRadarOverlayName = "Cactbot Radar";
    public const string CactbotCombinedTemplateName =
        "Cactbot Raidboss (Combined Alerts & Timeline)";
    private static readonly IReadOnlySet<string> ManagedCactbotOverlayNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            CactbotOverlayName,
            CactbotAlertsOverlayName,
            CactbotTimelineOverlayName,
            CactbotCombinedTemplateName,
            "Cactbot Configuration",
            "Cactbot DPS Xephero",
            "Cactbot DPS Rdmty",
            "Cactbot Eureka",
            "Cactbot Fisher",
            "Cactbot Jobs",
            "Cactbot OopsyRaidsy",
            "Cactbot PullCounter",
            CactbotRadarOverlayName,
            "Cactbot Test",
        };

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;
    private readonly IDataManager dataManager;
    private readonly Func<bool> useChineseRegion;
    private readonly Func<string> playerName;
    private readonly Func<IReadOnlyList<ActPlayerIdentity>> playerIdentities;
    private readonly Func<ActPlayerPose?> localPlayerPose;
    private readonly IChatGui chatGui;
    private readonly IFramework framework;
    private readonly ICondition condition;
    private readonly Func<EncounterModeSnapshot> encounterModeSnapshot;
    private readonly IGameInteropProvider gameInteropProvider;
    private readonly INotificationManager notificationManager;
    private readonly Func<bool> localDeathWhilePartyContinues;
    private readonly Func<string, HtmlOverlayWindowSettings> getOverlayWindowSettings;
    private readonly Func<IReadOnlyDictionary<string, HtmlOverlayWindowSettings>>
        getOverlayWindowSettingsSnapshot;
    private readonly Action persistOverlaySettings;
    private readonly Func<bool> debugMode;
    private readonly Func<bool> parityDiagnosticsEnabled;
    private readonly CachedDalamudGameStateProvider gameStateProvider = new();
    private readonly object encounterSync = new();
    private readonly object networkCaptureSync = new();
    private readonly object radarReloadSync = new();
    private readonly Dictionary<string, DamageHitCounter> damageHitCounters =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly RaidDpsEstimator raidDpsEstimator;
    private readonly EffectiveDamageLedger effectiveDamageLedger = new();
    private readonly EncounterDurationTracker encounterDurationTracker = new();
    private int effectiveDamageEventCursor;
    private EncounterData? effectiveDamageEncounter;
    private readonly FflogsParityDiagnosticRecorder parityDiagnosticRecorder = new();
    private IINACT.FfxivActPluginWrapper? parser;
    private ActPluginData? parserPluginData;
    private IINACT.Network.ZoneDownHookManager? zoneDownHookManager;
    private RainbowMage.OverlayPlugin.PluginMain? overlay;
    private HtmlOverlayForm? cactbotOverlay;
    private volatile bool htmlOverlaysSuppressed;
    private HtmlOverlayForm? cactbotSettings;
    private readonly ConcurrentDictionary<string, HtmlOverlayForm> htmlOverlays =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, OverlayConnectionAttempt>
        overlayConnectionAttempts = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<ActOverlayTemplate> overlayTemplates = [];
    private IReadOnlyList<OverlayTemplateSource> overlayTemplateSources = [];
    private string? overlayWebSocketUri;
    private FileStream? webViewSessionLock;
    private HttpClient? httpClient;
    private Func<string, bool>? externalTtsDispatcher;
    private Func<string, string, bool>? externalPostNamazuDispatcher;
    private PostNamazuEventSource? externalPostNamazuEventSource;
    private RainbowMage.OverlayPlugin.EventSources.CactbotEventSource? cactbotEventSource;
    private System.Threading.Timer? radarOptionsReloadTimer;
    private readonly List<LoadedActPlugin> customPlugins = [];
    private bool actGlobalsInitialized;
    private EncounterData? activeEncounter;
    private Guid activeEncounterId;
    private EncounterMode activeEncounterMode;
    private uint activeEncounterTerritoryId;
    private int activeEncounterPartyCapacity;
    private readonly Dictionary<string, ActPlayerIdentity> activeEncounterIdentities =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> lastKnownDead = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> observedDeaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> chatDamageTotals = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> chatDamageHitTotals = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> chatCriticalHitTotals = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> chatDirectHitTotals = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> chatCriticalDirectHitTotals = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> chatHighestDamageActions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> chatHighestDamageTotals = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<RecentNetworkDamage> recentNetworkDamage = [];
    private Guid chatEncounterId;
    private EncounterMode chatEncounterMode;
    private uint chatEncounterTerritoryId;
    private DateTimeOffset chatEncounterStart;
    private DateTimeOffset chatLastDamage;
    private DateTimeOffset activeEncounterRelevantStart;
    private DateTimeOffset lastRelevantCombatAction;
    private bool transitionStateDirty;
    private bool chatEncounterDirty;
    private bool chatEncounterPublished;
    private readonly ChineseCombatChatContext chatParser;
    private readonly IReadOnlySet<string> limitBreakActionNames;
    private string chatEnemy = string.Empty;
    private string chatZone = string.Empty;
    private bool activeEncounterPublished;
    private bool activeEncounterNamesLogged;
    private bool networkSentCaptureRequested;
    private bool networkSentSubscribed;
    private int parityDiagnosticFaulted;
    // Parser and overlay startup run on a worker; only the framework callback may refresh this
    // value from ICondition, so startup never touches the game's condition memory directly.
    private volatile bool frameworkInCombat;

    public SelfHostedActRuntime(
        IDalamudPluginInterface pluginInterface,
        IPluginLog log,
        IDataManager dataManager,
        Func<bool> useChineseRegion,
        Func<string> playerName,
        Func<IReadOnlyList<ActPlayerIdentity>> playerIdentities,
        Func<ActPlayerPose?> localPlayerPose,
        IChatGui chatGui,
        IFramework framework,
        ICondition condition,
        Func<EncounterModeSnapshot> encounterModeSnapshot,
        IGameInteropProvider gameInteropProvider,
        ISigScanner sigScanner,
        INotificationManager notificationManager,
        Func<string, uint?> resolveActorId,
        Func<bool> localDeathWhilePartyContinues,
        Func<string, HtmlOverlayWindowSettings> getOverlayWindowSettings,
        Func<IReadOnlyDictionary<string, HtmlOverlayWindowSettings>> getOverlayWindowSettingsSnapshot,
        Action persistOverlaySettings,
        Func<bool> debugMode,
        Func<bool> parityDiagnosticsEnabled,
        Func<string, ActCapability, bool> permissionCheck)
    {
        this.pluginInterface = pluginInterface;
        this.log = log;
        this.dataManager = dataManager;
        this.useChineseRegion = useChineseRegion;
        this.playerName = playerName;
        this.playerIdentities = playerIdentities;
        this.localPlayerPose = localPlayerPose;
        this.chatGui = chatGui;
        this.framework = framework;
        this.condition = condition;
        this.encounterModeSnapshot = encounterModeSnapshot;
        frameworkInCombat = encounterModeSnapshot().InCombat;
        this.gameInteropProvider = gameInteropProvider;
        this.notificationManager = notificationManager;
        this.localDeathWhilePartyContinues = localDeathWhilePartyContinues;
        this.getOverlayWindowSettings = getOverlayWindowSettings;
        this.getOverlayWindowSettingsSnapshot = getOverlayWindowSettingsSnapshot;
        this.persistOverlaySettings = persistOverlaySettings;
        this.debugMode = debugMode;
        this.parityDiagnosticsEnabled = parityDiagnosticsEnabled;
        HashSet<uint> weaponskillActionIds;
        try
        {
            weaponskillActionIds = LoadWeaponskillActionIds(dataManager);
        }
        catch (Exception ex)
        {
            weaponskillActionIds = [];
            log.Warning(ex, "Weaponskill action metadata could not be loaded; contextual guarantees will fail closed.");
        }
        raidDpsEstimator = new RaidDpsEstimator(weaponskillActionIds.Contains);
        HashSet<string> limitBreakActionNames;
        try
        {
            limitBreakActionNames = LoadLimitBreakActionNames(dataManager);
        }
        catch (Exception ex)
        {
            limitBreakActionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            log.Warning(ex, "Localized Limit Break action names could not be loaded.");
        }
        this.limitBreakActionNames = limitBreakActionNames;
        chatParser = new ChineseCombatChatContext(limitBreakActionNames);
        log.Information(
            $"Loaded {limitBreakActionNames.Count} localized Limit Break action names for combat attribution.");
        NativePostNamazuBridge.Configure(
            framework,
            log,
            sigScanner,
            resolveActorId,
            () => frameworkInCombat);
        LegacyResourceCompatibility.Configure(log, notificationManager);
        CompatibilityPermissionBroker.Configure(
            permissionCheck,
            message => log.Information($"ACT permission audit: {message}"));
    }

    public bool IsParserRunning => parser is not null;

    public bool IsOverlayRunning => overlay is not null;

    public void ConfigureExternalPluginBridges(
        Func<string, bool> ttsDispatcher,
        Func<string, string, bool> postNamazuDispatcher)
    {
        ArgumentNullException.ThrowIfNull(ttsDispatcher);
        ArgumentNullException.ThrowIfNull(postNamazuDispatcher);
        Volatile.Write(ref externalTtsDispatcher, ttsDispatcher);
        Volatile.Write(ref externalPostNamazuDispatcher, postNamazuDispatcher);
    }

    public bool HasVisibleEditingOverlay
        => cactbotOverlay?.IsVisibleEditing == true ||
           htmlOverlays.Values.Any(static window => window.IsVisibleEditing);

    public void SetHtmlOverlaysSuppressed(bool suppressed)
    {
        htmlOverlaysSuppressed = suppressed;
        cactbotOverlay?.SetTemporarilyHidden(suppressed);
        cactbotSettings?.SetTemporarilyHidden(suppressed);
        foreach (var window in htmlOverlays.Values)
        {
            window.SetTemporarilyHidden(suppressed);
        }
    }

    public bool ShowCactbotOverlay()
        => ShowHtmlOverlay(CactbotOverlayName);

    public bool ShowCactbotSettings()
    {
        if (cactbotSettings is null)
        {
            return false;
        }

        cactbotSettings.Show();
        return true;
    }

    public bool DispatchTts(string text)
    {
        if (!IsParserRunning || string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        ActGlobals.oFormActMain.TTS(text);
        return true;
    }

    public bool InjectExternalPluginLogLine(string line)
    {
        if (!IsParserRunning ||
            !TryParseExternalPluginLogLine(
                line,
                out var messageType,
                out var timestamp,
                out var payload))
        {
            return false;
        }

        var logOutput = ActGlobals.oFormActMain.FfxivPlugin?._dataCollection?._logOutput;
        if (logOutput is null)
        {
            return false;
        }

        // Re-enter through the native writer so OverlayPlugin still receives the event while
        // upload logs get the same ordering and checksum suffix as parser-generated lines.
        logOutput.WriteLine(messageType, timestamp, payload);
        return true;
    }

    private static bool TryParseExternalPluginLogLine(
        string line,
        out LogMessageType messageType,
        out DateTime timestamp,
        out string payload)
    {
        messageType = default;
        timestamp = default;
        payload = string.Empty;

        var typeSeparator = line.IndexOf('|');
        var timestampSeparator = typeSeparator < 0 ? -1 : line.IndexOf('|', typeSeparator + 1);
        if (typeSeparator <= 0 ||
            timestampSeparator <= typeSeparator + 1 ||
            timestampSeparator == line.Length - 1 ||
            line.IndexOfAny(['\r', '\n']) >= 0 ||
            !int.TryParse(
                line.AsSpan(0, typeSeparator),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var messageTypeValue) ||
            messageTypeValue < 0 ||
            !DateTimeOffset.TryParse(
                line.AsSpan(typeSeparator + 1, timestampSeparator - typeSeparator - 1),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var timestampValue))
        {
            return false;
        }

        messageType = (LogMessageType)messageTypeValue;
        timestamp = timestampValue.LocalDateTime;
        payload = line[(timestampSeparator + 1)..];
        return true;
    }

    public IReadOnlyList<ActOverlayTemplate> OverlayTemplates => overlayTemplates;

    public bool ApplyOverlayWindowSettings(string name)
    {
        name = NormalizeCactbotOverlayName(name);
        if (string.Equals(name, CactbotOverlayName, StringComparison.OrdinalIgnoreCase))
        {
            cactbotOverlay?.ApplySettings();
            return cactbotOverlay is not null;
        }

        if (!htmlOverlays.TryGetValue(name, out var overlayWindow))
        {
            return false;
        }

        overlayWindow.ApplySettings();
        return true;
    }

    public bool ShowHtmlOverlay(string name)
    {
        name = NormalizeCactbotOverlayName(name);
        if (string.Equals(name, CactbotOverlayName, StringComparison.OrdinalIgnoreCase))
        {
            if (cactbotOverlay is null)
            {
                return false;
            }

            cactbotOverlay.SetTemporarilyHidden(htmlOverlaysSuppressed);
            cactbotOverlay.Show();
            CloseConflictingRaidbossWindows(name);
            return true;
        }

        var template = overlayTemplates.FirstOrDefault(
            candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
        if (overlayWebSocketUri is null)
        {
            return false;
        }

        var settings = getOverlayWindowSettings(name);
        Uri pageUri;
        Size clientSize;
        string windowName;
        string userDataDirectory;
        var customOverlay = false;
        var initialConnectionMode = OverlayConnectionMode.Original;
        if (template is not null)
        {
            var source = overlayTemplateSources.First(
                candidate => string.Equals(candidate.Name, template.Name, StringComparison.OrdinalIgnoreCase));
            if (template.IsCactbot)
            {
                if (!TryBuildLocalCactbotOverlayUri(
                        source.Uri,
                        Path.Combine(pluginInterface.ConfigDirectory.FullName, "cactbot"),
                        new Uri(overlayWebSocketUri),
                        out pageUri))
                {
                    log.Warning(
                        $"Cactbot overlay '{name}' is unavailable in the installed local package.");
                    return false;
                }
            }
            else
            {
                pageUri = BuildTemplateUri(source, new Uri(overlayWebSocketUri));
            }
            clientSize = new Size(template.Width, template.Height);
            windowName = template.Name;
            userDataDirectory = Path.Combine(
                pluginInterface.ConfigDirectory.FullName,
                "webview2",
                SanitizePath(template.Name));
        }
        else
        {
            customOverlay = true;
            initialConnectionMode = ResolveInitialConnectionMode(settings);
            if (!TryBuildCustomOverlayUri(
                    settings.SourceUrl,
                    new Uri(overlayWebSocketUri),
                    initialConnectionMode,
                    out pageUri))
            {
                return false;
            }

            clientSize = new Size(900, 500);
            windowName = name;
            userDataDirectory = Path.Combine(
                pluginInterface.ConfigDirectory.FullName,
                "webview2",
                BuildCustomOverlayProfileName(name));
        }
        var created = false;
        if (!htmlOverlays.TryGetValue(windowName, out var window))
        {
            created = true;
            window = new HtmlOverlayForm(
                pageUri,
                userDataDirectory,
                Path.Combine(
                    pluginInterface.AssemblyLocation.Directory!.FullName,
                    "WebView2Loader.dll"),
                windowName,
                true,
                settings,
                clientSize,
                debugMode(),
                log,
                NotifyOverlayBrowserFailure,
                customOverlay
                    ? uri => OnCustomOverlayWebSocketConnected(windowName, uri)
                    : null);
            if (!htmlOverlays.TryAdd(windowName, window))
            {
                window.Dispose();
                window = htmlOverlays[windowName];
            }
        }

        if (customOverlay && !created &&
            settings.ConnectionState is OverlayConnectionState.None or
                OverlayConnectionState.Failed)
        {
            window.Navigate(pageUri);
        }
        window.SetTemporarilyHidden(htmlOverlaysSuppressed);
        window.Show();
        if (customOverlay &&
            (created || settings.ConnectionState is OverlayConnectionState.None or
                OverlayConnectionState.Failed))
        {
            StartCustomOverlayConnectionDetection(
                windowName,
                window,
                new Uri(overlayWebSocketUri),
                initialConnectionMode);
        }
        CloseConflictingRaidbossWindows(windowName);
        return true;
    }

    public bool HideHtmlOverlay(string name)
    {
        name = NormalizeCactbotOverlayName(name);
        var settings = getOverlayWindowSettings(name);
        settings.IsVisible = false;
        if (string.Equals(name, CactbotOverlayName, StringComparison.OrdinalIgnoreCase))
        {
            if (cactbotOverlay is null)
            {
                return false;
            }

            cactbotOverlay.Hide();
            return true;
        }

        if (!htmlOverlays.TryGetValue(name, out var window))
        {
            return overlayTemplates.Any(candidate => string.Equals(
                       candidate.Name,
                       name,
                       StringComparison.OrdinalIgnoreCase)) ||
                   TryNormalizeCustomOverlayUri(settings.SourceUrl, out _);
        }

        window.Hide();
        return true;
    }

    public bool DeleteHtmlOverlay(string name)
    {
        name = NormalizeCactbotOverlayName(name);
        if (string.IsNullOrWhiteSpace(name) || IsCactbotOverlayName(name))
        {
            return false;
        }

        if (overlayConnectionAttempts.TryRemove(name, out var attempt))
        {
            attempt.Cancel();
            attempt.Dispose();
        }

        if (htmlOverlays.TryRemove(name, out var window))
        {
            window.Dispose();
            return true;
        }

        return overlayTemplates.Any(candidate => string.Equals(
                   candidate.Name,
                   name,
                   StringComparison.OrdinalIgnoreCase)) ||
               TryNormalizeCustomOverlayUri(
                   getOverlayWindowSettings(name).SourceUrl,
                   out _);
    }

    public bool ResetCactbotOverlayWindow(string name)
    {
        name = NormalizeCactbotOverlayName(name);
        if (!IsCactbotOverlayName(name))
        {
            return false;
        }

        var settings = getOverlayWindowSettings(name);
        HtmlOverlayForm? window;
        if (string.Equals(name, CactbotOverlayName, StringComparison.OrdinalIgnoreCase))
        {
            window = cactbotOverlay;
        }
        else
        {
            htmlOverlays.TryGetValue(name, out window);
        }

        if (window is null)
        {
            settings.ResetRegistration();
        }
        else
        {
            window.ResetRegistrationAndLayout();
        }
        return true;
    }

    private void CloseConflictingRaidbossWindows(string openingName)
    {
        openingName = NormalizeCactbotOverlayName(openingName);
        if (string.Equals(
                openingName,
                CactbotOverlayName,
                StringComparison.OrdinalIgnoreCase))
        {
            CloseOverlayWindow(CactbotAlertsOverlayName);
            CloseOverlayWindow(CactbotTimelineOverlayName);
            return;
        }

        if (string.Equals(
                openingName,
                CactbotAlertsOverlayName,
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                openingName,
                CactbotTimelineOverlayName,
                StringComparison.OrdinalIgnoreCase))
        {
            CloseOverlayWindow(CactbotOverlayName);
        }
    }

    private void CloseOverlayWindow(string name)
    {
        var settings = getOverlayWindowSettings(name);
        settings.OpenOnStartup = false;
        settings.IsVisible = false;
        if (string.Equals(name, CactbotOverlayName, StringComparison.OrdinalIgnoreCase))
        {
            cactbotOverlay?.Hide();
            return;
        }

        if (htmlOverlays.TryGetValue(name, out var window))
        {
            window.Hide();
        }
    }

    private void NotifyOverlayBrowserFailure(string message)
    {
        try
        {
            _ = framework.RunOnFrameworkThread(() =>
                notificationManager.AddNotification(new Notification
                {
                    Title = "HTML 悬浮窗",
                    Content = message,
                    Type = NotificationType.Error,
                }));
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not display the HTML overlay failure notification.");
        }
    }

    public IReadOnlyList<string> LoadedCustomPluginIds
        => customPlugins.Select(plugin => plugin.Id).ToArray();

    public IReadOnlyList<ActPluginRuntimeStatus> CustomPluginStatuses
        => customPlugins
            .Select(plugin => new ActPluginRuntimeStatus(
                plugin.Id,
                plugin.Status,
                plugin.Stages,
                plugin.Diagnostics))
            .ToArray();

    public bool OpenCustomPluginConfiguration(string id)
    {
        var plugin = customPlugins.FirstOrDefault(
            candidate => string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase));
        if (plugin is null)
        {
            log.Warning($"ACT plugin '{id}' configuration was requested, but the plugin is not loaded.");
            return false;
        }

        plugin.OpenConfiguration();
        log.Information($"Opened ACT plugin '{id}' configuration.");
        return true;
    }

    public event Action<ActEncounterSnapshot, bool>? EncounterChanged;

    public event Action<DateTimeOffset, string, string, bool>? RawLogLineReceived;

    public event Action<uint, string>? ZoneChanged;

    public event Action<string, long, byte[]>? NetworkReceived;

    public event Action<string, long, byte[]>? NetworkSent;

    public void SetNetworkSentCaptureEnabled(bool enabled)
    {
        lock (networkCaptureSync)
        {
            networkSentCaptureRequested = enabled;
            UpdateNetworkSentSubscriptionLocked();
        }
    }

    public IINACT.FfxivActPluginWrapper Parser
        => parser ?? throw new InvalidOperationException("FFXIV_ACT_Plugin is not running.");

    public void StartParser(string logDirectory)
    {
        if (IsParserRunning)
        {
            return;
        }

        Directory.CreateDirectory(logDirectory);
        var sessionStartedAt = DateTime.Now;
        var logfileVersion = typeof(FFXIV_ACT_Plugin.Logfile.ILogOutput).Assembly
            .GetName()
            .Version
            ?? throw new InvalidOperationException(
                "FFXIV_ACT_Plugin.Logfile does not expose an assembly version.");
        var archivedLogPath = NetworkLogSessionRotator.RotateExisting(
            logDirectory,
            logfileVersion,
            sessionStartedAt,
            TimeSpan.FromSeconds(3));
        if (archivedLogPath is not null)
        {
            log.Information(
                $"Archived the previous parser session log as '{archivedLogPath}'.");
        }

        Directory.CreateDirectory(Path.Combine(pluginInterface.ConfigDirectory.FullName, "Config"));
        SetUpstreamLogger();
        ActGlobals.Init();
        actGlobalsInitialized = true;
        ActGlobals.oFormActMain = new FormActMain(log)
        {
            AppDataFolder = pluginInterface.ConfigDirectory,
            LogFilePath = logDirectory,
            WriteLogFile = true,
            InvokeSynchronously = true,
        };
        ActGlobals.oFormActMain.PlayTtsMethod = text =>
        {
            var dispatcher = Volatile.Read(ref externalTtsDispatcher);
            if (dispatcher?.Invoke(text) != true)
            {
                log.Warning("External ACT Host rejected a game-side TTS request.");
            }
        };
        ActGlobals.oFormActMain.AfterCombatAction += OnAfterCombatAction;
        ActGlobals.oFormActMain.AfterCombatEnd += OnAfterCombatEnd;

        var configuration = new IINACT.Configuration
        {
            LogFilePath = logDirectory,
            WriteLogFile = true,
        };
        configuration.Initialize(pluginInterface);
        configuration.PlayerCharacterName = playerName();
        try
        {
            var chineseRegion = useChineseRegion();
            log.Information(
                $"Starting ACT parser with region={(chineseRegion ? "Chinese" : "Global")}, " +
                $"language={dataManager.Language}.");
            IINACT.FfxivActPluginWrapper.ConfigureRegion(dataManager.Language, chineseRegion);
            parser = new IINACT.FfxivActPluginWrapper(
                configuration,
                dataManager.Language,
                chatGui,
                framework,
                condition,
                chineseRegion);
            // Keep the native hook disabled until every parser dependency has loaded.
            // A failed or stalled wrapper construction must not leave a half-started hook.
            zoneDownHookManager = new IINACT.Network.ZoneDownHookManager(
                notificationManager,
                gameInteropProvider);
            // IINACT registers its formatter in the wrapper constructor. Subscribe after it
            // so the external ACT Host receives both the raw pipe line and ACT's legacy line.
            ActGlobals.oFormActMain.BeforeLogLineRead += OnBeforeLogLineRead;
            parser.Subscription.ZoneChanged += OnZoneChangedForHost;
            parser.Subscription.NetworkReceived += OnNetworkReceivedForHost;
            lock (networkCaptureSync)
            {
                UpdateNetworkSentSubscriptionLocked();
            }
            parserPluginData = RegisterSystemPlugin(
                parser.ActPluginInstance,
                "FFXIV_ACT_Plugin.dll");
            framework.Update += OnFrameworkUpdate;
        }
        catch
        {
            lock (networkCaptureSync)
            {
                if (parser is not null && networkSentSubscribed)
                {
                    parser.Subscription.NetworkSent -= OnNetworkSentForHost;
                }
                networkSentSubscribed = false;
            }
            RemovePluginData(parserPluginData);
            parserPluginData = null;
            parser?.Dispose();
            parser = null;
            zoneDownHookManager?.Dispose();
            zoneDownHookManager = null;
            ActGlobals.Dispose();
            actGlobalsInitialized = false;
            throw;
        }
    }

    public void StartOverlay()
    {
        if (IsOverlayRunning)
        {
            return;
        }

        if (!IsParserRunning)
        {
            throw new InvalidOperationException("FFXIV_ACT_Plugin must be running before OverlayPlugin.");
        }

        try
        {
            webViewSessionLock = AcquireWebViewSessionLock(
                Path.Combine(
                    pluginInterface.ConfigDirectory.FullName,
                    "webview2-session.lock"),
                TimeSpan.FromSeconds(8));
            httpClient = new HttpClient();
            var container = new RainbowMage.OverlayPlugin.TinyIoCContainer();
            var overlayLogger = new RainbowMage.OverlayPlugin.Logger(log);
            container.Register(overlayLogger);
            container.Register<RainbowMage.OverlayPlugin.ILogger>(overlayLogger);
            gameStateProvider.Update(
                playerIdentities(),
                localPlayerPose(),
                frameworkInCombat);
            container.Register<RainbowMage.OverlayPlugin.MemoryProcessors.IDalamudGameStateProvider>(
                gameStateProvider);
            container.Register(httpClient);
            container.Register(new FileDialogManager());
            container.Register(pluginInterface);

            overlay = new RainbowMage.OverlayPlugin.PluginMain(
                pluginInterface.AssemblyLocation.Directory!.FullName,
                overlayLogger,
                container);
            container.Register(overlay);
            ActGlobals.oFormActMain.OverlayPluginContainer = container;
            overlay.InitPlugin(pluginInterface.ConfigDirectory.FullName);
            RegisterCactbotRadarReload(container);
            RegisterExternalPostNamazuAdapter(container);
            var server = container.Resolve<RainbowMage.OverlayPlugin.WebSocket.ServerController>();
            if (!server.Running)
            {
                server.Start();
            }

            if (!server.Running)
            {
                throw new InvalidOperationException(
                    "OverlayPlugin WebSocket server could not be started.",
                    server.LastException);
            }

            var webSocketUri = server.GetModernUrl("http://localhost/")
                .Split("OVERLAY_WS=", 2)[1];
            overlayWebSocketUri = webSocketUri;
            overlayTemplateSources = LoadOverlayTemplateConfig().Overlays;
            var cactbotDirectory = Path.Combine(
                pluginInterface.ConfigDirectory.FullName,
                "cactbot");
            var raidbossHtml = Path.Combine(
                cactbotDirectory,
                "ui",
                "raidboss",
                "raidboss.html");
            var availableTemplates = overlayTemplateSources
                .Where(template => !template.Features.Contains("system"))
                .Where(template => !string.Equals(
                    template.Name,
                    CactbotCombinedTemplateName,
                    StringComparison.OrdinalIgnoreCase))
                .Where(template =>
                    !template.Features.Contains("cactbot") ||
                    TryBuildLocalCactbotOverlayUri(
                        template.Uri,
                        cactbotDirectory,
                        new Uri(webSocketUri),
                        out _))
                .Select(template => new ActOverlayTemplate(
                    template.Name,
                    template.Uri,
                    template.SuggestedWidth ?? 900,
                    template.SuggestedHeight ?? 500,
                    template.Features.Contains("cactbot")))
                .ToList();
            if (File.Exists(raidbossHtml))
            {
                availableTemplates.Insert(
                    0,
                    new ActOverlayTemplate(
                        CactbotOverlayName,
                        new Uri(raidbossHtml).AbsoluteUri,
                        900,
                        320,
                        true));
                cactbotOverlay = new HtmlOverlayForm(
                    BuildOverlayUri(new Uri(raidbossHtml), "OVERLAY_WS", webSocketUri),
                    Path.Combine(pluginInterface.ConfigDirectory.FullName, "webview2"),
                    Path.Combine(
                        pluginInterface.AssemblyLocation.Directory!.FullName,
                        "WebView2Loader.dll"),
                    "Cactbot Raidboss",
                    true,
                    getOverlayWindowSettings(CactbotOverlayName),
                    new Size(900, 320),
                    debugMode(),
                    log,
                    NotifyOverlayBrowserFailure);

                var configHtml = Path.Combine(
                    pluginInterface.ConfigDirectory.FullName,
                    "cactbot",
                    "ui",
                    "config",
                    "config.html");
                if (File.Exists(configHtml))
                {
                    cactbotSettings = new HtmlOverlayForm(
                        BuildOverlayUri(new Uri(configHtml), "OVERLAY_WS", webSocketUri),
                        Path.Combine(pluginInterface.ConfigDirectory.FullName, "webview2"),
                        Path.Combine(
                            pluginInterface.AssemblyLocation.Directory!.FullName,
                            "WebView2Loader.dll"),
                        "Cactbot Settings",
                        false,
                        null,
                        new Size(1100, 760),
                        debugMode(),
                        log,
                        NotifyOverlayBrowserFailure);
                }
            }

            overlayTemplates = availableTemplates.ToArray();

            RestoreHtmlOverlays();
        }
        catch
        {
            UnregisterCactbotRadarReload();
            var windowsShutdown = Task.CompletedTask;
            try
            {
                try
                {
                    windowsShutdown = DisposeOverlayWindows();
                }
                catch (Exception ex)
                {
                    log.Error(ex, "Failed to close HTML overlay windows after startup failure.");
                }

                overlayWebSocketUri = null;
                overlayTemplates = [];
                overlayTemplateSources = [];
                try
                {
                    overlay?.DeInitPlugin();
                }
                catch (Exception ex)
                {
                    log.Error(ex, "Failed to roll back OverlayPlugin after startup failure.");
                }
            }
            finally
            {
                overlay = null;
                httpClient?.Dispose();
                httpClient = null;
                ReleaseWebViewSessionLockWhenSafe(windowsShutdown);
            }
            throw;
        }
    }

    private static OverlayTemplateDocument LoadOverlayTemplateConfig()
    {
        var assembly = typeof(RainbowMage.OverlayPlugin.PluginMain).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith("overlays.json", StringComparison.OrdinalIgnoreCase));
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("OverlayPlugin template resource could not be opened.");
        using var reader = new StreamReader(stream);
        return JsonSerializer.Deserialize<OverlayTemplateDocument>(
                   reader.ReadToEnd(),
                   new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? throw new InvalidDataException("OverlayPlugin template resource is invalid.");
    }

    internal static IReadOnlyList<ActOverlayTemplate> ProbeOverlayTemplates()
    {
        var sources = LoadOverlayTemplateConfig().Overlays;
        var webSocket = new Uri("ws://127.0.0.1:10501/ws");
        foreach (var source in sources)
        {
            _ = BuildTemplateUri(source, webSocket);
        }

        return sources
            .Where(template => !template.Features.Contains("system"))
            .Select(template => new ActOverlayTemplate(
                template.Name,
                template.Uri,
                template.SuggestedWidth ?? 900,
                template.SuggestedHeight ?? 500,
                template.Features.Contains("cactbot")))
            .ToArray();
    }

    private static Uri BuildTemplateUri(OverlayTemplateSource template, Uri webSocketUri)
    {
        var pageUri = new Uri(
            webSocketUri.Scheme == "ws" && !string.IsNullOrWhiteSpace(template.PlaintextUri)
                ? template.PlaintextUri
                : template.Uri);
        if (template.Features.Contains("overlay_ws"))
        {
            return BuildOverlayUri(pageUri, "OVERLAY_WS", webSocketUri.ToString());
        }

        if (template.Features.Contains("host_port"))
        {
            return BuildOverlayUri(
                pageUri,
                "HOST_PORT",
                webSocketUri.GetComponents(UriComponents.SchemeAndServer, UriFormat.SafeUnescaped));
        }

        return pageUri;
    }

    public static bool IsCactbotOverlayName(string? name)
        => !string.IsNullOrWhiteSpace(name) &&
           ManagedCactbotOverlayNames.Contains(name);

    public static string NormalizeCactbotOverlayName(string name)
        => string.Equals(
            name,
            CactbotCombinedTemplateName,
            StringComparison.OrdinalIgnoreCase)
            ? CactbotOverlayName
            : name;

    internal static bool TryBuildLocalCactbotOverlayUri(
        string templateUri,
        string cactbotDirectory,
        Uri webSocketUri,
        out Uri pageUri)
    {
        pageUri = null!;
        if (!Uri.TryCreate(templateUri, UriKind.Absolute, out var remoteUri) ||
            string.IsNullOrWhiteSpace(cactbotDirectory))
        {
            return false;
        }

        const string cactbotPathMarker = "/cactbot/";
        var markerIndex = remoteUri.AbsolutePath.IndexOf(
            cactbotPathMarker,
            StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return false;
        }

        var relativePath = Uri.UnescapeDataString(
                remoteUri.AbsolutePath[(markerIndex + cactbotPathMarker.Length)..])
            .Replace('/', Path.DirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        var root = Path.GetFullPath(cactbotDirectory);
        var localPath = Path.GetFullPath(Path.Combine(root, relativePath));
        var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!localPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(localPath))
        {
            return false;
        }

        var localUri = new Uri(
            new Uri(localPath).AbsoluteUri + remoteUri.Query + remoteUri.Fragment);
        pageUri = BuildOverlayUri(
            localUri,
            "OVERLAY_WS",
            webSocketUri.ToString());
        return true;
    }

    private static Uri BuildOverlayUri(Uri pageUri, string parameter, string value)
    {
        // OverlayPlugin templates historically consume raw OVERLAY_WS/HOST_PORT
        // values before URLSearchParams decoding, so preserve the unescaped value.
        var fragment = pageUri.Fragment;
        var separator = !string.IsNullOrEmpty(fragment)
            ? fragment.Contains('?') ? "&" : "?"
            : string.IsNullOrEmpty(pageUri.Query) ? "?" : "&";
        return new Uri($"{pageUri.AbsoluteUri}{separator}{parameter}={value}");
    }

    internal static bool TryBuildCustomOverlayUri(
        string sourceUrl,
        Uri webSocketUri,
        OverlayConnectionMode connectionMode,
        out Uri pageUri)
    {
        if (!TryNormalizeCustomOverlayUri(sourceUrl, out pageUri))
        {
            return false;
        }

        if (connectionMode == OverlayConnectionMode.Original)
        {
            return true;
        }

        // Retry modes must not inherit a stale parameter for the other protocol.
        // The saved source URL itself is never changed, so manual recovery remains reversible.
        pageUri = RemoveOverlayConnectionParameters(pageUri);

        if (connectionMode is OverlayConnectionMode.Auto or OverlayConnectionMode.OverlayPlugin &&
            !HasOverlayParameter(pageUri, "OVERLAY_WS"))
        {
            pageUri = BuildOverlayUri(
                pageUri,
                "OVERLAY_WS",
                webSocketUri.ToString());
        }
        if (connectionMode == OverlayConnectionMode.ActWebSocket &&
            !HasOverlayParameter(pageUri, "HOST_PORT"))
        {
            pageUri = BuildOverlayUri(
                pageUri,
                "HOST_PORT",
                webSocketUri.GetComponents(
                    UriComponents.SchemeAndServer,
                    UriFormat.SafeUnescaped));
        }

        return true;
    }

    private static Uri RemoveOverlayConnectionParameters(Uri pageUri)
    {
        var absolute = pageUri.AbsoluteUri;
        var fragmentIndex = absolute.IndexOf('#');
        var basePart = fragmentIndex >= 0 ? absolute[..fragmentIndex] : absolute;
        var fragment = fragmentIndex >= 0 ? absolute[(fragmentIndex + 1)..] : null;
        basePart = RemoveQueryParameters(basePart);
        if (fragment is not null)
        {
            fragment = RemoveQueryParameters(fragment);
        }

        return new Uri(fragment is null ? basePart : $"{basePart}#{fragment}");
    }

    private static string RemoveQueryParameters(string value)
    {
        var queryIndex = value.IndexOf('?');
        if (queryIndex < 0)
        {
            return value;
        }

        var retained = value[(queryIndex + 1)..]
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(parameter =>
            {
                var equalsIndex = parameter.IndexOf('=');
                var key = equalsIndex >= 0 ? parameter[..equalsIndex] : parameter;
                return !key.Equals("OVERLAY_WS", StringComparison.OrdinalIgnoreCase) &&
                       !key.Equals("HOST_PORT", StringComparison.OrdinalIgnoreCase);
            })
            .ToArray();
        return retained.Length == 0
            ? value[..queryIndex]
            : $"{value[..queryIndex]}?{string.Join('&', retained)}";
    }

    internal static bool TryBuildCustomOverlayUri(
        string sourceUrl,
        Uri webSocketUri,
        out Uri pageUri)
        => TryBuildCustomOverlayUri(
            sourceUrl,
            webSocketUri,
            OverlayConnectionMode.OverlayPlugin,
            out pageUri);

    internal static OverlayConnectionMode ResolveInitialConnectionMode(
        HtmlOverlayWindowSettings settings)
        => settings.ConnectionMode == OverlayConnectionMode.Auto
            ? settings.DetectedConnectionMode is
                OverlayConnectionMode.OverlayPlugin or OverlayConnectionMode.ActWebSocket
                ? settings.DetectedConnectionMode.Value
                : OverlayConnectionMode.OverlayPlugin
            : settings.ConnectionMode;

    internal static OverlayConnectionMode GetFallbackConnectionMode(
        OverlayConnectionMode mode)
        => mode == OverlayConnectionMode.ActWebSocket
            ? OverlayConnectionMode.OverlayPlugin
            : OverlayConnectionMode.ActWebSocket;

    private void StartCustomOverlayConnectionDetection(
        string name,
        HtmlOverlayForm window,
        Uri webSocketUri,
        OverlayConnectionMode initialMode)
    {
        if (overlayConnectionAttempts.TryRemove(name, out var previousAttempt))
        {
            previousAttempt.Cancel();
            previousAttempt.Dispose();
        }

        var settings = getOverlayWindowSettings(name);
        if (initialMode == OverlayConnectionMode.Original)
        {
            settings.ConnectionState = OverlayConnectionState.None;
            settings.ConnectionStateDetail = "原样打开，不检测解析器连接";
            return;
        }

        var attempt = new OverlayConnectionAttempt(initialMode);
        overlayConnectionAttempts[name] = attempt;
        settings.ConnectionState = OverlayConnectionState.Detecting;
        settings.ConnectionStateDetail = DescribeConnectionMode(initialMode);
        _ = DetectCustomOverlayConnectionAsync(
            name,
            window,
            webSocketUri,
            settings,
            attempt);
    }

    private async Task DetectCustomOverlayConnectionAsync(
        string name,
        HtmlOverlayForm window,
        Uri webSocketUri,
        HtmlOverlayWindowSettings settings,
        OverlayConnectionAttempt attempt)
    {
        try
        {
            await Task.Delay(CustomOverlayConnectionTimeout, attempt.Token).ConfigureAwait(false);
            if (settings.ConnectionMode != OverlayConnectionMode.Auto)
            {
                settings.ConnectionState = OverlayConnectionState.Failed;
                settings.ConnectionStateDetail = "未检测到解析器连接";
                return;
            }

            var fallbackMode = GetFallbackConnectionMode(attempt.Mode);
            if (!TryBuildCustomOverlayUri(
                    settings.SourceUrl,
                    webSocketUri,
                    fallbackMode,
                    out var fallbackUri))
            {
                settings.ConnectionState = OverlayConnectionState.Failed;
                settings.ConnectionStateDetail = "网址无效";
                return;
            }

            attempt.Mode = fallbackMode;
            settings.ConnectionState = OverlayConnectionState.Retrying;
            settings.ConnectionStateDetail = $"正在尝试{DescribeConnectionMode(fallbackMode)}";
            if (!window.Navigate(fallbackUri))
            {
                settings.ConnectionState = OverlayConnectionState.Failed;
                settings.ConnectionStateDetail = "悬浮窗已经关闭";
                return;
            }

            await Task.Delay(CustomOverlayConnectionTimeout, attempt.Token).ConfigureAwait(false);
            settings.ConnectionState = OverlayConnectionState.Failed;
            settings.ConnectionStateDetail = "两种连接方式均未建立数据通道";
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (overlayConnectionAttempts.TryGetValue(name, out var current) &&
                ReferenceEquals(current, attempt) &&
                settings.ConnectionState is OverlayConnectionState.Connected or
                    OverlayConnectionState.Failed)
            {
                overlayConnectionAttempts.TryRemove(name, out _);
                attempt.Dispose();
            }
        }
    }

    private void OnCustomOverlayWebSocketConnected(string name, Uri connectedUri)
    {
        if (overlayWebSocketUri is null ||
            !Uri.TryCreate(overlayWebSocketUri, UriKind.Absolute, out var expectedUri) ||
            !string.Equals(connectedUri.Host, expectedUri.Host, StringComparison.OrdinalIgnoreCase) ||
            connectedUri.Port != expectedUri.Port)
        {
            return;
        }

        var detectedMode = connectedUri.AbsolutePath.Equals(
            "/ws",
            StringComparison.OrdinalIgnoreCase)
            ? OverlayConnectionMode.OverlayPlugin
            : connectedUri.AbsolutePath is "/MiniParse" or "/BeforeLogLineRead"
                ? OverlayConnectionMode.ActWebSocket
                : (OverlayConnectionMode?)null;
        if (detectedMode is null)
        {
            return;
        }

        var settings = getOverlayWindowSettings(name);
        if (settings.ConnectionMode == OverlayConnectionMode.Auto)
        {
            if (settings.DetectedConnectionMode != detectedMode)
            {
                settings.DetectedConnectionMode = detectedMode;
                // Transient probe state is ignored; only a proven strategy is persisted.
                persistOverlaySettings();
            }
        }

        settings.ConnectionState = OverlayConnectionState.Connected;
        settings.ConnectionStateDetail = $"已连接（{DescribeConnectionMode(detectedMode.Value)}）";
        if (overlayConnectionAttempts.TryRemove(name, out var attempt))
        {
            attempt.Cancel();
            attempt.Dispose();
        }
    }

    private static string DescribeConnectionMode(OverlayConnectionMode mode)
        => mode switch
        {
            OverlayConnectionMode.OverlayPlugin => "现代悬浮窗协议",
            OverlayConnectionMode.ActWebSocket => "旧版 ACTWS 协议",
            OverlayConnectionMode.Original => "原样网址",
            _ => "自动检测",
        };

    public static bool TryNormalizeCustomOverlayUri(string sourceUrl, out Uri uri)
    {
        if (!Uri.TryCreate(sourceUrl?.Trim(), UriKind.Absolute, out uri!) ||
            (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(uri.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase)))
        {
            uri = null!;
            return false;
        }

        return true;
    }

    private static bool HasOverlayParameter(Uri uri, string parameter)
    {
        var marker = parameter + "=";
        return uri.Query.Contains(marker, StringComparison.OrdinalIgnoreCase) ||
               uri.Fragment.Contains(marker, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildCustomOverlayProfileName(string name)
    {
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..8];
        return $"custom-{SanitizePath(name)}-{hash}";
    }

    private static string SanitizePath(string value)
        => string.Concat(value.Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));

    public IReadOnlyList<(string Id, Exception Error)> LoadCustomPlugins(
        IEnumerable<RuntimePluginSpec> plugins)
    {
        var failures = new List<(string Id, Exception Error)>();
        foreach (var plugin in plugins
                     .OrderBy(GetPluginLoadPriority)
                     .ThenBy(plugin => plugin.Id, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var loadedPlugin = LoadedActPlugin.Load(plugin, log);
                customPlugins.Add(loadedPlugin);
                if (IsPluginInitializationError(loadedPlugin.Status))
                {
                    var error = new InvalidOperationException(
                        $"ACT plugin '{plugin.Id}' reported an initialization error:{Environment.NewLine}" +
                        loadedPlugin.Status);
                    log.Error(error, $"ACT plugin '{plugin.Id}' initialized incompletely.");
                    failures.Add((plugin.Id, error));
                    continue;
                }

                log.Information(
                    $"ACT plugin '{plugin.Id}' loaded. Status: {loadedPlugin.Status}");
            }
            catch (Exception ex)
            {
                log.Error(ex, $"ACT plugin '{plugin.Id}' failed to load.");
                failures.Add((plugin.Id, ex));
            }
        }

        return failures;
    }

    private static int GetPluginLoadPriority(RuntimePluginSpec plugin)
        => plugin.Id.ToLowerInvariant() switch
        {
            "cactbotself" => 100,
            "triggernometry" => 200,
            "postnamazu" => 300,
            "act.foxtts" => 400,
            _ => 1000,
        };

    private static bool IsPluginInitializationError(string status)
        => status.Contains("error", StringComparison.OrdinalIgnoreCase) ||
           status.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
           status.Contains("exception", StringComparison.OrdinalIgnoreCase);

    private static FileStream AcquireWebViewSessionLock(string path, TimeSpan timeout)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var timer = Stopwatch.StartNew();
        Exception? lastFailure = null;
        while (timer.Elapsed < timeout)
        {
            try
            {
                return new FileStream(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);
            }
            catch (IOException ex)
            {
                lastFailure = ex;
                Thread.Sleep(100);
            }
        }

        throw new TimeoutException(
            "The previous WebView2 overlay session did not finish shutting down within " +
            $"{timeout.TotalSeconds:0.#} seconds.",
            lastFailure);
    }

    private Task DisposeOverlayWindows()
    {
        foreach (var attempt in overlayConnectionAttempts.Values)
        {
            attempt.Cancel();
            attempt.Dispose();
        }
        overlayConnectionAttempts.Clear();

        var windows = new List<HtmlOverlayForm>();
        if (cactbotOverlay is not null)
        {
            windows.Add(cactbotOverlay);
        }
        if (cactbotSettings is not null)
        {
            windows.Add(cactbotSettings);
        }
        windows.AddRange(htmlOverlays.Values);

        var browserProcessIds = windows
            .SelectMany(static window => window.BrowserProcessIds)
            .ToHashSet();
        foreach (var window in windows)
        {
            try
            {
                window.Dispose();
            }
            catch (Exception ex)
            {
                log.Warning(ex, "Failed to close an HTML overlay window cleanly.");
            }
            finally
            {
                browserProcessIds.UnionWith(window.BrowserProcessIds);
            }
        }

        cactbotOverlay = null;
        cactbotSettings = null;
        htmlOverlays.Clear();
        return CompleteOverlayWindowShutdownAsync(windows, browserProcessIds);
    }

    private async Task CompleteOverlayWindowShutdownAsync(
        IReadOnlyList<HtmlOverlayForm> windows,
        HashSet<int> browserProcessIds)
    {
        await Task.WhenAll(windows.Select(static window => window.ShutdownCompletion))
            .ConfigureAwait(false);
        foreach (var window in windows)
        {
            browserProcessIds.UnionWith(window.BrowserProcessIds);
        }

        HtmlOverlayForm.WaitForBrowserProcessesExit(
            browserProcessIds,
            TimeSpan.FromSeconds(3),
            log);
    }

    private void ReleaseWebViewSessionLockWhenSafe(Task windowsShutdown)
    {
        var sessionLock = webViewSessionLock;
        webViewSessionLock = null;
        if (sessionLock is null)
        {
            return;
        }

        if (!windowsShutdown.IsCompleted)
        {
            log.Warning(
                "HTML overlay shutdown is still completing; retaining the WebView2 session lock " +
                "until every overlay UI thread has exited.");
        }

        _ = windowsShutdown.ContinueWith(
            static (_, state) => ((FileStream)state!).Dispose(),
            sessionLock,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public void StopOverlay()
    {
        UnregisterCactbotRadarReload();
        externalPostNamazuEventSource?.SetAction(null);
        externalPostNamazuEventSource = null;
        var windowsShutdown = Task.CompletedTask;
        try
        {
            try
            {
                windowsShutdown = DisposeOverlayWindows();
            }
            catch (Exception ex)
            {
                log.Warning(ex, "Failed to finish HTML overlay browser shutdown cleanly.");
            }
            overlayTemplates = [];
            overlayTemplateSources = [];
            overlayWebSocketUri = null;
            overlay?.DeInitPlugin();
        }
        finally
        {
            overlay = null;
            httpClient?.Dispose();
            httpClient = null;
            ReleaseWebViewSessionLockWhenSafe(windowsShutdown);
        }
    }

    public async Task<string?> CallOverlayHandlerAsync(
        string payload,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        if (payload.Length > 65_536)
        {
            throw new ArgumentOutOfRangeException(
                nameof(payload),
                "OverlayPlugin handler payload exceeds 65536 characters.");
        }

        var request = JObject.Parse(payload);
        if (request["call"]?.Type != JTokenType.String ||
            string.IsNullOrWhiteSpace(request.Value<string>("call")))
        {
            throw new InvalidDataException(
                "OverlayPlugin handler payload must contain a non-empty call name.");
        }

        return await framework
            .RunOnFrameworkThread(() =>
            {
                if (!IsOverlayRunning ||
                    ActGlobals.oFormActMain.OverlayPluginContainer is not
                        RainbowMage.OverlayPlugin.TinyIoCContainer container)
                {
                    throw new InvalidOperationException(
                        "The real game-side OverlayPlugin dispatcher is not running.");
                }

                var dispatcherType = typeof(RainbowMage.OverlayPlugin.PluginMain)
                                         .Assembly
                                         .GetType(
                                             "RainbowMage.OverlayPlugin.EventDispatcher",
                                             throwOnError: true)!
                                     ?? throw new TypeLoadException(
                                         "RainbowMage.OverlayPlugin.EventDispatcher");
                var resolve = container.GetType()
                                  .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                                  .Single(method =>
                                      method.Name == "Resolve" &&
                                      method.IsGenericMethodDefinition &&
                                      method.GetGenericArguments().Length == 1 &&
                                      method.GetParameters().Length == 0)
                                  .MakeGenericMethod(dispatcherType);
                var dispatcher = resolve.Invoke(container, null)
                                 ?? throw new InvalidOperationException(
                                     "OverlayPlugin EventDispatcher resolution returned null.");
                var callHandler = dispatcherType.GetMethod(
                                      "CallHandler",
                                      BindingFlags.Instance | BindingFlags.Public,
                                      binder: null,
                                      [typeof(JObject)],
                                      modifiers: null)
                                  ?? throw new MissingMethodException(
                                      dispatcherType.FullName,
                                      "CallHandler");
                var response = callHandler.Invoke(dispatcher, [request]) as JToken;
                var serialized = response?.ToString(Newtonsoft.Json.Formatting.None);
                if (serialized is not null && Encoding.UTF8.GetByteCount(serialized) > 900_000)
                {
                    throw new InvalidOperationException(
                        "OverlayPlugin handler response exceeds the bounded IPC frame.");
                }

                return serialized;
            })
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private void RegisterExternalPostNamazuAdapter(
        RainbowMage.OverlayPlugin.TinyIoCContainer container)
    {
        if (Volatile.Read(ref externalPostNamazuDispatcher) is null)
        {
            return;
        }

        var registry = container.Resolve<RainbowMage.OverlayPlugin.Registry>();
        var existing = registry.EventSources.FirstOrDefault(source =>
            string.Equals(
                source.Name,
                PostNamazuEventSource.EventSourceName,
                StringComparison.Ordinal));
        if (existing is not null and not PostNamazuEventSource)
        {
            log.Information("PostNamazu upstream OverlayPlugin event source is already active.");
            return;
        }

        externalPostNamazuEventSource = existing as PostNamazuEventSource;
        if (externalPostNamazuEventSource is null)
        {
            externalPostNamazuEventSource = new PostNamazuEventSource(container);
            registry.StartEventSource(externalPostNamazuEventSource);
        }

        externalPostNamazuEventSource.SetAction((action, payload) =>
        {
            var dispatcher = Volatile.Read(ref externalPostNamazuDispatcher);
            if (dispatcher?.Invoke(action, payload) != true)
            {
                throw new InvalidOperationException(
                    "Independent ACT Host rejected the PostNamazu OverlayPlugin action.");
            }
        });
        log.Information(
            "PostNamazu OverlayPlugin event source is bridged to the independent ACT Host.");
    }

    private void RegisterCactbotRadarReload(
        RainbowMage.OverlayPlugin.TinyIoCContainer container)
    {
        UnregisterCactbotRadarReload();
        var eventSource = container.Resolve<RainbowMage.OverlayPlugin.EventSources.CactbotEventSource>();
        lock (radarReloadSync)
        {
            cactbotEventSource = eventSource;
            eventSource.RadarOptionsChanged += OnCactbotRadarOptionsChanged;
        }
    }

    private void UnregisterCactbotRadarReload()
    {
        lock (radarReloadSync)
        {
            if (cactbotEventSource is not null)
            {
                cactbotEventSource.RadarOptionsChanged -= OnCactbotRadarOptionsChanged;
                cactbotEventSource = null;
            }

            radarOptionsReloadTimer?.Dispose();
            radarOptionsReloadTimer = null;
        }
    }

    private void OnCactbotRadarOptionsChanged(object? sender, EventArgs args)
    {
        lock (radarReloadSync)
        {
            if (cactbotEventSource is null)
            {
                return;
            }

            // The settings page can save several adjacent fields. A short debounce keeps
            // one user action from repeatedly rebuilding Radar's startup-only TTS state.
            radarOptionsReloadTimer ??= new System.Threading.Timer(
                _ => ReloadOpenCactbotRadar(),
                null,
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);
            radarOptionsReloadTimer.Change(
                TimeSpan.FromMilliseconds(150),
                Timeout.InfiniteTimeSpan);
        }
    }

    private void ReloadOpenCactbotRadar()
    {
        if (!htmlOverlays.TryGetValue(CactbotRadarOverlayName, out var radarWindow))
        {
            return;
        }

        if (radarWindow.Reload())
        {
            log.Information(
                "Reloading the existing Cactbot Radar window after its options changed.");
        }
    }

    private sealed record OverlayTemplateDocument(
        [property: JsonPropertyName("overlays")] IReadOnlyList<OverlayTemplateSource> Overlays);

    private sealed record OverlayTemplateSource(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("uri")] string Uri,
        [property: JsonPropertyName("plaintext_uri")] string? PlaintextUri,
        [property: JsonPropertyName("suggested_width")] int? SuggestedWidth,
        [property: JsonPropertyName("suggested_height")] int? SuggestedHeight,
        [property: JsonPropertyName("features")] IReadOnlyList<string> Features);

    public void StopParser()
    {
        for (var index = customPlugins.Count - 1; index >= 0; index--)
        {
            try
            {
                customPlugins[index].Dispose();
            }
            catch (Exception ex)
            {
                log.Error(ex, $"Failed to unload ACT plugin '{customPlugins[index].Id}'.");
            }
        }

        customPlugins.Clear();
        try
        {
            StopOverlay();
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to stop OverlayPlugin.");
        }

        try
        {
            RemovePluginData(parserPluginData);
            parserPluginData = null;
            if (parser is not null)
            {
                parser.Subscription.ZoneChanged -= OnZoneChangedForHost;
                parser.Subscription.NetworkReceived -= OnNetworkReceivedForHost;
                lock (networkCaptureSync)
                {
                    if (networkSentSubscribed)
                    {
                        parser.Subscription.NetworkSent -= OnNetworkSentForHost;
                        networkSentSubscribed = false;
                    }
                }
            }
            parser?.Dispose();
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to stop FFXIV_ACT_Plugin.");
        }

        parser = null;
        framework.Update -= OnFrameworkUpdate;
        try
        {
            zoneDownHookManager?.Dispose();
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to stop the Chinese-region packet unscrambler.");
        }

        zoneDownHookManager = null;
        if (actGlobalsInitialized)
        {
            try
            {
                ActGlobals.oFormActMain.AfterCombatAction -= OnAfterCombatAction;
                ActGlobals.oFormActMain.AfterCombatEnd -= OnAfterCombatEnd;
                ActGlobals.oFormActMain.BeforeLogLineRead -= OnBeforeLogLineRead;
                ActGlobals.Dispose();
            }
            finally
            {
                actGlobalsInitialized = false;
            }
        }

        gameStateProvider.Clear();
        lock (encounterSync)
        {
            raidDpsEstimator.Reset();
            effectiveDamageLedger.Reset();
            encounterDurationTracker.Reset();
            effectiveDamageEventCursor = 0;
            effectiveDamageEncounter = null;
            activeEncounter = null;
            activeEncounterId = Guid.Empty;
            activeEncounterMode = EncounterMode.OpenWorld;
            activeEncounterTerritoryId = 0;
            activeEncounterPartyCapacity = 0;
            activeEncounterIdentities.Clear();
            activeEncounterPublished = false;
            activeEncounterNamesLogged = false;
            activeEncounterRelevantStart = default;
            lastRelevantCombatAction = default;
            transitionStateDirty = false;
            lastKnownDead.Clear();
            observedDeaths.Clear();
            ResetChatEncounterUnsafe();
        }
        parityDiagnosticRecorder.ResetEncounter();
        Interlocked.Exchange(ref parityDiagnosticFaulted, 0);
    }

    private static ActPluginData RegisterSystemPlugin(IActPluginV1 plugin, string fileName)
    {
        var data = new ActPluginData(
            new FileInfo(Path.Combine(AppContext.BaseDirectory, fileName)),
            plugin,
            new TabPage(fileName),
            new Label { Text = "FFXIV_ACT_Plugin Started." });
        ActGlobals.oFormActMain.ActPlugins.Add(data);
        return data;
    }

    private static void RemovePluginData(ActPluginData? data)
    {
        if (data is null)
        {
            return;
        }

        ActGlobals.oFormActMain.ActPlugins.Remove(data);
        data.tpPluginSpace.Dispose();
        data.lblPluginStatus.Dispose();
        data.lblPluginTitle.Dispose();
        data.cbEnabled.Dispose();
    }

    public void Dispose()
    {
        StopParser();
        LegacyResourceCompatibility.StopServices();
        CompatibilityPermissionBroker.Reset();
    }

    private void OnBeforeLogLineRead(bool isImport, LogLineEventArgs logInfo)
    {
        RawLogLineReceived?.Invoke(
            new DateTimeOffset(logInfo.detectedTime),
            logInfo.originalLogLine,
            logInfo.logLine,
            isImport);
        if (isImport)
        {
            return;
        }

        if (parityDiagnosticsEnabled())
        {
            // Phase 0 records the raw parser input in a sidecar only. It must never
            // mutate the log line or influence the production encounter pipeline.
            ObserveParityRawLine(logInfo.originalLogLine);
        }

        if (effectiveDamageLedger.ObserveRawLine(
                new DateTimeOffset(logInfo.detectedTime),
                logInfo.originalLogLine))
        {
            lock (encounterSync)
            {
                transitionStateDirty = true;
            }
        }

        if (encounterDurationTracker.ObserveRawLine(
                new DateTimeOffset(logInfo.detectedTime),
                logInfo.originalLogLine,
                gameStateProvider.Identities))
        {
            lock (encounterSync)
            {
                transitionStateDirty = true;
            }
        }

        raidDpsEstimator.ObserveNetworkLine(
            new DateTimeOffset(logInfo.detectedTime),
            logInfo.originalLogLine);
        ObserveCommittedDamageEvents(gameStateProvider.Identities);

        var now = new DateTimeOffset(logInfo.detectedTime);
        var zone = logInfo.detectedZone ?? ActGlobals.oFormActMain.CurrentZone ?? string.Empty;
        var identities = gameStateProvider.Identities;
        ActEncounterSnapshot? completedEncounter = null;
        if (NetworkDamageFallbackParser.TryParse(
                logInfo.originalLogLine,
                out var networkDamage) &&
            !limitBreakActionNames.Contains(networkDamage.ActionName) &&
            ResolveNetworkDamageActor(identities, networkDamage) is { } networkActor)
        {
            lock (encounterSync)
            {
                completedEncounter = RecordFallbackDamageUnsafe(
                    now,
                    networkActor.DisplayName,
                    networkDamage.TargetName,
                    networkDamage.Damage,
                    networkDamage.ActionName,
                    networkDamage.IsCritical,
                    networkDamage.IsDirectHit,
                    zone);
                recentNetworkDamage.Add(new RecentNetworkDamage(
                    now,
                    networkActor.DisplayName,
                    networkDamage.TargetName,
                    networkDamage.Damage));
                PruneRecentNetworkDamageUnsafe(now);
            }
        }
        else
        {
            var fields = logInfo.originalLogLine.Split('|');
            if (fields.Length < 5 || fields[0] != "00")
            {
                return;
            }

            lock (encounterSync)
            {
                if (!chatParser.TryParse(
                        fields[4],
                        now,
                        out var actor,
                        out var target,
                        out var damage,
                        out var action,
                        out var isCritical,
                        out var isDirectHit))
                {
                    return;
                }

                // Localized chat remains a compatibility fallback for missing network lines.
                // The structured path above is region-neutral and wins one-for-one on duplicates.
                var isLimitBreak = string.Equals(
                    actor,
                    ChineseCombatChatContext.LimitBreakActorName,
                    StringComparison.OrdinalIgnoreCase);
                var identity = isLimitBreak
                    ? null
                    : ActPlayerIdentityResolver.Resolve(identities, actor);
                if (!isLimitBreak && identity is null)
                {
                    return;
                }

                var canonicalActor = isLimitBreak
                    ? ChineseCombatChatContext.LimitBreakActorName
                    : identity!.DisplayName;
                if (!isLimitBreak && ConsumeMatchingNetworkDamageUnsafe(
                        now,
                        canonicalActor,
                        target,
                        damage))
                {
                    return;
                }

                completedEncounter = RecordFallbackDamageUnsafe(
                    now,
                    canonicalActor,
                    target,
                    damage,
                    action,
                    isCritical,
                    isDirectHit,
                    zone);
            }
        }

        if (completedEncounter is not null)
        {
            EncounterChanged?.Invoke(completedEncounter, true);
        }
    }

    private static ActPlayerIdentity? ResolveNetworkDamageActor(
        IReadOnlyList<ActPlayerIdentity> identities,
        NetworkDamageFallbackEvent damageEvent)
        => identities.FirstOrDefault(identity =>
               identity.EntityId != 0 && identity.EntityId == damageEvent.SourceId) ??
           ActPlayerIdentityResolver.Resolve(identities, damageEvent.SourceName);

    private ActEncounterSnapshot? RecordFallbackDamageUnsafe(
        DateTimeOffset now,
        string actor,
        string target,
        long damage,
        string action,
        bool isCritical,
        bool isDirectHit,
        string zone)
    {
        ActEncounterSnapshot? completedEncounter = null;
        if (chatEncounterId == Guid.Empty || now - chatLastDamage > TimeSpan.FromSeconds(30))
        {
            if (chatEncounterPublished && activeEncounter is null)
            {
                completedEncounter = CreateChatEncounterSnapshot(
                    finished: true,
                    gameStateProvider.Identities);
            }

            ResetChatEncounterUnsafe();
            if (activeEncounter is null)
            {
                lastKnownDead.Clear();
                observedDeaths.Clear();
            }
            chatEncounterId = Guid.NewGuid();
            var gameState = encounterModeSnapshot();
            chatEncounterMode = gameState.Mode;
            chatEncounterTerritoryId = gameState.TerritoryId;
            chatEncounterStart = now;
        }

        chatLastDamage = now;
        chatEnemy = target;
        chatZone = zone;
        chatDamageTotals[actor] = chatDamageTotals.GetValueOrDefault(actor) + damage;
        chatDamageHitTotals[actor] = chatDamageHitTotals.GetValueOrDefault(actor) + 1;
        // Equal hits intentionally keep the first action so the meter does not churn.
        if (damage > chatHighestDamageTotals.GetValueOrDefault(actor))
        {
            chatHighestDamageTotals[actor] = damage;
            chatHighestDamageActions[actor] = action;
        }
        if (isCritical)
        {
            chatCriticalHitTotals[actor] = chatCriticalHitTotals.GetValueOrDefault(actor) + 1;
        }
        if (isDirectHit)
        {
            chatDirectHitTotals[actor] = chatDirectHitTotals.GetValueOrDefault(actor) + 1;
        }
        if (isCritical && isDirectHit)
        {
            chatCriticalDirectHitTotals[actor] =
                chatCriticalDirectHitTotals.GetValueOrDefault(actor) + 1;
        }
        chatEncounterDirty = true;
        return completedEncounter;
    }

    private bool ConsumeMatchingNetworkDamageUnsafe(
        DateTimeOffset now,
        string actor,
        string target,
        long damage)
    {
        PruneRecentNetworkDamageUnsafe(now);
        var index = recentNetworkDamage.FindIndex(item =>
            string.Equals(item.Actor, actor, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.Target, target, StringComparison.OrdinalIgnoreCase) &&
            item.Damage == damage);
        if (index < 0)
        {
            return false;
        }

        recentNetworkDamage.RemoveAt(index);
        return true;
    }

    private void PruneRecentNetworkDamageUnsafe(DateTimeOffset now)
        => recentNetworkDamage.RemoveAll(item =>
            now - item.ObservedAt > TimeSpan.FromSeconds(3));

    private void OnFrameworkUpdate(IFramework _)
    {
        var identities = playerIdentities();
        var gameState = encounterModeSnapshot();
        var inCombat = gameState.InCombat;
        frameworkInCombat = inCombat;
        gameStateProvider.Update(
            identities,
            localPlayerPose(),
            inCombat);
        TrackPlayerDeaths(identities);

        ActEncounterSnapshot? activeChatEncounter = null;
        ActEncounterSnapshot? completedChatEncounter = null;
        EncounterData? transitionEncounterToPublish = null;
        EncounterData? activeEncounterToEnd = null;
        var endingAtModeBoundary = false;
        lock (encounterSync)
        {
            var now = DateTimeOffset.Now;
            if (chatEncounterId != Guid.Empty && chatLastDamage != default)
            {
                if (chatEncounterDirty &&
                    now - chatEncounterStart >= TimeSpan.FromMilliseconds(250))
                {
                    if (activeEncounter is null)
                    {
                        activeChatEncounter = CreateChatEncounterSnapshot(
                            finished: false,
                            identities);
                        chatEncounterPublished |= activeChatEncounter is not null;
                    }
                    else
                    {
                        // Rebuild ACT's current snapshot so fallback damage can be merged
                        // without replacing healing already published for the same player.
                        transitionEncounterToPublish = activeEncounter;
                    }
                    chatEncounterDirty = false;
                }

                if (activeEncounter is null &&
                    !inCombat &&
                    !localDeathWhilePartyContinues())
                {
                    completedChatEncounter = CreateChatEncounterSnapshot(
                        finished: true,
                        identities);
                    ResetChatEncounterUnsafe();
                    lastKnownDead.Clear();
                    observedDeaths.Clear();
                }
            }

            endingAtModeBoundary = activeEncounter is not null &&
                                   activeEncounterMode != gameState.Mode;
            if (activeEncounter is not null &&
                (endingAtModeBoundary || OpenWorldEncounterEndPolicy.ShouldEnd(
                    gameState.Mode,
                    inCombat,
                    lastRelevantCombatAction,
                    now)))
            {
                activeEncounterToEnd = activeEncounter;
            }

            if (transitionStateDirty)
            {
                transitionEncounterToPublish = activeEncounter;
                transitionStateDirty = false;
            }
        }

        if (activeChatEncounter is not null)
        {
            EncounterChanged?.Invoke(activeChatEncounter, false);
        }

        if (completedChatEncounter is not null)
        {
            EncounterChanged?.Invoke(completedChatEncounter, true);
        }

        if (transitionEncounterToPublish is not null)
        {
            PublishEncounter(transitionEncounterToPublish, false);
        }

        if (activeEncounterToEnd is not null)
        {
            try
            {
                ActGlobals.oFormActMain.EndCombat(true);
            }
            catch (Exception ex)
            {
                log.Error(
                    ex,
                    endingAtModeBoundary
                        ? "Failed to end the ACT encounter at a mode boundary."
                        : "Failed to end the local open-world ACT encounter.");
                lock (encounterSync)
                {
                    lastRelevantCombatAction = DateTimeOffset.Now;
                }
            }
        }
    }

    private ActEncounterSnapshot? CreateChatEncounterSnapshot(
        bool finished,
        IReadOnlyList<ActPlayerIdentity> identities)
    {
        if (chatEncounterId == Guid.Empty)
        {
            return null;
        }

        var elapsedSeconds = Math.Max(
            1,
            ((finished ? chatLastDamage : DateTimeOffset.Now) - chatEncounterStart).TotalSeconds);
        var combatants = chatDamageTotals
            .Select(pair =>
            {
                var identity = ActPlayerIdentityResolver.Resolve(identities, pair.Key);
                var isLimitBreak = string.Equals(
                    pair.Key,
                    ChineseCombatChatContext.LimitBreakActorName,
                    StringComparison.OrdinalIgnoreCase);
                if (identity is null && !isLimitBreak)
                {
                    return null;
                }

                var displayName = isLimitBreak
                    ? ChineseCombatChatContext.LimitBreakActorName
                    : identity!.DisplayName;
                return new ActCombatantSnapshot(
                    displayName,
                    displayName,
                    isLimitBreak ? string.Empty : identity!.Job,
                    !isLimitBreak && identity!.IsLocalPlayer,
                    pair.Value,
                    0,
                    isLimitBreak ? 0 : observedDeaths.GetValueOrDefault(displayName),
                    pair.Value / elapsedSeconds,
                    pair.Value / elapsedSeconds,
                    pair.Value / elapsedSeconds,
                    chatDamageHitTotals.GetValueOrDefault(pair.Key),
                    chatCriticalHitTotals.GetValueOrDefault(pair.Key),
                    chatCriticalDirectHitTotals.GetValueOrDefault(pair.Key),
                    DirectHits: chatDirectHitTotals.GetValueOrDefault(pair.Key),
                    HighestDamageAction: chatHighestDamageActions.GetValueOrDefault(pair.Key) ?? string.Empty,
                    HighestDamage: chatHighestDamageTotals.GetValueOrDefault(pair.Key),
                    PartyGroup: isLimitBreak ? 0 : identity!.PartyGroup);
            })
            .Where(static combatant => combatant is not null)
            .Select(static combatant => combatant!)
            .ToArray();
        return combatants.Length == 0
            ? null
            : new ActEncounterSnapshot(
                activeEncounterId == Guid.Empty ? chatEncounterId : activeEncounterId,
                chatEncounterStart,
                finished ? chatLastDamage : null,
                chatZone,
                string.IsNullOrWhiteSpace(chatEnemy) ? "Encounter" : chatEnemy,
                combatants)
            {
                EncounterMode = activeEncounterId == Guid.Empty
                    ? chatEncounterMode
                    : activeEncounterMode,
                TerritoryId = activeEncounterId == Guid.Empty
                    ? chatEncounterTerritoryId
                    : activeEncounterTerritoryId,
                CurrentPartyMemberIds = identities
                    .Select(static identity => identity.DisplayName)
                    .ToArray(),
                PartyCapacity = Math.Max(activeEncounterPartyCapacity, identities.Count),
            };
    }

    private void OnAfterCombatAction(bool isImport, CombatActionEventArgs action)
    {
        if (isImport)
        {
            return;
        }

        var swing = action.combatAction;
        var identities = gameStateProvider.Identities;
        var attackerIdentity = ActPlayerIdentityResolver.Resolve(identities, swing.Attacker);
        if (attackerIdentity is null &&
            raidDpsEstimator.TryResolvePetOwner(swing.Attacker, out var ownerName))
        {
            attackerIdentity = ActPlayerIdentityResolver.Resolve(identities, ownerName);
        }
        var encounter = swing.ParentEncounter;
        if (parityDiagnosticsEnabled())
        {
            // Observe before DACT's current party and swing filters so every excluded
            // MasterSwing has a concrete reason in the parity ledger.
            ObserveParityCombatAction(
                swing,
                identities,
                attackerIdentity,
                encounter is not null);
        }
        if (encounter is null)
        {
            return;
        }

        var victimIdentity = ActPlayerIdentityResolver.Resolve(identities, swing.Victim);
        var actionTime = swing.Time == default
            ? DateTimeOffset.Now
            : new DateTimeOffset(swing.Time);
        lock (encounterSync)
        {
            if (!ReferenceEquals(effectiveDamageEncounter, encounter))
            {
                raidDpsEstimator.StartEncounter(actionTime);
                effectiveDamageEventCursor = 0;
                effectiveDamageLedger.StartEncounter(identities);
                encounterDurationTracker.StartEncounter(actionTime, identities);
                effectiveDamageEncounter = encounter;
            }
        }
        effectiveDamageLedger.ObserveCombatAction(
            swing,
            identities,
            attackerIdentity,
            victimIdentity);
        ObserveCommittedDamageEvents(identities);
        if (attackerIdentity is null && victimIdentity is null)
        {
            return;
        }

        var isDamageSwing = RaidDpsEstimator.IsDamageSwing(swing);
        if (!isDamageSwing)
        {
            lock (encounterSync)
            {
                if (!ReferenceEquals(activeEncounter, encounter))
                {
                    return;
                }
            }

            // Healing remains visible in ACT totals, but it must never create or extend
            // the damage window used by DPS/rDPS and FFLogs estimates.
            PublishEncounter(encounter, false);
            return;
        }

        lock (encounterSync)
        {
            if (!ReferenceEquals(activeEncounter, encounter))
            {
                activeEncounterRelevantStart = actionTime;
            }
            lastRelevantCombatAction = actionTime;
        }

        PublishEncounter(encounter, false);
    }

    private void ObserveCommittedDamageEvents(IReadOnlyList<ActPlayerIdentity> identities)
    {
        lock (encounterSync)
        {
            var knownIdentities = identities
                .Concat(activeEncounterIdentities.Values)
                .GroupBy(static identity => identity.EntityId)
                .Select(static group => group.First())
                .ToArray();
            var events = effectiveDamageLedger.GetCommittedEventsSince(
                effectiveDamageEventCursor,
                out var nextIndex);
            effectiveDamageEventCursor = nextIndex;
            foreach (var item in events)
            {
                ActPlayerIdentity? owner = null;
                if (uint.TryParse(
                        item.OwnerId,
                        NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture,
                        out var ownerId) && ownerId != 0)
                {
                    owner = knownIdentities.FirstOrDefault(identity => identity.EntityId == ownerId);
                }
                if (owner is null && uint.TryParse(
                        item.SourceId,
                        NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture,
                        out var sourceId) && sourceId != 0)
                {
                    owner = knownIdentities.FirstOrDefault(identity => identity.EntityId == sourceId);
                }
                owner ??= ActPlayerIdentityResolver.Resolve(knownIdentities, item.SourceName);
                if (owner is null &&
                    raidDpsEstimator.TryResolvePetOwner(item.SourceName, out var ownerName))
                {
                    owner = ActPlayerIdentityResolver.Resolve(knownIdentities, ownerName);
                }
                if (owner is not null)
                {
                    raidDpsEstimator.ObserveEffectiveDamage(item, owner.Name);
                }
            }
        }
    }

    private void OnAfterCombatEnd(EncounterData encounter)
    {
        lock (encounterSync)
        {
            if (!ReferenceEquals(activeEncounter, encounter))
            {
                return;
            }
        }

        PublishEncounter(encounter, true);
    }

    internal static bool IsTrackedCombatantEvent(
        string attacker,
        string victim,
        IReadOnlyList<ActPlayerIdentity> identities)
        => ActPlayerIdentityResolver.Resolve(identities, attacker) is not null ||
           ActPlayerIdentityResolver.Resolve(identities, victim) is not null;

    private void OnZoneChangedForHost(uint territoryId, string zoneName)
        => ZoneChanged?.Invoke(territoryId, zoneName);

    private void OnNetworkReceivedForHost(string connection, long epoch, byte[] message)
        => NetworkReceived?.Invoke(connection, epoch, message);

    private void OnNetworkSentForHost(string connection, long epoch, byte[] message)
        => NetworkSent?.Invoke(connection, epoch, message);

    private void UpdateNetworkSentSubscriptionLocked()
    {
        if (parser is null)
        {
            networkSentSubscribed = false;
            return;
        }

        if (networkSentCaptureRequested == networkSentSubscribed)
        {
            return;
        }

        if (networkSentCaptureRequested)
        {
            parser.Subscription.NetworkSent += OnNetworkSentForHost;
        }
        else
        {
            parser.Subscription.NetworkSent -= OnNetworkSentForHost;
        }

        networkSentSubscribed = networkSentCaptureRequested;
    }

    private void PublishEncounter(EncounterData encounter, bool finished)
    {
        try
        {
            ActEncounterSnapshot? snapshot = null;
            ActEncounterSnapshot? fallbackSnapshot = null;
            string? unmatchedNames = null;
            ParityCompletionRequest? parityCompletion = null;
            // NotACT exposes its internal ActionDataLock through this ACT-compatible
            // property. Keep every traversal of CombatantData/AttackType swings inside
            // it because the parser appends to those collections on another thread.
            lock (ActGlobals.oFormActMain.AfterCombatActionDataLock)
            {
                lock (encounterSync)
                {
                    if (!ReferenceEquals(activeEncounter, encounter))
                    {
                        var continuesChatEncounter = chatEncounterId != Guid.Empty;
                        damageHitCounters.Clear();
                        activeEncounterIdentities.Clear();
                        activeEncounterPartyCapacity = 0;
                        activeEncounter = encounter;
                        activeEncounterId = continuesChatEncounter
                            ? chatEncounterId
                            : Guid.NewGuid();
                        if (continuesChatEncounter)
                        {
                            activeEncounterMode = chatEncounterMode;
                            activeEncounterTerritoryId = chatEncounterTerritoryId;
                        }
                        else
                        {
                            var gameState = encounterModeSnapshot();
                            activeEncounterMode = gameState.Mode;
                            activeEncounterTerritoryId = gameState.TerritoryId;
                        }
                        activeEncounterPublished = false;
                        activeEncounterNamesLogged = false;
                        if (activeEncounterRelevantStart == default)
                        {
                            activeEncounterRelevantStart = encounter.StartTime == DateTime.MaxValue
                                ? DateTimeOffset.Now
                                : new DateTimeOffset(encounter.StartTime);
                        }
                        if (!continuesChatEncounter)
                        {
                            lastKnownDead.Clear();
                            observedDeaths.Clear();
                        }
                    }

                    var startTime = activeEncounterRelevantStart == default
                        ? encounter.StartTime == DateTime.MaxValue
                            ? DateTimeOffset.Now
                            : new DateTimeOffset(encounter.StartTime)
                        : activeEncounterRelevantStart;
                    DateTimeOffset? endTime = finished
                        ? lastRelevantCombatAction == default
                            ? encounter.EndTime == DateTime.MinValue
                                ? DateTimeOffset.Now
                                : new DateTimeOffset(encounter.EndTime)
                            : lastRelevantCombatAction
                        : null;
                    var measurementEndTime = endTime ?? DateTimeOffset.Now;
                    var damageMetricDuration = encounterDurationTracker
                        .ResolveDamageMetricDurationSeconds(
                            measurementEndTime,
                            useObservedDamageEnd: finished);
                    var effectiveEncounterSeconds = Math.Max(1, damageMetricDuration);
                    var identities = gameStateProvider.Identities;
                    // The largest live roster seen in this pull is the slot count. Cached
                    // identities only fill temporarily empty slots and never expand the party.
                    activeEncounterPartyCapacity = Math.Max(
                        activeEncounterPartyCapacity,
                        identities.Count);
                    CacheActiveEncounterIdentities(identities);
                    var cachedIdentities = activeEncounterIdentities.Values.ToArray();
                    if (finished)
                    {
                        effectiveDamageLedger.PrepareFinalSnapshot();
                        ObserveCommittedDamageEvents(identities);
                    }
                    var combatants = ResolveEncounterCombatants(
                            encounter,
                            identities,
                            cachedIdentities,
                            activeEncounterPartyCapacity)
                        .Select(item =>
                        {
                            var hitCounts = GetDamageHitCounts(item.Combatant);
                            var highestDamage = GetHighestDamageHit(item.Combatant);
                            var displayName = item.Identity?.DisplayName ?? item.Combatant.Name;
                            var actorName = item.Identity?.Name ?? item.Combatant.Name;
                            var effectiveDamage = 0L;
                            var hasEffectiveDamage = item.Identity is { EntityId: not 0 } &&
                                                     effectiveDamageLedger.TryResolveDamage(
                                                         item.Identity,
                                                         out effectiveDamage);
                            var totalDamage = hasEffectiveDamage
                                ? effectiveDamage
                                : item.Combatant.Damage;
                            var rawHealing = 0L;
                            var hasRawHealing = item.Identity is { EntityId: not 0 } &&
                                                effectiveDamageLedger.TryResolveHealing(
                                                    item.Identity,
                                                    out rawHealing);
                            // Some damage actions carry a secondary self-heal that ACT omits.
                            // The cumulative raw total supplements ACT without double-counting
                            // ordinary heals that both pipelines already observed.
                            var totalHealing = hasRawHealing
                                ? Math.Max(item.Combatant.Healed, rawHealing)
                                : item.Combatant.Healed;
                            var isLocalPlayer = item.Identity?.IsLocalPlayer == true ||
                                                string.Equals(
                                                    item.Combatant.Name,
                                                    "YOU",
                                                    StringComparison.OrdinalIgnoreCase) ||
                                                string.Equals(
                                                    item.Combatant.Name,
                                                    playerName(),
                                                    StringComparison.OrdinalIgnoreCase);
                            return new ActCombatantSnapshot(
                                displayName,
                                displayName,
                                item.Identity?.Job ?? string.Empty,
                                isLocalPlayer,
                                totalDamage,
                                totalHealing,
                                Math.Max(
                                    item.Combatant.Deaths,
                                    Math.Max(
                                        observedDeaths.GetValueOrDefault(displayName),
                                        observedDeaths.GetValueOrDefault(item.Combatant.Name))),
                                totalDamage / effectiveEncounterSeconds,
                                totalDamage / effectiveEncounterSeconds,
                                item.Combatant.ExtDPS,
                                hitCounts.DamageHits,
                                hitCounts.CriticalHits,
                                hitCounts.CriticalDirectHits,
                                hasEffectiveDamage
                                    ? raidDpsEstimator.ResolveRate(
                                        actorName,
                                        totalDamage,
                                        effectiveEncounterSeconds)
                                    : 0,
                                hitCounts.DirectHits,
                                highestDamage.Action,
                                highestDamage.Amount,
                                item.Identity?.PartyGroup ?? 0);
                        })
                        .ToArray();

                    if (combatants.Length > 0)
                    {
                        activeEncounterPublished |= combatants.Any(static combatant =>
                            combatant.TotalDamage > 0 || combatant.TotalHealing > 0);
                        snapshot = new ActEncounterSnapshot(
                            activeEncounterId,
                            startTime,
                            endTime,
                            encounter.ZoneName ?? ActGlobals.oFormActMain.CurrentZone ?? string.Empty,
                            encounter.Title ?? string.Empty,
                            combatants)
                        {
                            EncounterMode = activeEncounterMode,
                            TerritoryId = activeEncounterTerritoryId,
                            CombatDuration = TimeSpan.FromSeconds(effectiveEncounterSeconds),
                            IsTransitioning = !finished &&
                                              encounterDurationTracker.IsTransitioningAt(
                                                  measurementEndTime),
                            CurrentPartyMemberIds = identities
                                .Select(static identity => identity.DisplayName)
                                .ToArray(),
                            PartyCapacity = activeEncounterPartyCapacity,
                        };
                    }
                    else if (!activeEncounterNamesLogged)
                    {
                        activeEncounterNamesLogged = true;
                        unmatchedNames = string.Join(
                            ", ",
                            encounter.Items.Values.Select(item => item.Name));
                    }

                    fallbackSnapshot = CreateChatEncounterSnapshot(
                        finished,
                        identities);
                    snapshot = MergeFallbackDamage(snapshot, fallbackSnapshot);
                    if (snapshot is not null)
                    {
                        activeEncounterPublished |= snapshot.Combatants.Any(static combatant =>
                            combatant.TotalDamage > 0 || combatant.TotalHealing > 0);
                    }

                    if (finished)
                    {
                        if (parityDiagnosticsEnabled() && Volatile.Read(ref parityDiagnosticFaulted) == 0)
                        {
                            var diagnosticStart = encounter.StartTime == DateTime.MaxValue
                                ? startTime
                                : new DateTimeOffset(encounter.StartTime);
                            var diagnosticEnd = encounter.EndTime == DateTime.MinValue
                                ? endTime
                                : new DateTimeOffset(encounter.EndTime);
                            parityCompletion = new ParityCompletionRequest(
                                activeEncounterId,
                                encounter.ZoneName ?? ActGlobals.oFormActMain.CurrentZone ?? string.Empty,
                                encounter.Title ?? string.Empty,
                                diagnosticStart,
                                diagnosticEnd,
                                cachedIdentities,
                                ActGlobals.oFormActMain.DroppedLogLines,
                                ActGlobals.oFormActMain.DroppedCombatActions);
                        }
                        else
                        {
                            parityDiagnosticRecorder.ResetEncounter();
                            Interlocked.Exchange(ref parityDiagnosticFaulted, 0);
                        }

                        raidDpsEstimator.FinishEncounter();
                        effectiveDamageLedger.FinishEncounter();
                        encounterDurationTracker.FinishEncounter();
                        effectiveDamageEncounter = null;
                        activeEncounter = null;

                        activeEncounterId = Guid.Empty;
                        activeEncounterMode = EncounterMode.OpenWorld;
                        activeEncounterTerritoryId = 0;
                        activeEncounterPartyCapacity = 0;
                        activeEncounterIdentities.Clear();
                        activeEncounterPublished = false;
                        activeEncounterNamesLogged = false;
                        activeEncounterRelevantStart = default;
                        lastRelevantCombatAction = default;
                        transitionStateDirty = false;
                        lastKnownDead.Clear();
                        observedDeaths.Clear();
                        ResetChatEncounterUnsafe();
                    }
                }
            }

            if (parityCompletion is not null)
            {
                try
                {
                    var diagnostic = parityDiagnosticRecorder.Complete(
                        parityCompletion.EncounterId,
                        parityCompletion.Zone,
                        parityCompletion.EncounterName,
                        parityCompletion.FightStart,
                        parityCompletion.FightEnd,
                        parityCompletion.Identities,
                        parityCompletion.DroppedRawLogLines,
                        parityCompletion.DroppedNormalizedActions);
                    var output = FflogsParityReportWriter.Write(
                        Path.Combine(
                            pluginInterface.ConfigDirectory.FullName,
                            "logs",
                            "parity"),
                        diagnostic);
                    log.Information(
                        $"FFLogs parity diagnostic written to '{output.JsonPath}' and '{output.MarkdownPath}'.");
                    Interlocked.Exchange(ref parityDiagnosticFaulted, 0);
                }
                catch (Exception ex)
                {
                    // A developer diagnostic is lower priority than encounter publication;
                    // preserve the existing fault-isolation contract on every write failure.
                    log.Warning(ex, "FFLogs parity diagnostic could not be written.");
                    parityDiagnosticRecorder.ResetEncounter();
                    Interlocked.Exchange(ref parityDiagnosticFaulted, 0);
                }
            }

            if (unmatchedNames is not null)
            {
                log.Warning(
                    "ACT encounter has no combatants matching the current player or party. " +
                    $"Raw names: {unmatchedNames}. " +
                    "The network/chat damage fallback will be used for this encounter.");
            }

            if (snapshot is not null)
            {
                EncounterChanged?.Invoke(snapshot, finished);
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to publish ACT encounter snapshot.");
        }
    }

    internal static ActEncounterSnapshot? MergeFallbackDamage(
        ActEncounterSnapshot? primary,
        ActEncounterSnapshot? fallback)
    {
        if (primary is null)
        {
            return fallback;
        }
        if (fallback is null)
        {
            return primary;
        }

        var merged = primary.Combatants.ToList();
        foreach (var fallbackCombatant in fallback.Combatants.Where(static combatant =>
                     combatant.TotalDamage > 0))
        {
            var index = merged.FindIndex(primaryCombatant =>
                string.Equals(
                    primaryCombatant.Id,
                    fallbackCombatant.Id,
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    primaryCombatant.Name,
                    fallbackCombatant.Name,
                    StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                merged.Add(fallbackCombatant);
                continue;
            }

            if (merged[index].TotalDamage > 0)
            {
                // ACT remains authoritative whenever it produced damage. Adding both
                // sources would double-count every normal action line.
                continue;
            }

            var primaryCombatant = merged[index];
            merged[index] = primaryCombatant with
            {
                TotalDamage = fallbackCombatant.TotalDamage,
                Dps = fallbackCombatant.Dps,
                EncDps = fallbackCombatant.EncDps,
                ExtDps = fallbackCombatant.ExtDps,
                DamageHits = fallbackCombatant.DamageHits,
                CriticalHits = fallbackCombatant.CriticalHits,
                CriticalDirectHits = fallbackCombatant.CriticalDirectHits,
                Rdps = fallbackCombatant.Rdps,
                DirectHits = fallbackCombatant.DirectHits,
                HighestDamageAction = fallbackCombatant.HighestDamageAction,
                HighestDamage = fallbackCombatant.HighestDamage,
            };
        }

        // ACT can see only a late fragment when an opcode is stale while the structured
        // fallback still has the first and last damage lines. Keep the widest trustworthy
        // boundary so the header does not collapse a real pull to 00:00/00:01.
        var startTime = primary.StartTime <= fallback.StartTime
            ? primary.StartTime
            : fallback.StartTime;
        var endTime = primary.EndTime is null || fallback.EndTime is null
            ? null
            : primary.EndTime >= fallback.EndTime
                ? primary.EndTime
                : fallback.EndTime;
        return primary with
        {
            StartTime = startTime,
            EndTime = endTime,
            Combatants = merged,
        };
    }

    private void ObserveParityRawLine(string rawLine)
    {
        if (Volatile.Read(ref parityDiagnosticFaulted) != 0)
        {
            return;
        }
        try
        {
            parityDiagnosticRecorder.ObserveRawLine(rawLine);
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref parityDiagnosticFaulted, 1) == 0)
            {
                log.Warning(ex, "FFLogs parity raw observer was isolated for the current encounter.");
            }
        }
    }

    private void ObserveParityCombatAction(
        MasterSwing swing,
        IReadOnlyList<ActPlayerIdentity> identities,
        ActPlayerIdentity? attackerIdentity,
        bool hasEncounter)
    {
        if (Volatile.Read(ref parityDiagnosticFaulted) != 0)
        {
            return;
        }
        try
        {
            parityDiagnosticRecorder.ObserveCombatAction(
                swing,
                identities,
                attackerIdentity,
                hasEncounter);
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref parityDiagnosticFaulted, 1) == 0)
            {
                log.Warning(ex, "FFLogs parity normalized observer was isolated for the current encounter.");
            }
        }
    }

    private static HashSet<string> LoadLimitBreakActionNames(IDataManager dataManager)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var action in dataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>())
        {
            if (!IsDamageDealingLimitBreakAction(
                    action.ActionCategory.RowId,
                    action.CanTargetHostile))
            {
                continue;
            }

            var name = action.Name.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(name))
            {
                names.Add(name);
            }
        }
        return names;
    }

    internal static bool IsDamageDealingLimitBreakAction(
        uint actionCategoryId,
        bool canTargetHostile)
    {
        // Tank and healer limit breaks share the LB action categories but emit no damage.
        // Treating them as damage context lets the next unrelated split combat line leak into
        // the synthetic LB row, so only hostile-targeting LB actions may own damage output.
        return actionCategoryId is 9 or 15 && canTargetHostile;
    }

    private static HashSet<uint> LoadWeaponskillActionIds(IDataManager dataManager)
        => dataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>()
            .Where(static action => action.ActionCategory.RowId == 3)
            .Select(static action => action.RowId)
            .ToHashSet();

    private void RestoreHtmlOverlays()
    {
        foreach (var name in SelectHtmlOverlaysToRestore(getOverlayWindowSettingsSnapshot()))
        {
            try
            {
                if (ShowHtmlOverlay(name))
                {
                    log.Information($"Restored HTML overlay '{name}' from its saved open state.");
                }
                else
                {
                    log.Warning(
                        $"Saved HTML overlay '{name}' could not be restored. " +
                        "Its template or custom URL is unavailable.");
                }
            }
            catch (Exception ex)
            {
                log.Warning(ex, $"Saved HTML overlay '{name}' could not be restored.");
            }
        }
    }

    internal static IReadOnlyList<string> SelectHtmlOverlaysToRestore(
        IReadOnlyDictionary<string, HtmlOverlayWindowSettings> settings)
    {
        var names = settings
            .Where(pair =>
                pair.Value.OpenOnStartup &&
                !string.IsNullOrWhiteSpace(pair.Key))
            .Select(static pair => NormalizeCactbotOverlayName(pair.Key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (names.Contains(CactbotAlertsOverlayName) ||
            names.Contains(CactbotTimelineOverlayName))
        {
            names.Remove(CactbotOverlayName);
        }

        return names
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void CacheActiveEncounterIdentities(IReadOnlyList<ActPlayerIdentity> identities)
    {
        foreach (var identity in identities)
        {
            var staleKeys = activeEncounterIdentities
                .Where(pair =>
                    (identity.ContentId != 0 && pair.Value.ContentId == identity.ContentId) ||
                    (identity.EntityId != 0 && pair.Value.EntityId == identity.EntityId) ||
                    string.Equals(
                        pair.Value.DisplayName,
                        identity.DisplayName,
                        StringComparison.OrdinalIgnoreCase))
                .Select(static pair => pair.Key)
                .ToArray();
            foreach (var staleKey in staleKeys)
            {
                activeEncounterIdentities.Remove(staleKey);
            }

            var key = identity.ContentId != 0
                ? $"content:{identity.ContentId}"
                : identity.DisplayName;
            activeEncounterIdentities[key] = identity;
        }
    }

    internal static IReadOnlyList<(CombatantData Combatant, ActPlayerIdentity? Identity)>
        ResolveEncounterCombatants(
            EncounterData encounter,
            IReadOnlyList<ActPlayerIdentity> liveIdentities,
            IReadOnlyList<ActPlayerIdentity> cachedIdentities,
            int partyCapacity)
    {
        var partyIdentities = ResolvePartyIdentities(
            liveIdentities,
            cachedIdentities,
            partyCapacity);
        var allies = encounter.GetAllies();
        if (allies.Count == 0)
        {
            allies = encounter.Items.Values.ToList();
        }

        // ACT's relationship-based ally graph can contain pets and friendly NPCs. Dalamud's
        // live and encounter-scoped party identities are the authoritative player whitelist.
        // Limit Break remains a separate synthetic team-damage row for compatibility.
        return allies
            .Select(combatant =>
            {
                var identity = ActPlayerIdentityResolver.Resolve(partyIdentities, combatant.Name);
                return (Combatant: combatant, Identity: identity);
            })
            .Where(item =>
                item.Identity is not null ||
                string.Equals(
                    item.Combatant.Name,
                    ChineseCombatChatContext.LimitBreakActorName,
                    StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private static IReadOnlyList<ActPlayerIdentity> ResolvePartyIdentities(
        IReadOnlyList<ActPlayerIdentity> liveIdentities,
        IReadOnlyList<ActPlayerIdentity> cachedIdentities,
        int partyCapacity)
    {
        var boundedCapacity = Math.Clamp(partyCapacity, 0, 24);
        if (boundedCapacity == 0)
        {
            return [];
        }

        var resolved = new List<ActPlayerIdentity>(boundedCapacity);
        foreach (var identity in liveIdentities.Concat(cachedIdentities))
        {
            if (resolved.Any(existing => IsSamePlayerIdentity(existing, identity)))
            {
                continue;
            }

            resolved.Add(identity);
            if (resolved.Count == boundedCapacity)
            {
                break;
            }
        }

        return resolved;
    }

    private static bool IsSamePlayerIdentity(
        ActPlayerIdentity left,
        ActPlayerIdentity right)
        => (left.ContentId != 0 && left.ContentId == right.ContentId) ||
           (left.EntityId != 0 && left.EntityId == right.EntityId) ||
           string.Equals(left.DisplayName, right.DisplayName, StringComparison.OrdinalIgnoreCase);

    private CombatantHitCounts GetDamageHitCounts(CombatantData combatant)
    {
        var allDamage = combatant.Items.TryGetValue(
                CombatantData.DamageTypeDataOutgoingDamage,
                out var outgoingDamage)
            ? outgoingDamage.Items.Values.MaxBy(static attack => attack.Hits)
            : null;
        if (allDamage is null)
        {
            return new CombatantHitCounts(
                Math.Max(0, combatant.Hits),
                Math.Max(0, combatant.CritHits),
                0,
                0);
        }

        if (!damageHitCounters.TryGetValue(combatant.Name, out var counter) ||
            !ReferenceEquals(counter.Source, allDamage) ||
            counter.ProcessedSwings > allDamage.Items.Count)
        {
            counter = new DamageHitCounter(allDamage);
        }

        for (var index = counter.ProcessedSwings; index < allDamage.Items.Count; index++)
        {
            var swing = allDamage.Items[index];
            var isDirectHit = swing.Tags.TryGetValue("DirectHit", out var directHit) &&
                              string.Equals(directHit?.ToString(), "True", StringComparison.Ordinal);
            if (isDirectHit)
            {
                counter.DirectHits++;
            }
            if (swing.Critical && isDirectHit)
            {
                counter.CriticalDirectHits++;
            }
        }

        counter.ProcessedSwings = allDamage.Items.Count;
        damageHitCounters[combatant.Name] = counter;

        return new CombatantHitCounts(
            Math.Max(0, combatant.Hits),
            Math.Max(0, combatant.CritHits),
            counter.DirectHits,
            counter.CriticalDirectHits);
    }

    internal static HighestDamageHit GetHighestDamageHit(CombatantData combatant)
    {
        // AllOut also contains outgoing healing. Restricting the aggregate here keeps a
        // healer's largest cure out of the damage column and binds the action to this combatant.
        var allDamage = combatant.Items.TryGetValue(
                CombatantData.DamageTypeDataOutgoingDamage,
                out var outgoingDamage)
            ? outgoingDamage.Items.Values.MaxBy(static attack => attack.Hits)
            : null;
        var swing = allDamage?.Items
            .Where(static item => (long)item.Damage > 0)
            .MaxBy(static item => (long)item.Damage);
        return swing is null
            ? new HighestDamageHit(string.Empty, 0)
            : new HighestDamageHit(swing.AttackType ?? string.Empty, (long)swing.Damage);
    }

    internal readonly record struct HighestDamageHit(string Action, long Amount);

    private sealed class DamageHitCounter(AttackType source)
    {
        public AttackType Source { get; } = source;

        public int ProcessedSwings { get; set; }

        public int DirectHits { get; set; }

        public int CriticalDirectHits { get; set; }
    }

    private sealed record ParityCompletionRequest(
        Guid EncounterId,
        string Zone,
        string EncounterName,
        DateTimeOffset? FightStart,
        DateTimeOffset? FightEnd,
        IReadOnlyList<ActPlayerIdentity> Identities,
        long DroppedRawLogLines,
        long DroppedNormalizedActions);

    private sealed class OverlayConnectionAttempt(
        OverlayConnectionMode mode) : IDisposable
    {
        private readonly CancellationTokenSource cancellation = new();

        public OverlayConnectionMode Mode { get; set; } = mode;

        public CancellationToken Token => cancellation.Token;

        public void Cancel() => cancellation.Cancel();

        public void Dispose() => cancellation.Dispose();
    }

    private readonly record struct CombatantHitCounts(
        int DamageHits,
        int CriticalHits,
        int DirectHits,
        int CriticalDirectHits);

    private void TrackPlayerDeaths(IReadOnlyList<ActPlayerIdentity> identities)
    {
        EncounterData? encounterToPublish = null;
        lock (encounterSync)
        {
            if (activeEncounter is null && chatEncounterId == Guid.Empty)
            {
                return;
            }

            var changed = false;
            foreach (var identity in identities)
            {
                var name = identity.DisplayName;
                if (!lastKnownDead.TryGetValue(name, out var wasDead))
                {
                    if (identity.IsDead)
                    {
                        observedDeaths[name] = Math.Max(
                            1,
                            observedDeaths.GetValueOrDefault(name));
                        changed = true;
                    }
                }
                else if (!wasDead && identity.IsDead)
                {
                    observedDeaths[name] = observedDeaths.GetValueOrDefault(name) + 1;
                    changed = true;
                }

                lastKnownDead[name] = identity.IsDead;
            }

            if (changed)
            {
                if (activeEncounterPublished && activeEncounter is not null)
                {
                    encounterToPublish = activeEncounter;
                }
                else if (chatEncounterId != Guid.Empty)
                {
                    chatEncounterDirty = true;
                }
            }
        }

        if (encounterToPublish is not null)
        {
            PublishEncounter(encounterToPublish, false);
        }
    }

    private void ResetChatEncounterUnsafe()
    {
        chatEncounterId = Guid.Empty;
        chatEncounterMode = EncounterMode.OpenWorld;
        chatEncounterTerritoryId = 0;
        chatEncounterStart = default;
        chatLastDamage = default;
        chatEncounterDirty = false;
        chatEncounterPublished = false;
        chatDamageTotals.Clear();
        chatDamageHitTotals.Clear();
        chatCriticalHitTotals.Clear();
        chatDirectHitTotals.Clear();
        chatCriticalDirectHitTotals.Clear();
        chatHighestDamageActions.Clear();
        chatHighestDamageTotals.Clear();
        recentNetworkDamage.Clear();
        chatParser.Clear();
        chatEnemy = string.Empty;
        chatZone = string.Empty;
    }

    private readonly record struct RecentNetworkDamage(
        DateTimeOffset ObservedAt,
        string Actor,
        string Target,
        long Damage);

    private void SetUpstreamLogger()
    {
        var property = typeof(IINACT.Plugin).GetProperty(
            nameof(IINACT.Plugin.Log),
            BindingFlags.Public | BindingFlags.Static);
        property?.SetValue(null, log);
    }
}

public sealed record ActPluginRuntimeStatus(
    string Id,
    string Status,
    IReadOnlyList<CompatibilityStageResult> Stages,
    IReadOnlyList<ActPluginDiagnostic> Diagnostics);

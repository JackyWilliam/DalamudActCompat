using Advanced_Combat_Tracker;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using System.Reflection;
using System.Windows.Forms;

namespace DalamudActCompat.ActRuntime;

public sealed class SelfHostedActRuntime : IDisposable
{
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;
    private readonly IDataManager dataManager;
    private readonly Func<string> playerName;
    private readonly Func<IReadOnlyList<ActPlayerIdentity>> playerIdentities;
    private readonly IChatGui chatGui;
    private readonly IFramework framework;
    private readonly ICondition condition;
    private readonly IGameInteropProvider gameInteropProvider;
    private readonly INotificationManager notificationManager;
    private readonly Func<bool> localDeathWhilePartyContinues;
    private IINACT.FfxivActPluginWrapper? parser;
    private ActPluginData? parserPluginData;
    private IINACT.Network.ZoneDownHookManager? zoneDownHookManager;
    private RainbowMage.OverlayPlugin.PluginMain? overlay;
    private CactbotOverlayForm? cactbotOverlay;
    private HttpClient? httpClient;
    private readonly List<LoadedActPlugin> customPlugins = [];
    private bool actGlobalsInitialized;
    private EncounterData? activeEncounter;
    private Guid activeEncounterId;
    private readonly Dictionary<string, bool> lastKnownDead = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> observedDeaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> chatDamageTotals = new(StringComparer.OrdinalIgnoreCase);
    private Guid chatEncounterId;
    private DateTimeOffset chatEncounterStart;
    private DateTimeOffset chatLastDamage;
    private string chatActor = string.Empty;
    private string chatEnemy = string.Empty;
    private string chatZone = string.Empty;

    public SelfHostedActRuntime(
        IDalamudPluginInterface pluginInterface,
        IPluginLog log,
        IDataManager dataManager,
        Func<string> playerName,
        Func<IReadOnlyList<ActPlayerIdentity>> playerIdentities,
        IChatGui chatGui,
        IFramework framework,
        ICondition condition,
        IGameInteropProvider gameInteropProvider,
        INotificationManager notificationManager,
        Func<bool> localDeathWhilePartyContinues)
    {
        this.pluginInterface = pluginInterface;
        this.log = log;
        this.dataManager = dataManager;
        this.playerName = playerName;
        this.playerIdentities = playerIdentities;
        this.chatGui = chatGui;
        this.framework = framework;
        this.condition = condition;
        this.gameInteropProvider = gameInteropProvider;
        this.notificationManager = notificationManager;
        this.localDeathWhilePartyContinues = localDeathWhilePartyContinues;
    }

    public bool IsParserRunning => parser is not null;

    public bool IsOverlayRunning => overlay is not null;

    public bool ShowCactbotOverlay()
    {
        if (cactbotOverlay is null)
        {
            return false;
        }

        cactbotOverlay.Show();
        return true;
    }

    public IReadOnlyList<string> LoadedCustomPluginIds
        => customPlugins.Select(plugin => plugin.Id).ToArray();

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

    public IINACT.FfxivActPluginWrapper Parser
        => parser ?? throw new InvalidOperationException("FFXIV_ACT_Plugin is not running.");

    public void StartParser(string logDirectory)
    {
        if (IsParserRunning)
        {
            return;
        }

        Directory.CreateDirectory(logDirectory);
        Directory.CreateDirectory(Path.Combine(pluginInterface.ConfigDirectory.FullName, "Config"));
        SetUpstreamLogger();
        ActGlobals.Init();
        actGlobalsInitialized = true;
        ActGlobals.oFormActMain = new FormActMain(log)
        {
            AppDataFolder = pluginInterface.ConfigDirectory,
            LogFilePath = logDirectory,
            WriteLogFile = true,
        };
        ActGlobals.oFormActMain.BeforeLogLineRead += OnBeforeLogLineRead;
        ActGlobals.oFormActMain.AfterCombatAction += OnAfterCombatAction;
        ActGlobals.oFormActMain.AfterCombatEnd += OnAfterCombatEnd;

        var configuration = new IINACT.Configuration
        {
            LogFilePath = logDirectory,
            WriteLogFile = true,
        };
        configuration.Initialize(pluginInterface);
        try
        {
            IINACT.FfxivActPluginWrapper.ConfigureRegion(dataManager.Language);
            zoneDownHookManager = new IINACT.Network.ZoneDownHookManager(
                notificationManager,
                gameInteropProvider);
            parser = new IINACT.FfxivActPluginWrapper(
                configuration,
                dataManager.Language,
                chatGui,
                framework,
                condition);
            parserPluginData = RegisterSystemPlugin(
                parser.ActPluginInstance,
                "FFXIV_ACT_Plugin.dll");
            framework.Update += OnFrameworkUpdate;
        }
        catch
        {
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

        httpClient = new HttpClient();
        var container = new RainbowMage.OverlayPlugin.TinyIoCContainer();
        var overlayLogger = new RainbowMage.OverlayPlugin.Logger(log);
        container.Register(overlayLogger);
        container.Register<RainbowMage.OverlayPlugin.ILogger>(overlayLogger);
        container.Register(httpClient);
        container.Register(new FileDialogManager());
        container.Register(pluginInterface);

        try
        {
            overlay = new RainbowMage.OverlayPlugin.PluginMain(
                pluginInterface.AssemblyLocation.Directory!.FullName,
                overlayLogger,
                container);
            container.Register(overlay);
            ActGlobals.oFormActMain.OverlayPluginContainer = container;
            overlay.InitPlugin(pluginInterface.ConfigDirectory.FullName);
            var server = container.Resolve<RainbowMage.OverlayPlugin.WebSocket.ServerController>();
            var raidbossHtml = Path.Combine(
                pluginInterface.ConfigDirectory.FullName,
                "cactbot",
                "ui",
                "raidboss",
                "raidboss.html");
            if (File.Exists(raidbossHtml))
            {
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
                cactbotOverlay = new CactbotOverlayForm(
                    raidbossHtml,
                    webSocketUri,
                    Path.Combine(pluginInterface.ConfigDirectory.FullName, "webview2"),
                    Path.Combine(
                        pluginInterface.AssemblyLocation.Directory!.FullName,
                        "WebView2Loader.dll"),
                    log);
                cactbotOverlay.Show();
            }
        }
        catch
        {
            overlay = null;
            httpClient.Dispose();
            httpClient = null;
            throw;
        }
    }

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
                var loadedPlugin = LoadedActPlugin.Load(plugin);
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

    public void StopOverlay()
    {
        cactbotOverlay?.Dispose();
        cactbotOverlay = null;
        overlay?.DeInitPlugin();
        overlay = null;
        httpClient?.Dispose();
        httpClient = null;
    }

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

    public void Dispose() => StopParser();

    private void OnBeforeLogLineRead(bool isImport, LogLineEventArgs logInfo)
    {
        if (isImport)
        {
            return;
        }

        var fields = logInfo.originalLogLine.Split('|');
        if (fields.Length < 5 || fields[0] != "00")
        {
            return;
        }

        if (!ChineseCombatChatParser.TryParse(
                fields[4],
                chatActor,
                out chatActor,
                out var target,
                out var damage))
        {
            return;
        }

        var now = new DateTimeOffset(logInfo.detectedTime);
        if (chatEncounterId == Guid.Empty || now - chatLastDamage > TimeSpan.FromSeconds(30))
        {
            PublishChatEncounter(finished: chatEncounterId != Guid.Empty);
            chatEncounterId = Guid.NewGuid();
            chatEncounterStart = now;
            chatDamageTotals.Clear();
        }

        chatLastDamage = now;
        chatEnemy = target;
        chatZone = logInfo.detectedZone ?? ActGlobals.oFormActMain.CurrentZone ?? string.Empty;
        chatDamageTotals[chatActor] = chatDamageTotals.GetValueOrDefault(chatActor) + damage;
        PublishChatEncounter(finished: false);
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        TrackPlayerDeaths();
        if (chatEncounterId == Guid.Empty || chatLastDamage == default ||
            condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat] ||
            localDeathWhilePartyContinues() ||
            DateTimeOffset.Now - chatLastDamage < TimeSpan.FromSeconds(3))
        {
            return;
        }

        PublishChatEncounter(finished: true);
        chatEncounterId = Guid.Empty;
        chatDamageTotals.Clear();
        chatActor = string.Empty;
    }

    private void PublishChatEncounter(bool finished)
    {
        if (chatEncounterId == Guid.Empty)
        {
            return;
        }

        var localPlayerName = playerName();
        if (string.IsNullOrWhiteSpace(localPlayerName))
        {
            localPlayerName = ActGlobals.charName ?? string.Empty;
        }

        var identities = playerIdentities();
        var elapsedSeconds = Math.Max(
            1,
            ((finished ? chatLastDamage : DateTimeOffset.Now) - chatEncounterStart).TotalSeconds);
        var combatants = chatDamageTotals
            .Select(pair => (Pair: pair, Identity: ActPlayerIdentityResolver.Resolve(identities, pair.Key)))
            .Where(static item => item.Identity is not null)
            .Select(item => new ActCombatantSnapshot(
                item.Identity!.DisplayName,
                item.Identity.DisplayName,
                item.Identity.Job,
                item.Identity.IsLocalPlayer,
                item.Pair.Value,
                0,
                observedDeaths.GetValueOrDefault(item.Identity.DisplayName),
                item.Pair.Value / elapsedSeconds,
                item.Pair.Value / elapsedSeconds,
                item.Pair.Value / elapsedSeconds))
            .ToArray();
        EncounterChanged?.Invoke(
            new ActEncounterSnapshot(
                chatEncounterId,
                chatEncounterStart,
                finished ? chatLastDamage : null,
                chatZone,
                string.IsNullOrWhiteSpace(chatEnemy) ? "Encounter" : chatEnemy,
                combatants),
            finished);
    }

    private void OnAfterCombatAction(bool isImport, CombatActionEventArgs action)
    {
        if (isImport)
        {
            return;
        }

        var encounter = action.combatAction.ParentEncounter;
        if (encounter is null)
        {
            return;
        }

        PublishEncounter(encounter, false);
    }

    private void OnAfterCombatEnd(EncounterData encounter)
        => PublishEncounter(encounter, true);

    private void PublishEncounter(EncounterData encounter, bool finished)
    {
        try
        {
            ActEncounterSnapshot snapshot;
            lock (ActGlobals.oFormActMain.AfterCombatActionDataLock)
            {
                if (!ReferenceEquals(activeEncounter, encounter))
                {
                    activeEncounter = encounter;
                    activeEncounterId = Guid.NewGuid();
                    lastKnownDead.Clear();
                    observedDeaths.Clear();
                }

                var startTime = encounter.StartTime == DateTime.MaxValue
                    ? DateTimeOffset.Now
                    : new DateTimeOffset(encounter.StartTime);
                DateTimeOffset? endTime = finished
                    ? encounter.EndTime == DateTime.MinValue
                        ? DateTimeOffset.Now
                        : new DateTimeOffset(encounter.EndTime)
                    : null;
                var identities = playerIdentities();
                var combatants = encounter.Items.Values
                    .Select(combatant => (
                        Combatant: combatant,
                        Identity: ActPlayerIdentityResolver.Resolve(identities, combatant.Name)))
                    .Where(static item => item.Identity is not null)
                    .Select(item => new ActCombatantSnapshot(
                        item.Identity!.DisplayName,
                        item.Identity.DisplayName,
                        item.Identity.Job,
                        item.Identity.IsLocalPlayer,
                        item.Combatant.Damage,
                        item.Combatant.Healed,
                        Math.Max(
                            item.Combatant.Deaths,
                            observedDeaths.GetValueOrDefault(item.Identity.DisplayName)),
                        item.Combatant.DPS,
                        item.Combatant.EncDPS,
                        item.Combatant.ExtDPS))
                    .ToArray();

                snapshot = new ActEncounterSnapshot(
                    activeEncounterId,
                    startTime,
                    endTime,
                    encounter.ZoneName ?? ActGlobals.oFormActMain.CurrentZone ?? string.Empty,
                    encounter.Title ?? string.Empty,
                    combatants);

                if (finished)
                {
                    activeEncounter = null;
                    activeEncounterId = Guid.Empty;
                    lastKnownDead.Clear();
                    observedDeaths.Clear();
                }
            }

            EncounterChanged?.Invoke(snapshot, finished);
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to publish ACT encounter snapshot.");
        }
    }

    private void TrackPlayerDeaths()
    {
        if (activeEncounter is null)
        {
            return;
        }

        var changed = false;
        foreach (var identity in playerIdentities())
        {
            var name = identity.DisplayName;
            if (lastKnownDead.TryGetValue(name, out var wasDead) && !wasDead && identity.IsDead)
            {
                observedDeaths[name] = observedDeaths.GetValueOrDefault(name) + 1;
                changed = true;
            }

            lastKnownDead[name] = identity.IsDead;
        }

        if (changed && activeEncounter is not null)
        {
            PublishEncounter(activeEncounter, false);
        }
    }

    private void SetUpstreamLogger()
    {
        var property = typeof(IINACT.Plugin).GetProperty(
            nameof(IINACT.Plugin.Log),
            BindingFlags.Public | BindingFlags.Static);
        property?.SetValue(null, log);
    }
}

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
    private readonly Func<uint> playerJobId;
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
    private HttpClient? httpClient;
    private readonly List<LoadedActPlugin> customPlugins = [];
    private bool actGlobalsInitialized;
    private EncounterData? activeEncounter;
    private Guid activeEncounterId;
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
        Func<uint> playerJobId,
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
        this.playerJobId = playerJobId;
        this.chatGui = chatGui;
        this.framework = framework;
        this.condition = condition;
        this.gameInteropProvider = gameInteropProvider;
        this.notificationManager = notificationManager;
        this.localDeathWhilePartyContinues = localDeathWhilePartyContinues;
    }

    public bool IsParserRunning => parser is not null;

    public bool IsOverlayRunning => overlay is not null;

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
        foreach (var plugin in plugins)
        {
            try
            {
                customPlugins.Add(LoadedActPlugin.Load(plugin));
                log.Information($"ACT plugin '{plugin.Id}' loaded.");
            }
            catch (Exception ex)
            {
                log.Error(ex, $"ACT plugin '{plugin.Id}' failed to load.");
                failures.Add((plugin.Id, ex));
            }
        }

        return failures;
    }

    public void StopOverlay()
    {
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

        var localJob = ResolveJob(playerJobId());
        var elapsedSeconds = Math.Max(
            1,
            ((finished ? chatLastDamage : DateTimeOffset.Now) - chatEncounterStart).TotalSeconds);
        var combatants = chatDamageTotals
            .Select(pair => new ActCombatantSnapshot(
                pair.Key,
                pair.Key,
                string.Equals(pair.Key, localPlayerName, StringComparison.OrdinalIgnoreCase) ? localJob : string.Empty,
                string.Equals(pair.Key, localPlayerName, StringComparison.OrdinalIgnoreCase),
                pair.Value,
                0,
                0,
                pair.Value / elapsedSeconds,
                pair.Value / elapsedSeconds,
                pair.Value / elapsedSeconds))
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
                }

                var startTime = encounter.StartTime == DateTime.MaxValue
                    ? DateTimeOffset.Now
                    : new DateTimeOffset(encounter.StartTime);
                DateTimeOffset? endTime = finished
                    ? encounter.EndTime == DateTime.MinValue
                        ? DateTimeOffset.Now
                        : new DateTimeOffset(encounter.EndTime)
                    : null;
                var combatants = encounter.Items.Values
                    .Where(static combatant => combatant.Damage > 0 || combatant.Healed > 0 || combatant.Deaths > 0)
                    .Select(combatant => new ActCombatantSnapshot(
                        combatant.Name,
                        combatant.Name,
                        ResolveJob(combatant),
                        string.Equals(combatant.Name, ActGlobals.charName, StringComparison.OrdinalIgnoreCase),
                        combatant.Damage,
                        combatant.Healed,
                        combatant.Deaths,
                        combatant.DPS,
                        combatant.EncDPS,
                        combatant.ExtDPS))
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
                }
            }

            EncounterChanged?.Invoke(snapshot, finished);
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to publish ACT encounter snapshot.");
        }
    }

    private static string ResolveJob(CombatantData combatant)
    {
        if (combatant.Tags.TryGetValue("Job", out var job) && job is not null)
        {
            return job.ToString() ?? string.Empty;
        }

        return string.Empty;
    }

    private static string ResolveJob(uint? jobId)
        => jobId switch
        {
            19 => "PLD", 20 => "MNK", 21 => "WAR", 22 => "DRG", 23 => "BRD",
            24 => "WHM", 25 => "BLM", 26 => "ACN", 27 => "SMN", 28 => "SCH",
            30 => "NIN", 31 => "MCH", 32 => "DRK", 33 => "AST", 34 => "SAM",
            35 => "RDM", 36 => "BLU", 37 => "GNB", 38 => "DNC", 39 => "RPR",
            40 => "SGE", 41 => "VPR", 42 => "PCT",
            _ => string.Empty,
        };

    private void SetUpstreamLogger()
    {
        var property = typeof(IINACT.Plugin).GetProperty(
            nameof(IINACT.Plugin.Log),
            BindingFlags.Public | BindingFlags.Static);
        property?.SetValue(null, log);
    }
}

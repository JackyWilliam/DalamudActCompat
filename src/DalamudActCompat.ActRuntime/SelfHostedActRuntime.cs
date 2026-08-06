using Advanced_Combat_Tracker;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using System.Collections.Concurrent;
using System.Drawing;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;

namespace DalamudActCompat.ActRuntime;

public sealed class SelfHostedActRuntime : IDisposable
{
    public const string CactbotOverlayName = "Cactbot Raidboss";

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
    private readonly Func<string, HtmlOverlayWindowSettings> getOverlayWindowSettings;
    private readonly Func<bool> debugMode;
    private readonly CachedDalamudGameStateProvider gameStateProvider = new();
    private readonly object encounterSync = new();
    private readonly Dictionary<string, CriticalDirectHitCounter> criticalDirectHitCounters =
        new(StringComparer.OrdinalIgnoreCase);
    private IINACT.FfxivActPluginWrapper? parser;
    private ActPluginData? parserPluginData;
    private IINACT.Network.ZoneDownHookManager? zoneDownHookManager;
    private RainbowMage.OverlayPlugin.PluginMain? overlay;
    private HtmlOverlayForm? cactbotOverlay;
    private HtmlOverlayForm? cactbotSettings;
    private readonly ConcurrentDictionary<string, HtmlOverlayForm> htmlOverlays =
        new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<ActOverlayTemplate> overlayTemplates = [];
    private IReadOnlyList<OverlayTemplateSource> overlayTemplateSources = [];
    private string? overlayWebSocketUri;
    private HttpClient? httpClient;
    private Func<string, bool>? externalTtsDispatcher;
    private Func<string, string, bool>? externalPostNamazuDispatcher;
    private PostNamazuEventSource? externalPostNamazuEventSource;
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
    private DateTimeOffset activeEncounterRelevantStart;
    private DateTimeOffset lastRelevantCombatAction;
    private bool chatEncounterDirty;
    private bool chatEncounterPublished;
    private readonly ChineseCombatChatContext chatParser = new();
    private string chatEnemy = string.Empty;
    private string chatZone = string.Empty;
    private bool activeEncounterPublished;
    private bool activeEncounterNamesLogged;

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
        ISigScanner sigScanner,
        INotificationManager notificationManager,
        Func<string, uint?> resolveActorId,
        Func<bool> localDeathWhilePartyContinues,
        Func<string, HtmlOverlayWindowSettings> getOverlayWindowSettings,
        Func<bool> debugMode,
        Func<string, ActCapability, bool> permissionCheck)
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
        this.getOverlayWindowSettings = getOverlayWindowSettings;
        this.debugMode = debugMode;
        NativePostNamazuBridge.Configure(framework, log, sigScanner, resolveActorId);
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

    public bool ShowCactbotOverlay()
    {
        if (cactbotOverlay is null)
        {
            return false;
        }

        cactbotOverlay.Show();
        return true;
    }

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

    public IReadOnlyList<ActOverlayTemplate> OverlayTemplates => overlayTemplates;

    public bool ApplyOverlayWindowSettings(string name)
    {
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
        if (template is not null)
        {
            var source = overlayTemplateSources.First(
                candidate => string.Equals(candidate.Name, template.Name, StringComparison.OrdinalIgnoreCase));
            pageUri = BuildTemplateUri(source, new Uri(overlayWebSocketUri));
            clientSize = new Size(template.Width, template.Height);
            windowName = template.Name;
            userDataDirectory = Path.Combine(
                pluginInterface.ConfigDirectory.FullName,
                "webview2",
                SanitizePath(template.Name));
        }
        else if (TryBuildCustomOverlayUri(
                     settings.SourceUrl,
                     new Uri(overlayWebSocketUri),
                     out pageUri))
        {
            clientSize = new Size(900, 500);
            windowName = name;
            userDataDirectory = Path.Combine(
                pluginInterface.ConfigDirectory.FullName,
                "webview2",
                BuildCustomOverlayProfileName(name));
        }
        else
        {
            return false;
        }

        if (!htmlOverlays.TryGetValue(windowName, out var window))
        {
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
                log);
            if (!htmlOverlays.TryAdd(windowName, window))
            {
                window.Dispose();
                window = htmlOverlays[windowName];
            }
        }

        window.Show();
        return true;
    }

    public bool HideHtmlOverlay(string name)
    {
        var settings = getOverlayWindowSettings(name);
        settings.IsVisible = false;
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
        if (string.IsNullOrWhiteSpace(name) ||
            string.Equals(name, CactbotOverlayName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
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
            // IINACT registers its formatter in the wrapper constructor. Subscribe after it
            // so the external ACT Host receives both the raw pipe line and ACT's legacy line.
            ActGlobals.oFormActMain.BeforeLogLineRead += OnBeforeLogLineRead;
            parser.Subscription.ZoneChanged += OnZoneChangedForHost;
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
        gameStateProvider.Update(
            playerIdentities(),
            condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat]);
        container.Register<RainbowMage.OverlayPlugin.MemoryProcessors.IDalamudGameStateProvider>(
            gameStateProvider);
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
            overlayTemplates = overlayTemplateSources
                .Where(template => !template.Features.Contains("system"))
                .Select(template => new ActOverlayTemplate(
                    template.Name,
                    template.Uri,
                    template.SuggestedWidth ?? 900,
                    template.SuggestedHeight ?? 500))
                .ToArray();

            var raidbossHtml = Path.Combine(
                pluginInterface.ConfigDirectory.FullName,
                "cactbot",
                "ui",
                "raidboss",
                "raidboss.html");
            if (File.Exists(raidbossHtml))
            {
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
                    log);
                cactbotOverlay.Show();

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
                        log);
                }
            }
        }
        catch
        {
            cactbotOverlay?.Dispose();
            cactbotOverlay = null;
            cactbotSettings?.Dispose();
            cactbotSettings = null;
            foreach (var window in htmlOverlays.Values)
            {
                window.Dispose();
            }
            htmlOverlays.Clear();
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
            overlay = null;
            httpClient.Dispose();
            httpClient = null;
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
                template.SuggestedHeight ?? 500))
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
        out Uri pageUri)
    {
        if (!TryNormalizeCustomOverlayUri(sourceUrl, out pageUri))
        {
            return false;
        }

        if (!HasOverlayParameter(pageUri, "OVERLAY_WS"))
        {
            pageUri = BuildOverlayUri(
                pageUri,
                "OVERLAY_WS",
                webSocketUri.ToString());
        }
        if (!HasOverlayParameter(pageUri, "HOST_PORT"))
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

    public void StopOverlay()
    {
        externalPostNamazuEventSource?.SetAction(null);
        externalPostNamazuEventSource = null;
        cactbotOverlay?.Dispose();
        cactbotOverlay = null;
        cactbotSettings?.Dispose();
        cactbotSettings = null;
        foreach (var window in htmlOverlays.Values)
        {
            window.Dispose();
        }
        htmlOverlays.Clear();
        overlayTemplates = [];
        overlayTemplateSources = [];
        overlayWebSocketUri = null;
        overlay?.DeInitPlugin();
        overlay = null;
        httpClient?.Dispose();
        httpClient = null;
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
            activeEncounter = null;
            activeEncounterId = Guid.Empty;
            activeEncounterPublished = false;
            activeEncounterNamesLogged = false;
            activeEncounterRelevantStart = default;
            lastRelevantCombatAction = default;
            lastKnownDead.Clear();
            observedDeaths.Clear();
            ResetChatEncounterUnsafe();
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

        var fields = logInfo.originalLogLine.Split('|');
        if (fields.Length < 5 || fields[0] != "00")
        {
            return;
        }

        ActEncounterSnapshot? completedEncounter = null;
        lock (encounterSync)
        {
            var message = fields[4];
            var now = new DateTimeOffset(logInfo.detectedTime);
            if (!chatParser.TryParse(
                    message,
                    now,
                    out var actor,
                    out var target,
                    out var damage))
            {
                return;
            }

            // Keep split combat lines together for at most two seconds, but only let the
            // local player or party members create and extend this plugin's encounter.
            var identities = gameStateProvider.Identities;
            if (ActPlayerIdentityResolver.Resolve(identities, actor) is null)
            {
                return;
            }

            if (chatEncounterId == Guid.Empty || now - chatLastDamage > TimeSpan.FromSeconds(30))
            {
                if (chatEncounterPublished && !activeEncounterPublished)
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
                chatEncounterStart = now;
            }

            chatLastDamage = now;
            chatEnemy = target;
            chatZone = logInfo.detectedZone ?? ActGlobals.oFormActMain.CurrentZone ?? string.Empty;
            chatDamageTotals[actor] = chatDamageTotals.GetValueOrDefault(actor) + damage;
            chatEncounterDirty = true;
        }

        if (completedEncounter is not null)
        {
            EncounterChanged?.Invoke(completedEncounter, true);
        }
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        var identities = playerIdentities();
        var inCombat = condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat];
        gameStateProvider.Update(
            identities,
            inCombat);
        TrackPlayerDeaths(identities);

        ActEncounterSnapshot? activeChatEncounter = null;
        ActEncounterSnapshot? completedChatEncounter = null;
        EncounterData? activeEncounterToEnd = null;
        lock (encounterSync)
        {
            var now = DateTimeOffset.Now;
            if (chatEncounterId != Guid.Empty && chatLastDamage != default)
            {
                if (!activeEncounterPublished &&
                    chatEncounterDirty &&
                    now - chatEncounterStart >= TimeSpan.FromMilliseconds(250))
                {
                    activeChatEncounter = CreateChatEncounterSnapshot(
                        finished: false,
                        identities);
                    chatEncounterPublished |= activeChatEncounter is not null;
                    chatEncounterDirty = false;
                }

                if (activeEncounter is null &&
                    !inCombat &&
                    !localDeathWhilePartyContinues() &&
                    now - chatLastDamage >= TimeSpan.FromSeconds(3))
                {
                    completedChatEncounter = CreateChatEncounterSnapshot(
                        finished: true,
                        identities);
                    ResetChatEncounterUnsafe();
                    lastKnownDead.Clear();
                    observedDeaths.Clear();
                }
            }

            if (activeEncounter is not null &&
                !condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BoundByDuty] &&
                !inCombat &&
                lastRelevantCombatAction != default &&
                now - lastRelevantCombatAction >= TimeSpan.FromSeconds(3))
            {
                activeEncounterToEnd = activeEncounter;
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

        if (activeEncounterToEnd is not null)
        {
            try
            {
                ActGlobals.oFormActMain.EndCombat(true);
            }
            catch (Exception ex)
            {
                log.Error(ex, "Failed to end the local open-world ACT encounter.");
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
        return combatants.Length == 0
            ? null
            : new ActEncounterSnapshot(
                activeEncounterId == Guid.Empty ? chatEncounterId : activeEncounterId,
                chatEncounterStart,
                finished ? chatLastDamage : null,
                chatZone,
                string.IsNullOrWhiteSpace(chatEnemy) ? "Encounter" : chatEnemy,
                combatants);
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

        var identities = gameStateProvider.Identities;
        if (!IsTrackedCombatantEvent(
                action.combatAction.Attacker,
                action.combatAction.Victim,
                identities))
        {
            return;
        }

        lock (encounterSync)
        {
            var actionTime = action.combatAction.Time == default
                ? DateTimeOffset.Now
                : new DateTimeOffset(action.combatAction.Time);
            if (!ReferenceEquals(activeEncounter, encounter))
            {
                activeEncounterRelevantStart = actionTime;
            }
            lastRelevantCombatAction = actionTime;
        }

        PublishEncounter(encounter, false);
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

    private void PublishEncounter(EncounterData encounter, bool finished)
    {
        try
        {
            ActEncounterSnapshot? snapshot = null;
            ActEncounterSnapshot? fallbackSnapshot = null;
            string? unmatchedNames = null;
            lock (ActGlobals.oFormActMain.AfterCombatActionDataLock)
            {
                lock (encounterSync)
                {
                    if (!ReferenceEquals(activeEncounter, encounter))
                    {
                        var continuesChatEncounter = chatEncounterId != Guid.Empty;
                        criticalDirectHitCounters.Clear();
                        activeEncounter = encounter;
                        activeEncounterId = continuesChatEncounter
                            ? chatEncounterId
                            : Guid.NewGuid();
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
                    var encounterSeconds = Math.Max(
                        1,
                        ((endTime ?? DateTimeOffset.Now) - startTime).TotalSeconds);
                    var identities = gameStateProvider.Identities;
                    var combatants = encounter.Items.Values
                        .Select(combatant => (
                            Combatant: combatant,
                            Identity: ActPlayerIdentityResolver.Resolve(identities, combatant.Name)))
                        .Where(static item => item.Identity is not null)
                        .Select(item =>
                        {
                            var hitCounts = GetDamageHitCounts(item.Combatant);
                            return new ActCombatantSnapshot(
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
                                item.Combatant.Damage / encounterSeconds,
                                item.Combatant.ExtDPS,
                                hitCounts.DamageHits,
                                hitCounts.CriticalHits,
                                hitCounts.CriticalDirectHits);
                        })
                        .ToArray();

                    if (combatants.Length > 0)
                    {
                        activeEncounterPublished = true;
                        snapshot = new ActEncounterSnapshot(
                            activeEncounterId,
                            startTime,
                            endTime,
                            encounter.ZoneName ?? ActGlobals.oFormActMain.CurrentZone ?? string.Empty,
                            encounter.Title ?? string.Empty,
                            combatants);
                    }
                    else if (!activeEncounterNamesLogged)
                    {
                        activeEncounterNamesLogged = true;
                        unmatchedNames = string.Join(
                            ", ",
                            encounter.Items.Values.Select(item => item.Name));
                    }

                    if (finished)
                    {
                        activeEncounter = null;
                        if (snapshot is null)
                        {
                            fallbackSnapshot = CreateChatEncounterSnapshot(
                                finished: true,
                                identities);
                        }

                        activeEncounterId = Guid.Empty;
                        activeEncounterPublished = false;
                        activeEncounterNamesLogged = false;
                        activeEncounterRelevantStart = default;
                        lastRelevantCombatAction = default;
                        lastKnownDead.Clear();
                        observedDeaths.Clear();
                        ResetChatEncounterUnsafe();
                    }
                }
            }

            if (unmatchedNames is not null)
            {
                log.Warning(
                    "ACT encounter has no combatants matching the current player or party. " +
                    $"Raw names: {unmatchedNames}. " +
                    "The Chinese combat-chat fallback will be used for this encounter.");
            }

            if (snapshot is not null)
            {
                EncounterChanged?.Invoke(snapshot, finished);
            }
            else if (fallbackSnapshot is not null)
            {
                EncounterChanged?.Invoke(fallbackSnapshot, true);
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to publish ACT encounter snapshot.");
        }
    }

    private CombatantHitCounts GetDamageHitCounts(CombatantData combatant)
    {
        var allDamage = combatant.AllOut.Values.MaxBy(static attack => attack.Hits);
        if (allDamage is null)
        {
            return new CombatantHitCounts(
                Math.Max(0, combatant.Hits),
                Math.Max(0, combatant.CritHits),
                0);
        }

        if (!criticalDirectHitCounters.TryGetValue(combatant.Name, out var counter) ||
            !ReferenceEquals(counter.Source, allDamage) ||
            counter.ProcessedSwings > allDamage.Items.Count)
        {
            counter = new CriticalDirectHitCounter(allDamage);
        }

        for (var index = counter.ProcessedSwings; index < allDamage.Items.Count; index++)
        {
            var swing = allDamage.Items[index];
            if (swing.Critical &&
                swing.Tags.TryGetValue("DirectHit", out var directHit) &&
                string.Equals(directHit?.ToString(), "True", StringComparison.Ordinal))
            {
                counter.CriticalDirectHits++;
            }
        }

        counter.ProcessedSwings = allDamage.Items.Count;
        criticalDirectHitCounters[combatant.Name] = counter;

        return new CombatantHitCounts(
            Math.Max(0, combatant.Hits),
            Math.Max(0, combatant.CritHits),
            counter.CriticalDirectHits);
    }

    private sealed class CriticalDirectHitCounter(AttackType source)
    {
        public AttackType Source { get; } = source;

        public int ProcessedSwings { get; set; }

        public int CriticalDirectHits { get; set; }
    }

    private readonly record struct CombatantHitCounts(
        int DamageHits,
        int CriticalHits,
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
        chatEncounterStart = default;
        chatLastDamage = default;
        chatEncounterDirty = false;
        chatEncounterPublished = false;
        chatDamageTotals.Clear();
        chatParser.Clear();
        chatEnemy = string.Empty;
        chatZone = string.Empty;
    }

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

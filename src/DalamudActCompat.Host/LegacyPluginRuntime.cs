using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Windows.Forms;
using Advanced_Combat_Tracker;
using DalamudActCompat.Protocol;

namespace DalamudActCompat.Host;

internal sealed class LegacyPluginRuntime : IDisposable
{
    private static int windowsFormsExceptionHandlingConfigured;
    private readonly string pluginRoot;
    private readonly string configRoot;
    private readonly HashSet<string> allowedPluginIds;
    private readonly bool suppressTtsOutput;
    private readonly bool matchaOnly;
    private readonly List<LegacyPluginHandle> plugins = [];
    private readonly ConcurrentDictionary<string, HostPluginStage> stages =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ManualResetEventSlim ready = new();
    private Thread? actUiThread;
    private FormActMain? actMain;
    private Exception? startupFailure;
    private FFXIV_ACT_Plugin.FFXIV_ACT_Plugin? ffxivBridge;
    private SilverDasherDataSubscription? silverDasherSubscription;
    private SilverDasherWindowsNotifier? silverDasherWindowsNotifier;
    private MatchaDataSubscription? matchaSubscription;
    private MatchaWindowsNotifier? matchaWindowsNotifier;
    private long acceptedLogLines;
    private bool disposed;

    public LegacyPluginRuntime(
        string pluginRoot,
        string configRoot,
        IEnumerable<string> allowedPluginIds,
        bool suppressTtsOutput = false)
    {
        this.pluginRoot = Path.GetFullPath(pluginRoot);
        this.configRoot = Path.GetFullPath(configRoot);
        this.allowedPluginIds = allowedPluginIds.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        this.suppressTtsOutput = suppressTtsOutput;
        matchaOnly = this.allowedPluginIds.SetEquals(["matcha"]);
        if (matchaOnly)
        {
            SetStage("matcha", "ACT Host", "pending", "Waiting for the dedicated ACT UI host.");
            SetStage("matcha", "Host IPC", "pending", "Waiting for the dedicated game-side bridge.");
            SetStage("matcha", "FFXIV_ACT_Plugin discovery", "pending", "Waiting for the Matcha-only facade.");
            SetStage("matcha", "InitPlugin", "pending", "Waiting for the hash-pinned assembly.");
            SetStage("matcha", "Bidirectional network", "pending", "Waiting for isolated RX/TX queues.");
            SetStage("matcha", "Unload test", "pending", "Dedicated Host is still running.");
            return;
        }

        SetStage("postnamazu", "Assembly load", "pending", "Waiting for Host startup.");
        SetStage("postnamazu", "InitPlugin", "pending", "Waiting for assembly load.");
        SetStage("postnamazu", "ACT Host", "pending", "Waiting for ACT UI host.");
        SetStage("postnamazu", "Host IPC", "pending", "Waiting for the game-side bridge.");
        SetStage("postnamazu", "FFXIV_ACT_Plugin discovery", "pending", "Waiting for facade registration.");
        SetStage("postnamazu", "Triggernometry discovery", "pending", "Waiting for plugin discovery.");
        SetStage(
            "postnamazu",
            "OverlayPlugin discovery",
            "pending",
            "Waiting for the cross-process game-side OverlayPlugin adapter.");
        SetStage(
            "postnamazu",
            "Game process recognition",
            "brokered",
            "The semantic bridge never mistakes the Host for FFXIV. With GameCommand and NativeGameMemory granted, the original PostNamazu runtime receives the exact game process from the handshake.");
        SetStage("postnamazu", "Command bridge", "pending", "Waiting for PostNamazu initialization.");
        SetStage("postnamazu", "Log system", "pending", "Waiting for ACT event host.");
        SetStage("postnamazu", "Command send test", "not-tested", "Requires an explicit in-game test.");
        SetStage("postnamazu", "Unload test", "pending", "Host is still running.");
    }

    public IReadOnlyList<string> LoadedPluginIds
        => plugins.Select(plugin => plugin.Id).ToArray();

    public IReadOnlyList<HostPluginStage> GetStages()
    {
        stages["act-host|Log bridge"] = new HostPluginStage(
            "act-host",
            "Log bridge",
            "success",
            $"accepted={Volatile.Read(ref acceptedLogLines)}",
            DateTimeOffset.UtcNow);
        foreach (var plugin in plugins)
        {
            try
            {
                plugin.PollDiagnostics();
                if (plugin.GetRuntimeStage() is { } runtimeStage)
                {
                    stages[$"{runtimeStage.PluginId}|{runtimeStage.Stage}"] = runtimeStage;
                }
            }
            catch (Exception ex)
            {
                HostPluginBridge.ReportException(
                    plugin.Id,
                    "Diagnostic/status polling",
                    ex);
                SetStage(
                    plugin.Id,
                    "Diagnostic/status polling",
                    "failed",
                    ex.Message);
            }
        }

        return stages.Values
            .OrderBy(stage => stage.PluginId, StringComparer.Ordinal)
            .ThenBy(stage => stage.Stage, StringComparer.Ordinal)
            .ToArray();
    }

    public IReadOnlyList<HostPluginHealth> GetPluginHealth()
    {
        var now = DateTimeOffset.UtcNow;
        return actMain?.GetCallbackHealth()
                   .GroupBy(snapshot => snapshot.PluginId, StringComparer.Ordinal)
                   .Select(group =>
                   {
                       var active = group
                           .Where(snapshot => snapshot.ActiveSince is not null)
                           .OrderBy(snapshot => snapshot.ActiveSince)
                           .FirstOrDefault();
                       var circuitOpen = group.Any(snapshot => snapshot.CircuitOpen);
                       var exceptions = group.Sum(snapshot => snapshot.Exceptions);
                       var slowCalls = group.Sum(snapshot => snapshot.Timeouts);
                       var state = circuitOpen
                           ? "circuit-open"
                           : active is not null
                               ? "running"
                               : exceptions > 0 || slowCalls > 0
                                   ? "degraded"
                                   : "healthy";
                       return new HostPluginHealth(
                           group.Key,
                           state,
                           group.Sum(snapshot => snapshot.Completed),
                           exceptions,
                           slowCalls,
                           group.Max(snapshot => snapshot.LastDurationMilliseconds),
                           active?.Callback,
                           active?.ActiveSince is { } since
                               ? (long)(now - since).TotalMilliseconds
                               : 0,
                           circuitOpen);
                   })
                   .OrderBy(health => health.PluginId, StringComparer.Ordinal)
                   .Take(32)
                   .ToArray()
               ?? [];
    }

    public void Start()
    {
        Directory.CreateDirectory(pluginRoot);
        Directory.CreateDirectory(configRoot);
        Directory.CreateDirectory(Path.Combine(configRoot, "Config"));
        if (!matchaOnly && FoxTtsConfigurationDefaults.Ensure(configRoot))
        {
            Console.WriteLine(
                "Created ACT.FoxTTS defaults with Cafe TTS Pro selected; existing user settings are never overwritten.");
        }
        actUiThread = new Thread(RunActUi)
        {
            IsBackground = true,
            Name = "External ACT Host UI",
        };
        actUiThread.SetApartmentState(ApartmentState.STA);
        actUiThread.Start();
        if (!ready.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new TimeoutException("External ACT UI host did not initialize within ten seconds.");
        }

        if (startupFailure is not null)
        {
            ExceptionDispatchInfo.Capture(startupFailure).Throw();
        }

        var stagePluginId = matchaOnly ? "matcha" : "postnamazu";
        SetStage(stagePluginId, "ACT Host", "success", "External STA ACT UI host is active.");
        SetStage(stagePluginId, "Host IPC", "success", "Versioned named-pipe bridge is connected.");

        RegisterFfxivPluginIdentity();
        if (matchaOnly)
        {
            actMain!.BeforeLogLineRead += OnMatchaLogLine;
            actMain.PlayTtsMethod = HostPluginBridge.SendMatchaTts;
            LoadManifestPlugins();
            SetStage(
                "matcha",
                "Bidirectional network",
                plugins.Any(plugin => plugin.Id == "matcha") ? "success" : "failed",
                "Received and sent packets use a Matcha-only bounded dispatcher in a separate process.");
            return;
        }

        SetStage("postnamazu", "Log system", "success", "Bounded IPC log batches route through FormActMain.");
        RegisterOverlayPluginIdentity();
        TryLoad(
            "triggernometry",
            "Triggernometry.dll",
            "TriggernometryProxy.ProxyPlugin");
        var triggernometryAdministrator =
            HostPluginBridge.CheckTriggernometryAdministratorCapability(warnIfNotAdmin: false);
        SetStage(
            "triggernometry",
            "Administrator capability",
            triggernometryAdministrator ? "elevated" : "standard-user",
            triggernometryAdministrator
                ? "Host is elevated, but high-risk actions still require their own explicit capability grants."
                : "Logs, network-equivalent events, zone/combat events, regex, configuration, clipboard, and brokered TTS do not require elevation. Protected cross-integrity actions remain unavailable; do not elevate FFXIV.");
        SetStage(
            "postnamazu",
            "Triggernometry discovery",
            plugins.Any(plugin => plugin.Id == "triggernometry") ? "success" : "failed",
            plugins.Any(plugin => plugin.Id == "triggernometry")
                ? "Triggernometry is present in the ACT plugin list before PostNamazu initialization."
                : "Triggernometry was not loaded into the external ACT plugin list.");
        TryLoad(
            "postnamazu",
            "PostNamazu.dll",
            "PostNamazu.PostNamazu");
        LoadManifestPlugins();
        var isolatedTtsWriter = plugins
            .Select(plugin => plugin.TtsWriter)
            .FirstOrDefault(writer => writer is not null);
        Action<string>? hostTtsWriter = isolatedTtsWriter is null
            ? null
            : suppressTtsOutput
                ? text => Console.WriteLine($"test TTS output suppressed: {text}")
                : isolatedTtsWriter;
        HostPluginBridge.ConfigureTtsWriter(hostTtsWriter);
        actMain!.PlayTtsMethod = HostPluginBridge.SendTts;
    }

    public void AcceptLogs(IReadOnlyList<HostLogEvent> logs)
    {
        var target = actMain;
        if (target is null)
        {
            return;
        }

        foreach (var log in logs)
        {
            target.ParseRawLogLine(
                log.IsImport,
                log.Timestamp.LocalDateTime,
                string.IsNullOrEmpty(log.ActLine) ? log.Line : log.ActLine);
            Interlocked.Increment(ref acceptedLogLines);
        }
    }

    public void ChangeZone(uint territoryId, string zoneName)
    {
        actMain?.ChangeZone(zoneName);
        HostPluginBridge.PublishTriggernometryZoneChange(territoryId, zoneName);
    }

    public void SetCombatState(bool inCombat)
    {
        var target = actMain;
        if (target is null || target.InCombat == inCombat)
        {
            return;
        }

        if (inCombat)
        {
            target.SetEncounter(DateTime.Now, "ACT_HOST", "ACT_HOST");
        }
        else
        {
            target.EndCombat(true);
        }
    }

    public bool OpenPluginUi(string pluginId)
    {
        var plugin = plugins.FirstOrDefault(candidate => string.Equals(
            candidate.Id,
            pluginId,
            StringComparison.OrdinalIgnoreCase));
        return plugin?.OpenConfiguration() == true;
    }

    public bool InvokePlugin(HostPluginInvocation invocation)
    {
        var plugin = plugins.FirstOrDefault(candidate => string.Equals(
            candidate.Id,
            invocation.PluginId,
            StringComparison.OrdinalIgnoreCase));
        if (plugin is null)
        {
            return false;
        }

        try
        {
            return plugin.Invoke(invocation);
        }
        catch (Exception ex)
        {
            HostPluginBridge.ReportException(invocation.PluginId, invocation.Action, ex);
            Console.Error.WriteLine(
                $"Legacy plugin '{invocation.PluginId}' action '{invocation.Action}' failed: {ex}");
            return false;
        }
    }

    public bool PlayTts(string text)
    {
        try
        {
            HostPluginBridge.PlayTtsFromGame(text);
            return true;
        }
        catch (Exception ex)
        {
            HostPluginBridge.ReportException("act.foxtts", "Game-side TTS", ex);
            Console.Error.WriteLine($"Isolated ACT TTS provider failed: {ex}");
            return false;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        HostPluginBridge.ConfigureTtsWriter(null);
        HostPluginBridge.ConfigureSilverDasherNotificationWriter(null);
        HostPluginBridge.ConfigureMatchaNotificationWriter(null);
        if (matchaOnly && actMain is not null)
        {
            actMain.BeforeLogLineRead -= OnMatchaLogLine;
        }
        for (var index = plugins.Count - 1; index >= 0; index--)
        {
            plugins[index].Dispose();
        }
        silverDasherWindowsNotifier?.Dispose();
        silverDasherWindowsNotifier = null;
        HostPluginBridge.ConfigureSilverDasherSubscription(null);
        silverDasherSubscription?.Dispose();
        silverDasherSubscription = null;
        matchaWindowsNotifier?.Dispose();
        matchaWindowsNotifier = null;
        HostPluginBridge.ClearMatchaContext();
        matchaSubscription?.Dispose();
        matchaSubscription = null;
        SetStage(
            matchaOnly ? "matcha" : "postnamazu",
            "Unload test",
            "success",
            "DeInitPlugin was requested without blocking FFXIV.");
        plugins.Clear();
        var form = actMain;
        if (form is not null && form.IsHandleCreated)
        {
            form.BeginInvoke((Action)(() =>
            {
                try
                {
                    ActGlobals.Dispose();
                }
                finally
                {
                    Application.ExitThread();
                }
            }));
        }

        if (actUiThread?.Join(TimeSpan.FromSeconds(1)) == false)
        {
            Console.Error.WriteLine(
                "ACT UI thread did not stop within one second; supervisor process termination remains available.");
        }

        ready.Dispose();
    }

    private void RunActUi()
    {
        try
        {
            ConfigureWindowsFormsExceptionHandling();
            ActGlobals.Init();
            actMain = new FormActMain(new ConsoleActLogger())
            {
                AppDataFolder = new DirectoryInfo(configRoot),
                LogFilePath = Path.Combine(configRoot, "logs", "external-host.log"),
                WriteLogFile = false,
                UseExternalLogSource = true,
                CurrentZone = string.Empty,
                ShowInTaskbar = false,
                FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-32000, -32000),
                Size = new Size(1, 1),
                Opacity = 0,
            };
            ActGlobals.oFormActMain = actMain;
            actMain.Show();
            actMain.Hide();
            ready.Set();
            Application.Run(new ApplicationContext());
        }
        catch (Exception ex)
        {
            startupFailure = ex;
            ready.Set();
        }
    }

    private void RegisterFfxivPluginIdentity()
    {
        var assemblyPath = Path.Combine(AppContext.BaseDirectory, "FFXIV_ACT_Plugin.dll");
        ffxivBridge = new FFXIV_ACT_Plugin.FFXIV_ACT_Plugin();
        ffxivBridge.GetType()
            .GetProperty(
                "DataRepository",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(ffxivBridge, HostPluginBridge.FfxivRepository);
        actMain!.FfxivPlugin = ffxivBridge;
        var tab = new TabPage("FFXIV_ACT_Plugin");
        var status = new Label { Text = "External event bridge" };
        ActGlobals.oFormActMain.ActPlugins.Add(
            new ActPluginData(new FileInfo(assemblyPath), ffxivBridge, tab, status));
        SetStage(
            matchaOnly ? "matcha" : "postnamazu",
            "FFXIV_ACT_Plugin discovery",
            "success",
            "Official plugin type identity is present with a read-only game-side entity repository.");
    }

    private void RegisterOverlayPluginIdentity()
    {
        var assemblyPath = Path.Combine(AppContext.BaseDirectory, "OverlayPlugin.dll");
        var facade = new global::OverlayPlugin();
        var tab = new TabPage("OverlayPlugin");
        var status = new Label
        {
            Text = "Game-side OverlayPlugin event dispatcher bridge",
        };
        var overlayPluginData = new ActPluginData(
            new FileInfo(assemblyPath),
            facade,
            tab,
            status);
        ActGlobals.oFormActMain.ActPlugins.Add(overlayPluginData);
        SetStage(
            "triggernometry",
            "OverlayPlugin discovery",
            "success",
            "OverlayPlugin compatibility identity is second in ACT plugin order and calls the real game-side event dispatcher through bounded IPC.");
        Console.WriteLine(
            "OverlayPlugin compatibility identity registered before Triggernometry.");
    }

    private void TryLoad(string id, string assemblyName, string entryType)
        => TryLoadPath(
            id,
            Path.Combine(pluginRoot, id, assemblyName),
            entryType);

    private void TryLoadPath(string id, string assemblyPath, string entryType)
    {
        if (!allowedPluginIds.Contains(id))
        {
            Console.WriteLine($"Legacy plugin '{id}' is not allowed by the game-side policy.");
            SetStage(
                id,
                "InitPlugin",
                "disabled",
                "Disabled or not yet acknowledged by the user; restart Host after changing policy.");
            return;
        }

        if (!File.Exists(assemblyPath))
        {
            Console.WriteLine($"Legacy plugin '{id}' not installed at {assemblyPath}.");
            SetStage(id, "Assembly load", "failed", $"Assembly not found: {assemblyPath}");
            return;
        }

        SetStage(id, "Assembly load", "success", assemblyPath);

        try
        {
            if (id == "triggernometry")
            {
                PreloadSystemSpeechRuntime();
            }
            else if (id == "silverdasher")
            {
                PrepareSilverDasherContext(assemblyPath);
            }
            else if (id == "matcha")
            {
                PrepareMatchaContext(assemblyPath);
            }

            var handle = LegacyPluginHandle.Load(id, assemblyPath, entryType);
            plugins.Add(handle);
            Console.WriteLine($"Legacy plugin '{id}' loaded out-of-process. Status: {handle.Status}");
            SetStage(id, "InitPlugin", "success", handle.Status);
            if (id == "postnamazu")
            {
                SetStage(
                    id,
                    "Command bridge",
                    "success",
                    "All seven action modules remain registered: semantic actions use the game-side broker, while normalcommand and advanced native paths activate with full permissions.");
                SetStage(
                    id,
                    "OverlayPlugin discovery",
                    "success",
                    "The game-side OverlayPlugin event source forwards PostNamazu actions over bounded IPC.");
            }
            else if (id == "silverdasher")
            {
                silverDasherWindowsNotifier = new SilverDasherWindowsNotifier(actMain!);
                HostPluginBridge.ConfigureSilverDasherNotificationWriter(
                    silverDasherWindowsNotifier.TryShow);
                HostPluginBridge.ReplaySilverDasherState();
                SetStage(
                    id,
                    "Isolated event channel",
                    "success",
                    "Loaded last with a dedicated bounded dispatcher and plugin-scoped subscription facade.");
                SetStage(
                    id,
                    "Native game memory",
                    "brokered",
                    "The single process-access call is rewritten to the SilverDasher-only NativeGameMemory bridge.");
                SetStage(
                    id,
                    "Windows notifications",
                    "success",
                    "SilverDasher uses the isolated Host Windows shell first and falls back to the existing game-side notification channel if unavailable.");
            }
            else if (id == "matcha")
            {
                matchaWindowsNotifier = new MatchaWindowsNotifier(actMain!);
                HostPluginBridge.ConfigureMatchaNotificationWriter(
                    matchaWindowsNotifier.TryShow);
                SetStage(
                    id,
                    "Dedicated process isolation",
                    "success",
                    "Matcha is the only third-party plugin allowed in this Host process.");
                SetStage(
                    id,
                    "Windows notifications",
                    "success",
                    "Notifications use the Matcha Host Windows shell and a typed game-side fallback.");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Legacy plugin '{id}' failed out-of-process: {ex}");
            HostPluginBridge.ReportException(id, "InitPlugin", ex);
            SetStage(id, "InitPlugin", "failed", ex.ToString());
        }
    }

    private void LoadManifestPlugins()
    {
        var deferredSilverDasher = new List<(string AssemblyPath, string EntryType)>();
        foreach (var manifestPath in Directory.EnumerateFiles(
                     pluginRoot,
                     "actcompat.plugin.json",
                     SearchOption.AllDirectories))
        {
            try
            {
                var manifest = JsonSerializer.Deserialize<ExternalPluginManifest>(
                                   File.ReadAllText(manifestPath),
                                   new JsonSerializerOptions
                                   {
                                       PropertyNameCaseInsensitive = true,
                                   })
                               ?? throw new InvalidDataException(
                                   $"Plugin manifest is empty: {manifestPath}");
                if (string.IsNullOrWhiteSpace(manifest.Id) ||
                    string.IsNullOrWhiteSpace(manifest.EntryAssembly) ||
                    string.IsNullOrWhiteSpace(manifest.EntryType))
                {
                    throw new InvalidDataException(
                        $"Plugin manifest is incomplete: {manifestPath}");
                }

                if (manifest.Id is "triggernometry" or "postnamazu")
                {
                    continue;
                }

                var isMatcha = string.Equals(
                    manifest.Id,
                    "matcha",
                    StringComparison.OrdinalIgnoreCase);
                if (isMatcha != matchaOnly)
                {
                    // Matcha never enters the process shared by the existing extensions,
                    // and the Matcha-only Host ignores every other third-party manifest.
                    continue;
                }

                var directory = Path.GetDirectoryName(manifestPath)!;
                var assemblyPath = Path.GetFullPath(
                    Path.Combine(directory, manifest.EntryAssembly));
                if (!assemblyPath.StartsWith(
                        Path.GetFullPath(directory) + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"Plugin entry assembly escapes its install directory: {manifestPath}");
                }

                if (string.Equals(
                        manifest.Id,
                        "silverdasher",
                        StringComparison.OrdinalIgnoreCase))
                {
                    deferredSilverDasher.Add((assemblyPath, manifest.EntryType));
                }
                else
                {
                    TryLoadPath(
                        manifest.Id,
                        assemblyPath,
                        manifest.EntryType);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"External ACT plugin manifest failed ({manifestPath}): {ex}");
                HostPluginBridge.ReportException(
                    "plugin-discovery",
                    "Manifest",
                    ex);
            }
        }

        foreach (var (assemblyPath, entryType) in deferredSilverDasher)
        {
            TryLoadPath("silverdasher", assemblyPath, entryType);
        }
    }

    private void PrepareSilverDasherContext(string assemblyPath)
    {
        if (ffxivBridge is null)
        {
            throw new InvalidOperationException(
                "FFXIV_ACT_Plugin facade is unavailable for SilverDasher initialization.");
        }

        silverDasherSubscription ??= new SilverDasherDataSubscription();
        ffxivBridge.GetType()
            .GetProperty(
                "DataSubscription",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(ffxivBridge, silverDasherSubscription);
        HostPluginBridge.ConfigureSilverDasherRoot(
            Path.GetDirectoryName(Path.GetFullPath(assemblyPath))!);
        HostPluginBridge.ConfigureSilverDasherSubscription(silverDasherSubscription);
    }

    private void PrepareMatchaContext(string assemblyPath)
    {
        if (ffxivBridge is null)
        {
            throw new InvalidOperationException(
                "FFXIV_ACT_Plugin facade is unavailable for Matcha initialization.");
        }

        matchaSubscription ??= new MatchaDataSubscription();
        ffxivBridge.GetType()
            .GetProperty(
                "DataSubscription",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(ffxivBridge, matchaSubscription);
        HostPluginBridge.ConfigureMatchaContext(
            Path.GetDirectoryName(Path.GetFullPath(assemblyPath))!,
            configRoot,
            matchaSubscription);
    }

    private static void OnMatchaLogLine(bool isImport, LogLineEventArgs args)
    {
        if (!isImport)
        {
            HostPluginBridge.RelayMatchaLogLine(args.logLine);
        }
    }

    private void SetStage(
        string pluginId,
        string stage,
        string state,
        string detail)
        => stages[$"{pluginId}|{stage}"] = new HostPluginStage(
            pluginId,
            stage,
            state,
            detail,
            DateTimeOffset.UtcNow);

    private static void PreloadSystemSpeechRuntime()
    {
        if (AssemblyLoadContext.Default.Assemblies.Any(candidate => string.Equals(
                candidate.GetName().Name,
                "System.Speech",
                StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        string[] candidates =
        [
            Path.Combine(
                AppContext.BaseDirectory,
                "runtimes",
                "win",
                "lib",
                "net10.0",
                "System.Speech.dll"),
            Path.Combine(AppContext.BaseDirectory, "System.Speech.dll"),
        ];
        var implementation = candidates.FirstOrDefault(File.Exists)
                             ?? throw new FileNotFoundException(
                                 "The Windows System.Speech runtime implementation is missing.");
        var loaded = AssemblyLoadContext.Default.LoadFromAssemblyPath(
            Path.GetFullPath(implementation));
        Console.WriteLine(
            $"Preloaded {loaded.GetName().FullName} from {loaded.Location}.");
    }

    internal static void ConfigureWindowsFormsExceptionHandling()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        if (Interlocked.Exchange(
                ref windowsFormsExceptionHandlingConfigured,
                1) == 0)
        {
            Application.ThreadException += (_, args) =>
            {
                if (HostPluginBridge.ReportException(
                        "legacy-winforms",
                        "Application.ThreadException",
                        args.Exception))
                {
                    Console.Error.WriteLine(
                        $"Unhandled legacy plugin WinForms exception: {args.Exception}");
                }
            };
        }
    }
}

internal sealed class ExternalPluginManifest
{
    public string Id { get; set; } = string.Empty;

    public string EntryAssembly { get; set; } = string.Empty;

    public string EntryType { get; set; } = string.Empty;
}

internal sealed class LegacyPluginHandle : IDisposable
{
    private readonly object instance;
    private readonly MethodInfo deInitPlugin;
    private readonly Form configurationForm;
    private readonly Thread uiThread;
    private readonly ActPluginData pluginData;
    private readonly HashSet<string> seenDiagnostics = new(StringComparer.Ordinal);
    private readonly Queue<string> seenDiagnosticOrder = new();
    private bool disposed;

    private LegacyPluginHandle(
        string id,
        object instance,
        MethodInfo deInitPlugin,
        Form configurationForm,
        Thread uiThread,
        ActPluginData pluginData,
        string status,
        Action<string>? ttsWriter)
    {
        Id = id;
        this.instance = instance;
        this.deInitPlugin = deInitPlugin;
        this.configurationForm = configurationForm;
        this.uiThread = uiThread;
        this.pluginData = pluginData;
        Status = status;
        TtsWriter = ttsWriter;
    }

    public string Id { get; }

    public string Status { get; }

    public Action<string>? TtsWriter { get; }

    public bool OpenConfiguration()
    {
        if (!configurationForm.IsHandleCreated || configurationForm.IsDisposed)
        {
            return false;
        }

        configurationForm.BeginInvoke((Action)(() =>
        {
            var previousTopMost = configurationForm.TopMost;
            configurationForm.TopMost = true;
            configurationForm.Show();
            configurationForm.WindowState = FormWindowState.Normal;
            configurationForm.Activate();
            configurationForm.BringToFront();
            var releaseTopMost = new System.Windows.Forms.Timer { Interval = 500 };
            releaseTopMost.Tick += (_, _) =>
            {
                releaseTopMost.Stop();
                if (!configurationForm.IsDisposed)
                {
                    configurationForm.TopMost = previousTopMost;
                }
                releaseTopMost.Dispose();
            };
            releaseTopMost.Start();
        }));
        return true;
    }

    public bool Invoke(HostPluginInvocation invocation)
    {
        if (!string.Equals(Id, "postnamazu", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(invocation.Action, "overlay", StringComparison.OrdinalIgnoreCase) ||
            !invocation.Arguments.TryGetValue("command", out var command))
        {
            return false;
        }

        var payload = invocation.Arguments.GetValueOrDefault("payload", string.Empty);
        var doAction = instance.GetType().GetMethod(
                           "DoAction",
                           BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                           null,
                           [typeof(string), typeof(string)],
                           null)
                       ?? throw new MissingMethodException(
                           instance.GetType().FullName,
                           "DoAction");
        doAction.Invoke(instance, [command, payload]);
        return true;
    }

    public static LegacyPluginHandle Load(
        string id,
        string assemblyPath,
        string entryTypeName)
    {
        using var ready = new ManualResetEventSlim();
        Exception? failure = null;
        object? instance = null;
        MethodInfo? deInit = null;
        Form? form = null;
        ActPluginData? pluginData = null;
        Action<string>? ttsWriter = null;
        var finalStatus = string.Empty;
        var loadContext = AssemblyLoadContext.Default;
        var resolver = new AssemblyDependencyResolver(assemblyPath);
        Assembly? Resolve(AssemblyLoadContext context, AssemblyName name)
        {
            var shared = context.Assemblies.FirstOrDefault(candidate => string.Equals(
                candidate.GetName().Name,
                name.Name,
                StringComparison.OrdinalIgnoreCase));
            if (shared is not null)
            {
                return shared;
            }

            var dependency = resolver.ResolveAssemblyToPath(name);
            return dependency is null ? null : context.LoadFromAssemblyPath(dependency);
        }

        loadContext.Resolving += Resolve;
        var thread = new Thread(() =>
        {
            try
            {
                LegacyPluginRuntime.ConfigureWindowsFormsExceptionHandling();
                var assembly = id == "triggernometry"
                    ? LegacyAssemblyRewriter.LoadTriggernometry(assemblyPath, loadContext)
                    : id == "postnamazu"
                        ? LegacyAssemblyRewriter.LoadPostNamazu(assemblyPath, loadContext)
                        : id == "silverdasher"
                            ? LegacyAssemblyRewriter.LoadSilverDasher(assemblyPath, loadContext)
                            : id == "matcha"
                                ? LegacyAssemblyRewriter.LoadMatcha(assemblyPath, loadContext)
                            : loadContext.LoadFromAssemblyPath(assemblyPath);
                var entryType = assembly.GetType(entryTypeName, throwOnError: true)!;
                instance = Activator.CreateInstance(entryType)
                           ?? throw new InvalidOperationException($"Could not create {entryTypeName}.");
                if (instance is not IActPluginV1 actPlugin)
                {
                    throw new InvalidOperationException($"{entryTypeName} is not IActPluginV1.");
                }

                var init = entryType.GetMethod("InitPlugin", BindingFlags.Instance | BindingFlags.Public)
                           ?? throw new MissingMethodException(entryTypeName, "InitPlugin");
                deInit = entryType.GetMethod("DeInitPlugin", BindingFlags.Instance | BindingFlags.Public)
                         ?? throw new MissingMethodException(entryTypeName, "DeInitPlugin");
                var tab = new TabPage(id);
                var status = new Label();
                pluginData = new ActPluginData(new FileInfo(assemblyPath), actPlugin, tab, status);
                ActGlobals.oFormActMain.ActPlugins.Add(pluginData);
                var tabs = new TabControl { Dock = DockStyle.Fill };
                tabs.TabPages.Add(tab);
                form = new Form
                {
                    Text = id,
                    Width = 960,
                    Height = 720,
                    StartPosition = FormStartPosition.CenterScreen,
                    ShowInTaskbar = true,
                };
                form.Controls.Add(tabs);
                _ = form.Handle;
                try
                {
                    init.Invoke(instance, [tab, status]);
                }
                catch (TargetInvocationException ex) when (
                    id == "postnamazu" &&
                    ex.InnerException is FileLoadException)
                {
                    HostPluginBridge.ReportException(
                        id,
                        "InitPlugin optional legacy integration",
                        ex.InnerException);
                    status.Text =
                        "Base UI loaded; optional legacy integration unavailable in external Host.";
                }

                if (id == "triggernometry" &&
                    !IsTriggernometryInitialized(instance))
                {
                    throw new InvalidOperationException(
                        $"Triggernometry proxy loaded but core initialization failed: {status.Text}");
                }

                if (id == "triggernometry")
                {
                    LegacyAssemblyRewriter.RegisterTriggernometryPictoAct(loadContext);
                    Console.WriteLine(DescribeTriggernometryState(instance));
                }

                if (id == "postnamazu")
                {
                    status.Text =
                        "External Host active; semantic bridge available. Full native runtime follows the game process when explicitly permitted.";
                }

                if (id == "act.foxtts")
                {
                    if (!status.Text.StartsWith("Init Success", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            $"ACT.FoxTTS did not initialize its speech provider: {status.Text}");
                    }

                    ttsWriter = CreateFoxTtsWriter(instance, form);
                    Console.WriteLine("ACT.FoxTTS speech bridge ready in the external Host.");
                }

                if (tab.Controls.Count == 1)
                {
                    tab.Controls[0].Dock = DockStyle.Fill;
                }

                form.Text = string.IsNullOrWhiteSpace(tab.Text) ? id : tab.Text;
                form.FormClosing += (_, args) =>
                {
                    if (args.CloseReason == CloseReason.UserClosing)
                    {
                        args.Cancel = true;
                        form.Hide();
                    }
                };
                finalStatus = status.Text;
                ready.Set();
                Application.Run(new ApplicationContext());
            }
            catch (Exception ex)
            {
                failure = ex;
                if (pluginData is not null)
                {
                    ActGlobals.oFormActMain.ActPlugins.Remove(pluginData);
                }
                ready.Set();
            }
        })
        {
            IsBackground = true,
            Name = $"External ACT plugin UI: {id}",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!ready.Wait(TimeSpan.FromSeconds(30)))
        {
            loadContext.Resolving -= Resolve;
            throw new TimeoutException($"Legacy plugin '{id}' initialization exceeded 30 seconds.");
        }

        if (failure is not null)
        {
            loadContext.Resolving -= Resolve;
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return new LegacyPluginHandle(
            id,
            instance!,
            deInit!,
            form!,
            thread,
            pluginData!,
            finalStatus,
            ttsWriter);
    }

    private static Action<string> CreateFoxTtsWriter(object plugin, Control dispatcher)
    {
        var speak = plugin.GetType().GetMethod(
                        "Speak",
                        BindingFlags.Instance | BindingFlags.Public,
                        binder: null,
                        types: [typeof(string)],
                        modifiers: null)
                    ?? throw new MissingMethodException(plugin.GetType().FullName, "Speak");
        return message =>
        {
            try
            {
                dispatcher.BeginInvoke((Action)(() =>
                {
                    try
                    {
                        speak.Invoke(plugin, [message]);
                    }
                    catch (TargetInvocationException ex) when (ex.InnerException is not null)
                    {
                        HostPluginBridge.ReportException("act.foxtts", "Speak", ex.InnerException);
                    }
                    catch (Exception ex)
                    {
                        HostPluginBridge.ReportException("act.foxtts", "Speak", ex);
                    }
                }));
            }
            catch (Exception ex)
            {
                HostPluginBridge.ReportException("act.foxtts", "Queue Speak", ex);
            }
        };
    }

    private static bool IsTriggernometryInitialized(object proxy)
    {
        var realPlugin = proxy.GetType().GetField(
                "Instance",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(proxy);
        var property = realPlugin?.GetType().GetProperty(
            "isInitialized",
            BindingFlags.Instance | BindingFlags.NonPublic);
        return property?.GetValue(realPlugin) is true;
    }

    private static string DescribeTriggernometryState(object proxy)
    {
        var realPlugin = proxy.GetType().GetField(
                "Instance",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(proxy);
        if (realPlugin is null)
        {
            return "Triggernometry state: proxy core instance is unavailable.";
        }

        static int CountField(object target, string name)
            => target.GetType().GetField(
                    name,
                    BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(target) is System.Collections.ICollection collection
                ? collection.Count
                : -1;

        return "Triggernometry state: " +
               $"all={CountField(realPlugin, "Triggers")}, " +
               $"log={CountField(realPlugin, "ActiveTextTriggers")}, " +
               $"network={CountField(realPlugin, "ActiveFFXIVNetworkTriggers")}, " +
               $"act={CountField(realPlugin, "ActiveACTTriggers")}.";
    }

    public HostPluginStage? GetRuntimeStage()
    {
        if (Id != "triggernometry")
        {
            return null;
        }

        var realPlugin = GetTriggernometryInstance();
        if (realPlugin is null)
        {
            return new HostPluginStage(
                Id,
                "Runtime queues",
                "failed",
                "Triggernometry core instance is unavailable.",
                DateTimeOffset.UtcNow);
        }

        static int CountField(object target, string name)
            => target.GetType().GetField(
                    name,
                    BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(target) is System.Collections.ICollection collection
                ? collection.Count
                : -1;

        var initialized = realPlugin.GetType().GetProperty(
                "isInitialized",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(realPlugin) is true;
        var callbacks = string.Join(
            ",",
            ActGlobals.oFormActMain.GetCallbackHealth()
                .Where(callback => callback.PluginId.Contains(
                    "Triggernometry",
                    StringComparison.OrdinalIgnoreCase))
                .Select(callback => $"{callback.Callback}:{callback.Completed}"));

        return new HostPluginStage(
            Id,
            "Runtime queues",
            "success",
            $"events={CountField(realPlugin, "EventQueue")}, " +
            $"actions={CountField(realPlugin, "ActionQueue")}, " +
            $"triggers={CountField(realPlugin, "Triggers")}, " +
            $"log={CountField(realPlugin, "ActiveTextTriggers")}, " +
            $"network={CountField(realPlugin, "ActiveFFXIVNetworkTriggers")}, " +
            $"act={CountField(realPlugin, "ActiveACTTriggers")}, " +
            $"initialized={initialized}, " +
            $"actHandle={ActGlobals.oFormActMain.IsHandleCreated}, " +
            $"ttsHook={ActGlobals.oFormActMain.PlayTtsMethod is not null}, " +
            $"callbacks={callbacks}",
            DateTimeOffset.UtcNow);
    }

    public void PollDiagnostics()
    {
        if (Id != "triggernometry")
        {
            return;
        }

        var realPlugin = GetTriggernometryInstance();
        if (realPlugin is null)
        {
            return;
        }

        if (realPlugin.GetType().GetField(
                "log",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(realPlugin) is not System.Collections.IDictionary log)
        {
            return;
        }

        foreach (System.Collections.DictionaryEntry pair in log)
        {
            if (!string.Equals(pair.Key?.ToString(), "Error", StringComparison.Ordinal) ||
                pair.Value is not System.Collections.IEnumerable entries)
            {
                continue;
            }

            object[] snapshot;
            lock (pair.Value)
            {
                snapshot = entries.Cast<object>().ToArray();
            }

            foreach (var entry in snapshot)
            {
                var entryType = entry.GetType();
                var message = entryType.GetProperty("Message")
                    ?.GetValue(entry)?.ToString();
                if (string.IsNullOrWhiteSpace(message))
                {
                    continue;
                }

                if (HostPluginBridge.IsExpectedTriggernometryCompatibilityNotice(message))
                {
                    lock (pair.Value)
                    {
                        if (pair.Value is System.Collections.IList list &&
                            !list.IsReadOnly &&
                            !list.IsFixedSize)
                        {
                            list.Remove(entry);
                        }
                    }
                    continue;
                }

                var timestampValue = entryType.GetProperty("Timestamp")?.GetValue(entry);
                var timestamp = timestampValue is DateTime dateTime
                    ? new DateTimeOffset(dateTime)
                    : DateTimeOffset.UtcNow;
                var identity = $"{timestamp.UtcTicks}|{message}";
                if (!seenDiagnostics.Add(identity))
                {
                    continue;
                }

                seenDiagnosticOrder.Enqueue(identity);
                while (seenDiagnosticOrder.Count > 8_192)
                {
                    seenDiagnostics.Remove(seenDiagnosticOrder.Dequeue());
                }

                var repositoryFailure =
                    message.Contains("repository", StringComparison.OrdinalIgnoreCase) ||
                    message.Contains("update", StringComparison.OrdinalIgnoreCase) ||
                    message.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
                    message.Contains("仓库", StringComparison.Ordinal) ||
                    message.Contains("更新", StringComparison.Ordinal) ||
                    message.Contains("超时", StringComparison.Ordinal);
                HostPluginBridge.ReportDiagnosticMessage(
                    Id,
                    repositoryFailure
                        ? "Remote repository update"
                        : "Triggernometry internal error log",
                    "Triggernometry.InternalErrorRecord",
                    message,
                    "Triggernometry stores only this error message; it does not retain the Exception object or stack trace, so the compatibility Host will not fabricate one.",
                    entryType.Assembly.GetName().Name ?? string.Empty,
                    repositoryFailure
                        ? "Triggernometry.Core.Repository"
                        : entryType.FullName ?? string.Empty,
                    repositoryFailure
                        ? "CheckAndUpdateAsync/UpdateAsync"
                        : string.Empty,
                    timestamp);
            }
        }
    }

    private object? GetTriggernometryInstance()
        => instance.GetType().GetField(
                "Instance",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(instance);

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        try
        {
            configurationForm.BeginInvoke((Action)(() =>
            {
                try
                {
                    deInitPlugin.Invoke(instance, null);
                    (instance as IDisposable)?.Dispose();
                    ActGlobals.oFormActMain.ActPlugins.Remove(pluginData);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Legacy plugin '{Id}' DeInit failed: {ex}");
                }
                finally
                {
                    configurationForm.Dispose();
                    Application.ExitThread();
                }
            }));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Legacy plugin '{Id}' could not queue DeInit: {ex}");
        }

        if (!uiThread.Join(TimeSpan.FromMilliseconds(250)))
        {
            Console.Error.WriteLine(
                $"Legacy plugin '{Id}' UI thread is hung; Host process termination will contain it.");
        }
    }
}

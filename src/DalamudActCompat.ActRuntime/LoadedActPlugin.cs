using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using System.Windows.Forms;
using Advanced_Combat_Tracker;
using Dalamud.Plugin.Services;

namespace DalamudActCompat.ActRuntime;

internal sealed class LoadedActPlugin : IDisposable
{
    private readonly AssemblyLoadContext loadContext;
    private readonly Func<AssemblyLoadContext, AssemblyName, Assembly?> resolvingHandler;
    private readonly object instance;
    private readonly MethodInfo deInitPlugin;
    private readonly TabPage tabPage;
    private readonly Label statusLabel;
    private readonly ActPluginData pluginData;
    private readonly Form configurationForm;
    private readonly Thread uiThread;
    private readonly Action? removeTtsBridge;
    private readonly PluginDiagnosticJournal diagnostics;
    private readonly IPluginLog log;
    private readonly TriggernometryDiagnosticMonitor? triggernometryMonitor;
    private bool disposing;

    private LoadedActPlugin(
        string id,
        AssemblyLoadContext loadContext,
        Func<AssemblyLoadContext, AssemblyName, Assembly?> resolvingHandler,
        object instance,
        MethodInfo deInitPlugin,
        TabPage tabPage,
        Label statusLabel,
        ActPluginData pluginData,
        Form configurationForm,
        Thread uiThread,
        Action? removeTtsBridge,
        PluginDiagnosticJournal diagnostics,
        IPluginLog log,
        TriggernometryDiagnosticMonitor? triggernometryMonitor)
    {
        Id = id;
        this.loadContext = loadContext;
        this.resolvingHandler = resolvingHandler;
        this.instance = instance;
        this.deInitPlugin = deInitPlugin;
        this.tabPage = tabPage;
        this.statusLabel = statusLabel;
        this.pluginData = pluginData;
        this.configurationForm = configurationForm;
        this.uiThread = uiThread;
        this.removeTtsBridge = removeTtsBridge;
        this.diagnostics = diagnostics;
        this.log = log;
        this.triggernometryMonitor = triggernometryMonitor;
    }

    public string Id { get; }

    public string Status => statusLabel.Text;

    public IReadOnlyList<ActPluginDiagnostic> Diagnostics => diagnostics.Snapshot();

    public IReadOnlyList<CompatibilityStageResult> Stages
        => string.Equals(Id, "postnamazu", StringComparison.OrdinalIgnoreCase)
            ? NativePostNamazuBridge.Stages
            : [];

    public void OpenConfiguration()
        => configurationForm.BeginInvoke((Action)(() =>
        {
            configurationForm.Show();
            configurationForm.WindowState = FormWindowState.Normal;
            configurationForm.Activate();
            configurationForm.BringToFront();
        }));

    public static LoadedActPlugin Load(RuntimePluginSpec spec, IPluginLog log)
    {
        if (string.Equals(spec.Id, "triggernometry", StringComparison.OrdinalIgnoreCase))
        {
            LegacyResourceCompatibility.EnsureLegacyResourceDecoderAvailable();
        }

        var assemblyPath = Path.GetFullPath(Path.Combine(spec.InstallDirectory, spec.EntryAssembly));
        using var ready = new ManualResetEventSlim();
        Exception? failure = null;
        AssemblyLoadContext? loadContext = null;
        Func<AssemblyLoadContext, AssemblyName, Assembly?>? resolvingHandler = null;
        object? instance = null;
        MethodInfo? deInitPlugin = null;
        TabPage? tabPage = null;
        Label? statusLabel = null;
        ActPluginData? pluginData = null;
        Form? configurationForm = null;
        Action? removeTtsBridge = null;
        TriggernometryDiagnosticMonitor? triggernometryMonitor = null;
        var diagnostics = new PluginDiagnosticJournal(spec.Id);

        var uiThread = new Thread(() =>
        {
            try
            {
                loadContext = AssemblyLoadContext.GetLoadContext(typeof(IActPluginV1).Assembly)
                              ?? throw new InvalidOperationException("ACT host assembly has no load context.");
                var resolver = new AssemblyDependencyResolver(assemblyPath);
                resolvingHandler = (context, assemblyName) =>
                {
                    var shared = context.Assemblies.FirstOrDefault(
                        candidate => string.Equals(
                            candidate.GetName().Name,
                            assemblyName.Name,
                            StringComparison.OrdinalIgnoreCase));
                    if (shared is not null)
                    {
                        return shared;
                    }

                    var dependencyPath = resolver.ResolveAssemblyToPath(assemblyName);
                    return dependencyPath is null ? null : context.LoadFromAssemblyPath(dependencyPath);
                };
                loadContext.Resolving += resolvingHandler;
                var assembly = string.Equals(spec.Id, "postnamazu", StringComparison.OrdinalIgnoreCase)
                    ? LegacyResourceCompatibility.LoadPostNamazuWithClipboardCompatibility(
                        assemblyPath,
                        loadContext)
                    : loadContext.LoadFromAssemblyPath(assemblyPath);
                if (string.Equals(spec.Id, "triggernometry", StringComparison.OrdinalIgnoreCase))
                {
                    LegacyResourceCompatibility.ProbeEmbeddedResources(assembly, loadContext);
                }

                var entryType = assembly.GetType(spec.EntryType, throwOnError: true)!;
                instance = Activator.CreateInstance(entryType)
                           ?? throw new InvalidOperationException($"Could not create plugin type {spec.EntryType}.");
                if (instance is not IActPluginV1 actPlugin)
                {
                    throw new InvalidOperationException($"{spec.EntryType} does not implement IActPluginV1.");
                }

                var initPlugin = entryType.GetMethod("InitPlugin", BindingFlags.Public | BindingFlags.Instance)
                                 ?? throw new MissingMethodException(spec.EntryType, "InitPlugin");
                deInitPlugin = entryType.GetMethod("DeInitPlugin", BindingFlags.Public | BindingFlags.Instance)
                                   ?? throw new MissingMethodException(spec.EntryType, "DeInitPlugin");
                tabPage = new TabPage(spec.Id);
                statusLabel = new Label();
                pluginData = new ActPluginData(new FileInfo(assemblyPath), actPlugin, tabPage, statusLabel);
                ActGlobals.oFormActMain.ActPlugins.Add(pluginData);

                var tabs = new TabControl { Dock = DockStyle.Fill };
                tabs.TabPages.Add(tabPage);
                configurationForm = new Form
                {
                    Text = spec.Id,
                    Width = 960,
                    Height = 720,
                    StartPosition = FormStartPosition.CenterScreen,
                    ShowInTaskbar = true,
                };
                configurationForm.Controls.Add(tabs);
                _ = configurationForm.Handle;
                _ = tabs.Handle;
                _ = tabPage.Handle;
                configurationForm.PerformLayout();

                try
                {
                    initPlugin.Invoke(instance, [tabPage, statusLabel]);
                }
                catch (TargetInvocationException ex) when (
                    string.Equals(spec.Id, "postnamazu", StringComparison.OrdinalIgnoreCase) &&
                    ex.InnerException is FileLoadException fileLoadException &&
                    fileLoadException.FileName?.StartsWith(
                        "Advanced Combat Tracker,",
                        StringComparison.OrdinalIgnoreCase) == true)
                {
                    // PostNamazu treats its optional Triggernometry integration probe as
                    // fatal when the legacy strong-named ACT identity cannot be loaded on
                    // modern .NET. Keep its already initialized base UI and core services.
                    statusLabel.Text = "Loaded; legacy Triggernometry integration unavailable.";
                }

                if (string.Equals(spec.Id, "act.foxtts", StringComparison.OrdinalIgnoreCase) &&
                    statusLabel.Text.StartsWith("Init Success", StringComparison.OrdinalIgnoreCase))
                {
                    removeTtsBridge = InstallFoxTtsBridge(instance, log);
                }
                else if (string.Equals(
                             spec.Id,
                             "triggernometry",
                             StringComparison.OrdinalIgnoreCase))
                {
                    triggernometryMonitor = new TriggernometryDiagnosticMonitor(
                        instance,
                        diagnostics,
                        log);
                }

                NormalizePluginRootControl(tabPage);
                if (string.Equals(spec.Id, "postnamazu", StringComparison.OrdinalIgnoreCase) &&
                    !System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription.StartsWith(
                        ".NET Framework",
                        StringComparison.OrdinalIgnoreCase))
                {
                    StopLegacyPostNamazuProcessMonitor(instance);
                    statusLabel.Text = NativePostNamazuBridge.Start(instance);
                }

                configurationForm.Text = string.IsNullOrWhiteSpace(tabPage.Text) ? spec.Id : tabPage.Text;
                configurationForm.Shown += (_, _) => NormalizePluginRootControl(tabPage);
                configurationForm.FormClosing += (_, eventArgs) =>
                {
                    if (eventArgs.CloseReason == CloseReason.UserClosing)
                    {
                        eventArgs.Cancel = true;
                        configurationForm.Hide();
                    }
                };
                ready.Set();
                Application.Run(new ApplicationContext());
            }
            catch (Exception ex)
            {
                diagnostics.Record(ex, "插件初始化顺序或生命周期", "Load/InitPlugin", true);
                log.Error(ex, $"ACT plugin '{spec.Id}' UI thread failed during initialization.");
                triggernometryMonitor?.Dispose();
                triggernometryMonitor = null;
                removeTtsBridge?.Invoke();
                removeTtsBridge = null;
                failure = ex;
                if (pluginData is not null)
                {
                    ActGlobals.oFormActMain.ActPlugins.Remove(pluginData);
                    pluginData.lblPluginTitle.Dispose();
                    pluginData.cbEnabled.Dispose();
                }
                tabPage?.Dispose();
                statusLabel?.Dispose();
                configurationForm?.Dispose();
                if (loadContext is not null && resolvingHandler is not null)
                {
                    loadContext.Resolving -= resolvingHandler;
                }
                ready.Set();
            }
        })
        {
            IsBackground = true,
            Name = $"ACT plugin UI: {spec.Id}",
        };
        uiThread.SetApartmentState(ApartmentState.STA);
        uiThread.Start();
        if (!ready.Wait(TimeSpan.FromSeconds(30)))
        {
            throw new TimeoutException($"ACT plugin {spec.Id} UI initialization timed out.");
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return new LoadedActPlugin(
            spec.Id,
            loadContext!,
            resolvingHandler!,
            instance!,
            deInitPlugin!,
            tabPage!,
            statusLabel!,
            pluginData!,
            configurationForm!,
            uiThread,
            removeTtsBridge,
            diagnostics,
            log,
            triggernometryMonitor);
    }

    public void Dispose()
    {
        if (disposing)
        {
            return;
        }

        disposing = true;
        triggernometryMonitor?.Dispose();
        removeTtsBridge?.Invoke();
        try
        {
            configurationForm.BeginInvoke((Action)(() =>
            {
                try
                {
                    if (string.Equals(Id, "postnamazu", StringComparison.OrdinalIgnoreCase))
                    {
                        NativePostNamazuBridge.Stop(instance);
                    }

                    deInitPlugin.Invoke(instance, null);
                    (instance as IDisposable)?.Dispose();
                    ActGlobals.oFormActMain.ActPlugins.Remove(pluginData);
                    pluginData.lblPluginTitle.Dispose();
                    pluginData.cbEnabled.Dispose();
                }
                catch (Exception ex)
                {
                    diagnostics.Record(ex, "插件生命周期问题", "DeInitPlugin", true);
                    log.Error(ex, $"ACT plugin '{Id}' failed during DeInitPlugin.");
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
            diagnostics.Record(ex, "WinForms 宿主问题", "Queue DeInitPlugin", false);
            log.Error(ex, $"ACT plugin '{Id}' could not queue DeInitPlugin on its UI thread.");
        }

        // In-process .NET threads cannot be terminated safely. Never wait long
        // enough here to freeze Dalamud/FFXIV; a hung plugin is left on its
        // background thread and reported until out-of-process hosting owns it.
        if (uiThread.Join(TimeSpan.FromMilliseconds(250)))
        {
            loadContext.Resolving -= resolvingHandler;
        }
        else
        {
            var timeout = new TimeoutException(
                $"ACT plugin '{Id}' did not stop its UI thread within 250 ms.");
            diagnostics.Record(timeout, "UI 线程或 SynchronizationContext 问题", "DeInitPlugin", false);
            log.Warning(
                $"ACT plugin '{Id}' did not stop promptly. The game thread was released; " +
                "only the independent host process can forcibly contain this plugin.");
        }
    }

    internal static Action InstallFoxTtsBridge(
        object plugin,
        IPluginLog log)
    {
        var speak = plugin.GetType().GetMethod(
                        "Speak",
                        BindingFlags.Instance | BindingFlags.Public,
                        binder: null,
                        types: [typeof(string)],
                        modifiers: null)
                    ?? throw new MissingMethodException(plugin.GetType().FullName, "Speak");
        var actMain = ActGlobals.oFormActMain;
        var previous = actMain.PlayTtsMethod;
        FormActMain.PlayTtsDelegate bridge = message =>
        {
            log.Debug($"ACT.FoxTTS speech request: {message}");
            void Speak()
            {
                try
                {
                    speak.Invoke(plugin, [message]);
                    log.Debug("ACT.FoxTTS accepted the speech request.");
                }
                catch (TargetInvocationException ex) when (ex.InnerException is not null)
                {
                    log.Error(ex.InnerException, "ACT.FoxTTS failed to speak a message.");
                }
                catch (Exception ex)
                {
                    log.Error(ex, "ACT.FoxTTS failed to speak a message.");
                }
            }

            // FoxTTS' own ACT injector dispatches Speak on the thread pool. Keep the in-process
            // compatibility path equivalent so slow synthesis cannot backlog the plugin UI.
            _ = Task.Run(Speak);
        };
        actMain.PlayTtsMethod = bridge;
        log.Information("ACT.FoxTTS connected to the ACT TTS dispatcher.");

        return () =>
        {
            if (ReferenceEquals(actMain.PlayTtsMethod, bridge))
            {
                actMain.PlayTtsMethod = previous;
            }
        };
    }

    private static void NormalizePluginRootControl(TabPage tabPage)
    {
        if (tabPage.Controls.Count != 1)
        {
            tabPage.PerformLayout();
            return;
        }

        var root = tabPage.Controls[0];
        root.Dock = DockStyle.Fill;
        root.Visible = true;
        root.BringToFront();
        tabPage.PerformLayout();
        root.PerformLayout();
    }

    private static void StopLegacyPostNamazuProcessMonitor(object instance)
    {
        var field = instance.GetType().GetField(
            "_processManager",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var manager = field?.GetValue(instance);
        manager?.GetType()
            .GetMethod("StopProcessMonitoring", BindingFlags.Instance | BindingFlags.Public)
            ?.Invoke(manager, null);
    }
}

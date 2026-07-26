using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using System.Windows.Forms;
using Advanced_Combat_Tracker;

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
        Thread uiThread)
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
    }

    public string Id { get; }

    public string Status => statusLabel.Text;

    public void OpenConfiguration()
        => configurationForm.BeginInvoke((Action)(() =>
        {
            configurationForm.Show();
            configurationForm.WindowState = FormWindowState.Normal;
            configurationForm.Activate();
            configurationForm.BringToFront();
        }));

    public static LoadedActPlugin Load(RuntimePluginSpec spec)
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
                var assembly = loadContext.LoadFromAssemblyPath(assemblyPath);
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

                NormalizePluginRootControl(tabPage);
                if (string.Equals(spec.Id, "postnamazu", StringComparison.OrdinalIgnoreCase) &&
                    !System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription.StartsWith(
                        ".NET Framework",
                        StringComparison.OrdinalIgnoreCase))
                {
                    StopLegacyPostNamazuProcessMonitor(instance);
                    statusLabel.Text = "Loaded; GreyMagic process injection is unavailable on modern .NET.";
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
            uiThread);
    }

    public void Dispose()
    {
        if (disposing)
        {
            return;
        }

        disposing = true;
        configurationForm.BeginInvoke((Action)(() =>
        {
            try
            {
                deInitPlugin.Invoke(instance, null);
                (instance as IDisposable)?.Dispose();
                ActGlobals.oFormActMain.ActPlugins.Remove(pluginData);
                pluginData.lblPluginTitle.Dispose();
                pluginData.cbEnabled.Dispose();
            }
            finally
            {
                configurationForm.Dispose();
                Application.ExitThread();
            }
        }));
        uiThread.Join(TimeSpan.FromSeconds(10));
        loadContext.Resolving -= resolvingHandler;
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

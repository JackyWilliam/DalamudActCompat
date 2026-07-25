using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using System.Windows.Forms;
using Advanced_Combat_Tracker;

namespace DalamudActCompat.ActRuntime;

internal sealed class LoadedActPlugin : IDisposable
{
    private readonly AssemblyLoadContext loadContext;
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
        var assemblyPath = Path.GetFullPath(Path.Combine(spec.InstallDirectory, spec.EntryAssembly));
        using var ready = new ManualResetEventSlim();
        Exception? failure = null;
        AssemblyLoadContext? loadContext = null;
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
                loadContext = new PluginLoadContext(assemblyPath);
                var assembly = loadContext.LoadFromAssemblyPath(assemblyPath);
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
                initPlugin.Invoke(instance, [tabPage, statusLabel]);

                var tabs = new TabControl { Dock = DockStyle.Fill };
                tabs.TabPages.Add(tabPage);
                configurationForm = new Form
                {
                    Text = string.IsNullOrWhiteSpace(tabPage.Text) ? spec.Id : tabPage.Text,
                    Width = 960,
                    Height = 720,
                    StartPosition = FormStartPosition.CenterScreen,
                    ShowInTaskbar = true,
                };
                configurationForm.Controls.Add(tabs);
                configurationForm.FormClosing += (_, eventArgs) =>
                {
                    if (eventArgs.CloseReason == CloseReason.UserClosing)
                    {
                        eventArgs.Cancel = true;
                        configurationForm.Hide();
                    }
                };
                _ = configurationForm.Handle;
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
                loadContext?.Unload();
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
        loadContext.Unload();
    }

    private sealed class PluginLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver resolver;

        public PluginLoadContext(string entryAssembly)
            : base($"DalamudActCompat:{Path.GetFileNameWithoutExtension(entryAssembly)}", isCollectible: true)
            => resolver = new AssemblyDependencyResolver(entryAssembly);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var shared = AssemblyLoadContext.Default.Assemblies.FirstOrDefault(
                assembly => AssemblyName.ReferenceMatchesDefinition(assembly.GetName(), assemblyName));
            if (shared is not null)
            {
                return shared;
            }

            var path = resolver.ResolveAssemblyToPath(assemblyName);
            return path is null ? null : LoadFromAssemblyPath(path);
        }

        protected override nint LoadUnmanagedDll(string unmanagedDllName)
        {
            var path = resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path is null ? nint.Zero : LoadUnmanagedDllFromPath(path);
        }
    }
}

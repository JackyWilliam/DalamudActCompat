using System.Reflection;
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

    private LoadedActPlugin(
        string id,
        AssemblyLoadContext loadContext,
        object instance,
        MethodInfo deInitPlugin,
        TabPage tabPage,
        Label statusLabel,
        ActPluginData pluginData)
    {
        Id = id;
        this.loadContext = loadContext;
        this.instance = instance;
        this.deInitPlugin = deInitPlugin;
        this.tabPage = tabPage;
        this.statusLabel = statusLabel;
        this.pluginData = pluginData;
    }

    public string Id { get; }

    public string Status => statusLabel.Text;

    public static LoadedActPlugin Load(RuntimePluginSpec spec)
    {
        var assemblyPath = Path.GetFullPath(Path.Combine(spec.InstallDirectory, spec.EntryAssembly));
        var loadContext = new PluginLoadContext(assemblyPath);
        TabPage? tabPage = null;
        Label? statusLabel = null;
        ActPluginData? pluginData = null;
        try
        {
            var assembly = loadContext.LoadFromAssemblyPath(assemblyPath);
            var entryType = assembly.GetType(spec.EntryType, throwOnError: true)!;
            var instance = Activator.CreateInstance(entryType)
                           ?? throw new InvalidOperationException($"Could not create plugin type {spec.EntryType}.");
            if (instance is not IActPluginV1 actPlugin)
            {
                throw new InvalidOperationException($"{spec.EntryType} does not implement IActPluginV1.");
            }

            var initPlugin = entryType.GetMethod("InitPlugin", BindingFlags.Public | BindingFlags.Instance)
                             ?? throw new MissingMethodException(spec.EntryType, "InitPlugin");
            var deInitPlugin = entryType.GetMethod("DeInitPlugin", BindingFlags.Public | BindingFlags.Instance)
                               ?? throw new MissingMethodException(spec.EntryType, "DeInitPlugin");
            tabPage = new TabPage(spec.Id);
            statusLabel = new Label();
            pluginData = new ActPluginData(
                new FileInfo(assemblyPath),
                actPlugin,
                tabPage,
                statusLabel);
            ActGlobals.oFormActMain.ActPlugins.Add(pluginData);
            initPlugin.Invoke(instance, [tabPage, statusLabel]);
            return new LoadedActPlugin(
                spec.Id,
                loadContext,
                instance,
                deInitPlugin,
                tabPage,
                statusLabel,
                pluginData);
        }
        catch
        {
            if (pluginData is not null)
            {
                ActGlobals.oFormActMain.ActPlugins.Remove(pluginData);
                pluginData.lblPluginTitle.Dispose();
                pluginData.cbEnabled.Dispose();
            }

            tabPage?.Dispose();
            statusLabel?.Dispose();
            loadContext.Unload();
            throw;
        }
    }

    public void Dispose()
    {
        try
        {
            deInitPlugin.Invoke(instance, null);
            (instance as IDisposable)?.Dispose();
        }
        finally
        {
            ActGlobals.oFormActMain.ActPlugins.Remove(pluginData);
            tabPage.Dispose();
            statusLabel.Dispose();
            pluginData.lblPluginTitle.Dispose();
            pluginData.cbEnabled.Dispose();
            loadContext.Unload();
        }
    }

    private sealed class PluginLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver resolver;

        public PluginLoadContext(string entryAssembly)
            : base($"DalamudActCompat:{Path.GetFileNameWithoutExtension(entryAssembly)}", isCollectible: true)
        {
            resolver = new AssemblyDependencyResolver(entryAssembly);
        }

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

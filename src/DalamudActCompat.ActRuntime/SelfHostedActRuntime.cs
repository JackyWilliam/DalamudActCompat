using Advanced_Combat_Tracker;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using System.Reflection;

namespace DalamudActCompat.ActRuntime;

public sealed class SelfHostedActRuntime : IDisposable
{
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;
    private readonly IDataManager dataManager;
    private readonly IChatGui chatGui;
    private readonly IFramework framework;
    private readonly ICondition condition;
    private IINACT.FfxivActPluginWrapper? parser;
    private RainbowMage.OverlayPlugin.PluginMain? overlay;
    private HttpClient? httpClient;
    private readonly List<LoadedActPlugin> customPlugins = [];
    private bool actGlobalsInitialized;

    public SelfHostedActRuntime(
        IDalamudPluginInterface pluginInterface,
        IPluginLog log,
        IDataManager dataManager,
        IChatGui chatGui,
        IFramework framework,
        ICondition condition)
    {
        this.pluginInterface = pluginInterface;
        this.log = log;
        this.dataManager = dataManager;
        this.chatGui = chatGui;
        this.framework = framework;
        this.condition = condition;
    }

    public bool IsParserRunning => parser is not null;

    public bool IsOverlayRunning => overlay is not null;

    public IReadOnlyList<string> LoadedCustomPluginIds
        => customPlugins.Select(plugin => plugin.Id).ToArray();

    public IINACT.FfxivActPluginWrapper Parser
        => parser ?? throw new InvalidOperationException("FFXIV_ACT_Plugin is not running.");

    public void StartParser(string logDirectory)
    {
        if (IsParserRunning)
        {
            return;
        }

        Directory.CreateDirectory(logDirectory);
        SetUpstreamLogger();
        ActGlobals.Init();
        actGlobalsInitialized = true;
        ActGlobals.oFormActMain = new FormActMain(log)
        {
            LogFilePath = logDirectory,
            WriteLogFile = true,
        };

        var configuration = new IINACT.Configuration
        {
            LogFilePath = logDirectory,
            WriteLogFile = true,
        };
        configuration.Initialize(pluginInterface);
        try
        {
            parser = new IINACT.FfxivActPluginWrapper(
                configuration,
                dataManager.Language,
                chatGui,
                framework,
                condition);
        }
        catch
        {
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
            }
            catch (Exception ex)
            {
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
            parser?.Dispose();
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to stop FFXIV_ACT_Plugin.");
        }

        parser = null;
        if (actGlobalsInitialized)
        {
            try
            {
                ActGlobals.Dispose();
            }
            finally
            {
                actGlobalsInitialized = false;
            }
        }
    }

    public void Dispose() => StopParser();

    private void SetUpstreamLogger()
    {
        var property = typeof(IINACT.Plugin).GetProperty(
            nameof(IINACT.Plugin.Log),
            BindingFlags.Public | BindingFlags.Static);
        property?.SetValue(null, log);
    }
}

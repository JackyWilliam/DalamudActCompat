using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using DalamudActCompat.Core.Interfaces;
using DalamudActCompat.ActRuntime;
using DalamudActCompat.Compatibility.PluginHost;
using DalamudActCompat.Compatibility.Cactbot;
using DalamudActCompat.Core.Models;
using DalamudActCompat.Core.State;
using DalamudActCompat.Encounters;
using DalamudActCompat.Infrastructure.Ipc;
using DalamudActCompat.Infrastructure.Logging;
using DalamudActCompat.Infrastructure.Processes;
using DalamudActCompat.Infrastructure.Storage;
using DalamudActCompat.Meter;
using DalamudActCompat.Overlay;
using DalamudActCompat.Parser;
using DalamudActCompat.Protocol;
using DalamudActCompat.UI;
using System.Threading.Channels;

namespace DalamudActCompat.Plugin;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/actcompat";

    private readonly PluginServices services;
    private readonly WindowSystem windowSystem = new("DalamudActCompat");
    private readonly PluginConfiguration configuration;
    private readonly PluginPaths paths;
    private readonly PluginLogger logger;
    private readonly EncounterStateStore stateStore;
    private readonly IParserEngine parserEngine;
    private readonly SelfHostedActRuntime actRuntime;
    private readonly EncounterService encounterService;
    private readonly PluginLifecycle lifecycle;
    private readonly MeterWindow meterWindow;
    private readonly EncounterWindow encounterWindow;
    private readonly LogHistoryWindow logHistoryWindow;
    private readonly ControlCenterWindow settingsWindow;
    private readonly SettingsWindow advancedSettingsWindow;
    private readonly StatusWindow statusWindow;
    private readonly LauncherWindow launcherWindow;
    private readonly ThirdPartyPluginNoticeWindow thirdPartyPluginNoticeWindow;
    private readonly FactoryResetService factoryResetService;
    private readonly ActPluginPackageInstaller packageInstaller;
    private readonly BundledActPluginManager bundledPluginManager;
    private readonly BundledActPluginUpdateChecker bundledPluginUpdateChecker;
    private readonly CactbotPackageInstaller cactbotInstaller;
    private readonly FileDialogManager fileDialogManager = new();
    private readonly CancellationTokenSource bundledUpdateCancellation = new();
    private readonly SemaphoreSlim bundledUpdateCheckLock = new(1, 1);
    private readonly ActHostSupervisor hostSupervisor;
    private readonly Channel<HostCommandInvocation> hostCommandQueue =
        Channel.CreateBounded<HostCommandInvocation>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    private readonly CancellationTokenSource hostCommandCancellation = new();
    private readonly Task hostCommandWorker;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IPluginLog log,
        IClientState clientState,
        IPlayerState playerState,
        IDataManager dataManager,
        IChatGui chatGui,
        IFramework framework,
        ICondition condition,
        IGameInteropProvider gameInteropProvider,
        INotificationManager notificationManager,
        IPartyList partyList,
        ITextureProvider textureProvider)
    {
        services = new PluginServices(
            pluginInterface,
            commandManager,
            log,
            clientState,
            dataManager,
            chatGui,
            framework,
            condition,
            gameInteropProvider,
            notificationManager);
        var localDeathWhilePartyContinues = () =>
            condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Unconscious] &&
            condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BoundByDuty] &&
            partyList.Count > 1 &&
            partyList.Any(member => member.CurrentHP > 0);
        configuration = pluginInterface.GetPluginConfig() as PluginConfiguration ?? new PluginConfiguration();
        logger = new PluginLogger(log);
        paths = new PluginPaths(pluginInterface, configuration.ActPluginDirectory);
        if (string.IsNullOrWhiteSpace(configuration.LogDirectory))
        {
            configuration.LogDirectory = paths.CombatLogDirectory;
        }

        packageInstaller = new ActPluginPackageInstaller(paths);
        bundledPluginManager = new BundledActPluginManager(
            pluginInterface.AssemblyLocation.Directory!.FullName,
            typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "unknown",
            packageInstaller,
            configuration);
        bundledPluginUpdateChecker = new BundledActPluginUpdateChecker(
            paths.BundledPluginUpdateCacheDirectory);
        stateStore = new EncounterStateStore();
        paths.EnsureCreated();
        var hostIpcClient = new HostIpcClient(
            stateStore,
            logger,
            BuildHostPermissionSnapshot);
        hostSupervisor = new ActHostSupervisor(
            paths.HostDirectory,
            paths.ActPluginDirectory,
            paths.ConfigDirectory,
            hostIpcClient,
            logger);
        hostSupervisor.CommandRequested += OnHostCommandRequested;
        hostCommandWorker = Task.Run(
            () => RunHostCommandBrokerAsync(hostCommandCancellation.Token),
            CancellationToken.None);
        var jsonStore = new JsonFileStore();
        var repository = new EncounterRepository(jsonStore, paths);
        encounterService = new EncounterService(repository, stateStore, configuration, logger, paths);
        actRuntime = new SelfHostedActRuntime(
            pluginInterface,
            log,
            dataManager,
            () => playerState.CharacterName,
            () => BuildPlayerIdentities(
                playerState,
                partyList,
                condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Unconscious]),
            chatGui,
            framework,
            condition,
            gameInteropProvider,
            notificationManager,
            localDeathWhilePartyContinues,
            configuration.GetOverlayWindowSettings,
            () => configuration.DebugMode,
            configuration.IsActCapabilityAllowed);
        actRuntime.ConfigureExternalPluginBridges(
            text => hostSupervisor.RequestTts(text, "game-side-act"),
            (action, payload) => hostSupervisor.InvokePluginAction(
                "postnamazu",
                "overlay",
                new Dictionary<string, string>
                {
                    ["command"] = action,
                    ["payload"] = payload,
                }));
        actRuntime.RawLogLineReceived += OnRawLogLineForHost;
        actRuntime.ZoneChanged += OnZoneChangedForHost;
        actRuntime.EncounterChanged += OnEncounterChangedForHost;
        parserEngine = new ParserEngine(new IinactAdapter(
            actRuntime,
            logger,
            stateStore,
            encounterService,
            paths.CombatLogDirectory,
            () => configuration.EmbeddedPlugins.FfxivActPluginEnabled,
            () => configuration.EmbeddedPlugins.OverlayPluginEnabled,
            DiscoverRuntimePlugins));
        var meterService = new MeterService(stateStore, configuration.Meter);

        _ = new OverlayManager(new OverlayEventBus());

        var text = new UiText(configuration);
        var assetDirectory = Path.Combine(
            pluginInterface.AssemblyLocation.Directory!.FullName,
            "Assets");
        var logoTexture = textureProvider.GetFromFile(
            Path.Combine(assetDirectory, "act-logo.jpg"));
        var launcherTexture = textureProvider.GetFromFile(
            Path.Combine(assetDirectory, "act-button.png"));
        meterWindow = new MeterWindow(meterService, stateStore, configuration, text, SaveConfiguration);
        meterWindow.IsOpen = configuration.Meter.IsVisible;
        logHistoryWindow = new LogHistoryWindow(paths, text);
        encounterWindow = new EncounterWindow(stateStore, text, () => logHistoryWindow.IsOpen = true);
        factoryResetService = new FactoryResetService(
            parserEngine,
            paths,
            configuration,
            logger,
            SaveConfiguration);
        cactbotInstaller = new CactbotPackageInstaller(paths);
        thirdPartyPluginNoticeWindow = new ThirdPartyPluginNoticeWindow(
            bundledPluginManager.GetPendingDisclosures,
            InstallBundledPluginsAsync,
            logger,
            text);
        advancedSettingsWindow = new SettingsWindow(
            configuration,
            parserEngine,
            paths,
            logger,
            SaveConfiguration,
            FactoryResetAsync,
            () => packageInstaller.Discover(configuration.DisabledActPluginIds),
            SelectPluginPackage,
            OpenPluginDirectory,
            text,
            () => cactbotInstaller.IsInstalled,
            SelectCactbotPackage,
            OpenCactbotOverlay,
            OpenCactbotSettings,
            () => actRuntime.OverlayTemplates,
            OpenHtmlOverlay,
            name => _ = actRuntime.ApplyOverlayWindowSettings(name),
            OpenActPluginConfiguration,
            () => StartBundledPluginUpdateCheck(openWindow: true));
        statusWindow = new StatusWindow(
            parserEngine,
            text,
            () => hostSupervisor.Snapshot,
            RestartHostFromUi,
            StopHostFromUi);
        settingsWindow = new ControlCenterWindow(
            configuration,
            parserEngine,
            logger,
            text,
            logoTexture,
            SaveConfiguration,
            SetMeterVisible,
            () => SetMeterVisible(true),
            () => encounterWindow.IsOpen = true,
            () => statusWindow.IsOpen = true,
            () => advancedSettingsWindow.IsOpen = true,
            () => cactbotInstaller.IsInstalled,
            SelectCactbotPackage,
            OpenCactbotOverlay,
            OpenCactbotSettings,
            () => actRuntime.OverlayTemplates,
            OpenHtmlOverlay,
            name => _ = actRuntime.ApplyOverlayWindowSettings(name));
        launcherWindow = new LauncherWindow(
            configuration,
            launcherTexture,
            text,
            () => settingsWindow.IsOpen = true,
            ToggleMeter,
            SaveConfiguration)
        {
            IsOpen = true,
        };
        windowSystem.AddWindow(meterWindow);
        windowSystem.AddWindow(encounterWindow);
        windowSystem.AddWindow(logHistoryWindow);
        windowSystem.AddWindow(settingsWindow);
        windowSystem.AddWindow(advancedSettingsWindow);
        windowSystem.AddWindow(statusWindow);
        windowSystem.AddWindow(thirdPartyPluginNoticeWindow);
        windowSystem.AddWindow(launcherWindow);

        pluginInterface.UiBuilder.Draw += Draw;
        pluginInterface.UiBuilder.OpenConfigUi += OpenConfigUi;
        pluginInterface.UiBuilder.OpenMainUi += OpenMainUi;
        commandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open ACT Compat UI. Args: meter, cactbot, overlay [template], history, status, settings, sample, clear, host, stop, install <dll-or-zip>, factory-reset.",
        });

        lifecycle = new PluginLifecycle(parserEngine, encounterService, paths, configuration, logger);
        EnsureBundledCactbot(pluginInterface.AssemblyLocation.Directory!.FullName);
        lifecycle.Start();
        _ = Task.Run(StartIndependentHostAsync);
        StartBundledPluginUpdateCheck(openWindow: false);
    }

    public string Name => "Dalamud ACT Compat";

    public void Dispose()
    {
        bundledUpdateCancellation.Cancel();
        bundledPluginUpdateChecker.Dispose();
        services.CommandManager.RemoveHandler(CommandName);
        services.PluginInterface.UiBuilder.Draw -= Draw;
        services.PluginInterface.UiBuilder.OpenConfigUi -= OpenConfigUi;
        services.PluginInterface.UiBuilder.OpenMainUi -= OpenMainUi;
        settingsWindow.Detach();
        advancedSettingsWindow.Detach();
        statusWindow.Detach();
        windowSystem.RemoveAllWindows();
        actRuntime.RawLogLineReceived -= OnRawLogLineForHost;
        actRuntime.ZoneChanged -= OnZoneChangedForHost;
        actRuntime.EncounterChanged -= OnEncounterChangedForHost;
        hostSupervisor.CommandRequested -= OnHostCommandRequested;

        SaveConfiguration();
        var shutdown = Task.Run(DisposeComponentsAsync);
        var completed = ReferenceEquals(
            Task.WhenAny(shutdown, Task.Delay(TimeSpan.FromMilliseconds(250)))
                .GetAwaiter()
                .GetResult(),
            shutdown);
        if (!completed)
        {
            logger.Warning(
                "ACT compatibility shutdown exceeded 250 ms. Dalamud/FFXIV was released; " +
                "remaining in-process plugin cleanup will continue only on the background task.");
            _ = shutdown.ContinueWith(
                task => logger.Error(
                    task.Exception?.GetBaseException()
                    ?? new InvalidOperationException("Unknown shutdown failure."),
                    "ACT compatibility background shutdown failed."),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        else if (shutdown.IsFaulted)
        {
            logger.Error(
                shutdown.Exception?.GetBaseException() ?? new InvalidOperationException("Unknown shutdown failure."),
                "ACT compatibility background shutdown failed.");
        }
    }

    private void Draw()
    {
        OverlayEditShield.Draw(actRuntime.HasVisibleEditingOverlay);
        windowSystem.Draw();
        fileDialogManager.Draw();
    }

    private void OpenConfigUi() => settingsWindow.IsOpen = true;

    private void OpenMainUi()
        => settingsWindow.IsOpen = true;

    private void ToggleMeter()
    {
        var shouldShow = !configuration.Meter.IsVisible || !meterWindow.IsOpen;
        SetMeterVisible(shouldShow);
    }

    private void SetMeterVisible(bool visible)
    {
        configuration.Meter.IsVisible = visible;
        meterWindow.IsOpen = visible;
        SaveConfiguration();
    }

    private void OnCommand(string command, string arguments)
    {
        var trimmedArguments = arguments.Trim();
        var separator = trimmedArguments.IndexOf(' ');
        var verb = (separator < 0 ? trimmedArguments : trimmedArguments[..separator]).ToLowerInvariant();
        var remainder = separator < 0 ? string.Empty : trimmedArguments[(separator + 1)..].Trim();
        switch (verb)
        {
            case "history":
                encounterWindow.IsOpen = true;
                break;
            case "logs":
                logHistoryWindow.IsOpen = true;
                break;
            case "status":
                statusWindow.IsOpen = true;
                break;
            case "settings":
                settingsWindow.IsOpen = true;
                break;
            case "cactbot":
                if (!actRuntime.ShowCactbotOverlay())
                {
                    logger.Warning(
                        "Cactbot overlay is not running. Install Cactbot, enable OverlayPlugin, and restart the parser.");
                }
                break;
            case "overlay":
                OpenHtmlOverlay(string.IsNullOrWhiteSpace(remainder)
                    ? configuration.SelectedOverlayTemplate
                    : remainder);
                break;
            case "sample":
                LoadSampleEncounter();
                meterWindow.IsOpen = true;
                break;
            case "host":
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                        await hostSupervisor.StartAsync(timeout.Token).ConfigureAwait(false);
                        await parserEngine.RestartAsync(timeout.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        logger.Error(ex, "Host bridge command failed.");
                    }
                });
                statusWindow.IsOpen = true;
                break;
            case "stop":
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        await parserEngine.StopAsync(timeout.Token).ConfigureAwait(false);
                        await hostSupervisor.StopAsync(timeout.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        logger.Error(ex, "Host bridge stop command failed.");
                    }
                });
                break;
            case "clear":
                stateStore.ResetCurrent();
                break;
            case "factory-reset":
                settingsWindow.IsOpen = true;
                break;
            case "install":
                InstallActPlugin(remainder);
                break;
            case "meter":
            case "":
                meterWindow.IsOpen = true;
                break;
            default:
                settingsWindow.IsOpen = true;
                break;
        }
    }

    private void LoadSampleEncounter()
    {
        var snapshot = stateStore.GetSnapshot();
        stateStore.Replace(SampleEncounterFactory.Create(DateTimeOffset.UtcNow), snapshot.Recent);
        logger.Information("Loaded sample encounter snapshot.");
    }

    private void SaveConfiguration()
    {
        try
        {
            services.PluginInterface.SavePluginConfig(configuration);
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Failed to save plugin configuration.");
        }
    }

    private void EnsureBundledCactbot(string pluginAssemblyDirectory)
    {
        try
        {
            var bundledCactbotManager = new BundledCactbotManager(
                pluginAssemblyDirectory,
                cactbotInstaller);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var installed = bundledCactbotManager
                .EnsureCurrentAsync(timeout.Token)
                .GetAwaiter()
                .GetResult();
            if (installed)
            {
                logger.Information(
                    $"Installed bundled Cactbot {bundledCactbotManager.BundledVersion} into this user's plugin configuration.");
            }
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Bundled Cactbot installation failed.");
        }
    }

    private async Task<string> FactoryResetAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        return await factoryResetService.ResetAsync(timeout.Token).ConfigureAwait(false);
    }

    private void InstallActPlugin(string packagePath)
    {
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            logger.Warning("Usage: /actcompat install <path-to-plugin.dll-or-zip>");
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var installed = await packageInstaller.InstallAsync(
                    packagePath.Trim('"'),
                    timeout.Token).ConfigureAwait(false);
                configuration.DisabledActPluginIds.Remove(installed.Manifest.Id);
                SaveConfiguration();
                logger.Information(
                    $"Installed ACT plugin {installed.Manifest.Name} {installed.Manifest.Version}. Restart the ACT host to load it.");
            }
            catch (Exception ex)
            {
                logger.Error(ex, "ACT plugin package installation failed.");
            }
        });
    }

    private async Task InstallBundledPluginsAsync(
        IReadOnlyList<BundledActPluginDescriptor> plugins)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await hostSupervisor.StopAsync(timeout.Token).ConfigureAwait(false);
        await parserEngine.StopAsync(timeout.Token).ConfigureAwait(false);
        await bundledPluginManager
            .InstallAndAcknowledgeAsync(plugins, timeout.Token)
            .ConfigureAwait(false);
        SaveConfiguration();
        await hostSupervisor.StartAsync(timeout.Token).ConfigureAwait(false);
        if (configuration.EnableParsing && configuration.AutoStartParser)
        {
            await parserEngine.StartAsync(timeout.Token).ConfigureAwait(false);
        }

        services.NotificationManager.AddNotification(new()
        {
            Title = "ACT 兼容",
            Content = "第三方 DLL 已按告知版本安装/更新；作者、版本和来源告知已记录。",
        });
    }

    private void StartBundledPluginUpdateCheck(bool openWindow)
    {
        _ = Task.Run(async () =>
        {
            var cancellationToken = bundledUpdateCancellation.Token;
            if (!await bundledUpdateCheckLock
                    .WaitAsync(0)
                    .ConfigureAwait(false))
            {
                if (openWindow)
                {
                    await services.Framework
                        .RunOnFrameworkThread(thirdPartyPluginNoticeWindow.OpenNotice)
                        .ConfigureAwait(false);
                }

                return;
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await services.Framework
                    .RunOnFrameworkThread(
                        () => thirdPartyPluginNoticeWindow.BeginUpdateCheck(openWindow))
                    .ConfigureAwait(false);
                var check = await bundledPluginUpdateChecker
                    .CheckAsync(
                        bundledPluginManager.Plugins,
                        cancellationToken)
                    .ConfigureAwait(false);
                var appliedUpdates = bundledPluginManager.ApplyOnlineUpdates(
                    check.Updates);
                var pendingOnline = bundledPluginManager
                    .GetPendingDisclosures()
                    .Where(plugin => plugin.IsOnlineUpdate)
                    .ToArray();
                if (appliedUpdates > 0 && pendingOnline.Length > 0)
                {
                    using var stopTimeout = CancellationTokenSource
                        .CreateLinkedTokenSource(cancellationToken);
                    stopTimeout.CancelAfter(TimeSpan.FromSeconds(20));
                    await parserEngine
                        .StopAsync(stopTimeout.Token)
                        .ConfigureAwait(false);
                    await hostSupervisor
                        .StopAsync(stopTimeout.Token)
                        .ConfigureAwait(false);
                }

                var message = BuildBundledPluginUpdateMessage(
                    check,
                    pendingOnline.Length);
                await services.Framework
                    .RunOnFrameworkThread(
                        () => thirdPartyPluginNoticeWindow.CompleteUpdateCheck(
                            message,
                            openWindow))
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Plugin shutdown cancels the online check.
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                // Disposing the HTTP client can surface a non-cancellation exception.
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Bundled ACT plugin online update check failed.");
                await services.Framework
                    .RunOnFrameworkThread(
                        () => thirdPartyPluginNoticeWindow.CompleteUpdateCheck(
                            $"DLL 在线更新检查失败；仍可使用安装包内版本：{ex.GetBaseException().Message}",
                            openWindow))
                    .ConfigureAwait(false);
            }
            finally
            {
                bundledUpdateCheckLock.Release();
            }
        });
    }

    private static string BuildBundledPluginUpdateMessage(
        BundledActPluginUpdateCheckResult check,
        int pendingOnlineCount)
    {
        var summary = pendingOnlineCount > 0
            ? $"发现 {pendingOnlineCount} 项作者上游 DLL 更新；确认前这些 DLL 不会重新加载。"
            : "DLL 作者上游检查完成，当前没有新的在线更新。";
        if (check.Failures.Count == 0)
        {
            return summary;
        }

        return $"{summary} {check.Failures.Count} 项来源检查失败：" +
               string.Join("；", check.Failures);
    }

    private void SelectPluginPackage()
    {
        if (!Directory.Exists(paths.ActPluginDirectory))
        {
            ChoosePluginDirectory(SelectPluginPackage);
            return;
        }

        fileDialogManager.OpenFileDialog(
            "Select ACT plugin package",
            "ACT plugin{.dll,.zip}",
            (success, selectedPath) =>
            {
                if (success && !string.IsNullOrWhiteSpace(selectedPath))
                {
                    InstallActPlugin(selectedPath);
                }
            });
    }

    private void SelectCactbotPackage()
    {
        fileDialogManager.OpenFileDialog(
            "选择 OverlayPlugin/cactbot 官方 Release ZIP",
            "Cactbot Release{.zip}",
            async (success, selectedPath) =>
            {
                if (!success || string.IsNullOrWhiteSpace(selectedPath))
                {
                    return;
                }

                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                    await cactbotInstaller.InstallAsync(selectedPath, timeout.Token).ConfigureAwait(false);
                    services.NotificationManager.AddNotification(new()
                    {
                        Title = "ACT 兼容",
                        Content = "Cactbot 资源已安装。重启解析器以加载 OverlayPlugin 事件源。",
                    });
                }
                catch (Exception ex)
                {
                    logger.Error(ex, "Cactbot installation failed.");
                }
            });
    }

    private void OpenPluginDirectory()
    {
        if (!Directory.Exists(paths.ActPluginDirectory))
        {
            ChoosePluginDirectory(OpenPluginDirectory);
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(paths.ActPluginDirectory)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Failed to open the ACT plugin directory.");
        }
    }

    private void OpenActPluginConfiguration(string pluginId)
    {
        if (hostSupervisor.OpenPluginUi(pluginId))
        {
            return;
        }

        logger.Warning($"ACT plugin '{pluginId}' is not running in the independent Host.");
        services.NotificationManager.AddNotification(new()
        {
            Title = "ACT 兼容",
            Content = $"扩展 {pluginId} 未在独立 Host 中成功加载，请重启 Host 并查看状态日志。",
        });
    }

    private void OpenCactbotOverlay()
    {
        if (!actRuntime.ShowCactbotOverlay())
        {
            logger.Warning(
                "Cactbot overlay is not running. Install Cactbot, enable OverlayPlugin, and restart the parser.");
        }
    }

    private void OpenCactbotSettings()
    {
        if (!actRuntime.ShowCactbotSettings())
        {
            logger.Warning(
                "Cactbot settings are not available. Install Cactbot, enable OverlayPlugin, and restart the parser.");
        }
    }

    private void OpenHtmlOverlay(string name)
    {
        if (actRuntime.ShowHtmlOverlay(name))
        {
            configuration.SelectedOverlayTemplate = name;
            SaveConfiguration();
            return;
        }

        logger.Warning(
            $"HTML overlay '{name}' is unavailable. Enable OverlayPlugin, restart the parser, and select a listed template.");
    }

    private void ChoosePluginDirectory(Action continueWith)
    {
        fileDialogManager.OpenFolderDialog(
            "Choose where the ACT plugin folder should be created",
            (success, directory) =>
            {
                if (!success || string.IsNullOrWhiteSpace(directory))
                {
                    return;
                }

                paths.SetActPluginDirectory(directory);
                configuration.ActPluginDirectory = paths.ActPluginDirectory;
                SaveConfiguration();
                Directory.CreateDirectory(paths.ActPluginDirectory);
                continueWith();
            });
    }

    private async Task StartIndependentHostAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await hostSupervisor.StartAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.Error(
                ex,
                "Independent ACT Host did not start. In-process parsing and overlays remain available, " +
                "but traditional ACT plugins stay unloaded because hard crash isolation is not active.");
        }
    }

    private void RestartHostFromUi()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                await hostSupervisor.StopAsync(timeout.Token).ConfigureAwait(false);
                await hostSupervisor.StartAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "ACT Host UI restart failed.");
            }
        });
    }

    private void StopHostFromUi()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await hostSupervisor.StopAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "ACT Host UI stop failed.");
            }
        });
    }

    private HostPermissionSnapshot BuildHostPermissionSnapshot()
    {
        var capabilities = Enum.GetValues<ActCapability>();
        string[] pluginIds = ["triggernometry", "postnamazu"];
        var allowed = pluginIds.ToDictionary(
            pluginId => pluginId,
            pluginId => (IReadOnlyList<string>)capabilities
                .Where(capability =>
                    configuration.IsActCapabilityAllowed(pluginId, capability))
                .Select(capability => capability.ToString())
                .ToArray(),
            StringComparer.OrdinalIgnoreCase);
        return new HostPermissionSnapshot(
            allowed,
            packageInstaller
                .Discover(configuration.DisabledActPluginIds)
                .Where(plugin =>
                    plugin.Enabled &&
                    bundledPluginManager.IsAllowedToLoad(plugin))
                .Select(plugin => plugin.Manifest.Id)
                .ToArray());
    }

    private async Task DisposeComponentsAsync()
    {
        hostCommandQueue.Writer.TryComplete();
        await hostCommandCancellation.CancelAsync().ConfigureAwait(false);
        try
        {
            await hostCommandWorker.WaitAsync(TimeSpan.FromMilliseconds(500))
                .ConfigureAwait(false);
            hostCommandCancellation.Dispose();
        }
        catch (TimeoutException)
        {
            logger.Warning(
                "ACT Host command broker did not stop within 500 ms; it remains off the game thread.");
        }

        await lifecycle.DisposeAsync().ConfigureAwait(false);
        await encounterService.DisposeAsync().ConfigureAwait(false);
        await parserEngine.DisposeAsync().ConfigureAwait(false);
        await hostSupervisor.DisposeAsync().ConfigureAwait(false);
    }

    private void OnRawLogLineForHost(DateTimeOffset timestamp, string line, bool isImport)
        => hostSupervisor.PublishLog(timestamp, line, isImport);

    private void OnZoneChangedForHost(uint territoryId, string zoneName)
        => hostSupervisor.PublishZone(territoryId, zoneName);

    private void OnEncounterChangedForHost(ActEncounterSnapshot _, bool finished)
        => hostSupervisor.PublishEncounter(finished);

    private void OnHostCommandRequested(object? sender, HostCommandInvocation invocation)
    {
        if (hostCommandQueue.Writer.TryWrite(invocation))
        {
            return;
        }

        logger.Warning(
            $"ACT Host command broker queue is full; rejecting " +
            $"plugin={invocation.Request.PluginId} action={invocation.Request.Command}.");
        hostSupervisor.ReplyCommand(
            invocation.CorrelationId,
            false,
            "busy",
            "The bounded game-side command broker queue is full.");
    }

    private async Task RunHostCommandBrokerAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var invocation in hostCommandQueue.Reader.ReadAllAsync(cancellationToken)
                               .ConfigureAwait(false))
            {
                await HandleHostCommandAsync(invocation, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task HandleHostCommandAsync(
        HostCommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            switch (invocation.Request.Command)
            {
                case "tts":
                    if (!string.Equals(
                            invocation.Request.PluginId,
                            "triggernometry",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        throw new UnauthorizedAccessException(
                            "Only Triggernometry may request the TTS broker capability.");
                    }

                    if (!configuration.IsActCapabilityAllowed(
                            "triggernometry",
                            ActCapability.TextToSpeech))
                    {
                        throw new UnauthorizedAccessException(
                            "Triggernometry TTS capability is denied.");
                    }

                    if (!invocation.Request.Arguments.TryGetValue("text", out var speech) ||
                        speech.Length is 0 or > 2000)
                    {
                        throw new InvalidDataException("TTS payload is invalid.");
                    }

                    // Authorization remains game-side, while the actual FoxTTS callback
                    // stays in the disposable external Host. A blocked/native TTS provider
                    // can therefore be recovered by killing the Host without freezing FFXIV.
                    break;
                case "postnamazu.chat":
                    if (!string.Equals(
                            invocation.Request.PluginId,
                            "postnamazu",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        throw new UnauthorizedAccessException(
                            "Only PostNamazu may request the game-command broker capability.");
                    }

                    if (!configuration.IsActCapabilityAllowed(
                            "postnamazu",
                            ActCapability.GameCommand))
                    {
                        throw new UnauthorizedAccessException(
                            "PostNamazu game-command capability is denied.");
                    }

                    if (!invocation.Request.Arguments.TryGetValue("text", out var command))
                    {
                        throw new InvalidDataException("PostNamazu command payload is missing.");
                    }

                    ValidatePostNamazuCommand(command);
                    await NativePostNamazuBridge
                        .SendCommandAsync(command, timeout.Token)
                        .ConfigureAwait(false);
                    break;
                default:
                    throw new UnauthorizedAccessException(
                        $"Unknown Host command '{invocation.Request.Command}' is denied.");
            }

            logger.Information(
                $"ACT Host command broker allowed plugin={invocation.Request.PluginId} " +
                $"action={invocation.Request.Command}.");

            hostSupervisor.ReplyCommand(
                invocation.CorrelationId,
                true,
                "completed",
                null);
        }
        catch (Exception ex)
        {
            logger.Error(ex, $"ACT Host command '{invocation.Request.Command}' was rejected.");
            hostSupervisor.ReplyCommand(
                invocation.CorrelationId,
                false,
                "denied",
                ex.Message);
        }
    }

    private static void ValidatePostNamazuCommand(string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        if (command.Length > 500 || command.Contains('\r') || command.Contains('\n'))
        {
            throw new InvalidDataException(
                "PostNamazu commands must be a single line of at most 500 characters.");
        }

        if (!command.StartsWith('/') || command.StartsWith("//", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "PostNamazu commands must use one slash-prefixed semantic command.");
        }

        var verb = command.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0];
        string[] allowed =
        [
            "/e",
            "/echo",
            "/p",
            "/party",
            "/mk",
            "/marking",
            "/waymark",
        ];
        if (!allowed.Contains(verb, StringComparer.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException(
                $"PostNamazu command verb '{verb}' is outside the game-side whitelist.");
        }
    }

    private IReadOnlyList<RuntimePluginSpec> DiscoverRuntimePlugins()
        => [];

    private static IReadOnlyList<ActPlayerIdentity> BuildPlayerIdentities(
        IPlayerState playerState,
        IPartyList partyList,
        bool localPlayerDead)
    {
        var identities = new Dictionary<string, ActPlayerIdentity>(StringComparer.OrdinalIgnoreCase);
        if (playerState.IsLoaded && !string.IsNullOrWhiteSpace(playerState.CharacterName))
        {
            var identity = new ActPlayerIdentity(
                playerState.CharacterName,
                playerState.HomeWorld.ValueNullable?.Name.ToString() ?? string.Empty,
                playerState.ClassJob.ValueNullable?.Abbreviation.ToString() ?? string.Empty,
                true,
                localPlayerDead)
            {
                EntityId = playerState.EntityId,
                ContentId = playerState.ContentId,
                WorldId = playerState.HomeWorld.RowId,
                JobId = unchecked((byte)playerState.ClassJob.RowId),
                Level = unchecked((byte)playerState.EffectiveLevel),
                CurrentHp = localPlayerDead ? 0u : 1u,
                MaxHp = 1,
            };
            identities[identity.DisplayName] = identity;
        }

        foreach (var member in partyList)
        {
            var name = member.Name.TextValue;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var identity = new ActPlayerIdentity(
                name,
                member.World.ValueNullable?.Name.ToString() ?? string.Empty,
                member.ClassJob.ValueNullable?.Abbreviation.ToString() ?? string.Empty,
                (member.ContentId != 0 && member.ContentId == playerState.ContentId) ||
                (member.EntityId != 0 && member.EntityId == playerState.EntityId),
                member.MaxHP > 0 && member.CurrentHP == 0)
            {
                EntityId = member.EntityId,
                ContentId = member.ContentId,
                WorldId = member.World.RowId,
                JobId = unchecked((byte)member.ClassJob.RowId),
                Level = member.Level,
                CurrentHp = member.CurrentHP,
                MaxHp = member.MaxHP,
                CurrentMp = member.CurrentMP,
                MaxMp = member.MaxMP,
                TerritoryId = unchecked((ushort)member.Territory.RowId),
                PositionX = member.Position.X,
                PositionY = member.Position.Y,
                PositionZ = member.Position.Z,
            };
            identities[identity.DisplayName] = identity;
        }

        return identities.Values.ToArray();
    }
}

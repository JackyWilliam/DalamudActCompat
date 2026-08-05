using Advanced_Combat_Tracker;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using RainbowMage.OverlayPlugin;

namespace DalamudActCompat.ActRuntime;

public static class NativePostNamazuBridge
{
    private static readonly object SyncRoot = new();
    private static readonly Dictionary<int, Func<nint, ulong[], ulong>> NativeCalls = [];
    private static IFramework? framework;
    private static IPluginLog? log;
    private static ISigScanner? sigScanner;
    private static Func<string, uint?>? actorIdResolver;
    private static PostNamazuEventSource? overlayEventSource;
    private static IReadOnlyList<CompatibilityStageResult> stages = [];
    private static nint publicMarkFunction;
    private static PostNamazuWaymarkSnapshot[]? savedWaymarks;

    public static IReadOnlyList<CompatibilityStageResult> Stages
    {
        get
        {
            lock (SyncRoot)
            {
                return stages.ToArray();
            }
        }
    }

    internal static void Configure(
        IFramework frameworkService,
        IPluginLog pluginLog,
        ISigScanner scanner,
        Func<string, uint?> resolveActorId)
    {
        framework = frameworkService;
        log = pluginLog;
        sigScanner = scanner;
        actorIdResolver = resolveActorId;
        publicMarkFunction = nint.Zero;
        savedWaymarks = null;
    }

    internal static string Start(object plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        var results = new List<CompatibilityStageResult>();
        AddStage(results, "PostNamazu 程序集加载", CompatibilityStageState.Success,
            plugin.GetType().Assembly.FullName ?? plugin.GetType().Assembly.GetName().Name ?? "unknown");
        AddStage(results, "InitPlugin 调用", CompatibilityStageState.Success,
            "上游 InitPlugin 已返回；后续桥接阶段开始。");
        AddStage(results, "ACT Host 可用", CompatibilityStageState.Success,
            $"ActPlugins={ActGlobals.oFormActMain.ActPlugins.Count}");
        var ffxivPlugin = ActGlobals.oFormActMain.ActPlugins.FirstOrDefault(candidate =>
            string.Equals(
                candidate.pluginObj?.GetType().Assembly.GetName().Name,
                "FFXIV_ACT_Plugin",
                StringComparison.OrdinalIgnoreCase));
        AddStage(
            results,
            "FFXIV_ACT_Plugin 可发现",
            ffxivPlugin is null ? CompatibilityStageState.Failed : CompatibilityStageState.Success,
            ffxivPlugin is null
                ? "ACT 插件列表中没有 FFXIV_ACT_Plugin。"
                : ffxivPlugin.pluginObj.GetType().FullName ?? "type available");
        var overlayPlugin = ActGlobals.oFormActMain.ActPlugins.FirstOrDefault(candidate =>
            candidate.pluginObj?.GetType().Assembly.GetName().Name?.Contains(
                "OverlayPlugin",
                StringComparison.OrdinalIgnoreCase) == true);
        AddStage(
            results,
            "OverlayPlugin 可发现",
            overlayPlugin is null ? CompatibilityStageState.NotImplemented : CompatibilityStageState.Success,
            overlayPlugin is null
                ? "共享 OverlayPlugin 当前不在 ACT ActPlugins 列表中；将尝试事件源注册。"
                : overlayPlugin.pluginObj.GetType().FullName ?? "type available");
        var pluginType = plugin.GetType();
        var manager = pluginType.GetField(
                "_processManager",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(plugin)
            ?? throw new MissingMemberException(pluginType.FullName, "_processManager");
        var process = manager.GetType().GetMethod(
                "GetFFXIVProcess",
                BindingFlags.Instance | BindingFlags.Public)
            ?.Invoke(manager, null) as Process;
        AddStage(
            results,
            "游戏进程可识别",
            process is null ? CompatibilityStageState.Failed : CompatibilityStageState.Success,
            process is null
                ? "ProcessManager.GetFFXIVProcess 未返回活动进程。"
                : $"{process.ProcessName} ({process.Id})");
        if (process is null)
        {
            PublishStages(results);
            throw new InvalidOperationException("PostNamazu could not find the active FFXIV process.");
        }
        pluginType.GetField(
                "FFXIV",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.SetValue(plugin, process);
        var offsetsReady = pluginType.GetMethod(
                "GetOffsets",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.Invoke(plugin, null) as bool? == true;
        if (!offsetsReady)
        {
            AddStage(results, "命令桥初始化", CompatibilityStageState.Failed,
                "PostNamazu GetOffsets 未能解析当前客户端函数签名。");
            PublishStages(results);
            throw new InvalidOperationException(
                "PostNamazu could not resolve the active FFXIV function signatures.");
        }

        var failedModules = AttachCore(plugin);
        AddStage(
            results,
            "命令桥初始化",
            failedModules.Count == 0
                ? CompatibilityStageState.Success
                : CompatibilityStageState.Failed,
            failedModules.Count == 0
                ? "签名已解析，所有模块 Setup 返回非 Failure。"
                : $"模块不可用：{string.Join(", ", failedModules)}");
        var overlayIntegrationActive = false;
        try
        {
            overlayIntegrationActive = InitializeOverlayEventSource(plugin);
        }
        catch (Exception ex)
        {
            log?.Warning(ex, "PostNamazu optional OverlayPlugin integration could not be initialized.");
        }

        AddStage(
            results,
            "日志系统初始化",
            CompatibilityStageState.Success,
            "PostNamazu 使用共享 ACT BeforeLogLineRead/LogACT 链路。");
        AddStage(
            results,
            "OverlayPlugin 事件入口",
            overlayIntegrationActive
                ? CompatibilityStageState.Success
                : CompatibilityStageState.NotImplemented,
            overlayIntegrationActive
                ? "PostNamazu 事件处理器已注册到共享 OverlayPlugin dispatcher。"
                : "共享 dispatcher 未提供可用入口。");
        AddStage(
            results,
            "命令发送测试",
            CompatibilityStageState.NotTested,
            "未自动发送有副作用的游戏命令；需由用户手动触发对应功能测试。");
        AddStage(
            results,
            "卸载测试",
            CompatibilityStageState.NotTested,
            "将在本次实例 DeInitPlugin/Stop 完成后记录。");
        PublishStages(results);
        var moduleStatus = failedModules.Count == 0
            ? string.Empty
            : $" Unavailable modules: {string.Join(", ", failedModules)}.";
        var overlayStatus = overlayIntegrationActive
            ? " OverlayPlugin handler active."
            : " OverlayPlugin handler unavailable.";
        return $"Loaded; native Dalamud game-write bridge active.{overlayStatus}{moduleStatus}";
    }

    internal static void Stop(object plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        lock (SyncRoot)
        {
            overlayEventSource?.SetAction(null);
            var updated = stages.ToList();
            AddStage(updated, "卸载测试", CompatibilityStageState.Success,
                "事件入口已解除；上游 DeInitPlugin 正在退出。");
            stages = updated
                .GroupBy(stage => stage.Stage, StringComparer.Ordinal)
                .Select(group => group.Last())
                .ToArray();
        }
    }

    private static void AddStage(
        ICollection<CompatibilityStageResult> target,
        string stage,
        CompatibilityStageState state,
        string detail)
        => target.Add(new CompatibilityStageResult(stage, state, detail, DateTimeOffset.UtcNow));

    private static void PublishStages(IReadOnlyList<CompatibilityStageResult> results)
    {
        lock (SyncRoot)
        {
            stages = results.ToArray();
        }

        foreach (var result in results)
        {
            log?.Information(
                $"PostNamazu stage [{result.State}] {result.Stage}: {result.Detail}");
        }
    }

    public static void Attach(object plugin)
        => _ = AttachCore(plugin);

    public static void SkipLegacyProcessMonitoring(object _)
    {
    }

    private static IReadOnlyList<string> AttachCore(object plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        var pluginType = plugin.GetType();
        SetState(plugin, "Ready");

        var modules = pluginType.GetField(
                "Modules",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(plugin) as IEnumerable;
        pluginType.GetMethod(
                "GetRegion",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.Invoke(plugin, null);
        var failedModules = new List<string>();
        if (modules is not null)
        {
            foreach (var module in modules)
            {
                if (module is null)
                {
                    continue;
                }

                try
                {
                    SetState(module, "Waiting");
                    module.GetType()
                        .GetMethod("Setup", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        ?.Invoke(module, null);
                    var state = module.GetType().GetProperty(
                            "State",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        ?.GetValue(module)?.ToString();
                    if (string.Equals(state, "Failure", StringComparison.Ordinal))
                    {
                        failedModules.Add(module.GetType().Name);
                    }
                }
                catch (Exception ex)
                {
                    failedModules.Add(module.GetType().Name);
                    log?.Warning(
                        ex,
                        $"PostNamazu module {module.GetType().Name} failed to initialize.");
                }
            }
        }

        if (failedModules.Count > 0)
        {
            log?.Warning(
                $"PostNamazu loaded with unavailable modules: {string.Join(", ", failedModules)}.");
        }

        pluginType.GetMethod(
                "LogACT",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.Invoke(plugin, ["AttachedNative"]);
        log?.Information("PostNamazu attached through the Dalamud in-process native bridge.");
        return failedModules;
    }

    public static void Execute(Action action)
    {
        CompatibilityPermissionBroker.Demand("postnamazu", ActCapability.NativeGameMemory);
        RunOnFrameworkThread(action);
    }

    public static T Execute<T>(Func<T> function)
    {
        CompatibilityPermissionBroker.Demand("postnamazu", ActCapability.NativeGameMemory);
        T result = default!;
        RunOnFrameworkThread(() => result = function());
        return result;
    }

    public static void Call(nint function, object[] arguments)
        => _ = Call<nint>(function, arguments);

    public static T Call<T>(nint function, object[] arguments)
        where T : struct
    {
        CompatibilityPermissionBroker.Demand("postnamazu", ActCapability.NativeGameMemory);
        if (function == nint.Zero)
        {
            throw new ArgumentException("PostNamazu attempted to call a null game function.", nameof(function));
        }

        var nativeArguments = arguments.Select(ToNativeArgument).ToArray();
        ulong result = 0;
        RunOnFrameworkThread(() => result = GetNativeCall(nativeArguments.Length)(function, nativeArguments));
        return ConvertResult<T>(result);
    }

    public static unsafe T Read<T>(object? _, nint address)
        where T : unmanaged
    {
        CompatibilityPermissionBroker.Demand("postnamazu", ActCapability.NativeGameMemory);
        if (address == nint.Zero)
        {
            throw new ArgumentException("PostNamazu attempted to read a null address.", nameof(address));
        }

        T result = default;
        RunOnFrameworkThread(() => result = *(T*)address);
        return result;
    }

    public static unsafe void Write<T>(object? _, nint address, T value)
        where T : unmanaged
    {
        CompatibilityPermissionBroker.Demand("postnamazu", ActCapability.NativeGameMemory);
        if (address == nint.Zero)
        {
            throw new ArgumentException("PostNamazu attempted to write a null address.", nameof(address));
        }

        RunOnFrameworkThread(() => *(T*)address = value);
    }

    public static void WriteBytes(object? _, nint address, byte[] value)
    {
        CompatibilityPermissionBroker.Demand("postnamazu", ActCapability.NativeGameMemory);
        ArgumentNullException.ThrowIfNull(value);
        if (address == nint.Zero)
        {
            throw new ArgumentException("PostNamazu attempted to write a null address.", nameof(address));
        }

        RunOnFrameworkThread(() => Marshal.Copy(value, 0, address, value.Length));
    }

    public static unsafe void SendCommand(string command)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        SendCommandAsync(command, timeout.Token).GetAwaiter().GetResult();
    }

    public static unsafe Task SendCommandAsync(
        string command,
        CancellationToken cancellationToken)
    {
        CompatibilityPermissionBroker.Demand("postnamazu", ActCapability.GameCommand);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        if (!command.StartsWith('/'))
        {
            throw new ArgumentException("PostNamazu only permits game commands beginning with '/'.", nameof(command));
        }

        if (command.StartsWith("//", StringComparison.Ordinal))
        {
            command = command[1..];
        }

        return RunOnFrameworkThreadAsync(() =>
        {
            var uiModule = UIModule.Instance();
            if (uiModule is null)
            {
                throw new InvalidOperationException("FFXIV UIModule is unavailable.");
            }

            using var message = new Utf8String(command);
            uiModule->ProcessChatBoxEntry(&message);
        }, cancellationToken);
    }

    public static Task SendMarkAsync(
        string payload,
        CancellationToken cancellationToken)
    {
        CompatibilityPermissionBroker.Demand("postnamazu", ActCapability.GameCommand);
        var request = PostNamazuSemanticActions.ParseMark(payload);
        if (!request.LocalOnly)
        {
            CompatibilityPermissionBroker.Demand(
                "postnamazu",
                ActCapability.NativeGameMemory);
        }

        return RunOnFrameworkThreadAsync(() => ApplyMark(request), cancellationToken);
    }

    public static Task SendWaymarksAsync(
        string payload,
        CancellationToken cancellationToken)
    {
        CompatibilityPermissionBroker.Demand("postnamazu", ActCapability.GameCommand);
        var request = PostNamazuSemanticActions.ParseWaymarks(payload);
        if (RequiresNativeGameMemory(request))
        {
            CompatibilityPermissionBroker.Demand(
                "postnamazu",
                ActCapability.NativeGameMemory);
        }

        return RunOnFrameworkThreadAsync(() => ApplyWaymarks(request), cancellationToken);
    }

    public static Task SendPresetAsync(
        string payload,
        CancellationToken cancellationToken)
    {
        CompatibilityPermissionBroker.Demand("postnamazu", ActCapability.GameCommand);
        CompatibilityPermissionBroker.Demand("postnamazu", ActCapability.NativeGameMemory);
        var request = PostNamazuSemanticActions.ParsePreset(payload);
        return RunOnFrameworkThreadAsync(() => ApplyPreset(request), cancellationToken);
    }

    public static Task SendKeyAsync(
        string payload,
        CancellationToken cancellationToken)
    {
        CompatibilityPermissionBroker.Demand("postnamazu", ActCapability.GameCommand);
        var keyCode = PostNamazuSemanticActions.ParseKeyCode(payload);
        return RunOnFrameworkThreadAsync(() => SendKeyCode(keyCode), cancellationToken);
    }

    private static unsafe void ApplyMark(PostNamazuMarkAction request)
    {
        var actorId = ResolveActorId(request);
        var controller = MarkingController.Instance();
        if (controller is null)
        {
            throw new InvalidOperationException("FFXIV MarkingController is unavailable.");
        }

        controller->Markers[request.MarkerIndex] = (GameObjectId)(ulong)actorId;
        if (request.LocalOnly)
        {
            return;
        }

        var function = ResolvePublicMarkFunction();
        _ = GetNativeCall(3)(
            function,
            [unchecked((ulong)(nint)controller), unchecked((ulong)request.MarkerIndex), actorId]);
    }

    internal static bool RequiresNativeGameMemory(PostNamazuWaymarkAction request)
        => request.Operation switch
        {
            PostNamazuWaymarkOperation.Apply => !request.LocalOnly,
            PostNamazuWaymarkOperation.Save or
                PostNamazuWaymarkOperation.Load or
                PostNamazuWaymarkOperation.Publicize => true,
            PostNamazuWaymarkOperation.ClearLocal or
                PostNamazuWaymarkOperation.Reset => false,
            _ => throw new InvalidDataException(
                $"Unknown PostNamazu waymark operation {request.Operation}."),
        };

    private static unsafe void ApplyWaymarks(PostNamazuWaymarkAction request)
    {
        var controller = MarkingController.Instance();
        if (controller is null)
        {
            throw new InvalidOperationException("FFXIV MarkingController is unavailable.");
        }

        switch (request.Operation)
        {
            case PostNamazuWaymarkOperation.ClearLocal:
                for (var index = 0; index < 8; index++)
                {
                    controller->FieldMarkers[index] = default;
                }
                return;
            case PostNamazuWaymarkOperation.Save:
                savedWaymarks = ReadCurrentWaymarks(controller);
                return;
            case PostNamazuWaymarkOperation.Load:
                if (savedWaymarks is not null)
                {
                    WriteLocalWaymarks(controller, savedWaymarks);
                }
                return;
            case PostNamazuWaymarkOperation.Reset:
                savedWaymarks = null;
                return;
            case PostNamazuWaymarkOperation.Publicize:
                foreach (var marker in ReadCurrentWaymarks(controller))
                {
                    ApplyPublicWaymark(marker.Index, marker.Active, marker.Position);
                }
                return;
            case PostNamazuWaymarkOperation.Apply:
                break;
            default:
                throw new InvalidDataException(
                    $"Unknown PostNamazu waymark operation {request.Operation}.");
        }

        foreach (var update in request.Updates)
        {
            if (request.LocalOnly)
            {
                ref var marker = ref controller->FieldMarkers[update.Index];
                marker.Position = update.Position;
                marker.X = checked((int)(update.Position.X * 1000));
                marker.Y = checked((int)(update.Position.Y * 1000));
                marker.Z = checked((int)(update.Position.Z * 1000));
                marker.Active = update.Active;
                continue;
            }

            ApplyPublicWaymark(update.Index, update.Active, update.Position);
        }
    }

    private static unsafe PostNamazuWaymarkSnapshot[] ReadCurrentWaymarks(
        MarkingController* controller)
    {
        var result = new PostNamazuWaymarkSnapshot[8];
        for (var index = 0; index < result.Length; index++)
        {
            ref var marker = ref controller->FieldMarkers[index];
            result[index] = new PostNamazuWaymarkSnapshot(
                index,
                marker.Active,
                marker.Position);
        }

        return result;
    }

    private static unsafe void WriteLocalWaymarks(
        MarkingController* controller,
        IReadOnlyList<PostNamazuWaymarkSnapshot> markers)
    {
        foreach (var update in markers)
        {
            ref var marker = ref controller->FieldMarkers[update.Index];
            marker.Position = update.Position;
            marker.X = checked((int)(update.Position.X * 1000));
            marker.Y = checked((int)(update.Position.Y * 1000));
            marker.Z = checked((int)(update.Position.Z * 1000));
            marker.Active = update.Active;
        }
    }

    private static unsafe void ApplyPublicWaymark(int index, bool active, System.Numerics.Vector3 position)
    {
        _ = active
            ? GameMain.ExecuteCommand(
                317,
                index,
                unchecked((int)(position.X * 1000)),
                unchecked((int)(position.Y * 1000)),
                unchecked((int)(position.Z * 1000)))
            : GameMain.ExecuteCommand(318, index);
    }

    private static unsafe void ApplyPreset(PostNamazuPresetAction request)
    {
        var module = FieldMarkerModule.Instance();
        if (module is null)
        {
            throw new InvalidOperationException("FFXIV FieldMarkerModule is unavailable.");
        }

        var mapId = request.MapId;
        if (mapId is 0 or > 2000)
        {
            var gameMain = GameMain.Instance();
            mapId = gameMain is null ? (ushort)0 : gameMain->CurrentContentFinderConditionId;
        }

        if (mapId == 0)
        {
            throw new InvalidOperationException(
                "PostNamazu preset and current ContentFinderCondition ID are both invalid.");
        }

        ref var preset = ref module->Presets[request.Slot - 1];
        preset.ActiveMarkers = 0;
        foreach (var marker in request.Markers)
        {
            preset.Markers[marker.Index] = new GamePresetPoint
            {
                X = marker.Active ? unchecked((int)(marker.Position.X * 1000)) : 0,
                Y = marker.Active ? unchecked((int)(marker.Position.Y * 1000)) : 0,
                Z = marker.Active ? unchecked((int)(marker.Position.Z * 1000)) : 0,
            };
            preset.SetMarkerActive(marker.Index, marker.Active);
        }

        preset.ContentFinderConditionId = mapId;
        preset.Timestamp = unchecked((int)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    private static void SendKeyCode(int keyCode)
    {
        using var process = Process.GetCurrentProcess();
        var window = process.MainWindowHandle;
        if (window == nint.Zero)
        {
            throw new InvalidOperationException("FFXIV main window is unavailable.");
        }

        _ = SendMessage(window, 0x0100, keyCode, 0);
        _ = SendMessage(window, 0x0101, keyCode, 0);
    }

    private static uint ResolveActorId(PostNamazuMarkAction request)
    {
        if (request.ActorId is { } actorId)
        {
            return actorId;
        }

        var name = request.ActorName
                   ?? throw new InvalidDataException("PostNamazu actor name is missing.");
        return actorIdResolver?.Invoke(name)
               ?? throw new InvalidOperationException($"Could not find FFXIV actor '{name}'.");
    }

    private static nint ResolvePublicMarkFunction()
    {
        lock (SyncRoot)
        {
            if (publicMarkFunction != nint.Zero)
            {
                return publicMarkFunction;
            }

            var scanner = sigScanner
                          ?? throw new InvalidOperationException(
                              "The Dalamud signature scanner is not configured.");
            try
            {
                var call = scanner.ScanText(
                    "E8 ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 8B CB 48 89 86");
                publicMarkFunction = ResolveRelativeCall(call);
            }
            catch
            {
                publicMarkFunction = scanner.ScanText(
                    "48 89 5C 24 ?? 57 48 83 EC ?? 48 8B 0D ?? ?? ?? ?? 49 8B D8 8B FA E8 ?? ?? ?? ?? 48 85 C0");
            }

            log?.Information(
                $"PostNamazu public marking function resolved at 0x{publicMarkFunction:X}.");
            return publicMarkFunction;
        }
    }

    private static nint ResolveRelativeCall(nint instruction)
    {
        var displacement = Marshal.ReadInt32(instruction + 1);
        return instruction + 5 + displacement;
    }

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);

    private static async Task RunOnFrameworkThreadAsync(
        Action action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        var currentFramework = framework
            ?? throw new InvalidOperationException("The Dalamud framework bridge is not configured.");
        cancellationToken.ThrowIfCancellationRequested();
        if (currentFramework.IsInFrameworkUpdateThread)
        {
            action();
            return;
        }

        await currentFramework.RunOnFrameworkThread(() =>
            {
                // Prevent a command that timed out in the Broker from running
                // later when the framework queue finally resumes.
                cancellationToken.ThrowIfCancellationRequested();
                action();
            })
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static void RunOnFrameworkThread(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var currentFramework = framework
            ?? throw new InvalidOperationException("The Dalamud framework bridge is not configured.");
        if (currentFramework.IsInFrameworkUpdateThread)
        {
            action();
            return;
        }

        try
        {
            currentFramework.RunOnFrameworkThread(action)
                .WaitAsync(TimeSpan.FromSeconds(2))
                .GetAwaiter()
                .GetResult();
        }
        catch (TimeoutException ex)
        {
            log?.Error(
                ex,
                "PostNamazu framework bridge exceeded two seconds. The caller was released; " +
                "native work cannot be forcibly aborted safely in-process.");
            throw;
        }
    }

    private static void SetState(object target, string stateName)
    {
        var property = target.GetType().GetProperty(
            "State",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMemberException(target.GetType().FullName, "State");
        property.SetValue(target, Enum.Parse(property.PropertyType, stateName));
    }

    private static bool InitializeOverlayEventSource(object plugin)
    {
        if (ActGlobals.oFormActMain.OverlayPluginContainer is not TinyIoCContainer container)
        {
            return false;
        }

        var doAction = plugin.GetType().GetMethod(
                "DoAction",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(plugin.GetType().FullName, "DoAction");
        var action = (Action<string, string>)doAction.CreateDelegate(
            typeof(Action<string, string>),
            plugin);
        lock (SyncRoot)
        {
            var registry = container.Resolve<Registry>();
            var existingEventSource = registry.EventSources.FirstOrDefault(source =>
                string.Equals(source.Name, PostNamazuEventSource.EventSourceName, StringComparison.Ordinal));
            if (existingEventSource is not null &&
                existingEventSource is not PostNamazuEventSource)
            {
                log?.Information("PostNamazu upstream OverlayPlugin event source is already active.");
                return true;
            }

            overlayEventSource = existingEventSource as PostNamazuEventSource;
            if (overlayEventSource is null)
            {
                overlayEventSource = new PostNamazuEventSource(container);
                registry.StartEventSource(overlayEventSource);
            }

            overlayEventSource.SetAction(action);
        }

        log?.Information("PostNamazu OverlayPlugin handler registered in the shared event dispatcher.");
        return true;
    }

    private static ulong ToNativeArgument(object argument)
        => argument switch
        {
            nint value => unchecked((ulong)value),
            nuint value => value,
            byte value => value,
            sbyte value => unchecked((ulong)value),
            ushort value => value,
            short value => unchecked((ulong)value),
            uint value => value,
            int value => unchecked((ulong)value),
            ulong value => value,
            long value => unchecked((ulong)value),
            bool value => value ? 1UL : 0UL,
            _ => throw new NotSupportedException(
                $"PostNamazu native call argument type {argument.GetType().FullName} is unsupported."),
        };

    private static T ConvertResult<T>(ulong result)
        where T : struct
    {
        object value = typeof(T) == typeof(nint)
            ? (nint)unchecked((long)result)
            : typeof(T) == typeof(nuint)
                ? (nuint)result
                : Convert.ChangeType(result, typeof(T));
        return (T)value;
    }

    private static Func<nint, ulong[], ulong> GetNativeCall(int argumentCount)
    {
        lock (SyncRoot)
        {
            if (NativeCalls.TryGetValue(argumentCount, out var nativeCall))
            {
                return nativeCall;
            }

            var method = new DynamicMethod(
                $"PostNamazuNativeCall{argumentCount}",
                typeof(ulong),
                [typeof(nint), typeof(ulong[])],
                typeof(NativePostNamazuBridge).Module,
                skipVisibility: true);
            var il = method.GetILGenerator();
            for (var index = 0; index < argumentCount; index++)
            {
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldc_I4, index);
                il.Emit(OpCodes.Ldelem_I8);
            }

            il.Emit(OpCodes.Ldarg_0);
            il.EmitCalli(
                OpCodes.Calli,
                CallingConvention.Winapi,
                typeof(ulong),
                Enumerable.Repeat(typeof(ulong), argumentCount).ToArray());
            il.Emit(OpCodes.Ret);
            nativeCall = method.CreateDelegate<Func<nint, ulong[], ulong>>();
            NativeCalls.Add(argumentCount, nativeCall);
            return nativeCall;
        }
    }

    private sealed record PostNamazuWaymarkSnapshot(
        int Index,
        bool Active,
        System.Numerics.Vector3 Position);
}

using Advanced_Combat_Tracker;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
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
    private static PostNamazuEventSource? overlayEventSource;

    internal static void Configure(IFramework frameworkService, IPluginLog pluginLog)
    {
        framework = frameworkService;
        log = pluginLog;
    }

    internal static string Start(object plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        var pluginType = plugin.GetType();
        var manager = pluginType.GetField(
                "_processManager",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(plugin)
            ?? throw new MissingMemberException(pluginType.FullName, "_processManager");
        var process = manager.GetType().GetMethod(
                "GetFFXIVProcess",
                BindingFlags.Instance | BindingFlags.Public)
            ?.Invoke(manager, null) as Process
            ?? throw new InvalidOperationException("PostNamazu could not find the active FFXIV process.");
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
            throw new InvalidOperationException(
                "PostNamazu could not resolve the active FFXIV function signatures.");
        }

        var failedModules = AttachCore(plugin);
        var overlayIntegrationActive = false;
        try
        {
            overlayIntegrationActive = InitializeOverlayEventSource(plugin);
        }
        catch (Exception ex)
        {
            log?.Warning(ex, "PostNamazu optional OverlayPlugin integration could not be initialized.");
        }

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
        => RunOnFrameworkThread(action);

    public static T Execute<T>(Func<T> function)
    {
        T result = default!;
        RunOnFrameworkThread(() => result = function());
        return result;
    }

    public static void Call(nint function, object[] arguments)
        => _ = Call<nint>(function, arguments);

    public static T Call<T>(nint function, object[] arguments)
        where T : struct
    {
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
        if (address == nint.Zero)
        {
            throw new ArgumentException("PostNamazu attempted to write a null address.", nameof(address));
        }

        RunOnFrameworkThread(() => *(T*)address = value);
    }

    public static void WriteBytes(object? _, nint address, byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (address == nint.Zero)
        {
            throw new ArgumentException("PostNamazu attempted to write a null address.", nameof(address));
        }

        RunOnFrameworkThread(() => Marshal.Copy(value, 0, address, value.Length));
    }

    public static unsafe void SendCommand(string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        if (!command.StartsWith('/'))
        {
            throw new ArgumentException("PostNamazu only permits game commands beginning with '/'.", nameof(command));
        }

        if (command.StartsWith("//", StringComparison.Ordinal))
        {
            command = command[1..];
        }

        RunOnFrameworkThread(() =>
        {
            var uiModule = UIModule.Instance();
            if (uiModule is null)
            {
                throw new InvalidOperationException("FFXIV UIModule is unavailable.");
            }

            using var message = new Utf8String(command);
            uiModule->ProcessChatBoxEntry(&message);
        });
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

        currentFramework.RunOnFrameworkThread(action).GetAwaiter().GetResult();
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
}

using Advanced_Combat_Tracker;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace DalamudActCompat.Host;

public static class SimulantCompatibility
{
    public static object GetFfxivPlugin()
        => ActGlobals.oFormActMain.ActPlugins
            .Select(plugin => plugin.pluginObj)
            .First(plugin => plugin?.GetType().FullName == "FFXIV_ACT_Plugin.FFXIV_ACT_Plugin");

    // The zone handler of the loaded Simulant (v0.1.0.0 and later), if any. One slot: a
    // reloaded plugin replaces its predecessor, whose handler ignores calls once disposed.
    private static Action<uint, string>? zoneChanged;
    private static WeakReference<object>? observedStandaloneHost;
    private static readonly object StandaloneHostGate = new();

    /// <summary>
    /// Stands in for the FFXIV plugin's ZoneChanged subscription, which upstream makes only
    /// after ACT's status label reads "STARTED". Simulant uses it to drop the addresses of the
    /// entities it spawned and the party it simulated when the game leaves the zone.
    /// </summary>
    public static void ObserveZoneChanges(object pluginHost)
    {
        var type = pluginHost.GetType();
        var handler = type.GetMethod("OnFFXIVZoneChanged", BindingFlags.Instance | BindingFlags.NonPublic)
                      ?? throw new MissingMethodException(type.FullName, "OnFFXIVZoneChanged");
        // Upstream records the zone it subscribed in, so the first report of that same zone
        // does not count as a change.
        type.GetField("_entityZoneId", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.SetValue(pluginHost, HostPluginBridge.FfxivRepository.GetCurrentTerritoryID());
        Volatile.Write(ref zoneChanged, handler.CreateDelegate<Action<uint, string>>(pluginHost));
    }

    /// <summary>
    /// Idempotent replacement for Simulant v0.4.0.0+ RefreshXivPlugin, which runs in a 1-second
    /// loop and expects PluginStarted and DataSubscription on FFXIV_ACT_Plugin.
    /// </summary>
    public static object? BindStandaloneFfxivPlugin(object pluginHost)
    {
        var disposed = (bool?)pluginHost.GetType()
            .GetProperty("Disposed", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?
            .GetValue(pluginHost) == true;
        if (disposed)
        {
            return null;
        }

        lock (StandaloneHostGate)
        {
            if (observedStandaloneHost is null ||
                !observedStandaloneHost.TryGetTarget(out var current) ||
                !ReferenceEquals(current, pluginHost))
            {
                ObserveZoneChanges(pluginHost);
                observedStandaloneHost = new WeakReference<object>(pluginHost);
            }
        }

        return GetFfxivPlugin();
    }

    /// <summary>Called by the Host for every zone change the game reports.</summary>
    internal static void NotifyZoneChanged(uint territoryId, string zoneName)
    {
        try
        {
            Volatile.Read(ref zoneChanged)?.Invoke(territoryId, zoneName);
        }
        catch (Exception error)
        {
            // A simulator fault must not break zone handling for the other extensions.
            Console.Error.WriteLine($"Simulant zone change handler failed: {error}");
        }
    }

    /// <summary>
    /// Where Simulant keeps "Config\Simulant.config.xml". Upstream uses the application folder,
    /// which in a Host belongs to one DACT version; ACT's data folder survives updates.
    /// </summary>
    public static string GetConfigurationRoot()
        => ActGlobals.oFormActMain.AppDataFolder.FullName;

    public static void EnsureNativeAccess()
    {
        // Simulant borrows PostNamazu's native runtime, so both extensions must have their
        // own grants; enabling PostNamazu alone must not authorize a new simulator.
        if (!HostPluginBridge.IsSimulantNativeRuntimeAllowed())
        {
            throw new InvalidOperationException(
                "请在 DACT 扩展权限中授权 Simulant 的原生内存访问，以及 PostNamazu 的游戏命令和原生内存访问，然后重启 Host。");
        }

        if (!ActGlobals.oFormActMain.ActPlugins.Any(plugin =>
                plugin.pluginObj?.GetType().FullName == "PostNamazu.PostNamazu") ||
            !AppDomain.CurrentDomain.GetAssemblies().Any(assembly =>
                assembly.GetName().Name == "TriggernometryPlugin"))
        {
            throw new InvalidOperationException(
                "请先在 DACT 中安装并启用 PostNamazu 和 MnFeN Triggernometry，再初始化 Simulant。");
        }
    }

    public static void EnsureStandaloneNativeAccess()
    {
        if (!HostPluginBridge.IsSimulantStandaloneNativeRuntimeAllowed())
        {
            throw new InvalidOperationException(
                "请在 DACT 扩展权限中授权 Simulant 的原生内存访问，然后重启 Host。");
        }
    }

    public static Process? GetStandaloneGameProcess()
        => HostPluginBridge.IsSimulantStandaloneNativeRuntimeAllowed()
            ? HostPluginBridge.FfxivRepository.GetCachedGameProcessForAuthorizedBridge()
            : null;

    public static void WarnIfStandaloneNativeAccessDenied(object pluginHost)
    {
        if (HostPluginBridge.IsSimulantStandaloneNativeRuntimeAllowed())
        {
            return;
        }

        pluginHost.GetType()
            .GetMethod("LogWarning", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?
            .Invoke(pluginHost, ["请在 DACT 扩展权限中授权 Simulant 的原生内存访问，然后重启 Host。"]);
    }

    private static readonly Lazy<Type?> LogMessageTypeEnum = new(() =>
        Type.GetType("FFXIV_ACT_Plugin.Logfile.LogMessageType, FFXIV_ACT_Plugin.Logfile", throwOnError: false));

    public static void WriteActNetworkLog(int id, string payload, DateTimeOffset? timestamp)
    {
        if (payload is null)
        {
            throw new ArgumentNullException(nameof(payload));
        }

        if (payload.IndexOf('\r') >= 0 || payload.IndexOf('\n') >= 0)
        {
            throw new ArgumentException("网络日志内容必须在同一行。", nameof(payload));
        }

        var actMain = ActGlobals.oFormActMain
                      ?? throw new InvalidOperationException("ACT 主窗口尚未就绪。");
        var time = timestamp?.LocalDateTime ?? DateTime.Now;
        var typeName = LogMessageTypeEnum.Value is { } enumType
            ? Enum.ToObject(enumType, id).ToString()
            : id.ToString(CultureInfo.InvariantCulture);
        var sanitized = payload.Replace('\0', ' ').Replace('|', ':');
        // Match FFXIV_ACT_Plugin's ParseMediator.BeforeLogLineRead output so Host-side
        // Triggernometry triggers match simulated events without writing to Network_*.log.
        var line = string.Format(
            CultureInfo.InvariantCulture,
            "[{0:HH:mm:ss.fff}] {1} {2:X2}:{3}",
            time,
            typeName,
            id,
            sanitized);
        actMain.ParseRawLogLine(false, time, line);
    }

    public static Dictionary<string, string> ValidateSignatures(Dictionary<string, string> result)
    {
        SimulantEntryPointCompatibility.Recover(result);
        var failures = result.Where(pair => !string.IsNullOrEmpty(pair.Value)).ToArray();
        if (result.Count == 0 || failures.Length > 0)
        {
            throw new InvalidOperationException(
                "Simulant 地址扫描未全部通过，模拟保持关闭。请核对上游插件支持的游戏版本。\n" +
                string.Join("\n", failures.Select(pair => $"{pair.Key}: {pair.Value}")));
        }

        return result;
    }

    public static byte[] PrepareSendHookInstructions(byte[] savedEntry, IntPtr address)
    {
        EnsureNativeAccess();
        return PrepareSendHookInstructionsCore(savedEntry, address, SimulantEntryPointCompatibility.ReadMemory);
    }

    public static byte[] PrepareStandaloneSendHookInstructions(byte[] savedEntry, IntPtr address)
    {
        EnsureStandaloneNativeAccess();
        return PrepareSendHookInstructionsCore(savedEntry, address, SimulantEntryPointCompatibility.ReadStandaloneMemory);
    }

    private static byte[] PrepareSendHookInstructionsCore(
        byte[] savedEntry,
        IntPtr address,
        Func<IntPtr, int, byte[]> read)
    {
        if (savedEntry.Length != 15) throw new InvalidOperationException("发包入口备份长度无效。");
        // These three stack-relative moves are the upstream send hook's only supported
        // unmodified prologue. Preserve the saved bytes separately for exact restoration.
        if (savedEntry.AsSpan().SequenceEqual(new byte[]
            { 0x48, 0x89, 0x5C, 0x24, 0x08, 0x48, 0x89, 0x74, 0x24, 0x10, 0x4C, 0x89, 0x64, 0x24, 0x18 }))
            return savedEntry;
        return SimulantEntryPointCompatibility.RelocateJump(savedEntry, address, read);
    }

    public static void ValidateSendHookEntry()
    {
        EnsureNativeAccess();
        ValidateSendHookEntryCore(SimulantEntryPointCompatibility.ReadMemory);
    }

    public static void ValidateStandaloneSendHookEntry()
    {
        EnsureStandaloneNativeAccess();
        ValidateSendHookEntryCore(SimulantEntryPointCompatibility.ReadStandaloneMemory);
    }

    private static void ValidateSendHookEntryCore(Func<IntPtr, int, byte[]> read)
    {
        var assembly = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "Simulant");
        var address = (IntPtr)assembly.GetType("Simulant.Game.AddressStore")!
            .GetProperty("OnSendPacketFuncPtr")!.GetValue(null)!;
        if (address == IntPtr.Zero) throw new InvalidOperationException("发包函数尚未就绪。");
        // Check before upstream disables receiving packets, so an unsupported send entry
        // cannot leave only half of the firewall enabled.
        _ = PrepareSendHookInstructionsCore(read(address, 15), address, read);
    }

    public static IntPtr AllocateSendHookMemory(object memory, int size)
    {
        EnsureNativeAccess();
        return AllocateSendHookMemoryCore(
            HostPluginBridge.FfxivRepository.GetCurrentFFXIVProcess(),
            size);
    }

    public static IntPtr AllocateStandaloneSendHookMemory(object memory, int size)
    {
        EnsureStandaloneNativeAccess();
        return AllocateSendHookMemoryCore(
            HostPluginBridge.FfxivRepository.GetCachedGameProcessForAuthorizedBridge(),
            size);
    }

    private static IntPtr AllocateSendHookMemoryCore(Process? process, int size)
    {
        var assembly = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "Simulant");
        var address = (IntPtr)assembly.GetType("Simulant.Game.AddressStore")!
            .GetProperty("OnSendPacketFuncPtr")!.GetValue(null)!;
        var activeProcess = process ?? throw new InvalidOperationException("游戏进程尚未就绪。");
        return SimulantNearMemory.Allocate(activeProcess.Handle, address, size);
    }

    public static byte[] PrepareSendEntryPatch(byte[] upstreamPatch, IntPtr entry)
    {
        EnsureNativeAccess();
        return PrepareSendEntryPatchCore(upstreamPatch, entry);
    }

    public static byte[] PrepareStandaloneSendEntryPatch(byte[] upstreamPatch, IntPtr entry)
    {
        EnsureStandaloneNativeAccess();
        return PrepareSendEntryPatchCore(upstreamPatch, entry);
    }

    private static byte[] PrepareSendEntryPatchCore(byte[] upstreamPatch, IntPtr entry)
    {
        if (upstreamPatch.Length != 15 || !upstreamPatch.AsSpan(0, 6).SequenceEqual(new byte[] { 0xFF, 0x25, 0, 0, 0, 0 }))
            throw new InvalidDataException("发包入口补丁格式与适配版本不一致。");
        return SimulantNearMemory.CreateEntryPatch(entry,
            new IntPtr(BinaryPrimitives.ReadInt64LittleEndian(upstreamPatch.AsSpan(6))));
    }

    public static void EnableFirewall(object firewall)
    {
        ValidateSendHookEntry();
        var type = firewall.GetType();
        void Invoke(string name) => type.GetMethod(name)!.Invoke(firewall, null);
        try
        {
            // Allocate and install the send path first. Failure must not leave receiving
            // disabled, and a later receive failure rolls back either partially set hook.
            Invoke("SendHookEnable");
            Invoke("ReceiveHookEnable");
        }
        catch (Exception failure)
        {
            var rollbackErrors = new List<Exception>();
            if (type.GetField("_onReceiveOriginal", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(firewall) is byte[])
                try { Invoke("ReceiveHookDisable"); } catch (Exception error) { rollbackErrors.Add(error.GetBaseException()); }
            try { Invoke("SendHookDisable"); } catch (Exception error) { rollbackErrors.Add(error.GetBaseException()); }
            if (rollbackErrors.Count == 0)
            {
                // Allocation can fail after the upstream backup is captured. Clear that
                // incomplete attempt as well so the user can retry initialization.
                var caveField = type.GetField("_sendHookCave", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var cave = (IntPtr)caveField.GetValue(firewall)!;
                if (cave != IntPtr.Zero)
                {
                    dynamic plugin = ActGlobals.oFormActMain.ActPlugins.Select(p => p.pluginObj)
                        .First(p => p?.GetType().FullName == "PostNamazu.PostNamazu")!;
                    if (!(bool)plugin.Memory.FreeMemory(cave)) throw new AggregateException("防火墙开启失败，代码洞未能释放。", failure.GetBaseException());
                    caveField.SetValue(firewall, IntPtr.Zero);
                }
                type.GetField("_sendHookOriginal", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(firewall, null);
            }
            if (rollbackErrors.Count > 0) throw new AggregateException("防火墙开启失败，恢复时发生错误。", new[] { failure.GetBaseException() }.Concat(rollbackErrors));
            ExceptionDispatchInfo.Capture(failure.GetBaseException()).Throw();
        }
    }
}

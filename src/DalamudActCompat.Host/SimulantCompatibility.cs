using Advanced_Combat_Tracker;
using System.Buffers.Binary;
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
        if (savedEntry.Length != 15) throw new InvalidOperationException("发包入口备份长度无效。");
        // These three stack-relative moves are the upstream send hook's only supported
        // unmodified prologue. Preserve the saved bytes separately for exact restoration.
        if (savedEntry.AsSpan().SequenceEqual(new byte[]
            { 0x48, 0x89, 0x5C, 0x24, 0x08, 0x48, 0x89, 0x74, 0x24, 0x10, 0x4C, 0x89, 0x64, 0x24, 0x18 }))
            return savedEntry;
        return SimulantEntryPointCompatibility.RelocateJump(savedEntry, address, SimulantEntryPointCompatibility.ReadMemory);
    }

    public static void ValidateSendHookEntry()
    {
        EnsureNativeAccess();
        var assembly = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "Simulant");
        var address = (IntPtr)assembly.GetType("Simulant.Game.AddressStore")!
            .GetProperty("OnSendPacketFuncPtr")!.GetValue(null)!;
        if (address == IntPtr.Zero) throw new InvalidOperationException("发包函数尚未就绪。");
        // Check before upstream disables receiving packets, so an unsupported send entry
        // cannot leave only half of the firewall enabled.
        _ = PrepareSendHookInstructions(SimulantEntryPointCompatibility.ReadMemory(address, 15), address);
    }

    public static IntPtr AllocateSendHookMemory(object memory, int size)
    {
        EnsureNativeAccess();
        var assembly = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "Simulant");
        var address = (IntPtr)assembly.GetType("Simulant.Game.AddressStore")!
            .GetProperty("OnSendPacketFuncPtr")!.GetValue(null)!;
        var process = HostPluginBridge.FfxivRepository.GetCurrentFFXIVProcess()
                      ?? throw new InvalidOperationException("游戏进程尚未就绪。");
        return SimulantNearMemory.Allocate(process.Handle, address, size);
    }

    public static byte[] PrepareSendEntryPatch(byte[] upstreamPatch, IntPtr entry)
    {
        EnsureNativeAccess();
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

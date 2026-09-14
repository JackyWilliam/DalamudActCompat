using Advanced_Combat_Tracker;

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
        var failures = result.Where(pair => !string.IsNullOrEmpty(pair.Value)).ToArray();
        if (result.Count == 0 || failures.Length > 0)
        {
            throw new InvalidOperationException(
                "Simulant 地址扫描未全部通过，模拟保持关闭。请核对上游插件支持的游戏版本。\n" +
                string.Join("\n", failures.Select(pair => $"{pair.Key}: {pair.Value}")));
        }

        return result;
    }
}

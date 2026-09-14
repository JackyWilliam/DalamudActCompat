using System.Reflection;

namespace DalamudActCompat.Host;

public static class SimulantSimulationCompatibility
{
    private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public static bool CanStart(object host, object? preset)
    {
        string? error;
        try
        {
            var hostType = host.GetType();
            var ui = hostType.GetField("_ui", InstanceMembers)!.GetValue(host)!;
            var zone = hostType.GetField("ZoneService", InstanceMembers)!.GetValue(host)!;
            var ready = (bool)hostType.GetProperty("PluginReady", InstanceMembers)!.GetValue(host)!;
            var firewall = (bool)hostType.GetProperty("FirewallEnabled", InstanceMembers)!.GetValue(host)!;
            var switching = (bool)ui.GetType().GetField("_switchingTerritory", InstanceMembers)!.GetValue(ui)!;
            var loaded = (bool)zone.GetType().GetProperty("IsInSimulatedTerritory")!.GetValue(zone)!;
            var selected = preset is null ? 0 : (int)preset.GetType().GetProperty("TerritoryId")!.GetValue(preset)!;
            uint current = 0;
            if (ready && firewall && !switching && loaded && selected > 0)
            {
                SimulantCompatibility.EnsureNativeAccess();
                // Read the upstream field abstraction instead of duplicating a game offset.
                // The entry call can finish before the asynchronous zone change does.
                var gameMainType = hostType.Assembly.GetType("Simulant.Game.FFCS.Client.Game.GameMain")!;
                var gameMain = gameMainType.GetProperty("Instance")!.GetValue(null)!;
                var territory = gameMainType.GetProperty("CurrentTerritoryTypeId")!.GetValue(gameMain)!;
                current = (uint)territory.GetType().GetMethod("Get")!.Invoke(territory, null)!;
            }
            error = GetStartError(ready, firewall, switching, loaded, selected, current);
        }
        catch (Exception exception) { error = "无法确认模拟就绪状态：" + exception.GetBaseException().Message; }
        if (error is null) return true;
        host.GetType().GetMethod("LogWarning", InstanceMembers)!.Invoke(host, [error]);
        Console.WriteLine("Simulant start blocked: " + error);
        return false;
    }

    internal static string? GetStartError(bool ready, bool firewall, bool switching, bool loaded,
        int selected, uint current)
    {
        if (!ready) return "请先初始化插件，再开始模拟。";
        if (!firewall) return "请先启用防火墙，再加载区域并开始模拟。";
        if (selected <= 0) return "请先选择一个 [模拟] 预设。";
        if (switching) return "区域正在加载，请等待地图切换和阶段设置完成后再开始模拟。";
        if (!loaded) return "尚未进入模拟区域。请先点击左侧“加载区域”，进入副本后再点击“开始模拟”。";
        if (current != selected) return $"当前区域 {current} 与预设区域 {selected} 不一致。请加载对应区域并等待地图切换完成。";
        return null;
    }

    public static void TraceLoad(object host, int territoryId)
    {
        var message = $"正在加载区域：{territoryId}。请等待游戏画面切换及阶段设置完成。";
        host.GetType().GetMethod("LogRuntime", InstanceMembers)!.Invoke(host, [message]);
        Console.WriteLine($"Simulant territory load requested: {territoryId}.");
    }
}

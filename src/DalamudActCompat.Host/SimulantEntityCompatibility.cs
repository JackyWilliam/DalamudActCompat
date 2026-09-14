using System.Collections;

namespace DalamudActCompat.Host;

public static class SimulantEntityCompatibility
{
    public static IEnumerable<IntPtr> UseLiveEntityPointers(IEnumerable<IntPtr> snapshot)
    {
        var assembly = snapshot.GetType().Assembly;
        var managerType = assembly.GetType("Simulant.Game.FFCS.Client.Game.Object.GameObjectManager")!;
        if ((IntPtr)managerType.GetProperty("InstancePtr")!.GetValue(null)! == IntPtr.Zero)
            return snapshot;

        SimulantCompatibility.EnsureNativeAccess();
        // SpawnEObj looks up its new object immediately after the native call returns.
        // The Host's periodic combatant snapshot can still predate that call, so read
        // the upstream live table once instead of waiting for another IPC update.
        var manager = managerType.GetMethod("Instance")!.Invoke(null, null)!;
        var arrays = managerType.GetProperty("Objects")!.GetValue(manager)!;
        var sorted = arrays.GetType().GetProperty("IndexSorted")!.GetValue(arrays)!;
        var objects = (IEnumerable)sorted.GetType().GetProperty("GameObjects")!.GetValue(sorted)!;
        var pointer = assembly.GetType("Simulant.Game.FFCS.Client.Game.Object.GameObject")!.GetProperty("Ptr")!;
        var result = new List<IntPtr>();
        foreach (var entity in objects)
        {
            var address = (IntPtr)pointer.GetValue(entity)!;
            if (address != IntPtr.Zero) result.Add(address);
        }
        return result;
    }
}

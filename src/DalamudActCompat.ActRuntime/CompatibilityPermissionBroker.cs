using System.Collections.Concurrent;

namespace DalamudActCompat.ActRuntime;

public enum ActCapability
{
    ReadCombatLogs,
    ReadLocalConfiguration,
    TextToSpeech,
    Clipboard,
    NetworkRequest,
    LaunchExternalProcess,
    WriteFiles,
    GameCommand,
    NativeSystemAccess,
    NativeGameMemory,
    HighRiskScript,
}

public static class CompatibilityPermissionBroker
{
    private static readonly object SyncRoot = new();
    private static readonly ConcurrentDictionary<string, byte> Audited =
        new(StringComparer.Ordinal);
    private static Func<string, ActCapability, bool>? permissionCheck;
    private static Action<string>? audit;

    internal static void Configure(
        Func<string, ActCapability, bool> check,
        Action<string> auditWriter)
    {
        lock (SyncRoot)
        {
            permissionCheck = check;
            audit = auditWriter;
            Audited.Clear();
        }
    }

    internal static void Reset()
    {
        lock (SyncRoot)
        {
            permissionCheck = null;
            audit = null;
            Audited.Clear();
        }
    }

    public static bool IsAllowed(string pluginId, ActCapability capability)
    {
        Func<string, ActCapability, bool> check;
        lock (SyncRoot)
        {
            check = permissionCheck
                    ?? throw new InvalidOperationException(
                        "The ACT compatibility permission broker is not configured.");
        }

        var allowed = check(pluginId, capability);
        var key = $"{pluginId}|{capability}|{allowed}";
        if (Audited.TryAdd(key, 0))
        {
            audit?.Invoke(
                $"permission plugin={pluginId} capability={capability} decision=" +
                (allowed ? "allow" : "deny"));
        }

        return allowed;
    }

    public static void Demand(string pluginId, ActCapability capability)
    {
        if (!IsAllowed(pluginId, capability))
        {
            throw new UnauthorizedAccessException(
                $"ACT plugin '{pluginId}' is not authorized for capability '{capability}'.");
        }
    }
}

using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DalamudActCompat.Host;

public static partial class HostPluginBridge
{
    private const BindingFlags DiagnosticMembers = BindingFlags.Public | BindingFlags.NonPublic |
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy;
    private static readonly DrawingDiagnosticBudget DrawingBudget = new();
    private static readonly ConditionalWeakTable<object, DrawingCallbackBinding> DrawingCallbacks = new();
    private static readonly object DrawingEntityLock = new();
    private static readonly Dictionary<uint, (long Seen, string State)> DrawingEntities = [];
    private static uint drawingEntityZone;
    private static readonly string[] DrawingScalars =
    [
        "U7a_phase", "U7a1_光轮_is火safe", "U7a1_光轮_is右safe", "U7b_phase",
        "U7b11a_b真火", "U7b11a_b真冰", "U7b11a_b冰主斜", "U7b11a_b散raw", "myIdx", "myIdx_isTH",
    ];
    private static readonly HashSet<string> DrawingCallbackNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "ObjectScaling", "SetObjectScale", "SetOpacity", "SetStatusLoopVfx", "LockOn", "PictoACT", "AdvWm", "place",
    };
    private static readonly HashSet<string> DrawingLogTypes = new(StringComparer.OrdinalIgnoreCase)
    { "03", "04", "14", "1A", "1B", "1E", "23", "26", "35", "37", "107", "111", "1019D" };

    // Observe original return values and fields only. Re-evaluating a TN expression or
    // condition here could mutate variables, acquire a mutex, or hide the timing bug.
    public static void ObserveDrawingDiagnostic(object subject, object? context, string stage, object? result)
    {
        if (stage == "match" && result is null || !DrawingDiagnosticsAllowed()) return;
        try
        {
            var trigger = Member(context, "Trigger") ?? subject;
            if (!IsDrawingNode(trigger)) return;
            var text = context as string ?? Member(context, "triggeredText") as string ?? Member(context, "Text") as string;
            var evt = DrawingEvent.Parse(text);
            var plugin = Member(context, "Plugin") ?? DrawingPlugin(subject);
            var data = new Dictionary<string, object?>
            {
                ["nodeId"] = Member(trigger, "Id")?.ToString(),
                ["node"] = Clip(Member(trigger, "Name")?.ToString(), 120),
                ["contextId"] = Member(context, "id")?.ToString(),
                ["event"] = evt,
            };
            if (stage == "match")
            {
                data["matched"] = true;
                data["enabled"] = Member(trigger, "Enabled");
                data["parentsEnabled"] = ParentsDrawingEnabled(trigger);
                data["source"] = Member(trigger, "Source")?.ToString();
                data["refirePolicy"] = Member(trigger, "PeriodRefire")?.ToString();
                data["refireDelayedUntil"] = Member(trigger, "RefireDelayedUntil");
                data["repositoryRestrictions"] = Member(trigger, "RepoRestrictions")?.ToString();
            }
            else if (stage == "folder-filter") data["filterResult"] = result?.ToString();
            else if (stage == "fire-return") data["acceptedOrDeferred"] = result;
            else if (stage == "condition-blocked") data["blocked"] = result;
            else if (stage == "exception")
            {
                var error = result as Exception;
                while (error?.InnerException is not null) error = error.InnerException;
                data["exceptionType"] = error?.GetType().FullName;
                data["message"] = Clip(error?.Message, 400);
            }
            if (stage.StartsWith("action-", StringComparison.Ordinal) || stage == "exception")
            {
                data["action"] = Member(subject, "ActionType")?.ToString();
                data["order"] = Member(subject, "OrderNumber");
                data["callback"] = Member(subject, "NamedCallbackName");
                data["variable"] = Clip(Member(subject, "VariableName")?.ToString(), 100);
                if (stage == "action-return")
                {
                    // The original action swallows exceptions, so a return is not a
                    // success signal. Its separate exception record remains authoritative.
                    data["lastActionResult"] = LastActionResult(context);
                }
            }
            data["state"] = DrawingState(plugin, evt?.ActorId ?? 0);
            WriteDrawingDiagnostic(stage, data, stage == "exception");
        }
        catch (Exception ex) { DrawingObserverError(ex); }
    }

    public static void ObserveDrawingLog(object plugin, string line, string stage, object? source)
    {
        if (!DrawingDiagnosticsAllowed()) return;
        try
        {
            var evt = DrawingEvent.Parse(line);
            if (evt is null || !DrawingLogTypes.Contains(evt.Type)) return;
            // Player cast spam is unrelated to these encounter initializers. Keep
            // player-targeted markers/statuses, but only NPC cast/spawn traffic.
            if (evt.Type is "14" or "03" && (evt.ActorId & 0xF0000000) != 0x40000000) return;
            TrackDrawingEntity(evt.ActorId);
            WriteDrawingDiagnostic(stage, new()
            {
                ["event"] = evt, ["source"] = source?.ToString(),
                ["initialized"] = Member(plugin, "isInitialized"),
                ["state"] = DrawingState(plugin, evt.ActorId),
            });
            WriteDrawingInventory(plugin);
        }
        catch (Exception ex) { DrawingObserverError(ex); }
    }

    public static void InvokeDrawingCallback(object plugin, string name, string value, object? actionInstance)
    {
        var callback = DrawingCallbacks.GetValue(plugin, owner => new(owner)).Invoke;
        object? context = null;
        var trace = false;
        try
        {
            if (DrawingDiagnosticsAllowed() && DrawingCallbackNames.Contains(name))
            {
                context = Member(actionInstance, "ctx");
                trace = IsDrawingNode(Member(context, "Trigger"));
            }
        }
        catch (Exception ex) { DrawingObserverError(ex); }
        if (!trace)
        {
            callback(name, value);
            return;
        }
        var id = Guid.NewGuid().ToString("N");
        void Observe(string stage, Exception? error = null)
        {
            try
            {
                var evt = DrawingEvent.Parse(Member(context, "triggeredText") as string);
                WriteDrawingDiagnostic(stage, new()
                {
                    ["callId"] = id, ["contextId"] = Member(context, "id")?.ToString(),
                    ["nodeId"] = Member(Member(context, "Trigger"), "Id")?.ToString(), ["event"] = evt,
                    ["callback"] = name, ["registered"] = CallbackCount(plugin, name),
                    // Native CSV parameters contain the actual resolved address/number.
                    // PictoACT/waymark payloads can contain labels or player names: retain
                    // only length and digest, not arbitrary user text or chat commands.
                    ["parameters"] = SafeDrawingParameters(name, value),
                    ["state"] = DrawingState(plugin, evt?.ActorId ?? 0),
                    ["exceptionType"] = error?.GetType().FullName, ["message"] = Clip(error?.Message, 400),
                }, error is not null);
            }
            catch (Exception ex) { DrawingObserverError(ex); }
        }
        Observe("callback-enter");
        try
        {
            callback(name, value);
            Observe("callback-return");
        }
        catch (Exception ex)
        {
            Observe("callback-throw", ex);
            throw; // Preserve the exception and TN's original error handling.
        }
    }

    private sealed class DrawingCallbackBinding
    {
        internal Action<string, string> Invoke { get; }
        internal DrawingCallbackBinding(object plugin) => Invoke = (Action<string, string>)plugin.GetType()
            .GetMethod("InvokeNamedCallback", [typeof(string), typeof(string)])!
            .CreateDelegate(typeof(Action<string, string>), plugin);
    }

    private static bool DrawingDiagnosticsAllowed() =>
        FfxivRepositoryInstance.GetCurrentTerritoryID() is 1238 or 1363 && IsAllowed("triggernometry", "ReadCombatLogs");

    private static object? Member(object? obj, string name) => obj?.GetType().GetProperty(name, DiagnosticMembers)?.GetValue(obj)
        ?? obj?.GetType().GetField(name, DiagnosticMembers)?.GetValue(obj);

    private static object? DrawingPlugin(object subject)
    {
        var type = subject.GetType().Assembly.GetType("Triggernometry.Core.RealPlugin");
        return type?.GetProperty("Instance", DiagnosticMembers)?.GetValue(null) ?? type?.GetField("Instance", DiagnosticMembers)?.GetValue(null);
    }

    private static bool IsDrawingNode(object? node)
    {
        for (var depth = 0; node is not null && depth < 24; depth++, node = Member(node, "Parent"))
        {
            var name = Member(node, "Name") as string;
            if (name?.StartsWith("U7a", StringComparison.OrdinalIgnoreCase) == true ||
                name?.StartsWith("U7b", StringComparison.OrdinalIgnoreCase) == true) return true;
        }
        return false;
    }

    private static object? LastActionResult(object? context)
    {
        var values = Member(context, "ActionResults") as IList;
        if (values is null || !Monitor.TryEnter(values)) return "unavailable-or-busy";
        try { return values.Count == 0 ? null : values[values.Count - 1]; }
        finally { Monitor.Exit(values); }
    }

    private static int CallbackCount(object plugin, string name)
    {
        var callbacks = Member(plugin, "callbacksByName") as IDictionary;
        if (callbacks is null || !Monitor.TryEnter(callbacks)) return -1;
        try { return (callbacks[name] as ICollection)?.Count ?? 0; }
        finally { Monitor.Exit(callbacks); }
    }

    private static Dictionary<string, object?> DrawingState(object? plugin, uint actor)
    {
        var state = new Dictionary<string, object?>
        {
            ["snapshot"] = FfxivRepositoryInstance.DrawingDiagnosticSnapshot(actor),
            ["nativeAllowed"] = IsPostNamazuNativeRuntimeAllowed(),
            ["gameCommandAllowed"] = IsAllowed("postnamazu", "GameCommand"),
        };
        if (plugin is null) { state["variables"] = "plugin-unavailable"; return state; }
        var getStore = plugin.GetType().GetMethod("GetVariableStore", [typeof(bool)]);
        var temporary = getStore?.Invoke(plugin, [false]);
        var persistent = getStore?.Invoke(plugin, [true]);
        state["variables"] = ReadDrawingValues(Member(temporary, "Scalar"), DrawingScalars);
        var dicts = Member(persistent, "Dict") as IDictionary;
        if (dicts is not null && Monitor.TryEnter(dicts))
        {
            try
            {
                state["config"] = ReadDrawingValues(Member(dicts["U7a_cfg"], "Values"), ["P1光轮缩放on"]);
            }
            finally { Monitor.Exit(dicts); }
        }
        else state["config"] = "unavailable-or-busy";
        // Even reading a static native-bridge field can run its type initializer.
        // Report the Host attachment prerequisite without opening/scanning game memory.
        state["gameProcessRegistered"] = FfxivRepositoryInstance.GetGameProcessId() > 0;
        return state;
    }

    private static object ReadDrawingValues(object? source, string[] keys)
    {
        if (source is not IDictionary values || !Monitor.TryEnter(values)) return "unavailable-or-busy";
        try { return keys.ToDictionary(key => key, key => Clip(values[key]?.ToString(), 64)); }
        finally { Monitor.Exit(values); }
    }

    private static void WriteDrawingInventory(object plugin)
    {
        var zone = FfxivRepositoryInstance.GetCurrentTerritoryID();
        if (!DrawingBudget.InventoryDue(zone)) return;
        var triggers = Member(plugin, "Triggers") as IList;
        if (triggers is null || !Monitor.TryEnter(triggers)) return;
        object[] nodes;
        try { nodes = triggers.Cast<object>().Where(IsDrawingNode).Take(512).ToArray(); }
        finally { Monitor.Exit(triggers); }
        var enabled = nodes.Count(node => Member(node, "Enabled") is true);
        WriteDrawingDiagnostic("inventory", new()
        {
            ["tnAssembly"] = plugin.GetType().Assembly.GetName().Version?.ToString(),
            ["hostAssembly"] = typeof(HostPluginBridge).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            ["nodes"] = nodes.Length, ["enabledNodes"] = enabled,
            ["registeredCallbacks"] = DrawingCallbackNames.ToDictionary(name => name, name => CallbackCount(plugin, name)),
            ["note"] = "callback-return only confirms handler return, not rendered pixels",
        });
        foreach (var node in nodes.Where(HasDrawingCallback).Take(48))
            WriteDrawingDiagnostic("node-config", new()
            {
                ["nodeId"] = Member(node, "Id")?.ToString(), ["node"] = Clip(Member(node, "Name")?.ToString(), 120),
                ["enabled"] = Member(node, "Enabled"), ["source"] = Member(node, "Source")?.ToString(),
                ["parentsEnabled"] = ParentsDrawingEnabled(node), ["repositoryRestrictions"] = Member(node, "RepoRestrictions")?.ToString(),
            });
    }

    private static bool HasDrawingCallback(object node) => Member(node, "Actions") is IEnumerable actions &&
        actions.Cast<object>().Any(action => DrawingCallbackNames.Contains(Member(action, "NamedCallbackName") as string ?? "") ||
            DrawingScalars.Contains(Member(action, "VariableName") as string ?? ""));

    private static bool ParentsDrawingEnabled(object node)
    {
        for (var p = Member(node, "Parent"); p is not null; p = Member(p, "Parent"))
            if (Member(p, "Enabled") is false) return false;
        return true;
    }

    internal static object SafeDrawingParameters(string name, string value)
    {
        var native = name is "ObjectScaling" or "SetObjectScale" or "SetOpacity" or "SetStatusLoopVfx";
        return new
        {
            length = value.Length, digest = DrawingEvent.Digest(value),
            numericCsv = native && value.Length <= 160 && Regex.IsMatch(value, @"\A[0-9a-fA-FxX.,+\-\s]+\z", RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(20)) ? value : null,
        };
    }

    private static void TrackDrawingEntity(uint id)
    {
        if (id == 0) return;
        lock (DrawingEntityLock)
        {
            var zone = FfxivRepositoryInstance.GetCurrentTerritoryID();
            if (zone != drawingEntityZone) { DrawingEntities.Clear(); drawingEntityZone = zone; }
            if (!DrawingEntities.ContainsKey(id) && DrawingEntities.Count >= 64)
                DrawingEntities.Remove(DrawingEntities.MinBy(pair => pair.Value.Seen).Key);
            DrawingEntities[id] = (Environment.TickCount64, FfxivRepositoryInstance.DrawingDiagnosticEntityState(id));
        }
    }

    private static void ObserveDrawingEntityUpdates()
    {
        if (!DrawingDiagnosticsAllowed()) return;
        try
        {
            lock (DrawingEntityLock)
            {
                var zone = FfxivRepositoryInstance.GetCurrentTerritoryID();
                if (zone != drawingEntityZone) { DrawingEntities.Clear(); drawingEntityZone = zone; }
                foreach (var pair in DrawingEntities.ToArray())
                {
                    if (Environment.TickCount64 - pair.Value.Seen > 30000) { DrawingEntities.Remove(pair.Key); continue; }
                    var current = FfxivRepositoryInstance.DrawingDiagnosticEntityState(pair.Key);
                    if (current == pair.Value.State) continue;
                    DrawingEntities[pair.Key] = (pair.Value.Seen, current);
                    WriteDrawingDiagnostic("entity-change", new()
                    {
                        ["actor"] = pair.Key.ToString("X8"), ["elapsedMs"] = Environment.TickCount64 - pair.Value.Seen,
                        ["previous"] = pair.Value.State, ["current"] = current,
                        ["snapshot"] = FfxivRepositoryInstance.DrawingDiagnosticSnapshot(pair.Key),
                    });
                }
            }
        }
        catch (Exception ex) { DrawingObserverError(ex); }
    }

    private static void WriteDrawingDiagnostic(string stage, Dictionary<string, object?> data, bool error = false)
    {
        var zone = FfxivRepositoryInstance.GetCurrentTerritoryID();
        if (!DrawingBudget.Take(zone, error, out var dropped, stage is "received" or "queued")) return;
        data["schema"] = 1; data["stage"] = stage; data["zone"] = zone;
        data["utc"] = DateTimeOffset.UtcNow; data["suppressedSincePreviousWindow"] = dropped;
        Console.WriteLine("DACT_DRAW_DIAG " + JsonSerializer.Serialize(data));
    }

    private static void DrawingObserverError(Exception ex)
    {
        // Diagnostics must never become a new failure path on TN's log/action workers.
        try { WriteDrawingDiagnostic("observer-error", new() { ["exceptionType"] = ex.GetType().FullName }, true); }
        catch { }
    }

    private static string? Clip(string? value, int max) => value is null ? null : value[..Math.Min(value.Length, max)];
}

internal sealed record DrawingEvent(string Time, string Type, string Actor, string? Detail, string Key, string DigestValue)
{
    [System.Text.Json.Serialization.JsonIgnore]
    internal uint ActorId => uint.TryParse(Actor, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value) ? value : 0;
    internal static DrawingEvent? Parse(string? text)
    {
        if (text is null || text.Length > 32768 || text.Length < 20 || text[0] != '[' || text[13] != ']') return null;
        var payload = text.IndexOf(' ', 15);
        if (payload < 0) return null;
        var fields = text[(payload + 1)..].Split(':');
        if (fields.Length < 2 || fields[0].Length > 8) return null;
        var actor = fields[1];
        if (!uint.TryParse(actor, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _)) return null;
        var detailIndex = fields[0] switch { "03" => 9, "14" => 3, "1B" => 5, "111" or "107" => 2, _ => -1 };
        var detail = detailIndex >= 0 && fields.Length > detailIndex ? fields[detailIndex] : null;
        if (detail?.Any(c => !Uri.IsHexDigit(c)) == true) detail = null;
        var time = text[..14];
        return new(time, fields[0], actor.ToUpperInvariant(), detail, time + actor.ToUpperInvariant(), Digest(text));
    }
    internal static string Digest(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
}

internal sealed class DrawingDiagnosticBudget(Func<long>? clock = null)
{
    private readonly object sync = new();
    private readonly Func<long> clock = clock ?? (() => Environment.TickCount64);
    private long started;
    private long inventoryAt = long.MinValue;
    private uint zone;
    private int regular, traffic, errors, suppressed;
    internal bool Take(uint nextZone, bool error, out int dropped, bool eventTraffic = false)
    {
        lock (sync)
        {
            var now = clock(); dropped = 0;
            if (zone != nextZone || now - started >= 60000)
            {
                dropped = suppressed; suppressed = regular = traffic = errors = 0; started = now;
                if (zone != nextZone) inventoryAt = long.MinValue;
                zone = nextZone;
            }
            // Background status/update traffic must not consume the budget reserved
            // for actual matched nodes, their conditions, and drawing callbacks.
            if (error ? errors >= 60 : eventTraffic ? traffic >= 120 : regular >= 600) { suppressed++; return false; }
            if (error) errors++; else if (eventTraffic) traffic++; else regular++;
            return true;
        }
    }
    internal bool InventoryDue(uint nextZone)
    {
        lock (sync)
        {
            var now = clock();
            if (zone == nextZone && inventoryAt != long.MinValue && now - inventoryAt < 60000) return false;
            inventoryAt = now;
            return true;
        }
    }
}

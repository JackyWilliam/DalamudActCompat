using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using DalamudActCompat.Host;
using DalamudActCompat.Protocol;

internal static class DrawingDiagnosticTests
{
    internal static JsonElement[] ParseRecords(string log) => log.Split('\n')
        .Where(line => line.StartsWith("DACT_DRAW_DIAG "))
        .Select(line => JsonSerializer.Deserialize<JsonElement>(line[15..])).ToArray();

    internal static void Run()
    {
        const string line = "[21:51:53.611] ChatLog 03:40FF0001:PRIVATE_PLAYER:0:100:0:0:0:0:17821:0:";
        var evt = DrawingEvent.Parse(line);
        Require(evt?.ActorId == 0x40FF0001 && evt.Detail == "17821", "entity log fields");
        Require(!JsonSerializer.Serialize(evt).Contains("PRIVATE_PLAYER"), "player name leaked");
        Require(DrawingEvent.Parse("[21:51:53.611] ChatLog 00:PRIVATE_CHAT") is null, "chat accepted");
        Require(DrawingEvent.Parse(new string('x', 33000)) is null, "oversized input");
        var raw = DrawingEvent.Parse("[21:38:41.638] 273 111:4001895c:019D:1:2:0:0:");
        var converted = DrawingEvent.Parse("[21:38:41.638] ChatLog 1019D:4001895C::0:2015156:");
        Require(raw?.Key == converted?.Key, "raw/converted event identity");
        Require(!JsonSerializer.Serialize(HostPluginBridge.SafeDrawingParameters("PictoACT", "PRIVATE_LABEL"))
            .Contains("PRIVATE_LABEL"), "arbitrary drawing payload leaked");
        Require(JsonSerializer.Serialize(HostPluginBridge.SafeDrawingParameters("SetOpacity", "0x1234,0.5"))
            .Contains("0x1234,0.5"), "resolved numeric parameter missing");

        long now = 0;
        var budget = new DrawingDiagnosticBudget(() => now);
        for (var i = 0; i < 600; i++) Require(budget.Take(1238, false, out _), "premature limit");
        Require(!budget.Take(1238, false, out _), "normal budget unbounded");
        Require(budget.Take(1238, true, out _), "errors lost behind normal flood");
        now = 60000;
        Require(budget.Take(1238, false, out var dropped) && dropped == 1, "suppression summary missing");
        Require(budget.InventoryDue(1238) && !budget.InventoryDue(1238), "inventory flood");
        var trafficBudget = new DrawingDiagnosticBudget(() => now);
        for (var i = 0; i < 120; i++) Require(trafficBudget.Take(1238, false, out _, true), "premature traffic limit");
        Require(!trafficBudget.Take(1238, false, out _, true), "traffic unbounded");
        Require(trafficBudget.Take(1238, false, out _) && trafficBudget.Take(1238, true, out _),
            "background traffic starved node/error diagnostics");

        HostPluginBridge.ConfigurePermissions(new(new Dictionary<string, IReadOnlyList<string>>
        { ["triggernometry"] = ["ReadCombatLogs"] }, ["triggernometry"]));
        HostPluginBridge.ApplyFfxivEntitySnapshot(new(1238, 0, DateTimeOffset.UtcNow, []));
        var plugin = new FakePlugin();
        var node = new FakeNode { Name = "光轮", Parent = new() { Name = "U7a test" } };
        var context = new FakeContext { Trigger = node, Plugin = plugin, triggeredText = line };
        var instance = new FakeInstance { ctx = context };
        plugin.Store.Scalar["U7a1_光轮_is火safe"] = "1";
        var original = Console.Out;
        using var captured = new StringWriter();
        try
        {
            Console.SetOut(captured);
            HostPluginBridge.ObserveDrawingDiagnostic(node, context, "condition-blocked", true);
            HostPluginBridge.ObserveDrawingDiagnostic(node, context, "fire-return", false);
            HostPluginBridge.InvokeDrawingCallback(plugin, "SetOpacity", "0x1234,0.5", instance);
            Require(plugin.Calls == 1, "callback duplicated or suppressed");
            plugin.Failure = new InvalidOperationException("EXPECTED_DIAGNOSTIC_FAILURE");
            try { HostPluginBridge.InvokeDrawingCallback(plugin, "SetOpacity", "0x1234,0.5", instance); }
            catch (Exception ex) { Require(ReferenceEquals(ex, plugin.Failure), "original callback exception replaced"); }
            Require(plugin.Calls == 2, "throwing callback not executed once");
            HostPluginBridge.ObserveDrawingDiagnostic(node, context, "exception", plugin.Failure);
            var before = captured.ToString();
            HostPluginBridge.ApplyFfxivEntitySnapshot(new(177, 0, DateTimeOffset.UtcNow, []));
            HostPluginBridge.ObserveDrawingDiagnostic(node, context, "fire-return", false);
            Require(captured.ToString() == before, "other zones logged");
            HostPluginBridge.ApplyFfxivEntitySnapshot(new(1238, 0, DateTimeOffset.UtcNow, []));
            HostPluginBridge.ConfigurePermissions(new(new Dictionary<string, IReadOnlyList<string>>(), []));
            var afterPermissions = captured.ToString();
            HostPluginBridge.ObserveDrawingDiagnostic(node, context, "fire-return", false);
            Require(captured.ToString() == afterPermissions, "revoked log permission ignored");
        }
        finally { Console.SetOut(original); }
        var records = captured.ToString().Split('\n').Where(x => x.StartsWith("DACT_DRAW_DIAG "))
            .Select(x => JsonDocument.Parse(x[15..])).ToArray();
        Require(records.Any(r => r.RootElement.GetProperty("stage").GetString() == "condition-blocked" && r.RootElement.GetProperty("blocked").GetBoolean()), "condition outcome missing");
        Require(records.Any(r => r.RootElement.GetProperty("stage").GetString() == "callback-return" && r.RootElement.GetProperty("registered").GetInt32() == 0), "unregistered silent no-op not visible");
        Require(records.Any(r => r.RootElement.GetProperty("stage").GetString() == "callback-throw"), "callback failure missing");
        Require(records.All(r => r.RootElement.GetProperty("stage").GetString() != "observer-error"), "observer failed");
        Require(!captured.ToString().Contains("PRIVATE_PLAYER"), "input name leaked into diagnostic");
        foreach (var record in records) record.Dispose();
        HostPluginBridge.ConfigurePermissions(new(new Dictionary<string, IReadOnlyList<string>>
        { ["triggernometry"] = ["ReadCombatLogs"] }, ["triggernometry"]));
        plugin.Failure = null;
        captured.GetStringBuilder().Clear();
        try
        {
            Console.SetOut(captured);
            HostPluginBridge.InvokeDrawingCallback(plugin, "SetOpacity", "0x1234,0.5", new BrokenInstance());
        }
        finally { Console.SetOut(original); }
        Require(plugin.Calls == 3 && captured.ToString().Contains("observer-error"), "observer failure changed original dispatch");
        HostPluginBridge.ConfigurePermissions(new(new Dictionary<string, IReadOnlyList<string>>(), []));
        Console.WriteLine("Drawing diagnostics: event correlation/privacy/limits/conditions/callback/no-op/exception/zone/permissions passed.");
    }

    internal static void ValidateRewrittenAssembly(Assembly implementation)
    {
        if (implementation.GetType("Triggernometry.FFXIV.LogTranscribe.LogTranscriber") is null) return;
        // JIT all edited methods: loading the assembly alone does not validate injected
        // IL, especially branches into returns and exception-handler boundaries.
        foreach (var (typeName, methodName) in new[]
        {
            ("Triggernometry.Core.Trigger", "CheckMatch"), ("Triggernometry.Core.Trigger", "Fire"),
            ("Triggernometry.Core.Trigger", "TryBlockByCondition"), ("Triggernometry.Core.Folder", "PassesFilter"),
            ("Triggernometry.Core.ActionOld", "ExecutionImplementation"),
            ("Triggernometry.Core.RealPlugin", "LogLineQueuer"),
            ("Triggernometry.Core.Actions.ActionNamedCallback", "ExecuteImplementation"),
        })
        {
            var method = implementation.GetType(typeName)!.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (method is not null)
            {
                try { RuntimeHelpers.PrepareMethod(method.MethodHandle); }
                catch (Exception ex) { throw new InvalidOperationException("Drawing diagnostic IL: " + typeName + "." + methodName, ex); }
            }
        }
        var type = implementation.GetType("Triggernometry.Core.Trigger")!;
        var node = RuntimeHelpers.GetUninitializedObject(type);
        type.GetField("regexCache", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(node, new Regex("^match$"));
        var match = type.GetMethod("CheckMatch")!;
        Require(match.Invoke(node, ["match"]) is Match { Success: true } && match.Invoke(node, ["miss"]) is null,
            "regex result changed by observer");
        Console.WriteLine("Drawing diagnostics: real TN rewritten methods JIT and regex return semantics passed.");
    }

    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException("Drawing diagnostics: " + message); }

    private sealed class FakeNode
    {
        public string Name { get; set; } = "";
        public Guid Id { get; } = Guid.NewGuid();
        public FakeNode? Parent { get; set; }
    }
    private sealed class FakeContext
    {
        public FakeNode Trigger = null!;
        public FakePlugin Plugin = null!;
        public string triggeredText = "";
        public Guid id = Guid.NewGuid();
    }
    private sealed class FakeInstance { public FakeContext ctx = null!; }
    private sealed class BrokenInstance { public object ctx => throw new InvalidOperationException("Expected observer ABI mismatch"); }
    private sealed class FakeStore
    {
        public IDictionary Scalar { get; } = new Hashtable();
        public IDictionary Dict { get; } = new Hashtable();
    }
    private sealed class FakePlugin
    {
        public IDictionary callbacksByName = new Hashtable();
        public FakeStore Store { get; } = new();
        public FakeStore GetVariableStore(bool persistent) => Store;
        public int Calls;
        public Exception? Failure;
        public void InvokeNamedCallback(string name, string value)
        { Calls++; if (Failure is not null) throw Failure; }
    }
}

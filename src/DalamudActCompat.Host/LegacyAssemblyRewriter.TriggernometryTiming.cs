using Mono.Cecil;
using Mono.Cecil.Cil;

namespace DalamudActCompat.Host;

public static partial class LegacyAssemblyRewriter
{
    private static void UseTriggernometryEventTime(ModuleDefinition module)
    {
        // Upstream TN reads DateTime.Now when it queues, refire-checks and schedules an
        // event. In ACT that instant is the game event; across the Host IPC it trails by a
        // batch interval or a stall, which merges or splits refire windows and shifts delays.
        var plugin = module.GetType("Triggernometry.Core.RealPlugin");
        var logEvent = module.GetType("Triggernometry.Core.LogEvent");
        var trigger = module.Types.SelectMany(EnumerateTypes).FirstOrDefault(type =>
            type.Name == "Trigger" && type.Methods.Any(method => method.Name == "UpdateFireTiming"));
        var queuer = plugin?.Methods.SingleOrDefault(method =>
            method.Name == "LogLineQueuer" && method.HasBody && method.Parameters.Count == 3 &&
            method.Parameters[0].ParameterType.MetadataType == MetadataType.String);
        var testTrigger = plugin?.Methods.SingleOrDefault(method =>
            method.Name == "TestTrigger" && method.HasBody && method.Parameters.Count == 3 &&
            method.Parameters[1].ParameterType.FullName == logEvent?.FullName);
        var timestamp = logEvent?.Properties.SingleOrDefault(property => property.Name == "Timestamp")?.GetMethod;
        var fireTiming = trigger?.Methods
            .Where(method => method.HasBody && method.Name is "UpdateFireTiming" or "ComputeScheduleBaseTime")
            .ToArray() ?? [];
        if (queuer is null || testTrigger is null || timestamp is null || fireTiming.Length != 2 ||
            NowCalls(queuer).Length != 1 || NowCalls(testTrigger).Length != 1 ||
            fireTiming.Any(method => NowCalls(method).Length != 1))
        {
            // A different TN build keeps its own timing instead of failing to load.
            Console.WriteLine("Triggernometry event-time compatibility skipped: unrecognized trigger timing ABI.");
            return;
        }

        MethodReference Bridge(string name) => module.ImportReference(
            typeof(HostPluginBridge).GetMethod(name)
            ?? throw new MissingMethodException(typeof(HostPluginBridge).FullName, name));
        var fireNow = Bridge(nameof(HostPluginBridge.TriggernometryFireNow));

        // LogLineQueuer(text, zone, source): le.Timestamp = <game time parsed from text>.
        var queuedAt = NowCalls(queuer)[0];
        queuer.Body.GetILProcessor().InsertBefore(queuedAt, Instruction.Create(OpCodes.Ldarg_1));
        queuedAt.Operand = Bridge(nameof(HostPluginBridge.ResolveTriggernometryLogTime));

        // TestTrigger(trigger, logEvent, force): record the event time for its synchronous Fire.
        var testIl = testTrigger.Body.GetILProcessor();
        var entry = testTrigger.Body.Instructions[0];
        testIl.InsertBefore(entry, Instruction.Create(OpCodes.Ldarg_2));
        testIl.InsertBefore(entry, Instruction.Create(OpCodes.Callvirt, module.ImportReference(timestamp)));
        testIl.InsertBefore(entry, Instruction.Create(OpCodes.Call, Bridge(nameof(HostPluginBridge.BeginTriggernometryFire))));
        foreach (var method in fireTiming.Prepend(testTrigger))
        {
            NowCalls(method)[0].Operand = fireNow;
        }

        Console.WriteLine("Triggernometry refire windows and action delays follow game log time.");
    }

    private static Instruction[] NowCalls(MethodDefinition method)
        => method.Body.Instructions
            .Where(instruction => instruction.OpCode == OpCodes.Call && instruction.Operand is MethodReference
            {
                Name: "get_Now",
                DeclaringType.FullName: "System.DateTime",
            })
            .ToArray();
}

using Mono.Cecil;
using Mono.Cecil.Cil;

namespace DalamudActCompat.Host;

public static partial class LegacyAssemblyRewriter
{
    private static void ObserveTriggernometryDrawingPipeline(ModuleDefinition module)
    {
        // This observer targets the verified 2.2 ABI; older versions retain their
        // original behavior rather than guessing the layout of action/context objects.
        if (module.GetType("Triggernometry.FFXIV.LogTranscribe.LogTranscriber") is null)
        {
            Console.WriteLine("DACT_DRAW_DIAG_UNAVAILABLE: detailed observers require the Triggernometry 2.2 ABI.");
            return;
        }
        var notify = module.ImportReference(typeof(HostPluginBridge).GetMethod(nameof(HostPluginBridge.ObserveDrawingDiagnostic))!);
        var trigger = module.GetType("Triggernometry.Core.Trigger");
        var fire = trigger.Methods.Single(m => m.Name == "Fire" && m.Parameters.Count == 2);
        ObserveReturns(trigger.Methods.Single(m => m.Name == "CheckMatch"), 1, "match");
        ObserveReturns(fire, 1, "fire-return");
        var conditions = trigger.Methods.SingleOrDefault(m => m.Name == "TryBlockByCondition");
        if (conditions is not null) ObserveReturns(conditions, 1, "condition-blocked");
        ObserveExceptions(fire, 1);
        ObserveReturns(module.GetType("Triggernometry.Core.Folder").Methods.Single(m => m.Name == "PassesFilter"), 1, "folder-filter");

        var action = module.GetType("Triggernometry.Core.ActionOld").Methods.Single(m => m.Name == "ExecutionImplementation");
        var actionIl = action.Body.GetILProcessor();
        var actionFirst = action.Body.Instructions[0];
        foreach (var instruction in Notification(actionIl, 2, "action-enter", null)) actionIl.InsertBefore(actionFirst, instruction);
        ObserveReturns(action, 2, "action-return");
        ObserveExceptions(action, 2);

        var plugin = module.GetType("Triggernometry.Core.RealPlugin");
        var queue = plugin.Methods.Single(m => m.Name == "LogLineQueuer" && m.Parameters.Count == 3);
        var queueIl = queue.Body.GetILProcessor();
        var first = queue.Body.Instructions[0];
        foreach (var instruction in new[]
        {
            queueIl.Create(OpCodes.Ldarg_0), queueIl.Create(OpCodes.Ldarg_1), queueIl.Create(OpCodes.Ldstr, "queued"),
            queueIl.Create(OpCodes.Ldarg_3), queueIl.Create(OpCodes.Box, queue.Parameters[2].ParameterType),
            queueIl.Create(OpCodes.Call, module.ImportReference(typeof(HostPluginBridge).GetMethod(nameof(HostPluginBridge.ObserveDrawingLog))!)),
        }) queueIl.InsertBefore(first, instruction);

        var callback = module.GetType("Triggernometry.Core.Actions.ActionNamedCallback").Methods.Single(m => m.Name == "ExecuteImplementation");
        var callbackIl = callback.Body.GetILProcessor();
        var invoke = callback.Body.Instructions.Single(i => i.Operand is MethodReference m && m.Name == "InvokeNamedCallback");
        // Keep the original argument evaluation and dispatcher, adding only the
        // context carried by this ActionInstance for a reliable event/call correlation.
        callbackIl.InsertBefore(invoke, callbackIl.Create(OpCodes.Ldarg_1));
        invoke.OpCode = OpCodes.Call;
        invoke.Operand = module.ImportReference(typeof(HostPluginBridge).GetMethod(nameof(HostPluginBridge.InvokeDrawingCallback))!);

        // Added observations can move a short branch beyond its signed-byte range.
        // Cecil preserves the chosen opcode, so widen them before serializing the IL.
        foreach (var method in new[] { fire, conditions, action, queue, callback,
            trigger.Methods.Single(m => m.Name == "CheckMatch"),
            module.GetType("Triggernometry.Core.Folder").Methods.Single(m => m.Name == "PassesFilter") }.OfType<MethodDefinition>())
        foreach (var instruction in method.Body.Instructions.Where(i => i.OpCode.OperandType == OperandType.ShortInlineBrTarget))
        {
            instruction.OpCode = instruction.OpCode.Code switch
            {
                Code.Br_S => OpCodes.Br, Code.Brfalse_S => OpCodes.Brfalse, Code.Brtrue_S => OpCodes.Brtrue,
                Code.Beq_S => OpCodes.Beq, Code.Bge_S => OpCodes.Bge, Code.Bge_Un_S => OpCodes.Bge_Un,
                Code.Bgt_S => OpCodes.Bgt, Code.Bgt_Un_S => OpCodes.Bgt_Un, Code.Ble_S => OpCodes.Ble,
                Code.Ble_Un_S => OpCodes.Ble_Un, Code.Blt_S => OpCodes.Blt, Code.Blt_Un_S => OpCodes.Blt_Un,
                Code.Bne_Un_S => OpCodes.Bne_Un, Code.Leave_S => OpCodes.Leave,
                _ => throw new NotSupportedException("Unknown short diagnostic branch: " + instruction.OpCode),
            };
        }
        Console.WriteLine("DACT_DRAW_DIAG_READY: schema=1 zones=1238,1363; bounded passive TN 2.2 observers installed.");

        IEnumerable<Instruction> Notification(ILProcessor il, int contextArg, string stage, VariableDefinition? result)
        {
            yield return il.Create(OpCodes.Ldarg_0);
            yield return il.Create(contextArg == 1 ? OpCodes.Ldarg_1 : OpCodes.Ldarg_2);
            yield return il.Create(OpCodes.Ldstr, stage);
            yield return result is null ? il.Create(OpCodes.Ldnull) : il.Create(OpCodes.Ldloc, result);
            if (result?.VariableType.IsValueType == true) yield return il.Create(OpCodes.Box, result.VariableType);
            yield return il.Create(OpCodes.Call, notify);
        }

        void ObserveReturns(MethodDefinition method, int contextArg, string stage)
        {
            var il = method.Body.GetILProcessor();
            VariableDefinition? value = null;
            if (method.ReturnType.MetadataType != MetadataType.Void)
            {
                value = new(method.ReturnType);
                method.Body.Variables.Add(value);
                method.Body.InitLocals = true;
            }
            foreach (var exit in method.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToArray())
            {
                // Mutate the existing return so every branch/leave to it sees the
                // observer; storing/reloading the result preserves the original stack.
                exit.OpCode = value is null ? OpCodes.Nop : OpCodes.Stloc;
                exit.Operand = value;
                var tail = exit;
                foreach (var instruction in Notification(il, contextArg, stage, value)
                    .Concat(value is null ? [] : new[] { il.Create(OpCodes.Ldloc, value) })
                    .Append(il.Create(OpCodes.Ret)))
                { il.InsertAfter(tail, instruction); tail = instruction; }
            }
        }

        void ObserveExceptions(MethodDefinition method, int contextArg)
        {
            var il = method.Body.GetILProcessor();
            foreach (var handler in method.Body.ExceptionHandlers.Where(h => h.HandlerType == ExceptionHandlerType.Catch && h.CatchType.FullName == "System.Exception"))
            {
                var error = new VariableDefinition(handler.CatchType);
                method.Body.Variables.Add(error);
                method.Body.InitLocals = true;
                var original = handler.HandlerStart;
                var first = il.Create(OpCodes.Dup);
                il.InsertBefore(original, first);
                il.InsertBefore(original, il.Create(OpCodes.Stloc, error));
                foreach (var instruction in Notification(il, contextArg, "exception", error)) il.InsertBefore(original, instruction);
                // Preserve the caught exception on the stack for the original handler.
                foreach (var boundary in method.Body.ExceptionHandlers)
                {
                    if (boundary.TryEnd == original) boundary.TryEnd = first;
                    if (boundary.HandlerEnd == original) boundary.HandlerEnd = first;
                }
                handler.HandlerStart = first;
            }
        }
    }
}

using Mono.Cecil;
using Mono.Cecil.Cil;

namespace DalamudActCompat.Host;

public static partial class LegacyAssemblyRewriter
{
    private static void WrapTriggernometryLogOrdering(ModuleDefinition module)
    {
        // Earlier TN versions use a different log transcriber. Leave that ABI untouched.
        if (module.GetType("Triggernometry.FFXIV.LogTranscribe.LogTranscriber") is null) return;
        var type = module.GetType("Triggernometry.Core.RealPlugin");
        var original = type.Methods.Single(method => method.Name == "OnLogLineRead" &&
            !method.IsStatic && method.Parameters.Count == 3 &&
            method.Parameters[0].ParameterType.MetadataType == MetadataType.Boolean &&
            method.Parameters[1].ParameterType.MetadataType == MetadataType.String &&
            method.Parameters[2].ParameterType.MetadataType == MetadataType.String);
        var wrapper = new MethodDefinition(original.Name, original.Attributes, original.ReturnType);
        foreach (var parameter in original.Parameters)
            wrapper.Parameters.Add(new(parameter.Name, parameter.Attributes, parameter.ParameterType));
        original.Name = "OnLogLineRead__DalamudActCompatOrdered";
        original.Attributes = (original.Attributes & ~MethodAttributes.MemberAccessMask) | MethodAttributes.Private;

        // Retarget delegates and direct calls, retaining the complete original implementation
        // as the replay target. Other ACT extensions continue receiving their logs immediately.
        foreach (var caller in module.Types.SelectMany(EnumerateTypes).SelectMany(t => t.Methods).Where(m => m.HasBody))
        foreach (var instruction in caller.Body.Instructions)
            if (instruction.Operand is MethodReference called && called.Module == module &&
                called.MetadataToken == original.MetadataToken)
                instruction.Operand = wrapper;

        type.Methods.Add(wrapper);
        ReplaceWithBridge(wrapper, module.ImportReference(typeof(HostPluginBridge).GetMethod(
            nameof(HostPluginBridge.ProcessTriggernometryLog))!), loadInstance: true, loadParameters: true);

        var execute = module.GetType("Triggernometry.Core.ActionOld").Methods.Single(method =>
            method.Name == "ExecutionImplementation" && method.Parameters.Count == 2);
        var notify = module.ImportReference(typeof(HostPluginBridge).GetMethod(
            nameof(HostPluginBridge.CompleteTriggernometryInitializationAction))!);
        var il = execute.Body.GetILProcessor();
        foreach (var exit in execute.Body.Instructions.Where(instruction => instruction.OpCode == OpCodes.Ret).ToArray())
        {
            // Branches to the existing return must also pass the completion observer.
            exit.OpCode = OpCodes.Ldarg_0;
            var context = il.Create(OpCodes.Ldarg_2);
            var call = il.Create(OpCodes.Call, notify);
            il.InsertAfter(exit, context);
            il.InsertAfter(context, call);
            il.InsertAfter(call, il.Create(OpCodes.Ret));
        }
    }
}

using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace DalamudActCompat.Host;

public static partial class LegacyAssemblyRewriter
{
    public static Assembly LoadSimulant(string assemblyPath, AssemblyLoadContext loadContext)
    {
        using var definition = AssemblyDefinition.ReadAssembly(assemblyPath);
        var module = definition.MainModule;
        if (definition.Name.Name != "Simulant")
        {
            throw new InvalidDataException("Expected the upstream Simulant assembly.");
        }

        // Preserve the upstream simulator and embedded CSVs, converting only legacy form
        // resources. BinaryFormatter stays disabled throughout the shared Host.
        ConvertLegacyFormResources(module);
        var host = module.GetType("Simulant.Core.PluginHost")
                   ?? throw new TypeLoadException("Simulant.Core.PluginHost");
        var interop = module.GetType("Simulant.ACT.XivPluginInterop")
                      ?? throw new TypeLoadException("Simulant.ACT.XivPluginInterop");
        var attach = host.Methods.Single(method => method.Name == "Attach");
        var pluginField = interop.Fields.Single(field => field.Name == "plugin");
        attach.Body = new Mono.Cecil.Cil.MethodBody(attach);
        var il = attach.Body.GetILProcessor();
        // ACT's status-label polling never terminates against DACT's facade. The upstream
        // process-change callback is empty; bind the existing identity synchronously instead.
        il.Emit(OpCodes.Call, module.ImportReference(typeof(SimulantCompatibility).GetMethod(
            nameof(SimulantCompatibility.GetFfxivPlugin))!));
        il.Emit(OpCodes.Castclass, pluginField.FieldType);
        il.Emit(OpCodes.Stsfld, pluginField);
        il.Emit(OpCodes.Ret);

        // Attach no longer subscribes to the facade's process event. Drop the matching
        // unsubscribe only, retaining upstream firewall restoration and the rest of Dispose.
        var unsubscribe = host.Methods.Single(method => method.Name == "Dispose")
            .Body.Instructions.Single(instruction => instruction.Operand is MethodReference called &&
                called.Name == "remove_ProcessChanged");
        unsubscribe.OpCode = OpCodes.Pop;
        unsubscribe.Operand = null;
        host.Methods.Single(method => method.Name == "Dispose").Body.GetILProcessor()
            .InsertAfter(unsubscribe, Instruction.Create(OpCodes.Pop));

        var accessCheck = module.ImportReference(typeof(SimulantCompatibility).GetMethod(
            nameof(SimulantCompatibility.EnsureNativeAccess))!);
        foreach (var method in new[]
                 {
                     module.GetType("Simulant.ACT.NamazuInterop").Methods.Single(m => m.Name == "Init"),
                     module.GetType("Simulant.Core.Firewall.FirewallService").Methods.Single(m => m.Name == "Enable"),
                 })
        {
            method.Body.GetILProcessor().InsertBefore(method.Body.Instructions[0],
                Instruction.Create(OpCodes.Call, accessCheck));
        }

        // Upstream marks initialization successful even with missing signatures. A failed
        // scan must keep the firewall and simulation controls disabled on a newer game build.
        var scan = module.GetType("Simulant.Game.SigAddressScanner").Methods.Single(m => m.Name == "Scan");
        var validate = module.ImportReference(typeof(SimulantCompatibility).GetMethod(
            nameof(SimulantCompatibility.ValidateSignatures))!);
        foreach (var instruction in scan.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToArray())
        {
            instruction.OpCode = OpCodes.Call;
            instruction.Operand = validate;
            scan.Body.GetILProcessor().InsertAfter(instruction, Instruction.Create(OpCodes.Ret));
        }

        // Keep the original entry backup intact for Disable, but relocate an existing
        // Dalamud/plugin jump when upstream copies those bytes into its send-hook cave.
        var firewall = module.GetType("Simulant.Core.Firewall.FirewallService");
        var enableFirewall = firewall.Methods.Single(method => method.Name == "Enable");
        enableFirewall.Body.GetILProcessor().InsertBefore(enableFirewall.Body.Instructions[0],
            Instruction.Create(OpCodes.Call, module.ImportReference(typeof(SimulantCompatibility)
                .GetMethod(nameof(SimulantCompatibility.ValidateSendHookEntry))!)));
        var sendHook = firewall.Methods.Single(method => method.Name == "SendHookEnable");
        var copiedEntry = sendHook.Body.Instructions.Single(instruction =>
            instruction.OpCode == OpCodes.Ldfld && instruction.Operand is FieldReference field &&
            field.Name == "_sendHookOriginal" && instruction.Next.Operand is MethodReference call && call.Name == "AddRange");
        var sendAddress = module.GetType("Simulant.Game.AddressStore").Properties.Single(p => p.Name == "OnSendPacketFuncPtr").GetMethod;
        var getAddress = Instruction.Create(OpCodes.Call, sendAddress);
        var prepareEntry = Instruction.Create(OpCodes.Call, module.ImportReference(typeof(SimulantCompatibility)
            .GetMethod(nameof(SimulantCompatibility.PrepareSendHookInstructions))!));
        sendHook.Body.GetILProcessor().InsertAfter(copiedEntry, getAddress);
        sendHook.Body.GetILProcessor().InsertAfter(getAddress, prepareEntry);

        using var output = new MemoryStream();
        definition.Write(output);
        output.Position = 0;
        return loadContext.LoadFromStream(output);
    }
}

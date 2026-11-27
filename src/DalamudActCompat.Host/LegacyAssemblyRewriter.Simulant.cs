using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace DalamudActCompat.Host;

public static partial class LegacyAssemblyRewriter
{
    // The newest upstream build whose firewall, unload and start code the patches below were
    // written against (docs/simulant-compatibility.md).
    internal static readonly Version LastAdaptedSimulantVersion = new(0, 4, 0, 0);

    // v0.1.0.0 rebuilt the firewall around per-hook records, subscribes to process and zone
    // changes, updates itself and makes native calls through PostNamazu's call scheduler.
    // Builds before it keep the patches they were verified with.
    private static readonly Version FirstHookRecordSimulantVersion = new(0, 1, 0, 0);
    private static readonly Version LastPostNamazuSimulantVersion = new(0, 1, 0, 0);

    // v0.4.0.0 replaced PostNamazu / Triggernometry entity interop with its own mixed-mode
    // SimuMagic assembly, disk EXE scanning, Lumina game data, and direct FFXIV_ACT_Plugin polling.
    private static readonly Version FirstStandaloneSimulantVersion = new(0, 4, 0, 0);

    public static Assembly LoadSimulant(string assemblyPath, AssemblyLoadContext loadContext)
    {
        using var output = RewriteSimulant(assemblyPath, loadContext);
        return loadContext.LoadFromStream(output);
    }

    /// <summary>
    /// Applies the Simulant patches and returns the rewritten assembly without loading it, so a
    /// new upstream build can be checked against them before any of its code runs.
    /// </summary>
    internal static MemoryStream RewriteSimulant(string assemblyPath)
        => RewriteSimulant(assemblyPath, loadContext: null);

    private static MemoryStream RewriteSimulant(string assemblyPath, AssemblyLoadContext? loadContext)
    {
        using var definition = AssemblyDefinition.ReadAssembly(assemblyPath);
        var module = definition.MainModule;
        if (definition.Name.Name != "Simulant")
        {
            throw new InvalidDataException("Expected the upstream Simulant assembly.");
        }

        // These patches edit upstream IL by its shape, so an unadapted build fails at an
        // arbitrary lookup with "Sequence contains no matching element". Say what is wrong
        // and what works instead; nothing of it is loaded.
        if (definition.Name.Version > LastAdaptedSimulantVersion ||
            (definition.Name.Version > LastPostNamazuSimulantVersion &&
             definition.Name.Version < FirstStandaloneSimulantVersion))
        {
            throw new NotSupportedException(GetUnsupportedSimulantMessage(definition.Name.Version));
        }

        // Preserve the upstream simulator and embedded resources, converting only legacy form
        // resources. BinaryFormatter stays disabled throughout the shared Host.
        ConvertLegacyFormResources(module);
        var host = module.GetType("Simulant.Core.PluginHost")
                   ?? throw new TypeLoadException("Simulant.Core.PluginHost");
        var interop = module.GetType("Simulant.ACT.XivPluginInterop")
                      ?? throw new TypeLoadException("Simulant.ACT.XivPluginInterop");
        if (definition.Name.Version >= FirstStandaloneSimulantVersion)
        {
            RewriteStandaloneSimulant(definition, module, host, interop, loadContext);
            var standaloneOutput = new MemoryStream();
            definition.Write(standaloneOutput);
            standaloneOutput.Position = 0;
            return standaloneOutput;
        }

        var hookRecords = definition.Name.Version >= FirstHookRecordSimulantVersion;
        var attach = host.Methods.Single(method => method.Name == "Attach");
        var pluginField = interop.Fields.Single(field => field.Name == "plugin");
        attach.Body = new Mono.Cecil.Cil.MethodBody(attach);
        var il = attach.Body.GetILProcessor();
        // Upstream waits for ACT's status label to read "STARTED" before it binds anything.
        // That label never changes against DACT's facade, so bind the identity directly.
        il.Emit(OpCodes.Call, module.ImportReference(typeof(SimulantCompatibility).GetMethod(
            nameof(SimulantCompatibility.GetFfxivPlugin))!));
        il.Emit(OpCodes.Castclass, pluginField.FieldType);
        il.Emit(OpCodes.Stsfld, pluginField);
        if (hookRecords)
        {
            // From v0.1.0.0 the same wait also subscribes to zone changes, which is how the
            // simulator learns that the addresses of its spawned entities and simulated party
            // went stale. The facade raises no such event; DACT delivers its own zone changes
            // to the upstream handler instead. Process changes need no bridge: a Host never
            // outlives the game process it was started for.
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, module.ImportReference(typeof(SimulantCompatibility).GetMethod(
                nameof(SimulantCompatibility.ObserveZoneChanges))!));
        }
        il.Emit(OpCodes.Ret);

        if (!hookRecords)
        {
            // Attach no longer subscribes to the facade's process event. Drop the matching
            // unsubscribe only, retaining upstream firewall restoration and the rest of Dispose.
            // (v0.1.0.0 unsubscribes only what it recorded as subscribed, which is nothing.)
            var unsubscribe = host.Methods.Single(method => method.Name == "Dispose")
                .Body.Instructions.Single(instruction => instruction.Operand is MethodReference called &&
                    called.Name == "remove_ProcessChanged");
            unsubscribe.OpCode = OpCodes.Pop;
            unsubscribe.Operand = null;
            host.Methods.Single(method => method.Name == "Dispose").Body.GetILProcessor()
                .InsertAfter(unsubscribe, Instruction.Create(OpCodes.Pop));
        }

        var accessCheck = module.ImportReference(typeof(SimulantCompatibility).GetMethod(
            nameof(SimulantCompatibility.EnsureNativeAccess))!);
        var namazuInit = module.GetType("Simulant.ACT.NamazuInterop").Methods.Single(m => m.Name == "Init");
        namazuInit.Body.GetILProcessor().InsertBefore(namazuInit.Body.Instructions[0],
            Instruction.Create(OpCodes.Call, accessCheck));

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

        var firewall = module.GetType("Simulant.Core.Firewall.FirewallService");
        var sendAddress = module.GetType("Simulant.Game.AddressStore").Properties.Single(p => p.Name == "OnSendPacketFuncPtr").GetMethod;
        if (hookRecords)
        {
            AcceptPostNamazuSnapshot(namazuInit);
            PatchHookRecordFirewall(module, firewall, sendAddress);
            DisableSimulantUpdater(module);
            RedirectSimulantConfiguration(module, host);
        }
        else
        {
            PatchLegacyFirewall(module, firewall, sendAddress);
        }

        // A selected preset alone used to start a session even back in the real inn.
        // Check both the completed simulated-zone state and the live territory first.
        var presetControl = module.GetType("Simulant.UI.PresetControl");
        var start = presetControl.Methods.Single(method => method.Name == "btnStart_Click");
        var startIl = start.Body.GetILProcessor();
        var first = start.Body.Instructions[0];
        foreach (var instruction in new[]
                 {
                     Instruction.Create(OpCodes.Ldarg_0),
                     Instruction.Create(OpCodes.Ldfld, presetControl.Fields.Single(field => field.Name == "_host")),
                     Instruction.Create(OpCodes.Ldarg_0),
                     Instruction.Create(OpCodes.Ldfld, presetControl.Fields.Single(field => field.Name == "_preset")),
                     Instruction.Create(OpCodes.Call, module.ImportReference(typeof(SimulantSimulationCompatibility).GetMethod(nameof(SimulantSimulationCompatibility.CanStart))!)),
                     Instruction.Create(OpCodes.Brtrue, first),
                     Instruction.Create(OpCodes.Ret),
                 }) startIl.InsertBefore(first, instruction);

        var zone = module.GetType("Simulant.Core.Zone.ZoneService");
        PatchZoneTraceLoad(module, zone);

        var entityPointers = module.GetType("Simulant.ACT.TriggernometryInterop").Methods
            .Single(method => method.Name == "GetEntityPtrs");
        foreach (var instruction in entityPointers.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToArray())
        {
            instruction.OpCode = OpCodes.Call;
            instruction.Operand = module.ImportReference(typeof(SimulantEntityCompatibility)
                .GetMethod(nameof(SimulantEntityCompatibility.UseLiveEntityPointers))!);
            entityPointers.Body.GetILProcessor().InsertAfter(instruction, Instruction.Create(OpCodes.Ret));
        }

        var output = new MemoryStream();
        definition.Write(output);
        output.Position = 0;
        return output;
    }

    // From v0.4.0.0: Simulant ships its own mixed-mode SimuMagic (renamed GreyMagic),
    // scans the game EXE on disk, loads Lumina game data, and reads entities directly from
    // GameObjectManager. It no longer requires PostNamazu or Triggernometry.
    private static void RewriteStandaloneSimulant(
        AssemblyDefinition definition,
        ModuleDefinition module,
        TypeDefinition host,
        TypeDefinition interop,
        AssemblyLoadContext? loadContext)
    {
        if (loadContext is not null)
        {
            PreloadMixedModeGreyMagicCompatibility(
                definition,
                loadContext,
                "costura64.simumagic.dll",
                "SimuMagic",
                "simulant-simumagic",
                "Simulant SimuMagic");
        }
        else
        {
            _ = RewriteMixedModeGreyMagicImage(
                definition,
                "costura64.simumagic.dll",
                "Simulant SimuMagic");
        }

        var pluginField = interop.Fields.Single(field => field.Name == "plugin");
        var refresh = host.Methods.Single(method => method.Name == "RefreshXivPlugin");
        refresh.Body = new Mono.Cecil.Cil.MethodBody(refresh);
        var refreshIl = refresh.Body.GetILProcessor();
        // Upstream checks PluginStarted and subscribes to DataSubscription events on FFXIV_ACT_Plugin.
        // DACT's shared facade does not start that plugin or wire DataSubscription for Simulant; bind
        // XivPluginInterop.plugin directly and observe DACT's zone-change stream once per PluginHost
        // while leaving _subscribedXivPlugin null so Dispose does not touch DataSubscription.
        refreshIl.Emit(OpCodes.Ldarg_0);
        refreshIl.Emit(OpCodes.Call, module.ImportReference(typeof(SimulantCompatibility).GetMethod(
            nameof(SimulantCompatibility.BindStandaloneFfxivPlugin))!));
        refreshIl.Emit(OpCodes.Castclass, pluginField.FieldType);
        refreshIl.Emit(OpCodes.Stsfld, pluginField);
        refreshIl.Emit(OpCodes.Ret);

        var readProcess = host.Methods.Single(method => method.Name == "ReadGameProcess");
        readProcess.Body = new Mono.Cecil.Cil.MethodBody(readProcess);
        var readProcessIl = readProcess.Body.GetILProcessor();
        readProcessIl.Emit(OpCodes.Call, module.ImportReference(typeof(SimulantCompatibility).GetMethod(
            nameof(SimulantCompatibility.GetStandaloneGameProcess))!));
        readProcessIl.Emit(OpCodes.Ret);

        // UpdateGameProcess reads _subscribedXivPlugin to seed _entityZoneId. Redirect that single
        // read to XivPluginInterop.plugin so _subscribedXivPlugin can remain null for Dispose.
        var updateProcess = host.Methods.Single(method => method.Name == "UpdateGameProcess");
        var subscribedReads = updateProcess.Body.Instructions
            .Where(instruction => instruction.OpCode == OpCodes.Ldfld &&
                                  instruction.Operand is FieldReference { Name: "_subscribedXivPlugin" })
            .ToArray();
        if (subscribedReads.Length != 1 || subscribedReads[0].Previous?.OpCode != OpCodes.Ldarg_0)
        {
            throw new InvalidDataException("Unexpected Simulant UpdateGameProcess plugin field layout.");
        }

        subscribedReads[0].Previous.OpCode = OpCodes.Nop;
        subscribedReads[0].OpCode = OpCodes.Ldsfld;
        subscribedReads[0].Operand = pluginField;

        var retry = host.Methods.Single(method => method.Name == "RetryGameConnection");
        var retryIl = retry.Body.GetILProcessor();
        var retryFirst = retry.Body.Instructions[0];
        retryIl.InsertBefore(retryFirst, Instruction.Create(OpCodes.Ldarg_0));
        retryIl.InsertBefore(retryFirst, Instruction.Create(OpCodes.Call, module.ImportReference(
            typeof(SimulantCompatibility).GetMethod(nameof(SimulantCompatibility.WarnIfStandaloneNativeAccessDenied))!)));

        var gameInterop = module.GetType("Simulant.Game.GameInterop")
                          ?? throw new TypeLoadException("Simulant.Game.GameInterop");
        var connect = gameInterop.Methods.Single(method => method.Name == "Connect");
        connect.Body.GetILProcessor().InsertBefore(
            connect.Body.Instructions[0],
            Instruction.Create(OpCodes.Call, module.ImportReference(typeof(SimulantCompatibility).GetMethod(
                nameof(SimulantCompatibility.EnsureStandaloneNativeAccess))!)));

        var firewall = module.GetType("Simulant.Core.Firewall.FirewallService")
                       ?? throw new TypeLoadException("Simulant.Core.Firewall.FirewallService");
        var sendAddress = module.GetType("Simulant.Game.AddressStore")!
            .Properties.Single(p => p.Name == "OnSendPacketFuncPtr").GetMethod;
        PatchHookRecordFirewall(module, firewall, sendAddress, standalone: true);
        DisableSimulantUpdater(module);
        RedirectSimulantConfiguration(module, host);
        PatchZoneTraceLoad(module, module.GetType("Simulant.Core.Zone.ZoneService")!);

        var networkLog = module.GetType("Simulant.ACT.XivNetworkLog")
                         ?? throw new TypeLoadException("Simulant.ACT.XivNetworkLog");
        var writeLine = networkLog.Methods.Single(method => method.Name == "WriteLine");
        writeLine.Body = new Mono.Cecil.Cil.MethodBody(writeLine);
        var writeLineIl = writeLine.Body.GetILProcessor();
        writeLineIl.Emit(OpCodes.Ldarg_0);
        writeLineIl.Emit(OpCodes.Ldarg_1);
        writeLineIl.Emit(OpCodes.Ldarg_2);
        writeLineIl.Emit(OpCodes.Call, module.ImportReference(typeof(SimulantCompatibility).GetMethod(
            nameof(SimulantCompatibility.WriteActNetworkLog))!));
        writeLineIl.Emit(OpCodes.Ret);
    }

    private static void PatchZoneTraceLoad(ModuleDefinition module, TypeDefinition zone)
    {
        var enter = zone.Methods.Single(method => method.Name == "TryEnterTerritory");
        var enterIl = enter.Body.GetILProcessor();
        var enterFirst = enter.Body.Instructions[0];
        foreach (var instruction in new[]
                 {
                     Instruction.Create(OpCodes.Ldarg_0),
                     Instruction.Create(OpCodes.Ldfld, zone.Fields.Single(field => field.Name == "_host")),
                     Instruction.Create(OpCodes.Ldarg_1),
                     Instruction.Create(OpCodes.Call, module.ImportReference(typeof(SimulantSimulationCompatibility).GetMethod(nameof(SimulantSimulationCompatibility.TraceLoad))!)),
                 }) enterIl.InsertBefore(enterFirst, instruction);
    }

    // Up to v0.0.4.2: the firewall keeps its state in fields of the service itself.
    private static void PatchLegacyFirewall(ModuleDefinition module, TypeDefinition firewall, MethodDefinition sendAddress)
    {
        // Keep the original entry backup intact for Disable, but relocate an existing
        // Dalamud/plugin jump when upstream copies those bytes into its send-hook cave.
        var enableFirewall = firewall.Methods.Single(method => method.Name == "Enable");
        var sendHook = firewall.Methods.Single(method => method.Name == "SendHookEnable");
        var copiedEntry = sendHook.Body.Instructions.Single(instruction =>
            instruction.OpCode == OpCodes.Ldfld && instruction.Operand is FieldReference field &&
            field.Name == "_sendHookOriginal" && instruction.Next.Operand is MethodReference call && call.Name == "AddRange");
        var getAddress = Instruction.Create(OpCodes.Call, sendAddress);
        var prepareEntry = Instruction.Create(OpCodes.Call, module.ImportReference(typeof(SimulantCompatibility)
            .GetMethod(nameof(SimulantCompatibility.PrepareSendHookInstructions))!));
        sendHook.Body.GetILProcessor().InsertAfter(copiedEntry, getAddress);
        sendHook.Body.GetILProcessor().InsertAfter(getAddress, prepareEntry);

        var allocation = sendHook.Body.Instructions.Single(instruction => instruction.Operand is MethodReference called && called.Name == "AllocateMemory");
        allocation.OpCode = OpCodes.Call;
        allocation.Operand = module.ImportReference(typeof(SimulantCompatibility).GetMethod(nameof(SimulantCompatibility.AllocateSendHookMemory))!);

        // The upstream 15-byte patch overlaps a Dalamud trampoline returning at +10.
        // Keep its saved backup and cave logic, but write only a five-byte near jump.
        var entryWrite = sendHook.Body.Instructions.Last(instruction => instruction.Operand is MethodReference called && called.Name == "WriteBytes");
        var patchArray = entryWrite.Previous.Previous.Previous;
        if (patchArray.Operand is not MethodReference { Name: "ToArray" })
            throw new InvalidDataException("Unexpected Simulant send entry patch layout.");
        var entryAddress = Instruction.Create(OpCodes.Call, sendAddress);
        sendHook.Body.GetILProcessor().InsertAfter(patchArray, entryAddress);
        sendHook.Body.GetILProcessor().InsertAfter(entryAddress, Instruction.Create(OpCodes.Call,
            module.ImportReference(typeof(SimulantCompatibility).GetMethod(nameof(SimulantCompatibility.PrepareSendEntryPatch))!)));

        enableFirewall.Body = new Mono.Cecil.Cil.MethodBody(enableFirewall);
        var enableIl = enableFirewall.Body.GetILProcessor();
        enableIl.Emit(OpCodes.Ldarg_0);
        enableIl.Emit(OpCodes.Call, module.ImportReference(typeof(SimulantCompatibility).GetMethod(nameof(SimulantCompatibility.EnableFirewall))!));
        enableIl.Emit(OpCodes.Ret);
    }

    // From v0.1.0.0: each hook is a HookState record and SendHookEnableCore builds the cave.
    // Upstream now rolls a failed Enable back itself, so only the three places that touch the
    // send entry are adjusted; they do the same as the legacy patches above. Upstream still
    // copies the first 15 bytes into its cave and overwrites all 15 at the entry, which breaks
    // when Dalamud or another plugin already hooked that function.
    private static void PatchHookRecordFirewall(
        ModuleDefinition module,
        TypeDefinition firewall,
        MethodDefinition sendAddress,
        bool standalone = false)
    {
        var sendHook = firewall.Methods.Single(method => method.Name == "SendHookEnableCore");
        var sendIl = sendHook.Body.GetILProcessor();
        // cave.AddRange(state.Original): relocate a jump that is already at the entry.
        var copiedEntry = sendHook.Body.Instructions.Single(instruction =>
            instruction.OpCode == OpCodes.Ldfld && instruction.Operand is FieldReference { Name: "Original" } &&
            instruction.Next.Operand is MethodReference { Name: "AddRange" });
        var getAddress = Instruction.Create(OpCodes.Call, sendAddress);
        sendIl.InsertAfter(copiedEntry, getAddress);
        sendIl.InsertAfter(getAddress, Instruction.Create(OpCodes.Call, module.ImportReference(typeof(SimulantCompatibility)
            .GetMethod(standalone
                ? nameof(SimulantCompatibility.PrepareStandaloneSendHookInstructions)
                : nameof(SimulantCompatibility.PrepareSendHookInstructions))!)));

        // state.Memory.AllocateMemory(0x100): the cave has to be within reach of a near jump.
        var allocation = sendHook.Body.Instructions.Single(instruction => instruction.Operand is MethodReference { Name: "AllocateMemory" });
        allocation.OpCode = OpCodes.Call;
        allocation.Operand = module.ImportReference(typeof(SimulantCompatibility).GetMethod(standalone
            ? nameof(SimulantCompatibility.AllocateStandaloneSendHookMemory)
            : nameof(SimulantCompatibility.AllocateSendHookMemory))!);

        // WriteExact(state, target, patch.ToArray()): five bytes at the entry instead of 15.
        // WriteExact compares the count written with the array it was given, so it agrees.
        var entryWrite = sendHook.Body.Instructions.Last(instruction => instruction.Operand is MethodReference { Name: "WriteExact" });
        var patchArray = entryWrite.Previous;
        if (patchArray.Operand is not MethodReference { Name: "ToArray" })
            throw new InvalidDataException("Unexpected Simulant send entry patch layout.");
        var entryAddress = Instruction.Create(OpCodes.Call, sendAddress);
        sendIl.InsertAfter(patchArray, entryAddress);
        sendIl.InsertAfter(entryAddress, Instruction.Create(OpCodes.Call,
            module.ImportReference(typeof(SimulantCompatibility).GetMethod(standalone
                ? nameof(SimulantCompatibility.PrepareStandaloneSendEntryPatch)
                : nameof(SimulantCompatibility.PrepareSendEntryPatch))!)));

        // Enable installs the receive hook first. Refuse an entry the relocation cannot
        // handle before anything is written, rather than rely on the rollback afterwards.
        var enable = firewall.Methods.Single(method => method.Name == "Enable" && method.Parameters.Count == 0);
        enable.Body.GetILProcessor().InsertBefore(enable.Body.Instructions[0], Instruction.Create(OpCodes.Call,
            module.ImportReference(typeof(SimulantCompatibility).GetMethod(standalone
                ? nameof(SimulantCompatibility.ValidateStandaloneSendHookEntry)
                : nameof(SimulantCompatibility.ValidateSendHookEntry))!)));
    }

    // Upstream requires PostNamazu 1.3.6.7, a version number no official release carries yet.
    // DACT bundles a build of upstream master that has the call scheduler this is really
    // about and still declares 1.3.6.6 (tools/build-postnamazu-snapshot.ps1). Accept that
    // number; a PostNamazu without the scheduler is still refused right after, by upstream's
    // own lookup of the CallScheduler field.
    private static void AcceptPostNamazuSnapshot(MethodDefinition namazuInit)
    {
        var minimum = namazuInit.Body.Instructions.Single(instruction =>
            instruction.OpCode == OpCodes.Newobj &&
            instruction.Operand is MethodReference { DeclaringType.FullName: "System.Version", Parameters.Count: 4 });
        var revision = minimum.Previous;
        if (revision.OpCode != OpCodes.Ldc_I4_7 ||
            revision.Previous.OpCode != OpCodes.Ldc_I4_6 ||
            revision.Previous.Previous.OpCode != OpCodes.Ldc_I4_3 ||
            revision.Previous.Previous.Previous.OpCode != OpCodes.Ldc_I4_1)
        {
            throw new InvalidDataException("Unexpected Simulant PostNamazu version requirement.");
        }

        revision.OpCode = OpCodes.Ldc_I4_6;
    }

    // v0.1.0.0 checks GitHub on every start and, by default, replaces its own DLL in place.
    // Under DACT that file is the copy the user imported and granted native memory access to,
    // and a newer build is refused by the version check above until DACT is adapted to it.
    // The check and the manual install both go through RunAsync; make it do nothing.
    private static void DisableSimulantUpdater(ModuleDefinition module)
    {
        var run = module.GetType("Simulant.Core.PluginUpdater").Methods.Single(method =>
            method.Name == "RunAsync" && method.Parameters.Count == 1 &&
            method.ReturnType.FullName == "System.Threading.Tasks.Task");
        var completed = new MethodReference("get_CompletedTask", run.ReturnType, run.ReturnType) { HasThis = false };
        run.Body = new Mono.Cecil.Cil.MethodBody(run);
        var il = run.Body.GetILProcessor();
        il.Emit(OpCodes.Call, completed);
        il.Emit(OpCodes.Ret);
    }

    // v0.1.0.0 keeps its settings under Application.StartupPath. In a Host that is the folder
    // of one DACT version, so they would be lost with every DACT update.
    private static void RedirectSimulantConfiguration(ModuleDefinition module, TypeDefinition host)
    {
        var startupPaths = host.Methods.Where(method => method.IsConstructor && method.HasBody)
            .SelectMany(method => method.Body.Instructions)
            .Where(instruction => instruction.Operand is MethodReference
            {
                Name: "get_StartupPath", DeclaringType.FullName: "System.Windows.Forms.Application",
            })
            .ToArray();
        if (startupPaths.Length != 1)
            throw new InvalidDataException("Unexpected Simulant configuration path layout.");
        startupPaths[0].Operand = module.ImportReference(typeof(SimulantCompatibility).GetMethod(
            nameof(SimulantCompatibility.GetConfigurationRoot))!);
    }

    internal static string GetUnsupportedSimulantMessage(Version version)
        => $"DACT 目前适配到仿生石 v{LastAdaptedSimulantVersion}，导入的是 v{version}。" +
           "更新的版本 DACT 尚未适配，所以没有加载它。" +
           $"请先删除这个扩展，重新导入 v{LastAdaptedSimulantVersion} 的 Simulant.dll。";
}

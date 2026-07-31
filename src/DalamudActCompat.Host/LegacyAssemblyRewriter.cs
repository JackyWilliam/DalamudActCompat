using System.Collections;
using System.ComponentModel;
using System.Drawing;
using System.Formats.Nrbf;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Resources;
using System.Runtime.Loader;
using System.Runtime.Serialization;
using System.Windows.Forms;
using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Resources.Extensions;

namespace DalamudActCompat.Host;

public static class LegacyAssemblyRewriter
{
    public static Assembly LoadTriggernometry(
        string assemblyPath,
        AssemblyLoadContext loadContext)
    {
        var outer = loadContext.LoadFromAssemblyPath(assemblyPath);
        PreloadTriggernometryScriptingAssemblies(outer, loadContext);
        const string implementationResource = "costura.triggernometryplugin.dll.compressed";
        using var compressed = outer.GetManifestResourceStream(implementationResource)
                               ?? throw new MissingManifestResourceException(
                                   $"Triggernometry implementation resource {implementationResource} is missing.");
        using var deflate = new DeflateStream(compressed, CompressionMode.Decompress);
        using var implementation = new MemoryStream();
        deflate.CopyTo(implementation);
        implementation.Position = 0;
        using var patched = RewriteTriggernometryImplementation(implementation);
        _ = loadContext.LoadFromStream(patched);
        return outer;
    }

    public static Assembly LoadPostNamazu(
        string assemblyPath,
        AssemblyLoadContext loadContext)
    {
        using var input = File.OpenRead(assemblyPath);
        using var definition = AssemblyDefinition.ReadAssembly(input);
        var module = definition.MainModule;
        var bridgeType = typeof(HostPluginBridge);
        var setClipboard = module.ImportReference(
            bridgeType.GetMethod(nameof(HostPluginBridge.SetClipboardText))!);
        var getClipboard = module.ImportReference(
            bridgeType.GetMethod(nameof(HostPluginBridge.GetClipboardText))!);
        var copyPostNamazuLog = module.ImportReference(
            bridgeType.GetMethod(nameof(HostPluginBridge.CopyPostNamazuLog))!);
        var attach = module.ImportReference(
            bridgeType.GetMethod(nameof(HostPluginBridge.AttachPostNamazu))!);
        var skipMonitor = module.ImportReference(
            bridgeType.GetMethod(nameof(HostPluginBridge.SkipLegacyProcessMonitoring))!);
        var command = module.ImportReference(
            bridgeType.GetMethod(nameof(HostPluginBridge.SendPostNamazuCommand))!);
        var networkAllowed = module.ImportReference(
            bridgeType.GetMethod(nameof(HostPluginBridge.IsPostNamazuNetworkAllowed))!);
        var unsupported = module.ImportReference(
            bridgeType.GetMethods().Single(method =>
                method.Name == nameof(HostPluginBridge.UnsupportedNativeOperation) &&
                !method.IsGenericMethod));
        var unsupportedGeneric = module.ImportReference(
            bridgeType.GetMethods().Single(method =>
                method.Name == nameof(HostPluginBridge.UnsupportedNativeOperation) &&
                method.IsGenericMethod));
        var copyLogPatched = false;

        foreach (var type in module.Types.SelectMany(EnumerateTypes))
        {
            foreach (var method in type.Methods.Where(method => method.HasBody))
            {
                if (type.FullName == "PostNamazu.PostNamazu" && method.Name == "Attach")
                {
                    ReplaceWithBridge(method, attach, loadInstance: true, loadParameters: false);
                    continue;
                }

                if (type.FullName == "PostNamazu.PostNamazuUi" &&
                    method.Name == "CopyLog" &&
                    method.Parameters.Count == 1 &&
                    method.Parameters[0].ParameterType.MetadataType == MetadataType.Boolean)
                {
                    var listField = type.Fields.Single(field => field.Name == "lstMessages");
                    method.Body.ExceptionHandlers.Clear();
                    method.Body.Variables.Clear();
                    method.Body.Instructions.Clear();
                    method.Body.InitLocals = false;
                    var il = method.Body.GetILProcessor();
                    il.Append(il.Create(OpCodes.Ldarg_0));
                    il.Append(il.Create(OpCodes.Ldfld, listField));
                    il.Append(il.Create(OpCodes.Ldarg_1));
                    il.Append(il.Create(OpCodes.Call, copyPostNamazuLog));
                    il.Append(il.Create(OpCodes.Ret));
                    copyLogPatched = true;
                    continue;
                }

                if (type.FullName == "PostNamazu.Common.ProcessManager" &&
                    method.Name == "StartProcessMonitoring")
                {
                    ReplaceWithBridge(method, skipMonitor, loadInstance: true, loadParameters: false);
                    continue;
                }

                if (type.FullName == "PostNamazu.PostNamazu" &&
                    method.Name == "ServerStart")
                {
                    InsertBooleanGuard(method, networkAllowed, returnCompletedTask: false);
                }

                if (type.FullName == "PostNamazu.Actions.Command" &&
                    method.Name == "DoTextCommand" &&
                    method.Parameters.Count == 1)
                {
                    ReplaceWithBridge(method, command, loadInstance: false, loadParameters: true);
                    continue;
                }

                if ((type.FullName == "PostNamazu.PostNamazu" &&
                     method.Name is "Call" or "DirectCall" or "ExecuteInFrameLock") ||
                    (type.FullName == "PostNamazu.Actions.NamazuModule" &&
                     method.Name == "ExecuteWithLock"))
                {
                    var target = method.ReturnType.MetadataType == MetadataType.Void
                        ? unsupported
                        : MakeGenericMethod(unsupportedGeneric, [method.ReturnType]);
                    ReplaceWithBridge(method, target, loadInstance: false, loadParameters: false);
                    continue;
                }

                foreach (var instruction in method.Body.Instructions)
                {
                    if (instruction.Operand is not MethodReference called)
                    {
                        continue;
                    }

                    if (called.DeclaringType.FullName == typeof(Clipboard).FullName)
                    {
                        if (called.Name == nameof(Clipboard.SetText) && called.Parameters.Count == 1)
                        {
                            instruction.OpCode = OpCodes.Call;
                            instruction.Operand = setClipboard;
                        }
                        else if (called.Name == nameof(Clipboard.GetText) &&
                                 called.Parameters.Count == 0)
                        {
                            instruction.OpCode = OpCodes.Call;
                            instruction.Operand = getClipboard;
                        }
                    }
                }
            }
        }

        if (!copyLogPatched)
        {
            throw new InvalidOperationException(
                "PostNamazu CopyLog shape changed; asynchronous clipboard adapter was not applied.");
        }

        using var output = new MemoryStream();
        definition.Write(output);
        output.Position = 0;
        return loadContext.LoadFromStream(output);
    }

    private static MemoryStream RewriteTriggernometryImplementation(Stream implementation)
    {
        using var definition = AssemblyDefinition.ReadAssembly(implementation);
        var module = definition.MainModule;
        var resources = module.Resources
            .OfType<EmbeddedResource>()
            .Where(resource => resource.Name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (var resource in resources)
        {
            using var input = resource.GetResourceStream();
            using var reader = new ResourceReader(input);
            using var output = new MemoryStream();
            using (var writer = new PreserializedResourceWriter(output))
            {
                IDictionaryEnumerator entries = reader.GetEnumerator();
                while (entries.MoveNext())
                {
                    var key = (string)entries.Key;
                    reader.GetResourceData(key, out var typeName, out var payload);
                    if (typeName.StartsWith("ResourceTypeCode.", StringComparison.Ordinal))
                    {
                        writer.AddResource(key, entries.Value);
                    }
                    else
                    {
                        WriteConvertedResource(writer, key, DeserializeLegacyPayload(payload));
                    }
                }
            }

            var index = module.Resources.IndexOf(resource);
            module.Resources[index] = new EmbeddedResource(
                resource.Name,
                resource.Attributes,
                output.ToArray());
        }

        RedirectResourceManagerCalls(module);
        PatchTriggernometryCompatibility(module);
        var patched = new MemoryStream();
        definition.Write(patched);
        patched.Position = 0;
        return patched;
    }

    private static void PatchTriggernometryCompatibility(ModuleDefinition module)
    {
        var bridgeType = typeof(HostPluginBridge);
        var admin = module.ImportReference(
            bridgeType.GetMethod(nameof(HostPluginBridge.CheckTriggernometryAdministratorCapability))!);
        var enqueueGeneric = module.ImportReference(
            bridgeType.GetMethod(nameof(HostPluginBridge.EnqueueTriggerEventBounded))!);
        var unstoppable = module.ImportReference(
            bridgeType.GetMethod(nameof(HostPluginBridge.ReportUnstoppableTriggernometryThread))!);
        var networkAllowed = module.ImportReference(
            bridgeType.GetMethod(nameof(HostPluginBridge.IsTriggernometryNetworkAllowed))!);
        var scriptAllowed = module.ImportReference(
            bridgeType.GetMethod(nameof(HostPluginBridge.IsTriggernometryHighRiskScriptAllowed))!);
        var subscribeZoneChanges = module.ImportReference(
            bridgeType.GetMethod(nameof(HostPluginBridge.SubscribeTriggernometryZoneChanges))!);
        var unsubscribeZoneChanges = module.ImportReference(
            bridgeType.GetMethod(nameof(HostPluginBridge.UnsubscribeTriggernometryZoneChanges))!);
        var startProcessByName = module.ImportReference(
            bridgeType.GetMethod(
                nameof(HostPluginBridge.StartTriggernometryProcess),
                [typeof(string)])!);
        var startProcessByNameAndArguments = module.ImportReference(
            bridgeType.GetMethod(
                nameof(HostPluginBridge.StartTriggernometryProcess),
                [typeof(string), typeof(string)])!);
        var startProcessByInfo = module.ImportReference(
            bridgeType.GetMethod(
                nameof(HostPluginBridge.StartTriggernometryProcess),
                [typeof(System.Diagnostics.ProcessStartInfo)])!);
        var adminMethod = module.Types
            .SelectMany(EnumerateTypes)
            .SelectMany(type => type.Methods)
            .Single(method =>
                method.Name == "CheckIfAdministrator" &&
                method.ReturnType.MetadataType == MetadataType.Boolean &&
                method.Parameters.Count == 1 &&
                method.Parameters[0].ParameterType.MetadataType == MetadataType.Boolean);
        ReplaceWithBridge(adminMethod, admin, loadInstance: false, loadParameters: true);

        var ffxivBridge = module.Types
            .SelectMany(EnumerateTypes)
            .Single(type => type.FullName == "Triggernometry.PluginBridges.BridgeFFXIV");
        var subscribeZoneMethod = ffxivBridge.Methods.Single(method =>
            method.Name == "SubscribeToZoneChanged" && method.Parameters.Count == 1);
        var unsubscribeNetworkMethod = ffxivBridge.Methods.Single(method =>
            method.Name == "UnsubscribeFromNetworkEvents" && method.Parameters.Count == 1);
        ReplaceWithBridge(
            subscribeZoneMethod,
            subscribeZoneChanges,
            loadInstance: false,
            loadParameters: true);
        ReplaceWithBridge(
            unsubscribeNetworkMethod,
            unsubscribeZoneChanges,
            loadInstance: false,
            loadParameters: true);

        var repositoryUpdate = module.Types
            .SelectMany(EnumerateTypes)
            .SelectMany(type => type.Methods)
            .Single(method =>
                method.Name == "UpdateAllRepositoriesAsync" &&
                method.Parameters.Count == 1 &&
                method.ReturnType.FullName == typeof(Task).FullName);
        InsertBooleanGuard(repositoryUpdate, networkAllowed, returnCompletedTask: true);
        var endpointStart = module.Types
            .SelectMany(EnumerateTypes)
            .Single(type => type.FullName == "Triggernometry.Core.Endpoint")
            .Methods
            .Single(method => method.Name == "Start" && method.Parameters.Count == 0);
        InsertBooleanGuard(endpointStart, networkAllowed, returnCompletedTask: false);
        var scriptSecurity = module.Types
            .SelectMany(EnumerateTypes)
            .Single(type =>
                type.FullName == "Triggernometry.Core.Scripting.ScriptSecurity")
            .Methods
            .Single(method =>
                method.Name == "IsFeatureAllowedByConfig" &&
                method.ReturnType.MetadataType == MetadataType.Boolean);
        InsertBooleanGuard(scriptSecurity, scriptAllowed, returnCompletedTask: false);

        var enqueueCount = 0;
        var abortCount = 0;
        var processStartCount = 0;
        foreach (var instruction in module.Types
                     .SelectMany(EnumerateTypes)
                     .SelectMany(type => type.Methods)
                     .Where(method => method.HasBody)
                     .SelectMany(method => method.Body.Instructions))
        {
            if (instruction.Operand is MethodReference
                {
                    Name: "Enqueue",
                    DeclaringType: GenericInstanceType queueType,
                } &&
                queueType.ElementType.FullName == "System.Collections.Generic.Queue`1" &&
                queueType.GenericArguments is [{ FullName: "Triggernometry.Core.LogEvent" } argument])
            {
                instruction.OpCode = OpCodes.Call;
                instruction.Operand = MakeGenericMethod(enqueueGeneric, [argument]);
                enqueueCount++;
                continue;
            }

            if (instruction.Operand is MethodReference
                {
                    Name: nameof(Thread.Abort),
                    DeclaringType.FullName: "System.Threading.Thread",
                })
            {
                instruction.OpCode = OpCodes.Call;
                instruction.Operand = unstoppable;
                abortCount++;
                continue;
            }

            if (instruction.Operand is MethodReference
                {
                    Name: nameof(System.Diagnostics.Process.Start),
                    DeclaringType.FullName: "System.Diagnostics.Process",
                } processStart)
            {
                MethodReference? replacement = processStart.Parameters.Count switch
                {
                    1 when processStart.Parameters[0].ParameterType.FullName ==
                               typeof(string).FullName
                        => startProcessByName,
                    2 when processStart.Parameters[0].ParameterType.FullName ==
                               typeof(string).FullName &&
                           processStart.Parameters[1].ParameterType.FullName ==
                               typeof(string).FullName
                        => startProcessByNameAndArguments,
                    1 when processStart.Parameters[0].ParameterType.FullName ==
                               typeof(System.Diagnostics.ProcessStartInfo).FullName
                        => startProcessByInfo,
                    _ => null,
                };
                if (replacement is not null)
                {
                    instruction.OpCode = OpCodes.Call;
                    instruction.Operand = replacement;
                    processStartCount++;
                }
            }
        }

        if (enqueueCount != 2 || abortCount != 3 || processStartCount == 0)
        {
            throw new InvalidOperationException(
                "Unexpected Triggernometry patch shape: " +
                $"enqueue={enqueueCount}, abort={abortCount}, process={processStartCount}.");
        }
    }

    private static void PreloadTriggernometryScriptingAssemblies(
        Assembly outer,
        AssemblyLoadContext loadContext)
    {
        string[] resources =
        [
            "costura.microsoft.codeanalysis.dll.compressed",
            "costura.microsoft.codeanalysis.scripting.dll.compressed",
            "costura.microsoft.codeanalysis.csharp.dll.compressed",
            "costura.microsoft.codeanalysis.csharp.scripting.dll.compressed",
        ];
        foreach (var resourceName in resources)
        {
            using var compressed = outer.GetManifestResourceStream(resourceName)
                                   ?? throw new MissingManifestResourceException(resourceName);
            using var deflate = new DeflateStream(compressed, CompressionMode.Decompress);
            using var dependency = new MemoryStream();
            deflate.CopyTo(dependency);
            dependency.Position = 0;
            using var definition = AssemblyDefinition.ReadAssembly(dependency);
            if (loadContext.Assemblies.Any(candidate => string.Equals(
                    candidate.GetName().Name,
                    definition.Name.Name,
                    StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            dependency.Position = 0;
            loadContext.LoadFromStream(dependency);
        }
    }

    private static object DeserializeLegacyPayload(byte[] payload)
    {
        using var stream = new MemoryStream(payload, writable: false);
        var record = NrbfDecoder.DecodeClassRecord(stream, leaveOpen: true);
        if (record.TypeNameMatches(typeof(ImageListStreamer)))
        {
            return ReadByteArray(record, "Data");
        }

        if (record.TypeNameMatches(typeof(Bitmap)))
        {
            using var imageStream = new MemoryStream(ReadByteArray(record, "Data"), writable: false);
            using var bitmap = new Bitmap(imageStream);
            return new Bitmap(bitmap);
        }

        if (record.TypeNameMatches(typeof(Point)))
        {
            return new Point(record.GetInt32("x"), record.GetInt32("y"));
        }

        throw new NotSupportedException($"Legacy resource type {record.TypeName} is not allowed.");
    }

    private static byte[] ReadByteArray(ClassRecord record, string memberName)
    {
        var array = record.GetArrayRecord(memberName)
                    ?? throw new InvalidDataException($"{record.TypeName}/{memberName} has no array.");
        if (array.Lengths.Length != 1 || array.Lengths[0] > 64 * 1024 * 1024)
        {
            throw new InvalidDataException("Legacy resource byte array length is invalid.");
        }

        return (byte[])array.GetArray(typeof(byte[]), allowNulls: false);
    }

    private static void WriteConvertedResource(
        PreserializedResourceWriter writer,
        string key,
        object value)
    {
        var type = value.GetType();
        var typeName = type.AssemblyQualifiedName
                       ?? throw new InvalidOperationException($"{type.FullName} has no qualified name.");
        var converter = TypeDescriptor.GetConverter(type);
        if (value is byte[] imageListData &&
            key.EndsWith(".ImageStream", StringComparison.Ordinal))
        {
            writer.AddResource(key, imageListData);
            return;
        }

        if (converter.CanConvertTo(typeof(byte[])) && converter.CanConvertFrom(typeof(byte[])))
        {
            var bytes = converter.ConvertTo(
                            null,
                            CultureInfo.InvariantCulture,
                            value,
                            typeof(byte[])) as byte[]
                        ?? throw new InvalidOperationException($"Converter for {typeName} returned null.");
            writer.AddTypeConverterResource(key, bytes, typeName);
            return;
        }

        if (converter.CanConvertTo(typeof(string)) && converter.CanConvertFrom(typeof(string)))
        {
            writer.AddResource(
                key,
                converter.ConvertToInvariantString(value)
                ?? throw new InvalidOperationException($"Converter for {typeName} returned null."),
                typeName);
            return;
        }

        throw new NotSupportedException($"Resource {key}/{typeName} cannot be converted safely.");
    }

    public static object? GetResourceObject(ResourceManager manager, string key)
    {
        var value = manager.GetObject(key);
        if (value is not byte[] data || !key.EndsWith(".ImageStream", StringComparison.Ordinal))
        {
            return value;
        }

        var type = typeof(ImageListStreamer);
#pragma warning disable SYSLIB0050
        var info = new SerializationInfo(type, new FormatterConverter());
#pragma warning restore SYSLIB0050
        info.AddValue("Data", data, typeof(byte[]));
        var constructor = type.GetConstructor(
                              BindingFlags.Instance | BindingFlags.NonPublic,
                              null,
                              [typeof(SerializationInfo), typeof(StreamingContext)],
                              null)
                          ?? throw new MissingMethodException(type.FullName, ".ctor");
#pragma warning disable SYSLIB0050
        return constructor.Invoke([info, new StreamingContext(StreamingContextStates.All)]);
#pragma warning restore SYSLIB0050
    }

    private static void RedirectResourceManagerCalls(ModuleDefinition module)
    {
        var replacement = module.ImportReference(
            typeof(LegacyAssemblyRewriter).GetMethod(
                nameof(GetResourceObject),
                BindingFlags.Public | BindingFlags.Static)!);
        foreach (var instruction in module.Types
                     .SelectMany(EnumerateTypes)
                     .SelectMany(type => type.Methods)
                     .Where(method => method.HasBody)
                     .SelectMany(method => method.Body.Instructions))
        {
            if (instruction.OpCode == OpCodes.Callvirt &&
                instruction.Operand is MethodReference called &&
                called.DeclaringType.FullName == typeof(ResourceManager).FullName &&
                called.Name == nameof(ResourceManager.GetObject) &&
                called.Parameters.Count == 1)
            {
                instruction.OpCode = OpCodes.Call;
                instruction.Operand = replacement;
            }
        }
    }

    private static void ReplaceWithBridge(
        MethodDefinition method,
        MethodReference bridge,
        bool loadInstance,
        bool loadParameters)
    {
        method.Body = new Mono.Cecil.Cil.MethodBody(method);
        var processor = method.Body.GetILProcessor();
        if (loadInstance)
        {
            processor.Append(processor.Create(OpCodes.Ldarg_0));
        }
        if (loadParameters)
        {
            foreach (var parameter in method.Parameters)
            {
                processor.Append(processor.Create(OpCodes.Ldarg, parameter));
            }
        }

        processor.Append(processor.Create(OpCodes.Call, bridge));
        processor.Append(processor.Create(OpCodes.Ret));
    }

    private static void InsertBooleanGuard(
        MethodDefinition method,
        MethodReference isAllowed,
        bool returnCompletedTask)
    {
        if (!method.HasBody || method.Body.Instructions.Count == 0)
        {
            throw new InvalidOperationException(
                $"Cannot add a permission guard to {method.FullName}.");
        }

        var processor = method.Body.GetILProcessor();
        var first = method.Body.Instructions[0];
        processor.InsertBefore(first, processor.Create(OpCodes.Call, isAllowed));
        processor.InsertBefore(first, processor.Create(OpCodes.Brtrue, first));
        if (returnCompletedTask)
        {
            var completedTask = method.Module.ImportReference(
                typeof(Task).GetProperty(nameof(Task.CompletedTask))!.GetMethod!);
            processor.InsertBefore(first, processor.Create(OpCodes.Call, completedTask));
        }
        else if (method.ReturnType.MetadataType == MetadataType.Boolean)
        {
            processor.InsertBefore(first, processor.Create(OpCodes.Ldc_I4_0));
        }
        else if (method.ReturnType.MetadataType != MetadataType.Void)
        {
            throw new InvalidOperationException(
                $"Permission guard has no denied return for {method.FullName}.");
        }
        processor.InsertBefore(first, processor.Create(OpCodes.Ret));
    }

    private static GenericInstanceMethod MakeGenericMethod(
        MethodReference method,
        IEnumerable<TypeReference> arguments)
    {
        var generic = new GenericInstanceMethod(method);
        foreach (var argument in arguments)
        {
            generic.GenericArguments.Add(argument);
        }

        return generic;
    }

    private static IEnumerable<TypeDefinition> EnumerateTypes(TypeDefinition type)
    {
        yield return type;
        foreach (var nested in type.NestedTypes.SelectMany(EnumerateTypes))
        {
            yield return nested;
        }
    }
}

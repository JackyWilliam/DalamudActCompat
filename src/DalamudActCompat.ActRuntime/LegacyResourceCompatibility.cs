using System.Collections;
using System.ComponentModel;
using System.Drawing;
using System.Formats.Nrbf;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.IO.Compression;
using System.Reflection;
using System.Resources;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Windows.Forms;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Plugin.Services;
using DalamudActCompat.Protocol;
using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Resources.Extensions;

[assembly: InternalsVisibleTo("DalamudActCompat.LegacyResourceSmokeTests")]
[assembly: InternalsVisibleTo("DalamudActCompat.PackageSmokeTests")]

namespace DalamudActCompat.ActRuntime;

public static class LegacyResourceCompatibility
{
    private static readonly object ServiceSync = new();
    private static StaClipboardService? clipboardService;
    private static IPluginLog? compatibilityLog;
    private static INotificationManager? notificationManager;
    private static long triggerEventDrops;

    internal static void Configure(
        IPluginLog pluginLog,
        INotificationManager notifications)
    {
        lock (ServiceSync)
        {
            compatibilityLog = pluginLog;
            notificationManager = notifications;
            clipboardService ??= new StaClipboardService(pluginLog);
        }
    }

    internal static void StopServices()
    {
        lock (ServiceSync)
        {
            clipboardService?.Dispose();
            clipboardService = null;
            notificationManager = null;
            compatibilityLog = null;
        }
    }

    internal static void EnsureLegacyResourceDecoderAvailable()
    {
        if (typeof(NrbfDecoder).Assembly.GetName().Name != "System.Formats.Nrbf")
        {
            throw new InvalidOperationException(
                "System.Formats.Nrbf is unavailable for legacy Triggernometry resources.");
        }
    }

    internal static void ProbeEmbeddedResources(Assembly assembly, AssemblyLoadContext loadContext)
    {
        PreloadTriggernometryScriptingAssemblies(assembly, loadContext);
        assembly = ResolveImplementationAssembly(assembly, loadContext);
        var resourceNames = assembly.GetManifestResourceNames()
            .Where(name => name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (resourceNames.Length == 0)
        {
            throw new InvalidOperationException(
                $"Triggernometry implementation {assembly.FullName} has no embedded .resources payloads.");
        }

        foreach (var resourceName in resourceNames)
        {
            using var stream = assembly.GetManifestResourceStream(resourceName)
                               ?? throw new MissingManifestResourceException(
                                   $"Could not open Triggernometry resource {resourceName}.");
            using var reader = new DeserializingResourceReader(stream);
            IDictionaryEnumerator entries = reader.GetEnumerator();
            while (entries.MoveNext())
            {
                var key = (string)entries.Key;
                try
                {
                    _ = entries.Value;
                }
                catch (NotSupportedException ex)
                {
                    throw new NotSupportedException(
                        $"Triggernometry resource {resourceName}/{key} could not be read after safe NRBF conversion.",
                        ex);
                }
            }
        }
    }

    private static void PreloadTriggernometryScriptingAssemblies(
        Assembly assembly,
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
            using var compressed = assembly.GetManifestResourceStream(resourceName)
                                   ?? throw new MissingManifestResourceException(
                                       $"Triggernometry scripting dependency {resourceName} is missing.");
            using var deflate = new DeflateStream(compressed, CompressionMode.Decompress);
            using var dependency = new MemoryStream();
            deflate.CopyTo(dependency);
            dependency.Position = 0;
            using var definition = AssemblyDefinition.ReadAssembly(dependency);
            var assemblyName = definition.Name.Name;
            if (loadContext.Assemblies.Any(candidate => string.Equals(
                    candidate.GetName().Name,
                    assemblyName,
                    StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            dependency.Position = 0;
            loadContext.LoadFromStream(dependency);
        }
    }

    internal static Assembly LoadPostNamazuWithClipboardCompatibility(
        string assemblyPath,
        AssemblyLoadContext loadContext)
    {
        using var input = File.OpenRead(assemblyPath);
        using var definition = AssemblyDefinition.ReadAssembly(input);
        var setText = typeof(Clipboard).GetMethod(
                          nameof(Clipboard.SetText),
                          BindingFlags.Public | BindingFlags.Static,
                          binder: null,
                          [typeof(string)],
                          modifiers: null)
                      ?? throw new MissingMethodException(typeof(Clipboard).FullName, nameof(Clipboard.SetText));
        var getText = typeof(Clipboard).GetMethod(
                          nameof(Clipboard.GetText),
                          BindingFlags.Public | BindingFlags.Static,
                          binder: null,
                          Type.EmptyTypes,
                          modifiers: null)
                      ?? throw new MissingMethodException(typeof(Clipboard).FullName, nameof(Clipboard.GetText));
        var safeSetText = definition.MainModule.ImportReference(
            typeof(LegacyResourceCompatibility).GetMethod(
                nameof(SetClipboardText),
                BindingFlags.Public | BindingFlags.Static)!);
        var safeGetText = definition.MainModule.ImportReference(
            typeof(LegacyResourceCompatibility).GetMethod(
                nameof(GetClipboardText),
                BindingFlags.Public | BindingFlags.Static)!);
        var setTextFullName = definition.MainModule.ImportReference(setText).FullName;
        var getTextFullName = definition.MainModule.ImportReference(getText).FullName;
        var startHttpListener = definition.MainModule.ImportReference(
            typeof(LegacyResourceCompatibility).GetMethod(
                nameof(StartPostNamazuHttpListener),
                BindingFlags.Public | BindingFlags.Static)!);
        var skipHttpThreadAbort = definition.MainModule.ImportReference(
            typeof(LegacyResourceCompatibility).GetMethod(
                nameof(SkipPostNamazuThreadAbort),
                BindingFlags.Public | BindingFlags.Static)!);
        var patchedCalls = 0;

        foreach (var instruction in definition.MainModule.Types
                     .SelectMany(EnumerateTypes)
                     .SelectMany(type => type.Methods)
                     .Where(method => method.HasBody)
                     .SelectMany(method => method.Body.Instructions))
        {
            if (instruction.Operand is not MethodReference called ||
                called.DeclaringType.FullName != typeof(Clipboard).FullName)
            {
                continue;
            }

            if (called.FullName == setTextFullName)
            {
                instruction.OpCode = OpCodes.Call;
                instruction.Operand = safeSetText;
                patchedCalls++;
            }
            else if (called.FullName == getTextFullName)
            {
                instruction.OpCode = OpCodes.Call;
                instruction.Operand = safeGetText;
                patchedCalls++;
            }
        }

        patchedCalls += PatchPostNamazuNativeBridge(definition);
        var httpListenerPatched = false;
        var httpThreadAbortPatched = false;
        foreach (var method in definition.MainModule.Types
                     .SelectMany(EnumerateTypes)
                     .Where(type => type.FullName == "PostNamazu.Common.HttpServer")
                     .SelectMany(type => type.Methods)
                     .Where(method => (method.Name is "Listen" or "Stop") && method.HasBody))
        {
            foreach (var instruction in method.Body.Instructions)
            {
                if (instruction.Operand is MethodReference called &&
                    called.DeclaringType.FullName == typeof(HttpListener).FullName &&
                    called.Name == nameof(HttpListener.Start) &&
                    called.Parameters.Count == 0)
                {
                    instruction.OpCode = OpCodes.Call;
                    instruction.Operand = startHttpListener;
                    httpListenerPatched = true;
                }
                else if (instruction.Operand is MethodReference abort &&
                         abort.DeclaringType.FullName == typeof(Thread).FullName &&
                         abort.Name == nameof(Thread.Abort) &&
                         abort.Parameters.Count == 0)
                {
                    instruction.OpCode = OpCodes.Call;
                    instruction.Operand = skipHttpThreadAbort;
                    httpThreadAbortPatched = true;
                }
            }
        }

        if (patchedCalls == 0 || !httpListenerPatched || !httpThreadAbortPatched)
        {
            throw new InvalidOperationException(
                "PostNamazu compatibility shape changed; " +
                $"patchedCalls={patchedCalls}, httpListener={httpListenerPatched}, " +
                $"httpStop={httpThreadAbortPatched}.");
        }

        using var output = new MemoryStream();
        definition.Write(output);
        output.Position = 0;
        return loadContext.LoadFromStream(output);
    }

    private static int PatchPostNamazuNativeBridge(AssemblyDefinition definition)
    {
        var module = definition.MainModule;
        var bridgeType = typeof(NativePostNamazuBridge);
        var attachBridge = module.ImportReference(bridgeType.GetMethod(nameof(NativePostNamazuBridge.Attach))!);
        var sendCommandBridge = module.ImportReference(bridgeType.GetMethod(nameof(NativePostNamazuBridge.SendCommand))!);
        var callBridge = module.ImportReference(bridgeType.GetMethods()
            .Single(method => method.Name == nameof(NativePostNamazuBridge.Call) && !method.IsGenericMethod));
        var genericCallBridge = module.ImportReference(bridgeType.GetMethods()
            .Single(method => method.Name == nameof(NativePostNamazuBridge.Call) && method.IsGenericMethod));
        var executeBridge = module.ImportReference(bridgeType.GetMethods()
            .Single(method => method.Name == nameof(NativePostNamazuBridge.Execute) && !method.IsGenericMethod));
        var genericExecuteBridge = module.ImportReference(bridgeType.GetMethods()
            .Single(method => method.Name == nameof(NativePostNamazuBridge.Execute) && method.IsGenericMethod));
        var readBridge = module.ImportReference(bridgeType.GetMethod(nameof(NativePostNamazuBridge.Read))!);
        var writeBridge = module.ImportReference(bridgeType.GetMethod(nameof(NativePostNamazuBridge.Write))!);
        var writeBytesBridge = module.ImportReference(bridgeType.GetMethod(nameof(NativePostNamazuBridge.WriteBytes))!);
        var skipProcessMonitoringBridge = module.ImportReference(
            bridgeType.GetMethod(nameof(NativePostNamazuBridge.SkipLegacyProcessMonitoring))!);
        var patched = 0;

        foreach (var type in module.Types.SelectMany(EnumerateTypes))
        {
            foreach (var method in type.Methods.Where(method => method.HasBody))
            {
                if (type.FullName == "PostNamazu.PostNamazu" && method.Name == "Attach")
                {
                    ReplaceBody(method, attachBridge, loadInstance: true);
                    patched++;
                    continue;
                }

                if (type.FullName == "PostNamazu.Common.ProcessManager" &&
                    method.Name == "StartProcessMonitoring")
                {
                    ReplaceBody(method, skipProcessMonitoringBridge, loadInstance: true);
                    patched++;
                    continue;
                }

                if (type.FullName == "PostNamazu.Actions.Command" &&
                    method.Name == "DoTextCommand" &&
                    method.Parameters.Count == 1)
                {
                    ReplaceBody(method, sendCommandBridge, loadInstance: false);
                    patched++;
                    continue;
                }

                if (type.FullName == "PostNamazu.PostNamazu" &&
                    method.Name is "Call" or "DirectCall" or "ExecuteInFrameLock")
                {
                    var bridge = method.Name == "ExecuteInFrameLock"
                        ? method.HasGenericParameters
                            ? MakeGenericMethod(genericExecuteBridge, method.GenericParameters)
                            : executeBridge
                        : method.HasGenericParameters
                            ? MakeGenericMethod(genericCallBridge, method.GenericParameters)
                            : callBridge;
                    ReplaceBody(method, bridge, loadInstance: false);
                    patched++;
                    continue;
                }

                if (type.FullName == "PostNamazu.Actions.NamazuModule" &&
                    method.Name == "ExecuteWithLock")
                {
                    var bridge = method.HasGenericParameters
                        ? MakeGenericMethod(genericExecuteBridge, method.GenericParameters)
                        : executeBridge;
                    ReplaceBody(method, bridge, loadInstance: false);
                    patched++;
                    continue;
                }

                foreach (var instruction in method.Body.Instructions)
                {
                    if (instruction.Operand is not MethodReference called ||
                        called.DeclaringType.FullName != "GreyMagic.ExternalProcessMemory")
                    {
                        continue;
                    }

                    MethodReference? replacement = called.Name switch
                    {
                        "Read" when called is GenericInstanceMethod generic =>
                            MakeGenericMethod(readBridge, generic.GenericArguments),
                        "Write" when called is GenericInstanceMethod generic =>
                            MakeGenericMethod(writeBridge, generic.GenericArguments),
                        "WriteBytes" => writeBytesBridge,
                        _ => null,
                    };
                    if (replacement is null)
                    {
                        continue;
                    }

                    instruction.OpCode = OpCodes.Call;
                    instruction.Operand = replacement;
                    patched++;
                }
            }
        }

        return patched;
    }

    private static GenericInstanceMethod MakeGenericMethod(
        MethodReference method,
        IEnumerable<TypeReference> arguments)
    {
        var instance = new GenericInstanceMethod(method);
        foreach (var argument in arguments)
        {
            instance.GenericArguments.Add(argument);
        }

        return instance;
    }

    private static void ReplaceBody(
        MethodDefinition method,
        MethodReference bridge,
        bool loadInstance)
    {
        method.Body = new Mono.Cecil.Cil.MethodBody(method);
        var processor = method.Body.GetILProcessor();
        if (loadInstance)
        {
            processor.Append(processor.Create(OpCodes.Ldarg_0));
        }
        else
        {
            foreach (var parameter in method.Parameters)
            {
                processor.Append(processor.Create(OpCodes.Ldarg, parameter));
            }
        }

        processor.Append(processor.Create(OpCodes.Call, bridge));
        processor.Append(processor.Create(OpCodes.Ret));
    }

    public static void SetClipboardText(string text)
    {
        CompatibilityPermissionBroker.Demand("postnamazu", ActCapability.Clipboard);
        GetClipboardService().QueueSetText(text);
    }

    public static string GetClipboardText()
    {
        CompatibilityPermissionBroker.Demand("postnamazu", ActCapability.Clipboard);
        return GetClipboardService().GetText();
    }

    public static void StartPostNamazuHttpListener(HttpListener listener)
    {
        CompatibilityPermissionBroker.Demand("postnamazu", ActCapability.NetworkRequest);
        ArgumentNullException.ThrowIfNull(listener);
        var originalPrefixes = listener.Prefixes.Cast<string>().ToArray();
        if (OperatingSystem.IsWindows() && !IsCurrentProcessElevated())
        {
            var compatiblePrefixes = originalPrefixes
                .Select(prefix => prefix
                    .Replace("http://*:", "http://127.0.0.1:", StringComparison.OrdinalIgnoreCase)
                    .Replace("http://+:", "http://127.0.0.1:", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (!compatiblePrefixes.SequenceEqual(originalPrefixes, StringComparer.OrdinalIgnoreCase))
            {
                listener.Prefixes.Clear();
                foreach (var prefix in compatiblePrefixes)
                {
                    listener.Prefixes.Add(prefix);
                }
            }
        }

        try
        {
            listener.Start();
            compatibilityLog?.Information(
                $"PostNamazu HTTP listener started: {string.Join(",", listener.Prefixes.Cast<string>())}");
        }
        catch (Exception ex)
        {
            compatibilityLog?.Error(ex, "PostNamazu HTTP listener failed to start.");
            throw;
        }
    }

    public static void SkipPostNamazuThreadAbort(Thread _)
    {
        // HttpListener.Stop performs the shutdown; Thread.Abort is unavailable on modern .NET.
    }

    private static bool IsCurrentProcessElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity)
            .IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static bool CheckTriggernometryAdministratorCapability(bool warnIfNotAdmin)
    {
        bool isAdministrator;
        using (var identity = WindowsIdentity.GetCurrent())
        {
            isAdministrator = new WindowsPrincipal(identity)
                .IsInRole(WindowsBuiltInRole.Administrator);
        }

        if (!isAdministrator && warnIfNotAdmin)
        {
            const string message =
                "Triggernometry 正在普通权限下运行。日志、区域/战斗事件、正则、配置和 TTS " +
                "由兼容宿主提供，不需要管理员权限；需要提升权限的外部进程/受保护资源动作不会被自动放行。";
            compatibilityLog?.Information(message);
            notificationManager?.AddNotification(new Notification
            {
                Title = "Triggernometry 权限能力",
                Content = message,
                Type = NotificationType.Info,
            });
        }

        // Preserve the real Windows token state. Triggernometry uses this value
        // to tighten its script API policy while elevated, so returning true
        // here would both lie to the plugin and weaken its own safety checks.
        return isAdministrator;
    }

    public static void EnqueueTriggerEventBounded<T>(Queue<T> queue, T item)
    {
        ArgumentNullException.ThrowIfNull(queue);
        const int capacity = 8192;
        if (queue.Count >= capacity)
        {
            queue.Dequeue();
            var dropped = Interlocked.Increment(ref triggerEventDrops);
            if ((dropped & (dropped - 1)) == 0)
            {
                compatibilityLog?.Warning(
                    $"Triggernometry event queue reached {capacity}; dropped oldest low-priority " +
                    $"log event. Total dropped: {dropped}.");
            }
        }

        queue.Enqueue(item);
    }

    public static void ReportUnstoppableTriggernometryThread(Thread thread)
    {
        ArgumentNullException.ThrowIfNull(thread);
        compatibilityLog?.Warning(
            $"Triggernometry thread '{thread.Name ?? thread.ManagedThreadId.ToString()}' did not stop " +
            "cooperatively. Thread.Abort is intentionally disabled; independent Host process recovery " +
            "is the only safe forced-isolation boundary.");
    }

    private static Assembly ResolveImplementationAssembly(
        Assembly assembly,
        AssemblyLoadContext loadContext)
    {
        if (assembly.GetManifestResourceNames()
            .Any(name => name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase)))
        {
            return assembly;
        }

        const string implementationResource = "costura.triggernometryplugin.dll.compressed";
        if (!assembly.GetManifestResourceNames().Contains(
                implementationResource,
                StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Triggernometry assembly {assembly.FullName} has neither embedded .resources payloads " +
                "nor a Costura TriggernometryPlugin implementation assembly.");
        }

        using var compressed = assembly.GetManifestResourceStream(implementationResource)
                               ?? throw new MissingManifestResourceException(
                                   $"Could not open embedded implementation {implementationResource}.");
        using var deflate = new DeflateStream(compressed, CompressionMode.Decompress);
        using var implementation = new MemoryStream();
        deflate.CopyTo(implementation);
        implementation.Position = 0;
        using var patched = RewriteLegacyResources(implementation);
        return LoadPatchedTriggernometryImplementation(patched, loadContext);
    }

    private static Assembly LoadPatchedTriggernometryImplementation(
        MemoryStream patched,
        AssemblyLoadContext loadContext)
    {
        var image = patched.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(image));
        var cacheDirectory = Path.Combine(
            Path.GetTempPath(),
            "DalamudActCompat",
            "triggernometry");
        Directory.CreateDirectory(cacheDirectory);
        var assemblyPath = Path.Combine(
            cacheDirectory,
            $"TriggernometryPlugin-{hash}.dll");

        if (!File.Exists(assemblyPath))
        {
            var stagingPath = Path.Combine(
                cacheDirectory,
                $".{Environment.ProcessId}-{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllBytes(stagingPath, image);
                try
                {
                    File.Move(stagingPath, assemblyPath);
                }
                catch (IOException) when (File.Exists(assemblyPath))
                {
                    // Another process finished writing the same content-addressed image.
                }
            }
            finally
            {
                if (File.Exists(stagingPath))
                {
                    File.Delete(stagingPath);
                }
            }
        }

        return loadContext.LoadFromAssemblyPath(assemblyPath);
    }

    private static MemoryStream RewriteLegacyResources(Stream implementation)
    {
        using var definition = AssemblyDefinition.ReadAssembly(implementation);
        var resources = definition.MainModule.Resources
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
                        WriteConvertedResource(
                            writer,
                            key,
                            DeserializeLegacyPayload(payload));
                    }
                }
            }

            var index = definition.MainModule.Resources.IndexOf(resource);
            definition.MainModule.Resources[index] =
                new EmbeddedResource(resource.Name, resource.Attributes, output.ToArray());
        }

        RedirectResourceManagerCalls(definition.MainModule);
        PatchLegacyJavaScriptSerializer(definition.MainModule);
        PatchTriggernometryAdministratorCheck(definition.MainModule);
        PatchTriggernometryEventQueue(definition.MainModule);
        PatchTriggernometryThreadAbort(definition.MainModule);
        var patched = new MemoryStream();
        definition.Write(patched);
        patched.Position = 0;
        return patched;
    }

    private static void PatchLegacyJavaScriptSerializer(ModuleDefinition module)
    {
        var bridgeType = typeof(LegacyJavaScriptSerializer);
        var constructor = module.ImportReference(bridgeType.GetConstructor(Type.EmptyTypes)!);
        var serialize = module.ImportReference(bridgeType.GetMethod(
            nameof(LegacyJavaScriptSerializer.Serialize),
            [typeof(object)])!);
        var deserialize = module.ImportReference(bridgeType.GetMethods()
            .Single(method =>
                method.Name == nameof(LegacyJavaScriptSerializer.Deserialize) &&
                method.IsGenericMethodDefinition));
        var deserializeObject = module.ImportReference(bridgeType.GetMethod(
            nameof(LegacyJavaScriptSerializer.DeserializeObject),
            [typeof(string)])!);
        var patched = 0;

        foreach (var instruction in module.Types
                     .SelectMany(EnumerateTypes)
                     .SelectMany(type => type.Methods)
                     .Where(method => method.HasBody)
                     .SelectMany(method => method.Body.Instructions))
        {
            if (instruction.Operand is not MethodReference called ||
                called.DeclaringType.FullName !=
                "System.Web.Script.Serialization.JavaScriptSerializer")
            {
                continue;
            }

            MethodReference replacement = called.Name switch
            {
                ".ctor" when called.Parameters.Count == 0 => constructor,
                nameof(LegacyJavaScriptSerializer.Serialize)
                    when called.Parameters.Count == 1 => serialize,
                nameof(LegacyJavaScriptSerializer.Deserialize)
                    when called is GenericInstanceMethod generic &&
                         generic.GenericArguments.Count == 1 =>
                    MakeGenericMethod(deserialize, generic.GenericArguments),
                nameof(LegacyJavaScriptSerializer.DeserializeObject)
                    when called.Parameters.Count == 1 => deserializeObject,
                _ => throw new InvalidOperationException(
                    $"Unsupported JavaScriptSerializer call {called.FullName}."),
            };
            instruction.Operand = replacement;
            patched++;
        }

        if (patched != 24)
        {
            throw new InvalidOperationException(
                $"Expected 24 Triggernometry JavaScriptSerializer calls, patched {patched}.");
        }

        var systemWeb = module.AssemblyReferences.SingleOrDefault(reference =>
            reference.Name == "System.Web.Extensions");
        if (systemWeb is not null)
        {
            module.AssemblyReferences.Remove(systemWeb);
        }
    }

    private static void PatchTriggernometryAdministratorCheck(ModuleDefinition module)
    {
        var replacement = module.ImportReference(
            typeof(LegacyResourceCompatibility).GetMethod(
                nameof(CheckTriggernometryAdministratorCapability),
                BindingFlags.Public | BindingFlags.Static)!);
        var method = module.Types
            .SelectMany(EnumerateTypes)
            .SelectMany(type => type.Methods)
            .SingleOrDefault(candidate =>
                candidate.Name == "CheckIfAdministrator" &&
                candidate.ReturnType.MetadataType == MetadataType.Boolean &&
                candidate.Parameters.Count == 1 &&
                candidate.Parameters[0].ParameterType.MetadataType == MetadataType.Boolean)
            ?? throw new MissingMethodException(
                "Triggernometry implementation",
                "CheckIfAdministrator(Boolean)");
        ReplaceBody(method, replacement, loadInstance: false);
    }

    private static void PatchTriggernometryEventQueue(ModuleDefinition module)
    {
        var genericBridge = module.ImportReference(
            typeof(LegacyResourceCompatibility).GetMethod(
                nameof(EnqueueTriggerEventBounded),
                BindingFlags.Public | BindingFlags.Static)!);
        var patched = 0;
        foreach (var method in module.Types
                     .SelectMany(EnumerateTypes)
                     .SelectMany(type => type.Methods)
                     .Where(method => method.HasBody))
        {
            foreach (var instruction in method.Body.Instructions)
            {
                if (instruction.Operand is not MethodReference
                    {
                        Name: "Enqueue",
                        DeclaringType: GenericInstanceType queueType,
                    } called ||
                    queueType.ElementType.FullName != "System.Collections.Generic.Queue`1" ||
                    queueType.GenericArguments.Count != 1 ||
                    queueType.GenericArguments[0].FullName != "Triggernometry.Core.LogEvent")
                {
                    continue;
                }

                instruction.OpCode = OpCodes.Call;
                instruction.Operand = MakeGenericMethod(
                    genericBridge,
                    [queueType.GenericArguments[0]]);
                patched++;
            }
        }

        if (patched != 2)
        {
            throw new InvalidOperationException(
                $"Expected two Triggernometry LogEvent enqueue sites, patched {patched}.");
        }
    }

    private static void PatchTriggernometryThreadAbort(ModuleDefinition module)
    {
        var replacement = module.ImportReference(
            typeof(LegacyResourceCompatibility).GetMethod(
                nameof(ReportUnstoppableTriggernometryThread),
                BindingFlags.Public | BindingFlags.Static)!);
        var patched = 0;
        foreach (var instruction in module.Types
                     .SelectMany(EnumerateTypes)
                     .SelectMany(type => type.Methods)
                     .Where(method => method.HasBody)
                     .SelectMany(method => method.Body.Instructions))
        {
            if (instruction.Operand is not MethodReference
                {
                    Name: nameof(Thread.Abort),
                    DeclaringType.FullName: "System.Threading.Thread",
                })
            {
                continue;
            }

            instruction.OpCode = OpCodes.Call;
            instruction.Operand = replacement;
            patched++;
        }

        if (patched != 3)
        {
            throw new InvalidOperationException(
                $"Expected three Triggernometry Thread.Abort sites, patched {patched}.");
        }
    }

    private static StaClipboardService GetClipboardService()
    {
        lock (ServiceSync)
        {
            return clipboardService
                   ?? throw new InvalidOperationException(
                       "The ACT compatibility clipboard service is not configured.");
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
            using var imageStream = new MemoryStream(
                ReadByteArray(record, "Data"),
                writable: false);
            using var bitmap = new Bitmap(imageStream);
            return new Bitmap(bitmap);
        }

        if (record.TypeNameMatches(typeof(Point)))
        {
            return new Point(record.GetInt32("x"), record.GetInt32("y"));
        }

        throw new NotSupportedException(
            $"Legacy resource type {record.TypeName} is not in the safe NRBF conversion allowlist.");
    }

    private static byte[] ReadByteArray(ClassRecord record, string memberName)
    {
        var array = record.GetArrayRecord(memberName)
                    ?? throw new InvalidDataException(
                        $"Legacy resource {record.TypeName}/{memberName} has no byte array.");
        var lengths = array.Lengths;
        if (lengths.Length != 1 || lengths[0] > 64 * 1024 * 1024)
        {
            throw new InvalidDataException(
                $"Legacy resource {record.TypeName}/{memberName} has an invalid byte array length.");
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
                       ?? throw new InvalidOperationException(
                           $"Legacy resource type {type.FullName} has no assembly-qualified name.");
        TypeConverter converter = TypeDescriptor.GetConverter(type);
        if (value is byte[] imageListData &&
            key.EndsWith(".ImageStream", StringComparison.Ordinal))
        {
            writer.AddResource(key, imageListData);
            return;
        }

        if (converter.CanConvertTo(typeof(byte[])) &&
            converter.CanConvertFrom(typeof(byte[])))
        {
            var bytes = converter.ConvertTo(
                            null,
                            CultureInfo.InvariantCulture,
                            value,
                            typeof(byte[])) as byte[]
                        ?? throw new InvalidOperationException(
                            $"TypeConverter for {typeName} returned no byte array.");
            writer.AddTypeConverterResource(key, bytes, typeName);
            return;
        }

        if (converter.CanConvertTo(typeof(string)) &&
            converter.CanConvertFrom(typeof(string)))
        {
            var text = converter.ConvertToInvariantString(value)
                       ?? throw new InvalidOperationException(
                           $"TypeConverter for {typeName} returned no string.");
            writer.AddResource(key, text, typeName);
            return;
        }

        throw new NotSupportedException(
            $"Legacy resource {key} has type {typeName}, which has no safe byte[] or string TypeConverter.");
    }

    public static object? GetResourceObject(ResourceManager resourceManager, string key)
    {
        var value = resourceManager.GetObject(key);
        if (value is not byte[] data ||
            !key.EndsWith(".ImageStream", StringComparison.Ordinal))
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
                              binder: null,
                              [typeof(SerializationInfo), typeof(StreamingContext)],
                              modifiers: null)
                          ?? throw new MissingMethodException(
                              type.FullName,
                              ".ctor(SerializationInfo, StreamingContext)");
#pragma warning disable SYSLIB0050
        return constructor.Invoke([info, new StreamingContext(StreamingContextStates.All)]);
#pragma warning restore SYSLIB0050
    }

    private static void RedirectResourceManagerCalls(ModuleDefinition module)
    {
        var helper = typeof(LegacyResourceCompatibility).GetMethod(
                         nameof(GetResourceObject),
                         BindingFlags.Public | BindingFlags.Static)
                     ?? throw new MissingMethodException(
                         typeof(LegacyResourceCompatibility).FullName,
                         nameof(GetResourceObject));
        var helperReference = module.ImportReference(helper);
        foreach (var method in module.Types
                     .SelectMany(EnumerateTypes)
                     .SelectMany(type => type.Methods)
                     .Where(method => method.HasBody))
        {
            foreach (var instruction in method.Body.Instructions)
            {
                if (instruction.OpCode != OpCodes.Callvirt ||
                    instruction.Operand is not MethodReference called ||
                    called.DeclaringType.FullName != typeof(ResourceManager).FullName ||
                    called.Name != nameof(ResourceManager.GetObject) ||
                    called.Parameters.Count != 1 ||
                    called.Parameters[0].ParameterType.FullName != typeof(string).FullName)
                {
                    continue;
                }

                instruction.OpCode = OpCodes.Call;
                instruction.Operand = helperReference;
            }
        }
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

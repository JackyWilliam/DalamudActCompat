using System.Reflection;
using System.Runtime.Loader;
using System.Xml;
using DalamudActCompat.ActRuntime;
using DalamudActCompat.Protocol;
using Mono.Cecil;

if (args.Length is < 1 or > 3)
{
    throw new ArgumentException(
        "Pass Triggernometry.dll, optionally PostNamazu.dll, and optionally a Triggernometry export XML.");
}

var assemblyPath = Path.GetFullPath(args[0]);
if (!File.Exists(assemblyPath))
{
    throw new FileNotFoundException("Triggernometry assembly was not found.", assemblyPath);
}

LegacyResourceCompatibility.EnsureLegacyResourceDecoderAvailable();
var overlayTemplates = SelfHostedActRuntime.ProbeOverlayTemplates();
if (!overlayTemplates.Any(template => template.Name == "Kagerou" && !template.IsCactbot) ||
    !overlayTemplates.Any(template =>
        template.Name.Contains("Cactbot DPS", StringComparison.Ordinal) &&
        template.IsCactbot))
{
    throw new InvalidOperationException(
        "OverlayPlugin built-in HTML templates were not exposed by the runtime.");
}
AssertActorCastExtraRotationCompatibility();

var resolver = new AssemblyDependencyResolver(assemblyPath);
AssemblyLoadContext.Default.Resolving += ResolveDependency;
try
{
    var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
    LegacyResourceCompatibility.ProbeEmbeddedResources(
        assembly,
        AssemblyLoadContext.Default);
    string[] scriptingAssemblies =
    [
        "Microsoft.CodeAnalysis",
        "Microsoft.CodeAnalysis.Scripting",
        "Microsoft.CodeAnalysis.CSharp",
        "Microsoft.CodeAnalysis.CSharp.Scripting",
    ];
    foreach (var scriptingAssembly in scriptingAssemblies)
    {
        if (!AssemblyLoadContext.Default.Assemblies.Any(candidate => string.Equals(
                candidate.GetName().Name,
                scriptingAssembly,
                StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"Triggernometry scripting dependency {scriptingAssembly} was not preloaded.");
        }
    }

    var implementation = AppDomain.CurrentDomain.GetAssemblies()
        .SingleOrDefault(candidate => string.Equals(
            candidate.GetName().Name,
            "TriggernometryPlugin",
            StringComparison.OrdinalIgnoreCase));
    if (implementation is null)
    {
        throw new InvalidOperationException(
            "The patched TriggernometryPlugin implementation was not preloaded.");
    }

    if (string.IsNullOrWhiteSpace(implementation.Location) ||
        !File.Exists(implementation.Location))
    {
        throw new InvalidOperationException(
            "The patched Triggernometry implementation must be file-backed so Roslyn can reference it.");
    }

    if (implementation.GetManifestResourceNames()
        .Count(name => name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase)) < 10)
    {
        throw new InvalidOperationException(
            "The patched Triggernometry implementation is missing expected UI resources.");
    }

    AssertSystemWebExtensionsWasReplaced(implementation);
    AssertLegacyJavaScriptSerializerCompatibility();
    AssertIndexMemberParserCanExecute(implementation);
    AssertPostNamazuSemanticPayloads();
    AssertPostNamazuSemanticCallSafety();

    var administratorCheck = implementation
        .GetType("Triggernometry.Core.RealPlugin", throwOnError: true)!
        .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        .Single(method =>
            method.Name == "CheckIfAdministrator" &&
            method.ReturnType == typeof(bool) &&
            method.GetParameters() is [{ ParameterType: var parameterType }] &&
            parameterType == typeof(bool));
    AssertCallsCompatibilityMethod(
        administratorCheck,
        nameof(LegacyResourceCompatibility.CheckTriggernometryAdministratorCapability));
    var realPluginType = implementation
        .GetType("Triggernometry.Core.RealPlugin", throwOnError: true)!;
    AssertCallsCompatibilityMethod(
        realPluginType.GetMethod(
            "LogLineQueuer",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!,
        nameof(LegacyResourceCompatibility.EnqueueTriggerEventBounded));
    AssertCallsCompatibilityMethod(
        realPluginType.GetMethod(
            "LogLineQueuerMass",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!,
        nameof(LegacyResourceCompatibility.EnqueueTriggerEventBounded));
    AssertTriggernometryScriptCompiles(implementation);
    if (args.Length == 3)
    {
        AssertTriggernometryExportScriptsCompile(implementation, args[2]);
    }

    var proxyType = assembly.GetType("TriggernometryProxy.ProxyPlugin", throwOnError: true)!;
    _ = Activator.CreateInstance(proxyType)
        ?? throw new InvalidOperationException("Triggernometry proxy could not be constructed.");

    if (args.Length >= 2)
    {
        var postNamazuPath = Path.GetFullPath(args[1]);
        var postNamazu = LegacyResourceCompatibility.LoadPostNamazuWithClipboardCompatibility(
            postNamazuPath,
            AssemblyLoadContext.Default);
        var postNamazuType = postNamazu.GetType("PostNamazu.PostNamazu", throwOnError: true)!;
        AssertCallsNativeBridge(
            postNamazuType.GetMethod("Attach", BindingFlags.Instance | BindingFlags.NonPublic)!,
            nameof(NativePostNamazuBridge.Attach));
        AssertCallsNativeBridge(
            postNamazu.GetType("PostNamazu.Actions.Command", throwOnError: true)!
                .GetMethod("DoTextCommand", BindingFlags.Instance | BindingFlags.Public)!,
            nameof(NativePostNamazuBridge.SendCommand));
        AssertCallsNativeBridge(
            postNamazu.GetType("PostNamazu.Common.ProcessManager", throwOnError: true)!
                .GetMethod("StartProcessMonitoring", BindingFlags.Instance | BindingFlags.Public)!,
            nameof(NativePostNamazuBridge.SkipLegacyProcessMonitoring));
        var copyLog = postNamazu
            .GetType("PostNamazu.PostNamazuUi", throwOnError: true)!
            .GetMethod("CopyLog", BindingFlags.Instance | BindingFlags.NonPublic)!;
        AssertCallsCompatibilityMethod(
            copyLog,
            nameof(LegacyResourceCompatibility.SetClipboardText));
        AssertCallsCompatibilityMethod(
            postNamazu.GetType("PostNamazu.Common.HttpServer", throwOnError: true)!
                .GetMethod("Listen", BindingFlags.Instance | BindingFlags.NonPublic)!,
            nameof(LegacyResourceCompatibility.StartPostNamazuHttpListener));
        AssertCallsCompatibilityMethod(
            postNamazu.GetType("PostNamazu.Common.HttpServer", throwOnError: true)!
                .GetMethod("Stop", BindingFlags.Instance | BindingFlags.Public)!,
            nameof(LegacyResourceCompatibility.SkipPostNamazuThreadAbort));
    }

    Console.WriteLine(
        "Safe NRBF conversion, Triggernometry resources, and PostNamazu native bridge probes passed.");
}

finally
{
    AssemblyLoadContext.Default.Resolving -= ResolveDependency;
}

static void AssertSystemWebExtensionsWasReplaced(Assembly implementation)
{
    using var definition = AssemblyDefinition.ReadAssembly(implementation.Location);
    if (definition.MainModule.AssemblyReferences.Any(reference =>
            reference.Name == "System.Web.Extensions"))
    {
        throw new InvalidOperationException(
            "Patched Triggernometry still references System.Web.Extensions.");
    }

    var legacyTypeReferences = definition.MainModule.GetTypeReferences()
        .Where(reference =>
            reference.FullName ==
            "System.Web.Script.Serialization.JavaScriptSerializer")
        .ToArray();
    if (legacyTypeReferences.Length > 0)
    {
        throw new InvalidOperationException(
            "Patched Triggernometry still contains JavaScriptSerializer type references: " +
            string.Join(", ", legacyTypeReferences.Select(reference => reference.Scope)));
    }

    var indexMemberParser = definition.MainModule.Types
        .SelectMany(EnumerateCecilTypes)
        .Single(type =>
            type.FullName ==
            "Triggernometry.Expressions.String.Parsers.IndexMemberParser")
        .Methods
        .Single(method => method.Name == "TryParse");
    var serializerLocal = indexMemberParser.Body.Variables
        .Single(variable =>
            variable.VariableType.FullName ==
            typeof(LegacyJavaScriptSerializer).FullName);
    if (serializerLocal.VariableType.Scope is not AssemblyNameReference scope ||
        scope.Name != typeof(LegacyJavaScriptSerializer).Assembly.GetName().Name)
    {
        throw new InvalidOperationException(
            $"IndexMemberParser serializer local is bound to {serializerLocal.VariableType.Scope}.");
    }

    var replacementCalls = 0;
    foreach (var method in definition.MainModule.Types
                 .SelectMany(EnumerateCecilTypes)
                 .SelectMany(type => type.Methods)
                 .Where(method => method.HasBody))
    {
        foreach (var instruction in method.Body.Instructions)
        {
            if (instruction.Operand is not MemberReference member)
            {
                continue;
            }

            if (member.DeclaringType?.FullName ==
                "System.Web.Script.Serialization.JavaScriptSerializer")
            {
                throw new InvalidOperationException(
                    $"Patched Triggernometry still calls {member.FullName} from {method.FullName}.");
            }

            if (member.DeclaringType?.FullName ==
                typeof(LegacyJavaScriptSerializer).FullName)
            {
                replacementCalls++;
            }
        }
    }

    if (replacementCalls != 24)
    {
        throw new InvalidOperationException(
            $"Expected 24 Triggernometry JavaScriptSerializer bridge calls, found {replacementCalls}.");
    }
}

static void AssertIndexMemberParserCanExecute(Assembly implementation)
{
    var parser = implementation
        .GetType(
            "Triggernometry.Expressions.String.Parsers.IndexMemberParser",
            throwOnError: true)!
        .GetMethod(
            "TryParse",
            BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new MissingMethodException("IndexMemberParser.TryParse was not found.");
    var contextType = implementation.GetType(
        "Triggernometry.Core.Context",
        throwOnError: true)!;
    var unbound = contextType
        .GetProperty(
            "Unbound",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!
        .GetValue(null);
    var result = parser.Invoke(null, ["ACTCOMPAT_UNKNOWN.member", unbound]);
    if (result is not null)
    {
        throw new InvalidOperationException(
            $"Unknown IndexMemberParser expression unexpectedly returned '{result}'.");
    }
}

static void AssertLegacyJavaScriptSerializerCompatibility()
{
    var serializer = new LegacyJavaScriptSerializer();
    var untyped = serializer.Deserialize<object>(
        "{\"actor\":{\"id\":3758096384,\"position\":[1.25,true,null]}}")
        as Dictionary<string, object?>
        ?? throw new InvalidOperationException("Untyped JSON root was not converted to a dictionary.");
    var actor = untyped["actor"] as Dictionary<string, object?>
                ?? throw new InvalidOperationException("Nested JSON object conversion failed.");
    if (Convert.ToInt64(actor["id"]) != 3_758_096_384L)
    {
        throw new InvalidOperationException("Large untyped JSON integer conversion failed.");
    }

    var typed = serializer.Deserialize<SerializerProbe>("{\"name\":\"compat\",\"count\":17}")
                ?? throw new InvalidOperationException("Typed JSON conversion returned null.");
    if (typed.Name != "compat" || typed.Count != 17)
    {
        throw new InvalidOperationException("Typed JSON conversion lost properties.");
    }

    var roundTrip = serializer.Serialize(typed);
    if (!roundTrip.Contains("compat", StringComparison.Ordinal) ||
        !roundTrip.Contains("17", StringComparison.Ordinal))
    {
        throw new InvalidOperationException("Legacy JSON serialization lost values.");
    }
}

static void AssertActorCastExtraRotationCompatibility()
{
    var packetType = typeof(RainbowMage.OverlayPlugin.PluginMain).Assembly.GetType(
                         "RainbowMage.OverlayPlugin.NetworkProcessors.LineActorCastExtra+ActorCastExtraPacket",
                         throwOnError: true)!
                     ?? throw new InvalidOperationException(
                         "OverlayPlugin ActorCastExtra packet formatter was not found.");
    var convert = packetType.GetMethod(
                      "ConvertRotation",
                      BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                  ?? throw new MissingMethodException(packetType.FullName, "ConvertRotation");

    var regional = Convert.ToDouble(convert.Invoke(null, [1.25f]));
    var global = Convert.ToDouble(convert.Invoke(null, [(ushort)0]));
    if (Math.Abs(regional - 1.25d) > 0.0001d ||
        Math.Abs(global + Math.PI) > 0.0001d)
    {
        throw new InvalidOperationException(
            "OverlayPlugin ActorCastExtra rotation conversion lost CN/KR/TW or global compatibility.");
    }

    try
    {
        _ = convert.Invoke(null, [(byte)1]);
        throw new InvalidOperationException(
            "OverlayPlugin ActorCastExtra accepted an unknown rotation field type.");
    }
    catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException)
    {
        // Expected: unknown packet layouts must fail closed instead of emitting corrupt 0x107 logs.
    }
}

static void AssertPostNamazuSemanticPayloads()
{
    var mark = PostNamazuSemanticActions.ParseMark(
        "{\"ActorID\":0xE0000000,\"MarkType\":\"attack8\",\"LocalOnly\":true}");
    if (mark.ActorId != PostNamazuSemanticActions.ClearActorId ||
        mark.ActorName is not null ||
        mark.MarkerIndex != 16 ||
        !mark.LocalOnly)
    {
        throw new InvalidOperationException(
            "PostNamazu hexadecimal ActorID or marker mapping compatibility failed.");
    }

    var waymarks = PostNamazuSemanticActions.ParseWaymarks(
        "{\"LocalOnly\":true,\"A\":{\"X\":1.25,\"Y\":2.5,\"Z\":-3.75,\"Active\":true},\"B\":{}}");
    if (waymarks.Operation != PostNamazuWaymarkOperation.Apply ||
        !waymarks.LocalOnly || waymarks.Updates.Count != 2 ||
        !waymarks.Updates[0].Active || waymarks.Updates[1].Active ||
        waymarks.Updates[0].Position.X != 1.25f ||
        waymarks.Updates[0].Position.Y != 2.5f ||
        waymarks.Updates[0].Position.Z != -3.75f)
    {
        throw new InvalidOperationException("PostNamazu waymark JSON compatibility failed.");
    }

    if (NativePostNamazuBridge.RequiresNativeGameMemory(waymarks) ||
        NativePostNamazuBridge.RequiresNativeGameMemory(
            PostNamazuSemanticActions.ParseWaymarks("clear")) ||
        NativePostNamazuBridge.RequiresNativeGameMemory(
            PostNamazuSemanticActions.ParseWaymarks("reset")) ||
        !NativePostNamazuBridge.RequiresNativeGameMemory(
            PostNamazuSemanticActions.ParseWaymarks(
                "{\"LocalOnly\":false,\"A\":{\"X\":1,\"Y\":2,\"Z\":3,\"Active\":true}}")) ||
        !NativePostNamazuBridge.RequiresNativeGameMemory(
            PostNamazuSemanticActions.ParseWaymarks("save")) ||
        !NativePostNamazuBridge.RequiresNativeGameMemory(
            PostNamazuSemanticActions.ParseWaymarks("load")) ||
        !NativePostNamazuBridge.RequiresNativeGameMemory(
            PostNamazuSemanticActions.ParseWaymarks("public")))
    {
        throw new InvalidOperationException(
            "PostNamazu local/native waymark permission compatibility failed.");
    }

    if (PostNamazuSemanticActions.ParseWaymarks("clear").Operation !=
        PostNamazuWaymarkOperation.ClearLocal ||
        PostNamazuSemanticActions.ParseWaymarks("public").Operation !=
        PostNamazuWaymarkOperation.Publicize ||
        PostNamazuSemanticActions.ParseWaymarks("save").Operation !=
        PostNamazuWaymarkOperation.Save ||
        PostNamazuSemanticActions.ParseWaymarks("restore").Operation !=
        PostNamazuWaymarkOperation.Load)
    {
        throw new InvalidOperationException("PostNamazu waymark command compatibility failed.");
    }

    var byName = PostNamazuSemanticActions.ParseMark(
        "{\"Name\":\"Actor Name\",\"MarkType\":\"circle\"}");
    if (byName.ActorId is not null || byName.ActorName != "Actor Name" ||
        byName.MarkerIndex != 11 || byName.LocalOnly)
    {
        throw new InvalidOperationException("PostNamazu name-based marking compatibility failed.");
    }

    var preset = PostNamazuSemanticActions.ParsePreset(
        "{\"Name\":\"Slot 30\",\"MapID\":777,\"A\":{\"X\":1.25,\"Y\":2.5,\"Z\":-3.75,\"Active\":true}}");
    if (preset.Slot != 30 || preset.MapId != 777 || preset.Markers.Count != 8 ||
        !preset.Markers[0].Active || preset.Markers[0].Position.Z != -3.75f ||
        preset.Markers[1].Active || PostNamazuSemanticActions.ParseKeyCode("65") != 65)
    {
        throw new InvalidOperationException("PostNamazu preset/sendkey compatibility failed.");
    }
}

static void AssertPostNamazuSemanticCallSafety()
{
    using var assembly = AssemblyDefinition.ReadAssembly(
        typeof(NativePostNamazuBridge).Assembly.Location);
    var bridge = assembly.MainModule.Types.Single(type =>
        type.FullName == typeof(NativePostNamazuBridge).FullName);
    string[] semanticMethods =
    [
        "ApplyMark",
        "ApplyWaymarks",
        "ApplyPublicWaymark",
        "ApplyPreset",
        "SendCommandAsync",
        "SendKeyCode",
    ];

    foreach (var methodName in semanticMethods)
    {
        var methods = bridge.Methods
            .Where(method => method.Name == methodName && method.HasBody)
            .ToArray();
        if (methods.Length == 0)
        {
            throw new MissingMethodException(bridge.FullName, methodName);
        }

        foreach (var method in methods)
        {
            foreach (var instruction in method.Body.Instructions)
            {
                if (instruction.OpCode == Mono.Cecil.Cil.OpCodes.Calli)
                {
                    throw new InvalidOperationException(
                        $"Semantic PostNamazu action {method.FullName} contains an indirect native call.");
                }

                if (instruction.Operand is MethodReference called &&
                    called.DeclaringType.FullName == bridge.FullName &&
                    called.Name is "Call" or "GetNativeCall")
                {
                    throw new InvalidOperationException(
                        $"Semantic PostNamazu action {method.FullName} reaches unsafe native helper " +
                        $"{called.Name}.");
                }
            }
        }
    }

    var publicWaymark = bridge.Methods.Single(method =>
        method.Name == "ApplyPublicWaymark" && method.HasBody);
    if (!publicWaymark.Body.Instructions.Any(instruction =>
            instruction.Operand is MethodReference called &&
            called.DeclaringType.FullName ==
            "FFXIVClientStructs.FFXIV.Client.Game.GameMain" &&
            called.Name == "ExecuteCommand"))
    {
        throw new InvalidOperationException(
            "Public waymarks are not routed through GameMain.ExecuteCommand.");
    }
}

static IEnumerable<TypeDefinition> EnumerateCecilTypes(TypeDefinition type)
{
    yield return type;
    foreach (var nested in type.NestedTypes.SelectMany(EnumerateCecilTypes))
    {
        yield return nested;
    }
}

static void AssertCallsNativeBridge(MethodInfo method, string expectedMethod)
{
    var il = method.GetMethodBody()?.GetILAsByteArray()
        ?? throw new InvalidOperationException($"{method} has no IL body.");
    for (var index = 0; index <= il.Length - 5; index++)
    {
        if (il[index] != 0x28)
        {
            continue;
        }

        var token = BitConverter.ToInt32(il, index + 1);
        if (method.Module.ResolveMethod(token) is MethodInfo called &&
            called.DeclaringType == typeof(NativePostNamazuBridge) &&
            called.Name == expectedMethod)
        {
            return;
        }
    }

    throw new InvalidOperationException(
        $"{method.DeclaringType?.FullName}.{method.Name} was not redirected to " +
        $"{nameof(NativePostNamazuBridge)}.{expectedMethod}.");
}

static void AssertTriggernometryExportScriptsCompile(Assembly implementation, string exportPath)
{
    var fullPath = Path.GetFullPath(exportPath);
    if (!File.Exists(fullPath))
    {
        throw new FileNotFoundException("Triggernometry export XML was not found.", fullPath);
    }

    var document = new XmlDocument();
    document.Load(fullPath);
    var markActions = document.SelectNodes(
                              "//Action[@ActionType='NamedCallback' and @NamedCallbackName='mark']")
                          ?.Cast<XmlElement>()
                          .ToArray()
                      ?? [];
    var actorIdMark = markActions.SingleOrDefault(action =>
        action.ParentNode?.ParentNode is XmlElement trigger &&
        trigger.GetAttribute("Name").StartsWith("04 ", StringComparison.Ordinal) &&
        action.GetAttribute("OrderNumber") == "1");
    var clearMarks = markActions.Count(action =>
        action.GetAttribute("NamedCallbackParam").Contains(
            "\"ActorID\": 3758096384",
            StringComparison.Ordinal));
    if (actorIdMark is null ||
        !actorIdMark.GetAttribute("NamedCallbackParam").Contains(
            "\"ActorID\": \"0x${_me.id}\"",
            StringComparison.Ordinal) ||
        markActions.Count(action => action.GetAttribute("NamedCallbackParam").Contains(
            "\"ActorID\": \"0x${_me.id}\"",
            StringComparison.Ordinal)) != 1 ||
        clearMarks != 3)
    {
        throw new InvalidOperationException(
            "Triggernometry export must pass its hexadecimal entity ID with a 0x prefix and clear values as UInt32 JSON numbers.");
    }

    var scriptActions = document.SelectNodes("//Action[@ActionType='ExecuteScript']")
                            ?.Cast<XmlElement>()
                            .ToArray()
                        ?? [];
    if (scriptActions.Length == 0)
    {
        throw new InvalidOperationException(
            "Triggernometry export XML does not contain any ExecuteScript actions.");
    }

    foreach (var action in scriptActions)
    {
        var triggerName = (action.ParentNode?.ParentNode as XmlElement)
            ?.GetAttribute("Name");
        var source = action.GetAttribute("ExecScriptExpression");
        if (string.IsNullOrWhiteSpace(source))
        {
            throw new InvalidOperationException(
                $"Triggernometry trigger '{triggerName}' has an empty ExecuteScript action.");
        }

        AssertTriggernometryScriptCompiles(
            implementation,
            source,
            $"Triggernometry export script '{triggerName}'");
    }
}

static void AssertTriggernometryScriptCompiles(
    Assembly implementation,
    string? sourceOverride = null,
    string description = "Triggernometry SelfTest script")
{
    var scriptOptionsType = AppDomain.CurrentDomain.GetAssemblies()
        .Single(assembly => string.Equals(
            assembly.GetName().Name,
            "Microsoft.CodeAnalysis.Scripting",
            StringComparison.OrdinalIgnoreCase))
        .GetType("Microsoft.CodeAnalysis.Scripting.ScriptOptions", throwOnError: true)!;
    var options = scriptOptionsType
                      .GetProperty("Default", BindingFlags.Public | BindingFlags.Static)!
                      .GetValue(null)
                  ?? throw new InvalidOperationException("Roslyn default ScriptOptions were unavailable.");
    var addAssemblyReference = implementation
        .GetType("Triggernometry.Core.Scripting.ScriptOptionsExtensions", throwOnError: true)!
        .GetMethod(
            "AddMetadataReferenceFromAssembly",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException(
            "Triggernometry.Core.Scripting.ScriptOptionsExtensions",
            "AddMetadataReferenceFromAssembly");
    foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
    {
        options = addAssemblyReference.Invoke(null, [options, assembly])
                  ?? throw new InvalidOperationException(
                      $"Roslyn rejected metadata reference {assembly.FullName}.");
    }

    const string selfTestScript =
        """
        using System.Windows.Forms;
        using Triggernometry.PluginBridges.BridgeNamazu;

        if (BridgeNamazu.NamazuPlugin != null && !BridgeNamazu.NamazuPlugin.IsActionEnabled("command"))
        {
            Triggernometry.Core.Scripting.ScriptHelper.SetScalarVariable(false, "SelfTest_NamazuCommandDisabled", 1);
            MessageBox.Show("PostNamazu command module is disabled.");
        }
        """;
    var source = sourceOverride ?? selfTestScript;
    var csharpScriptType = AppDomain.CurrentDomain.GetAssemblies()
        .Single(assembly => string.Equals(
            assembly.GetName().Name,
            "Microsoft.CodeAnalysis.CSharp.Scripting",
            StringComparison.OrdinalIgnoreCase))
        .GetType("Microsoft.CodeAnalysis.CSharp.Scripting.CSharpScript", throwOnError: true)!;
    var create = csharpScriptType
        .GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Single(method =>
            method.Name == "Create" &&
            method.IsGenericMethodDefinition &&
            method.GetParameters() is
            [
                { ParameterType: var sourceType },
                _,
                _,
                _,
            ] &&
            sourceType == typeof(string));
    var script = create.MakeGenericMethod(typeof(object)).Invoke(
                     null,
                     [source, options, null, null])
                 ?? throw new InvalidOperationException($"Roslyn did not create {description}.");
    var compilation = script.GetType()
                          .GetMethod("GetCompilation", BindingFlags.Public | BindingFlags.Instance)!
                          .Invoke(script, null)
                      ?? throw new InvalidOperationException("Roslyn did not expose the SelfTest compilation.");
    var getDiagnostics = compilation.GetType()
        .GetMethod(
            "GetDiagnostics",
            BindingFlags.Public | BindingFlags.Instance,
            binder: null,
            [typeof(CancellationToken)],
            modifiers: null)
        ?? throw new MissingMethodException(compilation.GetType().FullName, "GetDiagnostics");
    var diagnostics = (System.Collections.IEnumerable)(getDiagnostics.Invoke(
        compilation,
        [CancellationToken.None]) ?? Array.Empty<object>());
    var errors = diagnostics.Cast<object>()
        .Where(diagnostic => string.Equals(
            diagnostic.GetType().GetProperty("Severity")?.GetValue(diagnostic)?.ToString(),
            "Error",
            StringComparison.Ordinal))
        .Select(static diagnostic => diagnostic.ToString())
        .ToArray();
    if (errors.Length > 0)
    {
        throw new InvalidOperationException(
            $"{description} did not compile:" + Environment.NewLine +
            string.Join(Environment.NewLine, errors));
    }
}

static void AssertCallsCompatibilityMethod(MethodInfo method, string expectedMethod)
{
    var il = method.GetMethodBody()?.GetILAsByteArray()
             ?? throw new InvalidOperationException($"{method} has no IL body.");
    for (var index = 0; index <= il.Length - 5; index++)
    {
        if (il[index] != 0x28)
        {
            continue;
        }

        var token = BitConverter.ToInt32(il, index + 1);
        if (method.Module.ResolveMethod(token) is MethodInfo called &&
            called.DeclaringType == typeof(LegacyResourceCompatibility) &&
            called.Name == expectedMethod)
        {
            return;
        }
    }

    throw new InvalidOperationException(
        $"{method.DeclaringType?.FullName}.{method.Name} does not call " +
        $"{nameof(LegacyResourceCompatibility)}.{expectedMethod}.");
}

Assembly? ResolveDependency(AssemblyLoadContext context, AssemblyName name)
{
    var dependencyPath = resolver.ResolveAssemblyToPath(name);
    return dependencyPath is null ? null : context.LoadFromAssemblyPath(dependencyPath);
}

internal sealed class SerializerProbe
{
    public string Name { get; set; } = string.Empty;

    public int Count { get; set; }
}

using System.Reflection;
using System.Runtime.Loader;
using DalamudActCompat.ActRuntime;
using DalamudActCompat.Protocol;
using Mono.Cecil;

if (args.Length is < 1 or > 2)
{
    throw new ArgumentException(
        "Pass the path to an installed Triggernometry.dll and optionally PostNamazu.dll.");
}

var assemblyPath = Path.GetFullPath(args[0]);
if (!File.Exists(assemblyPath))
{
    throw new FileNotFoundException("Triggernometry assembly was not found.", assemblyPath);
}

LegacyResourceCompatibility.EnsureLegacyResourceDecoderAvailable();
var overlayTemplates = SelfHostedActRuntime.ProbeOverlayTemplates();
if (!overlayTemplates.Any(template => template.Name == "Kagerou") ||
    !overlayTemplates.Any(template => template.Name.Contains("Cactbot DPS", StringComparison.Ordinal)))
{
    throw new InvalidOperationException(
        "OverlayPlugin built-in HTML templates were not exposed by the runtime.");
}

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
    AssertPostNamazuSemanticPayloads();

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
    AssertTriggernometrySelfTestScriptCompiles(implementation);

    var proxyType = assembly.GetType("TriggernometryProxy.ProxyPlugin", throwOnError: true)!;
    _ = Activator.CreateInstance(proxyType)
        ?? throw new InvalidOperationException("Triggernometry proxy could not be constructed.");

    if (args.Length == 2)
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

static void AssertPostNamazuSemanticPayloads()
{
    var mark = PostNamazuSemanticActions.ParseMark(
        "{\"ActorID\":0xE0000000,\"MarkType\":\"attack8\",\"LocalOnly\":true}");
    if (mark.ActorId != PostNamazuSemanticActions.ClearActorId ||
        mark.MarkerIndex != 16 ||
        !mark.LocalOnly)
    {
        throw new InvalidOperationException(
            "PostNamazu hexadecimal ActorID or marker mapping compatibility failed.");
    }

    var waymarks = PostNamazuSemanticActions.ParseWaymarks(
        "{\"LocalOnly\":true,\"A\":{\"X\":1.25,\"Y\":2.5,\"Z\":-3.75,\"Active\":true},\"B\":{}}");
    if (!waymarks.LocalOnly || waymarks.ClearAll || waymarks.Updates.Count != 2 ||
        !waymarks.Updates[0].Active || waymarks.Updates[1].Active ||
        waymarks.Updates[0].Position.X != 1.25f ||
        waymarks.Updates[0].Position.Y != 2.5f ||
        waymarks.Updates[0].Position.Z != -3.75f)
    {
        throw new InvalidOperationException("PostNamazu waymark JSON compatibility failed.");
    }

    if (!PostNamazuSemanticActions.ParseWaymarks("clear").ClearAll)
    {
        throw new InvalidOperationException("PostNamazu clear command compatibility failed.");
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

static void AssertTriggernometrySelfTestScriptCompiles(Assembly implementation)
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
                     [selfTestScript, options, null, null])
                 ?? throw new InvalidOperationException("Roslyn did not create the SelfTest script.");
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
            "Triggernometry SelfTest script did not compile:" + Environment.NewLine +
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

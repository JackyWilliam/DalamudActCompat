using System.Reflection;
using System.Runtime.Loader;
using DalamudActCompat.ActRuntime;

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

    if (implementation.GetManifestResourceNames()
        .Count(name => name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase)) < 10)
    {
        throw new InvalidOperationException(
            "The patched Triggernometry implementation is missing expected UI resources.");
    }

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

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

var resolver = new AssemblyDependencyResolver(assemblyPath);
AssemblyLoadContext.Default.Resolving += ResolveDependency;
try
{
    var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
    LegacyResourceCompatibility.ProbeEmbeddedResources(
        assembly,
        AssemblyLoadContext.Default);
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

    var proxyType = assembly.GetType("TriggernometryProxy.ProxyPlugin", throwOnError: true)!;
    _ = Activator.CreateInstance(proxyType)
        ?? throw new InvalidOperationException("Triggernometry proxy could not be constructed.");

    if (args.Length == 2)
    {
        var postNamazuPath = Path.GetFullPath(args[1]);
        var postNamazu = LegacyResourceCompatibility.LoadPostNamazuWithClipboardCompatibility(
            postNamazuPath,
            AssemblyLoadContext.Default);
        _ = postNamazu.GetType("PostNamazu.PostNamazu", throwOnError: true);
    }

    Console.WriteLine("Safe NRBF conversion and Triggernometry embedded-resource probes passed.");
}
finally
{
    AssemblyLoadContext.Default.Resolving -= ResolveDependency;
}

Assembly? ResolveDependency(AssemblyLoadContext context, AssemblyName name)
{
    var dependencyPath = resolver.ResolveAssemblyToPath(name);
    return dependencyPath is null ? null : context.LoadFromAssemblyPath(dependencyPath);
}

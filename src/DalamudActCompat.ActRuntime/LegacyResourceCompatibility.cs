using System.Collections;
using System.IO.Compression;
using System.Reflection;
using System.Resources;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Runtime.Serialization.Formatters.Binary;
using Mono.Cecil;
using System.Resources.Extensions;

[assembly: InternalsVisibleTo("DalamudActCompat.LegacyResourceSmokeTests")]

namespace DalamudActCompat.ActRuntime;

internal static class LegacyResourceCompatibility
{
    private const string BinaryFormatterSwitch =
        "System.Runtime.Serialization.EnableUnsafeBinaryFormatterSerialization";
    private const string ResourceBinaryFormatterSwitch =
        "System.Resources.Extensions.UseBinaryFormatter";

#pragma warning disable CA2255
    [ModuleInitializer]
    internal static void Initialize()
    {
        AppContext.SetSwitch(BinaryFormatterSwitch, true);
        AppContext.SetSwitch(ResourceBinaryFormatterSwitch, true);
    }
#pragma warning restore CA2255

    internal static void EnsureBinaryFormatterAvailable()
    {
        AppContext.SetSwitch(BinaryFormatterSwitch, true);
        AppContext.SetSwitch(ResourceBinaryFormatterSwitch, true);
        AppContext.TryGetSwitch(BinaryFormatterSwitch, out var enabled);
        if (!enabled)
        {
            throw new InvalidOperationException(
                $"Legacy resource compatibility switch {BinaryFormatterSwitch} is disabled.");
        }

        AppContext.TryGetSwitch(ResourceBinaryFormatterSwitch, out var resourceFormatterEnabled);
        if (!resourceFormatterEnabled)
        {
            throw new InvalidOperationException(
                $"Legacy resource compatibility switch {ResourceBinaryFormatterSwitch} is disabled.");
        }

        var payload = new ProbePayload("DalamudActCompat", 1);
        using var stream = new MemoryStream();
#pragma warning disable SYSLIB0011
        var formatter = new BinaryFormatter();
        formatter.Serialize(stream, payload);
        stream.Position = 0;
        var restored = formatter.Deserialize(stream) as ProbePayload;
#pragma warning restore SYSLIB0011
        if (restored != payload)
        {
            throw new InvalidOperationException(
                "System.Runtime.Serialization.Formatters loaded, but its BinaryFormatter smoke test returned invalid data.");
        }
    }

    internal static void ProbeEmbeddedResources(Assembly assembly, AssemblyLoadContext loadContext)
    {
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
                        $"Triggernometry resource {resourceName}/{key} cannot be deserialized by .NET 10 " +
                        "after conversion to DeserializingResourceReader format.",
                        ex);
                }
            }
        }
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
        return loadContext.LoadFromStream(patched);
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
#pragma warning disable SYSLIB0011
                        writer.AddBinaryFormattedResource(key, payload, typeName);
#pragma warning restore SYSLIB0011
                    }
                }
            }

            var index = definition.MainModule.Resources.IndexOf(resource);
            definition.MainModule.Resources[index] =
                new EmbeddedResource(resource.Name, resource.Attributes, output.ToArray());
        }

        var patched = new MemoryStream();
        definition.Write(patched);
        patched.Position = 0;
        return patched;
    }

    [Serializable]
    private sealed record ProbePayload(string Name, int Version);
}

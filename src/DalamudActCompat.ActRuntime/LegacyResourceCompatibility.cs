using System.Collections;
using System.ComponentModel;
using System.Drawing;
using System.Formats.Nrbf;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Resources;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Runtime.Serialization;
using System.Windows.Forms;
using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Resources.Extensions;

[assembly: InternalsVisibleTo("DalamudActCompat.LegacyResourceSmokeTests")]

namespace DalamudActCompat.ActRuntime;

public static class LegacyResourceCompatibility
{
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
        var patched = new MemoryStream();
        definition.Write(patched);
        patched.Position = 0;
        return patched;
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

using System.Diagnostics;
using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using DalamudActCompat.ActRuntime;
using DalamudActCompat.Infrastructure.Storage;

namespace DalamudActCompat.Compatibility.PluginHost;

public sealed partial class ActPluginPackageInstaller
{
    private const int MaximumEntryCount = 2048;
    private const long MaximumExpandedBytes = 256L * 1024 * 1024;
    private readonly PluginPaths paths;
    private readonly SemaphoreSlim installLock = new(1, 1);
    private static readonly KnownPlugin[] KnownPlugins =
    [
        new("cactbotself", "CactbotSelf / MoreLogLine", "CactbotSelf.dll", "CactbotSelf.CactbotSelf"),
        new("postnamazu", "PostNamazu", "PostNamazu.dll", "PostNamazu.PostNamazu"),
        new("act.foxtts", "ACT.FoxTTS", "ACT.FoxTTS.dll", "ACT.FoxTTS.FoxTTSPlugin"),
        new("triggernometry", "Triggernometry", "Triggernometry.dll", "TriggernometryProxy.ProxyPlugin"),
        new("silverdasher", "银山雀儿 / SilverDasher", "SilverDasher.dll", "SilverDasher.Loader.Loader"),
        new("matcha", "抹茶 / Cafe.Matcha", "Cafe.Matcha.dll", "Cafe.Matcha.MatchaInit"),
    ];

    public ActPluginPackageInstaller(PluginPaths paths)
    {
        this.paths = paths;
    }

    public static bool IsSpecializedPluginId(string pluginId)
        => KnownPlugins.Any(plugin => string.Equals(
            plugin.Id,
            pluginId,
            StringComparison.OrdinalIgnoreCase));

    public async Task<InstalledActPlugin> InstallAsync(
        string packagePath,
        CancellationToken cancellationToken)
    {
        var fullPackagePath = Path.GetFullPath(packagePath);
        if (!File.Exists(fullPackagePath))
        {
            throw new FileNotFoundException("ACT plugin package was not found.", fullPackagePath);
        }

        await installLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? stagingDirectory = null;
        string? installDirectory = null;
        string? backupDirectory = null;
        try
        {
            paths.EnsureCreated();
            if (!Directory.Exists(paths.ActPluginDirectory))
            {
                throw new DirectoryNotFoundException(
                    "The ACT plugin folder has not been created. Choose it from ACT Compat Settings first.");
            }

            stagingDirectory = Path.Combine(
                paths.PluginStagingDirectory,
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stagingDirectory);
            switch (Path.GetExtension(fullPackagePath).ToLowerInvariant())
            {
                case ".zip":
                    using (var archive = ZipFile.OpenRead(fullPackagePath))
                    {
                        ExtractSafely(archive, stagingDirectory);
                    }

                    break;
                case ".dll":
                    StageLooseDll(fullPackagePath, stagingDirectory);
                    break;
                default:
                    throw new InvalidDataException("Select an ACT plugin .dll or .zip file.");
            }

            CreateManifestWhenMissing(stagingDirectory);
            var manifest = await ReadManifestAsync(stagingDirectory, cancellationToken).ConfigureAwait(false);
            ValidateManifest(manifest, stagingDirectory);
            if (!IsSpecializedPluginId(manifest.Id))
            {
                var entryAssembly = Path.Combine(stagingDirectory, manifest.EntryAssembly);
                ValidateGenericEntryPoint(manifest, stagingDirectory);
                manifest.RequestedCapabilities = GetRequestedCapabilities(manifest)
                    .Concat(InferCapabilities(entryAssembly))
                    .Distinct()
                    .OrderBy(static capability => capability)
                    .Select(static capability => capability.ToString())
                    .ToArray();
                await File.WriteAllTextAsync(
                        Path.Combine(stagingDirectory, ActPluginManifest.FileName),
                        JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            installDirectory = Path.Combine(paths.ActPluginDirectory, manifest.Id);
            if (Directory.Exists(installDirectory))
            {
                backupDirectory = Path.Combine(
                    paths.PluginBackupDirectory,
                    $"{manifest.Id}-{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}");
                Directory.CreateDirectory(paths.PluginBackupDirectory);
                Directory.Move(installDirectory, backupDirectory);
            }

            Directory.Move(stagingDirectory, installDirectory);
            return CreateInstalledPlugin(manifest, installDirectory, enabled: true);
        }
        catch
        {
            if (stagingDirectory is not null && Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, true);
            }

            if (installDirectory is not null &&
                backupDirectory is not null &&
                !Directory.Exists(installDirectory) &&
                Directory.Exists(backupDirectory))
            {
                Directory.Move(backupDirectory, installDirectory);
            }

            throw;
        }
        finally
        {
            installLock.Release();
        }
    }

    public IReadOnlyList<InstalledActPlugin> Discover(ISet<string> disabledPluginIds)
    {
        if (!Directory.Exists(paths.ActPluginDirectory))
        {
            return [];
        }

        string[] directories;
        try
        {
            // Installation swaps directories atomically. Snapshot first so a concurrent
            // move/delete cannot throw later from a deferred filesystem enumerator.
            directories = Directory.GetDirectories(paths.ActPluginDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var plugins = new List<InstalledActPlugin>();
        foreach (var directory in directories)
        {
            try
            {
                var manifest = ReadManifest(directory);
                ValidateManifest(manifest, directory);
                plugins.Add(CreateInstalledPlugin(
                    manifest,
                    directory,
                    !disabledPluginIds.Contains(manifest.Id)));
            }
            catch
            {
                // Invalid packages stay on disk for diagnosis but are not load candidates.
            }
        }

        return plugins
            .OrderBy(plugin => GetPluginOrder(plugin.Manifest.Id))
            .ThenBy(plugin => plugin.Manifest.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static InstalledActPlugin CreateInstalledPlugin(
        ActPluginManifest manifest,
        string installDirectory,
        bool enabled)
    {
        string? detectedVersion = null;
        try
        {
            var entryAssembly = Path.Combine(installDirectory, manifest.EntryAssembly);
            var versionAssembly = string.Equals(
                    manifest.Id,
                    "silverdasher",
                    StringComparison.OrdinalIgnoreCase)
                ? Directory
                      .EnumerateFiles(
                          installDirectory,
                          "SilverDasher.Core.dll",
                          SearchOption.AllDirectories)
                      .FirstOrDefault() ?? entryAssembly
                : entryAssembly;
            // SilverDasher's loader and feature-bearing Core intentionally use different versions.
            detectedVersion = FileVersionInfo.GetVersionInfo(versionAssembly).FileVersion;
        }
        catch (Exception ex) when (
            ex is ArgumentException or FileNotFoundException or IOException or UnauthorizedAccessException)
        {
            // Manifest metadata remains the safe fallback when Windows cannot inspect a DLL.
        }

        return new InstalledActPlugin(
            manifest,
            installDirectory,
            enabled,
            detectedVersion);
    }

    public async Task<string?> UninstallAsync(
        string pluginId,
        CancellationToken cancellationToken)
    {
        if (!PluginIdPattern().IsMatch(pluginId))
        {
            throw new ArgumentException("Plugin id is invalid.", nameof(pluginId));
        }

        await installLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            paths.EnsureCreated();
            var installDirectory = Path.GetFullPath(
                Path.Combine(paths.ActPluginDirectory, pluginId));
            var pluginRoot = Path.GetFullPath(paths.ActPluginDirectory)
                             + Path.DirectorySeparatorChar;
            if (!installDirectory.StartsWith(
                    pluginRoot,
                    StringComparison.OrdinalIgnoreCase) ||
                !Directory.Exists(installDirectory))
            {
                return null;
            }

            var manifest = ReadManifest(installDirectory);
            ValidateManifest(manifest, installDirectory);
            if (!string.Equals(
                    manifest.Id,
                    pluginId,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "Plugin directory and manifest ids do not match.");
            }

            var backupDirectory = Path.Combine(
                paths.PluginBackupDirectory,
                $"{manifest.Id}-removed-{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}");
            // Moving instead of deleting keeps an explicit user uninstall recoverable while
            // immediately removing the package from discovery and Host startup.
            Directory.Move(installDirectory, backupDirectory);
            return backupDirectory;
        }
        finally
        {
            installLock.Release();
        }
    }

    public static IReadOnlyList<ActCapability> GetRequestedCapabilities(
        ActPluginManifest manifest)
        => (manifest.RequestedCapabilities ?? [])
            .Select(value => Enum.TryParse<ActCapability>(value, ignoreCase: true, out var capability)
                ? capability
                : (ActCapability?)null)
            .Where(static capability => capability.HasValue)
            .Select(static capability => capability!.Value)
            .Distinct()
            .OrderBy(static capability => capability)
            .ToArray();

    private static int GetPluginOrder(string pluginId)
        => pluginId.ToLowerInvariant() switch
        {
            "silverdasher" => 1,
            "matcha" => 2,
            _ => 0,
        };

    private static void ExtractSafely(ZipArchive archive, string stagingDirectory)
    {
        if (archive.Entries.Count > MaximumEntryCount)
        {
            throw new InvalidDataException($"Plugin package contains more than {MaximumEntryCount} entries.");
        }

        var expandedBytes = archive.Entries.Sum(entry => entry.Length);
        if (expandedBytes > MaximumExpandedBytes)
        {
            throw new InvalidDataException($"Expanded plugin package exceeds {MaximumExpandedBytes} bytes.");
        }

        var root = Path.GetFullPath(stagingDirectory) + Path.DirectorySeparatorChar;
        foreach (var entry in archive.Entries)
        {
            var destination = Path.GetFullPath(Path.Combine(stagingDirectory, entry.FullName));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Package entry escapes its install directory: {entry.FullName}");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: false);
        }
    }

    private static void CreateManifestWhenMissing(string stagingDirectory)
    {
        var manifestPath = Path.Combine(stagingDirectory, ActPluginManifest.FileName);
        if (File.Exists(manifestPath))
        {
            return;
        }

        foreach (var known in KnownPlugins)
        {
            var assembly = Directory
                .EnumerateFiles(stagingDirectory, known.AssemblyName, SearchOption.AllDirectories)
                .FirstOrDefault();
            if (assembly is null)
            {
                continue;
            }

            var relativeAssembly = Path.GetRelativePath(stagingDirectory, assembly);
            var versionAssembly = known.Id == "silverdasher"
                ? Directory
                    .EnumerateFiles(stagingDirectory, "SilverDasher.Core.dll", SearchOption.AllDirectories)
                    .FirstOrDefault()
                : null;
            var version = System.Diagnostics.FileVersionInfo
                              .GetVersionInfo(versionAssembly ?? assembly)
                              .FileVersion
                          ?? "unknown";
            var manifest = new ActPluginManifest
            {
                Id = known.Id,
                Name = known.Name,
                Version = version,
                SourceSha256 = ComputeSha256(assembly),
                HostApiVersion = 1,
                EntryAssembly = relativeAssembly,
                EntryType = known.EntryType,
            };
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions
            {
                WriteIndented = true,
            }));
            return;
        }

        var candidates = DiscoverActPluginEntryPoints(stagingDirectory);
        if (candidates.Count != 1)
        {
            throw new InvalidDataException(
                candidates.Count == 0
                    ? $"Package has no {ActPluginManifest.FileName} and no managed type implementing Advanced_Combat_Tracker.IActPluginV1 was found."
                    : $"Package contains {candidates.Count} ACT plugin entry points. Add {ActPluginManifest.FileName} to select one explicitly.");
        }

        var candidate = candidates[0];
        var generatedManifest = new ActPluginManifest
        {
            Id = CreatePluginId(candidate.AssemblyName),
            Name = candidate.AssemblyName,
            Version = candidate.Version,
            SourceSha256 = ComputeSha256(candidate.AssemblyPath),
            HostApiVersion = 1,
            EntryAssembly = Path.GetRelativePath(stagingDirectory, candidate.AssemblyPath),
            EntryType = candidate.EntryType,
            RequestedCapabilities = InferCapabilities(candidate.AssemblyPath)
                .Select(static capability => capability.ToString())
                .ToArray(),
        };
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(generatedManifest, new JsonSerializerOptions
        {
            WriteIndented = true,
        }));
    }

    private static void StageLooseDll(string dllPath, string stagingDirectory)
    {
        var fileName = Path.GetFileName(dllPath);
        var known = KnownPlugins.FirstOrDefault(
            plugin => string.Equals(plugin.AssemblyName, fileName, StringComparison.OrdinalIgnoreCase));
        if (known?.Id is "silverdasher" or "matcha")
        {
            throw new InvalidDataException(
                $"{known.Name} must be installed from its complete ZIP package so companion data files are preserved.");
        }

        File.Copy(dllPath, Path.Combine(stagingDirectory, fileName));
        var sourceDirectory = Path.GetDirectoryName(dllPath)!;
        CopyManagedDependencies(dllPath, sourceDirectory, stagingDirectory);
        CopyCompanionWhenPresent(sourceDirectory, stagingDirectory, $"{fileName}.config");
        CopyCompanionWhenPresent(
            sourceDirectory,
            stagingDirectory,
            $"{Path.GetFileNameWithoutExtension(fileName)}.pdb");

        if (known?.Id == "triggernometry")
        {
            foreach (var translation in Directory.EnumerateFiles(sourceDirectory, "*.triglations.xml"))
            {
                File.Copy(
                    translation,
                    Path.Combine(stagingDirectory, Path.GetFileName(translation)),
                    overwrite: false);
            }
        }
    }

    private static IReadOnlyList<ActPluginEntryPoint> DiscoverActPluginEntryPoints(
        string stagingDirectory)
    {
        var candidates = new List<ActPluginEntryPoint>();
        foreach (var assemblyPath in Directory.EnumerateFiles(
                     stagingDirectory,
                     "*.dll",
                     SearchOption.AllDirectories))
        {
            try
            {
                using var stream = File.OpenRead(assemblyPath);
                using var peReader = new PEReader(stream);
                if (!peReader.HasMetadata)
                {
                    continue;
                }

                var metadata = peReader.GetMetadataReader();
                var assembly = metadata.GetAssemblyDefinition();
                var assemblyName = metadata.GetString(assembly.Name);
                foreach (var typeHandle in metadata.TypeDefinitions)
                {
                    var type = metadata.GetTypeDefinition(typeHandle);
                    if ((type.Attributes & System.Reflection.TypeAttributes.Abstract) != 0 ||
                        !ImplementsActPluginInterface(metadata, typeHandle, []))
                    {
                        continue;
                    }

                    var typeNamespace = metadata.GetString(type.Namespace);
                    var typeName = metadata.GetString(type.Name);
                    candidates.Add(new ActPluginEntryPoint(
                        Path.GetFullPath(assemblyPath),
                        assemblyName,
                        assembly.Version.ToString(),
                        JoinTypeName(typeNamespace, typeName)));
                }
            }
            catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException)
            {
                // Native DLLs and managed netmodules are package data, not ACT entry assemblies.
            }
        }

        return candidates;
    }

    private static bool IsActPluginInterface(
        MetadataReader metadata,
        InterfaceImplementationHandle implementationHandle)
    {
        var implementation = metadata.GetInterfaceImplementation(implementationHandle);
        return GetTypeName(metadata, implementation.Interface) ==
               "Advanced_Combat_Tracker.IActPluginV1";
    }

    private static bool ImplementsActPluginInterface(
        MetadataReader metadata,
        TypeDefinitionHandle typeHandle,
        HashSet<TypeDefinitionHandle> visited)
    {
        if (!visited.Add(typeHandle))
        {
            return false;
        }

        var type = metadata.GetTypeDefinition(typeHandle);
        if (type.GetInterfaceImplementations().Any(handle =>
                IsActPluginInterface(metadata, handle)))
        {
            return true;
        }

        return !type.BaseType.IsNil &&
               type.BaseType.Kind == HandleKind.TypeDefinition &&
               ImplementsActPluginInterface(
                   metadata,
                   (TypeDefinitionHandle)type.BaseType,
                   visited);
    }

    private static void ValidateGenericEntryPoint(
        ActPluginManifest manifest,
        string stagingDirectory)
    {
        var entryAssembly = Path.GetFullPath(Path.Combine(
            stagingDirectory,
            manifest.EntryAssembly));
        var candidates = DiscoverActPluginEntryPoints(stagingDirectory);
        var matchingEntryPoint = candidates.Any(candidate =>
            string.Equals(candidate.AssemblyPath, entryAssembly, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(candidate.EntryType, manifest.EntryType, StringComparison.Ordinal));
        if (!matchingEntryPoint)
        {
            var discovered = candidates.Count == 0
                ? "none"
                : string.Join(", ", candidates.Select(static candidate => candidate.EntryType));
            var interfaceDetail = DescribeEntryTypeInterfaces(entryAssembly, manifest.EntryType);
            throw new InvalidDataException(
                $"Generic plugin entry type '{manifest.EntryType}' does not implement Advanced_Combat_Tracker.IActPluginV1 in '{manifest.EntryAssembly}'. Discovered: {discovered}. Interfaces: {interfaceDetail}.");
        }
    }

    private static string DescribeEntryTypeInterfaces(string assemblyPath, string entryType)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        var metadata = peReader.GetMetadataReader();
        foreach (var handle in metadata.TypeDefinitions)
        {
            var type = metadata.GetTypeDefinition(handle);
            var name = JoinTypeName(
                metadata.GetString(type.Namespace),
                metadata.GetString(type.Name));
            if (!string.Equals(name, entryType, StringComparison.Ordinal))
            {
                continue;
            }

            return string.Join(", ", type.GetInterfaceImplementations().Select(implementationHandle =>
            {
                var implementation = metadata.GetInterfaceImplementation(implementationHandle);
                return $"{implementation.Interface.Kind}:{GetTypeName(metadata, implementation.Interface)}";
            }));
        }

        return "entry type not found";
    }

    private static string GetTypeName(MetadataReader metadata, EntityHandle handle)
    {
        return handle.Kind switch
        {
            HandleKind.TypeReference => GetTypeReferenceName(
                metadata,
                (TypeReferenceHandle)handle),
            HandleKind.TypeDefinition => GetTypeDefinitionName(
                metadata,
                (TypeDefinitionHandle)handle),
            _ => string.Empty,
        };
    }

    private static string GetTypeReferenceName(
        MetadataReader metadata,
        TypeReferenceHandle handle)
    {
        var type = metadata.GetTypeReference(handle);
        return JoinTypeName(metadata.GetString(type.Namespace), metadata.GetString(type.Name));
    }

    private static string GetTypeDefinitionName(
        MetadataReader metadata,
        TypeDefinitionHandle handle)
    {
        var type = metadata.GetTypeDefinition(handle);
        return JoinTypeName(metadata.GetString(type.Namespace), metadata.GetString(type.Name));
    }

    private static string JoinTypeName(string typeNamespace, string typeName)
        => string.IsNullOrWhiteSpace(typeNamespace)
            ? typeName
            : $"{typeNamespace}.{typeName}";

    private static string CreatePluginId(string assemblyName)
    {
        var id = Regex.Replace(
                assemblyName.ToLowerInvariant(),
                "[^a-z0-9._-]+",
                "-",
                RegexOptions.CultureInvariant)
            .Trim('.', '-', '_');
        if (id.Length < 2)
        {
            id = $"plugin-{id}";
        }

        return id.Length <= 64 ? id : id[..64].TrimEnd('.', '-', '_');
    }

    private static IReadOnlyList<ActCapability> InferCapabilities(string assemblyPath)
    {
        var inferred = new HashSet<ActCapability>
        {
            // These are inherent to the ACT plugin contract, not guesses from implementation details.
            ActCapability.ReadCombatLogs,
            ActCapability.ReadLocalConfiguration,
        };
        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        var metadata = peReader.GetMetadataReader();
        var referencedTypes = metadata.TypeReferences
            .Select(handle => GetTypeName(metadata, handle))
            .ToArray();
        var referencedMembers = metadata.MemberReferences
            .Select(handle => metadata.GetString(metadata.GetMemberReference(handle).Name))
            .ToArray();
        if (referencedTypes.Any(static name => name.StartsWith("System.Net.", StringComparison.Ordinal)))
        {
            inferred.Add(ActCapability.NetworkRequest);
        }

        if (referencedTypes.Any(static name =>
                name is "System.Diagnostics.Process" or "System.Diagnostics.ProcessStartInfo"))
        {
            inferred.Add(ActCapability.LaunchExternalProcess);
        }

        if (referencedTypes.Any(static name => name.StartsWith("System.Speech.", StringComparison.Ordinal)) ||
            referencedMembers.Any(static name => name.Contains("PlayTts", StringComparison.OrdinalIgnoreCase)))
        {
            inferred.Add(ActCapability.TextToSpeech);
        }

        if (referencedTypes.Any(static name => name is "System.Windows.Forms.Clipboard"))
        {
            inferred.Add(ActCapability.Clipboard);
        }

        if (referencedTypes.Any(static name => name is "System.Windows.Forms.SendKeys"))
        {
            inferred.Add(ActCapability.GameCommand);
        }

        if (referencedTypes.Any(static name =>
                name is "System.IO.File" or "System.IO.Directory" or
                    "System.IO.FileStream" or "System.IO.StreamWriter" or
                    "System.IO.FileSystemWatcher"))
        {
            inferred.Add(ActCapability.WriteFiles);
        }

        if (referencedTypes.Any(static name =>
                name.StartsWith("System.Reflection.Emit.", StringComparison.Ordinal) ||
                name.Contains("CodeDomProvider", StringComparison.Ordinal) ||
                name.StartsWith("System.Management.Automation.", StringComparison.Ordinal)))
        {
            inferred.Add(ActCapability.HighRiskScript);
        }

        var nativeMethods = metadata.MethodDefinitions
            .Where(handle =>
                (metadata.GetMethodDefinition(handle).Attributes &
                 System.Reflection.MethodAttributes.PinvokeImpl) != 0)
            .Select(handle => metadata.GetString(metadata.GetMethodDefinition(handle).Name))
            .ToArray();
        if (nativeMethods.Length > 0)
        {
            inferred.Add(ActCapability.NativeSystemAccess);
        }

        if (nativeMethods.Any(static name => name is
                "OpenProcess" or "ReadProcessMemory" or "WriteProcessMemory" or
                "VirtualQueryEx" or "VirtualProtectEx"))
        {
            inferred.Add(ActCapability.NativeGameMemory);
        }

        return inferred.OrderBy(static capability => capability).ToArray();
    }

    private static void CopyManagedDependencies(
        string entryDll,
        string sourceDirectory,
        string stagingDirectory)
    {
        var pending = new Queue<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        pending.Enqueue(entryDll);
        while (pending.Count > 0 && visited.Count < 128)
        {
            var assemblyPath = pending.Dequeue();
            if (!visited.Add(Path.GetFullPath(assemblyPath)))
            {
                continue;
            }

            try
            {
                using var stream = File.OpenRead(assemblyPath);
                using var reader = new PEReader(stream);
                if (!reader.HasMetadata)
                {
                    continue;
                }

                var metadata = reader.GetMetadataReader();
                foreach (var referenceHandle in metadata.AssemblyReferences)
                {
                    var reference = metadata.GetAssemblyReference(referenceHandle);
                    var dependencyName = $"{metadata.GetString(reference.Name)}.dll";
                    var dependencyPath = Path.Combine(sourceDirectory, dependencyName);
                    var destinationPath = Path.Combine(stagingDirectory, dependencyName);
                    if (!File.Exists(dependencyPath) || File.Exists(destinationPath))
                    {
                        continue;
                    }

                    File.Copy(dependencyPath, destinationPath);
                    pending.Enqueue(dependencyPath);
                }

                for (var row = 1; row <= metadata.GetTableRowCount(TableIndex.ModuleRef); row++)
                {
                    var moduleHandle = MetadataTokens.ModuleReferenceHandle(row);
                    var moduleName = metadata.GetString(
                        metadata.GetModuleReference(moduleHandle).Name);
                    if (string.IsNullOrWhiteSpace(moduleName) ||
                        Path.GetFileName(moduleName) != moduleName)
                    {
                        continue;
                    }

                    var modulePath = Path.Combine(sourceDirectory, moduleName);
                    var destinationPath = Path.Combine(stagingDirectory, moduleName);
                    if (File.Exists(modulePath) && !File.Exists(destinationPath))
                    {
                        // P/Invoke companions are not AssemblyReferences but are still
                        // required when a standalone DLL was usable in the ACT folder.
                        File.Copy(modulePath, destinationPath);
                    }
                }
            }
            catch (BadImageFormatException)
            {
                // Validation later reports an invalid plugin; dependency discovery is best effort.
            }
        }
    }

    private static void CopyCompanionWhenPresent(
        string sourceDirectory,
        string stagingDirectory,
        string fileName)
    {
        var source = Path.Combine(sourceDirectory, fileName);
        if (File.Exists(source))
        {
            File.Copy(source, Path.Combine(stagingDirectory, fileName), overwrite: false);
        }
    }

    private static string ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static async Task<ActPluginManifest> ReadManifestAsync(
        string directory,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(Path.Combine(directory, ActPluginManifest.FileName));
        return await JsonSerializer.DeserializeAsync<ActPluginManifest>(
                   stream,
                   cancellationToken: cancellationToken).ConfigureAwait(false)
               ?? throw new InvalidDataException("Plugin manifest is empty.");
    }

    private static ActPluginManifest ReadManifest(string directory)
    {
        using var stream = File.OpenRead(Path.Combine(directory, ActPluginManifest.FileName));
        return JsonSerializer.Deserialize<ActPluginManifest>(stream)
               ?? throw new InvalidDataException("Plugin manifest is empty.");
    }

    private static void ValidateManifest(ActPluginManifest manifest, string directory)
    {
        if (!PluginIdPattern().IsMatch(manifest.Id))
        {
            throw new InvalidDataException("Plugin id must contain only lowercase letters, digits, dots, dashes, or underscores.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Name) ||
            string.IsNullOrWhiteSpace(manifest.Version) ||
            string.IsNullOrWhiteSpace(manifest.EntryAssembly) ||
            string.IsNullOrWhiteSpace(manifest.EntryType))
        {
            throw new InvalidDataException("Plugin manifest is missing required fields.");
        }

        if (manifest.HostApiVersion != 1)
        {
            throw new InvalidDataException($"Unsupported host API version: {manifest.HostApiVersion}");
        }

        var root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        var entryAssembly = Path.GetFullPath(Path.Combine(directory, manifest.EntryAssembly));
        if (!entryAssembly.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(entryAssembly))
        {
            throw new InvalidDataException("Plugin entry assembly is missing or outside the package.");
        }

        if (string.Equals(manifest.Id, "silverdasher", StringComparison.OrdinalIgnoreCase))
        {
            var entryDirectory = Path.GetDirectoryName(entryAssembly)!;
            string[] requiredFiles =
            [
                Path.Combine(entryDirectory, "libs", "SilverDasher.Core.dll"),
                Path.Combine(entryDirectory, "data", "opcodes.json"),
                Path.Combine(entryDirectory, "data", "territories.json"),
            ];
            if (!string.Equals(
                    Path.GetFileName(entryAssembly),
                    "SilverDasher.dll",
                    StringComparison.OrdinalIgnoreCase) ||
                requiredFiles.Any(path => !File.Exists(path)))
            {
                throw new InvalidDataException(
                    "SilverDasher package must preserve its loader plus the sibling libs and data directories.");
            }
        }
        else if (string.Equals(manifest.Id, "matcha", StringComparison.OrdinalIgnoreCase))
        {
            var entryDirectory = Path.GetDirectoryName(entryAssembly)!;
            string[] requiredFiles =
            [
                Path.Combine(entryDirectory, "data", "dynamic-event.json"),
                Path.Combine(entryDirectory, "data", "fate.json"),
                Path.Combine(entryDirectory, "data", "instance.json"),
                Path.Combine(entryDirectory, "data", "patch.json"),
                Path.Combine(entryDirectory, "data", "roulette.json"),
                Path.Combine(entryDirectory, "data", "template.json"),
                Path.Combine(entryDirectory, "data", "territory.json"),
                Path.Combine(entryDirectory, "data", "type.json"),
                Path.Combine(entryDirectory, "data", "world.json"),
                Path.Combine(entryDirectory, "upstream", "Cafe.Matcha.Upstream.dll"),
                Path.Combine(entryDirectory, "upstream", "Cafe.Matcha.Runtime.bin"),
            ];
            if (!string.Equals(
                    Path.GetFileName(entryAssembly),
                    "Cafe.Matcha.dll",
                    StringComparison.OrdinalIgnoreCase) ||
                requiredFiles.Any(path => !File.Exists(path)))
            {
                throw new InvalidDataException(
                    "Matcha package must preserve its entry assembly, complete data directory, and upstream compatibility companions.");
            }
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{1,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex PluginIdPattern();

    private sealed record KnownPlugin(string Id, string Name, string AssemblyName, string EntryType);

    private sealed record ActPluginEntryPoint(
        string AssemblyPath,
        string AssemblyName,
        string Version,
        string EntryType);
}

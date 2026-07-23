using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using DalamudActCompat.Compatibility.PluginHost;
using DalamudActCompat.Infrastructure.Storage;

var testRoot = Path.Combine(Path.GetTempPath(), $"DalamudActCompat-{Guid.NewGuid():N}");
Directory.CreateDirectory(testRoot);

try
{
    var packagePath = Path.Combine(testRoot, "valid.zip");
    await CreatePackageAsync(packagePath, "example.plugin", "1.0.0");
    var paths = new PluginPaths(Path.Combine(testRoot, "config"));
    var installer = new ActPluginPackageInstaller(paths);

    var installed = await installer.InstallAsync(packagePath, CancellationToken.None);
    Assert(installed.Manifest.Id == "example.plugin", "Valid package id was not preserved.");
    Assert(File.Exists(Path.Combine(installed.InstallDirectory, "Example.Plugin.dll")), "Entry assembly was not installed.");

    await CreatePackageAsync(packagePath, "example.plugin", "1.1.0");
    installed = await installer.InstallAsync(packagePath, CancellationToken.None);
    Assert(installed.Manifest.Version == "1.1.0", "Upgrade did not replace the installed package.");
    Assert(Directory.EnumerateDirectories(paths.PluginBackupDirectory).Any(), "Upgrade did not preserve a backup.");

    var unsafePackagePath = Path.Combine(testRoot, "unsafe.zip");
    using (var archive = ZipFile.Open(unsafePackagePath, ZipArchiveMode.Create))
    {
        var entry = archive.CreateEntry("../escape.dll");
        await using var stream = entry.Open();
        await stream.WriteAsync(new byte[] { 1, 2, 3 });
    }

    try
    {
        await installer.InstallAsync(unsafePackagePath, CancellationToken.None);
        throw new InvalidOperationException("Unsafe package was accepted.");
    }
    catch (InvalidDataException)
    {
    }

    ValidateFfxivModuleInitializer();

    Console.WriteLine("Package and FFXIV_ACT_Plugin smoke tests passed.");
    return 0;
}
finally
{
    if (Directory.Exists(testRoot))
    {
        Directory.Delete(testRoot, true);
    }
}

static void ValidateFfxivModuleInitializer()
{
    var projectRoot = FindProjectRoot();
    var assemblyPath = Path.Combine(
        projectRoot,
        "src",
        "DalamudActCompat",
        "bin",
        "Release",
        "FFXIV_ACT_Plugin.dll");
    Assert(File.Exists(assemblyPath), $"FFXIV_ACT_Plugin.dll was not found at {assemblyPath}.");

    using var stream = File.OpenRead(assemblyPath);
    using var peReader = new PEReader(stream);
    var metadata = peReader.GetMetadataReader();
    foreach (var typeHandle in metadata.TypeDefinitions)
    {
        var type = metadata.GetTypeDefinition(typeHandle);
        if (metadata.GetString(type.Name) != "<Module>")
        {
            continue;
        }

        foreach (var methodHandle in type.GetMethods())
        {
            var method = metadata.GetMethodDefinition(methodHandle);
            if (metadata.GetString(method.Name) != ".cctor")
            {
                continue;
            }

            var il = peReader.GetMethodBody(method.RelativeVirtualAddress).GetILBytes();
            Assert(il is [0x2a], "FFXIV_ACT_Plugin module initializer is not a single ret instruction.");
            return;
        }
    }

    throw new InvalidOperationException("FFXIV_ACT_Plugin module initializer was not found.");
}

static string FindProjectRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "DalamudActCompat.slnx")))
        {
            return directory.FullName;
        }

        directory = directory.Parent;
    }

    throw new DirectoryNotFoundException("Could not find the DalamudActCompat project root.");
}

static async Task CreatePackageAsync(string packagePath, string id, string version)
{
    File.Delete(packagePath);
    using var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create);
    var manifestEntry = archive.CreateEntry(ActPluginManifest.FileName);
    await using (var stream = manifestEntry.Open())
    {
        await JsonSerializer.SerializeAsync(stream, new ActPluginManifest
        {
            Id = id,
            Name = "Example Plugin",
            Version = version,
            EntryAssembly = "Example.Plugin.dll",
            EntryType = "Example.Plugin.EntryPoint",
            HostApiVersion = 1,
        });
    }

    var assemblyEntry = archive.CreateEntry("Example.Plugin.dll");
    await using var assemblyStream = assemblyEntry.Open();
    await assemblyStream.WriteAsync(new byte[] { 0x4d, 0x5a });
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

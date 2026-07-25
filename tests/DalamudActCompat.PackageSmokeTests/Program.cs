using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using DalamudActCompat.ActRuntime;
using DalamudActCompat.Compatibility.PluginHost;
using DalamudActCompat.Infrastructure.Storage;
using DalamudActCompat.Parser;

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
    ValidateFfxivPluginConstructor();
    ValidateFfxivRuntimeAssemblies();
    ValidateActEncounterMapping();
    ValidateChineseCombatChatParsing();

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

static void ValidateFfxivPluginConstructor()
{
    var assemblyPath = Path.Combine(
        FindProjectRoot(),
        "vendor",
        "IINACT",
        "external_dependencies",
        "FFXIV_ACT_Plugin.dll");
    Assert(File.Exists(assemblyPath), $"FFXIV_ACT_Plugin.dll was not found at {assemblyPath}.");

    using var stream = File.OpenRead(assemblyPath);
    using var peReader = new PEReader(stream);
    var metadata = peReader.GetMetadataReader();

    foreach (var typeHandle in metadata.TypeDefinitions)
    {
        var type = metadata.GetTypeDefinition(typeHandle);
        if (metadata.GetString(type.Namespace) != "FFXIV_ACT_Plugin" ||
            metadata.GetString(type.Name) != "FFXIV_ACT_Plugin")
            continue;

        foreach (var methodHandle in type.GetMethods())
        {
            var method = metadata.GetMethodDefinition(methodHandle);
            if (metadata.GetString(method.Name) != ".ctor" || method.RelativeVirtualAddress == 0)
                continue;

            var il = peReader.GetMethodBody(method.RelativeVirtualAddress).GetILBytes();
            Assert(il is not [0x14, 0x7a],
                "FFXIV_ACT_Plugin constructor is a protected throw-null stub.");
            return;
        }
    }

    throw new InvalidOperationException("FFXIV_ACT_Plugin constructor was not found.");
}

static void ValidateFfxivRuntimeAssemblies()
{
    var runtimeDirectory = Path.Combine(
        FindProjectRoot(),
        "src",
        "DalamudActCompat",
        "bin",
        "Release");
    var assemblyNames = new[]
    {
        "FFXIV_ACT_Plugin.Common.dll",
        "FFXIV_ACT_Plugin.Config.dll",
        "FFXIV_ACT_Plugin.Logfile.dll",
        "FFXIV_ACT_Plugin.Memory.dll",
        "FFXIV_ACT_Plugin.Network.dll",
        "FFXIV_ACT_Plugin.Parse.dll",
        "FFXIV_ACT_Plugin.Resource.dll",
    };

    foreach (var assemblyName in assemblyNames)
    {
        var assemblyPath = Path.Combine(runtimeDirectory, assemblyName);
        Assert(File.Exists(assemblyPath), $"{assemblyName} was not found at {assemblyPath}.");

        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        var metadata = peReader.GetMetadataReader();
        foreach (var typeHandle in metadata.TypeDefinitions)
        {
            var type = metadata.GetTypeDefinition(typeHandle);
            foreach (var methodHandle in type.GetMethods())
            {
                var method = metadata.GetMethodDefinition(methodHandle);
                if (method.RelativeVirtualAddress == 0)
                    continue;

                var il = peReader.GetMethodBody(method.RelativeVirtualAddress).GetILBytes();
                Assert(il is not [0x14, 0x7a],
                    $"{assemblyName} contains a protected throw-null method: " +
                    $"{metadata.GetString(type.Namespace)}.{metadata.GetString(type.Name)}." +
                    $"{metadata.GetString(method.Name)}.");
            }
        }
    }
}

static void ValidateActEncounterMapping()
{
    var id = Guid.NewGuid();
    var start = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(10);
    var snapshot = new ActEncounterSnapshot(
        id,
        start,
        null,
        "Test Zone",
        "Test Enemy",
        [
            new ActCombatantSnapshot("local", "You", "SAM", true, 120_000, 2_000, 0),
            new ActCombatantSnapshot("healer", "Healer", "WHM", false, 20_000, 90_000, 1),
        ]);

    var encounter = ActEncounterMapper.Map(snapshot);
    Assert(encounter.Id == id, "ACT encounter id was not preserved.");
    Assert(encounter.StartTime == start, "ACT encounter start time was not preserved.");
    Assert(encounter.IsActive, "Active ACT encounter was mapped as finished.");
    Assert(encounter.TotalDamage == 140_000, "ACT combatant damage totals were not mapped.");
    Assert(encounter.TotalHealing == 92_000, "ACT combatant healing totals were not mapped.");
    Assert(encounter.TotalDeaths == 1, "ACT combatant deaths were not mapped.");
    Assert(encounter.Combatants.Single(static combatant => combatant.IsLocalPlayer).Name == "You",
        "ACT local player marker was not mapped.");
    Assert(encounter.JobSummaries.Count == 2, "ACT job summaries were not generated.");
}

static void ValidateChineseCombatChatParsing()
{
    Assert(
        ChineseCombatChatParser.TryParse(
            "埃斯蒂尼安丿发动攻击 \uE06F 木人受到了3714点伤害。",
            string.Empty,
            out var actor,
            out var target,
            out var damage),
        "Chinese auto-attack combat chat was not parsed.");
    Assert(actor == "埃斯蒂尼安丿" && target == "木人" && damage == 3714,
        "Chinese auto-attack fields were mapped incorrectly.");
    Assert(
        ChineseCombatChatParser.TryParse(
            "  \uE06F 暴击！ 木人受到了39870(+56%)点伤害。",
            actor,
            out var inheritedActor,
            out target,
            out damage),
        "Chinese ability damage chat was not parsed.");
    Assert(inheritedActor == actor && target == "木人" && damage == 39870,
        "Chinese ability damage fields were mapped incorrectly.");
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

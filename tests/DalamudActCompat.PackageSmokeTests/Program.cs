using System.IO.Compression;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using System.Xml;
using Advanced_Combat_Tracker;
using Dalamud.Plugin.Services;
using DalamudActCompat.ActRuntime;
using DalamudActCompat.ActRuntime.Parity;
using DalamudActCompat.Compatibility.PluginHost;
using DalamudActCompat.Compatibility.Cactbot;
using DalamudActCompat.Core.Models;
using DalamudActCompat.Core.Interfaces;
using DalamudActCompat.Core.State;
using DalamudActCompat.Encounters;
using DalamudActCompat.Fflogs;
using DalamudActCompat.Infrastructure.Diagnostics;
using DalamudActCompat.Infrastructure.Cloud;
using DalamudActCompat.Infrastructure.Logging;
using DalamudActCompat.Infrastructure.Storage;
using DalamudActCompat.Infrastructure.Ipc;
using DalamudActCompat.Infrastructure.Processes;
using DalamudActCompat.Infrastructure.Resources;
using DalamudActCompat.Meter;
using DalamudActCompat.Overlay;
using DalamudActCompat.Parser;
using DalamudActCompat.Plugin;
using DalamudActCompat.Protocol;
using DalamudActCompat.UI;
using Machina.FFXIV;
using Machina.FFXIV.Headers.Opcodes;
using Newtonsoft.Json.Linq;
using RainbowMage.OverlayPlugin.EventSources;
using RainbowMage.OverlayPlugin.MemoryProcessors;
using RainbowMage.OverlayPlugin.MemoryProcessors.InCombat;
using RainbowMage.OverlayPlugin.MemoryProcessors.Party;
using RainbowMage.OverlayPlugin.WebSocket;

var testRoot = Path.Combine(Path.GetTempPath(), $"DalamudActCompat-{Guid.NewGuid():N}");
Directory.CreateDirectory(testRoot);

try
{
    ValidateSettingsSerializerMemberTypes();
    ValidateGameRegionSelection();
    ValidateActPluginDataCompatibility();
    ValidateActCustomTriggerCompatibility();
    ValidateSynchronousActInvocation();
    ValidatePostNamazuRawLogCompatibility();
    ValidatePostNamazuOverlayHandlerResponse();
    ValidateActTtsDispatch();
    ValidateFoxTtsBridge();
    ValidateRuntimePluginStartupOrder();
    ValidateBoundedHostQueue();
    ValidateHostMemoryProtectionPolicy();
    ValidateVersionedCompatibilityHostExtraction(testRoot);
    await ValidateResourcePackLifecycleAsync(testRoot);
    ValidateSilverDasherPermissionIsolation();
    await ValidateSilverDasherNotificationIpcAsync();
    await ValidatePostNamazuHeadingIpcAsync();
    ValidateMatchaPermissionIsolation();
    await ValidateMatchaTypedIpcAsync();
    ValidateBoundedNotActQueues();
    ValidateActCallbackCircuitBreaker();
    ValidateReflectionActLoggerOverloads();
    ValidateFfxivEntityDeltaBuilder();
    ValidatePlayerIdentityResolution();
    ValidateCombatEventScoping();
    ValidateEncounterModePolicy();
    ValidateParserFrameworkStateOwnership();
    ValidateRaidDpsEstimator();
    ValidateFflogsParityReplay(testRoot);
    ValidateEncounterParticipantsSurvivePartyDeparture();
    ValidateDalamudGameStateBridge();
    ValidateCactbotSpokenAlertDefaults();
    ValidateOverlayInitialStateEvents();
    await ValidateOverlayWebSocketFatalAcceptRecoveryAsync();
    ValidateHtmlOverlayDefaults();
    if (string.Equals(
            Environment.GetEnvironmentVariable("ACTCOMPAT_WEBVIEW_INPUT_SMOKE"),
            "1",
            StringComparison.Ordinal))
    {
        await ValidateLiveHtmlOverlayInputAsync(testRoot);
    }
    ValidateParserDependencyVersions();
    ValidateUnscramblerSupportPolicy();
    ValidatePluginRepositoryMetadata();
    ValidateChinese755hOpcodes();
    ValidateMeterRows();
    ValidateMeterLayout();
    ValidateIndependentMeterWindows();
    ValidatePictoActOverlayCommands();
    ValidateEmptyEncounterFiltering();
    ValidateDutyEncounterAggregation();
    ValidateDutyWipeTracking();
    ValidateDutyEncounterFolderAggregation();
    ValidateDutyEncounterPartySizes();
    ValidateDutyEncounterRosterReplacement();
    ValidateHighestDamageAggregation();
    ValidateControlCenterPresentation();
    ValidateInstalledPluginVersionDisplay(testRoot);
    ValidateDiagnosticReport(testRoot);
    ValidateCombatLogDirectoryConfiguration(testRoot);
    ValidateNetworkLogSessionRotation(testRoot);
    ValidateFflogsEstimateCurve();
    await ValidateFflogsPersistenceAsync(testRoot);
    ValidateFflogsCurrentEncounterTable();
    ValidateFflogsConcurrencyBoundaries();
    await ValidateFflogsCacheWritersAsync(testRoot);
    await ValidateBundledPluginInstallLifecycleAsync();
    await ValidatePluginLifecycleShutdownAsync(testRoot);
    ValidatePluginUnloadOwnership();
    await ValidateEncounterShutdownFlushAsync(testRoot);
    await ValidateEncounterRetentionAsync(testRoot);
    await ValidateAtomicEncounterStateUpdatesAsync();
    await ValidateFactoryResetRollbackAsync(testRoot);
    await ValidatePortableConfigurationArchiveAsync(testRoot);
    await ValidateEncryptedConfigurationBackupAsync(testRoot);
    await ValidateRealConfigurationBackupFixtureAsync(testRoot);
    ValidateCloudKeyEnvelopeAndCredentialProtection(testRoot);
    await ValidateCloudApiContractAsync(testRoot);
    await ValidateSavedCloudSessionRequiresServerValidationAsync(testRoot);
    await ValidateFirstLoginReportsServerUnbanAsync(testRoot);
    await ValidateCloudRegistrationSurvivesVersionRefreshFailureAsync(testRoot);
    await ValidateCommittedRegistrationSurvivesCredentialWriteFailureAsync(testRoot);
    await ValidateCloudBanResponseEnforcesMarkerAsync(testRoot);
    var cloudIntegrationBaseUrl = Environment.GetEnvironmentVariable(
        "DACT_CLOUD_INTEGRATION_BASE_URL");
    var cloudIntegrationAdminToken = Environment.GetEnvironmentVariable(
        "DACT_CLOUD_INTEGRATION_ADMIN_TOKEN");
    if (!string.IsNullOrWhiteSpace(cloudIntegrationBaseUrl) &&
        !string.IsNullOrWhiteSpace(cloudIntegrationAdminToken))
    {
        await ValidateLiveCloudIntegrationAsync(
            testRoot,
            new Uri(cloudIntegrationBaseUrl),
            cloudIntegrationAdminToken);
    }

    var packagePath = Path.Combine(testRoot, "valid.zip");
    await CreatePackageAsync(packagePath, "example.plugin", "1.0.0");
    var paths = new PluginPaths(Path.Combine(testRoot, "config"));
    Directory.CreateDirectory(paths.ActPluginDirectory);
    var installer = new ActPluginPackageInstaller(paths);
    await ValidateBundledPluginDisclosureAsync(testRoot);
    if (string.Equals(
            Environment.GetEnvironmentVariable("ACTCOMPAT_ONLINE_UPDATE_SMOKE"),
            "1",
            StringComparison.Ordinal))
    {
        await ValidateLiveBundledPluginUpdateCheckAsync(testRoot);
    }

    var cactbotPackage = Path.Combine(testRoot, "cactbot.zip");
    using (var archive = ZipFile.Open(cactbotPackage, ZipArchiveMode.Create))
    {
        await WriteArchiveEntryAsync(archive, "cactbot/cactbot/CactbotOverlay.dll", [1, 2, 3]);
        await WriteArchiveEntryAsync(archive, "cactbot/cactbot/ui/raidboss/raidboss.html", "<html></html>"u8.ToArray());
        await WriteArchiveEntryAsync(archive, "cactbot/cactbot/resources/test.txt", "ok"u8.ToArray());
    }
    var cactbotInstaller = new CactbotPackageInstaller(paths);
    await Task.WhenAll(
        cactbotInstaller.InstallAsync(cactbotPackage, CancellationToken.None),
        cactbotInstaller.InstallAsync(cactbotPackage, CancellationToken.None));
    Assert(cactbotInstaller.IsInstalled, "Official Cactbot package layout was not installed.");
    Assert(File.Exists(Path.Combine(paths.CactbotDirectory, "resources", "test.txt")),
        "Cactbot resources were not preserved.");
    var customCactbotUserFile = Path.Combine(paths.CactbotDirectory, "user", "custom.js");
    Directory.CreateDirectory(Path.GetDirectoryName(customCactbotUserFile)!);
    await File.WriteAllTextAsync(customCactbotUserFile, "custom");
    await cactbotInstaller.InstallAsync(cactbotPackage, CancellationToken.None);
    Assert(
        await File.ReadAllTextAsync(customCactbotUserFile) == "custom",
        "Cactbot upgrade overwrote the user's custom files.");
    cactbotInstaller.DeleteCommittedBackupAsync = _ =>
        Task.FromException(new IOException("simulated backup cleanup failure"));
    await cactbotInstaller.InstallAsync(cactbotPackage, CancellationToken.None);
    Assert(
        cactbotInstaller.IsInstalled &&
        Directory.EnumerateDirectories(
                paths.ConfigDirectory,
                $"{Path.GetFileName(paths.CactbotDirectory)}.backup-*")
            .Any(),
        "A committed Cactbot install was reported as failed when only backup cleanup was blocked.");
    cactbotInstaller.DeleteCommittedBackupAsync = backup =>
        Task.Run(() => Directory.Delete(backup, recursive: true));
    await cactbotInstaller.InstallAsync(cactbotPackage, CancellationToken.None);
    Assert(
        !Directory.EnumerateDirectories(
                paths.ConfigDirectory,
                $"{Path.GetFileName(paths.CactbotDirectory)}.backup-*")
            .Any(),
        "A stale Cactbot backup was not cleaned on the next successful install.");
    await ValidateCactbotMissingTargetBackupRecoveryAsync(testRoot, cactbotPackage);
    await ValidateCactbotPostShutdownPublicationGuardAsync();
    await ValidateBundledCactbotPortableInstallAsync(testRoot);
    await ValidateOfficialBundledCactbotAsync(testRoot);

    var installed = await installer.InstallAsync(packagePath, CancellationToken.None);
    Assert(installed.Manifest.Id == "example.plugin", "Valid package id was not preserved.");
    Assert(File.Exists(Path.Combine(installed.InstallDirectory, "Example.Plugin.dll")), "Entry assembly was not installed.");

    await CreatePackageAsync(packagePath, "example.plugin", "1.1.0");
    installed = await installer.InstallAsync(packagePath, CancellationToken.None);
    Assert(installed.Manifest.Version == "1.1.0", "Upgrade did not replace the installed package.");
    Assert(Directory.EnumerateDirectories(paths.PluginBackupDirectory).Any(), "Upgrade did not preserve a backup.");

    var knownPackagePath = Path.Combine(testRoot, "postnamazu.zip");
    using (var archive = ZipFile.Open(knownPackagePath, ZipArchiveMode.Create))
    {
        var entry = archive.CreateEntry("release/PostNamazu.dll");
        await using var stream = entry.Open();
        await stream.WriteAsync(new byte[] { 1, 2, 3 });
    }

    var knownPlugin = await installer.InstallAsync(knownPackagePath, CancellationToken.None);
    Assert(knownPlugin.Manifest.Id == "postnamazu", "Known third-party package was not recognized.");
    Assert(knownPlugin.Manifest.EntryType == "PostNamazu.PostNamazu", "Known plugin entry type is incorrect.");

    var silverDasherSource = Environment.GetEnvironmentVariable("ACTCOMPAT_SILVERDASHER_ROOT");
    if (!string.IsNullOrWhiteSpace(silverDasherSource) && Directory.Exists(silverDasherSource))
    {
        var silverDasherPackage = Path.Combine(testRoot, "silverdasher.zip");
        ZipFile.CreateFromDirectory(silverDasherSource, silverDasherPackage);
        var loaderPath = Directory.EnumerateFiles(
                silverDasherSource,
                "SilverDasher.dll",
                SearchOption.AllDirectories)
            .Single();
        var corePath = Directory.EnumerateFiles(
                silverDasherSource,
                "SilverDasher.Core.dll",
                SearchOption.AllDirectories)
            .Single();
        var originalLoaderHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(loaderPath)));
        var originalCoreHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(corePath)));
        var silverDasher = await installer.InstallAsync(
            silverDasherPackage,
            CancellationToken.None);
        Assert(
            silverDasher.Manifest is
            {
                Id: "silverdasher",
                Version: "0.6.0.4",
                EntryType: "SilverDasher.Loader.Loader",
            } &&
            silverDasher.Manifest.EntryAssembly.EndsWith(
                "SilverDasher.dll",
                StringComparison.OrdinalIgnoreCase),
            "The original SilverDasher 0.6.0.4 package did not produce the required compatibility manifest.");
        Assert(
            File.Exists(Path.Combine(
                silverDasher.InstallDirectory,
                Path.GetRelativePath(silverDasherSource, corePath))) &&
            Directory.EnumerateFiles(
                silverDasher.InstallDirectory,
                "fates.json",
                SearchOption.AllDirectories).Any(),
            "SilverDasher libs or data files were not preserved during ZIP installation.");
        Assert(
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(loaderPath))) == originalLoaderHash &&
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(corePath))) == originalCoreHash,
            "Installing SilverDasher modified the user's original DLL files.");
        try
        {
            await installer.InstallAsync(loaderPath, CancellationToken.None);
            throw new InvalidOperationException("A loose SilverDasher loader DLL was accepted without libs and data.");
        }
        catch (InvalidDataException)
        {
        }
        var incompleteSilverDasherPackage = Path.Combine(
            testRoot,
            "silverdasher-incomplete.zip");
        using (var archive = ZipFile.Open(
                   incompleteSilverDasherPackage,
                   ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("SilverDasher.dll");
            await using var source = File.OpenRead(loaderPath);
            await using var destination = entry.Open();
            await source.CopyToAsync(destination);
        }
        try
        {
            await installer.InstallAsync(
                incompleteSilverDasherPackage,
                CancellationToken.None);
            throw new InvalidOperationException(
                "An incomplete SilverDasher ZIP was accepted without sibling libs and data.");
        }
        catch (InvalidDataException)
        {
        }
    }

    var loosePluginDirectory = Path.Combine(testRoot, "loose-plugin");
    Directory.CreateDirectory(loosePluginDirectory);
    var loosePluginPath = Path.Combine(loosePluginDirectory, "Triggernometry.dll");
    var builtRuntimeDirectory = Path.Combine(
        FindProjectRoot(),
        "src",
        "DalamudActCompat",
        "bin",
        "Release");
    File.Copy(Path.Combine(builtRuntimeDirectory, "DalamudActCompat.ActRuntime.dll"), loosePluginPath);
    File.Copy(
        Path.Combine(builtRuntimeDirectory, "Advanced Combat Tracker.dll"),
        Path.Combine(loosePluginDirectory, "Advanced Combat Tracker.dll"));
    var translationPath = Path.Combine(loosePluginDirectory, "zh-CN.triglations.xml");
    await File.WriteAllTextAsync(translationPath, "<Triggernometry />");
    var loosePlugin = await installer.InstallAsync(loosePluginPath, CancellationToken.None);
    Assert(loosePlugin.Manifest.Id == "triggernometry", "Loose Triggernometry DLL was not recognized.");
    Assert(File.Exists(Path.Combine(loosePlugin.InstallDirectory, "Triggernometry.dll")),
        "Loose ACT plugin DLL was not installed.");
    Assert(File.Exists(Path.Combine(loosePlugin.InstallDirectory, "zh-CN.triglations.xml")),
        "Triggernometry translation companion was not preserved.");
    Assert(File.Exists(Path.Combine(loosePlugin.InstallDirectory, "Advanced Combat Tracker.dll")),
        "Loose ACT plugin managed dependency was not preserved.");

    var unknownDllPath = Path.Combine(loosePluginDirectory, "UnknownPlugin.dll");
    await File.WriteAllBytesAsync(unknownDllPath, new byte[] { 0x4d, 0x5a });
    try
    {
        await installer.InstallAsync(unknownDllPath, CancellationToken.None);
        throw new InvalidOperationException("Unknown loose DLL was accepted.");
    }
    catch (InvalidDataException)
    {
    }

    var genericDllPath = Path.Combine(loosePluginDirectory, "CommunityExample.dll");
    File.Copy(typeof(GenericActPluginFixture).Assembly.Location, genericDllPath);
    var genericPlugin = await installer.InstallAsync(genericDllPath, CancellationToken.None);
    Assert(
        genericPlugin.Manifest.Id == "dalamudactcompat.packagesmoketests" &&
        genericPlugin.Manifest.EntryType == typeof(GenericActPluginFixture).FullName,
        "A valid unknown IActPluginV1 DLL did not receive an automatic generic manifest.");
    Assert(
        ActPluginPackageInstaller.GetRequestedCapabilities(genericPlugin.Manifest)
            .Contains(ActCapability.ReadCombatLogs),
        "Generic plugin static preflight did not generate its baseline permission list.");
    var removedGenericPlugin = await installer.UninstallAsync(
        genericPlugin.Manifest.Id,
        CancellationToken.None);
    Assert(
        removedGenericPlugin is not null &&
        Directory.Exists(removedGenericPlugin) &&
        !Directory.Exists(genericPlugin.InstallDirectory) &&
        installer.Discover(new HashSet<string>(StringComparer.OrdinalIgnoreCase))
            .All(plugin => !string.Equals(
                plugin.Manifest.Id,
                genericPlugin.Manifest.Id,
                StringComparison.OrdinalIgnoreCase)),
        "A generic plugin uninstall was not removed from discovery or retained as a recoverable backup.");

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
    ValidateLegacyResourceRuntimeDependencies();
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

static void ValidateGameRegionSelection()
{
    var automaticChinese = GameRegionResolver.Resolve(
        GameRegionMode.Auto,
        "English",
        nativeClientLanguageCode: 4);
    var automaticGlobalWithChineseLanguagePack = GameRegionResolver.Resolve(
        GameRegionMode.Auto,
        "ChineseSimplified",
        nativeClientLanguageCode: 1);
    var allInternationalNativeCodesRemainGlobal = Enumerable.Range(0, 4).All(code =>
    {
        var selection = GameRegionResolver.Resolve(
            GameRegionMode.Auto,
            "ChineseSimplified",
            (byte)code);
        return selection is
        {
            DetectedRegion: HostGameRegion.Global,
            EffectiveRegion: HostGameRegion.Global,
            HasDetectedRegion: true,
        };
    });
    var unavailableNativeRegion = GameRegionResolver.Resolve(
        GameRegionMode.Auto,
        "ChineseSimplified",
        nativeClientLanguageCode: null);
    var manualGlobal = GameRegionResolver.Resolve(
        GameRegionMode.Global,
        "ChineseSimplified",
        nativeClientLanguageCode: 4);
    var manualChinese = GameRegionResolver.Resolve(
        GameRegionMode.Chinese,
        "English",
        nativeClientLanguageCode: 0);
    Assert(
        automaticChinese is
        {
            DetectedRegion: HostGameRegion.Chinese,
            EffectiveRegion: HostGameRegion.Chinese,
            ClientLanguage: HostClientLanguage.English,
            NativeClientLanguageCode: 4,
            HasDetectedRegion: true,
            IsManualOverride: false,
        } &&
        automaticGlobalWithChineseLanguagePack is
        {
            DetectedRegion: HostGameRegion.Global,
            EffectiveRegion: HostGameRegion.Global,
            ClientLanguage: HostClientLanguage.Chinese,
            NativeClientLanguageCode: 1,
            HasDetectedRegion: true,
        } &&
        allInternationalNativeCodesRemainGlobal &&
        unavailableNativeRegion is
        {
            DetectedRegion: HostGameRegion.Global,
            EffectiveRegion: HostGameRegion.Global,
            ClientLanguage: HostClientLanguage.Chinese,
            NativeClientLanguageCode: null,
            HasDetectedRegion: false,
        } &&
        manualGlobal is
        {
            DetectedRegion: HostGameRegion.Chinese,
            EffectiveRegion: HostGameRegion.Global,
            ClientLanguage: HostClientLanguage.Chinese,
            IsManualOverride: true,
        } &&
        manualChinese is
        {
            DetectedRegion: HostGameRegion.Global,
            EffectiveRegion: HostGameRegion.Chinese,
            ClientLanguage: HostClientLanguage.English,
        },
        "Native-client or manual CN/Global selection was not resolved independently from Dalamud language.");

    var configuration = new PluginConfiguration
    {
        Version = 10,
        GameRegionMode = GameRegionMode.Chinese,
    };
    Assert(
        configuration.ApplyMigrations() &&
        configuration.Version == 16 &&
        configuration.GameRegionMode == GameRegionMode.Auto,
        "Existing configurations were not migrated to automatic region detection.");

    configuration.GameRegionMode = GameRegionMode.Global;
    var snapshot = configuration.CreateSnapshot();
    configuration.GameRegionMode = GameRegionMode.Chinese;
    configuration.RestoreFrom(snapshot);
    Assert(
        configuration.GameRegionMode == GameRegionMode.Global,
        "A manual game-region override did not survive configuration snapshot restore.");

    IINACT.FfxivActPluginWrapper.ConfigureRegion(
        Dalamud.Game.ClientLanguage.English,
        chineseRegionOverride: true);
    Assert(
        OpcodeManager.Instance.GameRegion == GameRegion.Chinese,
        "The parser ignored a manual Chinese-region override on a Global-language client.");
    IINACT.FfxivActPluginWrapper.ConfigureRegion(
        Dalamud.Game.ClientLanguage.English,
        chineseRegionOverride: false);
    Assert(
        OpcodeManager.Instance.GameRegion == GameRegion.Global,
        "The parser ignored a manual Global-region override.");
}

static void ValidateVersionedCompatibilityHostExtraction(string testRoot)
{
    var hostRoot = Path.Combine(testRoot, "versioned-host-assets");
    Directory.CreateDirectory(hostRoot);
    var legacyHost = Path.Combine(hostRoot, "DalamudActCompat.Host.exe");
    File.WriteAllText(legacyHost, "locked previous host build");

    var log = DispatchProxy.Create<IPluginLog, NoOpPluginLogProxy>();
    var packagedHost = Path.Combine(
        FindProjectRoot(),
        "src",
        "DalamudActCompat",
        "bin",
        "Release",
        "host");
    var assets = new CompatibilityHostAssets(
        hostRoot,
        new PluginLogger(log),
        packagedHost);
    Assert(
        !string.Equals(assets.TargetDirectory, hostRoot, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(
            Path.GetDirectoryName(assets.TargetDirectory),
            hostRoot,
            StringComparison.OrdinalIgnoreCase),
        "Compatibility Host assets were not isolated under a build-specific directory.");

    // A running previous Host denies replacement of its executable. The current
    // build must extract elsewhere instead of requiring the user to find and kill it.
    using (new FileStream(legacyHost, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        assets.EnsureExtracted();
    }

    var currentHost = Path.Combine(
        assets.TargetDirectory,
        "DalamudActCompat.Host.exe");
    Assert(
        File.Exists(currentHost),
        "Compatibility Host was not extracted while the previous executable was locked.");
    Assert(
        File.ReadAllText(legacyHost) == "locked previous host build",
        "Versioned Host extraction modified the locked previous build.");

    // Reloads of the same build should only verify matching files, never replace
    // the executable that its own Host process is already using.
    using (new FileStream(currentHost, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        assets.EnsureExtracted();
    }

    var lateBoundRoot = Path.Combine(testRoot, "late-bound-host-assets");
    var lateBoundAssets = new CompatibilityHostAssets(
        lateBoundRoot,
        new PluginLogger(log));
    lateBoundAssets.SetPackagedHostDirectory(packagedHost);
    lateBoundAssets.EnsureExtracted();
    Assert(
        File.Exists(Path.Combine(lateBoundAssets.TargetDirectory, "DalamudActCompat.Host.exe")),
        "A Host resource pack completed after plugin construction but was not accepted for startup.");
}

static async Task ValidateResourcePackLifecycleAsync(string testRoot)
{
    var root = Path.Combine(testRoot, "resource-pack-lifecycle");
    Directory.CreateDirectory(root);
    var log = new PluginLogger(DispatchProxy.Create<IPluginLog, NoOpPluginLogProxy>());

    var resumeFixture = CreateResourcePackFixture(root, "resume", "resume-content", "1");
    var resumeCalls = 0;
    var resumeHandler = new ScriptedHttpMessageHandler(request =>
    {
        resumeCalls++;
        if (resumeCalls == 1)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new InterruptingReadStream(
                    resumeFixture.ArchiveBytes,
                    resumeFixture.ArchiveBytes.Length / 2)),
            };
        }

        var offset = request.Headers.Range?.Ranges.Single().From ?? 0;
        Assert(offset > 0, "An interrupted resource pack was restarted instead of resumed.");
        return ByteResponse(
            resumeFixture.ArchiveBytes[(int)offset..],
            HttpStatusCode.PartialContent);
    });
    var resumeEntry = resumeFixture.Entry with { Urls = ["https://packs.test/resume"] };
    WriteResourceCatalog(resumeFixture.PluginDirectory, resumeEntry);
    var resumeManager = new ResourcePackManager(
        resumeFixture.PluginDirectory,
        resumeFixture.CacheDirectory,
        log,
        new HttpClient(resumeHandler));
    var reportedProgress = new List<ResourcePackDownloadProgress>();
    var resumed = await resumeManager.ResolveDirectoryAsync(
        "test-pack",
        "missing",
        CancellationToken.None,
        new InlineProgress<ResourcePackDownloadProgress>(reportedProgress.Add));
    Assert(
        File.ReadAllText(Path.Combine(resumed, "payload.txt")) == "resume-content" &&
        resumeCalls == 2 &&
        reportedProgress.Count > 0 &&
        reportedProgress[^1].Percent == 100 &&
        reportedProgress.All(static item => item.Percent is >= 0 and <= 100),
        "Interrupted resource pack download did not resume, report progress, and install atomically.");
    _ = await resumeManager.ResolveDirectoryAsync("test-pack", "missing", CancellationToken.None);
    Assert(resumeCalls == 2, "Resolving the same content hash downloaded the resource pack again.");

    var cancelFixture = CreateResourcePackFixture(root, "cancel", "cancel-content", "1");
    var cancelEntry = cancelFixture.Entry with { Urls = ["https://packs.test/cancel"] };
    WriteResourceCatalog(cancelFixture.PluginDirectory, cancelEntry);
    var cancelHandler = new ScriptedHttpMessageHandler(_ => ByteResponse(cancelFixture.ArchiveBytes));
    var cancelManager = new ResourcePackManager(
        cancelFixture.PluginDirectory,
        cancelFixture.CacheDirectory,
        log,
        new HttpClient(cancelHandler));
    using var downloadCancellation = new CancellationTokenSource();
    await AssertThrowsAsync<OperationCanceledException>(() => cancelManager.ResolveDirectoryAsync(
        "test-pack",
        "missing",
        downloadCancellation.Token,
        new InlineProgress<ResourcePackDownloadProgress>(progress =>
        {
            if (progress.BytesReceived > 0)
            {
                downloadCancellation.Cancel();
            }
        })));
    Assert(
        !Directory.Exists(Path.Combine(cancelFixture.CacheDirectory, cancelEntry.Id, cancelEntry.Sha256)),
        "Cancelling a resource download still installed the core components.");
    var recoveredAfterCancel = await cancelManager.ResolveDirectoryAsync(
        "test-pack",
        "missing",
        CancellationToken.None);
    Assert(
        File.ReadAllText(Path.Combine(recoveredAfterCancel, "payload.txt")) == "cancel-content",
        "A cancelled resource download could not be retried from its retained partial archive.");

    var offlineHandler = new ScriptedHttpMessageHandler(_ =>
        throw new InvalidOperationException("A verified offline cache unexpectedly used the network."));
    var offlineManager = new ResourcePackManager(
        resumeFixture.PluginDirectory,
        resumeFixture.CacheDirectory,
        log,
        new HttpClient(offlineHandler));
    Assert(
        offlineManager.TryResolveAvailableDirectory(
            "test-pack",
            "missing",
            out var immediatelyAvailableDirectory) &&
        immediatelyAvailableDirectory == resumed &&
        offlineHandler.Requests.Count == 0,
        "Plugin startup could not reuse a verified resource pack without touching the network.");
    var offlineDirectory = await offlineManager.ResolveDirectoryAsync(
        "test-pack",
        "missing",
        CancellationToken.None);
    Assert(
        offlineDirectory == resumed && offlineHandler.Requests.Count == 0,
        "A verified same-hash cache was not available while offline.");

    File.WriteAllText(Path.Combine(resumed, "payload.txt"), "corrupted");
    var repairHandler = new ScriptedHttpMessageHandler(_ => ByteResponse(resumeFixture.ArchiveBytes));
    var repairManager = new ResourcePackManager(
        resumeFixture.PluginDirectory,
        resumeFixture.CacheDirectory,
        log,
        new HttpClient(repairHandler));
    var repaired = await repairManager.ResolveDirectoryAsync(
        "test-pack",
        "missing",
        CancellationToken.None);
    var installedRoot = Directory.GetParent(repaired)!.FullName;
    var idRoot = Directory.GetParent(installedRoot)!.FullName;
    Assert(
        File.ReadAllText(Path.Combine(repaired, "payload.txt")) == "resume-content" &&
        repairHandler.Requests.Count == 1 &&
        Directory.GetDirectories(
            idRoot,
            $"{Path.GetFileName(installedRoot)}.corrupt-*",
            SearchOption.TopDirectoryOnly).Length == 1,
        "A corrupted immutable cache was not quarantined and repaired from the verified archive.");

    var fallbackFixture = CreateResourcePackFixture(root, "source-fallback", "fallback-content", "1");
    var fallbackHandler = new ScriptedHttpMessageHandler(request =>
        request.RequestUri!.Host == "primary.test"
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : ByteResponse(fallbackFixture.ArchiveBytes));
    WriteResourceCatalog(
        fallbackFixture.PluginDirectory,
        fallbackFixture.Entry with
        {
            Urls = ["https://primary.test/pack", "https://secondary.test/pack"],
        });
    var fallbackManager = new ResourcePackManager(
        fallbackFixture.PluginDirectory,
        fallbackFixture.CacheDirectory,
        log,
        new HttpClient(fallbackHandler));
    var fallbackDirectory = await fallbackManager.ResolveDirectoryAsync(
        "test-pack",
        "missing",
        CancellationToken.None);
    Assert(
        File.ReadAllText(Path.Combine(fallbackDirectory, "payload.txt")) == "fallback-content" &&
        fallbackHandler.Requests.Any(uri => uri.Host == "primary.test") &&
        fallbackHandler.Requests.Any(uri => uri.Host == "secondary.test"),
        "Resource pack mirror fallback did not advance past a failed primary source.");

    var timeoutFixture = CreateResourcePackFixture(root, "timeout-fallback", "timeout-content", "1");
    var timeoutHandler = new ScriptedHttpMessageHandler(request =>
        request.RequestUri!.Host == "timeout.test"
            ? throw new TaskCanceledException("simulated HttpClient timeout")
            : ByteResponse(timeoutFixture.ArchiveBytes));
    WriteResourceCatalog(
        timeoutFixture.PluginDirectory,
        timeoutFixture.Entry with
        {
            Urls = ["https://timeout.test/pack", "https://secondary.test/pack"],
        });
    var timeoutManager = new ResourcePackManager(
        timeoutFixture.PluginDirectory,
        timeoutFixture.CacheDirectory,
        log,
        new HttpClient(timeoutHandler));
    var timeoutDirectory = await timeoutManager.ResolveDirectoryAsync(
        "test-pack",
        "missing",
        CancellationToken.None);
    Assert(
        File.ReadAllText(Path.Combine(timeoutDirectory, "payload.txt")) == "timeout-content" &&
        timeoutHandler.Requests.Any(uri => uri.Host == "timeout.test") &&
        timeoutHandler.Requests.Any(uri => uri.Host == "secondary.test"),
        "An HttpClient timeout was mistaken for caller cancellation and skipped mirror fallback.");

    var badHashFixture = CreateResourcePackFixture(root, "bad-hash", "bad-hash-content", "1");
    WriteResourceCatalog(
        badHashFixture.PluginDirectory,
        badHashFixture.Entry with
        {
            Sha256 = new string('0', 64),
            Urls = ["https://packs.test/bad-hash"],
        });
    var badHashManager = new ResourcePackManager(
        badHashFixture.PluginDirectory,
        badHashFixture.CacheDirectory,
        log,
        new HttpClient(new ScriptedHttpMessageHandler(_ => ByteResponse(badHashFixture.ArchiveBytes))));
    await AssertThrowsAsync<InvalidDataException>(() => badHashManager.ResolveDirectoryAsync(
        "test-pack",
        "missing",
        CancellationToken.None));

    var completedPartialFixture = CreateResourcePackFixture(
        root,
        "completed-partial",
        "completed-before-install",
        "1");
    WriteResourceCatalog(completedPartialFixture.PluginDirectory, completedPartialFixture.Entry);
    var completedDownloadDirectory = Path.Combine(
        completedPartialFixture.CacheDirectory,
        ".downloads");
    Directory.CreateDirectory(completedDownloadDirectory);
    var completedPartialPath = Path.Combine(
        completedDownloadDirectory,
        $"test-pack-{completedPartialFixture.Entry.Sha256}.partial");
    File.WriteAllBytes(completedPartialPath, completedPartialFixture.ArchiveBytes);
    var completedPartialHandler = new ScriptedHttpMessageHandler(_ =>
        throw new InvalidOperationException("A completed partial archive unexpectedly used the network."));
    var completedPartialManager = new ResourcePackManager(
        completedPartialFixture.PluginDirectory,
        completedPartialFixture.CacheDirectory,
        log,
        new HttpClient(completedPartialHandler));
    var completedPartialDirectory = await completedPartialManager.ResolveDirectoryAsync(
        "test-pack",
        "missing",
        CancellationToken.None);
    Assert(
        File.ReadAllText(Path.Combine(completedPartialDirectory, "payload.txt")) ==
        "completed-before-install" &&
        completedPartialHandler.Requests.Count == 0 &&
        !File.Exists(completedPartialPath),
        "A complete interrupted download was not verified and installed without an HTTP 416 retry.");

    var migrationFixture = CreateResourcePackFixture(root, "migration", "legacy-content", "1");
    var legacyDirectory = Path.Combine(migrationFixture.PluginDirectory, "legacy");
    Directory.CreateDirectory(legacyDirectory);
    File.WriteAllText(Path.Combine(legacyDirectory, "payload.txt"), "legacy-content");
    WriteResourceCatalog(migrationFixture.PluginDirectory, migrationFixture.Entry);
    var migrationHandler = new ScriptedHttpMessageHandler(_ =>
        throw new InvalidOperationException("Legacy migration unexpectedly used the network."));
    var migrationManager = new ResourcePackManager(
        migrationFixture.PluginDirectory,
        migrationFixture.CacheDirectory,
        log,
        new HttpClient(migrationHandler));
    var migrated = await migrationManager.ResolveDirectoryAsync(
        "test-pack",
        "legacy",
        CancellationToken.None);
    Assert(
        !string.Equals(migrated, legacyDirectory, StringComparison.OrdinalIgnoreCase) &&
        File.ReadAllText(Path.Combine(migrated, "payload.txt")) == "legacy-content" &&
        migrationHandler.Requests.Count == 0,
        "An existing all-in-one resource directory was not migrated into the hash cache.");

    var lockFixture = CreateResourcePackFixture(root, "lock", "locked-content", "1");
    WriteResourceCatalog(
        lockFixture.PluginDirectory,
        lockFixture.Entry with { Urls = ["https://packs.test/lock"] });
    var lockRoot = Path.Combine(lockFixture.CacheDirectory, "test-pack");
    Directory.CreateDirectory(lockRoot);
    var lockPath = Path.Combine(lockRoot, ".install.lock");
    var heldLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    var lockManager = new ResourcePackManager(
        lockFixture.PluginDirectory,
        lockFixture.CacheDirectory,
        log,
        new HttpClient(new ScriptedHttpMessageHandler(_ => ByteResponse(lockFixture.ArchiveBytes))));
    var lockedResolution = lockManager.ResolveDirectoryAsync(
        "test-pack",
        "missing",
        CancellationToken.None);
    await Task.Delay(250);
    Assert(!lockedResolution.IsCompleted, "Resource pack installation ignored its exclusive file lock.");
    heldLock.Dispose();
    var lockedDirectory = await lockedResolution;
    Assert(
        File.ReadAllText(Path.Combine(lockedDirectory, "payload.txt")) == "locked-content",
        "Resource pack installation did not continue after its file lock was released.");

    var rollbackV1 = CreateResourcePackFixture(root, "rollback", "stable-content", "1");
    WriteResourceCatalog(
        rollbackV1.PluginDirectory,
        rollbackV1.Entry with { Urls = ["https://packs.test/v1"] });
    var rollbackCache = rollbackV1.CacheDirectory;
    var v1Manager = new ResourcePackManager(
        rollbackV1.PluginDirectory,
        rollbackCache,
        log,
        new HttpClient(new ScriptedHttpMessageHandler(_ => ByteResponse(rollbackV1.ArchiveBytes))));
    _ = await v1Manager.ResolveDirectoryAsync("test-pack", "missing", CancellationToken.None);
    var rollbackV2 = CreateResourcePackFixture(root, "rollback-v2-source", "new-content", "2");
    WriteResourceCatalog(
        rollbackV1.PluginDirectory,
        rollbackV2.Entry with { Urls = ["https://packs.test/unavailable"] });
    var rollbackManager = new ResourcePackManager(
        rollbackV1.PluginDirectory,
        rollbackCache,
        log,
        new HttpClient(new ScriptedHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))));
    var rolledBack = await rollbackManager.ResolveDirectoryAsync(
        "test-pack",
        "missing",
        CancellationToken.None);
    Assert(
        File.ReadAllText(Path.Combine(rolledBack, "payload.txt")) == "stable-content",
        "A failed resource update did not roll back to the previous verified cache.");
}

static ResourcePackFixture CreateResourcePackFixture(
    string root,
    string name,
    string content,
    string version)
{
    var fixtureRoot = Path.Combine(root, name);
    var pluginDirectory = Path.Combine(fixtureRoot, "plugin");
    var cacheDirectory = Path.Combine(fixtureRoot, "cache");
    var sourceDirectory = Path.Combine(fixtureRoot, "source");
    Directory.CreateDirectory(pluginDirectory);
    Directory.CreateDirectory(cacheDirectory);
    Directory.CreateDirectory(sourceDirectory);
    File.WriteAllText(Path.Combine(sourceDirectory, "payload.txt"), content);
    var archivePath = Path.Combine(fixtureRoot, "pack.zip");
    using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
    {
        archive.CreateEntryFromFile(
            Path.Combine(sourceDirectory, "payload.txt"),
            "payload/payload.txt",
            CompressionLevel.NoCompression);
    }
    var archiveBytes = File.ReadAllBytes(archivePath);
    var entry = new ResourcePackEntry(
        "test-pack",
        version,
        "pack.zip",
        archiveBytes.Length,
        Convert.ToHexString(SHA256.HashData(archiveBytes)).ToLowerInvariant(),
        "payload",
        ResourcePackManager.ComputeDirectoryHash(sourceDirectory),
        ["https://packs.test/pack"]);
    return new ResourcePackFixture(pluginDirectory, cacheDirectory, archiveBytes, entry);
}

static void WriteResourceCatalog(string pluginDirectory, ResourcePackEntry entry)
{
    File.WriteAllText(
        Path.Combine(pluginDirectory, ResourcePackManager.ManifestFileName),
        JsonSerializer.Serialize(
            new ResourcePackCatalog(1, entry.Version, [entry]),
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true,
            }));
}

static HttpResponseMessage ByteResponse(
    byte[] bytes,
    HttpStatusCode status = HttpStatusCode.OK)
    => new(status) { Content = new ByteArrayContent(bytes) };

static async Task AssertThrowsAsync<TException>(Func<Task> action)
    where TException : Exception
{
    try
    {
        await action();
    }
    catch (TException)
    {
        return;
    }
    throw new InvalidOperationException($"Expected {typeof(TException).Name} was not thrown.");
}

static void ValidatePictoActOverlayCommands()
{
    Assert(
        PictoActNativeVfxBackend.UsesExpectedFieldLayout,
        "PictoACT native VFX fields drifted from the current FFXIVClientStructs layout.");
    Assert(
        PictoActOverlayService.ShouldDrawScreenFallback(
            PictoActShapeKind.Circle,
            nativeBackendAvailable: false) &&
        !PictoActOverlayService.ShouldDrawScreenFallback(
            PictoActShapeKind.Circle,
            nativeBackendAvailable: true) &&
        !PictoActOverlayService.ShouldDrawScreenFallback(
            PictoActShapeKind.Polygon,
            nativeBackendAvailable: true) &&
        !PictoActOverlayService.ShouldDrawScreenFallback(
            PictoActShapeKind.NativeOnly,
            nativeBackendAvailable: false),
        "PictoACT native-capable omens can regress to a depthless screen fallback.");
    var pluginSource = File.ReadAllText(Path.Combine(
        FindProjectRoot(), "src", "DalamudActCompat", "Plugin", "Plugin.cs"));
    Assert(
        Regex.IsMatch(
            pluginSource,
            @"RunOnFrameworkThread\s*\(\s*\(\)\s*=>\s*\{[^}]*" +
            @"ThrowIfCancellationRequested\(\);[^}]*pictoActOverlay\.Apply\(payload\);\s*\}",
            RegexOptions.Singleline),
        "PictoACT Host commands are not cancellation-guarded on the framework thread.");
    var beforeParse = DateTimeOffset.UtcNow;
    var commands = PictoActOverlayService.Parse(
        "Omen: Circle\nTag: ACTCOMPAT_SMOKE\nt: 5\nPos: <1.25, 2.5, -3.75>\n" +
        "Scale: 5, 5, 1\nColor: 0.2, 1, 0.3, 0.65\n---\n" +
        "Omen: Rect\nDelay: 2.5\nt: 5\nPos: 1, 2, 3\n" +
        "Angle: 0 + pi/2\nScale: 2.5, 28, 1\n---\n" +
        "Omen: Fan90\nt: 5\nPos: 1, 2, 3\nAngle: 90\u00b0\nScale: 12\n---\n" +
        "Action: Remove\nRegex: ^Auto$\n---\n" +
        "Action: Remove");
    Assert(
        commands.Count == 5 &&
        commands[0] is
        {
            Tag: "ACTCOMPAT_SMOKE",
            Remove: false,
            Shape:
            {
                Kind: PictoActShapeKind.Circle,
                PrimaryScale: 5,
                Position.X: 1.25f,
                Position.Y: -3.75f,
                Position.Z: 2.5f,
            },
        } &&
        commands[1] is
        {
            Tag: null,
            Remove: false,
            Shape:
            {
                Kind: PictoActShapeKind.Rectangle,
                PrimaryScale: 2.5f,
                SecondaryScale: 28,
                Color: { X: 1, Y: 0.82f, Z: 0.18f, W: 0.85f },
            },
        } &&
        commands[1].Shape!.StartsAt >= beforeParse.AddSeconds(2.4) &&
        MathF.Abs(commands[1].Shape!.Angle - MathF.PI / 2) < 0.0001f &&
        commands[2] is
        {
            Tag: null,
            Remove: false,
            Shape:
            {
                Kind: PictoActShapeKind.Fan,
                PrimaryScale: 12,
            },
        } &&
        MathF.Abs(commands[2].Shape!.FanRadians - MathF.PI / 2) < 0.0001f &&
        MathF.Abs(commands[2].Shape!.Angle - MathF.PI / 2) < 0.0001f &&
        commands[3] is { Tag: null, Regex: not null, Remove: true, Shape: null } &&
        commands[4] is { Tag: null, Regex: null, Remove: true, Shape: null },
        "Game-side PictoACT base-shape, Auto-tag, delay, or remove parsing failed.");

    var typedRemovals = PictoActOverlayService.Parse(
        "Action: Remove\nType: StaticVfx\nTag: STATIC\n---\n" +
        "Action: Remove\nType: Actor\nTag: ACTOR");
    Assert(
        typedRemovals[0].RemovalScope == PictoActRemovalScope.Static &&
        typedRemovals[1].RemovalScope == PictoActRemovalScope.Actor,
        "PictoACT typed StaticVfx/ActorVfx removal routing was not preserved.");

    var annotatedCommands = PictoActOverlayService.Parse(
        "这行是上游允许的说明文字\nOmen: Circle\nPos: 1, 2, 3\nScale: 3");
    Assert(
        annotatedCommands.Count == 1,
        "PictoACT free-form annotation lines were not ignored like upstream.");
    try
    {
        _ = PictoActOverlayService.Parse(
            "Omen: Circle\nomen: Donut\nPos: 1, 2, 3\nScale: 3");
        throw new InvalidOperationException("PictoACT duplicate keys were unexpectedly accepted.");
    }
    catch (InvalidDataException)
    {
        // Duplicate fields are ambiguous and upstream rejects them case-insensitively.
    }

    var overlay = new PictoActOverlayService(null!);
    overlay.Apply(
        "Omen: Circle\nt: 5\nPos: 1, 2, 3\nScale: 3\n---\n" +
        "Omen: Circle\nt: 5\nPos: 1, 2, 3\nScale: 5");
    Assert(
        overlay.ShapeCount == 2,
        "PictoACT Auto-tag creates unexpectedly replaced one another.");
    overlay.Apply(
        "Omen: Circle\nTag: ACTCOMPAT_REPLACE\nt: 5\nPos: 1, 2, 3\nScale: 3");
    overlay.Apply(
        "Omen: Circle\nTag: ACTCOMPAT_REPLACE\nt: 5\nPos: 1, 2, 3\nScale: 5");
    Assert(
        overlay.ShapeCount == 4,
        "PictoACT shapes sharing one selector tag did not coexist.");
    overlay.Apply("Action: Remove\nType: ActorVfx\nRegex: .*" );
    Assert(
        overlay.ShapeCount == 4,
        "PictoACT ActorVfx-only removal unexpectedly removed brokered static shapes.");
    overlay.Apply("Action: Remove\nRegex: ^Auto$");
    Assert(
        overlay.ShapeCount == 2,
        "PictoACT Regex removal did not remove all matching Auto-tag shapes.");
    overlay.Apply("Action: Remove");
    Assert(
        overlay.ShapeCount == 0,
        "PictoACT unfiltered removal did not clear every shape.");

    var ex7gCommands = PictoActOverlayService.Parse(
        "Omen: m131om_setu0f\nTag: Ex7g_塔\nt: 6.7\nO: 100, 100\n" +
        "θ: pi\nScale: 0.6, 25, 0.3\nColor: 1, 1, 5, 1\n---\n" +
        "Omen: k5d1_omen_o01pg\nTag: Ex7g_追踪球_1\nt: 22.7\n" +
        "Pos: 0, -3\nO: 100, 100\nθ: pi\nScale: 6, 6, 10\n" +
        "Color: 1, 0.5, 0.5, 1.2\n---\n" +
        "Action: Change\nRegex: ^Ex7g_追踪球_1$\nO: 110, 90\nθ: pi/2");
    Assert(
        ex7gCommands.Count == 3 &&
        ex7gCommands[0].Shape is
        {
            Kind: PictoActShapeKind.NativeOnly,
            VfxPath: "vfx/omen/eff/m131om_setu0f.avfx",
            Position.X: 100,
            Position.Z: 100,
            Color.Z: 5,
        } &&
        ex7gCommands[1].Shape is
        {
            Kind: PictoActShapeKind.NativeOnly,
            VfxPath: "vfx/omen/eff/k5d1_omen_o01pg.avfx",
            Position.X: 100,
            Position.Z: 97,
        } &&
        ex7gCommands[2] is
        {
            Change: true,
            Regex: not null,
            Patch:
            {
                TransformCenterSpecified: true,
                TransformRotationSpecified: true,
            },
        },
        "PictoACT did not parse the Ex7g custom omens or local O/θ transform commands.");

    overlay.Apply(
        "Omen: k5d1_omen_o01pg\nTag: Ex7g_追踪球_1\nt: 22.7\n" +
        "Pos: 0, -3\nO: 100, 100\nθ: pi\nScale: 6, 6, 10\n" +
        "Color: 1, 0.5, 0.5, 1.2");
    overlay.Apply("Action: Change\nRegex: ^Ex7g_追踪球_1$\nO: 110, 90\nθ: pi/2");
    Assert(
        overlay.ShapeSnapshot.Single() is
        {
            Position.X: 113,
            Position.Z: 90,
            Angle: var changedAngle,
        } &&
        MathF.Abs(changedAngle - MathF.PI / 2) < 0.0001f,
        "PictoACT Change did not update an existing Ex7g drawing in its local coordinate frame.");

    var bbyCommands = PictoActOverlayService.Parse(
        "Action: ExaFlare\nTag: Ex7x\ndelay0: 1.7\nt: 3\ndt: 1.1\n" +
        "O: 100, 100, 0\nθ: pi\ndPos: 0, -8\nn: 3\n" +
        "Omen: yazirushi1o0c\nScale: 6, 6, 5\n---\n" +
        "Omen: z6r1_b4_ibox_01k1\nTag: Ex7f\nt: 22\n" +
        "Pos: 95, 285\nScale: 5, 20, 1");
    Assert(
        bbyCommands.Count == 4 &&
        bbyCommands.Take(3).All(command =>
            command.Shape is
            {
                Kind: PictoActShapeKind.NativeOnly,
                VfxPath: "vfx/omen/eff/yazirushi1o0c.avfx",
            }) &&
        bbyCommands[0].Shape!.StartsAt < bbyCommands[1].Shape!.StartsAt &&
        bbyCommands[1].Shape!.StartsAt < bbyCommands[2].Shape!.StartsAt &&
        bbyCommands[0].Shape!.Position.Z == 100 &&
        bbyCommands[1].Shape!.Position.Z == 92 &&
        bbyCommands[2].Shape!.Position.Z == 84 &&
        bbyCommands[3].Shape is
        {
            Kind: PictoActShapeKind.NativeOnly,
            VfxPath: "vfx/omen/eff/z6r1_b4_ibox_01k1.avfx",
        },
        "PictoACT did not generically expand BBY ExaFlare or arbitrary omen resources.");

    var protocolCommands = PictoActOverlayService.Parse(
        "Omen: m0532om_don01x\nTag: CYL\nt: 5\nO: 100, 100\n" +
        "DirN4: 3 > 2 ? 1 : 0\nPos: polar 6, 90°, 0.1\n" +
        "ScaleCyl: 5√2 - 6, 30\n---\n" +
        "Omen: m131om_setu0f\nTag: TARGET\nt: 5\nO: 100, 100\nθ: pi\n" +
        "Pos: 2, -19\nTarget: -2, 19\nScale: 0.5, _d, 0\n---\n" +
        "Omen: m131om_setu0f\nTag: ENTITY\nt: 5\n" +
        "Pos: 1073741825\nTarget: 0x40000002\nScale: 0.1, _d + 14, 1",
        raw => raw switch
        {
            "1073741825" => new System.Numerics.Vector3(10, 1, 20),
            "0x40000002" => new System.Numerics.Vector3(13, 2, 24),
            _ => null,
        });
    Assert(
        protocolCommands.Count == 3 &&
        protocolCommands[0].Shape is
        {
            Position.X: var polarX,
            Position.Y: 0.1f,
            Position.Z: var polarZ,
            PrimaryScale: var cylindricalRadius,
            SecondaryScale: var cylindricalRadius2,
            TertiaryScale: 30,
            Angle: var directionAngle,
        } &&
        MathF.Abs(polarX - (100 - 3 * MathF.Sqrt(2))) < 0.0001f &&
        MathF.Abs(polarZ - (100 - 3 * MathF.Sqrt(2))) < 0.0001f &&
        MathF.Abs(cylindricalRadius - (5 * MathF.Sqrt(2) - 6)) < 0.0001f &&
        MathF.Abs(cylindricalRadius2 - cylindricalRadius) < 0.0001f &&
        MathF.Abs(directionAngle + MathF.PI / 4) < 0.0001f &&
        protocolCommands[1].Shape is
        {
            Position.X: 102,
            Position.Z: 81,
            SecondaryScale: var targetDistance,
            TertiaryScale: 0,
            Angle: var targetAngle,
        } &&
        MathF.Abs(targetDistance - MathF.Sqrt(1460)) < 0.0001f &&
        MathF.Abs(targetAngle - MathF.Atan2(-4, 38)) < 0.0001f &&
        protocolCommands[2].Shape is
        {
            Position.X: 10,
            Position.Y: 1,
            Position.Z: 20,
            SecondaryScale: 19,
            Angle: var entityAngle,
        } &&
        MathF.Abs(entityAngle - MathF.Atan2(3, 4)) < 0.0001f,
        "PictoACT Target, ScaleCyl, DirN, polar, entity-position, or math compatibility failed.");

    var entityThetaCommands = PictoActOverlayService.Parse(
        "Omen: Fan90\nTag: ENTITY_THETA_HEX\nt: 4\nO: 0x10000001\n" +
        "θ: 4001ABCD\nScale: 50, 50, 10\n---\n" +
        "Omen: Fan90\nTag: ENTITY_THETA_DIGITS\nt: 4\nO: 0x10000001\n" +
        "Theta: 40012345\nScale: 50, 50, 10",
        raw => raw switch
        {
            "0x10000001" => new System.Numerics.Vector3(10, 0, 20),
            "4001ABCD" => new System.Numerics.Vector3(13, 0, 24),
            "40012345" => new System.Numerics.Vector3(6, 0, 22),
            _ => null,
        });
    Assert(
        entityThetaCommands.Count == 2 &&
        entityThetaCommands[0].Shape is
        {
            Position.X: 10,
            Position.Z: 20,
            Angle: var hexadecimalEntityTheta,
        } &&
        MathF.Abs(hexadecimalEntityTheta - MathF.Atan2(3, 4)) < 0.0001f &&
        entityThetaCommands[1].Shape is { Angle: var allDigitEntityTheta } &&
        MathF.Abs(allDigitEntityTheta - MathF.Atan2(-4, 2)) < 0.0001f,
        "PictoACT θ/Theta did not preserve legacy entity-facing semantics.");

    var dynamicFollow = PictoActOverlayService.Parse(
        "Omen: Rect\nt: 5\nPos: 40000001\nTarget: 40000002\n" +
        "Scale: 0.5, _d, 1",
        raw => raw switch
        {
            "40000001" => new System.Numerics.Vector3(10, 0, 20),
            "40000002" => new System.Numerics.Vector3(13, 0, 24),
            _ => null,
        }).Single().Shape!;
    var refreshedDynamicFollow = PictoActOverlayService.RefreshDynamicShape(
        dynamicFollow,
        raw => raw switch
        {
            "40000001" => new System.Numerics.Vector3(20, 1, 30),
            "40000002" => new System.Numerics.Vector3(26, 2, 38),
            _ => null,
        },
        _ => null);
    Assert(
        dynamicFollow.RequiresDynamicRefresh &&
        refreshedDynamicFollow.Position is { X: 20, Y: 1, Z: 30 } &&
        MathF.Abs(refreshedDynamicFollow.Angle - MathF.Atan2(6, 8)) < 0.0001f &&
        MathF.Abs(refreshedDynamicFollow.SecondaryScale - 10) < 0.0001f,
        $"PictoACT moving Pos/Target or _d scale stopped following entity snapshots: " +
        $"pos={refreshedDynamicFollow.Position}, angle={refreshedDynamicFollow.Angle:R}, " +
        $"scaleY={refreshedDynamicFollow.SecondaryScale:R}.");

    var dynamicCenter = PictoActOverlayService.Parse(
        "Omen: Rect\nt: 5\nPos: 0, -3\nO: 10000001\nScale: 1, 6, 1",
        raw => raw == "10000001"
            ? new System.Numerics.Vector3(10, 0, 20)
            : null).Single().Shape!;
    var refreshedDynamicCenter = PictoActOverlayService.RefreshDynamicShape(
        dynamicCenter,
        raw => raw == "10000001"
            ? new System.Numerics.Vector3(100, 0, 100)
            : null,
        raw => raw == "10000001" ? MathF.PI / 2 : null);
    Assert(
        refreshedDynamicCenter.Position is { X: var centerX, Z: var centerZ } &&
        MathF.Abs(centerX - 103) < 0.0001f &&
        MathF.Abs(centerZ - 100) < 0.0001f &&
        MathF.Abs(refreshedDynamicCenter.Angle - MathF.PI / 2) < 0.0001f,
        "PictoACT dynamic O did not inherit the entity's current heading.");

    var coordinateTheta = PictoActOverlayService.Parse(
        "Omen: Fan90\nt: 5\nO: 10, 20, 0\nTheta: 13, 24, 0\n" +
        "Scale: 5").Single().Shape!;
    Assert(
        MathF.Abs(coordinateTheta.Angle - MathF.Atan2(3, 4)) < 0.0001f,
        "PictoACT θ/Theta coordinate targeting was not accepted.");

    var entityThetaBase = PictoActOverlayService.Parse(
        "Omen: Rect\nTag: ENTITY_THETA_CHANGE\nt: 5\nO: 10, 20\nθ: pi\n" +
        "Pos: 0, 0\nScale: 1, 10, 1").Single().Shape!;
    var entityThetaPatch = PictoActOverlayService.Parse(
        "Action: Change\nTag: ENTITY_THETA_CHANGE\nθ: 4001ABCD",
        raw => raw == "4001ABCD"
            ? new System.Numerics.Vector3(13, 0, 24)
            : null).Single().Patch!;
    var entityThetaChanged = PictoActOverlayService.ApplyPatch(
        entityThetaBase,
        entityThetaPatch);
    Assert(
        entityThetaChanged.Position is { X: 10, Z: 20 } &&
        MathF.Abs(entityThetaChanged.Angle - MathF.Atan2(3, 4)) < 0.0001f,
        "PictoACT Change with an entity θ lost the existing transform center.");

    var directionAndScaleDefaults = PictoActOverlayService.Parse(
        "Omen: Circle\nt: 5\nDir4: 0\nPos: 1, 2\nScale: 3").Single();
    Assert(
        directionAndScaleDefaults.Shape is
        {
            Position.X: var northX,
            Position.Z: var northZ,
            Angle: var northAngle,
            PrimaryScale: 3,
            SecondaryScale: 3,
            TertiaryScale: 1,
        } &&
        MathF.Abs(northX - 1) < 0.0001f &&
        MathF.Abs(northZ - 2) < 0.0001f &&
        MathF.Abs(MathF.Abs(northAngle) - MathF.PI) < 0.0001f,
        "PictoACT Dir north or single-value Scale defaults drifted from the original protocol.");

    var expectedP3Positions = new (float X, float Z)[][]
    {
        [
            (111.36f, 89.95f),
            (100.926310f, 84.860844f),
            (89.95f, 88.64f),
            (84.860844f, 99.073690f),
            (88.64f, 110.05f),
            (99.073690f, 115.139156f),
            (110.05f, 111.36f),
            (115.139156f, 100.926310f),
        ],
        [
            (111.36f, 110.05f),
            (115.139156f, 99.073690f),
            (110.05f, 88.64f),
            (99.073690f, 84.860844f),
            (88.64f, 89.95f),
            (84.860844f, 100.926310f),
            (89.95f, 111.36f),
            (100.926310f, 115.139156f),
        ],
    };
    for (var keepYIndex = 0; keepYIndex < expectedP3Positions.Length; keepYIndex++)
    {
        var keepY = keepYIndex == 0;
        for (var direction = 0; direction < 8; direction++)
        {
            // This is the raw U6b P3 expression path: Triggernometry forwards the
            // function text to PictoACT instead of expanding it before the callback.
            var p3Guide = PictoActOverlayService.Parse(
                $"Omen: m0532om_don01x\nt: 9.7\nO: 100, 100\n" +
                $"θ: DirToRad({direction}, 8)\n+Y: {keepY}\n" +
                "Pos: 11.36, -10.05, 0.1\nScale: 1, 1, 20").Single().Shape!;
            var expected = expectedP3Positions[keepYIndex][direction];
            Assert(
                MathF.Abs(p3Guide.Position.X - expected.X) < 0.0001f &&
                MathF.Abs(p3Guide.Position.Z - expected.Z) < 0.0001f,
                $"PictoACT U6b P3 DirToRad transform drifted for direction {direction}, " +
                $"keepY={keepY}: actual={p3Guide.Position}, expected=({expected.X}, {expected.Z}).");
        }
    }

    var negativeDirectionDivision = PictoActOverlayService.Parse(
        "Omen: Circle\nt: 5\nO: 100, 100\nθ: dir2rad(0, -8)\n" +
        "Pos: 11.36, -10.05, 0.1\nScale: 1").Single().Shape!;
    Assert(
        negativeDirectionDivision.TransformRotation is { } negativeRotation &&
        MathF.Abs(negativeRotation - (-MathF.PI * 7 / 8)) < 0.0001f,
        "PictoACT dir2rad negative divisions lost Triggernometry's half-step semantics.");

    var polygonCommand = PictoActOverlayService.Parse(
        "Action: △\nTag: POLYGON\nt: 8\nO: 100, 100\nθ: pi\n" +
        "Points: 0, -12; 3, -20; -3, -20; 0.001, -12; 0, -16\n" +
        "Color: 0, 1.2, 6, 1.5").Single();
    Assert(
        polygonCommand.Shape is
        {
            Kind: PictoActShapeKind.Polygon,
            SourcePolygon: { Count: 3 },
            Polygon: { Count: 3 } polygon,
        } &&
        MathF.Abs(polygon[0].X - 100) < 0.0001f &&
        MathF.Abs(polygon[0].Z - 88) < 0.0001f,
        "PictoACT △/Triangulate polygon parsing or trailing seed removal failed.");

    var nativePolygonShapes = PictoActOverlayService.BuildNativeShapes(polygonCommand.Shape!);
    Assert(
        nativePolygonShapes is
        [
            {
                Kind: PictoActShapeKind.NativeOnly,
                VfxPath: "vfx/omen/eff/x6d3_b2_triangle90_p1.avfx",
                Position.X: 100,
                Position.Z: 88,
                PrimaryScale: var polygonHalfBase,
                SecondaryScale: var polygonHeight,
                Color.Z: 6,
            },
        ] &&
        MathF.Abs(polygonHalfBase - 3 * MathF.Sqrt(2)) < 0.0001f &&
        MathF.Abs(polygonHeight - 8 * MathF.Sqrt(2)) < 0.0001f,
        "PictoACT Polygon did not map to the original game-native triangle omen geometry.");

    var concavePolygon = PictoActOverlayService.Parse(
        "Action: Triangulate\nTag: CONCAVE\nt: 8\n" +
        "Points: 0, 0; 4, 0; 4, 4; 2, 2; 0, 4\n" +
        "Color: 0.2, 0.8, 1, 0.7").Single().Shape!;
    var concaveNativeShapes = PictoActOverlayService.BuildNativeShapes(concavePolygon);
    var nativeTriangleArea = concaveNativeShapes.Sum(shape =>
        shape.PrimaryScale * shape.SecondaryScale / 2);
    Assert(
        concaveNativeShapes.Count >= 3 &&
        concaveNativeShapes.All(shape =>
            shape.Kind == PictoActShapeKind.NativeOnly &&
            shape.VfxPath == "vfx/omen/eff/x6d3_b2_triangle90_p1.avfx" &&
            shape.HasExplicitColor) &&
        MathF.Abs(nativeTriangleArea - 12) < 0.001f,
        $"PictoACT concave Polygon native decomposition changed its filled area " +
        $"(parts={concaveNativeShapes.Count}, area={nativeTriangleArea:R}).");

    var persistentPolygon = PictoActOverlayService.Parse(
        "Action: Triangulate\nt: 0\nPoints: 0,0; 4,0; 0,4").Single().Shape!;
    Assert(
        persistentPolygon.ExpiresAt == DateTimeOffset.MaxValue,
        "PictoACT Triangulate t: 0 did not preserve upstream persistent-duration semantics.");

    var clippedToViewport = PictoActOverlayService.ClipPolygonToRectangle(
        [
            new System.Numerics.Vector2(-20, 20),
            new System.Numerics.Vector2(50, 20),
            new System.Numerics.Vector2(50, 80),
            new System.Numerics.Vector2(-20, 80),
            new System.Numerics.Vector2(-20, 20),
        ],
        System.Numerics.Vector2.Zero,
        new System.Numerics.Vector2(100, 100));
    Assert(
        clippedToViewport.Count == 4 &&
        clippedToViewport.All(point =>
            point.X is >= 0 and <= 100 && point.Y is >= 0 and <= 100) &&
        clippedToViewport.Any(point => MathF.Abs(point.X) < 0.0001f),
        "PictoACT fallback fill did not clip an off-screen polygon to the viewport.");
    Assert(
        PictoActOverlayService.ClipPolygonToRectangle(
            [
                new System.Numerics.Vector2(-40, 20),
                new System.Numerics.Vector2(-20, 20),
                new System.Numerics.Vector2(-20, 40),
            ],
            System.Numerics.Vector2.Zero,
            new System.Numerics.Vector2(100, 100)).Count == 0,
        "PictoACT fallback clipping retained a polygon outside the viewport.");

    static (bool InFront, System.Numerics.Vector2 Screen) ProjectNearPlanePoint(
        System.Numerics.Vector3 point)
    {
        if (point.Z <= 0.1f)
        {
            return (false, default);
        }

        return (
            true,
            new System.Numerics.Vector2(
                50 + point.X / point.Z * 20,
                50 - (point.Y + 1) / point.Z * 20));
    }

    var nearPlaneClipped = PictoActOverlayService.ProjectTriangleAcrossNearPlane(
        new System.Numerics.Vector3(-2, 0, 2),
        new System.Numerics.Vector3(2, 0, 2),
        new System.Numerics.Vector3(0, 0, -1),
        ProjectNearPlanePoint);
    var nearPlaneViewportFill = PictoActOverlayService.ClipPolygonToRectangle(
        nearPlaneClipped,
        System.Numerics.Vector2.Zero,
        new System.Numerics.Vector2(100, 100));
    Assert(
        nearPlaneClipped.Count == 4 &&
        nearPlaneViewportFill.Count >= 3 &&
        nearPlaneViewportFill.All(point =>
            float.IsFinite(point.X) &&
            float.IsFinite(point.Y) &&
            point.X is >= 0 and <= 100 &&
            point.Y is >= 0 and <= 100),
        "PictoACT fallback fill discarded a triangle crossing the camera near plane.");
    Assert(
        PictoActOverlayService.ProjectTriangleAcrossNearPlane(
            new System.Numerics.Vector3(-2, 0, -2),
            new System.Numerics.Vector3(2, 0, -2),
            new System.Numerics.Vector3(0, 0, -1),
            ProjectNearPlanePoint).Count == 0,
        "PictoACT fallback fill retained a triangle entirely behind the camera.");

    overlay.Apply("Action: Remove");
    overlay.Apply(
        "Action: Triangulate\nTag: POLYGON\nt: 8\nO: 100, 100\nθ: pi\n" +
        "Points: 0, -12; 3, -20; -3, -20\nColor: 0, 1.2, 6, 1.5");
    overlay.Apply("Action: Change\nTag: POLYGON\nO: 110, 90\nθ: pi/2");
    Assert(
        overlay.ShapeSnapshot.Single() is
        {
            Kind: PictoActShapeKind.Polygon,
            Polygon: { } changedPolygon,
        } &&
        MathF.Abs(changedPolygon[0].X - 122) < 0.0001f &&
        MathF.Abs(changedPolygon[0].Z - 90) < 0.0001f,
        "PictoACT Change did not update a Triangulate polygon transform.");

    overlay.Apply("Action: Remove");
    overlay.Apply(
        "Omen: m131om_setu0f\nTag: TARGET_CHANGE\nt: 5\nPos: 0, 0\n" +
        "Target: 0, 10\nScale: 0.2, _d, 0.2");
    overlay.Apply(
        "Action: Change\nTag: TARGET_CHANGE\nPos: 2, 1\nTarget: 5, 5\n" +
        "Scale: 0.3, _d + 1, 0");
    Assert(
        overlay.ShapeSnapshot.Single() is
        {
            Position.X: 2,
            Position.Z: 1,
            SecondaryScale: 6,
            TertiaryScale: 0,
            Angle: var changedTargetAngle,
        } &&
        MathF.Abs(changedTargetAngle - MathF.Atan2(3, 4)) < 0.0001f,
        "PictoACT Change did not recalculate Target angle or _d scale.");

    overlay.Apply("Action: Remove");
    overlay.Apply("Omen: Circle\nTag: DELAYED\nt: 30\nPos: 1, 2\nScale: 3");
    overlay.Apply("Action: Change\nTag: DELAYED\nDelay: 10\nPos: 8, 9");
    Assert(
        overlay.ShapeSnapshot.Single().SourcePosition.X == 1,
        "PictoACT executed a delayed Change immediately.");
    overlay.ProcessPending(DateTimeOffset.UtcNow.AddSeconds(11));
    Assert(
        overlay.ShapeSnapshot.Single().SourcePosition is { X: 8, Z: 9 },
        "PictoACT did not execute a delayed Change when it became due.");

    overlay.Apply("Action: Remove\nTag: DELAYED\nDelay: 10");
    overlay.Apply("Action: Remove\nTag: DELAYED");
    overlay.ProcessPending(DateTimeOffset.UtcNow.AddSeconds(11));
    Assert(
        overlay.ShapeSnapshot.Count == 0,
        "PictoACT Remove did not cancel matching delayed operations.");

    overlay.Apply("Omen: Circle\nTag: OLD_ZONE\nt: 30\nPos: 1, 2\nScale: 3");
    overlay.Apply("Omen: Circle\nTag: OLD_ZONE_DELAYED\nDelay: 10\nt: 30\nPos: 3, 4\nScale: 3");
    overlay.Clear();
    overlay.ProcessPending(DateTimeOffset.UtcNow.AddSeconds(11));
    Assert(
        overlay.ShapeSnapshot.Count == 0,
        "PictoACT territory cleanup left a live or delayed drawing behind.");

    overlay.Apply(
        "Omen: Rect\nTag: ANGLE_MODE\nt: 30\nPos: 0, 0\nTarget: 0, 10\nScale: 1, 10, 1");
    overlay.Apply("Action: Change\nTag: ANGLE_MODE\nAngle: pi/2");
    Assert(
        overlay.ShapeSnapshot.Single() is
        {
            SourceTarget: null,
            SourceAngle: var angleModeAngle,
        } &&
        MathF.Abs(angleModeAngle - MathF.PI / 2) < 0.0001f,
        "PictoACT Angle Change did not switch a Target shape back to angle mode.");

    var rejectedTargetAndAngle = false;
    try
    {
        _ = PictoActOverlayService.Parse(
            "Omen: Rect\nt: 5\nPos: 0, 0\nTarget: 0, 10\nAngle: 0");
    }
    catch (InvalidDataException)
    {
        rejectedTargetAndAngle = true;
    }

    Assert(
        rejectedTargetAndAngle,
        "PictoACT accepted mutually exclusive Target and Angle fields.");
}

static void ValidatePluginRepositoryMetadata()
{
    var projectRoot = FindProjectRoot();
    using var document = JsonDocument.Parse(File.ReadAllText(
        Path.Combine(projectRoot, "repo", "pluginmaster.json")));
    var entry = document.RootElement.EnumerateArray().Single();
    var assemblyVersion = typeof(ControlCenterWindow).Assembly
        .GetName()
        .Version!
        .ToString(4);
    var expectedDownloadSuffix = $"/v{assemblyVersion}/DalamudActCompat-core.zip";
    Assert(
        entry.GetProperty("AssemblyVersion").GetString() == assemblyVersion &&
        entry.GetProperty("DownloadLinkInstall").GetString() is { } installUrl &&
        installUrl.EndsWith(expectedDownloadSuffix, StringComparison.Ordinal) &&
        entry.GetProperty("DownloadLinkUpdate").GetString() is { } updateUrl &&
        updateUrl.EndsWith(expectedDownloadSuffix, StringComparison.Ordinal) &&
        entry.GetProperty("CanUnloadAsync").GetBoolean() &&
        entry.GetProperty("DownloadLinkTesting").ValueKind == JsonValueKind.Null,
        "Dalamud custom repository version or release download links drifted from the plugin assembly.");
    var iconUrl = entry.GetProperty("IconUrl").GetString();
    Assert(
        Uri.TryCreate(iconUrl, UriKind.Absolute, out var iconUri) &&
        iconUri.Scheme == Uri.UriSchemeHttps &&
        iconUri.Host == "raw.githubusercontent.com" &&
        iconUri.AbsolutePath.EndsWith(
            "/src/DalamudActCompat/Assets/act-logo.jpg",
            StringComparison.Ordinal),
        "Dalamud custom repository does not expose the ACT logo through a public HTTPS IconUrl.");
    Assert(
        File.Exists(Path.Combine(
            projectRoot,
            "src",
            "DalamudActCompat",
            "Assets",
            "act-logo.jpg")),
        "The public custom-repository IconUrl target is missing from the source repository.");
}

static void ValidateBoundedHostQueue()
{
    using var queue = new BoundedHostMessageQueue();
    var session = Guid.NewGuid().ToString("N");
    for (var index = 0; index < HostProtocol.ControlQueueCapacity; index++)
    {
        Assert(
            queue.TryEnqueue(HostEnvelope.Create(
                session,
                index + 1,
                HostMessageTypes.Heartbeat,
                HostMessagePriority.Control,
                new { index })),
            "Control queue rejected an item before reaching its configured capacity.");
    }

    Assert(
        !queue.TryEnqueue(HostEnvelope.Create(
            session,
            HostProtocol.ControlQueueCapacity + 1,
            HostMessageTypes.Heartbeat,
            HostMessagePriority.Control,
            new { overflow = true })),
        "Control queue did not reject overflow.");

    queue.Clear();
    for (var index = 0; index <= HostProtocol.DataQueueCapacity; index++)
    {
        Assert(
            queue.TryEnqueue(HostEnvelope.Create(
                session,
                index + 1,
                HostMessageTypes.LogBatch,
                HostMessagePriority.Data,
                new { index })),
            "Data queue rejected a low-priority item instead of applying drop-oldest backpressure.");
    }

    Assert(
        queue.DataCount == HostProtocol.DataQueueCapacity,
        "Data queue exceeded its configured bound.");
    Assert(
        queue.DroppedDataMessages == 1,
        "Data queue did not record its dropped oldest item.");

    var existingDataCount = queue.DataCount;
    var existingDropCount = queue.DroppedDataMessages;
    for (var index = 0; index <= HostProtocol.SilverDasherQueueCapacity; index++)
    {
        Assert(
            queue.TryEnqueue(HostEnvelope.Create(
                session,
                HostProtocol.DataQueueCapacity + index + 2,
                HostMessageTypes.SilverDasherNetworkReceived,
                HostMessagePriority.SilverDasherData,
                new HostSilverDasherNetworkEvent("down", index, [1, 2, 3]))),
            "SilverDasher queue rejected a low-priority event instead of dropping its own oldest event.");
    }

    Assert(
        queue.SilverDasherCount == HostProtocol.SilverDasherQueueCapacity &&
        queue.DroppedSilverDasherMessages == 1,
        "SilverDasher event queue did not apply its independent bound.");
    Assert(
        queue.DataCount == existingDataCount &&
        queue.DroppedDataMessages == existingDropCount,
        "SilverDasher backpressure changed the existing ACT plugin data queue.");
    queue.Clear();
    Assert(
        queue.TryEnqueue(HostEnvelope.Create(
            session,
            1,
            HostMessageTypes.Snapshot,
            HostMessagePriority.State,
            new { value = 1 })) &&
        queue.TryEnqueue(HostEnvelope.Create(
            session,
            2,
            HostMessageTypes.Snapshot,
            HostMessagePriority.State,
            new { value = 2 })) &&
        queue.DataCount == 1,
        "State messages were not coalesced to the latest value.");

    queue.Clear();
    Assert(
        queue.TryEnqueue(HostEnvelope.Create(
            session,
            1,
            HostMessageTypes.FfxivEntities,
            HostMessagePriority.State,
            new { full = true })) &&
        queue.TryEnqueue(HostEnvelope.Create(
            session,
            2,
            HostMessageTypes.FfxivEntityDelta,
            HostMessagePriority.State,
            new { revision = 1 })) &&
        queue.TryEnqueue(HostEnvelope.Create(
            session,
            3,
            HostMessageTypes.FfxivEntityDelta,
            HostMessagePriority.State,
            new { revision = 2 })) &&
        queue.DataCount == 2,
        "Entity full/delta state did not keep one fallback and one coalesced latest delta.");
    var entityMessages = new[]
    {
        queue.DequeueAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult(),
        queue.DequeueAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult(),
    };
    Assert(
        entityMessages.Any(envelope => envelope.Type == HostMessageTypes.FfxivEntities) &&
        entityMessages.Single(envelope => envelope.Type == HostMessageTypes.FfxivEntityDelta)
            .Payload.GetProperty("revision").GetInt32() == 2,
        "Entity state coalescing did not retain the newest incremental overlay.");

    queue.Clear();
    for (var index = 0; index < HostProtocol.ControlQueueCapacity * 4; index++)
    {
        Assert(
            queue.TryEnqueue(HostEnvelope.Create(
                session,
                index + 1,
                index % 2 == 0
                    ? HostMessageTypes.CombatStarted
                    : HostMessageTypes.CombatEnded,
                HostMessagePriority.State,
                new HostCombatEvent(index % 2 == 0, DateTimeOffset.UtcNow))),
            "Combat state coalescing rejected a noisy transition.");
    }

    var latestCombatState = queue.DequeueAsync(CancellationToken.None)
        .AsTask()
        .GetAwaiter()
        .GetResult();
    Assert(
        queue.ControlCount == 0 &&
        queue.DataCount == 0 &&
        latestCombatState.Type == HostMessageTypes.CombatEnded,
        "Alternating combat transitions were not coalesced to their single latest state.");
}

static void ValidateHostMemoryProtectionPolicy()
{
    const long gib = 1024L * 1024L * 1024L;
    var startedAt = DateTimeOffset.Parse("2026-08-22T12:00:00+08:00");
    var policy = new HostMemoryProtectionPolicy();

    HostResourceSample Sample(
        DateTimeOffset timestamp,
        long privateBytes,
        long availableBytes = 8L * gib)
        => new(timestamp, privateBytes, privateBytes, availableBytes, 20);

    var ordinaryUsage = policy.Observe(Sample(startedAt, 16L * gib / 10), inCombat: false);
    Assert(
        ordinaryUsage.Snapshot.State == HostMemoryProtectionState.Normal &&
        !ordinaryUsage.ShouldRecycle,
        "A legitimate 1.6 GiB shared Host was treated as a memory leak.");

    var initialBreach = policy.Observe(Sample(startedAt, 3L * gib), inCombat: false);
    var briefBreach = policy.Observe(Sample(startedAt.AddSeconds(14), 3L * gib), inCombat: false);
    Assert(
        initialBreach.Snapshot.State == HostMemoryProtectionState.Monitoring &&
        !initialBreach.ShouldRecycle &&
        !briefBreach.ShouldRecycle,
        "The 3 GiB threshold recycled the Host without the 15-second confirmation window.");

    var combatBreach = policy.Observe(Sample(startedAt.AddSeconds(15), 3L * gib), inCombat: true);
    var afterCombat = policy.Observe(Sample(startedAt.AddSeconds(16), 3L * gib), inCombat: false);
    Assert(
        combatBreach.Snapshot.State == HostMemoryProtectionState.DeferredForCombat &&
        !combatBreach.ShouldRecycle &&
        afterCombat.ShouldRecycle,
        "Sustained ordinary pressure did not defer during combat and recover after combat.");

    policy.ResetForNewSession();
    var emergencyStart = policy.Observe(Sample(startedAt, 4L * gib), inCombat: true);
    var emergencyEarly = policy.Observe(Sample(startedAt.AddSeconds(9), 4L * gib), inCombat: true);
    var emergencyDue = policy.Observe(Sample(startedAt.AddSeconds(10), 4L * gib), inCombat: true);
    Assert(
        emergencyStart.Snapshot.State == HostMemoryProtectionState.EmergencyCountdown &&
        !emergencyStart.ShouldRecycle &&
        !emergencyEarly.ShouldRecycle &&
        emergencyDue.ShouldRecycle,
        "The 4 GiB emergency path did not preserve its cancellable 10-second countdown.");

    policy.ResetForNewSession();
    var lowMemoryStart = policy.Observe(
        Sample(startedAt, 3L * gib, availableBytes: gib),
        inCombat: true);
    Assert(
        lowMemoryStart.Snapshot.State == HostMemoryProtectionState.EmergencyCountdown &&
        !lowMemoryStart.ShouldRecycle,
        "Low system memory above the 3 GiB Host floor did not enter the emergency countdown.");

    policy.IgnoreCurrentSession();
    var ignored = policy.Observe(
        Sample(startedAt.AddMinutes(1), 5L * gib, availableBytes: gib),
        inCombat: false);
    Assert(
        ignored.Snapshot.State == HostMemoryProtectionState.Ignored &&
        !ignored.ShouldRecycle,
        "Ignoring automatic recovery for the current Host session was not respected.");
}

static void ValidateBoundedNotActQueues()
{
    var actMain = (FormActMain)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
        typeof(FormActMain));
    typeof(FormActMain).GetField(
            "<PluginLog>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic)!
        .SetValue(actMain, new TestActLogger());
    typeof(FormActMain).GetField(
            "<LogQueue>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic)!
        .SetValue(actMain, new System.Collections.Concurrent.ConcurrentQueue<string>());
    typeof(FormActMain).GetField(
            "afterActionsQueue",
            BindingFlags.Instance | BindingFlags.NonPublic)!
        .SetValue(
            actMain,
            new System.Collections.Concurrent.ConcurrentQueue<MasterSwing>());

    var enqueueLogLine = typeof(FormActMain).GetMethod(
                             "EnqueueLogLine",
                             BindingFlags.Instance | BindingFlags.NonPublic)
                         ?? throw new MissingMethodException(
                             typeof(FormActMain).FullName,
                             "EnqueueLogLine");
    for (var index = 0; index < 8193; index++)
    {
        enqueueLogLine.Invoke(actMain, [$"line-{index}"]);
    }

    Assert(actMain.LogQueue.Count == 8192, "NotACT log queue exceeded 8192 entries.");
    Assert(actMain.DroppedLogLines == 1, "NotACT log queue did not report drop-oldest.");

    var enqueueCombatAction = typeof(FormActMain).GetMethod(
                                  "EnqueueCombatAction",
                                  BindingFlags.Instance | BindingFlags.NonPublic)
                              ?? throw new MissingMethodException(
                                  typeof(FormActMain).FullName,
                                  "EnqueueCombatAction");
    for (var index = 0; index < 4097; index++)
    {
        enqueueCombatAction.Invoke(
            actMain,
            [System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(MasterSwing))]);
    }

    Assert(
        actMain.DroppedCombatActions == 1,
        "NotACT combat action queue did not report drop-oldest.");
}

static void ValidateActCustomTriggerCompatibility()
{
    var property = typeof(FormActMain).GetProperty(nameof(FormActMain.CustomTriggers));
    Assert(
        property?.PropertyType == typeof(SortedList<string, CustomTrigger>) &&
        property.GetMethod is not null,
        "ACT CustomTriggers getter is missing or has an incompatible ABI.");

    var expectedProperties = new Dictionary<string, Type>
    {
        [nameof(CustomTrigger.Active)] = typeof(bool),
        [nameof(CustomTrigger.RestrictToCategoryZone)] = typeof(bool),
        [nameof(CustomTrigger.Tabbed)] = typeof(bool),
        [nameof(CustomTrigger.Timer)] = typeof(bool),
        [nameof(CustomTrigger.SoundType)] = typeof(int),
        [nameof(CustomTrigger.Category)] = typeof(string),
        [nameof(CustomTrigger.ShortRegexString)] = typeof(string),
        [nameof(CustomTrigger.SoundData)] = typeof(string),
        [nameof(CustomTrigger.TimerName)] = typeof(string),
    };
    foreach (var expected in expectedProperties)
    {
        Assert(
            typeof(CustomTrigger).GetProperty(expected.Key)?.PropertyType == expected.Value,
            $"ACT CustomTrigger.{expected.Key} is missing or has an incompatible type.");
    }
}

static void ValidateSynchronousActInvocation()
{
    var actMain = (FormActMain)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
        typeof(FormActMain));
    actMain.InvokeSynchronously = true;
    var calls = 0;
    var result = actMain.Invoke((Func<int>)(() => ++calls));
    var asyncResult = actMain.BeginInvoke((Func<int>)(() => ++calls), null);
    var asyncValue = actMain.EndInvoke(asyncResult);
    Assert(
        result is 1 && asyncValue is 2 && calls == 2,
        "Handle-free ACT invocation did not execute synchronously in the in-process runtime.");
}

static void ValidateActCallbackCircuitBreaker()
{
    var actMain = (FormActMain)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
        typeof(FormActMain));
    typeof(FormActMain).GetField(
            "<PluginLog>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic)!
        .SetValue(actMain, new TestActLogger());
    var healthField = typeof(FormActMain).GetField(
                          "callbackHealth",
                          BindingFlags.Instance | BindingFlags.NonPublic)
                      ?? throw new MissingFieldException(
                          typeof(FormActMain).FullName,
                          "callbackHealth");
    healthField.SetValue(
        actMain,
        Activator.CreateInstance(healthField.FieldType)
        ?? throw new InvalidOperationException("Could not create callback health dictionary."));
    var invokeTracked = typeof(FormActMain).GetMethod(
                            "InvokeTracked",
                            BindingFlags.Instance | BindingFlags.NonPublic)
                        ?? throw new MissingMethodException(
                            typeof(FormActMain).FullName,
                            "InvokeTracked");
    var calls = 0;
    LogLineEventDelegate handler = (_, _) =>
    {
        calls++;
        throw new InvalidOperationException("injected callback failure");
    };
    Action<Delegate> invoke = callback =>
        ((LogLineEventDelegate)callback)(false, null!);
    for (var index = 0; index < 6; index++)
    {
        invokeTracked.Invoke(actMain, [handler, "FaultInjection", invoke]);
    }

    var health = actMain.GetCallbackHealth().Single();
    Assert(calls == 5, "Callback circuit did not stop the sixth failing invocation.");
    Assert(
        health.Exceptions == 5 && health.CircuitOpen,
        "Five consecutive callback exceptions did not open the per-plugin circuit.");
}

static void ValidateReflectionActLoggerOverloads()
{
    var adapterType = typeof(FormActMain).Assembly.GetType(
                          "Advanced_Combat_Tracker.ReflectionActLogger",
                          throwOnError: true)!
                      ?? throw new TypeLoadException(
                          "Advanced_Combat_Tracker.ReflectionActLogger");
    var probe = new OverloadedActLoggerProbe();
    var adapter = (IActLogger)(Activator.CreateInstance(
                      adapterType,
                      BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                      binder: null,
                      args: [probe],
                      culture: null)
                  ?? throw new InvalidOperationException(
                      "Could not create the reflected ACT logger adapter."));
    var expected = new ArgumentException("original parser failure");
    adapter.Error(expected, "preserve this failure");

    Assert(
        ReferenceEquals(probe.Exception, expected) &&
        probe.Message == "preserve this failure" &&
        !probe.StringOnlyOverloadCalled,
        "The ACT logger selected a string overload for an exception and replaced the parser failure.");
}

static void ValidateMeterRows()
{
    var defaultColor = new MeterSettings().LocalPlayerColor;
    var requestedDefault = new System.Numerics.Vector4(
        0x8B / 255f,
        0x57 / 255f,
        0x33 / 255f,
        0x73 / 255f);
    Assert(
        System.Numerics.Vector4.DistanceSquared(defaultColor, requestedDefault) < 0.000001f,
        "The Combat Meter local-player highlight is not #8B573373 by default.");

    var legacyConfiguration = new PluginConfiguration
    {
        Version = 1,
        Meter = new MeterSettings
        {
            LocalPlayerColor = new System.Numerics.Vector4(0.25f, 0.42f, 0.55f, 0.45f),
        },
    };
    Assert(
        legacyConfiguration.ApplyMigrations() &&
        legacyConfiguration.Version == 16 &&
        legacyConfiguration.Meter.DpsMetric == DpsMetric.Rdps &&
        legacyConfiguration.EnableParsing &&
        legacyConfiguration.AutoStartParser &&
        System.Numerics.Vector4.DistanceSquared(
            legacyConfiguration.Meter.LocalPlayerColor,
            requestedDefault) < 0.000001f,
        "The previous Combat Meter default highlight or parser startup was not migrated.");
    var customColor = new System.Numerics.Vector4(0.1f, 0.2f, 0.3f, 0.4f);
    var customizedConfiguration = new PluginConfiguration
    {
        Version = 1,
        Meter = new MeterSettings { LocalPlayerColor = customColor },
    };
    customizedConfiguration.ApplyMigrations();
    Assert(
        customizedConfiguration.Meter.LocalPlayerColor == customColor,
        "The default-color migration overwrote a user-customized highlight.");
    var parserMigration = new PluginConfiguration
    {
        Version = 2,
        EnableParsing = false,
        AutoStartParser = false,
        BundledPluginDisclosureKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["act.foxtts"] = "already-acknowledged",
        },
    };
    Assert(
        parserMigration.ApplyMigrations() &&
        parserMigration.Version == 16 &&
        parserMigration.Meter.DpsMetric == DpsMetric.Rdps &&
        parserMigration.EnableParsing &&
        parserMigration.AutoStartParser,
        "An existing user with prior third-party acknowledgements did not receive the one-time parser startup migration.");
    parserMigration.EnableParsing = false;
    parserMigration.AutoStartParser = false;
    Assert(
        !parserMigration.ApplyMigrations() &&
        !parserMigration.EnableParsing &&
        !parserMigration.AutoStartParser,
        "A post-migration manual parser preference was overwritten.");
    var newConfiguration = new PluginConfiguration();
    Assert(
        newConfiguration.Version == 16 &&
        newConfiguration.DisabledActPluginIds.Contains("silverdasher") &&
        newConfiguration.Meter.DpsMetric == DpsMetric.Rdps &&
        newConfiguration.EnableParsing &&
        newConfiguration.AutoStartParser &&
        newConfiguration.HideHtmlOverlaysWhenGameUnfocused &&
        !newConfiguration.SimplifiedModeEnabled &&
        !newConfiguration.EnableFflogsParityRecorder &&
        newConfiguration.GetOverlayWindowSettings(
            SelfHostedActRuntime.CactbotOverlayName).OpenOnStartup &&
        newConfiguration.GetOverlayWindowSettings(
            SelfHostedActRuntime.CactbotOverlayName).HasBeenOpened,
        "A new installation does not default to rDPS, keep SilverDasher disabled, or start the parser independently of third-party confirmation.");
    Assert(
        newConfiguration.Meter.ClassicWindow.IsEnabled &&
        !newConfiguration.Meter.HorizontalWindow.IsEnabled &&
        !newConfiguration.Meter.RoleSplitWindow.IsEnabled &&
        newConfiguration.Meter.ClassicWindow.Slots.Count >= 8 &&
        newConfiguration.Meter.HorizontalWindow.Slots.Count >= 8 &&
        newConfiguration.Meter.RoleSplitWindow.Slots.Count >= 8 &&
        newConfiguration.Meter.RoleSplitDamageWindow.Slots.Count >= 8 &&
        newConfiguration.Meter.RoleSplitHealerWindow.Slots.Count >= 8 &&
        newConfiguration.Meter.RoleSplitDamageSlots.Count >= 8 &&
        newConfiguration.Meter.RoleSplitHealerSlots.Count >= 8 &&
        newConfiguration.Meter.ShowTotalDamage &&
        newConfiguration.Meter.ShowHighestDamage,
        "The classic default or independently configurable Meter windows are missing.");
    var previousHorizontalUser = new PluginConfiguration
    {
        Version = 11,
        Meter = new MeterSettings
        {
            Preset = MeterPreset.HorizontalTransparent,
            IsLocked = true,
            AutoHideOutOfCombat = true,
        },
    };
    Assert(
        previousHorizontalUser.ApplyMigrations() &&
        previousHorizontalUser.Version == 16 &&
        !previousHorizontalUser.Meter.ClassicWindow.IsEnabled &&
        previousHorizontalUser.Meter.HorizontalWindow.IsEnabled &&
        previousHorizontalUser.Meter.HorizontalWindow.IsLocked &&
        previousHorizontalUser.Meter.HorizontalWindow.AutoHideOutOfCombat &&
        !previousHorizontalUser.Meter.RoleSplitWindow.IsEnabled,
        "The selected legacy preset was not migrated to its independent window.");
    var previousCompositeIdentityUser = new PluginConfiguration
    {
        Version = 12,
    };
    previousCompositeIdentityUser.Meter.ClassicWindow.ItemWidth = 210;
    previousCompositeIdentityUser.Meter.ClassicWindow.IsEnabled = true;
    previousCompositeIdentityUser.Meter.HorizontalWindow.IsEnabled = true;
    previousCompositeIdentityUser.Meter.ClassicWindow.Slots =
    [
        new MeterSlotDefinition(MeterSlotMetric.Job, 0, 0, 4, 2, MeterSlotAlignment.Left),
        new MeterSlotDefinition(MeterSlotMetric.PlayerName, 0, 0, 4, 2, MeterSlotAlignment.Left)
        {
            Visible = false,
        },
        new MeterSlotDefinition(MeterSlotMetric.Dps, 0, 0, 4, 2, MeterSlotAlignment.Left),
    ];
    Assert(
        previousCompositeIdentityUser.ApplyMigrations() &&
        previousCompositeIdentityUser.Version == 16 &&
        previousCompositeIdentityUser.Meter.ActiveWindowKind == MeterWindowKind.Classic &&
        previousCompositeIdentityUser.Meter.ClassicWindow.IsEnabled &&
        !previousCompositeIdentityUser.Meter.HorizontalWindow.IsEnabled &&
        !previousCompositeIdentityUser.Meter.RoleSplitWindow.IsEnabled &&
        previousCompositeIdentityUser.Meter.ClassicWindow.ItemWidth == 150 &&
        previousCompositeIdentityUser.Meter.ClassicWindow.Slots.Count(static slot =>
            slot.Metric == MeterSlotMetric.PlayerIdentity) == 1 &&
        previousCompositeIdentityUser.Meter.ClassicWindow.Slots.All(static slot =>
            slot.Metric is not MeterSlotMetric.Job and not MeterSlotMetric.PlayerName) &&
        previousCompositeIdentityUser.Meter.ClassicWindow.Slots.Any(static slot =>
            slot.Metric == MeterSlotMetric.Fflogs) &&
        previousCompositeIdentityUser.Meter.ClassicWindow.Slots.Any(static slot =>
            !slot.Visible && slot.Metric == MeterSlotMetric.EncDps) &&
        previousCompositeIdentityUser.Meter.ClassicWindow.Slots.Any(static slot =>
            !slot.Visible && slot.Metric == MeterSlotMetric.ExtDps),
        "The v13/v14 migrations did not merge job/name, add independent DPS rates, preserve the classic table, or enforce one active meter.");
    var previousOpacityUser = new PluginConfiguration
    {
        Version = 13,
        Meter = new MeterSettings
        {
            BackgroundOpacity = 0.42f,
            RoleSplitDamageCompact = true,
            RoleSplitHealerCompact = true,
        },
    };
    Assert(
        previousOpacityUser.ApplyMigrations() &&
        previousOpacityUser.Version == 16 &&
        Math.Abs(previousOpacityUser.Meter.ClassicWindow.BackgroundOpacity - 0.42f) < 0.0001f &&
        Math.Abs(previousOpacityUser.Meter.RoleSplitWindow.BackgroundOpacity - 0.42f) < 0.0001f &&
        previousOpacityUser.Meter.RoleSplitDamageCompact &&
        previousOpacityUser.Meter.RoleSplitHealerCompact,
        "The v14 migration did not preserve opacity or the independent role-collapse states.");
    var previousSharedRoleUser = new PluginConfiguration
    {
        Version = 14,
    };
    previousSharedRoleUser.Meter.RoleSplitWindow.Slots =
    [
        new MeterSlotDefinition(
            MeterSlotMetric.Hps,
            0,
            0,
            4,
            2,
            MeterSlotAlignment.Right)
        {
            Visible = false,
        },
        new MeterSlotDefinition(
            MeterSlotMetric.Dps,
            0,
            0,
            4,
            2,
            MeterSlotAlignment.Right),
    ];
    Assert(
        previousSharedRoleUser.ApplyMigrations() &&
        previousSharedRoleUser.Version == 16 &&
        previousSharedRoleUser.Meter.RoleSplitDamageSlots.Select(static slot =>
            (slot.Metric, slot.Visible)).SequenceEqual(
                previousSharedRoleUser.Meter.RoleSplitHealerSlots.Select(static slot =>
                    (slot.Metric, slot.Visible))) &&
        previousSharedRoleUser.Meter.RoleSplitDamageSlots.Zip(
            previousSharedRoleUser.Meter.RoleSplitHealerSlots).All(static pair =>
                pair.First.Id != pair.Second.Id),
        "The v15 migration did not clone the former shared role columns into independent D/T and H lists.");
    previousSharedRoleUser.Meter.RoleSplitHealerSlots[0].Visible = true;
    Assert(
        !previousSharedRoleUser.Meter.RoleSplitDamageSlots[0].Visible,
        "Editing an H column still mutates the D/T column configuration.");
    var healerDpsSlot = previousSharedRoleUser.Meter.RoleSplitHealerSlots.Single(static slot =>
        slot.Metric == MeterSlotMetric.Dps);
    healerDpsSlot.Visible = false;
    previousSharedRoleUser.Meter.NormalizeCustomization();
    Assert(
        !healerDpsSlot.Visible,
        "Normalizing the H column list forced DPS back on after the user disabled it.");
    var previousSharedRoleAppearance = new PluginConfiguration
    {
        Version = 15,
    };
    previousSharedRoleAppearance.Meter.ActivateWindow(MeterWindowKind.RoleSplit);
    previousSharedRoleAppearance.Meter.RoleSplitWindow.IsLocked = true;
    previousSharedRoleAppearance.Meter.RoleSplitWindow.ClickThroughWhenLocked = true;
    previousSharedRoleAppearance.Meter.RoleSplitWindow.AutoHideOutOfCombat = true;
    previousSharedRoleAppearance.Meter.RoleSplitWindow.ShowHeader = false;
    previousSharedRoleAppearance.Meter.RoleSplitWindow.FontScale = 1.35f;
    previousSharedRoleAppearance.Meter.RoleSplitWindow.BackgroundOpacity = 0.37f;
    previousSharedRoleAppearance.Meter.RoleSplitWindow.ItemWidth = 318;
    previousSharedRoleAppearance.Meter.RoleSplitWindow.SortMode = MeterSortMode.Hps;
    Assert(
        previousSharedRoleAppearance.ApplyMigrations() &&
        previousSharedRoleAppearance.Version == 16 &&
        previousSharedRoleAppearance.Meter.RoleSplitDamageWindow.IsEnabled &&
        previousSharedRoleAppearance.Meter.RoleSplitHealerWindow.IsEnabled &&
        previousSharedRoleAppearance.Meter.RoleSplitDamageWindow.IsLocked &&
        previousSharedRoleAppearance.Meter.RoleSplitHealerWindow.IsLocked &&
        previousSharedRoleAppearance.Meter.RoleSplitDamageWindow.ClickThroughWhenLocked &&
        previousSharedRoleAppearance.Meter.RoleSplitHealerWindow.ClickThroughWhenLocked &&
        previousSharedRoleAppearance.Meter.RoleSplitDamageWindow.AutoHideOutOfCombat &&
        previousSharedRoleAppearance.Meter.RoleSplitHealerWindow.AutoHideOutOfCombat &&
        !previousSharedRoleAppearance.Meter.RoleSplitDamageWindow.ShowHeader &&
        !previousSharedRoleAppearance.Meter.RoleSplitHealerWindow.ShowHeader &&
        Math.Abs(previousSharedRoleAppearance.Meter.RoleSplitDamageWindow.FontScale - 1.35f) < 0.0001f &&
        Math.Abs(previousSharedRoleAppearance.Meter.RoleSplitHealerWindow.FontScale - 1.35f) < 0.0001f &&
        Math.Abs(previousSharedRoleAppearance.Meter.RoleSplitDamageWindow.BackgroundOpacity - 0.37f) < 0.0001f &&
        Math.Abs(previousSharedRoleAppearance.Meter.RoleSplitHealerWindow.BackgroundOpacity - 0.37f) < 0.0001f &&
        Math.Abs(previousSharedRoleAppearance.Meter.RoleSplitDamageWindow.ItemWidth - 318) < 0.0001f &&
        Math.Abs(previousSharedRoleAppearance.Meter.RoleSplitHealerWindow.ItemWidth - 318) < 0.0001f &&
        previousSharedRoleAppearance.Meter.RoleSplitDamageWindow.SortMode == MeterSortMode.Hps &&
        previousSharedRoleAppearance.Meter.RoleSplitHealerWindow.SortMode == MeterSortMode.Hps,
        "The v16 migration did not preserve the former shared role appearance in both panes.");
    previousSharedRoleAppearance.Meter.RoleSplitHealerWindow.IsLocked = false;
    previousSharedRoleAppearance.Meter.RoleSplitHealerWindow.ShowHeader = true;
    previousSharedRoleAppearance.Meter.RoleSplitHealerWindow.FontScale = 0.82f;
    previousSharedRoleAppearance.Meter.RoleSplitHealerWindow.BackgroundOpacity = 0.19f;
    previousSharedRoleAppearance.Meter.RoleSplitHealerWindow.Slots.Single(static slot =>
        slot.Metric == MeterSlotMetric.TotalHealing).Visible = false;
    var restoredRoleAppearance = Newtonsoft.Json.JsonConvert.DeserializeObject<PluginConfiguration>(
        Newtonsoft.Json.JsonConvert.SerializeObject(previousSharedRoleAppearance));
    Assert(
        restoredRoleAppearance is not null &&
        restoredRoleAppearance.Version == 16 &&
        restoredRoleAppearance.Meter.RoleSplitDamageWindow.IsLocked &&
        !restoredRoleAppearance.Meter.RoleSplitHealerWindow.IsLocked &&
        !restoredRoleAppearance.Meter.RoleSplitDamageWindow.ShowHeader &&
        restoredRoleAppearance.Meter.RoleSplitHealerWindow.ShowHeader &&
        Math.Abs(restoredRoleAppearance.Meter.RoleSplitDamageWindow.FontScale - 1.35f) < 0.0001f &&
        Math.Abs(restoredRoleAppearance.Meter.RoleSplitHealerWindow.FontScale - 0.82f) < 0.0001f &&
        Math.Abs(restoredRoleAppearance.Meter.RoleSplitDamageWindow.BackgroundOpacity - 0.37f) < 0.0001f &&
        Math.Abs(restoredRoleAppearance.Meter.RoleSplitHealerWindow.BackgroundOpacity - 0.19f) < 0.0001f &&
        restoredRoleAppearance.Meter.RoleSplitDamageWindow.Slots.Single(static slot =>
            slot.Metric == MeterSlotMetric.TotalHealing).Visible &&
        !restoredRoleAppearance.Meter.RoleSplitHealerWindow.Slots.Single(static slot =>
            slot.Metric == MeterSlotMetric.TotalHealing).Visible,
        "D/T and H window appearance settings are still coupled or were not persisted.");
    var customStyle = new MeterCustomStyle
    {
        Name = "Watch layout",
        Slots =
        [
            new MeterSlotDefinition(
                MeterSlotMetric.HighestDamage,
                23,
                5,
                20,
                9,
                MeterSlotAlignment.Right),
        ],
    };
    Assert(
        customStyle.Normalize() &&
        customStyle.Slots[0].ColumnSpan == 1 &&
        customStyle.Slots[0].RowSpan == 1,
        "The horizontal custom-style grid did not snap slot bounds into 24x6.");
    var clonedStyle = customStyle.Clone("Watch layout copy");
    Assert(
        clonedStyle.Id != customStyle.Id &&
        clonedStyle.Slots[0].Id != customStyle.Slots[0].Id &&
        clonedStyle.Slots[0].Metric == MeterSlotMetric.HighestDamage,
        "Duplicating a custom Meter style reused mutable slot identities or lost its data assignment.");
    var restoredCustomStyle = Newtonsoft.Json.JsonConvert.DeserializeObject<MeterCustomStyle>(
                                  Newtonsoft.Json.JsonConvert.SerializeObject(customStyle))
                              ?? throw new InvalidOperationException(
                                  "The custom Meter style could not be restored.");
    var reloadedCustomStyle = Newtonsoft.Json.JsonConvert.DeserializeObject<MeterCustomStyle>(
                                  Newtonsoft.Json.JsonConvert.SerializeObject(restoredCustomStyle))
                              ?? throw new InvalidOperationException(
                                  "The custom Meter style could not be reloaded.");
    // A cold start must replace constructor defaults; merging here grows the slot list
    // on every load and also makes an untouched editor appear modified.
    Assert(
        restoredCustomStyle.Slots.Count == 1 &&
        reloadedCustomStyle.Slots.Count == 1 &&
        reloadedCustomStyle.Slots[0].Metric == MeterSlotMetric.HighestDamage,
        "Custom Meter style slots were appended to defaults during repeated JSON loads.");

    var previousDebugConfiguration = new PluginConfiguration
    {
        Version = 8,
        DebugMode = true,
        EnableFflogsParityRecorder = true,
    };
    Assert(
        previousDebugConfiguration.ApplyMigrations() &&
        previousDebugConfiguration.Version == 16 &&
        previousDebugConfiguration.DebugMode &&
        !previousDebugConfiguration.EnableFflogsParityRecorder,
        "The version-9 migration did not detach ordinary Debug from parity recording.");

    var previousV6Configuration = new PluginConfiguration
    {
        Version = 6,
        DisabledActPluginIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
    };
    Assert(
        previousV6Configuration.ApplyMigrations() &&
        previousV6Configuration.Version == 16 &&
        previousV6Configuration.DisabledActPluginIds.Contains("silverdasher"),
        "The first bundled SilverDasher release did not migrate existing users to the disabled default.");
    previousV6Configuration.DisabledActPluginIds.Remove("silverdasher");
    Assert(
        !previousV6Configuration.ApplyMigrations() &&
        !previousV6Configuration.DisabledActPluginIds.Contains("silverdasher"),
        "A post-migration manual SilverDasher enable choice was overwritten.");
    var manuallyEnabledSilverDasher = new PluginConfiguration();
    manuallyEnabledSilverDasher.DisabledActPluginIds.Remove("silverdasher");
    var coldStartedSilverDasher = Newtonsoft.Json.JsonConvert.DeserializeObject<
                                      PluginConfiguration>(
                                      Newtonsoft.Json.JsonConvert.SerializeObject(
                                          manuallyEnabledSilverDasher))
                                  ?? throw new InvalidOperationException(
                                      "The SilverDasher configuration could not be restored.");
    // This round-trip models Dalamud's next-game cold start, where Json.NET must
    // replace the non-empty default set with the user's saved empty array.
    Assert(
        !coldStartedSilverDasher.ApplyMigrations() &&
        !coldStartedSilverDasher.DisabledActPluginIds.Contains("silverdasher"),
        "A serialized manual SilverDasher enable choice was lost during cold start.");

    var previousGenericPluginUser = new PluginConfiguration
    {
        Version = 7,
        DisabledActPluginIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        ActPluginPermissions = new Dictionary<string, Dictionary<ActCapability, bool>>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["community.plugin"] = new()
            {
                [ActCapability.ReadCombatLogs] = true,
            },
        },
    };
    Assert(
        previousGenericPluginUser.ApplyMigrations() &&
        previousGenericPluginUser.Version == 16 &&
        previousGenericPluginUser.DisabledActPluginIds.Contains("community.plugin") &&
        previousGenericPluginUser.TrustedGenericActPluginIds.Count == 0,
        "A pre-consent generic plugin was allowed to remain active during configuration migration.");

    var previousEdpsUser = new PluginConfiguration
    {
        Version = 3,
        Meter = new MeterSettings { DpsMetric = DpsMetric.EncDps },
    };
    Assert(
        previousEdpsUser.ApplyMigrations() &&
        previousEdpsUser.Version == 16 &&
        previousEdpsUser.Meter.DpsMetric == DpsMetric.Rdps,
        "The one-time eDPS-to-rDPS migration was not applied.");
    previousEdpsUser.Meter.DpsMetric = DpsMetric.ExtDps;
    Assert(
        !previousEdpsUser.ApplyMigrations() &&
        previousEdpsUser.Meter.DpsMetric == DpsMetric.ExtDps,
        "A post-migration manual DPS metric choice was overwritten.");

    var previousCustomMetricUser = new PluginConfiguration
    {
        Version = 3,
        Meter = new MeterSettings { DpsMetric = DpsMetric.Dps },
    };
    Assert(
        previousCustomMetricUser.ApplyMigrations() &&
        previousCustomMetricUser.Version == 16 &&
        previousCustomMetricUser.Meter.DpsMetric == DpsMetric.Dps,
        "The rDPS migration overwrote a previously customized DPS metric.");

    var previousTimelineUser = new PluginConfiguration
    {
        Version = 4,
        SelectedOverlayTemplate = SelfHostedActRuntime.CactbotTimelineOverlayName,
        OverlayWindows = new Dictionary<string, HtmlOverlayWindowSettings>(
            StringComparer.OrdinalIgnoreCase)
        {
            [SelfHostedActRuntime.CactbotOverlayName] = new HtmlOverlayWindowSettings
            {
                Left = 100,
                Top = 120,
            },
            [SelfHostedActRuntime.CactbotTimelineOverlayName] =
                new HtmlOverlayWindowSettings
                {
                    OpenOnStartup = true,
                    Left = 640,
                    Top = 180,
                    Width = 720,
                    Height = 360,
                },
        },
    };
    Assert(
        previousTimelineUser.ApplyMigrations() &&
        previousTimelineUser.Version == 16 &&
        previousTimelineUser.SelectedCactbotOverlay ==
            SelfHostedActRuntime.CactbotTimelineOverlayName &&
        previousTimelineUser.SelectedOverlayTemplate == "Kagerou" &&
        previousTimelineUser.GetOverlayWindowSettings(
            SelfHostedActRuntime.CactbotTimelineOverlayName).OpenOnStartup &&
        previousTimelineUser.GetOverlayWindowSettings(
            SelfHostedActRuntime.CactbotTimelineOverlayName).HasBeenOpened &&
        previousTimelineUser.GetOverlayWindowSettings(
            SelfHostedActRuntime.CactbotTimelineOverlayName).Left == 640 &&
        !previousTimelineUser.GetOverlayWindowSettings(
            SelfHostedActRuntime.CactbotOverlayName).OpenOnStartup,
        "The Cactbot split migration lost an existing Timeline-only layout or reopened the combined window.");

    var previousCombinedTemplateUser = new PluginConfiguration
    {
        Version = 4,
        OverlayWindows = new Dictionary<string, HtmlOverlayWindowSettings>(
            StringComparer.OrdinalIgnoreCase)
        {
            [SelfHostedActRuntime.CactbotCombinedTemplateName] =
                new HtmlOverlayWindowSettings
                {
                    OpenOnStartup = true,
                    Left = 321,
                    Width = 876,
                },
        },
    };
    Assert(
        previousCombinedTemplateUser.ApplyMigrations() &&
        !previousCombinedTemplateUser.OverlayWindows.ContainsKey(
            SelfHostedActRuntime.CactbotCombinedTemplateName) &&
        previousCombinedTemplateUser.GetOverlayWindowSettings(
            SelfHostedActRuntime.CactbotOverlayName).OpenOnStartup &&
        previousCombinedTemplateUser.GetOverlayWindowSettings(
            SelfHostedActRuntime.CactbotOverlayName).HasBeenOpened &&
        previousCombinedTemplateUser.GetOverlayWindowSettings(
            SelfHostedActRuntime.CactbotOverlayName).Left == 321 &&
        previousCombinedTemplateUser.GetOverlayWindowSettings(
            SelfHostedActRuntime.CactbotOverlayName).Width == 876,
        "The legacy combined template was not normalized without losing its saved layout.");

    var previousCustomCactbotPrefixUser = new PluginConfiguration
    {
        Version = 4,
        SelectedOverlayTemplate = "Cactbot Personal",
        OverlayWindows = new Dictionary<string, HtmlOverlayWindowSettings>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["Cactbot Personal"] = new HtmlOverlayWindowSettings
            {
                OpenOnStartup = true,
                SourceUrl = "https://example.com/personal-overlay",
                Left = 456,
            },
        },
    };
    Assert(
        previousCustomCactbotPrefixUser.ApplyMigrations() &&
        previousCustomCactbotPrefixUser.SelectedOverlayTemplate == "Cactbot Personal" &&
        previousCustomCactbotPrefixUser.GetOverlayWindowSettings(
            "Cactbot Personal").OpenOnStartup &&
        previousCustomCactbotPrefixUser.GetOverlayWindowSettings(
            "Cactbot Personal").Left == 456,
        "The Cactbot split migration swallowed an existing custom overlay that only shared the prefix.");

    var previousV5CactbotUser = new PluginConfiguration
    {
        Version = 5,
        SelectedCactbotOverlay = SelfHostedActRuntime.CactbotOverlayName,
        OverlayWindows = new Dictionary<string, HtmlOverlayWindowSettings>(
            StringComparer.OrdinalIgnoreCase)
        {
            [SelfHostedActRuntime.CactbotOverlayName] = new HtmlOverlayWindowSettings
            {
                OpenOnStartup = true,
            },
            [SelfHostedActRuntime.CactbotAlertsOverlayName] = new HtmlOverlayWindowSettings(),
            [SelfHostedActRuntime.CactbotTimelineOverlayName] = new HtmlOverlayWindowSettings(),
            ["Cactbot DPS Xephero"] = new HtmlOverlayWindowSettings(),
            ["Cactbot Personal"] = new HtmlOverlayWindowSettings
            {
                OpenOnStartup = true,
            },
        },
    };
    Assert(
        previousV5CactbotUser.ApplyMigrations() &&
        previousV5CactbotUser.Version == 16 &&
        previousV5CactbotUser.GetOverlayWindowSettings(
            SelfHostedActRuntime.CactbotOverlayName).HasBeenOpened &&
        !previousV5CactbotUser.GetOverlayWindowSettings(
            SelfHostedActRuntime.CactbotAlertsOverlayName).HasBeenOpened &&
        !previousV5CactbotUser.GetOverlayWindowSettings(
            SelfHostedActRuntime.CactbotTimelineOverlayName).HasBeenOpened &&
        !previousV5CactbotUser.GetOverlayWindowSettings(
            "Cactbot DPS Xephero").HasBeenOpened &&
        !previousV5CactbotUser.GetOverlayWindowSettings(
            "Cactbot Personal").HasBeenOpened,
        "The Cactbot usage migration listed a selection/conflict ghost or captured a custom prefix overlay.");
    var selectedOnlyV5CactbotUser = new PluginConfiguration
    {
        Version = 5,
        SelectedCactbotOverlay = SelfHostedActRuntime.CactbotTimelineOverlayName,
        OverlayWindows = new Dictionary<string, HtmlOverlayWindowSettings>(
            StringComparer.OrdinalIgnoreCase)
        {
            [SelfHostedActRuntime.CactbotTimelineOverlayName] =
                new HtmlOverlayWindowSettings(),
            ["Cactbot DPS Rdmty"] = new HtmlOverlayWindowSettings
            {
                Left = 240,
            },
        },
    };
    Assert(
        selectedOnlyV5CactbotUser.ApplyMigrations() &&
        !selectedOnlyV5CactbotUser.GetOverlayWindowSettings(
            SelfHostedActRuntime.CactbotTimelineOverlayName).HasBeenOpened &&
        selectedOnlyV5CactbotUser.GetOverlayWindowSettings(
            "Cactbot DPS Rdmty").HasBeenOpened,
        "A selected-only Cactbot template became false history or a customized closed layout was lost.");

    var start = DateTimeOffset.UtcNow.AddSeconds(-10);
    var encounter = new Encounter(
        Guid.NewGuid(),
        start,
        start.AddSeconds(10),
        "Test Zone",
        "Test Enemy",
        [
            new Combatant(
                "tank", "Tank@Alpha", "PLD", true, 100_000, 10_000, 1,
                11_000, 10_000, 10_000,
                DamageHits: 20, CriticalHits: 5, CriticalDirectHits: 2, Rdps: 9_500,
                DirectHits: 8),
            new Combatant(
                "healer", "Healer@Beta", "WHM", false, 20_000, 200_000, 3,
                2_500, 2_000, 2_000, Rdps: 11_000),
            new Combatant("Limit Break", "Limit Break", "", false, 50_000, 0, 0, 5_000, 5_000, 5_000),
        ],
        [],
        [],
        [],
        [],
        [])
    {
        // Damage time excludes target downtime; HPS must still use the full ten-second pull.
        CombatDuration = TimeSpan.FromSeconds(5),
    };
    var state = new EncounterStateStore();
    state.Replace(encounter, []);
    var settings = new MeterSettings
    {
        RefreshIntervalMs = 250,
        DpsMetric = DpsMetric.EncDps,
        SortMode = MeterSortMode.Dps,
    };
    var meter = new MeterService(state, settings);
    var futureStart = DateTimeOffset.UtcNow.AddSeconds(10);
    Assert(
        (encounter with { StartTime = futureStart, EndTime = null }).Duration == TimeSpan.Zero &&
        (encounter with { StartTime = futureStart, EndTime = futureStart.AddSeconds(-1) }).Duration ==
        TimeSpan.Zero,
        "Encounter duration exposed a negative countdown during clock or log timestamp skew.");
    var rows = meter.GetRows();
    Assert(
        rows.Count == 3 &&
        rows.Any(static row => row.Name == "Limit Break" && row.TotalDamage == 50_000),
        "Limit Break damage is missing from the Meter rows.");
    Assert(
        rows[^1].Name == "Limit Break" &&
        rows[^1].Rank is null &&
        rows.Take(rows.Count - 1).Select(static row => row.Rank).SequenceEqual([1, 2]),
        "Limit Break participated in Meter ranking or was not pinned to the final row.");
    var historyRows = EncounterWindow.OrderCombatantsForDisplay(
        encounter,
        encounter.EffectiveDuration.TotalSeconds,
        settings.SortMode,
        settings.DpsMetric);
    Assert(
        historyRows[^1].Name == "Limit Break",
        "Limit Break was not pinned to the final row in encounter history.");
    Assert(
        MeterWindow.LimitBreakDisplayName == "LB (Limit Break)",
        "The Limit Break row does not show the requested player-column label.");
    Assert(
        encounter.Combatants.Any(static combatant => combatant.Name == "Limit Break"),
        "Hiding Limit Break from the Meter mutated the underlying encounter data.");
    Assert(rows[0].Name == "Tank@Alpha", "DPS sorting did not preserve the player server name.");
    Assert(rows[0].Job == "PLD", "The resolved player job was not preserved.");
    Assert(rows[0].Deaths == 1, "The resolved player death count was not preserved.");
    Assert(rows[0].Dps == 10_000, "EncDPS did not use the ACT encounter-duration field.");
    Assert(
        rows[0].CriticalHitPercent == 25 &&
        rows[0].DirectHitPercent == 40 &&
        rows[0].CriticalDirectHitPercent == 10,
        "The ACT Meter did not calculate critical, direct, and critical-direct rates from hit counts.");
    Assert(
        Math.Abs(rows.Sum(row => row.DamagePercent) - 100) < 0.01,
        "Meter damage percentages did not cover the encounter total.");

    var transientZeroHitCombatants = encounter.Combatants
        .Select(static combatant => combatant.Id == "tank"
            ? combatant with
            {
                DamageHits = 0,
                CriticalHits = 0,
                DirectHits = 0,
                CriticalDirectHits = 0,
            }
            : combatant)
        .ToArray();
    state.UpdateCurrent(encounter with { Combatants = transientZeroHitCombatants });
    Thread.Sleep(settings.RefreshIntervalMs + 20);
    rows = meter.GetRows();
    var tankWithTransientZeroHits = rows.Single(static row => row.Id == "tank");
    Assert(
        tankWithTransientZeroHits.CriticalHitPercent == 25 &&
        tankWithTransientZeroHits.DirectHitPercent == 40 &&
        tankWithTransientZeroHits.CriticalDirectHitPercent == 10,
        "A transient zero-hit ACT snapshot inserted '--' between valid Meter percentages.");

    state.UpdateCurrent(encounter with
    {
        Id = Guid.NewGuid(),
        Combatants = transientZeroHitCombatants,
    });
    rows = meter.GetRows();
    var tankInNewEncounter = rows.Single(static row => row.Id == "tank");
    Assert(
        tankInNewEncounter.CriticalHitPercent is null &&
        tankInNewEncounter.DirectHitPercent is null &&
        tankInNewEncounter.CriticalDirectHitPercent is null,
        "The Meter carried a cached hit rate into a different encounter.");

    state.UpdateCurrent(encounter);

    settings.DpsMetric = DpsMetric.Rdps;
    Thread.Sleep(settings.RefreshIntervalMs + 20);
    rows = meter.GetRows();
    Assert(
        rows[0].Name == "Tank@Alpha" &&
        rows[0].PersonalDps == 11_000 && rows[0].Rdps == 9_500 &&
        rows[1].Name == "Healer@Beta" &&
        rows[1].PersonalDps == 2_500 && rows[1].Rdps == 11_000,
        "The DPS ranking did not remain personal-DPS ordered or independent rDPS values were lost.");

    settings.SortMode = MeterSortMode.Hps;
    Thread.Sleep(settings.RefreshIntervalMs + 20);
    rows = meter.GetRows();
    Assert(rows[0].Name == "Healer@Beta", "HPS sorting did not promote the highest-healing player.");
    Assert(
        encounter.EffectiveDuration == TimeSpan.FromSeconds(5) &&
        encounter.Duration == TimeSpan.FromSeconds(10) &&
        rows[0].Hps == 20_000,
        "HPS reused the phase-adjusted damage duration instead of the full pull duration.");
    Assert(
        rows[^1].Name == "Limit Break" && rows[^1].Rank is null,
        "Limit Break participated in HPS ranking or was not pinned to the final row.");
    var compactRows = MeterWindow.SelectVisibleRows(rows, compactMode: true);
    Assert(
        compactRows.Count == 1 &&
        compactRows[0].IsLocalPlayer &&
        compactRows[0].Rank == 2,
        "Compact mode did not preserve the local player's full-party rank.");
    Assert(
        MeterWindow.SelectVisibleRows(rows, compactMode: false).Count == rows.Count,
        "Disabling compact mode did not restore the full Meter list.");

    settings.DpsMetric = DpsMetric.EncDps;
    settings.SortMode = MeterSortMode.Deaths;
    Thread.Sleep(settings.RefreshIntervalMs + 20);
    rows = meter.GetRows();
    Assert(
        rows[0].Name == "Tank@Alpha",
        "Legacy death sorting did not safely fall back to DPS.");
    Assert(
        MeterSortModeOptions.Supported.SequenceEqual(
            [MeterSortMode.Dps, MeterSortMode.Hps]),
        "Meter exposed a sort mode other than DPS or HPS.");
    Assert(
        JobDisplayFormatter.FormatText("WHM", JobDisplayStyle.Abbreviation) == "WHM" &&
        JobDisplayFormatter.FormatText("WHM", JobDisplayStyle.ChineseAbbreviation) == "白魔" &&
        JobDisplayFormatter.SupportedJobCodes.Count == 32 &&
        JobDisplayFormatter.SupportedJobCodes.Distinct(StringComparer.Ordinal).Count() == 32 &&
        JobDisplayFormatter.UsesIcon(JobDisplayStyle.MinimalIcon) &&
        JobDisplayFormatter.UsesIcon(JobDisplayStyle.ClassicIcon) &&
        JobDisplayFormatter.UsesIcon(JobDisplayStyle.FlatIcon),
        "The five job display styles were not preserved.");

    var configuration = new PluginConfiguration
    {
        UiLanguage = "zh-CN",
    };
    configuration.Meter.CompactMode = true;
    configuration.Meter.ExpandedWindowWidth = 612;
    configuration.Meter.ExpandedWindowHeight = 284;
    var restoredConfiguration = Newtonsoft.Json.JsonConvert.DeserializeObject<PluginConfiguration>(
                                    Newtonsoft.Json.JsonConvert.SerializeObject(configuration))
                                ?? throw new InvalidOperationException(
                                    "Compact Meter configuration could not be restored.");
    Assert(
        restoredConfiguration.Meter.CompactMode &&
        restoredConfiguration.Meter.ExpandedWindowWidth == 612 &&
        restoredConfiguration.Meter.ExpandedWindowHeight == 284,
        "The compact Meter mode or its expanded window size was not persisted.");
    var text = new UiText(configuration);
    settings.PlayerIdentityMode = PlayerIdentityMode.Job;
    Assert(
        PlayerIdentityFormatter.Format(encounter.Combatants[0], encounter.Combatants, settings, text) == "骑士",
        "Job identity mode did not replace the player ID with the localized job.");
    settings.PlayerIdentityMode = PlayerIdentityMode.Anonymous;
    settings.LocalPlayerAlias = "我";
    Assert(
        PlayerIdentityFormatter.Format(encounter.Combatants[0], encounter.Combatants, settings, text) == "我" &&
        PlayerIdentityFormatter.Format(encounter.Combatants[1], encounter.Combatants, settings, text) == "玩家 1",
        "Anonymous identity mode did not produce stable local and party aliases.");
    Assert(
        encounter.Combatants[0].Name == "Tank@Alpha",
        "Display-only player ID masking mutated the encounter data.");

    var emptyCompletion = encounter with
    {
        EndTime = encounter.EndTime?.AddSeconds(1),
        Combatants = [],
    };
    state.UpdateCurrent(emptyCompletion);
    Assert(
        state.GetDisplayEncounter()?.Id == encounter.Id &&
        state.GetDisplayEncounter()?.TotalDamage == encounter.TotalDamage,
        "An empty combat completion replaced the latest displayable Meter encounter.");
    state.UpdateCurrent(null);
    Assert(
        state.GetDisplayEncounter()?.TotalDamage == encounter.TotalDamage,
        "Clearing a transient current snapshot discarded the latest displayable Meter encounter.");

    var activeEncounter = encounter with
    {
        Id = Guid.NewGuid(),
        EndTime = null,
    };
    state.UpdateCurrent(activeEncounter);
    state.UpdateCurrent(null);
    Assert(
        state.GetDisplayEncounter() is { IsActive: false } retainedEncounter &&
        retainedEncounter.Id == activeEncounter.Id &&
        retainedEncounter.TotalDamage == activeEncounter.TotalDamage,
        "A cleared live snapshot did not retain a completed Meter encounter.");
    state.ResetCurrent();
    Assert(
        state.GetDisplayEncounter() is null,
        "An explicit Meter reset did not clear the retained encounter.");
}

static void ValidateMeterLayout()
{
    Assert(
        MeterWindow.CombatantRowSpacing == 3,
        "Combat Meter player rows are not separated by the requested three pixels.");
    Assert(
        File.Exists(Path.Combine(
            FindProjectRoot(),
            "src",
            "DalamudActCompat",
            "Assets",
            "JobIcons",
            "LimitBreak.png")),
        "The supplied Limit Break icon is missing from plugin assets.");
    Assert(
        File.Exists(Path.Combine(
            FindProjectRoot(),
            "src",
            "DalamudActCompat",
            "Assets",
            "StatusIcons",
            "CombatRunning.png")) &&
        File.Exists(Path.Combine(
            FindProjectRoot(),
            "src",
            "DalamudActCompat",
            "Assets",
            "StatusIcons",
            "CombatTransition.png")) &&
        File.Exists(Path.Combine(
            FindProjectRoot(),
            "src",
            "DalamudActCompat",
            "Assets",
            "StatusIcons",
            "CombatEnded.png")),
        "The requested running, transition, or ended Combat Meter status icon is missing.");
    Assert(
        new MeterSettings().ClassicWindow.ItemWidth == 150 &&
        !new MeterSettings().ClassicAllianceView,
        "The classic Meter no longer starts in editable 8-player mode.");
    var defaultColumns = new MeterSettings();
    var headerStart = new DateTimeOffset(2026, 8, 25, 20, 0, 0, TimeSpan.FromHours(8));
    var headerEncounter = new Encounter(
        Guid.NewGuid(),
        headerStart,
        headerStart.AddMinutes(2),
        "Training Dummy",
        "Training Dummy",
        [],
        [],
        [],
        [],
        [],
        [])
    {
        CombatDuration = TimeSpan.FromSeconds(1),
    };
    Assert(
        MeterWindow.ResolveHeaderDuration(headerEncounter) == TimeSpan.FromMinutes(2),
        "The Combat Meter header reused the one-second damage denominator " +
        "instead of the fight duration.");
    Assert(
        Math.Abs(MeterWindow.ResolveStableColumnWidth(28, 1, 28, 8) - 34) < 0.0001f,
        "A Meter column did not reserve its six-pixel header padding before truncating the label.");
    var clickThroughSettings = new MeterSettings
    {
        IsLocked = true,
        ClickThroughWhenLocked = true,
    };
    var clickThroughRows = MeterWindow.BuildRowsChildFlags(
        clickThroughSettings,
        useHorizontalScroll: false);
    var clickThroughScrollableRows = MeterWindow.BuildRowsChildFlags(
        clickThroughSettings,
        useHorizontalScroll: true);
    clickThroughSettings.IsLocked = false;
    var unlockedRows = MeterWindow.BuildRowsChildFlags(
        clickThroughSettings,
        useHorizontalScroll: false);
    Assert(
        (clickThroughRows & MeterWindow.NoInputsFlagMask) != 0 &&
        (clickThroughScrollableRows & MeterWindow.NoInputsFlagMask) != 0 &&
        (clickThroughScrollableRows & MeterWindow.HorizontalScrollbarFlagMask) != 0 &&
        (unlockedRows & MeterWindow.NoInputsFlagMask) == 0,
        "The expanded Combat Meter child window does not follow the locked click-through setting.");
    var summaryVisibilitySettings = new MeterSettings();
    Assert(
        MeterWindow.ShouldDrawTeamSummary(summaryVisibilitySettings),
        "The expanded classic Meter unexpectedly hid its team summary.");
    summaryVisibilitySettings.CompactMode = true;
    Assert(
        !MeterWindow.ShouldDrawTeamSummary(summaryVisibilitySettings),
        "The collapsed classic Meter still rendered its team summary.");
    summaryVisibilitySettings.CompactMode = false;
    summaryVisibilitySettings.ClassicAllianceView = true;
    Assert(
        !MeterWindow.ShouldDrawTeamSummary(summaryVisibilitySettings),
        "The fixed 24-player classic layout unexpectedly rendered the editable team summary.");
    Assert(
        MeterWindow.ResolveIdentityColumnWidth(600, 600, 240, 90) == 240 &&
        MeterWindow.ResolveIdentityColumnWidth(500, 600, 240, 90) == 140 &&
        MeterWindow.ResolveIdentityColumnWidth(450, 600, 240, 90) == 90 &&
        MeterWindow.ResolveIdentityColumnWidth(700, 600, 240, 90) == 340 &&
        !MeterWindow.ShouldEnableHorizontalScroll(450, 450) &&
        MeterWindow.ShouldEnableHorizontalScroll(448, 450),
        "The identity column did not shrink to its minimum before enabling horizontal scrolling.");
    var adaptiveWideColumns = MeterWindow.ResolveAdaptiveColumnWidths(
        800,
        600,
        240,
        90,
        104);
    var adaptiveNarrowColumns = MeterWindow.ResolveAdaptiveColumnWidths(
        500,
        600,
        240,
        90,
        104);
    Assert(
        adaptiveWideColumns.Identity == 320 &&
        adaptiveWideColumns.HighestDamage == 224 &&
        adaptiveNarrowColumns.Identity == 140 &&
        adaptiveNarrowColumns.HighestDamage == 104 &&
        MeterWindow.ResolveColumnTextOffset(104, 40, MeterSlotAlignment.Center) == 32 &&
        MeterWindow.ResolveColumnTextOffset(104, 40, MeterSlotAlignment.Right) == 64,
        "A wide Meter did not share spare width with the highest-damage column or changed narrow-window compression.");
    var defaultClassicSlots = defaultColumns.ClassicWindow.Slots;
    Assert(
        defaultClassicSlots.Any(static slot =>
            slot.Visible && slot.Metric == MeterSlotMetric.PlayerIdentity) &&
        defaultClassicSlots.Any(static slot =>
            slot.Visible && slot.Metric == MeterSlotMetric.Dps) &&
        defaultClassicSlots.Any(static slot =>
            slot.Visible && slot.Metric == MeterSlotMetric.TotalDamage) &&
        defaultClassicSlots.Any(static slot =>
            slot.Visible && slot.Metric == MeterSlotMetric.HighestDamageAction) &&
        defaultClassicSlots.Any(static slot =>
            !slot.Visible && slot.Metric == MeterSlotMetric.Fflogs) &&
        defaultClassicSlots.Any(static slot =>
            !slot.Visible && slot.Metric == MeterSlotMetric.EncDps) &&
        defaultClassicSlots.Any(static slot =>
            !slot.Visible && slot.Metric == MeterSlotMetric.ExtDps) &&
        defaultClassicSlots.All(static slot =>
            slot.Metric is not MeterSlotMetric.Job and not MeterSlotMetric.PlayerName),
        "The classic defaults lost composite identity, independent rate metrics, team totals, max hit, or the optional FFLogs slot.");
    var independentRateRow = new CombatantRow(
        "rate-row",
        "Rate Row",
        "SAM",
        false,
        1_000,
        0,
        180_000,
        0,
        100,
        null,
        null,
        null,
        0,
        PersonalDps: 1_000,
        Rdps: 1_100,
        EncDps: 900,
        ExtDps: 850);
    Assert(
        MeterSlotPresentation.Value(MeterSlotMetric.Dps, independentRateRow, "Rate Row") == "1,000" &&
        MeterSlotPresentation.Value(MeterSlotMetric.Rdps, independentRateRow, "Rate Row") == "1,100" &&
        MeterSlotPresentation.Value(MeterSlotMetric.EncDps, independentRateRow, "Rate Row") == "900" &&
        MeterSlotPresentation.Value(MeterSlotMetric.ExtDps, independentRateRow, "Rate Row") == "850",
        "Independent DPS-rate slots no longer render their own values.");
    var inverseRateRow = independentRateRow with
    {
        Id = "inverse-rate-row",
        Name = "Inverse Rate Row",
        PersonalDps = 1_200,
        Rdps = 800,
        EncDps = 1_300,
        ExtDps = 1_400,
    };
    Assert(
        MeterSlotPresentation.SortAndRank(
            [independentRateRow, inverseRateRow],
            MeterSortMode.Dps,
            DpsMetric.Dps)[0].Id == inverseRateRow.Id &&
        MeterSlotPresentation.SortAndRank(
            [independentRateRow, inverseRateRow],
            MeterSortMode.Dps,
            DpsMetric.Rdps)[0].Id == independentRateRow.Id &&
        MeterSlotPresentation.SortAndRank(
            [independentRateRow, inverseRateRow],
            MeterSortMode.Dps,
            DpsMetric.EncDps)[0].Id == inverseRateRow.Id &&
        MeterSlotPresentation.SortAndRank(
            [independentRateRow, inverseRateRow],
            MeterSortMode.Dps,
            DpsMetric.ExtDps)[0].Id == inverseRateRow.Id,
        "A window did not rank rows with its selected DPS, rDPS, EncDPS, or ExtDPS metric.");
    var existingColumnChoices = Newtonsoft.Json.JsonConvert.DeserializeObject<MeterSettings>(
        "{\"ShowHps\":true,\"ShowDirectHitRate\":true}")!;
    Assert(
        existingColumnChoices.ShowHps && existingColumnChoices.ShowDirectHitRate,
        "The new defaults overwrite explicit HPS or DH choices from an existing configuration.");
    Assert(
        MeterWindow.ShouldShowFflogsColumn(integrationEnabled: true, defaultColumns) &&
        !MeterWindow.ShouldShowFflogsColumn(integrationEnabled: false, defaultColumns),
        "The FFLogs column is not gated by the integration state.");
    defaultColumns.ShowFflogs = false;
    Assert(
        !MeterWindow.ShouldShowFflogsColumn(integrationEnabled: true, defaultColumns),
        "The FFLogs column ignores the user's visibility choice.");
    defaultColumns.ShowFflogs = true;
    defaultColumns.ShowHps = true;
    defaultColumns.ShowDirectHitRate = true;
    defaultColumns.ShowFflogs = false;
    defaultColumns.ShowDps = false;
    defaultColumns.ShowHps = false;
    defaultColumns.ShowCriticalHitRate = false;
    defaultColumns.ShowDirectHitRate = false;
    defaultColumns.ShowCriticalDirectHitRate = false;
    defaultColumns.ShowDamagePercent = false;
    defaultColumns.ShowTotalDamage = false;
    defaultColumns.ShowHighestDamage = false;
    defaultColumns.ShowDeaths = false;
    var restoredColumns = Newtonsoft.Json.JsonConvert.DeserializeObject<MeterSettings>(
        Newtonsoft.Json.JsonConvert.SerializeObject(defaultColumns));
    Assert(
        restoredColumns is not null &&
        !restoredColumns.ShowFflogs &&
        !restoredColumns.ShowDps &&
        !restoredColumns.ShowRdps &&
        !restoredColumns.ShowHps &&
        !restoredColumns.ShowCriticalHitRate &&
        !restoredColumns.ShowDirectHitRate &&
        !restoredColumns.ShowCriticalDirectHitRate &&
        !restoredColumns.ShowDamagePercent &&
        !restoredColumns.ShowTotalDamage &&
        !restoredColumns.ShowTotalHealing &&
        !restoredColumns.ShowHighestDamage &&
        !restoredColumns.ShowDeaths,
        "Customized Meter column visibility does not survive configuration persistence.");
    var opaqueBackground = new System.Numerics.Vector4(0.1f, 0.2f, 0.3f, 0.8f);
    var transparentBackground = MeterWindow.ApplyBackgroundOpacity(opaqueBackground, 0);
    var halfBackground = MeterWindow.ApplyBackgroundOpacity(opaqueBackground, 0.5f);
    Assert(
        MeterWindow.NormalizeBackgroundOpacity(-1) == 0 &&
        MeterWindow.NormalizeBackgroundOpacity(0) == 0 &&
        MeterWindow.NormalizeBackgroundOpacity(2) == 1 &&
        Math.Abs(MeterWindow.NormalizeBackgroundOpacity(float.NaN) - 0.85f) < 0.0001f &&
        transparentBackground.X == opaqueBackground.X &&
        transparentBackground.Y == opaqueBackground.Y &&
        transparentBackground.Z == opaqueBackground.Z &&
        transparentBackground.W == 0 &&
        Math.Abs(halfBackground.W - 0.4f) < 0.0001f,
        "Combat Meter background opacity zero is not fully transparent or alpha scaling is inconsistent.");

    var rowHeightMethod = typeof(MeterWindow).GetMethod(
                              "CalculateCombatantRowHeight",
                              BindingFlags.Static | BindingFlags.NonPublic)
                          ?? throw new InvalidOperationException(
                              "Meter row-height helper was not found.");
    var rowHeight = (float)(rowHeightMethod.Invoke(null, [19.0f])
                            ?? throw new InvalidOperationException(
                                "Combat Meter returned no row height."));
    Assert(
        rowHeight <= 30,
        "Combat Meter did not keep each player on one compact line.");
    var compactHeight = MeterWindow.CalculateCompactWindowHeight(
        showHeader: true,
        textLineHeightWithSpacing: 19,
        windowPaddingY: 9,
        itemSpacingY: MeterWindow.CombatantRowSpacing,
        scrollbarSize: 14,
        useHorizontalScroll: false);
    var compactHeightWithScroll = MeterWindow.CalculateCompactWindowHeight(
        showHeader: true,
        textLineHeightWithSpacing: 19,
        windowPaddingY: 9,
        itemSpacingY: MeterWindow.CombatantRowSpacing,
        scrollbarSize: 14,
        useHorizontalScroll: true);
    var compactHeightWithoutHeader = MeterWindow.CalculateCompactWindowHeight(
        showHeader: false,
        textLineHeightWithSpacing: 19,
        windowPaddingY: 9,
        itemSpacingY: MeterWindow.CombatantRowSpacing,
        scrollbarSize: 14,
        useHorizontalScroll: false);
    Assert(
        compactHeight == 121 &&
        compactHeightWithScroll == compactHeight + 14 &&
        compactHeightWithoutHeader == compactHeight - 18,
        "Compact mode did not size the Meter to exactly one header and one player row.");
    Assert(
        MeterWindow.CalculateEmptyStateWindowHeight(windowPaddingY: 9) == 62,
        "The empty Meter state did not retain the compact toggle at a compact height.");
    Assert(
        MeterWindow.EaseOutCubic(0) == 0 &&
        Math.Abs(MeterWindow.EaseOutCubic(0.5f) - 0.875f) < 0.0001f &&
        MeterWindow.EaseOutCubic(1) == 1,
        "The Meter collapse and expand transition did not use the expected smooth easing.");

    var invalidExpandedSize = new MeterSettings
    {
        ExpandedWindowWidth = float.NaN,
        ExpandedWindowHeight = 100,
    };
    Assert(
        MeterWindow.NormalizeExpandedWindowSize(invalidExpandedSize) ==
        new System.Numerics.Vector2(
            MeterWindow.DefaultExpandedWindowWidth,
            MeterWindow.DefaultExpandedWindowHeight),
        "Invalid saved Meter dimensions did not fall back to the established expanded size.");

    var iconSizeMethod = typeof(MeterWindow).GetMethod(
                             "CalculateJobIconSize",
                             BindingFlags.Static | BindingFlags.NonPublic)
                         ?? throw new InvalidOperationException(
                             "Meter job-icon sizing helper was not found.");
    var iconSize = (float)(iconSizeMethod.Invoke(null, [rowHeight, 17.0f])
                           ?? throw new InvalidOperationException(
                               "Meter returned no job-icon size."));
    Assert(
        iconSize is >= 20 and <= 24 && iconSize <= rowHeight - 4,
        "Job icons were not normalized to the one-line Meter slot.");

    var rateLabelMethod = typeof(MeterWindow).GetMethod(
                              "PrimaryRateLabel",
                              BindingFlags.Static | BindingFlags.NonPublic)
                          ?? throw new InvalidOperationException(
                              "Meter primary-rate label helper was not found.");
    var settings = new MeterSettings();
    Assert(
        settings.DpsMetric == DpsMetric.Rdps &&
        Equals(rateLabelMethod.Invoke(null, [MeterSortMode.Dps, settings]), "rDPS"),
        "The ACT Meter does not default to the rDPS calculation metric.");
    settings.DpsMetric = DpsMetric.EncDps;
    Assert(
        Equals(rateLabelMethod.Invoke(null, [MeterSortMode.Dps, settings]), "eDPS"),
        "The ACT Meter still labels encounter DPS as EncDPS instead of eDPS.");

    var rateColorMethod = typeof(MeterWindow).GetMethod(
                              "PrimaryRateColor",
                              BindingFlags.Static | BindingFlags.NonPublic)
                          ?? throw new InvalidOperationException(
                              "Meter primary-rate color helper was not found.");
    var ownRateColor = (System.Numerics.Vector4)rateColorMethod.Invoke(null, [true])!;
    var otherRateColor = (System.Numerics.Vector4)rateColorMethod.Invoke(null, [false])!;
    Assert(
        ownRateColor.X + ownRateColor.Y + ownRateColor.Z >
        otherRateColor.X + otherRateColor.Y + otherRateColor.Z,
        "The local player's DPS/HPS value is not brighter than party values.");

    var hitRateTextMethod = typeof(MeterWindow).GetMethod(
                                "FormatHitRateValue",
                                BindingFlags.Static | BindingFlags.NonPublic)
                            ?? throw new InvalidOperationException(
                                "Meter hit-rate formatter was not found.");
    Assert(
        Equals(hitRateTextMethod.Invoke(null, [25.0]), "25.0%") &&
        Equals(hitRateTextMethod.Invoke(null, [null]), "--"),
        "The Meter does not keep critical-rate values compact below their column headers.");

    var localizerType = typeof(MeterWindow).Assembly.GetType(
                            "DalamudActCompat.Meter.ZoneNameLocalizer")
                        ?? throw new InvalidOperationException(
                            "Meter zone-name localizer was not found.");
    var resolveZoneName = localizerType.GetMethod(
                              "Resolve",
                              BindingFlags.Static | BindingFlags.NonPublic)
                          ?? throw new InvalidOperationException(
                              "Meter zone-name resolver was not found.");
    var zoneNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Middle La Noscea"] = "中拉诺西亚",
    };
    Assert(
        Equals(resolveZoneName.Invoke(null, ["Middle La Noscea", zoneNames]), "中拉诺西亚") &&
        Equals(resolveZoneName.Invoke(null, ["Unknown Zone", zoneNames]), "Unknown Zone"),
        "Meter zone names no longer localize with a safe ACT-name fallback.");
    var resolveTerritoryName = localizerType.GetMethod(
                                   "ResolveByTerritory",
                                   BindingFlags.Static | BindingFlags.NonPublic)
                               ?? throw new InvalidOperationException(
                                   "Meter territory-name resolver was not found.");
    var territoryNames = new Dictionary<uint, string>
    {
        [1327] = "轻量级重型斗技场（零式）",
    };
    Assert(
        Equals(resolveTerritoryName.Invoke(
            null,
            [1327u, "AAC Heavyweight M4 (Savage)", territoryNames, zoneNames]),
            "轻量级重型斗技场（零式）"),
        "A known Territory ID did not take priority over ACT's English zone name.");
}

static void ValidateIndependentMeterWindows()
{
    var projectRoot = FindProjectRoot();
    var classicSource = File.ReadAllText(Path.Combine(
        projectRoot,
        "src",
        "DalamudActCompat",
        "Meter",
        "MeterWindow.cs"));
    var horizontalSource = File.ReadAllText(Path.Combine(
        projectRoot,
        "src",
        "DalamudActCompat",
        "Meter",
        "HorizontalMeterWindow.cs"));
    var roleSource = File.ReadAllText(Path.Combine(
        projectRoot,
        "src",
        "DalamudActCompat",
        "Meter",
        "RoleSplitMeterWindow.cs"));
    var editorSource = File.ReadAllText(Path.Combine(
        projectRoot,
        "src",
        "DalamudActCompat",
        "Meter",
        "MeterStyleEditorWindow.cs"));
    var meterSettingsSource = File.ReadAllText(Path.Combine(
        projectRoot,
        "src",
        "DalamudActCompat",
        "Meter",
        "MeterSettings.cs"));
    var previewInteractionSource = File.ReadAllText(Path.Combine(
        projectRoot,
        "src",
        "DalamudActCompat",
        "Meter",
        "MeterPreviewInteraction.cs"));
    var simplifiedSource = File.ReadAllText(Path.Combine(
        projectRoot,
        "src",
        "DalamudActCompat",
        "UI",
        "SimplifiedHomeWindow.cs"));
    var controlCenterSource = File.ReadAllText(Path.Combine(
        projectRoot,
        "src",
        "DalamudActCompat",
        "UI",
        "ControlCenterWindow.cs"));
    var settingsSource = File.ReadAllText(Path.Combine(
        projectRoot,
        "src",
        "DalamudActCompat",
        "UI",
        "SettingsWindow.cs"));

    Assert(
        editorSource.Contains("设为 DPS 排序依据", StringComparison.Ordinal) &&
        editorSource.Contains("profile.DpsSortMetric = dpsMetric", StringComparison.Ordinal),
        "The selected DPS slot has no right-side action for choosing the ranking metric.");

    Assert(
        typeof(HorizontalMeterWindow).IsSubclassOf(typeof(Dalamud.Interface.Windowing.Window)) &&
        typeof(RoleSplitMeterWindow).IsSubclassOf(typeof(Dalamud.Interface.Windowing.Window)) &&
        typeof(MeterWindow).IsSubclassOf(typeof(Dalamud.Interface.Windowing.Window)) &&
        horizontalSource.Contains("ImGui.SetNextWindowBgAlpha(0)", StringComparison.Ordinal) &&
        horizontalSource.Contains("ImGuiCol.WindowBg, Vector4.Zero", StringComparison.Ordinal) &&
        horizontalSource.Contains("ImGuiCol.ChildBg, Vector4.Zero", StringComparison.Ordinal) &&
        !horizontalSource.Contains("AddRectFilled", StringComparison.Ordinal) &&
        horizontalSource.Contains("scrollOffset", StringComparison.Ordinal) &&
        horizontalSource.Contains("viewport.Size.Y / 3", StringComparison.Ordinal) &&
        horizontalSource.Contains("Flags |= ImGuiWindowFlags.NoResize", StringComparison.Ordinal) &&
        horizontalSource.Contains("MeterSlotPresentation.SelectParty", StringComparison.Ordinal) &&
        horizontalSource.Contains("horizontal-party-", StringComparison.Ordinal),
        "The horizontal Meter is not a transparent eight-player slider with an alliance party selector.");
    Assert(
        classicSource.Contains("DrawClassicTable", StringComparison.Ordinal) &&
        classicSource.Contains("DrawAllianceCompactTiles", StringComparison.Ordinal) &&
        classicSource.Contains("ranked.Take(24)", StringComparison.Ordinal) &&
        classicSource.Contains("ClassicAllianceView", StringComparison.Ordinal) &&
        classicSource.Contains("classic-party-size-popup", StringComparison.Ordinal) &&
        classicSource.Contains("layout.EncDps", StringComparison.Ordinal) &&
        classicSource.Contains("layout.ExtDps", StringComparison.Ordinal) &&
        classicSource.Contains("layout.Identity", StringComparison.Ordinal) &&
        classicSource.Contains("case MeterSlotMetric.PlayerIdentity", StringComparison.Ordinal) &&
        !classicSource.Contains("NameRight", StringComparison.Ordinal) &&
        !classicSource.Contains("最高技能", StringComparison.Ordinal) &&
        classicSource.Contains("DrawRankingModeIcon", StringComparison.Ordinal) &&
        classicSource.Contains("FirstOrDefault(static row => row.IsLocalPlayer)", StringComparison.Ordinal) &&
        classicSource.Contains("minimumIdentityWidth", StringComparison.Ordinal) &&
        classicSource.Contains("杰克...", StringComparison.Ordinal) &&
        classicSource.Contains("SetTooltip(displayName)", StringComparison.Ordinal) &&
        classicSource.Contains("ShouldDrawTeamSummary(settings)", StringComparison.Ordinal) &&
        classicSource.Contains("MinimumSize = new Vector2(120, 90)", StringComparison.Ordinal),
        "The classic Meter lost its table modes, compact summary rule, or compressible identity column.");
    Assert(
        roleSource.Contains("DalamudActCompatRoleSplitDamageMeter", StringComparison.Ordinal) &&
        roleSource.Contains("DalamudActCompatRoleSplitHealerMeter", StringComparison.Ordinal) &&
        roleSource.Contains("classicRenderer.DrawClassicTable", StringComparison.Ordinal) &&
        roleSource.Contains("CreateTableSettings", StringComparison.Ordinal) &&
        roleSource.Contains("Profile.BackgroundOpacity", StringComparison.Ordinal) &&
        roleSource.Contains("RoleSplitDamageCompact", StringComparison.Ordinal) &&
        roleSource.Contains("DrawChevron", StringComparison.Ordinal) &&
        roleSource.Contains("ApplyCompactWindowHeight(hasEncounter: false", StringComparison.Ordinal) &&
        roleSource.Contains("AdvanceWindowHeightAnimation", StringComparison.Ordinal) &&
        roleSource.Contains("MeterWindow.EaseOutCubic", StringComparison.Ordinal) &&
        roleSource.Contains("configuration.Meter.RoleSplitDamageWindow", StringComparison.Ordinal) &&
        roleSource.Contains("configuration.Meter.RoleSplitHealerWindow", StringComparison.Ordinal) &&
        roleSource.Contains("private List<MeterSlotDefinition> Slots => Profile.Slots", StringComparison.Ordinal) &&
        roleSource.Contains("ShowDps = Has(MeterSlotMetric.Dps)", StringComparison.Ordinal) &&
        roleSource.Contains("ShowHps = Has(MeterSlotMetric.Hps)", StringComparison.Ordinal) &&
        roleSource.Contains("DpsSortMetric = Profile.DpsSortMetric", StringComparison.Ordinal) &&
        !roleSource.Contains("leadingDamage", StringComparison.Ordinal) &&
        !roleSource.Contains("titleHovered", StringComparison.Ordinal),
        "Role split lost its independent D/T/H columns, collapse control, empty-state collapse, or height animation.");
    Assert(
        editorSource.Contains("＋ 添加槽位", StringComparison.Ordinal) &&
        editorSource.Contains("恢复此模板默认槽位", StringComparison.Ordinal) &&
        editorSource.Contains("页面预览", StringComparison.Ordinal) &&
        !editorSource.Contains("真实页面预览", StringComparison.Ordinal) &&
        editorSource.Contains("meterWindow.DrawEditorPreview", StringComparison.Ordinal) &&
        editorSource.Contains("horizontalMeterWindow.DrawEditorPreview", StringComparison.Ordinal) &&
        editorSource.Contains("roleSplitDamageWindow.DrawEditorPreview", StringComparison.Ordinal) &&
        editorSource.Contains("meter-editor-role-group", StringComparison.Ordinal) &&
        editorSource.Contains("RoleSplitHealerWindow", StringComparison.Ordinal) &&
        editorSource.Contains("RoleSplitDamageWindow", StringComparison.Ordinal) &&
        editorSource.Contains("SynchronizeLegacyRoleSplitWindow", StringComparison.Ordinal) &&
        editorSource.Contains("roleSplitHealerPreviewInteraction", StringComparison.Ordinal) &&
        editorSource.Contains("CreateRoleSplitPreviewInteraction", StringComparison.Ordinal) &&
        editorSource.Contains("SelectRoleSplitGroup(group, slotId)", StringComparison.Ordinal) &&
        editorSource.Contains("ImGuiHoveredFlags.ChildWindows", StringComparison.Ordinal) &&
        !editorSource.Contains(
            "selectedRoleSplitGroup == RoleSplitGroup.DamageTank ? interaction : null",
            StringComparison.Ordinal) &&
        !editorSource.Contains(
            "selectedRoleSplitGroup == RoleSplitGroup.Healer ? interaction : null",
            StringComparison.Ordinal) &&
        editorSource.Contains("ActivateWindow(selectedKind)", StringComparison.Ordinal) &&
        editorSource.Contains("24 人本使用固定紧凑条", StringComparison.Ordinal) &&
        editorSource.Contains("profile.BackgroundOpacity", StringComparison.Ordinal) &&
        editorSource.Contains("configuration.Fflogs.Enabled", StringComparison.Ordinal) &&
        editorSource.Contains("Move up", StringComparison.Ordinal) &&
        editorSource.Contains("Move down", StringComparison.Ordinal) &&
        editorSource.Contains("保存", StringComparison.Ordinal) &&
        editorSource.Contains("取消", StringComparison.Ordinal) &&
        editorSource.Contains("真的要退出吗？", StringComparison.Ordinal) &&
        editorSource.Contains("保存并退出", StringComparison.Ordinal) &&
        editorSource.Contains("不保存并退出", StringComparison.Ordinal) &&
        editorSource.Contains("BeginPopupModal", StringComparison.Ordinal) &&
        editorSource.Contains("editingSnapshot", StringComparison.Ordinal) &&
        !editorSource.Contains("hasUnsavedChanges", StringComparison.Ordinal) &&
        editorSource.Contains("if (!HasUnsavedConfigurationChanges())", StringComparison.Ordinal) &&
        editorSource.Contains("HasUnsavedConfigurationChanges", StringComparison.Ordinal) &&
        editorSource.Contains("SerializeForChangeDetection", StringComparison.Ordinal) &&
        editorSource.Contains("CloseWithoutChanges", StringComparison.Ordinal) &&
        editorSource.Contains("ImGuiWindowFlags.NoScrollbar", StringComparison.Ordinal) &&
        editorSource.Contains("ImGui.GetFrameHeightWithSpacing()", StringComparison.Ordinal) &&
        editorSource.Contains("DrawInlineHelp", StringComparison.Ordinal) &&
        editorSource.Contains("moveButtonWidth", StringComparison.Ordinal) &&
        previewInteractionSource.Contains("SwapSlots", StringComparison.Ordinal) &&
        previewInteractionSource.Contains("IsMouseReleased", StringComparison.Ordinal) &&
        meterSettingsSource.Contains("MigrateIndependentRoleSplitWindows", StringComparison.Ordinal) &&
        meterSettingsSource.Contains("CopyRoleSplitAppearance", StringComparison.Ordinal) &&
        !editorSource.Contains("DragMode", StringComparison.Ordinal) &&
        !editorSource.Contains("24×6", StringComparison.Ordinal) &&
        !editorSource.Contains("BeginDragDrop", StringComparison.Ordinal),
        "The shared Meter editor lost runtime-identical previews, compact non-scrolling chrome, mutual activation, opacity, FFLogs gating, or dynamic slots.");
    var serializeForChangeDetection = typeof(MeterStyleEditorWindow).GetMethod(
                                          "SerializeForChangeDetection",
                                          BindingFlags.Static | BindingFlags.NonPublic)
                                      ?? throw new InvalidOperationException(
                                          "Meter editor change-detection serializer was not found.");
    var cleanEditorSettings = new MeterSettings
    {
        CustomStyles =
        [
            new MeterCustomStyle
            {
                Slots =
                [
                    new MeterSlotDefinition(
                        MeterSlotMetric.HighestDamage,
                        0,
                        0,
                        1,
                        1,
                        MeterSlotAlignment.Right),
                ],
            },
        ],
    };
    var transientEditorSettings = Newtonsoft.Json.JsonConvert.DeserializeObject<MeterSettings>(
                                      Newtonsoft.Json.JsonConvert.SerializeObject(cleanEditorSettings))
                                  ?? throw new InvalidOperationException(
                                      "Meter editor test settings could not be cloned.");
    transientEditorSettings.HorizontalWindow.IsEditing = true;
    var cleanFingerprint = (string)serializeForChangeDetection.Invoke(null, [cleanEditorSettings])!;
    var transientFingerprint = (string)serializeForChangeDetection.Invoke(null, [transientEditorSettings])!;
    transientEditorSettings.HorizontalWindow.FontScale += 0.1f;
    var changedFingerprint = (string)serializeForChangeDetection.Invoke(null, [transientEditorSettings])!;
    Assert(
        transientEditorSettings.CustomStyles.Single().Slots.Count == 1 &&
        cleanFingerprint == transientFingerprint &&
        cleanFingerprint != changedFingerprint,
        "Meter editor dirty-state detection prompts for tab browsing or misses a real style change.");
    Assert(
        controlCenterSource.Contains("BeginCombo(\"##meter-template\"", StringComparison.Ordinal) &&
        controlCenterSource.Contains("自定义", StringComparison.Ordinal) &&
        controlCenterSource.Contains("DrawPlayerIdentityControls", StringComparison.Ordinal) &&
        controlCenterSource.Contains("DrawFflogsSettings", StringComparison.Ordinal) &&
        Regex.Matches(controlCenterSource, "游戏失去焦点时隐藏网页悬浮窗").Count >= 2 &&
        !controlCenterSource.Contains("DPS 计算口径", StringComparison.Ordinal) &&
        !settingsSource.Contains("DPS 计算口径", StringComparison.Ordinal) &&
        !controlCenterSource.Contains("DrawMeterKindRadio", StringComparison.Ordinal) &&
        !controlCenterSource.Contains("SynchronizeClassicProfileFromQuickSettings", StringComparison.Ordinal),
        "Combat Meter settings did not consolidate template selection into a dropdown with external ID and FFLogs controls.");
    Assert(
        simplifiedSource.Contains("显示战斗统计悬浮窗", StringComparison.Ordinal) &&
        simplifiedSource.Contains("退出精简模式", StringComparison.Ordinal) &&
        simplifiedSource.Contains("关闭精简主页", StringComparison.Ordinal) &&
        simplifiedSource.Contains("IsOpen = false", StringComparison.Ordinal) &&
        Regex.IsMatch(
            simplifiedSource,
            "ImGui\\.Button\\(\\s*text\\.Get\\(\"退出精简模式\"") &&
        !simplifiedSource.Contains("EnableParsing", StringComparison.Ordinal) &&
        !simplifiedSource.Contains("HTML", StringComparison.Ordinal),
        "Simplified mode lost its dedicated controls or independent close action.");

    var settings = new MeterSettings();
    settings.ActivateWindow(MeterWindowKind.Horizontal);
    settings.HorizontalWindow.Slots.Add(new MeterSlotDefinition(
        MeterSlotMetric.TotalHealing,
        0,
        0,
        4,
        2,
        MeterSlotAlignment.Left));
    var restored = Newtonsoft.Json.JsonConvert.DeserializeObject<MeterSettings>(
        Newtonsoft.Json.JsonConvert.SerializeObject(settings));
    Assert(
        restored is not null &&
        !restored.ClassicWindow.IsEnabled &&
        restored.HorizontalWindow.IsEnabled &&
        !restored.RoleSplitWindow.IsEnabled &&
        restored.ActiveWindowKind == MeterWindowKind.Horizontal &&
        restored.HorizontalWindow.Slots.Count == settings.HorizontalWindow.Slots.Count &&
        restored.HorizontalWindow.Slots.Any(static slot =>
            slot.Metric == MeterSlotMetric.TotalHealing),
        "Single-template visibility or dynamic slots did not survive configuration persistence.");
    settings.ActivateWindow(MeterWindowKind.RoleSplit);
    Assert(
        !settings.ClassicWindow.IsEnabled &&
        !settings.HorizontalWindow.IsEnabled &&
        settings.RoleSplitWindow.IsEnabled &&
        settings.RoleSplitDamageWindow.IsEnabled &&
        settings.RoleSplitHealerWindow.IsEnabled &&
        settings.ActiveWindowKind == MeterWindowKind.RoleSplit,
        "Selecting role split did not disable both other meter templates.");

    var rankingProfile = new MeterWindowProfile
    {
        Slots =
        [
            new MeterSlotDefinition(MeterSlotMetric.Rdps, 0, 0, 4, 2, MeterSlotAlignment.Left),
            new MeterSlotDefinition(MeterSlotMetric.Dps, 0, 0, 4, 2, MeterSlotAlignment.Left),
            new MeterSlotDefinition(MeterSlotMetric.Hps, 0, 0, 4, 2, MeterSlotAlignment.Left)
            {
                Visible = false,
            },
        ],
    };
    Assert(
        MeterSlotPresentation.ReplacePrimaryMetric(rankingProfile, MeterSortMode.Hps) &&
        rankingProfile.Slots[0].Metric == MeterSlotMetric.Hps &&
        rankingProfile.Slots[1].Metric == MeterSlotMetric.Dps &&
        rankingProfile.Slots.Count(static slot => slot.Metric == MeterSlotMetric.Hps) == 1 &&
        MeterSlotPresentation.ReplacePrimaryMetric(rankingProfile, MeterSortMode.Dps) &&
        rankingProfile.Slots[0].Metric == MeterSlotMetric.Dps &&
        rankingProfile.Slots.Count(static slot => slot.Metric == MeterSlotMetric.Dps) == 1 &&
        rankingProfile.Slots.All(static slot =>
            !slot.Visible || slot.Metric != MeterSlotMetric.Hps),
        "DPS/HPS switching did not replace the earliest visible damage-rate slot without duplicates.");
}

static void ValidateFflogsEstimateCurve()
{
    var estimatePercentile = typeof(FflogsEstimateService).GetMethod(
        "EstimatePercentile",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("FFLogs percentile estimator was not found.");
    var curve = new FflogsCurvePoint[]
    {
        new(0, 1_000),
        new(50, 2_000),
        new(75, 3_000),
        new(100, 5_000),
    };
    var midpoint = (double)estimatePercentile.Invoke(null, [curve, 2_500d])!;
    Assert(
        Math.Abs(midpoint - 62.5) < 0.001,
        "FFLogs estimate did not interpolate between public ranking samples.");
    Assert(
        (double)estimatePercentile.Invoke(null, [curve, 100d])! == 0 &&
        (double)estimatePercentile.Invoke(null, [curve, 9_000d])! == 100,
        "FFLogs estimate did not clamp values outside the sampled curve.");

    Assert(
        Enumerable.Range(0, 26).All(percentile =>
            FflogsEstimateService.CurveSamplePercentiles.Contains(percentile)) &&
        FflogsEstimateService.CurveSamplePercentiles.Contains(30) &&
        FflogsEstimateService.CurveSamplePercentiles.Contains(90) &&
        Enumerable.Range(95, 6).All(percentile =>
            FflogsEstimateService.CurveSamplePercentiles.Contains(percentile)),
        "FFLogs curve sampling no longer protects the nonlinear low-percentile range.");

    FflogsCurvePoint[] lindwurmPaladinLowCurve =
    [
        new(16, 19_196.895),
        new(17, 19_320.374),
    ];
    var observedLindwurmPaladinDps = (double)estimatePercentile.Invoke(
        null,
        [lindwurmPaladinLowCurve, 19_245.4d])!;
    Assert(
        Math.Round(observedLindwurmPaladinDps, MidpointRounding.AwayFromZero) == 16,
        "Dense low-percentile sampling did not reproduce the observed Lindwurm Paladin parse.");

    FflogsCurvePoint[] lindwurmBlackMageLowCurve =
    [
        new(1, 18_235.211),
        new(2, 19_999.254),
    ];
    var observedLindwurmBlackMageDps = (double)estimatePercentile.Invoke(
        null,
        [lindwurmBlackMageLowCurve, 19_437.3d])!;
    Assert(
        Math.Round(observedLindwurmBlackMageDps, MidpointRounding.AwayFromZero) == 2,
        "Dense low-percentile sampling did not reproduce the observed Lindwurm Black Mage parse.");

    FflogsCurvePoint[] tyrantPaladinCurve =
    [
        new(25, 18_718.784),
        new(50, 20_830.289),
    ];
    var observedPaladinDps = (double)estimatePercentile.Invoke(
        null,
        [tyrantPaladinCurve, 20_550d])!;
    Assert(
        Math.Round(observedPaladinDps, MidpointRounding.AwayFromZero) == 47,
        "The local Paladin DPS did not calibrate to the observed CN ranking of 47.");

    FflogsCurvePoint[] tyrantMachinistCurve =
    [
        new(25, 29_403.226),
        new(50, 32_079.946),
    ];
    var observedMachinistDps = (double)estimatePercentile.Invoke(
        null,
        [tyrantMachinistCurve, 30_601d])!;
    Assert(
        Math.Round(observedMachinistDps, MidpointRounding.AwayFromZero) == 36,
        "The local Machinist DPS did not calibrate to the observed CN ranking of 36.");

    var fflogsSource = File.ReadAllText(Path.Combine(
        FindProjectRoot(),
        "src",
        "DalamudActCompat",
        "Fflogs",
        "FflogsEstimateService.cs"));
    Assert(
        fflogsSource.Contains("metric: $metric", StringComparison.Ordinal) &&
        fflogsSource.Contains(
            "metric = CurrentFflogsEncounterTable.RankingMetric",
            StringComparison.Ordinal) &&
        fflogsSource.Contains("$serverRegion: String", StringComparison.Ordinal) &&
        fflogsSource.Contains("$partition: Int", StringComparison.Ordinal) &&
        fflogsSource.Contains("serverRegion: $serverRegion", StringComparison.Ordinal) &&
        fflogsSource.Contains("partition: $partition", StringComparison.Ordinal) &&
        fflogsSource.Contains("serverRegion = scope.ServerRegion", StringComparison.Ordinal) &&
        fflogsSource.Contains("partition = scope.Partition", StringComparison.Ordinal) &&
        fflogsSource.Contains("var encounterDps = combatant.Dps", StringComparison.Ordinal),
        "FFLogs curves stopped following the selected game region or the lookup stopped using actual DPS.");

    var legendary = FflogsEstimateService.ColorForPercentile(100);
    var pink = FflogsEstimateService.ColorForPercentile(99);
    var orange = FflogsEstimateService.ColorForPercentile(95);
    var roundedOrange = FflogsEstimateService.ColorForPercentile(94.6);
    Assert(
        legendary != pink && pink != orange && roundedOrange == orange,
        "FFLogs estimate colors did not match the rounded score shown by the Meter.");

    var encounter = new Encounter(
        Guid.NewGuid(),
        DateTimeOffset.UtcNow.AddMinutes(-1),
        DateTimeOffset.UtcNow,
        "Test Zone",
        "Test Boss",
        [
            new Combatant("tank", "Tank", "PLD", true, 100, 0, 0),
            new Combatant("healer", "Healer", "WHM", false, 80, 0, 0),
            new Combatant("tank-2", "Tank 2", "PLD", false, 90, 0, 0),
            new Combatant("Limit Break", "Limit Break", "", false, 50, 0, 0),
        ],
        [],
        [],
        [],
        [],
        []);
    Assert(
        FflogsEstimateService.ResolveFflogsSpecs(encounter)
            .SequenceEqual(["Paladin", "WhiteMage"], StringComparer.OrdinalIgnoreCase) &&
        FflogsEstimateService.ResolveRankingCombatant(encounter, "healer", "")?.Name == "Healer" &&
        FflogsEstimateService.ResolveRankingCombatant(encounter, "missing", "Tank 2")?.Id == "tank-2",
        "FFLogs did not deduplicate party jobs or resolve estimates for non-local players.");
}

static async Task ValidateFflogsPersistenceAsync(string testRoot)
{
    var cachePath = Path.Combine(testRoot, "fflogs-persistence", "fflogs-cache.json");
    Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
    await File.WriteAllTextAsync(
        cachePath,
        JsonSerializer.Serialize(new FflogsCacheDocument(
            DateTimeOffset.UtcNow,
            [new FflogsEncounterCatalogEntry(104, "Lindwurm", "AAC Heavyweight", false)],
            [new FflogsCurveCacheEntry(
                104,
                "Lindwurm",
                "Paladin",
                DateTimeOffset.UtcNow,
                [new FflogsCurvePoint(0, 1_000), new FflogsCurvePoint(100, 3_000)],
                101,
                "CN",
                9,
                "dps",
                FflogsEstimateService.CurrentCurveFormatVersion)])));

    var settings = new FflogsSettings
    {
        ClientId = "client",
        ClientSecret = "secret",
    };
    var log = DispatchProxy.Create<IPluginLog, NoOpPluginLogProxy>();
    await using var service = new FflogsEstimateService(
        () => settings,
        cachePath,
        new PluginLogger(log));
    service.NotifyTerritoryChanged(1327, "AAC Heavyweight M4 (Savage)");
    settings.Enabled = true;

    var end = DateTimeOffset.UtcNow;
    var encounter = new Encounter(
        Guid.NewGuid(),
        end.AddSeconds(-30),
        end,
        "AAC Heavyweight M4 (Savage)",
        "Lindwurm",
        [new Combatant(
            "local", "Player", "PLD", true, 90_000, 0, 0,
            Dps: 2_000,
            EncDps: 2_500,
            ExtDps: 2_750,
            Rdps: 3_000)],
        [],
        [],
        [],
        [],
        [])
    {
        TerritoryId = 1327,
        CombatDuration = TimeSpan.FromSeconds(30),
    };

    var captured = service.CaptureAvailableEstimates(encounter);
    var capturedCombatant = captured.Combatants.Single();
    Assert(
        capturedCombatant.FflogsPercentile == 50 &&
        capturedCombatant.FflogsEncounterName == "Lindwurm" &&
        capturedCombatant.FflogsMetric == "DPS" &&
        capturedCombatant.FflogsDataUpdatedAt is not null,
        "An FFLogs estimate already visible at encounter end was not captured for history.");

    var dutyFinalizedFromAnActiveBoss = service.CaptureAvailableEstimates(encounter with
    {
        Combatants = [encounter.Combatants[0]],
        FflogsRankingEncounter = encounter with { EndTime = null },
    });
    Assert(
        dutyFinalizedFromAnActiveBoss.Combatants[0].FflogsPercentile == 50,
        "Leaving a duty during an active boss did not capture the available FFLogs estimate.");

    var restored = JsonSerializer.Deserialize<Encounter>(JsonSerializer.Serialize(captured))
                   ?? throw new InvalidOperationException(
                       "The encounter containing a captured FFLogs estimate could not be restored.");
    settings.Enabled = false;
    var historicalEstimate = service.GetEstimate(restored, "local", "Player");
    Assert(
        historicalEstimate is { Score: 50, EncounterName: "Lindwurm" },
        "A captured FFLogs estimate did not survive JSON history or later setting changes.");

    settings.Enabled = true;
    var unavailable = service.CaptureAvailableEstimates(encounter with
    {
        Combatants = [encounter.Combatants[0] with { Job = "WAR" }],
    });
    Assert(
        unavailable.Combatants[0].FflogsPercentile is null &&
        service.Status.State != FflogsEstimateState.Loading,
        "Encounter finalization queued a new FFLogs request instead of capturing only an available estimate.");
}

static void ValidateFflogsCurrentEncounterTable()
{
    var tableType = typeof(FflogsEstimateService).Assembly.GetType(
                        "DalamudActCompat.Fflogs.CurrentFflogsEncounterTable")
                    ?? throw new InvalidOperationException(
                        "The current FFLogs encounter table was not found.");
    var tryResolve = tableType.GetMethod(
                         "TryResolve",
                         BindingFlags.Static | BindingFlags.NonPublic)
                     ?? throw new InvalidOperationException(
                         "The current FFLogs duty resolver was not found.");
    var observePhase = tableType.GetMethod(
                           "ObservePhase",
                           BindingFlags.Static | BindingFlags.NonPublic)
                       ?? throw new InvalidOperationException(
                           "The FFLogs phase observer was not found.");
    var getRankingScope = tableType.GetMethod(
                              "GetRankingScope",
                              BindingFlags.Static | BindingFlags.NonPublic)
                          ?? throw new InvalidOperationException(
                              "The FFLogs ranking scope resolver was not found.");

    var chineseScope = getRankingScope.Invoke(null, [true])
                       ?? throw new InvalidOperationException("The Chinese FFLogs ranking scope was null.");
    var globalScope = getRankingScope.Invoke(null, [false])
                      ?? throw new InvalidOperationException("The global FFLogs ranking scope was null.");
    Assert(
        ReadProperty<string>(chineseScope, "ServerRegion") == "CN" &&
        ReadProperty<int?>(chineseScope, "Partition") == 9 &&
        ReadProperty<string?>(globalScope, "ServerRegion") is null &&
        ReadProperty<int?>(globalScope, "Partition") is null,
        "FFLogs ranking scopes no longer pin CN to partition 9 or use the latest global population.");

    object?[] normalM4Arguments = [1326u, 1, null];
    Assert(
        Equals(tryResolve.Invoke(null, normalM4Arguments), true) &&
        ReadEncounterValue(normalM4Arguments[2], "EncounterId") == 104 &&
        ReadEncounterValue(normalM4Arguments[2], "Difficulty") == 100,
        "AAC Heavyweight M4 did not resolve to the current Lindwurm Normal ranking.");

    object?[] oldDutyArguments = [1269u, 1, null];
    Assert(
        Equals(tryResolve.Invoke(null, oldDutyArguments), false),
        "A duty outside the current FFLogs tier was allowed to resolve.");

    var phaseTwo = (int)(observePhase.Invoke(
        null,
        [1327u, 1, "21|2026-08-08T00:00:00.0000000+08:00|40000001|Lindwurm|BBD8|Mindless Flesh"])
        ?? 0);
    object?[] savageM4Arguments = [1327u, phaseTwo, null];
    Assert(
        phaseTwo == 2 &&
        Equals(tryResolve.Invoke(null, savageM4Arguments), true) &&
        ReadEncounterValue(savageM4Arguments[2], "EncounterId") == 105 &&
        ReadEncounterValue(savageM4Arguments[2], "Difficulty") == 101,
        "M4S did not switch to the Lindwurm II Savage ranking after a phase-two action.");

    static int ReadEncounterValue(object? encounter, string propertyName)
        => (int)(encounter?.GetType().GetProperty(propertyName)?.GetValue(encounter)
                 ?? throw new InvalidOperationException($"Resolved FFLogs encounter has no {propertyName}."));

    static T ReadProperty<T>(object value, string propertyName)
        => (T)value.GetType().GetProperty(propertyName)!.GetValue(value)!;
}

static void ValidateEncounterParticipantsSurvivePartyDeparture()
{
    ActGlobals.Init();
    var encounter = new EncounterData("Player 1", "Test Duty", false, null!);
    var allies = Enumerable.Range(1, 8)
        .Select(index => new CombatantData($"Player {index}", encounter))
        .ToList();
    allies.Add(new CombatantData("Player 9", encounter));
    allies.Add(new CombatantData("Escort NPC", encounter));
    allies.Add(new CombatantData("Alliance Player", encounter));
    allies.Add(new CombatantData(ChineseCombatChatContext.LimitBreakActorName, encounter));
    encounter.SetAllies(allies);

    var cachedIdentities = Enumerable.Range(1, 8)
        .Select(index => new ActPlayerIdentity(
            $"Player {index}",
            "Test World",
            index <= 2 ? "WAR" : "DPS",
            index == 1,
            false)
        {
            ContentId = (ulong)index,
        })
        .ToArray();
    var liveIdentities = cachedIdentities.Take(5).ToArray();

    var resolved = SelfHostedActRuntime.ResolveEncounterCombatants(
        encounter,
        liveIdentities,
        cachedIdentities,
        partyCapacity: 8);

    Assert(
        resolved.Count == 9 &&
        resolved.Count(static item => item.Identity is not null) == 8,
        "Combat statistics did not contain exactly eight party players plus one Limit Break row.");
    Assert(
        resolved.All(static item =>
            item.Identity is not null ||
            item.Combatant.Name == ChineseCombatChatContext.LimitBreakActorName),
        "An ACT ally without a player identity leaked into combat statistics.");
    Assert(
        resolved.Skip(5).Select(static item => item.Identity!.Name)
            .Take(3)
            .SequenceEqual(["Player 6", "Player 7", "Player 8"]),
        "The departed players were not restored from the encounter identity cache.");
    Assert(
        resolved.All(static item =>
            item.Combatant.Name is not ("Escort NPC" or "Alliance Player")),
        "A friendly NPC or non-party alliance player was included in combat statistics.");
    Assert(
        resolved.Count(static item =>
            item.Combatant.Name == ChineseCombatChatContext.LimitBreakActorName &&
            item.Identity is null) == 1,
        "The synthetic Limit Break row was removed with non-player ACT allies.");
    Assert(
        resolved.All(static item => item.Combatant.Name != "Player 9"),
        "A ninth player outside the encounter's local party was included in combat statistics.");

    var replacementIdentity = new ActPlayerIdentity(
        "Player 9",
        "Test World",
        "DPS",
        false,
        false)
    {
        ContentId = 9,
    };
    var replacementParty = cachedIdentities
        .Take(7)
        .Append(replacementIdentity)
        .ToArray();
    var afterReplacement = SelfHostedActRuntime.ResolveEncounterCombatants(
        encounter,
        replacementParty,
        cachedIdentities,
        partyCapacity: 8);
    Assert(
        afterReplacement.Count == 9 &&
        afterReplacement.Any(static item => item.Combatant.Name == "Player 9") &&
        afterReplacement.All(static item => item.Combatant.Name != "Player 8"),
        "A live replacement did not take priority over a departed cached party player.");

    var withoutMetadata = SelfHostedActRuntime.ResolveEncounterCombatants(
        encounter,
        liveIdentities,
        [],
        partyCapacity: 8);
    Assert(
        withoutMetadata.Count == 6 &&
        withoutMetadata.Count(static item => item.Identity is not null) == 5 &&
        withoutMetadata.All(static item =>
            item.Combatant.Name is not ("Escort NPC" or "Alliance Player")),
        "ACT allies without authoritative player metadata were included in combat statistics.");

    var fourPlayerCache = cachedIdentities.Take(4).ToArray();
    var fourPlayerVacancy = SelfHostedActRuntime.ResolveEncounterCombatants(
        encounter,
        fourPlayerCache.Take(3).ToArray(),
        fourPlayerCache,
        partyCapacity: 4);
    var fourPlayerReplacement = SelfHostedActRuntime.ResolveEncounterCombatants(
        encounter,
        fourPlayerCache.Take(3).Append(replacementIdentity).ToArray(),
        fourPlayerCache,
        partyCapacity: 4);
    Assert(
        fourPlayerVacancy.Count == 5 &&
        fourPlayerVacancy.Any(static item => item.Combatant.Name == "Player 4") &&
        fourPlayerVacancy.All(static item => item.Combatant.Name != "Player 9") &&
        fourPlayerReplacement.Count == 5 &&
        fourPlayerReplacement.Any(static item => item.Combatant.Name == "Player 9") &&
        fourPlayerReplacement.All(static item => item.Combatant.Name != "Player 4"),
        "A four-player vacancy did not retain the departed member or a full replacement expanded the roster to five players.");
}

static void ValidateEmptyEncounterFiltering()
{
    var now = DateTimeOffset.UtcNow;
    var empty = new Encounter(
        Guid.NewGuid(),
        now,
        now,
        "Middle La Noscea",
        "Encounter",
        [new Combatant("local", "Player", "PLD", true, 0, 0, 0)],
        [],
        [],
        [],
        [],
        []);

    Assert(
        !IinactAdapter.HasMeaningfulActivity(empty),
        "A zero-damage missed action is still eligible for encounter history.");
    Assert(
        IinactAdapter.HasMeaningfulActivity(empty with
        {
            Combatants = [empty.Combatants[0] with { TotalDamage = 1 }],
        }) &&
        IinactAdapter.HasMeaningfulActivity(empty with
        {
            Combatants = [empty.Combatants[0] with { TotalHealing = 1 }],
        }) &&
        IinactAdapter.HasMeaningfulActivity(empty with
        {
            Combatants = [empty.Combatants[0] with { Deaths = 1 }],
        }),
        "The empty-encounter filter rejects a meaningful damage, healing, or death record.");
}

static void ValidateDutyEncounterAggregation()
{
    var start = new DateTimeOffset(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);
    var accumulator = new DutyEncounterAccumulator();
    var first = CreateDutySegment(
        Guid.NewGuid(),
        start,
        start.AddMinutes(2),
        "测试副本",
        "Boss A",
        damage: 100,
        healing: 20,
        deaths: 1,
        damageHits: 10,
        criticalHits: 3,
        directHits: 4,
        criticalDirectHits: 1);
    first = first with
    {
        Combatants =
        [
            first.Combatants[0] with
            {
                FflogsPercentile = 40,
                FflogsEncounterName = "Boss A",
            },
        ],
    };
    var afterFirst = accumulator.Update(first, finished: true, start.AddMinutes(2));
    Assert(
        afterFirst.IsActive &&
        afterFirst.TotalDamage == 100 &&
        afterFirst.SegmentRecords.Count == 1 &&
        afterFirst.SegmentRecords[0].Id == first.Id,
        "The first ACT record was not retained inside the active pull folder.");
    Assert(
        afterFirst.FflogsRankingEncounter?.EnemyName == "Boss A" &&
        afterFirst.FflogsRankingEncounter.TotalDamage == 100,
        "Duty aggregation did not retain the concrete boss segment for FFLogs estimation.");
    var duplicateFirst = accumulator.Update(first, finished: true, start.AddMinutes(3));
    Assert(
        duplicateFirst.TotalDamage == 100 && duplicateFirst.SegmentRecords.Count == 1,
        "A repeated completed ACT record was counted twice inside its pull folder.");

    var secondId = Guid.NewGuid();
    var secondActive = CreateDutySegment(
        secondId,
        start.AddMinutes(4),
        endTime: null,
        "测试副本",
        "Boss B",
        damage: 50,
        healing: 10,
        deaths: 0,
        damageHits: 5,
        criticalHits: 1,
        directHits: 2,
        criticalDirectHits: 1) with
    {
        IsTransitioning = true,
    };
    var combinedActive = accumulator.Update(secondActive, finished: false, start.AddMinutes(5));
    Assert(
        combinedActive.Id == afterFirst.Id &&
        combinedActive.TotalDamage == 150 &&
        combinedActive.TotalHealing == 30 &&
        combinedActive.TotalDeaths == 1 &&
        combinedActive.IsTransitioning &&
        combinedActive.EnemyName == "测试副本" &&
        combinedActive.SegmentRecords.Count == 2 &&
        combinedActive.SegmentRecords[0].Id == first.Id &&
        combinedActive.SegmentRecords[1].Id == secondId,
        "ACT records from one pull were not accumulated inside the same folder.");
    Assert(
        combinedActive.FflogsRankingEncounter?.Id == secondId &&
        combinedActive.FflogsRankingEncounter.TotalDamage == 50,
        "FFLogs estimation did not receive the active pull's concrete segment.");
    Assert(
        !JsonSerializer.Serialize(combinedActive).Contains(
            nameof(Encounter.FflogsRankingEncounter),
            StringComparison.Ordinal),
        "The transient FFLogs ranking segment leaked into persisted encounter JSON.");

    var secondFinished = secondActive with
    {
        EndTime = start.AddMinutes(6),
        IsTransitioning = false,
        Combatants =
        [
            secondActive.Combatants[0] with
            {
                TotalDamage = 80,
                TotalHealing = 15,
                DamageHits = 8,
                CriticalHits = 2,
                DirectHits = 3,
                CriticalDirectHits = 1,
                FflogsPercentile = 75,
                FflogsEncounterName = "Boss B",
            },
        ],
    };
    var folderBeforeCompletion = accumulator.Update(
        secondFinished,
        finished: true,
        start.AddMinutes(6));
    Assert(
        folderBeforeCompletion.IsActive &&
        folderBeforeCompletion.TotalDamage == 180 &&
        folderBeforeCompletion.TotalHealing == 35 &&
        folderBeforeCompletion.TotalDeaths == 1 &&
        folderBeforeCompletion.SegmentRecords.Count == 2,
        "Completing an ACT record closed or fragmented the active pull folder.");
    var completed = accumulator.Complete(start.AddMinutes(6))
                    ?? throw new InvalidOperationException(
                        "The duty pull folder produced no completed encounter.");
    Assert(
        !completed.IsActive &&
        completed.TotalDamage == 180 &&
        completed.TotalHealing == 35 &&
        completed.TotalDeaths == 1 &&
        completed.Combatants[0].DamageHits == 18 &&
        completed.Combatants[0].CriticalHits == 5 &&
        completed.Combatants[0].DirectHits == 7 &&
        completed.Combatants[0].CriticalDirectHits == 2 &&
        completed.SegmentRecords.Count == 2 &&
        completed.SegmentRecords.All(static segment => !segment.IsActive) &&
        completed.SegmentRecords.All(static segment => segment.SegmentRecords.Count == 0),
        "The completed pull folder lost its aggregate totals or concrete child records.");
    Assert(
        completed.Duration == TimeSpan.FromMinutes(6) &&
        completed.EffectiveDuration == TimeSpan.FromMinutes(4) &&
        Math.Abs(completed.Combatants[0].EncDps - (180d / 240)) < 0.0001,
        "The pull folder did not preserve separate wall-time and active-damage durations.");
    Assert(
        completed.FflogsRankingEncounter?.Id == secondId &&
        completed.FflogsRankingEncounter.TotalDamage == 80,
        "The completed pull did not retain its concrete segment for FFLogs estimation.");
    Assert(
        completed.Combatants[0].FflogsPercentile == 75 &&
        completed.Combatants[0].FflogsEncounterName == "Boss B",
        "Pull history did not retain the completed boss's FFLogs estimate.");

    var serialized = JsonSerializer.Serialize(completed);
    var restored = JsonSerializer.Deserialize<Encounter>(serialized)
                   ?? throw new InvalidOperationException(
                       "The completed duty pull could not be restored from JSON.");
    Assert(
        restored.CombatDuration == TimeSpan.FromMinutes(4) &&
        restored.EffectiveDuration == TimeSpan.FromMinutes(4) &&
        restored.TerritoryId == 777 &&
        restored.SegmentRecords.Count == 2 &&
        restored.SegmentRecords[0].Id == first.Id &&
        restored.SegmentRecords[1].Id == secondId &&
        restored.Combatants[0].FflogsPercentile == 75 &&
        restored.Combatants[0].FflogsEncounterName == "Boss B",
        "The pull folder, duration, or Territory ID was not preserved in encounter history.");
    Assert(
        EncounterWindow.FindRecentEncounter([restored], restored.Id)?.Id == restored.Id &&
        EncounterWindow.FindRecentEncounter([restored], secondId)?.Id == secondId &&
        EncounterWindow.FindRecentEncounter([restored], Guid.NewGuid()) is null,
        "Combat History cannot select the pull folder and its child ACT records independently.");

    var nextPull = accumulator.Update(
        secondActive,
        finished: false,
        start.AddMinutes(5));
    Assert(
        nextPull.Id != completed.Id &&
        nextPull.TotalDamage == 50 &&
        nextPull.TotalHealing == 10 &&
        nextPull.TotalDeaths == 0 &&
        nextPull.SegmentRecords.Count == 1,
        "A new pull folder inherited records or totals from the completed pull.");

    accumulator.Reset();
    Assert(
        !accumulator.HasData && accumulator.SegmentIds.Count == 0,
        "Resetting the current pull left accumulator state that could republish old totals.");
}

static void ValidateDutyEncounterFolderAggregation()
{
    var start = new DateTimeOffset(2026, 8, 15, 23, 47, 41, TimeSpan.FromHours(8));
    var firstPull = CreateDutySegment(
        Guid.NewGuid(),
        start,
        start.AddSeconds(28),
        "The Navel",
        "Titan",
        damage: 1_274_880,
        healing: 170_222,
        deaths: 1,
        damageHits: 10,
        criticalHits: 3,
        directHits: 4,
        criticalDirectHits: 1) with
    {
        SegmentRecords = [SampleEncounterFactory.Create(start)],
    };
    var secondPull = CreateDutySegment(
        Guid.NewGuid(),
        start.AddSeconds(46),
        start.AddSeconds(51),
        "The Navel",
        "Titan",
        damage: 44_315,
        healing: 0,
        deaths: 1,
        damageHits: 2,
        criticalHits: 0,
        directHits: 1,
        criticalDirectHits: 0);

    var accumulator = new DutyEncounterFolderAccumulator();
    var afterFirst = accumulator.Add(firstPull);
    var afterSecond = accumulator.Add(secondPull);
    Assert(
        afterSecond.Id == afterFirst.Id &&
        afterSecond.SegmentRecords.Count == 2 &&
        afterSecond.SegmentRecords[0].Id == firstPull.Id &&
        afterSecond.SegmentRecords[1].Id == secondPull.Id &&
        afterSecond.SegmentRecords.All(static pull => pull.SegmentRecords.Count == 0) &&
        afterSecond.Combatants.Count == 0 &&
        afterSecond.SegmentRecords.Sum(static pull => pull.TotalDeaths) == 2 &&
        afterSecond.SegmentRecords[0].TotalDamage == 1_274_880 &&
        afterSecond.SegmentRecords[1].TotalDamage == 44_315,
        "Two wipes from one duty entry were split into folders or their pull totals were accumulated together.");

    var completed = accumulator.Complete()
                    ?? throw new InvalidOperationException("The duty folder produced no history entry.");
    Assert(
        completed.Id == afterFirst.Id &&
        completed.SegmentRecords.Count == 2 &&
        !accumulator.HasData,
        "Completing a duty entry lost its stable folder ID or retained stale pull state.");

    var nextDuty = accumulator.Add(secondPull);
    Assert(
        nextDuty.Id != completed.Id && nextDuty.SegmentRecords.Count == 1,
        "A later duty entry reused the previous duty folder.");
}

static void ValidateDutyWipeTracking()
{
    var now = DateTimeOffset.UtcNow;
    var finishedPull = new Encounter(
        Guid.NewGuid(),
        now.AddMinutes(-1),
        now,
        "Test zone",
        "Test target",
        [new Combatant("local", "Player", "PLD", true, 1000, 0, 0)],
        [],
        [],
        [],
        [],
        []);
    var stateStore = new EncounterStateStore();
    stateStore.UpdateCurrent(finishedPull);
    Assert(
        stateStore.GetDisplayEncounter()?.Id == finishedPull.Id &&
        stateStore.GetDisplayEncounter()?.IsActive == false,
        "A completed pull was not retained for post-combat review.");
    var nextPull = finishedPull with
    {
        Id = Guid.NewGuid(),
        StartTime = now.AddSeconds(5),
        EndTime = null,
        Combatants =
        [
            finishedPull.Combatants[0] with
            {
                TotalDamage = 25,
            },
        ],
    };
    stateStore.UpdateCurrent(nextPull);
    Assert(
        stateStore.GetDisplayEncounter() is { IsActive: true } currentPull &&
        currentPull.Id == nextPull.Id && currentPull.TotalDamage == 25,
        "Meaningful data from the next pull did not replace the retained result from zero.");
    stateStore.ResetCurrent();
    Assert(
        stateStore.GetDisplayEncounter() is null,
        "A manual reset allowed retained totals to bounce back into the meter.");

    var tracker = new DutyWipeTracker();
    Assert(
        !tracker.Observe(trackDutyAttempt: true, inCombat: true, partyWiped: false) &&
        !tracker.Observe(trackDutyAttempt: true, inCombat: false, partyWiped: false),
        "An ordinary combat exit was mistaken for a party wipe.");
    Assert(
        !tracker.Observe(trackDutyAttempt: true, inCombat: true, partyWiped: true) &&
        tracker.Observe(trackDutyAttempt: true, inCombat: false, partyWiped: true) &&
        !tracker.Observe(trackDutyAttempt: true, inCombat: false, partyWiped: true),
        "A real party wipe was not latched until combat ended, or it reset more than once.");
    Assert(
        !tracker.Observe(trackDutyAttempt: true, inCombat: true, partyWiped: false) &&
        tracker.Observe(trackDutyAttempt: true, inCombat: false, partyWiped: true),
        "A later repull could not produce its own independent wipe reset.");
    Assert(
        !tracker.Observe(trackDutyAttempt: false, inCombat: false, partyWiped: true),
        "Leaving a duty was incorrectly reported as an in-duty wipe.");
    Assert(
        Plugin.IsDutyPartyWiped(
            boundByDuty: true,
            localPlayerUnconscious: true,
            anyPartyMemberAlive: false) &&
        !Plugin.IsDutyPartyWiped(true, true, anyPartyMemberAlive: true) &&
        !Plugin.IsDutyPartyWiped(true, localPlayerUnconscious: false, false) &&
        !Plugin.IsDutyPartyWiped(boundByDuty: false, true, false),
        "The party-wipe predicate no longer requires a bound duty, local death, and no survivor.");
}

static void ValidateDutyEncounterPartySizes()
{
    foreach (var partySize in new[] { 4, 8, 24 })
    {
        var start = new DateTimeOffset(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);
        var first = CreatePartySegment(
            Guid.NewGuid(),
            start,
            start.AddSeconds(30),
            partySize,
            damage: 3_000,
            dps: 150,
            encDps: 100,
            extDps: 75);
        var second = CreatePartySegment(
            Guid.NewGuid(),
            start.AddSeconds(90),
            start.AddSeconds(110),
            partySize,
            damage: 2_000,
            dps: 200,
            encDps: 100,
            extDps: 50);
        var accumulator = new DutyEncounterAccumulator();
        _ = accumulator.Update(first, finished: true, first.EndTime!.Value);
        var firstCompleted = accumulator.Complete(first.EndTime.Value)
                             ?? throw new InvalidOperationException(
                                 $"The first {partySize}-player pull did not complete.");
        _ = accumulator.Update(second, finished: true, second.EndTime!.Value);
        var secondCompleted = accumulator.Complete(second.EndTime.Value)
                              ?? throw new InvalidOperationException(
                                  $"The second {partySize}-player pull did not complete.");

        Assert(
            firstCompleted.Combatants.Count == partySize &&
            firstCompleted.EffectiveDuration == TimeSpan.FromSeconds(30) &&
            firstCompleted.Duration == TimeSpan.FromSeconds(30) &&
            secondCompleted.Combatants.Count == partySize &&
            secondCompleted.EffectiveDuration == TimeSpan.FromSeconds(20) &&
            secondCompleted.Duration == TimeSpan.FromSeconds(20),
            $"The {partySize}-player pulls did not preserve independent rosters and durations.");
        Assert(
            firstCompleted.Combatants.All(combatant =>
                Math.Abs(combatant.Dps - 150) < 0.0001 &&
                Math.Abs(combatant.EncDps - 100) < 0.0001 &&
                Math.Abs(combatant.ExtDps - 75) < 0.0001) &&
            secondCompleted.Combatants.All(combatant =>
                Math.Abs(combatant.Dps - 200) < 0.0001 &&
                Math.Abs(combatant.EncDps - 100) < 0.0001 &&
                Math.Abs(combatant.ExtDps - 50) < 0.0001),
            $"The {partySize}-player pulls mixed their DPS, EncDPS, or ExtDPS durations.");
    }
}

static Encounter CreatePartySegment(
    Guid id,
    DateTimeOffset start,
    DateTimeOffset end,
    int partySize,
    long damage,
    double dps,
    double encDps,
    double extDps)
    => new(
        id,
        start,
        end,
        "测试副本",
        "测试首领",
        Enumerable.Range(1, partySize)
            .Select(index => new Combatant(
                $"player-{index}",
                $"Player {index}",
                index <= 2 ? "PLD" : "DPS",
                index == 1,
                damage,
                0,
                0,
                dps,
                encDps,
                extDps))
            .ToArray(),
        [],
        [],
        [],
        [],
        []);

static Encounter CreateDutySegment(
    Guid id,
    DateTimeOffset start,
    DateTimeOffset? endTime,
    string zone,
    string enemy,
    long damage,
    long healing,
    int deaths,
    int damageHits = 0,
    int criticalHits = 0,
    int directHits = 0,
    int criticalDirectHits = 0)
    => new(
        id,
        start,
        endTime,
        zone,
        enemy,
        [new Combatant(
            "local", "Player", "PLD", true, damage, healing, deaths,
            DamageHits: damageHits,
            CriticalHits: criticalHits,
            DirectHits: directHits,
            CriticalDirectHits: criticalDirectHits)],
        Array.Empty<DamageEvent>(),
        Array.Empty<HealEvent>(),
        Array.Empty<DeathEvent>(),
        Array.Empty<ActionSummary>(),
        Array.Empty<JobSummary>())
    {
        TerritoryId = 777,
    };

static void ValidateControlCenterPresentation()
{
    Assert(
        ControlCenterWindow.EaseInOut(0) == 0 &&
        Math.Abs(ControlCenterWindow.EaseInOut(0.5f) - 0.5f) < 0.001f &&
        ControlCenterWindow.EaseInOut(1) == 1,
        "The ACT control center visibility transition is not a bounded ease-in-out curve.");
    Assert(
        ControlCenterWindow.FormatVersionLabel(new Version(0, 3, 7, 7)) == "v0.3.7.7",
        "The ACT control center no longer displays the full four-part assembly version.");
    Assert(
        !ControlCenterWindow.IsResetConfirmationExpired(11_000, 10_999) &&
        ControlCenterWindow.IsResetConfirmationExpired(11_000, 11_000),
        "The two-step encounter reset confirmation does not expire after ten seconds.");
    var resetProbe = new TestParserEngine(ParserState.Running);
    var resetForwarder = new ParserEngine(resetProbe);
    resetForwarder.ResetCurrentEncounter();
    Assert(
        resetProbe.ResetCount == 1,
        "The parser facade did not forward the full encounter reset to its runtime adapter.");
    Assert(
        ThirdPartyPluginNoticeWindow.ShouldOpenUpdateResult(1, failed: false, userInitiated: false) &&
        !ThirdPartyPluginNoticeWindow.ShouldOpenUpdateResult(0, failed: false, userInitiated: true) &&
        ThirdPartyPluginNoticeWindow.ShouldOpenUpdateResult(0, failed: true, userInitiated: true) &&
        !ThirdPartyPluginNoticeWindow.ShouldOpenUpdateResult(0, failed: true, userInitiated: false),
        "The DLL update-check window does not stay hidden when a successful check finds no updates.");
    Assert(
        ThirdPartyPluginNoticeWindow.ShouldShowCloseButton(
            ThirdPartyNoticeOpenMode.ManualDisclosure,
            pendingCount: 1,
            installInProgress: false,
            permissionChoicePending: false,
            ttsProChoicePending: false) &&
        ThirdPartyPluginNoticeWindow.ShouldShowCloseButton(
            ThirdPartyNoticeOpenMode.ManualUpdateCheck,
            pendingCount: 1,
            installInProgress: false,
            permissionChoicePending: false,
            ttsProChoicePending: false) &&
        !ThirdPartyPluginNoticeWindow.ShouldShowCloseButton(
            ThirdPartyNoticeOpenMode.RequiredAfterPluginUpdate,
            pendingCount: 1,
            installInProgress: false,
            permissionChoicePending: false,
            ttsProChoicePending: false) &&
        ThirdPartyPluginNoticeWindow.ShouldShowCloseButton(
            ThirdPartyNoticeOpenMode.RequiredAfterPluginUpdate,
            pendingCount: 0,
            installInProgress: false,
            permissionChoicePending: false,
            ttsProChoicePending: false) &&
        !ThirdPartyPluginNoticeWindow.ShouldShowCloseButton(
            ThirdPartyNoticeOpenMode.ManualDisclosure,
            pendingCount: 0,
            installInProgress: true,
            permissionChoicePending: false,
            ttsProChoicePending: false) &&
        !ThirdPartyPluginNoticeWindow.ShouldShowCloseButton(
            ThirdPartyNoticeOpenMode.ManualDisclosure,
            pendingCount: 0,
            installInProgress: false,
            permissionChoicePending: true,
            ttsProChoicePending: false) &&
        !ThirdPartyPluginNoticeWindow.ShouldShowCloseButton(
            ThirdPartyNoticeOpenMode.ManualDisclosure,
            pendingCount: 0,
            installInProgress: false,
            permissionChoicePending: false,
            ttsProChoicePending: true) &&
        ThirdPartyPluginNoticeWindow.CanAdvanceToPermissionChoice(0) &&
        !ThirdPartyPluginNoticeWindow.CanAdvanceToPermissionChoice(1),
        "The required third-party notice is dismissible before acknowledgement, a manual notice is not dismissible while idle, or permission flow advances with pending disclosures.");
    var degradedInstall = BundledPluginInstallOutcome.RuntimeRecoveryPending(
        new InvalidOperationException("expected runtime recovery warning"));
    Assert(
        BundledPluginInstallOutcome.Ready.RuntimeReady &&
        !degradedInstall.RuntimeReady &&
        degradedInstall.RuntimeWarning == "expected runtime recovery warning",
        "Bundled installation no longer distinguishes a durable install from runtime recovery.");
    Assert(
        Plugin.ShouldEnableBundledCapability(
            enableFullFunctionality: false,
            ActCapability.TextToSpeech) &&
        !Plugin.ShouldEnableBundledCapability(
            enableFullFunctionality: false,
            ActCapability.NetworkRequest) &&
        Plugin.ShouldEnableBundledCapability(
            enableFullFunctionality: true,
            ActCapability.NetworkRequest),
        "Declining full permissions does not restore safe defaults after a previous release granted them.");
    Assert(
        Plugin.ShouldOfferFoxTtsPro(suppressPrompt: false, isPro: false) &&
        !Plugin.ShouldOfferFoxTtsPro(suppressPrompt: false, isPro: true) &&
        !Plugin.ShouldOfferFoxTtsPro(suppressPrompt: true, isPro: false),
        "The Cafe TTS Pro prompt ignores the current engine or the user's never-remind choice.");
    var ttsPromptConfiguration = Newtonsoft.Json.JsonConvert.DeserializeObject<PluginConfiguration>(
        Newtonsoft.Json.JsonConvert.SerializeObject(new PluginConfiguration
        {
            SuppressFoxTtsProPrompt = true,
        })) ?? throw new InvalidOperationException(
            "The FoxTTS prompt configuration did not deserialize.");
    Assert(
        ttsPromptConfiguration.SuppressFoxTtsProPrompt,
        "The FoxTTS never-remind choice is not persisted across plugin updates.");
    ttsPromptConfiguration.ResetToDefaults(Path.GetTempPath());
    Assert(
        !ttsPromptConfiguration.SuppressFoxTtsProPrompt &&
        ttsPromptConfiguration.EnableParsing &&
        ttsPromptConfiguration.AutoStartParser &&
        ttsPromptConfiguration.AutoCloudSyncEnabled &&
        ttsPromptConfiguration.DisabledActPluginIds.Contains("silverdasher"),
        "Factory reset does not restore the FoxTTS prompt, SilverDasher disabled state, or independent parser startup defaults.");

    var combatant = new Combatant(
        "local",
        "Player",
        "WHM",
        true,
        120_000,
        60_000,
        1,
        Dps: 2_000,
        EncDps: 1_500,
        ExtDps: 1_250,
        Rdps: 1_750);
    Assert(
        EncounterWindow.ResolveRate(combatant, 60, MeterSortMode.Dps, DpsMetric.Rdps) == 1_750 &&
        EncounterWindow.ResolveRate(combatant, 60, MeterSortMode.Dps, DpsMetric.EncDps) == 1_500 &&
        EncounterWindow.ResolveRate(combatant, 60, MeterSortMode.Dps, DpsMetric.ExtDps) == 1_250 &&
        EncounterWindow.ResolveRate(combatant, 60, MeterSortMode.Hps, DpsMetric.EncDps) == 1_000,
        "Combat History no longer follows the Combat Meter DPS metric and DPS/HPS sort mode.");

    var projectRoot = FindProjectRoot();
    var controlCenterSource = File.ReadAllText(Path.Combine(
        projectRoot, "src", "DalamudActCompat", "UI", "ControlCenterWindow.cs"));
    var meterWindowSource = File.ReadAllText(Path.Combine(
        projectRoot, "src", "DalamudActCompat", "Meter", "MeterWindow.cs"));
    var historySource = File.ReadAllText(Path.Combine(
        projectRoot, "src", "DalamudActCompat", "Encounters", "EncounterWindow.cs"));
    var thirdPartySource = File.ReadAllText(Path.Combine(
        projectRoot, "src", "DalamudActCompat", "UI", "ThirdPartyPluginNoticeWindow.cs"));
    var statusSource = File.ReadAllText(Path.Combine(
        projectRoot, "src", "DalamudActCompat", "UI", "StatusWindow.cs"));
    var settingsSource = File.ReadAllText(Path.Combine(
        projectRoot, "src", "DalamudActCompat", "UI", "SettingsWindow.cs"));
    var pluginSource = File.ReadAllText(Path.Combine(
        projectRoot, "src", "DalamudActCompat", "Plugin", "Plugin.cs"));
    var parserAdapterSource = File.ReadAllText(Path.Combine(
        projectRoot, "src", "DalamudActCompat", "Parser", "IinactAdapter.cs"));
    var coreResourceWindowSource = File.ReadAllText(Path.Combine(
        projectRoot, "src", "DalamudActCompat", "UI", "CoreResourceDownloadWindow.cs"));
    var cloudBanNoticeSource = File.ReadAllText(Path.Combine(
        projectRoot, "src", "DalamudActCompat", "UI", "CloudBanNoticeWindow.cs"));
    var configurationSource = File.ReadAllText(Path.Combine(
        projectRoot, "src", "DalamudActCompat", "Plugin", "PluginConfiguration.cs"));
    var hostSupervisorSource = File.ReadAllText(Path.Combine(
        projectRoot,
        "src",
        "DalamudActCompat",
        "Infrastructure",
        "Processes",
        "ActHostSupervisor.cs"));
    var matchaNotifierSource = File.ReadAllText(Path.Combine(
        projectRoot, "src", "DalamudActCompat.Host", "MatchaWindowsNotifier.cs"));
    var silverNotifierSource = File.ReadAllText(Path.Combine(
        projectRoot, "src", "DalamudActCompat.Host", "SilverDasherWindowsNotifier.cs"));
    var notificationCenterSource = File.ReadAllText(Path.Combine(
        projectRoot, "src", "DalamudActCompat.Host", "WindowsNotificationCenter.cs"));
    Assert(
        controlCenterSource.Contains("text.Get(\"主页\", \"Home\")", StringComparison.Ordinal) &&
        controlCenterSource.Contains("(Page.Diagnostics, text.Get(\"设置&账号\", \"Settings & Account\"))", StringComparison.Ordinal) &&
        controlCenterSource.Contains("account-authentication-gate", StringComparison.Ordinal) &&
        controlCenterSource.Contains("account-authentication-hero", StringComparison.Ordinal) &&
        controlCenterSource.Contains("account-authentication-card", StringComparison.Ordinal) &&
        controlCenterSource.Contains("stackAuthenticationLayout", StringComparison.Ordinal) &&
        controlCenterSource.Contains("account-authentication-navigation", StringComparison.Ordinal) &&
        controlCenterSource.Contains("DrawAuthenticationGate(cloudSnapshot);", StringComparison.Ordinal) &&
        controlCenterSource.Contains("private void DrawAccountSettings()", StringComparison.Ordinal) &&
        controlCenterSource.Contains("记住登录状态（下次自动登录）", StringComparison.Ordinal) &&
        Regex.Matches(controlCenterSource, "ref cloudRememberLogin\\);").Count == 1 &&
        !controlCenterSource.Contains("重置后自动登录", StringComparison.Ordinal) &&
        controlCenterSource.Contains("恢复密钥自助改密", StringComparison.Ordinal) &&
        controlCenterSource.Contains("管理员一次性重置码（必填）", StringComparison.Ordinal) &&
        controlCenterSource.Contains("恢复密钥（必填）", StringComparison.Ordinal) &&
        controlCenterSource.Contains("将自动用于改密和解锁原有云备份", StringComparison.Ordinal) &&
        controlCenterSource.Contains("复制诊断日志", StringComparison.Ordinal) &&
        controlCenterSource.Contains("打开 FFLogs 上传日志", StringComparison.Ordinal) &&
        controlCenterSource.Contains("ImGui.SetClipboardText(directory);", StringComparison.Ordinal) &&
        controlCenterSource.Contains("DrawCombatLogDirectorySettings();", StringComparison.Ordinal) &&
        controlCenterSource.Contains("text.Get(\"当前路径\", \"Current path\")", StringComparison.Ordinal) &&
        controlCenterSource.Contains("text.Get(\"更改目录...\", \"Change directory...\")", StringComparison.Ordinal) &&
        controlCenterSource.Contains("最多保留最近 2 个不同内容的密文版本", StringComparison.Ordinal) &&
        !controlCenterSource.Contains("最多保留最近 10 个密文版本", StringComparison.Ordinal) &&
        controlCenterSource.Contains("Up to 2 encrypted versions with different content are retained", StringComparison.Ordinal) &&
        !controlCenterSource.Contains("The latest 10 encrypted versions are retained", StringComparison.Ordinal) &&
        controlCenterSource.Contains("打开游戏后自动同步配置", StringComparison.Ordinal) &&
        controlCenterSource.Contains("每次打开游戏并登录后自动检查一次", StringComparison.Ordinal) &&
        controlCenterSource.Contains("配置没变化就不上传", StringComparison.Ordinal) &&
        controlCenterSource.Contains("最终更改会在下次打开游戏后同步", StringComparison.Ordinal) &&
        controlCenterSource.Contains("cloud-summary-card", StringComparison.Ordinal) &&
        controlCenterSource.Contains("cloud-backup-card", StringComparison.Ordinal) &&
        controlCenterSource.Contains("cloud-invitation-support-card", StringComparison.Ordinal) &&
        controlCenterSource.Contains("CloudQuickPopupId", StringComparison.Ordinal) &&
        controlCenterSource.Contains("statusAction: () => cloudQuickPopupRequested = true", StringComparison.Ordinal) &&
        controlCenterSource.Contains("查看云同步状态", StringComparison.Ordinal) &&
        controlCenterSource.Contains("支持者可以联系管理员，申请增加超过默认 3 个的好友邀请名额", StringComparison.Ordinal) &&
        controlCenterSource.Contains("支持不会解除封禁、跳过风控或改变功能权限", StringComparison.Ordinal) &&
        !controlCenterSource.Contains("客户端只上传不可逆的 SHA-256 设备指纹", StringComparison.Ordinal) &&
        configurationSource.Contains("AutoCloudSyncEnabled { get; set; } = true", StringComparison.Ordinal) &&
        pluginSource.Contains("ScheduleCloudStartupSync(DateTimeOffset.UtcNow)", StringComparison.Ordinal) &&
        pluginSource.Contains("cloudStartupSyncRequested", StringComparison.Ordinal) &&
        !pluginSource.Contains("FileSystemWatcher", StringComparison.Ordinal) &&
        pluginSource.Contains("TryStartCloudAutoSync(now);", StringComparison.Ordinal) &&
        pluginSource.Contains("if (!TrySaveConfiguration())", StringComparison.Ordinal) &&
        pluginSource.Contains("ConditionFlag.InCombat", StringComparison.Ordinal) &&
        pluginSource.Contains("AutoUploadIfChangedAsync", StringComparison.Ordinal) &&
        controlCenterSource.Contains("恢复出厂设置...", StringComparison.Ordinal) &&
        typeof(ControlCenterWindow).GetConstructors().Single().GetParameters().Any(parameter =>
            parameter.Name == "factoryReset" && parameter.ParameterType == typeof(Func<Task<string>>)) &&
        typeof(ControlCenterWindow).GetConstructors().Single().GetParameters().Any(parameter =>
            parameter.Name == "buildDiagnosticReport" && parameter.ParameterType == typeof(Func<string>)) &&
        typeof(ControlCenterWindow).GetConstructors().Single().GetParameters().Any(parameter =>
            parameter.Name == "openCombatLogDirectory" && parameter.ParameterType == typeof(Func<string>)),
        "The login gate, reset form, account page, automatic cloud sync, two-version limit, recovery guard, diagnostic copy, or upload-log shortcut is missing.");
    Assert(
        controlCenterSource.Contains("FontAwesomeIcon.Download", StringComparison.Ordinal) &&
        controlCenterSource.Contains("FontAwesomeIcon.Times", StringComparison.Ordinal) &&
        controlCenterSource.Contains("下载中...", StringComparison.Ordinal) &&
        controlCenterSource.Contains("不可用", StringComparison.Ordinal) &&
        coreResourceWindowSource.Contains("ImGui.ProgressBar", StringComparison.Ordinal) &&
        coreResourceWindowSource.Contains("是否下载核心组件？", StringComparison.Ordinal) &&
        pluginSource.Contains("coreResourceDownloadWindow.Open", StringComparison.Ordinal),
        "Resource failure recovery no longer exposes unavailable, download, progress, cancel, and core retry UI states.");
    Assert(
        !controlCenterSource.Contains("QQ 群：582145824", StringComparison.Ordinal) &&
        !settingsSource.Contains("QQ 群：582145824", StringComparison.Ordinal) &&
        thirdPartySource.Contains(
            "DrawMetadata(text.Get(\"当前维护者\", \"Current maintainer\"), plugin.Maintainer);",
            StringComparison.Ordinal),
        "SilverDasher support information leaked into its introduction or disappeared from the third-party extension card.");
    Assert(
        controlCenterSource.Contains("out bool hostConfigurationChanged", StringComparison.Ordinal) &&
        controlCenterSource.Contains("hostConfigurationChanged |= extensionChanged", StringComparison.Ordinal) &&
        settingsSource.Contains("hostConfigurationChanged = true;", StringComparison.Ordinal) &&
        settingsSource.Contains(
            "permissionsChanged || hostConfigurationChanged",
            StringComparison.Ordinal),
        "Compatibility-extension enable/disable changes no longer restart the isolated Host immediately.");
    Assert(
        matchaNotifierSource.Contains("isGameForeground()", StringComparison.Ordinal) &&
        silverNotifierSource.Contains("isGameForeground()", StringComparison.Ordinal) &&
        notificationCenterSource.Contains("ToastContentBuilder", StringComparison.Ordinal) &&
        !matchaNotifierSource.Contains("ShowBalloonTip", StringComparison.Ordinal) &&
        !silverNotifierSource.Contains("ShowBalloonTip", StringComparison.Ordinal),
        "Matcha or SilverDasher notification routing regressed to a transient shell balloon or stopped checking game focus.");
    Assert(
        controlCenterSource.Contains("GenericPermissionPopupId", StringComparison.Ordinal) &&
        controlCenterSource.Contains("DrawGenericDeleteModal", StringComparison.Ordinal) &&
        pluginSource.Contains("QueuePluginInstallFeedback", StringComparison.Ordinal) &&
        pluginSource.Contains("InjectExternalPluginLogLine(logLine.Line)", StringComparison.Ordinal) &&
        Regex.Matches(pluginSource, Regex.Escape("ActPluginPermissions.Remove")).Count >= 3 &&
        hostSupervisorSource.Contains("WaitForPluginStageAsync", StringComparison.Ordinal),
        "Generic plugin consent/uninstall feedback, exact permission replacement, startup-stage waiting, or Matcha overlay log relay is missing.");
    var navigationIndex = controlCenterSource.IndexOf(
        "DrawPageTabs();",
        StringComparison.Ordinal);
    var scrollContentIndex = controlCenterSource.IndexOf(
        "control-center-page-content",
        StringComparison.Ordinal);
    Assert(
        navigationIndex >= 0 &&
        scrollContentIndex > navigationIndex &&
        controlCenterSource.Contains("overview-parser-card", StringComparison.Ordinal) &&
        controlCenterSource.Contains("overview-quick-actions-card", StringComparison.Ordinal) &&
        controlCenterSource.Contains("overview-general-card", StringComparison.Ordinal) &&
        controlCenterSource.Contains("关闭运行状态", StringComparison.Ordinal),
        "The fixed navigation, gold overview cards, or runtime-status toggle is missing.");
    var hostResourceMethodStart = pluginSource.IndexOf(
        "private async Task InitializeHostResourcesAsync",
        StringComparison.Ordinal);
    var resourceResolverMethodStart = pluginSource.IndexOf(
        "private async Task<string> ResolveResourcePackWithTimeoutAsync",
        hostResourceMethodStart,
        StringComparison.Ordinal);
    var hostResourceMethod = pluginSource[hostResourceMethodStart..resourceResolverMethodStart];
    var constructorSourceForAuthentication = pluginSource[..pluginSource.IndexOf(
        "public string Name",
        StringComparison.Ordinal)];
    Assert(
        constructorSourceForAuthentication.Contains("StartInitialResourcePreparation();", StringComparison.Ordinal) &&
        !constructorSourceForAuthentication.Contains("lifecycle.Start();", StringComparison.Ordinal) &&
        pluginSource.Contains("if (!cloudClient.Snapshot.IsSignedIn)", StringComparison.Ordinal) &&
        pluginSource.Contains("if (!IsDactAccessAllowed())", StringComparison.Ordinal) &&
        !hostResourceMethod.Contains("StartIndependentHostAsync", StringComparison.Ordinal),
        "Authentication no longer gates DACT runtime startup, or core resource download still starts a Host before login.");
    Assert(
        historySource.Contains("BrandedWindowChrome.Draw", StringComparison.Ordinal) &&
        historySource.Contains("combat-history-navigation", StringComparison.Ordinal) &&
        historySource.Contains("FflogsEstimateService.GetPersistedEstimate", StringComparison.Ordinal) &&
        historySource.Contains("FFLogs {", StringComparison.Ordinal) &&
        historySource.Contains("expandedRecentFolderIds", StringComparison.Ordinal) &&
        historySource.Contains("FindRecentEncounter", StringComparison.Ordinal) &&
        historySource.Contains("本次副本包含", StringComparison.Ordinal) &&
        historySource.Contains("ImGuiStyleVar.WindowRounding", StringComparison.Ordinal) &&
        historySource.Contains("ImGuiWindowFlags.NoTitleBar", StringComparison.Ordinal),
        "Combat History lost its branded frame, navigation rail, or saved FFLogs display.");
    var chromeSource = File.ReadAllText(Path.Combine(
        projectRoot, "src", "DalamudActCompat", "UI", "BrandedWindowChrome.cs"));
    Assert(
        controlCenterSource.Contains("control-center-navigation", StringComparison.Ordinal) &&
        chromeSource.Contains("DrawNavigationRail", StringComparison.Ordinal) &&
        chromeSource.Contains("NavigationIndicatorPositions", StringComparison.Ordinal) &&
        chromeSource.Contains("AdvanceNavigationIndicator", StringComparison.Ordinal),
        "Page-level navigation does not share the animated navigation rail design.");
    var animatedIndicator = BrandedWindowChrome.AdvanceNavigationIndicator(0, 1, 1f / 60f);
    Assert(
        animatedIndicator > 0 && animatedIndicator < 1 &&
        BrandedWindowChrome.AdvanceNavigationIndicator(0.9999f, 1, 1f / 60f) == 1,
        "The navigation indicator no longer eases toward its selected page.");
    var historyNavigationIndex = historySource.IndexOf(
        "combat-history-navigation",
        StringComparison.Ordinal);
    var historyContentIndex = historySource.IndexOf(
        "combat-history-page-content",
        StringComparison.Ordinal);
    Assert(
        historyNavigationIndex >= 0 && historyContentIndex > historyNavigationIndex,
        "Combat History navigation is no longer fixed above its scrolling page content.");
    var permissionChoiceIndex = thirdPartySource.IndexOf(
        "private void DrawPermissionChoiceModal",
        StringComparison.Ordinal);
    var ttsChoiceIndex = thirdPartySource.IndexOf(
        "private void DrawTtsProChoiceModal",
        StringComparison.Ordinal);
    var updateStatusIndex = thirdPartySource.IndexOf(
        "private void DrawUpdateStatusBanner",
        StringComparison.Ordinal);
    Assert(
        permissionChoiceIndex >= 0 &&
        ttsChoiceIndex > permissionChoiceIndex &&
        updateStatusIndex > ttsChoiceIndex,
        "The third-party permission or TTS prompt method could not be located.");
    var permissionChoiceMethod = thirdPartySource[permissionChoiceIndex..ttsChoiceIndex];
    var ttsChoiceMethod = thirdPartySource[ttsChoiceIndex..updateStatusIndex];
    Assert(
        thirdPartySource.Contains("Size = new Vector2(1200, 650);", StringComparison.Ordinal) &&
        thirdPartySource.Contains("\"third-party-plugin-cards\"", StringComparison.Ordinal) &&
        thirdPartySource.Contains("ImGuiTableFlags.SizingStretchSame", StringComparison.Ordinal) &&
        thirdPartySource.Contains("BrandedWindowChrome.Draw", StringComparison.Ordinal) &&
        thirdPartySource.Contains("DrawPermissionChoiceModal", StringComparison.Ordinal) &&
        thirdPartySource.Contains("DrawTtsProChoiceModal", StringComparison.Ordinal) &&
        thirdPartySource.Contains("ImGui.BeginPopupModal", StringComparison.Ordinal) &&
        thirdPartySource.Contains("不同意完整权限，保持安全模式", StringComparison.Ordinal) &&
        thirdPartySource.Contains("是否将 FoxTTS 改为 Cafe TTS Pro？", StringComparison.Ordinal) &&
        thirdPartySource.Contains("本次不更改", StringComparison.Ordinal) &&
        thirdPartySource.Contains("不再提醒", StringComparison.Ordinal) &&
        permissionChoiceMethod.Contains("ImGui.BeginPopupModal", StringComparison.Ordinal) &&
        !permissionChoiceMethod.Contains("ref ", StringComparison.Ordinal) &&
        ttsChoiceMethod.Contains("ref ttsPopupOpen", StringComparison.Ordinal) &&
        thirdPartySource.Contains("showCloseButton: ShouldShowCloseButton(", StringComparison.Ordinal) &&
        thirdPartySource.Contains("未确认的扩展保持禁用", StringComparison.Ordinal) &&
        thirdPartySource.Contains("BeginPermissionChoice();", StringComparison.Ordinal) &&
        thirdPartySource.Contains("shouldPromptForPermissions()", StringComparison.Ordinal) &&
        pluginSource.Contains("HasExplicitActCapabilityDecision", StringComparison.Ordinal) &&
        pluginSource.Contains("installCommitted = true;", StringComparison.Ordinal) &&
        pluginSource.Contains("BundledPluginInstallOutcome.RuntimeRecoveryPending", StringComparison.Ordinal) &&
        thirdPartySource.Contains("third-party-update-status", StringComparison.Ordinal) &&
        pluginSource.Contains("thirdPartyPluginNoticeWindow.OpenManualDisclosure", StringComparison.Ordinal) &&
        pluginSource.Contains("OpenRequiredAfterPluginUpdateWhenPending();", StringComparison.Ordinal) &&
        pluginSource.Contains("BeginUpdateCheck(userInitiated: true)", StringComparison.Ordinal) &&
        pluginSource.Contains("BeginUpdateCheck(userInitiated: false)", StringComparison.Ordinal) &&
        pluginSource.Contains("userInitiated: openWindow);", StringComparison.Ordinal) &&
        pluginSource.Contains("更新检查已经在进行中", StringComparison.Ordinal) &&
        pluginSource.Contains("services.NotificationManager.AddNotification", StringComparison.Ordinal) &&
        pluginSource.Contains("MatchaWorldChangedIconId = 61835", StringComparison.Ordinal) &&
        pluginSource.Contains("MatchaDutyEnteredIconId = 61832", StringComparison.Ordinal) &&
        pluginSource.Contains("gameNotification.IconTexture = notification.Kind switch", StringComparison.Ordinal),
        "The update notice is not a landscape branded window with a top modal and visible manual-check feedback.");
    var installBundledPluginsIndex = pluginSource.IndexOf(
        "private async Task<BundledPluginInstallOutcome> InstallBundledPluginsAsync",
        StringComparison.Ordinal);
    var startBundledUpdateCheckIndex = pluginSource.IndexOf(
        "private void StartBundledPluginUpdateCheck",
        installBundledPluginsIndex,
        StringComparison.Ordinal);
    var bundledInstallMethod = pluginSource[
        installBundledPluginsIndex..startBundledUpdateCheckIndex];
    var configurationMigrationIndex = pluginSource.IndexOf(
        "configuration.ApplyMigrations();",
        StringComparison.Ordinal);
    var lifecycleStartIndex = pluginSource.IndexOf(
        "lifecycle.Start();",
        StringComparison.Ordinal);
    Assert(
        configurationSource.Contains("public bool EnableParsing { get; set; } = true;", StringComparison.Ordinal) &&
        configurationSource.Contains("public bool AutoStartParser { get; set; } = true;", StringComparison.Ordinal) &&
        configurationMigrationIndex >= 0 &&
        lifecycleStartIndex > configurationMigrationIndex &&
        !bundledInstallMethod.Contains("configuration.EnableParsing =", StringComparison.Ordinal) &&
        !bundledInstallMethod.Contains("configuration.AutoStartParser =", StringComparison.Ordinal),
        "Parser startup remains coupled to third-party extension acknowledgement.");
    var buildBundledUpdateMessageIndex = pluginSource.IndexOf(
        "private static string BuildBundledPluginUpdateMessage",
        startBundledUpdateCheckIndex,
        StringComparison.Ordinal);
    var bundledUpdateCheckMethod = pluginSource[
        startBundledUpdateCheckIndex..buildBundledUpdateMessageIndex];
    Assert(
        !bundledUpdateCheckMethod.Contains("parserEngine.StopAsync", StringComparison.Ordinal) &&
        !bundledUpdateCheckMethod.Contains("hostSupervisor.StopAsync", StringComparison.Ordinal) &&
        bundledInstallMethod.Contains("BundledPluginInstallCoordinator.ExecuteAsync", StringComparison.Ordinal),
        "Checking for bundled DLL updates still pauses ACT services before installation begins.");
    var pluginConstructorEnd = pluginSource.IndexOf(
        "public string Name",
        StringComparison.Ordinal);
    var pluginConstructor = pluginSource[..pluginConstructorEnd];
    var lifecycleStartForCactbot = pluginSource.IndexOf(
        "lifecycle.Start();",
        StringComparison.Ordinal);
    var cactbotInitializationStart = pluginSource.IndexOf(
        "StartBundledCactbotInitialization",
        StringComparison.Ordinal);
    Assert(
        lifecycleStartForCactbot >= 0 &&
        cactbotInitializationStart > lifecycleStartForCactbot &&
        !pluginConstructor.Contains("EnsureCurrentAsync(timeout.Token).GetAwaiter()", StringComparison.Ordinal) &&
        pluginSource.Contains("ShutdownCactbotOperationsAsync", StringComparison.Ordinal) &&
        controlCenterSource.Contains("CactbotOperationState.Checking", StringComparison.Ordinal) &&
        controlCenterSource.Contains("CactbotOperationState.Installing", StringComparison.Ordinal),
        "Cactbot validation still blocks plugin startup or lacks tracked status/shutdown handling.");
    Assert(
        statusSource.Contains("BrandedWindowChrome.Draw", StringComparison.Ordinal) &&
        statusSource.Contains("runtime-status-content", StringComparison.Ordinal) &&
        statusSource.Contains("BeginGoldCard", StringComparison.Ordinal) &&
        statusSource.Contains("ImGuiWindowFlags.NoTitleBar", StringComparison.Ordinal),
        "The ACT compatibility status window does not use the new rounded branded card design.");
    var configurePermissionsIndex = pluginSource.IndexOf(
        "private void ConfigureBundledPluginPermissions",
        StringComparison.Ordinal);
    var permissionSaveIndex = pluginSource.IndexOf(
        "SaveConfiguration();",
        configurePermissionsIndex,
        StringComparison.Ordinal);
    var completeBundledSetupIndex = pluginSource.IndexOf(
        "private void CompleteBundledPluginSetup",
        StringComparison.Ordinal);
    var finalPermissionRestartIndex = pluginSource.IndexOf(
        "ApplyActPermissionChanges(",
        completeBundledSetupIndex,
        StringComparison.Ordinal);
    var permissionRefreshMethodIndex = pluginSource.IndexOf(
        "private void ApplyActPermissionChanges(",
        StringComparison.Ordinal);
    var stopHostBeforeFoxTtsIndex = pluginSource.IndexOf(
        "await hostSupervisor.StopAsync(timeout.Token)",
        permissionRefreshMethodIndex,
        StringComparison.Ordinal);
    var setFoxTtsProIndex = pluginSource.IndexOf(
        "FoxTtsConfigurationDefaults.SetPro(paths.ConfigDirectory);",
        permissionRefreshMethodIndex,
        StringComparison.Ordinal);
    var startHostAfterFoxTtsIndex = pluginSource.IndexOf(
        "await hostSupervisor.StartAsync(timeout.Token)",
        setFoxTtsProIndex,
        StringComparison.Ordinal);
    Assert(
        typeof(ActHostSupervisor).GetMethod(nameof(ActHostSupervisor.RestartAsync))?.ReturnType ==
        typeof(Task<bool>) &&
        Regex.Matches(
            controlCenterSource,
            Regex.Escape("applyPermissionChanges();"),
            RegexOptions.CultureInvariant).Count == 1 &&
        Regex.Matches(
            settingsSource,
            Regex.Escape("applyPermissionChanges();"),
            RegexOptions.CultureInvariant).Count == 1 &&
        controlCenterSource.Contains(
            "changed |= hostConfigurationChanged;",
            StringComparison.Ordinal) &&
        settingsSource.Contains(
            "changed |= permissionsChanged;",
            StringComparison.Ordinal) &&
        configurePermissionsIndex >= 0 &&
        permissionSaveIndex > configurePermissionsIndex &&
        completeBundledSetupIndex > permissionSaveIndex &&
        finalPermissionRestartIndex > completeBundledSetupIndex &&
        Regex.Matches(
            pluginSource,
            @"ApplyActPermissionChanges\(choice == FoxTtsProChoice\.EnablePro\);",
            RegexOptions.CultureInvariant).Count == 1 &&
        !pluginSource.Contains("autoOpenSilverDasherAfterSetup", StringComparison.Ordinal) &&
        !pluginSource.Contains("OpenPluginConfigurationAfterRestartAsync", StringComparison.Ordinal) &&
        permissionRefreshMethodIndex > finalPermissionRestartIndex &&
        stopHostBeforeFoxTtsIndex > permissionRefreshMethodIndex &&
        setFoxTtsProIndex > stopHostBeforeFoxTtsIndex &&
        startHostAfterFoxTtsIndex > setFoxTtsProIndex &&
        hostSupervisorSource.Contains(
            "public async Task<bool> RestartAsync",
            StringComparison.Ordinal),
        "ACT permissions are not saved before the final Host refresh, or FoxTTS Pro is not written between Host stop and start.");

    var cloudBanStopIndex = pluginSource.IndexOf(
        "private async Task StopDactForCloudBanAsync()",
        StringComparison.Ordinal);
    var ordinaryStopIndex = pluginSource.IndexOf(
        "private async Task StopDactComponentsAsync",
        cloudBanStopIndex,
        StringComparison.Ordinal);
    var cloudBanStopMethod = pluginSource[cloudBanStopIndex..ordinaryStopIndex];
    var startupBanIndex = constructorSourceForAuthentication.IndexOf(
        "OnCloudBanReceived(startupBan);",
        StringComparison.Ordinal);
    var cloudInitializeIndex = constructorSourceForAuthentication.IndexOf(
        "StartCloudOperation(cloudClient.InitializeAsync);",
        StringComparison.Ordinal);
    Assert(
        cloudBanStopMethod.Contains("RequestCloudBanParserStopAsync()", StringComparison.Ordinal) &&
        cloudBanStopMethod.Contains("StopDactHostsAsync", StringComparison.Ordinal) &&
        !cloudBanStopMethod.Contains("parserEngine.StopAsync", StringComparison.Ordinal) &&
        pluginSource.Contains(
            "TryStopParserForCloudBanOnFrameworkThread();",
            StringComparison.Ordinal) &&
        parserAdapterSource.Contains("framework.IsInFrameworkUpdateThread", StringComparison.Ordinal) &&
        parserAdapterSource.Contains("lifecycleLock.Wait(0)", StringComparison.Ordinal),
        "Live-ban enforcement can still unload game hooks from the cloud SSE worker instead of a framework frame.");
    Assert(
        pluginSource.Contains("Volatile.Read(ref pluginInitialized) == 0", StringComparison.Ordinal) &&
        constructorSourceForAuthentication.Contains("Volatile.Write(ref pluginInitialized, 1);", StringComparison.Ordinal) &&
        startupBanIndex >= 0 &&
        cloudInitializeIndex > startupBanIndex &&
        constructorSourceForAuthentication.Contains("if (cloudClient.ActiveBan is null)", StringComparison.Ordinal) &&
        pluginSource.Contains("cloudBanNoticeWindow.Show(notice, lifted: false);", StringComparison.Ordinal) &&
        pluginSource.Contains("requiresRestart: requiresRestart", StringComparison.Ordinal) &&
        pluginSource.Contains("本次登录已经生效", StringComparison.Ordinal) &&
        cloudBanNoticeSource.Contains("BrandedWindowChrome.Draw", StringComparison.Ordinal) &&
        cloudBanNoticeSource.Contains("ImGuiWindowFlags.NoNavInputs", StringComparison.Ordinal) &&
        cloudBanNoticeSource.Contains("ImGuiWindowFlags.NoNavFocus", StringComparison.Ordinal) &&
        cloudBanNoticeSource.Contains("text.Get(\"确认\", \"Confirm\")", StringComparison.Ordinal) &&
        cloudBanNoticeSource.Contains("账号已解封", StringComparison.Ordinal) &&
        cloudBanNoticeSource.Contains("您的账号及关联机器已经被封禁", StringComparison.Ordinal) &&
        cloudBanNoticeSource.Contains("附带的账号封禁已经解除", StringComparison.Ordinal) &&
        !cloudBanNoticeSource.Contains("BeginPopupModal", StringComparison.Ordinal) &&
        !cloudBanNoticeSource.Contains("OpenPopup", StringComparison.Ordinal) &&
        !cloudBanNoticeSource.Contains("OverlayEditShield", StringComparison.Ordinal),
        "Ban/unban notification is modal, unbranded, dismisses game input, or can lose a construction-time server unban.");

    var configurationPathPattern = new Regex(
        @"configuration(?:\.[A-Za-z_][A-Za-z0-9_]*)+",
        RegexOptions.CultureInvariant);
    var legacyConfigurationPaths = configurationPathPattern
        .Matches(settingsSource)
        .Select(match => match.Value)
        .ToHashSet(StringComparer.Ordinal);
    var controlCenterConfigurationPaths = configurationPathPattern
        .Matches(controlCenterSource)
        .Select(match => match.Value)
        .ToHashSet(StringComparer.Ordinal);
    var missingLegacyPaths = legacyConfigurationPaths
        .Except(controlCenterConfigurationPaths, StringComparer.Ordinal)
        // The metric selector was intentionally retired once DPS/rDPS/HPS became
        // independent columns; retaining it in the parser model keeps old JSON readable.
        .Where(static path => path != "configuration.Meter.DpsMetric")
        .Order(StringComparer.Ordinal)
        .ToArray();
    Assert(
        missingLegacyPaths.Length == 0,
        $"The new control center lost legacy setting paths: {string.Join(", ", missingLegacyPaths)}");
}

static void ValidateInstalledPluginVersionDisplay(string testRoot)
{
    var paths = new PluginPaths(Path.Combine(testRoot, "detected-plugin-version"));
    var installDirectory = Path.Combine(paths.ActPluginDirectory, "postnamazu");
    Directory.CreateDirectory(installDirectory);
    var entryAssembly = Path.Combine(installDirectory, "PostNamazu.dll");
    File.Copy(typeof(ActPluginPackageInstaller).Assembly.Location, entryAssembly);
    var detectedVersion = FileVersionInfo.GetVersionInfo(entryAssembly).FileVersion
                          ?? throw new InvalidOperationException(
                              "The version detection fixture has no FileVersion metadata.");
    var manifest = new ActPluginManifest
    {
        Id = "postnamazu",
        Name = "PostNamazu",
        Version = "1.3.6.6",
        SourceSha256 = "fixture",
        EntryAssembly = "PostNamazu.dll",
        EntryType = "PostNamazu.PostNamazu",
        HostApiVersion = 1,
    };
    File.WriteAllText(
        Path.Combine(installDirectory, ActPluginManifest.FileName),
        JsonSerializer.Serialize(manifest));

    var installed = new ActPluginPackageInstaller(paths)
        .Discover(new HashSet<string>(StringComparer.OrdinalIgnoreCase))
        .Single();
    Assert(
        installed.Manifest.Version == "1.3.6.6" &&
        installed.DetectedVersion == detectedVersion &&
        installed.DisplayVersion == detectedVersion &&
        installed.HasVersionMismatch,
        "The Extensions page did not prefer the actual DLL version while retaining manifest metadata.");
}

static void ValidateChinese755hOpcodes()
{
    OpcodeManager.Instance.SetRegion(GameRegion.Chinese);
    var opcodes = OpcodeManager.Instance.CurrentOpcodes;
    var expected = new Dictionary<string, ushort>
    {
        ["Ability1"] = 0x0296,
        ["Ability8"] = 0x0164,
        ["Ability16"] = 0x01B1,
        ["Ability24"] = 0x039B,
        ["Ability32"] = 0x0372,
        ["ActorCast"] = 0x018C,
        ["EffectResult"] = 0x02F5,
        ["EffectResultBasic"] = 0x03A3,
        ["ActorControl"] = 0x01DA,
        ["ActorControlSelf"] = 0x035D,
        ["ActorControlTarget"] = 0x013C,
        ["StatusEffectList"] = 0x01F1,
        ["StatusEffectList2"] = 0x009B,
        ["StatusEffectList3"] = 0x0153,
        ["BossStatusEffectList"] = 0x0320,
        ["StatusEffectListForay3"] = 0x00DC,
        ["PlayerSpawn"] = 0x0398,
        ["NpcSpawn"] = 0x006F,
        ["NpcSpawn2"] = 0x0287,
        ["ActorMove"] = 0x038D,
        ["ActorSetPos"] = 0x03DF,
        ["ActorGauge"] = 0x0221,
        ["PresetWaymark"] = 0x0149,
        ["Waymark"] = 0x0171,
        ["SystemLogMessage"] = 0x01E7,
    };

    foreach (var pair in expected)
    {
        Assert(
            opcodes.TryGetValue(pair.Key, out var actual) && actual == pair.Value,
            $"Chinese 7.55h opcode {pair.Key} was {actual:X}, expected {pair.Value:X}.");
    }
}

static void ValidatePostNamazuRawLogCompatibility()
{
    var method = typeof(FormActMain).GetMethod(
        "ParseRawLogLine",
        BindingFlags.Instance | BindingFlags.Public,
        binder: null,
        [typeof(bool), typeof(DateTime), typeof(string)],
        modifiers: null);
    Assert(
        method is not null && method.ReturnType == typeof(void),
        "The ACT three-argument ParseRawLogLine ABI required by PostNamazu is missing.");
}

static void ValidatePostNamazuOverlayHandlerResponse()
{
    using var container = new RainbowMage.OverlayPlugin.TinyIoCContainer();
    container.Register<RainbowMage.OverlayPlugin.ILogger>(new OverlayServerTestLogger());

    var dispatcherType = typeof(RainbowMage.OverlayPlugin.PluginMain).Assembly.GetType(
                             "RainbowMage.OverlayPlugin.EventDispatcher",
                             throwOnError: true)!
                         ?? throw new InvalidOperationException(
                             "OverlayPlugin EventDispatcher was not found.");
    var dispatcher = Activator.CreateInstance(dispatcherType, container)
                     ?? throw new InvalidOperationException(
                         "OverlayPlugin EventDispatcher could not be constructed.");
    container.Register(dispatcherType, dispatcher);

    using var eventSource = new PostNamazuEventSource(container);
    string? actualCommand = null;
    string? actualPayload = null;
    eventSource.SetAction((command, payload) =>
    {
        actualCommand = command;
        actualPayload = payload;
    });

    // Exercise the real dispatcher contract; invoking HandleAction directly would miss the
    // object-or-null validation that rejected successful PostNamazu calls in v0.3.9.23.
    var response = dispatcherType.GetMethod(
                       "CallHandler",
                       BindingFlags.Instance | BindingFlags.Public)!
                   .Invoke(
                       dispatcher,
                       [new JObject
                       {
                           ["call"] = "PostNamazu",
                           ["c"] = "command",
                           ["p"] = "/echo ACTCOMPAT_POSTNAMAZU",
                       }]);
    Assert(
        response is JObject &&
        actualCommand == "command" &&
        actualPayload == "/echo ACTCOMPAT_POSTNAMAZU",
        "PostNamazu OverlayPlugin handler did not return an object after dispatching its action.");
}

static void ValidateParserDependencyVersions()
{
    Assert(
        typeof(IINACT.Plugin).Assembly.GetName().Version == new Version(2, 10, 3, 6),
        "IINACT is not at 2.10.3.6.");
    Assert(
        typeof(FFXIVMemory).Assembly.GetName().Version == new Version(0, 19, 105, 0),
        "OverlayPlugin Core is not at 0.19.105.");

    var runtimeDirectory = Path.Combine(
        FindProjectRoot(),
        "src",
        "DalamudActCompat",
        "bin",
        "Release");
    AssertFileVersion(
        Path.Combine(runtimeDirectory, "Unscrambler.dll"),
        "7.55.2.0",
        "Unscrambler.XIV");
    AssertFileVersion(
        Path.Combine(runtimeDirectory, "FFXIV_ACT_Plugin.dll"),
        "3.0.2.8",
        "FFXIV_ACT_Plugin");
    var logfileAssemblyPath = Path.Combine(runtimeDirectory, "FFXIV_ACT_Plugin.Logfile.dll");
    Assert(
        FetchDependencies.LogFormatIdentity.Matches(
            logfileAssemblyPath,
            new Version(2, 10, 3, 6)),
        $"FFXIV_ACT_Plugin.Logfile identifies a stale IINACT version: {FetchDependencies.LogFormatIdentity.ReadTemplate(logfileAssemblyPath)}");

    var overlayAssembly = typeof(FFXIVMemory).Assembly;
    var opcodeResource = overlayAssembly
        .GetManifestResourceNames()
        .Single(name => name.EndsWith("opcodes.jsonc", StringComparison.OrdinalIgnoreCase));
    using var opcodeStream = overlayAssembly.GetManifestResourceStream(opcodeResource)
                             ?? throw new InvalidOperationException(
                                 "OverlayPlugin opcode resource was not found.");
    using var document = JsonDocument.Parse(
        opcodeStream,
        new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });
    var chinese755h = document.RootElement
        .GetProperty("Chinese")
        .GetProperty("2026.08.05.0000.0000");
    var global755h2 = document.RootElement
        .GetProperty("Global")
        .GetProperty("2026.08.11.0000.0000");
    Assert(
        chinese755h.GetProperty("MapEffect").GetProperty("opcode").GetInt32() == 188 &&
        chinese755h.GetProperty("RSVData").GetProperty("opcode").GetInt32() == 979 &&
        chinese755h.GetProperty("Countdown").GetProperty("opcode").GetInt32() == 802 &&
        chinese755h.GetProperty("ActorMove").GetProperty("opcode").GetInt32() == 909 &&
        chinese755h.GetProperty("ActorSetPos").GetProperty("opcode").GetInt32() == 991,
        "OverlayPlugin Chinese 7.55h opcodes are stale.");
    Assert(
        global755h2.GetProperty("MapEffect").GetProperty("opcode").GetInt32() == 135 &&
        global755h2.GetProperty("RSVData").GetProperty("opcode").GetInt32() == 273 &&
        global755h2.GetProperty("Countdown").GetProperty("opcode").GetInt32() == 120 &&
        global755h2.GetProperty("ActorMove").GetProperty("opcode").GetInt32() == 572 &&
        global755h2.GetProperty("ActorSetPos").GetProperty("opcode").GetInt32() == 301,
        "OverlayPlugin Global 7.55h2 opcodes are stale.");
}

static void ValidateSilverDasherPermissionIsolation()
{
    string[] existingPermissionGroup =
        BundledActPluginCapabilities.All.Select(entry => entry.PluginId).ToArray();
    Assert(
        existingPermissionGroup.SequenceEqual(
            new[] { "act.foxtts", "postnamazu", "triggernometry" },
            StringComparer.Ordinal),
        "SilverDasher changed the existing FoxTTS/PostNamazu/Triggernometry permission workflow.");
    Assert(
        BundledActPluginCapabilities.SilverDasher.Contains(ActCapability.ReadCombatLogs) &&
        BundledActPluginCapabilities.SilverDasher.Contains(ActCapability.NativeGameMemory),
        "SilverDasher does not expose its independent event and memory capability declarations.");
    Assert(
        BundledActPluginCapabilities.FullPermissionConfirmation
            .Select(entry => entry.PluginId)
            .SequenceEqual(
                new[] { "act.foxtts", "postnamazu", "triggernometry", "silverdasher", "matcha" },
                StringComparer.Ordinal),
        "The explicit full-permission confirmation does not preserve SilverDasher before the dedicated Matcha group.");
}

static void ValidateMatchaPermissionIsolation()
{
    Assert(
        BundledActPluginCapabilities.All.Select(entry => entry.PluginId).SequenceEqual(
            new[] { "act.foxtts", "postnamazu", "triggernometry" },
            StringComparer.Ordinal),
        "Matcha changed the existing shared Host permission workflow.");
    Assert(
        BundledActPluginCapabilities.Matcha.Contains(ActCapability.ReadCombatLogs) &&
        BundledActPluginCapabilities.Matcha.Contains(ActCapability.NetworkRequest) &&
        BundledActPluginCapabilities.Matcha.Contains(ActCapability.WriteFiles) &&
        !BundledActPluginCapabilities.Matcha.Contains(ActCapability.NativeGameMemory),
        "Matcha does not expose its dedicated least-privilege capability declaration.");
    Assert(
        BundledActPluginCapabilities.FullPermissionConfirmation[^1].PluginId == "matcha",
        "Matcha is not the final independently confirmed permission group.");
}

static async Task ValidateSilverDasherNotificationIpcAsync()
{
    var log = DispatchProxy.Create<IPluginLog, NoOpPluginLogProxy>();
    await using var client = new HostIpcClient(
        new EncounterStateStore(),
        new PluginLogger(log));
    HostSilverDasherNotification? received = null;
    client.SilverDasherNotificationRequested += (_, notification) => received = notification;
    var applyMessage = typeof(HostIpcClient).GetMethod(
                           "ApplyMessage",
                           BindingFlags.Instance | BindingFlags.NonPublic)
                       ?? throw new MissingMethodException(
                           typeof(HostIpcClient).FullName,
                           "ApplyMessage");
    applyMessage.Invoke(
        client,
        [
            HostEnvelope.Create(
                "silverdasher-notification-smoke",
                1,
                HostMessageTypes.SilverDasherNotification,
                HostMessagePriority.Critical,
                new HostSilverDasherNotification("测试通知", "坐标与状态")),
        ]);
    Assert(
        received is { Message: "测试通知", Detail: "坐标与状态" } &&
        !string.Equals(
            HostMessageTypes.SilverDasherNotification,
            HostMessageTypes.CommandRequest,
            StringComparison.Ordinal),
        "SilverDasher notification did not use its independent typed Host IPC channel.");
}

static async Task ValidateMatchaTypedIpcAsync()
{
    var log = DispatchProxy.Create<IPluginLog, NoOpPluginLogProxy>();
    await using var client = new HostIpcClient(
        new EncounterStateStore(),
        new PluginLogger(log));
    HostMatchaNotification? notification = null;
    HostMatchaLogLine? logLine = null;
    HostTtsRequest? tts = null;
    client.MatchaNotificationRequested += (_, value) => notification = value;
    client.MatchaLogLineRequested += (_, value) => logLine = value;
    client.MatchaTtsRequested += (_, value) => tts = value;
    var applyMessage = typeof(HostIpcClient).GetMethod(
                           "ApplyMessage",
                           BindingFlags.Instance | BindingFlags.NonPublic)
                       ?? throw new MissingMethodException(
                           typeof(HostIpcClient).FullName,
                           "ApplyMessage");
    applyMessage.Invoke(client, [HostEnvelope.Create(
        "matcha-typed-ipc-smoke",
        1,
        HostMessageTypes.MatchaNotification,
        HostMessagePriority.Critical,
        new HostMatchaNotification(
            "Windows fallback",
            HostMatchaNotificationKind.WorldChanged))]);
    applyMessage.Invoke(client, [HostEnvelope.Create(
        "matcha-typed-ipc-smoke",
        2,
        HostMessageTypes.MatchaLogLine,
        HostMessagePriority.Data,
        new HostMatchaLogLine("00|matcha"))]);
    applyMessage.Invoke(client, [HostEnvelope.Create(
        "matcha-typed-ipc-smoke",
        3,
        HostMessageTypes.MatchaTtsRequest,
        HostMessagePriority.Control,
        new HostTtsRequest("matcha speech", "matcha"))]);
    Assert(
        notification is
        {
            Message: "Windows fallback",
            Kind: HostMatchaNotificationKind.WorldChanged,
        } &&
        logLine?.Line == "00|matcha" &&
        tts is { Text: "matcha speech", Source: "matcha" },
        "Matcha notification, log, or TTS crossed an untyped/shared command channel.");
}

static async Task ValidatePostNamazuHeadingIpcAsync()
{
    var log = DispatchProxy.Create<IPluginLog, NoOpPluginLogProxy>();
    await using var client = new HostIpcClient(
        new EncounterStateStore(),
        new PluginLogger(log));
    HostPostNamazuHeading? received = null;
    client.PostNamazuHeadingRequested += (_, value) => received = value;
    var applyMessage = typeof(HostIpcClient).GetMethod(
                           "ApplyMessage",
                           BindingFlags.Instance | BindingFlags.NonPublic)
                       ?? throw new MissingMethodException(
                           typeof(HostIpcClient).FullName,
                           "ApplyMessage");
    var now = DateTimeOffset.UtcNow;
    applyMessage.Invoke(client, [HostEnvelope.Create(
        "postnamazu-heading-ipc-smoke",
        1,
        HostMessageTypes.PostNamazuSetHeading,
        HostMessagePriority.State,
        new HostPostNamazuHeading(0x12345678, 1.25f, now))]);
    Assert(
        received is { Address: 0x12345678, Heading: 1.25f },
        "PostNamazu heading did not cross its typed game-side IPC channel.");

    received = null;
    applyMessage.Invoke(client, [HostEnvelope.Create(
        "postnamazu-heading-ipc-smoke",
        2,
        HostMessageTypes.PostNamazuSetHeading,
        HostMessagePriority.State,
        new HostPostNamazuHeading(0x12345678, 1.25f, now.AddSeconds(-10)))]);
    Assert(
        received is null,
        "A stale PostNamazu heading crossed the game-side IPC validation boundary.");
}

static void ValidateUnscramblerSupportPolicy()
{
    var bundledPolicy = typeof(IINACT.Network.ZoneDownHookManager).GetMethod(
        "CanUseBundledVersionConstants",
        BindingFlags.NonPublic | BindingFlags.Static);
    var chineseRuntimePolicy = typeof(IINACT.Network.ZoneDownHookManager).GetMethod(
        "CanUseChineseRuntimeVersionConstants",
        BindingFlags.NonPublic | BindingFlags.Static);
    var chineseRuntimeFactory = typeof(IINACT.Network.ZoneDownHookManager).GetMethod(
        "GetChineseRuntimeVersionConstant",
        BindingFlags.NonPublic | BindingFlags.Static);
    var globalRuntimePolicy = typeof(IINACT.Network.ZoneDownHookManager).GetMethod(
        "CanUseGlobalRuntimeVersionConstants",
        BindingFlags.NonPublic | BindingFlags.Static);
    var globalRuntimeFactory = typeof(IINACT.Network.ZoneDownHookManager).GetMethod(
        "GetGlobalRuntimeVersionConstant",
        BindingFlags.NonPublic | BindingFlags.Static);
    Assert(
        bundledPolicy is not null &&
        chineseRuntimePolicy is not null &&
        chineseRuntimeFactory is not null &&
        globalRuntimePolicy is not null &&
        globalRuntimeFactory is not null,
        "Unscrambler support policy is missing.");

    static bool InvokeBundled(MethodInfo policy, GameRegion region, string version)
        => policy.Invoke(null, [region, version]) is true;

    static bool InvokeChineseRuntime(
        MethodInfo policy,
        GameRegion region,
        string version,
        int opcodeKeyTableSize)
        => policy.Invoke(null, [region, version, opcodeKeyTableSize]) is true;

    static bool InvokeGlobalRuntime(
        MethodInfo policy,
        GameRegion region,
        string version,
        int opcodeKeyTableSize)
        => policy.Invoke(null, [region, version, opcodeKeyTableSize]) is true;

    const string chinese755h = "2026.08.05.0000.0000";
    const uint chineseOpcodeKeyTableOffset = 0x231EB40;
    const int opcodeKeyTableSize = 89 * 4;
    var globalConstants = Unscrambler.Constants.VersionConstants.ForGameVersion(chinese755h);
    var unscrambler = Unscrambler.UnscramblerFactory.ForGameVersion(chinese755h);
    var expectedObfuscatedOpcodes = new Dictionary<string, int>
    {
        ["PlayerSpawn"] = 0x398,
        ["NpcSpawn"] = 0x06F,
        ["NpcSpawn2"] = 0x287,
        ["ActionEffect01"] = 0x296,
        ["ActionEffect08"] = 0x164,
        ["ActionEffect16"] = 0x1B1,
        ["ActionEffect24"] = 0x39B,
        ["ActionEffect32"] = 0x372,
        ["StatusEffectList"] = 0x1F1,
        ["StatusEffectList3"] = 0x153,
        ["Examine"] = 0x2BB,
        ["UpdateGearset"] = 0x336,
        ["UpdateParty"] = 0x1B8,
        ["ActorControl"] = 0x1DA,
        ["ActorCast"] = 0x18C,
        ["UnknownEffect01"] = 0x0BF,
        ["UnknownEffect16"] = 0x115,
        ["ActionEffect02"] = 0x305,
        ["ActionEffect04"] = 0x14D,
    };
    Assert(
        unscrambler is not null &&
        globalConstants.InitZoneOpcode == 0x028D &&
        globalConstants.UnknownObfuscationInitOpcode == 0x0128 &&
        globalConstants.OpcodeKeyTableOffset == 0x2323420 &&
        globalConstants.OpcodeKeyTableSize == opcodeKeyTableSize,
        "Unscrambler 7.55h1 factory or Global constants are incomplete.");
    Assert(
        globalConstants.ObfuscatedOpcodes.Count == expectedObfuscatedOpcodes.Count &&
        expectedObfuscatedOpcodes.All(pair =>
            globalConstants.ObfuscatedOpcodes.TryGetValue(pair.Key, out var opcode) &&
            opcode == pair.Value),
        "Unscrambler 7.55h1 contains incomplete or stale obfuscated opcodes.");

    const string global755h2 = "2026.08.11.0000.0000";
    const uint global755h2OpcodeKeyTableOffset = 0x23184C0;
    const int global755h2OpcodeKeyTableSize = 77 * 4;
    var expectedGlobal755h2Opcodes = new Dictionary<string, int>
    {
        ["PlayerSpawn"] = 0x32D,
        ["NpcSpawn"] = 0x0E9,
        ["NpcSpawn2"] = 0x21C,
        ["ActionEffect01"] = 0x371,
        ["ActionEffect08"] = 0x3C8,
        ["ActionEffect16"] = 0x1AF,
        ["ActionEffect24"] = 0x35A,
        ["ActionEffect32"] = 0x3D5,
        ["StatusEffectList"] = 0x2EC,
        ["StatusEffectList3"] = 0x263,
        ["Examine"] = 0x097,
        ["UpdateGearset"] = 0x173,
        ["UpdateParty"] = 0x3E4,
        ["ActorControl"] = 0x096,
        ["ActorCast"] = 0x136,
        ["UnknownEffect01"] = 0x33C,
        ["UnknownEffect16"] = 0x1D8,
        ["ActionEffect02"] = 0x2F4,
        ["ActionEffect04"] = 0x3AD,
    };
    var global755h2RuntimeConstants =
        (Unscrambler.Constants.VersionConstants)globalRuntimeFactory!.Invoke(
            null,
            [
                global755h2,
                global755h2OpcodeKeyTableOffset,
                global755h2OpcodeKeyTableSize,
            ])!;
    var global755h2Unscrambler = new Unscrambler.Unscramble.Versions.Unscrambler73();
    global755h2Unscrambler.Initialize(global755h2RuntimeConstants);
    Assert(
        global755h2RuntimeConstants.OpcodeKeyTableOffset == global755h2OpcodeKeyTableOffset &&
        global755h2RuntimeConstants.OpcodeKeyTableSize == global755h2OpcodeKeyTableSize &&
        global755h2RuntimeConstants.TableOffsets.Length == 0 &&
        global755h2RuntimeConstants.MidTableOffset == 0 &&
        global755h2RuntimeConstants.DayTableOffset == 0 &&
        global755h2RuntimeConstants.ObfuscatedOpcodes.Count == expectedGlobal755h2Opcodes.Count &&
        expectedGlobal755h2Opcodes.All(pair =>
            global755h2RuntimeConstants.ObfuscatedOpcodes.TryGetValue(pair.Key, out var opcode) &&
            opcode == pair.Value),
        "Global 7.55h2 runtime constants are incomplete or do not use the discovered key table.");

    var unscramblerToMachina = new Dictionary<string, string>
    {
        ["PlayerSpawn"] = "PlayerSpawn",
        ["NpcSpawn"] = "NpcSpawn",
        ["NpcSpawn2"] = "NpcSpawn2",
        ["ActionEffect01"] = "Ability1",
        ["ActionEffect08"] = "Ability8",
        ["ActionEffect16"] = "Ability16",
        ["ActionEffect24"] = "Ability24",
        ["ActionEffect32"] = "Ability32",
        ["StatusEffectList"] = "StatusEffectList",
        ["StatusEffectList3"] = "StatusEffectList3",
        ["ActorControl"] = "ActorControl",
        ["ActorCast"] = "ActorCast",
    };
    OpcodeManager.Instance.SetRegion(GameRegion.Global);
    var global755h2MachinaOpcodes = OpcodeManager.Instance.CurrentOpcodes;
    foreach (var pair in unscramblerToMachina)
    {
        Assert(
            global755h2RuntimeConstants.ObfuscatedOpcodes[pair.Key] ==
            global755h2MachinaOpcodes[pair.Value],
            $"Unscrambler opcode {pair.Key} does not match Global Machina {pair.Value}.");
    }
    Assert(
        InvokeGlobalRuntime(
            globalRuntimePolicy!,
            GameRegion.Global,
            global755h2,
            global755h2OpcodeKeyTableSize),
        "Global 7.55h2 does not use its verified runtime profile.");
    Assert(
        !InvokeGlobalRuntime(
            globalRuntimePolicy!,
            GameRegion.Global,
            global755h2,
            global755h2OpcodeKeyTableSize - 4) &&
        !InvokeGlobalRuntime(
            globalRuntimePolicy!,
            GameRegion.Chinese,
            global755h2,
            global755h2OpcodeKeyTableSize) &&
        !InvokeGlobalRuntime(
            globalRuntimePolicy!,
            GameRegion.Global,
            "2099.01.01.0000.0000",
            global755h2OpcodeKeyTableSize),
        "An unverified region, version, or Global key-table size is marked ranking-safe.");

    var chineseRuntimeConstants = (Unscrambler.Constants.VersionConstants)chineseRuntimeFactory!.Invoke(
        null,
        [chinese755h, chineseOpcodeKeyTableOffset, opcodeKeyTableSize])!;
    var chineseUnscrambler = new Unscrambler.Unscramble.Versions.Unscrambler73();
    chineseUnscrambler.Initialize(chineseRuntimeConstants);
    Assert(
        chineseRuntimeConstants.OpcodeKeyTableOffset == chineseOpcodeKeyTableOffset &&
        chineseRuntimeConstants.OpcodeKeyTableOffset != globalConstants.OpcodeKeyTableOffset &&
        chineseRuntimeConstants.OpcodeKeyTableSize == opcodeKeyTableSize &&
        chineseRuntimeConstants.TableOffsets.Length == 0 &&
        chineseRuntimeConstants.MidTableOffset == 0 &&
        chineseRuntimeConstants.DayTableOffset == 0 &&
        chineseRuntimeConstants.ObfuscatedOpcodes.Count == globalConstants.ObfuscatedOpcodes.Count &&
        globalConstants.ObfuscatedOpcodes.All(pair =>
            chineseRuntimeConstants.ObfuscatedOpcodes.TryGetValue(pair.Key, out var opcode) &&
            opcode == pair.Value),
        "Chinese 7.55h runtime constants reused a Global memory offset or lost official opcodes.");

    OpcodeManager.Instance.SetRegion(GameRegion.Chinese);
    var chineseOpcodes = OpcodeManager.Instance.CurrentOpcodes;
    foreach (var pair in unscramblerToMachina)
    {
        Assert(
            chineseRuntimeConstants.ObfuscatedOpcodes[pair.Key] == chineseOpcodes[pair.Value],
            $"Unscrambler opcode {pair.Key} does not match Chinese Machina {pair.Value}.");
    }
    Assert(
        !InvokeBundled(bundledPolicy!, GameRegion.Chinese, chinese755h),
        "Chinese 7.55h incorrectly reuses the Global key-table address.");
    Assert(
        InvokeBundled(bundledPolicy!, GameRegion.Global, chinese755h),
        "Global 7.55h no longer uses its bundled Unscrambler constants.");
    Assert(
        InvokeBundled(bundledPolicy!, GameRegion.Global, global755h2),
        "Global 7.55h2 no longer uses Unscrambler.XIV 7.55.2 bundled constants.");
    Assert(
        !InvokeBundled(bundledPolicy!, GameRegion.Korean, chinese755h),
        "Korean clients incorrectly reuse Global Unscrambler constants.");
    Assert(
        !InvokeBundled(bundledPolicy!, GameRegion.Global, "2099.01.01.0000.0000"),
        "Unknown Global game versions incorrectly use bundled Unscrambler constants.");
    Assert(
        InvokeChineseRuntime(
            chineseRuntimePolicy!,
            GameRegion.Chinese,
            chinese755h,
            opcodeKeyTableSize),
        "Chinese 7.55h does not use its runtime-discovered regional key table.");
    Assert(
        !InvokeChineseRuntime(
            chineseRuntimePolicy!,
            GameRegion.Chinese,
            chinese755h,
            opcodeKeyTableSize - 4),
        "Chinese 7.55h accepts an unexpected opcode key-table size.");
    Assert(
        !InvokeChineseRuntime(
            chineseRuntimePolicy!,
            GameRegion.Korean,
            chinese755h,
            opcodeKeyTableSize) &&
        !InvokeChineseRuntime(
            chineseRuntimePolicy!,
            GameRegion.Chinese,
            "2026.07.16.0001.0000",
            opcodeKeyTableSize) &&
        !InvokeChineseRuntime(
            chineseRuntimePolicy!,
            GameRegion.Chinese,
            "2099.01.01.0000.0000",
            opcodeKeyTableSize),
        "A different region or Chinese game version is incorrectly marked ranking-safe.");
}

static void AssertFileVersion(string path, string expected, string component)
{
    Assert(File.Exists(path), $"{component} assembly is missing: {path}");
    var actual = System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileVersion;
    Assert(actual == expected, $"{component} version was {actual}, expected {expected}.");
}

static async Task ValidateBundledPluginDisclosureAsync(string testRoot)
{
    var bundleParent = Path.Combine(
        FindProjectRoot(),
        "src",
        "DalamudActCompat",
        "bin",
        "Release");
    var paths = new PluginPaths(Path.Combine(testRoot, "bundled-config"));
    var installer = new ActPluginPackageInstaller(paths);
    var configuration = new DalamudActCompat.Plugin.PluginConfiguration();
    var manager = new BundledActPluginManager(
        "0.2.31.0",
        installer,
        configuration);
    Assert(
        manager.Plugins.Count == 0 && manager.GetPendingDisclosures().Count == 0,
        "A missing startup resource pack prevented creation of an empty bundled-plugin manager.");
    // Resource acquisition happens after the plugin constructor returns, so the manager must
    // accept the verified bundle only when that background work completes.
    manager.LoadBundle(Path.Combine(bundleParent, BundledActPluginManager.DirectoryName));

    var pending = manager.GetPendingDisclosures();
    Assert(pending.Count == 5, "A new install did not require all five bundled extension disclosures.");
    Assert(
        manager.GetDisclosures().Count == 5,
        "The third-party notice did not expose every bundled extension source.");
    Assert(
        pending.Any(plugin =>
            plugin.Id == "triggernometry" &&
            plugin.Author == "Paissa Heavy Industries" &&
            plugin.Maintainer.Contains("MnFeN", StringComparison.Ordinal) &&
            plugin.DownloadUrl.StartsWith("https://", StringComparison.Ordinal)),
        "Triggernometry author, maintainer, or DLL URL disclosure is incomplete.");
    Assert(
        pending.Any(plugin =>
            plugin.Id == "act.foxtts" &&
            plugin.Author == "Noisyfox" &&
            plugin.ProjectUrl == "https://github.com/Noisyfox/ACT.FoxTTS"),
        "ACT.FoxTTS author or project URL disclosure is incomplete.");
    Assert(
        pending.Any(plugin =>
            plugin.Id == "postnamazu" &&
            plugin.Author == "Natsukage" &&
            plugin.DownloadUrl.Contains("/releases/download/", StringComparison.Ordinal)),
        "PostNamazu author or DLL URL disclosure is incomplete.");
    Assert(
        pending.Any(plugin =>
            plugin.Id == "silverdasher" &&
            plugin.Version == "0.6.0.4" &&
            plugin.Maintainer.Contains("582145824", StringComparison.Ordinal) &&
            plugin.DisableOnlineUpdates &&
            !plugin.EnableAfterInstall &&
            plugin.PackageSha256 == "0999e18e103d0c9e8e7db3dfde12b23f1ff2df9565472d2aa832ebeaa3f7341d" &&
            File.Exists(plugin.PackagePath)),
        "SilverDasher complete-package version, support group, fixed hash, or bundled artifact is missing.");
    Assert(
        pending.Any(plugin =>
            plugin.Id == "matcha" &&
            plugin.Version == "26.8.12.1622" &&
            plugin.License == "AGPL-3.0" &&
            plugin.DisableOnlineUpdates &&
            plugin.EnableAfterInstall &&
            plugin.SourceUrl.EndsWith(
                "/6cf242b59475aa77e4c2deee61e1b9191be5ba13",
                StringComparison.Ordinal) &&
            plugin.PackageSha256 == "da2037d3fb75914fd980f72978debf83fc761f693adfff939dbf386f0196a89b" &&
            plugin.Sha256 == "3df088e73dd8a314a08a1b302a2fefe9bfefc1a52fce54032f719421cf7810fa" &&
            File.Exists(plugin.PackagePath)),
        "Matcha source commit, AGPL notice, fixed hashes, default-enable flag, or complete package is missing.");

    await manager.InstallAndAcknowledgeAsync(pending, CancellationToken.None);
    Assert(
        manager.GetPendingDisclosures().Count == 0,
        "Acknowledged current bundled DLLs still required disclosure.");
    var migratedPluginId = configuration.BundledPluginDisclosureKeys.Keys.First();
    var stableDisclosureKey = configuration.BundledPluginDisclosureKeys[migratedPluginId];
    configuration.BundledPluginDisclosureKeys[migratedPluginId] =
        $"0.2.30.0|{stableDisclosureKey}";
    Assert(
        manager.GetPendingDisclosures().Count == 0 &&
        configuration.BundledPluginDisclosureKeys[migratedPluginId] == stableDisclosureKey,
        "A legacy Host-version-prefixed disclosure key was not migrated in place.");
    Assert(
        manager.GetDisclosures().Count == 5 &&
        manager.GetDisclosures().All(plugin =>
            !string.IsNullOrWhiteSpace(plugin.Author) &&
            Uri.TryCreate(plugin.ProjectUrl, UriKind.Absolute, out _)),
        "Acknowledged DLL author and project URL disclosures disappeared from the notice.");
    var installed = installer.Discover(configuration.DisabledActPluginIds);
    Assert(installed.Count == 5, "Not all bundled extensions were installed.");
    var installedSilverDasher = installed.Single(plugin =>
        plugin.Manifest.Id == "silverdasher");
    Assert(
        !installedSilverDasher.Enabled &&
        installedSilverDasher.DisplayVersion == "0.6.0.4" &&
        !installedSilverDasher.HasVersionMismatch,
        "The bundled SilverDasher install did not preserve its disabled default or Core version.");
    Assert(
        installed.Where(plugin => plugin.Manifest.Id != "silverdasher").All(plugin => plugin.Enabled),
        "Installing SilverDasher unexpectedly changed another bundled extension's enabled state.");
    var installedMatcha = installed.Single(plugin => plugin.Manifest.Id == "matcha");
    Assert(
        installedMatcha.Enabled &&
        installed[^1].Manifest.Id == "matcha" &&
        File.Exists(Path.Combine(
            installedMatcha.InstallDirectory,
            "Plugins",
            "Cafe.Matcha",
            "Cafe.Matcha.dll")) &&
        File.Exists(Path.Combine(
            installedMatcha.InstallDirectory,
            "Plugins",
            "Cafe.Matcha",
            "data",
            "fate.json")) &&
        File.Exists(Path.Combine(
            installedMatcha.InstallDirectory,
            "Plugins",
            "Cafe.Matcha",
            "upstream",
            "Cafe.Matcha.Upstream.dll")) &&
        File.Exists(Path.Combine(
            installedMatcha.InstallDirectory,
            "Plugins",
            "Cafe.Matcha",
            "upstream",
            "Cafe.Matcha.Runtime.bin")),
        "The complete Matcha package was not installed enabled and ordered after SilverDasher.");
    Assert(
        File.Exists(Path.Combine(
            installedSilverDasher.InstallDirectory,
            "Plugins",
            "SilverDasher",
            "libs",
            "SilverDasher.Core.dll")) &&
        File.Exists(Path.Combine(
            installedSilverDasher.InstallDirectory,
            "Plugins",
            "SilverDasher",
            "data",
            "opcodes.json")),
        "The bundled SilverDasher install did not preserve its complete libs/data package.");
    Assert(
        installed.All(manager.IsAllowedToLoad),
        "Acknowledged current bundled DLLs were not enabled for runtime loading.");

    var triggernometry = installed.Single(plugin => plugin.Manifest.Id == "triggernometry");
    var duplicateDirectory = Path.Combine(paths.ActPluginDirectory, "triggernometry-old-copy");
    Directory.CreateDirectory(duplicateDirectory);
    foreach (var file in Directory.EnumerateFiles(triggernometry.InstallDirectory))
    {
        File.Copy(file, Path.Combine(duplicateDirectory, Path.GetFileName(file)));
    }

    Assert(
        manager.GetPendingDisclosures().Count == 0,
        "A duplicate legacy plugin id interrupted bundled DLL disclosure checks.");

    using (var httpClient = new HttpClient(
               new BundledPluginUpdateHandler(manager.Plugins)))
    using (var updateChecker = new BundledActPluginUpdateChecker(
               paths.BundledPluginUpdateCacheDirectory,
               httpClient))
    {
        var check = await updateChecker.CheckAsync(
            manager.Plugins,
            CancellationToken.None);
        Assert(
            check.Failures.Count == 0 && check.Updates.Count == 3,
            "Runtime author-source checking did not find all simulated DLL updates.");
        Assert(
            check.Updates.All(plugin =>
                plugin.IsOnlineUpdate &&
                plugin.DownloadUrl.StartsWith("https://", StringComparison.Ordinal) &&
                File.Exists(plugin.AssemblyPath)),
            "Runtime DLL update candidates were not cached with disclosure URLs.");
        Assert(
            manager.ApplyOnlineUpdates(check.Updates) == 3,
            "Runtime DLL update candidates were not applied to the disclosure gate.");
        var onlinePending = manager.GetPendingDisclosures();
        Assert(
            onlinePending.Count == 3 &&
            onlinePending.All(plugin => plugin.IsOnlineUpdate),
            "Online DLL updates did not require a new disclosure.");
        var onlineUpdateIds = check.Updates
            .Select(plugin => plugin.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert(
            installed.All(plugin => onlineUpdateIds.Contains(plugin.Manifest.Id)
                ? !manager.IsAllowedToLoad(plugin)
                : manager.IsAllowedToLoad(plugin)),
            "Online-updated DLLs were not gated, or a hash-fixed complete package was unnecessarily disabled.");

        await manager.InstallAndAcknowledgeAsync(
            onlinePending,
            CancellationToken.None);
        Assert(
            manager.GetPendingDisclosures().Count == 0 &&
            configuration.BundledPluginUpdateRecords.Count == 3,
            "Online DLL updates were not installed, acknowledged, and persisted.");
    }

    configuration = Newtonsoft.Json.JsonConvert.DeserializeObject<
                        DalamudActCompat.Plugin.PluginConfiguration>(
                        Newtonsoft.Json.JsonConvert.SerializeObject(configuration))
                    ?? throw new InvalidOperationException(
                        "Plugin configuration update records did not round-trip.");
    var persistedOnline = new BundledActPluginManager(
        bundleParent,
        "0.2.31.0",
        installer,
        configuration);
    Assert(
        persistedOnline.GetPendingDisclosures().Count == 0,
        "An accepted online DLL update was lost after recreating the manager.");
    Assert(
        installer
            .Discover(configuration.DisabledActPluginIds)
            .Where(plugin => configuration.BundledPluginUpdateRecords.ContainsKey(
                plugin.Manifest.Id))
            .Any(persistedOnline.IsAllowedToLoad),
        "Persisted online DLL updates were not loadable while offline.");

    var nextRelease = new BundledActPluginManager(
        bundleParent,
        "0.2.32.0",
        installer,
        configuration);
    Assert(
        nextRelease.GetPendingDisclosures().Count == 0,
        "An unchanged bundled artifact asked for disclosure again after a Host update.");
    var currentInstalled = installer
        .Discover(configuration.DisabledActPluginIds)
        .Where(plugin => !string.Equals(
            plugin.InstallDirectory,
            duplicateDirectory,
            StringComparison.OrdinalIgnoreCase))
        .ToArray();
    Assert(
        currentInstalled.All(nextRelease.IsAllowedToLoad),
        "Accepted current bundled DLLs became unloadable after an unrelated Host update.");

    var changedBaselineRecord = configuration.BundledPluginUpdateRecords.First();
    var acceptedBaselineSha = changedBaselineRecord.Value.BundledSha256WhenAccepted;
    changedBaselineRecord.Value.BundledSha256WhenAccepted = new string('0', 64);
    var changedBaselineRelease = new BundledActPluginManager(
        bundleParent,
        "0.2.32.0",
        installer,
        configuration);
    Assert(
        changedBaselineRelease.GetPendingDisclosures().Any(plugin =>
            string.Equals(
                plugin.Id,
                changedBaselineRecord.Key,
                StringComparison.OrdinalIgnoreCase)),
        "A changed bundled artifact was masked by an older accepted online update.");
    changedBaselineRecord.Value.BundledSha256WhenAccepted = acceptedBaselineSha;

    Assert(
        BundledActPluginCapabilities.FullPermissionConfirmation.Any(entry =>
            entry.Capabilities.Any(capability =>
                !configuration.HasExplicitActCapabilityDecision(
                    entry.PluginId,
                    capability))),
        "A fresh configuration unexpectedly contains explicit bundled capability decisions.");
    foreach (var (pluginId, capabilities) in
             BundledActPluginCapabilities.FullPermissionConfirmation)
    {
        foreach (var capability in capabilities)
        {
            configuration.SetActCapability(pluginId, capability, allowed: false);
        }
    }
    Assert(
        BundledActPluginCapabilities.FullPermissionConfirmation.All(entry =>
            entry.Capabilities.All(capability =>
                configuration.HasExplicitActCapabilityDecision(
                    entry.PluginId,
                    capability))),
        "Stored bundled capability decisions still look incomplete and would reopen the permission prompt.");
}

static async Task ValidateCactbotMissingTargetBackupRecoveryAsync(
    string testRoot,
    string validPackage)
{
    var paths = new PluginPaths(Path.Combine(testRoot, "cactbot-missing-target-recovery"));
    paths.EnsureCreated();
    await File.WriteAllBytesAsync(
        Path.Combine(paths.CactbotDirectory, "CactbotOverlay.dll"),
        [9, 8, 7]);
    var raidboss = Path.Combine(paths.CactbotDirectory, "ui", "raidboss", "raidboss.html");
    Directory.CreateDirectory(Path.GetDirectoryName(raidboss)!);
    await File.WriteAllTextAsync(raidboss, "old-raidboss");
    var userFile = Path.Combine(paths.CactbotDirectory, "user", "custom.js");
    Directory.CreateDirectory(Path.GetDirectoryName(userFile)!);
    await File.WriteAllTextAsync(userFile, "old-user-data");

    var installer = new CactbotPackageInstaller(paths);
    installer.MoveDirectory = (source, destination) =>
    {
        if (destination.Equals(paths.CactbotDirectory, StringComparison.OrdinalIgnoreCase))
        {
            if (source.StartsWith(paths.PluginStagingDirectory, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("simulated Cactbot commit failure");
            }

            if (source.StartsWith(
                    paths.CactbotDirectory + ".backup-",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("simulated Cactbot rollback failure");
            }
        }

        Directory.Move(source, destination);
    };

    AggregateException? failedReplacement = null;
    try
    {
        await installer.InstallAsync(validPackage, CancellationToken.None);
    }
    catch (AggregateException ex)
    {
        failedReplacement = ex;
    }

    var preservedBackup = Directory
        .EnumerateDirectories(
            paths.ConfigDirectory,
            $"{Path.GetFileName(paths.CactbotDirectory)}.backup-*")
        .Single();
    Assert(
        failedReplacement is not null &&
        !Directory.Exists(paths.CactbotDirectory) &&
        File.Exists(Path.Combine(preservedBackup, "CactbotOverlay.dll")) &&
        File.Exists(Path.Combine(preservedBackup, "user", "custom.js")),
        "A failed Cactbot commit/rollback did not preserve the previous installation as the recovery backup.");

    var invalidPackage = Path.Combine(testRoot, "cactbot-invalid-after-missing-target.zip");
    using (var archive = ZipFile.Open(invalidPackage, ZipArchiveMode.Create))
    {
        await WriteArchiveEntryAsync(archive, "not-cactbot/readme.txt", "invalid"u8.ToArray());
    }

    try
    {
        await installer.InstallAsync(invalidPackage, CancellationToken.None);
    }
    catch (InvalidDataException)
    {
    }
    Assert(
        !Directory.Exists(paths.CactbotDirectory) &&
        Directory.Exists(preservedBackup) &&
        File.Exists(Path.Combine(preservedBackup, "CactbotOverlay.dll")),
        "A transient recovery failure deleted the only Cactbot backup or created a false target directory.");

    installer.MoveDirectory = Directory.Move;
    var invalidFailed = false;
    try
    {
        await installer.InstallAsync(invalidPackage, CancellationToken.None);
    }
    catch (InvalidDataException)
    {
        invalidFailed = true;
    }

    Assert(
        invalidFailed &&
        installer.IsInstalled &&
        await File.ReadAllTextAsync(Path.Combine(paths.CactbotDirectory, "ui", "raidboss", "raidboss.html")) ==
            "old-raidboss" &&
        await File.ReadAllTextAsync(Path.Combine(paths.CactbotDirectory, "user", "custom.js")) ==
            "old-user-data",
        "The only recoverable Cactbot backup was deleted before the next package reached a valid staged state.");
}

static async Task ValidateCactbotPostShutdownPublicationGuardAsync()
{
    var publishCount = 0;
    await CactbotOperationLifecycle.PublishIfActiveAsync(
        shutdownStarted: false,
        CancellationToken.None,
        () =>
        {
            publishCount++;
            return Task.CompletedTask;
        });
    await CactbotOperationLifecycle.PublishIfActiveAsync(
        shutdownStarted: true,
        CancellationToken.None,
        () =>
        {
            publishCount++;
            return Task.CompletedTask;
        });
    using var canceled = new CancellationTokenSource();
    canceled.Cancel();
    await CactbotOperationLifecycle.PublishIfActiveAsync(
        shutdownStarted: false,
        canceled.Token,
        () =>
        {
            publishCount++;
            return Task.CompletedTask;
        });
    Assert(
        publishCount == 1,
        "Cactbot completion publication ran after plugin shutdown or cancellation.");

    var pluginSource = File.ReadAllText(Path.Combine(
        FindProjectRoot(), "src", "DalamudActCompat", "Plugin", "Plugin.cs"));
    Assert(
        pluginSource.Contains("PublishCactbotInstalledNotificationAsync", StringComparison.Ordinal) &&
        pluginSource.Contains("services.Framework.RunOnFrameworkThread", StringComparison.Ordinal) &&
        pluginSource.Contains("cancellationToken.ThrowIfCancellationRequested();", StringComparison.Ordinal),
        "Cactbot completion is not guarded and explicitly dispatched to the framework thread.");
}

static void ValidateFflogsConcurrencyBoundaries()
{
    var original = new FflogsSettings
    {
        Enabled = true,
        ClientId = "client",
        ClientSecret = "secret",
        EncounterMappings = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Boss"] = 1,
        },
    };
    var snapshot = original.Snapshot();
    snapshot.ClientId = "changed";
    snapshot.EncounterMappings["Boss"] = 2;
    snapshot.EncounterMappings["Other"] = 3;
    Assert(
        original.ClientId == "client" &&
        original.EncounterMappings["Boss"] == 1 &&
        !original.EncounterMappings.ContainsKey("Other"),
        "FFLogs configuration snapshots still share mutable UI state.");

    Assert(
        typeof(IAsyncDisposable).IsAssignableFrom(typeof(FflogsEstimateService)) &&
        typeof(FflogsEstimateService).GetMethod(nameof(FflogsEstimateService.BeginShutdown)) is not null,
        "FFLogs background work does not expose a cancellable, awaitable shutdown lifecycle.");

    var projectRoot = FindProjectRoot();
    var serviceSource = File.ReadAllText(Path.Combine(
        projectRoot, "src", "DalamudActCompat", "Fflogs", "FflogsEstimateService.cs"));
    var controlCenterSource = File.ReadAllText(Path.Combine(
        projectRoot, "src", "DalamudActCompat", "UI", "ControlCenterWindow.cs"));
    var adapterSource = File.ReadAllText(Path.Combine(
        projectRoot, "src", "DalamudActCompat", "Parser", "IinactAdapter.cs"));
    Assert(
        serviceSource.Contains("TryStartBackgroundTask", StringComparison.Ordinal) &&
        serviceSource.Contains("SaveCacheAsync", StringComparison.Ordinal) &&
        serviceSource.Contains("cacheWriteGate", StringComparison.Ordinal) &&
        serviceSource.Contains("missingSpecs", StringComparison.Ordinal) &&
        serviceSource.Contains("QueueCurveLoad(specName)", StringComparison.Ordinal) &&
        !serviceSource.Contains("_ = Task.Run", StringComparison.Ordinal) &&
        adapterSource.IndexOf(
            "encounter = CaptureFflogsEstimatesSafely(encounter);",
            StringComparison.Ordinal) is var captureIndex &&
        captureIndex >= 0 &&
        captureIndex < adapterSource.IndexOf("var segmentMode", StringComparison.Ordinal) &&
        controlCenterSource.Contains("UpdateFflogsSettings", StringComparison.Ordinal) &&
        controlCenterSource.Contains("configuration.Fflogs = next;", StringComparison.Ordinal),
        "FFLogs background preloading, UI ownership, task tracking, or serialized cache persistence regressed.");
}

static async Task ValidateFflogsCacheWritersAsync(string testRoot)
{
    var cacheDirectory = Path.Combine(testRoot, "fflogs-cache-writers");
    var cachePath = Path.Combine(cacheDirectory, "fflogs-cache.json");
    Directory.CreateDirectory(cacheDirectory);
    var seededCache = JsonSerializer.Serialize(new FflogsCacheDocument(
            DateTimeOffset.UtcNow,
            [
                new FflogsEncounterCatalogEntry(100, "Howling Blade", "AAC Cruiserweight", false),
                new FflogsEncounterCatalogEntry(104, "Lindwurm", "AAC Heavyweight", false),
            ],
            [
                new FflogsCurveCacheEntry(100, "Howling Blade", "Paladin", DateTimeOffset.UtcNow, [], 101),
                new FflogsCurveCacheEntry(104, "Lindwurm", "Paladin", DateTimeOffset.UtcNow, [], 0),
                new FflogsCurveCacheEntry(
                    104,
                    "Lindwurm",
                    "Paladin",
                    DateTimeOffset.UtcNow,
                    [],
                    100,
                    "CN",
                    9,
                    "rdps"),
                new FflogsCurveCacheEntry(
                    104,
                    "Lindwurm",
                    "WhiteMage",
                    DateTimeOffset.UtcNow,
                    [new FflogsCurvePoint(25, 16_000)],
                    100,
                    "CN",
                    9,
                    "dps",
                    FflogsEstimateService.CurrentCurveFormatVersion),
            ]));
    await File.WriteAllTextAsync(
        cachePath,
        seededCache.Replace(",\"Difficulty\":0", string.Empty, StringComparison.Ordinal));
    var log = DispatchProxy.Create<IPluginLog, NoOpPluginLogProxy>();
    var service = new FflogsEstimateService(
        () => new FflogsSettings(),
        cachePath,
        new PluginLogger(log));
    try
    {
        var firstTemporaryPath = FflogsEstimateService.CreateCacheTemporaryPath(cachePath);
        var secondTemporaryPath = FflogsEstimateService.CreateCacheTemporaryPath(cachePath);
        Assert(
            !firstTemporaryPath.Equals(secondTemporaryPath, StringComparison.OrdinalIgnoreCase),
            "FFLogs cache writers still share one temporary-file path.");

        await service.SaveCacheAsync(CancellationToken.None);
        await service.SaveCacheAsync(CancellationToken.None);
        await Task.WhenAll(
            service.SaveCacheAsync(CancellationToken.None),
            service.SaveCacheAsync(CancellationToken.None));

        using var cacheDocument = JsonDocument.Parse(await File.ReadAllTextAsync(cachePath));
        var cachedEncounterIds = cacheDocument.RootElement.GetProperty("Encounters")
            .EnumerateArray()
            .Select(static encounter => encounter.GetProperty("Id").GetInt32())
            .ToArray();
        var cachedCurveIds = cacheDocument.RootElement.GetProperty("Curves")
            .EnumerateArray()
            .Select(static curve => curve.GetProperty("EncounterId").GetInt32())
            .ToArray();
        Assert(
            cacheDocument.RootElement.TryGetProperty("CatalogFetchedAt", out _) &&
            cachedEncounterIds.SequenceEqual([104]) &&
            cachedCurveIds.SequenceEqual([104]) &&
            cacheDocument.RootElement.GetProperty("Curves")[0].GetProperty("Difficulty").GetInt32() == 100 &&
            cacheDocument.RootElement.GetProperty("Curves")[0].GetProperty("FormatVersion").GetInt32() ==
                FflogsEstimateService.CurrentCurveFormatVersion &&
            !Directory.EnumerateFiles(cacheDirectory, "*.tmp").Any(),
            "FFLogs cache persistence retained an old ranking tier/curve format, corrupted the cache, or left temp files behind.");
    }
    finally
    {
        await service.DisposeAsync();
    }

    var globalService = new FflogsEstimateService(
        () => new FflogsSettings(),
        cachePath,
        new PluginLogger(log),
        useChineseRankings: () => false);
    try
    {
        var globalReference = globalService.ReferenceSnapshot;
        Assert(
            globalReference.Region == "Global" &&
            globalReference.Partition is null &&
            globalReference.CurveCount == 0,
            "A Global client reused the cached CN partition or exposed a fixed partition number.");
    }
    finally
    {
        await globalService.DisposeAsync();
    }
}

static async Task ValidatePortableConfigurationArchiveAsync(string testRoot)
{
    var configurationRoot = Path.Combine(testRoot, "portable-configuration-source");
    var archiveDirectory = Path.Combine(testRoot, "portable-configuration-archives");
    Directory.CreateDirectory(configurationRoot);
    Directory.CreateDirectory(archiveDirectory);

    async Task WriteConfigurationFileAsync(string relativePath, string contents)
    {
        var path = Path.Combine(configurationRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, contents);
    }

    const string dactScope = "DalamudActCompat.json";
    var triggerScope = Path.Combine("plugin-configs", "Triggernometry");
    var matchaScope = Path.Combine("plugin-configs", "Matcha");
    var absentScope = Path.Combine("plugin-configs", "AbsentPlugin");
    string[] portableScopes = [dactScope, triggerScope, matchaScope, absentScope];

    await WriteConfigurationFileAsync(dactScope, "{\"meter\":\"cloud\"}");
    await WriteConfigurationFileAsync(
        Path.Combine(triggerScope, "config.xml"),
        "<triggers>cloud</triggers>");
    await WriteConfigurationFileAsync(
        Path.Combine(matchaScope, "profiles", "default.json"),
        "{\"profile\":\"cloud\"}");
    await WriteConfigurationFileAsync(
        Path.Combine("logs", "ffxiv", "Network_test.log"),
        "private combat log");
    await WriteConfigurationFileAsync(
        Path.Combine("host", "DalamudActCompat.Host.exe"),
        "machine-specific binary");
    await WriteConfigurationFileAsync(
        Path.Combine("webview2", "Cookies"),
        "machine-specific cookie");

    var service = new PortableConfigurationArchiveService();
    var exportedArchive = Path.Combine(archiveDirectory, "cloud-snapshot.dactbackup");
    var export = await service.ExportAsync(
        configurationRoot,
        exportedArchive,
        portableScopes,
        CancellationToken.None);
    Assert(
        export.ScopeCount == 4 &&
        export.FileCount == 3 &&
        export.UncompressedBytes > 0 &&
        File.Exists(exportedArchive),
        "Portable configuration export did not capture the declared DACT/plugin scopes.");

    using (var archive = ZipFile.OpenRead(exportedArchive))
    {
        var entryNames = archive.Entries
            .Select(static entry => entry.FullName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var manifestEntry = archive.GetEntry("manifest.json")
                            ?? throw new InvalidOperationException(
                                "Portable configuration archive has no manifest.");
        using var manifestReader = new StreamReader(manifestEntry.Open());
        var manifestText = await manifestReader.ReadToEndAsync();
        Assert(
            entryNames.SetEquals(
            [
                "manifest.json",
                "payload/DalamudActCompat.json",
                "payload/plugin-configs/Matcha/profiles/default.json",
                "payload/plugin-configs/Triggernometry/config.xml",
            ]) &&
            !manifestText.Contains(configurationRoot, StringComparison.OrdinalIgnoreCase) &&
            !manifestText.Contains("logs", StringComparison.OrdinalIgnoreCase) &&
            !manifestText.Contains("host", StringComparison.OrdinalIgnoreCase) &&
            !manifestText.Contains("webview2", StringComparison.OrdinalIgnoreCase),
            "Portable configuration export leaked excluded machine data or absolute paths.");
    }

    // Simulate a different computer with defaults, changed plugin settings, and
    // files that should disappear when a declared scope is replaced exactly.
    await WriteConfigurationFileAsync(dactScope, "{\"meter\":\"local\"}");
    await WriteConfigurationFileAsync(
        Path.Combine(triggerScope, "config.xml"),
        "<triggers>local</triggers>");
    await WriteConfigurationFileAsync(
        Path.Combine(triggerScope, "local-only.xml"),
        "<local />");
    Directory.Delete(Path.Combine(configurationRoot, matchaScope), recursive: true);
    await WriteConfigurationFileAsync(
        Path.Combine(absentScope, "local.json"),
        "{\"local\":true}");
    await WriteConfigurationFileAsync(
        Path.Combine("logs", "ffxiv", "Network_test.log"),
        "local combat log");

    var rollbackArchive = Path.Combine(archiveDirectory, "before-cloud-restore.dactbackup");
    var restored = await service.RestoreAsync(
        exportedArchive,
        configurationRoot,
        rollbackArchive,
        CancellationToken.None);
    Assert(
        restored.ScopeCount == 4 &&
        restored.FileCount == 3 &&
        File.Exists(rollbackArchive) &&
        await File.ReadAllTextAsync(Path.Combine(configurationRoot, dactScope)) ==
        "{\"meter\":\"cloud\"}" &&
        await File.ReadAllTextAsync(Path.Combine(configurationRoot, triggerScope, "config.xml")) ==
        "<triggers>cloud</triggers>" &&
        !File.Exists(Path.Combine(configurationRoot, triggerScope, "local-only.xml")) &&
        await File.ReadAllTextAsync(
            Path.Combine(configurationRoot, matchaScope, "profiles", "default.json")) ==
        "{\"profile\":\"cloud\"}" &&
        !Directory.Exists(Path.Combine(configurationRoot, absentScope)) &&
        await File.ReadAllTextAsync(
            Path.Combine(configurationRoot, "logs", "ffxiv", "Network_test.log")) ==
        "local combat log",
        "Portable configuration restore did not replace only the declared scopes.");

    var undoRollbackArchive = Path.Combine(archiveDirectory, "before-manual-undo.dactbackup");
    await service.RestoreAsync(
        rollbackArchive,
        configurationRoot,
        undoRollbackArchive,
        CancellationToken.None);
    Assert(
        await File.ReadAllTextAsync(Path.Combine(configurationRoot, dactScope)) ==
        "{\"meter\":\"local\"}" &&
        await File.ReadAllTextAsync(Path.Combine(configurationRoot, triggerScope, "config.xml")) ==
        "<triggers>local</triggers>" &&
        File.Exists(Path.Combine(configurationRoot, triggerScope, "local-only.xml")) &&
        !Directory.Exists(Path.Combine(configurationRoot, matchaScope)) &&
        await File.ReadAllTextAsync(Path.Combine(configurationRoot, absentScope, "local.json")) ==
        "{\"local\":true}" &&
        await File.ReadAllTextAsync(
            Path.Combine(configurationRoot, "logs", "ffxiv", "Network_test.log")) ==
        "local combat log",
        "The rollback snapshot could not restore the exact pre-cloud local state.");

    var failingService = new PortableConfigurationArchiveService
    {
        BeforeScopeCommit = (index, _) =>
        {
            if (index == 2)
            {
                throw new IOException("simulated cloud restore commit failure");
            }
        },
    };
    var failureRollbackArchive = Path.Combine(
        archiveDirectory,
        "before-failed-restore.dactbackup");
    var restoreFailed = false;
    try
    {
        await failingService.RestoreAsync(
            exportedArchive,
            configurationRoot,
            failureRollbackArchive,
            CancellationToken.None);
    }
    catch (IOException ex) when (ex.Message == "simulated cloud restore commit failure")
    {
        restoreFailed = true;
    }

    Assert(
        restoreFailed &&
        File.Exists(failureRollbackArchive) &&
        await File.ReadAllTextAsync(Path.Combine(configurationRoot, dactScope)) ==
        "{\"meter\":\"local\"}" &&
        await File.ReadAllTextAsync(Path.Combine(configurationRoot, triggerScope, "config.xml")) ==
        "<triggers>local</triggers>" &&
        File.Exists(Path.Combine(configurationRoot, triggerScope, "local-only.xml")) &&
        !Directory.Exists(Path.Combine(configurationRoot, matchaScope)) &&
        await File.ReadAllTextAsync(Path.Combine(configurationRoot, absentScope, "local.json")) ==
        "{\"local\":true}",
        "A mid-commit cloud restore failure did not roll every changed scope back.");

    var tamperedArchive = Path.Combine(archiveDirectory, "tampered-payload.dactbackup");
    File.Copy(exportedArchive, tamperedArchive);
    using (var archive = ZipFile.Open(tamperedArchive, ZipArchiveMode.Update))
    {
        const string triggerEntryName =
            "payload/plugin-configs/Triggernometry/config.xml";
        archive.GetEntry(triggerEntryName)?.Delete();
        var tamperedEntry = archive.CreateEntry(triggerEntryName);
        await using var tamperedWriter = new StreamWriter(tamperedEntry.Open());
        // The replacement has the same byte length, so integrity must depend on
        // the manifest hash rather than an incidental ZIP length mismatch.
        await tamperedWriter.WriteAsync("<triggers>evil!</triggers>");
    }

    var tamperedRejected = false;
    var tamperedRollback = Path.Combine(archiveDirectory, "tampered-rollback.dactbackup");
    try
    {
        await service.RestoreAsync(
            tamperedArchive,
            configurationRoot,
            tamperedRollback,
            CancellationToken.None);
    }
    catch (InvalidDataException)
    {
        tamperedRejected = true;
    }
    Assert(
        tamperedRejected &&
        !File.Exists(tamperedRollback) &&
        await File.ReadAllTextAsync(Path.Combine(configurationRoot, dactScope)) ==
        "{\"meter\":\"local\"}",
        "Portable configuration restore accepted a payload with a forged manifest hash.");

    var maliciousArchive = Path.Combine(archiveDirectory, "path-traversal.dactbackup");
    using (var archive = ZipFile.Open(maliciousArchive, ZipArchiveMode.Create))
    {
        var manifestEntry = archive.CreateEntry("manifest.json");
        await using var manifestWriter = new StreamWriter(manifestEntry.Open());
        await manifestWriter.WriteAsync(
            """
            {
              "formatVersion": 1,
              "createdAtUtc": "2026-09-02T00:00:00+00:00",
              "scopes": [
                { "relativePath": "../escaped.json", "kind": "File" }
              ],
              "files": [
                {
                  "relativePath": "../escaped.json",
                  "length": 1,
                  "sha256": "0000000000000000000000000000000000000000000000000000000000000000"
                }
              ]
            }
            """);
    }

    var traversalRejected = false;
    var maliciousRollback = Path.Combine(archiveDirectory, "malicious-rollback.dactbackup");
    try
    {
        await service.RestoreAsync(
            maliciousArchive,
            configurationRoot,
            maliciousRollback,
            CancellationToken.None);
    }
    catch (InvalidDataException)
    {
        traversalRejected = true;
    }
    Assert(
        traversalRejected &&
        !File.Exists(Path.Combine(testRoot, "escaped.json")) &&
        !File.Exists(maliciousRollback),
        "Portable configuration restore accepted a path traversal or mutated state before validation.");
}

static async Task ValidateEncryptedConfigurationBackupAsync(string testRoot)
{
    var fixtureRoot = Path.Combine(testRoot, "encrypted-configuration-backup");
    var configurationRoot = Path.Combine(fixtureRoot, "pluginConfigs");
    var pluginConfigurationDirectory = Path.Combine(configurationRoot, "DalamudActCompat");
    var archiveDirectory = Path.Combine(fixtureRoot, "archives");
    Directory.CreateDirectory(pluginConfigurationDirectory);
    Directory.CreateDirectory(archiveDirectory);

    static async Task WriteFileAsync(string root, string relativePath, string contents)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, contents);
    }

    const string cloudSecret = "cloud-fflogs-secret-for-encryption-test";
    var mainConfigurationPath = Path.Combine(configurationRoot, "DalamudActCompat.json");
    var cloudConfiguration = new JObject
    {
        ["Version"] = 16,
        ["LogDirectory"] = @"D:\old-computer\network-logs",
        ["ActPluginDirectory"] = @"D:\old-computer\act-plugins",
        ["HistoryLimit"] = 73,
        ["CloudMarker"] = "cloud",
        ["Fflogs"] = new JObject
        {
            ["Enabled"] = true,
            ["ClientId"] = "cloud-client",
            ["ClientSecret"] = cloudSecret,
        },
        ["ActPluginPermissions"] = new JObject
        {
            ["triggernometry"] = new JObject { ["HighRiskScript"] = true },
        },
    };
    await File.WriteAllTextAsync(
        mainConfigurationPath,
        cloudConfiguration.ToString(Newtonsoft.Json.Formatting.Indented));
    await WriteFileAsync(
        pluginConfigurationDirectory,
        "RainbowMage.OverlayPlugin.config.json",
        "{\"overlay\":\"cloud\"}");
    await WriteFileAsync(
        pluginConfigurationDirectory,
        Path.Combine("Config", "Triggernometry.config.xml"),
        "<triggers>cloud</triggers>");
    await WriteFileAsync(
        pluginConfigurationDirectory,
        Path.Combine("Config", "Cafe.Matcha.config"),
        "{\"matcha\":\"cloud\"}");
    await WriteFileAsync(
        pluginConfigurationDirectory,
        Path.Combine("Config", "PostNamazu.config.xml"),
        "<postnamazu>cloud</postnamazu>");
    await WriteFileAsync(
        pluginConfigurationDirectory,
        Path.Combine("Config", "ACT.FoxTTS.config.xml"),
        "<foxtts>cloud</foxtts>");
    await WriteFileAsync(
        pluginConfigurationDirectory,
        Path.Combine("SilverDasher", "config.json"),
        "{\"silverdasher\":\"cloud\"}");
    await WriteFileAsync(
        pluginConfigurationDirectory,
        Path.Combine("cactbot_user", "raidboss", "data", "user.js"),
        "export const cloud = true;");
    await WriteFileAsync(
        pluginConfigurationDirectory,
        Path.Combine("logs", "ffxiv", "Network_private.log"),
        "cloud log must never enter the backup");
    await WriteFileAsync(
        pluginConfigurationDirectory,
        Path.Combine("host", "machine.dll"),
        "machine binary must never enter the backup");
    await WriteFileAsync(
        pluginConfigurationDirectory,
        "RainbowMage.OverlayPlugin.config.json.backup",
        "old local backup must never enter the cloud backup");
    await WriteFileAsync(
        pluginConfigurationDirectory,
        "cloud-account.dat",
        "old-computer-protected-account");
    await WriteFileAsync(
        pluginConfigurationDirectory,
        "cloud-device.dat",
        "old-computer-device");

    var service = new PortableConfigurationBackupService();
    Assert(
        service.IncludedPaths.Count == 8 &&
        service.IncludedPaths.Contains(
            "DalamudActCompat/SilverDasher/config.json",
            StringComparer.OrdinalIgnoreCase),
        "The encrypted backup whitelist does not contain the agreed DACT/plugin scopes.");
    var recoveryKey = service.GenerateRecoveryKey();
    var encryptedArchive = Path.Combine(archiveDirectory, "cloud.dactcloud");
    var exported = await service.ExportEncryptedAsync(
        pluginConfigurationDirectory,
        encryptedArchive,
        recoveryKey,
        CancellationToken.None);
    var duplicateArchive = Path.Combine(archiveDirectory, "cloud-duplicate.dactcloud");
    var duplicateExport = await service.ExportEncryptedAsync(
        pluginConfigurationDirectory,
        duplicateArchive,
        recoveryKey,
        CancellationToken.None);
    var encryptedBytes = await File.ReadAllBytesAsync(encryptedArchive);
    Assert(
        exported.ScopeCount == 8 &&
        exported.FileCount == 8 &&
        exported.UncompressedBytes > 0 &&
        exported.EncryptedBytes == encryptedBytes.Length &&
        Regex.IsMatch(exported.ContentId, "^[A-Za-z0-9_-]{43}$") &&
        duplicateExport.ContentId == exported.ContentId &&
        !File.ReadAllBytes(duplicateArchive).SequenceEqual(encryptedBytes) &&
        !encryptedBytes.AsSpan().StartsWith("PK"u8) &&
        encryptedBytes.AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes(cloudSecret)) < 0,
        "The cloud backup encryption or deterministic content identity was invalid.");
    Assert(
        service.IsIncludedPath(pluginConfigurationDirectory, mainConfigurationPath) &&
        service.IsIncludedPath(
            pluginConfigurationDirectory,
            Path.Combine(pluginConfigurationDirectory, "cactbot_user", "raidboss.js")) &&
        !service.IsIncludedPath(
            pluginConfigurationDirectory,
            Path.Combine(pluginConfigurationDirectory, "logs", "ffxiv", "Network.log")),
        "Automatic cloud synchronization watched files outside its portable whitelist.");

    const string localLogDirectory = @"E:\new-computer\network-logs";
    const string localPluginDirectory = @"E:\new-computer\act-plugins";
    var localConfiguration = new JObject
    {
        ["Version"] = 16,
        ["LogDirectory"] = localLogDirectory,
        ["ActPluginDirectory"] = localPluginDirectory,
        ["HistoryLimit"] = 12,
        ["CloudMarker"] = "local",
        ["Fflogs"] = new JObject
        {
            ["ClientId"] = "local-client",
            ["ClientSecret"] = "local-secret",
        },
    };
    await File.WriteAllTextAsync(
        mainConfigurationPath,
        localConfiguration.ToString(Newtonsoft.Json.Formatting.Indented));
    await WriteFileAsync(
        pluginConfigurationDirectory,
        Path.Combine("Config", "Triggernometry.config.xml"),
        "<triggers>local</triggers>");
    await WriteFileAsync(
        pluginConfigurationDirectory,
        Path.Combine("Config", "local-only.xml"),
        "<local />");
    await WriteFileAsync(
        pluginConfigurationDirectory,
        Path.Combine("cactbot_user", "local-only.js"),
        "export const localOnly = true;");
    await WriteFileAsync(
        pluginConfigurationDirectory,
        Path.Combine("SilverDasher", "config.json"),
        "{\"silverdasher\":\"local\"}");
    await WriteFileAsync(
        pluginConfigurationDirectory,
        Path.Combine("logs", "ffxiv", "Network_private.log"),
        "local log remains local");
    await WriteFileAsync(
        pluginConfigurationDirectory,
        "cloud-account.dat",
        "new-computer-protected-account");
    await WriteFileAsync(
        pluginConfigurationDirectory,
        "cloud-device.dat",
        "new-computer-device");
    var changedArchive = Path.Combine(archiveDirectory, "cloud-changed.dactcloud");
    var changedExport = await service.ExportEncryptedAsync(
        pluginConfigurationDirectory,
        changedArchive,
        recoveryKey,
        CancellationToken.None);
    Assert(
        changedExport.ContentId != exported.ContentId,
        "Cloud content identity did not change after portable configuration changed.");

    var preview = await service.PreviewRestoreAsync(
        encryptedArchive,
        pluginConfigurationDirectory,
        recoveryKey,
        CancellationToken.None);
    Assert(
        preview.FileCount == 8 &&
        preview.ChangedFiles >= 3 &&
        preview.RemovedFiles >= 1 &&
        preview.Scopes.Count == 8,
        "Encrypted backup preview did not report the pending exact-scope changes.");

    var wrongKeyRejected = false;
    try
    {
        await service.PreviewRestoreAsync(
            encryptedArchive,
            pluginConfigurationDirectory,
            service.GenerateRecoveryKey(),
            CancellationToken.None);
    }
    catch (CryptographicException)
    {
        wrongKeyRejected = true;
    }
    Assert(
        wrongKeyRejected &&
        JObject.Parse(await File.ReadAllTextAsync(mainConfigurationPath))["CloudMarker"]?.Value<string>() ==
        "local",
        "A wrong recovery key was accepted or changed live configuration during preview.");

    var tamperedArchive = Path.Combine(archiveDirectory, "tampered.dactcloud");
    File.Copy(encryptedArchive, tamperedArchive);
    var tamperedBytes = await File.ReadAllBytesAsync(tamperedArchive);
    tamperedBytes[^1] ^= 0x5A;
    await File.WriteAllBytesAsync(tamperedArchive, tamperedBytes);
    var tamperRejected = false;
    try
    {
        await service.PreviewRestoreAsync(
            tamperedArchive,
            pluginConfigurationDirectory,
            recoveryKey,
            CancellationToken.None);
    }
    catch (CryptographicException)
    {
        tamperRejected = true;
    }
    Assert(
        tamperRejected,
        "Authenticated client-side encryption accepted a modified cloud payload.");

    var encryptedRollback = Path.Combine(archiveDirectory, "before-cloud-restore.dactcloud");
    var restored = await service.RestoreEncryptedAsync(
        encryptedArchive,
        pluginConfigurationDirectory,
        encryptedRollback,
        recoveryKey,
        CancellationToken.None);
    var restoredConfiguration = JObject.Parse(await File.ReadAllTextAsync(mainConfigurationPath));
    Assert(
        restored.ScopeCount == 8 &&
        restored.FileCount == 8 &&
        File.Exists(encryptedRollback) &&
        !File.ReadAllBytes(encryptedRollback).AsSpan().StartsWith("PK"u8) &&
        restoredConfiguration["CloudMarker"]?.Value<string>() == "cloud" &&
        restoredConfiguration["LogDirectory"]?.Value<string>() == localLogDirectory &&
        restoredConfiguration["ActPluginDirectory"]?.Value<string>() == localPluginDirectory &&
        restoredConfiguration["Fflogs"]?["ClientSecret"]?.Value<string>() == cloudSecret &&
        restoredConfiguration["ActPluginPermissions"]?["triggernometry"]?["HighRiskScript"]?
            .Value<bool>() == true &&
        await File.ReadAllTextAsync(Path.Combine(
            pluginConfigurationDirectory,
            "Config",
            "Triggernometry.config.xml")) == "<triggers>cloud</triggers>" &&
        File.Exists(Path.Combine(pluginConfigurationDirectory, "Config", "local-only.xml")) &&
        !File.Exists(Path.Combine(pluginConfigurationDirectory, "cactbot_user", "local-only.js")) &&
        await File.ReadAllTextAsync(Path.Combine(
            pluginConfigurationDirectory,
            "logs",
            "ffxiv",
            "Network_private.log")) == "local log remains local" &&
        await File.ReadAllTextAsync(Path.Combine(
            pluginConfigurationDirectory,
            "RainbowMage.OverlayPlugin.config.json.backup")) ==
        "old local backup must never enter the cloud backup" &&
        await File.ReadAllTextAsync(Path.Combine(
            pluginConfigurationDirectory,
            "cloud-account.dat")) == "new-computer-protected-account" &&
        await File.ReadAllTextAsync(Path.Combine(
            pluginConfigurationDirectory,
            "cloud-device.dat")) == "new-computer-device",
        "Encrypted restore did not apply the cloud snapshot while preserving local paths and exclusions.");

    var rollbackOfRollback = Path.Combine(archiveDirectory, "before-manual-rollback.dactcloud");
    await service.RestoreEncryptedAsync(
        encryptedRollback,
        pluginConfigurationDirectory,
        rollbackOfRollback,
        recoveryKey,
        CancellationToken.None);
    var rolledBackConfiguration = JObject.Parse(await File.ReadAllTextAsync(mainConfigurationPath));
    Assert(
        rolledBackConfiguration["CloudMarker"]?.Value<string>() == "local" &&
        rolledBackConfiguration["LogDirectory"]?.Value<string>() == localLogDirectory &&
        await File.ReadAllTextAsync(Path.Combine(
            pluginConfigurationDirectory,
            "Config",
            "Triggernometry.config.xml")) == "<triggers>local</triggers>" &&
        File.Exists(Path.Combine(pluginConfigurationDirectory, "Config", "local-only.xml")) &&
        File.Exists(Path.Combine(pluginConfigurationDirectory, "cactbot_user", "local-only.js")),
        "The encrypted pre-restore snapshot could not roll back the full local state.");

    var failingArchiveService = new PortableConfigurationArchiveService
    {
        BeforeScopeCommit = (index, _) =>
        {
            if (index == 3)
            {
                throw new IOException("simulated encrypted restore failure");
            }
        },
    };
    var failingService = new PortableConfigurationBackupService(
        failingArchiveService,
        new PortableConfigurationEncryptionService());
    var failureRollback = Path.Combine(archiveDirectory, "before-failed-restore.dactcloud");
    var restoreFailed = false;
    try
    {
        await failingService.RestoreEncryptedAsync(
            encryptedArchive,
            pluginConfigurationDirectory,
            failureRollback,
            recoveryKey,
            CancellationToken.None);
    }
    catch (IOException ex) when (ex.Message == "simulated encrypted restore failure")
    {
        restoreFailed = true;
    }
    var afterFailureConfiguration = JObject.Parse(await File.ReadAllTextAsync(mainConfigurationPath));
    Assert(
        restoreFailed &&
        File.Exists(failureRollback) &&
        afterFailureConfiguration["CloudMarker"]?.Value<string>() == "local" &&
        await File.ReadAllTextAsync(Path.Combine(
            pluginConfigurationDirectory,
            "Config",
            "Triggernometry.config.xml")) == "<triggers>local</triggers>" &&
        File.Exists(Path.Combine(pluginConfigurationDirectory, "Config", "local-only.xml")) &&
        File.Exists(Path.Combine(pluginConfigurationDirectory, "cactbot_user", "local-only.js")),
        "A failed encrypted restore did not automatically return every committed scope to its prior state.");
}

static async Task ValidateRealConfigurationBackupFixtureAsync(string testRoot)
{
    var sourcePluginConfigurationDirectory =
        Environment.GetEnvironmentVariable("DACT_REAL_CONFIGURATION_DIRECTORY");
    if (string.IsNullOrWhiteSpace(sourcePluginConfigurationDirectory))
    {
        return;
    }

    var sourceDirectory = Path.GetFullPath(sourcePluginConfigurationDirectory);
    var sourceRoot = Path.GetDirectoryName(sourceDirectory)
                     ?? throw new InvalidOperationException(
                         "Real DACT configuration fixture has no parent directory.");
    var service = new PortableConfigurationBackupService();
    var sourceFilesBefore = CaptureIncludedFileHashes(sourceRoot, service.IncludedPaths);
    var fixtureRoot = Path.Combine(testRoot, "real-configuration-backup-fixture");
    var targetRoot = Path.Combine(fixtureRoot, "pluginConfigs");
    var targetDirectory = Path.Combine(targetRoot, "DalamudActCompat");
    var archiveDirectory = Path.Combine(fixtureRoot, "archives");
    Directory.CreateDirectory(targetDirectory);
    Directory.CreateDirectory(archiveDirectory);

    foreach (var relativePath in service.IncludedPaths)
    {
        CopyFixtureEntry(
            Path.Combine(sourceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)),
            Path.Combine(targetRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
    }

    var targetMainConfiguration = Path.Combine(targetRoot, "DalamudActCompat.json");
    var originalConfiguration = JObject.Parse(await File.ReadAllTextAsync(targetMainConfiguration));
    var originalHistoryLimit = originalConfiguration["HistoryLimit"]?.Value<int>() ?? 20;
    var localLogDirectory = Path.Combine(targetDirectory, "logs", "ffxiv");
    var localPluginDirectory = Path.Combine(targetDirectory, "act-plugins-custom");
    originalConfiguration["HistoryLimit"] = originalHistoryLimit + 1;
    originalConfiguration["LogDirectory"] = localLogDirectory;
    originalConfiguration["ActPluginDirectory"] = localPluginDirectory;
    await File.WriteAllTextAsync(
        targetMainConfiguration,
        originalConfiguration.ToString(Newtonsoft.Json.Formatting.Indented));

    var changedPluginFile = service.IncludedPaths
        .Select(path => Path.Combine(targetRoot, path.Replace('/', Path.DirectorySeparatorChar)))
        .Where(path => !path.Equals(targetMainConfiguration, StringComparison.OrdinalIgnoreCase))
        .FirstOrDefault(File.Exists);
    byte[]? changedPluginBytes = null;
    if (changedPluginFile is not null)
    {
        changedPluginBytes = await File.ReadAllBytesAsync(changedPluginFile);
        await using var append = new FileStream(changedPluginFile, FileMode.Append, FileAccess.Write);
        await append.WriteAsync("\n"u8.ToArray());
    }

    var recoveryKey = service.GenerateRecoveryKey();
    var encryptedArchive = Path.Combine(archiveDirectory, "real-copy.dactcloud");
    await service.ExportEncryptedAsync(
        sourceDirectory,
        encryptedArchive,
        recoveryKey,
        CancellationToken.None);
    var preview = await service.PreviewRestoreAsync(
        encryptedArchive,
        targetDirectory,
        recoveryKey,
        CancellationToken.None);
    Assert(
        preview.ChangedFiles >= 1,
        "The real configuration copy did not expose the intentional restore difference.");

    var rollback = Path.Combine(archiveDirectory, "real-copy-rollback.dactcloud");
    await service.RestoreEncryptedAsync(
        encryptedArchive,
        targetDirectory,
        rollback,
        recoveryKey,
        CancellationToken.None);
    var restoredConfiguration = JObject.Parse(await File.ReadAllTextAsync(targetMainConfiguration));
    Assert(
        restoredConfiguration["HistoryLimit"]?.Value<int>() == originalHistoryLimit &&
        restoredConfiguration["LogDirectory"]?.Value<string>() == localLogDirectory &&
        restoredConfiguration["ActPluginDirectory"]?.Value<string>() == localPluginDirectory &&
        File.Exists(rollback),
        "The real configuration copy did not restore portable data or preserve new-machine paths.");
    if (changedPluginFile is not null && changedPluginBytes is not null)
    {
        var sourcePluginFile = Path.Combine(
            sourceRoot,
            Path.GetRelativePath(targetRoot, changedPluginFile));
        var restoredPluginHash = SHA256.HashData(
            await File.ReadAllBytesAsync(changedPluginFile));
        var sourcePluginHash = SHA256.HashData(
            await File.ReadAllBytesAsync(sourcePluginFile));
        Assert(
            restoredPluginHash.AsSpan().SequenceEqual(sourcePluginHash),
            "The real third-party plugin configuration copy was not restored byte-for-byte.");
    }

    var rollbackOfRollback = Path.Combine(archiveDirectory, "real-copy-rollback-undo.dactcloud");
    await service.RestoreEncryptedAsync(
        rollback,
        targetDirectory,
        rollbackOfRollback,
        recoveryKey,
        CancellationToken.None);
    var rolledBackConfiguration = JObject.Parse(await File.ReadAllTextAsync(targetMainConfiguration));
    Assert(
        rolledBackConfiguration["HistoryLimit"]?.Value<int>() == originalHistoryLimit + 1 &&
        sourceFilesBefore.OrderBy(static pair => pair.Key).SequenceEqual(
            CaptureIncludedFileHashes(sourceRoot, service.IncludedPaths)
                .OrderBy(static pair => pair.Key)),
        "The real configuration rollback failed or the read-only source fixture was modified.");

    static Dictionary<string, string> CaptureIncludedFileHashes(
        string root,
        IReadOnlyList<string> includedPaths)
    {
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var relativePath in includedPaths)
        {
            var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path))
            {
                hashes[relativePath] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            }
            else if (Directory.Exists(path))
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    var fileRelativePath = Path.GetRelativePath(root, file)
                        .Replace(Path.DirectorySeparatorChar, '/');
                    hashes[fileRelativePath] = Convert.ToHexString(
                        SHA256.HashData(File.ReadAllBytes(file)));
                }
            }
        }
        return hashes;
    }

    static void CopyFixtureEntry(string source, string destination)
    {
        if (File.Exists(source))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination);
            return;
        }
        if (!Directory.Exists(source))
        {
            return;
        }

        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }
}

static async Task ValidateFactoryResetRollbackAsync(string testRoot)
{
    var configRoot = Path.Combine(testRoot, "factory-reset-rollback");
    var paths = new PluginPaths(configRoot);
    paths.EnsureCreated();
    var originalFile = Path.Combine(configRoot, "user-data.txt");
    await File.WriteAllTextAsync(originalFile, "original");
    var configuration = new PluginConfiguration
    {
        DebugMode = true,
        HistoryLimit = 42,
        UiLanguage = "en",
    };
    var parser = new TestParserEngine(ParserState.Running);
    var log = DispatchProxy.Create<IPluginLog, NoOpPluginLogProxy>();
    var persistedConfiguration = Path.Combine(configRoot, "persisted-configuration.txt");
    var saveAttempts = 0;
    var service = new FactoryResetService(
        parser,
        paths,
        configuration,
        new PluginLogger(log),
        () =>
        {
            saveAttempts++;
            File.WriteAllText(
                persistedConfiguration,
                $"{configuration.DebugMode}|{configuration.HistoryLimit}|{configuration.UiLanguage}");
            if (saveAttempts == 1)
            {
                throw new IOException("simulated configuration commit failure");
            }
        });

    var failed = false;
    try
    {
        await service.ResetAsync(CancellationToken.None);
    }
    catch (IOException)
    {
        failed = true;
    }

    Assert(failed, "The simulated factory-reset commit failure was not observed.");
    Assert(
        File.Exists(originalFile) &&
        await File.ReadAllTextAsync(originalFile) == "original",
        "Factory-reset rollback did not restore files moved before the failed commit.");
    Assert(
        configuration.DebugMode &&
        configuration.HistoryLimit == 42 &&
        configuration.UiLanguage == "en",
        "Factory-reset rollback did not restore the in-memory configuration.");
    Assert(
        saveAttempts == 2 &&
        await File.ReadAllTextAsync(persistedConfiguration) == "True|42|en",
        "Factory-reset rollback did not persist the restored configuration snapshot.");
    Assert(
        parser.StopCount == 1 &&
        parser.StartCount == 1 &&
        parser.Status.State == ParserState.Running,
        "Factory-reset failure left a previously running parser stopped.");

    var partialRoot = Path.Combine(testRoot, "factory-reset-partial-staging");
    var partialPaths = new PluginPaths(partialRoot);
    partialPaths.EnsureCreated();
    var partialFiles = new Dictionary<string, string>
    {
        [Path.Combine(partialRoot, "first.cfg")] = "first",
        [Path.Combine(partialRoot, "second.cfg")] = "second",
        [Path.Combine(partialRoot, "third.cfg")] = "third",
    };
    foreach (var (path, contents) in partialFiles)
    {
        await File.WriteAllTextAsync(path, contents);
    }

    var partialConfiguration = new PluginConfiguration { HistoryLimit = 77 };
    var partialParser = new TestParserEngine(ParserState.Stopped);
    var partialSaveCalled = false;
    var partialService = new FactoryResetService(
        partialParser,
        partialPaths,
        partialConfiguration,
        new PluginLogger(log),
        () => partialSaveCalled = true);
    var stagedOriginalEntries = 0;
    partialService.StageEntry = (source, destination) =>
    {
        if (partialFiles.ContainsKey(source))
        {
            stagedOriginalEntries++;
            if (stagedOriginalEntries == 2)
            {
                throw new IOException("simulated partial staging failure");
            }
        }

        MoveTestEntry(source, destination);
    };

    var partialFailed = false;
    try
    {
        await partialService.ResetAsync(CancellationToken.None);
    }
    catch (IOException ex) when (ex.Message == "simulated partial staging failure")
    {
        partialFailed = true;
    }

    Assert(
        partialFailed && stagedOriginalEntries == 2 && !partialSaveCalled,
        "The partial factory-reset staging failure did not occur before defaults were applied.");
    foreach (var (path, contents) in partialFiles)
    {
        Assert(
            File.Exists(path) && await File.ReadAllTextAsync(path) == contents,
            $"Factory-reset partial staging rollback lost an original entry: {Path.GetFileName(path)}");
    }
    Assert(
        partialConfiguration.HistoryLimit == 77,
        "Factory-reset partial staging rollback changed the original in-memory configuration.");

    var doubleFailureRoot = Path.Combine(testRoot, "factory-reset-persist-rollback-failure");
    var doubleFailurePaths = new PluginPaths(doubleFailureRoot);
    doubleFailurePaths.EnsureCreated();
    var doubleFailureFile = Path.Combine(doubleFailureRoot, "original.cfg");
    await File.WriteAllTextAsync(doubleFailureFile, "original");
    var doubleFailureConfiguration = new PluginConfiguration { HistoryLimit = 88 };
    var doubleFailureService = new FactoryResetService(
        new TestParserEngine(ParserState.Stopped),
        doubleFailurePaths,
        doubleFailureConfiguration,
        new PluginLogger(log),
        () => throw new IOException("simulated persistent configuration failure"));
    AggregateException? rollbackAggregate = null;
    try
    {
        await doubleFailureService.ResetAsync(CancellationToken.None);
    }
    catch (AggregateException ex)
    {
        rollbackAggregate = ex;
    }

    Assert(
        rollbackAggregate is not null &&
        rollbackAggregate.Flatten().InnerExceptions.Any(error =>
            error.Message.Contains("persist", StringComparison.OrdinalIgnoreCase)) &&
        doubleFailureConfiguration.HistoryLimit == 88 &&
        File.Exists(doubleFailureFile),
        "A failed persisted rollback was swallowed or did not retain the restored in-memory/filesystem state.");

    var successRoot = Path.Combine(testRoot, "factory-reset-commit");
    var successPaths = new PluginPaths(successRoot);
    successPaths.EnsureCreated();
    var successOriginalFile = Path.Combine(successRoot, "user-data.txt");
    await File.WriteAllTextAsync(successOriginalFile, "backup-me");
    var successConfiguration = new PluginConfiguration
    {
        DebugMode = true,
        HistoryLimit = 99,
    };
    var successParser = new TestParserEngine(ParserState.Running);
    var successService = new FactoryResetService(
        successParser,
        successPaths,
        successConfiguration,
        new PluginLogger(log),
        () => File.WriteAllText(Path.Combine(successRoot, "configuration-saved.marker"), "saved"));
    var backup = await successService.ResetAsync(CancellationToken.None);
    Assert(
        File.Exists(Path.Combine(backup, "user-data.txt")) &&
        !File.Exists(successOriginalFile) &&
        File.Exists(Path.Combine(successRoot, "configuration-saved.marker")),
        "Factory reset did not commit the new layout while preserving the original files in its backup.");
    Assert(
        !successConfiguration.DebugMode &&
        successConfiguration.HistoryLimit == 20 &&
        successParser.StopCount == 1 &&
        successParser.StartCount == 0,
        "Successful factory reset changed its existing stop-and-reset semantics.");

    var shutdownRoot = Path.Combine(testRoot, "factory-reset-shutdown");
    var shutdownPaths = new PluginPaths(shutdownRoot);
    shutdownPaths.EnsureCreated();
    await File.WriteAllTextAsync(Path.Combine(shutdownRoot, "original.cfg"), "original");
    var shutdownParser = new TestParserEngine(ParserState.Running);
    using var pluginShutdown = new CancellationTokenSource();
    var shutdownService = new FactoryResetService(
        shutdownParser,
        shutdownPaths,
        new PluginConfiguration(),
        new PluginLogger(log),
        () => { });
    shutdownService.StageEntry = (_, _) =>
    {
        pluginShutdown.Cancel();
        throw new IOException("simulated staging failure during shutdown");
    };
    try
    {
        await shutdownService.ResetAsync(CancellationToken.None, pluginShutdown.Token);
        throw new InvalidOperationException("Factory reset did not expose the shutdown staging failure.");
    }
    catch (IOException ex) when (ex.Message == "simulated staging failure during shutdown")
    {
    }

    Assert(
        shutdownParser.StopCount == 1 && shutdownParser.StartCount == 0,
        "Factory-reset rollback restarted the parser after plugin shutdown began.");

    var operationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var cancellationObserved = false;
    var operationCount = 0;
    var coordinator = new FactoryResetOperationCoordinator(async shutdownToken =>
    {
        operationCount++;
        operationStarted.TrySetResult();
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, shutdownToken);
            return "unexpected";
        }
        catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
        {
            cancellationObserved = true;
            throw;
        }
    });
    var firstReset = coordinator.Start();
    var duplicateReset = coordinator.Start();
    Assert(
        ReferenceEquals(firstReset, duplicateReset),
        "Factory-reset coordinator allowed more than one simultaneous reset task.");
    await operationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
    Assert(
        await coordinator.WaitForShutdownAsync(TimeSpan.FromSeconds(2)) &&
        cancellationObserved &&
        operationCount == 1 &&
        coordinator.Start().IsCanceled,
        "Factory-reset coordinator did not cancel and join its active task during shutdown.");
}

static void MoveTestEntry(string source, string destination)
{
    if (Directory.Exists(source))
    {
        Directory.Move(source, destination);
    }
    else
    {
        File.Move(source, destination);
    }
}

static async Task ValidateBundledPluginInstallLifecycleAsync()
{
    var events = new List<string>();
    await BundledPluginInstallCoordinator.ExecuteAsync(
        hostWasRunning: true,
        parserWasRunning: true,
        _ => RecordAsync("stop-host"),
        _ => RecordAsync("stop-parser"),
        _ => RecordAsync("install"),
        _ => RecordAsync("start-host"),
        _ => RecordAsync("start-parser"),
        () => true,
        CancellationToken.None,
        TimeSpan.FromSeconds(1));
    Assert(
        events.SequenceEqual(
            ["stop-host", "stop-parser", "install", "start-host", "start-parser"]),
        "Bundled DLL installation did not pause and restore running ACT services in order.");

    events.Clear();
    var failed = false;
    try
    {
        await BundledPluginInstallCoordinator.ExecuteAsync(
            hostWasRunning: true,
            parserWasRunning: true,
            _ => RecordAsync("stop-host"),
            _ => RecordAsync("stop-parser"),
            _ => throw new InvalidOperationException("expected install failure"),
            _ => RecordAsync("start-host"),
            _ => RecordAsync("start-parser"),
            () => true,
            CancellationToken.None,
            TimeSpan.FromSeconds(1));
    }
    catch (InvalidOperationException ex) when (ex.Message == "expected install failure")
    {
        failed = true;
    }

    Assert(failed, "A bundled DLL installation failure was not observed by the caller.");
    Assert(
        events.SequenceEqual(
            ["stop-host", "stop-parser", "start-host", "start-parser"]),
        "ACT services were not restored after a bundled DLL installation failure.");

    events.Clear();
    await BundledPluginInstallCoordinator.ExecuteAsync(
        hostWasRunning: false,
        parserWasRunning: false,
        _ => RecordAsync("stop-host"),
        _ => RecordAsync("stop-parser"),
        _ => RecordAsync("install"),
        _ => RecordAsync("start-host"),
        _ => RecordAsync("start-parser"),
        () => true,
        CancellationToken.None,
        TimeSpan.FromSeconds(1));
    Assert(
        events.SequenceEqual(["install"]),
        "Bundled DLL installation changed ACT services that were already stopped.");

    Task RecordAsync(string value)
    {
        events.Add(value);
        return Task.CompletedTask;
    }
}

static async Task ValidateEncounterShutdownFlushAsync(string testRoot)
{
    var root = Path.Combine(testRoot, "encounter-shutdown-flush");
    var paths = new PluginPaths(root);
    var repository = new EncounterRepository(new JsonFileStore(), paths);
    var stateStore = new EncounterStateStore();
    var configuration = new PluginConfiguration { HistoryLimit = 5 };
    var log = DispatchProxy.Create<IPluginLog, NoOpPluginLogProxy>();
    var service = new EncounterService(
        repository,
        stateStore,
        configuration,
        new PluginLogger(log),
        paths);
    var encounter = SampleEncounterFactory.Create(DateTimeOffset.UtcNow) with
    {
        EndTime = DateTimeOffset.UtcNow,
    };
    var updatedEncounter = encounter with
    {
        EnemyName = "Updated duty folder",
        SegmentRecords = [encounter],
    };

    service.QueueFinishedEncounter(encounter);
    service.QueueFinishedEncounter(updatedEncounter);
    await service.DisposeAsync();
    await service.DisposeAsync();

    var persisted = await repository.LoadRecentAsync(CancellationToken.None);
    Assert(
        persisted.Count == 1 &&
        persisted[0].Id == encounter.Id &&
        persisted[0].EnemyName == "Updated duty folder",
        "A repeated duty-folder snapshot was duplicated or not flushed to history.");
    Assert(
        Directory.EnumerateFiles(paths.EncounterLogDirectory, "*.json").Count() == 1,
        "An encounter submitted immediately before shutdown did not write its individual log.");
}

static async Task ValidateEncounterRetentionAsync(string testRoot)
{
    var root = Path.Combine(testRoot, "encounter-retention");
    var paths = new PluginPaths(root);
    var networkDirectory = Path.Combine(root, "custom-network-logs");
    Directory.CreateDirectory(networkDirectory);
    for (var index = 0; index < 4; index++)
    {
        var path = Path.Combine(networkDirectory, $"Network_{index}.log");
        await File.WriteAllTextAsync(path, index.ToString());
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(index - 4));
    }

    var repository = new EncounterRepository(new JsonFileStore(), paths);
    var configuration = new PluginConfiguration { HistoryLimit = 2 };
    var log = DispatchProxy.Create<IPluginLog, NoOpPluginLogProxy>();
    var service = new EncounterService(
        repository,
        new EncounterStateStore(),
        configuration,
        new PluginLogger(log),
        paths,
        () => networkDirectory);
    await service.InitializeAsync(CancellationToken.None);
    var start = DateTimeOffset.UtcNow.AddMinutes(-3);
    for (var index = 0; index < 3; index++)
    {
        service.QueueFinishedEncounter(SampleEncounterFactory.Create(start.AddMinutes(index)) with
        {
            Id = Guid.NewGuid(),
            EndTime = start.AddMinutes(index).AddSeconds(30),
        });
    }
    await service.DisposeAsync();

    var history = await repository.LoadRecentAsync(CancellationToken.None);
    Assert(
        history.Count == 2 &&
        Directory.EnumerateFiles(paths.EncounterLogDirectory, "*.json").Count() == 2 &&
        Directory.EnumerateFiles(networkDirectory, "Network_*.log").Count() == 2,
        "History, encounter JSON, and Network logs were not independently retained at the configured count.");
}

static void ValidateDutyEncounterRosterReplacement()
{
    foreach (var partySize in new[] { 4, 8, 24 })
    {
        var start = new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);
        var original = CreatePartySegment(
            Guid.NewGuid(),
            start,
            start.AddSeconds(30),
            partySize,
            damage: 1_000,
            dps: 100,
            encDps: 100,
            extDps: 100);
        original = original with
        {
            EndTime = null,
            Combatants = original.Combatants
                .Append(new Combatant(
                    "limit-break",
                    "Limit Break",
                    string.Empty,
                    false,
                    250,
                    0,
                    0))
                .ToArray(),
        };
        var originalRoster = original.Combatants
            .Where(static combatant => combatant.Name != "Limit Break")
            .Select(static combatant => combatant.Id)
            .ToArray();
        var remainingRoster = originalRoster.Take(partySize - 1).ToArray();
        var replacementId = $"player-{partySize + 1}";
        var replacementRoster = remainingRoster.Append(replacementId).ToArray();
        var vacancy = original;
        var replacement = original with
        {
            Combatants = original.Combatants
                .Where(combatant => combatant.Id != $"player-{partySize}")
                .Append(new Combatant(
                    replacementId,
                    $"Player {partySize + 1}",
                    "DPS",
                    false,
                    500,
                    0,
                    0,
                    100,
                    100,
                    100))
                .ToArray(),
        };

        var accumulator = new DutyEncounterAccumulator();
        _ = accumulator.Update(
            original,
            finished: false,
            start.AddSeconds(30),
            originalRoster,
            partySize);
        var duringVacancy = accumulator.Update(
            vacancy,
            finished: false,
            start.AddSeconds(50),
            remainingRoster,
            partySize);
        Assert(
            duringVacancy.Combatants.Count == partySize + 1 &&
            duringVacancy.Combatants.Any(static combatant => combatant.Name == "Limit Break") &&
            duringVacancy.Combatants.Any(combatant => combatant.Id == $"player-{partySize}"),
            $"A {partySize}-player duty dropped the departed member before a replacement arrived.");

        var afterReplacement = accumulator.Update(
            replacement,
            finished: false,
            start.AddSeconds(70),
            replacementRoster,
            partySize);
        Assert(
            afterReplacement.Combatants.Count == partySize + 1 &&
            afterReplacement.Combatants.Any(static combatant => combatant.Name == "Limit Break") &&
            afterReplacement.Combatants.Any(combatant => combatant.Id == replacementId) &&
            afterReplacement.Combatants.All(combatant => combatant.Id != $"player-{partySize}"),
            $"A full replacement expanded a {partySize}-player duty beyond its roster capacity.");

        var completed = accumulator.Complete(start.AddSeconds(80))
                        ?? throw new InvalidOperationException(
                            $"The {partySize}-player replacement duty did not complete.");
        Assert(
            completed.Combatants.Count == partySize + 1 &&
            completed.Combatants.Any(static combatant => combatant.Name == "Limit Break") &&
            completed.Combatants.Any(combatant => combatant.Id == replacementId) &&
            completed.Combatants.All(combatant => combatant.Id != $"player-{partySize}") &&
            original.Combatants.Any(combatant => combatant.Id == $"player-{partySize}"),
            $"The {partySize}-player pull did not keep its current roster separate from earlier snapshots.");
    }
}

static void ValidateHighestDamageAggregation()
{
    var start = new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);
    Encounter Segment(int offset, string action, long amount)
        => new(
            Guid.NewGuid(),
            start.AddSeconds(offset),
            start.AddSeconds(offset + 10),
            "Test duty",
            "Test target",
            [new Combatant(
                "player",
                "Player",
                "SAM",
                true,
                1_000,
                0,
                0,
                HighestDamageAction: action,
                HighestDamage: amount,
                PartyGroup: 2)],
            [], [], [], [], []);

    var accumulator = new DutyEncounterAccumulator();
    var first = Segment(0, "First", 100);
    var equal = Segment(20, "Equal should not replace", 100);
    var higher = Segment(40, "Higher", 120);
    _ = accumulator.Update(first, finished: true, first.EndTime!.Value);
    _ = accumulator.Update(equal, finished: true, equal.EndTime!.Value);
    _ = accumulator.Update(higher, finished: true, higher.EndTime!.Value);
    var completed = accumulator.Complete(higher.EndTime.Value)
                    ?? throw new InvalidOperationException("Highest-hit duty did not complete.");
    var player = completed.Combatants.Single();
    Assert(
        player.HighestDamageAction == "Higher" &&
        player.HighestDamage == 120 &&
        player.PartyGroup == 2,
        "Duty aggregation did not replace only on a strictly higher hit or preserve alliance metadata.");

    ActGlobals.Init();
    Advanced_Combat_Tracker.Resources.NotActMainFormatter.SetupEnvironment();
    var actEncounter = new EncounterData("Player", "Test duty", false, null!);
    var actCombatant = new CombatantData("Player", actEncounter);
    actCombatant.AddCombatAction(new MasterSwing(
        2, false, 8_000, start.DateTime, 1, "Damage skill", "Player", "damage", "Target"));
    actCombatant.AddCombatAction(new MasterSwing(
        3, false, 63_300, start.AddMilliseconds(10).DateTime, 2, "Large heal", "Player", "healing", "Player"));
    var highestDamage = SelfHostedActRuntime.GetHighestDamageHit(actCombatant);
    Assert(
        highestDamage.Action == "Damage skill" && highestDamage.Amount == 8_000,
        "Highest damage selected a larger outgoing heal instead of the combatant's own damage action.");
}

static async Task ValidatePluginLifecycleShutdownAsync(string testRoot)
{
    var root = Path.Combine(testRoot, "plugin-lifecycle-shutdown");
    var paths = new PluginPaths(root);
    var configuration = new PluginConfiguration
    {
        EnableParsing = true,
        AutoStartParser = true,
    };
    var log = DispatchProxy.Create<IPluginLog, NoOpPluginLogProxy>();
    var logger = new PluginLogger(log);
    var encounterService = new EncounterService(
        new EncounterRepository(new JsonFileStore(), paths),
        new EncounterStateStore(),
        configuration,
        logger,
        paths);
    var parser = new BlockingStartupParserEngine();
    var lifecycle = new PluginLifecycle(
        parser,
        encounterService,
        paths,
        configuration,
        logger,
        TimeSpan.Zero);

    lifecycle.Start();
    await parser.StartEntered.WaitAsync(TimeSpan.FromSeconds(2));
    await lifecycle.DisposeAsync();
    await lifecycle.DisposeAsync();

    Assert(
        parser.StartCount == 1 &&
        parser.StopCount == 1 &&
        parser.StopObservedCompletedStartup,
        "Plugin shutdown did not cancel and join its exact startup task before stopping the parser.");

    await parser.DisposeAsync();
    await encounterService.DisposeAsync();

    var lockedRoot = Path.Combine(testRoot, "plugin-lifecycle-authentication-gate");
    var lockedPaths = new PluginPaths(lockedRoot);
    var lockedParser = new TestParserEngine(ParserState.Stopped);
    var lockedEncounterService = new EncounterService(
        new EncounterRepository(new JsonFileStore(), lockedPaths),
        new EncounterStateStore(),
        configuration,
        logger,
        lockedPaths);
    var lockedLifecycle = new PluginLifecycle(
        lockedParser,
        lockedEncounterService,
        lockedPaths,
        configuration,
        logger,
        TimeSpan.Zero,
        canStartParser: () => false);
    lockedLifecycle.Start();
    await lockedLifecycle.WaitForStartupAsync(CancellationToken.None);
    Assert(
        lockedParser.StartCount == 0,
        "Plugin lifecycle started the parser while the account gate was locked.");
    await lockedLifecycle.DisposeAsync();
    await lockedParser.DisposeAsync();
    await lockedEncounterService.DisposeAsync();
}

static void ValidatePluginUnloadOwnership()
{
    var projectRoot = FindProjectRoot();
    var pluginSource = File.ReadAllText(Path.Combine(
        projectRoot,
        "src",
        "DalamudActCompat",
        "Plugin",
        "Plugin.cs"));
    var lifecycleSource = File.ReadAllText(Path.Combine(
        projectRoot,
        "src",
        "DalamudActCompat",
        "Plugin",
        "PluginLifecycle.cs"));
    var runtimeSource = File.ReadAllText(Path.Combine(
        projectRoot,
        "src",
        "DalamudActCompat.ActRuntime",
        "SelfHostedActRuntime.cs"));
    var projectSource = File.ReadAllText(Path.Combine(
        projectRoot,
        "src",
        "DalamudActCompat",
        "DalamudActCompat.csproj"));
    var parserConstruction = runtimeSource.IndexOf(
        "parser = new IINACT.FfxivActPluginWrapper(",
        StringComparison.Ordinal);
    var hookConstruction = runtimeSource.IndexOf(
        "zoneDownHookManager = new IINACT.Network.ZoneDownHookManager(",
        StringComparison.Ordinal);

    Assert(
        projectSource.Contains("<CanUnloadAsync>true</CanUnloadAsync>", StringComparison.Ordinal) &&
        lifecycleSource.Contains("startupTask = Task.Run", StringComparison.Ordinal) &&
        lifecycleSource.Contains("await trackedStartup.ConfigureAwait(false);", StringComparison.Ordinal) &&
        pluginSource.Contains("await bundledActPluginInitializationTask.ConfigureAwait(false);", StringComparison.Ordinal) &&
        pluginSource.Contains("await independentHostStartupTask.ConfigureAwait(false);", StringComparison.Ordinal) &&
        pluginSource.Contains("await ShutdownBackgroundOperationsAsync().ConfigureAwait(false);", StringComparison.Ordinal) &&
        pluginSource.Contains("DisposeComponentsAsync().GetAwaiter().GetResult();", StringComparison.Ordinal) &&
        !pluginSource.Contains("_ = Task.Run", StringComparison.Ordinal) &&
        !pluginSource.Contains("shutdown exceeded 250 ms", StringComparison.OrdinalIgnoreCase) &&
        !pluginSource.Contains("hostCommandWorker.WaitAsync", StringComparison.Ordinal) &&
        !pluginSource.Contains("completion.WaitAsync(TimeSpan.FromSeconds(5))", StringComparison.Ordinal) &&
        parserConstruction >= 0 &&
        hookConstruction > parserConstruction,
        "Plugin unload can still return around a live startup task/hook, or the parser hook is enabled before dependency loading completes.");
}

static async Task ValidateAtomicEncounterStateUpdatesAsync()
{
    var stateStore = new EncounterStateStore();
    var start = DateTimeOffset.UtcNow;
    var current = SampleEncounterFactory.Create(start);
    var recent = SampleEncounterFactory.Create(start.AddMinutes(-1)) with
    {
        EndTime = start,
    };
    using var gate = new ManualResetEventSlim();

    var updateCurrent = Task.Run(() =>
    {
        gate.Wait();
        for (var iteration = 0; iteration < 1000; iteration++)
        {
            stateStore.UpdateCurrent(current);
        }
    });
    var updateRecent = Task.Run(() =>
    {
        gate.Wait();
        for (var iteration = 0; iteration < 1000; iteration++)
        {
            stateStore.UpdateRecent([recent]);
        }
    });

    gate.Set();
    await Task.WhenAll(updateCurrent, updateRecent);
    var snapshot = stateStore.GetSnapshot();
    Assert(
        snapshot.Current?.Id == current.Id &&
        snapshot.Recent.Count == 1 &&
        snapshot.Recent[0].Id == recent.Id,
        "Concurrent Current and Recent encounter updates overwrote one another.");
}

static async Task ValidateBundledCactbotPortableInstallAsync(string testRoot)
{
    var pluginDirectory = Path.Combine(testRoot, "portable-plugin");
    var bundleDirectory = Path.Combine(
        pluginDirectory,
        BundledCactbotManager.DirectoryName);
    Directory.CreateDirectory(bundleDirectory);
    var archiveName = "cactbot-test.zip";
    var archivePath = Path.Combine(bundleDirectory, archiveName);
    var builtRuntime = Path.Combine(
        FindProjectRoot(),
        "src",
        "DalamudActCompat",
        "bin",
        "Release",
        "DalamudActCompat.ActRuntime.dll");
    var assemblyBytes = await File.ReadAllBytesAsync(builtRuntime);
    using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
    {
        await WriteArchiveEntryAsync(
            archive,
            "cactbot/cactbot/CactbotOverlay.dll",
            assemblyBytes);
        await WriteArchiveEntryAsync(
            archive,
            "cactbot/cactbot/ui/raidboss/raidboss.html",
            "<html></html>"u8.ToArray());
    }

    var version = System.Diagnostics.FileVersionInfo.GetVersionInfo(builtRuntime).FileVersion!;
    var sha256 = Convert
        .ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(archivePath)))
        .ToLowerInvariant();
    var lockPath = Path.Combine(
        bundleDirectory,
        BundledCactbotManager.LockFileName);
    await File.WriteAllTextAsync(
        lockPath,
        JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            version,
            projectUrl = "https://github.com/OverlayPlugin/cactbot",
            downloadUrl = "https://example.invalid/cactbot-test.zip",
            relativeArchive = archiveName,
            sha256,
        }));

    var otherUserConfig = Path.Combine(testRoot, "other-user", "plugin-config");
    var paths = new PluginPaths(otherUserConfig);
    var installer = new CactbotPackageInstaller(paths);
    var manager = new BundledCactbotManager(pluginDirectory, installer);
    Assert(
        await manager.EnsureCurrentAsync(CancellationToken.None),
        "Fresh user did not receive bundled Cactbot.");
    Assert(
        installer.IsInstalled &&
        File.Exists(Path.Combine(
            otherUserConfig,
            "cactbot",
            "ui",
            "raidboss",
            "raidboss.html")),
        "Bundled Cactbot was not installed under the supplied user's config path.");
    Assert(
        !await manager.EnsureCurrentAsync(CancellationToken.None),
        "Current bundled Cactbot was unnecessarily reinstalled.");
}

static async Task ValidateOfficialBundledCactbotAsync(string testRoot)
{
    var pluginOutputDirectory = Path.Combine(
        FindProjectRoot(),
        "src",
        "DalamudActCompat",
        "bin",
        "Release");
    var paths = new PluginPaths(Path.Combine(
        testRoot,
        "official-bundle-user",
        "plugin-config"));
    var installer = new CactbotPackageInstaller(paths);
    var manager = new BundledCactbotManager(pluginOutputDirectory, installer);
    Assert(
        await manager.EnsureCurrentAsync(CancellationToken.None),
        "Official bundled Cactbot was not installed for a fresh user.");
    Assert(
        installer.IsInstalled &&
        installer.InstalledVersion is { } installedVersion &&
        installedVersion >= Version.Parse(manager.BundledVersion),
        "Official bundled Cactbot version or layout is invalid after installation.");
}

static async Task ValidateLiveBundledPluginUpdateCheckAsync(string testRoot)
{
    var bundleParent = Path.Combine(
        FindProjectRoot(),
        "src",
        "DalamudActCompat",
        "bin",
        "Release");
    var paths = new PluginPaths(Path.Combine(testRoot, "live-update-config"));
    var installer = new ActPluginPackageInstaller(paths);
    var configuration = new DalamudActCompat.Plugin.PluginConfiguration();
    var manager = new BundledActPluginManager(
        bundleParent,
        "0.2.31.0",
        installer,
        configuration);
    using var checker = new BundledActPluginUpdateChecker(
        paths.BundledPluginUpdateCacheDirectory);
    var check = await checker.CheckAsync(
        manager.Plugins,
        CancellationToken.None);
    Assert(
        check.Failures.Count == 0,
        "Live author-source DLL checking failed: " +
        string.Join("; ", check.Failures));
    Assert(
        check.Updates.Count == 0,
        "The bundled DLL lock is stale compared with the live author sources.");
}

static void ValidateFfxivEntityDeltaBuilder()
{
    var baselineAt = DateTimeOffset.UtcNow;
    var player = new HostFfxivCombatant(
        Id: 0x10000001,
        OwnerId: 0,
        Type: 1,
        Job: 19,
        Level: 100,
        Name: "Delta Player",
        CurrentHp: 100_000,
        MaxHp: 100_000,
        CurrentMp: 10_000,
        MaxMp: 10_000,
        CurrentCp: 0,
        MaxCp: 0,
        CurrentGp: 0,
        MaxGp: 0,
        IsCasting: true,
        CastId: 123,
        CastTargetId: 0x40000001,
        CastTime: 1,
        MaxCastTime: 3,
        PosX: 1,
        PosY: 2,
        PosZ: 3,
        Heading: 0.5f,
        CurrentWorldId: 21,
        WorldId: 21,
        WorldName: "Ravana",
        BNpcNameId: 0,
        BNpcId: 0,
        TargetId: 0x40000001,
        EffectiveDistance: 1,
        PartyType: 1,
        Address: 0x12345678,
        Statuses: [new HostFfxivStatus(1191, 1, 20, 0x10000001)]);
    var baseline = new HostFfxivEntitySnapshot(1, player.Id, baselineAt, [player]);

    var tickingOnly = player with
    {
        CastTime = 1.03f,
        Statuses = [new HostFfxivStatus(1191, 1, 19.97f, 0x10000001)],
    };
    var tickingDelta = FfxivEntitySnapshotBuilder.BuildDelta(
        baseline,
        new HostFfxivEntitySnapshot(
            1,
            player.Id,
            baselineAt.AddMilliseconds(30),
            [tickingOnly]));
    Assert(
        tickingDelta.Upserts.Count == 0 && tickingDelta.RemovedIds.Count == 0,
        "Continuously ticking cast/effect clocks escaped into the high-frequency entity delta.");

    var moved = player with { PosX = 4 };
    var spawned = player with
    {
        Id = 0x40000002,
        Type = 2,
        Name = "Delta Boss",
        Address = 0x23456789,
    };
    var changedDelta = FfxivEntitySnapshotBuilder.BuildDelta(
        baseline,
        new HostFfxivEntitySnapshot(
            1,
            player.Id,
            baselineAt.AddMilliseconds(60),
            [moved, spawned]));
    Assert(
        changedDelta.BaseTimestamp == baseline.Timestamp &&
        changedDelta.Upserts.Select(combatant => combatant.Id).ToHashSet().SetEquals(
            [player.Id, spawned.Id]) &&
        changedDelta.RemovedIds.Count == 0,
        "Position changes or newly spawned entities were omitted from the incremental update.");

    var latestDuplicate = moved with { PosX = 5 };
    var invalidPlaceholder = moved with
    {
        Id = 0xE0000000,
        Name = "Invalid Placeholder",
    };
    var normalizedDelta = FfxivEntitySnapshotBuilder.BuildDelta(
        baseline,
        new HostFfxivEntitySnapshot(
            1,
            player.Id,
            baselineAt.AddMilliseconds(75),
            [moved, latestDuplicate, invalidPlaceholder, invalidPlaceholder]));
    Assert(
        normalizedDelta.Upserts.Count == 1 &&
        normalizedDelta.Upserts[0].Id == player.Id &&
        normalizedDelta.Upserts[0].PosX == latestDuplicate.PosX,
        "Duplicate or invalid object-table identities escaped incremental normalization.");

    var removedDelta = FfxivEntitySnapshotBuilder.BuildDelta(
        baseline,
        new HostFfxivEntitySnapshot(
            1,
            player.Id,
            baselineAt.AddMilliseconds(90),
            []));
    Assert(
        removedDelta.RemovedIds.SequenceEqual([player.Id]),
        "Entity removal was omitted from the incremental update.");
}

static void ValidatePlayerIdentityResolution()
{
    ActPlayerIdentity[] identities =
    [
        new("Same Name", "Alpha", "PLD", true, false),
        new("Same Name", "Beta", "WHM", false, false),
        new("Unique Name", "Gamma", "DRG", false, true),
    ];

    Assert(
        ActPlayerIdentityResolver.Resolve(identities, "Same Name@Beta")?.Job == "WHM",
        "Cross-world player identity did not resolve by UserName@ServerName.");
    Assert(
        ActPlayerIdentityResolver.Resolve(identities, "Same Name") is null,
        "Ambiguous cross-world player name should not be guessed.");
    Assert(
        ActPlayerIdentityResolver.Resolve(identities, "Unique Name")?.DisplayName == "Unique Name@Gamma",
        "Unique player name did not resolve to its world-qualified display name.");
    Assert(
        ActPlayerIdentityResolver.Resolve(identities, "YOU")?.DisplayName == "Same Name@Alpha",
        "ACT's YOU alias did not resolve to the local player.");
    Assert(
        ActPlayerIdentityResolver.Resolve(identities, "Summon") is null,
        "Unknown pets or NPCs must not resolve as players.");
}

static void ValidateCombatEventScoping()
{
    ActPlayerIdentity[] identities =
    [
        new("Local Player", "Alpha", "PLD", true, false),
        new("Party Member", "Beta", "WHM", false, false),
    ];

    Assert(
        SelfHostedActRuntime.IsTrackedCombatantEvent(
            "Local Player@Alpha",
            "Training Dummy",
            identities),
        "A local-player combat event was rejected by encounter scoping.");
    Assert(
        SelfHostedActRuntime.IsTrackedCombatantEvent(
            "Enemy",
            "Party Member@Beta",
            identities),
        "An event targeting a party member was rejected by encounter scoping.");
    Assert(
        !SelfHostedActRuntime.IsTrackedCombatantEvent(
            "Nearby Stranger",
            "Training Dummy",
            identities),
        "A nearby stranger can still extend the local ACT encounter.");

    var now = DateTimeOffset.UtcNow;
    Assert(
        !OpenWorldEncounterEndPolicy.ShouldEnd(
            EncounterMode.OpenWorld,
            localPlayerInCombat: false,
            lastRelevantCombatAction: now.AddSeconds(-4),
            now) &&
        OpenWorldEncounterEndPolicy.ShouldEnd(
            EncounterMode.OpenWorld,
            localPlayerInCombat: false,
            lastRelevantCombatAction: now.AddSeconds(-5),
            now) &&
        !OpenWorldEncounterEndPolicy.ShouldEnd(
            EncounterMode.DutyAttempt,
            false,
            now.AddMinutes(-1),
            now) &&
        !OpenWorldEncounterEndPolicy.ShouldEnd(
            EncounterMode.LargeScaleFieldDuty,
            false,
            now.AddMinutes(-1),
            now) &&
        !OpenWorldEncounterEndPolicy.ShouldEnd(
            EncounterMode.OpenWorld,
            true,
            now.AddMinutes(-1),
            now),
        "An outdoor party encounter still follows the local combat flag instead of bounded inactivity.");

    var runtimeSource = File.ReadAllText(Path.Combine(
        FindProjectRoot(),
        "src",
        "DalamudActCompat.ActRuntime",
        "SelfHostedActRuntime.cs"));
    Assert(
        runtimeSource.Contains("encounterModeSnapshot()", StringComparison.Ordinal) &&
        runtimeSource.Contains("activeEncounterMode != gameState.Mode", StringComparison.Ordinal) &&
        runtimeSource.Contains("lastRelevantCombatAction", StringComparison.Ordinal) &&
        runtimeSource.Contains("OpenWorldEncounterEndPolicy.ShouldEnd", StringComparison.Ordinal) &&
        runtimeSource.Contains("ActGlobals.oFormActMain.EndCombat(true)", StringComparison.Ordinal) &&
        runtimeSource.Contains("counter.DirectHits++;", StringComparison.Ordinal) &&
        runtimeSource.Contains("chatDirectHitTotals", StringComparison.Ordinal),
        "Combat scoping or direct-hit collection regressed in the self-hosted ACT runtime.");
}

static void ValidateEncounterModePolicy()
{
    Assert(
        new uint[] { 26, 38, 41, 47, 48, 61 }
            .All(EncounterModeStateProvider.IsExplorationIntendedUse) &&
        !EncounterModeStateProvider.IsExplorationIntendedUse(52) &&
        !EncounterModeStateProvider.IsExplorationIntendedUse(53),
        "The exploration TIU set is incomplete or incorrectly includes Delubrum Reginae.");
    Assert(
        EncounterModePolicy.Resolve(
            EncounterMode.OpenWorld,
            isLoading: false,
            territoryKnown: true,
            explorationTerritory: true,
            boundByDuty: true,
            largeScaleDynamicEventInside: false,
            baldesionArsenalInside: false) == EncounterMode.OpenWorld,
        "An exploration-zone FATE can still be classified as a duty by BoundByDuty.");
    Assert(
        EncounterModePolicy.Resolve(
            EncounterMode.OpenWorld,
            isLoading: false,
            territoryKnown: true,
            explorationTerritory: false,
            boundByDuty: true,
            largeScaleDynamicEventInside: false,
            baldesionArsenalInside: false) == EncounterMode.DutyAttempt,
        "A normal duty no longer enters the attempt accumulator.");
    Assert(
        EncounterModePolicy.Resolve(
            EncounterMode.OpenWorld,
            isLoading: false,
            territoryKnown: true,
            explorationTerritory: true,
            boundByDuty: true,
            largeScaleDynamicEventInside: true,
            baldesionArsenalInside: false) == EncounterMode.LargeScaleFieldDuty &&
        EncounterModePolicy.Resolve(
            EncounterMode.OpenWorld,
            isLoading: false,
            territoryKnown: true,
            explorationTerritory: true,
            boundByDuty: true,
            largeScaleDynamicEventInside: false,
            baldesionArsenalInside: true) == EncounterMode.LargeScaleFieldDuty,
        "A dynamic large-scale duty or Baldesion Arsenal map was not recognized.");
    Assert(
        EncounterModePolicy.Resolve(
            EncounterMode.LargeScaleFieldDuty,
            isLoading: true,
            territoryKnown: true,
            explorationTerritory: true,
            boundByDuty: false,
            largeScaleDynamicEventInside: false,
            baldesionArsenalInside: false) == EncounterMode.LargeScaleFieldDuty &&
        EncounterModePolicy.Resolve(
            EncounterMode.OpenWorld,
            isLoading: true,
            territoryKnown: true,
            explorationTerritory: true,
            boundByDuty: true,
            largeScaleDynamicEventInside: false,
            baldesionArsenalInside: false) == EncounterMode.OpenWorld,
        "Loading either splits a confirmed large-scale duty or promotes an unconfirmed registration.");
    Assert(
        EncounterModePolicy.Resolve(
            EncounterMode.OpenWorld,
            isLoading: false,
            territoryKnown: false,
            explorationTerritory: false,
            boundByDuty: true,
            largeScaleDynamicEventInside: false,
            baldesionArsenalInside: false) == EncounterMode.DutyAttempt,
        "Unknown territory metadata no longer preserves BoundByDuty compatibility.");
    Assert(
        IinactAdapter.CanAccumulateSegment(
            EncounterMode.DutyAttempt,
            EncounterMode.DutyAttempt,
            EncounterMode.DutyAttempt) &&
        IinactAdapter.CanAccumulateSegment(
            EncounterMode.LargeScaleFieldDuty,
            EncounterMode.LargeScaleFieldDuty,
            null) &&
        !IinactAdapter.CanAccumulateSegment(
            EncounterMode.LargeScaleFieldDuty,
            EncounterMode.OpenWorld,
            null) &&
        !IinactAdapter.CanAccumulateSegment(
            EncounterMode.DutyAttempt,
            EncounterMode.LargeScaleFieldDuty,
            EncounterMode.LargeScaleFieldDuty),
        "A late ACT callback can still reopen a finalized or different-mode accumulator.");
    Assert(
        IinactAdapter.ShouldFinalizeAccumulatedMode(
            EncounterMode.DutyAttempt,
            EncounterMode.OpenWorld) &&
        IinactAdapter.ShouldFinalizeAccumulatedMode(
            EncounterMode.LargeScaleFieldDuty,
            EncounterMode.OpenWorld) &&
        !IinactAdapter.ShouldFinalizeAccumulatedMode(
            EncounterMode.OpenWorld,
            EncounterMode.LargeScaleFieldDuty) &&
        !IinactAdapter.ShouldFinalizeAccumulatedMode(
            EncounterMode.LargeScaleFieldDuty,
            EncounterMode.LargeScaleFieldDuty),
        "Encounter-mode boundaries no longer finalize exactly the accumulated side.");

    var projectRoot = FindProjectRoot();
    var providerSource = File.ReadAllText(Path.Combine(
        projectRoot,
        "src",
        "DalamudActCompat",
        "Parser",
        "EncounterModeStateProvider.cs"));
    var adapterSource = File.ReadAllText(Path.Combine(
        projectRoot,
        "src",
        "DalamudActCompat",
        "Parser",
        "IinactAdapter.cs"));
    Assert(
        providerSource.Contains("26, 38, 41, 47, 48, 61", StringComparison.Ordinal) &&
        providerSource.Contains("row.EventType.RowId == 4", StringComparison.Ordinal) &&
        providerSource.Contains("GetCurrentEvent()", StringComparison.Ordinal) &&
        providerSource.Contains("DynamicEventState.Inactive", StringComparison.Ordinal) &&
        providerSource.Contains("row.PlaceName.RowId != mainPlaceName", StringComparison.Ordinal) &&
        !providerSource.Contains("520, 521, 524, 525, 526, 527", StringComparison.Ordinal) &&
        adapterSource.Contains("gameState.Mode == EncounterMode.DutyAttempt", StringComparison.Ordinal) &&
        adapterSource.Contains("lock (encounterModeTransitionLock)", StringComparison.Ordinal) &&
        adapterSource.Contains("A local eight-player party wipe never represents", StringComparison.Ordinal),
        "The table-driven field-operation classifier or large-scale wipe boundary regressed.");
}

static void ValidateParserFrameworkStateOwnership()
{
    var projectRoot = FindProjectRoot();
    var adapterSource = File.ReadAllText(Path.Combine(
        projectRoot,
        "src",
        "DalamudActCompat",
        "Parser",
        "IinactAdapter.cs"));
    var runtimeSource = File.ReadAllText(Path.Combine(
        projectRoot,
        "src",
        "DalamudActCompat.ActRuntime",
        "SelfHostedActRuntime.cs"));
    var pluginSource = File.ReadAllText(Path.Combine(
        projectRoot,
        "src",
        "DalamudActCompat",
        "Plugin",
        "Plugin.cs"));
    var encounterStart = adapterSource.IndexOf(
        "private void OnEncounterChanged(",
        StringComparison.Ordinal);
    var frameworkStart = adapterSource.IndexOf(
        "private void OnFrameworkUpdate(",
        StringComparison.Ordinal);
    var encounterSource = encounterStart >= 0 && frameworkStart > encounterStart
        ? adapterSource[encounterStart..frameworkStart]
        : string.Empty;
    var overlayStart = runtimeSource.IndexOf(
        "public void StartOverlay()",
        StringComparison.Ordinal);
    var overlayEnd = runtimeSource.IndexOf(
        "public void StopOverlay()",
        StringComparison.Ordinal);
    var overlaySource = overlayStart >= 0 && overlayEnd > overlayStart
        ? runtimeSource[overlayStart..overlayEnd]
        : string.Empty;

    Assert(
        encounterSource.Contains("ReadFrameworkGameState()", StringComparison.Ordinal) &&
        !encounterSource.Contains("getTerritoryId()", StringComparison.Ordinal) &&
        !encounterSource.Contains("isBoundByDuty()", StringComparison.Ordinal) &&
        !encounterSource.Contains("isInCombat()", StringComparison.Ordinal) &&
        adapterSource.Contains("var gameState = getEncounterModeSnapshot();", StringComparison.Ordinal) &&
        pluginSource.Contains("encounterModeStateProvider.Update();", StringComparison.Ordinal) &&
        pluginSource.Contains("encounterModeStateProvider.Read", StringComparison.Ordinal) &&
        adapterSource.IndexOf("actRuntime.StartParser", StringComparison.Ordinal) is var runtimeStartIndex &&
        runtimeStartIndex >= 0 &&
        runtimeStartIndex < adapterSource.IndexOf(
            "SubscribeFrameworkUpdates();",
            StringComparison.Ordinal) &&
        pluginSource.IndexOf(
            "framework.Update += OnFrameworkUpdateForHost",
            StringComparison.Ordinal) is var providerSubscriptionIndex &&
        providerSubscriptionIndex >= 0 &&
        providerSubscriptionIndex < pluginSource.IndexOf(
            "new IinactAdapter(",
            StringComparison.Ordinal) &&
        overlaySource.Contains("frameworkInCombat", StringComparison.Ordinal) &&
        !overlaySource.Contains("condition[", StringComparison.Ordinal) &&
        !pluginSource.Contains("() => playerState.CharacterName", StringComparison.Ordinal) &&
        pluginSource.Contains(
            "FirstOrDefault(static identity => identity.IsLocalPlayer)?.Name",
            StringComparison.Ordinal),
        "Parser startup or ACT encounter callbacks can still read game state outside Framework.Update.");
}

static void ValidateRaidDpsEstimator()
{
    var start = DateTimeOffset.UtcNow;
    var estimator = new RaidDpsEstimator();
    static string TechnicalFinishAction(string actionId) =>
        $"22|time|10000001|Dancer|{actionId}|Technical Finish|10000001|Dancer|0|0|0|0|0|0|0|0|0|0|0|0|0|0|0|0";

    Assert(
        RaidDpsEstimator.IsDamageSwingType(1) &&
        RaidDpsEstimator.IsDamageSwingType(2) &&
        !RaidDpsEstimator.IsDamageSwingType(3),
        "ACT healing swings can still enter the DPS/rDPS damage window.");
    estimator.StartEncounter(start);
    estimator.ObserveNetworkLine(start, TechnicalFinishAction("81C1"));
    estimator.ObserveStatusLine(
        start,
        "26|time|71E|技巧舞步结束|20.00|10000001|Dancer|10000002|Paladin@Alpha|");
    estimator.ObserveDamage(
        start.AddSeconds(1),
        "Paladin",
        "Boss",
        1_030,
        critical: false,
        directHit: false);
    Assert(
        Math.Abs(estimator.ResolveDamageAdjustment("Paladin@Alpha") + 30) < 0.001 &&
        Math.Abs(estimator.ResolveDamageAdjustment("Dancer") - 30) < 0.001 &&
        Math.Abs(
            estimator.ResolveDamageAdjustment("Paladin") +
            estimator.ResolveDamageAdjustment("Dancer")) < 0.001,
        "Three-step Technical Finish was not attributed at 3%.");

    estimator.ObserveStatusLine(
        start.AddSeconds(2),
        "30|time|71E|技巧舞步结束|0.00|10000001|Dancer|10000002|Paladin|");
    estimator.ObserveDamage(
        start.AddSeconds(3),
        "Paladin",
        "Boss",
        1_050,
        critical: false,
        directHit: false);
    Assert(
        Math.Abs(estimator.ResolveDamageAdjustment("Paladin") + 30) < 0.001,
        "A removed raid buff continued changing rDPS attribution.");

    estimator.Reset();
    estimator.StartEncounter(start);
    estimator.ObserveNetworkLine(start, TechnicalFinishAction("81C2"));
    estimator.ObserveStatusLine(
        start,
        "26|time|71E|Technical Finish|20.00|10000001|Dancer|10000002|Paladin|");
    estimator.ObserveDamage(
        start.AddSeconds(1),
        "Paladin",
        "Boss",
        1_050,
        critical: false,
        directHit: false);
    Assert(
        Math.Abs(estimator.ResolveReceivedDamage("Paladin") - 50) < 0.001 &&
        Math.Abs(estimator.ResolveContributedDamage("Dancer") - 50) < 0.001,
        "Four-step Technical Finish was not attributed at 5%.");

    estimator.Reset();
    estimator.StartEncounter(start);
    estimator.ObserveNetworkLine(start, TechnicalFinishAction("81C1"));
    estimator.ObserveStatusLine(
        start,
        "26|time|71E|Technical Finish|20.00|10000001|Dancer|10000002|Paladin|");
    estimator.ObserveStatusLine(
        start,
        "26|time|839|Standard Finish|60.00|10000001|Dancer|10000002|Paladin|");
    estimator.ObserveDamage(
        start.AddSeconds(1),
        "Paladin",
        "Boss",
        10_815,
        critical: false,
        directHit: false);
    var overlapTotals = estimator.ResolveAttributionTotals(
        RaidDpsEstimator.AttributionKind.Percentage);
    Assert(
        Math.Abs(estimator.ResolveReceivedDamage("Paladin") - 815) < 0.001 &&
        Math.Abs(estimator.ResolveContributedDamage("Dancer") - 815) < 0.001 &&
        Math.Abs(overlapTotals.Received - overlapTotals.Contributed) < 0.001,
        "Standard and three-step Technical overlap stopped using multiplicative conservation.");

    estimator.Reset();
    estimator.StartEncounter(start);
    estimator.ObserveNetworkLine(start, TechnicalFinishAction("81C1"));
    estimator.ObserveStatusLine(
        start,
        "26|time|71E|Technical Finish|20.00|10000001|Dancer|10000002|Sage|");
    estimator.ObserveStatusLine(
        start.AddSeconds(1),
        "26|time|1234|Eukrasian Dosis III|30.00|10000002|Sage|40000001|Boss|");
    estimator.ObserveStatusLine(
        start.AddSeconds(2),
        "30|time|71E|Technical Finish|0.00|10000001|Dancer|10000002|Sage|");
    estimator.ObserveEffectiveDamage(
        new EffectiveDamageEvent(
            start.AddSeconds(4),
            "10000002",
            "Sage",
            string.Empty,
            "40000001",
            "Boss",
            "Eukrasian Dosis III (*)",
            1_030,
            Critical: false,
            DirectHit: false,
            IsPeriodic: true),
        "Sage");
    Assert(
        Math.Abs(estimator.ResolveContributedDamage("Dancer") - 30) < 0.001,
        "A periodic snapshot did not retain its three-step Technical multiplier.");

    estimator.Reset();
    estimator.StartEncounter(start);
    estimator.ObserveNetworkLine(start, TechnicalFinishAction("81C2"));
    estimator.ObserveStatusLine(
        start,
        "26|time|71E|Technical Finish|20.00|10000001|Dancer|10000002|Sage|");
    estimator.ObserveStatusLine(
        start.AddSeconds(1),
        "26|time|1234|Eukrasian Dosis III|30.00|10000002|Sage|40000001|Boss|");
    estimator.ObserveStatusLine(
        start.AddSeconds(2),
        "30|time|71E|Technical Finish|0.00|10000001|Dancer|10000002|Sage|");
    estimator.ObserveEffectiveDamage(
        new EffectiveDamageEvent(
            start.AddSeconds(4),
            "10000002",
            "Sage",
            string.Empty,
            "40000001",
            "Boss",
            "Eukrasian Dosis III (*)",
            1_050,
            Critical: false,
            DirectHit: false,
            IsPeriodic: true),
        "Sage");
    Assert(
        Math.Abs(estimator.ResolveContributedDamage("Dancer") - 50) < 0.001,
        "A periodic snapshot did not retain its four-step Technical multiplier.");

    estimator.Reset();
    estimator.StartEncounter(start);
    estimator.ObserveStatusLine(
        start,
        "26|time|312|战斗连祷|20.00|10000003|Dragoon|10000002|Paladin|");
    estimator.ObserveDamage(
        start.AddSeconds(1),
        "Paladin",
        "Boss",
        1_600,
        critical: true,
        directHit: false);
    var paladinAdjustment = estimator.ResolveDamageAdjustment("Paladin");
    var dragoonAdjustment = estimator.ResolveDamageAdjustment("Dragoon");
    Assert(
        paladinAdjustment < 0 && dragoonAdjustment > 0 &&
        Math.Abs(paladinAdjustment + dragoonAdjustment) < 0.001,
        "Critical-hit raid-buff attribution was not positive and damage-conserving.");

    var percentageFirst = new RaidDpsEstimator(
        ownershipModel: RaidDpsOwnershipModel.PercentageFirst);
    var sharedBaseLog = new RaidDpsEstimator(
        ownershipModel: RaidDpsOwnershipModel.SharedBaseLog);
    foreach (var ownershipEstimator in new[] { percentageFirst, sharedBaseLog })
    {
        ownershipEstimator.StartEncounter(start);
        ownershipEstimator.ObserveNetworkLine(start, TechnicalFinishAction("81C2"));
        ownershipEstimator.ObserveStatusLine(
            start,
            "26|time|71E|Technical Finish|20.00|10000001|Dancer|10000002|Paladin|");
        ownershipEstimator.ObserveStatusLine(
            start,
            "26|time|312|Battle Litany|20.00|10000003|Dragoon|10000002|Paladin|");
        ownershipEstimator.ObserveDamage(
            start.AddSeconds(1),
            "Paladin",
            "Boss",
            1_600,
            critical: true,
            directHit: false);
    }
    var currentPercentage = percentageFirst.ResolveContributedDamage(
        "Dancer",
        RaidDpsEstimator.AttributionKind.Percentage);
    var sharedPercentage = sharedBaseLog.ResolveContributedDamage(
        "Dancer",
        RaidDpsEstimator.AttributionKind.Percentage);
    var currentTotal = percentageFirst.ResolveReceivedDamage("Paladin");
    var sharedTotal = sharedBaseLog.ResolveReceivedDamage("Paladin");
    Assert(
        sharedPercentage < currentPercentage &&
        Math.Abs(sharedTotal - currentTotal) < 0.001 &&
        Math.Abs(
            sharedBaseLog.ResolveAttributionTotals().Received -
            sharedBaseLog.ResolveAttributionTotals().Contributed) < 0.001,
        "SharedBaseLog did not reassign only the percentage/rate interaction while preserving total conservation.");

    estimator.Reset();
    estimator.StartEncounter(start);
    estimator.ObserveNetworkLine(start, TechnicalFinishAction("81C2"));
    estimator.ObserveStatusLine(
        start,
        "26|time|71E|技巧舞步结束|20.00|10000001|Dancer|10000001|Dancer|");
    estimator.ObserveDamage(
        start.AddSeconds(1),
        "Dancer",
        "Boss",
        1_050,
        critical: false,
        directHit: false);
    Assert(
        Math.Abs(estimator.ResolveContributedDamage("Dancer")) < 0.001 &&
        Math.Abs(estimator.ResolveDamageAdjustment("Dancer")) < 0.001,
        "Dancer self Technical damage was incorrectly treated as rDPS contribution.");

    estimator.Reset();
    estimator.StartEncounter(start);
    estimator.ObserveDamage(
        start.AddSeconds(2),
        "Paladin",
        "Boss",
        500,
        critical: false,
        directHit: false);
    estimator.ObserveDamage(
        start.AddSeconds(12),
        "Paladin",
        "Boss",
        500,
        critical: false,
        directHit: false);
    Assert(
        Math.Abs(estimator.ResolveRate("Paladin", 1_000, 30) - (1_000d / 30)) < 0.001,
        "rDPS did not use the authoritative DamageMetricDuration supplied by the encounter tracker.");

    estimator.Reset();
    estimator.StartEncounter(start);
    estimator.ObserveNetworkLine(
        start,
        "03|time|10000009|Summoner|1B|64|0000|434|Test World|");
    estimator.ObserveNetworkLine(
        start,
        "03|time|40000001|Solar Bahamut|00|64|10000009|00||");
    Assert(
        estimator.TryResolvePetOwner("Solar Bahamut", out var petOwner) &&
        petOwner == "Summoner",
        "A party pet was not resolved back to its owner from AddCombatant data.");
    estimator.ObserveNetworkLine(start, TechnicalFinishAction("81C2"));
    estimator.ObserveStatusLine(
        start,
        "26|time|71E|Technical Finish|20.00|10000001|Dancer|10000009|Summoner|");
    estimator.ObserveStatusLine(
        start,
        "26|time|71E|Technical Finish|20.00|10000001|Dancer|40000001|Solar Bahamut|");
    estimator.ObserveDamage(
        start.AddSeconds(1),
        "Summoner",
        "Boss",
        1_050,
        critical: false,
        directHit: false,
        damageSourceName: "Solar Bahamut");
    var petTechnicalTotals = estimator.ResolveAttributionTotals(
        RaidDpsEstimator.AttributionKind.Percentage);
    Assert(
        Math.Abs(estimator.ResolveReceivedDamage("Summoner") - 50) < 0.001 &&
        Math.Abs(estimator.ResolveContributedDamage("Dancer") - 50) < 0.001 &&
        Math.Abs(petTechnicalTotals.Received - petTechnicalTotals.Contributed) < 0.001,
        "Pet damage under Technical was not owner-attributed exactly once and conserved.");

    estimator.Reset();
    estimator.StartEncounter(start);
    estimator.ObserveNetworkLine(
        start,
        "03|time|10000001|Summoner|1B|64|0000|434|Test World|");
    estimator.ObserveNetworkLine(
        start,
        "03|time|40000001|Solar Bahamut|00|64|10000001|00||");
    estimator.ObserveStatusLine(
        start,
        "26|time|F2F|The Balance|20.00|10000002|Astrologian|10000001|Summoner|");
    estimator.ObserveStatusLine(
        start,
        "26|time|F2F|The Balance|20.00|10000002|Astrologian|40000001|Solar Bahamut|");
    estimator.ObserveDamage(
        start.AddSeconds(1),
        "Summoner",
        "Boss",
        1_030,
        critical: false,
        directHit: false,
        damageSourceName: "Solar Bahamut");
    Assert(
        Math.Abs(estimator.ResolveDamageAdjustment("Summoner") + 30) < 0.001 &&
        Math.Abs(estimator.ResolveDamageAdjustment("Astrologian") - 30) < 0.001,
        "The Balance did not use its official 3% off-role multiplier for pet-owner damage.");

    estimator.Reset();
    estimator.StartEncounter(start);
    estimator.ObserveNetworkLine(
        start,
        "03|time|10000001|Paladin|13|64|0000|434|Test World|");
    estimator.ObserveNetworkLine(
        start,
        "03|time|10000002|Black Mage|19|64|0000|434|Test World|");
    estimator.ObserveStatusLine(
        start,
        "26|time|F2F|The Balance|20.00|10000003|Astrologian|10000001|Paladin|");
    estimator.ObserveStatusLine(
        start,
        "26|time|F31|The Spear|20.00|10000003|Astrologian|10000002|Black Mage|");
    estimator.ObserveDamage(
        start.AddSeconds(1),
        "Paladin",
        "Boss",
        1_060,
        critical: false,
        directHit: false);
    estimator.ObserveDamage(
        start.AddSeconds(1),
        "Black Mage",
        "Boss",
        1_060,
        critical: false,
        directHit: false);
    Assert(
        Math.Abs(estimator.ResolveContributedDamage("Astrologian") - 120) < 0.001,
        "AST cards did not retain their official 6% multipliers on matching target roles.");

    estimator.Reset();
    estimator.StartEncounter(start);
    estimator.ObserveNetworkLine(
        start,
        "03|time|10000001|Paladin|13|64|0000|434|Test World|");
    estimator.ObserveNetworkLine(
        start,
        "03|time|10000002|Black Mage|19|64|0000|434|Test World|");
    estimator.ObserveStatusLine(
        start,
        "26|time|F2F|The Balance|20.00|10000003|Astrologian|10000002|Black Mage|");
    estimator.ObserveStatusLine(
        start,
        "26|time|F31|The Spear|20.00|10000003|Astrologian|10000001|Paladin|");
    estimator.ObserveDamage(
        start.AddSeconds(1),
        "Paladin",
        "Boss",
        1_030,
        critical: false,
        directHit: false);
    estimator.ObserveDamage(
        start.AddSeconds(1),
        "Black Mage",
        "Boss",
        1_030,
        critical: false,
        directHit: false);
    Assert(
        Math.Abs(estimator.ResolveContributedDamage("Astrologian") - 60) < 0.001,
        "AST cards did not use their official 3% multipliers on off-role targets.");

    estimator.Reset();
    estimator.StartEncounter(start);
    estimator.ObserveStatusLine(
        start,
        "26|time|8A9|Mage's Ballad|45.00|10000003|Bard|10000002|Sage|");
    estimator.ObserveDamage(
        start.AddSeconds(1),
        "Sage",
        "Boss",
        1_010,
        critical: false,
        directHit: false);
    Assert(
        Math.Abs(estimator.ResolveReceivedDamage("Sage") - 10) < 0.001 &&
        Math.Abs(estimator.ResolveContributedDamage("Bard") - 10) < 0.001,
        "Mage's Ballad was not attributed at its official fixed 1% multiplier.");

    estimator.Reset();
    estimator.StartEncounter(start);
    estimator.ObserveStatusLine(
        start,
        "26|time|511|Embolden|20.00|10000003|Red Mage|10000002|Sage|");
    estimator.ObserveStatusLine(
        start.AddSeconds(1),
        "26|time|1234|Eukrasian Dosis III|30.00|10000002|Sage|40000001|Boss|");
    estimator.ObserveStatusLine(
        start.AddSeconds(2),
        "30|time|511|Embolden|0.00|10000003|Red Mage|10000002|Sage|");
    estimator.ObserveEffectiveDamage(
        new EffectiveDamageEvent(
            start.AddSeconds(4),
            "10000002",
            "Sage",
            string.Empty,
            "40000001",
            "Boss",
            "Eukrasian Dosis III (*)",
            1_050,
            Critical: false,
            DirectHit: false,
            IsPeriodic: true),
        "Sage");
    var dotSnapshotTransfer = 50d;
    var attributionTotals = estimator.ResolveAttributionTotals();
    Assert(
        Math.Abs(estimator.ResolveReceivedDamage("Sage") - dotSnapshotTransfer) < 0.001 &&
        Math.Abs(estimator.ResolveContributedDamage("Red Mage") - dotSnapshotTransfer) < 0.001 &&
        Math.Abs(attributionTotals.Received - attributionTotals.Contributed) < 0.001,
        "Percentage DoT attribution did not retain its application-time external buff snapshot.");

    estimator.Reset();
    estimator.StartEncounter(start);
    estimator.ObserveStatusLine(
        start,
        "26|time|1235|Dia|30.00|10000004|White Mage|40000001|Boss|");
    estimator.ObserveStatusLine(
        start.AddSeconds(1),
        "26|time|511|Embolden|20.00|10000003|Red Mage|10000004|White Mage|");
    estimator.ObserveEffectiveDamage(
        new EffectiveDamageEvent(
            start.AddSeconds(4),
            "10000004",
            "White Mage",
            string.Empty,
            "40000001",
            "Boss",
            "Dia (DoT)",
            1_050,
            Critical: false,
            DirectHit: false,
            IsPeriodic: true),
        "White Mage");
    estimator.ObserveEffectiveDamage(
        new EffectiveDamageEvent(
            start.AddSeconds(4),
            "10000004",
            "White Mage",
            string.Empty,
            "40000001",
            "Boss",
            "Glare",
            1_050,
            Critical: false,
            DirectHit: false,
            IsPeriodic: false),
        "White Mage");
    Assert(
        Math.Abs(estimator.ResolveReceivedDamage("White Mage") - 50) < 0.001,
        "An unbuffed DoT snapshot used tick-time buffs or direct damage stopped using hit-time buffs.");

    estimator.Reset();
    estimator.StartEncounter(start);
    estimator.ObserveStatusLine(
        start,
        "26|time|511|Embolden|20.00|10000003|Red Mage|10000005|Dark Knight|");
    estimator.ObserveStatusLine(
        start.AddSeconds(1),
        "26|time|2ED|Salted Earth|15.00|10000005|Dark Knight|10000005|Dark Knight|");
    estimator.ObserveStatusLine(
        start.AddSeconds(2),
        "30|time|511|Embolden|0.00|10000003|Red Mage|10000005|Dark Knight|");
    estimator.ObserveEffectiveDamage(
        new EffectiveDamageEvent(
            start.AddSeconds(4),
            "10000005",
            "Dark Knight",
            string.Empty,
            "40000001",
            "Boss",
            "Salted Earth",
            1_050,
            Critical: false,
            DirectHit: false,
            IsPeriodic: true),
        "Dark Knight");
    Assert(
        Math.Abs(estimator.ResolveReceivedDamage("Dark Knight") - 50) < 0.001,
        "A self-status ground effect did not reuse its application snapshot on enemy ticks.");

    estimator.Reset();
    estimator.StartEncounter(start);
    var guaranteedSamurai =
        "21|time|10000006|Samurai|1D3F|Midare Setsugekka|40000001|Boss|000003|00640000|0|0|0|0|0|0|0|0|0|0|0|0|0|0|500|500|10000|10000|||0|0|0|0|100000|100000|10000|10000|||0|0|0|0|00000011|0|raw-11";
    estimator.ObserveNetworkLine(start.AddSeconds(1), guaranteedSamurai);
    estimator.ObserveEffectiveDamage(
        new EffectiveDamageEvent(
            start.AddSeconds(1),
            "10000006",
            "Samurai",
            string.Empty,
            "40000001",
            "Boss",
            "Midare Setsugekka",
            100,
            Critical: true,
            DirectHit: false,
            IsPeriodic: false),
        "Samurai");
    var samuraiBaseline = estimator.ResolveHitBaseline("Samurai");
    Assert(
        samuraiBaseline.CriticalSamples == 0 &&
        samuraiBaseline.DirectHitSamples == 1 &&
        samuraiBaseline.DirectHits == 0,
        "A guaranteed-Crit action polluted Crit samples or incorrectly removed its natural DH sample.");

    estimator.ObserveStatusLine(
        start.AddSeconds(2),
        "26|time|4C5|Chain Stratagem|20.00|10000007|Scholar|40000001|Boss|");
    estimator.ObserveNetworkLine(start.AddSeconds(3), guaranteedSamurai);
    estimator.ObserveEffectiveDamage(
        new EffectiveDamageEvent(
            start.AddSeconds(3),
            "10000006",
            "Samurai",
            string.Empty,
            "40000001",
            "Boss",
            "Midare Setsugekka",
            1_600,
            Critical: true,
            DirectHit: false,
            IsPeriodic: false),
        "Samurai");
    var deterministicCriticalTransfer = 1_600 - (1_600 / 1.0375);
    Assert(
        Math.Abs(
            estimator.ResolveContributedDamage(
                "Scholar",
                RaidDpsEstimator.AttributionKind.Critical) -
            deterministicCriticalTransfer) < 0.001,
        "Guaranteed Crit damage did not use deterministic rate-buff scaling.");

    estimator.Reset();
    estimator.StartEncounter(start);
    estimator.ObserveStatusLine(
        start,
        "26|time|353|Reassemble|5.00|10000008|Machinist|10000008|Machinist|");
    var reassembledDrill =
        "21|time|10000008|Machinist|4072|Drill|40000001|Boss|000003|00640000|0|0|0|0|0|0|0|0|0|0|0|0|0|0|500|500|10000|10000|||0|0|0|0|100000|100000|10000|10000|||0|0|0|0|00000012|0|raw-12";
    estimator.ObserveNetworkLine(start.AddSeconds(1), reassembledDrill);
    estimator.ObserveEffectiveDamage(
        new EffectiveDamageEvent(
            start.AddSeconds(1),
            "10000008",
            "Machinist",
            string.Empty,
            "40000001",
            "Boss",
            "Drill",
            100,
            Critical: true,
            DirectHit: true,
            IsPeriodic: false),
        "Machinist");
    var machinistBaseline = estimator.ResolveHitBaseline("Machinist");
    Assert(
        machinistBaseline.CriticalSamples == 0 && machinistBaseline.DirectHitSamples == 0,
        "A Reassemble-guaranteed CDH action polluted a natural HitBaseline dimension.");

    estimator.Reset();
    estimator.StartEncounter(start);
    estimator.ObserveStatusLine(
        start,
        "26|time|721|Devilment|20.00|10000009|Dancer|10000008|Machinist|");
    var fullMetalField =
        "21|time|10000008|Machinist|9076|Full Metal Field|40000001|Boss|000003|00640000|0|0|0|0|0|0|0|0|0|0|0|0|0|0|500|500|10000|10000|||0|0|0|0|100000|100000|10000|10000|||0|0|0|0|00000013|0|raw-13";
    estimator.ObserveNetworkLine(start.AddSeconds(1), fullMetalField);
    estimator.ObserveEffectiveDamage(
        new EffectiveDamageEvent(
            start.AddSeconds(1),
            "10000008",
            "Machinist",
            string.Empty,
            "40000001",
            "Boss",
            "Full Metal Field",
            1_000,
            Critical: true,
            DirectHit: true,
            IsPeriodic: false),
        "Machinist");
    var guaranteedCritical = estimator.ResolveContributedDamage(
        "Dancer",
        RaidDpsEstimator.AttributionKind.Critical);
    var guaranteedDirect = estimator.ResolveContributedDamage(
        "Dancer",
        RaidDpsEstimator.AttributionKind.DirectHit);
    var combinedDeterministicRatio = 1.075 * 1.04;
    Assert(
        guaranteedCritical > 0 && guaranteedDirect > 0 &&
        Math.Abs(
            guaranteedCritical + guaranteedDirect -
            (1_000 - (1_000 / combinedDeterministicRatio))) < 0.001,
        "A guaranteed CDH action did not retain deterministic Crit/DH overlap attribution.");

    estimator.Reset();
    estimator.StartEncounter(start);
    estimator.ObserveStatusLine(
        start,
        "26|time|4C5|Chain Stratagem|20.00|10000007|Scholar|40000001|Boss|");
    estimator.ObserveStatusLine(
        start.AddSeconds(1),
        "26|time|1236|Bioblaster|15.00|10000008|Machinist|40000001|Boss|");
    estimator.ObserveStatusLine(
        start.AddSeconds(2),
        "30|time|4C5|Chain Stratagem|0.00|10000007|Scholar|40000001|Boss|");
    estimator.ObserveEffectiveDamage(
        new EffectiveDamageEvent(
            start.AddSeconds(4),
            "10000008",
            "Machinist",
            string.Empty,
            "40000001",
            "Boss",
            "Bioblaster (*)",
            1_000,
            Critical: false,
            DirectHit: false,
            IsPeriodic: true),
        "Machinist");
    var applyInTickOut = estimator.ResolveContributedDamage(
        "Scholar",
        RaidDpsEstimator.AttributionKind.Critical);
    Assert(
        applyInTickOut > 0,
        "A periodic tick lost its application-time Crit/DH buff snapshot.");

    estimator.Reset();
    estimator.StartEncounter(start);
    estimator.ObserveStatusLine(
        start,
        "26|time|1236|Bioblaster|15.00|10000008|Machinist|40000001|Boss|");
    estimator.ObserveStatusLine(
        start.AddSeconds(1),
        "26|time|4C5|Chain Stratagem|20.00|10000007|Scholar|40000001|Boss|");
    estimator.ObserveStatusLine(
        start.AddSeconds(2),
        "26|time|1236|Bioblaster|15.00|10000008|Machinist|40000001|Boss|");
    estimator.ObserveStatusLine(
        start.AddSeconds(3),
        "30|time|4C5|Chain Stratagem|0.00|10000007|Scholar|40000001|Boss|");
    estimator.ObserveEffectiveDamage(
        new EffectiveDamageEvent(
            start.AddSeconds(4),
            "10000008",
            "Machinist",
            string.Empty,
            "40000001",
            "Boss",
            "Bioblaster (*)",
            1_000,
            Critical: false,
            DirectHit: false,
            IsPeriodic: true),
        "Machinist");
    Assert(
        estimator.ResolveContributedDamage(
            "Scholar",
            RaidDpsEstimator.AttributionKind.Critical) > 0,
        "A periodic refresh did not replace its Crit/DH application snapshot.");

    estimator.Reset();
    estimator.StartEncounter(start);
    estimator.ObserveStatusLine(
        start,
        "26|time|1236|Bioblaster|15.00|10000008|Machinist|40000001|Boss|");
    estimator.ObserveStatusLine(
        start.AddSeconds(1),
        "26|time|4C5|Chain Stratagem|20.00|10000007|Scholar|40000001|Boss|");
    estimator.ObserveEffectiveDamage(
        new EffectiveDamageEvent(
            start.AddSeconds(4),
            "10000008",
            "Machinist",
            string.Empty,
            "40000001",
            "Boss",
            "Bioblaster (*)",
            1_000,
            Critical: false,
            DirectHit: false,
            IsPeriodic: true),
        "Machinist");
    var applyOutTickIn = estimator.ResolveContributedDamage(
        "Scholar",
        RaidDpsEstimator.AttributionKind.Critical);
    var criticalTotals = estimator.ResolveAttributionTotals(RaidDpsEstimator.AttributionKind.Critical);
    Assert(
        Math.Abs(applyOutTickIn) < 0.001 &&
        Math.Abs(criticalTotals.Received - criticalTotals.Contributed) < 0.001,
        "A periodic tick inherited tick-time Crit/DH buffs or broke category conservation.");

    ValidateLifeSurgeGuaranteedCriticalState(start);
}

static void ValidateLifeSurgeGuaranteedCriticalState(DateTimeOffset start)
{
    const string dragoonId = "10000010";
    const string dancerId = "10000009";
    const string firstTargetId = "40000001";
    const string secondTargetId = "40000002";

    static RaidDpsEstimator NewEstimator() => new(actionId =>
        actionId is 0x64AB or 0x9058 or 0x0DE4 or 0x56);

    static string CriticalAction(string actionId, string actionName, string targetId) =>
        string.Join(
            '|',
            "21", "time", dragoonId, "Dragoon", actionId, actionName,
            targetId, targetId == firstTargetId ? "Boss A" : "Boss B",
            "00002003", "00640000",
            "0", "0", "0", "0", "0", "0", "0", "0",
            "0", "0", "0", "0", "0", "0");

    static EffectiveDamageEvent Damage(
        DateTimeOffset timestamp,
        string actionName,
        string targetId) =>
        new(
            timestamp,
            dragoonId,
            "Dragoon",
            string.Empty,
            targetId,
            targetId == firstTargetId ? "Boss A" : "Boss B",
            actionName,
            1_000,
            Critical: true,
            DirectHit: false,
            IsPeriodic: false);

    static void ApplyDevilmentAndLifeSurge(RaidDpsEstimator estimator, DateTimeOffset timestamp)
    {
        estimator.ObserveStatusLine(
            timestamp,
            $"26|time|721|Devilment|20.00|{dancerId}|Dancer|{dragoonId}|Dragoon|");
        estimator.ObserveStatusLine(
            timestamp,
            $"26|time|74|Life Surge|5.00|{dragoonId}|Dragoon|{dragoonId}|Dragoon|");
    }

    void AssertCoveredWeaponskill(string actionId, string actionName, bool removeBeforeAction)
    {
        var estimator = NewEstimator();
        estimator.StartEncounter(start);
        ApplyDevilmentAndLifeSurge(estimator, start);
        var hitTime = start.AddSeconds(1);
        if (removeBeforeAction)
        {
            estimator.ObserveStatusLine(
                hitTime,
                $"30|time|74|Life Surge|0.00|{dragoonId}|Dragoon|{dragoonId}|Dragoon|");
        }
        estimator.ObserveNetworkLine(hitTime, CriticalAction(actionId, actionName, firstTargetId));
        if (!removeBeforeAction)
        {
            estimator.ObserveStatusLine(
                hitTime,
                $"30|time|74|Life Surge|0.00|{dragoonId}|Dragoon|{dragoonId}|Dragoon|");
        }
        estimator.ObserveEffectiveDamage(Damage(hitTime, actionName, firstTargetId), "Dragoon");

        var expectedContribution = 1_000 - (1_000 / 1.075);
        Assert(
            Math.Abs(
                estimator.ResolveContributedDamage(
                    "Dancer",
                    RaidDpsEstimator.AttributionKind.Critical) -
                expectedContribution) < 0.001,
            $"Life Surge did not mark {actionName} as contextual guaranteed Crit.");
    }

    // Both packet orders occur in real/parser-replayed data: the effective ledger commits
    // after the action/status pair, so the consume must bind regardless of their order.
    AssertCoveredWeaponskill("64AB", "Heavens' Thrust", removeBeforeAction: false);
    AssertCoveredWeaponskill("9058", "Drakesbane", removeBeforeAction: true);
    AssertCoveredWeaponskill("DE4", "Wheeling Thrust", removeBeforeAction: false);

    var nonWeaponskill = NewEstimator();
    nonWeaponskill.StartEncounter(start);
    nonWeaponskill.ObserveStatusLine(
        start,
        $"26|time|74|Life Surge|5.00|{dragoonId}|Dragoon|{dragoonId}|Dragoon|");
    var randomCritTime = start.AddSeconds(1);
    nonWeaponskill.ObserveNetworkLine(
        randomCritTime,
        CriticalAction("4057", "High Jump", firstTargetId));
    nonWeaponskill.ObserveEffectiveDamage(
        Damage(randomCritTime, "High Jump", firstTargetId),
        "Dragoon");
    var weaponskillTime = start.AddSeconds(2);
    nonWeaponskill.ObserveNetworkLine(
        weaponskillTime,
        CriticalAction("64AB", "Heavens' Thrust", firstTargetId));
    nonWeaponskill.ObserveStatusLine(
        weaponskillTime,
        $"30|time|74|Life Surge|0.00|{dragoonId}|Dragoon|{dragoonId}|Dragoon|");
    nonWeaponskill.ObserveEffectiveDamage(
        Damage(weaponskillTime, "Heavens' Thrust", firstTargetId),
        "Dragoon");
    Assert(
        nonWeaponskill.ResolveHitBaseline("Dragoon").CriticalSamples == 1,
        "A random-Crit ability consumed Life Surge or the following weaponskill lost the guarantee.");

    var expired = NewEstimator();
    expired.StartEncounter(start);
    expired.ObserveStatusLine(
        start,
        $"26|time|74|Life Surge|1.00|{dragoonId}|Dragoon|{dragoonId}|Dragoon|");
    var afterExpiry = start.AddSeconds(2);
    expired.ObserveNetworkLine(
        afterExpiry,
        CriticalAction("64AB", "Heavens' Thrust", firstTargetId));
    expired.ObserveEffectiveDamage(Damage(afterExpiry, "Heavens' Thrust", firstTargetId), "Dragoon");
    Assert(
        expired.ResolveHitBaseline("Dragoon").CriticalSamples == 1,
        "An expired Life Surge still guaranteed a later weaponskill.");

    var removed = NewEstimator();
    removed.StartEncounter(start);
    removed.ObserveStatusLine(
        start,
        $"26|time|74|Life Surge|5.00|{dragoonId}|Dragoon|{dragoonId}|Dragoon|");
    removed.ObserveStatusLine(
        start.AddSeconds(1),
        $"30|time|74|Life Surge|0.00|{dragoonId}|Dragoon|{dragoonId}|Dragoon|");
    var afterRemove = start.AddSeconds(2);
    removed.ObserveNetworkLine(
        afterRemove,
        CriticalAction("64AB", "Heavens' Thrust", firstTargetId));
    removed.ObserveEffectiveDamage(Damage(afterRemove, "Heavens' Thrust", firstTargetId), "Dragoon");
    Assert(
        removed.ResolveHitBaseline("Dragoon").CriticalSamples == 1,
        "A Life Surge remove without a same-timestamp weaponskill leaked into a later action.");

    var multiTarget = NewEstimator();
    multiTarget.StartEncounter(start);
    ApplyDevilmentAndLifeSurge(multiTarget, start);
    var aoeTime = start.AddSeconds(1);
    multiTarget.ObserveNetworkLine(
        aoeTime,
        CriticalAction("56", "Doom Spike", firstTargetId));
    multiTarget.ObserveStatusLine(
        aoeTime,
        $"30|time|74|Life Surge|0.00|{dragoonId}|Dragoon|{dragoonId}|Dragoon|");
    multiTarget.ObserveNetworkLine(
        aoeTime,
        CriticalAction("56", "Doom Spike", secondTargetId));
    multiTarget.ObserveEffectiveDamage(Damage(aoeTime, "Doom Spike", firstTargetId), "Dragoon");
    multiTarget.ObserveEffectiveDamage(Damage(aoeTime, "Doom Spike", secondTargetId), "Dragoon");
    var expectedMultiTargetContribution = 2 * (1_000 - (1_000 / 1.075));
    Assert(
        Math.Abs(
            multiTarget.ResolveContributedDamage(
                "Dancer",
                RaidDpsEstimator.AttributionKind.Critical) -
            expectedMultiTargetContribution) < 0.001 &&
        Math.Abs(
            multiTarget.ResolveContributedDamage(
                "Dancer",
                RaidDpsEstimator.AttributionKind.DirectHit)) < 0.001,
        "Life Surge multi-target damage consumed the guarantee too early or more than once.");

    var deathReset = NewEstimator();
    deathReset.StartEncounter(start);
    deathReset.ObserveStatusLine(
        start,
        $"26|time|74|Life Surge|5.00|{dragoonId}|Dragoon|{dragoonId}|Dragoon|");
    deathReset.ObserveNetworkLine(
        start.AddSeconds(1),
        $"25|time|{dragoonId}|Dragoon|E0000000||death|");
    var afterDeath = start.AddSeconds(2);
    deathReset.ObserveNetworkLine(
        afterDeath,
        CriticalAction("64AB", "Heavens' Thrust", firstTargetId));
    deathReset.ObserveEffectiveDamage(Damage(afterDeath, "Heavens' Thrust", firstTargetId), "Dragoon");
    Assert(
        deathReset.ResolveHitBaseline("Dragoon").CriticalSamples == 1,
        "Life Surge state survived player death/reset cleanup.");
}

static void ValidateFflogsParityReplay(string testRoot)
{
    Assert(
        FflogsParityRawNormalizer.TryDecodeDamageEffect(
            "754003",
            "06FF4001",
            out var largeDamage,
            out var largeCritical,
            out var largeDirectHit) &&
        largeDamage == 67_327 &&
        !largeCritical &&
        largeDirectHit,
        "Raw parity normalizer no longer matches FFXIV_ACT_Plugin's large-damage encoding or hit flags.");
    ValidateEffectiveDamageLedger();
    ValidateEncounterDurationTracker();

    var fixtureDirectory = Path.Combine(
        FindProjectRoot(),
        "tests",
        "DalamudActCompat.PackageSmokeTests",
        "Fixtures",
        "FflogsParity");
    var rawFixture = FflogsParityReplay.ReadRawFixture(
        Path.Combine(fixtureDirectory, "two-target-transition.raw.json"));
    var rawDiagnostic = FflogsParityReplay.ReplayRaw(rawFixture);
    Assert(
        rawDiagnostic.IncludedDamage == rawFixture.ExpectedIncludedDamage,
        "Raw parity replay did not reproduce the expected DamageLedger total.");
    Assert(
        Math.Abs(
            rawDiagnostic.Durations.CurrentDamageMetricDurationSeconds -
            rawFixture.ExpectedCurrentDamageMetricDurationSeconds) < 0.001,
        "Raw parity replay did not reproduce current union-downtime duration semantics.");
    Assert(
        Math.Abs(rawDiagnostic.Durations.CandidateDamageMetricDurationSeconds - 5) < 0.001,
        "Raw parity replay did not isolate the all-targets-unavailable candidate duration.");
    Assert(
        rawDiagnostic.DamageLedger.Count(static entry =>
            entry.Decision == ParityLedgerDecision.Excluded &&
            entry.ExclusionReason == ParityExclusionReason.NonPartySource) == 1,
        "Raw parity replay did not retain a non-party damage exclusion reason.");
    Assert(
        rawDiagnostic.Actors.Single(static actor => actor.Name == "Player One") is
        {
            IncludedDamage: 6000,
            CriticalHits: 2,
            DirectHits: 2,
            CriticalDirectHits: 1,
            CriticalRate: > 0.666 and < 0.667,
            DirectHitRate: > 0.666 and < 0.667,
            CriticalDirectRate: > 0.333 and < 0.334,
        },
        "Raw parity replay lost pet ownership or critical/direct-hit aggregation.");

    var normalizedFixture = FflogsParityReplay.ReadNormalizedFixture(
        Path.Combine(fixtureDirectory, "two-target-transition.normalized.json"));
    var normalizedDiagnostic = FflogsParityReplay.ReplayNormalized(normalizedFixture);
    Assert(
        normalizedDiagnostic.IncludedDamage == normalizedFixture.ExpectedIncludedDamage,
        "Normalized parity replay did not reproduce the expected DamageLedger total.");
    Assert(
        Math.Abs(
            normalizedDiagnostic.Durations.CurrentDamageMetricDurationSeconds -
            normalizedFixture.ExpectedCurrentDamageMetricDurationSeconds) < 0.001,
        "Normalized parity replay did not reproduce current union-downtime duration semantics.");
    Assert(
        normalizedDiagnostic.Durations.FightDurationSeconds == 8,
        "Normalized parity replay changed explicit fight boundaries.");
    Assert(
        normalizedDiagnostic.DamageLedger.Any(static entry =>
            entry.ExclusionReason == ParityExclusionReason.NotDamageSwing),
        "Normalized parity replay did not explain why a non-damage swing was excluded.");

    var reportDirectory = Path.Combine(testRoot, "parity-report");
    var reportPaths = FflogsParityReportWriter.Write(reportDirectory, normalizedDiagnostic);
    Assert(
        File.Exists(reportPaths.JsonPath) && File.Exists(reportPaths.MarkdownPath),
        "Parity diagnostic report files were not written.");
    var markdown = File.ReadAllText(reportPaths.MarkdownPath);
    Assert(
        markdown.Contains("FightDuration", StringComparison.Ordinal) &&
        markdown.Contains("DamageMetricDuration", StringComparison.Ordinal) &&
        markdown.Contains("all-targets-unavailable", StringComparison.Ordinal) &&
        markdown.Contains("## Damage Ledger", StringComparison.Ordinal) &&
        markdown.Contains("NonPartySource", StringComparison.Ordinal),
        "Parity report omitted required duration, downtime, or ledger evidence.");

    var reconciliationFixture = FflogsParityReplay.ReadReconciliationFixture(
        Path.Combine(fixtureDirectory, "event-reconciliation.json"));
    var reconciliationDiagnostic = FflogsParityReplay.ReplayReconciliation(
        reconciliationFixture,
        fixtureDirectory);
    var reconciliation = reconciliationDiagnostic.Reconciliation;
    Assert(
        reconciliationDiagnostic.IncludedDamage == reconciliationFixture.ExpectedIncludedDamage,
        "Paired parity replay changed the expected included DamageLedger total.");
    Assert(
        reconciliation.Events.Count(static item => item.Status == ParityCorrelationStatus.Matched) ==
        reconciliationFixture.ExpectedMatched &&
        reconciliation.Events.Count(static item => item.Status == ParityCorrelationStatus.Ambiguous) ==
        reconciliationFixture.ExpectedAmbiguous &&
        reconciliation.Events.Count(static item => item.Status == ParityCorrelationStatus.UnmatchedRaw) ==
        reconciliationFixture.ExpectedUnmatchedRaw &&
        reconciliation.Events.Count(static item => item.Status == ParityCorrelationStatus.UnmatchedNormalized) ==
        reconciliationFixture.ExpectedUnmatchedNormalized &&
        reconciliation.Events.Count(static item => item.Status == ParityCorrelationStatus.UnmatchedReference) == 1,
        $"Paired parity replay states changed: matched={reconciliation.Events.Count(static item => item.Status == ParityCorrelationStatus.Matched)}, " +
        $"ambiguous={reconciliation.Events.Count(static item => item.Status == ParityCorrelationStatus.Ambiguous)}, " +
        $"unmatchedRaw={reconciliation.Events.Count(static item => item.Status == ParityCorrelationStatus.UnmatchedRaw)}, " +
        $"unmatchedNormalized={reconciliation.Events.Count(static item => item.Status == ParityCorrelationStatus.UnmatchedNormalized)}.");
    Assert(
        reconciliation.Conservation is
        {
            RawConserved: true,
            NormalizedConserved: true,
            OwnerAttributionConserved: true,
        } &&
        reconciliation.Conservation.RawClassifiedDamage ==
        reconciliation.Conservation.MatchedRawDamage +
        reconciliation.Conservation.IntentionallyIgnoredRawDamage +
        reconciliation.Conservation.UnmatchedRawDamage &&
        reconciliation.Conservation.UnmatchedRawDamage ==
        reconciliation.Conservation.AmbiguousRawDamage +
        reconciliation.Conservation.UnmatchedRawOnlyDamage &&
        reconciliation.Conservation.NormalizedDamage ==
        reconciliation.Conservation.IncludedLedgerDamage +
        reconciliation.Conservation.ExcludedLedgerDamage &&
        reconciliation.Conservation.SourceDamageBeforeOwnerAttribution ==
        reconciliation.Conservation.OwnerAttributedDamage,
        "Raw, normalized, or pet-owner damage conservation failed.");
    Assert(
        reconciliation.Events.Any(static item =>
            item.Status == ParityCorrelationStatus.Ambiguous &&
            item.Category == "DoTNormalizationCandidate") &&
        reconciliation.Events.Any(static item => item.Category == "FriendlyTargetCandidate") &&
        reconciliation.Events.Any(static item => item.Category == "EnvironmentCandidate") &&
        reconciliation.Events.Any(static item => item.Category == "OverkillCandidate") &&
        reconciliation.Events.Any(static item => item.TargetName == "Trash") &&
        reconciliation.Events.Count(static item => item.Category == "PetOwnerAttribution") >= 2,
        "Reconciliation fixture lost DoT, friendly, environment, overkill, target-scope, or pet-respawn coverage.");
    Assert(
        reconciliation.TopPositiveReferenceDeltaEvents.Count >= 2 &&
        reconciliation.TopPositiveReferenceDeltaEvents[0].AbilityName == "Large Hit" &&
        reconciliation.TopPositiveReferenceDeltaEvents[0].NormalizedAmount -
        reconciliation.TopPositiveReferenceDeltaEvents[0].ReferenceAmount == 100 &&
        reconciliation.TopActorDeltas.Count > 0 &&
        reconciliation.TopAbilityDeltas.Count > 0 &&
        reconciliation.TopTargetDeltas.Count > 0,
        "Exact reference pairing did not expose positive event-level deltas.");
    Assert(
        reconciliationDiagnostic.DeltaBreakdown.Any(static item =>
            item.Category.StartsWith("ParserNormalization/", StringComparison.Ordinal)),
        "Parser normalization was not expanded into event-addressable categories.");

    var jsonReference = ParityReferenceFightImporter.Read(
        Path.Combine(fixtureDirectory, "event-reconciliation.reference.json"));
    var csvReference = ParityReferenceFightImporter.Read(
        Path.Combine(fixtureDirectory, "event-reconciliation.reference.csv"));
    Assert(
        jsonReference.DamageEvents.Count == 10 &&
        csvReference.DamageEvents is [{ SourceName: "Player, One", Amount: 950, Overkill: 50 }],
        "JSON or quoted CSV reference import boundary changed.");

    var reconciliationMarkdown = FflogsParityReportWriter.BuildMarkdown(reconciliationDiagnostic);
    Assert(
        reconciliationMarkdown.Contains("## Event Reconciliation", StringComparison.Ordinal) &&
        reconciliationMarkdown.Contains("Top 50 unmatched normalized events", StringComparison.Ordinal) &&
        reconciliationMarkdown.Contains("Top actors contributing to delta", StringComparison.Ordinal) &&
        reconciliationMarkdown.Contains("Owner conservation", StringComparison.Ordinal),
        "Parity Markdown omitted Phase 1A reconciliation evidence.");
}

static void ValidateEffectiveDamageLedger()
{
    Assert(
        FfxivActionEffectDecoder.TryDecodeHealing(
            "4",
            "90448000",
            out var normalHealing,
            out var normalHealingCritical) &&
        normalHealing == 36_932 &&
        !normalHealingCritical &&
        FfxivActionEffectDecoder.TryDecodeHealing(
            "200004",
            "E47C8000",
            out var criticalHealing,
            out var criticalHealingCritical) &&
        criticalHealing == 58_492 &&
        criticalHealingCritical,
        "Raw ActionEffect healing no longer decodes normal and critical self-heals.");

    var player = new ActPlayerIdentity("Player One", string.Empty, "SMN", true, false)
    {
        EntityId = 0x10000001,
    };
    var identities = new[] { player };
    var ledger = new EffectiveDamageLedger();
    ledger.ObserveRawLine(
        DateTimeOffset.Parse("2026-08-12T20:10:00+08:00"),
        "03|2026-08-12T20:10:00+08:00|40000001|Player Pet|00|64|10000001|00||");

    var first = DateTimeOffset.Parse("2026-08-12T20:10:01+08:00");
    ledger.ObserveRawLine(
        first,
        "21|2026-08-12T20:10:01+08:00|10000001|Player One|07|Attack|40000010|Boss|000003|00640000|0|0|0|0|0|0|0|0|0|0|0|0|0|0|500|500|10000|10000|||0|0|0|0|100000|100000|10000|10000|||0|0|0|0|00000001|0|raw-01");
    ledger.StartEncounter(identities);
    ledger.ObserveNormalizedDamage(
        new NormalizedDamageCandidate(
            first,
            "10000001",
            "Player One",
            string.Empty,
            "40000010",
            "Boss",
            "Attack",
            100,
            IsDamageSwing: false,
            IsPartyOwned: true,
            IsPartyTarget: false),
        identities);
    ledger.ObserveRawLine(
        first.AddMilliseconds(500),
        "37|2026-08-12T20:10:01.500+08:00|40000010|Boss|00000001|400|");

    var rejected = first.AddSeconds(1);
    ledger.ObserveRawLine(
        rejected,
        "21|2026-08-12T20:10:02+08:00|10000001|Player One|07|Attack|40000010|Boss|000003|00190000|0|0|0|0|0|0|0|0|0|0|0|0|0|0|400|500|10000|10000|||0|0|0|0|100000|100000|10000|10000|||0|0|0|0|00000002|0|raw-02");
    ledger.ObserveNormalizedDamage(
        new NormalizedDamageCandidate(
            rejected,
            "10000001",
            "Player One",
            string.Empty,
            "40000010",
            "Boss",
            "Attack",
            25,
            IsDamageSwing: false,
            IsPartyOwned: true,
            IsPartyTarget: false),
        identities);

    var direct = first.AddSeconds(2);
    ledger.ObserveRawLine(
        direct,
        "21|2026-08-12T20:10:03+08:00|10000001|Player One|1000|Direct Hit|40000010|Boss|000003|00C80000|0|0|0|0|0|0|0|0|0|0|0|0|0|0|400|500|10000|10000|||0|0|0|0|100000|100000|10000|10000|||0|0|0|0|00000003|0|raw-03");
    ledger.ObserveRawLine(
        direct.AddMilliseconds(500),
        "37|2026-08-12T20:10:03.500+08:00|40000010|Boss|00000003|200|");

    var periodic = first.AddSeconds(3);
    ledger.ObserveRawLine(
        periodic,
        "24|2026-08-12T20:10:04+08:00|40000010|Boss|DoT|0|0064|200|500|10000|10000|||||||10000001|Player One|0|100000|100000|10000|10000|||||||raw-04");
    ledger.ObserveNormalizedDamage(
        new NormalizedDamageCandidate(
            periodic,
            "10000001",
            "Player One",
            string.Empty,
            "40000010",
            "Boss",
            "Burn (*)",
            60,
            IsDamageSwing: false,
            IsPartyOwned: true,
            IsPartyTarget: false),
        identities);
    ledger.ObserveNormalizedDamage(
        new NormalizedDamageCandidate(
            periodic,
            "40000001",
            "Player Pet",
            "10000001",
            "40000010",
            "Boss",
            "Pet Burn (*)",
            40,
            IsDamageSwing: false,
            IsPartyOwned: true,
            IsPartyTarget: false),
        identities);
    ledger.ObserveNormalizedDamage(
        new NormalizedDamageCandidate(
            periodic,
            "10000001",
            "Player One",
            string.Empty,
            "10000001",
            "Player One",
            "Shield (*)",
            500,
            IsDamageSwing: false,
            IsPartyOwned: true,
            IsPartyTarget: true),
        identities);
    ledger.ObserveNormalizedDamage(
        new NormalizedDamageCandidate(
            periodic,
            "10000001",
            "Player One",
            string.Empty,
            "40000010",
            "Boss",
            "Attack",
            30,
            IsDamageSwing: false,
            IsPartyOwned: true,
            IsPartyTarget: false),
        identities);
    ledger.ObserveRawLine(
        periodic,
        "21|2026-08-12T20:10:04+08:00|10000001|Player One|07|Attack|40000010|Boss|000003|001E0000|0|0|0|0|0|0|0|0|0|0|0|0|0|0|200|500|10000|10000|||0|0|0|0|100000|100000|10000|10000|||0|0|0|0|00000004|0|raw-04a");

    var lethal = first.AddSeconds(4);
    ledger.ObserveRawLine(
        lethal,
        "21|2026-08-12T20:10:05+08:00|40000001|Player Pet|1001|Pet Hit|40000010|Boss|000003|00960000|0|0|0|0|0|0|0|0|0|0|0|0|0|0|100|500|10000|10000|||0|0|0|0|100000|100000|10000|10000|||0|0|0|0|00000005|0|raw-05");
    ledger.ObserveRawLine(
        lethal.AddMilliseconds(500),
        "37|2026-08-12T20:10:05.500+08:00|40000010|Boss|00000005|1|");

    var overkillTick = first.AddSeconds(5);
    ledger.ObserveNormalizedDamage(
        new NormalizedDamageCandidate(
            overkillTick,
            "10000001",
            "Player One",
            string.Empty,
            "40000010",
            "Boss",
            "Burn (*)",
            50,
            IsDamageSwing: false,
            IsPartyOwned: true,
            IsPartyTarget: false),
        identities);
    ledger.ObserveRawLine(
        overkillTick,
        "24|2026-08-12T20:10:06+08:00|40000010|Boss|DoT|0|0032|1|500|10000|10000|||||||10000001|Player One|0|100000|100000|10000|10000|||||||raw-06");

    ledger.ObserveRawLine(
        first.AddSeconds(5.5),
        "21|2026-08-12T20:10:06.500+08:00|10000001|Player One|404B|Confiteor|40000010|Boss|754003|10224002|4|90448000|0|0|0|0|0|0|0|0|0|0|0|0|15051162|15394639|10000|10000|||99.96|92.82|0.00|0.03|414116|416649|10000|10000|||100.15|98.42|0.00|-3.11|00002A7B|0|1|00||01|404B|404B|0.600|0166|raw-heal");
    ledger.ObserveRawLine(
        first.AddSeconds(5.6),
        "21|2026-08-12T20:10:06.600+08:00|10000001|Player One|6494|Blade of Faith|40000010|Boss|752003|E2EF4001|200004|E47C8000|0|0|0|0|0|0|0|0|0|0|0|0|14868114|15394639|10000|10000|||99.96|92.82|0.00|0.03|416649|416649|9200|10000|||100.15|98.32|0.00|-3.11|00002A7E|0|1|00||01|6494|6494|0.600|0166|raw-heal-2");
    ledger.ObserveRawLine(
        first.AddSeconds(5.7),
        "21|2026-08-12T20:10:06.700+08:00|10000001|Player One|6495|Blade of Truth|40000010|Boss|750003|6DA84001|4|90768000|0|0|0|0|0|0|0|0|0|0|0|0|14730212|15394639|10000|10000|||99.96|92.82|0.00|0.03|416649|416649|8200|10000|||100.15|98.32|0.00|-3.11|00002A80|0|1|00||01|6495|6495|0.600|0166|raw-heal-3");
    ledger.ObserveRawLine(
        first.AddSeconds(5.8),
        "21|2026-08-12T20:10:06.800+08:00|10000001|Player One|6496|Blade of Valor|40000010|Boss|750003|B6234001|4|93B88000|0|0|0|0|0|0|0|0|0|0|0|0|14622348|15394639|10000|10000|||99.96|92.82|0.00|0.03|416649|416649|7400|10000|||100.15|98.32|0.00|-3.11|00002A83|0|1|00||01|6496|6496|0.600|0166|raw-heal-4");

    var snapshot = ledger.GetSnapshot();
    var committed = ledger.GetCommittedEventsSince(0, out var nextEventIndex);
    Assert(
        ledger.TryResolveDamage(player, out var playerDamage) &&
        playerDamage == 500 &&
        snapshot.SourceDamage == 500 &&
        snapshot.OwnerDamage == 500 &&
        snapshot.SourceTotals["10000001"] == 360 &&
        snapshot.SourceTotals["40000001"] == 140,
        "Effective DamageLedger lost confirmed autos/periodics, admitted an unconfirmed event, " +
        "failed event-level overkill exclusion, or broke pet-owner conservation.");
    Assert(
        ledger.TryResolveHealing(player, out var playerHealing) && playerHealing == 170_222,
        "Self-heals embedded in damage actions did not remain cumulative in the player's HPS numerator.");
    ledger.ObserveRawLine(
        first.AddSeconds(5.9),
        "24|2026-08-12T20:10:06.900+08:00|10000001|Player One|HoT|0|505A|416649|416649|7400|10000|||99.83|100.73|0.00|-3.11|10000001|Player One|0|416649|416649|7400|10000|||99.83|100.73|0.00|-3.11|raw-hot");
    Assert(
        ledger.TryResolveHealing(player, out playerHealing) && playerHealing == 190_792,
        "Periodic healing was not added to the same cumulative HPS numerator.");
    Assert(
        committed.Count == 5 &&
        committed.Sum(static item => item.Amount) == snapshot.OwnerDamage &&
        committed.Count(static item => item.IsPeriodic) == 2 &&
        committed[0].Timestamp == first &&
        committed.Any(static item => item.SourceId == "40000001" && item.OwnerId == "10000001") &&
        nextEventIndex == committed.Count,
        "The immutable effective-event stream diverged from ledger totals, periodics, or pet ownership.");

    var delayedPeriodicLedger = new EffectiveDamageLedger();
    delayedPeriodicLedger.StartEncounter(identities);
    var delayedPeriodic = first.AddSeconds(10);
    delayedPeriodicLedger.ObserveRawLine(
        delayedPeriodic,
        "24|2026-08-12T20:10:11+08:00|40000010|Boss|DoT|0|0064|1000|1000|10000|10000|||||||10000001|Player One|0|100000|100000|10000|10000|||||||raw-07");
    delayedPeriodicLedger.ObserveRawLine(
        delayedPeriodic.AddMilliseconds(44),
        "37|2026-08-12T20:10:11.044+08:00|40000010|Boss|00000006|900|");
    delayedPeriodicLedger.ObserveNormalizedDamage(
        new NormalizedDamageCandidate(
            delayedPeriodic,
            string.Empty,
            "Player One",
            string.Empty,
            "40000010",
            "Boss",
            "Burn (*)",
            60,
            IsDamageSwing: false,
            IsPartyOwned: true,
            IsPartyTarget: false),
        identities);
    delayedPeriodicLedger.ObserveNormalizedDamage(
        new NormalizedDamageCandidate(
            delayedPeriodic,
            "40000001",
            "Player Pet",
            "10000001",
            "40000010",
            "Boss",
            "Pet Burn (*)",
            40,
            IsDamageSwing: false,
            IsPartyOwned: true,
            IsPartyTarget: false),
        identities);
    delayedPeriodicLedger.ObserveRawLine(
        delayedPeriodic.AddSeconds(2.001),
        "39|2026-08-12T20:10:13.001+08:00|10000001|Player One|100000|100000|10000|10000||||||||raw-08");
    var delayedSnapshot = delayedPeriodicLedger.GetSnapshot();
    Assert(
        delayedPeriodicLedger.TryResolveDamage(player, out var delayedDamage) &&
        delayedDamage == 100 &&
        delayedSnapshot.OwnerDamage == 100 &&
        delayedSnapshot.SourceTotals["10000001"] == 60 &&
        delayedSnapshot.SourceTotals["40000001"] == 40,
        "Parser-delayed periodic candidates were flushed before complete player/pet attribution or remained under a name-only owner key.");
}

static void ValidateEncounterDurationTracker()
{
    var player = new ActPlayerIdentity("Player One", string.Empty, "PLD", true, false)
    {
        EntityId = 0x10000001,
    };
    var identities = new[] { player };
    var start = DateTimeOffset.Parse("2026-08-12T20:20:00+08:00");

    var rotating = new EncounterDurationTracker();
    rotating.ObserveTargetPresence(start.AddSeconds(-1), "40000010", "Boss A");
    rotating.ObserveTargetPresence(start.AddSeconds(-1), "40000011", "Boss B");
    rotating.StartEncounter(start, identities);
    rotating.ObserveConfirmedDamage(
        start,
        "10000001",
        "Player One",
        string.Empty,
        "40000010",
        "Boss A");
    rotating.ObserveTargetability(start.AddSeconds(2), "40000010", "Boss A", false);
    rotating.ObserveTargetability(start.AddSeconds(3), "40000011", "Boss B", true);
    rotating.ObserveConfirmedDamage(
        start.AddSeconds(3),
        "10000001",
        "Player One",
        string.Empty,
        "40000011",
        "Boss B");
    rotating.ObserveTargetability(start.AddSeconds(4), "40000011", "Boss B", false);
    Assert(
        rotating.IsTransitioningAt(start.AddSeconds(4.5)),
        "The duration tracker did not expose phase-global target unavailability.");
    rotating.ObserveTargetability(start.AddSeconds(5), "40000010", "Boss A", true);
    rotating.ObserveConfirmedDamage(
        start.AddSeconds(5),
        "10000001",
        "Player One",
        string.Empty,
        "40000010",
        "Boss A");
    rotating.ObserveConfirmedDamage(
        start.AddSeconds(10),
        "10000001",
        "Player One",
        string.Empty,
        "40000010",
        "Boss A");
    Assert(
        Math.Abs(rotating.ResolveFightDurationSeconds(start.AddSeconds(12)) - 12) < 0.001 &&
        Math.Abs(rotating.ResolveDamageMetricDurationSeconds(
            start.AddSeconds(12),
            useObservedDamageEnd: true) - 9) < 0.001 &&
        Math.Abs(rotating.ResolveActorActiveTimeSeconds("10000001") - 10) < 0.001,
        "FightDuration, phase-global DamageMetricDuration, and ActorActiveTime were not kept separate.");
    Assert(
        !rotating.IsTransitioningAt(start.AddSeconds(5.5)),
        "One available target did not end the phase-global transition state.");

    var adds = new EncounterDurationTracker();
    adds.ObserveTargetPresence(start.AddSeconds(-1), "40000020", "Boss");
    adds.ObserveTargetPresence(start.AddSeconds(2), "40000021", "Add");
    adds.StartEncounter(start, identities);
    adds.ObserveConfirmedDamage(start, "10000001", "Player One", string.Empty, "40000020", "Boss");
    adds.ObserveConfirmedDamage(start.AddSeconds(3), "10000001", "Player One", string.Empty, "40000021", "Add");
    adds.ObserveConfirmedDamage(
        start.AddSeconds(4),
        "10000001",
        "Player One",
        string.Empty,
        "40000021",
        "Add");
    adds.ObserveRawLine(
        start.AddSeconds(4.1),
        "25|time|40000021|Add|E0000000||death-add",
        identities);
    adds.ObserveTargetability(start.AddSeconds(6), "40000020", "Boss", false);
    adds.ObserveTargetability(start.AddSeconds(7), "40000020", "Boss", true);
    adds.ObserveConfirmedDamage(start.AddSeconds(10), "10000001", "Player One", string.Empty, "40000020", "Boss");
    Assert(
        Math.Abs(adds.ResolveDamageMetricDurationSeconds(
            start.AddSeconds(10),
            useObservedDamageEnd: true) - 9) < 0.001,
        "A defeated add remained in the historical target intersection and hid later boss downtime.");

    var hpFloor = new EncounterDurationTracker();
    hpFloor.ObserveTargetPresence(start.AddSeconds(-1), "40000060", "Floor Boss");
    hpFloor.ObserveTargetPresence(start.AddSeconds(-1), "40000061", "Other Boss");
    hpFloor.ObserveRawLine(
        start,
        "21|time|10000001|Player One|1000|Floor Hit|40000060|Floor Boss|000003|00640000|0|0|0|0|0|0|0|0|0|0|0|0|0|0|500|500|10000|10000|||0|0|0|0|100000|100000|10000|10000|||0|0|0|0|00000010|0|floor-hit",
        identities);
    hpFloor.StartEncounter(start, identities);
    hpFloor.ObserveRawLine(
        start.AddSeconds(0.1),
        "37|time|40000060|Floor Boss|00000010|1|",
        identities);
    hpFloor.ObserveTargetability(start.AddSeconds(1), "40000061", "Other Boss", false);
    hpFloor.ObserveTargetability(start.AddSeconds(2), "40000060", "Floor Boss", false);
    hpFloor.ObserveRawLine(
        start.AddSeconds(2.5),
        "24|time|40000060|Floor Boss|DoT|0|0064|1|500|10000|10000|||||||10000001|Player One|0|100000|100000|10000|10000|||||||floor-overkill",
        identities);
    hpFloor.ObserveTargetability(start.AddSeconds(3), "40000060", "Floor Boss", true);
    hpFloor.ObserveTargetability(start.AddSeconds(5), "40000061", "Other Boss", true);
    hpFloor.ObserveConfirmedDamage(
        start.AddSeconds(10),
        "10000001",
        "Player One",
        string.Empty,
        "40000061",
        "Other Boss");
    Assert(
        Math.Abs(hpFloor.ResolveDamageMetricDurationSeconds(
            start.AddSeconds(10),
            useObservedDamageEnd: true) - 8.9) < 0.001,
        "An HP floor retired a live target or hid its later targetable interval and overkill evidence.");

    var periodicFloor = new EncounterDurationTracker();
    periodicFloor.ObserveTargetPresence(start.AddSeconds(-1), "40000062", "Periodic Floor Boss");
    periodicFloor.ObserveTargetPresence(start.AddSeconds(-1), "40000063", "Periodic Other Boss");
    periodicFloor.StartEncounter(start, identities);
    periodicFloor.ObserveConfirmedDamage(
        start,
        "10000001",
        "Player One",
        string.Empty,
        "40000062",
        "Periodic Floor Boss");
    periodicFloor.ObserveRawLine(
        start.AddSeconds(1),
        "24|time|40000062|Periodic Floor Boss|DoT|0|0002|2|500|10000|10000|||||||10000001|Player One|0|100000|100000|10000|10000|||||||periodic-floor",
        identities);
    periodicFloor.ObserveTargetability(
        start.AddSeconds(2),
        "40000063",
        "Periodic Other Boss",
        false);
    periodicFloor.ObserveRawLine(
        start.AddSeconds(2.5),
        "24|time|40000062|Periodic Floor Boss|DoT|0|0002|1|500|10000|10000|||||||10000001|Player One|0|100000|100000|10000|10000|||||||periodic-overkill",
        identities);
    periodicFloor.ObserveTargetability(
        start.AddSeconds(5),
        "40000063",
        "Periodic Other Boss",
        true);
    periodicFloor.ObserveConfirmedDamage(
        start.AddSeconds(10),
        "10000001",
        "Player One",
        string.Empty,
        "40000063",
        "Periodic Other Boss");
    Assert(
        Math.Abs(periodicFloor.ResolveDamageMetricDurationSeconds(
            start.AddSeconds(10),
            useObservedDamageEnd: true) - 10) < 0.001,
        "A lethal-looking periodic or later overkill incorrectly retired target membership.");

    var despawned = new EncounterDurationTracker();
    despawned.ObserveTargetPresence(start.AddSeconds(-1), "40000022", "Despawn Boss");
    despawned.ObserveTargetPresence(start.AddSeconds(2), "40000023", "Despawn Add");
    despawned.StartEncounter(start, identities);
    despawned.ObserveConfirmedDamage(
        start,
        "10000001",
        "Player One",
        string.Empty,
        "40000022",
        "Despawn Boss");
    despawned.ObserveConfirmedDamage(
        start.AddSeconds(3),
        "10000001",
        "Player One",
        string.Empty,
        "40000023",
        "Despawn Add");
    despawned.ObserveRawLine(
        start.AddSeconds(4),
        "261|time|Remove|40000023|despawn-add",
        identities);
    despawned.ObserveTargetability(start.AddSeconds(6), "40000022", "Despawn Boss", false);
    despawned.ObserveTargetability(start.AddSeconds(7), "40000022", "Despawn Boss", true);
    despawned.ObserveConfirmedDamage(
        start.AddSeconds(10),
        "10000001",
        "Player One",
        string.Empty,
        "40000022",
        "Despawn Boss");
    Assert(
        Math.Abs(despawned.ResolveDamageMetricDurationSeconds(
            start.AddSeconds(10),
            useObservedDamageEnd: true) - 9) < 0.001,
        "An explicit despawn did not retire an add before later boss downtime.");

    var replacement = new EncounterDurationTracker();
    replacement.ObserveTargetPresence(start.AddSeconds(-1), "40000030", "Phase One");
    replacement.ObserveTargetPresence(start.AddSeconds(5), "40000031", "Phase Two");
    replacement.StartEncounter(start, identities);
    replacement.ObserveConfirmedDamage(start, "10000001", "Player One", string.Empty, "40000030", "Phase One");
    replacement.ObserveTargetability(start.AddSeconds(3), "40000030", "Phase One", false);
    replacement.ObserveConfirmedDamage(start.AddSeconds(5), "10000001", "Player One", string.Empty, "40000031", "Phase Two");
    replacement.ObserveTargetability(start.AddSeconds(7), "40000031", "Phase Two", false);
    replacement.ObserveTargetability(start.AddSeconds(8), "40000031", "Phase Two", true);
    replacement.ObserveConfirmedDamage(start.AddSeconds(10), "10000001", "Player One", string.Empty, "40000031", "Phase Two");
    Assert(
        Math.Abs(replacement.ResolveDamageMetricDurationSeconds(
            start.AddSeconds(10),
            useObservedDamageEnd: true) - 7) < 0.001,
        "A phase replacement did not retire the old target while preserving the targetless transition.");

    var departed = new EncounterDurationTracker();
    departed.ObserveTargetPresence(start.AddSeconds(-1), "40000040", "Twin A");
    departed.ObserveTargetPresence(start.AddSeconds(-1), "40000041", "Twin B");
    departed.StartEncounter(start, identities);
    departed.ObserveConfirmedDamage(start, "10000001", "Player One", string.Empty, "40000040", "Twin A");
    departed.ObserveConfirmedDamage(start.AddSeconds(1), "10000001", "Player One", string.Empty, "40000041", "Twin B");
    departed.ObserveTargetability(start.AddSeconds(3), "40000040", "Twin A", false);
    departed.ObserveTargetability(start.AddSeconds(6), "40000041", "Twin B", false);
    departed.ObserveTargetability(start.AddSeconds(7), "40000041", "Twin B", true);
    departed.ObserveConfirmedDamage(start.AddSeconds(10), "10000001", "Player One", string.Empty, "40000041", "Twin B");
    Assert(
        Math.Abs(departed.ResolveDamageMetricDurationSeconds(
            start.AddSeconds(10),
            useObservedDamageEnd: true) - 9) < 0.001,
        "A permanently departed twin polluted the current phase target set.");

    var captured = new EncounterDurationTracker();
    captured.ObserveTargetPresence(start.AddSeconds(-1), "40000050", "Red");
    captured.ObserveTargetPresence(start.AddSeconds(-1), "40000051", "Blue");
    var firstAction =
        "21|time|10000001|Player One|1000|Opening Hit|40000050|Red|000003|00640000|0|0|0|0|0|0|0|0|0|0|0|0|0|0|500|500|10000|10000|||0|0|0|0|100000|100000|10000|10000|||0|0|0|0|00000001|0|raw-01";
    captured.ObserveRawLine(start, firstAction, identities);
    captured.StartEncounter(start, identities);
    captured.ObserveRawLine(
        start.AddSeconds(0.757),
        "37|time|40000050|Red|00000001|400|",
        identities);
    captured.ObserveConfirmedDamage(
        start.AddSeconds(82),
        "10000001",
        "Player One",
        string.Empty,
        "40000051",
        "Blue");
    captured.ObserveTargetability(start.AddSeconds(79.998), "40000050", "Red", false);
    captured.ObserveTargetability(start.AddSeconds(96.303), "40000051", "Blue", false);
    captured.ObserveTargetability(start.AddSeconds(107.490), "40000051", "Blue", true);
    captured.ObserveTargetability(start.AddSeconds(148.141), "40000050", "Red", true);
    captured.ObserveTargetability(start.AddSeconds(158.081), "40000050", "Red", false);
    captured.ObserveTargetability(start.AddSeconds(158.081), "40000051", "Blue", false);
    captured.ObserveTargetability(start.AddSeconds(173.540), "40000050", "Red", true);
    captured.ObserveTargetability(start.AddSeconds(173.540), "40000051", "Blue", true);
    captured.ObserveConfirmedDamage(
        start.AddSeconds(423.540),
        "10000001",
        "Player One",
        string.Empty,
        "40000051",
        "Blue");
    captured.ObserveRawLine(
        start.AddSeconds(424),
        firstAction.Replace(
            "|00000001|0|raw-01",
            "|00000002|0|raw-02",
            StringComparison.Ordinal),
        identities);
    Assert(
        Math.Abs(captured.ResolveDamageMetricDurationSeconds(
            start.AddSeconds(424),
            useObservedDamageEnd: true) - 396.137) < 0.001,
        "The captured dual-boss boundary did not use confirmed EffectResult time or phase-global downtime.");
}

static void ValidateDalamudGameStateBridge()
{
    var mapped = ActCoordinateMapper.FromDalamud(10, 20, 30);
    Assert(
        mapped == new ActPosition(10, 30, 20),
        "Dalamud X/Y/Z was not converted to ACT X/Z/Y at the Radar boundary.");

    var provider = new CachedDalamudGameStateProvider();
    ActPlayerIdentity[] identities =
    [
        new("Local Player", "Alpha", "PLD", true, false)
        {
            EntityId = 0x10000001,
            ContentId = 0x12345678,
            WorldId = 21,
            JobId = 19,
            Level = 100,
            CurrentHp = 120_000,
            MaxHp = 120_000,
            CurrentMp = 10_000,
            MaxMp = 10_000,
            TerritoryId = 1234,
            PositionX = 10,
            PositionY = 30,
            PositionZ = 20,
            Rotation = 1.25f,
        },
        new("Party Member", "Beta", "WHM", false, false)
        {
            EntityId = 0x10000002,
            ContentId = 0x87654321,
            WorldId = 22,
            JobId = 24,
            Level = 100,
            CurrentHp = 80_000,
            MaxHp = 80_000,
            CurrentMp = 9_000,
            MaxMp = 10_000,
            TerritoryId = 1234,
            PositionX = 40,
            PositionY = 60,
            PositionZ = 50,
            Rotation = -0.75f,
        },
    ];

    var livePose = ActPlayerPose.FromDalamud(0x10000001, 11, 21, 31, 1.5f);
    provider.Update(identities, livePose, true);
    Assert(provider.Snapshot.GameExists, "Dalamud game state did not report the active game.");
    Assert(provider.Snapshot.InGameCombat, "Dalamud combat state was not preserved.");
    Assert(provider.Snapshot.Player?.JobId == 19, "Dalamud local player job was not preserved.");
    Assert(
        provider.Snapshot.Player is
        {
            PositionX: 11,
            PositionY: 31,
            PositionZ: 21,
            Rotation: 1.5f,
        },
        "Radar did not consume the frame-rate local player pose in ACT coordinates.");
    Assert(
        provider.Snapshot.Party[1] is
        {
            PositionX: 40,
            PositionY: 60,
            PositionZ: 50,
            Rotation: -0.75f,
        },
        "The lightweight local pose unexpectedly rewrote another party member.");
    Assert(
        provider.Identities[0].PositionY == 30 && provider.Identities[0].PositionZ == 20,
        "The frame-rate pose mutated the 500 ms identity snapshot.");
    Assert(provider.Snapshot.Party.Count == 2, "Dalamud party members were not preserved.");

    provider.Update(
        identities,
        ActPlayerPose.FromDalamud(0x10000009, 99, 98, 97, 2.5f),
        true);
    Assert(
        provider.Snapshot.Player is
        {
            PositionX: 10,
            PositionY: 30,
            PositionZ: 20,
            Rotation: 1.25f,
        },
        "A stale pose from another entity was applied to the local player.");

    var overlayAssembly = typeof(IDalamudGameStateProvider).Assembly;
    var partyAdapterType = overlayAssembly.GetType(
        "RainbowMage.OverlayPlugin.MemoryProcessors.Party.DalamudPartyMemory",
        throwOnError: true)!;
    var partyAdapter = (IPartyMemory)Activator.CreateInstance(
        partyAdapterType,
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
        binder: null,
        [provider],
        culture: null)!;
    var party = partyAdapter.GetPartyLists();
    Assert(party.memberCount == 2, "OverlayPlugin PartyChanged bridge lost party members.");
    Assert(party.partyMembers[0].classJob == 19, "OverlayPlugin PartyChanged bridge lost the local job.");
    Assert(party.partyMembers[1].name == "Party Member", "OverlayPlugin PartyChanged bridge lost a party name.");

    var combatAdapterType = overlayAssembly.GetType(
        "RainbowMage.OverlayPlugin.MemoryProcessors.InCombat.DalamudInCombatMemory",
        throwOnError: true)!;
    var combatAdapter = (IInCombatMemory)Activator.CreateInstance(
        combatAdapterType,
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
        binder: null,
        [provider],
        culture: null)!;
    Assert(combatAdapter.IsValid(), "OverlayPlugin InCombat bridge did not see the game.");
    Assert(combatAdapter.GetInCombat(), "OverlayPlugin InCombat bridge lost the combat state.");

    provider.Clear();
    Assert(!provider.Snapshot.GameExists, "Clearing the Dalamud game state left the game active.");
}

static void ValidateHtmlOverlayDefaults()
{
    Assert(
        typeof(SelfHostedActRuntime).GetMethod(
            nameof(SelfHostedActRuntime.DeleteHtmlOverlay),
            BindingFlags.Instance | BindingFlags.Public,
            null,
            [typeof(string)],
            null)?.ReturnType == typeof(bool),
        "The HTML overlay runtime no longer exposes explicit window deletion.");
    Assert(
        typeof(SelfHostedActRuntime).GetMethod(
            nameof(SelfHostedActRuntime.InjectExternalPluginLogLine),
            BindingFlags.Instance | BindingFlags.Public,
            null,
            [typeof(string)],
            null)?.ReturnType == typeof(bool),
        "Host-generated plugin log lines can no longer re-enter the game-side OverlayPlugin pipeline.");
    Assert(
        typeof(SelfHostedActRuntime).GetMethod(
            nameof(SelfHostedActRuntime.ResetCactbotOverlayWindow),
            BindingFlags.Instance | BindingFlags.Public,
            null,
            [typeof(string)],
            null)?.ReturnType == typeof(bool),
        "The Cactbot manager no longer exposes an in-place window reset.");
    Assert(
        typeof(ControlCenterWindow).GetConstructors()
            .Single()
            .GetParameters()
            .Any(parameter =>
                parameter.Name == "deleteHtmlOverlay" &&
                parameter.ParameterType == typeof(Action<string>)),
        "The control center no longer exposes the created-overlay delete action.");
    Assert(
        typeof(ControlCenterWindow).GetConstructors()
            .Single()
            .GetParameters()
            .Any(parameter =>
                parameter.Name == "uninstallGenericPlugin" &&
                parameter.ParameterType == typeof(Action<string>)),
        "The control center no longer exposes the generic ACT plugin uninstall action.");
    Assert(
        typeof(ControlCenterWindow).GetConstructors()
            .Single()
            .GetParameters()
            .Any(parameter =>
                parameter.Name == "dismissPluginInstallFailure" &&
                parameter.ParameterType == typeof(Action)),
        "The control center can no longer clear a dismissed plugin-import failure.");
    Assert(
        typeof(ControlCenterWindow).GetConstructors()
            .Single()
            .GetParameters()
            .Any(parameter =>
                parameter.Name == "openHtmlOverlay" &&
                parameter.ParameterType == typeof(Func<string, bool>)) &&
        typeof(SettingsWindow).GetConstructors()
            .Single()
            .GetParameters()
            .Any(parameter =>
                parameter.Name == "deleteHtmlOverlay" &&
                parameter.ParameterType == typeof(Action<string>)),
        "Overlay editing cannot verify preview startup or the advanced Cactbot list cannot reset an entry.");
    Assert(
        typeof(ControlCenterWindow).GetConstructors()
            .Single()
            .GetParameters()
            .Any(parameter =>
                parameter.Name == "resetCurrentEncounter" &&
                parameter.ParameterType == typeof(Action)),
        "The control center no longer owns the guarded encounter-reset action.");

    var controlCenterSource = File.ReadAllText(Path.Combine(
        FindProjectRoot(),
        "src",
        "DalamudActCompat",
        "UI",
        "ControlCenterWindow.cs"));
    var meterWindowSource = File.ReadAllText(Path.Combine(
        FindProjectRoot(),
        "src",
        "DalamudActCompat",
        "Meter",
        "MeterWindow.cs"));
    var meterEditorSource = File.ReadAllText(Path.Combine(
        FindProjectRoot(),
        "src",
        "DalamudActCompat",
        "Meter",
        "MeterStyleEditorWindow.cs"));
    var roleSplitSource = File.ReadAllText(Path.Combine(
        FindProjectRoot(),
        "src",
        "DalamudActCompat",
        "Meter",
        "RoleSplitMeterWindow.cs"));
    var launcherWindowSource = File.ReadAllText(Path.Combine(
        FindProjectRoot(),
        "src",
        "DalamudActCompat",
        "UI",
        "LauncherWindow.cs"));
    var createdOverlayIndex = controlCenterSource.IndexOf(
        "changed |= DrawCreatedHtmlOverlays();",
        StringComparison.Ordinal);
    var templateOverlayIndex = controlCenterSource.IndexOf(
        "从模板创建",
        StringComparison.Ordinal);
    var usedCactbotIndex = controlCenterSource.IndexOf(
        "打开过的 Cactbot 悬浮窗",
        StringComparison.Ordinal);
    var availableCactbotIndex = controlCenterSource.IndexOf(
        "从本地模板添加",
        StringComparison.Ordinal);
    var settingsWindowSource = File.ReadAllText(Path.Combine(
        FindProjectRoot(),
        "src",
        "DalamudActCompat",
        "UI",
        "SettingsWindow.cs"));
    var helpWindowSource = File.ReadAllText(Path.Combine(
        FindProjectRoot(),
        "src",
        "DalamudActCompat",
        "UI",
        "HelpWindow.cs"));
    var brandedChromeSource = File.ReadAllText(Path.Combine(
        FindProjectRoot(),
        "src",
        "DalamudActCompat",
        "UI",
        "BrandedWindowChrome.cs"));
    var macroPluginSource = File.ReadAllText(Path.Combine(
        FindProjectRoot(),
        "src",
        "DalamudActCompat",
        "Plugin",
        "Plugin.cs"));
    var toggleMeterStart = macroPluginSource.IndexOf(
        "private void ToggleMeter()",
        StringComparison.Ordinal);
    var setMeterVisibleStart = macroPluginSource.IndexOf(
        "private void SetMeterVisible(bool visible)",
        StringComparison.Ordinal);
    var toggleMeterSource = toggleMeterStart >= 0 && setMeterVisibleStart > toggleMeterStart
        ? macroPluginSource[toggleMeterStart..setMeterVisibleStart]
        : string.Empty;
    var parserAdapterSource = File.ReadAllText(Path.Combine(
        FindProjectRoot(),
        "src",
        "DalamudActCompat",
        "Parser",
        "IinactAdapter.cs"));
    var readmeSource = File.ReadAllText(Path.Combine(
        FindProjectRoot(),
        "README.md"));
    var englishReadmeSource = File.ReadAllText(Path.Combine(
        FindProjectRoot(),
        "README_EN.md"));
    var readmeImagePaths = new[]
    {
        "control-center-overview.png",
        "combat-meter-settings.png",
        "overlay-settings.png",
        "extension-management.png",
        "general-settings.png",
        "encounter-history.png",
        "runtime-status.png",
    }.Select(name => Path.Combine(
        FindProjectRoot(),
        "docs",
        "images",
        "readme",
        name));
    // README structure is part of the install/support contract, but its wording may evolve.
    Assert(
        !readmeSource.Contains("/actcompat settings", StringComparison.Ordinal) &&
        readmeSource.Contains("/actcompat off", StringComparison.Ordinal) &&
        readmeSource.Contains("[English](README_EN.md)", StringComparison.Ordinal) &&
        readmeSource.Contains("## 安装", StringComparison.Ordinal) &&
        readmeSource.Contains("### 1. 添加自定义插件仓库", StringComparison.Ordinal) &&
        readmeSource.Contains(
            "https://raw.githubusercontent.com/JackyWilliam/DalamudActCompatRepo/main/pluginmaster.json",
            StringComparison.Ordinal) &&
        readmeSource.Contains("## 界面预览", StringComparison.Ordinal) &&
        readmeSource.Contains(
            "docs/images/readme/control-center-overview.png",
            StringComparison.Ordinal) &&
        readmeImagePaths.All(File.Exists) &&
        readmeSource.Contains("## 控制中心", StringComparison.Ordinal) &&
        readmeSource.Contains("## 扩展、权限与重启", StringComparison.Ordinal) &&
        readmeSource.Contains("### 战斗统计窗口不见了或一直为空", StringComparison.Ordinal) &&
        readmeSource.Contains("副本内团灭重开后", StringComparison.Ordinal) &&
        readmeSource.Contains("同一次进本放在一个文件夹", StringComparison.Ordinal) &&
        readmeSource.Contains("每一把作为独立子记录保存", StringComparison.Ordinal) &&
        readmeSource.Contains("**HPS**", StringComparison.Ordinal) &&
        readmeSource.Contains("**FFLogs 区间估算**", StringComparison.Ordinal) &&
        readmeSource.Contains("重启共享 Host", StringComparison.Ordinal) &&
        englishReadmeSource.Contains("[简体中文](README.md)", StringComparison.Ordinal) &&
        englishReadmeSource.Contains("## Installation", StringComparison.Ordinal) &&
        englishReadmeSource.Contains("## Interface preview", StringComparison.Ordinal) &&
        englishReadmeSource.Contains("## Control Center", StringComparison.Ordinal) &&
        englishReadmeSource.Contains("## Troubleshooting", StringComparison.Ordinal) &&
        englishReadmeSource.Contains("## License", StringComparison.Ordinal),
        "The bilingual README install, preview, usage, or support contract regressed.");
    Assert(
        createdOverlayIndex >= 0 && templateOverlayIndex > createdOverlayIndex &&
        usedCactbotIndex >= 0 && availableCactbotIndex > usedCactbotIndex &&
        controlCenterSource.Contains("HTML 悬浮窗", StringComparison.Ordinal) &&
        controlCenterSource.Contains("从网址创建", StringComparison.Ordinal) &&
        controlCenterSource.Contains("html-overlay-create-tabs", StringComparison.Ordinal) &&
        controlCenterSource.Contains(
            "BrandedWindowChrome.DrawNavigationRail(",
            StringComparison.Ordinal) &&
        !controlCenterSource.Contains("BeginTabBar(\"html-overlay-create-tabs\")", StringComparison.Ordinal) &&
        controlCenterSource.Contains("ResolveOverlayDisplayName", StringComparison.Ordinal) &&
        controlCenterSource.Contains("保存名称", StringComparison.Ordinal) &&
        controlCenterSource.Contains("只添加你信任的悬浮窗页面", StringComparison.Ordinal) &&
        controlCenterSource.Contains(
            ".Where(static template => template.IsCactbot)",
            StringComparison.Ordinal) &&
        controlCenterSource.Contains(
            "!SelfHostedActRuntime.IsCactbotOverlayName(name)",
            StringComparison.Ordinal) &&
        controlCenterSource.Contains(
            "pair.Value.HasBeenOpened",
            StringComparison.Ordinal) &&
        controlCenterSource.Contains("deleteHtmlOverlay(selectedName)", StringComparison.Ordinal) &&
        settingsWindowSource.Contains("打开过的 Cactbot 悬浮窗", StringComparison.Ordinal) &&
        settingsWindowSource.Contains("从本地模板添加", StringComparison.Ordinal) &&
        settingsWindowSource.Contains("settings.HasBeenOpened", StringComparison.Ordinal),
        "Cactbot usage/history or created/custom HTML overlay list ordering regressed.");
    Assert(
        controlCenterSource.Contains("helpAction: openHelp", StringComparison.Ordinal) &&
        controlCenterSource.Contains("helpTooltip: text.Get(\"帮助\", \"Help\")", StringComparison.Ordinal) &&
        !controlCenterSource.Contains("需要更多帮助吗？", StringComparison.Ordinal) &&
        !controlCenterSource.Contains("DrawHelpEntry", StringComparison.Ordinal) &&
        brandedChromeSource.Contains("?##help-{id}", StringComparison.Ordinal) &&
        brandedChromeSource.Contains("helpAction();", StringComparison.Ordinal) &&
        brandedChromeSource.Contains("const float actionButtonSize = 28;", StringComparison.Ordinal) &&
        brandedChromeSource.Contains("actionButtonOffsetY", StringComparison.Ordinal) &&
        brandedChromeSource.Contains(
            "new Vector2(actionButtonSize, actionButtonSize)",
            StringComparison.Ordinal) &&
        brandedChromeSource.Contains("const float helpCloseGap = 3;", StringComparison.Ordinal) &&
        brandedChromeSource.Contains("actionButtonSize + helpCloseGap", StringComparison.Ordinal) &&
        brandedChromeSource.Contains("statusWidth", StringComparison.Ordinal) &&
        brandedChromeSource.Contains("statusAction();", StringComparison.Ordinal) &&
        brandedChromeSource.Contains("##status-{id}", StringComparison.Ordinal) &&
        typeof(HelpWindow).IsSubclassOf(typeof(Dalamud.Interface.Windowing.Window)) &&
        helpWindowSource.Contains("help-document-navigation", StringComparison.Ordinal) &&
        helpWindowSource.Contains("使用须知", StringComparison.Ordinal) &&
        helpWindowSource.Contains("不要去绿玩面前跳脸。", StringComparison.Ordinal) &&
        helpWindowSource.Contains("一经发现立刻踢出！", StringComparison.Ordinal) &&
        helpWindowSource.Contains("宏指令", StringComparison.Ordinal) &&
        helpWindowSource.Contains("ImGui.SetClipboardText(command);", StringComparison.Ordinal) &&
        helpWindowSource.Contains("InputTextWithHint", StringComparison.Ordinal) &&
        helpWindowSource.Contains("help-search-card", StringComparison.Ordinal) &&
        helpWindowSource.Contains("ImGui.Dummy(new Vector2(0, 8))", StringComparison.Ordinal) &&
        Regex.IsMatch(
            helpWindowSource,
            "\"help-search-card\",\\s*new Vector2\\(-1, 58\\),\\s*false") &&
        !helpWindowSource.Contains(
            "ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(Gold.X, Gold.Y, Gold.Z, 0.72f))",
            StringComparison.Ordinal) &&
        helpWindowSource.Contains("ImGui.Button(\"Search\"", StringComparison.Ordinal) &&
        helpWindowSource.Contains("searchDraft", StringComparison.Ordinal) &&
        !helpWindowSource.Contains("text.Get(\"清除\", \"Clear\")", StringComparison.Ordinal) &&
        helpWindowSource.Contains("CreateSearchEntries", StringComparison.Ordinal) &&
        helpWindowSource.Contains("常见问题", StringComparison.Ordinal) &&
        helpWindowSource.Contains("控制中心五个页面怎么用", StringComparison.Ordinal) &&
        helpWindowSource.Contains("如何给扩展开权限", StringComparison.Ordinal) &&
        helpWindowSource.Contains("插件打不开、命令没反应或一直初始化", StringComparison.Ordinal) &&
        helpWindowSource.Contains("没有战斗统计、没有队员或窗口不见了", StringComparison.Ordinal) &&
        helpWindowSource.Contains("战斗结束后悬浮窗会保留上一把结果", StringComparison.Ordinal) &&
        helpWindowSource.Contains("手动重置会立即清空", StringComparison.Ordinal) &&
        helpWindowSource.Contains("历史记录以“一次副本进入”为一个可展开文件夹", StringComparison.Ordinal) &&
        helpWindowSource.Contains("HPS 用本把从开怪到结束的完整经过时间计算", StringComparison.Ordinal) &&
        helpWindowSource.Contains("24 人本把所有玩家放在同一紧凑列表", StringComparison.Ordinal) &&
        helpWindowSource.Contains("总伤害、最高伤害和死亡", StringComparison.Ordinal) &&
        helpWindowSource.Contains("最高伤害使用紧凑宽度显示", StringComparison.Ordinal) &&
        helpWindowSource.Contains("三个模板互斥启用", StringComparison.Ordinal) &&
        helpWindowSource.Contains("页面预览直接复用真实悬浮窗渲染", StringComparison.Ordinal) &&
        helpWindowSource.Contains("后续刷新不会把旧数据带回", StringComparison.Ordinal) &&
        helpWindowSource.Contains("横版始终没有背景", StringComparison.Ordinal) &&
        helpWindowSource.Contains("只有存在未保存修改时", StringComparison.Ordinal) &&
        helpWindowSource.Contains("没有修改会直接关闭", StringComparison.Ordinal) &&
        helpWindowSource.Contains("职业 / ID 会先缩到两字", StringComparison.Ordinal) &&
        helpWindowSource.Contains("可分别开关 FFLogs、DPS、EncDPS、ExtDPS、rDPS、HPS", StringComparison.Ordinal) &&
        helpWindowSource.Contains("反馈问题时请提供什么", StringComparison.Ordinal) &&
        helpWindowSource.Contains("重启共享 Host", StringComparison.Ordinal) &&
        helpWindowSource.Contains("实际 FileVersion", StringComparison.Ordinal) &&
        new[]
        {
            "/actcompat on",
            "/actcompat off",
            "/actcompat meter",
            "/actcompat simple on|off",
            "/actcompat history",
            "/actcompat logs",
            "/actcompat status",
            "/actcompat cactbot",
            "/actcompat overlay",
            "/actcompat clear",
            "/actcompat install",
            "/actcompat factory-reset",
            "/actcompat host",
            "/actcompat stop",
            "/actcompat sample",
        }.All(command => helpWindowSource.Contains(command, StringComparison.Ordinal)) &&
        macroPluginSource.Contains("case \"on\":", StringComparison.Ordinal) &&
        Regex.IsMatch(
            macroPluginSource,
            "case \\\"off\\\":\\s+settingsWindow\\.HideAnimated\\(\\);") &&
        macroPluginSource.Contains("case \"on\":", StringComparison.Ordinal) &&
        macroPluginSource.Contains("OpenConfigUi();", StringComparison.Ordinal) &&
        macroPluginSource.Contains("case \"simple\":", StringComparison.Ordinal) &&
        macroPluginSource.Contains("SetSimplifiedMode", StringComparison.Ordinal) &&
        macroPluginSource.Contains(
            "verb is not (\"\" or \"on\" or \"simple\" or \"meter\" or \"clear\")",
            StringComparison.Ordinal) &&
        macroPluginSource.Contains(
            "Only meter, clear, and simple commands are available in simplified mode.",
            StringComparison.Ordinal) &&
        macroPluginSource.Contains("GameForegroundDetector.IsCurrentProcessForeground()", StringComparison.Ordinal) &&
        launcherWindowSource.Contains("!configuration.SimplifiedModeEnabled", StringComparison.Ordinal) &&
        Regex.IsMatch(
            macroPluginSource,
            "case \\\"meter\\\":\\s+OpenMeter\\(\\);") &&
        macroPluginSource.Contains("private void ToggleMeter()", StringComparison.Ordinal) &&
        toggleMeterSource.Contains("SetMeterVisible(visible);", StringComparison.Ordinal) &&
        !toggleMeterSource.Contains("LocateOnNextDraw", StringComparison.Ordinal) &&
        macroPluginSource.Contains("meterWindow.LocateOnNextDraw();", StringComparison.Ordinal) &&
        macroPluginSource.Contains(
            "() => Volatile.Read(ref playerIdentitySnapshot)",
            StringComparison.Ordinal) &&
        macroPluginSource.Contains(
            "Volatile.Write(ref playerIdentitySnapshot, identities);",
            StringComparison.Ordinal) &&
        !macroPluginSource.Contains(
            "() => BuildPlayerIdentities(",
            StringComparison.Ordinal) &&
        controlCenterSource.Contains("public void LocateAnimated()", StringComparison.Ordinal) &&
        controlCenterSource.Contains("ImGui.SetNextWindowPos", StringComparison.Ordinal) &&
        meterWindowSource.Contains("public void LocateOnNextDraw()", StringComparison.Ordinal) &&
        meterWindowSource.Contains("locatePreviewExpiresAt", StringComparison.Ordinal) &&
        meterWindowSource.Contains("ImGui.SetNextWindowPos", StringComparison.Ordinal) &&
        launcherWindowSource.Contains("private readonly Action toggleMeter;", StringComparison.Ordinal) &&
        launcherWindowSource.Contains("打开或关闭战斗统计", StringComparison.Ordinal) &&
        macroPluginSource.Contains(
            "hostSupervisor.PublishZone(territoryId, localizedZoneName)",
            StringComparison.Ordinal) &&
        !macroPluginSource.Contains(
            "Cafe.Matcha configuration requires the WriteFiles capability.",
            StringComparison.Ordinal) &&
        !macroPluginSource.Contains("case \"settings\":", StringComparison.Ordinal) &&
        helpWindowSource.Contains("版权声明", StringComparison.Ordinal) &&
        helpWindowSource.Contains("Copyright © 2026 DalamudActCompat contributors.", StringComparison.Ordinal) &&
        !helpWindowSource.Contains("BeginPopupModal", StringComparison.Ordinal),
        "The help entry, macro command reference, copy action, or branded help document regressed.");
    Assert(
        macroPluginSource.Contains("parserEngine.ResetCurrentEncounter", StringComparison.Ordinal) &&
        !macroPluginSource.Contains("stateStore.ResetCurrent", StringComparison.Ordinal) &&
        parserAdapterSource.Contains(
            "RememberFinalizedSegmentsUnsafe(dutySession.SegmentIds)",
            StringComparison.Ordinal) &&
        parserAdapterSource.Contains("dutyWipeTracker.Observe", StringComparison.Ordinal) &&
        !parserAdapterSource.Contains("OpenWorldCombatResetTracker", StringComparison.Ordinal) &&
        parserAdapterSource.Contains("stateStore.UpdateCurrent(encounter);", StringComparison.Ordinal) &&
        parserAdapterSource.Contains("getEncounterModeSnapshot()", StringComparison.Ordinal) &&
        !parserAdapterSource.Contains("finished && !inCombat", StringComparison.Ordinal) &&
        macroPluginSource.Contains("ConditionFlag.Unconscious", StringComparison.Ordinal) &&
        macroPluginSource.Contains("IsDutyPartyWiped", StringComparison.Ordinal) &&
        parserAdapterSource.Contains("completeFolder: false", StringComparison.Ordinal) &&
        macroPluginSource.Contains("ConditionFlag.InCombat", StringComparison.Ordinal) &&
        parserAdapterSource.Contains("dutySession.Reset();", StringComparison.Ordinal) &&
        parserAdapterSource.Contains("dutyFolder.Add(completedPull)", StringComparison.Ordinal) &&
        parserAdapterSource.Contains("QueueFinishedEncounter(folderSnapshot)", StringComparison.Ordinal),
        "Encounter reset can still republish an old pull, or duty attempts are no longer finalized independently.");
    Assert(
        HelpWindow.MatchesSearch("鲶鱼 重启", "鲶鱼精更新后重启共享 Host") &&
        HelpWindow.MatchesSearch("postnamazu HOST", "PostNamazu uses Shared Host") &&
        !HelpWindow.MatchesSearch("FFLogs 上传", "FFLogs 本地预估") &&
        !HelpWindow.MatchesSearch("   ", "任意内容"),
        "Help search no longer supports case-insensitive multi-keyword matching or empty-query handling.");
    var launcherPositionAtLargeSize = LauncherWindow.ResolveViewportPosition(
        new Vector2(21, 22),
        Vector2.Zero,
        new Vector2(1920, 1080),
        68);
    var launcherPositionAtSmallSize = LauncherWindow.ResolveViewportPosition(
        new Vector2(21, 22),
        Vector2.Zero,
        new Vector2(1280, 720),
        68);
    var launcherPositionInDesktopViewport = LauncherWindow.ResolveViewportPosition(
        new Vector2(21, 22),
        new Vector2(400, 300),
        new Vector2(1280, 720),
        68);
    Assert(
        launcherPositionAtLargeSize == new Vector2(21, 22) &&
        launcherPositionAtSmallSize == launcherPositionAtLargeSize &&
        launcherPositionInDesktopViewport == new Vector2(421, 322),
        "The quick button no longer preserves viewport-relative coordinates when the game window changes size or desktop position.");
    Assert(
        controlCenterSource.Contains("allowScrolling: false", StringComparison.Ordinal) &&
        controlCenterSource.Contains(
            "ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse",
            StringComparison.Ordinal),
        "Overview or Combat Meter settings regained a nested scrolling region.");
    var pluginFailure = new ThirdPartyPluginInstallStatus(
        ThirdPartyPluginInstallState.Failed,
        "FishersIntuition",
        Detail: "No compatible plugin entry point was found.",
        Diagnostic: "System.InvalidOperationException: preflight failed");
    var pluginFailureLog = ControlCenterWindow.BuildPluginInstallFailureLog(pluginFailure);
    Assert(
        controlCenterSource.Contains("PluginInstallFailurePopupId", StringComparison.Ordinal) &&
        controlCenterSource.Contains("复制日志", StringComparison.Ordinal) &&
        controlCenterSource.Contains("关闭并清除记录", StringComparison.Ordinal) &&
        pluginFailureLog.Contains("FishersIntuition", StringComparison.Ordinal) &&
        pluginFailureLog.Contains("preflight failed", StringComparison.Ordinal),
        "Plugin import failures no longer open a dismissible dialog with copyable diagnostics.");
    Assert(
        controlCenterSource.Contains(
            "private const int ResetConfirmationMilliseconds = 10_000;",
            StringComparison.Ordinal),
        "The encounter-reset confirmation is not configured to close after ten seconds.");
    var fflogsApiCardIndex = controlCenterSource.IndexOf(
        "fflogs-api-refresh-card",
        StringComparison.Ordinal);
    var fflogsAutomaticCardIndex = controlCenterSource.IndexOf(
        "fflogs-automatic-encounter-card",
        StringComparison.Ordinal);
    Assert(
        fflogsApiCardIndex >= 0 && fflogsAutomaticCardIndex > fflogsApiCardIndex &&
        controlCenterSource.Contains("API 凭据与数据刷新", StringComparison.Ordinal) &&
        controlCenterSource.Contains("当前副本自动识别", StringComparison.Ordinal) &&
        !controlCenterSource.Contains("绑定当前战斗", StringComparison.Ordinal),
        "FFLogs automatic duty matching is missing or manual encounter binding is still exposed.");
    Assert(
        !controlCenterSource.Contains("control-center-sidebar", StringComparison.Ordinal) &&
        !controlCenterSource.Contains("parser-state-card", StringComparison.Ordinal) &&
        controlCenterSource.Contains("ImGuiStyleVar.WindowRounding", StringComparison.Ordinal) &&
        controlCenterSource.Contains("VersionLabel", StringComparison.Ordinal),
        "The control center did not move branding, centered parser state, version, and close control into rounded top chrome.");
    Assert(
        !meterWindowSource.Contains("ResetCurrent", StringComparison.Ordinal) &&
        !meterWindowSource.Contains("清空当前战斗", StringComparison.Ordinal) &&
        !meterWindowSource.Contains("DrawControls(", StringComparison.Ordinal) &&
        meterWindowSource.Contains("DrawClassicTable", StringComparison.Ordinal) &&
        meterWindowSource.Contains("DrawAllianceCompactTiles", StringComparison.Ordinal) &&
        meterWindowSource.Contains("MeterSlotPresentation.DisplayName", StringComparison.Ordinal) &&
        meterWindowSource.Contains("DrawTeamSummary", StringComparison.Ordinal) &&
        meterWindowSource.Contains("FormatHighestDamage", StringComparison.Ordinal) &&
        meterWindowSource.Contains("DrawBoldText(", StringComparison.Ordinal) &&
        meterWindowSource.Contains("MeterSlotAlignment.Center", StringComparison.Ordinal) &&
        meterWindowSource.Contains("const string ellipsis = \"...\"", StringComparison.Ordinal) &&
        meterWindowSource.Contains("ApplyBackgroundOpacity", StringComparison.Ordinal) &&
        meterEditorSource.Contains("profile.BackgroundOpacity", StringComparison.Ordinal) &&
        roleSplitSource.Contains("Profile.BackgroundOpacity", StringComparison.Ordinal) &&
        meterEditorSource.Contains("configuration.Meter.LocalPlayerColor", StringComparison.Ordinal) &&
        meterEditorSource.Contains("MeterSlotDefaults.EditableMetrics", StringComparison.Ordinal) &&
        controlCenterSource.Contains("DrawPlayerIdentityControls", StringComparison.Ordinal) &&
        controlCenterSource.Contains("DrawFflogsSettings", StringComparison.Ordinal),
        "The Meter tiles, external identity/FFLogs settings, team summary, or editor-owned style controls regressed.");

    var settings = new HtmlOverlayWindowSettings();
    Assert(!settings.IsVisible, "HTML overlays must remain closed until explicitly opened.");
    Assert(!settings.OpenOnStartup, "HTML overlays must not auto-open before the user opens them.");
    Assert(settings.IsClickThrough, "HTML overlays must be click-through by default.");
    Assert(settings.IsLocked, "HTML overlays must be locked by default.");
    Assert(!settings.IsEditing, "HTML overlays must not start in editing mode.");
    Assert(settings.ZoomFactor == 1.0f, "HTML overlay default zoom changed unexpectedly.");
    Assert(string.IsNullOrEmpty(settings.SourceUrl), "Template overlays unexpectedly received a custom source URL.");
    settings.SetEditing(true);
    Assert(
        settings.IsEditing && !settings.IsClickThrough && !settings.IsLocked,
        "HTML overlay editing mode did not disable click-through and locking together.");
    settings.SetEditing(false);
    Assert(
        !settings.IsEditing && settings.IsClickThrough && settings.IsLocked,
        "Finishing HTML overlay editing did not restore click-through and locking together.");
    settings.SetEditing(true);
    settings.SetLocked(true);
    Assert(
        !settings.IsEditing && settings.IsLocked && !settings.IsClickThrough,
        "Locking an HTML overlay did not leave temporary editing mode cleanly.");
    settings.SetLocked(false);

    settings.OpenOnStartup = true;
    settings.HasBeenOpened = true;
    settings.DisplayName = "团队统计";
    var serializedOverlaySettings = Newtonsoft.Json.JsonConvert.SerializeObject(settings);
    var restoredOverlaySettings = Newtonsoft.Json.JsonConvert.DeserializeObject<HtmlOverlayWindowSettings>(
                                      serializedOverlaySettings)
                                  ?? throw new InvalidOperationException(
                                      "HTML overlay settings did not deserialize.");
    Assert(
        serializedOverlaySettings.Contains(nameof(HtmlOverlayWindowSettings.OpenOnStartup), StringComparison.Ordinal) &&
        serializedOverlaySettings.Contains(nameof(HtmlOverlayWindowSettings.HasBeenOpened), StringComparison.Ordinal) &&
        !serializedOverlaySettings.Contains(nameof(HtmlOverlayWindowSettings.IsVisible), StringComparison.Ordinal) &&
        !serializedOverlaySettings.Contains(nameof(HtmlOverlayWindowSettings.IsEditing), StringComparison.Ordinal) &&
        restoredOverlaySettings.OpenOnStartup &&
        restoredOverlaySettings.HasBeenOpened &&
        restoredOverlaySettings.DisplayName == "团队统计" &&
        ControlCenterWindow.ResolveOverlayDisplayName("Kagerou", restoredOverlaySettings) == "团队统计",
        "The overlay usage, startup state, or user-visible name was not persisted independently of runtime identity.");
    var resetSettings = new HtmlOverlayWindowSettings
    {
        OpenOnStartup = true,
        HasBeenOpened = true,
        IsClickThrough = false,
        IsLocked = false,
        ZoomFactor = 1.5f,
        DisplayName = "旧名称",
        SourceUrl = "https://example.com/overlay",
        Left = 10,
        Top = 20,
        Width = 700,
        Height = 300,
    };
    resetSettings.ResetRegistration();
    Assert(
        !resetSettings.OpenOnStartup &&
        !resetSettings.HasBeenOpened &&
        resetSettings.IsClickThrough &&
        resetSettings.IsLocked &&
        resetSettings.ZoomFactor == 1.0f &&
        string.IsNullOrEmpty(resetSettings.DisplayName) &&
        string.IsNullOrEmpty(resetSettings.SourceUrl) &&
        resetSettings.Left is null &&
        resetSettings.Top is null &&
        resetSettings.Width is null &&
        resetSettings.Height is null,
        "Removing a Cactbot overlay did not reset its saved layout and display state in place.");
    var registrationConfiguration = new PluginConfiguration();
    var registeredSettings = registrationConfiguration.RegisterOverlayWindow(
        SelfHostedActRuntime.CactbotTimelineOverlayName);
    Assert(
        registeredSettings.HasBeenOpened &&
        ReferenceEquals(
            registeredSettings,
            registrationConfiguration.GetOverlayWindowSettings(
                SelfHostedActRuntime.CactbotTimelineOverlayName)),
        "A successful overlay open did not register persistent usage on the existing settings object.");
    var overlaysToRestore = SelfHostedActRuntime.SelectHtmlOverlaysToRestore(
        new Dictionary<string, HtmlOverlayWindowSettings>(StringComparer.OrdinalIgnoreCase)
        {
            ["Skills"] = restoredOverlaySettings,
            ["Closed"] = new HtmlOverlayWindowSettings(),
            [SelfHostedActRuntime.CactbotOverlayName] = new HtmlOverlayWindowSettings
            {
                OpenOnStartup = true,
            },
        });
    Assert(
        overlaysToRestore.SequenceEqual(
            [SelfHostedActRuntime.CactbotOverlayName, "Skills"]),
        "HTML overlay startup restoration did not preserve Cactbot and user-open custom windows.");
    var conflictingRaidbossWindowsToRestore = SelfHostedActRuntime.SelectHtmlOverlaysToRestore(
        new Dictionary<string, HtmlOverlayWindowSettings>(StringComparer.OrdinalIgnoreCase)
        {
            [SelfHostedActRuntime.CactbotOverlayName] = new HtmlOverlayWindowSettings
            {
                OpenOnStartup = true,
            },
            [SelfHostedActRuntime.CactbotAlertsOverlayName] = new HtmlOverlayWindowSettings
            {
                OpenOnStartup = true,
            },
            [SelfHostedActRuntime.CactbotTimelineOverlayName] = new HtmlOverlayWindowSettings
            {
                OpenOnStartup = true,
            },
            ["Skills"] = restoredOverlaySettings,
        });
    Assert(
        conflictingRaidbossWindowsToRestore.SequenceEqual(
            [
                SelfHostedActRuntime.CactbotAlertsOverlayName,
                SelfHostedActRuntime.CactbotTimelineOverlayName,
                "Skills",
            ]) &&
        !SelfHostedActRuntime.IsCactbotOverlayName("Cactbot Personal"),
        "Conflicting Raidboss startup state was not normalized or a custom Cactbot-prefixed name was captured.");

    var probedTemplates = SelfHostedActRuntime.ProbeOverlayTemplates();
    Assert(
        probedTemplates.Single(template => template.Name == "Kagerou").IsCactbot == false &&
        probedTemplates.Single(template =>
            template.Name == SelfHostedActRuntime.CactbotTimelineOverlayName).IsCactbot &&
        probedTemplates
            .Where(static template => template.IsCactbot)
            .All(template => SelfHostedActRuntime.IsCactbotOverlayName(template.Name)),
        "Overlay templates lost their Cactbot classification.");

    var localCactbotRoot = Path.Combine(
        Path.GetTempPath(),
        $"actcompat-cactbot-uri-{Guid.NewGuid():N}");
    var localRaidboss = Path.Combine(localCactbotRoot, "ui", "raidboss", "raidboss.html");
    var outsideCactbotPage = Path.Combine(
        Path.GetDirectoryName(localCactbotRoot)!,
        $"{Path.GetFileName(localCactbotRoot)}-outside.html");
    Directory.CreateDirectory(Path.GetDirectoryName(localRaidboss)!);
    File.WriteAllText(localRaidboss, "<html></html>");
    File.WriteAllText(outsideCactbotPage, "<html></html>");
    try
    {
        Assert(
            SelfHostedActRuntime.TryBuildLocalCactbotOverlayUri(
                "https://overlayplugin.github.io/cactbot/ui/raidboss/raidboss.html?alerts=0&timeline=1",
                localCactbotRoot,
                new Uri("ws://127.0.0.1:10501/ws"),
                out var localTimelineUri) &&
            localTimelineUri.IsFile &&
            localTimelineUri.AbsoluteUri.Contains("alerts=0&timeline=1", StringComparison.Ordinal) &&
            localTimelineUri.AbsoluteUri.Contains(
                "OVERLAY_WS=ws://127.0.0.1:10501/ws",
                StringComparison.Ordinal) &&
            !localTimelineUri.AbsoluteUri.Contains("proxy.iinact.com", StringComparison.Ordinal),
            "Timeline-only did not resolve to the installed local Cactbot page with its mode and WebSocket parameters.");
        Assert(
            !SelfHostedActRuntime.TryBuildLocalCactbotOverlayUri(
                "https://overlayplugin.github.io/cactbot/ui/fisher/fisher.html",
                localCactbotRoot,
                new Uri("ws://127.0.0.1:10501/ws"),
                out _),
            "A missing local Cactbot page silently fell back to a remote template.");
        Assert(
            !SelfHostedActRuntime.TryBuildLocalCactbotOverlayUri(
                $"https://overlayplugin.github.io/cactbot/%2e%2e%2f{Uri.EscapeDataString(Path.GetFileName(outsideCactbotPage))}",
                localCactbotRoot,
                new Uri("ws://127.0.0.1:10501/ws"),
                out _),
            "An encoded Cactbot template path escaped the installed local package root.");
    }
    finally
    {
        Directory.Delete(localCactbotRoot, recursive: true);
        File.Delete(outsideCactbotPage);
    }

    Assert(
        SelfHostedActRuntime.TryBuildCustomOverlayUri(
            "https://souma.diemoe.net/ff14-overlay-vue/#/teamWatch",
            new Uri("ws://127.0.0.1:10501/ws"),
            out var customOverlayUri) &&
        customOverlayUri.AbsoluteUri.Contains(
            "#/teamWatch?OVERLAY_WS=ws://127.0.0.1:10501/ws",
            StringComparison.Ordinal) &&
        !customOverlayUri.AbsoluteUri.Contains("HOST_PORT=", StringComparison.Ordinal),
        "Hash-routed custom overlays did not receive the modern OverlayPlugin WebSocket parameter.");
    Assert(
        SelfHostedActRuntime.TryBuildCustomOverlayUri(
            "https://example.invalid/overlay",
            new Uri("ws://127.0.0.1:10501/ws"),
            OverlayConnectionMode.ActWebSocket,
            out var actWsOverlayUri) &&
        actWsOverlayUri.Query.Contains(
            "HOST_PORT=ws://127.0.0.1:10501",
            StringComparison.Ordinal) &&
        !actWsOverlayUri.Query.Contains("OVERLAY_WS=", StringComparison.Ordinal),
        "The automatic fallback URI did not isolate the legacy ACTWS parameter.");
    var overlaySettings = new HtmlOverlayWindowSettings();
    Assert(
        overlaySettings.ConnectionMode == OverlayConnectionMode.Auto &&
        overlaySettings.DetectedConnectionMode is null,
        "Custom overlays no longer default to automatic protocol detection.");
    Assert(
        !SelfHostedActRuntime.TryNormalizeCustomOverlayUri(
            "javascript:alert(1)",
            out _),
        "Custom overlays accepted a non-http/file executable URL scheme.");
    Assert(
        new PluginConfiguration().AutoCheckBundledPluginUpdates,
        "Existing users no longer retain the startup extension-update check by default.");

    var formType = typeof(HtmlOverlayWindowSettings).Assembly.GetType(
                       "DalamudActCompat.ActRuntime.HtmlOverlayForm")
                   ?? throw new InvalidOperationException("HTML overlay form was not found.");
    var combineInitializationTasks = formType.GetMethod(
                                         "CombineInitializationTasks",
                                         BindingFlags.Static | BindingFlags.NonPublic)
                                     ?? throw new InvalidOperationException(
                                         "WebView2 in-flight initialization combiner was not found.");
    var firstInitialization = new TaskCompletionSource<bool>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var combinedInitialization = combineInitializationTasks.Invoke(
                                     null,
                                     [firstInitialization.Task, Task.CompletedTask]) as Task
                                 ?? throw new InvalidOperationException(
                                     "WebView2 initialization combiner did not return a Task.");
    Assert(
        !combinedInitialization.IsCompleted,
        "A newer completed WebView2 attempt hid an older in-flight initialization.");
    firstInitialization.SetResult(true);
    combinedInitialization.GetAwaiter().GetResult();
    Assert(
        combinedInitialization.IsCompletedSuccessfully,
        "The combined WebView2 initialization did not finish after every attempt completed.");
    var isRaidbossPage = formType.GetMethod(
                             "IsCactbotRaidbossPage",
                             BindingFlags.Static | BindingFlags.NonPublic)
                         ?? throw new InvalidOperationException(
                             "Cactbot raidboss page detector was not found.");
    Assert(
        isRaidbossPage.Invoke(
            null,
            [new Uri("file:///C:/cactbot/ui/raidboss/raidboss.html?OVERLAY_WS=ws://127.0.0.1")])
            as bool? == true,
        "The Cactbot raidboss page was not recognized for responsive alert layout.");
    Assert(
        isRaidbossPage.Invoke(
            null,
            [new Uri("file:///C:/cactbot/ui/config/config.html")])
            as bool? == false,
        "The responsive raidboss layout leaked into another Cactbot page.");
    var layoutScript = formType.GetField(
                           "CactbotResponsiveAlertLayoutScript",
                           BindingFlags.Static | BindingFlags.NonPublic)
                       ?.GetRawConstantValue() as string
                       ?? throw new InvalidOperationException(
                           "Cactbot responsive alert layout script was not found.");
    Assert(
        layoutScript.Contains("#popup-text-container", StringComparison.Ordinal) &&
        layoutScript.Contains("z-index: 2147483646", StringComparison.Ordinal) &&
        layoutScript.Contains(
            "#container:not(.hide-alerts).dalamud-act-compat-alert-repair",
            StringComparison.Ordinal) &&
        layoutScript.Contains("new MutationObserver", StringComparison.Ordinal) &&
        layoutScript.Contains("window.chrome?.webview?.postMessage", StringComparison.Ordinal) &&
        layoutScript.Contains("alertsDisabled", StringComparison.Ordinal) &&
        layoutScript.Contains("#popup-text-info", StringComparison.Ordinal) &&
        layoutScript.Contains("max-height: 220px", StringComparison.Ordinal),
        "The Cactbot layout no longer repairs and reports unexpectedly invisible alert text.");
    Assert(
        !layoutScript.Contains(
            ".hide-alerts #popup-text-container { display: block",
            StringComparison.Ordinal) &&
        !layoutScript.Contains(
            "#container.hide-alerts.dalamud-act-compat-alert-repair",
            StringComparison.Ordinal),
        "The Cactbot visibility repair can override a user's explicit disabled-alert setting.");
    var isTransientWebViewFailure = formType.GetMethod(
                                        "IsTransientWebViewInitializationFailure",
                                        BindingFlags.Static | BindingFlags.NonPublic)
                                    ?? throw new InvalidOperationException(
                                        "WebView2 transient initialization detector was not found.");
    Assert(
        isTransientWebViewFailure.Invoke(
            null,
            [new System.Runtime.InteropServices.COMException(
                "The operation was aborted.",
                unchecked((int)0x80004004))]) as bool? == true &&
        isTransientWebViewFailure.Invoke(
            null,
            [new System.Runtime.InteropServices.COMException(
                "The profile is busy.",
                unchecked((int)0x800700AA))]) as bool? == true &&
        isTransientWebViewFailure.Invoke(
            null,
            [new InvalidOperationException("permanent")]) as bool? == false,
        "Cactbot WebView2 startup no longer retries transient abort/profile-busy failures only.");
    var describeWebViewFailure = formType.GetMethod(
                                     "DescribeWebViewInitializationFailure",
                                     BindingFlags.Static | BindingFlags.NonPublic)
                                 ?? throw new InvalidOperationException(
                                     "WebView2 initialization failure classifier was not found.");
    Assert(
        (describeWebViewFailure.Invoke(
             null,
             [new FileNotFoundException("missing")]) as string)?.Contains(
            "WebView2Loader.dll",
            StringComparison.Ordinal) == true &&
        (describeWebViewFailure.Invoke(
             null,
             [new UnauthorizedAccessException("denied")]) as string)?.Contains(
            "权限",
            StringComparison.Ordinal) == true &&
        (describeWebViewFailure.Invoke(
             null,
             [new InvalidOperationException("unknown")]) as string)?.Contains(
            "Dalamud 日志",
            StringComparison.Ordinal) == true,
        "WebView2 failures are still collapsed into the misleading Runtime-missing prompt.");

    var interactionType = formType.GetNestedType(
                              "OverlayInteraction",
                              BindingFlags.NonPublic)
                          ?? throw new InvalidOperationException(
                              "HTML overlay interaction mode was not found.");
    var getInteraction = formType.GetMethod(
                             "GetOverlayInteraction",
                             BindingFlags.Static | BindingFlags.NonPublic)
                         ?? throw new InvalidOperationException(
                             "HTML overlay cursor interaction helper was not found.");
    var move = Enum.Parse(interactionType, "Move");
    var resize = Enum.Parse(interactionType, "Resize");
    Assert(
        Equals(
            getInteraction.Invoke(
                null,
                [new System.Drawing.Size(900, 320), new System.Drawing.Point(450, 160)]),
            move),
        "HTML overlay cursor polling no longer treats the client area as a drag target.");
    Assert(
        Equals(
            getInteraction.Invoke(
                null,
                [new System.Drawing.Size(900, 320), new System.Drawing.Point(875, 295)]),
            resize),
        "HTML overlay cursor polling no longer exposes an accessible bottom-right resize target.");
    var shouldBeginInteraction = formType.GetMethod(
                                     "ShouldBeginOverlayInteraction",
                                     BindingFlags.Static | BindingFlags.NonPublic)
                                 ?? throw new InvalidOperationException(
                                     "HTML overlay interaction transition helper was not found.");
    Assert(
        shouldBeginInteraction.Invoke(
            null,
            [true, true, true, false, true]) as bool? == true,
        "A fresh left click inside a visible editing overlay did not begin an interaction.");
    Assert(
        shouldBeginInteraction.Invoke(
            null,
            [true, true, true, true, true]) as bool? == false,
        "Holding the settings click while entering edit mode incorrectly began a drag.");
    Assert(
        shouldBeginInteraction.Invoke(
            null,
            [true, true, true, false, false]) as bool? == false,
        "A click outside the overlay incorrectly began an interaction.");
    var hasExceededDragThreshold = formType.GetMethod(
                                       "HasExceededDragThreshold",
                                       BindingFlags.Static | BindingFlags.NonPublic)
                                   ?? throw new InvalidOperationException(
                                       "HTML overlay drag threshold helper was not found.");
    Assert(
        hasExceededDragThreshold.Invoke(
            null,
            [new System.Drawing.Point(400, 300), new System.Drawing.Point(404, 304)]) as bool? == false &&
        hasExceededDragThreshold.Invoke(
            null,
            [new System.Drawing.Point(400, 300), new System.Drawing.Point(406, 300)]) as bool? == true,
        "HTML overlay clicks are no longer separated from intentional drag gestures.");
    var tryAcquireInteraction = formType.GetMethod(
                                    "TryAcquireInteraction",
                                    BindingFlags.Static | BindingFlags.NonPublic)
                                ?? throw new InvalidOperationException(
                                    "HTML overlay interaction ownership helper was not found.");
    var releaseInteraction = formType.GetMethod(
                                 "ReleaseInteraction",
                                 BindingFlags.Static | BindingFlags.NonPublic)
                             ?? throw new InvalidOperationException(
                                 "HTML overlay interaction release helper was not found.");
    var firstWindow = (nint)101;
    var secondWindow = (nint)202;
    Assert(
        tryAcquireInteraction.Invoke(null, [firstWindow]) as bool? == true,
        "The first editing overlay could not acquire mouse interaction ownership.");
    Assert(
        tryAcquireInteraction.Invoke(null, [secondWindow]) as bool? == false,
        "Two editing overlays acquired the same mouse interaction.");
    releaseInteraction.Invoke(null, [secondWindow]);
    Assert(
        tryAcquireInteraction.Invoke(null, [secondWindow]) as bool? == false,
        "A non-owner released another overlay's mouse interaction.");
    releaseInteraction.Invoke(null, [firstWindow]);
    Assert(
        tryAcquireInteraction.Invoke(null, [secondWindow]) as bool? == true,
        "Mouse interaction ownership was not released after dragging ended.");
    releaseInteraction.Invoke(null, [secondWindow]);
    var calculateBounds = formType.GetMethod(
                              "CalculateInteractionBounds",
                              BindingFlags.Static | BindingFlags.NonPublic)
                          ?? throw new InvalidOperationException(
                              "HTML overlay interaction calculation was not found.");
    var startBounds = new System.Drawing.Rectangle(100, 200, 900, 320);
    var startCursor = new System.Drawing.Point(400, 300);
    var currentCursor = new System.Drawing.Point(430, 350);
    Assert(
        calculateBounds.Invoke(null, [startBounds, startCursor, currentCursor, move])
            is System.Drawing.Rectangle { X: 130, Y: 250, Width: 900, Height: 320 },
        "HTML overlay dragging no longer follows the global cursor delta.");
    Assert(
        calculateBounds.Invoke(null, [startBounds, startCursor, currentCursor, resize])
            is System.Drawing.Rectangle { X: 100, Y: 200, Width: 930, Height: 370 },
        "HTML overlay resizing no longer follows the global cursor delta.");
    var shouldEnableBrowserInput = formType.GetMethod(
                                       "ShouldEnableBrowserInput",
                                       BindingFlags.Static | BindingFlags.NonPublic)
                                   ?? throw new InvalidOperationException(
                                       "HTML overlay browser input routing helper was not found.");
    settings.SetEditing(true);
    Assert(
        shouldEnableBrowserInput.Invoke(null, [settings]) as bool? == true,
        "Windowed WebView2 did not keep page clicks enabled while editing an overlay.");
    settings.SetEditing(false);
    Assert(
        shouldEnableBrowserInput.Invoke(null, [settings]) as bool? == false,
        "Windowed WebView2 still captured page input after editing restored click-through.");
    var calculateExtendedStyle = formType.GetMethod(
                                     "CalculateOverlayExtendedStyle",
                                     BindingFlags.Static | BindingFlags.NonPublic)
                                 ?? throw new InvalidOperationException(
                                     "HTML overlay extended-style helper was not found.");
    var clickThroughStyle = (nint)calculateExtendedStyle.Invoke(
        null,
        [(nint)0x00000100, true])!;
    Assert(
        (clickThroughStyle & (nint)0x00080000) != nint.Zero &&
        (clickThroughStyle & (nint)0x00000080) != nint.Zero &&
        (clickThroughStyle & (nint)0x00040000) == nint.Zero &&
        (clickThroughStyle & (nint)0x00000020) != nint.Zero &&
        (clickThroughStyle & (nint)0x08000000) != nint.Zero,
        "A click-through HTML overlay no longer remains a hidden tool window that is layered, transparent, and non-activating.");
    var interactiveStyle = (nint)calculateExtendedStyle.Invoke(
        null,
        [clickThroughStyle, false])!;
    Assert(
        (interactiveStyle & (nint)0x00080000) != nint.Zero &&
        (interactiveStyle & (nint)0x00000080) != nint.Zero &&
        (interactiveStyle & (nint)0x00040000) == nint.Zero &&
        (interactiveStyle & (nint)0x00000020) == nint.Zero &&
        (interactiveStyle & (nint)0x08000000) == nint.Zero,
        "An interactive HTML overlay entered the task switcher, passed clicks through, or refused activation.");
    var htmlOverlayFormSource = File.ReadAllText(Path.Combine(
        FindProjectRoot(),
        "src",
        "DalamudActCompat.ActRuntime",
        "HtmlOverlayForm.cs"));
    Assert(
        htmlOverlayFormSource.Contains(
            "SwpNoSize | SwpNoMove | SwpNoActivate | SwpFrameChanged",
            StringComparison.Ordinal) &&
        htmlOverlayFormSource.Contains("sealed class EditChromeForm", StringComparison.Ordinal) &&
        htmlOverlayFormSource.Contains("WsExTransparent", StringComparison.Ordinal) &&
        htmlOverlayFormSource.Contains(
            "MaximumWebViewInitializationAttempts = 6",
            StringComparison.Ordinal) &&
        htmlOverlayFormSource.Contains("ReplaceWebViewControl();", StringComparison.Ordinal) &&
        htmlOverlayFormSource.Contains("RetryFailedWebViewAsync", StringComparison.Ordinal) &&
        htmlOverlayFormSource.Contains("pendingInitialization", StringComparison.Ordinal) &&
        htmlOverlayFormSource.Contains("ShutdownCompletion", StringComparison.Ordinal) &&
        htmlOverlayFormSource.Contains("private volatile bool desiredVisible;", StringComparison.Ordinal) &&
        htmlOverlayFormSource.Contains("public void SetTemporarilyHidden(bool hidden)", StringComparison.Ordinal) &&
        htmlOverlayFormSource.Contains("if (desiredVisible)", StringComparison.Ordinal) &&
        htmlOverlayFormSource.Contains("form.BeginInvoke(async () =>", StringComparison.Ordinal) &&
        !htmlOverlayFormSource.Contains("form.Invoke(() =>", StringComparison.Ordinal) &&
        htmlOverlayFormSource.Contains(
            "WaitForBrowserProcessesExit",
            StringComparison.Ordinal) &&
        !htmlOverlayFormSource.Contains("MessageBox.Show", StringComparison.Ordinal),
        "HTML overlays lost native edit chrome, bounded WebView2 recovery, shutdown waiting, or restored a blocking error popup.");
    var selfHostedRuntimeSource = File.ReadAllText(Path.Combine(
        FindProjectRoot(),
        "src",
        "DalamudActCompat.ActRuntime",
        "SelfHostedActRuntime.cs"));
    Assert(
        selfHostedRuntimeSource.Contains("webview2-session.lock", StringComparison.Ordinal) &&
        selfHostedRuntimeSource.Contains(
            "TryBuildLocalCactbotOverlayUri",
            StringComparison.Ordinal) &&
        selfHostedRuntimeSource.Contains(
            "CloseConflictingRaidbossWindows",
            StringComparison.Ordinal) &&
        selfHostedRuntimeSource.Contains(
            "ReleaseWebViewSessionLockWhenSafe",
            StringComparison.Ordinal) &&
        selfHostedRuntimeSource.Contains("SetHtmlOverlaysSuppressed", StringComparison.Ordinal) &&
        selfHostedRuntimeSource.Contains("cactbotSettings?.SetTemporarilyHidden", StringComparison.Ordinal) &&
        selfHostedRuntimeSource.Contains("window.SetTemporarilyHidden(suppressed)", StringComparison.Ordinal) &&
        Regex.Matches(
            selfHostedRuntimeSource,
            Regex.Escape("cactbotOverlay.Show();")).Count == 1,
        "Cactbot windows are no longer lifecycle-locked, local-only, conflict-safe, or startup-intent driven.");
    var calculateBrowserInputPoint = formType.GetMethod(
                                         "CalculateBrowserInputPoint",
                                         BindingFlags.Static | BindingFlags.NonPublic)
                                     ?? throw new InvalidOperationException(
                                         "HTML overlay browser-input coordinate helper was not found.");
    Assert(
        calculateBrowserInputPoint.Invoke(
            null,
            [
                new System.Drawing.Size(1000, 500),
                new System.Drawing.SizeF(500, 250),
                new System.Drawing.Point(250, 100),
            ]) is System.Drawing.PointF { X: 125, Y: 50 },
        "HTML overlay proxy clicks no longer map to browser viewport coordinates.");
    var calculateInputProxyRectangles = formType.GetMethod(
                                            "CalculateInputProxyRectangles",
                                            BindingFlags.Static | BindingFlags.NonPublic)
                                        ?? throw new InvalidOperationException(
                                            "HTML overlay dynamic input-region mapper was not found.");
    var mappedInputRectangles = calculateInputProxyRectangles.Invoke(
        null,
        [
            new System.Drawing.Size(400, 200),
            new System.Drawing.SizeF(200, 100),
            new[] { new System.Drawing.RectangleF(50, 20, 100, 40) },
        ]) as System.Drawing.Rectangle[];
    Assert(
        mappedInputRectangles is [{ X: 98, Y: 38, Width: 204, Height: 84 }],
        "HTML overlay visible browser regions no longer map to the input proxy with safe padding.");
    var inputRegionScript = formType.GetField(
                                "OverlayInputRegionScript",
                                BindingFlags.Static | BindingFlags.NonPublic)
                            ?.GetRawConstantValue() as string
                            ?? throw new InvalidOperationException(
                                "HTML overlay dynamic input-region script was not found.");
    Assert(
        htmlOverlayFormSource.Contains("Opacity = 0.01", StringComparison.Ordinal) &&
        htmlOverlayFormSource.Contains("inputProxy.Show(form)", StringComparison.Ordinal) &&
        htmlOverlayFormSource.Contains("proxy.MouseWheel += OnInputProxyMouseWheel", StringComparison.Ordinal) &&
        htmlOverlayFormSource.Contains("Input.dispatchMouseEvent", StringComparison.Ordinal) &&
        htmlOverlayFormSource.Contains("type = \"mouseWheel\"", StringComparison.Ordinal) &&
        htmlOverlayFormSource.Contains("inputProxy.Region = nextRegion", StringComparison.Ordinal) &&
        inputRegionScript.Contains("ResizeObserver", StringComparison.Ordinal) &&
        inputRegionScript.Contains("MutationObserver", StringComparison.Ordinal) &&
        inputRegionScript.Contains("dalamud-act-compat:input-regions:", StringComparison.Ordinal),
        "The transparent HTML overlay no longer has a content-shaped dynamic input proxy.");
    Assert(
        inputRegionScript.Contains("performance.now()", StringComparison.Ordinal) &&
        inputRegionScript.Contains("diagnostics.refreshCount", StringComparison.Ordinal) &&
        inputRegionScript.Contains("scannedElements", StringComparison.Ordinal) &&
        inputRegionScript.Contains("peakFinalRectangles", StringComparison.Ordinal) &&
        inputRegionScript.Contains("diagnosticWindowMilliseconds >= 10000", StringComparison.Ordinal) &&
        htmlOverlayFormSource.Contains(
            "window.__dalamudActCompatInputRegionDiagnostics",
            StringComparison.Ordinal) &&
        htmlOverlayFormSource.Contains("RecordRegionRebuildDiagnostic", StringComparison.Ordinal) &&
        htmlOverlayFormSource.Contains("Windows input-region rebuild diagnostic", StringComparison.Ordinal),
        "HTML dynamic hit-region profiling is missing, unthrottled, or no longer debug-only.");
    var disabledDiagnosticBranch = inputRegionScript.IndexOf(
        "if (diagnostics === null)",
        StringComparison.Ordinal);
    var enabledDiagnosticMeasurement = inputRegionScript.IndexOf(
        "const startedAt = performance.now();",
        StringComparison.Ordinal);
    var disabledFastPath = disabledDiagnosticBranch >= 0 &&
                           enabledDiagnosticMeasurement > disabledDiagnosticBranch
        ? inputRegionScript[disabledDiagnosticBranch..enabledDiagnosticMeasurement]
        : string.Empty;
    Assert(
        inputRegionScript.Contains("let diagnostics = null;", StringComparison.Ordinal) &&
        inputRegionScript.Contains(
            "if (window.__dalamudActCompatInputRegionDiagnostics === true)",
            StringComparison.Ordinal) &&
        disabledFastPath.Contains("rectangles: collectRegions()", StringComparison.Ordinal) &&
        disabledFastPath.Contains("return;", StringComparison.Ordinal) &&
        !disabledFastPath.Contains("performance.now()", StringComparison.Ordinal) &&
        !disabledFastPath.Contains("Math.max", StringComparison.Ordinal) &&
        htmlOverlayFormSource.IndexOf("if (!debugMode)", StringComparison.Ordinal) <
        htmlOverlayFormSource.IndexOf(
            "var diagnosticTimer = Stopwatch.StartNew();",
            StringComparison.Ordinal),
        "HTML input-region diagnostics still execute measurement work on the disabled fast path.");
    var shieldType = typeof(MeterService).Assembly.GetType(
                         "DalamudActCompat.UI.OverlayEditShield",
                         throwOnError: true)
                     ?? throw new InvalidOperationException(
                         "The transparent overlay edit shield was not found.");
    var isShieldRequired = shieldType.GetMethod(
                               "IsRequired",
                               BindingFlags.Static | BindingFlags.NonPublic)
                           ?? throw new InvalidOperationException(
                               "The transparent overlay edit shield condition was not found.");
    Assert(
        isShieldRequired.Invoke(null, [false, false]) as bool? == false,
        "The transparent edit shield remained active without a visible editing overlay.");
    Assert(
        isShieldRequired.Invoke(null, [true, false]) as bool? == true,
        "The transparent edit shield did not activate for a visible editing overlay.");
    Assert(
        isShieldRequired.Invoke(null, [true, true]) as bool? == false,
        "The transparent edit shield blocked an open ACT management window.");
}

static async Task ValidateLiveHtmlOverlayInputAsync(string testRoot)
{
    var projectRoot = FindProjectRoot();
    var loaderPath = Path.Combine(
        projectRoot,
        "src",
        "DalamudActCompat.ActRuntime",
        "bin",
        "Release",
        "net10.0-windows",
        "win-x64",
        "WebView2Loader.dll");
    Assert(File.Exists(loaderPath), "The live WebView2 input smoke could not find WebView2Loader.dll.");

    var pagePath = Path.Combine(testRoot, "html-overlay-input-smoke.html");
    await File.WriteAllTextAsync(
        pagePath,
        """
        <!doctype html>
        <html>
        <body style="margin:0;background:transparent">
          <div id="panel" style="position:absolute;left:20px;top:20px;width:200px;height:100px;
                                 overflow:auto;background:rgba(20,30,40,.9)">
            <button id="probe" style="position:absolute;left:20px;top:20px;width:120px;height:50px"
                    onclick="document.documentElement.dataset.clicked='true'">Click</button>
            <div style="height:600px"></div>
          </div>
          <script>
            window.collapseProbe = () => {
              const panel = document.getElementById('panel');
              panel.style.width = '60px';
              panel.style.height = '30px';
              document.getElementById('probe').style.display = 'none';
            };
          </script>
        </body>
        </html>
        """);

    var formType = typeof(HtmlOverlayWindowSettings).Assembly.GetType(
                       "DalamudActCompat.ActRuntime.HtmlOverlayForm")
                   ?? throw new InvalidOperationException("HTML overlay form was not found.");
    var settings = new HtmlOverlayWindowSettings
    {
        IsClickThrough = false,
        IsLocked = true,
        Left = 80,
        Top = 80,
        Width = 320,
        Height = 200,
    };
    var log = DispatchProxy.Create<IPluginLog, NoOpPluginLogProxy>();
    var instance = Activator.CreateInstance(
                       formType,
                       BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                       binder: null,
                       [
                           new Uri(pagePath),
                           Path.Combine(testRoot, "webview2-input-smoke"),
                           loaderPath,
                           "HTML Input Proxy Smoke",
                           true,
                           settings,
                           new System.Drawing.Size(320, 200),
                           false,
                           log,
                           null,
                           null,
                       ],
                       culture: null)
                   ?? throw new InvalidOperationException("HTML overlay input smoke form was not created.");
    var webViewField = formType.GetField("webView", BindingFlags.Instance | BindingFlags.NonPublic)
                       ?? throw new InvalidOperationException("HTML overlay WebView field was not found.");
    var formField = formType.GetField("form", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("HTML overlay host form field was not found.");
    var proxyField = formType.GetField("inputProxy", BindingFlags.Instance | BindingFlags.NonPublic)
                     ?? throw new InvalidOperationException("HTML overlay input proxy field was not found.");
    var editChromeField = formType.GetField(
                              "editChrome",
                              BindingFlags.Instance | BindingFlags.NonPublic)
                          ?? throw new InvalidOperationException(
                              "Native HTML overlay edit chrome field was not found.");
    var browserStateField = formType.GetField(
                                "browserState",
                                BindingFlags.Instance | BindingFlags.NonPublic)
                            ?? throw new InvalidOperationException(
                             "HTML overlay browser state field was not found.");
    var navigationStartedField = formType.GetField(
                                     "navigationStarted",
                                     BindingFlags.Instance | BindingFlags.NonPublic)
                                 ?? throw new InvalidOperationException(
                                     "HTML overlay navigation state field was not found.");
    var show = formType.GetMethod("Show", BindingFlags.Instance | BindingFlags.Public)
               ?? throw new InvalidOperationException("HTML overlay show method was not found.");
    var hide = formType.GetMethod("Hide", BindingFlags.Instance | BindingFlags.Public)
               ?? throw new InvalidOperationException("HTML overlay hide method was not found.");
    var applySettings = formType.GetMethod("ApplySettings", BindingFlags.Instance | BindingFlags.Public)
                        ?? throw new InvalidOperationException(
                            "HTML overlay ApplySettings method was not found.");
    var browserProcessIdsProperty = formType.GetProperty(
                                        "BrowserProcessIds",
                                        BindingFlags.Instance | BindingFlags.Public)
                                    ?? throw new InvalidOperationException(
                                     "HTML overlay browser process list was not found.");
    var shutdownCompletionProperty = formType.GetProperty(
                                         "ShutdownCompletion",
                                         BindingFlags.Instance | BindingFlags.Public)
                                     ?? throw new InvalidOperationException(
                                         "HTML overlay shutdown completion was not exposed.");
    var waitForBrowserProcessesExit = formType.GetMethod(
                                          "WaitForBrowserProcessesExit",
                                          BindingFlags.Static | BindingFlags.Public)
                                      ?? throw new InvalidOperationException(
                                          "HTML overlay browser shutdown waiter was not found.");

    NativeInputProbe.GetCursorPos(out var originalCursor);
    try
    {
        show.Invoke(instance, null);
        Control? webView = null;
        Form? hostForm = null;
        Form? proxy = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            webView = webViewField.GetValue(instance) as Control;
            hostForm = formField.GetValue(instance) as Form;
            proxy = proxyField.GetValue(instance) as Form;
            if (webView?.IsHandleCreated == true && hostForm?.IsHandleCreated == true &&
                proxy?.IsHandleCreated == true &&
                await ExecuteBrowserScriptAsync(webView, "document.readyState") == "\"complete\"")
            {
                break;
            }

            await Task.Delay(100);
        }

        Assert(
            webView is not null && hostForm is not null && proxy is not null,
            "The live HTML input smoke did not create its windows.");
        var liveWebView = webView!;
        var liveHostForm = hostForm!;
        var liveProxy = proxy!;
        settings.DisplayName = "Renamed HTML Overlay";
        applySettings.Invoke(instance, null);
        var renamedWindows = false;
        deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var hostRenamed = await InvokeControlAsync(
                liveHostForm,
                () => liveHostForm.Text == settings.DisplayName);
            var proxyRenamed = await InvokeControlAsync(
                liveProxy,
                () => liveProxy.Text == $"{settings.DisplayName} Input");
            renamedWindows = hostRenamed && proxyRenamed;
            if (renamedWindows)
            {
                break;
            }

            await Task.Delay(100);
        }

        Assert(
            renamedWindows,
            "Renaming an open HTML overlay did not update its host and input-window titles.");
        var proxyHandle = await InvokeControlAsync(liveProxy, () => liveProxy.Handle);
        var hostHandle = await InvokeControlAsync(liveHostForm, () => liveHostForm.Handle);
        var hostStyle = NativeInputProbe.GetWindowLongPtr(hostHandle, NativeInputProbe.GwlExStyle);
        var proxyStyle = NativeInputProbe.GetWindowLongPtr(proxyHandle, NativeInputProbe.GwlExStyle);
        Assert(
            (hostStyle & (nint)0x00000080) != nint.Zero &&
            (hostStyle & (nint)0x00040000) == nint.Zero &&
            (proxyStyle & (nint)0x00000080) != nint.Zero &&
            (proxyStyle & (nint)0x00040000) == nint.Zero,
            "The live HTML overlay host or input proxy is still eligible for Alt+Tab or Task View.");
        var initialRegionReady = false;
        deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var contentWindow = await WindowAtProxyPointAsync(
                liveProxy,
                new System.Drawing.Point(100, 65));
            var blankWindow = await WindowAtProxyPointAsync(
                liveProxy,
                new System.Drawing.Point(280, 160));
            initialRegionReady = contentWindow == proxyHandle &&
                                 blankWindow != proxyHandle &&
                                 blankWindow != hostHandle;
            if (initialRegionReady)
            {
                break;
            }

            await Task.Delay(100);
        }

        Assert(
            initialRegionReady,
            "The live HTML input proxy did not exclude transparent space outside visible content.");
        var clickPoint = await InvokeControlAsync(
            liveProxy,
            () => liveProxy.PointToScreen(new System.Drawing.Point(100, 65)));
        NativeInputProbe.SetCursorPos(clickPoint.X, clickPoint.Y);
        NativeInputProbe.MouseEvent(NativeInputProbe.LeftDown, 0, 0, 0, UIntPtr.Zero);
        await Task.Delay(80);
        NativeInputProbe.MouseEvent(NativeInputProbe.LeftUp, 0, 0, 0, UIntPtr.Zero);

        var clicked = false;
        deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            clicked = await ExecuteBrowserScriptAsync(
                          liveWebView,
                          "document.documentElement.dataset.clicked || ''") == "\"true\"";
            if (clicked)
            {
                break;
            }

            await Task.Delay(100);
        }

        Assert(clicked, "A physical proxy click did not reach the live WebView2 button.");

        var onMouseWheel = typeof(Control).GetMethod(
                               "OnMouseWheel",
                               BindingFlags.Instance | BindingFlags.NonPublic)
                           ?? throw new InvalidOperationException(
                               "The WinForms mouse-wheel dispatcher was not found.");
        await InvokeControlAsync(liveProxy, () =>
        {
            onMouseWheel.Invoke(
                liveProxy,
                [new MouseEventArgs(MouseButtons.None, 0, 100, 65, -120)]);
            return true;
        });
        var scrolled = false;
        deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            scrolled = await ExecuteBrowserScriptAsync(
                           liveWebView,
                           "document.getElementById('panel').scrollTop") != "0";
            if (scrolled)
            {
                break;
            }

            await Task.Delay(100);
        }

        Assert(scrolled, "An input-proxy wheel event did not scroll the live WebView2 page.");

        await ExecuteBrowserScriptAsync(liveWebView, "window.collapseProbe(); true");
        var collapsedRegionReady = false;
        deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var collapsedContentWindow = await WindowAtProxyPointAsync(
                liveProxy,
                new System.Drawing.Point(40, 30));
            var releasedWindow = await WindowAtProxyPointAsync(
                liveProxy,
                new System.Drawing.Point(100, 65));
            collapsedRegionReady = collapsedContentWindow == proxyHandle &&
                                   releasedWindow != proxyHandle &&
                                   releasedWindow != hostHandle;
            if (collapsedRegionReady)
            {
                break;
            }

            await Task.Delay(100);
        }

        Assert(
            collapsedRegionReady,
            "The live HTML input proxy did not shrink after the visible page content collapsed.");

        await InvokeControlAsync(liveWebView, () =>
        {
            liveWebView.Visible = false;
            return true;
        });
        settings.SetEditing(true);
        applySettings.Invoke(instance, null);
        var editingUsesFullRegion = false;
        var nativeEditChromeVisible = false;
        deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            editingUsesFullRegion =
                await WindowAtProxyPointAsync(liveProxy, new System.Drawing.Point(280, 160)) ==
                proxyHandle;
            if (editChromeField.GetValue(instance) is Form chrome && chrome.IsHandleCreated)
            {
                nativeEditChromeVisible = await InvokeControlAsync(
                    chrome,
                    () => chrome.Visible && chrome.Bounds == liveHostForm.Bounds &&
                          chrome.Text == $"{settings.DisplayName} Edit Boundary" &&
                          (NativeInputProbe.GetWindowLongPtr(
                               chrome.Handle,
                               NativeInputProbe.GwlExStyle) & (nint)0x00000020) != nint.Zero);
            }
            if (editingUsesFullRegion && nativeEditChromeVisible)
            {
                break;
            }

            await Task.Delay(100);
        }

        Assert(
            editingUsesFullRegion && nativeEditChromeVisible,
            "HTML overlay editing mode did not restore the full input region and independent native edit boundary.");

        var beforeMove = await InvokeControlAsync(liveHostForm, () => liveHostForm.Bounds);
        var moveStart = await InvokeControlAsync(
            liveHostForm,
            () => liveHostForm.PointToScreen(new System.Drawing.Point(
                liveHostForm.ClientSize.Width / 2,
                liveHostForm.ClientSize.Height / 2)));
        NativeInputProbe.SetCursorPos(moveStart.X, moveStart.Y);
        NativeInputProbe.MouseEvent(NativeInputProbe.LeftDown, 0, 0, 0, UIntPtr.Zero);
        await Task.Delay(80);
        NativeInputProbe.SetCursorPos(moveStart.X + 45, moveStart.Y + 30);
        await Task.Delay(180);
        NativeInputProbe.MouseEvent(NativeInputProbe.LeftUp, 0, 0, 0, UIntPtr.Zero);
        await Task.Delay(120);
        var movedBounds = await InvokeControlAsync(liveHostForm, () => liveHostForm.Bounds);
        Assert(
            movedBounds.Left >= beforeMove.Left + 35 &&
            movedBounds.Top >= beforeMove.Top + 20,
            "A physical drag over the native edit boundary did not move the blank-capable overlay host.");

        var beforeResize = movedBounds;
        var resizeStart = await InvokeControlAsync(
            liveHostForm,
            () => liveHostForm.PointToScreen(new System.Drawing.Point(
                liveHostForm.ClientSize.Width - 8,
                liveHostForm.ClientSize.Height - 8)));
        NativeInputProbe.SetCursorPos(resizeStart.X, resizeStart.Y);
        NativeInputProbe.MouseEvent(NativeInputProbe.LeftDown, 0, 0, 0, UIntPtr.Zero);
        await Task.Delay(80);
        NativeInputProbe.SetCursorPos(resizeStart.X + 35, resizeStart.Y + 25);
        await Task.Delay(180);
        NativeInputProbe.MouseEvent(NativeInputProbe.LeftUp, 0, 0, 0, UIntPtr.Zero);
        await Task.Delay(120);
        var resizedBounds = await InvokeControlAsync(liveHostForm, () => liveHostForm.Bounds);
        Assert(
            resizedBounds.Width >= beforeResize.Width + 25 &&
            resizedBounds.Height >= beforeResize.Height + 15,
            "A physical drag on the native bottom-right grip did not resize the overlay host.");

        await InvokeControlAsync(liveWebView, () =>
        {
            liveWebView.Visible = true;
            return true;
        });
        settings.SetEditing(false);
        applySettings.Invoke(instance, null);
        var finishedEditingRestoresClickThrough = false;
        deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var interactiveWindow = await WindowAtProxyPointAsync(
                liveProxy,
                new System.Drawing.Point(100, 65));
            finishedEditingRestoresClickThrough = interactiveWindow != proxyHandle &&
                                                  interactiveWindow != hostHandle;
            if (finishedEditingRestoresClickThrough)
            {
                break;
            }

            await Task.Delay(100);
        }

        Assert(
            finishedEditingRestoresClickThrough &&
            settings.IsLocked &&
            settings.IsClickThrough &&
            !settings.IsEditing,
            "Finishing HTML overlay editing did not restore the locked click-through state.");
        if (editChromeField.GetValue(instance) is Form hiddenChrome)
        {
            Assert(
                !await InvokeControlAsync(hiddenChrome, () => hiddenChrome.Visible),
                "The native HTML overlay edit boundary remained visible after editing finished.");
        }

        hide.Invoke(instance, null);
        browserStateField.SetValue(
            instance,
            Enum.Parse(browserStateField.FieldType, "Failed"));
        show.Invoke(instance, null);
        var reopenedAfterFailure = false;
        deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            reopenedAfterFailure = string.Equals(
                browserStateField.GetValue(instance)?.ToString(),
                "Loaded",
                StringComparison.Ordinal);
            if (reopenedAfterFailure)
            {
                break;
            }

            await Task.Delay(100);
        }

        Assert(
            reopenedAfterFailure,
            "Closing and reopening a failed HTML overlay did not retry its WebView2 page.");

        hide.Invoke(instance, null);
        var webViewBeforePreNavigationRetry = webViewField.GetValue(instance);
        navigationStartedField.SetValue(instance, false);
        browserStateField.SetValue(
            instance,
            Enum.Parse(browserStateField.FieldType, "Failed"));
        show.Invoke(instance, null);
        var rebuiltBeforeNavigation = false;
        deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            rebuiltBeforeNavigation =
                !ReferenceEquals(webViewField.GetValue(instance), webViewBeforePreNavigationRetry) &&
                string.Equals(
                    browserStateField.GetValue(instance)?.ToString(),
                    "Loaded",
                    StringComparison.Ordinal);
            if (rebuiltBeforeNavigation)
            {
                break;
            }

            await Task.Delay(100);
        }

        Assert(
            rebuiltBeforeNavigation,
            "A failed WebView2 that had not begun navigation reloaded about:blank instead of rebuilding.");
    }
    finally
    {
        NativeInputProbe.SetCursorPos(originalCursor.X, originalCursor.Y);
        var browserProcessIds =
            (browserProcessIdsProperty.GetValue(instance) as IEnumerable<int> ?? [])
            .ToHashSet();
        ((IDisposable)instance).Dispose();
        var shutdownCompletion = shutdownCompletionProperty.GetValue(instance) as Task
                                 ?? throw new InvalidOperationException(
                                     "HTML overlay shutdown completion was not a Task.");
        await shutdownCompletion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert(shutdownCompletion.IsCompletedSuccessfully, "HTML overlay shutdown did not complete cleanly.");
        browserProcessIds.UnionWith(
            browserProcessIdsProperty.GetValue(instance) as IEnumerable<int> ?? []);
        waitForBrowserProcessesExit.Invoke(
            null,
            [browserProcessIds, TimeSpan.FromSeconds(5), log]);
    }
}

static async Task<nint> WindowAtProxyPointAsync(Form proxy, System.Drawing.Point clientPoint)
{
    var screenPoint = await InvokeControlAsync(proxy, () => proxy.PointToScreen(clientPoint));
    return NativeInputProbe.WindowFromPoint(new NativeInputProbe.Point
    {
        X = screenPoint.X,
        Y = screenPoint.Y,
    });
}

static Task<T> InvokeControlAsync<T>(Control control, Func<T> action)
{
    var completion = new TaskCompletionSource<T>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    control.BeginInvoke(() =>
    {
        try
        {
            completion.SetResult(action());
        }
        catch (Exception ex)
        {
            completion.SetException(ex);
        }
    });
    return completion.Task;
}

static Task<string> ExecuteBrowserScriptAsync(Control webView, string script)
{
    var completion = new TaskCompletionSource<string>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    webView.BeginInvoke(async () =>
    {
        try
        {
            var coreWebView = webView.GetType()
                .GetProperty("CoreWebView2", BindingFlags.Instance | BindingFlags.Public)
                ?.GetValue(webView);
            if (coreWebView is null)
            {
                completion.SetResult(string.Empty);
                return;
            }

            var executeScript = coreWebView.GetType().GetMethod(
                                    "ExecuteScriptAsync",
                                    BindingFlags.Instance | BindingFlags.Public,
                                    binder: null,
                                    [typeof(string)],
                                    modifiers: null)
                                ?? throw new InvalidOperationException(
                                    "WebView2 ExecuteScriptAsync was not found.");
            var result = executeScript.Invoke(coreWebView, [script]) as Task<string>
                         ?? throw new InvalidOperationException(
                             "WebView2 ExecuteScriptAsync did not return a string task.");
            completion.SetResult(await result);
        }
        catch (Exception ex)
        {
            completion.SetException(ex);
        }
    });
    return completion.Task;
}

static void ValidateActTtsDispatch()
{
    var actMain = (FormActMain)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
        typeof(FormActMain));
    string? spoken = null;
    actMain.PlayTtsMethod = message => spoken = message;
    actMain.TTS("FoxTTS bridge");
    Assert(
        spoken == "FoxTTS bridge",
        "The ACT TTS entry point bypassed the standard PlayTtsMethod delegate.");
}

static void ValidateFoxTtsBridge()
{
    var previousActMain = ActGlobals.oFormActMain;
    var actMain = (FormActMain)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
        typeof(FormActMain));
    var restored = false;
    actMain.PlayTtsMethod = _ => restored = true;
    ActGlobals.oFormActMain = actMain;
    try
    {
        using var foxTts = new FoxTtsProbe();
        var log = DispatchProxy.Create<IPluginLog, NoOpPluginLogProxy>();
        var removeBridge = LoadedActPlugin.InstallFoxTtsBridge(foxTts, log);
        actMain.TTS("Cactbot alert");
        Assert(
            foxTts.Wait(TimeSpan.FromSeconds(5)) && foxTts.LastMessage == "Cactbot alert",
            "The ACT.FoxTTS bridge did not forward a TTS request to FoxTTS Speak.");

        removeBridge();
        actMain.TTS("restored");
        Assert(restored, "Removing the ACT.FoxTTS bridge did not restore the previous TTS delegate.");
    }
    finally
    {
        ActGlobals.oFormActMain = previousActMain;
    }
}

static void ValidateRuntimePluginStartupOrder()
{
    var mustLoadBeforeOverlay = typeof(IinactAdapter).GetMethod(
                                    "MustLoadBeforeOverlay",
                                    BindingFlags.Static | BindingFlags.NonPublic)
                                ?? throw new InvalidOperationException(
                                    "Runtime plugin startup phase selector was not found.");
    var foxTts = new RuntimePluginSpec(
        "ACT.FoxTTS",
        "C:\\plugins\\foxtts",
        "ACT.FoxTTS.dll",
        "ACT.FoxTTS.FoxTTSPlugin");
    var triggernometry = new RuntimePluginSpec(
        "triggernometry",
        "C:\\plugins\\triggernometry",
        "Triggernometry.dll",
        "Triggernometry.Plugin");
    Assert(
        mustLoadBeforeOverlay.Invoke(null, [foxTts]) as bool? == true,
        "FoxTTS is no longer guaranteed to own the ACT TTS dispatcher before Cactbot starts.");
    Assert(
        mustLoadBeforeOverlay.Invoke(null, [triggernometry]) as bool? == false,
        "An unrelated ACT extension was moved ahead of OverlayPlugin startup.");
}

static void ValidateCactbotSpokenAlertDefaults()
{
    var method = typeof(CactbotEventSource).GetMethod(
        "EnsureDefaultAlertOutput",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Cactbot alert-output migration helper was not found.");
    var radarChangeMethod = typeof(CactbotEventSource).GetMethod(
        "HaveRadarOptionsChanged",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Cactbot Radar option-change helper was not found.");
    var previousOptions = JObject.FromObject(new
    {
        radar = new { TTS = true, PopSound = true },
        raidboss = new { DefaultAlertOutput = "ttsAndText" },
    });
    var radarDisabled = JObject.FromObject(new
    {
        radar = new { TTS = false, PopSound = true },
        raidboss = new { DefaultAlertOutput = "ttsAndText" },
    });
    var raidbossOnly = JObject.FromObject(new
    {
        radar = new { TTS = true, PopSound = true },
        raidboss = new { DefaultAlertOutput = "textOnly" },
    });
    Assert(
        radarChangeMethod.Invoke(null, ["options", previousOptions, radarDisabled]) as bool? == true,
        "Turning off Radar TTS did not request a targeted Radar reload.");
    Assert(
        radarChangeMethod.Invoke(null, ["options", previousOptions, raidbossOnly]) as bool? == false,
        "A raidboss-only option edit incorrectly requested a Radar reload.");
    Assert(
        radarChangeMethod.Invoke(null, ["user", previousOptions, radarDisabled]) as bool? == false,
        "A non-options cactbot save incorrectly requested a Radar reload.");
    var empty = new Dictionary<string, JToken>();
    Assert(
        method.Invoke(null, [empty]) as bool? == true,
        "A new Cactbot configuration did not select text and TTS output.");
    Assert(
        empty["options"]["raidboss"]?["DefaultAlertOutput"]?.Value<string>() == "ttsAndText" &&
        empty["options"]["raidboss"]?["DefaultPlayerLabel"]?.Value<string>() == "jobFull" &&
        empty["options"]["raidboss"]?["SpokenAlertsEnabled"] is null,
        "The initial Cactbot output or full-job player label was written incorrectly.");

    var legacyEnabled = new Dictionary<string, JToken>
    {
        ["options"] = JObject.FromObject(new
        {
            raidboss = new
            {
                SpokenAlertsEnabled = true,
            },
        }),
    };
    Assert(
        method.Invoke(null, [legacyEnabled]) as bool? == true &&
        legacyEnabled["options"]["raidboss"]?["DefaultAlertOutput"]?.Value<string>() ==
            "ttsAndText" &&
        legacyEnabled["options"]["raidboss"]?["SpokenAlertsEnabled"] is null,
        "The v0.2.24 spoken-alert setting was not migrated to text and TTS output.");

    var legacyDisabled = new Dictionary<string, JToken>
    {
        ["options"] = JObject.FromObject(new
        {
            raidboss = new
            {
                SpokenAlertsEnabled = false,
            },
        }),
    };
    Assert(
        method.Invoke(null, [legacyDisabled]) as bool? == true &&
        legacyDisabled["options"]["raidboss"]?["DefaultAlertOutput"]?.Value<string>() ==
            "textAndSound" &&
        legacyDisabled["options"]["raidboss"]?["SpokenAlertsEnabled"] is null,
        "A legacy Cactbot spoken-alert opt-out was not preserved during migration.");

    var existing = new Dictionary<string, JToken>
    {
        ["options"] = JObject.FromObject(new
        {
            raidboss = new
            {
                DefaultAlertOutput = "textOnly",
                DefaultPlayerLabel = "name",
            },
        }),
    };
    Assert(
        method.Invoke(null, [existing]) as bool? == false &&
        existing["options"]["raidboss"]?["DefaultAlertOutput"]?.Value<string>() == "textOnly" &&
        existing["options"]["raidboss"]?["DefaultPlayerLabel"]?.Value<string>() == "name",
        "An explicit Cactbot output mode or player label was overwritten.");

    var guardType = typeof(CactbotEventSource).Assembly.GetType(
                        "RainbowMage.OverlayPlugin.EventSources.CactbotTtsDuplicateGuard",
                        throwOnError: true)!;
    var guard = Activator.CreateInstance(
                    guardType,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    binder: null,
                    [TimeSpan.FromMilliseconds(500)],
                    culture: null)
                ?? throw new InvalidOperationException("Cactbot TTS duplicate guard was not created.");
    var tryAccept = guardType.GetMethod(
                        "TryAccept",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("Cactbot TTS duplicate guard has no acceptance method.");
    var start = Stopwatch.GetTimestamp();
    Assert(
        tryAccept.Invoke(guard, ["Stack marker", start]) as bool? == true &&
        tryAccept.Invoke(guard, ["Stack marker", start + Stopwatch.Frequency / 10]) as bool? == false &&
        tryAccept.Invoke(guard, ["Spread marker", start + Stopwatch.Frequency / 5]) as bool? == true &&
        tryAccept.Invoke(guard, ["Stack marker", start + Stopwatch.Frequency]) as bool? == true,
        "Two Cactbot windows no longer collapse the same simultaneous TTS request to one playback.");
}

static void ValidateOverlayInitialStateEvents()
{
    var eventSource = typeof(CactbotEventSource).Assembly.GetType(
                          "RainbowMage.OverlayPlugin.EventSources.FFXIVOptionalEventSource")
                      ?? throw new InvalidOperationException(
                          "OverlayPlugin optional event source was not found.");
    var createZone = eventSource.GetMethod(
                         "CreateChangeZoneEvent",
                         BindingFlags.Static | BindingFlags.NonPublic)
                     ?? throw new InvalidOperationException(
                         "Initial ChangeZone event factory was not found.");
    var zone = (JObject)createZone.Invoke(null, [0x155u, "The Goblet"])!;
    Assert(
        zone["type"]?.Value<string>() == "ChangeZone" &&
        zone["zoneID"]?.Value<uint>() == 0x155u &&
        zone["zoneName"]?.Value<string>() == "The Goblet",
        "Initial ChangeZone event did not match the OverlayPlugin protocol.");

    var createPlayer = eventSource.GetMethod(
                           "CreateChangePrimaryPlayerEvent",
                           BindingFlags.Static | BindingFlags.NonPublic)
                       ?? throw new InvalidOperationException(
                           "Initial ChangePrimaryPlayer event factory was not found.");
    var player = (JObject)createPlayer.Invoke(null, [0x100227F7u, "Test Player"])!;
    Assert(
        player["type"]?.Value<string>() == "ChangePrimaryPlayer" &&
        player["charID"]?.Value<uint>() == 0x100227F7u &&
        player["charName"]?.Value<string>() == "Test Player",
        "Initial ChangePrimaryPlayer event did not match the OverlayPlugin protocol.");
}

static async Task ValidateOverlayWebSocketFatalAcceptRecoveryAsync()
{
    using var container = new RainbowMage.OverlayPlugin.TinyIoCContainer();
    var logger = new OverlayServerTestLogger();
    var config = new OverlayServerTestConfig
    {
        WSServerIP = IPAddress.Loopback.ToString(),
        WSServerPort = 0,
        WSServerRunning = true,
    };
    container.Register<RainbowMage.OverlayPlugin.ILogger>(logger);
    container.Register<RainbowMage.OverlayPlugin.IPluginConfig>(config);

    var controller = new ServerController(container);
    controller.Start();
    Assert(controller.Running, "Overlay WebSocket recovery probe could not start its listener.");

    var originalServer = GetOverlayServer(controller);
    InvalidateOverlayListener(originalServer);

    var timeout = Stopwatch.StartNew();
    object? recoveredServer = null;
    while (timeout.Elapsed < TimeSpan.FromSeconds(5))
    {
        recoveredServer = GetOverlayServer(controller, required: false);
        if (controller.Running &&
            recoveredServer is not null &&
            !ReferenceEquals(recoveredServer, originalServer))
        {
            break;
        }

        await Task.Delay(25);
    }

    Assert(
        controller.Running &&
        recoveredServer is not null &&
        !ReferenceEquals(recoveredServer, originalServer),
        "A fatal accept error did not replace the invalid WebSocket listener.");
    Assert(
        !((NetCoreServer.TcpServer)originalServer).IsAccepting,
        "The invalid WebSocket listener remained in the recursive accept state.");
    // Windows may report either fatal code for the same externally closed listener, depending on runtime timing.
    var fatalAcceptErrors = logger.Messages.Where(message =>
        message.Contains("caught an error with code NotSocket", StringComparison.Ordinal) ||
        message.Contains("caught an error with code InvalidArgument", StringComparison.Ordinal)).ToArray();
    Assert(
        fatalAcceptErrors.Length == 1,
        $"The invalid WebSocket listener emitted {fatalAcceptErrors.Length} fatal errors before its circuit breaker ran.");
    Assert(
        logger.Messages.Any(message =>
            message.Contains("recovered with a new listener", StringComparison.Ordinal)),
        "The WebSocket controller did not report successful listener recovery.");

    controller.Stop();
    await Task.Delay(500);
    Assert(
        !controller.Running && GetOverlayServer(controller, required: false) is null,
        "An explicit WebSocket stop was undone by a pending recovery task.");
}

static object GetOverlayServer(ServerController controller, bool required = true)
{
    var server = typeof(ServerController).GetProperty(
                     "Server",
                     BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(controller);
    if (server is null && required)
    {
        throw new InvalidOperationException("Overlay WebSocket controller has no active server.");
    }

    return server!;
}

static void InvalidateOverlayListener(object server)
{
    var acceptor = typeof(NetCoreServer.TcpServer).GetField(
                       "_acceptorSocket",
                       BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(server) as Socket
                   ?? throw new InvalidOperationException("NetCoreServer acceptor socket was not found.");

    // A native close preserves the managed Socket's live state and reproduces the stale listener from the crash log.
    var result = NativeSocketProbe.Close(acceptor.Handle);
    Assert(result == 0, $"The native WebSocket listener close failed with error {Marshal.GetLastWin32Error()}.");
}

static void ValidateSettingsSerializerMemberTypes()
{
    var owner = new EnumSettingsOwner();
    using var serializer = new SettingsSerializer(owner);
    serializer.AddIntSetting(nameof(EnumSettingsOwner.PluginIntegration));
    using var textReader = new StringReader(
        "<SettingsSerializer><PluginIntegration>Auto</PluginIntegration></SettingsSerializer>");
    using var reader = new XmlTextReader(textReader);
    reader.ReadToFollowing("SettingsSerializer");
    serializer.ImportFromXml(reader);
    Assert(
        owner.PluginIntegration == PluginIntegrationMode.Auto,
        "SettingsSerializer did not use the member's enum type for a legacy integer registration.");
}

static void ValidateActPluginDataCompatibility()
{
    var type = typeof(ActPluginData);
    Assert(
        type.GetField("pPluginInfo")?.FieldType == typeof(Panel),
        "ActPluginData.pPluginInfo compatibility field is missing.");
    Assert(
        type.GetField("btnXButton")?.FieldType == typeof(Button),
        "ActPluginData.btnXButton compatibility field is missing.");
    Assert(
        typeof(FormActMain).GetMethod(
            "PluginGetSelfData",
            BindingFlags.Instance | BindingFlags.Public,
            null,
            [typeof(IActPluginV1)],
            null)?.ReturnType == typeof(ActPluginData),
        "FormActMain does not expose the exact PluginGetSelfData(IActPluginV1) ABI required by SilverDasher.");
    Assert(
        typeof(FormActMain).GetMethod(
            "PluginGetSelfData",
            BindingFlags.Instance | BindingFlags.Public,
            null,
            [typeof(object)],
            null)?.ReturnType == typeof(ActPluginData),
        "The existing PluginGetSelfData(object) compatibility overload was replaced.");
}

static ZipArchiveEntry? FindArchiveEntry(ZipArchive archive, string normalizedPath)
{
    return archive.Entries.SingleOrDefault(entry => string.Equals(
        entry.FullName.Replace('\\', '/'),
        normalizedPath,
        StringComparison.OrdinalIgnoreCase));
}

static void ValidateLegacyResourceRuntimeDependencies()
{
    var releaseDirectory = Path.Combine(
        FindProjectRoot(),
        "src",
        "DalamudActCompat",
        "bin",
        "Release");
    var nrbfPath = Path.Combine(
        releaseDirectory,
        "System.Formats.Nrbf.dll");
    Assert(
        File.Exists(nrbfPath),
        "Safe NRBF resource decoder was not copied to the plugin output.");

    var packagePath = Path.Combine(
        releaseDirectory,
        "DalamudActCompat",
        "latest.zip");
    Assert(File.Exists(packagePath), $"Dalamud release package was not found at {packagePath}.");
    using var archive = ZipFile.OpenRead(packagePath);
    Assert(
        archive.Entries.Any(entry => string.Equals(
            entry.Name,
            "System.Formats.Nrbf.dll",
            StringComparison.OrdinalIgnoreCase)),
        "Safe NRBF resource decoder is missing from the Dalamud release package.");
    Assert(
        archive.Entries.All(entry => !string.Equals(
            entry.Name,
            "System.Runtime.Serialization.Formatters.dll",
            StringComparison.OrdinalIgnoreCase)),
        "The removed BinaryFormatter compatibility package must not be shipped.");
    Assert(
        archive.Entries.Any(entry => string.Equals(
            entry.Name,
            "SharpCompress.dll",
            StringComparison.OrdinalIgnoreCase)),
        "The runtime ACT.FoxTTS 7z reader is missing from the release package.");
    Assert(
        FindArchiveEntry(archive, "host/dnlib.dll") is not null,
        "The mixed-mode GreyMagic compatibility rewriter is missing from the Host package.");
    Assert(
        FindArchiveEntry(archive, "LICENSES/dnlib-MIT.txt") is not null,
        "The dnlib MIT license is missing from the release package.");

    foreach (var style in new[] { "Minimal", "Classic", "Flat" })
    {
        foreach (var job in JobDisplayFormatter.SupportedJobCodes)
        {
            Assert(
                FindArchiveEntry(archive, $"Assets/JobIcons/{style}/{job}.png") is not null,
                $"Release package is missing the {style} {job} job icon.");
        }
    }

    var hostSpeech = FindArchiveEntry(archive, "host/System.Speech.dll");
    var runtimeSpeech = FindArchiveEntry(
        archive,
        "runtimes/win/lib/net10.0/System.Speech.dll");
    Assert(
        hostSpeech is not null && runtimeSpeech is not null,
        "The Compatibility Host or Windows System.Speech runtime implementation is missing.");
    using (var hostSpeechStream = hostSpeech!.Open())
    using (var runtimeSpeechStream = runtimeSpeech!.Open())
    {
        Assert(
            SHA256.HashData(hostSpeechStream).AsSpan()
                .SequenceEqual(SHA256.HashData(runtimeSpeechStream)),
            "The Compatibility Host packaged the System.Speech reference assembly instead of its Windows runtime implementation.");
    }

    var requiredBundledPluginFiles = new[]
    {
        "BundledActPlugins/bundled-plugins.lock.json",
        "BundledActPlugins/triggernometry/Triggernometry.dll",
        "BundledActPlugins/triggernometry/zh-CN.triglations.xml",
        "BundledActPlugins/triggernometry/LICENSE.txt",
        "BundledActPlugins/act.foxtts/ACT.FoxTTS.dll",
        "BundledActPlugins/act.foxtts/LICENSE.txt",
        "BundledActPlugins/postnamazu/PostNamazu.dll",
        "BundledActPlugins/silverdasher/SilverDasher-0.6.0.4-cafe.zip",
        "BundledActPlugins/matcha/Cafe.Matcha-26.8.12.1622-dact3.zip",
        "BundledActPlugins/matcha/LICENSE.txt",
        "BundledActPlugins/matcha/BUILD.md",
        "BundledActPlugins/matcha/dact-compat.patch",
        "BundledActPlugins/matcha/GenerateRuntimeData.ps1",
    };
    foreach (var required in requiredBundledPluginFiles)
    {
        Assert(
            archive.Entries.Any(entry =>
                string.Equals(
                    entry.FullName.Replace('\\', '/'),
                    required,
                    StringComparison.OrdinalIgnoreCase)),
            $"Bundled third-party plugin file is missing from the release package: {required}.");
    }

    var bundledLockEntry = FindArchiveEntry(
        archive,
        "BundledActPlugins/bundled-plugins.lock.json")
        ?? throw new InvalidDataException("Bundled ACT plugin lock is missing from the release package.");
    using var bundledLockStream = bundledLockEntry.Open();
    using var bundledLock = JsonDocument.Parse(bundledLockStream);
    foreach (var plugin in bundledLock.RootElement.GetProperty("plugins").EnumerateArray())
    {
        var relativeAssembly = plugin.GetProperty("relativeAssembly").GetString()
            ?? throw new InvalidDataException("Bundled ACT plugin assembly path is empty.");
        var expectedHash = plugin.GetProperty("sha256").GetString()
            ?? throw new InvalidDataException("Bundled ACT plugin SHA-256 is empty.");
        string actualHash;
        if (plugin.TryGetProperty("relativePackage", out var relativePackageProperty) &&
            !string.IsNullOrWhiteSpace(relativePackageProperty.GetString()))
        {
            var relativePackage = relativePackageProperty.GetString()!;
            var expectedPackageHash = plugin.GetProperty("packageSha256").GetString()
                ?? throw new InvalidDataException("Bundled ACT plugin package SHA-256 is empty.");
            var packageEntry = FindArchiveEntry(
                archive,
                $"BundledActPlugins/{relativePackage}")
                ?? throw new InvalidDataException(
                    $"Bundled ACT plugin package is missing: {relativePackage}.");
            using var packageMemory = new MemoryStream();
            using (var packageStream = packageEntry.Open())
            {
                packageStream.CopyTo(packageMemory);
            }

            var actualPackageHash = Convert.ToHexString(
                SHA256.HashData(packageMemory.ToArray()));
            Assert(
                string.Equals(
                    actualPackageHash,
                    expectedPackageHash,
                    StringComparison.OrdinalIgnoreCase),
                $"Bundled ACT plugin package does not match its locked SHA-256: {relativePackage}.");
            packageMemory.Position = 0;
            using var packageArchive = new ZipArchive(
                packageMemory,
                ZipArchiveMode.Read,
                leaveOpen: true);
            var assemblyEntry = packageArchive.Entries.SingleOrDefault(entry =>
                string.Equals(
                    entry.FullName.Replace('\\', '/'),
                    relativeAssembly.Replace('\\', '/'),
                    StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException(
                    $"Bundled ACT plugin package entry assembly is missing: {relativeAssembly}.");
            using var assemblyStream = assemblyEntry.Open();
            actualHash = Convert.ToHexString(SHA256.HashData(assemblyStream));
        }
        else
        {
            var assemblyEntry = FindArchiveEntry(
                archive,
                $"BundledActPlugins/{relativeAssembly}")
                ?? throw new InvalidDataException(
                    $"Bundled ACT plugin assembly is missing: {relativeAssembly}.");
            using var assemblyStream = assemblyEntry.Open();
            actualHash = Convert.ToHexString(SHA256.HashData(assemblyStream));
        }

        Assert(
            string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase),
            $"Bundled ACT plugin does not match its locked SHA-256: {relativeAssembly}.");
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
            new ActCombatantSnapshot(
                "local", "You", "SAM", true, 120_000, 2_000, 0,
                13_000, 12_000, 12_000,
                DamageHits: 40, CriticalHits: 12, CriticalDirectHits: 4,
                Rdps: 11_500, DirectHits: 16,
                HighestDamageAction: "Midare Setsugekka",
                HighestDamage: 99_999,
                PartyGroup: 3),
            new ActCombatantSnapshot("healer", "Healer", "WHM", false, 20_000, 90_000, 1),
            new ActCombatantSnapshot(
                "early",
                "Early Pull",
                "DRG",
                false,
                1,
                0,
                0,
                double.PositiveInfinity,
                double.NegativeInfinity,
                double.NaN,
                Rdps: double.NaN),
        ])
    {
        CombatDuration = TimeSpan.FromSeconds(9),
        IsTransitioning = true,
        PartyCapacity = 24,
    };

    var encounter = ActEncounterMapper.Map(snapshot);
    Assert(encounter.Id == id, "ACT encounter id was not preserved.");
    Assert(encounter.StartTime == start, "ACT encounter start time was not preserved.");
    Assert(encounter.IsActive, "Active ACT encounter was mapped as finished.");
    Assert(
        encounter.IsTransitioning &&
        encounter.CombatDuration == TimeSpan.FromSeconds(9) &&
        encounter.PartyCapacity == 24,
        "ACT transition state, effective combat duration, or party capacity was not mapped.");
    Assert(encounter.TotalDamage == 140_001, "ACT combatant damage totals were not mapped.");
    Assert(encounter.TotalHealing == 92_000, "ACT combatant healing totals were not mapped.");
    Assert(encounter.TotalDeaths == 1, "ACT combatant deaths were not mapped.");
    Assert(encounter.Combatants.Single(static combatant => combatant.IsLocalPlayer).Name == "You",
        "ACT local player marker was not mapped.");
    Assert(encounter.JobSummaries.Count == 3, "ACT job summaries were not generated.");
    var local = encounter.Combatants.Single(static combatant => combatant.IsLocalPlayer);
    Assert(
        local.Dps == 13_000 && local.EncDps == 12_000 &&
        local.ExtDps == 12_000 && local.Rdps == 11_500,
        "ACT DPS metric fields were not mapped.");
    Assert(
        local.DamageHits == 40 && local.CriticalHits == 12 &&
        local.DirectHits == 16 && local.CriticalDirectHits == 4,
        "ACT critical, direct, and critical-direct hit counts were not mapped.");
    Assert(
        local.HighestDamageAction == "Midare Setsugekka" &&
        local.HighestDamage == 99_999 &&
        local.PartyGroup == 3,
        "ACT highest-hit or 24-player alliance metadata was not mapped.");
    var legacyHistoryJson = System.Text.Json.Nodes.JsonNode
        .Parse(JsonSerializer.Serialize(encounter))!
        .AsObject();
    legacyHistoryJson.Remove(nameof(Encounter.SegmentRecords));
    var legacyHistoryEncounter = JsonSerializer.Deserialize<Encounter>(
        legacyHistoryJson.ToJsonString());
    Assert(
        legacyHistoryEncounter?.SegmentRecords.Count == 0,
        "History files saved before pull folders were introduced are no longer readable.");
    var early = encounter.Combatants.Single(static combatant => combatant.Name == "Early Pull");
    Assert(early.Dps == 0 && early.EncDps == 0 && early.ExtDps == 0 && early.Rdps == 0,
        "Non-finite ACT rates were not normalized before persistence.");
    Assert(
        !string.IsNullOrWhiteSpace(JsonSerializer.Serialize(encounter)),
        "A mapped ACT encounter with early-pull rates could not be serialized.");

    var legacyCombatant = JsonSerializer.Deserialize<Combatant>(
        """
        {
          "Id": "legacy",
          "Name": "Legacy Player",
          "Job": "PLD",
          "IsLocalPlayer": true,
          "TotalDamage": 1,
          "TotalHealing": 0,
          "Deaths": 0,
          "Dps": 1,
          "EncDps": 1,
          "ExtDps": 1
        }
        """);
    Assert(
        legacyCombatant is
        {
            DamageHits: 0,
            CriticalHits: 0,
            DirectHits: 0,
            CriticalDirectHits: 0,
            FflogsPercentile: null,
            FflogsEncounterName: null,
            Rdps: 0,
        },
        "Legacy combatant JSON without hit-count, FFLogs, or rDPS fields is no longer compatible.");
}

static void ValidateChineseCombatChatParsing()
{
    Assert(
        ChineseCombatChatParser.TryExtractActor(
            "附近玩家发动了技能。",
            out var announcedActor) &&
        announcedActor == "附近玩家",
        "Chinese split combat chat did not update its announced actor before the damage line.");
    Assert(
        ChineseCombatChatParser.TryExtractActionAnnouncement(
            "埃斯蒂尼安丿咏唱了“注药III”。",
            out var castingActor,
            out var castingAction) &&
        castingActor == "埃斯蒂尼安丿" &&
        castingAction == "注药III",
        "Chinese cast announcements were not accepted as split combat context.");
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
            out damage,
            out var isCritical,
            out var isDirectHit),
        "Chinese ability damage chat was not parsed.");
    Assert(
        inheritedActor == actor && target == "木人" && damage == 39870 &&
        isCritical && !isDirectHit,
        "Chinese ability damage fields were mapped incorrectly.");
    Assert(
        ChineseCombatChatParser.TryParse(
            "  \uE06F 直击加暴击！ 木人受到了23175点伤害。",
            actor,
            out _,
            out target,
            out damage,
            out isCritical,
            out isDirectHit) &&
        target == "木人" && damage == 23175 && isCritical && isDirectHit,
        "Chinese critical-direct damage did not retain both hit flags.");

    var context = new ChineseCombatChatContext();
    var observedAt = DateTimeOffset.UtcNow;
    Assert(
        !context.TryParse(
            "本地玩家发动了技能。",
            observedAt,
            out _,
            out _,
            out _),
        "A split combat actor announcement was mistaken for a complete damage line.");
    Assert(
        !context.TryParse(
            "本地玩家向附近玩家挥了挥手。",
            observedAt.AddMilliseconds(50),
            out _,
            out _,
            out _),
        "A player emote was mistaken for combat damage.");
    Assert(
        !context.TryParse(
            "  \uE06F 附近玩家受到了900点伤害。",
            observedAt.AddMilliseconds(100),
            out _,
            out _,
            out _),
        "Damage after an unrelated emote inherited the local player's stale attacker context.");
    Assert(
        !context.TryParse(
            "队友发动了技能。",
            observedAt.AddSeconds(1),
            out _,
            out _,
            out _) &&
        context.TryParse(
            "  \uE06F 木人受到了1200点伤害。",
            observedAt.AddSeconds(1.1),
            out var contextualActor,
            out _,
            out _) &&
        contextualActor == "队友",
        "Adjacent split combat lines no longer retain their legitimate attacker context.");

    var interleavedContext = new ChineseCombatChatContext();
    Assert(
        !interleavedContext.TryParse(
            "队员甲发动了“技能甲”。",
            observedAt,
            out _, out _, out _) &&
        !interleavedContext.TryParse(
            "队员乙发动了“技能乙”。",
            observedAt.AddMilliseconds(10),
            out _, out _, out _) &&
        interleavedContext.TryParse(
            "  \uE06F 木人受到了1000点伤害。",
            observedAt.AddMilliseconds(20),
            out var firstInterleavedActor,
            out _,
            out _,
            out var firstInterleavedAction,
            out _,
            out _) &&
        interleavedContext.TryParse(
            "  \uE06F 木人受到了2000点伤害。",
            observedAt.AddMilliseconds(30),
            out var secondInterleavedActor,
            out _,
            out _,
            out var secondInterleavedAction,
            out _,
            out _) &&
        firstInterleavedActor == "队员甲" &&
        firstInterleavedAction == "技能甲" &&
        secondInterleavedActor == "队员乙" &&
        secondInterleavedAction == "技能乙",
        "Interleaved Chinese action announcements crossed actors or highest-damage action names.");

    Assert(
        SelfHostedActRuntime.IsDamageDealingLimitBreakAction(9, canTargetHostile: true) &&
        SelfHostedActRuntime.IsDamageDealingLimitBreakAction(15, canTargetHostile: true) &&
        !SelfHostedActRuntime.IsDamageDealingLimitBreakAction(9, canTargetHostile: false) &&
        !SelfHostedActRuntime.IsDamageDealingLimitBreakAction(15, canTargetHostile: false) &&
        !SelfHostedActRuntime.IsDamageDealingLimitBreakAction(2, canTargetHostile: true),
        "Defensive Limit Break actions can still claim an unrelated damage line.");

    var limitBreakContext = new ChineseCombatChatContext(
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "终结时刻" });
    Assert(
        !limitBreakContext.TryParse(
            "埃斯蒂尼安丿发动了“终结时刻”。",
            observedAt,
            out _,
            out _,
            out _) &&
        limitBreakContext.TryParse(
            "  \uE06F 木人受到了936686点伤害。",
            observedAt.AddMilliseconds(10),
            out var limitBreakActor,
            out _,
            out var limitBreakDamage) &&
        limitBreakActor == ChineseCombatChatContext.LimitBreakActorName &&
        limitBreakDamage == 936686,
        "Chinese Limit Break damage was attributed to the player instead of the synthetic LB combatant.");

    Assert(
        NetworkDamageFallbackParser.TryParse(
            "21|2026-08-24T22:11:10.0000000+08:00|10028D6F|埃斯蒂尼安丿|5EF8|注药III|400018C8|木人|752003|9F630000",
            out var chineseNetworkDamage) &&
        chineseNetworkDamage.SourceId == 0x10028D6F &&
        chineseNetworkDamage.SourceName == "埃斯蒂尼安丿" &&
        chineseNetworkDamage.TargetName == "木人" &&
        chineseNetworkDamage.ActionName == "注药III" &&
        chineseNetworkDamage.Damage == 40803 &&
        chineseNetworkDamage.IsCritical &&
        !chineseNetworkDamage.IsDirectHit,
        "The real Sage Dosis III network line did not produce region-neutral fallback damage.");
    Assert(
        NetworkDamageFallbackParser.TryParse(
            "21|2026-08-24T14:11:10.0000000Z|10028D6F|Example Player|5EF8|Dosis III|400018C8|Striking Dummy|752003|9F630000",
            out var globalNetworkDamage) &&
        globalNetworkDamage.Damage == chineseNetworkDamage.Damage &&
        globalNetworkDamage.ActionName == "Dosis III",
        "The structured damage fallback still depends on Chinese combat text.");
    Assert(
        NetworkDamageFallbackParser.TryParse(
            "24|2026-08-24T14:11:13.0000000Z|400018C8|Striking Dummy|DoT|0|00000ABC|0|0|0|0|0|0|0|0|0|0|10028D6F|Example Player|checksum",
            out var periodicNetworkDamage) &&
        periodicNetworkDamage.SourceId == 0x10028D6F &&
        periodicNetworkDamage.TargetName == "Striking Dummy" &&
        periodicNetworkDamage.Damage == 0xABC &&
        periodicNetworkDamage.ActionName == "DoT",
        "Region-neutral fallback damage dropped periodic damage after the opening cast.");

    var snapshotTime = DateTimeOffset.UtcNow;
    var healingActSnapshot = new ActEncounterSnapshot(
        Guid.NewGuid(),
        snapshotTime.AddSeconds(4),
        snapshotTime.AddSeconds(5),
        "Middle La Noscea",
        "木人",
        [new ActCombatantSnapshot("player", "Player", "SGE", true, 0, 16703, 0)]);
    var chatFallbackSnapshot = new ActEncounterSnapshot(
        healingActSnapshot.Id,
        snapshotTime,
        snapshotTime.AddSeconds(12),
        "Middle La Noscea",
        "木人",
        [new ActCombatantSnapshot(
            "player",
            "Player",
            "SGE",
            true,
            40803,
            0,
            0,
            Dps: 40803,
            DamageHits: 1,
            CriticalHits: 1,
            HighestDamageAction: "Dosis III",
            HighestDamage: 40803)]);
    var mergedFallbackSnapshot = SelfHostedActRuntime.MergeFallbackDamage(
        healingActSnapshot,
        chatFallbackSnapshot);
    Assert(
        mergedFallbackSnapshot?.Combatants.Single() is
        {
            TotalDamage: 40803,
            TotalHealing: 16703,
            DamageHits: 1,
            CriticalHits: 1,
            HighestDamageAction: "Dosis III",
        } &&
        mergedFallbackSnapshot.StartTime == snapshotTime &&
        mergedFallbackSnapshot.EndTime == snapshotTime.AddSeconds(12),
        "Fallback damage replaced ACT healing or failed to restore fragmented fight boundaries.");
    var authoritativeActSnapshot = healingActSnapshot with
    {
        Combatants =
        [
            healingActSnapshot.Combatants[0] with
            {
                TotalDamage = 12345,
                Dps = 12345,
            },
        ],
    };
    Assert(
        SelfHostedActRuntime.MergeFallbackDamage(
            authoritativeActSnapshot,
            chatFallbackSnapshot)?.Combatants.Single().TotalDamage == 12345 &&
        ReferenceEquals(
            SelfHostedActRuntime.MergeFallbackDamage(null, chatFallbackSnapshot),
            chatFallbackSnapshot),
        "Fallback damage double-counted an authoritative ACT total or failed to cover an empty ACT snapshot.");
}

static void ValidateDiagnosticReport(string testRoot)
{
    var launcherRoot = Path.Combine(testRoot, "diagnostic-launcher");
    var paths = new PluginPaths(Path.Combine(
        launcherRoot,
        "pluginConfigs",
        "DalamudActCompat"));
    paths.EnsureCreated();
    Assert(
        paths.CombatLogDirectory == Path.Combine(paths.LogDirectory, "ffxiv") &&
        Directory.Exists(paths.CombatLogDirectory),
        "The FFLogs upload shortcut does not target the raw FFXIV Network log directory.");
    File.WriteAllLines(
        Path.Combine(launcherRoot, "dalamud.log"),
        [
            "[OtherPlugin] private unrelated line",
            "[DalamudActCompat] parser failed token=secret-token path=C:\\Users\\Alice\\game",
            "   at DalamudActCompat.Parser.Start()",
            "[OtherPlugin] another unrelated line",
        ]);
    File.WriteAllLines(
        Path.Combine(launcherRoot, "dalamud.old.log"),
        ["[DalamudActCompat] old failure https://example.invalid/?api_key=old-secret"]);
    File.WriteAllLines(
        Path.Combine(paths.LogDirectory, "external-host.log"),
        ["host extension failed password=host-secret Authorization: Bearer bearer-secret"]);

    var manifest = new ActPluginManifest
    {
        Id = "triggernometry",
        Name = "Triggernometry",
        Version = "2.1.2.2",
        HostApiVersion = 1,
    };
    var host = new HostSupervisorSnapshot(
        HostSupervisorState.Running,
        ProcessRunning: true,
        HostConnectionStatus.Connected,
        ControlQueueLength: 1,
        DataQueueLength: 2,
        DroppedMessages: 0,
        SessionId: "0123456789abcdef",
        LastWrittenSequence: 8,
        HostAcknowledgedSequence: 7,
        HostWorkingSetBytes: 64 * 1024 * 1024,
        HostThreadCount: 4,
        HealthState: "Healthy",
        HealthDetail: "ready at C:\\Users\\Alice\\runtime",
        PluginHealth: [],
        Diagnostics: [],
        PluginStages: []);
    var report = DiagnosticReportBuilder.Build(
        paths,
        new DiagnosticReportSnapshot(
            "0.3.7.3",
            "15.0.0",
            new ParserStatus(
                ParserState.Running,
                "Running",
                DateTimeOffset.Parse("2026-08-08T12:00:00+08:00")),
            host,
            ParsingEnabled: true,
            DebugMode: false,
            FflogsEnabled: true,
            MeterSortMode.Dps,
            MeterCompactMode: false,
            [new InstalledActPlugin(manifest, "C:\\Users\\Alice\\plugin", Enabled: true)]));

    Assert(
        report.Contains("Plugin: 0.3.7.3", StringComparison.Ordinal) &&
        report.Contains("parser failed", StringComparison.Ordinal) &&
        report.Contains("at DalamudActCompat.Parser.Start()", StringComparison.Ordinal) &&
        report.Contains("host extension failed", StringComparison.Ordinal) &&
        report.Contains("triggernometry; version=2.1.2.2; enabled=True", StringComparison.Ordinal),
        "The one-click diagnostic report omitted version, plugin, Host, or exception context.");
    Assert(
        !report.Contains("private unrelated", StringComparison.Ordinal) &&
        !report.Contains("Alice", StringComparison.Ordinal) &&
        !report.Contains("secret-token", StringComparison.Ordinal) &&
        !report.Contains("old-secret", StringComparison.Ordinal) &&
        !report.Contains("host-secret", StringComparison.Ordinal) &&
        !report.Contains("bearer-secret", StringComparison.Ordinal) &&
        report.Contains("<redacted>", StringComparison.Ordinal) &&
        report.Contains("<user>", StringComparison.Ordinal),
        "The diagnostic report included unrelated Dalamud lines or failed to redact sensitive values.");
    Assert(
        report.Length <= DiagnosticReportBuilder.MaximumReportCharacters &&
        report.Contains("combat/network logs and configuration files are excluded", StringComparison.Ordinal),
        "The diagnostic report is unbounded or does not explain its privacy exclusions.");

    var selected = DiagnosticReportBuilder.SelectRelevantDalamudLines(
        [
            "[OtherPlugin] before",
            "[DalamudActCompat] failure",
            "   at DalamudActCompat.Test()",
            "[OtherPlugin] after",
        ]);
    Assert(
        selected.Count == 2 &&
        selected[0].Contains("failure", StringComparison.Ordinal) &&
        selected[1].Contains("at DalamudActCompat.Test()", StringComparison.Ordinal),
        "The diagnostic log filter did not retain only plugin lines and their exception continuation.");
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
            EntryType = typeof(GenericActPluginFixture).FullName!,
            HostApiVersion = 1,
        });
    }

    var assemblyEntry = archive.CreateEntry("Example.Plugin.dll");
    await using var assemblyStream = assemblyEntry.Open();
    await assemblyStream.WriteAsync(await File.ReadAllBytesAsync(
        typeof(GenericActPluginFixture).Assembly.Location));
}

static async Task WriteArchiveEntryAsync(ZipArchive archive, string path, byte[] content)
{
    var entry = archive.CreateEntry(path);
    await using var stream = entry.Open();
    await stream.WriteAsync(content);
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void ValidateCombatLogDirectoryConfiguration(string testRoot)
{
    var pluginType = typeof(ControlCenterWindow).Assembly.GetType(
        "DalamudActCompat.Plugin.Plugin",
        throwOnError: true)!;
    var prepareDirectory = pluginType.GetMethod(
        "TryPrepareCombatLogDirectory",
        BindingFlags.NonPublic | BindingFlags.Static)!;
    var requestedDirectory = Path.Combine(testRoot, "custom-fflogs-upload-logs");
    object?[] validArguments = [requestedDirectory, null, null];
    var valid = (bool)prepareDirectory.Invoke(null, validArguments)!;
    var normalizedDirectory = (string)validArguments[1]!;
    Assert(
        valid &&
        normalizedDirectory == Path.GetFullPath(requestedDirectory) &&
        Directory.Exists(normalizedDirectory) &&
        !Directory.EnumerateFiles(normalizedDirectory, ".dact-write-probe-*.tmp").Any(),
        "A writable custom FFLogs upload directory was rejected or its permission probe was left behind.");

    object?[] invalidArguments = [" ", null, null];
    Assert(
        !(bool)prepareDirectory.Invoke(null, invalidArguments)! &&
        !string.IsNullOrWhiteSpace((string)invalidArguments[2]!),
        "An empty FFLogs upload directory was accepted without a validation error.");

    var fileInsteadOfDirectory = Path.Combine(testRoot, "fflogs-directory-collision");
    File.WriteAllText(fileInsteadOfDirectory, "occupied");
    object?[] unwritableArguments = [fileInsteadOfDirectory, null, null];
    Assert(
        !(bool)prepareDirectory.Invoke(null, unwritableArguments)! &&
        !string.IsNullOrWhiteSpace((string)unwritableArguments[2]!),
        "A file collision was accepted as a writable FFLogs upload directory.");

    var restoredConfiguration = Newtonsoft.Json.JsonConvert.DeserializeObject<PluginConfiguration>(
        Newtonsoft.Json.JsonConvert.SerializeObject(new PluginConfiguration
        {
            LogDirectory = normalizedDirectory,
        }));
    var constructorParameters = typeof(ControlCenterWindow).GetConstructors().Single().GetParameters();
    Assert(
        restoredConfiguration?.LogDirectory == normalizedDirectory &&
        constructorParameters.Any(parameter =>
            parameter.Name == "getCombatLogDirectory" &&
            parameter.ParameterType == typeof(Func<string>)) &&
        constructorParameters.Any(parameter =>
            parameter.Name == "selectCombatLogDirectory" &&
            parameter.ParameterType == typeof(Action<Action<bool, string>>)) &&
        constructorParameters.Any(parameter =>
            parameter.Name == "resetCombatLogDirectory" &&
            parameter.ParameterType == typeof(Action<Action<bool, string>>)) &&
        typeof(IinactAdapter).GetConstructors().Single().GetParameters().Any(parameter =>
            parameter.Name == "getLogDirectory" &&
            parameter.ParameterType == typeof(Func<string>)),
        "The custom upload directory is not persisted, exposed on Settings, or resolved again when the parser restarts.");
}

static void ValidateNetworkLogSessionRotation(string testRoot)
{
    var logDirectory = Path.Combine(testRoot, "network-log-session-rotation");
    Directory.CreateDirectory(logDirectory);
    var logfileVersion = new Version(3, 0, 2, 8);
    var sessionStartedAt = new DateTime(2026, 9, 2, 14, 30, 45, 123, DateTimeKind.Local);
    var activeLogPath = NetworkLogSessionRotator.BuildActiveLogPath(
        logDirectory,
        logfileVersion,
        sessionStartedAt);
    Assert(
        Path.GetFileName(activeLogPath) == "Network_30208_20260902.log",
        "Network log rotation did not mirror the upstream logfile naming rule.");

    var unrelatedLogPath = Path.Combine(logDirectory, "Network_21035_20260902.log");
    File.WriteAllText(unrelatedLogPath, "different parser version");
    File.WriteAllText(activeLogPath, "first parser session");
    var archivedLogPath = NetworkLogSessionRotator.RotateExisting(
        logDirectory,
        logfileVersion,
        sessionStartedAt,
        TimeSpan.Zero);
    Assert(
        archivedLogPath is not null &&
        Path.GetFileName(archivedLogPath) ==
            "Network_30208_20260902_session-20260902-143045123.log" &&
        !File.Exists(activeLogPath) &&
        File.ReadAllText(unrelatedLogPath) == "different parser version" &&
        File.ReadAllText(archivedLogPath) == "first parser session",
        "The current Network log was not isolated without disturbing other parser versions.");

    File.WriteAllText(activeLogPath, "second parser session");
    var collisionArchivePath = NetworkLogSessionRotator.RotateExisting(
        logDirectory,
        logfileVersion,
        sessionStartedAt,
        TimeSpan.Zero);
    Assert(
        collisionArchivePath is not null &&
        Path.GetFileName(collisionArchivePath) ==
            "Network_30208_20260902_session-20260902-143045123-2.log" &&
        File.ReadAllText(collisionArchivePath) == "second parser session",
        "Network log rotation overwrote an existing session archive.");

    File.WriteAllText(activeLogPath, "locked parser session");
    using (var lockedLog = new FileStream(
               activeLogPath,
               FileMode.Open,
               FileAccess.ReadWrite,
               FileShare.ReadWrite))
    {
        try
        {
            NetworkLogSessionRotator.RotateExisting(
                logDirectory,
                logfileVersion,
                sessionStartedAt.AddSeconds(1),
                TimeSpan.Zero);
            throw new InvalidOperationException(
                "Network log rotation appended after the previous session remained locked.");
        }
        catch (IOException)
        {
            // A locked previous session must block startup so incompatible parser
            // identities can never be appended to the same physical file.
        }
    }

    Assert(
        File.Exists(activeLogPath) &&
        NetworkLogSessionRotator.RotateExisting(
            logDirectory,
            logfileVersion,
            sessionStartedAt.AddSeconds(2),
            TimeSpan.Zero) is not null,
        "Network log rotation did not recover after the previous writer released the file.");
}

static void ValidateCloudKeyEnvelopeAndCredentialProtection(string testRoot)
{
    var encryption = new PortableConfigurationEncryptionService();
    var envelopeService = new CloudKeyEnvelopeService();
    var recoveryKey = encryption.GenerateRecoveryKey();
    var envelope = envelopeService.Create(recoveryKey, "a-correct-password");
    var recoveryVerifier = envelopeService.CreateRecoveryVerifier(recoveryKey);
    Assert(
        envelope.Format == CloudKeyEnvelopeService.EnvelopeFormat &&
        envelope.Iterations == CloudKeyEnvelopeService.PasswordIterations &&
        envelopeService.Open(envelope, "a-correct-password") == recoveryKey &&
        Regex.IsMatch(recoveryVerifier, "^[A-Za-z0-9_-]{43}$") &&
        recoveryVerifier == envelopeService.CreateRecoveryVerifier(recoveryKey) &&
        recoveryVerifier != envelope.KeyId,
        "Cloud password envelope or domain-separated recovery verifier is invalid.");
    try
    {
        _ = envelopeService.Open(envelope, "a-wrong-password");
        throw new InvalidOperationException("Cloud key envelope accepted a wrong password.");
    }
    catch (CryptographicException)
    {
        // A wrong password must fail authentication before yielding any account key.
    }

    var credentialPath = Path.Combine(testRoot, "cloud-security", "account.dat");
    var store = new CloudCredentialStore(credentialPath);
    var credentials = new CloudStoredCredentials(
        "cloud_user",
        "secret-session-token",
        DateTimeOffset.UtcNow.AddDays(10),
        recoveryKey);
    store.Save(credentials);
    Assert(store.Load() == credentials, "DPAPI cloud credentials did not round-trip.");
    var protectedBytes = File.ReadAllBytes(credentialPath);
    Assert(
        protectedBytes.AsSpan().IndexOf("secret-session-token"u8) < 0 &&
        protectedBytes.AsSpan().IndexOf("dact1_"u8) < 0,
        "Cloud credential file exposed a session token or recovery key in plaintext.");

    var recoveryOnly = credentials with
    {
        Token = string.Empty,
        ExpiresAt = DateTimeOffset.MinValue,
    };
    store.Save(recoveryOnly);
    Assert(
        store.Load() == recoveryOnly,
        "Cloud credential store rejected the recovery-only state needed after session revocation.");

    var banPath = Path.Combine(testRoot, "cloud-security", "ban.dat");
    var banStore = new CloudBanStore(banPath);
    var notice = new CloudBanNotice(
        "account_banned",
        "account",
        DateTimeOffset.UtcNow,
        null,
        "private-test-reason");
    banStore.Save(notice);
    Assert(banStore.Load() == notice, "DPAPI cloud ban marker did not round-trip.");
    Assert(
        File.ReadAllBytes(banPath).AsSpan().IndexOf("private-test-reason"u8) < 0,
        "Cloud ban marker exposed its server-provided reason in plaintext.");
    File.Delete(banPath);
    banStore.EnsurePresent(notice);
    Assert(
        File.Exists(banPath) && banStore.Load() == notice,
        "Deleted cloud ban marker was not restored from the in-memory ban state.");

    var paths = new PluginPaths(Path.Combine(testRoot, "cloud-machine"));
    paths.EnsureCreated();
    var identity = new CloudMachineIdentity(paths.CloudDeviceFile);
    var firstDeviceId = identity.GetDeviceId();
    var secondDeviceId = identity.GetDeviceId();
    Assert(
        firstDeviceId == secondDeviceId &&
        Regex.IsMatch(firstDeviceId, "^dact-device-v1_[A-Za-z0-9_-]{43}$"),
        "Cloud machine identifier was unstable or exposed an invalid shape.");
}

static async Task ValidateCloudApiContractAsync(string testRoot)
{
    var backupBytes = "DACTE2E1\u0001encrypted-test-payload"u8.ToArray();
    var backupHash = Convert.ToHexString(SHA256.HashData(backupBytes)).ToLowerInvariant();
    var createdAt = DateTimeOffset.UtcNow;
    var envelope = new CloudKeyEnvelope(
        CloudKeyEnvelopeService.EnvelopeFormat,
        Convert.ToBase64String(new byte[32]).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
        CloudKeyEnvelopeService.PasswordIterations,
        Convert.ToBase64String(new byte[16]).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
        Convert.ToBase64String(new byte[12]).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
        Convert.ToBase64String(new byte[16]).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
        Convert.ToBase64String(new byte[32]).TrimEnd('=').Replace('+', '-').Replace('/', '_'));
    var handler = new ScriptedHttpMessageHandler(request =>
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path.EndsWith("/auth/register", StringComparison.Ordinal))
        {
            var json = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            Assert(
                json.Contains("\"activationKey\":\"DACT-TEST\"", StringComparison.Ordinal) &&
                json.Contains("\"keyEnvelope\"", StringComparison.Ordinal) &&
                json.Contains("\"recoveryVerifier\"", StringComparison.Ordinal),
                "Cloud registration request omitted its activation key, password envelope, or recovery verifier.");
            return JsonResponse(HttpStatusCode.Created, new
            {
                token = "session-token",
                tokenType = "Bearer",
                expiresAt = createdAt.AddDays(30),
                user = new { id = "user-id", username = "cloud_user" },
                keyEnvelope = envelope,
            });
        }
        if (path.EndsWith("/auth/recovery-verifier", StringComparison.Ordinal))
        {
            Assert(
                request.Method == HttpMethod.Put &&
                request.Headers.Authorization?.Parameter == "session-token",
                "Recovery verifier enrollment omitted its session authorization.");
            return JsonResponse(HttpStatusCode.OK, new { recoveryEnabled = true });
        }
        if (path.EndsWith("/auth/access-status", StringComparison.Ordinal))
        {
            return JsonResponse(HttpStatusCode.OK, new
            {
                banned = true,
                sessionActive = false,
                wasBanRevoked = false,
                banType = "account",
                bannedAt = createdAt,
                banExpiresAt = (DateTimeOffset?)null,
                banReason = "contract-test",
            });
        }
        if (path.EndsWith("/auth/key-envelope", StringComparison.Ordinal) &&
            request.Method == HttpMethod.Put)
        {
            return JsonResponse(HttpStatusCode.OK, new { keyEnvelope = envelope });
        }
        if (path.EndsWith("/auth/events", StringComparison.Ordinal))
        {
            var json = JsonSerializer.Serialize(new
            {
                code = "account_banned",
                banType = "account",
                bannedAt = createdAt,
                banExpiresAt = (DateTimeOffset?)null,
                banReason = "contract-test",
            });
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"event: ban\ndata: {json}\n\n"),
            };
            response.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
            return response;
        }
        if (path.EndsWith("/invitations", StringComparison.Ordinal) &&
            request.Method == HttpMethod.Get)
        {
            return JsonResponse(HttpStatusCode.OK, new
            {
                quota = 3,
                used = 1,
                remaining = 2,
                invitations = new[]
                {
                    new
                    {
                        id = "invite-id",
                        codeHint = "DACT-…TEST",
                        name = "好友邀请",
                        inviteeContact = "好友游戏ID",
                        status = "available",
                        createdAt,
                        expiresAt = (DateTimeOffset?)null,
                        usedAt = (DateTimeOffset?)null,
                    },
                },
            });
        }
        if (path.EndsWith("/invitations", StringComparison.Ordinal) &&
            request.Method == HttpMethod.Post)
        {
            var requestBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            using var requestJson = JsonDocument.Parse(requestBody);
            Assert(
                requestJson.RootElement.GetProperty("inviteeContact").GetString() == "好友QQ12345",
                "Cloud invitation creation omitted the invitee game or QQ identifier.");
            return JsonResponse(HttpStatusCode.Created, new
            {
                id = "invite-id-2",
                activationKey = "DACT-FRIEND-KEY",
                codeHint = "DACT-…-KEY",
                name = "好友邀请",
                inviteeContact = "好友QQ12345",
                status = "unused",
                createdAt,
            });
        }
        if (path.EndsWith("/backups", StringComparison.Ordinal) && request.Method == HttpMethod.Get)
        {
            Assert(
                request.Headers.Authorization?.Parameter == "session-token",
                "Cloud backup list omitted its bearer token.");
            return JsonResponse(HttpStatusCode.OK, new
            {
                backups = new[]
                {
                    new
                    {
                        id = "backup-id",
                        createdAt,
                        sizeBytes = backupBytes.Length,
                        sha256 = backupHash,
                        contentId = new string('C', 43),
                    },
                },
            });
        }
        if (path.EndsWith("/backups", StringComparison.Ordinal) && request.Method == HttpMethod.Post)
        {
            var uploaded = request.Content!.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            Assert(
                uploaded.SequenceEqual(backupBytes) &&
                request.Headers.GetValues("X-DACT-Content-Id").Single() == new string('C', 43),
                "Cloud upload changed encrypted bytes or omitted its content identity.");
            return JsonResponse(HttpStatusCode.Created, new
            {
                id = "backup-id",
                createdAt,
                sizeBytes = backupBytes.Length,
                sha256 = backupHash,
                contentId = new string('C', 43),
            });
        }
        if (path.EndsWith("/backups/backup-id", StringComparison.Ordinal))
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(backupBytes),
            };
            response.Content.Headers.ContentLength = backupBytes.Length;
            response.Headers.TryAddWithoutValidation("X-Content-SHA256", backupHash);
            return response;
        }
        return JsonResponse(HttpStatusCode.NotFound, new { error = "not_found", message = "missing" });
    });
    using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://cloud.test/") };
    using var api = new CloudApiClient(httpClient);
    var authentication = await api.RegisterAsync(
        "cloud_user",
        "a-correct-password",
        "DACT-TEST",
        $"dact-device-v1_{new string('A', 43)}",
        envelope,
        new string('V', 43),
        CancellationToken.None);
    Assert(
        authentication.User.Username == "cloud_user" && authentication.KeyEnvelope == envelope,
        "Cloud authentication response lost account or envelope fields.");

    var access = await api.GetAccessStatusAsync("session-token", CancellationToken.None);
    Assert(
        access.Banned && access.BanType == "account" && access.BanReason == "contract-test",
        "Cloud access status lost the server-provided ban details.");
    await api.UpdateKeyEnvelopeAsync("session-token", envelope, CancellationToken.None);
    await api.UpdateRecoveryVerifierAsync(
        "session-token",
        new string('V', 43),
        CancellationToken.None);
    CloudBanNotice? liveBan = null;
    await api.ListenForBanEventsAsync(
        "session-token",
        notice =>
        {
            liveBan = notice;
            return Task.CompletedTask;
        },
        CancellationToken.None);
    Assert(
        liveBan is { Code: "account_banned", BanReason: "contract-test" },
        "Cloud event stream did not deliver the live ban notice.");

    var invitations = await api.ListInvitationsAsync("session-token", CancellationToken.None);
    Assert(
        invitations.Quota == 3 && invitations.Remaining == 2 &&
        invitations.Invitations.Single().Status == "available",
        "Cloud invitation quota or history was not parsed.");
    var invitation = await api.CreateInvitationAsync(
        "session-token",
        "好友QQ12345",
        CancellationToken.None);
    Assert(
        invitation.ActivationKey == "DACT-FRIEND-KEY" &&
        invitation.InviteeContact == "好友QQ12345",
        "Cloud invitation creation did not preserve its invitee contact or expose its one-time activation key.");

    var versions = await api.ListBackupsAsync("session-token", CancellationToken.None);
    Assert(versions.Count == 1 && versions[0].Id == "backup-id", "Cloud versions were not parsed.");
    var uploadPath = Path.Combine(testRoot, "cloud-api-upload.dactcloud");
    File.WriteAllBytes(uploadPath, backupBytes);
    var uploadedVersion = await api.UploadBackupAsync(
        "session-token",
        uploadPath,
        new string('C', 43),
        CancellationToken.None);
    Assert(uploadedVersion.Sha256 == backupHash, "Cloud upload response was not parsed.");
    var downloadPath = Path.Combine(testRoot, "cloud-api-download.dactcloud");
    await api.DownloadBackupAsync(
        "session-token",
        versions[0],
        downloadPath,
        CancellationToken.None);
    Assert(
        File.ReadAllBytes(downloadPath).SequenceEqual(backupBytes),
        "Cloud download failed authenticated size/hash verification.");
}

static async Task ValidateSavedCloudSessionRequiresServerValidationAsync(string testRoot)
{
    var paths = new PluginPaths(Path.Combine(testRoot, "cloud-auto-login-gate"));
    paths.EnsureCreated();
    var credentialStore = new CloudCredentialStore(paths.CloudCredentialFile);
    credentialStore.Save(new CloudStoredCredentials(
        "saved_user",
        "saved-session-token",
        DateTimeOffset.UtcNow.AddDays(1),
        new PortableConfigurationBackupService().GenerateRecoveryKey()));
    using var logoutRequestStarted = new ManualResetEventSlim();
    using var releaseLogout = new ManualResetEventSlim();
    var handler = new ScriptedHttpMessageHandler(request =>
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path.EndsWith("/auth/logout", StringComparison.Ordinal))
        {
            logoutRequestStarted.Set();
            releaseLogout.Wait(TimeSpan.FromSeconds(5));
            return JsonResponse(HttpStatusCode.NoContent, new { });
        }
        if (path.EndsWith("/auth/me", StringComparison.Ordinal))
        {
            return JsonResponse(HttpStatusCode.OK, new { username = "saved_user" });
        }
        if (path.EndsWith("/backups", StringComparison.Ordinal))
        {
            return JsonResponse(HttpStatusCode.OK, new { backups = Array.Empty<object>() });
        }
        if (path.EndsWith("/invitations", StringComparison.Ordinal))
        {
            return JsonResponse(HttpStatusCode.OK, new
            {
                quota = 3,
                used = 0,
                remaining = 3,
                invitations = Array.Empty<object>(),
            });
        }
        return JsonResponse(HttpStatusCode.NotFound, new
        {
            error = "not_found",
            message = "missing",
        });
    });
    using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://cloud.test/") };
    using var api = new CloudApiClient(httpClient);
    using var service = new CloudClientService(
        paths,
        api,
        credentialStore,
        new CloudBanStore(paths.CloudBanFile),
        new CloudMachineIdentity(paths.CloudDeviceFile),
        new CloudKeyEnvelopeService(),
        new PortableConfigurationBackupService());

    Assert(
        !service.Snapshot.IsSignedIn && service.Snapshot.IsBusy &&
        service.Snapshot.Username == "saved_user",
        "A saved auto-login token unlocked DACT before server validation completed.");
    await service.InitializeAsync(CancellationToken.None);
    Assert(service.Snapshot.IsSignedIn, "A server-validated saved session did not unlock DACT.");

    var logoutTask = Task.Run(() => service.LogoutAsync(CancellationToken.None));
    Assert(
        logoutRequestStarted.Wait(TimeSpan.FromSeconds(2)),
        "Cloud logout did not reach the server request.");
    Assert(
        !service.Snapshot.IsSignedIn && !File.Exists(paths.CloudCredentialFile),
        "Cloud logout waited for the server before revoking local DACT access.");
    releaseLogout.Set();
    await logoutTask;
}

static async Task ValidateFirstLoginReportsServerUnbanAsync(string testRoot)
{
    const string password = "unban-login-password";
    var backupService = new PortableConfigurationBackupService();
    var envelopeService = new CloudKeyEnvelopeService();
    var recoveryKey = backupService.GenerateRecoveryKey();
    var envelope = envelopeService.Create(recoveryKey, password);
    var handler = new ScriptedHttpMessageHandler(request =>
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path.EndsWith("/auth/login", StringComparison.Ordinal))
        {
            return JsonResponse(HttpStatusCode.OK, new
            {
                token = "unban-login-token",
                tokenType = "Bearer",
                expiresAt = DateTimeOffset.UtcNow.AddDays(1),
                user = new { id = "unban-user-id", username = "unban_user" },
                keyEnvelope = envelope,
                wasBanRevoked = true,
            });
        }
        if (path.EndsWith("/backups", StringComparison.Ordinal))
        {
            return JsonResponse(HttpStatusCode.OK, new { backups = Array.Empty<object>() });
        }
        if (path.EndsWith("/invitations", StringComparison.Ordinal))
        {
            return JsonResponse(HttpStatusCode.OK, new
            {
                quota = 3,
                used = 0,
                remaining = 3,
                invitations = Array.Empty<object>(),
            });
        }
        return JsonResponse(HttpStatusCode.NotFound, new
        {
            error = "not_found",
            message = "missing",
        });
    });
    using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://cloud.test/") };
    using var api = new CloudApiClient(httpClient);
    var paths = new PluginPaths(Path.Combine(testRoot, "cloud-first-login-unban"));
    paths.EnsureCreated();
    using var service = new CloudClientService(
        paths,
        api,
        new CloudCredentialStore(paths.CloudCredentialFile),
        new CloudBanStore(paths.CloudBanFile),
        new CloudMachineIdentity(paths.CloudDeviceFile),
        envelopeService,
        backupService);
    var notification = new TaskCompletionSource<bool>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    service.BanLifted += previousBan => notification.TrySetResult(previousBan is null);

    await service.LoginAsync(
        "unban_user",
        password,
        recoveryKey,
        rememberLogin: false,
        cancellationToken: CancellationToken.None);
    Assert(
        await notification.Task.WaitAsync(TimeSpan.FromSeconds(2)) &&
        service.Snapshot.IsSignedIn,
        "The first successful login after an administrator unban did not surface the server notice.");
}

static async Task ValidateCloudRegistrationSurvivesVersionRefreshFailureAsync(string testRoot)
{
    var createdAt = DateTimeOffset.UtcNow;
    var handler = new ScriptedHttpMessageHandler(request =>
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path.EndsWith("/auth/register", StringComparison.Ordinal))
        {
            using var registration = JsonDocument.Parse(
                request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var envelope = registration.RootElement.GetProperty("keyEnvelope").Clone();
            return JsonResponse(HttpStatusCode.Created, new
            {
                token = "registration-token",
                tokenType = "Bearer",
                expiresAt = createdAt.AddDays(30),
                user = new { id = "registration-user-id", username = "registration_user" },
                keyEnvelope = envelope,
            });
        }
        if (path.EndsWith("/backups", StringComparison.Ordinal))
        {
            return JsonResponse(
                HttpStatusCode.ServiceUnavailable,
                new { error = "temporarily_unavailable", message = "temporary list failure" });
        }
        return JsonResponse(HttpStatusCode.NotFound, new { error = "not_found", message = "missing" });
    });
    using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://cloud.test/") };
    using var api = new CloudApiClient(httpClient);
    var paths = new PluginPaths(Path.Combine(testRoot, "cloud-registration-refresh-failure"));
    paths.EnsureCreated();
    using var service = new CloudClientService(
        paths,
        api,
        new CloudCredentialStore(paths.CloudCredentialFile),
        new CloudBanStore(paths.CloudBanFile),
        new CloudMachineIdentity(paths.CloudDeviceFile),
        new CloudKeyEnvelopeService(),
        new PortableConfigurationBackupService());

    await service.RegisterAsync(
        "registration_user",
        "registration-password",
        "DACT-TEST",
        rememberLogin: true,
        cancellationToken: CancellationToken.None);

    var snapshot = service.Snapshot;
    Assert(
        snapshot.IsSignedIn &&
        snapshot.StatusIsError &&
        snapshot.RecoveryKeyToSave?.StartsWith("dact1_", StringComparison.Ordinal) == true &&
        File.Exists(paths.CloudCredentialFile),
        "A transient version-list failure hid the committed registration or its recovery key.");

    var optOutPaths = new PluginPaths(Path.Combine(
        testRoot,
        "cloud-registration-no-auto-login"));
    optOutPaths.EnsureCreated();
    using var optOutService = new CloudClientService(
        optOutPaths,
        api,
        new CloudCredentialStore(optOutPaths.CloudCredentialFile),
        new CloudBanStore(optOutPaths.CloudBanFile),
        new CloudMachineIdentity(optOutPaths.CloudDeviceFile),
        new CloudKeyEnvelopeService(),
        new PortableConfigurationBackupService());
    await optOutService.RegisterAsync(
        "registration_user",
        "registration-password",
        "DACT-TEST",
        rememberLogin: false,
        cancellationToken: CancellationToken.None);
    Assert(
        optOutService.Snapshot.IsSignedIn && !File.Exists(optOutPaths.CloudCredentialFile),
        "Disabling auto-login still persisted the cloud session on disk.");
}

static async Task ValidateCloudBanResponseEnforcesMarkerAsync(string testRoot)
{
    var bannedAt = DateTimeOffset.UtcNow;
    var handler = new ScriptedHttpMessageHandler(request =>
        request.RequestUri!.AbsolutePath.EndsWith("/auth/login", StringComparison.Ordinal)
            ? JsonResponse(HttpStatusCode.Forbidden, new
            {
                error = "account_banned",
                message = "您的账号已经被封禁。",
                banType = "cascade",
                bannedAt,
                banExpiresAt = (DateTimeOffset?)null,
                banReason = "resale-test",
            })
            : JsonResponse(HttpStatusCode.NotFound, new
            {
                error = "not_found",
                message = "missing",
            }));
    using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://cloud.test/") };
    using var api = new CloudApiClient(httpClient);
    var paths = new PluginPaths(Path.Combine(testRoot, "cloud-ban-response"));
    paths.EnsureCreated();
    var banStore = new CloudBanStore(paths.CloudBanFile);
    using var service = new CloudClientService(
        paths,
        api,
        new CloudCredentialStore(paths.CloudCredentialFile),
        banStore,
        new CloudMachineIdentity(paths.CloudDeviceFile),
        new CloudKeyEnvelopeService(),
        new PortableConfigurationBackupService());
    var notification = new TaskCompletionSource<CloudBanNotice>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    service.BanReceived += notice => notification.TrySetResult(notice);

    await service.LoginAsync(
        "banned_user",
        "banned-password",
        string.Empty,
        rememberLogin: true,
        cancellationToken: CancellationToken.None);
    var received = await notification.Task.WaitAsync(TimeSpan.FromSeconds(2));
    Assert(
        received.BanType == "cascade" && received.BanReason == "resale-test" &&
        service.Snapshot.ActiveBan == received && File.Exists(paths.CloudBanFile),
        "A login-time ban did not switch the client into its persistent blocked state.");

    File.Delete(paths.CloudBanFile);
    var deadline = DateTime.UtcNow.AddSeconds(3);
    while (!File.Exists(paths.CloudBanFile) && DateTime.UtcNow < deadline)
    {
        await Task.Delay(50);
    }
    Assert(
        File.Exists(paths.CloudBanFile) && banStore.Load() == received,
        "The running client did not restore a deleted cloud ban marker.");

    var corruptPaths = new PluginPaths(Path.Combine(testRoot, "cloud-corrupt-ban-marker"));
    corruptPaths.EnsureCreated();
    File.WriteAllBytes(corruptPaths.CloudBanFile, "not-a-valid-dpapi-marker"u8.ToArray());
    using var corruptMarkerService = new CloudClientService(
        corruptPaths,
        api,
        new CloudCredentialStore(corruptPaths.CloudCredentialFile),
        new CloudBanStore(corruptPaths.CloudBanFile),
        new CloudMachineIdentity(corruptPaths.CloudDeviceFile),
        new CloudKeyEnvelopeService(),
        new PortableConfigurationBackupService());
    Assert(
        corruptMarkerService.ActiveBan?.BanType == "unknown",
        "A present but damaged cloud ban marker incorrectly failed open.");

    var directoryMarkerPaths = new PluginPaths(
        Path.Combine(testRoot, "cloud-directory-ban-marker"));
    directoryMarkerPaths.EnsureCreated();
    Directory.CreateDirectory(directoryMarkerPaths.CloudBanFile);
    using var directoryMarkerService = new CloudClientService(
        directoryMarkerPaths,
        api,
        new CloudCredentialStore(directoryMarkerPaths.CloudCredentialFile),
        new CloudBanStore(directoryMarkerPaths.CloudBanFile),
        new CloudMachineIdentity(directoryMarkerPaths.CloudDeviceFile),
        new CloudKeyEnvelopeService(),
        new PortableConfigurationBackupService());
    Assert(
        directoryMarkerService.ActiveBan?.BanType == "unknown",
        "Replacing the cloud ban marker with a directory incorrectly failed open.");
}

static async Task ValidateCommittedRegistrationSurvivesCredentialWriteFailureAsync(
    string testRoot)
{
    var createdAt = DateTimeOffset.UtcNow;
    var handler = new ScriptedHttpMessageHandler(request =>
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path.EndsWith("/auth/register", StringComparison.Ordinal))
        {
            using var registration = JsonDocument.Parse(
                request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return JsonResponse(HttpStatusCode.Created, new
            {
                token = "committed-registration-token",
                tokenType = "Bearer",
                expiresAt = createdAt.AddDays(30),
                user = new { id = "committed-user-id", username = "committed_user" },
                keyEnvelope = registration.RootElement.GetProperty("keyEnvelope").Clone(),
            });
        }
        if (path.EndsWith("/backups", StringComparison.Ordinal))
        {
            return JsonResponse(HttpStatusCode.OK, new { backups = Array.Empty<object>() });
        }
        if (path.EndsWith("/invitations", StringComparison.Ordinal))
        {
            return JsonResponse(HttpStatusCode.OK, new
            {
                quota = 3,
                used = 0,
                remaining = 3,
                invitations = Array.Empty<object>(),
            });
        }
        return JsonResponse(HttpStatusCode.NotFound, new
        {
            error = "not_found",
            message = "missing",
        });
    });
    using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://cloud.test/") };
    using var api = new CloudApiClient(httpClient);
    var paths = new PluginPaths(Path.Combine(testRoot, "cloud-credential-write-failure"));
    paths.EnsureCreated();
    var directoryInPlaceOfCredentialFile = Path.Combine(paths.ConfigDirectory, "account-directory");
    Directory.CreateDirectory(directoryInPlaceOfCredentialFile);
    using var service = new CloudClientService(
        paths,
        api,
        new CloudCredentialStore(directoryInPlaceOfCredentialFile),
        new CloudBanStore(paths.CloudBanFile),
        new CloudMachineIdentity(paths.CloudDeviceFile),
        new CloudKeyEnvelopeService(),
        new PortableConfigurationBackupService());

    await service.RegisterAsync(
        "committed_user",
        "registration-password",
        "DACT-TEST",
        rememberLogin: true,
        cancellationToken: CancellationToken.None);
    Assert(
        service.Snapshot.IsSignedIn && service.Snapshot.StatusIsError &&
        service.Snapshot.StatusMessage.Contains("自动登录状态未能保存", StringComparison.Ordinal) &&
        service.Snapshot.RecoveryKeyToSave?.StartsWith("dact1_", StringComparison.Ordinal) == true,
        "A local credential write failure hid a remotely committed registration or recovery key.");
}

static HttpResponseMessage JsonResponse(HttpStatusCode status, object value)
    => new(status)
    {
        Content = JsonContent.Create(value),
    };

static async Task ValidateLiveCloudIntegrationAsync(
    string testRoot,
    Uri baseAddress,
    string adminToken)
{
    using var adminClient = new HttpClient { BaseAddress = baseAddress };
    adminClient.DefaultRequestHeaders.Authorization =
        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", adminToken);
    using var activationResponse = await adminClient.PostAsJsonAsync(
        "api/v1/admin/activation-keys",
        new { name = "C# 真实联调", note = "隔离临时数据库" });
    activationResponse.EnsureSuccessStatusCode();
    using var activationJson = JsonDocument.Parse(
        await activationResponse.Content.ReadAsStringAsync());
    var activationKey = activationJson.RootElement.GetProperty("activationKey").GetString()
                        ?? throw new InvalidDataException("Integration activation key was empty.");

    var username = $"integration_{Guid.NewGuid():N}"[..32];
    const string password = "integration-password";
    var deviceId = $"dact-device-v1_{new string('I', 43)}";
    var backupService = new PortableConfigurationBackupService();
    var envelopeService = new CloudKeyEnvelopeService();
    var recoveryKey = backupService.GenerateRecoveryKey();
    var envelope = envelopeService.Create(recoveryKey, password);
    var recoveryVerifier = envelopeService.CreateRecoveryVerifier(recoveryKey);
    using var clientHttp = new HttpClient { BaseAddress = baseAddress };
    using var api = new CloudApiClient(clientHttp);
    var registered = await api.RegisterAsync(
        username,
        password,
        activationKey,
        deviceId,
        envelope,
        recoveryVerifier,
        CancellationToken.None);
    Assert(
        registered.KeyEnvelope is not null &&
        envelopeService.Open(registered.KeyEnvelope, password) == recoveryKey,
        "Real HTTP registration did not preserve the password-wrapped account key.");

    var integrationRoot = Path.Combine(testRoot, "live-cloud-integration");
    var configurationRoot = Path.Combine(integrationRoot, "pluginConfigs");
    var pluginConfigurationDirectory = Path.Combine(configurationRoot, "DalamudActCompat");
    var archiveDirectory = Path.Combine(integrationRoot, "archives");
    Directory.CreateDirectory(pluginConfigurationDirectory);
    Directory.CreateDirectory(archiveDirectory);
    var mainConfigurationPath = Path.Combine(configurationRoot, "DalamudActCompat.json");
    await File.WriteAllTextAsync(
        mainConfigurationPath,
        new JObject
        {
            ["Version"] = 16,
            ["LogDirectory"] = @"C:\remote\logs",
            ["ActPluginDirectory"] = @"C:\remote\plugins",
            ["CloudMarker"] = "remote",
        }.ToString(Newtonsoft.Json.Formatting.Indented));
    var triggerPath = Path.Combine(
        pluginConfigurationDirectory,
        "Config",
        "Triggernometry.config.xml");
    Directory.CreateDirectory(Path.GetDirectoryName(triggerPath)!);
    await File.WriteAllTextAsync(triggerPath, "<triggers>remote</triggers>");
    var encryptedArchive = Path.Combine(archiveDirectory, "upload.dactcloud");
    var exported = await backupService.ExportEncryptedAsync(
        pluginConfigurationDirectory,
        encryptedArchive,
        recoveryKey,
        CancellationToken.None);
    await api.UploadBackupAsync(
        registered.Token,
        encryptedArchive,
        exported.ContentId,
        CancellationToken.None);

    var loggedIn = await api.LoginAsync(
        username,
        password,
        deviceId,
        CancellationToken.None);
    Assert(
        loggedIn.KeyEnvelope is not null &&
        envelopeService.Open(loggedIn.KeyEnvelope, password) == recoveryKey,
        "Real HTTP login could not recover the account data key.");
    var versions = await api.ListBackupsAsync(loggedIn.Token, CancellationToken.None);
    Assert(versions.Count == 1, "Real HTTP cloud version list did not contain the upload.");
    var downloadedArchive = Path.Combine(archiveDirectory, "download.dactcloud");
    await api.DownloadBackupAsync(
        loggedIn.Token,
        versions[0],
        downloadedArchive,
        CancellationToken.None);

    const string localLogDirectory = @"D:\local\logs";
    const string localPluginDirectory = @"D:\local\plugins";
    await File.WriteAllTextAsync(
        mainConfigurationPath,
        new JObject
        {
            ["Version"] = 16,
            ["LogDirectory"] = localLogDirectory,
            ["ActPluginDirectory"] = localPluginDirectory,
            ["CloudMarker"] = "local",
        }.ToString(Newtonsoft.Json.Formatting.Indented));
    var preview = await backupService.PreviewRestoreAsync(
        downloadedArchive,
        pluginConfigurationDirectory,
        recoveryKey,
        CancellationToken.None);
    Assert(preview.ChangedFiles >= 1, "Real HTTP restore preview found no changed files.");
    var rollback = Path.Combine(archiveDirectory, "rollback.dactcloud");
    await backupService.RestoreEncryptedAsync(
        downloadedArchive,
        pluginConfigurationDirectory,
        rollback,
        recoveryKey,
        CancellationToken.None);
    var restored = JObject.Parse(await File.ReadAllTextAsync(mainConfigurationPath));
    Assert(
        restored["CloudMarker"]?.Value<string>() == "remote" &&
        restored["LogDirectory"]?.Value<string>() == localLogDirectory &&
        restored["ActPluginDirectory"]?.Value<string>() == localPluginDirectory,
        "Real HTTP restore did not apply cloud data while preserving machine-local paths.");
    await backupService.RestoreEncryptedAsync(
        rollback,
        pluginConfigurationDirectory,
        Path.Combine(archiveDirectory, "rollback-of-rollback.dactcloud"),
        recoveryKey,
        CancellationToken.None);
    Assert(
        JObject.Parse(await File.ReadAllTextAsync(mainConfigurationPath))["CloudMarker"]?
            .Value<string>() == "local",
        "Real HTTP restore rollback did not recover the local configuration.");

    const string newPassword = "integration-new-password";
    var reset = await api.ResetPasswordWithRecoveryAsync(
        username,
        recoveryVerifier,
        newPassword,
        deviceId,
        envelopeService.Create(recoveryKey, newPassword),
        CancellationToken.None);
    Assert(
        reset.KeyEnvelope is not null &&
        envelopeService.Open(reset.KeyEnvelope, newPassword) == recoveryKey,
        "Real HTTP password reset changed or lost the account data key.");
    try
    {
        _ = await api.LoginAsync(username, password, deviceId, CancellationToken.None);
        throw new InvalidOperationException("Old password remained valid after real HTTP reset.");
    }
    catch (CloudApiException exception) when (exception.StatusCode == HttpStatusCode.Unauthorized)
    {
        // The server invalidates the old password and sessions after reset.
    }
    _ = await api.LoginAsync(username, newPassword, deviceId, CancellationToken.None);
}

public sealed class GenericActPluginFixture : IActPluginV1
{
    public void InitPlugin(TabPage pluginScreenSpace, Label pluginStatusText)
        => pluginStatusText.Text = "ready";

    public void DeInitPlugin()
    {
    }
}

internal enum PluginIntegrationMode
{
    Disabled,
    Auto,
}

internal sealed class TestParserEngine : IParserEngine
{
    public TestParserEngine(ParserState initialState)
    {
        Status = new ParserStatus(initialState, initialState.ToString(), DateTimeOffset.UtcNow);
    }

    public event EventHandler<ParserStatus>? StatusChanged;

    public ParserStatus Status { get; private set; }

    public int StartCount { get; private set; }

    public int StopCount { get; private set; }

    public int ResetCount { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StartCount++;
        SetStatus(ParserState.Running);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StopCount++;
        SetStatus(ParserState.Stopped);
        return Task.CompletedTask;
    }

    public async Task RestartAsync(CancellationToken cancellationToken)
    {
        await StopAsync(cancellationToken);
        await StartAsync(cancellationToken);
    }

    public void ResetCurrentEncounter() => ResetCount++;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private void SetStatus(ParserState state)
    {
        Status = new ParserStatus(state, state.ToString(), DateTimeOffset.UtcNow);
        StatusChanged?.Invoke(this, Status);
    }
}

internal sealed class BlockingStartupParserEngine : IParserEngine
{
    private readonly TaskCompletionSource startEntered = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource startCompleted = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public event EventHandler<ParserStatus>? StatusChanged;

    public ParserStatus Status { get; private set; } = new(
        ParserState.Stopped,
        ParserState.Stopped.ToString(),
        DateTimeOffset.UtcNow);

    public Task StartEntered => startEntered.Task;

    public int StartCount { get; private set; }

    public int StopCount { get; private set; }

    public bool StopObservedCompletedStartup { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        StartCount++;
        SetStatus(ParserState.Initializing);
        startEntered.TrySetResult();
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        finally
        {
            startCompleted.TrySetResult();
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StopCount++;
        StopObservedCompletedStartup = startCompleted.Task.IsCompleted;
        SetStatus(ParserState.Stopped);
        return Task.CompletedTask;
    }

    public async Task RestartAsync(CancellationToken cancellationToken)
    {
        await StopAsync(cancellationToken);
        await StartAsync(cancellationToken);
    }

    public void ResetCurrentEncounter()
    {
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private void SetStatus(ParserState state)
    {
        Status = new ParserStatus(state, state.ToString(), DateTimeOffset.UtcNow);
        StatusChanged?.Invoke(this, Status);
    }
}

internal sealed class EnumSettingsOwner
{
    public PluginIntegrationMode PluginIntegration { get; set; }
}

internal sealed class FoxTtsProbe : IDisposable
{
    private readonly ManualResetEventSlim spoken = new();

    public string? LastMessage { get; private set; }

    public void Speak(string message)
    {
        LastMessage = message;
        spoken.Set();
    }

    public bool Wait(TimeSpan timeout) => spoken.Wait(timeout);

    public void Dispose() => spoken.Dispose();
}

public class NoOpPluginLogProxy : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var returnType = targetMethod?.ReturnType;
        return returnType is null || returnType == typeof(void)
            ? null
            : returnType.IsValueType
                ? Activator.CreateInstance(returnType)
                : null;
    }
}

public class NoOpDataManagerProxy : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var returnType = targetMethod?.ReturnType;
        return returnType is null || returnType == typeof(void)
            ? null
            : returnType.IsValueType
                ? Activator.CreateInstance(returnType)
                : null;
    }
}

internal static class NativeInputProbe
{
    public const int GwlExStyle = -20;
    public const uint LeftDown = 0x0002;
    public const uint LeftUp = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    public static extern nint WindowFromPoint(Point point);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern nint GetWindowLongPtr(nint windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "mouse_event")]
    public static extern void MouseEvent(
        uint flags,
        uint dx,
        uint dy,
        uint data,
        UIntPtr extraInfo);
}

internal static class NativeSocketProbe
{
    [DllImport("Ws2_32.dll", EntryPoint = "closesocket", SetLastError = true)]
    public static extern int Close(nint socket);
}

internal sealed class OverlayServerTestLogger : RainbowMage.OverlayPlugin.ILogger
{
    private readonly object syncRoot = new();
    private readonly List<string> messages = [];

    public IReadOnlyList<string> Messages
    {
        get
        {
            lock (syncRoot)
                return messages.ToArray();
        }
    }

    public void Log(RainbowMage.OverlayPlugin.LogLevel level, string message)
    {
        lock (syncRoot)
            messages.Add(message);
    }

    public void Log(RainbowMage.OverlayPlugin.LogLevel level, string format, params object[] args)
        => Log(level, string.Format(format, args));

    public void RegisterListener(Action<RainbowMage.OverlayPlugin.LogEntry> listener)
    {
    }

    public void ClearListener()
    {
    }
}

internal sealed class OverlayServerTestConfig : RainbowMage.OverlayPlugin.IPluginConfig
{
    public RainbowMage.OverlayPlugin.OverlayConfigList<RainbowMage.OverlayPlugin.IOverlayConfig> Overlays { get; set; } = null!;
    public bool HideOverlaysWhenNotActive { get; set; }
    public bool HideOverlayDuringCutscene { get; set; }
    public string WSServerIP { get; set; } = IPAddress.Loopback.ToString();
    public int WSServerPort { get; set; }
    public bool WSServerSSL { get; set; }
    public bool WSServerRunning { get; set; }
    public Version Version { get; set; } = new(1, 0);
    public Dictionary<string, JObject> EventSourceConfigs { get; set; } = [];

    public void MarkDirty()
    {
    }

    public void Save()
    {
    }
}

internal sealed class TestActLogger : IActLogger
{
    public void Error(Exception exception, string message)
    {
    }

    public void Verbose(Exception exception, string message)
    {
    }

    public void Warning(string message)
    {
    }
}

internal sealed class OverloadedActLoggerProbe
{
    public Exception? Exception { get; private set; }

    public string? Message { get; private set; }

    public bool StringOnlyOverloadCalled { get; private set; }

    public void Error(string message, params object[] values)
        => StringOnlyOverloadCalled = true;

    public void Error(Exception exception, string message, params object[] values)
    {
        Exception = exception;
        Message = message;
    }
}

internal sealed class BundledPluginUpdateHandler : HttpMessageHandler
{
    private const string TriggerTranslationUrl =
        "https://1824544011.v.123pan.cn/1824544011/Triggernometry_Release_CN/zh-CN.triglations.xml";
    private const string FoxApiUrl =
        "https://api.github.com/repos/Noisyfox/ACT.FoxTTS/releases/latest";
    private const string PostApiUrl =
        "https://api.github.com/repos/Natsukage/PostNamazu/releases/latest";
    private const string FoxDownloadUrl = "https://downloads.example/ACT.FoxTTS.7z";
    private const string PostDownloadUrl = "https://downloads.example/PostNamazu.zip";

    private readonly string triggerDownloadUrl;
    private readonly byte[] triggerAssembly;
    private readonly byte[] foxArchive;
    private readonly byte[] postArchive;

    public BundledPluginUpdateHandler(
        IReadOnlyList<BundledActPluginDescriptor> bundled)
    {
        var trigger = bundled.Single(plugin => plugin.Id == "triggernometry");
        var fox = bundled.Single(plugin => plugin.Id == "act.foxtts");
        var post = bundled.Single(plugin => plugin.Id == "postnamazu");
        triggerDownloadUrl = trigger.DownloadUrl;
        triggerAssembly = CreateChangedAssembly(trigger.AssemblyPath);
        foxArchive = CreateArchive(
            "release/ACT.FoxTTS.dll",
            CreateChangedAssembly(fox.AssemblyPath));
        postArchive = CreateArchive(
            "release/PostNamazu.dll",
            CreateChangedAssembly(post.AssemblyPath));
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var url = request.RequestUri?.AbsoluteUri ?? string.Empty;
        var response = url switch
        {
            var value when value == triggerDownloadUrl =>
                Bytes(triggerAssembly),
            TriggerTranslationUrl =>
                Bytes("<translations />"u8.ToArray()),
            FoxApiUrl =>
                Json(new
                {
                    assets = new[]
                    {
                        new
                        {
                            name = "ACT.FoxTTS-Test-Release.7z",
                            browser_download_url = FoxDownloadUrl,
                        },
                    },
                }),
            PostApiUrl =>
                Json(new
                {
                    assets = new[]
                    {
                        new
                        {
                            name = "PostNamazu.zip",
                            browser_download_url = PostDownloadUrl,
                        },
                    },
                }),
            FoxDownloadUrl =>
                Bytes(foxArchive),
            PostDownloadUrl =>
                Bytes(postArchive),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
        return Task.FromResult(response);
    }

    private static HttpResponseMessage Json<T>(T value)
        => Bytes(JsonSerializer.SerializeToUtf8Bytes(value));

    private static HttpResponseMessage Bytes(byte[] value)
        => new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(value),
        };

    private static byte[] CreateChangedAssembly(string path)
    {
        var original = File.ReadAllBytes(path);
        Array.Resize(ref original, original.Length + 1);
        original[^1] = 0x5a;
        return original;
    }

    private static byte[] CreateArchive(string path, byte[] content)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(
                   output,
                   ZipArchiveMode.Create,
                   leaveOpen: true))
        {
            var entry = archive.CreateEntry(path);
            using var stream = entry.Open();
            stream.Write(content);
        }

        return output.ToArray();
    }
}

sealed record ResourcePackFixture(
    string PluginDirectory,
    string CacheDirectory,
    byte[] ArchiveBytes,
    ResourcePackEntry Entry);

sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

sealed class ScriptedHttpMessageHandler(
    Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
{
    private readonly object syncRoot = new();
    private readonly List<Uri> requests = [];

    public IReadOnlyList<Uri> Requests
    {
        get
        {
            lock (syncRoot)
            {
                return requests.ToArray();
            }
        }
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        lock (syncRoot)
        {
            requests.Add(request.RequestUri!);
        }
        return Task.FromResult(responseFactory(request));
    }
}

sealed class InterruptingReadStream(byte[] bytes, int interruptAfter) : Stream
{
    private readonly MemoryStream inner = new(bytes, writable: false);
    private bool interrupted;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position
    {
        get => inner.Position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ThrowIfInterrupted();
        var available = Math.Min(count, interruptAfter - (int)inner.Position);
        return inner.Read(buffer, offset, Math.Max(0, available));
    }

    public override int Read(Span<byte> buffer)
    {
        ThrowIfInterrupted();
        var available = Math.Min(buffer.Length, interruptAfter - (int)inner.Position);
        return inner.Read(buffer[..Math.Max(0, available)]);
    }

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Read(buffer.Span));
    }

    private void ThrowIfInterrupted()
    {
        if (inner.Position < interruptAfter || interrupted)
        {
            return;
        }
        interrupted = true;
        throw new IOException("simulated interrupted download");
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }
        base.Dispose(disposing);
    }
}

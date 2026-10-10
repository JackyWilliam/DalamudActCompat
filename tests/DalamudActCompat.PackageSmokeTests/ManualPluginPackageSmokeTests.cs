using System.Reflection;
using Dalamud.Plugin.Services;
using DalamudActCompat.ActRuntime;
using DalamudActCompat.Compatibility.PluginHost;
using DalamudActCompat.Core.State;
using DalamudActCompat.Infrastructure.Ipc;
using DalamudActCompat.Infrastructure.Logging;
using DalamudActCompat.Infrastructure.Storage;
using DalamudActCompat.Protocol;

internal static class ManualPluginPackageSmokeTests
{
    public static async Task RunAsync(string root)
    {
        var log = DispatchProxy.Create<IPluginLog, NoOpPluginLogProxy>();
        await using var generic = new HostIpcClient(new EncounterStateStore(), new PluginLogger(log), expectedTtsSource: "generic");
        await using var matcha = new HostIpcClient(new EncounterStateStore(), new PluginLogger(log));
        var apply = typeof(HostIpcClient).GetMethod("ApplyMessage", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var genericCount = 0;
        var matchaCount = 0;
        generic.MatchaTtsRequested += (_, _) => genericCount++;
        matcha.MatchaTtsRequested += (_, _) => matchaCount++;
        // The wire message is shared with Matcha, but each connection must accept
        // only its own source. This catches the previous generic-TTS silent drop.
        foreach (var speech in new[]
                 {
                     new HostTtsRequest("石牢第一", "generic"), new HostTtsRequest("matcha", "matcha"),
                     new HostTtsRequest("wrong source", "other"), new HostTtsRequest(" ", "generic"),
                     new HostTtsRequest(new string('a', 2001), "generic"),
                 })
        {
            var message = HostEnvelope.Create("fixture", 1, HostMessageTypes.MatchaTtsRequest, HostMessagePriority.Control, speech);
            apply.Invoke(generic, [message]);
            apply.Invoke(matcha, [message]);
        }
        Assert(genericCount == 1 && matchaCount == 1, "TTS source or payload validation crossed Host boundaries.");

        var uploaderPackage = Environment.GetEnvironmentVariable("ACTCOMPAT_UPLOADER_PACKAGE");
        var titanDll = Environment.GetEnvironmentVariable("ACTCOMPAT_TITAN_DLL");
        if (string.IsNullOrEmpty(uploaderPackage) || string.IsNullOrEmpty(titanDll))
        {
            Console.WriteLine("PASS: generic/Matcha TTS validation. Optional manual-plugin files were not supplied.");
            return;
        }
        var paths = new PluginPaths(Path.Combine(root, "manual-plugins"));
        var installer = new ActPluginPackageInstaller(paths);
        var uploader = await installer.InstallAsync(uploaderPackage, CancellationToken.None, userInitiated: true);
        Check(uploader, "actlogsuploader", "ACTLogsUploader.Plugin",
            [ActCapability.NetworkRequest, ActCapability.LaunchExternalProcess, ActCapability.HighRiskScript, ActCapability.NativeSystemAccess]);
        Assert(Directory.GetFiles(uploader.InstallDirectory, "*.dll", SearchOption.AllDirectories).Length == 15, "Uploader ZIP dependencies were lost.");
        // Use the installed copy as the loose-DLL source, not the user's folder.
        // InstallAsync stages before it backs up the destination.
        uploader = await installer.InstallAsync(Path.Combine(uploader.InstallDirectory, uploader.Manifest.EntryAssembly), CancellationToken.None, userInitiated: true);
        var entryDirectory = Path.GetDirectoryName(Path.Combine(uploader.InstallDirectory, uploader.Manifest.EntryAssembly))!;
        Assert(File.Exists(Path.Combine(entryDirectory, "ClearScript.V8.ICUData.dll")) &&
               File.Exists(Path.Combine(entryDirectory, "ClearScriptV8.win-x64.dll")), "Loose import lost dynamic V8 companions.");
        var incomplete = Path.Combine(root, "incomplete-uploader"); Directory.CreateDirectory(incomplete);
        File.Copy(Path.Combine(entryDirectory, "ACTLogsUploader.dll"), Path.Combine(incomplete, "ACTLogsUploader.dll"));
        try
        {
            await installer.InstallAsync(Path.Combine(incomplete, "ACTLogsUploader.dll"), CancellationToken.None, userInitiated: true);
            throw new InvalidOperationException("Incomplete uploader was accepted.");
        }
        catch (InvalidDataException ex) when (ex.Message.Contains("ClearScript", StringComparison.Ordinal)) { }
        Assert(File.Exists(Path.Combine(entryDirectory, "ClearScriptV8.win-x64.dll")), "Failed import replaced the working installation.");
        var titan = await installer.InstallAsync(titanDll, CancellationToken.None, userInitiated: true);
        Check(titan, "act.jsb_5", "JJJSBTT", [ActCapability.TextToSpeech, ActCapability.GameCommand, ActCapability.NativeSystemAccess]);
        Assert(installer.Discover(new HashSet<string>()).Count == 2, "Manual imports were not discoverable together.");
        Console.WriteLine("PASS: original ZIP/DLL imports, third Host routing, permission declarations, dynamic companions and failed-install preservation.");
    }

    private static void Check(InstalledActPlugin plugin, string id, string entry, ActCapability[] capabilities)
    {
        Assert(plugin.Manifest.Id == id && plugin.Manifest.EntryType == entry && plugin.Manifest.UserInstalled &&
               !ActPluginPackageInstaller.IsSpecializedPluginId(id), "Manual plugin was assigned to a bundled/specialized Host.");
        Assert(capabilities.All(ActPluginPackageInstaller.GetRequestedCapabilities(plugin.Manifest).Contains), "Required capabilities were not declared.");
    }
    private static void Assert(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
}

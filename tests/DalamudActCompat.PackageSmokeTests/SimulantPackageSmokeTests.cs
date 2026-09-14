using DalamudActCompat.ActRuntime;
using DalamudActCompat.Compatibility.PluginHost;
using DalamudActCompat.Infrastructure.Storage;
using DalamudActCompat.Plugin;

internal static class SimulantPackageSmokeTests
{
    public static async Task RunAsync(string root)
    {
        var config = new PluginConfiguration();
        Assert(ActPluginPackageInstaller.IsSpecializedPluginId("SIMULANT"),
            "Simulant was routed to the generic Host without its dependencies.");
        Assert(!config.IsActCapabilityAllowed("simulant", ActCapability.NativeGameMemory),
            "A manual Simulant installation must not inherit native memory permission.");
        Assert(BundledActPluginCapabilities.Simulant.SequenceEqual([ActCapability.NativeGameMemory]),
            "The Simulant permission entry no longer matches its native bridge.");

        // The original fixture is supplied by the test caller. Product code neither fetches
        // Simulant nor creates an installation until the user imports their own package.
        var paths = new PluginPaths(Path.Combine(root, "simulant-install-test"));
        paths.EnsureCreated();
        var installer = new ActPluginPackageInstaller(paths);
        Assert(installer.Discover(config.DisabledActPluginIds).Count == 0,
            "Simulant appeared before a manual import.");
        var original = Environment.GetEnvironmentVariable("ACTCOMPAT_SIMULANT_DLL");
        if (!string.IsNullOrWhiteSpace(original))
        {
            var installed = await installer.InstallAsync(original, CancellationToken.None);
            Assert(installed.Manifest.Id == "simulant" && installed.Manifest.EntryType == "Simulant.PluginMain",
                "The original DLL did not produce the specialized Simulant manifest.");
            Assert(installed.Manifest.SourceSha256.Equals(
                "12F31A446D6F137A7D0B39939F9E0361240D9209AC3677624FF95431C2578467", StringComparison.OrdinalIgnoreCase),
                "Installation modified the author's original fixture DLL.");
            Assert(ActPluginPackageInstaller.GetRequestedCapabilities(installed.Manifest).Contains(ActCapability.NativeGameMemory),
                "The installed manifest omits Simulant's native permission.");
        }
        Console.WriteLine("PASS: Simulant manual import, specialized routing, original DLL preservation, and default permissions.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

using System.Net;
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
            "A new Simulant installation must not inherit native memory permission.");
        Assert(BundledActPluginCapabilities.Simulant.SequenceEqual([ActCapability.NativeGameMemory]),
            "The Simulant permission entry no longer matches its native bridge.");

        var cache = Path.Combine(root, "simulant-download-test");
        using (var client = new HttpClient(new ResponseHandler(() => new ByteArrayContent([1, 2, 3]))))
        {
            await RejectAsync(() => SimulantDownload.DownloadAsync(cache, client, CancellationToken.None));
        }
        // Correct length alone must never authorize a corrupted or substituted release DLL.
        using (var client = new HttpClient(new ResponseHandler(() => new ByteArrayContent(new byte[SimulantDownload.FileSize]))))
        {
            await RejectAsync(() => SimulantDownload.DownloadAsync(cache, client, CancellationToken.None));
        }
        Assert(!Directory.EnumerateFiles(cache, "*.download", SearchOption.AllDirectories).Any(),
            "Failed downloads left a staging artifact.");
        Assert(!File.Exists(Path.Combine(cache, "simulant", SimulantDownload.Version, "Simulant.dll")),
            "A rejected download became installable.");

        var original = Environment.GetEnvironmentVariable("ACTCOMPAT_SIMULANT_DLL");
        if (!string.IsNullOrWhiteSpace(original))
        {
            using var handler = new ResponseHandler(() => new StreamContent(File.OpenRead(original)));
            using var client = new HttpClient(handler);
            var path = await SimulantDownload.DownloadAsync(cache, client, CancellationToken.None);
            Assert(await SimulantDownload.DownloadAsync(cache, client, CancellationToken.None) == path && handler.RequestCount == 1,
                "A verified cached original was downloaded again.");
            var paths = new PluginPaths(Path.Combine(root, "simulant-install-test"));
            paths.EnsureCreated();
            var installed = await new ActPluginPackageInstaller(paths).InstallAsync(path, CancellationToken.None);
            Assert(installed.Manifest.Id == "simulant" && installed.Manifest.EntryType == "Simulant.PluginMain",
                "The original DLL did not produce the specialized Simulant manifest.");
            Assert(installed.Manifest.SourceSha256.Equals(SimulantDownload.Sha256, StringComparison.OrdinalIgnoreCase),
                "Installation modified the author's DLL.");
            Assert(ActPluginPackageInstaller.GetRequestedCapabilities(installed.Manifest).Contains(ActCapability.NativeGameMemory),
                "The installed manifest omits Simulant's native permission.");
        }
        Console.WriteLine("PASS: Simulant routing, permissions, bounded download/hash rejection, cache, and original installation.");
    }

    private static async Task RejectAsync(Func<Task<string>> action)
    {
        try { await action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Invalid Simulant download was accepted.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class ResponseHandler(Func<HttpContent> content) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert(request.RequestUri?.AbsoluteUri == SimulantDownload.DownloadUrl, "Download escaped the pinned upstream release.");
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content() });
        }
    }
}

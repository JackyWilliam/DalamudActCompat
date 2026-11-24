using DalamudActCompat.Infrastructure.Resources;
using DalamudActCompat.Protocol;

namespace DalamudActCompat.Fflogs.Upload;

// The FF Logs parser is JavaScript and runs in V8. The Host ships ClearScript's managed
// bindings; the native V8 library and its ICU data are about 15 MB to download, which only
// players who upload logs need. DACT therefore does not publish them: the first parse fetches
// Microsoft's own NuGet packages and accepts nothing but the exact archives pinned here. The
// pins do not change between DACT versions, so the verified cache survives updates.
//
// Upgrading ClearScript means changing the Host's package version and these pins together; the
// package smoke test compares them with the packages the build restored.
internal static class FflogsUploadEngineFiles
{
    public const string PackageVersion = "7.5.0";
    public const string NativePackageId = "microsoft.clearscript.v8.native.win-x64";
    public const string IcuDataPackageId = "microsoft.clearscript.v8.icudata";

    public static readonly ResourcePackEntry Native = Pin(
        "fflogs-v8-native",
        NativePackageId,
        size: 9_945_862,
        sha256: "fa20949041d120931d80441abaaa1bd410be1f46dc6fb3e59fdc980541dced22",
        contentDirectory: "runtimes",
        contentSha256: "2ff619d21e695cf3cd67cf8bf09d6b97b1f8b1238f6ac7cc3249af1b43df9b03");

    public static readonly ResourcePackEntry IcuData = Pin(
        "fflogs-v8-icudata",
        IcuDataPackageId,
        size: 4_677_145,
        sha256: "841df9fac5d64622f16e9ddaca621b4769c3bc22fd089de2878c50d1b9c3b71c",
        contentDirectory: "lib",
        contentSha256: "eee48e98dc9472601a7f5be3290f8e1fbeb4edc864ac7068ebaa60ceacec309c");

    // Where the two files sit inside the packages' content directories.
    public static FflogsUploadEngine Locate(string nativeContentDirectory, string icuDataContentDirectory)
        => new(
            Path.Combine(nativeContentDirectory, "win-x64", "native"),
            Path.Combine(icuDataContentDirectory, "netstandard1.0", "ClearScript.V8.ICUData.dll"));

    public static async Task<FflogsUploadEngine> ResolveAsync(
        ResourcePackManager packs,
        CancellationToken cancellationToken,
        Action<int> reportPercent)
    {
        var total = Native.Size + IcuData.Size;
        var native = await packs.ResolvePinnedAsync(
                Native,
                cancellationToken,
                new Relay(progress => reportPercent(Percent(progress.BytesReceived, total))))
            .ConfigureAwait(false);
        var icuData = await packs.ResolvePinnedAsync(
                IcuData,
                cancellationToken,
                new Relay(progress => reportPercent(Percent(Native.Size + progress.BytesReceived, total))))
            .ConfigureAwait(false);
        return Locate(native, icuData);
    }

    private static int Percent(long received, long total)
        => total <= 0 ? 0 : (int)Math.Clamp(received * 100L / total, 0, 100);

    private static ResourcePackEntry Pin(
        string id,
        string packageId,
        long size,
        string sha256,
        string contentDirectory,
        string contentSha256)
    {
        var fileName = $"{packageId}.{PackageVersion}.nupkg";
        var path = $"{packageId}/{PackageVersion}/{fileName}";
        return new ResourcePackEntry(
            id,
            PackageVersion,
            fileName,
            size,
            sha256,
            contentDirectory,
            contentSha256,
            [
                $"https://api.nuget.org/v3-flatcontainer/{path}",
                // Huawei Cloud's NuGet mirror (the package base address its service index
                // lists), for players in China who cannot reach nuget.org. The archive is
                // verified against the pin whichever source served it.
                $"https://repo.huaweicloud.com/artifactory/api/nuget/v3/nuget-remote/{path}",
            ]);
    }

    // Progress<T> would post each report to the thread pool and let them arrive out of order.
    private sealed class Relay(Action<ResourcePackDownloadProgress> report) : IProgress<ResourcePackDownloadProgress>
    {
        public void Report(ResourcePackDownloadProgress value) => report(value);
    }
}

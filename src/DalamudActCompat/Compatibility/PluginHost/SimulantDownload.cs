using System.Net.Http;
using System.Security.Cryptography;

namespace DalamudActCompat.Compatibility.PluginHost;

internal static class SimulantDownload
{
    public const string Version = "0.0.4.2";
    public const string SourceUrl = "https://github.com/MnFeN/Simulant";
    public const string DownloadUrl = SourceUrl + "/releases/download/v" + Version + "/Simulant.dll";
    public const string Sha256 = "12F31A446D6F137A7D0B39939F9E0361240D9209AC3677624FF95431C2578467";
    public const long FileSize = 28_793_856;

    public static async Task<string> DownloadAsync(
        string cacheRoot,
        HttpClient client,
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(cacheRoot, "simulant", Version);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "Simulant.dll");
        // Pin the tested upstream binary because new signatures and layouts require another
        // compatibility review. The original DLL is fetched from its author, never repacked.
        if (File.Exists(path) && await IsValidAsync(path, cancellationToken).ConfigureAwait(false))
        {
            return path;
        }

        var staging = Path.Combine(directory, $"{Guid.NewGuid():N}.download");
        try
        {
            using var response = await client.GetAsync(
                DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } length && length != FileSize)
            {
                throw new InvalidDataException("Simulant 下载大小与已验证版本不一致。");
            }

            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var output = File.Create(staging))
            {
                var buffer = new byte[81920];
                long total = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    total += count;
                    if (total > FileSize)
                    {
                        throw new InvalidDataException("Simulant 下载超出预期大小。");
                    }
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                }
            }

            if (!await IsValidAsync(staging, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidDataException("Simulant SHA-256 校验失败，请重新下载。");
            }
            File.Move(staging, path, overwrite: true);
            return path;
        }
        finally
        {
            if (File.Exists(staging))
            {
                File.Delete(staging);
            }
        }
    }

    private static async Task<bool> IsValidAsync(string path, CancellationToken cancellationToken)
    {
        if (new FileInfo(path).Length != FileSize)
        {
            return false;
        }
        await using var input = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false)) == Sha256;
    }
}

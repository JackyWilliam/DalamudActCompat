using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using SharpCompress.Archives;

namespace DalamudActCompat.Compatibility.PluginHost;

public sealed class BundledActPluginUpdateCheckResult
{
    public BundledActPluginUpdateCheckResult(
        IReadOnlyList<BundledActPluginDescriptor> updates,
        IReadOnlyList<string> failures)
    {
        Updates = updates;
        Failures = failures;
    }

    public IReadOnlyList<BundledActPluginDescriptor> Updates { get; }

    public IReadOnlyList<string> Failures { get; }
}

public sealed partial class BundledActPluginUpdateChecker : IDisposable
{
    private const long MaximumDownloadBytes = 128L * 1024 * 1024;
    private const long MaximumAssemblyBytes = 64L * 1024 * 1024;
    private const string TriggerTranslationUrl =
        "https://1824544011.v.123pan.cn/1824544011/Triggernometry_Release_CN/zh-CN.triglations.xml";

    private readonly string cacheDirectory;
    private readonly HttpClient httpClient;
    private readonly bool ownsHttpClient;

    public BundledActPluginUpdateChecker(
        string cacheDirectory,
        HttpClient? httpClient = null)
    {
        this.cacheDirectory = Path.GetFullPath(cacheDirectory);
        this.httpClient = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(45),
        };
        ownsHttpClient = httpClient is null;
        if (!this.httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            this.httpClient.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("DalamudActCompat", "0.2"));
        }

        if (!this.httpClient.DefaultRequestHeaders.Accept.Any())
        {
            this.httpClient.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        }
    }

    public async Task<BundledActPluginUpdateCheckResult> CheckAsync(
        IReadOnlyList<BundledActPluginDescriptor> bundledPlugins,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(cacheDirectory);
        var checks = bundledPlugins
            .Select(plugin => CheckSafelyAsync(plugin, cancellationToken))
            .ToArray();
        var outcomes = await Task.WhenAll(checks).ConfigureAwait(false);
        return new BundledActPluginUpdateCheckResult(
            outcomes
                .Where(outcome => outcome.Update is not null)
                .Select(outcome => outcome.Update!)
                .ToArray(),
            outcomes
                .Where(outcome => !string.IsNullOrWhiteSpace(outcome.Failure))
                .Select(outcome => outcome.Failure!)
                .ToArray());
    }

    public void Dispose()
    {
        if (ownsHttpClient)
        {
            httpClient.Dispose();
        }
    }

    private async Task<CheckOutcome> CheckSafelyAsync(
        BundledActPluginDescriptor bundled,
        CancellationToken cancellationToken)
    {
        try
        {
            return new CheckOutcome(
                await CheckPluginAsync(bundled, cancellationToken).ConfigureAwait(false),
                null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new CheckOutcome(
                null,
                $"{bundled.Name}: {ex.GetBaseException().Message}");
        }
    }

    private async Task<BundledActPluginDescriptor?> CheckPluginAsync(
        BundledActPluginDescriptor bundled,
        CancellationToken cancellationToken)
    {
        var workDirectory = Path.Combine(
            cacheDirectory,
            $"{bundled.Id}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDirectory);
        try
        {
            var candidate = bundled.Id switch
            {
                "triggernometry" => await CheckTriggernometryAsync(
                    bundled,
                    workDirectory,
                    cancellationToken).ConfigureAwait(false),
                "act.foxtts" => await CheckGithubArchiveAsync(
                    bundled,
                    workDirectory,
                    "Noisyfox/ACT.FoxTTS",
                    FoxTtsAssetPattern(),
                    "ACT.FoxTTS.dll",
                    isSevenZip: true,
                    cancellationToken).ConfigureAwait(false),
                "postnamazu" => await CheckGithubArchiveAsync(
                    bundled,
                    workDirectory,
                    "Natsukage/PostNamazu",
                    PostNamazuAssetPattern(),
                    "PostNamazu.dll",
                    isSevenZip: false,
                    cancellationToken).ConfigureAwait(false),
                _ => throw new InvalidDataException(
                    $"No online update source is registered for {bundled.Id}."),
            };
            if (candidate is null)
            {
                TryDeleteDirectory(workDirectory);
            }

            return candidate;
        }
        catch
        {
            TryDeleteDirectory(workDirectory);
            throw;
        }
    }

    private async Task<BundledActPluginDescriptor?> CheckTriggernometryAsync(
        BundledActPluginDescriptor bundled,
        string workDirectory,
        CancellationToken cancellationToken)
    {
        var assemblyPath = Path.Combine(workDirectory, "Triggernometry.dll");
        var translationPath = Path.Combine(workDirectory, "zh-CN.triglations.xml");
        await DownloadFileAsync(
            bundled.DownloadUrl,
            assemblyPath,
            cancellationToken).ConfigureAwait(false);
        await DownloadFileAsync(
            TriggerTranslationUrl,
            translationPath,
            cancellationToken).ConfigureAwait(false);
        return CreateCandidate(
            bundled,
            assemblyPath,
            bundled.DownloadUrl);
    }

    private async Task<BundledActPluginDescriptor?> CheckGithubArchiveAsync(
        BundledActPluginDescriptor bundled,
        string workDirectory,
        string repository,
        Regex assetPattern,
        string assemblyName,
        bool isSevenZip,
        CancellationToken cancellationToken)
    {
        var release = await GetLatestReleaseAsync(
            repository,
            assetPattern,
            cancellationToken).ConfigureAwait(false);
        var archivePath = Path.Combine(workDirectory, release.AssetName);
        var assemblyPath = Path.Combine(workDirectory, assemblyName);
        await DownloadFileAsync(
            release.DownloadUrl,
            archivePath,
            cancellationToken).ConfigureAwait(false);
        if (isSevenZip)
        {
            await ExtractSingleFileFromArchiveAsync(
                archivePath,
                assemblyName,
                assemblyPath,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await ExtractSingleFileFromZipAsync(
                archivePath,
                assemblyName,
                assemblyPath,
                cancellationToken).ConfigureAwait(false);
        }

        return CreateCandidate(
            bundled,
            assemblyPath,
            release.DownloadUrl);
    }

    private async Task<ReleaseAsset> GetLatestReleaseAsync(
        string repository,
        Regex assetPattern,
        CancellationToken cancellationToken)
    {
        var uri = $"https://api.github.com/repos/{repository}/releases/latest";
        using var response = await httpClient
            .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 2L * 1024 * 1024)
        {
            throw new InvalidDataException(
                $"GitHub release response for {repository} is unexpectedly large.");
        }

        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var document = await JsonDocument
            .ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var assets = document.RootElement.GetProperty("assets")
            .EnumerateArray()
            .Where(asset =>
                asset.TryGetProperty("name", out var name) &&
                assetPattern.IsMatch(name.GetString() ?? string.Empty))
            .ToArray();
        if (assets.Length != 1)
        {
            throw new InvalidDataException(
                $"Expected one {repository} release asset, found {assets.Length}.");
        }

        return new ReleaseAsset(
            assets[0].GetProperty("name").GetString()
                ?? throw new InvalidDataException("Release asset name is empty."),
            assets[0].GetProperty("browser_download_url").GetString()
                ?? throw new InvalidDataException("Release asset URL is empty."));
    }

    private async Task DownloadFileAsync(
        string sourceUrl,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidDataException($"Update URL is not HTTPS: {sourceUrl}");
        }

        using var response = await httpClient
            .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumDownloadBytes)
        {
            throw new InvalidDataException($"Download exceeds {MaximumDownloadBytes} bytes.");
        }

        await using var source = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            useAsync: true);
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await source
                .ReadAsync(buffer, cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > MaximumDownloadBytes)
            {
                throw new InvalidDataException(
                    $"Download exceeds {MaximumDownloadBytes} bytes.");
            }

            await destination
                .WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task ExtractSingleFileFromZipAsync(
        string archivePath,
        string assemblyName,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var entries = archive.Entries
            .Where(entry => string.Equals(
                Path.GetFileName(entry.FullName),
                assemblyName,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (entries.Length != 1 || entries[0].Length > MaximumAssemblyBytes)
        {
            throw new InvalidDataException(
                $"Archive must contain one {assemblyName} within the size limit.");
        }

        await using var source = entries[0].Open();
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            useAsync: true);
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExtractSingleFileFromArchiveAsync(
        string archivePath,
        string assemblyName,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        using var archive = ArchiveFactory.OpenArchive(archivePath);
        var entries = archive.Entries
            .Where(entry =>
                !entry.IsDirectory &&
                string.Equals(
                    Path.GetFileName(entry.Key),
                    assemblyName,
                    StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (entries.Length != 1 || entries[0].Size > MaximumAssemblyBytes)
        {
            throw new InvalidDataException(
                $"Archive must contain one {assemblyName} within the size limit.");
        }

        await using var source = entries[0].OpenEntryStream();
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            useAsync: true);
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
    }

    private static BundledActPluginDescriptor? CreateCandidate(
        BundledActPluginDescriptor bundled,
        string assemblyPath,
        string downloadUrl)
    {
        var version = FileVersionInfo.GetVersionInfo(assemblyPath).FileVersion;
        if (string.IsNullOrWhiteSpace(version))
        {
            throw new InvalidDataException(
                $"Downloaded assembly has no file version: {bundled.Id}.");
        }

        using var stream = File.OpenRead(assemblyPath);
        var sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        var versionComparison = CompareVersions(version, bundled.Version);
        if (versionComparison < 0 ||
            versionComparison == 0 &&
            string.Equals(sha256, bundled.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new BundledActPluginDescriptor
        {
            Id = bundled.Id,
            Name = bundled.Name,
            Version = version,
            Author = bundled.Author,
            Maintainer = bundled.Maintainer,
            Copyright = bundled.Copyright,
            ProjectUrl = bundled.ProjectUrl,
            DownloadUrl = downloadUrl,
            SourceUrl = bundled.SourceUrl,
            License = bundled.License,
            LicenseFile = bundled.LicenseFile,
            RelativeAssembly = bundled.RelativeAssembly,
            Sha256 = sha256,
            AssemblyPath = assemblyPath,
            IsOnlineUpdate = true,
        };
    }

    internal static int CompareVersions(string left, string right)
    {
        if (!Version.TryParse(left, out var leftVersion) ||
            !Version.TryParse(right, out var rightVersion))
        {
            throw new InvalidDataException(
                $"Plugin version is not numeric: '{left}' or '{right}'.");
        }

        return leftVersion.CompareTo(rightVersion);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Update cache cleanup is best-effort.
        }
    }

    [GeneratedRegex(
        "^ACT\\.FoxTTS-.+-Release\\.7z$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FoxTtsAssetPattern();

    [GeneratedRegex(
        "^PostNamazu\\.zip$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PostNamazuAssetPattern();

    private sealed record ReleaseAsset(string AssetName, string DownloadUrl);

    private sealed record CheckOutcome(
        BundledActPluginDescriptor? Update,
        string? Failure);
}

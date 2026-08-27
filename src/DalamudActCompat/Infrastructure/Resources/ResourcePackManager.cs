using DalamudActCompat.Infrastructure.Logging;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DalamudActCompat.Infrastructure.Resources;

public sealed class ResourcePackManager : IDisposable
{
    public const string ManifestFileName = "resource-packs.json";
    private const int DownloadAttemptsPerSource = 2;
    private const int InstallLockAttempts = 1200;
    private readonly string pluginDirectory;
    private readonly string cacheDirectory;
    private readonly PluginLogger logger;
    private readonly HttpClient httpClient;
    private readonly bool ownsHttpClient;
    private readonly ResourcePackCatalog? catalog;

    public ResourcePackManager(
        string pluginDirectory,
        string cacheDirectory,
        PluginLogger logger,
        HttpClient? httpClient = null)
    {
        this.pluginDirectory = Path.GetFullPath(pluginDirectory);
        this.cacheDirectory = Path.GetFullPath(cacheDirectory);
        this.logger = logger;
        ownsHttpClient = httpClient is null;
        this.httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        Directory.CreateDirectory(this.cacheDirectory);
        var manifestPath = Path.Combine(this.pluginDirectory, ManifestFileName);
        catalog = File.Exists(manifestPath) ? LoadCatalog(manifestPath) : null;
    }

    public async Task<string> ResolveDirectoryAsync(
        string packId,
        string localDirectoryName,
        CancellationToken cancellationToken)
    {
        var localDirectory = Path.Combine(pluginDirectory, localDirectoryName);
        var entry = catalog?.Packs.FirstOrDefault(candidate => string.Equals(
            candidate.Id,
            packId,
            StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            if (Directory.Exists(localDirectory))
            {
                return localDirectory;
            }

            throw new FileNotFoundException(
                $"Resource pack '{packId}' and local directory '{localDirectoryName}' are both missing.");
        }

        ValidateEntry(entry);
        var exact = GetContentDirectory(entry);
        if (IsInstalledPackValid(entry, exact))
        {
            UpdateState(entry.Id, entry.Sha256);
            return Path.Combine(exact, entry.ContentDirectory);
        }

        await using var installLock = await AcquireInstallLockAsync(entry.Id, cancellationToken)
            .ConfigureAwait(false);
        if (IsInstalledPackValid(entry, exact))
        {
            UpdateState(entry.Id, entry.Sha256);
            return Path.Combine(exact, entry.ContentDirectory);
        }

        if (Directory.Exists(localDirectory) &&
            string.Equals(
                ComputeDirectoryHash(localDirectory),
                entry.ContentSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            InstallDirectoryAtomically(entry, localDirectory);
            UpdateState(entry.Id, entry.Sha256);
            logger.Information($"Migrated existing {entry.Id} resources into the immutable pack cache.");
            return Path.Combine(exact, entry.ContentDirectory);
        }

        try
        {
            await DownloadAndInstallAsync(entry, cancellationToken).ConfigureAwait(false);
            UpdateState(entry.Id, entry.Sha256);
            return Path.Combine(exact, entry.ContentDirectory);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var fallback = TryResolvePrevious(entry.Id, entry.Sha256);
            if (fallback is not null)
            {
                logger.Warning(
                    $"Resource pack {entry.Id} could not be updated; the previous verified cache is being used: {ex.GetBaseException().Message}");
                return fallback;
            }

            throw;
        }
    }

    private async Task DownloadAndInstallAsync(
        ResourcePackEntry entry,
        CancellationToken cancellationToken)
    {
        var downloadDirectory = Path.Combine(cacheDirectory, ".downloads");
        Directory.CreateDirectory(downloadDirectory);
        var partialPath = Path.Combine(downloadDirectory, $"{entry.Id}-{entry.Sha256}.partial");
        if (File.Exists(partialPath))
        {
            var partialLength = new FileInfo(partialPath).Length;
            if (partialLength == entry.Size)
            {
                try
                {
                    // A process may stop after the final byte reaches disk but before install.
                    // Reusing that verified archive avoids a Range request beyond EOF (HTTP 416).
                    ValidateArchive(entry, partialPath);
                    ExtractAndInstallAtomically(entry, partialPath);
                    TryDeleteFile(partialPath);
                    return;
                }
                catch (InvalidDataException)
                {
                    TryDeleteFile(partialPath);
                }
            }
            else if (partialLength > entry.Size)
            {
                TryDeleteFile(partialPath);
            }
        }

        Exception? lastFailure = null;
        foreach (var url in entry.Urls)
        {
            for (var attempt = 1; attempt <= DownloadAttemptsPerSource; attempt++)
            {
                try
                {
                    await DownloadWithResumeAsync(url, partialPath, cancellationToken)
                        .ConfigureAwait(false);
                    ValidateArchive(entry, partialPath);
                    ExtractAndInstallAtomically(entry, partialPath);
                    TryDeleteFile(partialPath);
                    return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lastFailure = ex;
                    logger.Warning(
                        $"Resource pack {entry.Id} source failed (attempt {attempt}/{DownloadAttemptsPerSource}): {url}: {ex.GetBaseException().Message}");
                    if (ex is InvalidDataException)
                    {
                        // A bad archive cannot be resumed safely from another mirror.
                        TryDeleteFile(partialPath);
                    }
                }
            }
        }

        throw new InvalidDataException(
            $"Every download source failed for resource pack {entry.Id}.",
            lastFailure);
    }

    private async Task DownloadWithResumeAsync(
        string url,
        string partialPath,
        CancellationToken cancellationToken)
    {
        var existingLength = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (existingLength > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existingLength, null);
        }

        using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var append = existingLength > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var destination = new FileStream(
            partialPath,
            append ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        destination.Flush(flushToDisk: true);
    }

    private void ExtractAndInstallAtomically(ResourcePackEntry entry, string archivePath)
    {
        var stagingRoot = Path.Combine(cacheDirectory, ".staging");
        Directory.CreateDirectory(stagingRoot);
        var staging = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            foreach (var item in archive.Entries)
            {
                var destination = Path.GetFullPath(Path.Combine(staging, item.FullName));
                var prefix = staging.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!destination.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"Resource pack entry escapes its root: {item.FullName}");
                }

                if (string.IsNullOrEmpty(item.Name))
                {
                    Directory.CreateDirectory(destination);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                item.ExtractToFile(destination, overwrite: false);
            }

            var content = Path.Combine(staging, entry.ContentDirectory);
            ValidateExtractedContent(entry, content);
            WriteCompletionMarker(staging, entry);
            CommitStaging(entry, staging);
            staging = string.Empty;
        }
        finally
        {
            if (!string.IsNullOrEmpty(staging) && Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    private void InstallDirectoryAtomically(ResourcePackEntry entry, string sourceDirectory)
    {
        var stagingRoot = Path.Combine(cacheDirectory, ".staging");
        Directory.CreateDirectory(stagingRoot);
        var staging = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
        try
        {
            var destinationContent = Path.Combine(staging, entry.ContentDirectory);
            CopyDirectory(sourceDirectory, destinationContent);
            WriteCompletionMarker(staging, entry);
            CommitStaging(entry, staging);
            staging = string.Empty;
        }
        finally
        {
            // A failed migration must not leave a half-populated directory that a later
            // startup could mistake for a usable immutable pack.
            if (!string.IsNullOrEmpty(staging) && Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    public void Dispose()
    {
        if (ownsHttpClient)
        {
            httpClient.Dispose();
        }
    }

    private void CommitStaging(ResourcePackEntry entry, string staging)
    {
        var target = GetContentDirectory(entry);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (Directory.Exists(target))
        {
            if (IsInstalledPackValid(entry, target))
            {
                Directory.Delete(staging, recursive: true);
                return;
            }

            var quarantine = target + $".corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
            Directory.Move(target, quarantine);
        }
        Directory.Move(staging, target);
    }

    private string? TryResolvePrevious(string packId, string expectedSha)
    {
        var idRoot = Path.Combine(cacheDirectory, Sanitize(packId));
        if (!Directory.Exists(idRoot))
        {
            return null;
        }

        var state = LoadState(idRoot);
        var candidates = new[] { state?.PreviousSha, state?.CurrentSha }
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Concat(Directory.GetDirectories(idRoot)
                .Select(Path.GetFileName)
                .Where(static value => !string.IsNullOrWhiteSpace(value)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(value => !string.Equals(value, expectedSha, StringComparison.OrdinalIgnoreCase));
        foreach (var sha in candidates)
        {
            var root = Path.Combine(idRoot, sha!);
            var marker = LoadCompletionMarker(root);
            if (marker is null || !string.Equals(marker.Id, packId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var content = Path.Combine(root, marker.ContentDirectory);
            if (Directory.Exists(content) && string.Equals(
                    ComputeDirectoryHash(content),
                    marker.ContentSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                return content;
            }
        }
        return null;
    }

    private bool IsInstalledPackValid(ResourcePackEntry entry, string root)
    {
        var marker = LoadCompletionMarker(root);
        var content = Path.Combine(root, entry.ContentDirectory);
        return marker is not null &&
               string.Equals(marker.Id, entry.Id, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(marker.Sha256, entry.Sha256, StringComparison.OrdinalIgnoreCase) &&
               Directory.Exists(content) &&
               string.Equals(
                   ComputeDirectoryHash(content),
                   entry.ContentSha256,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateExtractedContent(ResourcePackEntry entry, string content)
    {
        if (!Directory.Exists(content) || !string.Equals(
                ComputeDirectoryHash(content),
                entry.ContentSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Extracted resource pack content hash is invalid: {entry.Id}");
        }
    }

    private static void ValidateArchive(ResourcePackEntry entry, string archivePath)
    {
        var file = new FileInfo(archivePath);
        if (file.Length != entry.Size)
        {
            throw new InvalidDataException(
                $"Resource pack {entry.Id} size mismatch. Expected {entry.Size}, got {file.Length}.");
        }
        using var stream = file.OpenRead();
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (!string.Equals(hash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Resource pack {entry.Id} SHA-256 mismatch.");
        }
    }

    private async Task<FileStream> AcquireInstallLockAsync(
        string packId,
        CancellationToken cancellationToken)
    {
        var idRoot = Path.Combine(cacheDirectory, Sanitize(packId));
        Directory.CreateDirectory(idRoot);
        var lockPath = Path.Combine(idRoot, ".install.lock");
        for (var attempt = 0; attempt < InstallLockAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (attempt < InstallLockAttempts - 1)
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }
        throw new IOException($"Timed out waiting for the resource pack lock: {packId}");
    }

    private void UpdateState(string packId, string sha)
    {
        var idRoot = Path.Combine(cacheDirectory, Sanitize(packId));
        Directory.CreateDirectory(idRoot);
        var current = LoadState(idRoot);
        if (string.Equals(current?.CurrentSha, sha, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        var next = new ResourcePackState(sha, current?.CurrentSha ?? current?.PreviousSha ?? string.Empty);
        var path = Path.Combine(idRoot, "state.json");
        WriteJsonAtomically(path, next);
    }

    private static ResourcePackState? LoadState(string idRoot)
        => ReadJson<ResourcePackState>(Path.Combine(idRoot, "state.json"));

    private static ResourcePackEntry? LoadCompletionMarker(string root)
        => ReadJson<ResourcePackEntry>(Path.Combine(root, ".complete.json"));

    private static void WriteCompletionMarker(string root, ResourcePackEntry entry)
        => WriteJsonAtomically(Path.Combine(root, ".complete.json"), entry);

    private string GetContentDirectory(ResourcePackEntry entry)
        => Path.Combine(cacheDirectory, Sanitize(entry.Id), entry.Sha256.ToLowerInvariant());

    private static ResourcePackCatalog LoadCatalog(string path)
    {
        using var stream = File.OpenRead(path);
        var result = JsonSerializer.Deserialize<ResourcePackCatalog>(stream, JsonOptions)
                     ?? throw new InvalidDataException("Resource pack manifest is empty.");
        if (result.SchemaVersion != 1 || string.IsNullOrWhiteSpace(result.Version))
        {
            throw new InvalidDataException("Resource pack manifest metadata is invalid.");
        }
        return result;
    }

    private static void ValidateEntry(ResourcePackEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Id) ||
            string.IsNullOrWhiteSpace(entry.Version) ||
            string.IsNullOrWhiteSpace(entry.FileName) ||
            entry.Size <= 0 ||
            entry.Sha256.Length != 64 ||
            entry.ContentSha256.Length != 64 ||
            string.IsNullOrWhiteSpace(entry.ContentDirectory) ||
            entry.ContentDirectory.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0 ||
            entry.Urls.Count < 1)
        {
            throw new InvalidDataException($"Resource pack manifest entry is invalid: {entry.Id}");
        }
    }

    public static string ComputeDirectoryHash(string directory)
    {
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                     .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
        {
            var relative = file[root.Length..].Replace(Path.DirectorySeparatorChar, '/');
            aggregate.AppendData(Encoding.UTF8.GetBytes(relative));
            aggregate.AppendData([0]);
            using var stream = File.OpenRead(file);
            aggregate.AppendData(SHA256.HashData(stream));
        }
        return Convert.ToHexString(aggregate.GetHashAndReset()).ToLowerInvariant();
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
    }

    private static T? ReadJson<T>(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<T>(stream, JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return default;
        }
    }

    private static void WriteJsonAtomically<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temporary);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string Sanitize(string value)
        => new(value.Select(character => char.IsLetterOrDigit(character) || character is '-' or '_'
            ? character
            : '_').ToArray());

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
}

public sealed record ResourcePackCatalog(
    int SchemaVersion,
    string Version,
    IReadOnlyList<ResourcePackEntry> Packs);

public sealed record ResourcePackEntry(
    string Id,
    string Version,
    string FileName,
    long Size,
    string Sha256,
    string ContentDirectory,
    string ContentSha256,
    IReadOnlyList<string> Urls);

public sealed record ResourcePackState(string CurrentSha, string PreviousSha);

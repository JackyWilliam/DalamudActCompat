using System.IO.Compression;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DalamudActCompat.Infrastructure.Storage;

internal sealed class PortableConfigurationArchiveService
{
    private const int CurrentFormatVersion = 1;
    private const int MaximumArchiveEntries = 4096;
    private const long MaximumExpandedBytes = 64L * 1024 * 1024;
    private const long MaximumManifestBytes = 1024 * 1024;
    private const string ManifestEntryName = "manifest.json";
    private const string PayloadPrefix = "payload/";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    // Tests inject a failure between staging the old scope and committing the new one.
    // Production callers leave this as a no-op so rollback exercises the real move path.
    internal Action<int, string> BeforeScopeCommit { get; set; } = static (_, _) => { };

    public async Task<PortableConfigurationExportResult> ExportAsync(
        string configurationRoot,
        string archivePath,
        IReadOnlyCollection<string> relativeScopes,
        CancellationToken cancellationToken)
    {
        var root = ValidateConfigurationRoot(configurationRoot, mustExist: true);
        var destination = ValidateArchivePathOutsideRoot(root, archivePath);
        if (File.Exists(destination) || Directory.Exists(destination))
        {
            throw new IOException($"Portable configuration archive already exists: {destination}");
        }

        var scopes = NormalizeScopes(root, relativeScopes);
        var snapshots = scopes
            .Select(relativePath => CaptureScope(root, relativePath))
            .ToArray();
        var destinationDirectory = Path.GetDirectoryName(destination)
                                   ?? throw new InvalidOperationException(
                                       "Portable configuration archive has no parent directory.");
        Directory.CreateDirectory(destinationDirectory);
        var temporaryArchive = $"{destination}.tmp-{Guid.NewGuid():N}";
        var fileRecords = new List<ArchiveFile>();
        long totalBytes = 0;
        try
        {
            await using (var output = new FileStream(
                             temporaryArchive,
                             FileMode.CreateNew,
                             FileAccess.ReadWrite,
                             FileShare.None,
                             bufferSize: 81920,
                             useAsync: true))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false))
            {
                foreach (var snapshot in snapshots)
                {
                    foreach (var file in snapshot.Files)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var record = await AddFileAsync(
                                archive,
                                root,
                                file,
                                cancellationToken)
                            .ConfigureAwait(false);
                        fileRecords.Add(record);
                        totalBytes += record.Length;
                        if (fileRecords.Count > MaximumArchiveEntries - 1 ||
                            totalBytes > MaximumExpandedBytes)
                        {
                            throw new InvalidDataException(
                                "Portable configuration export exceeds its safety limits.");
                        }
                    }
                }

                var manifest = new ArchiveManifest
                {
                    FormatVersion = CurrentFormatVersion,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                    Scopes = snapshots
                        .Select(static snapshot => new ArchiveScope
                        {
                            RelativePath = snapshot.RelativePath,
                            Kind = snapshot.Kind,
                        })
                        .ToList(),
                    Files = fileRecords,
                };
                var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
                var manifestEntry = archive.CreateEntry(ManifestEntryName, CompressionLevel.Optimal);
                await using var manifestOutput = manifestEntry.Open();
                await manifestOutput.WriteAsync(manifestBytes, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryArchive, destination);
            return new PortableConfigurationExportResult(
                destination,
                snapshots.Length,
                fileRecords.Count,
                totalBytes,
                ComputeContentFingerprint(snapshots, fileRecords));
        }
        catch
        {
            TryDeleteFile(temporaryArchive);
            throw;
        }
    }

    public async Task<PortableConfigurationRestoreResult> RestoreAsync(
        string archivePath,
        string configurationRoot,
        string rollbackArchivePath,
        CancellationToken cancellationToken,
        Func<string, CancellationToken, Task>? prepareRollbackAsync = null)
    {
        var root = ValidateConfigurationRoot(configurationRoot, mustExist: false);
        var sourceArchive = ValidateArchivePathOutsideRoot(root, archivePath);
        if (!File.Exists(sourceArchive))
        {
            throw new FileNotFoundException(
                "Portable configuration archive was not found.",
                sourceArchive);
        }

        var rollbackArchive = ValidateArchivePathOutsideRoot(root, rollbackArchivePath);
        if (PathsEqual(sourceArchive, rollbackArchive))
        {
            throw new InvalidOperationException(
                "The source and rollback configuration archives must be different files.");
        }
        if (File.Exists(rollbackArchive) || Directory.Exists(rollbackArchive))
        {
            throw new IOException($"Rollback archive already exists: {rollbackArchive}");
        }

        Directory.CreateDirectory(root);
        EnsurePathHasNoReparsePoints(root, root);
        var operationRoot = Path.Combine(
            Path.GetDirectoryName(root)
            ?? throw new InvalidOperationException("Configuration root has no parent directory."),
            $".{Path.GetFileName(root)}.restore-{Guid.NewGuid():N}");
        var stagedRoot = Path.Combine(operationRoot, "staged");
        var undoRoot = Path.Combine(operationRoot, "undo");
        Directory.CreateDirectory(stagedRoot);
        Directory.CreateDirectory(undoRoot);

        try
        {
            var manifest = await ValidateAndExtractAsync(
                    sourceArchive,
                    root,
                    stagedRoot,
                    cancellationToken)
                .ConfigureAwait(false);
            var relativeScopes = manifest.Scopes
                .Select(static scope => scope.RelativePath)
                .ToArray();

            // A successful restore keeps this snapshot so the user can undo it later;
            // the same snapshot also documents the exact state used by automatic rollback.
            await ExportAsync(root, rollbackArchive, relativeScopes, cancellationToken)
                .ConfigureAwait(false);
            if (prepareRollbackAsync is not null)
            {
                // The cloud layer encrypts the rollback before any live file changes,
                // so an encryption failure cannot leave the installation half-restored.
                await prepareRollbackAsync(rollbackArchive, cancellationToken)
                    .ConfigureAwait(false);
            }
            ApplySnapshot(root, stagedRoot, undoRoot, manifest, cancellationToken);

            return new PortableConfigurationRestoreResult(
                sourceArchive,
                rollbackArchive,
                manifest.Scopes.Count,
                manifest.Files.Count);
        }
        finally
        {
            TryDeleteDirectory(operationRoot);
        }
    }

    internal async Task<PortableConfigurationArchiveInspection> ExtractVerifiedAsync(
        string archivePath,
        string configurationRoot,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        var root = ValidateConfigurationRoot(configurationRoot, mustExist: false);
        var sourceArchive = ValidateArchivePathOutsideRoot(root, archivePath);
        if (!File.Exists(sourceArchive))
        {
            throw new FileNotFoundException(
                "Portable configuration archive was not found.",
                sourceArchive);
        }

        var destination = Path.GetFullPath(destinationRoot);
        if (EntryExists(destination))
        {
            throw new IOException(
                $"Portable configuration extraction directory already exists: {destination}");
        }

        Directory.CreateDirectory(destination);
        try
        {
            var manifest = await ValidateAndExtractAsync(
                    sourceArchive,
                    root,
                    destination,
                    cancellationToken)
                .ConfigureAwait(false);
            return new PortableConfigurationArchiveInspection(
                manifest.CreatedAtUtc,
                manifest.Scopes
                    .Select(static scope => new PortableConfigurationArchiveScope(
                        scope.RelativePath,
                        scope.Kind))
                    .ToArray(),
                manifest.Files
                    .Select(static file => new PortableConfigurationArchiveFile(
                        file.RelativePath,
                        file.Length,
                        file.Sha256))
                    .ToArray());
        }
        catch
        {
            TryDeleteDirectory(destination);
            throw;
        }
    }

    private void ApplySnapshot(
        string root,
        string stagedRoot,
        string undoRoot,
        ArchiveManifest manifest,
        CancellationToken cancellationToken)
    {
        foreach (var scope in manifest.Scopes)
        {
            EnsurePathHasNoReparsePoints(root, GetSafePath(root, scope.RelativePath));
        }

        var transactions = new List<ScopeTransaction>();
        try
        {
            for (var index = 0; index < manifest.Scopes.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var scope = manifest.Scopes[index];
                var target = GetSafePath(root, scope.RelativePath);
                var undo = GetSafePath(undoRoot, scope.RelativePath);
                var hadOriginal = EntryExists(target);
                if (hadOriginal)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(undo)!);
                    MoveEntry(target, undo);
                }

                var createdParentDirectories = scope.Kind == ArchiveScopeKind.Missing
                    ? []
                    : FindMissingParentDirectories(root, Path.GetDirectoryName(target)!);
                transactions.Add(new ScopeTransaction(
                    target,
                    undo,
                    hadOriginal,
                    createdParentDirectories));
                BeforeScopeCommit(index, scope.RelativePath);
                if (scope.Kind == ArchiveScopeKind.Missing)
                {
                    continue;
                }

                var staged = GetSafePath(stagedRoot, scope.RelativePath);
                if (scope.Kind == ArchiveScopeKind.Directory && !Directory.Exists(staged))
                {
                    Directory.CreateDirectory(staged);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                MoveEntry(staged, target);
            }
        }
        catch (Exception failure)
        {
            var rollbackFailures = RollbackTransactions(transactions);
            if (rollbackFailures.Count > 0)
            {
                throw new AggregateException(
                    "Portable configuration restore failed and rollback was incomplete.",
                    [failure, .. rollbackFailures]);
            }

            ExceptionDispatchInfo.Capture(failure).Throw();
            throw;
        }
    }

    private static List<Exception> RollbackTransactions(
        IReadOnlyList<ScopeTransaction> transactions)
    {
        var failures = new List<Exception>();
        foreach (var transaction in transactions.Reverse())
        {
            try
            {
                DeleteEntry(transaction.TargetPath);
                if (transaction.HadOriginal && EntryExists(transaction.UndoPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(transaction.TargetPath)!);
                    MoveEntry(transaction.UndoPath, transaction.TargetPath);
                }
                foreach (var directory in transaction.CreatedParentDirectories)
                {
                    if (Directory.Exists(directory) &&
                        !Directory.EnumerateFileSystemEntries(directory).Any())
                    {
                        Directory.Delete(directory);
                    }
                }
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }

        return failures;
    }

    private static async Task<ArchiveManifest> ValidateAndExtractAsync(
        string archivePath,
        string configurationRoot,
        string stagedRoot,
        CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count == 0 || archive.Entries.Count > MaximumArchiveEntries)
        {
            throw new InvalidDataException(
                $"Portable configuration archive must contain between 1 and {MaximumArchiveEntries} entries.");
        }

        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var name = NormalizeArchiveEntryName(entry.FullName);
            if (!entries.TryAdd(name, entry))
            {
                throw new InvalidDataException($"Portable configuration archive contains duplicate entry '{name}'.");
            }
        }

        if (!entries.TryGetValue(ManifestEntryName, out var manifestEntry) ||
            manifestEntry.Length <= 0 ||
            manifestEntry.Length > MaximumManifestBytes)
        {
            throw new InvalidDataException("Portable configuration archive has no valid manifest.");
        }

        ArchiveManifest manifest;
        await using (var manifestStream = manifestEntry.Open())
        {
            manifest = await JsonSerializer.DeserializeAsync<ArchiveManifest>(
                           manifestStream,
                           JsonOptions,
                           cancellationToken)
                       .ConfigureAwait(false)
                       ?? throw new InvalidDataException(
                           "Portable configuration archive manifest is empty.");
        }

        ValidateManifest(configurationRoot, manifest);
        var expectedEntries = manifest.Files
            .Select(static file => PayloadPrefix + file.RelativePath)
            .Append(ManifestEntryName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (entries.Keys.Any(name => !expectedEntries.Contains(name)) ||
            expectedEntries.Any(name => !entries.ContainsKey(name)))
        {
            throw new InvalidDataException(
                "Portable configuration archive entries do not match its manifest.");
        }

        foreach (var file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = entries[PayloadPrefix + file.RelativePath];
            if (entry.Length != file.Length)
            {
                throw new InvalidDataException(
                    $"Portable configuration entry length does not match its manifest: {file.RelativePath}");
            }

            var destination = GetSafePath(stagedRoot, file.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using (var input = entry.Open())
            await using (var output = new FileStream(
                             destination,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 81920,
                             useAsync: true))
            {
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }

            var actualHash = await ComputeSha256Async(destination, cancellationToken)
                .ConfigureAwait(false);
            if (!actualHash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Portable configuration entry hash does not match its manifest: {file.RelativePath}");
            }
        }

        return manifest;
    }

    private static void ValidateManifest(string root, ArchiveManifest manifest)
    {
        if (manifest.FormatVersion != CurrentFormatVersion)
        {
            throw new InvalidDataException(
                $"Unsupported portable configuration format version {manifest.FormatVersion}.");
        }
        if (manifest.Scopes is null ||
            manifest.Scopes.Count == 0 ||
            manifest.Scopes.Count > MaximumArchiveEntries)
        {
            throw new InvalidDataException("Portable configuration manifest has no valid scopes.");
        }
        if (manifest.Files is null || manifest.Files.Count > MaximumArchiveEntries - 1)
        {
            throw new InvalidDataException("Portable configuration manifest exceeds its safety limits.");
        }

        foreach (var scope in manifest.Scopes)
        {
            if (!Enum.IsDefined(scope.Kind))
            {
                throw new InvalidDataException(
                    $"Portable configuration manifest contains an invalid scope kind: {scope.Kind}");
            }
            scope.RelativePath = NormalizeRelativePath(root, scope.RelativePath);
        }
        EnsureScopesDoNotOverlap(
            manifest.Scopes.Select(static scope => scope.RelativePath).ToArray(),
            "manifest scopes");
        manifest.Scopes = manifest.Scopes
            .OrderBy(static scope => scope.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expandedBytes = 0;
        foreach (var file in manifest.Files)
        {
            file.RelativePath = NormalizeRelativePath(root, file.RelativePath);
            if (!files.Add(file.RelativePath) ||
                file.Length < 0 ||
                file.Length > MaximumExpandedBytes ||
                string.IsNullOrWhiteSpace(file.Sha256) ||
                file.Sha256.Length != 64 ||
                file.Sha256.Any(static character => !Uri.IsHexDigit(character)))
            {
                throw new InvalidDataException(
                    $"Portable configuration manifest contains an invalid file record: {file.RelativePath}");
            }
            expandedBytes += file.Length;
            if (expandedBytes > MaximumExpandedBytes)
            {
                throw new InvalidDataException(
                    "Portable configuration manifest exceeds its expanded-size limit.");
            }

            var owner = manifest.Scopes.SingleOrDefault(scope => ScopeContains(scope, file.RelativePath));
            if (owner is null || owner.Kind == ArchiveScopeKind.Missing)
            {
                throw new InvalidDataException(
                    $"Portable configuration file is outside its declared scopes: {file.RelativePath}");
            }
        }

        foreach (var scope in manifest.Scopes)
        {
            if (scope.Kind == ArchiveScopeKind.File &&
                !manifest.Files.Any(file => PathsEqual(file.RelativePath, scope.RelativePath)))
            {
                throw new InvalidDataException(
                    $"Portable configuration file scope has no payload: {scope.RelativePath}");
            }
        }
    }

    private static bool ScopeContains(ArchiveScope scope, string filePath)
        => scope.Kind switch
        {
            ArchiveScopeKind.File => PathsEqual(scope.RelativePath, filePath),
            ArchiveScopeKind.Directory => filePath.StartsWith(
                scope.RelativePath + "/",
                StringComparison.OrdinalIgnoreCase),
            _ => false,
        };

    private static ScopeSnapshot CaptureScope(string root, string relativePath)
    {
        var path = GetSafePath(root, relativePath);
        EnsurePathHasNoReparsePoints(root, path);
        if (File.Exists(path))
        {
            return new ScopeSnapshot(relativePath, ArchiveScopeKind.File, [path]);
        }
        if (!Directory.Exists(path))
        {
            return new ScopeSnapshot(relativePath, ArchiveScopeKind.Missing, []);
        }

        var files = EnumeratePortableFiles(path)
            .OrderBy(static file => file, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new ScopeSnapshot(relativePath, ArchiveScopeKind.Directory, files);
    }

    private static IEnumerable<string> EnumeratePortableFiles(string directory)
    {
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            RejectReparsePoint(current);
            foreach (var file in Directory.EnumerateFiles(current))
            {
                RejectReparsePoint(file);
                yield return file;
            }
            foreach (var child in Directory.EnumerateDirectories(current))
            {
                RejectReparsePoint(child);
                pending.Push(child);
            }
        }
    }

    private static async Task<ArchiveFile> AddFileAsync(
        ZipArchive archive,
        string root,
        string filePath,
        CancellationToken cancellationToken)
    {
        var relativePath = ToArchivePath(Path.GetRelativePath(root, filePath));
        var entry = archive.CreateEntry(PayloadPrefix + relativePath, CompressionLevel.Optimal);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long length = 0;
        var buffer = new byte[81920];
        await using var input = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            buffer.Length,
            useAsync: true);
        await using var output = entry.Open();
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            length += read;
            if (length > MaximumExpandedBytes)
            {
                throw new InvalidDataException(
                    $"Portable configuration file exceeds {MaximumExpandedBytes} bytes: {relativePath}");
            }
            hasher.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return new ArchiveFile
        {
            RelativePath = relativePath,
            Length = length,
            Sha256 = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant(),
        };
    }

    private static string ComputeContentFingerprint(
        IReadOnlyList<ScopeSnapshot> scopes,
        IReadOnlyList<ArchiveFile> files)
    {
        // Archive creation time and encryption randomness are intentionally excluded,
        // otherwise an unchanged configuration would consume another server version.
        var canonical = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                FormatVersion = CurrentFormatVersion,
                Scopes = scopes.Select(static scope => new
                {
                    scope.RelativePath,
                    scope.Kind,
                }),
                Files = files.Select(static file => new
                {
                    file.RelativePath,
                    file.Length,
                    file.Sha256,
                }),
            },
            JsonOptions);
        return Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant();
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);
        var hash = await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static List<string> NormalizeScopes(
        string root,
        IReadOnlyCollection<string> relativeScopes)
    {
        if (relativeScopes.Count == 0)
        {
            throw new ArgumentException(
                "At least one portable configuration scope is required.",
                nameof(relativeScopes));
        }

        var normalized = relativeScopes
            .Select(relativePath => NormalizeRelativePath(root, relativePath))
            .OrderBy(static relativePath => relativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        EnsureScopesDoNotOverlap(normalized, nameof(relativeScopes));
        return normalized;
    }

    private static void EnsureScopesDoNotOverlap(
        IReadOnlyList<string> normalizedScopes,
        string sourceName)
    {
        for (var left = 0; left < normalizedScopes.Count; left++)
        {
            for (var right = left + 1; right < normalizedScopes.Count; right++)
            {
                if (PathsEqual(normalizedScopes[left], normalizedScopes[right]) ||
                    normalizedScopes[right].StartsWith(
                        normalizedScopes[left] + "/",
                        StringComparison.OrdinalIgnoreCase) ||
                    normalizedScopes[left].StartsWith(
                        normalizedScopes[right] + "/",
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException(
                        $"Portable configuration scopes overlap: '{normalizedScopes[left]}' and '{normalizedScopes[right]}'.",
                        sourceName);
                }
            }
        }
    }

    private static string NormalizeRelativePath(string root, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        var nativePath = relativePath.Replace(
            Path.AltDirectorySeparatorChar,
            Path.DirectorySeparatorChar);
        if (Path.IsPathFullyQualified(nativePath))
        {
            throw new InvalidDataException(
                $"Portable configuration path must be relative: {relativePath}");
        }

        var segments = nativePath.Split(
            Path.DirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 ||
            segments.Any(static segment => segment is "." or ".."))
        {
            throw new InvalidDataException(
                $"Portable configuration path is not canonical: {relativePath}");
        }

        var fullPath = Path.GetFullPath(Path.Combine(root, Path.Combine(segments)));
        EnsureInsideRoot(root, fullPath);
        return ToArchivePath(Path.GetRelativePath(root, fullPath));
    }

    private static string NormalizeArchiveEntryName(string entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName) || entryName.Contains('\\'))
        {
            throw new InvalidDataException(
                $"Portable configuration archive contains an invalid entry name: {entryName}");
        }

        var segments = entryName.Split('/');
        if (segments.Length == 0 ||
            segments.Any(string.IsNullOrEmpty) ||
            segments.Any(static segment => segment is "." or ".."))
        {
            throw new InvalidDataException(
                $"Portable configuration archive contains a non-canonical entry: {entryName}");
        }
        return string.Join('/', segments);
    }

    private static string ValidateConfigurationRoot(string configurationRoot, bool mustExist)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationRoot);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configurationRoot));
        if (mustExist && !Directory.Exists(root))
        {
            throw new DirectoryNotFoundException(
                $"Configuration root was not found: {root}");
        }
        if (File.Exists(root))
        {
            throw new IOException($"Configuration root is a file: {root}");
        }
        return root;
    }

    private static string ValidateArchivePathOutsideRoot(string root, string archivePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        var fullPath = Path.GetFullPath(archivePath);
        if (PathsEqual(root, fullPath) || IsInsideRoot(root, fullPath))
        {
            throw new InvalidOperationException(
                "Portable configuration archives must be stored outside the configuration root.");
        }
        return fullPath;
    }

    private static string GetSafePath(string root, string relativePath)
    {
        var nativePath = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(root, nativePath));
        EnsureInsideRoot(root, fullPath);
        return fullPath;
    }

    private static void EnsureInsideRoot(string root, string path)
    {
        if (!IsInsideRoot(root, path))
        {
            throw new InvalidDataException(
                $"Portable configuration path escapes its root: {path}");
        }
    }

    private static bool IsInsideRoot(string root, string path)
        => path.StartsWith(
            Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private static void EnsurePathHasNoReparsePoints(string root, string path)
    {
        RejectReparsePoint(root);
        var relativePath = Path.GetRelativePath(root, path);
        var current = root;
        foreach (var segment in relativePath.Split(
                     Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!EntryExists(current))
            {
                break;
            }
            RejectReparsePoint(current);
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if (EntryExists(path) &&
            File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException(
                $"Portable configuration scopes cannot contain links or reparse points: {path}");
        }
    }

    private static void MoveEntry(string source, string destination)
    {
        if (Directory.Exists(source))
        {
            Directory.Move(source, destination);
        }
        else if (File.Exists(source))
        {
            File.Move(source, destination);
        }
        else
        {
            throw new FileNotFoundException(
                "Portable configuration entry disappeared before it could be moved.",
                source);
        }
    }

    private static void DeleteEntry(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static bool EntryExists(string path)
        => Directory.Exists(path) || File.Exists(path);

    private static IReadOnlyList<string> FindMissingParentDirectories(
        string root,
        string parentDirectory)
    {
        var missing = new List<string>();
        var current = parentDirectory;
        while (!PathsEqual(root, current))
        {
            EnsureInsideRoot(root, current);
            if (Directory.Exists(current) || File.Exists(current))
            {
                break;
            }

            missing.Add(current);
            current = Path.GetDirectoryName(current)
                      ?? throw new InvalidOperationException(
                          "Portable configuration scope has no parent directory.");
        }
        return missing;
    }

    private static bool PathsEqual(string left, string right)
        => left.Equals(right, StringComparison.OrdinalIgnoreCase);

    private static string ToArchivePath(string path)
        => path.Replace(Path.DirectorySeparatorChar, '/');

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Cleanup failure must not replace the actual export failure.
        }
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
            // A leftover staging directory is safer than hiding the restore result.
        }
    }

    private sealed record ScopeSnapshot(
        string RelativePath,
        ArchiveScopeKind Kind,
        IReadOnlyList<string> Files);

    private sealed record ScopeTransaction(
        string TargetPath,
        string UndoPath,
        bool HadOriginal,
        IReadOnlyList<string> CreatedParentDirectories);

    private sealed class ArchiveManifest
    {
        public int FormatVersion { get; set; }

        public DateTimeOffset CreatedAtUtc { get; set; }

        public List<ArchiveScope> Scopes { get; set; } = [];

        public List<ArchiveFile> Files { get; set; } = [];
    }

    private sealed class ArchiveScope
    {
        public string RelativePath { get; set; } = string.Empty;

        public ArchiveScopeKind Kind { get; set; }
    }

    private sealed class ArchiveFile
    {
        public string RelativePath { get; set; } = string.Empty;

        public long Length { get; set; }

        public string Sha256 { get; set; } = string.Empty;
    }

}

internal enum ArchiveScopeKind
{
    Missing,
    File,
    Directory,
}

internal sealed record PortableConfigurationExportResult(
    string ArchivePath,
    int ScopeCount,
    int FileCount,
    long UncompressedBytes,
    string ContentFingerprint);

internal sealed record PortableConfigurationRestoreResult(
    string ArchivePath,
    string RollbackArchivePath,
    int ScopeCount,
    int FileCount);

internal sealed record PortableConfigurationArchiveInspection(
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<PortableConfigurationArchiveScope> Scopes,
    IReadOnlyList<PortableConfigurationArchiveFile> Files);

internal sealed record PortableConfigurationArchiveScope(
    string RelativePath,
    ArchiveScopeKind Kind);

internal sealed record PortableConfigurationArchiveFile(
    string RelativePath,
    long Length,
    string Sha256);
